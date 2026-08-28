using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TecnalHub.Protocol;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Persistence;

namespace TecnalHub.Services.KlaTesting;

public sealed class KlaTestRunner : IKlaTestRunner
{
    private readonly IDeviceService _device;
    private readonly ICommandArbiter _arbiter;
    private readonly IKlaTestStore _store;
    private readonly IKlaAnalysisEngine _analysisEngine;
    private readonly ISettingsService _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<KlaTestRunner> _log;
    private readonly ITimer _watchdog;

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
    private int _lastFlowCommandId;
    private int _minimumExpectedFlowCommandId;
    private bool _openAirAfterClosing;
    private bool _completeAfterClosing;
    private bool _abortAfterClosing;
    private string _terminalReason = "";
    private bool _disposed;
    private double? _currentDODerivative;
    private int _stabilityConfirmationCount;
    private int _ventFlowStableCount;
    private double? _ventFlowDeviation;

    public KlaTestRunner(
        IDeviceService device,
        ICommandArbiter arbiter,
        IKlaTestStore store,
        IKlaAnalysisEngine analysisEngine,
        ISettingsService settings,
        TimeProvider? time = null,
        ILogger<KlaTestRunner>? log = null)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _arbiter = arbiter ?? throw new ArgumentNullException(nameof(arbiter));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _analysisEngine = analysisEngine ?? throw new ArgumentNullException(nameof(analysisEngine));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger<KlaTestRunner>.Instance;

