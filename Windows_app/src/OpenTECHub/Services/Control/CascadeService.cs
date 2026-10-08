using System.Globalization;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.KlaMapping;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Telemetry;

namespace OpenTECHub.Services.Control;

/// <summary>
/// The application-side runtime for the oxygen cascade.
/// </summary>
/// <remarks>
/// <para>
/// Owns a <see cref="CascadeController"/> and drives it from live dissolved-oxygen telemetry.
/// Engaging (<see cref="Engage"/>) claims ownership of the oxygen actuators through the
/// <see cref="ICommandArbiter"/>, allocates the control effort with the selected mode
/// (agitation-only, aeration-only, the percentage-window cascade, or the published kLa path)
/// and dispatches the combined frame each step, safe-aborting on stale oxygen, link loss or a
/// loss of ownership. Computation only runs while engaged; <see cref="Arm"/> is a test seam that
/// starts the loop without actuating.
/// </para>
/// </remarks>
public interface ICascadeService
{
    /// <summary>True while the loop is computing on each telemetry frame.</summary>
    bool IsArmed { get; }

    /// <summary>True while the loop is actuating under <see cref="CommandOwner.Automatic"/> ownership.</summary>
    bool IsEngaged { get; }

    /// <summary>True while engaged but waiting for the PC–Hub link or a fresh oxygen reading (D-065).</summary>
    bool IsHolding => false;

    /// <summary>The active actuator-allocation mode.</summary>
    CascadeMode Mode { get; }

    /// <summary>The published kLa path selected for the trajectory mode, or null.</summary>
    KlaPublishedProfile? ActivePath { get; }

    /// <summary>The published receipts available to select, most recent first.</summary>
    IReadOnlyList<KlaPublishedProfile> AvailablePaths { get; }

    /// <summary>The current kLa demand while engaged in the trajectory mode, else null.</summary>
    double? ActiveKlaDemand { get; }

    /// <summary>The persisted configuration currently loaded into the controller.</summary>
    CascadeSettings Configuration { get; }

    /// <summary>The active tuning, as the controller sees it.</summary>
    CascadeTuning Tuning { get; }

    /// <summary>The most recent decomposed evaluation, or <see cref="CascadeTerms.Empty"/>.</summary>
    CascadeTerms Terms { get; }

    /// <summary>The most recent actuation, or null when not computing.</summary>
    CascadeActuationResult? LastActuation { get; }
    /// <summary>Last cascade output accepted for dispatch, distinct from advisory computation.</summary>
    CascadeActuationResult? LastCommandedActuation => null;

    /// <summary>The dissolved-oxygen target, in percent.</summary>
    double OxygenSetpoint { get; }

    /// <summary>The configured actuator windows, for the allocation bar.</summary>
    IReadOnlyList<ActuatorWindow> Windows { get; }

    /// <summary>The latest valid dissolved-oxygen reading, or null before the first frame.</summary>
    double? LatestOxygen { get; }

    /// <summary>The live PV/SP/kLa/output trend for the tuning chart, filled while armed.</summary>
    CascadeTrend Trend { get; }

    /// <summary>Raised on the UI thread after each processed frame or state change.</summary>
    event Action? Updated;

    /// <summary>Test seam: starts loop computation without actuating, resetting loop state. Never sends.</summary>
    void Arm();

    /// <summary>Stops computation and clears the live terms. Disengages first if engaged.</summary>
    void Disarm();

    /// <summary>Loads a new configuration, preserving probe history for a gains-only change.</summary>
    void Configure(CascadeSettings settings);

    /// <summary>Chooses the actuator-allocation mode. Ignored while engaged.</summary>
    void SelectMode(CascadeMode mode);

    /// <summary>Selects the published kLa path for the trajectory mode. Ignored while engaged.</summary>
    void SelectPath(KlaPublishedProfile? profile);

    /// <summary>Refreshes <see cref="AvailablePaths"/> from the receipt store.</summary>
    Task<IReadOnlyList<KlaPublishedProfile>> LoadAvailablePathsAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether live actuation may be engaged right now, with the reason it may not.</summary>
    bool CanEngage(out string? reason);

