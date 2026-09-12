using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Control;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.ViewModels;

/// <summary>One selectable pump profile mode, for the mode dropdown.</summary>
public sealed record PumpModeOption(PumpProfileMode Mode, string Label)
{
    // The application ComboBox template renders selected records through ToString(),
    // while its drop-down items honour DisplayMemberPath. Keep both surfaces human-readable.
    public override string ToString() => Label;
}

/// <summary>
/// The external peristaltic pump (Phase 3 WP2): enable, the five firmware profile modes with a
/// shared flow/accumulated-volume preview, and the optional proportional-gas coupling.
/// </summary>
/// <remarks>
/// <para>
/// The enable is an immediate toggle: on it sends <c>pumpComm:1</c>; off sends <b>two ordered
/// frames</b>, <c>{"mode":0,"speed":0}</c> and then <c>{"pumpComm":0}</c>. v.6's single
/// <c>{"pumpComm":0,"mode":0,"speed":0}</c> does not stop the pump — the Hub clears routing
/// while parsing that frame and then drops its own <c>mode:0</c>, so the node keeps dosing and
/// only its telemetry goes quiet. A profile is staged (mode + operating window + parameters) and
/// sent by <b>Aplicar perfil</b>; the operating window is shared across modes rather than stored
/// per mode as v.6 did ([[D-021]], <c>docs/DECISIONS.md</c>).
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
    private readonly ICommandArbiter? _arbiter;
    private readonly IManualDispatcher _dispatcher;
    private readonly ISettingsService _settings;
    private readonly ICascadeService? _cascade;
    private bool _initialised;

    /// <summary>Guards the enable setter while a refused toggle is being rolled back.</summary>
    private bool _revertingEnable;

    /// <summary>Guards the gas-proportional toggle while a refused toggle is rolled back.</summary>
    private bool _revertingGasProportional;

    private readonly TimeProvider _timeProvider;
    private DateTime? _resetVolumeRequestedAt;
    private bool _awaitingResetVolume;

    private PumpControlSettings _committed;

    /// <summary>Latest pump volume from telemetry, mL. Drives the gas coupling and the readout.</summary>
    private double _lastPumpVolumeMl;
    private double? _lastGasFlowSentLpm;

    /// <summary>True when proportional gas was refused or overridden and needs automatic retry upon aeration release (AUD-004).</summary>
    private bool _gasRetryPending;
    private bool _aerationOverridden;

    public PumpControlViewModel(
        IDeviceService device,
        ISettingsService settings,
        IManualDispatcher? dispatcher = null,
        ICommandArbiter? arbiter = null,
        TimeProvider? timeProvider = null,
        ICascadeService? cascade = null)
    {
        _device = device;
        _settings = settings;
        _cascade = cascade;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _arbiter = arbiter ?? (device as ICommandArbiter);
        _dispatcher = dispatcher ?? (device as IManualDispatcher) ?? new ManualDispatcher((_arbiter as IDeviceService) ?? device);
        _committed = settings.Current.PumpControl;
        Status = new ExternalDeviceStatus(DeviceNames.ExternalPump, "da bomba externa", timeProvider) { NodeKind = NodeFirmwareCatalog.Pump };
        Status.PropertyChanged += OnStatusChanged;

        if (_cascade is not null)
        {
            _cascade.ProportionalGasActivePredicate = () => IsGasProportionalActive;
        }

        ModeOptions =
        [
            new(PumpProfileMode.Constant, "Const"),
            new(PumpProfileMode.Linear, "Linear"),
            new(PumpProfileMode.Exponential, "Exp"),
            new(PumpProfileMode.Polynomial, "Poli"),
            new(PumpProfileMode.Piecewise, "Segmentos"),
        ];

        Load(_committed);
        _device.TelemetryReceived += OnTelemetryReceived;
        _device.StateChanged += OnDeviceStateChanged;

        if (_arbiter is not null)
        {
            CurrentOwner = _arbiter.OwnerOf(ActuatorId.ExternalPump);
            _aerationOverridden = _arbiter.OwnerOf(ActuatorId.Aeration) != CommandOwner.Manual;
            if (_aerationOverridden)
            {
                _gasRetryPending = true;
            }

            _arbiter.OwnershipChanged += OnOwnershipChanged;
            _arbiter.OwnershipRevoked += OnOwnershipRevoked;
        }

        _initialised = true;
        ValidateAndRefresh();
        HasPendingChange = false;
    }

    public IReadOnlyList<PumpModeOption> ModeOptions { get; }

    /// <summary>Presence, routing and pending state of the node behind the Hub.</summary>
    public ExternalDeviceStatus Status { get; }

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

    /// <summary>Volume the node's own profile integral expects by now, mL.</summary>
    [ObservableProperty]
    public partial string PumpTargetVolumeText { get; set; } = "—";

    /// <summary>Profile mode the node reports running, compared against what was staged.</summary>
    [ObservableProperty]
    public partial string PumpModeText { get; set; } = "—";

    /// <summary>Dosing / waiting for the window / stopped, from the node's own state machine.</summary>
    [ObservableProperty]
    public partial string PumpRunStateText { get; set; } = "—";

    [ObservableProperty]
    public partial string PumpPwmText { get; set; } = "—";

    [ObservableProperty]
    public partial string PumpSpeedText { get; set; } = "—";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    public bool ShowConstant => SelectedModeOption.Mode == PumpProfileMode.Constant;

    public bool ShowLinear => SelectedModeOption.Mode == PumpProfileMode.Linear;

    public bool ShowExponential => SelectedModeOption.Mode == PumpProfileMode.Exponential;

    public bool ShowPolynomial => SelectedModeOption.Mode == PumpProfileMode.Polynomial;

    public bool ShowPiecewise => SelectedModeOption.Mode == PumpProfileMode.Piecewise;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(IsOwnedByOther))]
    [NotifyPropertyChangedFor(nameof(HasOwnerBadge))]
    [NotifyPropertyChangedFor(nameof(OwnerBadgeText))]
    [NotifyPropertyChangedFor(nameof(OwnerLockReason))]
    public partial CommandOwner CurrentOwner { get; set; } = CommandOwner.Manual;

    public bool IsOwnedByOther => CurrentOwner != CommandOwner.Manual;
    public bool HasOwnerBadge => IsOwnedByOther;
    public string? OwnerBadgeText => OwnershipUi.GetBadgeText(CurrentOwner);
    public string? OwnerLockReason => OwnershipUi.GetLockReason(CurrentOwner);

    public bool IsValid => ValidationError is null;

    public bool CanApply => IsEnabled && IsValid && Status.CanSend && !IsOwnedByOther;

    public bool CanResetVolume => IsEnabled && Status.CanSend && !IsOwnedByOther && !_awaitingResetVolume;

    [ObservableProperty]
    public partial string PidKpText { get; set; } = "1.000";

    [ObservableProperty]
    public partial string PidKiText { get; set; } = "0.000";

    [ObservableProperty]
    public partial string PidKdText { get; set; } = "0.000";

    public bool CanEditPid => false;

    public string PidUnavailableText => "O firmware atual da bomba (3.9) não ecoa ganhos PID. Edição desativada temporariamente.";

    public string StateText => IsEnabled ? "Ativa" : "Desligada";

    /// <summary>True when the pump is active and proportional gas coupling is enabled and driving aeration.</summary>
    public bool IsGasProportionalActive => _initialised && IsEnabled && GasProportionalEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsGasProportionalActive));
        OnPropertyChanged(nameof(CanResetVolume));
        ResetVolumeCommand.NotifyCanExecuteChanged();
        ValidateAndRefresh();

        if (!_initialised || _revertingEnable)
        {
            return;
        }

        if (IsOwnedByOther)
        {
            RevertEnable(value);
            StatusText = OwnerLockReason ?? "Bomba externa sob controle de outro processo.";
            return;
        }

        if (value && GasProportionalEnabled && _cascade is { IsEngaged: true })
        {
            _revertingGasProportional = true;
            GasProportionalEnabled = false;
            _revertingGasProportional = false;
            StatusText = "Gás proporcional desativado: controle de oxigênio (cascata/mapa) em execução.";
        }

        if (value)
        {
            var enable = _dispatcher.Dispatch(CommandBuilders.PumpEnable());
            if (!enable.Accepted)
            {
                RevertEnable(true);
                StatusText = DispatchRefusal.Describe(enable);
                return;
            }

            Status.IsCommRequested = true;
            StatusText = "Bomba externa ativada.";
            return;
        }

        // Stop the profile while the Hub is still routing. Sent the other way round - or
        // merged into one frame, as v.6 did - the Hub discards mode:0 and the node keeps
        // dosing behind a switch that reads "Desligada".
        var stop = _dispatcher.Dispatch(CommandBuilders.PumpStopProfile());
        if (!stop.Accepted)
        {
            RevertEnable(false);
            StatusText = DispatchRefusal.Describe(stop);
            return;
        }

        _lastGasFlowSentLpm = null;
        _gasRetryPending = false;
        _aerationOverridden = false;

        var disable = _dispatcher.DispatchSeparateFrame(CommandBuilders.PumpRoutingDisabled());
        if (!disable.Accepted)
        {
            StatusText = "Perfil interrompido, mas o roteamento do Hub não foi desligado: " +
                         DispatchRefusal.Describe(disable);
            return;
        }

        Status.IsCommRequested = false;
        Status.MarkCommandDispatched();
        StatusText = "Bomba externa desativada (perfil parado e roteamento desligado).";
    }

    /// <summary>Puts the switch back after a refused toggle, without resending anything.</summary>
    private void RevertEnable(bool attempted)
    {
        _revertingEnable = true;
        IsEnabled = !attempted;
        _revertingEnable = false;
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
        if (_initialised && !_revertingGasProportional && value && _cascade is { IsEngaged: true })
        {
            _revertingGasProportional = true;
            GasProportionalEnabled = false;
            _revertingGasProportional = false;
            StatusText = "Gás proporcional indisponível: controle de oxigênio (cascata/mapa) em execução.";
            return;
        }

        OnPropertyChanged(nameof(IsGasProportionalActive));
        RefreshPendingState();
        RefreshGasReadout();
        if (_initialised && !value)
        {
            _lastGasFlowSentLpm = null;
            _gasRetryPending = false;
            _aerationOverridden = false;
            StatusText = "Acoplamento de gás proporcional desativado; a vazão permanece no último valor.";
        }
    }

    partial void OnInitialVolumeTextChanged(string value) { RefreshPendingState(); RefreshGasReadout(); }

    partial void OnVvmTextChanged(string value) { RefreshPendingState(); RefreshGasReadout(); }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void ApplyProfile()
    {
        if (IsOwnedByOther)
        {
            StatusText = OwnerLockReason ?? "Operação bloqueada pelo controlador atual.";
            return;
        }

        if (!TryBuildSpec(out var spec, out var error))
        {
            StatusText = error ?? "Revise os parâmetros do perfil da bomba.";
            return;
        }

        var command = PumpProfileMath.BuildCommand(spec);
        var result = _dispatcher.Dispatch(command);
        if (!result.Accepted)
        {
            // The staged profile stays staged and unversioned: bumping the persisted version
            // for a frame that never left would make a later comparison against the node's
            // reported mode meaningless.
            StatusText = DispatchRefusal.Describe(result);
            return;
        }

        if (TryGetStagedSettings(out var staged))
        {
            _committed = staged with { Version = _committed.Version + 1 };
            _settings.Update(settings => settings with { PumpControl = _committed });
        }

        HasPendingChange = false;
        Status.MarkCommandDispatched();
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

    [RelayCommand(CanExecute = nameof(CanResetVolume))]
    private void ResetVolume()
    {
        if (IsOwnedByOther)
        {
            StatusText = OwnerLockReason ?? "Operação bloqueada pelo controlador atual.";
            return;
        }

        var command = CommandBuilders.PumpResetVolume();
        var result = _dispatcher.Dispatch(command);
        if (!result.Accepted)
        {
            StatusText = DispatchRefusal.Describe(result);
            return;
        }

        _resetVolumeRequestedAt = _timeProvider.GetUtcNow().UtcDateTime;
        _awaitingResetVolume = true;
        OnPropertyChanged(nameof(CanResetVolume));
        ResetVolumeCommand.NotifyCanExecuteChanged();
        Status.MarkCommandDispatched();
        StatusText = "Comando de zerar volume enviado. Aguardando confirmação do nó...";
    }

    /// <summary>
    /// The pump's contribution to the merged operator safe-stop frame.
    /// </summary>
    /// <remarks>
    /// Only the profile stop belongs in the merged frame. Adding <c>pumpComm:0</c> to it
    /// would make the Hub drop the <c>mode:0</c> travelling beside it, so routing is cleared
    /// by <see cref="BuildRoutingDisable"/> on the frame after.
    /// </remarks>
    public OpenTECCommand BuildSafeStop() => CommandBuilders.PumpStopProfile();

    /// <summary>The follow-up frame that clears the Hub's pump routing after a safe stop.</summary>
    public OpenTECCommand BuildRoutingDisable() => CommandBuilders.PumpRoutingDisabled();

    /// <summary>Marks the pump stopped after a bulk safe-stop already sent its disable frame.</summary>
    public void MarkStopped()
    {
        _initialised = false; // suppress the immediate re-send from the setter
        IsEnabled = false;
        _initialised = true;
        _lastGasFlowSentLpm = null;
        _gasRetryPending = false;
        _aerationOverridden = false;
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
            CalibrationSlope = _committed.CalibrationSlope,
            CalibrationIntercept = _committed.CalibrationIntercept,
            PidKp = DosingInput.TryParseDouble(PidKpText, out var kp) ? kp : _committed.PidKp,
            PidKi = DosingInput.TryParseDouble(PidKiText, out var ki) ? ki : _committed.PidKi,
            PidKd = DosingInput.TryParseDouble(PidKdText, out var kd) ? kd : _committed.PidKd,
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
        PidKpText = DosingInput.Format(s.PidKp, 3);
        PidKiText = DosingInput.Format(s.PidKi, 3);
        PidKdText = DosingInput.Format(s.PidKd, 3);
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
        Status.Update(
            snapshot.HasPumpTelemetry,
            snapshot.PumpOnline,
            snapshot.PumpCommandPending,
            snapshot.PumpCommEnabled,
            snapshot.PumpNode);

        UpdateNodeStateReadouts(snapshot);

        _lastPumpVolumeMl = snapshot.PumpVolume > SensorReadings.NotReceived ? snapshot.PumpVolume : 0.0;

        PumpFlowText = snapshot.PumpFlow > SensorReadings.NotReceived
            ? snapshot.PumpFlow.ToString("F3", CultureInfo.CurrentCulture)
            : "—";
        PumpVolumeText = snapshot.PumpVolume > SensorReadings.NotReceived
            ? snapshot.PumpVolume.ToString("F3", CultureInfo.CurrentCulture)
            : "—";

        RefreshGasReadout();
        MaybeSendProportionalGas();

        if (_awaitingResetVolume)
        {
            if (snapshot.PumpVolume is >= 0 and < 0.05)
            {
                _awaitingResetVolume = false;
                _resetVolumeRequestedAt = null;
                OnPropertyChanged(nameof(CanResetVolume));
                ResetVolumeCommand.NotifyCanExecuteChanged();
                StatusText = "Volume acumulado da bomba zerado com sucesso.";
            }
            else if (_resetVolumeRequestedAt.HasValue &&
                     (_timeProvider.GetUtcNow().UtcDateTime - _resetVolumeRequestedAt.Value).TotalSeconds > 5.0)
            {
                _awaitingResetVolume = false;
                _resetVolumeRequestedAt = null;
                OnPropertyChanged(nameof(CanResetVolume));
                ResetVolumeCommand.NotifyCanExecuteChanged();
                StatusText = "Aviso: nó da bomba não confirmou zeramento do volume em 5 s.";
            }
        }
    }

    /// <summary>
    /// While the coupling is active, drive the air flow from the pump volume, sending only when
    /// the target moves materially. Refused by the arbiter if the cascade owns aeration.
    /// When <paramref name="force"/> is true, bypasses the resend threshold check to re-establish
    /// coupling immediately upon ownership restoration (AUD-004).
    /// </summary>
    private void MaybeSendProportionalGas(bool force = false)
    {
        // A dead node's last volume is not a measurement. The parser invalidates PumpVolume
        // when the pump goes absent, and recomputing Q_g from the zero that leaves behind
        // would silently drop aeration to its base rate; hold the last gas setpoint instead.
        if (Status.IsOffline || (_cascade is { IsEngaged: true }))
        {
            return;
        }

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

        if (!force && _lastGasFlowSentLpm is { } last && Math.Abs(qg - last) < GasFlowResendThresholdLpm)
        {
            return;
        }

        var result = _dispatcher.Dispatch(
            CommandBuilders.FlowSetpoint(qg, maxFlow, valve1: false, valve2: false));

        if (!result.Accepted)
        {
            // Aeration is owned by the cascade or a recipe. Remembering qg as sent would
            // suppress every retry until the calculated flow moved by the resend threshold,
            // so the coupling would stay dead long after ownership came back (AUD-004).
            _lastGasFlowSentLpm = null;
            _gasRetryPending = true;
            _aerationOverridden = true;
            StatusText = DispatchRefusal.Describe(result);
            return;
        }

        var wasOverriddenOrPending = _gasRetryPending || _aerationOverridden;
        _lastGasFlowSentLpm = qg;
        _gasRetryPending = false;
        _aerationOverridden = false;

        if (wasOverriddenOrPending)
        {
            StatusText = $"Vazão proporcional restabelecida ({qg.ToString("F2", CultureInfo.CurrentCulture)} L/min).";
        }
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

    /// <summary>
    /// The node's own view of what it is doing: mode, profile state and target volume.
    /// </summary>
    /// <remarks>
    /// All of this was already on the wire and thrown away. The target volume in particular
    /// is the node's own profile integral, which is the only way to see the pump falling
    /// behind its profile without recomputing it here from a different clock.
    /// </remarks>
    private void UpdateNodeStateReadouts(SensorSnapshot snapshot)
    {
        PumpTargetVolumeText = snapshot.PumpTargetVolume > SensorReadings.NotReceived
            ? snapshot.PumpTargetVolume.ToString("F3", CultureInfo.CurrentCulture)
            : "—";

        PumpPwmText = snapshot.PumpPwm > SensorReadings.NotReceived
            ? snapshot.PumpPwm.ToString("F0", CultureInfo.CurrentCulture)
            : "—";

        PumpSpeedText = snapshot.PumpSpeed > SensorReadings.NotReceived
            ? snapshot.PumpSpeed.ToString("F1", CultureInfo.CurrentCulture)
            : "—";

        PumpModeText = snapshot.PumpMode switch
        {
            < 0 => "—",
            0 => "Nenhum",
            var mode when mode <= ModeOptions.Count => ModeOptions[mode - 1].Label,
            var mode => mode.ToString(CultureInfo.CurrentCulture),
        };

        PumpRunStateText = !Status.IsOnline
            ? "—"
            : snapshot.PumpActive
                ? "Dosando"
                : snapshot.PumpWaiting
                    ? "Aguardando janela"
                    : "Parada";
    }

    private void OnStatusChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ExternalDeviceStatus.CanSend) or null))
        {
            return;
        }

        OnPropertyChanged(nameof(CanApply));
        ApplyProfileCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanResetVolume));
        ResetVolumeCommand.NotifyCanExecuteChanged();
    }

    private void OnDeviceStateChanged(ConnectionStateChange change)
    {
        if (change.State != ConnectionState.Connected)
        {
            Status.MarkHubUnavailable();
            _awaitingResetVolume = false;
            _resetVolumeRequestedAt = null;
            OnPropertyChanged(nameof(CanResetVolume));
            ResetVolumeCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnOwnershipChanged(OwnershipTransfer transfer)
        => RunOnUi(() => HandleOwnershipTransfer(transfer));

    private void OnOwnershipRevoked(OwnershipTransfer transfer)
        => RunOnUi(() => HandleOwnershipTransfer(transfer));

    private void HandleOwnershipTransfer(OwnershipTransfer transfer)
    {
        if (transfer.Actuators.Contains(ActuatorId.ExternalPump))
        {
            CurrentOwner = transfer.To;
        }

        if (transfer.Actuators.Contains(ActuatorId.Aeration))
        {
            if (transfer.To == CommandOwner.Manual)
            {
                if (_gasRetryPending || _aerationOverridden || (GasProportionalEnabled && IsEnabled))
                {
                    _aerationOverridden = false;
                    MaybeSendProportionalGas(force: true);
                }
            }
            else
            {
                _aerationOverridden = true;
                _lastGasFlowSentLpm = null;
                _gasRetryPending = true;
            }
        }
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(action);
        }
        else
        {
            action();
        }
    }

    public void Dispose()
    {
        _device.TelemetryReceived -= OnTelemetryReceived;
        _device.StateChanged -= OnDeviceStateChanged;
        Status.PropertyChanged -= OnStatusChanged;
        if (_arbiter is not null)
        {
            _arbiter.OwnershipChanged -= OnOwnershipChanged;
            _arbiter.OwnershipRevoked -= OnOwnershipRevoked;
        }

        if (_cascade is not null && ReferenceEquals(_cascade.ProportionalGasActivePredicate?.Target, this))
        {
            _cascade.ProportionalGasActivePredicate = null;
        }
    }
}