        _device.TelemetryReceived += OnTelemetryReceived;
        _device.StateChanged += OnDeviceStateChanged;
        _watchdog = _time.CreateTimer(_ => CheckWatchdog(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public KlaTestDocument? CurrentTest => _currentTest;
    public KlaTestRun? CurrentRun => _currentRun;
    public KlaTestCondition? CurrentCondition => _currentCondition;
    public RunPhase Phase => _phase;
    public bool IsRunning => _phase is not (RunPhase.Idle or RunPhase.Completed or RunPhase.Faulted);
    public bool IsInReview => _phase == RunPhase.Reviewing;
    public double CurrentDO => _currentDO;
    public double CurrentDORaw => _currentDORaw;
    public double CurrentFlowMeasured => _currentFlowMeasured;
    public double? CurrentDODerivative => _currentDODerivative;
    public int StabilityConfirmationCount => _stabilityConfirmationCount;
    public int VentFlowStableCount => _ventFlowStableCount;
    public double? VentFlowDeviation => _ventFlowDeviation;
    public string StatusMessage => _statusMessage;

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
        ArgumentNullException.ThrowIfNull(doc);
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

    public Task StartTestAsync(KlaTestDocument doc, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _currentTest = doc;
            _currentTest.Status = KlaTestStatus.Running;
            _currentTest.StartedUtc ??= _time.GetUtcNow();
            _store.SaveTestManifest(_currentTest);

            _testStartMonotonic = GetMonotonicSeconds();
            _phase = RunPhase.Idle;
            _statusMessage = $"Teste '{doc.Name}' ativo. Selecione uma condição para iniciar a corrida.";
        }

        LogEvent("TestStarted", $"Teste '{doc.Name}' iniciado.");
        RaiseStateChanged();
        return Task.CompletedTask;
    }

    public async Task StartRunAsync(KlaTestCondition condition, int replicateNumber, CancellationToken ct = default)
    {
        if (_currentTest is null)
        {
            throw new InvalidOperationException("Nenhum teste de kLa ativo.");
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
        if (!double.IsFinite(_currentDO) || _currentDO is < 0 or > 200)
        {
            throw new InvalidOperationException("Leitura de oxigênio inválida.");
        }
        ValidateSettings(_currentTest.Settings);
        if (_currentTest.Settings.VentStabilizationEnabled &&
            _currentTest.SelectedVentValve == _currentTest.SelectedNitrogenValve)
        {
            throw new InvalidOperationException(
                "A válvula de alívio deve ser diferente da válvula do N₂.");
        }
        if (condition.AgitationRpm <= 0 || condition.AirflowLpm <= 0 || replicateNumber < 1)
        {
            throw new InvalidOperationException("Condição inválida: rotação, vazão e replicata devem ser positivas.");
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

            _runPoints.Clear();
            ResetStabilityDetection();

            _currentRun = new KlaTestRun
            {
                TestId = _currentTest.TestId,
                ConditionId = condition.ConditionId,
                ReplicateNumber = replicateNumber,
                AgitationRpm = condition.AgitationRpm,
                AirflowLpm = condition.AirflowLpm,
                NitrogenValve = _currentTest.SelectedNitrogenValve,
                VentValve = _currentTest.SelectedVentValve,
                CurrentPhase = RunPhase.Preflight,
                StartedUtc = _time.GetUtcNow(),
            };

            var runFolder = _store.InitializeRunFolder(_currentTest.FolderName, _currentRun);
            _currentRun.FolderName = runFolder;

            _phase = RunPhase.Preflight;
            _phaseStartMonotonic = GetMonotonicSeconds();
            _runStartMonotonic = _phaseStartMonotonic;
            _statusMessage = $"Pré-voo da corrida {runFolder}...";
            _store.SaveTestManifest(_currentTest);
        }

        LogEvent("RunStarted", $"Iniciando corrida {_currentRun.FolderName} (N={condition.AgitationRpm} rpm, Q={condition.AirflowLpm} L/min, Rep={replicateNumber}).");
        RaiseStateChanged();

        // 1. Claim ownership of Agitation and Aeration
        _arbiter.Claim(
            CommandOwner.KlaAssay,
            [ActuatorId.Agitation, ActuatorId.Aeration],
            $"Ensaio kLa: {_currentRun.FolderName}");

        if (_arbiter.OwnerOf(ActuatorId.Agitation) != CommandOwner.KlaAssay ||
            _arbiter.OwnerOf(ActuatorId.Aeration) != CommandOwner.KlaAssay)
        {
            await AbortTestAsync("Falha ao obter posse dos atuadores de agitação e aeração.");
            return;
        }

        // 2. Initial Gas State: Close all gases first
        var initialDo = _currentDO;

        _openAirAfterClosing = initialDo <= _currentTest.Settings.DOMinPercent;
        SetPhase(RunPhase.ClosingAllGas, "Intertravamento: fechando todas as válvulas...");
        DispatchFlowOrAbort(CommandBuilders.FlowSafeStop(MaxFlow), "fechar todas as válvulas");
    }

    public Task StopRunAndReviewAsync(string reason = "Parada pelo operador")
    {
        if (_phase is not (RunPhase.Deoxygenating or RunPhase.ClosingNitrogen or RunPhase.WaitingForDOStability or
            RunPhase.OpeningVent or RunPhase.StabilizingVentFlow or
            RunPhase.Reoxygenating or RunPhase.OpeningAir or RunPhase.OpeningNitrogen))
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
        if (_currentTest is null || _currentRun is null || _currentCondition is null)
        {
            throw new InvalidOperationException("Nenhuma corrida ativa para aceitar.");
        }

        lock (_gate)
        {
            if (analysis.Quality == DecisionQuality.Inconclusive)
            {
                throw new InvalidOperationException("Uma análise inconclusiva não pode ser aceita. Ajuste a região/Ceq ou rejeite a corrida.");
            }
            analysis.RevisionNumber = _currentRun.AnalysisHistory.Count + 1;
            _currentRun.LatestAnalysis = analysis;
            _currentRun.AnalysisHistory.Add(analysis);
            _currentRun.CompletedUtc = _time.GetUtcNow();
            _currentRun.CurrentPhase = RunPhase.Accepted;

            // Save raw data and analysis
            _store.SaveRunRawData(_currentTest.FolderName, _currentRun.FolderName, _runPoints);
            var rawPath = _store.GetRunRawDataPath(_currentTest.FolderName, _currentRun.FolderName);
            analysis.RawDataSha256 = KlaTestFileContracts.ComputeFileSha256(rawPath);

            _store.SaveRunAnalysis(_currentTest.FolderName, _currentRun.FolderName, analysis);
            _store.SaveRunResult(_currentTest.FolderName, _currentRun.FolderName, _currentRun, analysis);

            // Update conditions and summary
            _currentCondition.CompletedReplicates++;
            _currentCondition.AcceptedReplicates++;
            if (_currentCondition.AcceptedReplicates >= _currentCondition.RequestedReplicates)
            {
                _currentCondition.Status = ConditionStatus.Completed;
            }

            _currentTest.Runs.RemoveAll(r => r.RunId == _currentRun.RunId);
            _currentTest.Runs.Add(new KlaTestRunSummary
            {
                RunId = _currentRun.RunId,
                ConditionId = _currentRun.ConditionId,
                ReplicateNumber = _currentRun.ReplicateNumber,
                FolderName = _currentRun.FolderName,
                AgitationRpm = _currentRun.AgitationRpm,
                AirflowLpm = _currentRun.AirflowLpm,
                Phase = RunPhase.Accepted,
                Decision = analysis.Quality,
                KlaPerHour = analysis.KlaPerHour,
                AnalysisR2 = analysis.AnalysisR2,
                StartedUtc = _currentRun.StartedUtc,
                CompletedUtc = _currentRun.CompletedUtc,
            });

            _store.SaveConditionsTable(_currentTest.FolderName, _currentTest.Conditions);
            _store.SaveTestManifest(_currentTest);
            _store.UpdateResultsSummary(_currentTest.FolderName, _currentTest);

            SetPhase(RunPhase.Accepted, $"Corrida {_currentRun.FolderName} aceita (kLa = {analysis.KlaPerHour:F1} h⁻¹).");
        }

        LogEvent("RunAccepted", $"Corrida {_currentRun.FolderName} aceita com kLa = {analysis.KlaPerHour:F1} h⁻¹ (R² = {analysis.AnalysisR2:F4}).");
        return Task.CompletedTask;
    }

    public Task RejectRunAsync(string reason)
    {
        if (_currentTest is null || _currentRun is null || _currentCondition is null)
        {
            throw new InvalidOperationException("Nenhuma corrida ativa para rejeitar.");
        }

        lock (_gate)
        {
            var analysis = _currentRun.LatestAnalysis ?? new KlaAnalysisRevision
            {
                Quality = DecisionQuality.Inconclusive,
                RejectionReason = reason,
                TStartSeconds = 0,
                TEndSeconds = PhaseElapsedSeconds,
            };

            analysis.Quality = DecisionQuality.Inconclusive;
            analysis.RejectionReason = reason;
            _currentRun.LatestAnalysis = analysis;
            _currentRun.AnalysisHistory.Add(analysis);
            _currentRun.CompletedUtc = _time.GetUtcNow();
            _currentRun.CurrentPhase = RunPhase.Rejected;

            _store.SaveRunRawData(_currentTest.FolderName, _currentRun.FolderName, _runPoints);
            var rawPath = _store.GetRunRawDataPath(_currentTest.FolderName, _currentRun.FolderName);
            analysis.RawDataSha256 = KlaTestFileContracts.ComputeFileSha256(rawPath);

            _store.SaveRunAnalysis(_currentTest.FolderName, _currentRun.FolderName, analysis);
            _store.SaveRunResult(_currentTest.FolderName, _currentRun.FolderName, _currentRun, analysis);

            _currentCondition.CompletedReplicates++;
            _currentCondition.RejectedReplicates++;

            _currentTest.Runs.RemoveAll(r => r.RunId == _currentRun.RunId);
            _currentTest.Runs.Add(new KlaTestRunSummary
            {
                RunId = _currentRun.RunId,
                ConditionId = _currentRun.ConditionId,
                ReplicateNumber = _currentRun.ReplicateNumber,
                FolderName = _currentRun.FolderName,
                AgitationRpm = _currentRun.AgitationRpm,
                AirflowLpm = _currentRun.AirflowLpm,
                Phase = RunPhase.Rejected,
                Decision = DecisionQuality.Inconclusive,
                KlaPerHour = null,
                AnalysisR2 = null,
                StartedUtc = _currentRun.StartedUtc,
                CompletedUtc = _currentRun.CompletedUtc,
            });

            _store.SaveConditionsTable(_currentTest.FolderName, _currentTest.Conditions);
            _store.SaveTestManifest(_currentTest);
            _store.UpdateResultsSummary(_currentTest.FolderName, _currentTest);

            SetPhase(RunPhase.Rejected, $"Corrida {_currentRun.FolderName} rejeitada: {reason}.");
        }

        LogEvent("RunRejected", $"Corrida {_currentRun.FolderName} rejeitada: {reason}");
        return Task.CompletedTask;
    }

    public Task RepeatRunAsync()
    {
        if (_currentCondition is null)
        {
            return Task.CompletedTask;
        }

        var sameReplicate = _currentRun?.ReplicateNumber ?? Math.Max(1, _currentCondition.CompletedReplicates + 1);
        return StartRunAsync(_currentCondition, sameReplicate);
    }

    public Task CompleteTestAsync()
    {
        lock (_gate)
        {
            if (_currentTest is null)
            {
                return Task.CompletedTask;
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
                _arbiter.Release(CommandOwner.KlaAssay, $"Abortado sem confirmação por perda de comunicação: {reason}");
                SetPhase(RunPhase.Faulted, $"Teste abortado sem confirmação de fechamento: {reason}");
            }
        }

        LogEvent("TestAborted", $"Teste abortado: {reason}");
        return Task.CompletedTask;
    }

    public void UpdateLiveSettings(KlaTestSettings settings)
    {
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
            if (_phase == RunPhase.Deoxygenating && _currentDO <= settings.DOMinPercent)
            {
                TransitionToReoxygenation();
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
        lock (_gate)
        {
            if (_currentTest is null)
            {
                return;
            }

            _currentTest.Settings = _currentTest.Settings with { DegassingAgitationRpm = rpm };
            _store.SaveTestManifest(_currentTest);

            if (_phase == RunPhase.Deoxygenating)
            {
                _arbiter.Dispatch(CommandOwner.KlaAssay, CommandBuilders.MotorSetpoint((int)rpm));
            }
        }
    }

    private double MaxFlow => _settings.Current.Setpoints.MaxFlowLitresPerMinute;

    private void OnTelemetryReceived(SensorSnapshot s)
    {
        _lastTelemetryMonotonic = GetMonotonicSeconds();
        _currentDO = s.OxygenCalibrated;
        _currentDORaw = s.OxygenRaw;
        _currentFlowMeasured = s.FlowRate;
        _lastFlowmeterOnline = s.FlowmeterOnline;
        _lastFlowCommandId = s.FlowCommandId;

        if (_currentTest is null || _phase is RunPhase.Idle or RunPhase.Completed or RunPhase.Faulted)
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
            var relSec = Math.Max(0, GetMonotonicSeconds() - _runStartMonotonic);
            var monoSec = GetMonotonicSeconds();

            var v1 = s.FlowValve1 != 0;
            var v2 = s.FlowValve2 != 0;
            var vFlow = s.FlowValveMain != 0;
            var agitationSetpoint = _phase is RunPhase.OpeningNitrogen or RunPhase.Deoxygenating or
                RunPhase.ClosingNitrogen or RunPhase.WaitingForDOStability
                ? _currentTest.Settings.DegassingAgitationRpm
                : _currentCondition?.AgitationRpm ?? 0;

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
                VFlow: vFlow);

            var capturesRunData = _phase is RunPhase.Preflight or RunPhase.ClosingAllGas or
                RunPhase.OpeningNitrogen or RunPhase.Deoxygenating or RunPhase.ClosingNitrogen or RunPhase.WaitingForDOStability or
                RunPhase.OpeningVent or RunPhase.StabilizingVentFlow or
                RunPhase.OpeningAir or RunPhase.Reoxygenating or RunPhase.StoppingRun or RunPhase.Aborting;
            if (capturesRunData)
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
                EventDetail: _statusMessage);

            _globalSamples.Add(sample);
            _store.AppendGlobalSeriesSample(_currentTest.FolderName, sample);

            // Phase State Transitions
            if (_phase == RunPhase.ClosingAllGas && IsGasStateConfirmed(s, (0, false, false, true)))
            {
                if (_openAirAfterClosing)
                {
                    BeginAirAdmission();
                }
                else
                {
                    OpenNitrogen();
                }
            }
            else if (_phase == RunPhase.StoppingRun && IsGasStateConfirmed(s, (0, false, false, true)))
            {
                SetPhase(RunPhase.Reviewing, "Válvulas fechadas. Revise e classifique a corrida.");
            }
            else if (_phase == RunPhase.Aborting && IsGasStateConfirmed(s, (0, false, false, true)))
            {
                FinalizeTerminalClose();
            }
            else if (_phase == RunPhase.OpeningNitrogen && IsGasStateConfirmed(s, _targetGasState))
            {
                SetPhase(RunPhase.Deoxygenating, $"Desoxigenação com N₂ em andamento (DO = {s.OxygenCalibrated:F1}%)...");
            }
            else if (_phase == RunPhase.OpeningAir && IsGasStateConfirmed(s, _targetGasState))
            {
                SetPhase(RunPhase.Reoxygenating, $"Reoxigenação com Ar em andamento (DO = {s.OxygenCalibrated:F1}%)...");
            }
            else if (_phase == RunPhase.ClosingNitrogen && IsGasStateConfirmed(s, _targetGasState))
            {
                BeginPostNitrogenStabilityWait();
            }
            else if (_phase == RunPhase.WaitingForDOStability)
            {
                EvaluatePostNitrogenStability(monoSec, s.OxygenCalibrated);
            }
            else if (_phase == RunPhase.OpeningVent && IsGasStateConfirmed(s, _targetGasState))
            {
                BeginVentFlowStabilization();
            }
            else if (_phase == RunPhase.StabilizingVentFlow)
            {
                EvaluateVentFlowStability(s.FlowRate);
            }
            else if (_phase == RunPhase.Deoxygenating && s.OxygenCalibrated <= _currentTest.Settings.DOMinPercent)
            {
                TransitionToReoxygenation();
            }
            else if (_phase == RunPhase.Reoxygenating && s.OxygenCalibrated >= _currentTest.Settings.DOMaxPercent)
            {
                StopRunAndReviewAsync("DO máxima atingida com sucesso");
            }

            RaiseStateChanged();
        }
    }

