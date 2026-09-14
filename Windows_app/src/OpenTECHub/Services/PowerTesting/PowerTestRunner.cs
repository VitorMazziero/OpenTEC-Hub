using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Control;

namespace OpenTECHub.Services.PowerTesting;

/// <summary>
/// Phase-1 automatic impeller-power runner. It owns agitation, waits for measured speed,
/// executes the two-gate capture, persists every valid sample, and returns the actuator safely.
/// </summary>
public sealed class PowerTestRunner : IPowerTestRunner
{
    private const double MaxFlow = 15.0;

    /// <summary>The A/B/C wiring; the runner has no settings service, so the composition root passes it in.</summary>
    private readonly Func<GasRigConfiguration> _rig;
    private GasRigConfiguration Rig => _rig();

    /// <summary>Builds the flow frame for <paramref name="route"/> and records the pair the echo must confirm.</summary>
    private OpenTECCommand RouteFrame(double flow, GasRoute route)
    {
        var (v1, v2) = GasRouting.Resolve(route, Rig);
        _targetGasState = (flow, v1, v2, flow <= 0.0);
        return CommandBuilders.FlowRoute(flow, MaxFlow, route, Rig);
    }
    private static readonly ActuatorId[] Phase1Actuators = [ActuatorId.Agitation];
    private static readonly ActuatorId[] GassedActuators = [ActuatorId.Agitation, ActuatorId.Aeration];

    private readonly IDeviceService _device;
    private readonly ICommandArbiter _arbiter;
    private readonly IPowerTestStore _store;
    private readonly IPowerAnalysisEngine _analysis;
    private readonly IPowerTestInterlock _interlock;
    private readonly TimeProvider _time;
    private readonly ITimer _watchdog;
    private readonly PowerMotorRouteCoordinator _routeCoordinator;

    private readonly List<PowerDataPoint> _runPoints = [];
    private readonly List<PowerGlobalSeriesSample> _globalSamples = [];

    private PowerTestDocument? _currentTest;
    private PowerRun? _currentRun;
    private PowerCondition? _currentCondition;
    private PowerCaptureController? _capture;
    private PowerRunPhase _phase = PowerRunPhase.Idle;
    private string _statusMessage = "Nenhum ensaio de potência carregado.";
    private double _phaseStartMonotonic;
    private double _testStartMonotonic;
    private double _runStartMonotonic;
    private double _lastTelemetryMonotonic;
    private double _lastValidServoMonotonic;
    private double _currentRpm;
    private double _currentTorquePercent;
    private int _speedStableCount;
    private int _commandedRpm;
    private bool _disposed;
    private (double Flow, bool V1, bool V2, bool VFlow)? _targetGasState;
    private long _minimumExpectedFlowCommandId;
    private long _lastFlowCommandId;
    private int _ventFlowStableCount;
    private double? _ventFlowDeviation;
    private readonly List<double> _ventFlowWindow = [];

    /// <summary>Sequence failures per condition in this assay, for <see cref="UnattendedFailurePolicy.RetryThenSkip"/>.</summary>
    private readonly Dictionary<Guid, int> _sequenceFailures = new();
    private double? _pairedUngassedP0W;
    private double? _pairedUngassedP0Ci95W;
    private bool _isSubphase2Both;

    public PowerTestRunner(
        IDeviceService device,
        ICommandArbiter arbiter,
        IPowerTestStore store,
        IPowerAnalysisEngine analysis,
        IPowerTestInterlock interlock,
        TimeProvider time,
        Func<GasRigConfiguration>? gasRig = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(interlock);
        ArgumentNullException.ThrowIfNull(time);

        _device = device;
        _arbiter = arbiter;
        _store = store;
        _analysis = analysis;
        _interlock = interlock;
        _time = time;
        _rig = gasRig ?? (static () => GasRigConfiguration.Default);

        _device.TelemetryReceived += OnTelemetryReceived;
        _device.StateChanged += OnConnectionStateChanged;
        _arbiter.OwnershipRevoked += OnOwnershipRevoked;
        _store.WriteFailed += OnStoreWriteFailed;

        if (_device.Latest is { } latest)
        {
            _lastTelemetryMonotonic = GetMonotonicSeconds();
            if (HasValidServoMeasurement(latest))
            {
                _lastValidServoMonotonic = _lastTelemetryMonotonic;
                _currentRpm = latest.ServoRpm;
                _currentTorquePercent = latest.ServoTorquePct;
            }
        }

        _watchdog = _time.CreateTimer(
            _ => CheckWatchdog(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        _routeCoordinator = new PowerMotorRouteCoordinator(_arbiter, _device, CommandOwner.PowerAssay);
    }

    public PowerMotorRouteCoordinator RouteCoordinator => _routeCoordinator;
    public PowerTestDocument? CurrentTest => _currentTest;
    public PowerRun? CurrentRun => _currentRun;
    public PowerCondition? CurrentCondition => _currentCondition;
    public PowerRunPhase Phase => _phase;
    public bool IsRunning => _currentTest?.Status == PowerTestStatus.Running &&
                             _phase is not (PowerRunPhase.Idle or PowerRunPhase.Completed or
                                 PowerRunPhase.Faulted or PowerRunPhase.Accepted or PowerRunPhase.Rejected);
    public bool IsInReview => _phase == PowerRunPhase.Reviewing;
    public bool IsPausedByOperator => _phase == PowerRunPhase.PausedByOperator;
    public bool IsPausedForMeasurement => _phase == PowerRunPhase.PausedForMeasurement;
    public double CurrentRpm => _currentRpm;
    public double CurrentTorquePercent => _currentTorquePercent;
    public double CurrentTorqueCi95Percent => _capture?.CurrentTorqueCi95Percent ?? 0.0;
    public double CurrentTorqueCiTargetPercent => _capture?.CurrentTargetTorqueCi95Percent ?? 0.0;
    public int CurrentAttempt => _currentRun?.Tries ?? 0;
    public string StatusMessage => _statusMessage;


    public double PhaseElapsedSeconds => _phaseStartMonotonic > 0
        ? Math.Max(0, GetMonotonicSeconds() - _phaseStartMonotonic)
        : 0;

    public double TotalElapsedSeconds => _testStartMonotonic > 0
        ? Math.Max(0, GetMonotonicSeconds() - _testStartMonotonic)
        : 0;

    public IReadOnlyList<PowerDataPoint> CurrentRunPoints => _runPoints.ToArray();
    public IReadOnlyList<PowerGlobalSeriesSample> GlobalSeriesSamples => _globalSamples.ToArray();

    public event Action? StateChanged;
    public event Action<PowerDataPoint>? DataPointAdded;
    public event Action<PowerRun>? RunStarted;
    public event Action<string>? Logged;

    public bool CanStart(PowerTestDocument doc, out string? reason)
        => CanStart(doc, null, out reason);

    /// <summary>
    /// Preflight for the condition that is about to run. A null condition means "whichever
    /// one <see cref="NextPendingCondition"/> would pick", which is what the readout and
    /// <see cref="StartTestAsync"/> ask about.
    /// </summary>
    /// <remarks>
    /// The gas requirements are scoped to that one condition on purpose. Scoping them to the
    /// whole plan made a single gassed row anywhere in the table block the ungassed P0 rows
    /// as well, so a flowmeter dropout stopped an assay that did not need the flowmeter yet.
    /// </remarks>
    private bool CanStart(PowerTestDocument doc, PowerCondition? condition, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(doc);

        try
        {
            ValidateDocument(doc);
        }
        catch (InvalidOperationException ex)
        {
            reason = ex.Message;
            return false;
        }

        if (_device.State != ConnectionState.Connected)
        {
            reason = "Conecte o Hub antes de iniciar o ensaio de potência.";
            return false;
        }

        var now = GetMonotonicSeconds();
        if (_lastValidServoMonotonic <= 0 ||
            now - _lastValidServoMonotonic > doc.Settings.MeasurementTimeoutSeconds ||
            _device.Latest is not { } latest || !HasValidServoMeasurement(latest))
        {
            reason = "O servo precisa estar online, roteado e entregando uma amostra recente.";
            return false;
        }

        // "Both" runs an ungassed subphase and then a gassed one, so it needs the loop too.
        var target = condition ?? NextPendingConditionOf(doc);
        var needsGas = target is null
            ? doc.Conditions.Any(c => c.GasMode is PowerGasMode.Gassed or PowerGasMode.Both)
            : target.GasMode is PowerGasMode.Gassed or PowerGasMode.Both;

        // An open gas path is NOT an operator error, and the assay no longer refuses to start
        // over one: StartRunCore shuts it before the P0 measurement. The old refusal ("Feche a
        // vazão e a rota de gás antes de medir P0") also counted the residual rate the sensor
        // reads with every valve closed, which no operator action could clear.
        //
        // An ungassed condition still starts with the flowmeter offline, because a rig with no
        // flowmeter at all reports exactly that and pure-agitation assays must remain runnable.
        var mustCloseGas = HasOpenGasPath(latest);

        if (needsGas && (!latest.FlowmeterOnline || !double.IsFinite(latest.FlowRate)))
        {
            reason = "O fluxômetro precisa estar online para ensaios com aeração.";
            return false;
        }

        if (!_interlock.CanStart(out reason))
        {
            return false;
        }

        var owner = _arbiter.OwnerOf(ActuatorId.Agitation);
        if (owner is not (CommandOwner.Manual or CommandOwner.PowerAssay))
        {
            reason = $"A agitação pertence a {owner}; libere-a antes de iniciar.";
            return false;
        }

        if (needsGas || mustCloseGas)
        {
            var gasOwner = _arbiter.OwnerOf(ActuatorId.Aeration);
            if (gasOwner is not (CommandOwner.Manual or CommandOwner.PowerAssay))
            {
                reason = $"A malha de gás pertence a {gasOwner}; libere-a antes de iniciar.";
                return false;
            }
        }

        reason = null;
        return true;
    }

    public void PrepareTest(PowerTestDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        _currentTest = doc;
        _currentRun = null;
        _currentCondition = null;
        _capture = null;
        _runPoints.Clear();
        _globalSamples.Clear();
        _phase = PowerRunPhase.Idle;
        _phaseStartMonotonic = 0;
        _testStartMonotonic = 0;
        _statusMessage = $"Ensaio '{doc.Name}' carregado.";
        RaiseStateChanged();
    }

    public void ClearTest()
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("Interrompa o ensaio antes de fechá-lo.");
        }

        _currentTest = null;
        _currentRun = null;
        _currentCondition = null;
        _capture = null;
        _runPoints.Clear();
        _globalSamples.Clear();
        _phase = PowerRunPhase.Idle;
        _phaseStartMonotonic = 0;
        _testStartMonotonic = 0;
        _runStartMonotonic = 0;
        _statusMessage = "Nenhum ensaio de potência carregado.";
        RaiseStateChanged();
    }

