using System;
using System.Collections.Generic;
using System.Linq;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;

namespace OpenTECHub.Services.KlaTesting;

public sealed partial class KlaTestRunner
{
    private readonly KlaAssayCoordinator _assayCoordinator;
    private bool _hasOxygenSample;
    private bool _lastOxygenRejected;
    private double _lastOxygenMonotonic;
    private double _gasOffMonotonic;
    private double _gasOffInitialDo;
    private double? _restorationStableSince;
    private readonly List<(double Time, double DO)> _initialOxygenHistory = [];
    private bool IsBiotic => _currentTest?.EffectiveProtocol == KlaAssayProtocol.Biotic;
    private KlaProtocolSettings OperationalSettings => _currentTest?.ProtocolSettings ?? new();
    private double RemovalRpm => _currentTest?.ProtocolSettings?.OxygenRemovalAgitationRpm
        ?? _currentTest?.Settings.DegassingAgitationRpm ?? 100;
    private double ReturnRpm => _currentRun?.Acquisition?.ReturnAgitationRpm
        ?? InitialReturnRpm;
    private double InitialReturnRpm => _recipeReturnSnapshot?.AgitationSetpointRpm ??
        OperationalSettings.ReturnAgitationRpm ?? _device.Latest?.ServoRpm ?? double.NaN;

    private void ValidateBioticPreflight(KlaTestCondition condition)
    {
        KlaAssayDefinition.FromDocument(_currentTest!).Validate();
        if (_currentTest!.NitrogenIsolationConfirmedUtc is null)
        {
            throw new InvalidOperationException("Sessão biótica: confirme N₂ isolado fisicamente na fonte; B e C compartilham a saída.");
        }
        var p = OperationalSettings;
        if (p.RemovalTargetDoPercent is null || p.AerationReturn.MaximumGasOffSeconds is null ||
            p.AerationReturn.MaximumRecoverySeconds is null || p.OperatingRange is null)
        {
            throw new InvalidOperationException("Defina OD alvo, tempos máximos e faixa de retomada para o procedimento biótico.");
        }
        var s = _device.Latest!;
        if (!s.FlowControlEnabled || s.FlowValveMain != 0 || s.FlowCommandPending ||
            s.FlowCommandAck != s.FlowCommandId || !double.IsFinite(s.FlowRate) || !double.IsFinite(s.FlowSetpoint) ||
            s.FlowSetpoint <= 0 || Math.Abs(s.FlowRate - s.FlowSetpoint) > p.ReturnFlowToleranceLpm ||
            GasRouting.Interpret(s.FlowValve1 != 0, s.FlowValve2 != 0, s.FlowSetpoint, Rig) != ObservedGasRoute.Reactor)
        {
            throw new InvalidOperationException("Início biótico requer ar estável entrando no reator, fluxômetro ligado e eco confirmado.");
        }
        if (!s.HasServoTelemetry || !s.HasServoSample || !s.ServoOnline || !double.IsFinite(InitialReturnRpm) || InitialReturnRpm <= 0)
        {
            throw new InvalidOperationException("Sem evidência de agitação medida para confirmar a retomada.");
        }
        // Stability belongs to the latest observed window. Wall time advancing while the
        // preparation checkpoint is flushed must not remove its first sample. Freshness
        // is checked independently before both preflight validations.
        var history = _initialOxygenHistory.Where(x => _lastOxygenMonotonic - x.Time <= p.InitialStabilitySeconds + 1e-6).ToList();
        var slope = TryCalculateSlope(history, p.InitialStabilitySeconds);
        if (slope is null || Math.Abs(slope.Value) > _currentTest.Settings.StabilityDerivativeThresholdPercentPerSecond ||
            _currentDO <= p.RemovalTargetDoPercent || _currentDO < p.OperatingRange.MinimumOperatingDoPercent ||
            _currentDO > p.OperatingRange.MaximumOperatingDoPercent)
        {
            throw new InvalidOperationException("Aguarde equilíbrio inicial de OD dentro da faixa configurada.");
        }
        if (condition.AirflowLpm > MaxFlow)
        {
            throw new InvalidOperationException("Vazão da condição excede a capacidade configurada do equipamento.");
        }
    }

    private void BeginBioticRemoval()
    {
        _restorationStableSince = null;
        _currentRun!.Outcome = new() { Restoration = KlaRestorationState.Pending };
        _store.SaveRunPhysicalOutcome(_currentTest!.FolderName, _currentRun.FolderName, _currentRun.Outcome);
        SetPhase(RunPhase.DivertingAir, "Desviando ar para escape; fluxômetro ligado e N₂ isolado.");
        DispatchMotorOrAbort((int)OperationalSettings.OxygenRemovalAgitationRpm, "agitação de remoção biótica");
        // Keep the already stable flow. At recovery the requested assay condition is applied.
        DispatchFlowOrAbort(RouteFrame(_currentRun.Acquisition!.ReturnAirflowLpm!.Value, GasRoute.VentAndNitrogen), "desviar ar ao escape");
    }

