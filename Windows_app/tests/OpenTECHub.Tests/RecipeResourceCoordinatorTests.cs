using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipeResourceCoordinatorTests
{
    private sealed class Producer(string id, params ActuatorId[] resources) : IRecipeResourceProducer, IRecipeResourceSuspension
    {
        public string NodeId => id;
        public IReadOnlyList<ActuatorId> Resources => resources;
        public int Pauses, Resumes, Stops;
        public TaskCompletionSource? Quiescence;
        public async Task<IRecipeResourceSuspension> SuspendAsync(CancellationToken ct)
        {
            Pauses++;
            if (Quiescence is not null) await Quiescence.Task.WaitAsync(ct);
            return this;
        }
        public void Resume() => Resumes++;
        public void Stop() => Stops++;
    }

    [Fact]
    public async Task No_handoff_before_producer_ack_and_no_resume_before_exact_return_and_persistence()
    {
        var request = RecipeExecutionContractTests.Request();
        var device = new RecordingDeviceService(); using var arbiter = new CommandArbiter(device, TimeProvider.System);
        arbiter.Claim(CommandOwner.Recipe, [ActuatorId.Agitation, ActuatorId.Aeration], "start");
        var coordinator = new RecipeResourceCoordinator(arbiter, TimeProvider.System);
        var cascade = new Producer("cascade", ActuatorId.Agitation, ActuatorId.Aeration)
            { Quiescence = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var temperature = new Producer("temperature", ActuatorId.Temperature);
        coordinator.Register(cascade); coordinator.Register(temperature);
        var pending = coordinator.ReserveForAssayAsync(request.Context, [ActuatorId.Agitation, ActuatorId.Aeration], TimeSpan.FromSeconds(5));
        Assert.False(pending.IsCompleted);
        Assert.Equal(CommandOwner.Recipe, arbiter.OwnerOf(ActuatorId.Agitation));
        cascade.Quiescence.SetResult();
        var lease = await pending;
        lease.BeginAssay(request.Restoration.BeforeAssay);
        Assert.Equal(CommandOwner.KlaAssay, arbiter.OwnerOf(ActuatorId.Agitation));
        Assert.Equal(0, cascade.Resumes); Assert.Equal(0, temperature.Pauses);
        var result = new KlaAssayApiResult(new() { Restoration = KlaRestorationState.Confirmed }, 40)
            { ReturnSnapshotId = request.Restoration.BeforeAssay.SnapshotId, PersistenceReceiptId = "durable-1" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => lease.ReturnAsync(result with { ReturnSnapshotId = Guid.NewGuid() }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => lease.ReturnAsync(result with { PersistenceReceiptId = null }));
        Assert.Equal(0, cascade.Resumes);
        await lease.ReturnAsync(result);
        Assert.Equal(CommandOwner.Recipe, arbiter.OwnerOf(ActuatorId.Agitation));
        Assert.Equal(1, cascade.Resumes);
        Assert.True(arbiter.Dispatch(CommandOwner.Recipe, CommandBuilders.MotorSetpoint(300)).Accepted);
    }

    [Fact]
    public async Task Cancelled_wait_does_not_disturb_an_active_assay()
    {
        var request = RecipeExecutionContractTests.Request();
        using var arbiter = new CommandArbiter(new RecordingDeviceService(), TimeProvider.System);
        arbiter.Claim(CommandOwner.Recipe, [ActuatorId.Agitation, ActuatorId.Aeration], "start");
        var coordinator = new RecipeResourceCoordinator(arbiter, TimeProvider.System);
        var cascade = new Producer("cascade", ActuatorId.Agitation, ActuatorId.Aeration); coordinator.Register(cascade);
        var first = await coordinator.ReserveForAssayAsync(request.Context, [ActuatorId.Agitation, ActuatorId.Aeration], TimeSpan.FromSeconds(5));
        first.BeginAssay(request.Restoration.BeforeAssay);
        using var cancellation = new CancellationTokenSource();
        var second = coordinator.ReserveForAssayAsync(request.Context, [ActuatorId.Agitation, ActuatorId.Aeration], TimeSpan.FromSeconds(5), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal(1, cascade.Pauses); Assert.Equal(0, cascade.Resumes);
        Assert.Equal(CommandOwner.KlaAssay, arbiter.OwnerOf(ActuatorId.Agitation));
        first.Fail(); Assert.Equal(1, cascade.Stops); Assert.Equal(0, cascade.Resumes);
    }

    [Fact]
    public async Task Aborting_before_actuation_restores_producers_and_releases_the_assay_slot()
    {
        var request = RecipeExecutionContractTests.Request();
        using var arbiter = new CommandArbiter(new RecordingDeviceService(), TimeProvider.System);
        arbiter.Claim(CommandOwner.Recipe, [ActuatorId.Agitation, ActuatorId.Aeration], "start");
        var coordinator = new RecipeResourceCoordinator(arbiter, TimeProvider.System);
        var cascade = new Producer("cascade", ActuatorId.Agitation); coordinator.Register(cascade);
        var first = await coordinator.ReserveForAssayAsync(request.Context, [ActuatorId.Agitation, ActuatorId.Aeration], TimeSpan.FromSeconds(5));
        first.AbortBeforeAssay(); Assert.Equal(1, cascade.Resumes);
        var next = await coordinator.ReserveForAssayAsync(request.Context, [ActuatorId.Agitation, ActuatorId.Aeration], TimeSpan.FromSeconds(5));
        next.AbortBeforeAssay(); Assert.Equal(2, cascade.Resumes);
    }

    [Fact]
    public async Task Emergency_prevents_late_return_from_resuming_the_controller()
    {
        var request = RecipeExecutionContractTests.Request();
        using var arbiter = new CommandArbiter(new RecordingDeviceService(), TimeProvider.System);
        arbiter.Claim(CommandOwner.Recipe, [ActuatorId.Agitation, ActuatorId.Aeration], "start");
        var coordinator = new RecipeResourceCoordinator(arbiter, TimeProvider.System);
        var cascade = new Producer("cascade", ActuatorId.Agitation); coordinator.Register(cascade);
        var lease = await coordinator.ReserveForAssayAsync(request.Context, [ActuatorId.Agitation, ActuatorId.Aeration], TimeSpan.FromSeconds(5));
        lease.BeginAssay(request.Restoration.BeforeAssay);
        arbiter.ReturnToManual("emergency", isSafeAbort: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lease.ReturnAsync(new(new() { Restoration = KlaRestorationState.Confirmed }, 40)
            { ReturnSnapshotId = request.Restoration.BeforeAssay.SnapshotId, PersistenceReceiptId = "durable-1" }));
        lease.Fail(); Assert.Equal(0, cascade.Resumes); Assert.Equal(1, cascade.Stops);
    }
}
