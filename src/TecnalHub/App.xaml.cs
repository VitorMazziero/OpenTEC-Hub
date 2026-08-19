using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Persistence;
using TecnalHub.Services.Theme;
using TecnalHub.ViewModels;

namespace TecnalHub;

/// <summary>
/// Application entry point and composition root.
/// </summary>
/// <remarks>
/// <para>
/// The only place services are constructed. Everything else takes its dependencies
/// through the constructor - see <c>docs/ARCHITECTURE.md</c>.
/// </para>
/// <para>
/// <b>Startup budget: first frame under 2 s.</b> Nothing here may block on I/O.
/// Reaching the device is kicked off from <c>ContentRendered</c>, after the window is
/// visible - never before. v.6's cold start was dominated by exactly this kind of
/// work happening ahead of the first frame.
/// </para>
/// </remarks>
public partial class App : Application
{
    private readonly Stopwatch _startupTimer = Stopwatch.StartNew();

    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Any unhandled exception must reach the log before the process dies, or a
        // field crash leaves nothing to diagnose.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        ConfigureLogging();

        var services = new ServiceCollection();
        ConfigureServices(services);
        _services = services.BuildServiceProvider();

        var settings = _services.GetRequiredService<ISettingsService>();
        _services.GetRequiredService<IThemeService>().Apply(settings.Current.Theme);

        var shell = _services.GetRequiredService<ShellViewModel>();
        var window = new MainWindow { DataContext = shell };

        window.ContentRendered += OnShellRendered;
        MainWindow = window;
        window.Show();
    }

    private void OnShellRendered(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            window.ContentRendered -= OnShellRendered;
        }

        var log = _services?.GetRequiredService<ILogger<App>>();
        log?.LogInformation(
            "First frame after {ElapsedMs} ms (budget 2000 ms)",
            _startupTimer.ElapsedMilliseconds);

        // Only now do we touch hardware.
        _services?.GetRequiredService<ShellViewModel>().StartAutoConnect();
    }

    private static void ConfigureLogging()
    {
        var logPath = Path.Combine(AppPaths.LogDirectory, "tecnalhub-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("=== TECNAL-Hub starting ===");
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(dispose: true);
        });

        services.AddSingleton<ISettingsService>(sp =>
            new SettingsService(sp.GetRequiredService<ILogger<SettingsService>>()));

        services.AddSingleton<IThemeService, ThemeService>();

        // The dispatcher captured here is the UI one, because the container is built
        // on the UI thread during OnStartup. DeviceService uses it to marshal
        // telemetry, so ViewModels never have to think about threads.
        services.AddSingleton<IDeviceService>(sp => new DeviceService(
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<ILogger<DeviceService>>(),
            sp.GetRequiredService<ILoggerFactory>(),
            Dispatcher.CurrentDispatcher));

        services.AddSingleton<ConnectionViewModel>();
        services.AddSingleton<ShellViewModel>();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "Unhandled exception on the UI thread");

        MessageBox.Show(
            $"Ocorreu um erro inesperado.\n\n{e.Exception.Message}\n\n" +
            $"O registro completo está em:\n{AppPaths.LogDirectory}",
            "TECNAL-Hub",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        // Keep running: an operator mid-cultivation is better served by a degraded
        // window than by the controller vanishing.
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception outside the UI thread");
        Log.CloseAndFlush();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("=== TECNAL-Hub exiting ===");

        if (_services is { } services)
        {
            // Flush settings and close the link before the process goes away.
            services.GetRequiredService<ISettingsService>().SaveNowAsync().GetAwaiter().GetResult();

            if (services.GetRequiredService<IDeviceService>() is IAsyncDisposable device)
            {
                device.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            services.Dispose();
        }

        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
