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
        AerationStepLpmText = cfg.AerationStepLpm.ToString("0.0###", CultureInfo.InvariantCulture);

        // Restart effort (D-074)
        UseRestartEffort = cfg.UseRestartEffort;
        RestartEffortText = cfg.RestartEffortPercent.ToString("0.0", CultureInfo.InvariantCulture);
        _lastEffort = cfg.LastEffortPercent;
        LastEffortText = cfg.LastEffortPercent is { } last
            ? $"Último esforço registrado: {last.ToString("0.0", CultureInfo.CurrentCulture)} %" +
              (cfg.LastEffortAt is { } at ? $" ({at.ToLocalTime():dd/MM HH:mm})" : "")
            : "Nenhum esforço registrado ainda (fica salvo ao desativar o controle).";

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
    [NotifyPropertyChangedFor(nameof(ShowPhysicalLimits))]
    [NotifyPropertyChangedFor(nameof(ShowEffortWindows))]
    [NotifyPropertyChangedFor(nameof(ShowAdvancedGains))]
    [NotifyPropertyChangedFor(nameof(ShowKlaPathSelector))]
    [NotifyPropertyChangedFor(nameof(ModeExplanation))]
    [NotifyPropertyChangedFor(nameof(RestartPreviewText))]
    public partial CascadeModeOption SelectedMode { get; set; }

    public bool ShowAgitationLimits => SelectedMode.Mode is CascadeMode.AgitationOnly or CascadeMode.DualCascade;

    public bool ShowAerationLimits => SelectedMode.Mode is CascadeMode.AerationOnly or CascadeMode.DualCascade;

    /// <summary>The limits card: it was keyed to agitation alone, which hid the aeration fields in aeration-only mode.</summary>
    public bool ShowPhysicalLimits => ShowAgitationLimits || ShowAerationLimits;

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

    [ObservableProperty] public partial string KDotText { get; set; } = "0.075";
    [ObservableProperty] public partial string KpText { get; set; } = "0.035";
    [ObservableProperty] public partial string KiText { get; set; } = "0.0010";
    [ObservableProperty] public partial string KdText { get; set; } = "3.500";
    [ObservableProperty] public partial string TPredText { get; set; } = "60.0";
    [ObservableProperty] public partial string TauDText { get; set; } = "30.0";
    [ObservableProperty] public partial string IMinText { get; set; } = "-2.5";
    [ObservableProperty] public partial string IMaxText { get; set; } = "2.5";
    [ObservableProperty] public partial string MWindowText { get; set; } = "2400";
    [ObservableProperty] public partial string JAvgText { get; set; } = "20";
    [ObservableProperty] public partial string NPredText { get; set; } = "15";
    [ObservableProperty] public partial string IntervalText { get; set; } = "3.0";

    // ── Physical Limits ──

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RestartPreviewText))] public partial string AgitationMinRpmText { get; set; } = "50";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RestartPreviewText))] public partial string AgitationMaxRpmText { get; set; } = "800";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RestartPreviewText))] public partial string AerationMinLpmText { get; set; } = "0.50";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RestartPreviewText))] public partial string AerationMaxLpmText { get; set; } = "12.00";

    /// <summary>Grid of the aeration setpoint sent to the flowmeter, in L/min (D-070).</summary>
    [ObservableProperty] public partial string AerationStepLpmText { get; set; } = "0.2";

    // ── Effort Windows (Cascata) ──

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RestartPreviewText))] public partial string AgitationEffortStartText { get; set; } = "0";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RestartPreviewText))] public partial string AgitationEffortEndText { get; set; } = "90";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RestartPreviewText))] public partial string AerationEffortStartText { get; set; } = "10";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RestartPreviewText))] public partial string AerationEffortEndText { get; set; } = "100";

    // ── Restart during a run (D-074) ──

    private readonly double? _lastEffort;

    /// <summary>Engage from <see cref="RestartEffortText"/> instead of the manual setpoints.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RestartPreviewText))]
    public partial bool UseRestartEffort { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RestartPreviewText))]
    public partial string RestartEffortText { get; set; } = "0.0";

    [ObservableProperty] public partial string LastEffortText { get; set; } = "";

    public bool HasLastEffort => _lastEffort is not null;

    /// <summary>What the actuators start at for the restart effort, with the limits being edited.</summary>
    public string RestartPreviewText
    {
        get
        {
            if (!UseRestartEffort) return "Desligado: ao ativar, o esforço parte dos setpoints manuais de agitação e vazão (sem salto).";
            var effort = ParseDouble(RestartEffortText, -1);
            if (effort is < 0 or > 100) return "O esforço deve estar entre 0 e 100 %.";
            if (SelectedMode.Mode == CascadeMode.KlaPath) return "No modo Mapa, agitação e vazão seguem o mapa kLa publicado a partir deste esforço.";
            var agitation = new ActuatorWindow(CascadeController.AgitationActuator,
                ParseDouble(AgitationMinRpmText, 0), ParseDouble(AgitationMaxRpmText, 0),
                ParseDouble(AgitationEffortStartText, 0), ParseDouble(AgitationEffortEndText, 100));
            var aeration = new ActuatorWindow(CascadeController.AerationActuator,
                ParseDouble(AerationMinLpmText, 0), ParseDouble(AerationMaxLpmText, 0),
                ParseDouble(AerationEffortStartText, 0), ParseDouble(AerationEffortEndText, 100));
            CascadeAllocation allocation = SelectedMode.Mode switch
            {
                CascadeMode.AgitationOnly => SingleActuatorAllocation.Agitation(agitation.Min, agitation.Max, 0),
                CascadeMode.AerationOnly => SingleActuatorAllocation.Aeration(aeration.Min, aeration.Max, 0),
                _ => new WindowAllocation(agitation, aeration),
            };
            var (rpm, lpm) = allocation.Allocate(effort);
            var c = CultureInfo.CurrentCulture;
            return SelectedMode.Mode switch
            {
                CascadeMode.AgitationOnly => $"Ao ativar: agitação {rpm.ToString("F0", c)} rpm (a vazão fica onde está).",
                CascadeMode.AerationOnly => $"Ao ativar: aeração {lpm.ToString("F2", c)} L/min (a agitação fica onde está).",
                _ => $"Ao ativar: agitação {rpm.ToString("F0", c)} rpm e aeração {lpm.ToString("F2", c)} L/min.",
            };
        }
    }

    [RelayCommand(CanExecute = nameof(HasLastEffort))]
    private void UseLastEffort()
    {
        if (_lastEffort is not { } last) return;
        RestartEffortText = last.ToString("0.0", CultureInfo.InvariantCulture);
        UseRestartEffort = true;
    }

    // ── Advanced Gains (Cascata) ──

    [ObservableProperty] public partial bool HabilitarGainScheduling { get; set; }
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
        // The formats keep the usual decimals but never drop a digit, so reopening and applying
        // the page cannot round a saved gain (D-071).
        var p = _modePids.TryGetValue(mode, out var val) ? val : new ModePidSettings();

        KDotText = p.KDot.ToString("0.000####", CultureInfo.InvariantCulture);
        KpText = p.Kp.ToString("0.000####", CultureInfo.InvariantCulture);
        KiText = p.Ki.ToString("0.0000###", CultureInfo.InvariantCulture);
        KdText = p.Kd.ToString("0.000####", CultureInfo.InvariantCulture);
        TPredText = p.TPred.ToString("0.0#####", CultureInfo.InvariantCulture);
        TauDText = p.TauD.ToString("0.0#####", CultureInfo.InvariantCulture);
        IMinText = p.IMin.ToString("0.0#####", CultureInfo.InvariantCulture);
        IMaxText = p.IMax.ToString("0.0#####", CultureInfo.InvariantCulture);
        MWindowText = p.MWindow.ToString(CultureInfo.InvariantCulture);
        JAvgText = p.JAvg.ToString(CultureInfo.InvariantCulture);
        NPredText = p.NPred.ToString(CultureInfo.InvariantCulture);
        IntervalText = p.IntervalSeconds.ToString("0.0#####", CultureInfo.InvariantCulture);
        FatorGanhoAeracaoText = p.FatorGanhoAeracao.ToString("0.00#####", CultureInfo.InvariantCulture);
        HabilitarGainScheduling = p.HabilitarGainScheduling;

        _loading = false;
    }

    private void SavePidFieldsToDictionary(CascadeMode mode)
    {
        _modePids[mode] = new ModePidSettings
        {
            KDot = ParseDouble(KDotText, 0.075),
            Kp = ParseDouble(KpText, 0.035),
            Ki = ParseDouble(KiText, 0.001),
            Kd = ParseDouble(KdText, 3.50),
            TPred = ParseDouble(TPredText, 60.0),
            TauD = ParseDouble(TauDText, 30.0),
            IMin = ParseDouble(IMinText, -2.5),
            IMax = ParseDouble(IMaxText, 2.5),
            MWindow = ParseInt(MWindowText, 2400),
            JAvg = ParseInt(JAvgText, 20),
            NPred = ParseInt(NPredText, 15),
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

            var aerStep = ParseDouble(AerationStepLpmText, -1);
            if (aerStep < 0.01 || aerStep > 5)
            {
                ValidationError = "O passo da vazão de aeração deve estar entre 0,01 e 5 L/min.";
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

        if (UseRestartEffort && ParseDouble(RestartEffortText, -1) is < 0 or > 100)
        {
            ValidationError = "O esforço de reinício deve estar entre 0 e 100 %.";
            return false;
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
            AerationStepLpm = ParseDouble(AerationStepLpmText, currentCfg.AerationStepLpm),

            AgitationEffortStart = ParseDouble(AgitationEffortStartText, currentCfg.AgitationEffortStart),
            AgitationEffortEnd = ParseDouble(AgitationEffortEndText, currentCfg.AgitationEffortEnd),
            AerationEffortStart = ParseDouble(AerationEffortStartText, currentCfg.AerationEffortStart),
            AerationEffortEnd = ParseDouble(AerationEffortEndText, currentCfg.AerationEffortEnd),

            UseRestartEffort = UseRestartEffort,
            RestartEffortPercent = Math.Clamp(ParseDouble(RestartEffortText, currentCfg.RestartEffortPercent), 0, 100),

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

        _settings.Update(s => s with
        {
            Cascade = updated with { LastEffortPercent = s.Cascade.LastEffortPercent, LastEffortAt = s.Cascade.LastEffortAt },
        });
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
