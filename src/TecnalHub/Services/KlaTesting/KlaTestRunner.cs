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
        if (condition.AgitationRpm <= 0 || condition.AirflowLpm <= 0 || replicateNumber < 1)
        {
            throw new InvalidOperationException("Condição inválida: rotação, vazão e replicata devem ser positivas.");
        }

        lock (_gate)
        {
            _currentCondition = condition;
            condition.Status = ConditionStatus.InProgress;

            _runPoints.Clear();

            _currentRun = new KlaTestRun
            {
                TestId = _currentTest.TestId,
                ConditionId = condition.ConditionId,
                ReplicateNumber = replicateNumber,
                AgitationRpm = condition.AgitationRpm,
                AirflowLpm = condition.AirflowLpm,
                NitrogenValve = _currentTest.SelectedNitrogenValve,
                CurrentPhase = RunPhase.Preflight,
                StartedUtc = _time.GetUtcNow(),
            };

            var runFolder = _store.InitializeRunFolder(_currentTest.FolderName, _currentRun);
            _currentRun.FolderName = runFolder;

            _phase = RunPhase.Preflight;
            _phaseStartMonotonic = GetMonotonicSeconds();
            _runStartMonotonic = _phaseStartMonotonic;
            _statusMessage = $"Pré-voo da corrida {runFolder}...";
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
        if (_phase is not (RunPhase.Deoxygenating or RunPhase.Reoxygenating or RunPhase.OpeningAir or RunPhase.OpeningNitrogen))
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
            var agitationSetpoint = _phase is RunPhase.OpeningNitrogen or RunPhase.Deoxygenating or RunPhase.ClosingNitrogen
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
                RunPhase.OpeningNitrogen or RunPhase.Deoxygenating or RunPhase.ClosingNitrogen or
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
                    OpenAir();
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
                // Nitrogen closed confirmed -> Now open Air
                var targetFlow = _currentCondition?.AirflowLpm ?? 1.0;
                var targetRpm = _currentCondition?.AgitationRpm ?? 300.0;

                _targetGasState = (targetFlow, false, false, false);
                SetPhase(RunPhase.OpeningAir, "Abrindo Ar...");

                DispatchMotorOrAbort((int)targetRpm, "ajustar agitação de reoxigenação");
                DispatchFlowOrAbort(CommandBuilders.FlowSetpoint(targetFlow, MaxFlow, false, false), "abrir ar");
            }
            else if (_phase == RunPhase.Deoxygenating && s.OxygenCalibrated <= _currentTest.Settings.DOMinPercent)
            {
                TransitionToReoxygenation();
            }
            else if (_phase == RunPhase.Reoxygenating && s.OxygenCalibrated >= _currentTest.Settings.DOMaxPercent)
            {
                StopRunAndReviewAsync("DO máxima atingida com sucesso");
            }
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

    private void OpenAir()
    {
        var targetFlow = _currentCondition!.AirflowLpm;
        _targetGasState = (targetFlow, false, false, false);
        SetPhase(RunPhase.OpeningAir, "Abrindo ar...");
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
        if ((_phase is RunPhase.ClosingAllGas or RunPhase.OpeningNitrogen or RunPhase.ClosingNitrogen or RunPhase.OpeningAir or RunPhase.StoppingRun or RunPhase.Aborting) &&
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
