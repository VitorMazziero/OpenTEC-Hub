using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTECHub.Services.Control;
using OpenTECHub.Services.KlaMapping;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.ViewModels;

public sealed record CascadeModeOption(CascadeMode Mode, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class OxygenConfigViewModel : ObservableObject, IDisposable
{
    private readonly ICascadeService _cascade;
    private readonly ISettingsService _settings;
    private readonly IKlaProfileStore? _store;

    private readonly Dictionary<CascadeMode, ModePidSettings> _modePids = new();

    private bool _loading;

    public OxygenConfigViewModel(
        ICascadeService cascade,
        ISettingsService settings,
        IKlaProfileStore? store = null)
    {
        _cascade = cascade;
        _settings = settings;
        _store = store;

        var cfg = settings.Current.Cascade;

        OxygenSetpointText = cfg.OxygenSetpointPercent.ToString("F1", CultureInfo.InvariantCulture);

        // Initialize dictionary with copies of current settings
        _modePids[CascadeMode.AgitationOnly] = cfg.AgitationPid with { };
        _modePids[CascadeMode.AerationOnly] = cfg.AerationPid with { };
        _modePids[CascadeMode.DualCascade] = cfg.CascadePid with { };
        _modePids[CascadeMode.KlaPath] = cfg.MapPid with { };

        // Shared physical limits
        AgitationMinRpmText = cfg.AgitationMinRpm.ToString("F0", CultureInfo.InvariantCulture);
        AgitationMaxRpmText = cfg.AgitationMaxRpm.ToString("F0", CultureInfo.InvariantCulture);
        AerationMinLpmText = cfg.AerationMinLpm.ToString("F2", CultureInfo.InvariantCulture);
        AerationMaxLpmText = cfg.AerationMaxLpm.ToString("F2", CultureInfo.InvariantCulture);

        // Cascata effort windows
        AgitationEffortStartText = cfg.AgitationEffortStart.ToString("F0", CultureInfo.InvariantCulture);
        AgitationEffortEndText = cfg.AgitationEffortEnd.ToString("F0", CultureInfo.InvariantCulture);
        AerationEffortStartText = cfg.AerationEffortStart.ToString("F0", CultureInfo.InvariantCulture);
        AerationEffortEndText = cfg.AerationEffortEnd.ToString("F0", CultureInfo.InvariantCulture);

        Modes =
        [
            new(CascadeMode.AgitationOnly, "Agitação"),
            new(CascadeMode.AerationOnly, "Aeração"),
            new(CascadeMode.DualCascade, "Cascata"),
            new(CascadeMode.KlaPath, "Mapa"),
        ];

        SelectedMode = Modes.FirstOrDefault(m => m.Mode == cascade.Mode) ?? Modes[2];
        LoadPidFieldsForMode(SelectedMode.Mode);

        LoadAvailablePaths();
        if (_store != null)
        {
            _store.ProfilePublished += OnProfilePublished;
        }
    }

    public IReadOnlyList<CascadeModeOption> Modes { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAgitationLimits))]
    [NotifyPropertyChangedFor(nameof(ShowAerationLimits))]
    [NotifyPropertyChangedFor(nameof(ShowEffortWindows))]
    [NotifyPropertyChangedFor(nameof(ShowAdvancedGains))]
    [NotifyPropertyChangedFor(nameof(ShowKlaPathSelector))]
    [NotifyPropertyChangedFor(nameof(ModeExplanation))]
    public partial CascadeModeOption SelectedMode { get; set; }

    public bool ShowAgitationLimits => SelectedMode.Mode is CascadeMode.AgitationOnly or CascadeMode.DualCascade;

    public bool ShowAerationLimits => SelectedMode.Mode is CascadeMode.AerationOnly or CascadeMode.DualCascade;

    public bool ShowEffortWindows => SelectedMode.Mode is CascadeMode.DualCascade;

    public bool ShowAdvancedGains => SelectedMode.Mode is CascadeMode.DualCascade;

    public bool ShowKlaPathSelector => SelectedMode.Mode is CascadeMode.KlaPath;

    public string ModeExplanation => SelectedMode.Mode switch
    {
        CascadeMode.AgitationOnly => "O controle ajusta apenas a agitação (RPM). A vazão de ar é mantida fixa.",
        CascadeMode.AerationOnly => "O controle ajusta apenas a vazão de ar (L/min). A agitação é mantida fixa.",
        CascadeMode.DualCascade => "Atuação sequencial de agitação e aeração por janelas de esforço sobrepostas com escalonamento de ganho.",
        CascadeMode.KlaPath => "O controle segue a trajetória ótima na superfície kLa calibrada.",
        _ => "",
    };

    // ── Dissolved-oxygen target (applies to every mode) ──

    [ObservableProperty] public partial string OxygenSetpointText { get; set; } = "30.0";

    // ── PID Fields ──

    [ObservableProperty] public partial string KDotText { get; set; } = "0.070";
    [ObservableProperty] public partial string KpText { get; set; } = "0.065";
    [ObservableProperty] public partial string KiText { get; set; } = "0.0010";
    [ObservableProperty] public partial string KdText { get; set; } = "0.500";
    [ObservableProperty] public partial string TPredText { get; set; } = "60.0";
    [ObservableProperty] public partial string TauDText { get; set; } = "20.0";
    [ObservableProperty] public partial string IMinText { get; set; } = "-30.0";
    [ObservableProperty] public partial string IMaxText { get; set; } = "30.0";
    [ObservableProperty] public partial string MWindowText { get; set; } = "120";
    [ObservableProperty] public partial string JAvgText { get; set; } = "9";
    [ObservableProperty] public partial string NPredText { get; set; } = "7";
    [ObservableProperty] public partial string IntervalText { get; set; } = "3.0";

    // ── Physical Limits ──

    [ObservableProperty] public partial string AgitationMinRpmText { get; set; } = "150";
    [ObservableProperty] public partial string AgitationMaxRpmText { get; set; } = "350";
    [ObservableProperty] public partial string AerationMinLpmText { get; set; } = "0.5";
    [ObservableProperty] public partial string AerationMaxLpmText { get; set; } = "5.0";

    // ── Effort Windows (Cascata) ──

    [ObservableProperty] public partial string AgitationEffortStartText { get; set; } = "0";
    [ObservableProperty] public partial string AgitationEffortEndText { get; set; } = "40";
    [ObservableProperty] public partial string AerationEffortStartText { get; set; } = "30";
    [ObservableProperty] public partial string AerationEffortEndText { get; set; } = "70";

    // ── Advanced Gains (Cascata) ──

    [ObservableProperty] public partial bool HabilitarGainScheduling { get; set; } = true;
    [ObservableProperty] public partial string FatorGanhoAeracaoText { get; set; } = "1.43";

    // ── Kla Profiles (Mapa) ──

    [ObservableProperty] public partial IReadOnlyList<KlaPublishedProfile> AvailablePaths { get; set; } = [];
    [ObservableProperty] public partial KlaPublishedProfile? SelectedPath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationError))]
    public partial string? ValidationError { get; set; }

    public bool HasValidationError => !string.IsNullOrWhiteSpace(ValidationError);

    public Action? CloseRequested { get; set; }

    public bool DialogResult { get; private set; }

    partial void OnSelectedModeChanged(CascadeModeOption oldValue, CascadeModeOption newValue)
    {
        if (_loading)
        {
            return;
        }

        ValidationError = null;

        if (oldValue != null)
        {
            SavePidFieldsToDictionary(oldValue.Mode);
        }

        LoadPidFieldsForMode(newValue.Mode);
    }

    private void LoadPidFieldsForMode(CascadeMode mode)
    {
        _loading = true;
        var p = _modePids.TryGetValue(mode, out var val) ? val : new ModePidSettings();

        KDotText = p.KDot.ToString("F3", CultureInfo.InvariantCulture);
        KpText = p.Kp.ToString("F3", CultureInfo.InvariantCulture);
        KiText = p.Ki.ToString("F4", CultureInfo.InvariantCulture);
        KdText = p.Kd.ToString("F3", CultureInfo.InvariantCulture);
        TPredText = p.TPred.ToString("F1", CultureInfo.InvariantCulture);
        TauDText = p.TauD.ToString("F1", CultureInfo.InvariantCulture);
        IMinText = p.IMin.ToString("F1", CultureInfo.InvariantCulture);
        IMaxText = p.IMax.ToString("F1", CultureInfo.InvariantCulture);
        MWindowText = p.MWindow.ToString(CultureInfo.InvariantCulture);
        JAvgText = p.JAvg.ToString(CultureInfo.InvariantCulture);
        NPredText = p.NPred.ToString(CultureInfo.InvariantCulture);
        IntervalText = p.IntervalSeconds.ToString("F1", CultureInfo.InvariantCulture);
        FatorGanhoAeracaoText = p.FatorGanhoAeracao.ToString("F2", CultureInfo.InvariantCulture);
        HabilitarGainScheduling = p.HabilitarGainScheduling;

        _loading = false;
    }

    private void SavePidFieldsToDictionary(CascadeMode mode)
    {
        _modePids[mode] = new ModePidSettings
        {
            KDot = ParseDouble(KDotText, 0.07),
            Kp = ParseDouble(KpText, 0.065),
            Ki = ParseDouble(KiText, 0.001),
            Kd = ParseDouble(KdText, 0.50),
            TPred = ParseDouble(TPredText, 60.0),
            TauD = ParseDouble(TauDText, 20.0),
            IMin = ParseDouble(IMinText, -30.0),
            IMax = ParseDouble(IMaxText, 30.0),
            MWindow = ParseInt(MWindowText, 120),
            JAvg = ParseInt(JAvgText, 9),
            NPred = ParseInt(NPredText, 7),
            IntervalSeconds = ParseDouble(IntervalText, 3.0),
            FatorGanhoAeracao = ParseDouble(FatorGanhoAeracaoText, 1.43),
            HabilitarGainScheduling = HabilitarGainScheduling,
        };
    }

    private async void LoadAvailablePaths()
    {
        if (_store != null)
        {
            var paths = await _store.LoadPublishedAsync().ConfigureAwait(true);
            AvailablePaths = paths;
            SelectedPath = paths.FirstOrDefault(p => p.ReceiptFingerprint == _cascade.ActivePath?.ReceiptFingerprint)
                           ?? paths.FirstOrDefault();
        }
    }

    private void OnProfilePublished(KlaPublishedProfile profile)
    {
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher)
        {
            dispatcher.InvokeAsync(LoadAvailablePaths);
        }
        else
        {
            LoadAvailablePaths();
        }
    }

    private bool ValidateInputs()
    {
        ValidationError = null;

        // Dissolved-oxygen target
        var oxygenSp = ParseDouble(OxygenSetpointText, -1);
        if (oxygenSp < 0 || oxygenSp > 100)
        {
            ValidationError = "O setpoint de oxigênio dissolvido deve estar entre 0 e 100%.";
            return false;
        }

        // Physical limits
        if (ShowAgitationLimits)
        {
            var agitMin = ParseDouble(AgitationMinRpmText, -1);
            var agitMax = ParseDouble(AgitationMaxRpmText, -1);
            if (agitMin < 0)
            {
                ValidationError = "A rotação mínima de agitação deve ser maior ou igual a 0 RPM.";
                return false;
            }
            if (agitMax <= agitMin)
            {
                ValidationError = "A rotação máxima de agitação deve ser estritamente maior que a mínima.";
                return false;
            }
        }

        if (ShowAerationLimits)
        {
            var aerMin = ParseDouble(AerationMinLpmText, -1);
            var aerMax = ParseDouble(AerationMaxLpmText, -1);
            if (aerMin < 0)
            {
                ValidationError = "A vazão mínima de aeração deve ser maior ou igual a 0 L/min.";
                return false;
            }
            if (aerMax <= aerMin)
            {
                ValidationError = "A vazão máxima de aeração deve ser estritamente maior que a mínima.";
                return false;
            }
        }

        // Effort windows (Cascata)
        if (ShowEffortWindows)
        {
            var agitStart = ParseDouble(AgitationEffortStartText, -1);
            var agitEnd = ParseDouble(AgitationEffortEndText, -1);
            var aerStart = ParseDouble(AerationEffortStartText, -1);
            var aerEnd = ParseDouble(AerationEffortEndText, -1);

            if (agitStart < 0 || agitEnd > 100 || agitEnd <= agitStart)
            {
                ValidationError = "A janela de esforço da agitação deve estar contida em 0–100% com Fim > Início.";
                return false;
            }
            if (aerStart < 0 || aerEnd > 100 || aerEnd <= aerStart)
            {
                ValidationError = "A janela de esforço da aeração deve estar contida em 0–100% com Fim > Início.";
                return false;
            }
        }

        // PID parameters
        var kDot = ParseDouble(KDotText, -1);
        if (kDot < 0)
        {
            ValidationError = "O ganho K_DOT deve ser maior ou igual a zero.";
            return false;
        }

        var kp = ParseDouble(KpText, -1);
        if (kp < 0)
        {
            ValidationError = "O ganho proporcional Kp deve ser maior ou igual a zero.";
            return false;
        }

        var ki = ParseDouble(KiText, -1);
        if (ki < 0)
        {
            ValidationError = "O ganho integral Ki deve ser maior ou igual a zero.";
            return false;
        }

        var kd = ParseDouble(KdText, -1);
        if (kd < 0)
        {
            ValidationError = "O ganho derivativo Kd deve ser maior ou igual a zero.";
            return false;
        }

        var tPred = ParseDouble(TPredText, -1);
        if (tPred < 0)
        {
            ValidationError = "O horizonte de predição T_pred deve ser maior ou igual a zero.";
            return false;
        }

        var tauD = ParseDouble(TauDText, -1);
        if (tauD < 0)
        {
            ValidationError = "A constante de tempo Tau_D deve ser maior ou igual a zero.";
            return false;
        }

        var iMin = ParseDouble(IMinText, 0);
        var iMax = ParseDouble(IMaxText, 0);
        if (iMax <= iMin)
        {
            ValidationError = "O limite superior da integral deve ser estritamente maior que o inferior.";
            return false;
        }

        var mWindow = ParseInt(MWindowText, 0);
        if (mWindow <= 0)
        {
            ValidationError = "A janela do integrador deve ser maior que zero segundos.";
            return false;
        }

        var jAvg = ParseInt(JAvgText, 0);
        if (jAvg < 2)
        {
            ValidationError = "A janela de derivada (J_AVG) deve conter pelo menos 2 amostras.";
            return false;
        }

        var nPred = ParseInt(NPredText, 0);
        if (nPred < 2)
        {
            ValidationError = "A janela do preditor (N_PRED) deve conter pelo menos 2 amostras.";
            return false;
        }

        var interval = ParseDouble(IntervalText, 0);
        if (interval <= 0)
        {
            ValidationError = "O período do loop deve ser maior que zero segundos.";
            return false;
        }

        return true;
    }

    [RelayCommand]
    private void Apply()
    {
        if (!ValidateInputs())
        {
            return;
        }

        SavePidFieldsToDictionary(SelectedMode.Mode);

        var currentCfg = _settings.Current.Cascade;
        var updated = currentCfg with
        {
            OxygenSetpointPercent = ParseDouble(OxygenSetpointText, currentCfg.OxygenSetpointPercent),

            AgitationMinRpm = ParseDouble(AgitationMinRpmText, currentCfg.AgitationMinRpm),
            AgitationMaxRpm = ParseDouble(AgitationMaxRpmText, currentCfg.AgitationMaxRpm),
            AerationMinLpm = ParseDouble(AerationMinLpmText, currentCfg.AerationMinLpm),
            AerationMaxLpm = ParseDouble(AerationMaxLpmText, currentCfg.AerationMaxLpm),

            AgitationEffortStart = ParseDouble(AgitationEffortStartText, currentCfg.AgitationEffortStart),
            AgitationEffortEnd = ParseDouble(AgitationEffortEndText, currentCfg.AgitationEffortEnd),
            AerationEffortStart = ParseDouble(AerationEffortStartText, currentCfg.AerationEffortStart),
            AerationEffortEnd = ParseDouble(AerationEffortEndText, currentCfg.AerationEffortEnd),

            AgitationPid = _modePids[CascadeMode.AgitationOnly],
            AerationPid = _modePids[CascadeMode.AerationOnly],
            CascadePid = _modePids[CascadeMode.DualCascade],
            MapPid = _modePids[CascadeMode.KlaPath],
        };

        var wasEngaged = _cascade.IsEngaged;

        // Applying while engaged re-engages so the new mode and parameters take effect live.
        // Block the one foreseeable re-engage failure before tearing down the running loop.
        if (wasEngaged && SelectedMode.Mode == CascadeMode.KlaPath && SelectedPath is null)
        {
            ValidationError = "Selecione um mapa kLa publicado para reativar no modo Mapa.";
            return;
        }

        // Capture the running actuators so the re-engage is bumpless.
        var reengageRpm = _cascade.LastActuation?.AgitationRpm ?? _settings.Current.Setpoints.MotorRpm;
        var reengageLpm = _cascade.LastActuation?.AerationLpm ?? _settings.Current.Setpoints.FlowLitresPerMinute;

        if (wasEngaged)
        {
            _cascade.Disengage("reconfiguração do controle de oxigênio");
        }

        _settings.Update(s => s with { Cascade = updated });
        _cascade.Configure(updated);
        _cascade.SelectMode(SelectedMode.Mode);

        if (SelectedMode.Mode == CascadeMode.KlaPath)
        {
            _cascade.SelectPath(SelectedPath);
        }

        if (wasEngaged)
        {
            _cascade.Engage(reengageRpm, reengageLpm);
            if (!_cascade.IsEngaged)
            {
                _cascade.CanEngage(out var reason);
                ValidationError = reason ?? "Não foi possível reativar o controle de oxigênio com a nova configuração.";
                return;
            }
        }

        DialogResult = true;
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        DialogResult = false;
        CloseRequested?.Invoke();
    }

    private static double ParseDouble(string? text, double fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        return double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : fallback;
    }

    private static int ParseInt(string? text, int fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : fallback;
    }

    public void Dispose()
    {
        if (_store != null)
        {
            _store.ProfilePublished -= OnProfilePublished;
        }
    }
}
