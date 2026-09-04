using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;

namespace OpenTECHub.Services.PowerTesting;

/// <summary>
/// Phase-1 automatic impeller-power runner. It owns agitation, waits for measured speed,
/// executes the two-gate capture, persists every valid sample, and returns the actuator safely.
/// </summary>
public sealed class PowerTestRunner : IPowerTestRunner
{
    private static readonly ActuatorId[] Phase1Actuators = [ActuatorId.Agitation];

    private readonly IDeviceService _device;
    private readonly ICommandArbiter _arbiter;
    private readonly IPowerTestStore _store;
    private readonly IPowerAnalysisEngine _analysis;
    private readonly IPowerTestInterlock _interlock;
    private readonly TimeProvider _time;
    private readonly ITimer _watchdog;

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

    public PowerTestRunner(
        IDeviceService device,
        ICommandArbiter arbiter,
        IPowerTestStore store,
        IPowerAnalysisEngine analysis,
        IPowerTestInterlock interlock,
        TimeProvider time)
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

        _device.TelemetryReceived += OnTelemetryReceived;
        _device.StateChanged += OnConnectionStateChanged;
        _arbiter.OwnershipRevoked += OnOwnershipRevoked;

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
    }

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
    public event Action<string>? Logged;

    public bool CanStart(PowerTestDocument doc, out string? reason)
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

        if (HasActiveGasPath(latest))
        {
            reason = "Feche a vazão e a rota de gás antes de medir P0 na Fase 1.";
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
        doc.Status = PowerTestStatus.Running;
        doc.StartedUtc ??= _time.GetUtcNow();
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
        }
        _testStartMonotonic = GetMonotonicSeconds();
        _store.SaveTestManifest(doc);
        LogEvent("TestStarted", $"Ensaio '{doc.Name}' iniciado.");

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
        if (!CanStart(_currentTest, out var reason))
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
        if (_arbiter.OwnerOf(ActuatorId.Agitation) != CommandOwner.PowerAssay)
        {
            throw new InvalidOperationException("O ensaio perdeu a posse da agitação.");
        }

        if (_capture is null)
        {
            throw new InvalidOperationException("O controlador da captura não está disponível.");
        }
        _speedStableCount = 0;
        _capture.Reset();
        SetPhase(PowerRunPhase.SettingSpeed, "Medida restabelecida; reaproximando a rotação antes de recapturar.");
        DispatchMotorOrFault(_commandedRpm, "retomar a rotação");
        LogEvent("MeasurementResumed", "Telemetria válida restabelecida; as duas portas foram reiniciadas.");
        return Task.CompletedTask;
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
        AcceptRunCore(autoAdvance: false);
        return Task.CompletedTask;
    }

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
        _commandedRpm = Math.Clamp(
            (int)Math.Round(condition.AgitationRpm, MidpointRounding.AwayFromZero),
            (int)doc.Settings.MinRpm,
            (int)doc.Settings.MaxRpm);

        var actualMode = condition.GasMode == PowerGasMode.Both
            ? PowerGasMode.Ungassed
            : condition.GasMode;
        _currentRun = new PowerRun
        {
            TestId = doc.TestId,
            ConditionId = condition.ConditionId,
            ReplicateNumber = replicateNumber,
            AgitationRpm = _commandedRpm,
            GasFlowLpm = null,
            GasMode = actualMode,
            CurrentPhase = PowerRunPhase.Preflight,
            StartedUtc = _time.GetUtcNow(),
            IsRelative = doc.RelativeMode || doc.Calibration is null || doc.Tare is null,
            Tries = 1,
        };
        _store.InitializeRunFolder(doc.FolderName, _currentRun);

        double? sigma = doc.Tare is { Points.Count: > 0 } tare
            ? TareInterpolator.InterpolateSigmaTauPercent(tare, _commandedRpm)
            : null;
        _capture = new PowerCaptureController(doc.Settings, sigma);
        _runStartMonotonic = GetMonotonicSeconds();
        SetPhase(PowerRunPhase.Preflight, $"Pré-voo da corrida {_currentRun.FolderName}.");

        _arbiter.Claim(CommandOwner.PowerAssay, Phase1Actuators, $"Ensaio de potência: {_currentRun.FolderName}");
        if (_arbiter.OwnerOf(ActuatorId.Agitation) != CommandOwner.PowerAssay)
        {
            FaultWithoutSafeCommand("Falha ao obter posse da agitação.");
            return;
        }

        SetPhase(PowerRunPhase.PreparingCondition, "Preparando amostragem e condição não-gaseificada.");
        if (!Dispatch(CommandBuilders.ServoPollInterval(doc.Settings.CaptureServoPollMs), "baixar servoPollMs"))
        {
            return;
        }
        SetPhase(PowerRunPhase.SettingSpeed, $"Aguardando a medida estabilizar em {_commandedRpm} rpm.");
        DispatchMotorOrFault(_commandedRpm, "ajustar a rotação");

        _store.SaveConditionsTable(doc.FolderName, doc.Conditions);
        _store.SaveTestManifest(doc);
        LogEvent("RunStarted", $"Corrida {_currentRun.FolderName}: {_commandedRpm} rpm, tentativa 1.");
    }

    private void OnTelemetryReceived(SensorSnapshot snapshot)
    {
        var now = GetMonotonicSeconds();
        _lastTelemetryMonotonic = now;
        if (!HasValidServoMeasurement(snapshot))
        {
            if (RequiresServoMeasurement(_phase))
            {
                PauseForMeasurement("A medida do servo ficou ausente, offline ou sem roteamento.");
            }
            return;
        }

        _lastValidServoMonotonic = now;
        _currentRpm = snapshot.ServoRpm;
        _currentTorquePercent = snapshot.ServoTorquePct;

        if (_currentTest is null)
        {
            RaiseStateChanged();
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
                StopForReview("A rotação medida não entrou na banda dentro do tempo limite.", PowerStopReason.Tmax);
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
        if (_capture is null || _currentRun is null || _currentTest is null)
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
        run.CurrentPhase = PowerRunPhase.Accepted;
        run.CompletedUtc ??= _time.GetUtcNow();
        condition.CompletedReplicates++;
        condition.AcceptedReplicates++;
        condition.Status = condition.AcceptedReplicates >= condition.RequestedReplicates
            ? PowerConditionStatus.Completed
            : PowerConditionStatus.Pending;

        UpsertCurrentRunSummary(PowerRunPhase.Accepted);
        UpdateReplicateAgreement(condition);
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

    private void StopForReview(string reason, PowerStopReason stopReason)
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
        SafeParkAndRelease(reason);
        _currentRun.CurrentPhase = PowerRunPhase.Reviewing;
        PersistCurrentRun();
        SetPhase(PowerRunPhase.Reviewing, reason);
        LogEvent("RunStoppedForReview", reason);
    }

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
            _arbiter.Dispatch(
                CommandOwner.PowerAssay,
                CommandBuilders.ServoPollInterval(doc.Settings.RestoreServoPollMs));
        }
        _arbiter.Release(CommandOwner.PowerAssay, reason);
    }

    private bool DispatchMotorOrFault(int rpm, string action)
        => Dispatch(CommandBuilders.MotorSetpoint(Math.Max((int)_currentTest!.Settings.MinRpm, rpm)), action);

    private bool Dispatch(OpenTECCommand command, string action)
    {
        var result = _arbiter.Dispatch(CommandOwner.PowerAssay, command);
        if (result.Accepted)
        {
            return true;
        }
        FaultWithoutSafeCommand($"Comando recusado ao tentar {action}.");
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
        if (_currentTest is null || !RequiresServoMeasurement(_phase))
        {
            return;
        }
        var last = Math.Max(_lastTelemetryMonotonic, _lastValidServoMonotonic);
        if (last > 0 && GetMonotonicSeconds() - last > _currentTest.Settings.MeasurementTimeoutSeconds)
        {
            PauseForMeasurement("A telemetria do servo ficou desatualizada.");
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
        if (transfer.Actuators.Contains(ActuatorId.Agitation) &&
            _currentTest?.Status == PowerTestStatus.Running)
        {
            FaultWithoutSafeCommand("A posse da agitação foi revogada pelo aborto seguro do link.");
        }
    }

    private PowerCondition? NextPendingCondition() => _currentTest?.Conditions
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

    private static bool HasActiveGasPath(SensorSnapshot snapshot)
    {
        const double flowToleranceLpm = 0.1;
        var measuredFlowActive = double.IsFinite(snapshot.FlowRate) &&
                                 snapshot.FlowRate > SensorReadings.NotReceived &&
                                 Math.Abs(snapshot.FlowRate) > flowToleranceLpm;
        var setpointActive = double.IsFinite(snapshot.FlowSetpoint) &&
                             snapshot.FlowSetpoint > SensorReadings.NotReceived &&
                             Math.Abs(snapshot.FlowSetpoint) > flowToleranceLpm;
        var routedOpen = snapshot.FlowValveMain == 0 &&
                         (snapshot.FlowValve1 == 1 || snapshot.FlowValve2 == 1);
        return measuredFlowActive || setpointActive || routedOpen;
    }

    private static bool RequiresServoMeasurement(PowerRunPhase phase) => phase is
        PowerRunPhase.SettingSpeed or
        PowerRunPhase.SettlingTorque or
        PowerRunPhase.AccumulatingToTarget or
        PowerRunPhase.PausedByOperator or
        PowerRunPhase.HoldingForManualEnergy;

    private static double? OptionalReading(double value) =>
        double.IsFinite(value) && value > SensorReadings.NotReceived ? value : null;

    private static double CalibratedTorqueNm(double torquePercent, PowerTestDocument doc)
    {
        if (doc.Calibration is { } calibration)
        {
            return calibration.Scale * (torquePercent / 100.0 * calibration.MotorRatedTorqueNm) + calibration.Offset;
        }
        return torquePercent / 100.0 * doc.MotorRatedTorqueNm;
    }

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
        if (!doc.RelativeMode && (doc.Calibration is null || doc.Tare is null))
        {
            throw new InvalidOperationException("Selecione tara e calibração ou confirme explicitamente o modo relativo.");
        }
        if (doc.Tare is { ImpellerSetHash.Length: > 0 } tare)
        {
            var geometryHash = PowerTestFileContracts.ComputeImpellerSetHash(doc.Geometry);
            if (!string.Equals(tare.ImpellerSetHash, geometryHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("A tara pertence a outro conjunto de impelidores.");
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
            !double.IsFinite(settings.VentFlowToleranceLpm) || settings.VentFlowToleranceLpm <= 0 ||
            settings.VentFlowStableSamples < 1 ||
            !double.IsFinite(settings.VentAgitationRpm) ||
            settings.VentAgitationRpm < 15 || settings.VentAgitationRpm > 1000 ||
            !double.IsFinite(settings.MaxVentStabilizationSeconds) || settings.MaxVentStabilizationSeconds <= 0 ||
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
        if (condition.GasMode == PowerGasMode.Gassed)
        {
            throw new InvalidOperationException("Condições gaseificadas entram na Fase 2; a Fase 1 executa P0.");
        }
    }

    private double GetMonotonicSeconds() => (double)_time.GetTimestamp() / _time.TimestampFrequency;

    private void RaiseStateChanged() => StateChanged?.Invoke();

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
    }
}