    private void EvaluateBioticTelemetry(SensorSnapshot s, bool newOxygen, double now, double relativeSeconds)
    {
        if (_phase == RunPhase.DivertingAir && IsGasStateConfirmed(s, _targetGasState))
        {
            _gasOffMonotonic = now; _gasOffInitialDo = _currentDO;
            RecordGasEvent(relativeSeconds, KlaGasEventKind.GasOffConfirmed, $"Air to vent; isolated N2; cmd={s.FlowCommandAck}");
            LogEvent("GasOffConfirmed", $"Ar no escape; N₂ isolado; t={relativeSeconds:F3}s.");
            SetPhase(RunPhase.MeasuringConsumption, "Medindo decaimento respiratório; fluxômetro permanece ligado.");
        }
        else if (_phase == RunPhase.MeasuringConsumption && newOxygen)
        {
            var p = OperationalSettings;
            var remaining = p.AerationReturn.MaximumGasOffSeconds!.Value - (now - _gasOffMonotonic);
            var floor = Math.Max(p.RemovalTargetDoPercent!.Value, p.AerationReturn.MinimumDoPercent ?? 0);
            var recent = _initialOxygenHistory.Where(x => now - x.Time <= _currentTest!.Settings.StabilityDerivativeSpanSeconds).ToList();
            var slope = TryCalculateSlope(recent, _currentTest!.Settings.StabilityDerivativeSpanSeconds) ?? 0;
            // Anticipate the fall over the independently supplied probe delay plus command budget.
            var delay = (p.Probe.ResponseTimeSeconds ?? 0) + p.CommandConfirmationTimeoutSeconds;
            if (_currentDO + Math.Min(0, slope) * delay <= floor || remaining <= 0 ||
                (p.AerationReturn.MaximumDoDropPoints is { } drop && _gasOffInitialDo - _currentDO >= drop))
            {
                LogEvent("AerationReturnCriterion", "Alvo/queda/tempo de remoção atingido ou projetado.");
                SwitchToReactor();
            }
        }
        else if (_phase == RunPhase.SwitchingToReactor && IsGasStateConfirmed(s, _targetGasState))
        {
            ConfirmSwitchToReactor(s, relativeSeconds);
        }
        else if (_phase == RunPhase.Reoxygenating && newOxygen)
        {
            var p = OperationalSettings;
            var range = p.OperatingRange!;
            var recent = _initialOxygenHistory.Where(x => now - x.Time <= p.RecoveryStabilitySeconds).ToList();
            var slope = TryCalculateSlope(recent, p.RecoveryStabilitySeconds);
            if (_currentDO >= range.MinimumOperatingDoPercent && _currentDO <= range.MaximumOperatingDoPercent &&
                slope.HasValue && Math.Abs(slope.Value) <= _currentTest!.Settings.StabilityDerivativeThresholdPercentPerSecond)
            {
                BeginCultivationRestoration("Recuperação estável na faixa configurada");
            }
        }
        else if (_phase == RunPhase.RestoringCultivation)
        {
            var p = OperationalSettings;
            var physical = IsGasStateConfirmed(s, _targetGasState) && s.FlowControlEnabled &&
                Math.Abs(s.FlowRate - _targetGasState.Flow) <= p.ReturnFlowToleranceLpm &&
                s.HasServoTelemetry && s.HasServoSample && s.ServoOnline &&
                Math.Abs(s.ServoRpm - ReturnRpm) <= p.ReturnAgitationToleranceRpm;
            if (!physical)
            {
                _restorationStableSince = null;
                return;
            }
            if (!newOxygen)
            {
                return;
            }
            var range = p.OperatingRange!;
            var recent = _initialOxygenHistory.Where(x => now - x.Time <= p.RecoveryStabilitySeconds).ToList();
            var slope = TryCalculateSlope(recent, p.RecoveryStabilitySeconds);
            if (_currentDO < range.MinimumOperatingDoPercent || _currentDO > range.MaximumOperatingDoPercent ||
                slope is null || Math.Abs(slope.Value) > _currentTest!.Settings.StabilityDerivativeThresholdPercentPerSecond)
            {
                _restorationStableSince = null;
                return;
            }
            _restorationStableSince ??= now;
            if (now - _restorationStableSince < p.RecoveryStabilitySeconds)
            {
                return;
            }
            var resumed = _assayCoordinator.Release(true, ReturnRpm, _targetGasState.Flow, "Cultivo restaurado");
            if (!resumed)
            {
                FailCultivationRestoration("Ar/agitação confirmados, mas o controlador anterior não retomou.");
                return;
            }
            _currentRun!.Outcome = (_currentRun.Outcome ?? new()) with
            { Restoration = _recipeReturnSnapshot is null ? KlaRestorationState.Confirmed : KlaRestorationState.Pending };
            PersistPhysicalOutcome();
            LogEvent("CultivationRestored", $"Ar, agitação e controle confirmados; t={relativeSeconds:F3}s.");
            if (_completeAfterClosing)
            {
                _currentTest!.Status = KlaTestStatus.Completed;
                _currentTest.CompletedUtc = _time.GetUtcNow();
                _store.SaveTestManifest(_currentTest);
                SetPhase(RunPhase.Completed, "Teste concluído com cultivo retomado.");
            }
            else
            {
                SetPhase(RunPhase.Reviewing, "Cultivo retomado. Revise a corrida.");
            }
            _completeAfterClosing = false; _abortAfterClosing = false;
        }
    }