    private void TransitionToReoxygenation()
    {
        LogEvent("DOMinReached", $"DO atingiu {_currentDO:F1}% (limiar {_currentTest?.Settings.DOMinPercent:F1}%). Fechando N₂...");

        _targetGasState = (0.0, false, false, true);
        SetPhase(RunPhase.ClosingNitrogen, "Fechando N₂...");

        _openAirAfterClosing = true;
        DispatchFlowOrAbort(CommandBuilders.FlowSafeStop(MaxFlow), "fechar nitrogênio");
    }

    private void BeginPostNitrogenStabilityWait()
    {
        ResetStabilityDetection();
        LogEvent("NitrogenClosed", "N₂ fechado e confirmado. Aguardando dissipação do gás residual e estabilização da sonda.");
        SetPhase(RunPhase.WaitingForDOStability, "N₂ desligado · aguardando atraso mínimo e estabilidade de dDO/dt...");
    }

    private void EvaluatePostNitrogenStability(double monotonicSeconds, double dissolvedOxygen)
    {
        var settings = _currentTest!.Settings;
        _stabilityWindow.Add((monotonicSeconds, dissolvedOxygen));

        var span = settings.StabilityDerivativeSpanSeconds;
        var oldestUseful = monotonicSeconds - Math.Max(30.0, span * 2.0);
        _stabilityWindow.RemoveAll(p => p.Time < oldestUseful);

        var derivativePoints = _stabilityWindow.Where(p => p.Time >= monotonicSeconds - span).ToList();
        _currentDODerivative = TryCalculateSlope(derivativePoints, span);

        var minimumDelayRemaining = Math.Max(0, settings.PostNitrogenMinimumDelaySeconds - PhaseElapsedSeconds);
        if (minimumDelayRemaining > 0)
        {
            _stabilityConfirmationCount = 0;
            _statusMessage = $"N₂ fechado · atraso de dissipação: {minimumDelayRemaining:F1} s restantes";
            return;
        }

        if (!_currentDODerivative.HasValue)
        {
            _stabilityConfirmationCount = 0;
            _statusMessage = $"N₂ fechado · formando janela de derivada ({span:F1} s)...";
            return;
        }

        var threshold = settings.StabilityDerivativeThresholdPercentPerSecond;
        if (Math.Abs(_currentDODerivative.Value) <= threshold)
        {
            _stabilityConfirmationCount++;
        }
        else
        {
            _stabilityConfirmationCount = 0;
        }

        _statusMessage = $"N₂ fechado · dDO/dt = {_currentDODerivative.Value:+0.000;-0.000;0.000} %/s · estabilidade {_stabilityConfirmationCount}/{settings.StabilityRequiredSamples}";
        if (_stabilityConfirmationCount < settings.StabilityRequiredSamples)
        {
            return;
        }

        LogEvent("DOStable", $"DO estabilizado após N₂: dDO/dt={_currentDODerivative.Value:F4} %/s, {settings.StabilityRequiredSamples} confirmações.");
        BeginAirAdmission();
    }

