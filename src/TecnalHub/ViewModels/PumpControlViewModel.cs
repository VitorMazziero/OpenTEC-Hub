using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TecnalHub.Protocol;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Control;
using TecnalHub.Services.Persistence;

namespace TecnalHub.ViewModels;

/// <summary>One selectable pump profile mode, for the mode dropdown.</summary>
public sealed record PumpModeOption(PumpProfileMode Mode, string Label);

/// <summary>
/// The external peristaltic pump (Phase 3 WP2): enable, the five firmware profile modes with a
/// shared flow/accumulated-volume preview, and the optional proportional-gas coupling.
/// </summary>
/// <remarks>
/// <para>
/// The enable is an immediate toggle: on it sends <c>pumpComm:1</c>, off sends the safe disabled
/// frame <c>pumpComm:0, mode:0, speed:0</c>. A profile is staged (mode + operating window +
/// parameters) and sent by <b>Aplicar perfil</b>; the operating window is shared across modes
/// rather than stored per mode as v.6 did ([[D-021]], <c>docs/DECISIONS.md</c>).
/// </para>
/// <para>
/// Proportional gas couples the pump volume to the air flow: <c>Q_g = (V₀ + PumpVol/1000)·vvm</c>,
/// clamped to <c>maxFlow</c> and dispatched as a standard aeration frame through the same arbiter
/// as manual/cascade flow. It is refused when the cascade owns aeration — that is the point of the
/// single command owner. Both valves are closed on that frame; v.6's stray nitrogen-open-at-zero
/// quirk is not reproduced.
/// </para>
/// </remarks>
public sealed partial class PumpControlViewModel : ObservableObject, IDisposable
{
    /// <summary>Only send a new proportional-gas flow when it moves at least this much, L/min.</summary>
    private const double GasFlowResendThresholdLpm = 0.01;

    private readonly IDeviceService _device;
    private readonly ISettingsService _settings;
    private bool _initialised;
    private PumpControlSettings _committed;

    /// <summary>Latest pump volume from telemetry, mL. Drives the gas coupling and the readout.</summary>
    private double _lastPumpVolumeMl;
    private double? _lastGasFlowSentLpm;

    public PumpControlViewModel(IDeviceService device, ISettingsService settings)
    {
        _device = device;
        _settings = settings;
        _committed = settings.Current.PumpControl;

        ModeOptions =
        [
            new(PumpProfileMode.Constant, "Constante"),
            new(PumpProfileMode.Linear, "Linear"),
            new(PumpProfileMode.Exponential, "Exponencial"),
            new(PumpProfileMode.Polynomial, "Polinomial"),
            new(PumpProfileMode.Piecewise, "Por segmentos"),
        ];

        Load(_committed);
        _device.TelemetryReceived += OnTelemetryReceived;
        _initialised = true;
        ValidateAndRefresh();
        HasPendingChange = false;
    }

    public IReadOnlyList<PumpModeOption> ModeOptions { get; }

