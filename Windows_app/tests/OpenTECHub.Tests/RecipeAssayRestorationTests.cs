using System.Text.Json;
using System.IO;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Recipes;
using OpenTECHub.Services.Persistence;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipeAssayRestorationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LifecycleRecoversWithItsOwnDeadlineAfterAcquisitionCancellation(bool failRecording)
    {
        using var fixture = new Fixture(); await fixture.Initialize();
        var directory = Path.Combine(Path.GetTempPath(), "recipe-lifecycle-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var writer = new BackgroundFileWriter(synchronous: true);
            var store = new KlaTestStore(directory, writer);
            var settings = new MemorySettingsService(new AppSettings { GasRig = GasRigSettings.From(fixture.Rig) });
            using var runner = new KlaTestRunner(fixture.Device, fixture.Arbiter, store, new KlaAnalysisEngine(), settings,
                fixture.Clock, actuationRelease: new(isIsolatedSimulation: true), recipeLease: fixture.Lease, recipeReturnSnapshot: fixture.Snapshot);
            var document = store.CreateTest("independent recovery", new KlaTestSettings { DOMinPercent = 10, AirPrestageLeadPercent = 0 });
            document.NitrogenSourceConfirmedUtc = fixture.Clock.GetUtcNow();
            var condition = new KlaTestCondition { AgitationRpm = 450, AirflowLpm = 3, RequestedReplicates = 1 };
            document.Conditions.Add(condition);
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            fixture.Device.PushTelemetry(fixture.Sample(285, 2, command: 1, oxygen: 80));
            using var cancellation = new CancellationTokenSource();
            var lifecycle = new KlaRecipePulseLifecycle(runner, fixture.Lease, new RecipeAssayRestoration(fixture.Device, fixture.Clock), fixture.Clock);
            var task = lifecycle.ExecuteAsync(document, condition, 1, fixture.Contract(),
                new() { MaximumTelemetryAgeSeconds = 5 }, cancellation.Token);
            await WaitUntil(() => fixture.Device.Sent.Count > 0);
            fixture.Device.Sent.Clear();
            cancellation.Cancel();
            await fixture.DriveRoute(command: 20);
            if (failRecording) writer.Dispose();
            await fixture.PushStable(command: 20);
            var result = await task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(KlaRecipeAcquisitionState.Cancelled, result.Acquisition.State);
            Assert.Equal(KlaRestorationState.Confirmed, result.Recovery.Restoration);
            Assert.Equal(KlaRestorationState.Confirmed, runner.CurrentRun!.Outcome!.Restoration);
            Assert.Equal(CommandOwner.KlaAssay, fixture.Arbiter.OwnerOf(ActuatorId.Agitation));
            Assert.False(fixture.Lease.HasReturnedSuccessfully);
            Assert.Equal(failRecording, result.RecoveryRecordingError is not null);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task AutonomousAcquisitionCompletesFromTelemetryWithoutAcceptingAReplicate()
    {
        using var fixture = new Fixture(); await fixture.Initialize();
        var directory = Path.Combine(Path.GetTempPath(), "recipe-completion-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new KlaTestStore(directory);
            var settings = new MemorySettingsService(new AppSettings { GasRig = GasRigSettings.From(fixture.Rig) });
            using var runner = new KlaTestRunner(fixture.Device, fixture.Arbiter, store, new KlaAnalysisEngine(), settings,
                fixture.Clock, actuationRelease: new(isIsolatedSimulation: true), recipeLease: fixture.Lease, recipeReturnSnapshot: fixture.Snapshot);
            var document = store.CreateTest("automatic completion", new KlaTestSettings
            {
                DOMinPercent = 10, DOMaxPercent = 85, AirPrestageLeadPercent = 0, MaxPrestageSeconds = 60,
                StabilityDerivativeSpanSeconds = 2, StabilityDerivativeThresholdPercentPerSecond = .05, StabilityRequiredSamples = 3,
                PrestageFlowToleranceLpm = .2, PrestageFlowStableSamples = 3, PrestageFlowStabilityStdDevLpm = 0
            });
            document.NitrogenSourceConfirmedUtc = fixture.Clock.GetUtcNow();
            var condition = new KlaTestCondition { AgitationRpm = 450, AirflowLpm = 3, RequestedReplicates = 1 };
            document.Conditions.Add(condition);
            void Push(double oxygen, double flow, GasRoute route, long command)
            {
                fixture.Clock.Advance(TimeSpan.FromSeconds(1));
                fixture.Device.PushTelemetry(fixture.Sample(450, flow, route, command: command, oxygen: oxygen));
            }
            Push(80, 2, GasRoute.Reactor, 1);
            var task = new KlaRecipeAcquisition(runner).ExecuteAsync(document, condition, 1, CancellationToken.None);
            Push(80, 0, GasRoute.Closed, 2);
            Push(70, 0, GasRoute.VentAndNitrogen, 3);
            Push(10, 0, GasRoute.VentAndNitrogen, 3);
            for (var index = 0; index < 6; index++) Push(10, 3, GasRoute.VentAndNitrogen, 4);
            Push(20, 3, GasRoute.Reactor, 5);
            for (var index = 0; index < 12; index++) Push(90, 3, GasRoute.Reactor, 5);
            for (var index = 0; index < 3; index++) Push(90, 0, GasRoute.Closed, 6);
            var result = await task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(KlaRecipeAcquisitionState.Completed, result.State);
            Assert.Equal(RunPhase.Reviewing, result.AcquisitionPhase);
            Assert.Equal(KlaRestorationState.Pending, runner.CurrentRun!.Outcome!.Restoration);
            Assert.DoesNotContain(document.Runs, run => run.Phase == RunPhase.Accepted);
            Assert.Equal(CommandOwner.KlaAssay, fixture.Arbiter.OwnerOf(ActuatorId.Agitation));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutonomousAcquisitionSealsCommandsOnCancellationOrPreparationFailure(bool failPreparation)
    {
        using var fixture = new Fixture();
        await fixture.Initialize();
        var directory = Path.Combine(Path.GetTempPath(), "recipe-acquisition-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new KlaTestStore(directory);
            var settings = new MemorySettingsService(new AppSettings { GasRig = GasRigSettings.From(fixture.Rig) });
            using var runner = new KlaTestRunner(fixture.Device, fixture.Arbiter, store, new KlaAnalysisEngine(), settings,
                fixture.Clock, actuationRelease: new(isIsolatedSimulation: true), recipeLease: fixture.Lease,
                recipeReturnSnapshot: fixture.Snapshot, beforeActuation: (_, _, _) => failPreparation
                    ? Task.FromException(new IOException("controlled preparation failure")) : Task.CompletedTask);
            var document = store.CreateTest("autonomous acquisition", new KlaTestSettings { DOMinPercent = 10, AirPrestageLeadPercent = 0 });
            document.NitrogenSourceConfirmedUtc = fixture.Clock.GetUtcNow();
            var condition = new KlaTestCondition { AgitationRpm = 450, AirflowLpm = 3, RequestedReplicates = 1 };
            document.Conditions.Add(condition);
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            fixture.Device.PushTelemetry(fixture.Sample(285, 2, command: 1, oxygen: 80));
            using var cancellation = new CancellationTokenSource();
            var acquisition = new KlaRecipeAcquisition(runner);
            var task = acquisition.ExecuteAsync(document, condition, 1, cancellation.Token);
            if (!failPreparation)
            {
                await WaitUntil(() => fixture.Device.Sent.Count > 0);
                cancellation.Cancel();
            }
            var result = await task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(failPreparation ? KlaRecipeAcquisitionState.Failed : KlaRecipeAcquisitionState.Cancelled, result.State);
            if (failPreparation) Assert.Empty(fixture.Device.Sent);
            Assert.Equal(CommandOwner.KlaAssay, fixture.Arbiter.OwnerOf(ActuatorId.Agitation));
            Assert.Equal(KlaRestorationState.Pending, runner.CurrentRun!.Outcome!.Restoration);
            fixture.Device.Sent.Clear();
            fixture.Clock.Advance(TimeSpan.FromSeconds(20));
            fixture.Device.PushTelemetry(fixture.Sample(0, 0, GasRoute.Closed, oxygen: 1));
            runner.CheckWatchdog();
            Assert.Empty(fixture.Device.Sent);
            await Assert.ThrowsAsync<InvalidOperationException>(() => acquisition.ExecuteAsync(document, condition, 1, CancellationToken.None));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    internal sealed class Fixture : IDisposable
    {
        public readonly TestClock Clock = new(DateTimeOffset.Parse("2026-10-07T12:00:00Z"));
        public readonly RecordingDeviceService Device = new();
        public readonly CommandArbiter Arbiter;
        public RecipeAssayResourceLease Lease = null!;
        public KlaReturnSnapshot Snapshot = null!;
        public GasRigConfiguration Rig = new(GasInput.Input1);
        public Fixture() { Arbiter = new(Device, Clock); }
        internal static async Task ReturnWithStore(RecipeAssayResourceLease lease, KlaRecipeRestorationContract contract, TestClock clock, KlaAssayApiResult result)
        {
            var directory = Path.Combine(Path.GetTempPath(), "return-receipt-" + Guid.NewGuid().ToString("N"));
            try
            {
                var invocation = RecipeExecutionContractTests.Request();
                invocation = invocation with
                {
                    Context = invocation.Context with { RecipeRunId = lease.Authority.ExecutionId, NodeId = lease.Authority.BlockId },
                    AcquisitionDeadlineUtc = clock.GetUtcNow().AddMinutes(10),
                    Restoration = contract,
                    Definition = invocation.Definition with { Settings = invocation.Definition.Settings with
                        { MaxDegassingTimeMinutes = .5, MaxPrestageSeconds = 15 } }
                };
                var request = KlaRecipePulseMapper.Create(invocation, "simulator-A", invocation.Definition.Conditions[0].ConditionId,
                    1, 1, clock.GetUtcNow().AddMinutes(1));
                var store = new KlaTestStore(directory);
                Directory.CreateDirectory(Path.Combine(directory, "session", KlaTestFileContracts.RunsDirectoryName, "run"));
                var checkpoint = new KlaAttemptPersistenceCheckpoint
                {
                    Request = request, Authority = lease.Authority, TestId = Guid.NewGuid(), RunId = Guid.NewGuid(),
                    TestFolder = "session", RunFolder = "run", Phase = KlaAttemptPersistencePhase.BeforeActuation
                };
                await store.PersistRecipeAttemptAsync(checkpoint);
                File.WriteAllText(store.GetRunRawDataPath("session", "run"), "time,oxygen\n0,40\n");
                await store.PersistRecipeAttemptAsync(checkpoint with
                {
                    Phase = KlaAttemptPersistencePhase.Terminal, DecisionJson = "{\"author\":\"AutomaticPolicy\"}",
                    Result = result with { TestFolder = "session", RunFolder = "run", PersistenceReceiptId = null }
                });
                await lease.ReturnPersistedAsync(store, request.RequestId, "session", "run");
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
        public async Task Initialize(double rpm = 300, double flow = 2, GasRoute route = GasRoute.Reactor, bool modbus = true)
        {
            Arbiter.Claim(CommandOwner.Recipe, [ActuatorId.Agitation, ActuatorId.Aeration, ActuatorId.Oxygen], "start");
            Arbiter.Dispatch(CommandOwner.Recipe, CommandBuilders.MotorControlMode(modbus));
            Arbiter.Dispatch(CommandOwner.Recipe, CommandBuilders.MotorSetpoint((int)rpm));
            Arbiter.Dispatch(CommandOwner.Recipe, CommandBuilders.FlowmeterLoopEnabled(true));
            Arbiter.Dispatch(CommandOwner.Recipe, CommandBuilders.FlowRoute(flow, 10, route, Rig));
            Arbiter.Dispatch(CommandOwner.Recipe, OpenTECCommand.Create().Set(CommandKeys.FlowKp, 0.25));
            Arbiter.Dispatch(CommandOwner.Recipe, OpenTECCommand.Create().Set(CommandKeys.OxygenMonitor, 30));
            Device.PushTelemetry(Sample(rpm - 15, flow, route, modbus, command: 1) with { FlowRate = Math.Max(0, flow - .2) });
            var coordinator = new RecipeResourceCoordinator(Arbiter, Clock);
            Lease = await coordinator.ReserveForAssayAsync(RecipeExecutionContractTests.Request().Context,
                [ActuatorId.Agitation, ActuatorId.Aeration, ActuatorId.Oxygen], TimeSpan.FromSeconds(3));
            Snapshot = Lease.CaptureReturnSnapshot(Rig, Clock, Device.Latest, Clock.GetUtcNow());
            Lease.BeginAssay(Snapshot);
            Device.Sent.Clear();
        }
        public KlaRecipeRestorationContract Contract(double deadline = 30) => new()
        {
            BeforeAssay = Snapshot, MaximumRecoverySeconds = deadline, StabilitySeconds = 2,
            AgitationToleranceRpm = 2, FlowToleranceLpm = .05
        };
        public SensorSnapshot Sample(double rpm = 300, double flow = 2, GasRoute route = GasRoute.Reactor,
            bool modbus = true, long command = 2, double oxygen = 40)
        {
            var valves = GasRouting.Resolve(route, Rig);
            return new()
            {
                FlowSetpoint = flow, FlowRate = flow, FlowControlEnabled = true, FlowmeterOnline = true,
                FlowValve1 = valves.Valve1 ? 1 : 0, FlowValve2 = valves.Valve2 ? 1 : 0, FlowValveMain = flow == 0 ? 1 : 0,
                FlowCommandId = command, FlowCommandAck = command, MotorControlViaModbus = modbus,
                ServoMotorRouteAck = modbus ? 1 : 0, ServoCommandPending = false, HasServoTelemetry = true,
                HasServoSample = true, ServoOnline = true, ServoRpm = rpm, OxygenCalibrated = oxygen, FlowKp = .25
            };
        }
        public Task<RecipeAssayRecoveryResult> Restore(RecipeAssayRecoveryCriteria? criteria = null, double deadline = 30)
            => new RecipeAssayRestoration(Device, Clock).RestoreAsync(Lease, Contract(deadline), criteria ?? new() { MaximumTelemetryAgeSeconds = 5 });
        public async Task DriveRoute(bool modbus = true, long command = 2)
        {
            await WaitUntil(() => Device.Sent.Count >= 3);
            Device.PushTelemetry(Sample(modbus: modbus, command: command));
            await WaitUntil(() => Device.Sent.Count >= 5);
        }
        public async Task PushStable(double rpm = 300, double flow = 2, GasRoute route = GasRoute.Reactor, bool modbus = true, long command = 2)
        {
            for (var i = 0; i < 6; i++)
            {
                Clock.Advance(TimeSpan.FromSeconds(1)); Device.PushTelemetry(Sample(rpm, flow, route, modbus, command));
                await Task.Yield();
            }
        }
        public void Dispose() { Lease?.Fail(); Arbiter.Dispose(); }
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate()) await Task.Delay(1, timeout.Token);
    }

    [Fact]
    public async Task Capture_merges_prior_configuration_and_separates_desired_transport_and_measurements()
    {
        using var fixture = new Fixture(); await fixture.Initialize();
        var snapshot = fixture.Snapshot;
        Assert.Equal(300, snapshot.AgitationSetpointRpm); Assert.Equal(2, snapshot.AirflowSetpointLpm);
        Assert.Equal(285, snapshot.Observation!.MeasuredAgitationRpm); Assert.Equal(1.8, snapshot.Observation.MeasuredFlowLpm);
        var gas = snapshot.Actuators.Single(a => a.Actuator == ActuatorId.Aeration);
        Assert.Contains("flowKp", gas.DesiredCommandJson); Assert.Contains("flowmeterComm", gas.DesiredCommandJson);
        Assert.Equal(gas.DesiredCommandJson, gas.TransportAcceptedCommandJson);
        fixture.Lease.DispatchAssay(CommandBuilders.FlowRoute(4, 10, GasRoute.VentAndNitrogen, fixture.Rig));
        Assert.Equal(2, snapshot.AirflowSetpointLpm);
        Assert.Equal(2, RecipeAssayReturnState.Number(RecipeAssayReturnState.Validate(snapshot)[ActuatorId.Aeration], CommandKeys.FlowSetpoint));
    }

    [Theory]
    [InlineData(GasRoute.Reactor, 300, 2, true)]
    [InlineData(GasRoute.VentAndNitrogen, 450, 3, false)]
    [InlineData(GasRoute.Closed, 0, 0, true)]
    public async Task Full_return_restores_route_modes_configuration_and_off_without_releasing_before_persistence(
        GasRoute route, double rpm, double flow, bool modbus)
    {
        using var fixture = new Fixture(); await fixture.Initialize(rpm, flow, route, modbus);
        var task = fixture.Restore(); await fixture.DriveRoute(modbus);
        await fixture.PushStable(rpm, flow, route, modbus);
        var result = await task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(KlaRestorationState.Confirmed, result.Restoration);
        Assert.Equal(fixture.Snapshot.SnapshotId, result.SnapshotId); Assert.NotNull(result.ConfirmationJson);
        Assert.Equal(CommandOwner.KlaAssay, fixture.Arbiter.OwnerOf(ActuatorId.Agitation));
        Assert.Equal(fixture.Snapshot.Actuators.Single(a => a.Actuator == ActuatorId.Aeration).DesiredCommandJson, fixture.Device.Sent[0]);
        Assert.Equal(CommandBuilders.MotorSetpoint(0).ToJson(), fixture.Device.Sent[1]);
        Assert.Equal(CommandBuilders.MotorControlMode(modbus).ToJson(), fixture.Device.Sent[2]);
        Assert.Equal(CommandBuilders.MotorSetpoint((int)rpm).ToJson(), fixture.Device.Sent[3]);
        var apiResult = new KlaAssayApiResult(new() { Restoration = result.Restoration }, 40)
            { ReturnSnapshotId = result.SnapshotId };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Lease.ReturnAsync(apiResult));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Lease.ReturnAsync(apiResult with { PersistenceReceiptId = "fabricated" }));
        await Fixture.ReturnWithStore(fixture.Lease, fixture.Contract(), fixture.Clock, apiResult);
        Assert.Equal(CommandOwner.Recipe, fixture.Arbiter.OwnerOf(ActuatorId.Agitation));
    }

    [Fact]
    public async Task A_fabricated_confirmed_result_cannot_resume_a_captured_lease_without_physical_recovery()
    {
        using var fixture = new Fixture(); await fixture.Initialize();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Lease.ReturnAsync(new(new() { Restoration = KlaRestorationState.Confirmed }, 40)
            { ReturnSnapshotId = fixture.Snapshot.SnapshotId, PersistenceReceiptId = "test-only" }));
        Assert.True(fixture.Lease.IsAssayAuthorityCurrent);
    }

    [Fact]
    public async Task Snapshot_roundtrip_preserves_authority_but_changed_configuration_is_refused_before_sending()
    {
        using var fixture = new Fixture(); await fixture.Initialize();
        var copy = JsonSerializer.Deserialize<KlaReturnSnapshot>(JsonSerializer.Serialize(fixture.Snapshot))!;
        fixture.Lease.ValidateRecoverySnapshot(copy);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RecipeAssayRestoration(fixture.Device, fixture.Clock)
            .RestoreAsync(fixture.Lease, fixture.Contract() with { BeforeAssay = copy with { AirInletInput = GasInput.Input2 } },
                new() { MaximumTelemetryAgeSeconds = 5 }));
        Assert.Empty(fixture.Device.Sent);
    }

    [Theory]
    [InlineData("ack")]
    [InlineData("rpm")]
    [InlineData("route")]
    [InlineData("gain")]
    [InlineData("freshness")]
    [InlineData("oxygen")]
    public async Task Wrong_or_missing_confirmation_never_counts_as_return(string fault)
    {
        using var fixture = new Fixture(); await fixture.Initialize();
        var task = fixture.Restore(fault == "oxygen" ? new() { MaximumTelemetryAgeSeconds = 5,
            MinimumOxygenPercent = 25, MaximumOxygenPercent = 60, MaximumOxygenSlopePercentPerSecond = .1 } : null, deadline: .15);
        await fixture.DriveRoute();
        for (var i = 0; i < 5; i++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(fault == "freshness" ? 6 : 1));
            var sample = fixture.Sample() with { FlowCommandId = fault == "ack" ? 1 : 2, FlowCommandAck = fault == "ack" ? 1 : 2,
                ServoRpm = fault == "rpm" ? 500 : 300, ServoMotorRouteAck = fault == "route" ? 0 : 1,
                FlowKp = fault == "gain" ? null : .25, OxygenUpdated = fault != "oxygen" };
            fixture.Device.PushTelemetry(sample); await Task.Yield();
        }
        var result = await task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(KlaRestorationState.Failed, result.Restoration);
        Assert.Equal(CommandOwner.KlaAssay, fixture.Arbiter.OwnerOf(ActuatorId.Agitation));
    }

    [Fact]
    public async Task Emergency_during_route_wait_prevents_final_reference_and_late_return()
    {
        using var fixture = new Fixture(); await fixture.Initialize();
        var task = fixture.Restore(deadline: .1);
        await WaitUntil(() => fixture.Device.Sent.Count == 3);
        fixture.Arbiter.DispatchSafety(CommandBuilders.MotorSetpoint(0), "emergency");
        fixture.Device.PushTelemetry(fixture.Sample());
        var result = await task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(result.EmergencyStopped); Assert.Equal(KlaRestorationState.Failed, result.Restoration);
        Assert.DoesNotContain(CommandBuilders.MotorSetpoint(300).ToJson(), fixture.Device.Sent);
        Assert.False(fixture.Lease.DispatchAssay(CommandBuilders.MotorSetpoint(300)).Accepted);
    }

    [Fact]
    public async Task Cancelled_acquisition_does_not_cancel_an_independent_recovery_deadline()
    {
        using var fixture = new Fixture(); await fixture.Initialize();
        using var acquisition = new CancellationTokenSource(); acquisition.Cancel();
        var task = fixture.Restore(); await fixture.DriveRoute(); await fixture.PushStable();
        Assert.Equal(KlaRestorationState.Confirmed, (await task.WaitAsync(TimeSpan.FromSeconds(3))).Restoration);
        Assert.True(acquisition.IsCancellationRequested);
    }

    [Fact]
    public async Task Disconnection_during_recovery_produces_failure_and_invalidates_return_authority()
    {
        using var fixture = new Fixture(); await fixture.Initialize();
        var task = fixture.Restore(); await WaitUntil(() => fixture.Device.Sent.Count == 3);
        fixture.Device.PushState(ConnectionState.Reconnecting);
        var result = await task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(KlaRestorationState.Failed, result.Restoration); Assert.True(result.EmergencyStopped);
        Assert.Equal(CommandOwner.Manual, fixture.Arbiter.OwnerOf(ActuatorId.Agitation));
    }

    public static IEnumerable<object[]> RunnerRecoveryCases()
    {
        foreach (var protocol in new[] { KlaAssayProtocol.Abiotic, KlaAssayProtocol.Biotic })
            for (var stage = 0; stage < (protocol == KlaAssayProtocol.Abiotic ? 6 : 5); stage++)
                foreach (var exit in new[] { "cancel", "fault", "review" }) yield return [protocol, stage, exit];
    }

    [Theory]
    [MemberData(nameof(RunnerRecoveryCases))]
    public async Task Common_runner_is_sealed_and_restores_snapshot_after_exit_at_each_protocol_stage(
        KlaAssayProtocol protocol, int stage, string exit)
    {
        using var fixture = new Fixture(); await fixture.Initialize();
        var directory = Path.Combine(Path.GetTempPath(), "opentechub-r13-" + Guid.NewGuid().ToString("N"));
        var store = new KlaTestStore(directory);
        try
        {
            var settings = new MemorySettingsService(new AppSettings { GasRig = GasRigSettings.From(fixture.Rig) });
            using var runner = new KlaTestRunner(fixture.Device, fixture.Arbiter, store, new KlaAnalysisEngine(), settings,
                fixture.Clock, actuationRelease: new(isIsolatedSimulation: true), recipeLease: fixture.Lease, recipeReturnSnapshot: fixture.Snapshot);
            var document = store.CreateTest("reserved runner", new KlaTestSettings
            {
                DOMinPercent = 10, DOMaxPercent = 85, StabilityDerivativeSpanSeconds = 2,
                StabilityDerivativeThresholdPercentPerSecond = .05, StabilityRequiredSamples = 3,
                PrestageFlowToleranceLpm = .2, PrestageFlowStableSamples = 3, PrestageFlowStabilityStdDevLpm = 0,
                AirPrestageLeadPercent = 0, MaxPrestageSeconds = 60
            });
            document.Protocol = protocol;
            document.NitrogenSourceConfirmedUtc = fixture.Clock.GetUtcNow();
            document.NitrogenIsolationConfirmedUtc = fixture.Clock.GetUtcNow();
            document.ProtocolSettings = new()
            {
                OperatingRange = KlaOperatingRange.CurrentCultivation, RemovalTargetDoPercent = 10,
                ReturnAgitationRpm = 300, InitialStabilitySeconds = 2, RecoveryStabilitySeconds = 2,
                CommandConfirmationTimeoutSeconds = 10, OxygenSampleTimeoutSeconds = 5,
                AerationReturn = new() { MaximumGasOffSeconds = 30, MaximumRecoverySeconds = 20 }
            };
            var condition = new KlaTestCondition { AgitationRpm = 450, AirflowLpm = 3, RequestedReplicates = 1 };
            document.Conditions.Add(condition); await runner.StartTestAsync(document);
            void Push(double oxygen, double flow, GasRoute route, long command)
            {
                fixture.Clock.Advance(TimeSpan.FromSeconds(1));
                fixture.Device.PushTelemetry(fixture.Sample(285, flow, route, command: command, oxygen: oxygen));
            }
            for (var i = 0; i < 3; i++) Push(80, 2, GasRoute.Reactor, 1);
            await runner.StartRunAsync(condition, 1);
            Assert.Equal(300, runner.CurrentRun!.Acquisition!.ReturnAgitationRpm);
            Assert.Equal(2, runner.CurrentRun.Acquisition.ReturnAirflowLpm);
            if (protocol == KlaAssayProtocol.Biotic)
            {
                if (stage >= 1) Push(80, 2, GasRoute.VentAndNitrogen, 2);
                if (stage >= 2) Push(10, 2, GasRoute.VentAndNitrogen, 2);
                if (stage >= 3) Push(20, 3, GasRoute.Reactor, 3);
                if (stage >= 4) await runner.StopRunAndReviewAsync();
                Assert.Equal(new[] { RunPhase.DivertingAir, RunPhase.MeasuringConsumption, RunPhase.SwitchingToReactor,
                    RunPhase.Reoxygenating, RunPhase.RestoringCultivation }[stage], runner.Phase);
            }
            else
            {
                if (stage >= 1) Push(80, 0, GasRoute.Closed, 2);
                if (stage >= 2) Push(70, 0, GasRoute.VentAndNitrogen, 3);
                if (stage >= 3) Push(10, 0, GasRoute.VentAndNitrogen, 3);
                if (stage >= 4) for (var i = 0; i < 6; i++) Push(10, 3, GasRoute.VentAndNitrogen, 4);
                if (stage >= 5) Push(20, 3, GasRoute.Reactor, 5);
                Assert.Equal(new[] { RunPhase.ClosingAllGas, RunPhase.OpeningNitrogen, RunPhase.Deoxygenating,
                    RunPhase.PrestagingAir, RunPhase.SwitchingToReactor, RunPhase.Reoxygenating }[stage], runner.Phase);
            }
            if (exit == "cancel") await runner.AbortTestAsync("controlled cancellation");
            else if (exit == "review") await runner.StopRunAndReviewAsync("controlled end");
            else { fixture.Clock.Advance(TimeSpan.FromSeconds(6)); runner.CheckWatchdog(); }
            runner.SealRecipeAcquisitionForRecovery();
            fixture.Device.Sent.Clear();
            Push(1, 0, GasRoute.Closed, 10); runner.CheckWatchdog();
            Assert.Throws<InvalidOperationException>(() => runner.SetDegassingAgitation(200));
            Assert.Empty(fixture.Device.Sent); // Even late operator/settings updates cannot enqueue through this runner.
            var task = fixture.Restore(protocol == KlaAssayProtocol.Biotic ? new() { MaximumTelemetryAgeSeconds = 5,
                MinimumOxygenPercent = 30, MaximumOxygenPercent = 90, MaximumOxygenSlopePercentPerSecond = .05 } : null);
            await fixture.DriveRoute(command: 20); await fixture.PushStable(command: 20);
            var result = await task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(KlaRestorationState.Confirmed, result.Restoration);
            runner.RecordRecipeRecovery(result);
            runner.SealRecipeAcquisitionForRecovery(); // Repeated cleanup must preserve confirmed recovery.
            Assert.Equal(KlaRestorationState.Confirmed, runner.CurrentRun.Outcome!.Restoration);
            Assert.Throws<InvalidOperationException>(runner.ConfirmRecipeReturn);
            await Fixture.ReturnWithStore(fixture.Lease, fixture.Contract(), fixture.Clock, new(new() { Restoration = result.Restoration }, 40)
                { ReturnSnapshotId = result.SnapshotId });
            runner.ConfirmRecipeReturn();
            Assert.Equal(CommandOwner.Recipe, fixture.Arbiter.OwnerOf(ActuatorId.Agitation));
            Assert.True(File.Exists(Path.Combine(directory, document.FolderName, "Corridas", runner.CurrentRun.FolderName, "estado-fisico.json")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