    private static double? TryCalculateSlope(IReadOnlyList<(double Time, double DO)> points, double requestedSpan)
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
        _ventFlowStableCount = 0;
        _ventFlowDeviation = null;
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

        return flowOk && v1Ok && v2Ok && vFlowOk && ackOk;
    }

    private bool _lastFlowmeterOnline;

    private void OpenNitrogen()
    {
        var isV1 = _currentTest!.SelectedNitrogenValve == NitrogenValve.Valve1;
        _targetGasState = (0, isV1, !isV1, true);
        SetPhase(RunPhase.OpeningNitrogen, $"Abrindo N₂ em {(isV1 ? "valve_1" : "valve_2")}...");
        DispatchMotorOrAbort((int)_currentTest.Settings.DegassingAgitationRpm, "ajustar agitação de desoxigenação");
        DispatchFlowOrAbort(CommandBuilders.FlowSetpoint(0, MaxFlow, isV1, !isV1), "abrir nitrogênio");
    }

    /// <summary>
    /// True when this test routes the flowmeter's start-up pulse through the vent valve. The
    /// vent must sit on the output the N₂ line does not use; a collision disables the detour
    /// rather than energising the nitrogen valve by mistake.
    /// </summary>
    private bool ShouldVentBeforeAir()
    {
        if (_currentTest is null || !_currentTest.Settings.VentStabilizationEnabled)
        {
            return false;
        }

        if (_currentTest.SelectedVentValve == _currentTest.SelectedNitrogenValve)
        {
            LogEvent(
                "VentSkipped",
                "Alívio ignorado: a válvula selecionada coincide com a do N₂. O ar será admitido direto no reator.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Entry point for every path that admits air. Without the vent line the flowmeter opens
    /// straight into the vessel; with it, the flow is first raised and settled outside the
    /// vessel so the run starts at its declared airflow instead of on the meter's pulse.
    /// </summary>
    /// <remarks>
    /// The condition's rotation is commanded only when the vent closes. Holding the assay
    /// rotation through the vent wait would re-oxygenate the broth by surface aeration while no
    /// gas is being sparged, so the vent wait runs at its own low rotation instead.
    /// </remarks>
    private void BeginAirAdmission()
    {
        if (ShouldVentBeforeAir())
        {
            OpenVent();
            return;
        }

        OpenAir();
    }

    private void OpenVent()
    {
        var targetFlow = _currentCondition!.AirflowLpm;
        var ventIsV1 = _currentTest!.SelectedVentValve == NitrogenValve.Valve1;
        _ventFlowStableCount = 0;
        _ventFlowDeviation = null;
        if (_currentRun is not null)
        {
            _currentRun.UsedVentStabilization = true;
        }

        _targetGasState = (targetFlow, ventIsV1, !ventIsV1, false);
        SetPhase(
            RunPhase.OpeningVent,
            $"Abrindo alívio em {(ventIsV1 ? "valve_1" : "valve_2")} a {_currentTest.Settings.VentAgitationRpm:F0} rpm, " +
            $"levando o fluxômetro a {targetFlow:F2} L/min...");
        DispatchMotorOrAbort((int)_currentTest.Settings.VentAgitationRpm, "ajustar agitação durante o alívio");
        DispatchFlowOrAbort(
            CommandBuilders.FlowSetpoint(targetFlow, MaxFlow, ventIsV1, !ventIsV1),
            "abrir a válvula de alívio");
    }

    private void BeginVentFlowStabilization()
    {
        _ventFlowStableCount = 0;
        _ventFlowDeviation = null;
        LogEvent(
            "VentOpened",
            "Alívio aberto e confirmado. O gás sai pelo alívio até a vazão assentar em " +
            $"{_currentCondition!.AirflowLpm:F2} ± {_currentTest!.Settings.VentFlowToleranceLpm:F2} L/min.");
        SetPhase(RunPhase.StabilizingVentFlow, "Alívio aberto · aguardando a vazão assentar...");
    }

    /// <summary>
    /// Holds the run outside the vessel until the measured flow sits within tolerance of the
    /// requested airflow for the configured number of consecutive readings. Only then is the
    /// vent closed, which is the instant the assay actually starts.
    /// </summary>
    private void EvaluateVentFlowStability(double measuredFlow)
    {
        var settings = _currentTest!.Settings;
        var targetFlow = _currentCondition!.AirflowLpm;
        var deviation = measuredFlow - targetFlow;
        _ventFlowDeviation = deviation;

        if (Math.Abs(deviation) <= settings.VentFlowToleranceLpm)
        {
            _ventFlowStableCount++;
        }
        else
        {
            _ventFlowStableCount = 0;
        }

        _statusMessage =
            $"Alívio aberto · {measuredFlow:F2} L/min (alvo {targetFlow:F2} ± {settings.VentFlowToleranceLpm:F2}) · " +
            $"estabilidade {_ventFlowStableCount}/{settings.VentFlowStableSamples}";
        if (_ventFlowStableCount < settings.VentFlowStableSamples)
        {
            return;
        }

        LogEvent(
            "VentFlowStable",
            $"Vazão estabilizada em {measuredFlow:F2} L/min após {_ventFlowStableCount} confirmações. " +
            "Fechando o alívio e iniciando a reoxigenação.");
        OpenAir(fromVent: true);
    }

    private void OpenAir(bool fromVent = false)
    {
        var targetFlow = _currentCondition!.AirflowLpm;
        _targetGasState = (targetFlow, false, false, false);
        SetPhase(
            RunPhase.OpeningAir,
            fromVent ? "Fechando o alívio e direcionando o ar ao reator..." : "Abrindo ar...");
        DispatchMotorOrAbort((int)_currentCondition.AgitationRpm, "ajustar agitação de reoxigenação");
        DispatchFlowOrAbort(CommandBuilders.FlowSetpoint(targetFlow, MaxFlow, false, false), "abrir ar");
    }

    private void DispatchFlowOrAbort(TecnalCommand command, string action)
    {
        _minimumExpectedFlowCommandId = _lastFlowCommandId + 1;
        var result = _arbiter.Dispatch(CommandOwner.KlaAssay, command);
        if (!result.Accepted)
        {
            throw new InvalidOperationException($"Comando recusado ao tentar {action}.");
        }
    }

    private void DispatchMotorOrAbort(int rpm, string action)
    {
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

        if (!double.IsFinite(settings.PostNitrogenMinimumDelaySeconds) || settings.PostNitrogenMinimumDelaySeconds < 0 ||
            !double.IsFinite(settings.StabilityDerivativeSpanSeconds) || settings.StabilityDerivativeSpanSeconds <= 0 ||
            !double.IsFinite(settings.StabilityDerivativeThresholdPercentPerSecond) || settings.StabilityDerivativeThresholdPercentPerSecond <= 0 ||
            settings.StabilityRequiredSamples is < 1 or > 100 ||
            !double.IsFinite(settings.MaxPostNitrogenStabilizationSeconds) || settings.MaxPostNitrogenStabilizationSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Os parâmetros de estabilização pós-N₂ são inválidos.");
        }

        if (settings.MaxPostNitrogenStabilizationSeconds <= settings.PostNitrogenMinimumDelaySeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "O tempo máximo pós-N₂ deve ser maior que o atraso mínimo.");
        }

        if (!double.IsFinite(settings.VentFlowToleranceLpm) || settings.VentFlowToleranceLpm <= 0 ||
            settings.VentFlowStableSamples is < 1 or > 100 ||
            !double.IsFinite(settings.MaxVentStabilizationSeconds) || settings.MaxVentStabilizationSeconds <= 0 ||
            !double.IsFinite(settings.VentAgitationRpm) || settings.VentAgitationRpm is < 50 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Os parâmetros de estabilização no alívio são inválidos.");
        }
    }

    private void OnDeviceStateChanged(ConnectionStateChange change)
    {
        if (change.State != ConnectionState.Connected && IsRunning)
        {
            _ = AbortTestAsync($"Conexão perdida com o biorreator ({change.State}).");
        }
    }

    private void CheckWatchdog()
    {
        if (!IsRunning || _phase == RunPhase.Reviewing)
        {
            return;
        }
        var now = GetMonotonicSeconds();
        if (_lastTelemetryMonotonic > 0 && now - _lastTelemetryMonotonic > 5)
        {
            _ = AbortTestAsync("Telemetria ficou desatualizada por mais de 5 segundos.");
            return;
        }
        if ((_phase is RunPhase.ClosingAllGas or RunPhase.OpeningNitrogen or RunPhase.ClosingNitrogen or RunPhase.OpeningVent or RunPhase.OpeningAir or RunPhase.StoppingRun or RunPhase.Aborting) &&
            PhaseElapsedSeconds > 10)
        {
            if (_phase == RunPhase.Aborting)
            {
                _arbiter.Release(CommandOwner.KlaAssay, "Tempo limite no fechamento; estado físico não confirmado");
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
        else if (_phase == RunPhase.WaitingForDOStability &&
                 PhaseElapsedSeconds > _currentTest.Settings.MaxPostNitrogenStabilizationSeconds)
        {
            _ = StopRunAndReviewAsync("DO não estabilizou dentro do tempo máximo pós-N₂");
        }
        else if (_phase == RunPhase.StabilizingVentFlow &&
                 PhaseElapsedSeconds > _currentTest.Settings.MaxVentStabilizationSeconds)
        {
            _ = StopRunAndReviewAsync("A vazão não estabilizou no alívio dentro do tempo máximo");
        }
    }

    private void FinalizeTerminalClose()
    {
        if (_currentTest is null)
        {
            return;
        }

        _arbiter.Release(CommandOwner.KlaAssay, _terminalReason);
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
            _store.SaveConditionsTable(_currentTest.FolderName, _currentTest.Conditions);
            _store.SaveTestManifest(_currentTest);
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

        _device.TelemetryReceived -= OnTelemetryReceived;
        _device.StateChanged -= OnDeviceStateChanged;
        _watchdog.Dispose();
    }
}