    /// <summary>
    /// Takes ownership of the oxygen actuators and begins actuating, initialised bumplessly
    /// from the currently commanded actuator values so the transfer has no setpoint jump.
    /// </summary>
    void Engage(double currentAgitationRpm, double currentAerationLpm);

    /// <summary>Releases ownership and stops actuation.</summary>
    void Disengage(string reason);

    /// <summary>Clears the reported integral contribution while the loop keeps running.</summary>
    void ResetIntegral();
    void SuspendForKlaAssay() => throw new NotSupportedException("Suspensão de cascata indisponível.");
    void ResumeAfterKlaAssay(bool engage, double rpm, double flow) => throw new NotSupportedException("Retomada de cascata indisponível.");

    /// <summary>True when a gain schedule is driving the cascade gains (WP8).</summary>
    bool IsGainSchedulingEnabled { get; }

    /// <summary>The gains the schedule is currently applying, or null when scheduling is off.</summary>
    GainSet? ScheduledGains { get; }

    /// <summary>The active schedule segment (1-based for display), or 0 when off.</summary>
    int GainScheduleSegment { get; }

    /// <summary>The number of schedule segments, or 0 when off.</summary>
    int GainScheduleSegmentCount { get; }

    /// <summary>The active schedule version, for the versioned-profile display.</summary>
    int GainScheduleVersion { get; }

    /// <summary>Applies a new gain schedule (WP8), rebuilding the scheduler around the base tuning.</summary>
    void ConfigureGainSchedule(GainScheduleSettings schedule);

    /// <summary>Optional predicate queried by <see cref="CanEngage"/> to verify whether an external subsystem
    /// (such as proportional gas coupling on the external pump) is currently claiming aeration.
    /// </summary>
    Func<bool>? ProportionalGasActivePredicate { get; set; }

    /// <summary>Coordinator managing Modbus priority and UART fallback for motor rotation.</summary>
    MotorRouteCoordinator? RouteCoordinator => null;
}

/// <inheritdoc cref="ICascadeService"/>
public sealed class CascadeService : ICascadeService, IDisposable
{
    private bool _klaSuspended;
    public void SuspendForKlaAssay()
    {
        _klaSuspended = true;
        Disengage("Suspensão para ensaio kLa");
        _lastStepAt = null;
    }
    public void ResumeAfterKlaAssay(bool engage, double rpm, double flow)
    {
        _lastStepAt = null;
        _klaSuspended = false;
        if (engage)
        {
            Engage(rpm, flow);
        }
    }
    /// <summary>The actuators the combined cascade frame writes: motor, flow group and the O₂ monitor.</summary>
    private static readonly ActuatorId[] CascadeActuators =
        [ActuatorId.Agitation, ActuatorId.Aeration, ActuatorId.Oxygen];

    /// <summary>Consecutive missing-oxygen frames after which the engaged loop holds instead of computing.</summary>
    private const int StaleOxygenFrameLimit = 3;

    /// <summary>A step gap this many nominal periods long is a pause, not a sample: rebase instead of integrating.</summary>
    private const double MaximumStepGapPeriods = 5;

    /// <summary>
    /// Set when the loop must restart from the next fresh oxygen sample without integrating the gap:
    /// after the PC–Hub link returns or the oxygen reading comes back (D-065). The engagement is kept.
    /// </summary>
    private bool _rebasePending;
    private bool _oxygenHeld;

    private readonly IDeviceService _device;
    private readonly ICommandArbiter _arbiter;

    /// <summary>The A/B/C wiring, read at each actuation so a Configurações change applies live.</summary>
    private readonly Func<GasRigConfiguration> _rig;
    private readonly IKlaProfileStore _store;
    private readonly ITelemetryHistory? _history;
    private readonly IEventJournal? _journal;
    private readonly TimeProvider _time;
    private readonly double _nominalStepSeconds;
    private readonly MotorRouteCoordinator _routeCoordinator;

    private readonly CascadeTrend _trend = new();

    private CascadeController _controller;
    private CascadeSettings _configuration;
    private GainScheduleSettings _gainSchedule;
    private GainScheduler? _scheduler;
    private DateTimeOffset? _lastStepAt;
    private IReadOnlyList<KlaPublishedProfile> _availablePaths = [];
    private int _staleOxygenFrames;

