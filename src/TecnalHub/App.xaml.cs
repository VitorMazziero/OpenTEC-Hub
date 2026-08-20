using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Control;
using TecnalHub.Services.Dialogs;
using TecnalHub.Services.Persistence;
using TecnalHub.Services.Platform;
using TecnalHub.Services.Telemetry;
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
        WireBindingDiagnostics();

        var services = new ServiceCollection();
        ConfigureServices(services);
        _services = services.BuildServiceProvider();

        var settings = _services.GetRequiredService<ISettingsService>();
        _services.GetRequiredService<IThemeService>().Apply(settings.Current.Theme);

        var shell = _services.GetRequiredService<ShellViewModel>();
        var window = new MainWindow(settings) { DataContext = shell };

        window.ContentRendered += OnShellRendered;
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// Routes WPF data-binding failures into the log.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A failed binding is silent.</b> WPF resolves it to nothing, the property keeps
    /// its default, and the window renders looking almost right. That is how
    /// <c>{Binding SelectedSubsystem, RelativeSource={RelativeSource AncestorType=Window}}</c>
    /// - which asks the Window object for a property only its DataContext has - put two
    /// detail panes on screen at once during Phase 1b, with nothing anywhere reporting a
    /// problem.
    /// </para>
    /// <para>
    /// Debug only: the listener costs a trace hop per binding failure, and a shipped
    /// build should have none left to report.
    /// </para>
    /// </remarks>
    [Conditional("DEBUG")]
    private static void WireBindingDiagnostics()
    {
        PresentationTraceSources.Refresh();

        var source = PresentationTraceSources.DataBindingSource;
        source.Listeners.Add(new BindingFailureListener());
        source.Switch.Level = SourceLevels.Warning | SourceLevels.Error;
    }

    /// <summary>Writes binding-failure traces to Serilog.</summary>
    private sealed class BindingFailureListener : TraceListener
    {
        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                Serilog.Log.Warning("XAML binding failure: {Message}", message);
            }
        }
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
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IFileInteractionService, FileInteractionService>();

        // The dispatcher captured here is the UI one, because the container is built
        // on the UI thread during OnStartup. DeviceService uses it to marshal
        // telemetry, so ViewModels never have to think about threads.
        services.AddSingleton<DeviceService>(sp => new DeviceService(
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<ILogger<DeviceService>>(),
            sp.GetRequiredService<ILoggerFactory>(),
            Dispatcher.CurrentDispatcher));

        services.AddSingleton(TimeProvider.System);

        // One command arbiter owns the wire. It decorates the transport wrapper, so the
        // IDeviceService everything else resolves IS the arbiter: a plain Send is a Manual
        // dispatch, and nothing can reach the wire without an owner. The same instance is
        // exposed as ICommandArbiter for the ownership and lifecycle surface.
        services.AddSingleton<CommandArbiter>(sp => new CommandArbiter(
            sp.GetRequiredService<DeviceService>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<CommandArbiter>>()));
        services.AddSingleton<IDeviceService>(sp => sp.GetRequiredService<CommandArbiter>());
        services.AddSingleton<ICommandArbiter>(sp => sp.GetRequiredService<CommandArbiter>());

        // The advisory oxygen cascade. It subscribes to telemetry and computes, but never
        // sends - live actuation waits for command ownership and the bioreactor.
        services.AddSingleton<ICascadeService, CascadeService>();

        services.AddSingleton<ITelemetryHistory>(_ => new TelemetryHistory());
        services.AddSingleton<ISessionLogger, SessionLogger>();
        services.AddSingleton<ISessionFileService, SessionFileService>();
        services.AddSingleton<IEventJournal, EventJournal>();

        services.AddSingleton<ConnectionViewModel>();
        services.AddSingleton<ChartsViewModel>();
        services.AddSingleton<HistoricalViewModel>();
        services.AddSingleton<EventsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<PHControlViewModel>();
        services.AddSingleton<CalibrationViewModel>();
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
            // The container owns the async-only settings, session-log and device
            // services. A synchronous Dispose throws after an otherwise clean exit;
            // DisposeAsync closes them in reverse registration order and also disposes
            // the synchronous ViewModels and theme service exactly once.
            services.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
