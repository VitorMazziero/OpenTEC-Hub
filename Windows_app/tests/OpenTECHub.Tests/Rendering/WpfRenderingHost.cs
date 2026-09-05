using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Alarms;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Control;
using OpenTECHub.Services.Dialogs;
using OpenTECHub.Services.KlaMapping;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Platform;
using OpenTECHub.Services.PowerMapping;
using OpenTECHub.Services.PowerTesting;
using OpenTECHub.Services.Recipes;
using OpenTECHub.Services.Safety;
using OpenTECHub.Services.Telemetry;
using OpenTECHub.Services.Theme;
using OpenTECHub.ViewModels;

namespace OpenTECHub.Tests.Rendering;

/// <summary>
/// Dedicated background STA dispatcher host for in-memory WPF rendering and tests.
/// Eliminates Windows UI Automation COM (0x80004002) dependency by rendering
/// controls directly via RenderTargetBitmap.
/// </summary>
public static class WpfRenderingHost
{
    private static readonly object SyncLock = new();
    private static Thread? _staThread;
    private static Dispatcher? _dispatcher;
    private static readonly ManualResetEventSlim ReadyEvent = new(false);
    private static IServiceProvider? _services;

    public static IServiceProvider Services
    {
        get
        {
            EnsureInitialized();
            return _services!;
        }
    }

    public static void EnsureInitialized()
    {
        if (_dispatcher != null)
        {
            return;
        }

        lock (SyncLock)
        {
            if (_dispatcher != null)
            {
                return;
            }

            _staThread = new Thread(RunStaMessagePump)
            {
                Name = "WpfRenderingHost_STA",
                IsBackground = true,
            };
            _staThread.SetApartmentState(ApartmentState.STA);
            _staThread.Start();

            ReadyEvent.Wait(TimeSpan.FromSeconds(10));

            if (_dispatcher == null)
            {
                throw new InvalidOperationException("WPF Rendering Host STA thread failed to initialize within timeout.");
            }
        }
    }

    private static void RunStaMessagePump()
    {
        // 1. Point WPF pack URI resolution to OpenTECHub assembly
        if (Application.ResourceAssembly == null)
        {
            Application.ResourceAssembly = typeof(OpenTECHub.App).Assembly;
        }

        // 2. Initialize App instance if none exists
        if (Application.Current == null)
        {
            var app = new OpenTECHub.App();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.InitializeComponent();

            // 3. Initialize test workspace in temp folder
            var tempWorkspace = Path.Combine(Path.GetTempPath(), "OpenTEC_Test_Workspace_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempWorkspace);
            AppPaths.InitializeWorkspace(tempWorkspace, persist: false);

            // 4. Configure in-memory / mock services
            var serviceCollection = new ServiceCollection();
            App.ConfigureServices(serviceCollection, null);

            // Override device service with recording device for safe test execution
            var recordingDevice = new RecordingDeviceService();
            serviceCollection.AddSingleton<IDeviceService>(recordingDevice);
            serviceCollection.AddSingleton<RecordingDeviceService>(recordingDevice);

            _services = serviceCollection.BuildServiceProvider();
            app.Services = _services;

            // Apply initial light theme
            var theme = _services.GetService<IThemeService>();
            theme?.Apply(ThemePreference.Light);
        }
        else if ((Application.Current as OpenTECHub.App)?.Services == null)
        {
            var app = (OpenTECHub.App)Application.Current;
            var serviceCollection = new ServiceCollection();
            App.ConfigureServices(serviceCollection, null);
            _services = serviceCollection.BuildServiceProvider();
            app.Services = _services;
        }
        else
        {
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _services = (Application.Current as OpenTECHub.App)?.Services;
        }

        _dispatcher = Dispatcher.CurrentDispatcher;
        ReadyEvent.Set();

        Dispatcher.Run();
    }

    public static T Run<T>(Func<T> action)
    {
        EnsureInitialized();
        if (Thread.CurrentThread == _staThread)
        {
            return action();
        }

        return _dispatcher!.Invoke(action);
    }

    public static void Run(Action action)
    {
        EnsureInitialized();
        if (Thread.CurrentThread == _staThread)
        {
            action();
            return;
        }

        _dispatcher!.Invoke(action);
    }

    public static void PumpDispatcher()
    {
        Run(() =>
        {
            var frame = new DispatcherFrame();
            _dispatcher!.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new DispatcherOperationCallback(f =>
                {
                    ((DispatcherFrame)f!).Continue = false;
                    return null;
                }),
                frame);
            Dispatcher.PushFrame(frame);
        });
    }