    public Task StartTestAsync(PowerTestDocument doc, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_currentTest?.Status == PowerTestStatus.Running)
        {
            throw new InvalidOperationException("Já existe um ensaio de potência em execução.");
        }
        if (!CanStart(doc, out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        _currentTest = doc;
        _sequenceFailures.Clear();
        doc.Status = PowerTestStatus.Running;
        doc.StartedUtc ??= _time.GetUtcNow();
        doc.GasRig = Persistence.GasRigSettings.From(Rig);
        doc.CompletedUtc = null;
        doc.InterruptionReason = null;
        if (_device.Latest is { } latest)
        {
            doc.HubFirmwareVersion = string.IsNullOrWhiteSpace(latest.HubFirmwareVersion)
                ? null
                : latest.HubFirmwareVersion;
            doc.HubProtocolVersion = latest.HubProtocolVersion > 0
                ? latest.HubProtocolVersion
                : null;
            doc.ExternalNodes = Services.Communication.ExternalNodeProvenance.From(latest);
        }
        _testStartMonotonic = GetMonotonicSeconds();
        _store.SaveTestManifest(doc);
        LogEvent("TestStarted", $"Ensaio '{doc.Name}' iniciado. Arranjo: {Rig.Describe()}.");

        var next = NextPendingCondition();
        if (next is null)
        {
            SetPhase(PowerRunPhase.Idle, "Ensaio ativo, sem condições pendentes.");
            return Task.CompletedTask;
        }

        StartRunCore(next, Math.Max(1, next.CompletedReplicates + 1), cancellationToken);
        return Task.CompletedTask;
    }

    public Task StartRunAsync(
        PowerCondition condition,
        int replicateNumber,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_currentTest is null)
        {
            throw new InvalidOperationException("Nenhum ensaio de potência ativo.");
        }
        if (_currentTest.Status != PowerTestStatus.Running)
        {
            throw new InvalidOperationException("O ensaio precisa estar em execução antes de iniciar uma corrida.");
        }
        if (_phase is not (PowerRunPhase.Idle or PowerRunPhase.Accepted or PowerRunPhase.Rejected))
        {
            throw new InvalidOperationException("Já existe uma corrida de potência em execução.");
        }
        if (!CanStart(_currentTest, condition, out var reason))
        {
            throw new InvalidOperationException(reason);
        }
        StartRunCore(condition, replicateNumber, cancellationToken);
        return Task.CompletedTask;
    }

    public Task ResumeAfterMeasurementAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_phase != PowerRunPhase.PausedForMeasurement || _currentTest is null ||
            _currentRun is null || _currentCondition is null)
        {
            throw new InvalidOperationException("Nenhuma captura está pausada por falta de medida.");
        }
        if (_device.Latest is not { } latest || !HasValidServoMeasurement(latest) ||
            GetMonotonicSeconds() - _lastValidServoMonotonic > _currentTest.Settings.MeasurementTimeoutSeconds)
        {
            throw new InvalidOperationException("A medida do servo ainda não voltou de forma válida.");
        }
        if (_currentRun.GasMode == PowerGasMode.Gassed && (!latest.FlowmeterOnline || !double.IsFinite(latest.FlowRate)))
        {
            throw new InvalidOperationException("A medida do fluxômetro ainda não voltou de forma válida.");
        }
        if (_arbiter.OwnerOf(ActuatorId.Agitation) != CommandOwner.PowerAssay)
        {
            throw new InvalidOperationException("O ensaio perdeu a posse da agitação.");
        }
        if (_currentRun.GasMode == PowerGasMode.Gassed && _arbiter.OwnerOf(ActuatorId.Aeration) != CommandOwner.PowerAssay)
        {
            throw new InvalidOperationException("O ensaio perdeu a posse da malha de gás.");
        }

        if (_capture is null)
        {
            throw new InvalidOperationException("O controlador da captura não está disponível.");
        }
        _speedStableCount = 0;
        _capture.Reset();
        if (_currentRun.GasMode == PowerGasMode.Gassed && _currentCondition.GasFlowLpm is { } flow)
        {
            StartGassedSequence(flow);
        }
        else
        {
            SetPhase(PowerRunPhase.SettingSpeed, "Medida restabelecida; reaproximando a rotação antes de recapturar.");
            DispatchMotorOrFault(_commandedRpm, "retomar a rotação");
        }
        LogEvent("MeasurementResumed", "Telemetria válida restabelecida; as duas portas foram reiniciadas.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Resume from <see cref="PowerRunPhase.PausedForMeasurement"/> when every precondition the
    /// operator-driven path checks is already satisfied. Returns false without side effects when
    /// something is still missing, so the caller falls back to waiting for the operator.
    /// </summary>
    private bool TryResumeAfterMeasurement()
    {
        try
        {
            ResumeAfterMeasurementAsync().GetAwaiter().GetResult();
            LogEvent("MeasurementAutoResumed", "Retomada automática: aceite automático está ligado.");
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public Task PauseAsync()
    {
        if (_currentRun is null || _currentTest is null ||
            _phase is not (PowerRunPhase.SettingSpeed or PowerRunPhase.SettlingTorque or
                PowerRunPhase.AccumulatingToTarget))
        {
            throw new InvalidOperationException("Nenhuma captura ativa pode ser pausada.");
        }

        _capture?.Reset();
        _speedStableCount = 0;
        SetPhase(PowerRunPhase.PausedByOperator,
            "Captura pausada pelo operador; a condição permanece na rotação comandada.");
        PersistCurrentRun();
        LogEvent("OperatorPaused", "A janela parcial foi descartada; a retomada repetirá as duas portas.");
        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_phase != PowerRunPhase.PausedByOperator || _currentTest is null ||
            _currentRun is null || _currentCondition is null)
        {
            throw new InvalidOperationException("A captura não está pausada pelo operador.");
        }
        if (_device.Latest is not { } latest || !HasValidServoMeasurement(latest) ||
            GetMonotonicSeconds() - _lastValidServoMonotonic > _currentTest.Settings.MeasurementTimeoutSeconds)
        {
            throw new InvalidOperationException("A medida do servo não está válida para retomar.");
        }
        if (_arbiter.OwnerOf(ActuatorId.Agitation) != CommandOwner.PowerAssay)
        {
            throw new InvalidOperationException("O ensaio perdeu a posse da agitação.");
        }

        _capture?.Reset();
        _speedStableCount = 0;
        SetPhase(PowerRunPhase.SettingSpeed,
            "Captura retomada; reaproximando a rotação e reiniciando as duas portas.");
        DispatchMotorOrFault(_commandedRpm, "retomar a rotação");
        LogEvent("OperatorResumed", "Captura retomada com a janela estatística zerada.");
        return Task.CompletedTask;
    }

    public Task SkipCurrentConditionAsync(string reason = "Condição pulada pelo operador")
    {
        if (_currentRun is null || _currentCondition is null || _currentTest is null || !IsRunning)
        {
            throw new InvalidOperationException("Nenhuma condição ativa pode ser pulada.");
        }

        var detail = string.IsNullOrWhiteSpace(reason) ? "Condição pulada pelo operador" : reason.Trim();
        _currentRun.StopReason = PowerStopReason.Aborted;
        _currentRun.CurrentPhase = PowerRunPhase.Rejected;
        _currentRun.CompletedUtc = _time.GetUtcNow();
        _currentCondition.CompletedReplicates++;
        _currentCondition.RejectedReplicates++;
        _currentCondition.Status = PowerConditionStatus.Skipped;
        UpsertCurrentRunSummary(PowerRunPhase.Rejected);
        SafeParkAndRelease(detail);
        PersistCurrentRun();
        SetPhase(PowerRunPhase.Rejected, detail);
        LogEvent("ConditionSkipped", detail);
        return Task.CompletedTask;
    }

    public Task SubmitManualEnergyAsync(
        double electricalPowerW,
        string? instrument = null,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_phase != PowerRunPhase.HoldingForManualEnergy || _currentRun is null)
        {
            throw new InvalidOperationException("A corrida não está aguardando a leitura do wattímetro.");
        }
        if (!double.IsFinite(electricalPowerW) || electricalPowerW < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(electricalPowerW));
        }

        _currentRun.ManualElec = new ManualElecReading
        {
            PowerElectricalW = electricalPowerW,
            PowerMechanicalWAtReading = _currentRun.MeanShaftPowerW,
            Rpm = _currentRun.MeanRpmMeasured,
            GasOpen = false,
            Instrument = string.IsNullOrWhiteSpace(instrument) ? null : instrument.Trim(),
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            TimestampUtc = _time.GetUtcNow(),
        };
        LogEvent("ManualEnergyCaptured", $"Leitura elétrica de {electricalPowerW:F2} W vinculada à corrida.");
        FinishCapturedRun();
        return Task.CompletedTask;
    }

    public Task StopRunAndReviewAsync(string reason = "Parada pelo operador")
    {
        if (_currentRun is null || _currentTest is null || !IsRunning)
        {
            return Task.CompletedTask;
        }
        StopForReview(reason, PowerStopReason.Aborted);
        return Task.CompletedTask;
    }

    public Task AcceptRunAsync()
    {
        if (_phase != PowerRunPhase.Reviewing || _currentRun is null ||
            _currentCondition is null || _currentTest is null)
        {
            throw new InvalidOperationException("Nenhuma corrida está aguardando aceite.");
        }
        if (!HasCapture(_currentRun))
        {
            throw new InvalidOperationException(NoCaptureMessage);
        }
        AcceptRunCore(autoAdvance: false);
        return Task.CompletedTask;
    }

    /// <summary>
    /// A run that stopped for review before capturing anything — vent, valve or speed time-out —
    /// has no result to accept. Bench of 2026-09-11: two such runs were accepted with n = 0 and
    /// P = 0 W, entered the summary and marked their conditions complete (D-050).
    /// </summary>
    public const string NoCaptureMessage = "Corrida sem captura: repita ou rejeite.";

    public static bool HasCapture(PowerRun run) => run.SampleCount > 0 && double.IsFinite(run.NetPowerW);

    public Task RejectRunAsync(string reason)
    {
        if (_phase != PowerRunPhase.Reviewing || _currentRun is null ||
            _currentCondition is null || _currentTest is null)
        {
            throw new InvalidOperationException("Nenhuma corrida está aguardando revisão.");
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Informe o motivo da rejeição.", nameof(reason));
        }

        _currentRun.CurrentPhase = PowerRunPhase.Rejected;
        _currentRun.CompletedUtc ??= _time.GetUtcNow();
        _currentCondition.CompletedReplicates++;
        _currentCondition.RejectedReplicates++;
        _currentCondition.Status = PowerConditionStatus.Pending;
        UpsertCurrentRunSummary(PowerRunPhase.Rejected);
        PersistCurrentRun();
        SetPhase(PowerRunPhase.Rejected, $"Corrida rejeitada: {reason.Trim()}");
        LogEvent("RunRejected", reason.Trim());
        SafeParkAndRelease("corrida rejeitada");
        return Task.CompletedTask;
    }

    public Task RepeatRunAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_currentCondition is null)
        {
            throw new InvalidOperationException("Nenhuma condição disponível para repetir.");
        }
        if (_phase == PowerRunPhase.Reviewing && _currentRun is { } reviewedRun)
        {
            reviewedRun.CurrentPhase = PowerRunPhase.Rejected;
            reviewedRun.CompletedUtc ??= _time.GetUtcNow();
            _currentCondition.CompletedReplicates++;
            _currentCondition.RejectedReplicates++;
            _currentCondition.Status = PowerConditionStatus.Pending;
            PersistCurrentRun();
            SetPhase(PowerRunPhase.Rejected, "Corrida anterior rejeitada para repetição.");
            LogEvent("RunRepeated", "A corrida em revisão foi marcada como rejeitada antes da repetição.");
        }
        var replicate = _currentRun?.ReplicateNumber ?? Math.Max(1, _currentCondition.CompletedReplicates + 1);
        return StartRunAsync(_currentCondition, replicate, cancellationToken);
    }

    public Task CompleteTestAsync()
    {
        if (_currentTest is null)
        {
            return Task.CompletedTask;
        }
        if (_phase == PowerRunPhase.Completed)
        {
            return Task.CompletedTask;
        }
        if (_phase is not (PowerRunPhase.Idle or PowerRunPhase.Accepted or
                           PowerRunPhase.Rejected or PowerRunPhase.PreparingNextRun))
        {
            throw new InvalidOperationException("Conclua a revisão ou aborte a corrida ativa antes de finalizar o ensaio.");
        }

        SafeParkAndRelease("ensaio concluído");
        _currentTest.Status = PowerTestStatus.Completed;
        _currentTest.CompletedUtc = _time.GetUtcNow();
        _currentTest.InterruptionReason = null;
        _store.SaveTestManifest(_currentTest);
        _store.UpdateResultsSummary(_currentTest.FolderName, _currentTest);
        SetPhase(PowerRunPhase.Completed, $"Ensaio '{_currentTest.Name}' concluído.");
        LogEvent("TestCompleted", _statusMessage);
        return Task.CompletedTask;
    }

    public Task AbortTestAsync(string reason)
    {
        if (_currentTest is null || _phase is PowerRunPhase.Completed or PowerRunPhase.Faulted)
        {
            return Task.CompletedTask;
        }

        SetPhase(PowerRunPhase.Aborting, $"Abortando: {reason}");
        if (_currentRun is { } run && run.CurrentPhase is not (
                PowerRunPhase.Accepted or
                PowerRunPhase.Rejected or
                PowerRunPhase.Reviewing or
                PowerRunPhase.Faulted))
        {
            run.StopReason = PowerStopReason.Aborted;
            run.CurrentPhase = PowerRunPhase.Faulted;
            run.CompletedUtc = _time.GetUtcNow();
        }
        SafeParkAndRelease($"ensaio abortado: {reason}");
        _currentTest.Status = PowerTestStatus.Interrupted;
        _currentTest.InterruptionReason = reason;
        PersistCurrentRun();
        SetPhase(PowerRunPhase.Faulted, $"Ensaio interrompido: {reason}");
        LogEvent("TestAborted", reason);
        return Task.CompletedTask;
    }

    private void StartRunCore(PowerCondition condition, int replicateNumber, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var doc = _currentTest ?? throw new InvalidOperationException("Nenhum ensaio ativo.");
        ValidateCondition(condition, doc.Settings);
        if (replicateNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(replicateNumber));
        }

        _currentCondition = condition;
        condition.Status = PowerConditionStatus.InProgress;
        _runPoints.Clear();
        _speedStableCount = 0;
        _ventFlowStableCount = 0;
        _ventFlowDeviation = null;
        _pairedUngassedP0W = null;
        _pairedUngassedP0Ci95W = null;
        _isSubphase2Both = false;
        _commandedRpm = Math.Clamp(
            (int)Math.Round(condition.AgitationRpm, MidpointRounding.AwayFromZero),
            (int)doc.Settings.MinRpm,
            (int)doc.Settings.MaxRpm);

        // The assay owns the aeration loop whenever gas is part of this condition ("Both" opens
        // it in subphase 2) and also whenever the path is currently open, because shutting it
        // before P0 is the assay's job - it is what the old preflight asked the operator to do.
        var needsGas = condition.GasMode is PowerGasMode.Gassed or PowerGasMode.Both;
        var ownsGas = needsGas || (_device.Latest is { } preflight && HasOpenGasPath(preflight));
        var actualMode = condition.GasMode == PowerGasMode.Both
            ? PowerGasMode.Ungassed
            : condition.GasMode;
        var initialGasFlow = actualMode == PowerGasMode.Gassed ? condition.GasFlowLpm : null;

        _currentRun = new PowerRun
        {
            TestId = doc.TestId,
            ConditionId = condition.ConditionId,
            ReplicateNumber = replicateNumber,
            AgitationRpm = _commandedRpm,
            GasFlowLpm = initialGasFlow,
            GasMode = actualMode,
            CurrentPhase = PowerRunPhase.Preflight,
            StartedUtc = _time.GetUtcNow(),
            IsRelative = doc.Tare is null,
            Tries = 1,
        };
        _store.InitializeRunFolder(doc.FolderName, _currentRun);

        double? sigma = doc.Tare is { Points.Count: > 0 } tare
            ? TareInterpolator.InterpolateSigmaTauPercent(tare, _commandedRpm)
            : null;
        _capture = new PowerCaptureController(doc.Settings, sigma);
        _runStartMonotonic = GetMonotonicSeconds();
        RunStarted?.Invoke(_currentRun);
        SetPhase(PowerRunPhase.Preflight, $"Pré-voo da corrida {_currentRun.FolderName}.");

        var actuators = ownsGas ? GassedActuators : Phase1Actuators;
        _arbiter.Claim(CommandOwner.PowerAssay, actuators, $"Ensaio de potência: {_currentRun.FolderName}");
        if (_arbiter.OwnerOf(ActuatorId.Agitation) != CommandOwner.PowerAssay ||
            (ownsGas && _arbiter.OwnerOf(ActuatorId.Aeration) != CommandOwner.PowerAssay))
        {
            FaultWithoutSafeCommand("Falha ao obter posse dos atuadores.");
            return;
        }

        SetPhase(PowerRunPhase.PreparingCondition, "Preparando amostragem e condição do ensaio.");
        if (!Dispatch(CommandBuilders.ServoPollInterval(doc.Settings.CaptureServoPollMs), "baixar servoPollMs"))
        {
            return;
        }

        _routeCoordinator.EnsurePrimaryRoute(out var routeMsg);
        if (!_routeCoordinator.RouteRequestAccepted) { FaultWithoutSafeCommand(routeMsg); return; }
        LogEvent("MotorRoute", routeMsg);
        if (_routeCoordinator.IsUartFallback && _commandedRpm > PowerMotorRouteCoordinator.UartFallbackMaxRpm)
        {
            FaultWithoutSafeCommand(
                $"Em modo de fallback UART, a rotação máxima é de {PowerMotorRouteCoordinator.UartFallbackMaxRpm:F0} rpm. " +
                $"A condição de {_commandedRpm} rpm requer comunicação Modbus.");
            return;
        }

        if (actualMode == PowerGasMode.Gassed)
        {
            _arbiter.Claim(CommandOwner.PowerAssay, GassedActuators, $"Ensaio de potência (gás): {condition.ConditionId}");
            if (_arbiter.OwnerOf(ActuatorId.Aeration) != CommandOwner.PowerAssay)
            {
                FaultWithoutSafeCommand("Falha ao obter posse da malha de gás para Subfase 2.");
                return;
            }
            StartGassedSequence(condition.GasFlowLpm ?? 0.0);
        }
        else
        {
            if (ownsGas)
            {
                // max flow is constant MaxFlow
                // This is the close the old preflight demanded from the operator. It runs for
                // every ungassed measurement now, including the P0 subphase of a "Both" row,
                // which is exactly where the previous `needsGas` test skipped it.
                DispatchFlow(CommandBuilders.FlowSafeStop(MaxFlow), "fechar gás para medição P0");
            }
            SetPhase(PowerRunPhase.SettingSpeed, $"Aguardando a medida estabilizar em {_commandedRpm} rpm.");
            DispatchMotorOrFault(_commandedRpm, "ajustar a rotação");
        }

        _store.SaveConditionsTable(doc.FolderName, doc.Conditions);
        _store.SaveTestManifest(doc);
        LogEvent("RunStarted", $"Corrida {_currentRun.FolderName}: {_commandedRpm} rpm, modo {actualMode}, tentativa 1.");
    }

    /// <summary>
    /// Every gassed condition raises its flow on the B/C output first: the meter's start-up
    /// pulse and its settling go out of C, never into the vessel. The reactor only ever sees the
    /// one frame that closes B/C and opens A with a settled setpoint (plan §3.4). On this assay
    /// the B line carries no nitrogen — pinched or not connected — so the shared output is moot.
    /// </summary>
    private void StartGassedSequence(double targetFlow)
    {
        var doc = _currentTest!;
        _currentRun!.UsedVentStabilization = true;
        _ventFlowStableCount = 0;
        _ventFlowDeviation = null;
        _ventFlowWindow.Clear();

        var route = GasRouting.Describe(GasRoute.VentAndNitrogen, Rig);
        DispatchMotorOrFault((int)doc.Settings.PrestageAgitationRpm, "reduzir agitação durante a pré-estabilização por C");
        DispatchFlow(RouteFrame(targetFlow, GasRoute.VentAndNitrogen), "abrir a descarga (C)");
        SetPhase(
            PowerRunPhase.PrestagingFlow,
            $"Ar por C ({route}); estabilizando vazão em {targetFlow:F2} ± {doc.Settings.PrestageFlowToleranceLpm:F2} L/min.");
        LogEvent("PrestageStarted", $"Ar por C ({route}), alvo {targetFlow:F2} L/min.");
    }

    private void StartBothSubphase2()
    {
        var doc = _currentTest!;
        var condition = _currentCondition!;
        _isSubphase2Both = true;
        _runPoints.Clear();
        _speedStableCount = 0;
        _ventFlowStableCount = 0;
        _ventFlowDeviation = null;

        var replicate = _currentRun?.ReplicateNumber ?? 1;
        _currentRun = new PowerRun
        {
            TestId = doc.TestId,
            ConditionId = condition.ConditionId,
            ReplicateNumber = replicate,
            AgitationRpm = _commandedRpm,
            GasFlowLpm = condition.GasFlowLpm,
            GasMode = PowerGasMode.Gassed,
            CurrentPhase = PowerRunPhase.Preflight,
            StartedUtc = _time.GetUtcNow(),
            IsRelative = doc.Tare is null,
            Tries = 1,
            ReferenceP0W = _pairedUngassedP0W,
            ReferenceP0Ci95W = _pairedUngassedP0Ci95W,
            P0Provenance = P0Provenance.MeasuredUngassed,
        };
        _store.InitializeRunFolder(doc.FolderName, _currentRun);

        double? sigma = doc.Tare is { Points.Count: > 0 } tare
            ? TareInterpolator.InterpolateSigmaTauPercent(tare, _commandedRpm)
            : null;
        _arbiter.Claim(CommandOwner.PowerAssay, GassedActuators, $"Ensaio de potência (gás): {condition.ConditionId}");
        if (_arbiter.OwnerOf(ActuatorId.Aeration) != CommandOwner.PowerAssay)
        {
            FaultWithoutSafeCommand("Falha ao obter posse da malha de gás para Subfase 2.");
            return;
        }

        _capture = new PowerCaptureController(doc.Settings, sigma);
        _runStartMonotonic = GetMonotonicSeconds();
        RunStarted?.Invoke(_currentRun);
        StartGassedSequence(condition.GasFlowLpm ?? 0.0);
    }

    private bool DispatchFlow(OpenTECCommand command, string action)
    {
        if (_device.Latest is { } latest)
        {
            _lastFlowCommandId = latest.FlowCommandId;
            _minimumExpectedFlowCommandId = latest.FlowCommandId + 1;
        }
        return Dispatch(command, action);
    }

    private bool IsGasStateConfirmed(SensorSnapshot s, (double Flow, bool V1, bool V2, bool VFlow)? target)
    {
        if (target is null)
        {
            return true;
        }
        var expected = target.Value;
        var flowOk = Math.Abs(s.FlowSetpoint - expected.Flow) < 0.1;
        var v1Ok = (s.FlowValve1 != 0) == expected.V1;
        var v2Ok = (s.FlowValve2 != 0) == expected.V2;
        var vFlowOk = (s.FlowValveMain != 0) == expected.VFlow;
        var ackOk = s.FlowmeterOnline && !s.FlowCommandPending &&
                    s.FlowCommandId >= _minimumExpectedFlowCommandId &&
                    s.FlowCommandAck == s.FlowCommandId;

        return flowOk && v1Ok && v2Ok && vFlowOk && ackOk;
    }

    /// <summary>
    /// One telemetry frame raises <see cref="StateChanged"/> <em>at most once</em>, after every
    /// mutation of the frame. A frame used to raise it up to three times (a <c>SetPhase</c>, a
    /// <c>DataPointAdded</c> re-entering the page, and the explicit raise at the end), and each
    /// raise cost the page a full refresh on the UI thread. Raises outside a frame — operator
    /// commands, the watchdog — are immediate as before.
    /// </summary>
    private void OnTelemetryReceived(SensorSnapshot snapshot)
    {
        _inTelemetryFrame = true;
        _stateChangePending = false;
        try
        {
            ProcessTelemetryFrame(snapshot);
        }
        finally
        {
            _inTelemetryFrame = false;
            if (_stateChangePending)
            {
                _stateChangePending = false;
                StateChanged?.Invoke();
            }
        }
    }

    private bool _inTelemetryFrame;
    private bool _stateChangePending;

    private void ProcessTelemetryFrame(SensorSnapshot snapshot)
    {
        var now = GetMonotonicSeconds();
        _lastTelemetryMonotonic = now;
        if (!HasValidServoMeasurement(snapshot))
        {
            if (RequiresServoMeasurement(_phase))
            {
                PauseForMeasurement("A medida do servo ficou ausente, offline ou desabilitada no Hub.");
            }
            return;
        }

        _lastValidServoMonotonic = now;
        _currentRpm = snapshot.ServoRpm;
        _currentTorquePercent = snapshot.ServoTorquePct;
        RaiseStateChanged();

        if (_currentTest is null)
        {
            return;
        }

        if (RequiresServoMeasurement(_phase) &&
            (Math.Abs(snapshot.ServoTorquePct) > _currentTest.Settings.MaxTorquePercent ||
             snapshot.ServoRpm > _currentTest.Settings.MaxRpm + _currentTest.Settings.SpeedToleranceRpm))
        {
            StopForReview("Limite de torque ou rotação excedido.", PowerStopReason.Aborted);
            return;
        }

        if (_phase == PowerRunPhase.PausedForMeasurement)
        {
            // An unattended assay must survive a telemetry blip on its own - a flowmeter that
            // drops for a few seconds is the common case here. The partial window was already
            // discarded when the pause began, so resuming just restarts this point cleanly.
            if (_currentTest.Settings.AutoAcceptRuns && TryResumeAfterMeasurement())
            {
                return;
            }
            _statusMessage = "Medida restabelecida. Confirme a retomada para reiniciar a captura.";
            RaiseStateChanged();
            return;
        }

        if (_phase == PowerRunPhase.PausedByOperator)
        {
            if (_currentRun is not null)
            {
                AppendSample(snapshot, now, counted: false);
            }
            RaiseStateChanged();
            return;
        }

        if (_phase == PowerRunPhase.PreparingNextRun)
        {
            AppendGlobalSample(snapshot, now, counted: false);
            if (SpeedIsConfirmed(snapshot.ServoRpm, (int)_currentTest.Settings.MinRpm))
            {
                var next = NextPendingCondition();
                if (next is null)
                {
                    CompleteTestAsync().GetAwaiter().GetResult();
                }
                else
                {
                    StartRunCore(next, Math.Max(1, next.CompletedReplicates + 1), CancellationToken.None);
                }
            }
            return;
        }

        if (_currentRun is null || _currentCondition is null)
        {
            RaiseStateChanged();
            return;
        }

        if (RequiresGasMeasurement(_phase) && (!snapshot.FlowmeterOnline || !double.IsFinite(snapshot.FlowRate)))
        {
            PauseForMeasurement("A telemetria do fluxômetro ficou ausente ou offline.");
            return;
        }

        if (_phase == PowerRunPhase.PrestagingFlow)
        {
            AppendSample(snapshot, now, counted: false);
            if (!IsGasStateConfirmed(snapshot, _targetGasState))
            {
                if (PhaseElapsedSeconds >= 10.0)
                {
                    StopForReview("Tempo limite de confirmação da saída B/C (descarga) excedido.", PowerStopReason.Tmax, sequenceFailure: true);
                }
                return;
            }

            var targetFlow = _targetGasState!.Value.Flow;
            var dev = snapshot.FlowRate - targetFlow;
            _ventFlowDeviation = dev;
            var settings = _currentTest!.Settings;
            if (Math.Abs(dev) <= settings.PrestageFlowToleranceLpm)
            {
                _ventFlowStableCount++;
            }
            else
            {
                _ventFlowStableCount = 0;
            }

            // Second way out: the flow has settled (low spread over the last N readings) close to
            // the target, even if it sits just outside the tolerance band — the bench controller's
            // steady offset. The reactor phase measures the flow again anyway.
            FlowSettling.Push(_ventFlowWindow, snapshot.FlowRate, settings.PrestageFlowStableSamples);
            var settled = PrestageFlowHasSettled(_ventFlowWindow, targetFlow, settings, out var spread);

            var offsetText = dev >= 0 ? $"+{dev:F2}" : $"{dev:F2}";
            _statusMessage = $"Ar por C · {snapshot.FlowRate:F2} L/min " +
                $"(alvo {targetFlow:F2} ± {settings.PrestageFlowToleranceLpm:F2}) · " +
                $"estabilidade {_ventFlowStableCount}/{settings.PrestageFlowStableSamples} · " +
                $"estabilizando há {PhaseElapsedSeconds:F0} s · offset {offsetText} · σ {spread:F3}";

            if (_ventFlowStableCount >= settings.PrestageFlowStableSamples || settled)
            {
                var how = _ventFlowStableCount >= settings.PrestageFlowStableSamples ? "dentro da banda" : $"estável (σ {spread:F3} L/min, offset {offsetText} L/min)";
                LogEvent("PrestageFlowStable", $"Vazão estabilizada em {snapshot.FlowRate:F2} L/min por C ({how}) após {PhaseElapsedSeconds:F0} s. Comutando para o reator.");
                // One frame: B/C close and A opens in the same JSON (plan §3.4).
                DispatchFlow(RouteFrame(targetFlow, GasRoute.Reactor), "comutar fluxo ao reator (A)");
                SetPhase(PowerRunPhase.OpeningGas, "Fechando B/C e abrindo A: vazão ao reator...");
            }
            else if (PhaseElapsedSeconds >= settings.MaxPrestageSeconds)
            {
                StopForReview("Tempo limite da pré-estabilização por C excedido.", PowerStopReason.Tmax, sequenceFailure: true);
            }
            return;
        }

        if (_phase == PowerRunPhase.OpeningGas)
        {
            AppendSample(snapshot, now, counted: false);
            if (IsGasStateConfirmed(snapshot, _targetGasState))
            {
                _speedStableCount = 0;
                SetPhase(PowerRunPhase.SettingSpeed, $"Gás estabelecido no reator; aguardando rotação estabilizar em {_commandedRpm} rpm.");
                DispatchMotorOrFault(_commandedRpm, "ajustar rotação para medição");
            }
            else if (PhaseElapsedSeconds >= 10.0)
            {
                StopForReview("Tempo limite de confirmação da válvula do reator excedido.", PowerStopReason.Tmax, sequenceFailure: true);
            }
            return;
        }

        if (_phase == PowerRunPhase.SettingSpeed)
        {
            AppendSample(snapshot, now, counted: false);
            if (SpeedIsConfirmed(snapshot.ServoRpm, _commandedRpm))
            {
                _capture?.Reset();
                SetPhase(PowerRunPhase.SettlingTorque, "Rotação medida confirmada; aguardando estacionariedade do torque.");
            }
            else if (PhaseElapsedSeconds >= _currentTest.Settings.MaxSpeedSettlingSeconds)
            {
                var timeoutReason = _routeCoordinator.IsUartFallback && _commandedRpm > PowerMotorRouteCoordinator.UartFallbackMaxRpm
                    ? $"A rotação medida não alcançou {_commandedRpm} rpm: no modo de fallback UART a rotação máxima é de {PowerMotorRouteCoordinator.UartFallbackMaxRpm:F0} rpm."
                    : "A rotação medida não entrou na banda dentro do tempo limite.";
                StopForReview(timeoutReason, PowerStopReason.Tmax, sequenceFailure: true);
            }
            return;
        }

        if (_phase is PowerRunPhase.SettlingTorque or PowerRunPhase.AccumulatingToTarget)
        {
            var counted = _phase == PowerRunPhase.AccumulatingToTarget;
            var priorCaptureState = _capture!.State;
            _capture.Add(now, snapshot.ServoTorquePct, snapshot.ServoRpm);
            AppendSample(snapshot, now, counted);

            if (priorCaptureState == CaptureState.Settling && _capture.State == CaptureState.Accumulating)
            {
                SetPhase(PowerRunPhase.AccumulatingToTarget, "Torque estacionário; acumulando até o alvo de IC95.");
            }

            if (_capture.IsDone)
            {
                HandleCaptureStopped();
            }
            else
            {
                RaiseStateChanged();
            }
            return;
        }

        if (_phase == PowerRunPhase.HoldingForManualEnergy)
        {
            AppendSample(snapshot, now, counted: false);
            RaiseStateChanged();
        }
    }

    private void HandleCaptureStopped()
    {
        if (_capture is null || _currentRun is null || _currentTest is null || _currentCondition is null)
        {
            return;
        }

        if (_capture.State == CaptureState.TimedOut && _currentRun.Tries < _currentTest.Settings.MaxTries)
        {
            _currentRun.Tries++;
            _capture.Reset();
            SetPhase(
                PowerRunPhase.SettlingTorque,
                $"Precisão não atingida; recaptura {_currentRun.Tries}/{_currentTest.Settings.MaxTries} no mesmo ponto.");
            LogEvent("CaptureRetry", $"Reinício das duas portas, tentativa {_currentRun.Tries}.");
            return;
        }

        var stopReason = _capture.State == CaptureState.Converged
            ? PowerStopReason.Target
            : PowerStopReason.NotConverged;
        CaptureResultIntoRun(stopReason);

        if (_currentCondition.GasMode == PowerGasMode.Both && !_isSubphase2Both)
        {
            _pairedUngassedP0W = _currentRun.NetPowerW;
            _pairedUngassedP0Ci95W = _currentRun.Ci95PowerW;
            _currentRun.CurrentPhase = PowerRunPhase.Captured;
            PersistCurrentRun();
            LogEvent("Subphase1P0Captured",
                $"Subfase 1 (P0) concluída: P0={_currentRun.NetPowerW:F3} W. Iniciando Subfase 2 (PG).");

            StartBothSubphase2();
            return;
        }

        if (_currentTest.Settings.ManualEnergyCaptureEnabled)
        {
            _currentRun.CurrentPhase = PowerRunPhase.HoldingForManualEnergy;
            PersistCurrentRun();
            SetPhase(PowerRunPhase.HoldingForManualEnergy, "Condição mantida; informe a leitura do wattímetro.");
            return;
        }

        FinishCapturedRun();
    }

    private void CaptureResultIntoRun(PowerStopReason stopReason)
    {
        var doc = _currentTest!;
        var run = _currentRun!;
        var captureResult = _capture!.Result(stopReason);
        var result = _analysis.AnalyzePoint(new PowerPointInput
        {
            MeanTorquePercent = captureResult.MeanTorquePercent,
            TorquePercentCi95 = captureResult.TorqueCi95Percent,
            MeanRpm = captureResult.MeanRpm,
            Fluid = doc.Fluid,
            Geometry = doc.Geometry,
            Calibration = doc.Calibration,
            Tare = doc.Tare,
            SnrFloorMultiple = doc.Settings.SnrFloorMultiple,
            MotorRatedTorqueNm = doc.MotorRatedTorqueNm,
        });

        run.SampleCount = captureResult.SampleCount;
        run.MeanRpmMeasured = captureResult.MeanRpm;
        run.MeanTorquePercent = captureResult.MeanTorquePercent;
        run.MeanTorqueNm = result.MeanTorqueNm;
        run.MeanShaftPowerW = result.ShaftPowerW;
        run.NetPowerW = result.NetPowerW;
        run.TorqueCi95Percent = captureResult.TorqueCi95Percent;
        run.Ci95PowerW = result.NetPowerCi95W;
        run.Analysis = result;
        run.StopReason = stopReason;
        run.IsRelative = result.IsRelative;
        run.CompletedUtc = _time.GetUtcNow();

        if (run.GasMode == PowerGasMode.Gassed && run.GasFlowLpm is { } flowLpm)
        {
            run.GassedPowerW = run.NetPowerW;
            if (doc.Geometry.LiquidVolumeM3 > 0)
            {
                run.GasFlowVvm = PowerCalc.LpmToVvm(flowLpm, doc.Geometry.LiquidVolumeM3);
            }
            if (doc.Geometry.Impellers.Count > 0)
            {
                var dRef = doc.Geometry.Impellers[0].DiameterM;
                if (dRef > 0 && run.MeanRpmMeasured > 0)
                {
                    run.GasFlowNumber = PowerCalc.AerationNumber(flowLpm, run.MeanRpmMeasured, dRef);
                    run.FroudeNumber = PowerCalc.FroudeNumber(run.MeanRpmMeasured, dRef);
                }
            }

            if (run.ReferenceP0W is null)
            {
                var (p0, p0Ci, prov) = _analysis.ResolveReferenceP0(run.MeanRpmMeasured, doc);
                run.ReferenceP0W = p0;
                run.ReferenceP0Ci95W = p0Ci;
                run.P0Provenance = prov;
            }

            if (run.ReferenceP0W is { } refP0 && refP0 > 0)
            {
                var (ratio, ratioCi) = PowerCalc.PropagatePowerRatioUncertainty(
                    run.NetPowerW,
                    run.Ci95PowerW,
                    refP0,
                    run.ReferenceP0Ci95W ?? 0.0);
                run.PowerRatio = ratio;
                run.PowerRatioCi95 = ratioCi;
            }
        }
    }

    private void FinishCapturedRun()
    {
        var run = _currentRun!;
        run.CurrentPhase = PowerRunPhase.Captured;
        PersistCurrentRun();
        SetPhase(
            PowerRunPhase.Captured,
            run.StopReason == PowerStopReason.Target
                ? "Ponto capturado pelo alvo de confiança."
                : "Ponto capturado como melhor esforço; precisão não atingida.");
        LogEvent("RunCaptured", $"n={run.SampleCount}; IC95 torque={run.TorqueCi95Percent:F4}%; {run.StopReason}.");

        if (_currentTest!.Settings.AutoAcceptRuns)
        {
            AcceptRunCore(autoAdvance: true);
            return;
        }

        SafeParkAndRelease("corrida aguardando revisão");
        run.CurrentPhase = PowerRunPhase.Reviewing;
        PersistCurrentRun();
        SetPhase(PowerRunPhase.Reviewing, "Ponto capturado. Revise e aceite, rejeite ou repita.");
    }

    private void AcceptRunCore(bool autoAdvance)
    {
        var run = _currentRun!;
        var condition = _currentCondition!;
        if (!HasCapture(run))
        {
            throw new InvalidOperationException(NoCaptureMessage);
        }
        run.CurrentPhase = PowerRunPhase.Accepted;
        run.CompletedUtc ??= _time.GetUtcNow();
        condition.CompletedReplicates++;
        condition.AcceptedReplicates++;
        condition.Status = condition.AcceptedReplicates >= condition.RequestedReplicates
            ? PowerConditionStatus.Completed
            : PowerConditionStatus.Pending;

        UpsertCurrentRunSummary(PowerRunPhase.Accepted);
        UpdateReplicateAgreement(condition);
        _sequenceFailures.Remove(condition.ConditionId);
        PersistCurrentRun();
        SetPhase(PowerRunPhase.Accepted, "Corrida aceita.");
        LogEvent("RunAccepted", $"Corrida {run.FolderName} aceita.");

        if (!autoAdvance)
        {
            SafeParkAndRelease("corrida aceita");
            return;
        }

        var next = NextPendingCondition();
        if (next is null)
        {
            CompleteTestAsync().GetAwaiter().GetResult();
            return;
        }

        _speedStableCount = 0;
        DispatchMotorOrFault((int)_currentTest!.Settings.MinRpm, "reaproximar para a próxima réplica");
        if (_phase == PowerRunPhase.Faulted)
        {
            return;
        }
        SetPhase(PowerRunPhase.PreparingNextRun, "Retornando à rotação mínima antes da próxima réplica.");
    }

    private void StopForReview(string reason, PowerStopReason stopReason, bool sequenceFailure = false)
    {
        if (_currentRun is null)
        {
            return;
        }
        if (_capture is { SampleCount: > 0 })
        {
            CaptureResultIntoRun(stopReason);
        }
        else
        {
            _currentRun.StopReason = stopReason;
            _currentRun.CompletedUtc = _time.GetUtcNow();
        }

        if (sequenceFailure && _currentTest is { } doc && _currentCondition is { } condition &&
            doc.Settings.AutoAcceptRuns && doc.Settings.UnattendedFailurePolicy == UnattendedFailurePolicy.RetryThenSkip)
        {
            HandleUnattendedSequenceFailure(reason, condition);
            return;
        }

        SafeParkAndRelease(reason);
        _currentRun.CurrentPhase = PowerRunPhase.Reviewing;
        PersistCurrentRun();
        SetPhase(PowerRunPhase.Reviewing, reason);
        LogEvent("RunStoppedForReview", reason);
    }

    /// <summary>
    /// <see cref="UnattendedFailurePolicy.RetryThenSkip"/>: the failed run is rejected with its
    /// reason, the condition gets one more try, and a second failure marks it skipped so the
    /// sequence moves on. Nothing here accepts anything (D-050). Bench of 2026-09-11: one vent
    /// time-out on the first gassed condition parked a 9 h unattended assay for the night.
    /// </summary>
    private void HandleUnattendedSequenceFailure(string reason, PowerCondition condition)
    {
        var run = _currentRun!;
        var failures = _sequenceFailures.GetValueOrDefault(condition.ConditionId) + 1;
        _sequenceFailures[condition.ConditionId] = failures;

        run.CurrentPhase = PowerRunPhase.Rejected;
        condition.CompletedReplicates++;
        condition.RejectedReplicates++;
        UpsertCurrentRunSummary(PowerRunPhase.Rejected);
        // Gas off, but ownership kept: the assay goes on, as after an auto-accepted run.
        ParkGasKeepingOwnership();

        if (failures < 2)
        {
            condition.Status = PowerConditionStatus.Pending;
            PersistCurrentRun();
            SetPhase(PowerRunPhase.Rejected, $"{reason} Repetindo a condição uma vez (modo autônomo).");
            LogEvent("RunRejected", $"{reason} Falha de sequência em modo autônomo; a condição será repetida uma vez.");
        }
        else
        {
            condition.Status = PowerConditionStatus.Skipped;
            PersistCurrentRun();
            SetPhase(PowerRunPhase.Rejected, $"{reason} Condição pulada após duas falhas de sequência (modo autônomo).");
            LogEvent("RunRejected", $"{reason} Segunda falha de sequência em modo autônomo.");
            LogEvent("ConditionSkipped", $"Condição {condition.AgitationRpm:F0} rpm / {condition.GasFlowLpm ?? 0:F2} L/min pulada: {reason}");
        }

        AdvanceUnattended();
    }

    private void ParkGasKeepingOwnership()
    {
        if (_arbiter.OwnerOf(ActuatorId.Aeration) == CommandOwner.PowerAssay)
        {
            _arbiter.Dispatch(CommandOwner.PowerAssay, CommandBuilders.FlowSafeStop(MaxFlow));
        }
    }

    /// <summary>Returns to the minimum speed and lets the next frame start the next pending condition, or completes the assay.</summary>
    private void AdvanceUnattended()
    {
        if (NextPendingCondition() is null)
        {
            CompleteTestAsync().GetAwaiter().GetResult();
            return;
        }

        _speedStableCount = 0;
        DispatchMotorOrFault((int)_currentTest!.Settings.MinRpm, "reaproximar para a próxima condição");
        if (_phase == PowerRunPhase.Faulted)
        {
            return;
        }
        SetPhase(PowerRunPhase.PreparingNextRun, "Retornando à rotação mínima antes da próxima condição.");
    }

    /// <summary>
    /// The vent flow has settled when the spread of the last window is small and the mean sits
    /// within the stability error of the target — <see cref="FlowSettling.HasSettled"/>, shared
    /// with the kLa runner's pre-staging on C.
    /// </summary>
    internal static bool PrestageFlowHasSettled(IReadOnlyList<double> window, double targetFlow, PowerTestSettings settings, out double standardDeviation)
        => FlowSettling.HasSettled(
            window,
            targetFlow,
            settings.PrestageFlowStableSamples,
            settings.PrestageFlowStabilityStdDevLpm,
            settings.PrestageFlowStabilityMaxErrorLpm,
            out standardDeviation);

    private void PauseForMeasurement(string reason)
    {
        if (_phase == PowerRunPhase.PausedForMeasurement || _currentRun is null)
        {
            return;
        }
        _capture?.Reset();
        _speedStableCount = 0;
        _currentRun.CurrentPhase = PowerRunPhase.PausedForMeasurement;
        PersistCurrentRun();
        SetPhase(PowerRunPhase.PausedForMeasurement, reason + " A média corrente foi descartada.");
        LogEvent("MeasurementLost", reason);
    }

    private void AppendSample(SensorSnapshot snapshot, double now, bool counted)
    {
        var doc = _currentTest!;
        var run = _currentRun!;
        var torqueNm = CalibratedTorqueNm(snapshot.ServoTorquePct, doc);
        var shaftPowerW = PowerCalc.ShaftPower(torqueNm, snapshot.ServoRpm);
        var point = new PowerDataPoint(
            _time.GetUtcNow(),
            Math.Max(0, now - _runStartMonotonic),
            _phase,
            snapshot.ServoRpm,
            snapshot.ServoTorquePct,
            torqueNm,
            shaftPowerW,
            OptionalReading(snapshot.FlowRate),
            counted,
            run.Tries);
        _runPoints.Add(point);
        _store.AppendRunRawDataPoint(doc.FolderName, run.FolderName, point);
        AppendGlobalSample(snapshot, now, counted);
        DataPointAdded?.Invoke(point);
    }

    private void AppendGlobalSample(SensorSnapshot snapshot, double now, bool counted)
    {
        var doc = _currentTest!;
        var torqueNm = CalibratedTorqueNm(snapshot.ServoTorquePct, doc);
        double? meanPower = _capture is { SampleCount: > 0 }
            ? PowerCalc.ShaftPower(CalibratedTorqueNm(_capture.CurrentMeanTorquePercent, doc), _capture.CurrentMeanRpm)
            : null;
        double? ciTorqueNm = _capture is { SampleCount: > 0 }
            ? CalibratedTorqueCiNm(_capture.CurrentMeanTorquePercent, _capture.CurrentTorqueCi95Percent, doc)
            : null;
        double? ciPower = ciTorqueNm is { } ci
            ? Math.Abs(PowerCalc.ShaftPower(ci, _capture!.CurrentMeanRpm))
            : null;

        var sample = new PowerGlobalSeriesSample(
            _time.GetUtcNow(),
            Math.Max(0, now - _testStartMonotonic),
            doc.TestId,
            _currentRun?.RunId,
            _currentCondition?.ConditionId,
            _currentRun?.ReplicateNumber,
            _phase,
            snapshot.ServoRpm,
            snapshot.ServoTorquePct,
            torqueNm,
            PowerCalc.ShaftPower(torqueNm, snapshot.ServoRpm),
            OptionalReading(snapshot.FlowRate),
            OptionalReading(snapshot.Temperature),
            meanPower,
            ciPower,
            _capture?.SampleCount ?? 0,
            doc.SettingsRevision,
            counted ? "Counted" : "Observed",
            _statusMessage,
            _currentRun?.Tries ?? 1);
        _globalSamples.Add(sample);
        _store.AppendGlobalSeriesSample(doc.FolderName, sample);
    }

    private bool SpeedIsConfirmed(double measuredRpm, int targetRpm)
    {
        if (Math.Abs(measuredRpm - targetRpm) <= _currentTest!.Settings.SpeedToleranceRpm)
        {
            _speedStableCount++;
        }
        else
        {
            _speedStableCount = 0;
        }
        return _speedStableCount >= _currentTest.Settings.SpeedStableSamples;
    }

    private void UpdateReplicateAgreement(PowerCondition condition)
    {
        var accepted = _currentTest!.Runs
            .Where(r => r.ConditionId == condition.ConditionId && r.Phase == PowerRunPhase.Accepted)
            .ToArray();
        condition.HasReplicateDisagreement = false;
        condition.ReproducibilityWarning = null;
        for (var i = 0; i < accepted.Length; i++)
        {
            for (var j = i + 1; j < accepted.Length; j++)
            {
                var difference = Math.Abs(accepted[i].MeanTorquePercent - accepted[j].MeanTorquePercent);
                var nonOverlap = (accepted[i].TorqueCi95Percent ?? 0) + (accepted[j].TorqueCi95Percent ?? 0);
                if (difference > nonOverlap)
                {
                    condition.HasReplicateDisagreement = true;
                    condition.ReproducibilityWarning =
                        "As médias das réplicas diferem mais que a soma dos IC95 individuais; verifique efeito de partida.";
                    return;
                }
            }
        }
    }

    private void UpsertCurrentRunSummary(PowerRunPhase phase)
    {
        var run = _currentRun!;
        var doc = _currentTest!;
        doc.Runs.RemoveAll(item => item.RunId == run.RunId);
        doc.Runs.Add(new PowerRunSummary
        {
            RunId = run.RunId,
            ConditionId = run.ConditionId,
            ReplicateNumber = run.ReplicateNumber,
            FolderName = run.FolderName,
            AgitationRpm = run.AgitationRpm,
            GasFlowLpm = run.GasFlowLpm,
            GasMode = run.GasMode,
            Phase = phase,
            StopReason = run.StopReason,
            SampleCount = run.SampleCount,
            MeanRpmMeasured = run.MeanRpmMeasured,
            MeanTorquePercent = run.MeanTorquePercent,
            MeanTorqueNm = run.MeanTorqueNm,
            MeanShaftPowerW = run.MeanShaftPowerW,
            NetPowerW = run.NetPowerW,
            TorqueCi95Percent = run.TorqueCi95Percent,
            Ci95PowerW = run.Ci95PowerW,
            Analysis = run.Analysis,
            Tries = run.Tries,
            IsRelative = run.IsRelative,
            StartedUtc = run.StartedUtc,
            CompletedUtc = run.CompletedUtc,
            ManualElec = run.ManualElec,
            GasFlowVvm = run.GasFlowVvm,
            GasFlowNumber = run.GasFlowNumber,
            FroudeNumber = run.FroudeNumber,
            GassedPowerW = run.GassedPowerW,
            ReferenceP0W = run.ReferenceP0W,
            ReferenceP0Ci95W = run.ReferenceP0Ci95W,
            P0Provenance = run.P0Provenance,
            PowerRatio = run.PowerRatio,
            PowerRatioCi95 = run.PowerRatioCi95,
            UsedVentStabilization = run.UsedVentStabilization,
        });
    }

    private void PersistCurrentRun()
    {
        if (_currentTest is null)
        {
            return;
        }
        if (_currentRun is { } run)
        {
            UpsertCurrentRunSummary(run.CurrentPhase);
            _store.SaveRunResult(_currentTest.FolderName, run);
        }
        _store.SaveConditionsTable(_currentTest.FolderName, _currentTest.Conditions);
        _store.SaveTestManifest(_currentTest);
        _store.UpdateResultsSummary(_currentTest.FolderName, _currentTest);
    }

    private void SafeParkAndRelease(string reason)
    {
        if (_currentTest is { } doc)
        {
            if (_arbiter.OwnerOf(ActuatorId.Agitation) == CommandOwner.PowerAssay)
            {
                _arbiter.Dispatch(
                    CommandOwner.PowerAssay,
                    CommandBuilders.MotorSetpoint((int)doc.Settings.MinRpm));
            }
            if (_arbiter.OwnerOf(ActuatorId.Aeration) == CommandOwner.PowerAssay)
            {
                // max flow is constant MaxFlow
                _arbiter.Dispatch(
                    CommandOwner.PowerAssay,
                    CommandBuilders.FlowSafeStop(MaxFlow));
            }
            _arbiter.Dispatch(
                CommandOwner.PowerAssay,
                CommandBuilders.ServoPollInterval(doc.Settings.RestoreServoPollMs));
        }
        _arbiter.Release(CommandOwner.PowerAssay, reason);
    }

    private bool DispatchMotorOrFault(int rpm, string action)
    {
        if (_routeCoordinator.IsUartFallback && rpm > PowerMotorRouteCoordinator.UartFallbackMaxRpm)
        {
            FaultWithoutSafeCommand(
                $"Em modo de fallback UART, a rotação máxima é de {PowerMotorRouteCoordinator.UartFallbackMaxRpm:F0} rpm. " +
                $"Comando de {rpm} rpm ({action}) requer comunicação Modbus.");
            return false;
        }
        return Dispatch(CommandBuilders.MotorSetpoint(Math.Max((int)_currentTest!.Settings.MinRpm, rpm)), action);
    }

    private bool Dispatch(OpenTECCommand command, string action)
    {
        var result = _arbiter.Dispatch(CommandOwner.PowerAssay, command);
        if (result.Accepted)
        {
            return true;
        }
        FaultWithoutSafeCommand(
            $"Comando recusado ao tentar {action}: {string.Join(", ", result.Refused)} " +
            $"(AgOwner={_arbiter.OwnerOf(ActuatorId.Agitation)}, GasOwner={_arbiter.OwnerOf(ActuatorId.Aeration)}).");
        return false;
    }

    private void FaultWithoutSafeCommand(string reason)
    {
        if (_phase == PowerRunPhase.Faulted)
        {
            return;
        }
        if (_currentRun is { } run)
        {
            run.StopReason = PowerStopReason.Aborted;
            run.CurrentPhase = PowerRunPhase.Faulted;
            run.CompletedUtc = _time.GetUtcNow();
        }
        if (_currentTest is { } doc)
        {
            doc.Status = PowerTestStatus.Interrupted;
            doc.InterruptionReason = reason;
            PersistCurrentRun();
        }
        _arbiter.Release(CommandOwner.PowerAssay, reason);
        SetPhase(PowerRunPhase.Faulted, reason);
        LogEvent("RunnerFaulted", reason);
    }

    private void CheckWatchdog()
    {
        if (_currentTest is null)
        {
            return;
        }
        if (RequiresServoMeasurement(_phase))
        {
            var last = Math.Max(_lastTelemetryMonotonic, _lastValidServoMonotonic);
            if (last > 0 && GetMonotonicSeconds() - last > _currentTest.Settings.MeasurementTimeoutSeconds)
            {
                PauseForMeasurement("A telemetria do servo ficou desatualizada.");
            }
        }
        if (RequiresGasMeasurement(_phase) && _device.Latest is { } latest)
        {
            if (!latest.FlowmeterOnline)
            {
                PauseForMeasurement("A telemetria do fluxômetro ficou offline.");
            }
        }
    }

    private void OnConnectionStateChanged(ConnectionStateChange change)
    {
        if (change.State != ConnectionState.Connected && IsRunning)
        {
            FaultWithoutSafeCommand("A conexão com o Hub foi perdida durante o ensaio.");
        }
    }

    private void OnOwnershipRevoked(OwnershipTransfer transfer)
    {
        if ((transfer.Actuators.Contains(ActuatorId.Agitation) ||
             transfer.Actuators.Contains(ActuatorId.Aeration)) &&
            _currentTest?.Status == PowerTestStatus.Running)
        {
            FaultWithoutSafeCommand("A posse dos atuadores foi revogada pelo aborto seguro do link.");
        }
    }

    private PowerCondition? NextPendingCondition() => NextPendingConditionOf(_currentTest);

    private static PowerCondition? NextPendingConditionOf(PowerTestDocument? doc) => doc?.Conditions
        .Where(condition => condition.Status != PowerConditionStatus.Skipped &&
                            condition.AcceptedReplicates < condition.RequestedReplicates)
        .OrderBy(condition => condition.OrderIndex)
        .ThenBy(condition => condition.AgitationRpm)
        .FirstOrDefault();

    private void SetPhase(PowerRunPhase phase, string message)
    {
        _phase = phase;
        _phaseStartMonotonic = GetMonotonicSeconds();
        _statusMessage = message;
        if (_currentRun is { } run && phase is not (
                PowerRunPhase.Idle or
                PowerRunPhase.PreparingNextRun or
                PowerRunPhase.Completed or
                PowerRunPhase.Aborting or
                PowerRunPhase.Faulted))
        {
            run.CurrentPhase = phase;
        }
        RaiseStateChanged();
    }

    private void LogEvent(string eventType, string message)
    {
        if (_currentTest is { FolderName.Length: > 0 } doc)
        {
            _store.AppendEventLog(
                doc.FolderName,
                new PowerTestEventLogEntry(_time.GetUtcNow(), eventType, message));
        }
        Logged?.Invoke(message);
    }

    private static bool HasValidServoMeasurement(SensorSnapshot snapshot) =>
        snapshot.HasServoTelemetry &&
        snapshot.HasServoSample &&
        snapshot.ServoOnline &&
        snapshot.ServoCommEnabled == true &&
        double.IsFinite(snapshot.ServoRpm) &&
        double.IsFinite(snapshot.ServoTorquePct);

    /// <summary>
    /// True when the flowmeter reports a path that is commanded open, i.e. one the assay has
    /// to shut before P0 is meaningful.
    /// </summary>
    /// <remarks>
    /// The measured rate is deliberately NOT evidence here. A fully closed path still reads a
    /// small residual on the sensor - and the reading also decays over seconds after a close -
    /// so counting it made a start refusal that no operator action could clear. What the valves
    /// and the echoed setpoint say is commanded state, which is the question actually being asked.
    /// </remarks>
    private static bool HasOpenGasPath(SensorSnapshot snapshot)
    {
        const double flowToleranceLpm = 0.1;
        var setpointActive = double.IsFinite(snapshot.FlowSetpoint) &&
                             snapshot.FlowSetpoint > SensorReadings.NotReceived &&
                             snapshot.FlowSetpoint > flowToleranceLpm;
        var routedOpen = snapshot.FlowValveMain == 0 &&
                         (snapshot.FlowValve1 == 1 || snapshot.FlowValve2 == 1);
        return setpointActive || routedOpen;
    }

    private static bool RequiresServoMeasurement(PowerRunPhase phase) => phase is
        PowerRunPhase.SettingSpeed or
        PowerRunPhase.SettlingTorque or
        PowerRunPhase.AccumulatingToTarget or
        PowerRunPhase.PausedByOperator or
        PowerRunPhase.HoldingForManualEnergy;

    private bool RequiresGasMeasurement(PowerRunPhase phase) =>
        (_currentRun?.GasMode == PowerGasMode.Gassed || phase is PowerRunPhase.PrestagingFlow or PowerRunPhase.OpeningGas) &&
        phase is PowerRunPhase.PrestagingFlow or
                 PowerRunPhase.OpeningGas or
                 PowerRunPhase.SettlingTorque or
                 PowerRunPhase.AccumulatingToTarget or
                 PowerRunPhase.HoldingForManualEnergy;

    private static double? OptionalReading(double value) =>
        double.IsFinite(value) && value > SensorReadings.NotReceived ? value : null;

    private static double CalibratedTorqueNm(double torquePercent, PowerTestDocument doc) =>
        torquePercent / 100.0 * doc.MotorRatedTorqueNm;

    private static double CalibratedTorqueCiNm(
        double meanTorquePercent,
        double torqueCi95Percent,
        PowerTestDocument doc) => Math.Abs(
        CalibratedTorqueNm(meanTorquePercent + torqueCi95Percent, doc) -
        CalibratedTorqueNm(meanTorquePercent, doc));

    private static void ValidateDocument(PowerTestDocument doc)
    {
        if (string.IsNullOrWhiteSpace(doc.FolderName))
        {
            throw new InvalidOperationException("O ensaio precisa estar salvo antes de iniciar.");
        }
        if (doc.Geometry is null || doc.Fluid is null || doc.Settings is null || doc.Conditions is null)
        {
            throw new InvalidOperationException("O documento do ensaio está incompleto ou corrompido.");
        }
        if (doc.Geometry.Impellers is null || doc.Geometry.Impellers.Count == 0 ||
            doc.Geometry.Impellers.Any(impeller => impeller is null ||
                !double.IsFinite(impeller.DiameterM) || impeller.DiameterM <= 0))
        {
            throw new InvalidOperationException("Cadastre ao menos um impelidor com diâmetro positivo.");
        }
        if (!double.IsFinite(doc.Fluid.DensityKgM3) || doc.Fluid.DensityKgM3 <= 0 ||
            !double.IsFinite(doc.Fluid.ViscosityPaS) || doc.Fluid.ViscosityPaS <= 0)
        {
            throw new InvalidOperationException("Densidade e viscosidade do fluido devem ser positivas.");
        }
        ValidateSettings(doc.Settings);
        // A tara realizada no ar mede o atrito mecânico parasita do eixo/motor/rolamentos e pode ser
        // reutilizada entre diferentes configurações de impelidores do ensaio sem bloquear a execução.
        if (doc.Tare is { CalibrationHash.Length: > 0 } calibratedTare)
        {
            var calibrationHash = PowerTestFileContracts.ComputeTorqueCalibrationHash(
                doc.Calibration,
                doc.MotorRatedTorqueNm);
            if (!string.Equals(calibratedTare.CalibrationHash, calibrationHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("A calibração de torque mudou após a tara; refaça a tara no ar.");
            }
        }
        if (doc.Conditions.Count == 0)
        {
            throw new InvalidOperationException("Inclua ao menos uma condição na tabela.");
        }
        foreach (var condition in doc.Conditions)
        {
            if (condition is null)
            {
                throw new InvalidOperationException("A tabela contém uma condição inválida.");
            }
            ValidateCondition(condition, doc.Settings);
        }
    }

    private static void ValidateSettings(PowerTestSettings settings)
    {
        if (!double.IsFinite(settings.MinRpm) || !double.IsFinite(settings.MaxRpm) ||
            settings.MinRpm < 15 || settings.MaxRpm > 1000 || settings.MinRpm > settings.MaxRpm)
        {
            throw new InvalidOperationException("A faixa de rotação deve respeitar o contrato de 15 a 1000 rpm.");
        }
        if (!double.IsFinite(settings.DefaultStepRpm) || settings.DefaultStepRpm <= 0 ||
            !double.IsFinite(settings.MinStepRpm) || settings.MinStepRpm <= 0 ||
            settings.DefaultStepRpm < settings.MinStepRpm ||
            !double.IsFinite(settings.SpeedToleranceRpm) || settings.SpeedToleranceRpm <= 0 ||
            settings.SpeedStableSamples < 1 ||
            !double.IsFinite(settings.MaxSpeedSettlingSeconds) || settings.MaxSpeedSettlingSeconds <= 0 ||
            !double.IsFinite(settings.StationarityWindowSeconds) || settings.StationarityWindowSeconds <= 0 ||
            !double.IsFinite(settings.StationaritySlopeTolerancePercentPerSecond) ||
            settings.StationaritySlopeTolerancePercentPerSecond < 0 ||
            settings.StationarityRequiredSamples < 1 || settings.MinSamples < 2 ||
            !double.IsFinite(settings.MaxCaptureSeconds) || settings.MaxCaptureSeconds <= 0 ||
            settings.MaxTries < 1 ||
            !double.IsFinite(settings.MeasurementTimeoutSeconds) || settings.MeasurementTimeoutSeconds <= 0 ||
            !double.IsFinite(settings.RelativeCiFraction) || settings.RelativeCiFraction < 0 ||
            !double.IsFinite(settings.CiFloorSigmaMultiple) || settings.CiFloorSigmaMultiple < 0 ||
            !double.IsFinite(settings.MaxTorquePercent) || settings.MaxTorquePercent <= 0 ||
            !double.IsFinite(settings.SnrFloorMultiple) || settings.SnrFloorMultiple <= 0 ||
            !double.IsFinite(settings.PrestageFlowToleranceLpm) || settings.PrestageFlowToleranceLpm <= 0 ||
            settings.PrestageFlowStableSamples < 1 ||
            !double.IsFinite(settings.PrestageAgitationRpm) ||
            settings.PrestageAgitationRpm < 15 || settings.PrestageAgitationRpm > 1000 ||
            !double.IsFinite(settings.MaxPrestageSeconds) || settings.MaxPrestageSeconds <= 0 ||
            !double.IsFinite(settings.PrestageFlowStabilityStdDevLpm) || settings.PrestageFlowStabilityStdDevLpm < 0 ||
            !double.IsFinite(settings.PrestageFlowStabilityMaxErrorLpm) || settings.PrestageFlowStabilityMaxErrorLpm < 0 ||
            settings.CaptureServoPollMs is < CommandBuilders.ServoPollMinimumMs or > CommandBuilders.ServoPollMaximumMs ||
            settings.RestoreServoPollMs is < CommandBuilders.ServoPollMinimumMs or > CommandBuilders.ServoPollMaximumMs)
        {
            throw new InvalidOperationException("Os limites de captura do ensaio de potência são inválidos.");
        }
    }

    private static void ValidateCondition(PowerCondition condition, PowerTestSettings settings)
    {
        if (!double.IsFinite(condition.AgitationRpm) ||
            condition.AgitationRpm < settings.MinRpm || condition.AgitationRpm > settings.MaxRpm)
        {
            throw new InvalidOperationException(
                $"A condição deve ficar entre {settings.MinRpm:F0} e {settings.MaxRpm:F0} rpm.");
        }
        if (condition.RequestedReplicates < 1)
        {
            throw new InvalidOperationException("Cada condição precisa de ao menos uma réplica.");
        }
        if (condition.GasMode is PowerGasMode.Gassed or PowerGasMode.Both)
        {
            if (condition.GasFlowLpm is not { } flow || !double.IsFinite(flow) || flow < 0)
            {
                throw new InvalidOperationException("Condições gaseificadas exigem vazão de gás válida (≥ 0 L/min).");
            }
        }
    }

    private double GetMonotonicSeconds() => (double)_time.GetTimestamp() / _time.TimestampFrequency;

    private void RaiseStateChanged()
    {
        if (_inTelemetryFrame)
        {
            _stateChangePending = true;
            return;
        }

        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _watchdog.Dispose();
        _device.TelemetryReceived -= OnTelemetryReceived;
        _device.StateChanged -= OnConnectionStateChanged;
        _arbiter.OwnershipRevoked -= OnOwnershipRevoked;
        _store.WriteFailed -= OnStoreWriteFailed;
    }

    public bool IsStorageCompromised { get; private set; }

    /// <summary>
    /// Raised on the writer's thread. A write that failed is a hole in the record: the flag is
    /// set once, the message names the first file, and the journal gets an entry — which may
    /// itself fail, in which case the log line is the evidence.
    /// </summary>
    private void OnStoreWriteFailed(string path, Exception exception)
    {
        var first = !IsStorageCompromised;
        IsStorageCompromised = true;
        if (first)
        {
            var name = System.IO.Path.GetFileName(path);
            _statusMessage = $"⚠ Gravação comprometida ({name}: {exception.Message}). {_statusMessage}";
            try
            {
                LogEvent("StorageWriteFailed", $"{path}: {exception.GetType().Name}: {exception.Message}");
            }
            catch
            {
                // The journal is on the same storage.
            }
            RaiseStateChanged();
        }
    }
}
