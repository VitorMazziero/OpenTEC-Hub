using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using OpenTECHub.Services.Alarms;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Control;
using OpenTECHub.Services.Diagnostics;
using OpenTECHub.Services.Dialogs;
using OpenTECHub.Services.KlaMapping;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.PowerMapping;
using OpenTECHub.Services.PowerTesting;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Platform;
using OpenTECHub.Services.Recipes;
using OpenTECHub.Services.Safety;
using OpenTECHub.Services.Telemetry;
using OpenTECHub.Services.Theme;
using OpenTECHub.ViewModels;

namespace OpenTECHub;

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
    private IServiceProvider? _testServices;
    public IServiceProvider? Services
    {
        get => _testServices ?? _services;
        internal set => _testServices = value;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Any unhandled exception must reach the log before the process dies, or a
        // field crash leaves nothing to diagnose.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var playback = ParseKlaPlaybackOptions(e.Args);
        var workspace = ParseWorkspaceStartupOptions(e.Args);
        PromptOrInitializeWorkspace(
            explicitWorkspace: workspace.Path,
            skipPrompt: workspace.SkipPrompt || playback is not null);
        ConfigureLogging();
        WireBindingDiagnostics();

        var services = new ServiceCollection();
        ConfigureServices(services, playback);
        _services = services.BuildServiceProvider();

        var settings = _services.GetRequiredService<ISettingsService>();
        var theme = _services.GetRequiredService<IThemeService>();
        theme.Apply(settings.Current.Theme);

        var shell = _services.GetRequiredService<ShellViewModel>();
        var navOption = ParseNavigationStartupOption(e.Args);
        if (!string.IsNullOrWhiteSpace(navOption))
        {
            shell.SelectedNavigationId = navOption;
        }
        else if (playback is not null)
        {
            shell.SelectedNavigationId = "kla-determination";
        }
        var window = new MainWindow(settings, theme) { DataContext = shell };
        if (playback is not null)
        {
            window.Title = $"OpenTEC-Hub — SIMULAÇÃO kLa: {playback.DisplayName}";
        }

        var exitAfterMs = ParseExitAfterMsStartupOption(e.Args);
        if (exitAfterMs > 0)
        {
            window.ContentRendered += async (_, _) =>
            {
                await Task.Delay(exitAfterMs);
                Current.Shutdown();
            };
        }

        window.ContentRendered += OnShellRendered;
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// Routes WPF data-binding failures into the log.
    /// </summary>
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

    private static void PromptOrInitializeWorkspace(string? explicitWorkspace = null, bool skipPrompt = false)
    {
        var configured = AppPaths.ReadConfiguredWorkspace();
        if (!string.IsNullOrWhiteSpace(explicitWorkspace))
        {
            AppPaths.InitializeWorkspace(Path.GetFullPath(explicitWorkspace));
            return;
        }

        if (skipPrompt)
        {
            AppPaths.InitializeWorkspace(
                !string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured)
                    ? configured
                    : AppPaths.DefaultDataDirectory);
            return;
        }

        var initialDir = !string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured)
            ? configured
            : (Directory.Exists(AppPaths.DefaultDataDirectory)
                ? AppPaths.DefaultDataDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Selecione a Pasta Raiz do OpenTEC-Hub (Workspace / Sessão Global)",
            InitialDirectory = initialDir,
            Multiselect = false,
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            AppPaths.InitializeWorkspace(dialog.FolderName);
        }
        else
        {
            AppPaths.InitializeWorkspace(configured ?? AppPaths.DefaultDataDirectory);
        }
    }

    private sealed record WorkspaceStartupOptions(string? Path, bool SkipPrompt);

    private static WorkspaceStartupOptions ParseWorkspaceStartupOptions(IReadOnlyList<string> args)
    {
        string? path = null;
        var skipPrompt = false;
        for (var index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index], "--no-workspace-prompt", StringComparison.OrdinalIgnoreCase))
            {
                skipPrompt = true;
            }
            else if (string.Equals(args[index], "--workspace", StringComparison.OrdinalIgnoreCase) &&
                     index + 1 < args.Count && !string.IsNullOrWhiteSpace(args[index + 1]))
            {
                path = args[++index];
                skipPrompt = true;
            }
        }

        return new WorkspaceStartupOptions(path, skipPrompt);
    }

    private static void ConfigureLogging()
    {
        var logPath = Path.Combine(AppPaths.LogDirectory, "opentechub-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("=== OpenTEC-Hub starting ===");
    }

    private static string? ParseNavigationStartupOption(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if ((args[i].Equals("--nav", StringComparison.OrdinalIgnoreCase) ||
                 args[i].Equals("--page", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < args.Count)
            {
                return args[++i];
            }
        }
        return null;
    }

    private static int ParseExitAfterMsStartupOption(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].Equals("--exit-after-ms", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < args.Count &&
                int.TryParse(args[++i], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                return Math.Max(0, parsed);
            }
        }
        return 0;
    }

    private static KlaPlaybackOptions? ParseKlaPlaybackOptions(IReadOnlyList<string> args)
    {
        string? file = null;
        var speed = 10.0;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].Equals("--kla-test-file", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                file = args[++i];
            }
            else if (args[i].Equals("--kla-test-speed", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count &&
                     double.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                         System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                speed = parsed;
            }
        }
        return string.IsNullOrWhiteSpace(file) ? null : new KlaPlaybackOptions(Path.GetFullPath(file), Math.Clamp(speed, 0.1, 100));
    }

    internal static void ConfigureServices(IServiceCollection services, KlaPlaybackOptions? playback)
    {
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(dispose: true);
        });

        services.AddSingleton<ISettingsService>(sp =>
            new SettingsService(sp.GetRequiredService<ILogger<SettingsService>>()));

        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<ICrashReporter, CrashReporter>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IFileInteractionService, FileInteractionService>();
        services.AddSingleton<IApplicationRestartService, ApplicationRestartService>();
        services.AddSingleton<IBackupService, BackupService>();
        services.AddSingleton<IWorkspaceMigrationService, WorkspaceMigrationService>();
        services.AddSingleton<IKlaMappingEngine, KlaMappingEngine>();
        services.AddSingleton<IKlaProfileStore>(_ => new KlaProfileStore(AppPaths.KlaMappingDirectory));
        services.AddSingleton<IKlaTestStore>(_ => new KlaTestStore(AppPaths.KlaTestsDirectory));
        services.AddSingleton<IKlaAnalysisEngine, KlaAnalysisEngine>();
        services.AddSingleton<IKlaTestRunner, KlaTestRunner>();
        services.AddSingleton<IPowerTestStore>(_ => new PowerTestStore(AppPaths.PowerTestsDirectory));
        services.AddSingleton<IPowerMapStore>(_ => new PowerMapStore(AppPaths.PowerMapsDirectory));
        services.AddSingleton<IPowerAnalysisEngine, PowerAnalysisEngine>();
        services.AddSingleton<IPowerMapEngine, PowerMapEngine>();
        services.AddSingleton<IKlaPowerIntegrationService, KlaPowerIntegrationService>();

        // The dispatcher captured here is the UI one, because the container is built
        // on the UI thread during OnStartup. DeviceService uses it to marshal
        // telemetry, so ViewModels never have to think about threads.
        if (playback is null)
        {
            services.AddSingleton<DeviceService>(sp => new DeviceService(
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<ILogger<DeviceService>>(),
                sp.GetRequiredService<ILoggerFactory>(),
                Dispatcher.CurrentDispatcher));
        }
        else
        {
            services.AddSingleton(playback);
            services.AddSingleton<KlaPlaybackDeviceService>(sp => new KlaPlaybackDeviceService(
                playback,
                sp.GetRequiredService<ILogger<KlaPlaybackDeviceService>>(),
                Dispatcher.CurrentDispatcher));
        }

        services.AddSingleton(TimeProvider.System);

        // One command arbiter owns the wire. It decorates the transport wrapper, so the
        // IDeviceService everything else resolves IS the arbiter: a plain Send is a Manual
        // dispatch, and nothing can reach the wire without an owner. The same instance is
        // exposed as ICommandArbiter for the ownership and lifecycle surface.
        services.AddSingleton<CommandArbiter>(sp => new CommandArbiter(
            playback is null
                ? sp.GetRequiredService<DeviceService>()
                : sp.GetRequiredService<KlaPlaybackDeviceService>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<CommandArbiter>>()));
        services.AddSingleton<IDeviceService>(sp => sp.GetRequiredService<CommandArbiter>());
        services.AddSingleton<ICommandArbiter>(sp => sp.GetRequiredService<CommandArbiter>());

        // Manual sends that report whether they were accepted. IDeviceService.Send is void
        // and swallows an ownership refusal; a card that commits on it can persist a
        // setpoint the hardware never received (AUD-003).
        services.AddSingleton<IManualDispatcher>(sp =>
            new ManualDispatcher(sp.GetRequiredService<IDeviceService>()));

        services.AddSingleton<ITelemetryHistory>(_ => new TelemetryHistory());

        // The oxygen cascade subscribes to telemetry and actuates only while engaged,
        // after command ownership and connection preconditions are satisfied.
        services.AddSingleton<ICascadeService, CascadeService>();

        // The conditional-OUR soft sensor (WP8). Observes DOT, airflow and the commanded
        // agitation against the active published kLa map; it never actuates.
        services.AddSingleton<IOurSoftSensor, OurSoftSensorService>();

        services.AddSingleton<ISessionLogger, SessionLogger>();
        services.AddSingleton<ISessionFileService, SessionFileService>();
        services.AddSingleton<IEventJournal, EventJournal>();

        // The operational safety alarm engine (Phase 2 WP4). It watches the arbiter and the
        // link, latches the six system alarms, journals them and drives the audible indication.
        services.AddSingleton<IAlarmAnnunciator, AlarmAnnunciator>();
        services.AddSingleton<IAlarmService, AlarmService>();

        // The recipe execution engine (Phase 3 WP4). It drives the same arbiter as manual control
        // under CommandOwner.Recipe, so starting a recipe claims the wire and deactivates the
        // manual surfaces; a link loss safe-aborts it.
        services.AddSingleton<IRecipeEngine>(sp => new RecipeEngine(
            sp.GetRequiredService<ICommandArbiter>(),
            sp.GetRequiredService<IDeviceService>(),
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IEventJournal>(),
            klaStore: sp.GetService<IKlaProfileStore>()));

        // Recipes are saved as versioned JSON in the per-user recipes folder (Minhas Receitas).
        services.AddSingleton<IRecipeStore>(_ => new RecipeStore(AppPaths.RecipesDirectory));

        // The phase-1 power assay uses the same ownership and cultivation gates as the other
        // automatic workflows. The runner is telemetry-driven and contains no UI dependency.
        services.AddSingleton<IPowerTestInterlock, PowerTestInterlock>();
        services.AddSingleton<IPowerTestRunner, PowerTestRunner>();

        // The safety coordinator (AUD-001) resolves ownership conflicts during safe stops,
        // revokes automation ownerships across recipes, cascade, and assays, and guarantees
        // safe zero-setpoint dispatch to the wire.
        services.AddSingleton<ISafetyCoordinator>(sp => new SafetyCoordinator(
            sp.GetRequiredService<ICommandArbiter>(),
            sp.GetRequiredService<IDeviceService>(),
            recipeEngine: sp.GetService<IRecipeEngine>(),
            cascade: sp.GetService<ICascadeService>(),
            klaRunner: sp.GetService<IKlaTestRunner>(),
            powerRunner: sp.GetService<IPowerTestRunner>(),
            sp.GetService<ILogger<SafetyCoordinator>>()));

        services.AddSingleton<ConnectionViewModel>();
        services.AddSingleton<ChartsViewModel>();
        services.AddSingleton<HistoricalViewModel>();
        services.AddSingleton<EventsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<PHControlViewModel>();
        services.AddSingleton<NutrientControlViewModel>();
        services.AddSingleton<AntifoamControlViewModel>();
        services.AddSingleton<FoamControlViewModel>();
        services.AddSingleton<FlaskAgitatorViewModel>();
        services.AddSingleton<BiomassControlViewModel>();
        services.AddSingleton<PumpControlViewModel>();
        // Constructed explicitly so the journal reaches it: the servo link carries no
        // acknowledgement, so the record of a command being sent and of it visibly
        // taking effect is the only trace an operator will have months later.
        services.AddSingleton(sp => new ServoDriveViewModel(
            sp.GetRequiredService<IDeviceService>(),
            journal: sp.GetRequiredService<IEventJournal>()));
        services.AddSingleton<CalibrationViewModel>();
        services.AddSingleton<KlaDeterminationViewModel>();
        services.AddSingleton<KlaMappingViewModel>();
        services.AddSingleton<PowerMapViewModel>();
        services.AddSingleton<PowerTestViewModel>();
        services.AddSingleton<ReceitasViewModel>();
        services.AddSingleton<ShellViewModel>();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashReporter.GenerateAndSaveReport(e.Exception, "DispatcherUnhandledException", isTerminating: false);

        // Keep running: an operator mid-cultivation is better served by a degraded
        // window than by the controller vanishing.
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception ?? new InvalidOperationException($"Unhandled domain exception: {e.ExceptionObject}");
        if (CrashReporter.IsShutdownCrtUnloadException(ex))
        {
            Log.Warning("Exceção inofensiva de descarregamento CRT/WPF suprimida durante o encerramento do processo.");
            return;
        }

        CrashReporter.GenerateAndSaveReport(ex, "AppDomain.CurrentDomain.UnhandledException", isTerminating: e.IsTerminating);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        CrashReporter.GenerateAndSaveReport(e.Exception, "TaskScheduler.UnobservedTaskException", isTerminating: false);
        e.SetObserved();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("=== OpenTEC-Hub exiting ===");

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
