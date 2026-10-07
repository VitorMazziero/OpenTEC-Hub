using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Control;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;

namespace OpenTECHub.Services.KlaTesting;

public sealed partial class KlaTestRunner : IKlaTestRunner
{
    private readonly IDeviceService _device;
    private readonly ICommandArbiter _arbiter;
    private readonly IKlaTestStore _store;
    private readonly IKlaAnalysisEngine _analysisEngine;
    private readonly ISettingsService _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<KlaTestRunner> _log;
    private readonly ITimer _watchdog;
    private readonly MotorRouteCoordinator _routeCoordinator;
    private readonly KlaReturnSnapshot? _recipeReturnSnapshot;
    private readonly RecipeAssayResourceLease? _recipeLease;
    private volatile bool _recipeAcquisitionSealed;
    private volatile bool _preparationPending;
    private readonly Func<KlaTestDocument, KlaTestRun, CancellationToken, Task>? _beforeActuation;

    private readonly object _gate = new();
    private readonly List<KlaRawDataPoint> _runPoints = [];
    private readonly List<KlaGlobalSeriesSample> _globalSamples = [];
    private readonly List<(double Time, double DO)> _stabilityWindow = [];

    private KlaTestDocument? _currentTest;
    private KlaTestRun? _currentRun;
    private KlaTestCondition? _currentCondition;
    private RunPhase _phase = RunPhase.Idle;
    private string _statusMessage = "Pronto para iniciar.";
    private double _currentDO;
    private double _currentDORaw;
    private double _currentFlowMeasured;
    private double _phaseStartMonotonic;
    private double _testStartMonotonic;
    private double _runStartMonotonic;
    private double _lastTelemetryMonotonic;
    private (double Flow, bool V1, bool V2, bool VFlow) _targetGasState;
    private long _lastFlowCommandId;
    private long _minimumExpectedFlowCommandId;
    private double _flowRequestedMonotonic;
    private long _lastLoggedConfirmation = -1;
    private bool _startAtFloor;
    private bool _prestageConfirmed;
    private bool _completeAfterClosing;
    private bool _abortAfterClosing;
    private string _terminalReason = "";
    private bool _disposed;
    private double? _currentDODerivative;
    private int _stabilityConfirmationCount;
    private int _prestageFlowStableCount;
    private double? _prestageFlowDeviation;
    private readonly List<double> _prestageFlowWindow = [];

