using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Dialogs;
using OpenTECHub.Services.Documentation;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Platform;
using OpenTECHub.Services.PowerTesting;
using OpenTECHub.Services.Recipes;
using OpenTECHub.Services.Telemetry;
using OpenTECHub.Services.Theme;

namespace OpenTECHub.ViewModels;

public sealed record SettingsSection(string Id, string Label, string Glyph);

public sealed record SettingOption<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Advanced settings: everything that is genuinely configuration.
/// </summary>
/// <remarks>
/// <para>
/// v.6 put connecting in the same window as calibration coefficients, spike-filter
/// thresholds and log paths, so every operator had to walk past all of it to do the one
/// thing they came for. Connecting now lives on the chip; this page keeps everything
/// else. <b>Nothing was removed</b> - it is just no longer in the way.
/// </para>
/// <para>
/// Edits are staged and applied together rather than written on every keystroke.
/// Calibration is the reason: coefficients are entered as a pair, and applying a new
/// slope against an old intercept - even for the half-second before the second field is
/// typed - would put visibly wrong numbers on screen and into the session log.
/// </para>
/// </remarks>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly IThemeService _theme;
    private readonly IDeviceService _device;
    private readonly IDialogService _dialogs;
    private readonly IBackupService? _backup;
    private readonly IFileInteractionService? _files;
    private readonly IWorkspaceMigrationService? _migration;
    private readonly IApplicationRestartService? _restart;
    private readonly IRecipeEngine? _recipes;
    private readonly IKlaTestRunner? _klaTests;
    private readonly IPowerTestRunner? _powerTests;
    private readonly IEventJournal? _journal;
    private readonly ISessionLogger? _sessionLogger;

    private bool _loading;
    private CalibrationSettings _persistedCalibration = new();

    public SettingsViewModel(
        ISettingsService settings,
        IThemeService theme,
        IDeviceService device,
        IDialogService dialogs,
        IBackupService? backup = null,
        IFileInteractionService? files = null,
        IWorkspaceMigrationService? migration = null,
        IApplicationRestartService? restart = null,
        IRecipeEngine? recipes = null,
        IKlaTestRunner? klaTests = null,
        ISessionLogger? sessionLogger = null,
        HubNodesViewModel? hubNodes = null,
        IPowerTestRunner? powerTests = null,
        IEventJournal? journal = null)
    {
        _powerTests = powerTests;
        _journal = journal;
        _settings = settings;
        _theme = theme;
        _device = device;
        _dialogs = dialogs;
        _backup = backup;
        _files = files;
        _migration = migration;
        _restart = restart;
        _recipes = recipes;
        _klaTests = klaTests;
        _sessionLogger = sessionLogger;

        ThemeOptions = [ThemePreference.System, ThemePreference.Light, ThemePreference.Dark];
        Sections =
        [
            new("connection", "Conexão", "NodeGraph"),
            new("calibration", "Calibração", "Target"),
            new("acquisition", "Aquisição", "Trend"),
            new("units", "Unidades", "Pressure"),
            new("backup", "Backup e dados", "File"),
            new("device", "Comandos do equipamento", "Gear"),
            new(DocumentationSectionId, "Documentação", "Book"),
        ];
        HubNodes = hubNodes ?? new HubNodesViewModel(device);
        SelectedSection = Sections[0];
        HubNodes.IsActive = SelectedSection.Id == "connection";
        DocumentationTopics = DocumentationCatalog.Topics;
        SelectedDocumentationTopic = DocumentationTopics[0];

        TemperatureUnitOptions =
        [
            new(TemperatureUnitPreference.Celsius, "°C — Celsius"),
            new(TemperatureUnitPreference.Fahrenheit, "°F — Fahrenheit"),
        ];
        PressureUnitOptions =
        [
            new(PressureUnitPreference.KPa, "kPa"),
            new(PressureUnitPreference.MmHg, "mmHg"),
            new(PressureUnitPreference.Bar, "bar"),
        ];

        // The previews decode the LIVE raw count, so they have to be re-evaluated as
        // telemetry arrives - not only when a coefficient is edited. Without this they
        // read "sem leitura bruta disponível" forever, having been computed once before
        // the first frame ever landed.
        _device.TelemetryReceived += OnTelemetryReceived;
        _settings.Changed += OnSettingsChanged;

        // The footer logos follow the applied theme, which can change from outside this
        // page (caption toggle, live Windows switch), so track the service, not Theme.
        IsDarkTheme = _theme.IsDark;
        _theme.ThemeChanged += OnThemeChanged;

        Load(settings.Current);
    }

    private void OnThemeChanged(bool isDark) => IsDarkTheme = isDark;

    private void OnTelemetryReceived(SensorSnapshot _)
    {
        OnPropertyChanged(nameof(OxygenPreview));
        OnPropertyChanged(nameof(PHPreview));
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        var previous = _persistedCalibration;
        var current = settings.Calibration;

        var oxygenChanged = previous.OxygenA != current.OxygenA ||
                            previous.OxygenB != current.OxygenB;
        var phChanged = previous.PHSlope != current.PHSlope ||
                        previous.PHIntercept != current.PHIntercept;

        _persistedCalibration = current;

        // Keep staged Theme in sync if the user toggled it externally (e.g. caption button)
        // and hasn't made unapplied edits in the settings page.
        if (!HasChanges && Theme != settings.Theme)
        {
            Theme = settings.Theme;
        }

        if (!oxygenChanged && !phChanged)
        {
            return;
        }

        // Guided procedures apply through ISettingsService while this singleton is
        // still alive. Refresh only changed coefficients so a later Settings Apply
        // cannot silently restore stale calibration values. Unrelated staged fields,
        // including the other probe, remain untouched.
        _loading = true;
        try
        {
            if (oxygenChanged)
            {
                OxygenA = FormatPrecise(current.OxygenA);
                OxygenB = FormatPrecise(current.OxygenB);
            }

            if (phChanged)
            {
                PHSlope = FormatPrecise(current.PHSlope);
                PHIntercept = FormatPrecise(current.PHIntercept);
            }
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(nameof(OxygenPreview));
        OnPropertyChanged(nameof(PHPreview));
        StatusMessage = "Coeficientes sincronizados com a calibração guiada.";
    }

    public IReadOnlyList<ThemePreference> ThemeOptions { get; }

    public IReadOnlyList<SettingsSection> Sections { get; }

    // ---- Documentation -----------------------------------------------
    // The in-app manual. It lives in Settings because it is the one destination that is
    // always reachable and never part of a running procedure - a page an operator can
    // open mid-assay without touching the assay.

    /// <summary>Id of the documentation section, used by the deep links from other pages.</summary>
    public const string DocumentationSectionId = "documentation";

    public IReadOnlyList<DocumentationTopic> DocumentationTopics { get; }

    [ObservableProperty]
    public partial DocumentationTopic SelectedDocumentationTopic { get; set; }

    /// <summary>
    /// Opens the documentation on <paramref name="topicId"/>, or on its first page when the
    /// id is unknown.
    /// </summary>
    /// <remarks>
    /// Unknown never means "do nothing": a help button whose topic was renamed must still
    /// land the operator in the manual, not fail silently on the page they were reading.
    /// </remarks>
    public void SelectDocumentation(string? topicId)
    {
        SelectedDocumentationTopic = DocumentationCatalog.Find(topicId) ?? DocumentationTopics[0];
        SelectedSection = Sections.First(section => section.Id == DocumentationSectionId);
    }

    public IReadOnlyList<SettingOption<TemperatureUnitPreference>> TemperatureUnitOptions { get; }

    public IReadOnlyList<SettingOption<PressureUnitPreference>> PressureUnitOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDocumentationSelected))]
    public partial SettingsSection SelectedSection { get; set; }

    /// <summary>
    /// True while the manual is open, which is the one section that wants the whole page.
    /// </summary>
    /// <remarks>
    /// Every other section is a form of short fields, so the decorative vessel beside it costs
    /// nothing. Documentation is prose: the same picture would hold the text to half the width
    /// and roughly double the scrolling, for no gain.
    /// </remarks>
    public bool IsDocumentationSelected => SelectedSection?.Id == DocumentationSectionId;

    // ── Gás e válvulas: the A/B/C wiring (plan Etapa 8) ──────────────────────

    /// <summary>Which flowmeter input drives valve A; B and C share the other one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GasVentInputText))]
    [NotifyPropertyChangedFor(nameof(GasRigSummary))]
    [NotifyPropertyChangedFor(nameof(IsGasRigDefault))]
    public partial GasInput GasAirInletInput { get; set; } = GasRigConfiguration.Default.AirInletInput;

    public IReadOnlyList<EnumChoice<GasInput>> GasInputOptions { get; } =
    [
        new(GasInput.Input1, "Entrada 1 (MOSFET 1)"),
        new(GasInput.Input2, "Entrada 2 (MOSFET 2)"),
    ];

    /// <summary>The line the operator does not choose: B and C go on the other input.</summary>
    public string GasVentInputText => $"B e C ligadas na entrada {(int)new GasRigConfiguration(GasAirInletInput).VentAndNitrogenInput}";

    public string GasRigSummary => $"Arranjo: {new GasRigConfiguration(GasAirInletInput).Describe()}";

    public bool IsGasRigDefault => GasAirInletInput == GasRigConfiguration.Default.AirInletInput;

    /// <summary>Click to enlarge the flowchart; the page is narrow on a laptop.</summary>
    [ObservableProperty]
    public partial bool IsGasDiagramEnlarged { get; set; }

    [RelayCommand]
    private void ToggleGasDiagram() => IsGasDiagramEnlarged = !IsGasDiagramEnlarged;

    [RelayCommand]
    private void RestoreGasRigDefault() => GasAirInletInput = GasRigConfiguration.Default.AirInletInput;

    /// <summary>Why the wiring cannot change right now, or null when it can.</summary>
    private string? GasRigChangeBlockedReason()
    {
        if (_recipes is not null && _recipes.State is RecipeRunState.Running or RecipeRunState.Paused)
        {
            return "Há uma receita em execução. Finalize-a antes de mudar o arranjo de válvulas.";
        }
        if (_klaTests is { IsRunning: true })
        {
            return "Há um ensaio de kLa em andamento. Finalize-o antes de mudar o arranjo de válvulas.";
        }
        if (_powerTests is { IsRunning: true })
        {
            return "Há um ensaio de potência em andamento. Finalize-o antes de mudar o arranjo de válvulas.";
        }
        return null;
    }

    partial void OnGasAirInletInputChanged(GasInput value)
    {
        if (_loading)
        {
            return;
        }

        var current = _settings.Current.GasRig.AirInletInput;
        if (value == current)
        {
            return;
        }

        if (GasRigChangeBlockedReason() is { } reason)
        {
            // Put the selector back without re-entering this handler as an edit.
            _loading = true;
            try { GasAirInletInput = current; }
            finally { _loading = false; }
            StatusMessage = reason;
            return;
        }

        var rig = new GasRigConfiguration(value);
        _settings.Update(s => s with { GasRig = GasRigSettings.From(rig) });
        _journal?.Add(AuditSource.Application, AuditSeverity.Warning,
            $"Arranjo de válvulas: A → entrada {(int)value}; B e C → entrada {(int)rig.VentAndNitrogenInput}.",
            "Documentação › Gás e válvulas. Vale para os próximos comandos e corridas; ensaios já iniciados com outro arranjo são recusados.");
        StatusMessage = $"Arranjo de válvulas salvo: {rig.Describe()}.";
    }

    /// <summary>The "Nós na rede do Hub" panel of the Conexão section. Polls only while that section is open.</summary>
    public HubNodesViewModel HubNodes { get; }

    partial void OnSelectedSectionChanged(SettingsSection value)
        => HubNodes.IsActive = value?.Id == "connection";

    // ---- Connection --------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    public partial bool AutoConnect { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    public partial bool BackupEnabled { get; set; }

    [ObservableProperty]
    public partial string WorkspaceDirectory { get; set; } = AppPaths.DataDirectory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    public partial string DataDelayMs { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    public partial string IpAddress { get; set; } = "";

    // ---- Calibration -------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    [NotifyPropertyChangedFor(nameof(OxygenPreview))]
    public partial string OxygenA { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    [NotifyPropertyChangedFor(nameof(OxygenPreview))]
    public partial string OxygenB { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    [NotifyPropertyChangedFor(nameof(PHPreview))]
    public partial string PHSlope { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    [NotifyPropertyChangedFor(nameof(PHPreview))]
    public partial string PHIntercept { get; set; } = "";

    // ---- Spike filters -----------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    public partial string PHAbsoluteThreshold { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    public partial string PHFollowTolerance { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    public partial string PHConfirmRuns { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    public partial string OxygenAbsoluteThreshold { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    public partial string OxygenFollowTolerance { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    public partial string OxygenConfirmRuns { get; set; } = "";

    // ---- Logging and appearance --------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    public partial string SessionLogPath { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    public partial ThemePreference Theme { get; set; }

    /// <summary>
    /// Tracks the theme actually applied (not the staged <see cref="Theme"/>), so the
    /// footer logos swap the instant the theme changes — including from the caption
    /// toggle or a live Windows theme switch, before Apply is pressed.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnespLogoSource))]
    [NotifyPropertyChangedFor(nameof(FapespLogoSource))]
    public partial bool IsDarkTheme { get; set; }

    /// <summary>UNESP credit logo, white-ink under the dark theme and dark-ink otherwise.</summary>
    public string UnespLogoSource => IsDarkTheme
        ? "/OpenTECHub;component/Resources/Images/Logo_Unesp_dark_theme.png"
        : "/OpenTECHub;component/Resources/Images/Logo_Unesp.png";

    /// <summary>FAPESP credit logo, paired with <see cref="UnespLogoSource"/>.</summary>
    public string FapespLogoSource => IsDarkTheme
        ? "/OpenTECHub;component/Resources/Images/logos-fapesp_dark_theme.png"
        : "/OpenTECHub;component/Resources/Images/logos-fapesp.png";

    // ---- Presentation units -----------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    public partial SettingOption<TemperatureUnitPreference> TemperatureUnit { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    public partial SettingOption<PressureUnitPreference> PressureUnit { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    public partial string VesselVolumeLitres { get; set; } = "";

    // ---- State -------------------------------------------------------

    /// <summary>Non-null when something on the page cannot be applied.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial string? ValidationError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial bool HasChanges { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "";

    public bool IsValid => ValidationError is null;

    public bool CanApply => IsValid && HasChanges;

    /// <summary>
    /// What the current raw oxygen count decodes to with the coefficients as typed.
    /// </summary>
    /// <remarks>
    /// A live preview because coefficients are otherwise impossible to sanity-check:
    /// <c>0.0305473419314</c> looks exactly as plausible as <c>0.305473419314</c>, and
    /// only the decoded value reveals which one is right.
    /// </remarks>
    public string OxygenPreview => DescribePreview(
        _device.Latest?.OxygenRaw,
        OxygenA, OxygenB,
        static (a, b, raw) => Math.Max((a * raw) + b, 0.0),
        "%");

    public string PHPreview => DescribePreview(
        _device.Latest?.PHRaw,
        PHSlope, PHIntercept,
        static (slope, intercept, raw) => (slope * raw) + intercept,
        "");

    // ---- Commands ----------------------------------------------------

    /// <summary>Writes every staged edit at once.</summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        Validate();
        if (!IsValid)
        {
            return;
        }

        _settings.Update(s => s with
        {
            Connection = s.Connection with
            {
                AutoConnect = AutoConnect,
                BackupEnabled = BackupEnabled,
                DataDelayMs = ParseInt(DataDelayMs, s.Connection.DataDelayMs),
                IpAddress = string.IsNullOrWhiteSpace(IpAddress) ? s.Connection.IpAddress : IpAddress.Trim(),
            },
            Calibration = s.Calibration with
            {
                OxygenA = ParseDouble(OxygenA, s.Calibration.OxygenA),
                OxygenB = ParseDouble(OxygenB, s.Calibration.OxygenB),
                PHSlope = ParseDouble(PHSlope, s.Calibration.PHSlope),
                PHIntercept = ParseDouble(PHIntercept, s.Calibration.PHIntercept),
            },
            Filters = s.Filters with
            {
                PHAbsoluteThreshold = ParseDouble(PHAbsoluteThreshold, s.Filters.PHAbsoluteThreshold),
                PHFollowTolerance = ParseDouble(PHFollowTolerance, s.Filters.PHFollowTolerance),
                PHConfirmRuns = ParseInt(PHConfirmRuns, s.Filters.PHConfirmRuns),
                OxygenAbsoluteThreshold = ParseDouble(OxygenAbsoluteThreshold, s.Filters.OxygenAbsoluteThreshold),
                OxygenFollowTolerance = ParseDouble(OxygenFollowTolerance, s.Filters.OxygenFollowTolerance),
                OxygenConfirmRuns = ParseInt(OxygenConfirmRuns, s.Filters.OxygenConfirmRuns),
            },
            Units = s.Units with
            {
                Temperature = TemperatureUnit.Value,
                Pressure = PressureUnit.Value,
                VesselVolumeLitres = ParseDouble(VesselVolumeLitres, s.Units.VesselVolumeLitres),
            },
            Logging = s.Logging with
            {
                SessionLogPath = string.IsNullOrWhiteSpace(SessionLogPath) ? null : SessionLogPath.Trim(),
            },
            Theme = Theme,
        });

        _theme.Apply(Theme);

        // The device only learns of a new telemetry period if it is told.
        _device.Send(CommandBuilders.DataDelay(ParseInt(DataDelayMs, 2000)));

        HasChanges = false;
        StatusMessage = "Configurações aplicadas.";
    }

    /// <summary>Discards staged edits and reloads what is persisted.</summary>
    [RelayCommand]
    private void Revert()
    {
        Load(_settings.Current);
        StatusMessage = "Alterações descartadas.";
    }

    /// <summary>
    /// Restores the factory calibration and filter values.
    /// </summary>
    /// <remarks>
    /// Staged, not applied: restoring defaults is exactly the moment an operator most
    /// wants to see what they are about to commit before committing it.
    /// </remarks>
    [RelayCommand]
    private void RestoreDefaults()
    {
        var defaults = new AppSettings();
        LoadCalibration(defaults);
        LoadFilters(defaults);

        HasChanges = true;
        StatusMessage = "Valores de fábrica carregados — revise e aplique.";
    }

    /// <summary>Asks the device to reset its process variables.</summary>
    [RelayCommand]
    private void ResetDeviceVariables()
    {
        var command = CommandBuilders.ResetVariables();
        if (!_dialogs.ConfirmDestructive(
                "Resetar variáveis do módulo",
                "Apaga o estado de processo mantido pelo módulo durante a operação atual. " +
                "Faça isto apenas com o cultivo em condição segura.",
                command.ToJson()))
        {
            StatusMessage = "Reset cancelado; nenhum comando foi enviado.";
            return;
        }

        _device.Send(command);
        StatusMessage = "Comando de reset enviado ao equipamento.";
    }

    /// <summary>Asks the device to restart its internal communications.</summary>
    [RelayCommand]
    private void RestartDeviceComms()
    {
        var command = CommandBuilders.Restart();
        if (!_dialogs.ConfirmDestructive(
                "Reiniciar comunicações",
                "Interrompe e reinicia as comunicações internas do módulo. Leituras e " +
                "confirmações ficarão indisponíveis durante o reinício.",
                command.ToJson()))
        {
            StatusMessage = "Reinício cancelado; nenhum comando foi enviado.";
            return;
        }

        _device.Send(command);
        StatusMessage = "Comando de reinício enviado ao equipamento.";
    }

    // ---- Loading and validation --------------------------------------

    private void Load(AppSettings settings)
    {
        _loading = true;
        try
        {
            GasAirInletInput = settings.GasRig.AirInletInput;
            AutoConnect = settings.Connection.AutoConnect;
            BackupEnabled = settings.Connection.BackupEnabled;
            DataDelayMs = Format(settings.Connection.DataDelayMs);
            IpAddress = settings.Connection.IpAddress;

            LoadCalibration(settings);
            _persistedCalibration = settings.Calibration;
            LoadFilters(settings);

            SessionLogPath = settings.Logging.SessionLogPath ?? "";
            Theme = settings.Theme;
            TemperatureUnit = TemperatureUnitOptions.First(option => option.Value == settings.Units.Temperature);
            PressureUnit = PressureUnitOptions.First(option => option.Value == settings.Units.Pressure);
            VesselVolumeLitres = Format(settings.Units.VesselVolumeLitres);
        }
        finally
        {
            _loading = false;
        }

        HasChanges = false;
        Validate();
    }

    private void LoadCalibration(AppSettings settings)
    {
        // Round-trip precision: these coefficients carry a dozen significant figures,
        // and a display format that truncated them would silently degrade the
        // calibration every time the page was opened and applied.
        OxygenA = FormatPrecise(settings.Calibration.OxygenA);
        OxygenB = FormatPrecise(settings.Calibration.OxygenB);
        PHSlope = FormatPrecise(settings.Calibration.PHSlope);
        PHIntercept = FormatPrecise(settings.Calibration.PHIntercept);
    }

    private void LoadFilters(AppSettings settings)
    {
        PHAbsoluteThreshold = Format(settings.Filters.PHAbsoluteThreshold);
        PHFollowTolerance = Format(settings.Filters.PHFollowTolerance);
        PHConfirmRuns = Format(settings.Filters.PHConfirmRuns);
        OxygenAbsoluteThreshold = Format(settings.Filters.OxygenAbsoluteThreshold);
        OxygenFollowTolerance = Format(settings.Filters.OxygenFollowTolerance);
        OxygenConfirmRuns = Format(settings.Filters.OxygenConfirmRuns);
    }

    /// <summary>Marks the page dirty whenever any staged field changes.</summary>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (_loading ||
            e.PropertyName is null or
            nameof(HasChanges) or nameof(ValidationError) or nameof(StatusMessage) or
            nameof(SelectedSection) or
            nameof(IsValid) or nameof(CanApply) or
            nameof(OxygenPreview) or nameof(PHPreview))
        {
            return;
        }

        HasChanges = true;
        Validate();
    }

    private void Validate()
    {
        if (!TryDouble(OxygenA, out var oxygenA) || oxygenA == 0)
        {
            ValidationError = "Coeficiente A do oxigênio inválido (não pode ser zero).";
            return;
        }

        if (!TryDouble(OxygenB, out _))
        {
            ValidationError = "Coeficiente B do oxigênio inválido.";
            return;
        }

        if (!TryDouble(PHSlope, out var phSlope) || phSlope == 0)
        {
            ValidationError = "Inclinação do pH inválida (não pode ser zero).";
            return;
        }

        if (!TryDouble(PHIntercept, out _))
        {
            ValidationError = "Intercepto do pH inválido.";
            return;
        }

        foreach (var (text, label) in new[]
                 {
                     (PHAbsoluteThreshold, "limiar absoluto do pH"),
                     (PHFollowTolerance, "tolerância de acompanhamento do pH"),
                     (OxygenAbsoluteThreshold, "limiar absoluto do oxigênio"),
                     (OxygenFollowTolerance, "tolerância de acompanhamento do oxigênio"),
                 })
        {
            if (!TryDouble(text, out var value) || value <= 0)
            {
                ValidationError = $"Valor inválido para {label} (deve ser maior que zero).";
                return;
            }
        }

        foreach (var (text, label) in new[]
                 {
                     (PHConfirmRuns, "confirmações do pH"),
                     (OxygenConfirmRuns, "confirmações do oxigênio"),
                 })
        {
            if (!TryInt(text, out var runs) || runs < 1)
            {
                ValidationError = $"Valor inválido para {label} (mínimo 1).";
                return;
            }
        }

        // Below ~100 ms the device cannot keep up, and above a minute the app's own
        // silence timeout would fire before the next frame arrives.
        if (!TryInt(DataDelayMs, out var delay) || delay is < 100 or > 60_000)
        {
            ValidationError = "Período de telemetria inválido (100–60000 ms).";
            return;
        }

        if (!TryDouble(VesselVolumeLitres, out var volume) || volume <= 0)
        {
            ValidationError = "Volume nominal da dorna inválido (deve ser maior que zero).";
            return;
        }

        ValidationError = null;
    }

    // ---- Formatting helpers ------------------------------------------

    private string DescribePreview(
        double? raw,
        string firstText,
        string secondText,
        Func<double, double, double, double> decode,
        string unit)
    {
        if (raw is not { } rawValue || rawValue <= SensorReadings.NotReceived)
        {
            return "sem leitura bruta disponível";
        }

        if (!TryDouble(firstText, out var first) || !TryDouble(secondText, out var second))
        {
            return "coeficientes inválidos";
        }

        var decoded = decode(first, second, rawValue);

        // Formatted first, then trimmed: calling TrimEnd on the interpolated expression
        // collapses it to a string and loses the culture-aware string.Create overload.
        var text = string.Create(CultureInfo.CurrentCulture,
            $"bruto {rawValue:F0} → {decoded:F2} {unit}");

        return text.TrimEnd();
    }

    /// <summary>Accepts a decimal comma, as a pt-BR keyboard produces.</summary>
    private static bool TryDouble(string? text, out double value)
        => double.TryParse((text ?? "").Trim().Replace(',', '.'),
            NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool TryInt(string? text, out int value)
        => int.TryParse((text ?? "").Trim(),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static double ParseDouble(string? text, double fallback)
        => TryDouble(text, out var value) ? value : fallback;

    private static int ParseInt(string? text, int fallback)
        => TryInt(text, out var value) ? value : fallback;

    private static string Format(double value)
        => value.ToString("G", CultureInfo.CurrentCulture);

    private static string Format(int value)
        => value.ToString(CultureInfo.CurrentCulture);

    /// <summary>Round-trip format, so no precision is lost by displaying a value.</summary>
    private static string FormatPrecise(double value)
        => value.ToString("R", CultureInfo.CurrentCulture);

    [RelayCommand]
    private async Task ExportBackupAsync()
    {
        if (_backup is null || _files is null)
        {
            return;
        }

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture);
        var path = _files.ChooseSavePath(
            "Exportar Backup do Sistema",
            $"Backup_OpenTEC_{timestamp}.tecbkp",
            "Backup OpenTEC (*.tecbkp;*.zip)|*.tecbkp;*.zip|Todos os arquivos (*.*)|*.*",
            ".tecbkp");

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        StatusMessage = "Exportando backup do sistema...";
        var result = await _backup.ExportBackupAsync(path).ConfigureAwait(true);
        if (result.Success)
        {
            StatusMessage = $"Backup exportado com sucesso ({result.RecipesCount} receitas, {result.MapsCount} mapas).";
        }
        else
        {
            StatusMessage = result.Message;
        }
    }

    [RelayCommand]
    private async Task ImportBackupAsync()
    {
        if (_backup is null || _files is null)
        {
            return;
        }

        var path = _files.ChooseOpenPath(
            "Importar Backup do Sistema",
            "Backup OpenTEC (*.tecbkp;*.zip)|*.tecbkp;*.zip|Todos os arquivos (*.*)|*.*",
            ".tecbkp");

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var confirmed = _dialogs.Confirm(
            "Restaurar Backup",
            "Esta ação restaurará todas as receitas, predefinições e mapas kLa contidos no arquivo de backup. " +
            "Os dados locais existentes serão substituídos. Deseja continuar?",
            confirmText: "Restaurar",
            cancelText: "Cancelar",
            isDanger: true);

        if (!confirmed)
        {
            StatusMessage = "Importação de backup cancelada.";
            return;
        }

        StatusMessage = "Importando e restaurando dados...";
        var result = await _backup.ImportBackupAsync(path).ConfigureAwait(true);
        if (result.Success)
        {
            StatusMessage = $"Backup importado com sucesso ({result.RecipesCount} receitas, {result.MapsCount} mapas restaurados).";
            Load(_settings.Current);
        }
        else
        {
            StatusMessage = result.Message;
        }
    }

    public IReadOnlyList<WorkspaceFolderInfo> WorkspaceFolders =>
    [
        new(
            "Mapas",
            "Mapas\\",
            "Experimentos de kLa, superfícies de oxigenação e perfis publicados para controle.",
            "*.kla.json",
            AppPaths.KlaMappingDirectory),
        new(
            "Testes de kLa",
            "Testes-kLa\\",
            "Campanhas de Gassing-Out, curvas brutas, análises e resultados experimentais.",
            "teste.json, *.csv",
            AppPaths.KlaTestsDirectory),
        new(
            "Receitas",
            "Receitas\\",
            "Receitas de bioprocesso do operador, fases e automações programadas.",
            "*.recipe.json",
            AppPaths.RecipesDirectory),
        new(
            "Sessões",
            "Sessoes\\",
            "Arquivos de telemetria contínua e exportações de dados das corridas de fermentação.",
            "session_*.txt, *.tsv",
            AppPaths.SessionsDirectory),
        new(
            "Logs",
            "Logs\\",
            "Registros de auditoria, eventos operacionais e histórico de execução.",
            "opentechub-*.log",
            AppPaths.LogDirectory),
        new(
            "Configurações",
            "Configuracoes\\",
            "Configurações de calibração, preferências e parâmetros operacionais.",
            "settings.json",
            AppPaths.ConfigDirectory),
        new(
            "Testes de Potência",
            "Testes-Potencia\\",
            "Ensaios autocontidos de curva de potência, Np, regime gaseificado e flooding.",
            "ensaio.json, *.csv",
            AppPaths.PowerTestsDirectory),
        new(
            "Mapas de Potência",
            "Mapas-Potencia\\",
            "Síntese de superfícies 2D (N, Qg), fronteira de flooding, benchmark de impelidores e correlações kLa.",
            "*.json, *.csv",
            AppPaths.PowerMapsDirectory),
        new(
            "Backups",
            "Backups\\",
            "Destino padrão sugerido para pacotes de backup e restauração completos.",
            "*.tecbkp, *.zip",
            AppPaths.BackupsDirectory),
    ];

    /// <summary>
    /// Copies the workspace onto a new root and restarts the app there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The old command only moved the static path, which the settings file, the recipe,
    /// map and kLa-test stores, the backup service and the log sink had all already read
    /// in the composition root - so the app carried on writing half its data to the folder
    /// the operator had just left. Copying and restarting moves everything at once.
    /// </para>
    /// <para>
    /// The copy never deletes and never overwrites: the old folder stays as it was, and
    /// anything already present at the destination wins. The switch is refused outright
    /// while a recipe or a kLa run is executing - the restart would abandon the run.
    /// </para>
    /// </remarks>
    [RelayCommand]
    private async Task ChangeWorkspaceDirectoryAsync()
    {
        if (_files is null || _migration is null)
        {
            return;
        }

        if (_recipes is not null && _recipes.State is RecipeRunState.Running or RecipeRunState.Paused)
        {
            StatusMessage = "Há uma receita em execução. Finalize-a antes de trocar o diretório de trabalho.";
            return;
        }

        if (_klaTests is { IsRunning: true })
        {
            StatusMessage = "Há um ensaio de kLa em andamento. Finalize-o antes de trocar o diretório de trabalho.";
            return;
        }

        var source = AppPaths.DataDirectory;
        var selected = _files.ChooseFolder("Selecionar Diretório de Trabalho (Workspace / Sessão Global)", WorkspaceDirectory);
        if (string.IsNullOrWhiteSpace(selected))
        {
            StatusMessage = "Alteração de diretório cancelada.";
            return;
        }

        if (!_migration.CanMigrate(source, selected, out var reason))
        {
            StatusMessage = reason ?? "Pasta de destino inválida.";
            return;
        }

        var preview = _migration.Preview(source);
        var connectionWarning = _device.State == ConnectionState.Connected
            ? "\n\nATENÇÃO: o equipamento está conectado. O reinício interrompe a aquisição por alguns segundos; " +
              "os setpoints permanecem ativos no Hub."
            : string.Empty;

        var confirmed = _dialogs.Confirm(
            "Alterar Diretório de Trabalho (Workspace)",
            $"Os dados atuais serão COPIADOS para a nova pasta. Nada é apagado: a pasta atual permanece intacta.\n\n" +
            $"De:   {source}\n" +
            $"Para: {selected}\n\n" +
            $"{preview.FileCount} arquivo(s), {FormatSize(preview.TotalBytes)}. Arquivos que já existirem no destino " +
            "não são sobrescritos.\n\n" +
            $"Ao final o OpenTEC-Hub será REINICIADO automaticamente na nova pasta.{connectionWarning}",
            confirmText: "Copiar e reiniciar",
            cancelText: "Cancelar");

        if (!confirmed)
        {
            StatusMessage = "Alteração de diretório cancelada.";
            return;
        }

        StatusMessage = "Copiando dados para a nova pasta de trabalho...";

        // Closed for the duration of the copy so the two folders cannot end up holding two
        // different versions of the same session file: the instance that starts in the new
        // workspace reopens the copy and appends to it. The rows not written during the copy
        // are a gap of a second, and a gap is easier to read than a divergence.
        var openSession = _sessionLogger?.CurrentPath;
        _sessionLogger?.Stop();

        var result = await _migration.MigrateAsync(source, selected).ConfigureAwait(true);
        if (!result.Success)
        {
            if (openSession is { Length: > 0 })
            {
                _sessionLogger?.Start(openSession);
            }

            StatusMessage = result.Message;
            return;
        }

        // Persisted, not applied: this process keeps its own paths until it exits, so a
        // failed restart leaves a consistent app pointed at the folder it started in.
        AppPaths.SaveConfiguredWorkspace(selected);

        if (_restart?.RestartWithWorkspace(selected) != true)
        {
            if (openSession is { Length: > 0 })
            {
                _sessionLogger?.Start(openSession);
            }

            StatusMessage = $"{result.Message} Não foi possível reiniciar automaticamente — " +
                            "feche e abra o OpenTEC-Hub para usar a nova pasta.";
            return;
        }

        StatusMessage = $"{result.Message} Reiniciando na nova pasta...";
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.0} GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0.0} kB",
        _ => $"{bytes} B",
    };

    [RelayCommand]
    private void OpenWorkspaceFolder()
    {
        _files?.OpenFolder(WorkspaceDirectory);
    }

    [RelayCommand]
    private void OpenSubfolder(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            _files?.OpenFolder(path);
        }
    }

    public void Dispose()
    {
        _device.TelemetryReceived -= OnTelemetryReceived;
        _settings.Changed -= OnSettingsChanged;
        _theme.ThemeChanged -= OnThemeChanged;
        HubNodes.Dispose();
    }
}

public sealed record WorkspaceFolderInfo(
    string Name,
    string RelativePath,
    string Description,
    string FileTypes,
    string FullPath);
