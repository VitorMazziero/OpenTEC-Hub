using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TecnalHub.Protocol;
using TecnalHub.Services.Alarms;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Control;
using TecnalHub.Services.Dialogs;
using TecnalHub.Services.KlaMapping;
using TecnalHub.Services.Persistence;
using TecnalHub.Services.Recipes;
using TecnalHub.Services.Telemetry;
using TecnalHub.Services.Theme;

namespace TecnalHub.ViewModels;

/// <summary>One selectable owner, with the reason it may not be selectable yet.</summary>
public sealed record CommandOwnerOption(CommandOwner Owner, string Label, string? UnavailableReason)
{
    public bool IsAvailable => UnavailableReason is null;

    public override string ToString() => Label;
}

/// <summary>A variable offered to the KPI strip's configuration list.</summary>
public sealed partial class KpiOption : ObservableObject
{
    public KpiOption(ProcessVariableViewModel variable, bool isPinned)
    {
        Variable = variable;
        IsPinned = isPinned;
    }

    public ProcessVariableViewModel Variable { get; }

    public string DisplayName => Variable.DisplayName;

    [ObservableProperty]
    public partial bool IsPinned { get; set; }
}

/// <summary>A destination in the navigation rail.</summary>
/// <param name="Id">Stable identifier, used for selection and persistence.</param>
/// <param name="Label">pt-BR text shown in the rail.</param>
/// <param name="Glyph">
/// <para>
/// Short icon name, resolved by <c>Icon.Key</c> against
/// <c>Resources/Icons/Icons.xaml</c>. A plain string rather than a <c>Geometry</c> so
/// no WPF type reaches the ViewModel.
/// </para>
/// <para>
/// These were previously Segoe MDL2 Assets codepoints (<c></c> and friends).
/// Nothing ever set an icon font, so they would have rendered as tofu had anything
/// bound them - and an icon font would have been the wrong answer regardless, because
/// the Fluent set is Windows 11 only. See the header of <c>Icons.xaml</c>.
/// </para>
/// </param>
/// <param name="StartsGroup">
/// Draws a divider above this item. The rail is grouped Operação / Registro / Sistema,
/// with a rule and no heading - a heading per group would cost more vertical space than
/// the grouping saves at four destinations.
/// </param>
public sealed record NavigationItem(string Id, string Label, string Glyph, bool StartsGroup = false);

/// <summary>One searchable page or action exposed by the Ctrl+K palette.</summary>
public sealed record CommandPaletteEntry(
    string Id,
    string Label,
    string Hint,
    bool IsAvailable = true,
    string UnavailableReason = "");