    public KlaTestRunner(
        IDeviceService device,
        ICommandArbiter arbiter,
        IKlaTestStore store,
        IKlaAnalysisEngine analysisEngine,
        ISettingsService settings,
        TimeProvider? time = null,
        ILogger<KlaTestRunner>? log = null,
        ICascadeService? cascade = null, IOurSoftSensor? our = null, KlaActuationRelease? actuationRelease = null,
        RecipeAssayResourceLease? recipeLease = null, KlaReturnSnapshot? recipeReturnSnapshot = null,
        Func<KlaTestDocument, KlaTestRun, CancellationToken, Task>? beforeActuation = null)
    {
        if ((recipeLease is null) != (recipeReturnSnapshot is null))
            throw new ArgumentException("Runner de receita requer reserva e snapshot juntos.");
        if (recipeLease is not null)
        {
            recipeLease.ValidateRecoverySnapshot(recipeReturnSnapshot!);
            RecipeAssayReturnState.Validate(recipeReturnSnapshot!);
        }
        _recipeReturnSnapshot = recipeReturnSnapshot; _recipeLease = recipeLease;
        _beforeActuation = beforeActuation;
        _device = device ?? throw new ArgumentNullException(nameof(device));
        ArgumentNullException.ThrowIfNull(arbiter);
        _arbiter = recipeLease is null ? arbiter : new ReservedKlaCommandArbiter(arbiter, recipeLease);
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _store.WriteFailed += OnStoreWriteFailed;
        _analysisEngine = analysisEngine ?? throw new ArgumentNullException(nameof(analysisEngine));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger<KlaTestRunner>.Instance;
        _routeCoordinator = new MotorRouteCoordinator(_arbiter, _device, CommandOwner.KlaAssay);
        _assayCoordinator = new(_arbiter, cascade, our, recipeLease);
        _actuationRelease = actuationRelease ?? new();

        _device.TelemetryReceived += OnTelemetryReceived;
        _device.StateChanged += OnDeviceStateChanged;
        _watchdog = _time.CreateTimer(_ => CheckWatchdog(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public MotorRouteCoordinator RouteCoordinator => _routeCoordinator;
    public bool RequiresRecipeSnapshotRestoration => _recipeReturnSnapshot is not null;

    /// <summary>Seals every runner enqueue before the independent recovery service starts writing.</summary>
    public void SealRecipeAcquisitionForRecovery()
    {
        if (_recipeReturnSnapshot is null) throw new InvalidOperationException("Runner não pertence a uma receita.");
        if (_recipeAcquisitionSealed) return;
        ((ReservedKlaCommandArbiter)_arbiter).Seal();
        _recipeAcquisitionSealed = true;
        _device.TelemetryReceived -= OnTelemetryReceived;
        _watchdog.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        lock (_gate)
        {
            if (_currentRun is null) return;
            _currentRun.Outcome = (_currentRun.Outcome ?? new()) with { Restoration = KlaRestorationState.Pending };
            PersistPhysicalOutcome();
            SetPhase(RunPhase.RestoringCultivation, "Aquisição encerrada; restaurando snapshot da receita.");
        }
    }

    public void ConfirmRecipeReturn() => _assayCoordinator.CompleteRecipeReturn();

    /// <summary>Records physical return only. This does not release ownership, resume observers or imply durable storage.</summary>
    public void RecordRecipeRecovery(RecipeAssayRecoveryResult result)
    {
        lock (_gate)
        {
            if (_recipeReturnSnapshot is null || !_recipeAcquisitionSealed || result.SnapshotId != _recipeReturnSnapshot.SnapshotId || _currentRun is null ||
                result.Restoration is not (KlaRestorationState.Confirmed or KlaRestorationState.Failed) ||
                result.Restoration == KlaRestorationState.Confirmed && _recipeLease?.IsAssayAuthorityCurrent != true)
                throw new InvalidOperationException("Recuperação não corresponde ao runner/autoridade da receita.");
            _currentRun.Outcome = (_currentRun.Outcome ?? new()) with
            { Restoration = result.Restoration, RestorationReason = result.Reason };
            PersistPhysicalOutcome();
            SetPhase(result.Restoration == KlaRestorationState.Confirmed ? RunPhase.Reviewing : RunPhase.Faulted,
                result.Restoration == KlaRestorationState.Confirmed ? "Snapshot restaurado; aguardando decisão automática e persistência." : $"Recuperação falhou: {result.Reason}");
        }
    }
    private readonly KlaActuationRelease _actuationRelease;

    public KlaTestDocument? CurrentTest => _currentTest;
    public KlaTestRun? CurrentRun => _currentRun;
    internal bool UsesRecipeAuthority => _recipeLease is not null;
    public KlaTestCondition? CurrentCondition => _currentCondition;
    public RunPhase Phase => _phase;
    public bool IsRunning => _phase is not (RunPhase.Idle or RunPhase.Completed or RunPhase.Faulted);
    public bool IsInReview => _phase == RunPhase.Reviewing;
    public double CurrentDO => _currentDO;
    public double CurrentDORaw => _currentDORaw;
    public double CurrentFlowMeasured => _currentFlowMeasured;
    public double? CurrentDODerivative => _currentDODerivative;
    public int StabilityConfirmationCount => _stabilityConfirmationCount;
    public int PrestageFlowStableCount => _prestageFlowStableCount;
    public double? PrestageFlowDeviation => _prestageFlowDeviation;
    public string StatusMessage => _statusMessage;

    /// <summary>True once a queued write of this assay's files failed (D-048); the run continues, the operator is told.</summary>
    public bool IsStorageCompromised { get; private set; }

    private void OnStoreWriteFailed(string path, Exception exception)
    {
        var first = !IsStorageCompromised;
        IsStorageCompromised = true;
        if (first)
        {
            _statusMessage = $"⚠ Gravação comprometida ({System.IO.Path.GetFileName(path)}: {exception.Message}). {_statusMessage}";
            _log?.LogError(exception, "Falha ao gravar {Path} durante o ensaio de kLa", path);
            RaiseStateChanged();
        }
    }

    public double PhaseElapsedSeconds
    {
        get
        {
            var now = GetMonotonicSeconds();
            return _phaseStartMonotonic > 0 ? Math.Max(0, now - _phaseStartMonotonic) : 0;
        }
    }

    public double TotalElapsedSeconds
    {
        get
        {
            var now = GetMonotonicSeconds();
            return _testStartMonotonic > 0 ? Math.Max(0, now - _testStartMonotonic) : 0;
        }
    }

    public IReadOnlyList<KlaRawDataPoint> CurrentRunPoints
    {
        get
        {
            lock (_gate)
            {
                return _runPoints.ToList();
            }
        }
    }

    public IReadOnlyList<KlaGlobalSeriesSample> GlobalSeriesSamples
    {
        get
        {
            lock (_gate)
            {
                return _globalSamples.ToList();
            }
        }
    }

    public event Action? StateChanged;
    public event Action<KlaRawDataPoint>? DataPointAdded;
    public event Action<string>? Logged;

    public void PrepareTest(KlaTestDocument doc)
    {
        if (_preparationPending) throw new InvalidOperationException("Preparação persistida em andamento.");
        ArgumentNullException.ThrowIfNull(doc);
        if (_phase is RunPhase.DivertingAir or RunPhase.MeasuringConsumption or RunPhase.SwitchingToReactor or
            RunPhase.RestoringCultivation or RunPhase.Reoxygenating or RunPhase.Deoxygenating or RunPhase.PrestagingAir)
        {
            throw new InvalidOperationException("Finalize a aquisição e a retomada antes de trocar a sessão.");
        }
        lock (_gate)
        {
            _currentTest = doc;
            _currentRun = null;
            _currentCondition = null;
            _phase = RunPhase.Idle;
            _phaseStartMonotonic = 0;
            _testStartMonotonic = 0;
            _statusMessage = $"Teste '{doc.Name}' carregado. Selecione uma condição para iniciar ou uma corrida para revisar.";
            _runPoints.Clear();
            _globalSamples.Clear();
            ResetStabilityDetection();
        }
        RaiseStateChanged();
    }

    /// <summary>Hub and node identity at the moment the assay starts, for the manifest.</summary>
    private void RecordProvenance(KlaTestDocument doc)
    {
        if (_device.Latest is not { } latest)
        {
            return;
        }

        doc.HubFirmwareVersion = string.IsNullOrWhiteSpace(latest.HubFirmwareVersion) ? null : latest.HubFirmwareVersion;
        doc.HubProtocolVersion = latest.HubProtocolVersion > 0 ? latest.HubProtocolVersion : null;
        doc.ExternalNodes = Services.Communication.ExternalNodeProvenance.From(latest);
    }

    public Task StartTestAsync(KlaTestDocument doc, CancellationToken ct = default)
    {
        if (_recipeAcquisitionSealed) throw new InvalidOperationException("Runner de tentativa encerrado para recuperação.");
        ct.ThrowIfCancellationRequested();
        if (doc.Runs.Any(r => r.Outcome?.Restoration is KlaRestorationState.Pending or KlaRestorationState.Failed))
        {
            throw new InvalidOperationException("Confirme a recuperação manual da sessão anterior antes de iniciar outra corrida.");
        }
        if (_currentRun is not null && _phase is not (RunPhase.Idle or RunPhase.Completed or RunPhase.Accepted or RunPhase.Rejected))
        {
            throw new InvalidOperationException("Conclua a corrida atual antes de iniciar outra sessão.");
        }
        lock (_gate)
        {
            _currentTest = doc;
            _currentTest.Status = KlaTestStatus.Running;
            _currentTest.StartedUtc ??= _time.GetUtcNow();
            _currentTest.GasRig ??= GasRigSettings.From(Rig);
            RecordProvenance(_currentTest);
            _store.SaveTestManifest(_currentTest);

            _testStartMonotonic = GetMonotonicSeconds();
            _phase = RunPhase.Idle;
            _statusMessage = $"Teste '{doc.Name}' ativo. Selecione uma condição para iniciar a corrida.";
        }

        LogEvent("TestStarted", $"Teste '{doc.Name}' iniciado. Arranjo: {_currentTest.GasRig.ToConfiguration().Describe()}.");
        RaiseStateChanged();
        return Task.CompletedTask;
    }

    public async Task StartRunAsync(KlaTestCondition condition, int replicateNumber, CancellationToken ct = default)
    {
        if (_recipeAcquisitionSealed) throw new InvalidOperationException("Runner de tentativa encerrado para recuperação.");
        if (_currentTest is null)
        {
            throw new InvalidOperationException("Nenhum teste de kLa ativo.");
        }

        ct.ThrowIfCancellationRequested();
        if (_phase is not (RunPhase.Idle or RunPhase.Accepted or RunPhase.Rejected or RunPhase.Completed))
        {
            throw new InvalidOperationException("Conclua e classifique a corrida atual antes de iniciar outra.");
        }
        if (_currentRun?.Outcome?.Restoration is KlaRestorationState.Pending or KlaRestorationState.Failed)
        {
            throw new InvalidOperationException("Retomada anterior falhou. Confirme a recuperação manual antes de preparar outra sessão.");
        }
        _actuationRelease.EnsureCanRun(_currentTest.EffectiveProtocol);
        _assayCoordinator.Validate();
        if (_currentTest.Runs.Any(r => r.Outcome?.Restoration is KlaRestorationState.Pending or KlaRestorationState.Failed))
        {
            throw new InvalidOperationException("Há uma retomada pendente ou falha nesta sessão. Confira o estado físico antes de iniciar outra corrida.");
        }
        if (!IsBiotic && (_arbiter.OwnerOf(ActuatorId.Agitation) == CommandOwner.Automatic ||
            _arbiter.OwnerOf(ActuatorId.Aeration) == CommandOwner.Automatic))
        {
            throw new InvalidOperationException("Ensaio abiótico requer controle manual; encerre a cascata antes de iniciar.");
        }
        var runDefinition = KlaRunDefinition.Create(_currentTest, condition, replicateNumber);
        if (_currentTest.SequenceLimits is not null)
        {
            var ready = KlaSequence.Check(_currentTest,
                new(condition.ConditionId, replicateNumber, runDefinition.AttemptNumber), _time.GetUtcNow());
            if (!ready.CanStart) throw new InvalidOperationException(ready.Reason);
        }

        if (_device.State != ConnectionState.Connected)
        {
            throw new InvalidOperationException("O biorreator não está conectado.");
        }

        if (_lastTelemetryMonotonic <= 0 || GetMonotonicSeconds() - _lastTelemetryMonotonic > 5)
        {
            throw new InvalidOperationException("Telemetria ausente ou desatualizada. Aguarde uma leitura válida antes de iniciar.");
        }
        if (!_lastFlowmeterOnline)
        {
            throw new InvalidOperationException("O fluxômetro está offline.");
        }
        if (!double.IsFinite(_currentDO) || _currentDO < 0)
        {
            throw new InvalidOperationException("Leitura de oxigênio inválida.");
        }
        ValidateSettings(_currentTest.Settings);
        if (!_hasOxygenSample || _lastOxygenRejected || GetMonotonicSeconds() - _lastOxygenMonotonic >
            (_currentTest.ProtocolSettings?.OxygenSampleTimeoutSeconds ?? 10))
        {
            throw new InvalidOperationException("Sem amostra nova e válida de oxigênio. Aguarde OD antes de iniciar.");
        }
        if (IsBiotic)
        {
            ValidateBioticPreflight(condition);
        }
        if (_currentTest.IsLegacyRig)
        {
            throw new InvalidOperationException(
                "Montagem anterior ao arranjo A/B/C — este ensaio é só leitura. Crie um ensaio novo para o arranjo atual.");
        }
        var rig = Rig;
        if (_currentTest.GasRig is { } recordedRig && recordedRig.ToConfiguration() != rig)
        {
            throw new InvalidOperationException(
                $"O arranjo configurado ({rig.Describe()}) difere do gravado neste ensaio ({recordedRig.ToConfiguration().Describe()}). " +
                "Consulte Documentação › Gás e válvulas ou crie um ensaio novo.");
        }
        if (!double.IsFinite(condition.AgitationRpm) || !double.IsFinite(condition.AirflowLpm) ||
            condition.AgitationRpm <= 0 || condition.AirflowLpm <= 0 || condition.AirflowLpm > MaxFlow || replicateNumber < 1)
        {
            throw new InvalidOperationException("Condição inválida: rotação, vazão e replicata devem ser positivas.");
        }

        // A run that begins at the floor never opens the N₂; every other run does, and the
        // source is the operator's hand — the preflight confirmation is the only evidence.
        var startAtFloor = _currentDO <= _currentTest.Settings.DOMinPercent + Math.Max(0.0, _currentTest.Settings.AirPrestageLeadPercent);
        if (!IsBiotic && !startAtFloor && _currentTest.NitrogenSourceConfirmedUtc is null)
        {
            throw new InvalidOperationException(
                "Confirme no pré-voo que o N₂ está aberto na fonte antes de iniciar a desoxigenação.");
        }

        lock (_gate)
        {
            if (_currentTest.Status != KlaTestStatus.Running)
            {
                _currentTest.Status = KlaTestStatus.Running;
                _currentTest.StartedUtc ??= _time.GetUtcNow();
                _currentTest.CompletedUtc = null;
                _currentTest.InterruptionReason = null;
                _testStartMonotonic = GetMonotonicSeconds();
            }

            _currentCondition = condition;
            condition.Status = ConditionStatus.InProgress;
            _currentTest.GasRig ??= GasRigSettings.From(rig);

            _runPoints.Clear();
            ResetStabilityDetection();
            _completeAfterClosing = false;
            _abortAfterClosing = false;
            _terminalReason = "";
            _startAtFloor = startAtFloor;

            _currentRun = new KlaTestRun
            {
                Definition = runDefinition,
                AttemptNumber = runDefinition.AttemptNumber,
                Context = runDefinition.Context,
                Acquisition = new(_settings.Current.Calibration.OxygenA, _settings.Current.Calibration.OxygenB,
                    _time.GetUtcNow(), OperationalSettings.OxygenSampleTimeoutSeconds,
                    _recipeReturnSnapshot?.AgitationSetpointRpm ?? (IsBiotic ? InitialReturnRpm : null),
                    _recipeReturnSnapshot?.AirflowSetpointLpm ?? (IsBiotic ? _device.Latest!.FlowSetpoint : null)),
                TestId = _currentTest.TestId,
                ConditionId = condition.ConditionId,
                ReplicateNumber = replicateNumber,
                AgitationRpm = condition.AgitationRpm,
                AirflowLpm = condition.AirflowLpm,
                SkippedNitrogen = startAtFloor,
                CurrentPhase = RunPhase.Preflight,
                StartedUtc = _time.GetUtcNow(),
            };

            _preparationPending = _beforeActuation is not null;

            var runFolder = _store.InitializeRunFolder(_currentTest.FolderName, _currentRun);
            _currentRun.FolderName = runFolder;
            _store.SaveRunAcquisition(_currentTest.FolderName, runFolder, _currentRun.Acquisition);

            _phase = RunPhase.Preflight;
            _phaseStartMonotonic = GetMonotonicSeconds();
            _runStartMonotonic = _phaseStartMonotonic;
            _statusMessage = $"Pré-voo da corrida {runFolder}...";
            _store.SaveTestManifest(_currentTest);
        }

        LogEvent(
            "RunStarted",
            $"Iniciando corrida {_currentRun.FolderName} (N={condition.AgitationRpm} rpm, Q={condition.AirflowLpm} L/min, Rep={replicateNumber}). " +
            $"Arranjo: {rig.Describe()}. DO inicial {_currentDO:F1}%" +
            (IsBiotic ? " — respiração; ar no escape e N₂ isolado."
                : startAtFloor ? " — já no piso, sem fase de N₂." : $" — N₂ aberto na fonte confirmado em {_currentTest.NitrogenSourceConfirmedUtc:HH:mm:ss} UTC."));
        RaiseStateChanged();

        if (_beforeActuation is not null)
        {
            try
            {
                await _beforeActuation(_currentTest, _currentRun, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (_phase != RunPhase.Preflight || IsStorageCompromised || _device.State != ConnectionState.Connected || !_lastFlowmeterOnline ||
                    _lastOxygenRejected || GetMonotonicSeconds() - _lastOxygenMonotonic > OperationalSettings.OxygenSampleTimeoutSeconds ||
                    _currentRun.Acquisition?.OxygenCalibrationA != _settings.Current.Calibration.OxygenA ||
                    _currentRun.Acquisition?.OxygenCalibrationB != _settings.Current.Calibration.OxygenB ||
                    _startAtFloor != (_currentDO <= _currentTest.Settings.DOMinPercent + Math.Max(0.0, _currentTest.Settings.AirPrestageLeadPercent)) ||
                    _recipeLease is not null && !_recipeLease.IsAssayAuthorityCurrent)
                    throw new InvalidOperationException("Pré-voo inválido após a persistência da preparação.");
                if (IsBiotic) ValidateBioticPreflight(condition);
            }
            catch
            {
                lock (_gate)
                {
                    _phase = RunPhase.Faulted;
                    _currentRun.CurrentPhase = RunPhase.Faulted;
                    _statusMessage = "Preparação não confirmada; aquisição não iniciada.";
                }
                throw;
            }
            finally { _preparationPending = false; }
        }

        // 1. Claim ownership of Agitation and Aeration
        _assayCoordinator.Acquire($"Ensaio kLa: {_currentRun.FolderName}");

        if (_arbiter.OwnerOf(ActuatorId.Agitation) != CommandOwner.KlaAssay ||
            _arbiter.OwnerOf(ActuatorId.Aeration) != CommandOwner.KlaAssay)
        {
            await AbortTestAsync("Falha ao obter posse dos atuadores de agitação e aeração.");
            return;
        }

        try
        {
            _routeCoordinator.EnsurePrimaryRoute(out var routeMsg);
            if (!_routeCoordinator.RouteRequestAccepted) { await AbortTestAsync(routeMsg); return; }
            LogEvent("MotorRoute", routeMsg);

            if (_routeCoordinator.IsUartFallback && condition.AgitationRpm > MotorRouteCoordinator.UartFallbackMaxRpm)
            {
                await AbortTestAsync(
                    $"Em modo de fallback UART, a rotação máxima é de {MotorRouteCoordinator.UartFallbackMaxRpm:F0} rpm. " +
                    $"A condição de {condition.AgitationRpm:F0} rpm requer comunicação Modbus com o servo drive.");
                return;
            }

            // 2. Initial Gas State: Close all gases first
            if (IsBiotic)
            {
                BeginBioticRemoval();
                return;
            }
            SetPhase(RunPhase.ClosingAllGas, "Intertravamento: fechando todas as válvulas...");
            DispatchFlowOrAbort(CommandBuilders.FlowSafeStop(MaxFlow), "fechar todas as válvulas");
        }
        catch (Exception error)
        {
            await AbortTestAsync($"Falha no início da corrida: {error.Message}");
            throw;
        }
    }

    public Task StopRunAndReviewAsync(string reason = "Parada pelo operador")
    {
        if (IsBiotic)
        {
            BeginCultivationRestoration(reason);
            return Task.CompletedTask;
        }
        if (_phase is not (RunPhase.OpeningNitrogen or RunPhase.Deoxygenating or RunPhase.PrestagingAir or
            RunPhase.SwitchingToReactor or RunPhase.Reoxygenating))
        {
            return Task.CompletedTask;
        }

        LogEvent("RunStopped", $"Corrida interrompida para revisão: {reason}");

        SetPhase(RunPhase.StoppingRun, "Fechando todas as válvulas antes da revisão...");
        DispatchFlowOrAbort(CommandBuilders.FlowSafeStop(MaxFlow), "parar a corrida");
        return Task.CompletedTask;
    }

    public Task AcceptRunAsync(KlaAnalysisRevision analysis)
    {
        EnsureBioticReviewReady();
        analysis = KlaTestFileContracts.DeserializeAnalysis(KlaTestFileContracts.SerializeAnalysis(analysis))!;
        if (_currentTest is null || _currentRun is null || _currentCondition is null)
        {
            throw new InvalidOperationException("Nenhuma corrida ativa para aceitar.");
        }

        lock (_gate)
        {
            if (analysis.Quality == DecisionQuality.Inconclusive ||
                analysis.Outcome?.KlaQuality is KlaScientificQuality.Inconclusive or
                    KlaScientificQuality.NotEvaluated or KlaScientificQuality.NotApplicable)
            {
                throw new InvalidOperationException("Uma análise inconclusiva não pode ser aceita. Ajuste a região/Ceq ou rejeite a corrida.");
            }
            analysis.RevisionNumber = Math.Max(_currentRun.AnalysisHistory.Select(a => a.RevisionNumber).DefaultIfEmpty(0).Max(),
                _store.LoadRunAnalysis(_currentTest.FolderName, _currentRun.FolderName)?.RevisionNumber ?? 0) + 1;
            analysis.Outcome = (analysis.Outcome ?? KlaRunOutcome.FromLegacy(analysis.Quality)) with
            {
                OperatorDecision = KlaOperatorDecision.Accepted,
                Restoration = _currentRun.Outcome?.Restoration ?? analysis.EffectiveOutcome.Restoration,
            };
            _currentRun.Outcome = analysis.Outcome;
            _currentRun.LatestAnalysis = analysis;
            _currentRun.CompletedUtc = _time.GetUtcNow();
            _currentRun.CurrentPhase = RunPhase.Accepted;
            _currentRun.RemovalSeconds = KlaSequence.RemovalExposure(_runPoints);

            // Save raw data and analysis. The store returns the hash the file will carry, so the
            // seal does not wait for the queued write.
            analysis.RawDataSha256 = _store.SaveRunRawData(_currentTest.FolderName, _currentRun.FolderName, _runPoints);
            _currentRun.AnalysisHistory.Add(KlaTestFileContracts.DeserializeAnalysis(KlaTestFileContracts.SerializeAnalysis(analysis))!);

            _store.SaveRunAnalysis(_currentTest.FolderName, _currentRun.FolderName, analysis);
            _store.SaveRunResult(_currentTest.FolderName, _currentRun.FolderName, _currentRun, analysis);

            _currentTest.Runs.RemoveAll(r => r.RunId == _currentRun.RunId);
            _currentTest.Runs.Add(new KlaTestRunSummary
            {
                RunId = _currentRun.RunId,
                AttemptNumber = _currentRun.AttemptNumber,
                Context = _currentRun.Context,
                RemovalSeconds = KlaSequence.RemovalExposure(_runPoints),
                ConditionId = _currentRun.ConditionId,
                ReplicateNumber = _currentRun.ReplicateNumber,
                FolderName = _currentRun.FolderName,
                AgitationRpm = _currentRun.AgitationRpm,
                AirflowLpm = _currentRun.AirflowLpm,
                Phase = RunPhase.Accepted,
                Definition = _currentRun.Definition,
                Outcome = analysis.Outcome,
                Decision = analysis.Quality,
                KlaPerHour = analysis.KlaPerHour,
                AnalysisR2 = analysis.AnalysisR2,
                StartedUtc = _currentRun.StartedUtc,
                CompletedUtc = _currentRun.CompletedUtc,
                SwitchRelativeSeconds = _currentRun.SwitchRelativeSeconds,
                SwitchFlowRateLpm = _currentRun.SwitchFlowRateLpm,
                SwitchDoPercent = _currentRun.SwitchDoPercent,
            });

            KlaSequence.RefreshCounters(_currentTest, _currentCondition);
            _store.SaveConditionsTable(_currentTest.FolderName, _currentTest.Conditions);
            _store.SaveTestManifest(_currentTest);
            _store.UpdateResultsSummary(_currentTest.FolderName, _currentTest);

            // The accepted state is only announced after every queued raw-data and
            // analysis write has reached the filesystem.
            _store.FlushAsync().GetAwaiter().GetResult();

            SetPhase(RunPhase.Accepted, $"Corrida {_currentRun.FolderName} aceita (kLa = {analysis.KlaPerHour:F1} h⁻¹).");
        }

        LogEvent("RunAccepted", $"Corrida {_currentRun.FolderName} aceita com kLa = {analysis.KlaPerHour:F1} h⁻¹ (R² = {analysis.AnalysisR2:F4}).");
        return Task.CompletedTask;
    }

    public Task RejectRunAsync(string reason)
    {
        EnsureBioticReviewReady();
        if (_currentTest is null || _currentRun is null || _currentCondition is null)
        {
            throw new InvalidOperationException("Nenhuma corrida ativa para rejeitar.");
        }

        lock (_gate)
        {
            var analysis = _currentRun.LatestAnalysis is { } latest
                ? KlaTestFileContracts.DeserializeAnalysis(KlaTestFileContracts.SerializeAnalysis(latest))!
                : new KlaAnalysisRevision
            {
                Quality = DecisionQuality.Inconclusive,
                RejectionReason = reason,
                TStartSeconds = 0,
                TEndSeconds = PhaseElapsedSeconds,
            };

            analysis.Quality = DecisionQuality.Inconclusive;
            analysis.RevisionNumber = Math.Max(_currentRun.AnalysisHistory.Select(a => a.RevisionNumber).DefaultIfEmpty(0).Max(),
                _store.LoadRunAnalysis(_currentTest.FolderName, _currentRun.FolderName)?.RevisionNumber ?? 0) + 1;
            analysis.RejectionReason = reason;
            analysis.Outcome = (analysis.Outcome ?? KlaRunOutcome.FromLegacy(analysis.Quality)) with
            {
                OperatorDecision = KlaOperatorDecision.Rejected,
                Restoration = _currentRun.Outcome?.Restoration ?? analysis.EffectiveOutcome.Restoration,
            };
            _currentRun.Outcome = analysis.Outcome;
            _currentRun.LatestAnalysis = analysis;
            _currentRun.CompletedUtc = _time.GetUtcNow();
            _currentRun.CurrentPhase = RunPhase.Rejected;
            _currentRun.RemovalSeconds = KlaSequence.RemovalExposure(_runPoints);

            analysis.RawDataSha256 = _store.SaveRunRawData(_currentTest.FolderName, _currentRun.FolderName, _runPoints);
            _currentRun.AnalysisHistory.Add(KlaTestFileContracts.DeserializeAnalysis(KlaTestFileContracts.SerializeAnalysis(analysis))!);

            _store.SaveRunAnalysis(_currentTest.FolderName, _currentRun.FolderName, analysis);
            _store.SaveRunResult(_currentTest.FolderName, _currentRun.FolderName, _currentRun, analysis);

            _currentTest.Runs.RemoveAll(r => r.RunId == _currentRun.RunId);
            _currentTest.Runs.Add(new KlaTestRunSummary
            {
                RunId = _currentRun.RunId,
                AttemptNumber = _currentRun.AttemptNumber,
                Context = _currentRun.Context,
                RemovalSeconds = KlaSequence.RemovalExposure(_runPoints),
                ConditionId = _currentRun.ConditionId,
                ReplicateNumber = _currentRun.ReplicateNumber,
                FolderName = _currentRun.FolderName,
                AgitationRpm = _currentRun.AgitationRpm,
                AirflowLpm = _currentRun.AirflowLpm,
                Phase = RunPhase.Rejected,
                Definition = _currentRun.Definition,
                Outcome = analysis.Outcome,
                Decision = DecisionQuality.Inconclusive,
                KlaPerHour = null,
                AnalysisR2 = null,
                StartedUtc = _currentRun.StartedUtc,
                CompletedUtc = _currentRun.CompletedUtc,
                SwitchRelativeSeconds = _currentRun.SwitchRelativeSeconds,
                SwitchFlowRateLpm = _currentRun.SwitchFlowRateLpm,
                SwitchDoPercent = _currentRun.SwitchDoPercent,
            });

            KlaSequence.RefreshCounters(_currentTest, _currentCondition);
            _store.SaveConditionsTable(_currentTest.FolderName, _currentTest.Conditions);
            _store.SaveTestManifest(_currentTest);
            _store.UpdateResultsSummary(_currentTest.FolderName, _currentTest);

            _store.FlushAsync().GetAwaiter().GetResult();

            SetPhase(RunPhase.Rejected, $"Corrida {_currentRun.FolderName} rejeitada: {reason}.");
        }

        LogEvent("RunRejected", $"Corrida {_currentRun.FolderName} rejeitada: {reason}");
        return Task.CompletedTask;
    }

    public async Task RepeatRunAsync()
    {
        if (_currentCondition is null)
        {
            return;
        }

        var sameReplicate = _currentRun?.ReplicateNumber ?? Math.Max(1, _currentCondition.CompletedReplicates + 1);
        if (_phase == RunPhase.Reviewing)
        {
            await RejectRunAsync("Repetição solicitada pelo operador");
        }
        await StartRunAsync(_currentCondition, sameReplicate);
    }

    public Task CompleteTestAsync()
    {
        if (IsBiotic)
        {
            if (_currentRun is null)
            {
                _currentTest!.Status = KlaTestStatus.Completed;
                _currentTest.CompletedUtc = _time.GetUtcNow();
                _store.SaveTestManifest(_currentTest);
                SetPhase(RunPhase.Completed, "Sessão concluída sem corrida; cultivo não perturbado.");
                return Task.CompletedTask;
            }
            _completeAfterClosing = true;
            _abortAfterClosing = false;
            BeginCultivationRestoration("Teste concluído");
            return Task.CompletedTask;
        }
        lock (_gate)
        {
            if (_currentTest is null)
            {
                return Task.CompletedTask;
            }

            if (_arbiter.OwnerOf(ActuatorId.Aeration) != CommandOwner.KlaAssay ||
                _arbiter.OwnerOf(ActuatorId.Agitation) != CommandOwner.KlaAssay)
            {
                _assayCoordinator.Validate();
                if (_arbiter.OwnerOf(ActuatorId.Aeration) != CommandOwner.Manual ||
                    _arbiter.OwnerOf(ActuatorId.Agitation) != CommandOwner.Manual)
                {
                    throw new InvalidOperationException("Conclua o controle concorrente antes de encerrar o ensaio abiótico.");
                }
                _assayCoordinator.Acquire("Encerramento do ensaio abiótico");
            }

            _completeAfterClosing = true;
            _abortAfterClosing = false;
            _terminalReason = "Teste concluído";
            SetPhase(RunPhase.Aborting, "Encerrando: fechando todas as válvulas...");
            DispatchMotorOrAbort(0, "parar agitação");
            DispatchFlowOrAbort(CommandBuilders.FlowSafeStop(MaxFlow), "encerrar o teste");
        }
        return Task.CompletedTask;
    }

    public Task AbortTestAsync(string reason)
    {
        if (IsBiotic)
        {
            _abortAfterClosing = true;
            _completeAfterClosing = false;
            if (_currentTest is not null)
            {
                _currentTest.Status = KlaTestStatus.Interrupted;
                _currentTest.InterruptionReason = reason;
            }
            BeginCultivationRestoration(reason);
            return Task.CompletedTask;
        }
        lock (_gate)
        {
            if (_currentTest is not null)
            {
                _currentTest.Status = KlaTestStatus.Interrupted;
                _currentTest.InterruptionReason = reason;
                foreach (var cond in _currentTest.Conditions)
                {
                    if (cond.Status == ConditionStatus.InProgress)
                    {
                        cond.Status = cond.AcceptedReplicates >= cond.RequestedReplicates
                            ? ConditionStatus.Completed
                            : ConditionStatus.Pending;
                    }
                }
                foreach (var condition in _currentTest.Conditions) KlaSequence.RefreshCounters(_currentTest, condition);
                _store.SaveConditionsTable(_currentTest.FolderName, _currentTest.Conditions);
                _store.SaveTestManifest(_currentTest);
            }
            _completeAfterClosing = false;
            _abortAfterClosing = true;
            _terminalReason = reason;

            if (_device.State == ConnectionState.Connected && _lastFlowmeterOnline &&
                _arbiter.OwnerOf(ActuatorId.Aeration) == CommandOwner.KlaAssay)
            {
                SetPhase(RunPhase.Aborting, $"Abortando: fechando válvulas — {reason}");
                _arbiter.Dispatch(CommandOwner.KlaAssay, CommandBuilders.MotorSetpoint(0));
                DispatchFlowOrAbort(CommandBuilders.FlowSafeStop(MaxFlow), "abortar o teste");
            }
            else
            {
                _assayCoordinator.Release(false, 0, 0, $"Abortado sem confirmação por perda de comunicação: {reason}");
                SetPhase(RunPhase.Faulted, $"Teste abortado sem confirmação de fechamento: {reason}");
            }
        }

        LogEvent("TestAborted", $"Teste abortado: {reason}");
        return Task.CompletedTask;
    }

    public void UpdateLiveSettings(KlaTestSettings settings)
    {
        if (_preparationPending) throw new InvalidOperationException("Preparação persistida em andamento.");
        if (_recipeAcquisitionSealed) throw new InvalidOperationException("Configuração da tentativa encerrada é imutável.");
        ValidateSettings(settings);
        lock (_gate)
        {
            if (_currentTest is null)
            {
                return;
            }

            _currentTest.Settings = settings;
            _currentTest.SettingsRevision++;
            _store.SaveTestManifest(_currentTest);

            // React immediately if thresholds crossed
            if (_phase == RunPhase.Deoxygenating && _currentDO <= PrestageTriggerPercent)
            {
                BeginAirPrestage();
            }
            else if (_phase == RunPhase.Reoxygenating && _currentDO >= settings.DOMaxPercent)
            {
                StopRunAndReviewAsync("DO máxima atingida após ajuste de limiar");
            }
        }

        LogEvent("SettingsUpdated", $"Limiares atualizados: DOMin={settings.DOMinPercent:F1}%, DOMax={settings.DOMaxPercent:F1}%.");
    }

    public void SetDegassingAgitation(double rpm)
    {
        if (_preparationPending) throw new InvalidOperationException("Preparação persistida em andamento.");
        if (_recipeAcquisitionSealed) throw new InvalidOperationException("Configuração da tentativa encerrada é imutável.");
        if (!double.IsFinite(rpm) || rpm <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rpm));
        }
        lock (_gate)
        {
            if (_currentTest is null)
            {
                return;
            }

            _currentTest.Settings = _currentTest.Settings with { DegassingAgitationRpm = rpm };
            if (_currentTest.ProtocolSettings is { } protocol)
            {
                _currentTest.ProtocolSettings = protocol with { OxygenRemovalAgitationRpm = rpm };
            }
            _store.SaveTestManifest(_currentTest);

            if (_phase is RunPhase.Deoxygenating or RunPhase.PrestagingAir)
            {
                var clampedRpm = _routeCoordinator.ClampRpm(rpm);
                _arbiter.Dispatch(CommandOwner.KlaAssay, CommandBuilders.MotorSetpoint((int)clampedRpm));
            }
        }
    }

    /// <summary>A reading the Hub actually sent, or null when it never arrived.</summary>
    /// <remarks>
    /// The wire writes <see cref="SensorReadings.NotReceived"/> for a channel this module does
    /// not carry, and that sentinel is negative precisely so it can never be mistaken for a
    /// measurement. Mapping it to null here keeps the sentinel out of the recorded file.
    /// </remarks>
    private static double? OptionalReading(double value) =>
        double.IsFinite(value) && value > SensorReadings.NotReceived ? value : null;

    /// <summary>
    /// The shaft speed the servo reported, or null on a bench without one.
    /// </summary>
    /// <remarks>
    /// A kLa run commands agitation but never verifies it, so the assay's own record could not
    /// show whether the condition it reports was the condition the vessel ran. Recording the
    /// measurement alongside the setpoint is what makes that checkable afterwards - and null,
    /// not zero, when this module has no servo, since 0 rpm is a real and different state.
    /// </remarks>
    private static double? MeasuredRpm(SensorSnapshot snapshot) =>
        snapshot.HasServoTelemetry && snapshot.HasServoSample && snapshot.ServoOnline
            ? OptionalReading(snapshot.ServoRpm)
            : null;

    private double MaxFlow => _settings.Current.Setpoints.MaxFlowLitresPerMinute;

    /// <summary>The A/B/C wiring in force; read at each dispatch so a Configurações change applies to the next run.</summary>
    private GasRigConfiguration Rig => _settings.Current.GasRig.ToConfiguration();

    /// <summary>
    /// Builds the flow frame for <paramref name="route"/> and records the exact pair the
    /// flowmeter must echo before the phase advances. The only place this runner touches the
    /// valves: the rig has no default path, so every setpoint carries its destination.
    /// </summary>
    private OpenTECCommand RouteFrame(double flow, GasRoute route)
    {
        var rig = _currentTest?.GasRig?.ToConfiguration() ?? Rig;
        var (v1, v2) = GasRouting.Resolve(route, rig);
        _targetGasState = (flow, v1, v2, flow <= 0.0);
        return CommandBuilders.FlowRoute(flow, MaxFlow, route, rig);
    }

    private void OnTelemetryReceived(SensorSnapshot s)
    {
        if (_recipeAcquisitionSealed) return;
        _lastTelemetryMonotonic = GetMonotonicSeconds();
        if (s.OxygenUpdated)
        {
            _lastOxygenRejected = !double.IsFinite(s.OxygenCalibrated) || s.OxygenCalibrated < 0 ||
                !double.IsFinite(s.OxygenRaw) || s.OxygenRaw < 0;
        }
        var newOxygen = s.OxygenUpdated && double.IsFinite(s.OxygenCalibrated) && s.OxygenCalibrated >= 0 &&
            double.IsFinite(s.OxygenRaw) && s.OxygenRaw >= 0 &&
            (!_hasOxygenSample || _lastOxygenMonotonic < _lastTelemetryMonotonic);
        if (newOxygen)
        {
            _hasOxygenSample = true;
            _lastOxygenMonotonic = _lastTelemetryMonotonic;
            _currentDO = s.OxygenCalibrated;
            _currentDORaw = s.OxygenRaw;
            _initialOxygenHistory.Add((_lastOxygenMonotonic, _currentDO));
            _initialOxygenHistory.RemoveAll(x => _lastTelemetryMonotonic - x.Time > 120);
        }
        _currentFlowMeasured = s.FlowRate;
        _lastFlowmeterOnline = s.FlowmeterOnline;
        _lastFlowCommandId = s.FlowCommandId;

        if (_preparationPending || _currentTest is null || _phase is RunPhase.Idle or RunPhase.Completed or RunPhase.Faulted)
        {
            return;
        }

        if (!s.FlowmeterOnline && _phase != RunPhase.Aborting)
        {
            _ = AbortTestAsync("Fluxômetro offline durante o teste.");
            return;
        }

        lock (_gate)
        {
            var nowUtc = _time.GetUtcNow();
            if (_currentRun?.Acquisition is { } acquisition &&
                (_settings.Current.Calibration.OxygenA != acquisition.OxygenCalibrationA ||
                 _settings.Current.Calibration.OxygenB != acquisition.OxygenCalibrationB))
            {
                _ = AbortTestAsync("Calibração de oxigênio mudou durante a corrida.");
                return;
            }
            var relSec = Math.Max(0, GetMonotonicSeconds() - _runStartMonotonic);
            var monoSec = GetMonotonicSeconds();

            var v1 = s.FlowValve1 != 0;
            var v2 = s.FlowValve2 != 0;
            var vFlow = s.FlowValveMain != 0;
            var agitationSetpoint = _phase is RunPhase.OpeningNitrogen or RunPhase.Deoxygenating or RunPhase.PrestagingAir or
                RunPhase.DivertingAir or RunPhase.MeasuringConsumption
                ? RemovalRpm
                : _phase == RunPhase.RestoringCultivation ? ReturnRpm : _currentCondition?.AgitationRpm ?? 0;

            var temperature = OptionalReading(s.Temperature);
            var rpmMeasured = MeasuredRpm(s);

            var rawPt = new KlaRawDataPoint(
                TimestampUtc: nowUtc,
                RelativeSeconds: relSec,
                Phase: _phase,
                DORaw: s.OxygenRaw,
                DOFiltered: s.OxygenCalibrated,
                FlowMeasured: s.FlowRate,
                FlowSetpoint: s.FlowSetpoint,
                AgitationSetpoint: agitationSetpoint,
                Valve1: v1,
                Valve2: v2,
                VFlow: vFlow,
                TemperatureC: temperature,
                RpmMeasured: rpmMeasured);

            var capturesRunData = _phase is RunPhase.Preflight or RunPhase.ClosingAllGas or
                RunPhase.OpeningNitrogen or RunPhase.Deoxygenating or RunPhase.PrestagingAir or
                RunPhase.SwitchingToReactor or RunPhase.Reoxygenating or RunPhase.StoppingRun or RunPhase.Aborting;
            if ((capturesRunData || _phase is RunPhase.DivertingAir or RunPhase.MeasuringConsumption or RunPhase.RestoringCultivation) && newOxygen)
            {
                _runPoints.Add(rawPt);
                if (_currentRun is not null)
                {
                    _store.AppendRunRawDataPoint(_currentTest.FolderName, _currentRun.FolderName, rawPt);
                }
                DataPointAdded?.Invoke(rawPt);
            }

            var sample = new KlaGlobalSeriesSample(
                TimestampUtc: nowUtc,
                MonotonicSeconds: Math.Max(0, monoSec - _testStartMonotonic),
                TestId: _currentTest.TestId,
                RunId: _currentRun?.RunId,
                ConditionId: _currentCondition?.ConditionId,
                Replicate: _currentRun?.ReplicateNumber,
                Phase: _phase,
                DORaw: s.OxygenRaw,
                DOFiltered: s.OxygenCalibrated,
                DOMin: _currentTest.Settings.DOMinPercent,
                DOMax: _currentTest.Settings.DOMaxPercent,
                FlowMeasured: s.FlowRate,
                FlowSetpoint: s.FlowSetpoint,
                AgitationSetpoint: agitationSetpoint,
                Valve1: v1,
                Valve2: v2,
                VFlow: vFlow,
                CommandId: s.FlowCommandId,
                CommandAck: s.FlowCommandAck,
                CommandPending: s.FlowCommandPending,
                SettingsRevision: _currentTest.SettingsRevision,
                EventCode: _phase.ToString(),
                EventDetail: _statusMessage,
                TemperatureC: temperature,
                RpmMeasured: rpmMeasured);

            if (newOxygen)
            {
                _globalSamples.Add(sample);
                _store.AppendGlobalSeriesSample(_currentTest.FolderName, sample);
            }
            else
            {
                LogEvent("TelemetryWithoutNewOxygen", $"Sem OD novo; fase {_phase}; Q={s.FlowRate}; cmd={s.FlowCommandId}; ack={s.FlowCommandAck}.");
            }

            if (IsBiotic)
            {
                try
                {
                    if (_phase is RunPhase.DivertingAir or RunPhase.MeasuringConsumption or RunPhase.SwitchingToReactor or RunPhase.Reoxygenating or RunPhase.RestoringCultivation &&
                        (_arbiter.OwnerOf(ActuatorId.Aeration) != CommandOwner.KlaAssay ||
                         _arbiter.OwnerOf(ActuatorId.Agitation) != CommandOwner.KlaAssay))
                    {
                        FailCultivationRestoration("Posse dos atuadores perdida durante a aquisição.");
                    }
                    else
                    {
                        EvaluateBioticTelemetry(s, newOxygen, monoSec, relSec);
                    }
                }
                catch (Exception error)
                {
                    BeginCultivationRestoration($"Falha de comando: {error.Message}");
                }
                RaiseStateChanged();
                return;
            }

            // Phase State Transitions
            if (_phase == RunPhase.ClosingAllGas && IsGasStateConfirmed(s, (0, false, false, true)))
            {
                if (_startAtFloor)
                {
                    BeginAirPrestage();
                }
                else
                {
                    OpenNitrogen();
                }
            }
            else if (_phase == RunPhase.StoppingRun && IsGasStateConfirmed(s, (0, false, false, true)))
            {
                _assayCoordinator.Release(true, 0, 0, "Revisão abiótica após fechamento confirmado");
                SetPhase(RunPhase.Reviewing, "Válvulas fechadas. Revise e classifique a corrida.");
            }
            else if (_phase == RunPhase.Aborting && IsGasStateConfirmed(s, (0, false, false, true)))
            {
                FinalizeTerminalClose();
            }
            else if (_phase == RunPhase.OpeningNitrogen && IsGasStateConfirmed(s, _targetGasState))
            {
                RecordGasEvent(relSec, KlaGasEventKind.GasOffConfirmed, $"N2 route; cmd={s.FlowCommandAck}");
                SetPhase(RunPhase.Deoxygenating, $"Desoxigenação com N₂ em andamento (DO = {s.OxygenCalibrated:F1}%)...");
            }
            else if (_phase == RunPhase.PrestagingAir && newOxygen)
            {
                EvaluateAirPrestage(s, monoSec);
            }
            else if (_phase == RunPhase.SwitchingToReactor && IsGasStateConfirmed(s, _targetGasState))
            {
                ConfirmSwitchToReactor(s, relSec);
            }
            else if (_phase == RunPhase.Deoxygenating && newOxygen && s.OxygenCalibrated <= PrestageTriggerPercent)
            {
                BeginAirPrestage();
            }
            else if (_phase == RunPhase.Reoxygenating && newOxygen && s.OxygenCalibrated >= _currentTest.Settings.DOMaxPercent)
            {
                StopRunAndReviewAsync("DO máxima atingida com sucesso");
            }

            RaiseStateChanged();
        }
    }

    internal static double? TryCalculateSlope(IReadOnlyList<(double Time, double DO)> points, double requestedSpan)
    {
        if (points.Count < 2 || points[^1].Time - points[0].Time < requestedSpan * 0.8)
        {
            return null;
        }

        var meanT = points.Average(p => p.Time);
        var meanDO = points.Average(p => p.DO);
        var denominator = points.Sum(p => Math.Pow(p.Time - meanT, 2));
        if (denominator <= 1e-9)
        {
            return null;
        }

        return points.Sum(p => (p.Time - meanT) * (p.DO - meanDO)) / denominator;
    }

    private void ResetStabilityDetection()
    {
        _stabilityWindow.Clear();
        _currentDODerivative = null;
        _stabilityConfirmationCount = 0;
        _prestageFlowStableCount = 0;
        _prestageFlowDeviation = null;
        _prestageFlowWindow.Clear();
        _prestageConfirmed = false;
    }

    private bool IsGasStateConfirmed(SensorSnapshot s, (double Flow, bool V1, bool V2, bool VFlow) target)
    {
        var flowOk = Math.Abs(s.FlowSetpoint - target.Flow) < 0.1;
        var v1Ok = (s.FlowValve1 != 0) == target.V1;
        var v2Ok = (s.FlowValve2 != 0) == target.V2;
        var vFlowOk = (s.FlowValveMain != 0) == target.VFlow;
        var ackOk = s.FlowmeterOnline && !s.FlowCommandPending &&
                    s.FlowCommandId >= _minimumExpectedFlowCommandId &&
                    s.FlowCommandAck == s.FlowCommandId;

        var confirmed = flowOk && v1Ok && v2Ok && vFlowOk && ackOk;
        if (confirmed && s.FlowCommandId != _lastLoggedConfirmation)
        {
            _lastLoggedConfirmation = s.FlowCommandId;
            LogEvent("GasCommandConfirmed", $"cmd={s.FlowCommandId}; latency={GetMonotonicSeconds() - _flowRequestedMonotonic:F3}s; " +
                $"t={GetMonotonicSeconds() - _runStartMonotonic:F3}s.");
        }
        return confirmed;
    }

    private bool _lastFlowmeterOnline;

    private void OpenNitrogen()
    {
        // N₂ enters through B, which shares its output with C: setpoint 0 keeps the air shut.
        var frame = RouteFrame(0, GasRoute.VentAndNitrogen);
        SetPhase(RunPhase.OpeningNitrogen, $"Abrindo N₂ — {GasRouting.Describe(GasRoute.VentAndNitrogen, Rig)}...");
        DispatchMotorOrAbort((int)RemovalRpm, "ajustar agitação de desoxigenação");
        DispatchFlowOrAbort(frame, "abrir nitrogênio");
    }

    /// <summary>
    /// Entry point for every path that reaches the DO floor: the assay airflow is requested on the
    /// <em>same</em> B/C route, so the meter's start-up pulse and its settling go out of C while
    /// the N₂ keeps stripping. Nothing enters the reactor until the flow and the floor are both
    /// still. A run that starts at the floor comes here straight from the interlock.
    /// </summary>
    private void BeginAirPrestage()
    {
        var targetFlow = _currentCondition!.AirflowLpm;
        ResetStabilityDetection();
        var frame = RouteFrame(targetFlow, GasRoute.VentAndNitrogen);
        LogEvent(
            "AirPrestageStarted",
            (_startAtFloor
                ? $"DO já no piso ({_currentDO:F1}% ≤ {_currentTest!.Settings.DOMinPercent:F1}%): sem fase de N₂. "
                : $"DO atingiu {_currentDO:F1}% (gatilho {PrestageTriggerPercent:F1}%). ") +
            $"Pedindo {targetFlow:F2} L/min por C com o N₂ ainda aberto; o reator só recebe ar com a vazão e o piso assentados.");
        SetPhase(RunPhase.PrestagingAir, $"Ar por C a {targetFlow:F2} L/min · aguardando o eco do fluxômetro...");
        DispatchMotorOrAbort((int)RemovalRpm, "manter a agitação de desoxigenação");
        DispatchFlowOrAbort(frame, "pré-estabilizar o ar por C");
    }

    /// <summary>DO at which the air is pre-staged: the floor plus the configured lead.</summary>
    private double PrestageTriggerPercent =>
        _currentTest!.Settings.DOMinPercent + Math.Max(0.0, _currentTest.Settings.AirPrestageLeadPercent);

    /// <summary>
    /// Holds the run on C until two independent things are still at the same time: the measured
    /// flow (inside the band for N frames, or settled per <see cref="FlowSettling"/>) and the DO
    /// floor (at or below <c>DOMin</c> with a flat derivative for the configured confirmations).
    /// Only then is the one-frame switch to A commanded.
    /// </summary>
    private void EvaluateAirPrestage(SensorSnapshot s, double monotonicSeconds)
    {
        var settings = _currentTest!.Settings;
        var targetFlow = _currentCondition!.AirflowLpm;

        if (!_prestageConfirmed)
        {
            if (!IsGasStateConfirmed(s, _targetGasState))
            {
                _statusMessage = $"Ar por C a {targetFlow:F2} L/min · aguardando o eco do fluxômetro...";
                return;
            }

            _prestageConfirmed = true;
            LogEvent("AirPrestaged", $"B/C confirmada com {targetFlow:F2} L/min: o ar sai por C enquanto o N₂ segura o piso.");
        }

        // (a) Flow: in band for N consecutive frames, or settled near the target.
        var deviation = s.FlowRate - targetFlow;
        _prestageFlowDeviation = deviation;
        _prestageFlowStableCount = Math.Abs(deviation) <= settings.PrestageFlowToleranceLpm ? _prestageFlowStableCount + 1 : 0;
        FlowSettling.Push(_prestageFlowWindow, s.FlowRate, settings.PrestageFlowStableSamples);
        var settled = FlowSettling.HasSettled(
            _prestageFlowWindow, targetFlow, settings.PrestageFlowStableSamples,
            settings.PrestageFlowStabilityStdDevLpm, settings.PrestageFlowStabilityMaxErrorLpm, out var spread);
        var flowReady = _prestageFlowStableCount >= settings.PrestageFlowStableSamples || settled;

        // (b) DO floor: at or below DOMin with a flat derivative, for the configured confirmations.
        _stabilityWindow.Add((monotonicSeconds, s.OxygenCalibrated));
        var span = settings.StabilityDerivativeSpanSeconds;
        _stabilityWindow.RemoveAll(pt => pt.Time < monotonicSeconds - Math.Max(30.0, span * 2.0));
        // A hair of slack on the window edge: a frame exactly one span old must count, and the
        // monotonic seconds are a double built from ticks.
        var oldest = monotonicSeconds - span - 0.01;
        _currentDODerivative = TryCalculateSlope(_stabilityWindow.Where(pt => pt.Time >= oldest).ToList(), span);
        var atFloor = s.OxygenCalibrated <= settings.DOMinPercent;
        var flat = _currentDODerivative.HasValue &&
                   Math.Abs(_currentDODerivative.Value) <= settings.StabilityDerivativeThresholdPercentPerSecond;
        _stabilityConfirmationCount = atFloor && flat ? _stabilityConfirmationCount + 1 : 0;
        var doReady = _stabilityConfirmationCount >= settings.StabilityRequiredSamples;

        var derivativeText = _currentDODerivative.HasValue ? $"{_currentDODerivative.Value:+0.000;-0.000;0.000} %/s" : "janela…";
        _statusMessage =
            $"Ar por C a {targetFlow:F2} L/min ({deviation:+0.00;-0.00;0.00}) · DO {s.OxygenCalibrated:F1}% (dDO/dt {derivativeText}) · " +
            $"vazão {_prestageFlowStableCount}/{settings.PrestageFlowStableSamples}" + (settled ? " (assentada)" : "") +
            $" · sonda {_stabilityConfirmationCount}/{settings.StabilityRequiredSamples}";

        if (!flowReady || !doReady)
        {
            return;
        }

        var how = _prestageFlowStableCount >= settings.PrestageFlowStableSamples
            ? "dentro da banda"
            : $"assentada (σ {spread:F3} L/min)";
        LogEvent(
            "PrestageStable",
            $"Vazão {s.FlowRate:F2} L/min {how} e DO no piso ({s.OxygenCalibrated:F1}%, dDO/dt {derivativeText}). " +
            "Fechando B/C e abrindo A em uma frame.");
        SwitchToReactor();
    }

    /// <summary>
    /// The only frame that ever moves gas into the reactor: B/C off and A on in one JSON, with
    /// the setpoint the meter already holds. The condition's rotation arrives with it.
    /// </summary>
    private void SwitchToReactor()
    {
        var targetFlow = _currentCondition!.AirflowLpm;
        var frame = RouteFrame(targetFlow, GasRoute.Reactor);
        SetPhase(RunPhase.SwitchingToReactor, $"Comutando para o reator — {GasRouting.Describe(GasRoute.Reactor, Rig)}...");
        DispatchMotorOrAbort((int)_currentCondition.AgitationRpm, "ajustar agitação de reoxigenação");
        DispatchFlowOrAbort(frame, "abrir ar ao reator (A)");
    }

    /// <summary>The flowmeter echoed A open and B/C closed: this frame is <c>t = 0</c>.</summary>
    private void ConfirmSwitchToReactor(SensorSnapshot s, double relativeSeconds)
    {
        RecordGasEvent(relativeSeconds, KlaGasEventKind.GasOnConfirmed, $"Reactor route; cmd={s.FlowCommandAck}");
        if (_currentRun is not null)
        {
            _currentRun.SwitchRelativeSeconds = relativeSeconds;
            _currentRun.SwitchFlowRateLpm = s.FlowRate;
            _currentRun.SwitchDoPercent = s.OxygenCalibrated;
        }

        LogEvent(
            "SwitchedToReactor",
            $"A confirmada aberta, B/C fechadas: t = 0 aos {relativeSeconds:F1} s da corrida, " +
            $"vazão {s.FlowRate:F2} L/min, DO inicial {s.OxygenCalibrated:F1}%.");
        SetPhase(RunPhase.Reoxygenating, $"Reoxigenação com ar em andamento (DO = {s.OxygenCalibrated:F1}%)...");
    }

    private void DispatchFlowOrAbort(OpenTECCommand command, string action)
    {
        _minimumExpectedFlowCommandId = _lastFlowCommandId + 1;
        _flowRequestedMonotonic = GetMonotonicSeconds();
        LogEvent("GasCommandRequested", $"{action}; minCmd={_minimumExpectedFlowCommandId}; t={_flowRequestedMonotonic - _runStartMonotonic:F3}s.");
        var result = _arbiter.Dispatch(CommandOwner.KlaAssay, command);
        if (!result.Accepted)
        {
            throw new InvalidOperationException($"Comando recusado ao tentar {action}.");
        }
    }

    private void DispatchMotorOrAbort(int rpm, string action)
    {
        if (_routeCoordinator.IsUartFallback && rpm > MotorRouteCoordinator.UartFallbackMaxRpm)
        {
            throw new InvalidOperationException(
                $"Em modo de fallback UART, a rotação máxima é de {MotorRouteCoordinator.UartFallbackMaxRpm:F0} rpm. " +
                $"Comando de {rpm} rpm ({action}) requer comunicação Modbus com o servo drive.");
        }

        var result = _arbiter.Dispatch(CommandOwner.KlaAssay, CommandBuilders.MotorSetpoint(rpm));
        if (!result.Accepted)
        {
            throw new InvalidOperationException($"Comando recusado ao tentar {action}.");
        }
    }

    private static void ValidateSettings(KlaTestSettings settings)
    {
        if (!double.IsFinite(settings.DOMinPercent) || !double.IsFinite(settings.DOMaxPercent) ||
            settings.DOMinPercent < 0 || settings.DOMaxPercent > 110 || settings.DOMinPercent >= settings.DOMaxPercent)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "DO mínima deve ser menor que DO máxima, dentro de 0–110%.");
        }

        if (!double.IsFinite(settings.DegassingAgitationRpm) || settings.DegassingAgitationRpm <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "A agitação de desoxigenação deve ser positiva.");
        }