    public CascadeService(
        IDeviceService device,
        ICommandArbiter arbiter,
        ISettingsService settings,
        IKlaProfileStore store,
        TimeProvider time,
        ITelemetryHistory? history = null,
        IEventJournal? journal = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);

        _device = device;
        _arbiter = arbiter;
        _store = store;
        _history = history;
        _journal = journal;
        _time = time;
        _configuration = settings.Current.Cascade;
        Mode = settings.Current.Cascade.Mode;
        _gainSchedule = settings.Current.GainSchedule;
        _nominalStepSeconds = Math.Max(settings.Current.Connection.DataDelayMs, 250) / 1000.0;
        _rig = () => settings.Current.GasRig.ToConfiguration();
        _routeCoordinator = new MotorRouteCoordinator(arbiter, device, CommandOwner.Automatic);
        _controller = Build(_configuration, Mode);
        RebuildScheduler();

        _device.TelemetryReceived += OnTelemetry;
        _arbiter.OwnershipChanged += OnOwnershipChanged;
        _arbiter.LinkHoldChanged += OnLinkHoldChanged;
    }

    /// <summary>True while the engaged loop waits for the link or for a fresh oxygen reading.</summary>
    public bool IsHolding => IsEngaged && (_arbiter.IsLinkHeld || _oxygenHeld);

    private void OnLinkHoldChanged(bool held)
    {
        if (!IsEngaged) return;
        _rebasePending = true;
        _lastDispatchedCommandJson = null;
        _journal?.Add(AuditSource.Application, AuditSeverity.Warning, held
            ? "Controle de O₂ mantido: enlace com o Hub perdido; o Hub segue com os últimos comandos."
            : "Controle de O₂ retomado após a reconexão do enlace.");
        Updated?.Invoke();
    }

    public MotorRouteCoordinator RouteCoordinator => _routeCoordinator;

    public bool IsArmed { get; private set; }

    public bool IsEngaged { get; private set; }

    public CascadeMode Mode { get; private set; } = CascadeMode.DualCascade;

    public KlaPublishedProfile? ActivePath { get; private set; }

    public IReadOnlyList<KlaPublishedProfile> AvailablePaths => _availablePaths;

    public double? ActiveKlaDemand { get; private set; }

    public CascadeSettings Configuration => _configuration;

    public CascadeTuning Tuning => _controller.Tuning;

    public CascadeTerms Terms { get; private set; } = CascadeTerms.Empty;

    public CascadeActuationResult? LastActuation { get; private set; }
    public CascadeActuationResult? LastCommandedActuation { get; private set; }
    private string? _lastDispatchedCommandJson;

    public double OxygenSetpoint => _controller.OxygenSetpoint;

    public IReadOnlyList<ActuatorWindow> Windows => _controller.Windows;

    public double? LatestOxygen { get; private set; }

    public CascadeTrend Trend => _trend;

    public bool IsGainSchedulingEnabled => _scheduler is not null;

    public GainSet? ScheduledGains => _scheduler?.Effective;

    public int GainScheduleSegment => _scheduler is { } s ? s.Segment + 1 : 0;

    public int GainScheduleSegmentCount => _scheduler is not null ? _gainSchedule.Breakpoints.Length - 1 : 0;

    public int GainScheduleVersion => _gainSchedule.Version;

    public event Action? Updated;

    public void Arm()
    {
        if (IsArmed)
        {
            return;
        }

        StartComputing();
        Updated?.Invoke();
    }

    public void Disarm()
    {
        if (IsEngaged)
        {
            Disengage("cascata desarmada pelo operador");
        }

        if (!IsArmed)
        {
            return;
        }

        IsArmed = false;
        Terms = CascadeTerms.Empty;
        LastActuation = null;
        ActiveKlaDemand = null;
        _trend.Clear();
        Updated?.Invoke();
    }

    public void Configure(CascadeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var windowsChanged = !SameWindows(_configuration, settings);
        _configuration = settings;

        if (windowsChanged)
        {
            _controller = Build(settings, Mode);
            if (IsArmed)
            {
                _lastStepAt = null;
            }

            // A live loop cannot keep running under an allocation whose bands just moved.
            if (IsEngaged)
            {
                Disengage("configuração da cascata alterada");
            }
        }
        else
        {
            _controller.Retune(ToTuning(settings, Mode));
            _controller.OxygenSetpoint = settings.OxygenSetpointPercent;
        }

        // The base tuning changed, so a live schedule must ramp from the new base.
        RebuildScheduler();
        Updated?.Invoke();
    }

    public void ConfigureGainSchedule(GainScheduleSettings schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        _gainSchedule = schedule;
        RebuildScheduler();
        Updated?.Invoke();
    }

    /// <summary>
    /// (Re)builds the gain scheduler from the current schedule settings and base tuning. An invalid
    /// or disabled schedule leaves the controller on its single base tuning.
    /// </summary>
    private void RebuildScheduler()
    {
        if (!_gainSchedule.Enabled)
        {
            _scheduler = null;
            return;
        }

        var breakpoints = ToSchedule(_gainSchedule);
        if (GainSchedule.ValidateBreakpoints(breakpoints).Count > 0)
        {
            _scheduler = null;
            return;
        }

        var baseTuning = ToTuning(_configuration, Mode);
        _scheduler = new GainScheduler(
            new GainSchedule(breakpoints),
            baseTuning,
            Math.Max(_gainSchedule.MaxGainSlewPerSecond, 1e-6),
            new GainSet(baseTuning.Kp, baseTuning.Ki, baseTuning.Kd));
    }

    private static IReadOnlyList<GainScheduleBreakpoint> ToSchedule(GainScheduleSettings s)
        => [.. s.Breakpoints.Select(b => new GainScheduleBreakpoint(b.EffortPercent, b.Kp, b.Ki, b.Kd))];

    private void JournalGainTransition(GainScheduleUpdate update)
    {
        var c = CultureInfo.CurrentCulture;
        _journal?.Add(
            AuditSource.Application,
            AuditSeverity.Information,
            $"Escalonamento de ganho da cascata: segmento {update.Segment + 1}/{_gainSchedule.Breakpoints.Length - 1} " +
            $"em esforço {update.EffortPercent.ToString("F0", c)} %.",
            $"Kp={update.Effective.Kp.ToString("F3", c)}, Ki={update.Effective.Ki.ToString("F4", c)}, " +
            $"Kd={update.Effective.Kd.ToString("F3", c)} (perfil v{_gainSchedule.Version}).");
    }

    public void SelectMode(CascadeMode mode)
    {
        if (IsEngaged || Mode == mode)
        {
            return;
        }

        Mode = mode;
        _controller.Retune(ToTuning(_configuration, mode));
        RebuildScheduler();
        Updated?.Invoke();
    }

    public void SelectPath(KlaPublishedProfile? profile)
    {
        if (IsEngaged)
        {
            return;
        }

        ActivePath = profile;
        Updated?.Invoke();
    }

    public async Task<IReadOnlyList<KlaPublishedProfile>> LoadAvailablePathsAsync(
        CancellationToken cancellationToken = default)
    {
        _availablePaths = await _store.LoadPublishedAsync(cancellationToken).ConfigureAwait(true);

        // Keep the current selection if it survived a refresh; otherwise drop it.
        if (ActivePath is { } active &&
            _availablePaths.All(p => p.ReceiptFingerprint != active.ReceiptFingerprint))
        {
            ActivePath = null;
        }

        Updated?.Invoke();
        return _availablePaths;
    }

    public Func<bool>? ProportionalGasActivePredicate { get; set; }

    public bool CanEngage(out string? reason)
    {
        if (_device.State is not ConnectionState.Connected)
        {
            reason = "Conecte-se ao equipamento antes de ativar o Automático.";
            return false;
        }

        if (Mode == CascadeMode.KlaPath && ActivePath is null)
        {
            reason = "Selecione um mapa kLa publicado para o modo trajetória.";
            return false;
        }

        if (ProportionalGasActivePredicate?.Invoke() == true)
        {
            reason = "O acoplamento de gás proporcional ao volume dosado está ativo na Bomba Externa. Desative-o para iniciar o controle de oxigênio (cascata/mapa).";
            return false;
        }

        foreach (var actuator in CascadeActuators)
        {
            var owner = _arbiter.OwnerOf(actuator);
            if (owner is not (CommandOwner.Manual or CommandOwner.Automatic))
            {
                reason = $"{CommandActuators.Label(actuator)} pertence a outro dono no momento.";
                return false;
            }
        }

        reason = null;
        return true;
    }

    public void Engage(double currentAgitationRpm, double currentAerationLpm)
    {
        if (IsEngaged || !CanEngage(out _))
        {
            return;
        }

        var allocation = BuildAllocation(currentAgitationRpm, currentAerationLpm);

        if (!IsArmed)
        {
            StartComputing();
        }

        _controller.SetAllocation(allocation);
        _controller.Retune(ToTuning(_configuration, Mode));

        // Bumpless: place the loop at the effort that reproduces the actuator the operator
        // left running, so the first automatic frame nudges from there rather than jumping.
        var effort = Mode == CascadeMode.AerationOnly
            ? allocation.EffortForAeration(currentAerationLpm)
            : allocation.EffortForAgitation(currentAgitationRpm);
        _controller.Preload(effort);

        _arbiter.Claim(CommandOwner.Automatic, CascadeActuators, $"cascata O₂ · {ModeLabel(Mode)}");
        _routeCoordinator.EnsurePrimaryRoute(out var routeMsg);
        if (!_routeCoordinator.RouteRequestAccepted) { _arbiter.Release(CommandOwner.Automatic, routeMsg); return; }
        _journal?.Add(AuditSource.Application, AuditSeverity.Information, routeMsg);
        _staleOxygenFrames = 0;
        IsEngaged = true;
        LastCommandedActuation = null;
        _lastDispatchedCommandJson = null;
        Updated?.Invoke();
    }

    public void Disengage(string reason)
    {
        if (!IsEngaged)
        {
            return;
        }

        // Clear the flag first, so the ownership-change event this Release raises is not
        // mistaken for an external takeover.
        IsEngaged = false;
        ActiveKlaDemand = null;
        LastCommandedActuation = null;
        _arbiter.Release(CommandOwner.Automatic, reason);
        _controller.SetAllocation(BuildWindowAllocation());
        Updated?.Invoke();
    }

    public void ResetIntegral()
    {
        _controller.ResetIntegral();
        Updated?.Invoke();
    }

    /// <summary>Returns the specific PID settings for the given mode from the configuration.</summary>
    public static ModePidSettings GetPidForMode(CascadeMode mode, CascadeSettings cfg) => mode switch
    {
        CascadeMode.AgitationOnly => cfg.AgitationPid,
        CascadeMode.AerationOnly => cfg.AerationPid,
        CascadeMode.DualCascade => cfg.CascadePid,
        CascadeMode.KlaPath => cfg.MapPid,
        _ => cfg.CascadePid,
    };

    /// <summary>Projects mode PID settings onto the controller's tuning record.</summary>
    public static CascadeTuning ToTuning(ModePidSettings p) => new()
    {
        KDot = p.KDot,
        Kp = p.Kp,
        Ki = p.Ki,
        Kd = p.Kd,
        PredictionHorizonSeconds = p.TPred,
        TauD = p.TauD,
        IntegralMin = p.IMin,
        IntegralMax = p.IMax,
        MWindow = p.MWindow,
        JAvg = p.JAvg,
        NPred = p.NPred,
        IntervalSeconds = p.IntervalSeconds,
        FatorGanhoAeracao = p.FatorGanhoAeracao,
        HabilitarGainScheduling = p.HabilitarGainScheduling,
        OutputMin = 0,
        OutputMax = 100,
    };

    /// <summary>Projects persisted settings onto the controller's tuning record for dual cascade.</summary>
    public static CascadeTuning ToTuning(CascadeSettings c) => ToTuning(GetPidForMode(CascadeMode.DualCascade, c));

    /// <summary>Projects persisted settings onto the controller's tuning record for a specific mode.</summary>
    public static CascadeTuning ToTuning(CascadeSettings c, CascadeMode mode) => ToTuning(GetPidForMode(mode, c));

    private void StartComputing()
    {
        _controller.Reset();
        _controller.OxygenSetpoint = _configuration.OxygenSetpointPercent;
        _controller.Retune(ToTuning(_configuration, Mode));
        _lastStepAt = null;
        _trend.Clear();
        Terms = CascadeTerms.Empty;
        LastActuation = null;
        IsArmed = true;
    }

    private CascadeAllocation BuildAllocation(double currentAgitationRpm, double currentAerationLpm) => Mode switch
    {
        CascadeMode.AgitationOnly => SingleActuatorAllocation.Agitation(
            _configuration.AgitationMinRpm, _configuration.AgitationMaxRpm, currentAerationLpm),
        CascadeMode.AerationOnly => SingleActuatorAllocation.Aeration(
            _configuration.AerationMinLpm, _configuration.AerationMaxLpm, currentAgitationRpm),
        CascadeMode.DualCascade => BuildWindowAllocation(),
        _ => new KlaPathAllocation(
            (ActivePath ?? throw new InvalidOperationException("No published kLa path is selected."))
                .Payload.Allocation),
    };

    private WindowAllocation BuildWindowAllocation() => new(
        new ActuatorWindow(CascadeController.AgitationActuator,
            _configuration.AgitationMinRpm, _configuration.AgitationMaxRpm,
            _configuration.AgitationEffortStart, _configuration.AgitationEffortEnd),
        new ActuatorWindow(CascadeController.AerationActuator,
            _configuration.AerationMinLpm, _configuration.AerationMaxLpm,
            _configuration.AerationEffortStart, _configuration.AerationEffortEnd));

    private static string ModeLabel(CascadeMode mode) => mode switch
    {
        CascadeMode.AgitationOnly => "somente agitação",
        CascadeMode.AerationOnly => "somente aeração",
        CascadeMode.DualCascade => "cascata (percentuais)",
        _ => "trajetória kLa",
    };

    private static CascadeController Build(CascadeSettings c, CascadeMode mode = CascadeMode.DualCascade) => new(
        ToTuning(c, mode),
        new ActuatorWindow(
            CascadeController.AgitationActuator,
            c.AgitationMinRpm, c.AgitationMaxRpm, c.AgitationEffortStart, c.AgitationEffortEnd),
        new ActuatorWindow(
            CascadeController.AerationActuator,
            c.AerationMinLpm, c.AerationMaxLpm, c.AerationEffortStart, c.AerationEffortEnd),
        c.OxygenSetpointPercent);

    private static bool SameWindows(CascadeSettings a, CascadeSettings b) =>
        a.AgitationMinRpm == b.AgitationMinRpm &&
        a.AgitationMaxRpm == b.AgitationMaxRpm &&
        a.AgitationEffortStart == b.AgitationEffortStart &&
        a.AgitationEffortEnd == b.AgitationEffortEnd &&
        a.AerationMinLpm == b.AerationMinLpm &&
        a.AerationMaxLpm == b.AerationMaxLpm &&
        a.AerationEffortStart == b.AerationEffortStart &&
        a.AerationEffortEnd == b.AerationEffortEnd;

    private void OnTelemetry(SensorSnapshot snapshot)
    {
        LatestOxygen = snapshot.OxygenCalibrated > SensorReadings.NotReceived
            ? snapshot.OxygenCalibrated
            : null;

        if (LatestOxygen is not { } oxygen)
        {
            // No usable oxygen: a live loop flying blind must not actuate on a stale value. It holds
            // (the Hub keeps the last commands) and resumes from the next fresh reading (D-065).
            if (IsEngaged && ++_staleOxygenFrames >= StaleOxygenFrameLimit && !_oxygenHeld)
            {
                _oxygenHeld = true;
                _rebasePending = true;
                _lastDispatchedCommandJson = null;
                _journal?.Add(AuditSource.Application, AuditSeverity.Warning,
                    "Controle de O₂ mantido: sem leitura de oxigênio; retoma com a próxima leitura válida.");
            }

            Updated?.Invoke();
            return;
        }

        if (!IsArmed)
        {
            Updated?.Invoke();
            return;
        }

        if (_klaSuspended)
        {
            return;
        }

        _staleOxygenFrames = 0;
        if (_oxygenHeld)
        {
            _oxygenHeld = false;
            _journal?.Add(AuditSource.Application, AuditSeverity.Information, "Controle de O₂ retomado: leitura de oxigênio restabelecida.");
        }

        var now = _time.GetUtcNow();
        var dt = _lastStepAt is { } last ? (now - last).TotalSeconds : _nominalStepSeconds;
        _lastStepAt = now;
        if (dt <= 0)
        {
            dt = _nominalStepSeconds;
        }

        if (IsEngaged && (_rebasePending || dt > Math.Max(30, MaximumStepGapPeriods * Math.Max(_nominalStepSeconds, GetPidForMode(Mode, _configuration).IntervalSeconds))))
        {
            // Bumpless return: keep the effort and integral, restart time and derivative at this sample.
            _controller.ResumeFromSuspension(oxygen);
            _rebasePending = false;
            _lastDispatchedCommandJson = null;
            Updated?.Invoke();
            return;
        }

        // Gain scheduling (WP8): drive the controller's gains from the current control effort,
        // with a bounded transition, before this step's update. Only the gains change — the rate
        // window is untouched, so the probe history is preserved.
        if (_scheduler is { } scheduler)
        {
            var scheduled = scheduler.Step(_controller.Effort, dt);
            _controller.Retune(scheduled.Tuning);
            if (scheduled.SegmentChanged)
            {
                JournalGainTransition(scheduled);
            }
        }

        LastActuation = _controller.Update(oxygen, dt);
        if (_routeCoordinator.IsUartFallback && LastActuation.AgitationRpm > MotorRouteCoordinator.UartFallbackMaxRpm)
        {
            LastActuation = LastActuation with
            {
                AgitationRpm = (int)MotorRouteCoordinator.UartFallbackMaxRpm
            };
        }
        Terms = LastActuation.Terms;

        if (IsEngaged)
        {
            ActiveKlaDemand = _controller.Allocation is KlaPathAllocation kla
                ? kla.KlaForEffort(Terms.Output)
                : null;

            var frame = CascadeController.BuildCommand(LastActuation, _rig());
            var commandJson = frame.ToJson();
            if (commandJson != _lastDispatchedCommandJson)
            {
                var result = _arbiter.Dispatch(CommandOwner.Automatic, frame);
                if (!result.Accepted && result.LinkUnavailable)
                {
                    _rebasePending = true; // resend from fresh data when the link returns
                }
                else if (!result.Accepted)
                {
                    // Ownership was taken from under us between frames; stop cleanly.
                    Disengage("aborto seguro: posse dos atuadores perdida");
                }
                else
                {
                    _lastDispatchedCommandJson = commandJson;
                    LastCommandedActuation = LastActuation;
                }
            }
        }

        _history?.RecordCascade(
            Terms.Output,
            Terms.PredictedMeasurement,
            Terms.Error + Terms.PredictedMeasurement,
            Terms.MeasurementRate,
            ActiveKlaDemand);

        _trend.Add(oxygen, _controller.OxygenSetpoint, ActiveKlaDemand, Terms.Output,
            now.ToUnixTimeMilliseconds() / 60000.0);

        Updated?.Invoke();
    }

    /// <summary>
    /// Reacts to ownership moving away from the cascade — a manual takeover or the arbiter's
    /// safe abort on link loss. Our own Claim/Release never trip this because the engaged flag
    /// is set after Claim and cleared before Release.
    /// </summary>
    private void OnOwnershipChanged(OwnershipTransfer transfer)
    {
        if (!IsEngaged)
        {
            return;
        }

        var stillOurs = CascadeActuators.All(a => _arbiter.OwnerOf(a) == CommandOwner.Automatic);
        if (!stillOurs)
        {
            IsEngaged = false;
            ActiveKlaDemand = null;
            LastCommandedActuation = null;
            _controller.SetAllocation(BuildWindowAllocation());
            Updated?.Invoke();
        }
    }

    public void Dispose()
    {
        _device.TelemetryReceived -= OnTelemetry;
        _arbiter.OwnershipChanged -= OnOwnershipChanged;
        _arbiter.LinkHoldChanged -= OnLinkHoldChanged;
    }
}
