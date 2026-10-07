using System.Collections.Immutable;
using System.IO;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class KlaRecipeOrchestratorTests : IDisposable
{
    public enum Scenario { Normal, Retry, Exhaust, CancelDuringWait, CancelDuringAssay, BlockDeadline, ExposureBudget, RefuseConditional, DuplicateConditions, SelectionWriteFailure, ResultWriteFailure }
    private readonly string _root = Path.Combine(Path.GetTempPath(), "recipe-matrix-" + Guid.NewGuid().ToString("N"));
    private sealed class Producer(Action onResume) : IRecipeResourceProducer
    {
        public string NodeId => "cascade";
        public IReadOnlyList<ActuatorId> Resources => [ActuatorId.Agitation, ActuatorId.Aeration];
        public bool Suspended;
        public int Pauses, Resumes;
        private void Resume() { Suspended = false; Resumes++; onResume(); }
        public Task<IRecipeResourceSuspension> SuspendAsync(CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Suspended = true; Pauses++; return Task.FromResult<IRecipeResourceSuspension>(new Suspension(this)); }
        private sealed class Suspension(Producer owner) : IRecipeResourceSuspension
        {
            public ControllerReturnSnapshot CaptureControllerState() => new()
            { ControllerId = "cascade", WasActive = true, StateVersion = "test", StateJson = "{\"integral\":1}" };
            public void Resume() { owner.Resume(); }
            public void Stop() { owner.Suspended = true; }
        }
    }

    [Theory]
    [InlineData(KlaAssayProtocol.Abiotic, KlaCaptureMode.Single, Scenario.Normal)]
    [InlineData(KlaAssayProtocol.Abiotic, KlaCaptureMode.Multiple, Scenario.Normal)]
    [InlineData(KlaAssayProtocol.Biotic, KlaCaptureMode.Single, Scenario.Normal)]
    [InlineData(KlaAssayProtocol.Biotic, KlaCaptureMode.Multiple, Scenario.Normal)]
    [InlineData(KlaAssayProtocol.Abiotic, KlaCaptureMode.Single, Scenario.Retry)]
    [InlineData(KlaAssayProtocol.Biotic, KlaCaptureMode.Single, Scenario.Retry)]
    [InlineData(KlaAssayProtocol.Abiotic, KlaCaptureMode.Single, Scenario.Exhaust)]
    [InlineData(KlaAssayProtocol.Biotic, KlaCaptureMode.Single, Scenario.Exhaust)]
    [InlineData(KlaAssayProtocol.Abiotic, KlaCaptureMode.Multiple, Scenario.CancelDuringWait)]
    [InlineData(KlaAssayProtocol.Biotic, KlaCaptureMode.Multiple, Scenario.CancelDuringWait)]
    [InlineData(KlaAssayProtocol.Abiotic, KlaCaptureMode.Single, Scenario.CancelDuringAssay)]
    [InlineData(KlaAssayProtocol.Biotic, KlaCaptureMode.Single, Scenario.CancelDuringAssay)]
    [InlineData(KlaAssayProtocol.Abiotic, KlaCaptureMode.Single, Scenario.BlockDeadline)]
    [InlineData(KlaAssayProtocol.Biotic, KlaCaptureMode.Single, Scenario.BlockDeadline)]
    [InlineData(KlaAssayProtocol.Abiotic, KlaCaptureMode.Multiple, Scenario.ExposureBudget)]
    [InlineData(KlaAssayProtocol.Biotic, KlaCaptureMode.Multiple, Scenario.ExposureBudget)]
    [InlineData(KlaAssayProtocol.Abiotic, KlaCaptureMode.Single, Scenario.RefuseConditional)]
    [InlineData(KlaAssayProtocol.Biotic, KlaCaptureMode.Single, Scenario.RefuseConditional)]
    [InlineData(KlaAssayProtocol.Abiotic, KlaCaptureMode.Multiple, Scenario.DuplicateConditions)]
    [InlineData(KlaAssayProtocol.Biotic, KlaCaptureMode.Multiple, Scenario.DuplicateConditions)]
    [InlineData(KlaAssayProtocol.Abiotic, KlaCaptureMode.Single, Scenario.SelectionWriteFailure)]
    [InlineData(KlaAssayProtocol.Biotic, KlaCaptureMode.Single, Scenario.SelectionWriteFailure)]
    [InlineData(KlaAssayProtocol.Abiotic, KlaCaptureMode.Single, Scenario.ResultWriteFailure)]
    [InlineData(KlaAssayProtocol.Biotic, KlaCaptureMode.Single, Scenario.ResultWriteFailure)]
    public async Task Real_matrix_runs_restores_recaptures_and_persists_without_dialogs(KlaAssayProtocol protocol, KlaCaptureMode mode, Scenario scenario)
    {
        using var fixture = new RecipeAssayRestorationTests.Fixture(virtualTimers: true);
        var arbiter = fixture.Arbiter; var device = fixture.Device; var clock = fixture.Clock;
        arbiter.Claim(CommandOwner.Recipe, [ActuatorId.Agitation, ActuatorId.Aeration, ActuatorId.Oxygen], "matrix");
        arbiter.Dispatch(CommandOwner.Recipe, CommandBuilders.MotorControlMode(true));
        arbiter.Dispatch(CommandOwner.Recipe, CommandBuilders.MotorSetpoint(300));
        arbiter.Dispatch(CommandOwner.Recipe, CommandBuilders.FlowmeterLoopEnabled(true));
        arbiter.Dispatch(CommandOwner.Recipe, CommandBuilders.FlowRoute(2, 10, GasRoute.Reactor, fixture.Rig));
        arbiter.Dispatch(CommandOwner.Recipe, OpenTECCommand.Create().Set(CommandKeys.FlowKp, 0.25));
        arbiter.Dispatch(CommandOwner.Recipe, OpenTECCommand.Create().Set(CommandKeys.OxygenMonitor, 30));
        var coordinator = new RecipeResourceCoordinator(arbiter, clock);
        var request = RecipeExecutionContractTests.Request(protocol, mode);
        var initial = await coordinator.ReserveForAssayAsync(request.Context,
            [ActuatorId.Agitation, ActuatorId.Aeration, ActuatorId.Oxygen], TimeSpan.FromSeconds(30));
        var snapshot = initial.CaptureReturnSnapshot(fixture.Rig, clock); initial.AbortBeforeAssay();
        var requested = 300;
        var producer = new Producer(() => arbiter.Dispatch(CommandOwner.Recipe, CommandBuilders.MotorSetpoint(requested += 10)));
        coordinator.Register(producer);
        request = request with
        {
            Restoration = new() { BeforeAssay = snapshot, MaximumRecoverySeconds = 30, StabilitySeconds = 2,
                AgitationToleranceRpm = 2, FlowToleranceLpm = .05 }, AcquisitionDeadlineUtc = clock.GetUtcNow().AddHours(1),
            Retry = request.Retry with { MaximumBlockSeconds = 1800, MinimumInterAssaySeconds = .02 },
            Quality = request.Quality with { AllowedConditionalReasonCodes =
                ["constant_process_conditions_not_independently_verified", "unknown_probe_response_rate_is_conditional",
                 "constant_representative_our_not_independently_verified", "physical_saturation_unavailable_balance_not_checked",
                 "ols_interval_is_conditional_on_equilibrium_and_correlated_errors"] },
            Definition = request.Definition with
            {
                SequenceLimits = null,
                Settings = request.Definition.Settings with { MaxDegassingTimeMinutes = .5, MaxPrestageSeconds = 15,
                    MaxReoxygenationTimeMinutes = 4, DOMinPercent = 10, DOMaxPercent = 75, AirPrestageLeadPercent = 0,
                    StabilityDerivativeSpanSeconds = 2, StabilityDerivativeThresholdPercentPerSecond = .05,
                    StabilityRequiredSamples = 3, PrestageFlowToleranceLpm = .2, PrestageFlowStableSamples = 3,
                    PrestageFlowStabilityStdDevLpm = 0 },
                ProtocolSettings = request.Definition.ProtocolSettings with
                { OperatingRange = KlaOperatingRange.CurrentCultivation, RemovalTargetDoPercent = 10,
                    InitialStabilitySeconds = 2, RecoveryStabilitySeconds = 2,
                    AerationReturn = request.Definition.ProtocolSettings.AerationReturn with { MinimumInterAssaySeconds = .02 } }
            }
        };
        if (scenario == Scenario.RefuseConditional) request = request with { Quality = request.Quality with { AllowedConditionalReasonCodes = [] } };
        if (scenario == Scenario.ExposureBudget) request = request with { Retry = request.Retry with { MaximumCumulativeGasOffSecondsPerCultivation = 100 } };
        if (scenario == Scenario.BlockDeadline) request = request with { Retry = request.Retry with { MaximumBlockSeconds = 30 } };
        if (scenario == Scenario.CancelDuringWait) request = request with { Retry = request.Retry with { MinimumInterAssaySeconds = 10 },
            Definition = request.Definition with { ProtocolSettings = request.Definition.ProtocolSettings with
                { AerationReturn = request.Definition.ProtocolSettings.AerationReturn with { MinimumInterAssaySeconds = 10 } } } };
        if (scenario == Scenario.DuplicateConditions) request = request with { Definition = request.Definition with
            { Conditions = request.Definition.Conditions.Select(c => c with { AgitationRpm = 300, AirflowLpm = 2 }).ToImmutableArray() } };
        using var writer = new BackgroundFileWriter(synchronous: true);
        var store = new KlaTestStore(_root, writer);
        var document = store.CreateTest("autonomous matrix", request.Definition);
        document.NitrogenSourceConfirmedUtc = clock.GetUtcNow(); document.NitrogenIsolationConfirmedUtc = clock.GetUtcNow();
        store.SaveTestManifest(document);
        var settings = new MemorySettingsService(new AppSettings { GasRig = GasRigSettings.From(fixture.Rig) });
        var capabilities = new KlaAssayExecutionCapabilities { InstallationId = "test", ProfileId = request.Quality.ProfileId,
            ProfileVersion = request.Quality.Version, Protocols = [KlaAssayProtocol.Abiotic, KlaAssayProtocol.Biotic],
            EvidenceId = "isolated-matrix", IsIsolatedSimulation = true };
        var factory = new KlaRecipeAssayExecutionFactory(device, arbiter, store, new KlaAnalysisEngine(), settings, clock, true);
        var preparer = new KlaRecipePulsePreparer(coordinator, factory, settings, clock, capabilities,
            new() { MaximumTelemetryAgeSeconds = 5, MinimumOxygenPercent = protocol == KlaAssayProtocol.Biotic ? 30 : null,
                MaximumOxygenPercent = protocol == KlaAssayProtocol.Biotic ? 100 : null,
                MaximumOxygenSlopePercentPerSecond = protocol == KlaAssayProtocol.Biotic ? .05 : null }, TimeSpan.FromSeconds(30));
        var router = new KlaRecipeExecutionRouter(capabilities);
        var api = new KlaAssayApi(Path.Combine(_root, "api.json"), router, clock);
        var orchestrator = new KlaRecipeOrchestrator(api, router, preparer, store, clock);
        using var cancellation = new CancellationTokenSource();
        var execution = orchestrator.ExecuteAsync(request, document, cancellation.Token);
        bool injected = false;
        void InjectFailure(KlaRecipeOrchestrator current)
        {
            if (injected || current.CurrentPhase != RunPhase.Reviewing) return;
            if (scenario == Scenario.SelectionWriteFailure)
            {
                var item = current.CurrentItem!;
                var identity = KlaRecipePulseMapper.Create(request, capabilities.InstallationId, item.ConditionId,
                    item.ReplicateNumber, item.AttemptNumber, request.Restoration.BeforeAssay.CapturedUtc.AddSeconds(1)).RequestId;
                var runs = Path.Combine(_root, document.FolderName, KlaTestFileContracts.RunsDirectoryName);
                Directory.CreateDirectory(Path.Combine(Directory.GetDirectories(runs).Single(), $"receita-{identity:N}-Selection.json"));
            }
            else if (scenario == Scenario.ResultWriteFailure)
                Directory.CreateDirectory(Path.Combine(_root, document.FolderName, $"receita-{request.Context.InvocationId:N}-Result.json"));
            else return;
            injected = true;
        }
        try
        {
            await Drive(execution, orchestrator, fixture, request, () => producer.Suspended, scenario, cancellation, InjectFailure);
            var result = await execution.WaitAsync(TimeSpan.FromSeconds(10));
            var succeeds = scenario is Scenario.Normal or Scenario.Retry or Scenario.DuplicateConditions;
            var writeFailure = scenario is Scenario.SelectionWriteFailure or Scenario.ResultWriteFailure;
            var expectedStatus = scenario is Scenario.CancelDuringWait or Scenario.CancelDuringAssay
                ? KlaRecipeTerminalStatus.Cancelled : writeFailure ? KlaRecipeTerminalStatus.PersistenceFailure : KlaRecipeTerminalStatus.Inconclusive;
            Assert.True(succeeds ? result.Status is KlaRecipeTerminalStatus.Completed or KlaRecipeTerminalStatus.CompletedWithWarnings
                : result.Status == expectedStatus,
                $"{result.Status}: {result.Reason}; " + string.Join(";", result.Attempts.Select(a => string.Join(",", a.ReasonCodes))));
            result.ValidateAgainst(request);
            var expected = succeeds ? request.Definition.Conditions.Sum(c => c.RequestedReplicates) + (scenario == Scenario.Retry ? 1 : 0)
                : scenario == Scenario.Exhaust ? 2 : 1;
            Assert.Equal(scenario == Scenario.SelectionWriteFailure ? 0 : expected, result.Attempts.Length); Assert.Equal(expected, producer.Resumes);
            Assert.Equal(expected, producer.Pauses); Assert.False(producer.Suspended);
            if (scenario is Scenario.Retry or Scenario.Exhaust) Assert.Equal(KlaAutomaticDecision.Retry, result.Attempts[0].Decision);
            if (scenario == Scenario.Exhaust) Assert.Equal(KlaAutomaticDecision.NotSelected, result.Attempts[^1].Decision);
            if (scenario is Scenario.CancelDuringAssay or Scenario.BlockDeadline) Assert.Equal(KlaAutomaticDecision.Aborted, result.Attempts[^1].Decision);
            if (scenario == Scenario.RefuseConditional) Assert.Equal(KlaAutomaticDecision.NotSelected, result.Attempts[^1].Decision);
            if (succeeds) Assert.Equal(request.Definition.Conditions.Sum(c => c.RequestedReplicates),
                result.Attempts.Count(a => a.Decision == KlaAutomaticDecision.Selected));
            Assert.Equal(expected, result.Pulses.Select(p => p.RecipePulse!.Invocation.Restoration.BeforeAssay.SnapshotId).Distinct().Count());
            Assert.Equal(Enumerable.Range(0, expected).Select(i => 300.0 + 10 * i),
                result.Pulses.Select(p => p.RecipePulse!.Invocation.Restoration.BeforeAssay.AgitationSetpointRpm));
            Assert.True(result.PreAssayStateRestored); Assert.Equal(!writeFailure, result.PersistenceConfirmed);
            var reopened = new KlaTestStore(_root).ReadRecipeResult(document.FolderName, request.Context.InvocationId)!;
            if (writeFailure) { Assert.True(injected); Assert.Null(reopened); }
            else Assert.Equal(result.Status, reopened.Status);
            var common = new KlaTestStore(_root).LoadTest(document.FolderName)!;
            Assert.Equal(RecipeContractSerializer.Fingerprint(request), RecipeContractSerializer.Fingerprint(common.RecipeRequest!));
            if (!writeFailure)
                Assert.Equal(common.Runs.Count + 1,
                    File.ReadAllLines(Path.Combine(_root, document.FolderName, KlaAutomaticResultsSummary.FileName)).Length);
            Assert.Equal(result.Attempts.Count(a => a.Decision == KlaAutomaticDecision.Selected), common.Conditions.Sum(c => c.AcceptedReplicates));
            Assert.All(common.Runs, r => Assert.Equal(KlaOperatorDecision.Pending, r.EffectiveOutcome.OperatorDecision));
            await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.ExecuteAsync(request, document));
        }
        finally
        {
            cancellation.Cancel();
            if (!execution.IsCompleted) await Drive(execution, orchestrator, fixture, request, () => producer.Suspended, Scenario.Normal, cancellation);
            try { await execution.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            api.Dispose();
        }
    }

    internal static async Task Drive(Task execution, KlaRecipeOrchestrator orchestrator,
        RecipeAssayRestorationTests.Fixture fixture, KlaRecipeRequest request, Func<bool> producerSuspended, Scenario scenario,
        CancellationTokenSource cancellation, Action<KlaRecipeOrchestrator>? onProgress = null)
    {
        var recovery = 0; var off = 0; Guid? previousSnapshot = null;
        long command = 1;
        void OnCommand(string json)
        {
            if (CommandActuators.ActuatorsIn(OpenTECCommand.Parse(json)).Contains(ActuatorId.Aeration))
                Interlocked.Increment(ref command);
        }
        fixture.Device.CommandSent += OnCommand;
        try
        {
        for (var index = 0; index < 2500 && !execution.IsCompleted; index++)
        {
            onProgress?.Invoke(orchestrator);
            var phase = orchestrator.CurrentPhase;
            var snapshot = orchestrator.CurrentReturnSnapshot;
            if (snapshot?.SnapshotId != previousSnapshot) { recovery = 0; off = 0; previousSnapshot = snapshot?.SnapshotId; }
            var condition = request.Definition.Conditions.FirstOrDefault(c => c.ConditionId == orchestrator.CurrentItem?.ConditionId);
            var rpm = snapshot?.AgitationSetpointRpm ?? 300; double flow = snapshot?.AirflowSetpointLpm ?? 2, oxygen = 80;
            var route = GasRoute.Reactor;
            if (orchestrator.IsWaiting)
            {
                Assert.False(producerSuspended());
                if (scenario == Scenario.CancelDuringWait) cancellation.Cancel();
                fixture.Clock.Advance(TimeSpan.FromSeconds(.02));
            }
            else if (phase.HasValue)
            {
                if (phase != RunPhase.Reviewing) fixture.Clock.Advance(TimeSpan.FromSeconds(1));
                switch (phase)
                {
                    case RunPhase.ClosingAllGas: flow = 0; route = GasRoute.Closed; break;
                    case RunPhase.StoppingRun: flow = 0; route = GasRoute.Closed; break;
                    case RunPhase.OpeningNitrogen:
                    case RunPhase.Deoxygenating: oxygen = 10; flow = 0; rpm = 100; route = GasRoute.VentAndNitrogen; break;
                    case RunPhase.PrestagingAir: oxygen = 10; flow = condition!.AirflowLpm; rpm = 100; route = GasRoute.VentAndNitrogen; break;
                    case RunPhase.DivertingAir:
                    case RunPhase.MeasuringConsumption: oxygen = 80 - Math.Min(12, ++off); flow = snapshot!.AirflowSetpointLpm; rpm = 100; route = GasRoute.VentAndNitrogen; break;
                    case RunPhase.SwitchingToReactor: oxygen = request.Definition.Protocol == KlaAssayProtocol.Abiotic ? 10 : 70;
                        flow = condition!.AirflowLpm; rpm = condition.AgitationRpm; break;
                    case RunPhase.Reoxygenating:
                        if (scenario == Scenario.CancelDuringAssay) cancellation.Cancel();
                        var shortWindow = scenario == Scenario.Exhaust || scenario == Scenario.Retry && orchestrator.CurrentItem!.AttemptNumber == 1;
                        oxygen = shortWindow ? request.Definition.Protocol == KlaAssayProtocol.Abiotic ? 80 : 70
                            : request.Definition.Protocol == KlaAssayProtocol.Abiotic
                                ? 100 - 90 * Math.Exp(-40.0 * ++recovery / 3600) : 80 - 10 * Math.Exp(-40.0 * ++recovery / 3600);
                        flow = condition!.AirflowLpm; rpm = condition.AgitationRpm; break;
                }
            }
            fixture.Device.PushTelemetry(fixture.Sample(rpm, flow, route, command: Volatile.Read(ref command), oxygen: oxygen));
            await Task.Delay(1);
        }
        }
        finally { fixture.Device.CommandSent -= OnCommand; }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
