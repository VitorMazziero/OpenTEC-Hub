using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TecnalHub.Protocol;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Dialogs;
using TecnalHub.Services.Persistence;
using TecnalHub.Services.Platform;
using TecnalHub.Services.Theme;

namespace TecnalHub.ViewModels;

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

    private bool _loading;
    private CalibrationSettings _persistedCalibration = new();

    public SettingsViewModel(
        ISettingsService settings,
        IThemeService theme,
        IDeviceService device,
        IDialogService dialogs,
        IBackupService? backup = null,
        IFileInteractionService? files = null)
    {
        _settings = settings;
        _theme = theme;
        _device = device;
        _dialogs = dialogs;
        _backup = backup;
        _files = files;

        ThemeOptions = [ThemePreference.System, ThemePreference.Light, ThemePreference.Dark];
        Sections =
        [
            new("connection", "Conexão", "NodeGraph"),
            new("calibration", "Calibração", "Target"),
            new("acquisition", "Aquisição", "Trend"),
            new("units", "Unidades", "Pressure"),
            new("logging", "Registro e aparência", "EventLog"),
            new("backup", "Backup e dados", "File"),
            new("device", "Comandos do equipamento", "Gear"),
        ];
        SelectedSection = Sections[0];

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

        Load(settings.Current);
    }

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

    public IReadOnlyList<SettingOption<TemperatureUnitPreference>> TemperatureUnitOptions { get; }

    public IReadOnlyList<SettingOption<PressureUnitPreference>> PressureUnitOptions { get; }

    [ObservableProperty]
    public partial SettingsSection SelectedSection { get; set; }

    // ---- Connection --------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    public partial bool AutoConnect { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    public partial bool BackupEnabled { get; set; }

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
            $"Backup_Tecnal_{timestamp}.tecbkp",
            "Backup TECNAL (*.tecbkp;*.zip)|*.tecbkp;*.zip|Todos os arquivos (*.*)|*.*",
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
            "Backup TECNAL (*.tecbkp;*.zip)|*.tecbkp;*.zip|Todos os arquivos (*.*)|*.*",
            ".tecbkp");

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var confirmed = _dialogs.ConfirmDestructive(
            "Restaurar Backup",
            "Esta ação restaurará todas as receitas, predefinições e mapas kLa contidos no arquivo de backup. " +
            "Os dados locais existentes serão substituídos. Deseja continuar?",
            path);

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

    public void Dispose()
    {
        _device.TelemetryReceived -= OnTelemetryReceived;
        _settings.Changed -= OnSettingsChanged;
    }
}

