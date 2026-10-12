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

    // ---- Pump 3.13 integration (drive, refusal, reboots) -------------------------
    private long? _lastRejCmdId;
    private string? _lastRejErr;
    private long? _lastBootCount;

    /// <summary>
    /// Session volume before an unexplained drop (a reboot of an older pump, or one that lost
    /// its NVS). While the reported volume stays below it, proportional gas holds its last
    /// setpoint instead of falling back towards V₀·vvm.
    /// </summary>
    private double? _gasHoldVolumeMl;
    private DateTime? _liquidBlockedSince;

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

        Dosing = new PumpDosingViewModel(settings, _dispatcher, Status, _timeProvider,
            () => IsOwnedByOther, () => OwnerLockReason);
        Dosing.PropertyChanged += OnDosingChanged;

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
    public partial string PiecewiseTimesText { get; set; } = "0; 60";

    [ObservableProperty]
    public partial string PiecewiseFlowsText { get; set; } = "1; 1";

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

    public bool CanApply => IsEnabled && IsValid && Status.CanSend && !IsOwnedByOther && !NoCurveOnNode;

    // ---- Card layout (plan 2026-10-11, Phase 5) -------------------------------------

    /// <summary>Continuous profiles (needs the polynomial curve on the node). The default.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPulseMode))]
    public partial bool IsContinuousMode { get; set; } = true;

    /// <summary>Pulse dosing (needs a dose point). Its panel arrives with Phase 6.</summary>
    public bool IsPulseMode
    {
        get => !IsContinuousMode;
        set
        {
            if (value)
            {
                IsContinuousMode = false;
            }
        }
    }

    /// <summary>The pulse panel (Phase 6): target, granularity slider, start/stop with echo.</summary>
    public PumpDosingViewModel Dosing { get; }

    /// <summary>Pulses need pump 3.13; an online node without its fields disables the segment.</summary>
    public bool CanUsePulseMode => !Dosing.NodeLacksDosing;

    public string PulseModeHint => Dosing.NodeLacksDosing
        ? "Dosagem por pulsos requer a bomba 3.13."
        : IsPulseMode
            ? "Pulsos em velocidade fixa (ponto de dosagem); a curva polinomial não é usada."
            : "Perfis Const, Linear, Exp, Poli e Segmentos sobre a curva polinomial do nó.";

    partial void OnIsContinuousModeChanged(bool value)
    {
        OnPropertyChanged(nameof(PulseModeHint));
        OnPropertyChanged(nameof(ShowNoCurveWarning));
    }

    /// <summary>The no-curve warning matters only for continuous profiles; pulses do not use the curve.</summary>
    public bool ShowNoCurveWarning => NoCurveOnNode && IsContinuousMode;

    private void OnDosingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PumpDosingViewModel.NodeLacksDosing))
        {
            OnPropertyChanged(nameof(CanUsePulseMode));
            OnPropertyChanged(nameof(PulseModeHint));
            if (Dosing.NodeLacksDosing && IsPulseMode)
            {
                IsContinuousMode = true;
            }
        }
        else if (e.PropertyName == nameof(PumpDosingViewModel.IsDosing) && Dosing.IsDosing && IsContinuousMode)
        {
            // A dosing run found on the node (or started elsewhere) opens the pulse panel.
            IsContinuousMode = false;
        }
    }

    /// <summary>
    /// The node echoes its calibration (3.12+) but reports no curve: a continuous profile would
    /// not turn the motor (flow → speed gives 0 without <c>pump_poly_cal</c>).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    public partial bool NoCurveOnNode { get; set; }

    [ObservableProperty] public partial string TargetFlowNowText { get; set; } = "—";
    [ObservableProperty] public partial string SummaryText { get; set; } = "—";
    [ObservableProperty] public partial string CycleVolumeText { get; set; } = "—";
    [ObservableProperty] public partial string DeviationText { get; set; } = "";
    [ObservableProperty] public partial string CurveStatusText { get; set; } = "Curva: —";
    [ObservableProperty] public partial string DosePointStatusText { get; set; } = "Ponto: —";
    [ObservableProperty] public partial string LiquidSensorText { get; set; } = "";
    [ObservableProperty] public partial string LastRebootText { get; set; } = "";
    [ObservableProperty] public partial string GasSummaryText { get; set; } = "";

    // ---- Live overlay and deviation (Phase 7) ----------------------------------------

    private readonly PumpLiveSeries _profileLive = new();
    private readonly PumpDeviationTracker _profileDeviation = new() { HoldMinutes = 2.0 };

    /// <summary>Measured flow and cycle volume on the node's profile clock (<c>PumpProfileMin</c>, pump 3.13).</summary>
    [ObservableProperty] public partial IReadOnlyList<double>? LiveMinutes { get; set; }
    [ObservableProperty] public partial IReadOnlyList<double>? LiveFlow { get; set; }
    [ObservableProperty] public partial IReadOnlyList<double>? LiveVolume { get; set; }

    /// <summary>Cycle volume off the node's target by more than 5 % for over 2 min.</summary>
    [ObservableProperty] public partial bool IsDeviationAlert { get; set; }

    /// <summary>Why the measured series is not drawn over the plan, if it is not.</summary>
    [ObservableProperty] public partial string LiveNote { get; set; } = "";

    /// <summary>Last snapshot, for readouts that also change with the staged profile.</summary>
    private SensorSnapshot? _lastSnapshot;

    public bool CanResetVolume => IsEnabled && Status.CanSend && !IsOwnedByOther && !_awaitingResetVolume;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendPid))]
    public partial string PidKpText { get; set; } = "0.500";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendPid))]
    public partial string PidKiText { get; set; } = "0.050";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendPid))]
    public partial string PidKdText { get; set; } = "0.001";

    // ---- PID of the node (pump 3.10 echoes kp/ki/kd; 3.9 does not) ----------------
    // The gains are sent on their own frame and taken as applied only when the node echoes
    // the same three numbers; the persisted copy follows the echo, never the click.

    /// <summary>Gains as the node last reported them; null until a 3.10 pump echoes them.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPidEcho))]
    [NotifyPropertyChangedFor(nameof(CanEditPid))]
    [NotifyPropertyChangedFor(nameof(CanSendPid))]
    [NotifyPropertyChangedFor(nameof(PidUnavailableText))]
    [NotifyPropertyChangedFor(nameof(AppliedPidText))]
    public partial (double Kp, double Ki, double Kd)? AppliedPid { get; set; }

    public bool HasPidEcho => AppliedPid.HasValue;

    public bool CanEditPid => HasPidEcho && !_awaitingPidEcho;

    public bool CanSendPid => CanEditPid && IsEnabled && Status.CanSend && !IsOwnedByOther && TryParsePid(out _, out _, out _);

    public string? PidUnavailableText => HasPidEcho
        ? null
        : "A bomba não ecoou os ganhos PID (firmware 3.9 ou nó ausente). Edição liberada quando o eco chegar (3.10+).";

    public string AppliedPidText => AppliedPid is { } p
        ? $"Nó: Kp {p.Kp.ToString("0.###", CultureInfo.CurrentCulture)} · Ki {p.Ki.ToString("0.###", CultureInfo.CurrentCulture)} · Kd {p.Kd.ToString("0.###", CultureInfo.CurrentCulture)}"
        : "Nó: —";

    private bool _awaitingPidEcho;
    private (double Kp, double Ki, double Kd)? _requestedPid;
    private DateTime? _pidRequestedAt;

    private bool TryParsePid(out double kp, out double ki, out double kd)
    {
        kp = ki = kd = 0.0;
        return DosingInput.TryParseDouble(PidKpText, out kp) && kp >= 0.0 &&
               DosingInput.TryParseDouble(PidKiText, out ki) && ki >= 0.0 &&
               DosingInput.TryParseDouble(PidKdText, out kd) && kd >= 0.0;
    }

    [RelayCommand(CanExecute = nameof(CanSendPid))]
    private void SendPid()
    {
        if (!TryParsePid(out var kp, out var ki, out var kd))
        {
            StatusText = "Ganhos PID: números ≥ 0.";
            return;
        }

        var result = _dispatcher.Dispatch(CommandBuilders.PumpPid(kp, ki, kd));
        if (!result.Accepted)
        {
            StatusText = DispatchRefusal.Describe(result);
            return;
        }

        _requestedPid = (kp, ki, kd);
        _pidRequestedAt = _timeProvider.GetUtcNow().UtcDateTime;
        _awaitingPidEcho = true;
        Status.MarkCommandDispatched();
        NotifyPidAvailability();
        StatusText = "Ganhos PID enviados ao nó; aguardando eco.";
    }

    private void TrackPidEcho(SensorSnapshot snapshot)
    {
        if (snapshot.PumpPidKp is { } kp && snapshot.PumpPidKi is { } ki && snapshot.PumpPidKd is { } kd)
        {
            var echoed = (kp, ki, kd);
            if (AppliedPid != echoed)
            {
                AppliedPid = echoed;
            }

            if (_requestedPid is { } requested &&
                Math.Abs(kp - requested.Kp) <= 0.0005 && Math.Abs(ki - requested.Ki) <= 0.0005 && Math.Abs(kd - requested.Kd) <= 0.0005)
            {
                _requestedPid = null;
                _pidRequestedAt = null;
                _awaitingPidEcho = false;
                _committed = _committed with { PidKp = kp, PidKi = ki, PidKd = kd };
                PersistCommitted();
                NotifyPidAvailability();
                StatusText = "Ganhos PID confirmados pelo nó.";
            }
        }
        else if (AppliedPid is not null && !snapshot.PumpOnline)
        {
            AppliedPid = null;
        }

        if (_awaitingPidEcho && _pidRequestedAt.HasValue &&
            (_timeProvider.GetUtcNow().UtcDateTime - _pidRequestedAt.Value).TotalSeconds > 15.0)
        {
            _requestedPid = null;
            _pidRequestedAt = null;
            _awaitingPidEcho = false;
            NotifyPidAvailability();
            StatusText = "Aviso: o nó não ecoou os ganhos PID pedidos em 15 s.";
        }
    }

    private void NotifyPidAvailability()
    {
        OnPropertyChanged(nameof(CanEditPid));
        OnPropertyChanged(nameof(CanSendPid));
        SendPidCommand.NotifyCanExecuteChanged();
    }

    // ---- Bench potentiometers (pump 3.10) ------------------------------------------
    // A manual-speed run (calibration) locks the knobs out on the node until "pot":1. The
    // echo says which side owns the motor; the toggle hands it back or takes it away.

    /// <summary>True when the node reports the potentiometers in command; null on a 3.9 node.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PotentiometersText))]
    [NotifyPropertyChangedFor(nameof(CanTogglePotentiometers))]
    public partial bool? PotentiometersEnabled { get; set; }

    public string PotentiometersText => PotentiometersEnabled switch
    {
        true => "Potenciômetros de bancada no comando (modo ocioso).",
        false => "Potenciômetros travados: velocidade manual do app ou trava explícita.",
        null => "Estado dos potenciômetros desconhecido (firmware 3.9 ou nó ausente).",
    };

    public bool CanTogglePotentiometers => PotentiometersEnabled.HasValue && IsEnabled && Status.CanSend && !IsOwnedByOther;

    [RelayCommand(CanExecute = nameof(CanTogglePotentiometers))]
    private void TogglePotentiometers()
    {
        var enable = PotentiometersEnabled != true;
        var result = _dispatcher.Dispatch(CommandBuilders.PumpPotentiometers(enable));
        if (!result.Accepted)
        {
            StatusText = DispatchRefusal.Describe(result);
            return;
        }

        Status.MarkCommandDispatched();
        StatusText = enable
            ? "Devolvendo o motor aos potenciômetros de bancada; a velocidade manual é esquecida."
            : "Travando os potenciômetros de bancada.";
    }

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
            StatusText = "Perfil interrompido, mas a bomba continua habilitada no Hub: " +
                         DispatchRefusal.Describe(disable);
            return;
        }

        Status.IsCommRequested = false;
        Status.MarkCommandDispatched();
        StatusText = "Bomba externa desativada (perfil parado e desabilitada no Hub).";
    }

    /// <summary>Puts the switch back after a refused toggle, without resending anything.</summary>
    private void RevertEnable(bool attempted)
    {
        _revertingEnable = true;
        IsEnabled = !attempted;
        _revertingEnable = false;
    }

    partial void OnNoCurveOnNodeChanged(bool value)
    {
        ApplyProfileCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ShowNoCurveWarning));
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

    partial void OnInitialVolumeTextChanged(string value) { RefreshPendingState(); RefreshGasReadout(); RefreshCardReadouts(); }

    partial void OnVvmTextChanged(string value) { RefreshPendingState(); RefreshGasReadout(); RefreshCardReadouts(); }

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
            PersistCommitted();
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

    /// <summary>
    /// Saves the card's own fields. The curve, the hose library selection and the dosing
    /// settings belong to Calibrações and to the pulse panel: they are taken from the current
    /// settings, so a profile send never rolls back a calibration or the dose point saved since
    /// this card loaded.
    /// </summary>
    private void PersistCommitted()
        => _settings.Update(s => s with
        {
            PumpControl = _committed with
            {
                CalibrationSlope = s.PumpControl.CalibrationSlope,
                CalibrationIntercept = s.PumpControl.CalibrationIntercept,
                CalibrationQt = s.PumpControl.CalibrationQt,
                CalibrationSt = s.PumpControl.CalibrationSt,
                CalibrationMLow = s.PumpControl.CalibrationMLow,
                CalibrationMHigh = s.PumpControl.CalibrationMHigh,
                CalibrationPoints = s.PumpControl.CalibrationPoints,
                SelectedProfileName = s.PumpControl.SelectedProfileName,
                Dose = s.PumpControl.Dose,
            },
        });

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
        RefreshCardReadouts();
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
            // Where aeration will be when the staged plan has delivered its volume on top of today's.
            var planMl = Preview?.TotalVolumeMl ?? 0.0;
            var qgEnd = Math.Clamp((v0 + ((_lastPumpVolumeMl + planMl) / 1000.0)) * vvm, 0.0, maxFlow);
            GasSummaryText = (GasProportionalEnabled ? "ligado" : "desligado") +
                             $" · Q_g agora {qg.ToString("F2", CultureInfo.CurrentCulture)} · no fim do plano {qgEnd.ToString("F2", CultureInfo.CurrentCulture)} L/min";
        }
        else
        {
            GasFlowText = "—";
            GasSummaryText = GasProportionalEnabled ? "ligado · parâmetros inválidos" : "desligado";
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
        TrackPidEcho(snapshot);
        PotentiometersEnabled = snapshot.PumpOnline ? snapshot.PumpPotEnabled : null;
        OnPropertyChanged(nameof(CanTogglePotentiometers));
        TogglePotentiometersCommand.NotifyCanExecuteChanged();
        NotifyPidAvailability();

        TrackRefusal(snapshot);
        Dosing.OnSnapshot(snapshot);
        TrackReboot(snapshot);
        TrackLiquidSensor(snapshot);

        _lastPumpVolumeMl = snapshot.PumpVolume > SensorReadings.NotReceived ? snapshot.PumpVolume : 0.0;

        PumpFlowText = snapshot.PumpFlow > SensorReadings.NotReceived
            ? snapshot.PumpFlow.ToString("F3", CultureInfo.CurrentCulture)
            : "—";
        PumpVolumeText = snapshot.PumpVolume > SensorReadings.NotReceived
            ? snapshot.PumpVolume.ToString("F3", CultureInfo.CurrentCulture)
            : "—";

        _lastSnapshot = snapshot;
        RefreshGasReadout();
        RefreshCardReadouts();
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

        // After an unexplained volume drop, hold the last gas setpoint until the pump's volume
        // is back where it was; recomputing from the lower figure would cut aeration.
        if (_gasHoldVolumeMl is { } hold)
        {
            if (_lastPumpVolumeMl < hold)
            {
                return;
            }

            _gasHoldVolumeMl = null;
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

        // Proportional gas is air to the reactor (A); a zero coupling closes everything.
        var result = _dispatcher.Dispatch(CommandBuilders.FlowRoute(
            qg, maxFlow, qg > 0.0 ? GasRoute.Reactor : GasRoute.Closed, _settings.Current.GasRig.ToConfiguration()));

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

    /// <summary>
    /// Parses a list of numbers. With a comma decimal separator a plain comma cannot also
    /// separate items ("0,000, 60,000"), so items are split on ';' when present, else on a comma
    /// followed by a space or on spaces; a lone number such as "1,5" is one item.
    /// </summary>
    internal static bool TryParseList(string? text, out IReadOnlyList<double> values)
    {
        var raw = (text ?? "").Trim();
        string[] tokens;
        if (raw.Contains(';'))
        {
            tokens = raw.Split(';');
        }
        else if (System.Text.RegularExpressions.Regex.IsMatch(raw, @",\s|\s"))
        {
            tokens = System.Text.RegularExpressions.Regex.Split(raw, @",\s+|\s+");
        }
        else if (DosingInput.TryParseDouble(raw, out _))
        {
            tokens = [raw];
        }
        else
        {
            tokens = raw.Split(',');
        }

        var list = new List<double>();
        foreach (var token in tokens.Select(t => t.Trim()).Where(t => t.Length > 0))
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

    /// <summary>
    /// Items separated by "; " so a comma decimal separator stays unambiguous, with up to six
    /// significant digits: three fixed decimals turned a 0,0002 coefficient into 0,000.
    /// </summary>
    internal static string FormatList(IReadOnlyList<double> values)
        => string.Join("; ", values.Select(v => v.ToString("G6", System.Globalization.CultureInfo.CurrentCulture)));

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

        // Pump 3.13 says who drives the motor; older pumps only expose the profile state,
        // which reads "Parada" during pulse dosing.
        PumpRunStateText = !Status.IsOnline
            ? "—"
            : snapshot.PumpDrive is { } drive
                ? PumpNodeText.Drive(drive)
                : snapshot.PumpActive
                    ? "Dosando"
                    : snapshot.PumpWaiting
                        ? "Aguardando janela"
                        : "Parada";
    }

    /// <summary>
    /// Row and status-strip readouts: target flow now, live summary, cycle volume and deviation,
    /// calibration state, liquid sensor, last reboot and the proportional-gas summary.
    /// </summary>
    private void RefreshCardReadouts()
    {
        if (!_initialised)
        {
            return;
        }

        var s = _lastSnapshot;
        var online = s is not null && Status.IsOnline;
        var c = CultureInfo.CurrentCulture;

        // Calibration: the curve on the node (CRC echo) and the dose point chosen in Calibrações.
        NoCurveOnNode = online && s!.PumpTransitionSpeed is not null && s.PumpCalCrc is null or 0;
        CurveStatusText = !online || s!.PumpTransitionSpeed is null
            ? "Curva no nó: —"
            : NoCurveOnNode ? "Curva no nó ✗ (modo contínuo indisponível)" : $"Curva no nó ✓ (CRC {s.PumpCalCrc:X8})";
        var point = _settings.Current.PumpControl.Dose.Point;
        DosePointStatusText = point is null
            ? "Ponto de dosagem ✗"
            : $"Ponto ✓ S {point.SpeedUnits.ToString("F0", c)} → {point.RateMlMin.ToString("F2", c)} mL/min" +
              (DateTimeOffset.TryParse(point.CapturedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? $" ({at.LocalDateTime:dd/MM})" : "");

        if (!online)
        {
            TargetFlowNowText = "—";
            SummaryText = Status.IsOffline ? "Sem comunicação" : "—";
            CycleVolumeText = "—";
            DeviationText = "";
            LiquidSensorText = "";
            LastRebootText = "";
            return;
        }

        var drive = s!.PumpDrive;
        var running = drive == (int)PumpDriveState.ProfileRunning || (drive is null && s.PumpActive);
        var waiting = drive == (int)PumpDriveState.ProfileWaiting || (drive is null && s.PumpWaiting);
        var dosing = s.PumpDoseActive == true;
        var blocked = s.PumpLiquidOk == false && drive is >= (int)PumpDriveState.ManualSpeed and <= (int)PumpDriveState.DosePulse;

        // Target flow now: the committed profile at the node's own clock (3.13), else the mean
        // flow of the dosing run.
        var committedSpec = SpecFrom(_committed);
        if (dosing && s.PumpDoseMl is { } ml && s.PumpDosePeriodS is { } per && per > 0)
        {
            TargetFlowNowText = (ml / per * 60.0).ToString("F3", c);
        }
        else if (running && s.PumpProfileMin is { } tMin && committedSpec is not null)
        {
            TargetFlowNowText = PumpProfileMath.FlowAt(committedSpec, tMin).ToString("F3", c);
        }
        else if (running && committedSpec is { Mode: PumpProfileMode.Constant })
        {
            TargetFlowNowText = committedSpec.Lambda.ToString("F3", c);
        }
        else
        {
            TargetFlowNowText = running || waiting ? "—" : "0";
        }

        var volume = s.PumpVolume > SensorReadings.NotReceived ? s.PumpVolume : double.NaN;
        CycleVolumeText = s.PumpCycleVolume is { } cyc ? cyc.ToString("F2", c) + " mL" : "—";
        UpdateProfileLive(s, running);
        if (running && s.PumpCycleVolume is { } cycle && s.PumpTargetVolume > SensorReadings.NotReceived)
        {
            var dev = cycle - s.PumpTargetVolume;
            DeviationText = $"Desvio {(dev >= 0 ? "+" : "−")}{Math.Abs(dev).ToString("F2", c)} mL" +
                            (_profileDeviation.Relative is { } r ? $" ({(r * 100).ToString("+0.#;−0.#;0", c)} %)" : "") +
                            (IsDeviationAlert ? " — acima de 5 % há mais de 2 min" : "");
        }
        else if (dosing && s.PumpDoseDelivered is { } done && s.PumpDoseTotal is { } tot && tot > 0)
        {
            DeviationText = $"Dosagem {done.ToString("F2", c)} / {tot.ToString("F1", c)} mL ({done / tot * 100.0:F0} %)";
        }
        else
        {
            DeviationText = "";
        }

        SummaryText = blocked
            ? "Bloqueada: sensor de líquido seco"
            : dosing
                ? $"{(s.PumpDoseDelivered ?? 0).ToString("F2", c)}" + (s.PumpDoseTotal is { } t2 && t2 > 0 ? $" / {t2.ToString("F1", c)}" : "") + " mL" +
                  (s.PumpDosePulse == true ? " · pulso" : s.PumpDoseNextS is { } next ? $" · próx. {next / 60}:{next % 60:00}" : "")
                : running
                    ? (s.PumpProfileMin is { } tm ? $"{tm.ToString("F0", c)} / {_committed.FinalMinutes.ToString("F0", c)} min · " : "") +
                      (s.PumpCycleVolume is { } cv ? cv.ToString("F1", c) + " mL" : "")
                    : waiting
                        ? "Aguardando janela"
                        : $"Parada · {(double.IsNaN(volume) ? "—" : volume.ToString("F2", c))} mL";

        LiquidSensorText = s.PumpLiquidSensor switch
        {
            true when s.PumpLiquidOk == false => "Sensor de líquido: seco (motor bloqueado)",
            true => "Sensor de líquido: com líquido",
            false => "Sensor de líquido: desligado",
            null => "",
        };
        LastRebootText = s.PumpUptimeS is { } up
            ? $"Ligada há {FormatUptime(up)} ({PumpNodeText.ResetReason(s.PumpResetReason)})"
            : "";
    }

    /// <summary>
    /// Appends the running profile's measured flow and cycle volume on the node's own clock
    /// (<c>t_min</c>), so the overlay stays aligned after the app or the node restarts (§5.4).
    /// The deviation compares the cycle volume with the node's own target integral.
    /// </summary>
    private void UpdateProfileLive(SensorSnapshot s, bool running)
    {
        if (!running)
        {
            if (_profileLive.Count > 0 && s.PumpDrive == (int)PumpDriveState.Stopped)
            {
                // Keep the last run on screen until a new one starts; only the alert clears.
                _profileDeviation.Reset();
                IsDeviationAlert = false;
            }

            return;
        }

        if (s.PumpProfileMin is not { } tMin)
        {
            LiveNote = "Sem o relógio do perfil (bomba anterior à 3.13): a medida não é sobreposta ao plano.";
            return;
        }

        LiveNote = "";
        var flow = s.PumpFlow > SensorReadings.NotReceived ? s.PumpFlow : 0.0;
        var cycle = s.PumpCycleVolume ?? 0.0;
        _profileLive.Add(tMin, flow, cycle);
        (LiveMinutes, LiveFlow, LiveVolume) = _profileLive.Snapshot();
        if (s.PumpTargetVolume > SensorReadings.NotReceived)
        {
            _profileDeviation.Update(tMin, s.PumpTargetVolume, cycle);
            IsDeviationAlert = _profileDeviation.IsAlert;
        }
    }

    private static string FormatUptime(long seconds)
        => seconds < 3600 ? $"{seconds / 60} min" : seconds < 86400 ? $"{seconds / 3600} h {seconds % 3600 / 60:00} min" : $"{seconds / 86400} d {seconds % 86400 / 3600} h";

    /// <summary>The profile last sent, as a spec (for the target flow at the node's clock).</summary>
    private static PumpProfileSpec? SpecFrom(PumpControlSettings s)
    {
        try
        {
            return new PumpProfileSpec(s.Mode, s.InitMinutes, s.FinalMinutes,
                s.Mode switch
                {
                    PumpProfileMode.Constant => s.LambdaConst,
                    PumpProfileMode.Linear => s.LambdaLinear,
                    PumpProfileMode.Exponential => s.LambdaExp,
                    _ => 0.0,
                },
                s.Mode switch
                {
                    PumpProfileMode.Linear => s.PhiLinear,
                    PumpProfileMode.Exponential => s.PhiExp,
                    _ => 0.0,
                },
                s.PolynomialCoefficients, s.PiecewiseTimes, s.PiecewiseFlows);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A change of <c>PumpRejCmdId</c>/<c>PumpRejErr</c> means the node refused the last frame.</summary>
    private void TrackRefusal(SensorSnapshot snapshot)
    {
        var id = snapshot.PumpRejCmdId;
        var err = snapshot.PumpRejErr;
        var changed = (id, err) != (_lastRejCmdId, _lastRejErr);
        var first = _lastRejCmdId is null && _lastRejErr is null;
        _lastRejCmdId = id;
        _lastRejErr = err;
        if (!changed || first || string.IsNullOrEmpty(err))
        {
            return;
        }

        // Whatever echo was awaited will not come.
        if (_awaitingPidEcho)
        {
            _requestedPid = null;
            _pidRequestedAt = null;
            _awaitingPidEcho = false;
            NotifyPidAvailability();
        }

        StatusText = $"A bomba recusou o último comando: {PumpDoseRules.DescribeRefusal(err)}.";
        Dosing.OnRefused(err);
    }

    /// <summary>
    /// A changed boot counter (pump 3.13), or a volume that fell without a reset being asked
    /// for, means the node restarted. Proportional gas holds until the volume recovers.
    /// </summary>
    private void TrackReboot(SensorSnapshot snapshot)
    {
        var rebooted = false;
        if (snapshot.PumpBootCount is { } boot)
        {
            rebooted = _lastBootCount is { } last && boot != last;
            _lastBootCount = boot;
        }

        var volume = snapshot.PumpVolume;
        var dropped = volume > SensorReadings.NotReceived && !_awaitingResetVolume &&
                      _lastPumpVolumeMl > 0.0 && volume < _lastPumpVolumeMl - 0.05;
        if (dropped)
        {
            _gasHoldVolumeMl = _lastPumpVolumeMl;
        }

        if (rebooted)
        {
            StatusText = $"A bomba externa reiniciou ({PumpNodeText.ResetReason(snapshot.PumpResetReason)}).";
        }
        else if (dropped)
        {
            StatusText = "O volume da bomba caiu sem pedido de zerar (reinício?); o gás proporcional segura o último valor.";
        }
    }

    /// <summary>Warns when the liquid sensor holds a motor that something asked to run, for over 5 s.</summary>
    private void TrackLiquidSensor(SensorSnapshot snapshot)
    {
        var wantsMotor = snapshot.PumpDrive is >= (int)PumpDriveState.ManualSpeed and <= (int)PumpDriveState.DosePulse;
        if (snapshot.PumpLiquidOk == false && wantsMotor)
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            _liquidBlockedSince ??= now;
            if ((now - _liquidBlockedSince.Value).TotalSeconds > 5.0)
            {
                StatusText = "Sensor de líquido seco: o motor está bloqueado.";
            }
        }
        else
        {
            _liquidBlockedSince = null;
        }
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
            Dosing.NotifyCommands();
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
        Dosing.PropertyChanged -= OnDosingChanged;
        Dosing.Detach();
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