    /// <summary>Pump command routing enabled — <c>pumpComm</c>. Immediate, like v.6.</summary>
    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConstant))]
    [NotifyPropertyChangedFor(nameof(ShowLinear))]
    [NotifyPropertyChangedFor(nameof(ShowExponential))]
    [NotifyPropertyChangedFor(nameof(ShowPolynomial))]
    [NotifyPropertyChangedFor(nameof(ShowPiecewise))]
    public partial PumpModeOption SelectedModeOption { get; set; }

    [ObservableProperty]
    public partial string InitMinutesText { get; set; } = "0";

    [ObservableProperty]
    public partial string FinalMinutesText { get; set; } = "60";

    [ObservableProperty]
    public partial string LambdaConstText { get; set; } = "1";

    [ObservableProperty]
    public partial string LambdaLinearText { get; set; } = "1";

    [ObservableProperty]
    public partial string PhiLinearText { get; set; } = "0";

    [ObservableProperty]
    public partial string LambdaExpText { get; set; } = "1";

    [ObservableProperty]
    public partial string PhiExpText { get; set; } = "0";

    [ObservableProperty]
    public partial string PolynomialCoefficientsText { get; set; } = "1";

    [ObservableProperty]
    public partial string PiecewiseTimesText { get; set; } = "0, 60";

    [ObservableProperty]
    public partial string PiecewiseFlowsText { get; set; } = "1, 1";

    [ObservableProperty]
    public partial bool GasProportionalEnabled { get; set; }

    [ObservableProperty]
    public partial string InitialVolumeText { get; set; } = "1.0";

    [ObservableProperty]
    public partial string VvmText { get; set; } = "0.5";

    [ObservableProperty]
    public partial bool HasPendingChange { get; set; }

    [ObservableProperty]
    public partial string? ValidationError { get; set; }

    /// <summary>The sampled flow/volume preview for the active profile, or null when invalid.</summary>
    [ObservableProperty]
    public partial PumpPreview? Preview { get; set; }

    [ObservableProperty]
    public partial string PeakFlowText { get; set; } = "—";

    [ObservableProperty]
    public partial string TotalVolumeText { get; set; } = "—";

    [ObservableProperty]
    public partial string GasFlowText { get; set; } = "—";

    [ObservableProperty]
    public partial string PumpFlowText { get; set; } = "—";

    [ObservableProperty]
    public partial string PumpVolumeText { get; set; } = "—";

    [ObservableProperty]
    public partial string StatusText { get; set; } =
        "Parâmetros restaurados para revisão; nenhum comando foi enviado.";

    public bool ShowConstant => SelectedModeOption.Mode == PumpProfileMode.Constant;

    public bool ShowLinear => SelectedModeOption.Mode == PumpProfileMode.Linear;

    public bool ShowExponential => SelectedModeOption.Mode == PumpProfileMode.Exponential;

    public bool ShowPolynomial => SelectedModeOption.Mode == PumpProfileMode.Polynomial;

    public bool ShowPiecewise => SelectedModeOption.Mode == PumpProfileMode.Piecewise;

    public bool IsValid => ValidationError is null;

    public bool CanApply => IsEnabled && IsValid;

    public string StateText => IsEnabled ? "Ativa" : "Desligada";

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(StateText));
        ValidateAndRefresh();

        if (!_initialised)
        {
            return;
        }

        _device.Send(value ? CommandBuilders.PumpEnable() : CommandBuilders.PumpDisable());
        if (!value)
        {
            _lastGasFlowSentLpm = null;
        }

        StatusText = value
            ? "Bomba externa ativada."
            : "Bomba externa desativada (quadro seguro pumpComm:0, mode:0, speed:0).";
    }

    partial void OnSelectedModeOptionChanged(PumpModeOption value) => ValidateAndRefresh();

    partial void OnInitMinutesTextChanged(string value) => ValidateAndRefresh();

    partial void OnFinalMinutesTextChanged(string value) => ValidateAndRefresh();

    partial void OnLambdaConstTextChanged(string value) => ValidateAndRefresh();

    partial void OnLambdaLinearTextChanged(string value) => ValidateAndRefresh();

    partial void OnPhiLinearTextChanged(string value) => ValidateAndRefresh();

    partial void OnLambdaExpTextChanged(string value) => ValidateAndRefresh();

    partial void OnPhiExpTextChanged(string value) => ValidateAndRefresh();

    partial void OnPolynomialCoefficientsTextChanged(string value) => ValidateAndRefresh();

    partial void OnPiecewiseTimesTextChanged(string value) => ValidateAndRefresh();

    partial void OnPiecewiseFlowsTextChanged(string value) => ValidateAndRefresh();

    partial void OnGasProportionalEnabledChanged(bool value)
    {
        RefreshPendingState();
        RefreshGasReadout();
        if (_initialised && !value)
        {
            _lastGasFlowSentLpm = null;
            StatusText = "Acoplamento de gás proporcional desativado; a vazão permanece no último valor.";
        }
    }

    partial void OnInitialVolumeTextChanged(string value) { RefreshPendingState(); RefreshGasReadout(); }

    partial void OnVvmTextChanged(string value) { RefreshPendingState(); RefreshGasReadout(); }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void ApplyProfile()
    {
        if (!TryBuildSpec(out var spec, out var error))
        {
            StatusText = error ?? "Revise os parâmetros do perfil da bomba.";
            return;
        }

        var command = PumpProfileMath.BuildCommand(spec);
        _device.Send(command);

        if (TryGetStagedSettings(out var staged))
        {
            _committed = staged with { Version = _committed.Version + 1 };
            _settings.Update(settings => settings with { PumpControl = _committed });
        }

        HasPendingChange = false;
        StatusText = $"Perfil {SelectedModeOption.Label.ToLower(CultureInfo.CurrentCulture)} enviado " +
                     $"({command.Count} campos, versão {_committed.Version}).";
    }

    [RelayCommand]
    private void Revert()
    {
        Load(_committed);
        HasPendingChange = false;
        StatusText = "Alterações não enviadas do perfil da bomba foram revertidas.";
    }

    /// <summary>The safe disabled frame, for the operator safe-stop on Controle.</summary>
    public TecnalCommand BuildSafeStop() => CommandBuilders.PumpDisable();

    /// <summary>Marks the pump stopped after a bulk safe-stop already sent its disable frame.</summary>
    public void MarkStopped()
    {
        _initialised = false; // suppress the immediate re-send from the setter
        IsEnabled = false;
        _initialised = true;
        _lastGasFlowSentLpm = null;
    }

    public bool TryGetStagedSettings(out PumpControlSettings settings)
    {
        settings = _committed;
        if (!TryBuildSpec(out _, out _))
        {
            return false;
        }

        if (!DosingInput.TryParseDouble(InitMinutesText, out var init) ||
            !DosingInput.TryParseDouble(FinalMinutesText, out var final) ||
            !DosingInput.TryParseDouble(LambdaConstText, out var lambdaConst) ||
            !DosingInput.TryParseDouble(LambdaLinearText, out var lambdaLinear) ||
            !DosingInput.TryParseDouble(PhiLinearText, out var phiLinear) ||
            !DosingInput.TryParseDouble(LambdaExpText, out var lambdaExp) ||
            !DosingInput.TryParseDouble(PhiExpText, out var phiExp) ||
            !TryParseList(PolynomialCoefficientsText, out var coeffs) ||
            !TryParseList(PiecewiseTimesText, out var times) ||
            !TryParseList(PiecewiseFlowsText, out var flows) ||
            !TryParseGas(out var gasEnabled, out var v0, out var vvm))
        {
            return false;
        }

        settings = new PumpControlSettings
        {
            Version = _committed.Version,
            Mode = SelectedModeOption.Mode,
            InitMinutes = init,
            FinalMinutes = final,
            LambdaConst = lambdaConst,
            LambdaLinear = lambdaLinear,
            PhiLinear = phiLinear,
            LambdaExp = lambdaExp,
            PhiExp = phiExp,
            PolynomialCoefficients = [.. coeffs],
            PiecewiseTimes = [.. times],
            PiecewiseFlows = [.. flows],
            GasProportionalEnabled = gasEnabled,
            InitialVolumeLitres = v0,
            Vvm = vvm,
        };
        return true;
    }

    /// <summary>Parses and validates the active mode's fields into a profile spec.</summary>
    public bool TryBuildSpec(out PumpProfileSpec spec, out string? error)
    {
        spec = null!;

        if (!DosingInput.TryParseDouble(InitMinutesText, out var init) || init < 0.0)
        {
            error = "Tempo inicial: minutos ≥ 0.";
            return false;
        }

        if (!DosingInput.TryParseDouble(FinalMinutesText, out var final) || final <= 0.0)
        {
            error = "Tempo final: minutos > 0.";
            return false;
        }

        if (final <= init)
        {
            error = "O tempo final deve ser maior que o inicial.";
            return false;
        }

        double lambda = 0, phi = 0;
        IReadOnlyList<double> coeffs = [];
        IReadOnlyList<double> times = [];
        IReadOnlyList<double> flows = [];

        switch (SelectedModeOption.Mode)
        {
            case PumpProfileMode.Constant:
                if (!DosingInput.TryParseDouble(LambdaConstText, out lambda))
                {
                    error = "λ: número válido.";
                    return false;
                }

                break;

            case PumpProfileMode.Linear:
                if (!DosingInput.TryParseDouble(LambdaLinearText, out lambda) ||
                    !DosingInput.TryParseDouble(PhiLinearText, out phi))
                {
                    error = "λ e φ: números válidos.";
                    return false;
                }

                break;

            case PumpProfileMode.Exponential:
                if (!DosingInput.TryParseDouble(LambdaExpText, out lambda) ||
                    !DosingInput.TryParseDouble(PhiExpText, out phi))
                {
                    error = "λ e φ: números válidos.";
                    return false;
                }

                break;

            case PumpProfileMode.Polynomial:
                if (!TryParseList(PolynomialCoefficientsText, out coeffs) || coeffs.Count == 0)
                {
                    error = "Coeficientes: lista de números separada por vírgula.";
                    return false;
                }

                if (coeffs.Count > CommandKeys.MaxPolynomialCoefficientIndex + 1)
                {
                    error = $"No máximo {CommandKeys.MaxPolynomialCoefficientIndex + 1} coeficientes (p0..p{CommandKeys.MaxPolynomialCoefficientIndex}).";
                    return false;
                }

                break;

            case PumpProfileMode.Piecewise:
                if (!TryParseList(PiecewiseTimesText, out times) || !TryParseList(PiecewiseFlowsText, out flows))
                {
                    error = "Tempos e vazões: listas de números separadas por vírgula.";
                    return false;
                }

                if (times.Count != flows.Count)
                {
                    error = "Tempos e vazões devem ter a mesma quantidade de pontos.";
                    return false;
                }

                if (times.Count is < 2 or > CommandKeys.MaxPiecewiseSegments)
                {
                    error = $"Por segmentos: de 2 a {CommandKeys.MaxPiecewiseSegments} pontos.";
                    return false;
                }

                if (Math.Abs(times[0]) > 1e-9)
                {
                    error = "O primeiro ponto de tempo (t0) deve ser 0.";
                    return false;
                }

                for (var i = 1; i < times.Count; i++)
                {
                    if (times[i] <= times[i - 1])
                    {
                        error = "Os pontos de tempo devem ser crescentes.";
                        return false;
                    }
                }

                break;

            default:
                error = "Modo inválido.";
                return false;
        }

        spec = new PumpProfileSpec(
            SelectedModeOption.Mode, init, final, lambda, phi, coeffs, times, flows);
        error = null;
        return true;
    }

    private void Load(PumpControlSettings s)
    {
        SelectedModeOption = ModeOptions.FirstOrDefault(o => o.Mode == s.Mode) ?? ModeOptions[0];
        InitMinutesText = DosingInput.Format(s.InitMinutes, 1);
        FinalMinutesText = DosingInput.Format(s.FinalMinutes, 1);
        LambdaConstText = DosingInput.Format(s.LambdaConst, 3);
        LambdaLinearText = DosingInput.Format(s.LambdaLinear, 3);
        PhiLinearText = DosingInput.Format(s.PhiLinear, 3);
        LambdaExpText = DosingInput.Format(s.LambdaExp, 3);
        PhiExpText = DosingInput.Format(s.PhiExp, 3);
        PolynomialCoefficientsText = FormatList(s.PolynomialCoefficients);
        PiecewiseTimesText = FormatList(s.PiecewiseTimes);
        PiecewiseFlowsText = FormatList(s.PiecewiseFlows);
        GasProportionalEnabled = s.GasProportionalEnabled;
        InitialVolumeText = DosingInput.Format(s.InitialVolumeLitres, 2);
        VvmText = DosingInput.Format(s.Vvm, 2);
    }

    private void ValidateAndRefresh()
    {
        if (!_initialised)
        {
            return;
        }

        var valid = TryBuildSpec(out var spec, out var error);
        ValidationError = valid ? null : error;
        Preview = valid ? PumpProfileMath.Sample(spec) : null;
        PeakFlowText = valid ? Preview!.PeakFlowMlPerMin.ToString("F2", CultureInfo.CurrentCulture) : "—";
        TotalVolumeText = valid ? Preview!.TotalVolumeMl.ToString("F1", CultureInfo.CurrentCulture) : "—";

        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(CanApply));
        ApplyProfileCommand.NotifyCanExecuteChanged();
        RefreshPendingState();
        RefreshGasReadout();
    }

    private void RefreshPendingState()
    {
        if (!_initialised)
        {
            return;
        }

        HasPendingChange = !TryGetStagedSettings(out var staged) || staged != _committed;
    }

    private void RefreshGasReadout()
    {
        if (TryParseGas(out _, out var v0, out var vvm))
        {
            var maxFlow = _settings.Current.Setpoints.MaxFlowLitresPerMinute;
            var qg = Math.Clamp((v0 + (_lastPumpVolumeMl / 1000.0)) * vvm, 0.0, maxFlow);
            GasFlowText = qg.ToString("F2", CultureInfo.CurrentCulture);
        }
        else
        {
            GasFlowText = "—";
        }
    }

    private void OnTelemetryReceived(SensorSnapshot snapshot)
    {
        _lastPumpVolumeMl = snapshot.PumpVolume > SensorReadings.NotReceived ? snapshot.PumpVolume : 0.0;

        PumpFlowText = snapshot.PumpFlow > SensorReadings.NotReceived
            ? snapshot.PumpFlow.ToString("F3", CultureInfo.CurrentCulture)
            : "—";
        PumpVolumeText = snapshot.PumpVolume > SensorReadings.NotReceived
            ? snapshot.PumpVolume.ToString("F3", CultureInfo.CurrentCulture)
            : "—";

        RefreshGasReadout();
        MaybeSendProportionalGas();
    }

    /// <summary>
    /// While the coupling is active, drive the air flow from the pump volume, sending only when
    /// the target moves materially. Refused by the arbiter if the cascade owns aeration.
    /// </summary>
    private void MaybeSendProportionalGas()
    {
        if (!_initialised || !GasProportionalEnabled || !IsEnabled)
        {
            return;
        }

        if (!TryParseGas(out var enabled, out var v0, out var vvm) || !enabled)
        {
            return;
        }

        var maxFlow = _settings.Current.Setpoints.MaxFlowLitresPerMinute;
        var qg = Math.Clamp((v0 + (_lastPumpVolumeMl / 1000.0)) * vvm, 0.0, maxFlow);

        if (_lastGasFlowSentLpm is { } last && Math.Abs(qg - last) < GasFlowResendThresholdLpm)
        {
            return;
        }

        _device.Send(CommandBuilders.FlowSetpoint(qg, maxFlow, valve1: false, valve2: false));
        _lastGasFlowSentLpm = qg;
    }

    private bool TryParseGas(out bool enabled, out double initialVolume, out double vvm)
    {
        enabled = GasProportionalEnabled;
        vvm = 0.0;
        return DosingInput.TryParseDouble(InitialVolumeText, out initialVolume) && initialVolume >= 0.0 &&
               DosingInput.TryParseDouble(VvmText, out vvm) && vvm >= 0.0;
    }

    private static bool TryParseList(string? text, out IReadOnlyList<double> values)
    {
        var list = new List<double>();
        foreach (var token in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!DosingInput.TryParseDouble(token, out var value))
            {
                values = [];
                return false;
            }

            list.Add(value);
        }

        values = list;
        return list.Count > 0;
    }

    private static string FormatList(IReadOnlyList<double> values)
        => string.Join(", ", values.Select(v => DosingInput.Format(v, 3)));

    public void Dispose() => _device.TelemetryReceived -= OnTelemetryReceived;
}