    public static void SetTheme(bool isDark)
    {
        Run(() =>
        {
            var theme = Services.GetRequiredService<IThemeService>();
            theme.Apply(isDark ? ThemePreference.Dark : ThemePreference.Light);
            PumpDispatcher();
        });
    }

    /// <summary>
    /// Renders any FrameworkElement in a hosted test window and captures it to a RenderTargetBitmap.
    /// </summary>
    public static RenderTargetBitmap RenderElement(
        FrameworkElement element,
        double width,
        double height,
        double dpi = 96.0)
    {
        return Run(() =>
        {
            var container = new Border
            {
                Width = width,
                Height = height,
                Background = (Brush)Application.Current.FindResource("SurfaceBaseBrush"),
                Child = element,
            };

            var window = new Window
            {
                Width = width,
                Height = height,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                Content = container,
            };

            window.Show();
            PumpDispatcher();

            container.Measure(new Size(width, height));
            container.Arrange(new Rect(0, 0, width, height));
            container.UpdateLayout();
            PumpDispatcher();

            var pixelWidth = Math.Max(1, (int)Math.Round(width * dpi / 96.0));
            var pixelHeight = Math.Max(1, (int)Math.Round(height * dpi / 96.0));

            var rtb = new RenderTargetBitmap(pixelWidth, pixelHeight, dpi, dpi, PixelFormats.Pbgra32);
            rtb.Render(container);

            window.Close();
            container.Child = null;

            return rtb;
        });
    }

    /// <summary>
    /// Renders the Shell MainWindow into a RenderTargetBitmap at the specified DPI.
    /// </summary>
    public static RenderTargetBitmap RenderWindow(
        Window window,
        double width,
        double height,
        double dpi = 96.0)
    {
        return Run(() =>
        {
            // MainWindow decides for itself whether to open maximized - from the saved
            // placement, or from ShouldStartMaximizedForSmallScreen() on a laptop-sized
            // panel. Both contradict an offscreen capture, which needs the exact size and
            // position set below, and WPF refuses outright to Show() a maximized window
            // with ShowActivated false. Normal is forced first so the capture is the same
            // on every host, whatever screen the suite happens to run on.
            window.WindowState = WindowState.Normal;

            window.Width = width;
            window.Height = height;
            window.WindowStyle = WindowStyle.None;
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000;
            window.Top = -32000;

            window.Show();
            PumpDispatcher();

            window.Measure(new Size(width, height));
            window.Arrange(new Rect(0, 0, width, height));
            window.UpdateLayout();
            PumpDispatcher();

            var pixelWidth = Math.Max(1, (int)Math.Round(width * dpi / 96.0));
            var pixelHeight = Math.Max(1, (int)Math.Round(height * dpi / 96.0));

            var rtb = new RenderTargetBitmap(pixelWidth, pixelHeight, dpi, dpi, PixelFormats.Pbgra32);
            rtb.Render(window);

            window.Close();

            return rtb;
        });
    }

    /// <summary>
    /// Encodes and saves a RenderTargetBitmap as a PNG file.
    /// </summary>
    public static void SavePng(RenderTargetBitmap bitmap, string destinationPath)
    {
        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = File.Create(destinationPath);
        encoder.Save(stream);
    }
}