        if (settings.SmoothingWindowSize is < 1 or > 101 || settings.SmoothingWindowSize % 2 == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "A janela de suavização deve ser ímpar, entre 1 e 101.");
        }

        if (!double.IsFinite(settings.MaxDegassingTimeMinutes) || settings.MaxDegassingTimeMinutes <= 0 ||
            !double.IsFinite(settings.MaxReoxygenationTimeMinutes) || settings.MaxReoxygenationTimeMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Os tempos máximos devem ser positivos.");
        }

        if (!double.IsFinite(settings.StabilityDerivativeSpanSeconds) || settings.StabilityDerivativeSpanSeconds <= 0 ||
            !double.IsFinite(settings.StabilityDerivativeThresholdPercentPerSecond) || settings.StabilityDerivativeThresholdPercentPerSecond <= 0 ||
            settings.StabilityRequiredSamples is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Os parâmetros de estabilidade da sonda no piso são inválidos.");
        }

        if (!double.IsFinite(settings.AirPrestageLeadPercent) || settings.AirPrestageLeadPercent < 0 ||
            settings.DOMinPercent + settings.AirPrestageLeadPercent >= settings.DOMaxPercent ||
            !double.IsFinite(settings.PrestageFlowToleranceLpm) || settings.PrestageFlowToleranceLpm <= 0 ||
            settings.PrestageFlowStableSamples is < 1 or > 100 ||
            !double.IsFinite(settings.PrestageFlowStabilityStdDevLpm) || settings.PrestageFlowStabilityStdDevLpm < 0 ||
            !double.IsFinite(settings.PrestageFlowStabilityMaxErrorLpm) || settings.PrestageFlowStabilityMaxErrorLpm < 0 ||
            !double.IsFinite(settings.MaxPrestageSeconds) || settings.MaxPrestageSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Os parâmetros da pré-estabilização do ar por C são inválidos.");
        }
    }

    private void OnDeviceStateChanged(ConnectionStateChange change)
    {
        if (_preparationPending) return;
        if (change.State != ConnectionState.Connected && IsBiotic && _phase == RunPhase.RestoringCultivation)
        {
            FailCultivationRestoration("Comunicação perdida durante a retomada.");
            return;
        }
        if (change.State != ConnectionState.Connected && IsRunning)
        {
            _ = AbortTestAsync($"Conexão perdida com o biorreator ({change.State}).");
        }
    }

    internal void CheckWatchdog()
    {
        if (_recipeAcquisitionSealed || _preparationPending) return;
        if (!IsRunning || _phase == RunPhase.Reviewing)
        {
            return;
        }
        var now = GetMonotonicSeconds();
        if (IsBiotic && _phase == RunPhase.RestoringCultivation)
        {
            if (_arbiter.OwnerOf(ActuatorId.Aeration) != CommandOwner.KlaAssay ||
                _arbiter.OwnerOf(ActuatorId.Agitation) != CommandOwner.KlaAssay)
            {
                FailCultivationRestoration("Posse dos atuadores perdida durante a retomada.");
                return;
            }
            if (PhaseElapsedSeconds > OperationalSettings.AerationReturn.MaximumRecoverySeconds)
            {
                FailCultivationRestoration("Tempo limite de retomada excedido.");
            }
            return;
        }
        if (IsBiotic && _phase is RunPhase.DivertingAir or RunPhase.MeasuringConsumption or RunPhase.SwitchingToReactor or RunPhase.Reoxygenating &&
            (_arbiter.OwnerOf(ActuatorId.Aeration) != CommandOwner.KlaAssay ||
             _arbiter.OwnerOf(ActuatorId.Agitation) != CommandOwner.KlaAssay))
        {
            FailCultivationRestoration("Posse dos atuadores perdida durante a aquisição.");
            return;
        }
        if (_phase is not (RunPhase.Aborting or RunPhase.StoppingRun) &&
            _hasOxygenSample && now - _lastOxygenMonotonic > OperationalSettings.OxygenSampleTimeoutSeconds)
        {
            _ = AbortTestAsync("Canal de oxigênio sem amostra nova dentro do tempo configurado.");
            return;
        }
        if (IsBiotic && _phase == RunPhase.MeasuringConsumption &&
            now - _gasOffMonotonic >= OperationalSettings.AerationReturn.MaximumGasOffSeconds)
        {
            SwitchToReactor();
            return;
        }
        if (_lastTelemetryMonotonic > 0 && now - _lastTelemetryMonotonic > 5)
        {
            if (_phase == RunPhase.Aborting)
            {
                _assayCoordinator.Release(false, 0, 0, "Sem telemetria para confirmar fechamento");
                SetPhase(RunPhase.Faulted, "Fechamento não confirmado por falta de telemetria.");
                return;
            }
            _ = AbortTestAsync("Telemetria ficou desatualizada por mais de 5 segundos.");
            return;
        }
        var awaitingEcho = _phase is RunPhase.ClosingAllGas or RunPhase.OpeningNitrogen or RunPhase.SwitchingToReactor or RunPhase.DivertingAir or
            RunPhase.StoppingRun or RunPhase.Aborting || (_phase == RunPhase.PrestagingAir && !_prestageConfirmed);
        if (awaitingEcho && PhaseElapsedSeconds > OperationalSettings.CommandConfirmationTimeoutSeconds)
        {
            if (_phase == RunPhase.Aborting)
            {
                _assayCoordinator.Release(false, 0, 0, "Tempo limite no fechamento; estado físico não confirmado");
                SetPhase(RunPhase.Faulted, "Falha: fechamento das válvulas não foi confirmado em 10 segundos.");
                return;
            }
            _ = AbortTestAsync("Tempo limite de 10 segundos aguardando confirmação do fluxômetro.");
            return;
        }
        if (_currentTest is null)
        {
            return;
        }
        if (_phase == RunPhase.Deoxygenating && PhaseElapsedSeconds > _currentTest.Settings.MaxDegassingTimeMinutes * 60)
        {
            _ = AbortTestAsync("Tempo máximo de desoxigenação excedido.");
        }
        else if (_phase == RunPhase.Reoxygenating && PhaseElapsedSeconds > _currentTest.Settings.MaxReoxygenationTimeMinutes * 60)
        {
            _ = StopRunAndReviewAsync("Tempo máximo de reoxigenação excedido");
        }
        else if (_phase == RunPhase.PrestagingAir && PhaseElapsedSeconds > _currentTest.Settings.MaxPrestageSeconds)
        {
            _ = StopRunAndReviewAsync(
                $"A pré-estabilização do ar por C não concluiu em {_currentTest.Settings.MaxPrestageSeconds:F0} s (vazão ou piso de DO sem assentar)");
        }
    }

    private void FinalizeTerminalClose()
    {
        if (_currentTest is null)
        {
            return;
        }

        _assayCoordinator.Release(true, 0, 0, _terminalReason);
        if (_completeAfterClosing)
        {
            _currentTest.Status = KlaTestStatus.Completed;
            _currentTest.CompletedUtc = _time.GetUtcNow();
            foreach (var cond in _currentTest.Conditions)
            {
                if (cond.Status == ConditionStatus.InProgress)
                {
                    cond.Status = cond.AcceptedReplicates >= cond.RequestedReplicates
                        ? ConditionStatus.Completed
                        : (cond.AcceptedReplicates > 0 ? ConditionStatus.Completed : ConditionStatus.Pending);
                }
            }
            foreach (var condition in _currentTest.Conditions) KlaSequence.RefreshCounters(_currentTest, condition);
            _store.SaveConditionsTable(_currentTest.FolderName, _currentTest.Conditions);
            _store.SaveTestManifest(_currentTest);
            _store.FlushAsync().GetAwaiter().GetResult();
            SetPhase(RunPhase.Completed, $"Teste '{_currentTest.Name}' concluído com válvulas confirmadas fechadas.");
            LogEvent("TestCompleted", $"Teste '{_currentTest.Name}' finalizado com fechamento confirmado.");
        }
        else if (_abortAfterClosing)
        {
            SetPhase(RunPhase.Faulted, $"Teste abortado com válvulas confirmadas fechadas: {_terminalReason}");
        }
        _completeAfterClosing = false;
        _abortAfterClosing = false;
    }

    private void SetPhase(RunPhase phase, string message)
    {
        _phase = phase;
        _phaseStartMonotonic = GetMonotonicSeconds();
        _statusMessage = message;
        if (_currentRun is not null)
        {
            _currentRun.CurrentPhase = phase;
        }
        LogEvent("PhaseChanged", $"{phase}; t={Math.Max(0, _phaseStartMonotonic - _runStartMonotonic):F3}s; {message}");
        RaiseStateChanged();
    }

    private void LogEvent(string eventType, string message)
    {
        _log.LogInformation("[KlaRunner] {EventType}: {Message}", eventType, message);
        Logged?.Invoke($"[{eventType}] {message}");

        if (_currentTest is not null)
        {
            _store.AppendEventLog(_currentTest.FolderName, new KlaTestEventLogEntry(
                TimestampUtc: _time.GetUtcNow(),
                EventType: eventType,
                Message: message));
        }
    }

    private double GetMonotonicSeconds() => _time.GetTimestamp() / (double)_time.TimestampFrequency;

    private void RaiseStateChanged() => StateChanged?.Invoke();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (_recipeReturnSnapshot is null && IsBiotic && _currentRun?.Outcome?.Restoration == KlaRestorationState.Pending)
        {
            BeginCultivationRestoration("Aplicativo encerrando");
            FailCultivationRestoration("Encerramento antes da confirmação física; conferir retomada manualmente.");
        }

        _device.TelemetryReceived -= OnTelemetryReceived;
        _store.WriteFailed -= OnStoreWriteFailed;
        _device.StateChanged -= OnDeviceStateChanged;
        _watchdog.Dispose();
    }
}