    private void BeginCultivationRestoration(string reason)
    {
        if (_currentRun is null || _phase is RunPhase.RestoringCultivation or RunPhase.Faulted)
        {
            return;
        }
        // A completed/accepted run has no actuator lease to change; its restoration is already recorded.
        if (_currentRun.Outcome?.Restoration == KlaRestorationState.Confirmed &&
            _phase is RunPhase.Reviewing or RunPhase.Accepted or RunPhase.Rejected)
        {
            if (_completeAfterClosing)
            {
                _currentTest!.Status = KlaTestStatus.Completed;
                _store.SaveTestManifest(_currentTest);
                SetPhase(RunPhase.Completed, "Teste concluído; cultivo já retomado.");
            }
            return;
        }
        if (_device.State != ConnectionState.Connected || !_lastFlowmeterOnline ||
            _arbiter.OwnerOf(ActuatorId.Aeration) != CommandOwner.KlaAssay ||
            _arbiter.OwnerOf(ActuatorId.Agitation) != CommandOwner.KlaAssay)
        {
            FailCultivationRestoration($"Não foi possível comandar retomada: {reason}");
            return;
        }
        _terminalReason = reason;
        _restorationStableSince = null;
        SetPhase(RunPhase.RestoringCultivation, $"Retomando ar e agitação do cultivo: {reason}");
        try
        {
            DispatchMotorOrAbort((int)ReturnRpm, "restaurar agitação");
            DispatchFlowOrAbort(RouteFrame(_currentRun.Acquisition!.ReturnAirflowLpm!.Value, GasRoute.Reactor), "retomar ar no reator");
            LogEvent("RestorationRequested", $"N={ReturnRpm}; Q={_targetGasState.Flow}; t={PhaseElapsedSeconds:F3}s.");
        }
        catch (Exception error)
        {
            FailCultivationRestoration(error.Message);
        }
    }

    private void FailCultivationRestoration(string reason)
    {
        if (_currentRun is not null)
        {
            _currentRun.Outcome = (_currentRun.Outcome ?? new()) with
            { Restoration = KlaRestorationState.Failed, RestorationReason = reason };
        }
        _assayCoordinator.Release(false, 0, 0, reason);
        if (_currentTest is not null)
        {
            _currentTest.Status = KlaTestStatus.Interrupted;
            _currentTest.InterruptionReason = reason;
            PersistPhysicalOutcome();
        }
        SetPhase(RunPhase.Faulted, $"Retomada não confirmada: {reason}");
    }

    private void PersistPhysicalOutcome()
    {
        if (_currentTest is null || _currentRun is null)
        {
            return;
        }
        _currentRun.RawDataSha256 = _store.SaveRunRawData(_currentTest.FolderName, _currentRun.FolderName, _runPoints);
        _store.SaveRunPhysicalOutcome(_currentTest.FolderName, _currentRun.FolderName, _currentRun.Outcome!);
        _store.SaveTestManifest(_currentTest);
    }

    private void EnsureBioticReviewReady()
    {
        if (!IsBiotic && _currentRun is not null && _phase != RunPhase.Reviewing)
        {
            throw new InvalidOperationException("Aguarde fechamento confirmado e revisão antes de classificar a corrida.");
        }
        if (IsBiotic && (_phase != RunPhase.Reviewing || _currentRun?.Outcome?.Restoration != KlaRestorationState.Confirmed))
        {
            throw new InvalidOperationException("Aguarde a retomada confirmada do cultivo antes de classificar a corrida.");
        }
    }

    private void RecordGasEvent(double seconds, KlaGasEventKind kind, string evidence)
    {
        if (_currentRun is null || _currentTest is null)
        {
            return;
        }
        _currentRun.GasEvents.Add(new(seconds, kind, evidence));
        _store.SaveRunGasEvents(_currentTest.FolderName, _currentRun.FolderName, _currentRun.GasEvents);
    }
}
