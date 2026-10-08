using System.IO;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipeRampPreparationTests
{
    private sealed class Cascade(ControllerReturnSnapshot state) : IRecipeResourceProducer, IRecipeResourceSuspension
    {
        public string NodeId => state.ControllerId;
        public IReadOnlyList<ActuatorId> Resources => [ActuatorId.Agitation, ActuatorId.Aeration, ActuatorId.Oxygen];
        public bool Paused;
        public Task<IRecipeResourceSuspension> SuspendAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); Paused = true; return Task.FromResult<IRecipeResourceSuspension>(this); }
        public ControllerReturnSnapshot CaptureControllerState() { Assert.True(Paused); return state; }
        public void Resume() => Paused = false;
        public void Stop() => Paused = true;
    }

    [Fact]
    public async Task OxygenPreparationCapturesTheSelectedPausedControllerAndResumesItAfterStorage()
    {
        var root = Path.Combine(Path.GetTempPath(), "ramp-cascade-prepare-" + Guid.NewGuid().ToString("N"));
        using var writer = new BackgroundFileWriter();
        var time = TimeProvider.System;
        using var arbiter = new CommandArbiter(new RecordingDeviceService(), time);
        var configuration = new RecipeRampBlockConfiguration(new() { Lines = [new() {
            Variable = SetpointVariable.Oxygen, OxygenTarget = RampOxygenTarget.ActiveCascadeReference,
            FinalSetpoint = 50, EndAfterSeconds = 60 }] }, "cascade");
        var resources = RecipeRampInitialState.ResourcesFor(configuration.Definition);
        arbiter.Claim(CommandOwner.Recipe, resources, "start");
        var state = new ControllerReturnSnapshot { ControllerId = "cascade", WasActive = true,
            StateVersion = "recipe-cascade-v1", StateJson = "{\"Pid\":{\"Setpoint\":30}}" };
        var cascade = new Cascade(state);
        var coordinator = new RecipeResourceCoordinator(arbiter, time);
        coordinator.Register(cascade);
        using var producer = new RecipeRampResourceProducer("ramp", resources, new RecipeRampActiveClock(time));
        var execution = Guid.NewGuid(); var invocation = Guid.NewGuid();
        try
        {
            var result = await coordinator.PrepareRampAsync(execution, invocation, configuration, producer, authority =>
            {
                Assert.True(cascade.Paused);
                return new(1, invocation, configuration, RecipeRampInitialState.Capture(configuration, arbiter, authority, time, state));
            }, new RecipeRampCheckpointStore(root, writer), TimeSpan.FromSeconds(5));
            Assert.False(cascade.Paused);
            Assert.Equal(state, result.InitialState.Controller);
            Assert.Equal(30, result.InitialState.ConfirmedStarts[SetpointVariable.Oxygen]);
            coordinator.Unregister(producer.NodeId);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static RecipeRampBlockConfiguration Configuration() => new(new() { Lines = [
        new() { Variable = SetpointVariable.Agitation, StartSource = SetpointStartSource.Explicit,
            InitialSetpoint = 300, FinalSetpoint = 400, EndAfterSeconds = 60 },
        new() { Variable = SetpointVariable.Flow, StartSource = SetpointStartSource.Explicit,
            InitialSetpoint = 1, FinalSetpoint = 2, EndAfterSeconds = 60 }] }, null);

    [Fact]
    public async Task DurablePreparationRegistersTheProducerBeforeAnAssayCanSuspendIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "ramp-prepare-" + Guid.NewGuid().ToString("N"));
        using var writer = new BackgroundFileWriter();
        var time = TimeProvider.System;
        using var arbiter = new CommandArbiter(new RecordingDeviceService(), time);
        var configuration = Configuration();
        var resources = RecipeRampInitialState.ResourcesFor(configuration.Definition);
        arbiter.Claim(CommandOwner.Recipe, resources, "start");
        var coordinator = new RecipeResourceCoordinator(arbiter, time);
        var clock = new RecipeRampActiveClock(time);
        using var producer = new RecipeRampResourceProducer("ramp", resources, clock);
        var execution = Guid.NewGuid(); var invocation = Guid.NewGuid();
        var store = new RecipeRampCheckpointStore(root, writer);
        try
        {
            var prepared = await coordinator.PrepareRampAsync(execution, invocation, configuration, producer,
                authority => new(1, invocation, configuration,
                    RecipeRampInitialState.Capture(configuration, arbiter, authority, time)), store, TimeSpan.FromSeconds(5));
            Assert.Equal(prepared.InitialState.SnapshotId, store.ReadStart(execution, invocation)!.InitialState.SnapshotId);
            Assert.False(clock.IsSuspended);
            var assay = await coordinator.ReserveForAssayAsync(RecipeExecutionContractTests.Request().Context,
                resources, TimeSpan.FromSeconds(5));
            Assert.True(clock.IsSuspended);
            assay.AbortBeforeAssay();
            Assert.False(clock.IsSuspended);
            coordinator.Unregister(producer.NodeId);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedPreparationStopsUnregisteredProducerAndReleasesDrainedReservation(bool foreignCapture)
    {
        var root = Path.Combine(Path.GetTempPath(), "ramp-prepare-failure-" + Guid.NewGuid().ToString("N"));
        if (!foreignCapture) File.WriteAllText(root, "occupied");
        using var writer = new BackgroundFileWriter();
        var time = TimeProvider.System;
        using var arbiter = new CommandArbiter(new RecordingDeviceService(), time);
        var configuration = Configuration();
        var resources = RecipeRampInitialState.ResourcesFor(configuration.Definition);
        arbiter.Claim(CommandOwner.Recipe, resources, "start");
        var coordinator = new RecipeResourceCoordinator(arbiter, time);
        var clock = new RecipeRampActiveClock(time);
        using var producer = new RecipeRampResourceProducer("ramp", resources, clock);
        var execution = Guid.NewGuid(); var invocation = Guid.NewGuid();
        try
        {
            var preparation = coordinator.PrepareRampAsync(execution, invocation, configuration, producer,
                authority => new(1, foreignCapture ? Guid.NewGuid() : invocation, configuration,
                    RecipeRampInitialState.Capture(configuration, arbiter, authority, time)),
                new RecipeRampCheckpointStore(root, writer), TimeSpan.FromSeconds(5));
            if (foreignCapture) await Assert.ThrowsAsync<InvalidOperationException>(() => preparation);
            else await Assert.ThrowsAnyAsync<IOException>(() => preparation);
            Assert.True(producer.StopToken.IsCancellationRequested);
            Assert.True(arbiter.Dispatch(CommandOwner.Recipe, CommandBuilders.MotorSetpoint(300)).Accepted);
            var assay = await coordinator.ReserveForAssayAsync(RecipeExecutionContractTests.Request().Context,
                resources, TimeSpan.FromSeconds(5));
            assay.AbortBeforeAssay();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            else if (File.Exists(root)) File.Delete(root);
        }
    }
}