/// <summary>
/// Shell state: navigation, the always-visible KPI strip, and the live variables.
/// </summary>
/// <remarks>
/// Owns the single set of <see cref="ProcessVariableViewModel"/> instances that the
/// KPI strip, synoptic and detail pane all bind to, so those three views can never
/// disagree about a value. See <c>docs/UI_DESIGN.md</c> section 4.
/// </remarks>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceService _device;
    private readonly IAlarmService _alarms;
    private readonly ISettingsService _settings;
    private readonly NutrientControlViewModel _nutrientControl;
    private readonly IThemeService _theme;
    private readonly ITelemetryHistory _history;
    private readonly ISessionLogger _sessionLogger;
    private readonly IDialogService _dialogs;
    private readonly IRecipeEngine _recipeEngine;
    private readonly ILogger<ShellViewModel> _log;
    private readonly IReadOnlyList<CommandPaletteEntry> _commandPaletteCatalog;
    private UnitSettings _appliedUnits;

    /// <summary>Drives the liveness check and the status-bar clock.</summary>
    private readonly DispatcherTimer _tick;

    /// <summary>When the last accepted telemetry frame arrived.</summary>
    private DateTimeOffset? _lastFrameAt;

    /// <summary>Suppresses persistence while the constructor seeds the UI state.</summary>
    private readonly bool _uiLoaded;

    public ShellViewModel(
        IDeviceService device,
        ISettingsService settings,
        IThemeService theme,
        ConnectionViewModel connection,
        ChartsViewModel charts,
        HistoricalViewModel historical,
        EventsViewModel events,
        SettingsViewModel settings_,
        PHControlViewModel phControl,
        NutrientControlViewModel nutrientControl,
        AntifoamControlViewModel antifoamControl,
        FoamControlViewModel foamControl,
        FlaskAgitatorViewModel flaskAgitator,
        BiomassControlViewModel biomassControl,
        PumpControlViewModel pumpControl,
        CalibrationViewModel calibration,
        KlaDeterminationViewModel klaDetermination,
        KlaMappingViewModel klaMapping,
        IKlaProfileStore klaProfileStore,
        IDialogService dialogs,
        ICascadeService cascade,
        IOurSoftSensor ourSensor,
        IAlarmService alarms,
        ITelemetryHistory history,
        ISessionLogger sessionLogger,
        IRecipeEngine recipeEngine,
        ReceitasViewModel receitas,
        ILogger<ShellViewModel> log)
    {
        _device = device;
        _alarms = alarms;
        _settings = settings;
        _theme = theme;
        _history = history;
        _sessionLogger = sessionLogger;
        _dialogs = dialogs;
        _log = log;
        Connection = connection;
        Charts = charts;
        Historical = historical;
        Events = events;
        Settings = settings_;
        PHControl = phControl;
        Calibration = calibration;
        KlaDetermination = klaDetermination;
        KlaMapping = klaMapping;
        Receitas = receitas;
        _recipeEngine = recipeEngine;
        _recipeEngine.StateChanged += OnRecipeStateChanged;
        _appliedUnits = settings.Current.Units;

        Temperature = new ProcessVariableViewModel("temperature", "Temperatura", "°C", decimals: 1, channel: TelemetryChannel.Temperature);
        // No RPM feedback exists on the wire, so this variable can only ever show
        // what was commanded.
        Motor = new ProcessVariableViewModel(
            "motor", "Agitação", "rpm", decimals: 0, isCommandedOnly: true,
            channel: TelemetryChannel.MotorRpm);
        // pH is parsed, spike-filtered, calibrated and echoed back to the device as
        // pHCal in Phase 1, and it is already offered as a chart channel. It had no
        // tile, so the charts advertised a variable the dashboard denied existed.
        // The five-field dosing contract already exists in v.6 and in the firmware.
        // Probe calibration remains app-side; control is now exposed separately.
        Ph = new ProcessVariableViewModel("ph", "pH", "", decimals: 2, channel: TelemetryChannel.PH);
        Oxygen = new ProcessVariableViewModel("oxygen", "Oxigênio", "%", decimals: 1, channel: TelemetryChannel.Oxygen);
        Flow = new ProcessVariableViewModel("flow", "Vazão", "L/min", decimals: 2, channel: TelemetryChannel.Flow);
        Pressure = new ProcessVariableViewModel("pressure", "Pressão", "mmHg", decimals: 1, isControllable: false,
            channel: TelemetryChannel.Pressure);

        // WP7 dosing auxiliaries on the synoptic. Nutrient has no telemetry — it is a
        // commanded-only pump, so it shows a commanded duty cycle. Antifoam's figure has
        // no documented unit, so it is unitless. Level is the distance/foam sensor in mm.
        _nutrientControl = nutrientControl;
        Nutrient = new ProcessVariableViewModel(
            "nutrient", "Nutriente", "%", decimals: 0, isControllable: false, isCommandedOnly: true,
            detailNote: "Bomba comandada, sem realimentação — o valor é o ciclo útil. Ajuste a dosagem em Controle → Dosagem — Nutriente.");
        Antifoam = new ProcessVariableViewModel(
            "antifoam", "Antiespumante", "", decimals: 2, isControllable: false,
            channel: TelemetryChannel.Antifoam,
            detailNote: "Figura sem unidade documentada. Ajuste a bomba em Controle → Dosagem — Antiespumante.");
        Level = new ProcessVariableViewModel(
            "level", "Nível", "mm", decimals: 0, isControllable: false,
            channel: TelemetryChannel.Distance,
            detailNote: "Nível/espuma pelo sensor de distância. Configure o sensor em Controle → Controle de espuma.");

        // Phase 3 sensors on the synoptic. Biomass shows absorbance (AU); a cells/mL curve
        // needs a growth calibration that does not exist yet. The external pump shows its
        // reported flow. Both are read-only measurements configured on Controle.
        Biomass = new ProcessVariableViewModel(
            "biomass", "Biomassa", "Abs", decimals: 3, isControllable: false,
            channel: TelemetryChannel.Biomass,
            detailNote: "Absorbância do sensor óptico. Ative o sensor e capture o branco em Controle → Biomassa.");
        Pump = new ProcessVariableViewModel(
            "pump", "Bomba externa", "mL/min", decimals: 3, isControllable: false,
            channel: TelemetryChannel.PumpFlow,
            detailNote: "Vazão informada pela bomba peristáltica. Configure o perfil em Controle → Bomba externa.");

        // KPI-strip order, which is also the variable-rail order.
        Variables = [Temperature, Ph, Oxygen, Motor, Flow, Pressure, Nutrient, Antifoam, Level, Biomass, Pump];

        // Ranges come from docs/PROTOCOL.md section 3.1 and are paired with the
        // command builders, so validation and the wire cannot drift apart.
        var setpoints = settings.Current.Setpoints;
        var maxFlow = setpoints.MaxFlowLitresPerMinute;
        FlowControl = new FlowControlViewModel(maxFlow);

        // The synoptic shows presence for every external node, not only the flowmeter, so
        // it needs the same status objects Controle binds to. Exposed rather than
        // duplicated: two copies of "is this device there" is how they end up disagreeing.
        BiomassControl = biomassControl;
        PumpControl = pumpControl;
        FoamControl = foamControl;
        FlaskAgitator = flaskAgitator;

        Subsystems =
        [
            new SubsystemViewModel(Temperature,
                new SubsystemSpec(15, 60, IsInteger: false,
                    value => TecnalCommand.Create().Set(CommandKeys.TempSetpoint, value),
                    () => TecnalCommand.Create().Set(CommandKeys.TempSetpoint, 0.0)),
                device, setpoints.TemperatureCelsius, setpoints.TemperatureEnabled),

            new SubsystemViewModel(Motor,
                new SubsystemSpec(50, 1000, IsInteger: true,
                    value => CommandBuilders.MotorSetpoint((int)value),
                    () => CommandBuilders.MotorSetpoint(0),
                    // The wire carries no RPM key at all, so there is no signal whose
                    // health could be reported.
                    HasHealth: false),
                device, setpoints.MotorRpm, setpoints.MotorEnabled),

            new SubsystemViewModel(Oxygen,
                new SubsystemSpec(0, 100, IsInteger: false,
                    value => TecnalCommand.Create().Set(CommandKeys.OxygenMonitor, 100.0),
                    () => TecnalCommand.Create().Set(CommandKeys.OxygenMonitor, 0.0),
                    // Oxygen is the one device with an app-side controller the app can observe,
                    // so it carries the Cascata / PID / Saída tabs. Their content is the live
                    // cascade state (ShellViewModel.CascadeDetail); it is controlled on Controle.
                    HasOutput: true, HasCascade: true, HasPid: true, HasCalibration: true, HasSetpointEntry: false),
                device, 100.0, setpoints.OxygenEnabled),

            new SubsystemViewModel(Flow,
                new SubsystemSpec(0, maxFlow, IsInteger: false,
                    // The loop flag rides alongside the setpoint rather than inside it: the
                    // v05 frame stays exactly the reliable keys, and the Hub still learns that
                    // the loop is on (see CommandBuilders.FlowmeterLoopEnabled).
                    value => FlowControl.BuildSetpointUsingObservedValves(value)
                        .Merge(CommandBuilders.FlowmeterLoopEnabled(true)),
                    // Safe-stop, not merely zero flow: both valves are forced closed,
                    // because leaving nitrogen open through a stop is a hazard.
                    () => FlowControl.BuildSafeStop().Merge(CommandBuilders.FlowmeterLoopEnabled(false)),
                    // Valve states and the vent flag come back on the wire, so the app
                    // can show what the actuator is doing rather than only what it asked.
                    HasOutput: true, HasCalibration: true,
                    OnCommitted: (_, enabled) => FlowControl.CommitFromFlowSetpoint(enabled),
                    CanApplyNow: () => FlowControl.CanSendFlowCommands),
                device, setpoints.FlowLitresPerMinute, setpoints.FlowEnabled),

            new SubsystemViewModel(Pressure,
                new SubsystemSpec(1, 380, IsInteger: false,
                    value => TecnalCommand.Create().Set(CommandKeys.PressureReference, value),
                    () => TecnalCommand.Create().Set(CommandKeys.PressureReference, 0.0)),
                device, setpoints.PressureKilopascal, setpoints.PressureEnabled),
        ];

        ApplyUnits(_appliedUnits);

        SelectedVariable = Temperature;
        SelectedSubsystem = Subsystems[0];
        Control = new ControlViewModel(
            Subsystems, FlowControl, PHControl, nutrientControl, antifoamControl, foamControl, flaskAgitator,
            biomassControl, pumpControl, device, settings, dialogs, cascade, receitas, klaProfileStore,
            alarms);
        CascadeDetail = new CascadeDetailViewModel(cascade);
        Our = new OurViewModel(ourSensor);

        // Nutrient is commanded-only: reflect its applied duty cycle onto the synoptic tile.
        _nutrientControl.PropertyChanged += OnNutrientCommandChanged;

        NavigationItems =
        [
            new NavigationItem("dashboard", "Painel", "Vessel"),
            new NavigationItem("control", "Controle", "Sliders"),
            new NavigationItem("recipes", "Receitas", "NodeGraph"),
            new NavigationItem("charts", "Gráficos", "Trend", StartsGroup: true),
            new NavigationItem("history", "Históricos", "Export"),
            new NavigationItem("events", "Eventos", "EventLog"),
            new NavigationItem("calibrations", "Calibrações", "Target", StartsGroup: true),
            new NavigationItem("settings", "Configurações", "Gear"),
            new NavigationItem("kla-determination", "Determinar kLa", "Target", StartsGroup: true),
            new NavigationItem("kla-mapping", "Mapeamento kLa", "NodeGraph"),
        ];
        _commandPaletteCatalog =
        [
            .. NavigationItems.Select((item, index) =>
                new CommandPaletteEntry($"nav:{item.Id}", item.Label, $"Ctrl+{index + 1}")),
            new("rail", "Alternar barra de variáveis", "Ctrl+R"),
            new("reconnect", "Reconectar ao equipamento", "F5"),
            new("charts-pause", "Pausar / continuar gráficos", "Espaço"),
            new("theme", "Alternar tema claro / escuro", ""),
            new("zero-time", "Zerar tempo da sessão", ""),
            new("recipe-save", "Salvar receita", "Ctrl+S", IsAvailable: false,
                UnavailableReason: "Disponível quando o editor de receitas entrar na Fase 3."),
        ];
        RebuildCommandPalette();

        SelectedNavigationId = NavigationItems.Any(item => item.Id == settings.Current.Ui.LastPage)
            ? settings.Current.Ui.LastPage
            : "dashboard";

        // Pinned set: what the operator last chose, else every variable this phase has.
        var pinned = settings.Current.Ui.PinnedKpis;
        foreach (var id in OrderedIds(pinned))
        {
            if (Variables.FirstOrDefault(v => v.Id == id) is { } variable)
            {
                KpiOptions.Add(new KpiOption(variable, pinned.Length == 0 || pinned.Contains(id)));
            }
        }

        foreach (var option in KpiOptions)
        {
            option.PropertyChanged += OnKpiOptionChanged;
        }

        RebuildPinned();
        IsVariableRailVisible = settings.Current.Ui.ShowVariableRail;

        _device.StateChanged += OnStateChanged;
        _device.TelemetryReceived += OnTelemetryReceived;
        _device.SessionTimeZeroed += OnSessionTimeZeroed;
        _alarms.Changed += OnAlarmsChanged;
        _settings.Changed += OnSettingsChanged;
        _sessionLogger.StatusChanged += OnSessionStatusChanged;
        Historical.OpenGraphsRequested += OnOpenGraphsRequested;

        var sessionPath = settings.Current.Logging.SessionLogPath;
        if (string.IsNullOrWhiteSpace(sessionPath) || !File.Exists(sessionPath))
        {
            var initialFileName = AppPaths.FormatSessionFileName(null);
            sessionPath = Path.Combine(AppPaths.SessionsDirectory, initialFileName);
            settings.Update(s => s with
            {
                Logging = s.Logging with { SessionLogPath = sessionPath }
            });
        }
        _sessionLogger.Start(sessionPath);
        _activeSessionName = Path.GetFileNameWithoutExtension(sessionPath);

        // 1 Hz: fast enough to notice a stalled link within one emission period, slow
        // enough to cost nothing. The device emits every 2 s.
        _tick = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _tick.Tick += OnTick;
        _tick.Start();

        _uiLoaded = true;
    }

    /// <summary>
    /// Variable ids in the operator's saved order, with anything unsaved appended.
    /// </summary>
    /// <remarks>
    /// A variable added by a later phase must appear rather than vanish because it was
    /// not in a preference file written before it existed.
    /// </remarks>
    private IEnumerable<string> OrderedIds(string[] saved)
        => saved.Where(id => Variables.Any(v => v.Id == id))
                .Concat(Variables.Select(v => v.Id).Where(id => !saved.Contains(id)));

    public ConnectionViewModel Connection { get; }

    public ChartsViewModel Charts { get; }

    public HistoricalViewModel Historical { get; }

    public EventsViewModel Events { get; }

    public SettingsViewModel Settings { get; }

    /// <summary>Complete pH dosing state shared by Painel, Controle and calibration interlock.</summary>
    public PHControlViewModel PHControl { get; }

    /// <summary>Guided pH, oxygen and airflow calibration procedures.</summary>
    public CalibrationViewModel Calibration { get; }

    /// <summary>Gassing-out kLa experimental testing and regression.</summary>
    public KlaDeterminationViewModel KlaDetermination { get; }

    /// <summary>Operator-created kLa experiments, paper path search and publication.</summary>
    public KlaMappingViewModel KlaMapping { get; }

    /// <summary>The graphical recipe editor and its execution engine (WP4).</summary>
    public ReceitasViewModel Receitas { get; }

    /// <summary>All-setpoints and valve-control page.</summary>
    public ControlViewModel Control { get; }

    /// <summary>Live cascade state behind the oxygen detail pane's Cascata/PID/Saída tabs.</summary>
    public CascadeDetailViewModel CascadeDetail { get; }

    /// <summary>Conditional-OUR soft-sensor readout on the cascade tuning workspace (WP8).</summary>
    public OurViewModel Our { get; }

    /// <summary>Shared staged/observed flow state used by both detail and Controle.</summary>
    public FlowControlViewModel FlowControl { get; }

    /// <summary>Biomass card state, for the synoptic's presence chips.</summary>
    public BiomassControlViewModel BiomassControl { get; }

    /// <summary>External-pump card state, for the synoptic's presence chips.</summary>
    public PumpControlViewModel PumpControl { get; }

    /// <summary>Level/foam card state, for the synoptic's presence chips.</summary>
    public FoamControlViewModel FoamControl { get; }

    /// <summary>
    /// Flask-agitator card state. Not a synoptic tile — see the legend note — but exposed
    /// so the shell can reach its presence without going through Controle.
    /// </summary>
    public FlaskAgitatorViewModel FlaskAgitator { get; }

    /// <summary>Ring-buffered telemetry, for the detail pane's inline trend.</summary>
    public ITelemetryHistory History => _history;

    /// <summary>Every live variable, in KPI-strip order.</summary>
    public IReadOnlyList<ProcessVariableViewModel> Variables { get; }

    public ProcessVariableViewModel Temperature { get; }

    public ProcessVariableViewModel Motor { get; }

    public ProcessVariableViewModel Ph { get; }

    public ProcessVariableViewModel Oxygen { get; }

    public ProcessVariableViewModel Flow { get; }

    public ProcessVariableViewModel Pressure { get; }

    /// <summary>Nutrient dosing — commanded-only duty cycle, on the synoptic (WP7).</summary>
    public ProcessVariableViewModel Nutrient { get; }

    /// <summary>Antifoam figure (unitless), on the synoptic (WP7).</summary>
    public ProcessVariableViewModel Antifoam { get; }

    /// <summary>Level/foam distance in millimetres, on the synoptic (WP7).</summary>
    public ProcessVariableViewModel Level { get; }

    /// <summary>Biomass absorbance (AU), on the synoptic (WP1).</summary>
    public ProcessVariableViewModel Biomass { get; }

    /// <summary>External-pump reported flow (mL/min), on the synoptic (WP2).</summary>
    public ProcessVariableViewModel Pump { get; }

    /// <summary>Controllable subsystems, aligned with <see cref="Variables"/>.</summary>
    public IReadOnlyList<SubsystemViewModel> Subsystems { get; }

    public IReadOnlyList<NavigationItem> NavigationItems { get; }

    public ObservableCollection<CommandPaletteEntry> CommandPaletteResults { get; } = [];

    [ObservableProperty]
    private string _activeSessionName = "";

    [ObservableProperty]
    private bool _isSessionNameSavedNotificationVisible;

    private System.Threading.CancellationTokenSource? _sessionNotificationCts;

    [RelayCommand]
    private void UpdateActiveSessionName()
    {
        if (string.IsNullOrWhiteSpace(ActiveSessionName))
        {
            ActiveSessionName = Path.GetFileNameWithoutExtension(_sessionLogger.CurrentPath) ?? Path.GetFileNameWithoutExtension(AppPaths.FormatSessionFileName(null));
            return;
        }

        var fileName = AppPaths.FormatSessionFileName(ActiveSessionName);
        var path = Path.Combine(AppPaths.SessionsDirectory, fileName);

        if (!string.Equals(_sessionLogger.CurrentPath, path, StringComparison.OrdinalIgnoreCase))
        {
            _sessionLogger.Stop();
            _settings.Update(s => s with
            {
                Logging = s.Logging with { SessionLogPath = path }
            });
            _sessionLogger.Start(path);
            ActiveSessionName = Path.GetFileNameWithoutExtension(path);

            Events.Journal.Add(
                AuditSource.Application,
                AuditSeverity.Information,
                $"Sessão alterada para: {ActiveSessionName}",
                path);
        }

        ShowSessionNameSavedFeedback();
    }

    private void ShowSessionNameSavedFeedback()
    {
        _sessionNotificationCts?.Cancel();
        _sessionNotificationCts = new System.Threading.CancellationTokenSource();
        var token = _sessionNotificationCts.Token;
        IsSessionNameSavedNotificationVisible = true;

        System.Threading.Tasks.Task.Delay(3000, token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    IsSessionNameSavedNotificationVisible = false;
                });
            }
        }, System.Threading.Tasks.TaskScheduler.Default);
    }

    // ── KPI strip configuration ──────────────────────────────────────────────

    /// <summary>Every variable, pinned or not, in strip order.</summary>
    public ObservableCollection<KpiOption> KpiOptions { get; } = [];

    /// <summary>The tiles actually shown, derived from <see cref="KpiOptions"/>.</summary>
    public ObservableCollection<ProcessVariableViewModel> PinnedVariables { get; } = [];

    /// <summary>Option B's variable rail, as the operator set it. Collapsed by default.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVariableRail))]
    public partial bool IsVariableRailVisible { get; set; }

    /// <summary>
    /// False when the window is too narrow to carry the rail as well.
    /// </summary>
    /// <remarks>
    /// Set by the shell's responsive switch. Kept separate from
    /// <see cref="IsVariableRailVisible"/> on purpose: narrowing the window must not
    /// silently rewrite a saved preference, so the rail comes back by itself when there
    /// is room for it again.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVariableRail))]
    public partial bool IsRailAffordable { get; set; } = true;

    /// <summary>Whether the rail is actually on screen: wanted, and affordable.</summary>
    public bool ShowVariableRail => IsVariableRailVisible && IsRailAffordable;

    /// <summary>
    /// True when the shell replaces navigation labels with the 52 px icon strip.
    /// </summary>
    /// <remarks>
    /// This is transient responsive state, never a preference: widening the window
    /// restores the complete rail automatically.
    /// </remarks>
    [ObservableProperty]
    public partial bool IsNavigationCompact { get; set; }

    // ── Liveness ─────────────────────────────────────────────────────────────

    /// <summary>
    /// True when no telemetry frame has arrived for several emission periods.
    /// </summary>
    /// <remarks>
    /// <b>A frozen link that keeps showing the last good frame is the failure this
    /// application exists to prevent.</b> The connection can be perfectly healthy at the
    /// socket level while the device has stopped emitting - the simulator has a
    /// dedicated <c>stall</c> scenario for exactly this. When it happens every readout
    /// goes to an em dash rather than continuing to display a measurement nobody took.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SystemState))]
    [NotifyPropertyChangedFor(nameof(SystemStateText))]
    public partial bool IsTelemetryStale { get; set; }

    /// <summary>Clock time of the last accepted frame, for the status bar.</summary>
    [ObservableProperty]
    public partial string LastUpdateText { get; set; } = "—";

    // ── Status bar ───────────────────────────────────────────────────────────

    /// <summary>Running recipe.</summary>
    [ObservableProperty]
    public partial string RecipeName { get; set; } = "—";

    [ObservableProperty]
    public partial string PhaseName { get; set; } = "—";

    /// <summary>
    /// What the recipe engine will do next. Only the engine can know this, so it stays
    /// an em dash until Phase 3 rather than inventing a countdown.
    /// </summary>
    [ObservableProperty]
    public partial string NextActionText { get; set; } = "—";

    public string LoggingSummary => _sessionLogger.IsLogging
        ? $"gravando · {_sessionLogger.RowsWritten} linhas"
        : "parado";

    /// <summary>
    /// One dot for "is the whole system healthy", composed from the link, the sensor
    /// module and telemetry liveness.
    /// </summary>
    public VariableState SystemState
    {
        get
        {
            if (_device.State is not Protocol.ConnectionState.Connected)
            {
                return VariableState.Alarm;
            }

            return IsTelemetryStale || IsSensorModuleOffline
                ? VariableState.Warning
                : VariableState.Ok;
        }
    }

    public string SystemStateText => SystemState switch
    {
        VariableState.Ok => "Sistema online",
        VariableState.Warning => IsTelemetryStale ? "Dados congelados" : "Módulo offline",
        _ => "Sistema offline",
    };

    // ── Operational alarms (WP4) ─────────────────────────────────────────────

    /// <summary>True while any system alarm is latched, so the banner shows.</summary>
    public bool HasAlarms => _alarms.HasActiveAlarms;

    /// <summary>The alarm the banner headlines, or null.</summary>
    public AlarmSnapshot? AlarmHeadline => _alarms.Headline;

    public string AlarmHeadlineText => AlarmHeadline?.Title ?? "";

    public string AlarmHeadlineDetail => AlarmHeadline?.Detail ?? "";

    /// <summary>The banner colour, reusing the process-state palette.</summary>
    public VariableState AlarmState => AlarmHeadline?.Severity == AlarmSeverity.Warning
        ? VariableState.Warning
        : VariableState.Alarm;

    /// <summary>
    /// A count suffix for the banner when more than one alarm is latched, e.g. "+2".
    /// </summary>
    public string AlarmMoreText
    {
        get
        {
            var others = _alarms.Snapshot().Count - 1;
            return others > 0 ? $"+{others}" : "";
        }
    }

    /// <summary>Whether the annunciator is currently sounding, so Silenciar is offered.</summary>
    public bool IsAlarmAudible => _alarms.IsAudible;

    /// <summary>Whether there is anything left to acknowledge.</summary>
    public bool HasUnacknowledgedAlarms => _alarms.AnnunciatingCount > 0;

    [RelayCommand]
    private void AcknowledgeAlarms() => _alarms.AcknowledgeAll();

    [RelayCommand]
    private void SilenceAlarms() => _alarms.Silence();

    private void OnAlarmsChanged()
    {
        OnPropertyChanged(nameof(HasAlarms));
        OnPropertyChanged(nameof(AlarmHeadline));
        OnPropertyChanged(nameof(AlarmHeadlineText));
        OnPropertyChanged(nameof(AlarmHeadlineDetail));
        OnPropertyChanged(nameof(AlarmState));
        OnPropertyChanged(nameof(AlarmMoreText));
        OnPropertyChanged(nameof(IsAlarmAudible));
        OnPropertyChanged(nameof(HasUnacknowledgedAlarms));
    }

    [ObservableProperty]
    public partial string SelectedNavigationId { get; set; }

    [ObservableProperty]
    public partial bool IsCommandPaletteOpen { get; set; }

    [ObservableProperty]
    public partial string CommandSearchText { get; set; } = "";

    [ObservableProperty]
    public partial CommandPaletteEntry? SelectedCommandPaletteEntry { get; set; }

    /// <summary>The variable whose controls fill the detail pane.</summary>
    [ObservableProperty]
    public partial ProcessVariableViewModel? SelectedVariable { get; set; }

    /// <summary>
    /// The subsystem behind <see cref="SelectedVariable"/>, or null when the selected
    /// variable is read-only (pressure has no controllable half in Phase 1 scope
    /// beyond its reference, which is exposed here for completeness).
    /// </summary>
    [ObservableProperty]
    public partial SubsystemViewModel? SelectedSubsystem { get; set; }

    /// <summary>pH uses a dedicated five-field detail panel rather than a scalar subsystem.</summary>
    public bool IsPHSelected => ReferenceEquals(SelectedVariable, Ph);

    /// <summary>True only for variables with neither scalar nor pH-specific controls.</summary>
    public bool ShowReadOnlyDetail
        => SelectedVariable is not null && SelectedSubsystem is null && !IsPHSelected;

    /// <summary>Elapsed run time reported by the device, formatted for the header.</summary>
    [ObservableProperty]
    public partial string ElapsedText { get; set; } = "—";

    /// <summary>
    /// True when the sensor module behind the ESP32 is not answering.
    /// </summary>
    /// <remarks>
    /// Independent of the PC link: the USB or Wi-Fi connection can be perfectly
    /// healthy while the module's internal UART is down. Conflating the two would tell
    /// the operator the wrong thing.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SystemState))]
    [NotifyPropertyChangedFor(nameof(SystemStateText))]
    public partial bool IsSensorModuleOffline { get; set; }

    [RelayCommand]
    private void Navigate(string id)
    {
        if (NavigationItems.Any(item => item.Id == id))
        {
            SelectedNavigationId = id;
        }
    }

    /// <summary>Opens a Controle table row in the process-first dashboard.</summary>
    [RelayCommand]
    private void OpenVariableFromControl(ProcessVariableViewModel variable)
    {
        SelectedVariable = variable;
        SelectedNavigationId = "dashboard";
    }

    private void OnOpenGraphsRequested() => SelectedNavigationId = "charts";

    partial void OnSelectedNavigationIdChanged(string value)
    {
        if (!_uiLoaded || !NavigationItems.Any(item => item.Id == value))
        {
            return;
        }

        _settings.Update(settings => settings with
        {
            Ui = settings.Ui with { LastPage = value },
        });
    }

    // ── Command palette ------------------------------------------------------

    [RelayCommand]
    private void OpenCommandPalette()
    {
        CommandSearchText = "";
        RebuildCommandPalette();
        IsCommandPaletteOpen = true;
    }

    [RelayCommand]
    private void CloseCommandPalette() => IsCommandPaletteOpen = false;

    [RelayCommand]
    private void ExecuteCommandPaletteEntry(CommandPaletteEntry? entry)
    {
        if (entry is not { IsAvailable: true })
        {
            return;
        }

        if (entry.Id.StartsWith("nav:", StringComparison.Ordinal))
        {
            Navigate(entry.Id[4..]);
        }
        else
        {
            switch (entry.Id)
            {
                case "rail":
                    ToggleVariableRail();
                    break;
                case "reconnect":
                    Connection.ReconnectCommand.Execute(null);
                    break;
                case "charts-pause":
                    SelectedNavigationId = "charts";
                    Charts.TogglePauseCommand.Execute(null);
                    break;
                case "theme":
                    ToggleTheme();
                    break;
                case "zero-time":
                    ZeroSessionTimeCommand.Execute(null);
                    break;
            }
        }

        IsCommandPaletteOpen = false;
    }

    partial void OnCommandSearchTextChanged(string value) => RebuildCommandPalette();

    private void RebuildCommandPalette()
    {
        var search = CommandSearchText.Trim();
        var selectedId = SelectedCommandPaletteEntry?.Id;

        CommandPaletteResults.Clear();
        foreach (var entry in _commandPaletteCatalog.Where(entry =>
                     search.Length == 0 ||
                     entry.Label.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                     entry.Hint.Contains(search, StringComparison.CurrentCultureIgnoreCase)))
        {
            CommandPaletteResults.Add(entry);
        }

        SelectedCommandPaletteEntry =
            CommandPaletteResults.FirstOrDefault(entry => entry.Id == selectedId) ??
            CommandPaletteResults.FirstOrDefault(entry => entry.IsAvailable) ??
            CommandPaletteResults.FirstOrDefault();
    }

    // ── KPI strip ────────────────────────────────────────────────────────────

    private void OnKpiOptionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(KpiOption.IsPinned))
        {
            RebuildPinned();
            PersistUi();
        }
    }

    /// <summary>
    /// Refreshes the visible tiles from the pin state.
    /// </summary>
    /// <remarks>
    /// A pinned tile whose channel has no data stays on screen showing an em dash. It is
    /// never dropped: tiles that come and go are how an operator stops trusting the strip
    /// to be the safety glance it exists to be.
    /// </remarks>
    private void RebuildPinned()
    {
        PinnedVariables.Clear();

        foreach (var option in KpiOptions.Where(o => o.IsPinned))
        {
            PinnedVariables.Add(option.Variable);
        }
    }

    [RelayCommand]
    private void MoveKpiUp(KpiOption option)
    {
        var index = KpiOptions.IndexOf(option);
        if (index > 0)
        {
            KpiOptions.Move(index, index - 1);
            RebuildPinned();
            PersistUi();
        }
    }

    [RelayCommand]
    private void MoveKpiDown(KpiOption option)
    {
        var index = KpiOptions.IndexOf(option);
        if (index >= 0 && index < KpiOptions.Count - 1)
        {
            KpiOptions.Move(index, index + 1);
            RebuildPinned();
            PersistUi();
        }
    }

    /// <summary>Restores declaration order with everything pinned.</summary>
    [RelayCommand]
    private void ResetKpis()
    {
        for (var target = 0; target < Variables.Count; target++)
        {
            var current = KpiOptions.IndexOf(KpiOptions.First(o => o.Variable == Variables[target]));
            if (current != target)
            {
                KpiOptions.Move(current, target);
            }
        }

        foreach (var option in KpiOptions)
        {
            option.IsPinned = true;
        }

        RebuildPinned();
        PersistUi();
    }

    [RelayCommand]
    private void ToggleVariableRail() => IsVariableRailVisible = !IsVariableRailVisible;

    partial void OnIsVariableRailVisibleChanged(bool value) => PersistUi();

    private void PersistUi()
    {
        if (!_uiLoaded)
        {
            return;
        }

        _settings.Update(s => s with
        {
            Ui = s.Ui with
            {
                PinnedKpis = [.. KpiOptions.Where(o => o.IsPinned).Select(o => o.Variable.Id)],
                ShowVariableRail = IsVariableRailVisible,
            },
        });
    }

    // ── Liveness ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Notices a link that has gone quiet without dropping.
    /// </summary>
    /// <remarks>
    /// Three emission periods of silence, so one late frame is not an alarm. The period
    /// comes from the configured <c>dataDelay</c> rather than a constant, because an
    /// operator who slows telemetry to 10 s should not get a permanent stale warning.
    /// </remarks>
    private void OnTick(object? sender, EventArgs e)
    {
        // Alarms are evaluated on every tick, connected or not: link loss, on-delay expiry
        // and the audio-silence timeout all need the clock to advance even when no telemetry
        // is arriving.
        _alarms.Poll();

        if (_device.State is not Protocol.ConnectionState.Connected || _lastFrameAt is not { } last)
        {
            return;
        }

        var budget = TimeSpan.FromMilliseconds(Math.Max(_settings.Current.Connection.DataDelayMs, 500) * 3);
        var stale = DateTimeOffset.Now - last > budget;

        if (stale == IsTelemetryStale)
        {
            return;
        }

        IsTelemetryStale = stale;

        if (stale)
        {
            _log.LogWarning("Telemetry stalled: no frame for {Elapsed:0.0} s",
                (DateTimeOffset.Now - last).TotalSeconds);

            // The link is up but nothing is being measured. Showing the last frame as
            // though it were live is the exact failure this guard exists to prevent.
            foreach (var variable in Variables)
            {
                variable.Clear();
            }
        }
    }

    [RelayCommand]
    private void SelectVariable(ProcessVariableViewModel variable) => SelectedVariable = variable;

    /// <summary>Deselects, collapsing the detail pane back to the bare synoptic.</summary>
    [RelayCommand]
    private void ClearSelection() => SelectedVariable = null;

    /// <summary>
    /// Keeps everything that follows the selection in step.
    /// </summary>
    /// <remarks>
    /// <b>On the property, not in the command.</b> Selection arrives three ways - a KPI
    /// tile raises the command, the variable rail two-way binds
    /// <see cref="SelectedVariable"/> directly, and the synoptic raises the command
    /// again. With the sync inside the command, the rail set the selection without ever
    /// updating the detail pane, so clicking pH left the previous subsystem's controls on
    /// screen under the wrong heading. They are three windows onto one selection, which
    /// only holds if the property itself is what reacts.
    /// </remarks>
    partial void OnSelectedVariableChanged(ProcessVariableViewModel? value)
    {
        foreach (var candidate in Variables)
        {
            candidate.IsSelected = ReferenceEquals(candidate, value);
        }

        // pH intentionally remains outside the scalar subsystem list: its apply is an
        // atomic five-field state and is rendered by the dedicated pH detail view.
        SelectedSubsystem = value is null
            ? null
            : Subsystems.FirstOrDefault(s => s.Variable == value);
        OnPropertyChanged(nameof(IsPHSelected));
        OnPropertyChanged(nameof(ShowReadOnlyDetail));
    }

    partial void OnSelectedSubsystemChanged(SubsystemViewModel? value)
        => OnPropertyChanged(nameof(ShowReadOnlyDetail));

    /// <summary>Selects a subsystem by id, for clicks on the synoptic.</summary>
    [RelayCommand]
    private void SelectById(string id)
    {
        if (Variables.FirstOrDefault(v => v.Id == id) is { } variable)
        {
            SelectVariable(variable);
        }
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        var next = _theme.IsDark ? ThemePreference.Light : ThemePreference.Dark;
        _theme.Apply(next);
        _settings.Update(s => s with { Theme = next });
    }

    /// <summary>Only meaningful with a device clock to rebase against.</summary>
    public bool CanZeroSessionTime => _device.State is Protocol.ConnectionState.Connected;

    /// <summary>
    /// Rebases the operator session clock to now.
    /// </summary>
    /// <remarks>
    /// A local display/log offset exactly as v.6 kept one: the device clock is never
    /// reset and samples already written are never rewritten. The header shows zero
    /// immediately; the device's next frame, carrying the same offset, confirms it.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanZeroSessionTime))]
    private void ZeroSessionTime()
    {
        _device.ZeroSessionTime();
        ElapsedText = TimeSpan.Zero.ToString(@"hh\:mm\:ss");
    }

    /// <summary>
    /// Starts a named run/step in a new session log file and zeroes the relative time.
    /// </summary>
    [RelayCommand]
    private void StartQuickSession()
    {
        var defaultPrefix = "Ensaio";
        if (!_dialogs.PromptInput(
            "Nova Corrida / Etapa de Processo",
            "Digite o nome ou rótulo do ensaio / etapa:",
            out var response,
            defaultPrefix))
        {
            return;
        }

        var fileName = AppPaths.FormatSessionFileName(response);
        var path = Path.Combine(AppPaths.SessionsDirectory, fileName);
        var cleanName = Path.GetFileNameWithoutExtension(path);

        _sessionLogger.Stop();
        _settings.Update(settings => settings with
        {
            Logging = settings.Logging with { SessionLogPath = path },
        });
        _sessionLogger.Start(path);

        if (CanZeroSessionTime)
        {
            _device.ZeroSessionTime();
        }
        ElapsedText = TimeSpan.Zero.ToString(@"hh\:mm\:ss");

        Events.Journal.Add(
            AuditSource.Application,
            AuditSeverity.Information,
            $"Nova corrida/etapa iniciada: {cleanName}",
            path);

        _log.LogInformation("Nova corrida/etapa iniciada com sucesso em {Path}", path);
    }

    /// <summary>
    /// Starts the link, after the shell is on screen.
    /// </summary>
    /// <remarks>
    /// Called from <c>ContentRendered</c>, never from the constructor: the cold-start
    /// budget is first frame under 2 s, and nothing may sit between process start and
    /// a visible window.
    /// </remarks>
    public void StartAutoConnect()
    {
        if (!_settings.Current.Connection.AutoConnect)
        {
            _log.LogInformation("Auto-connect disabled by settings");
            return;
        }

        _log.LogInformation("Auto-connect starting");
        _device.Connect();
    }

    /// <summary>
    /// The device echoed the new session offset. The command already zeroed the header
    /// optimistically; this is the authoritative confirmation from the worker.
    /// </summary>
    private void OnSessionTimeZeroed(double offsetMinutes)
        => ElapsedText = TimeSpan.Zero.ToString(@"hh\:mm\:ss");

    private void OnStateChanged(ConnectionStateChange change)
    {
        OnPropertyChanged(nameof(SystemState));
        OnPropertyChanged(nameof(SystemStateText));
        OnPropertyChanged(nameof(CanZeroSessionTime));
        ZeroSessionTimeCommand.NotifyCanExecuteChanged();

        if (change.State is ConnectionState.Connected)
        {
            _lastFrameAt = DateTimeOffset.Now;
            return;
        }

        // Nothing is being measured any more; showing the last values as though they
        // were live is the failure this project is trying to avoid.
        foreach (var variable in Variables)
        {
            variable.Clear();
        }

        ElapsedText = "—";
        IsSensorModuleOffline = false;
        IsTelemetryStale = false;
        LastUpdateText = "—";
        _lastFrameAt = null;

        OnPropertyChanged(nameof(SystemState));
        OnPropertyChanged(nameof(SystemStateText));
    }

    private void OnNutrientCommandChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NutrientControlViewModel.AppliedDutyCyclePercent)
                           or nameof(NutrientControlViewModel.AppliedIsEnabled))
        {
            Nutrient.PushCommanded(
                _nutrientControl.AppliedIsEnabled ? _nutrientControl.AppliedDutyCyclePercent : null);
        }
    }

    private void OnTelemetryReceived(SensorSnapshot snapshot)
    {
        Temperature.Push(snapshot.Temperature);
        Ph.Push(snapshot.PHCalibrated);
        Oxygen.Push(snapshot.OxygenCalibrated);
        Flow.Push(snapshot.FlowRate);
        Pressure.Push(snapshot.Pressure);
        Antifoam.Push(snapshot.Antifoam);
        Level.Push(snapshot.Distance);
        Biomass.Push(snapshot.BiomassAbsorbance);
        Pump.Push(snapshot.PumpFlow);

        // Nutrient, like Motor, is deliberately not pushed from telemetry: the device
        // reports no nutrient feedback. Its tile shows the commanded duty cycle, updated
        // in OnNutrientCommandChanged.

        // Motor is deliberately not pushed here: the device reports no RPM feedback,
        // so there is nothing to measure. Its "value" is whatever was last commanded,
        // which the UI must present as a command rather than a reading - see
        // MotorIsCommandedOnly. v.6 logs the same commanded figure.
        Flow.Setpoint = snapshot.FlowSetpoint >= 0 ? snapshot.FlowSetpoint : null;
        FlowControl.UpdateTelemetry(snapshot);

        IsSensorModuleOffline = !snapshot.SensorCommOk;

        _lastFrameAt = DateTimeOffset.Now;
        IsTelemetryStale = false;
        LastUpdateText = _lastFrameAt.Value.ToString("HH:mm:ss");
        // History first: the charts read from it, and a row written to the log should
        // never describe a frame the charts have not seen.
        var commandedRpm = Motor.Value ?? 0;
        _history.Add(snapshot, commandedRpm);
        _sessionLogger.Write(snapshot, commandedRpm, DescribeConnection());

        var minutes = snapshot.TimeMinutes;
        ElapsedText = minutes < 0
            ? "—"
            : TimeSpan.FromMinutes(minutes).ToString(@"hh\:mm\:ss");
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        if (settings.Units == _appliedUnits)
        {
            return;
        }

        _appliedUnits = settings.Units;
        ApplyUnits(_appliedUnits);
    }

    private void OnSessionStatusChanged()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplySessionStatus();
        }
        else
        {
            dispatcher.BeginInvoke(ApplySessionStatus);
        }
    }

    private void ApplySessionStatus()
    {
        OnPropertyChanged(nameof(LoggingSummary));
        if (!string.IsNullOrWhiteSpace(_sessionLogger.CurrentPath))
        {
            var name = Path.GetFileNameWithoutExtension(_sessionLogger.CurrentPath);
            if (!string.Equals(ActiveSessionName, name, StringComparison.OrdinalIgnoreCase))
            {
                ActiveSessionName = name;
            }
        }
    }

    public bool IsRecipeRunning => _recipeEngine?.State is RecipeRunState.Running or RecipeRunState.Paused;
    public bool IsManualOperationEnabled => !IsRecipeRunning;

    /// <summary>
    /// Reflects the recipe engine's run state onto the shell: while a recipe runs it owns the wire.
    /// Marshalled to the UI thread, because the engine raises this from its background run task.
    /// </summary>
    private void OnRecipeStateChanged()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyRecipeState();
        }
        else
        {
            dispatcher.BeginInvoke(ApplyRecipeState);
        }
    }

    private void ApplyRecipeState()
    {
        var running = IsRecipeRunning;
        RecipeName = running ? _recipeEngine?.Current?.Name ?? "Receita" : "—";
        OnPropertyChanged(nameof(IsRecipeRunning));
        OnPropertyChanged(nameof(IsManualOperationEnabled));
    }

    private void ApplyUnits(UnitSettings units)
    {
        var temperatureScale = UnitConversions.TemperatureToDisplay(1, units.Temperature) -
                               UnitConversions.TemperatureToDisplay(0, units.Temperature);
        var temperatureOffset = UnitConversions.TemperatureToDisplay(0, units.Temperature);
        Subsystems[0].SetPresentation(
            UnitConversions.TemperatureLabel(units.Temperature),
            decimals: 1,
            scale: temperatureScale,
            offset: temperatureOffset);

        var pressureScale = UnitConversions.PressureToDisplay(1, units.Pressure) -
                            UnitConversions.PressureToDisplay(0, units.Pressure);
        Subsystems[4].SetPresentation(
            UnitConversions.PressureLabel(units.Pressure),
            decimals: units.Pressure == PressureUnitPreference.Bar ? 3 : 1,
            scale: pressureScale);
    }

    /// <summary>
    /// Connection status as v.6 writes it into the log's final column.
    /// </summary>
    /// <remarks>
    /// Kept in the file so a gap in the data can be attributed to a dropped link
    /// rather than to the process.
    /// </remarks>
    private string DescribeConnection() => _device.State switch
    {
        Protocol.ConnectionState.Connected => _device.Medium switch
        {
            TransportMedium.WiFi => "WiFi",
            TransportMedium.Simulation => "SIMULAÇÃO kLa",
            _ => "USB",
        },
        Protocol.ConnectionState.Reconnecting => "Reconectando",
        Protocol.ConnectionState.Connecting => "Conectando",
        Protocol.ConnectionState.Faulted => "Falha",
        _ => "Desconectado",
    };

    /// <summary>Captures the applied setpoints so they are restored next launch.</summary>
    public void PersistSetpoints()
    {
        double Applied(int index, double fallback)
            => Subsystems[index].AppliedSetpoint ?? fallback;

        _settings.Update(s => s with
        {
            Setpoints = s.Setpoints with
            {
                TemperatureCelsius = Applied(0, s.Setpoints.TemperatureCelsius),
                MotorRpm = (int)Applied(1, s.Setpoints.MotorRpm),
                OxygenPercent = Applied(2, s.Setpoints.OxygenPercent),
                FlowLitresPerMinute = Applied(3, s.Setpoints.FlowLitresPerMinute),
                MaxFlowLitresPerMinute = FlowControl.AppliedMaxFlow,
                PressureKilopascal = Applied(4, s.Setpoints.PressureKilopascal),
            },
        });
    }

    public void Dispose()
    {
        _tick.Stop();
        _tick.Tick -= OnTick;

        foreach (var option in KpiOptions)
        {
            option.PropertyChanged -= OnKpiOptionChanged;
        }

        _device.StateChanged -= OnStateChanged;
        _device.TelemetryReceived -= OnTelemetryReceived;
        _device.SessionTimeZeroed -= OnSessionTimeZeroed;
        _nutrientControl.PropertyChanged -= OnNutrientCommandChanged;
        _alarms.Changed -= OnAlarmsChanged;
        _settings.Changed -= OnSettingsChanged;
        _sessionLogger.StatusChanged -= OnSessionStatusChanged;
        _recipeEngine.StateChanged -= OnRecipeStateChanged;
        Historical.OpenGraphsRequested -= OnOpenGraphsRequested;
        Settings.Dispose();
        Receitas.Dispose();
        Control.Dispose();
        CascadeDetail.Dispose();
        Our.Dispose();
        Calibration.Dispose();
        PHControl.Dispose();
        Connection.Dispose();
    }
}
