using System.Collections.Immutable;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipeRampGuardedDestinationTests
{
    private sealed class Destination : IRecipeRampDestination
    {
        public ImmutableArray<LinearRampSample> Applied;
        public TaskCompletionSource? Confirmation;
        public int Confirmations;
        public Task<bool> TryApplyAsync(ImmutableArray<LinearRampSample> references, CancellationToken cancellation)
        { cancellation.ThrowIfCancellationRequested(); Applied = references; return Task.FromResult(true); }
        public async Task ConfirmFinalAsync(ImmutableArray<LinearRampSample> references, CancellationToken cancellation)
        {
            Confirmations++;
            if (Confirmation is not null) await Confirmation.Task.WaitAsync(cancellation);
        }
    }

    private static LinearSetpointRampTrajectory Trajectory() => new(new() { Lines = [new()
    {
        Variable = SetpointVariable.Flow, StartSource = SetpointStartSource.Explicit,
        InitialSetpoint = 1, FinalSetpoint = 2, EndAfterSeconds = 10
    }] }, new Dictionary<SetpointVariable, double>(), (_, value) => value);

    [Fact]
    public async Task SamplesInsideLeaseAndCannotCompleteDuringAssayOrRecipePause()
    {
        var time = new TestClock(DateTimeOffset.UnixEpoch, virtualTimers: true);
        var clock = new RecipeRampActiveClock(time);
        using var producer = new RecipeRampResourceProducer("ramp", [ActuatorId.Aeration], clock);
        var target = new Destination();
        Assert.Throws<ArgumentException>(() => new RecipeRampGuardedDestination(producer,
            new RecipeRampActiveClock(time), target, time, TimeSpan.FromSeconds(5)));
        var guarded = new RecipeRampGuardedDestination(producer, clock, target, time, TimeSpan.FromSeconds(5));
        var trajectory = Trajectory();
        time.Advance(TimeSpan.FromSeconds(4));
        var first = await guarded.TryApplyTrajectoryAsync(trajectory, clock, [], default);
        Assert.Equal(1.4, first[0].Reference);
        var pause = await producer.SuspendAsync(default);
        time.Advance(TimeSpan.FromHours(2));
        Assert.Empty(await guarded.TryApplyTrajectoryAsync(trajectory, clock, first, default));
        Assert.False(await guarded.TryConfirmFinalAsync(trajectory.Sample(10), default));
        Assert.Equal(0, target.Confirmations);
        pause.Resume();
        time.Advance(TimeSpan.FromSeconds(6));
        var final = await guarded.TryApplyTrajectoryAsync(trajectory, clock, first, default);
        Assert.Equal(2, final[0].Reference);
        clock.Suspend("recipe");
        Assert.False(await guarded.TryConfirmFinalAsync(final, default));
        clock.Resume("recipe");
        Assert.True(await guarded.TryConfirmFinalAsync(final, default));
        Assert.Equal(1, target.Confirmations);
        await Assert.ThrowsAsync<InvalidOperationException>(() => guarded.TryApplyAsync(first, default));
    }

    [Fact]
    public async Task ReservationWaitsForConfirmationAndTimeoutDrainsItsLease()
    {
        var time = new TestClock(DateTimeOffset.UnixEpoch, virtualTimers: true);
        var clock = new RecipeRampActiveClock(time);
        using var producer = new RecipeRampResourceProducer("ramp", [ActuatorId.Aeration], clock);
        var target = new Destination { Confirmation = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var guarded = new RecipeRampGuardedDestination(producer, clock, target, time, TimeSpan.FromSeconds(5));
        using var arbiter = new CommandArbiter(new RecordingDeviceService(), time);
        arbiter.Claim(CommandOwner.Recipe, [ActuatorId.Agitation, ActuatorId.Aeration], "recipe");
        var coordinator = new RecipeResourceCoordinator(arbiter, time);
        coordinator.Register(producer);
        var confirming = guarded.TryConfirmFinalAsync(Trajectory().Sample(10), default);
        var pause = coordinator.ReserveForAssayAsync(RecipeExecutionContractTests.Request().Context,
            [ActuatorId.Agitation, ActuatorId.Aeration], TimeSpan.FromSeconds(30));
        Assert.False(pause.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<TimeoutException>(() => confirming);
        var receipt = await pause.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(CommandOwner.Recipe, arbiter.OwnerOf(ActuatorId.Aeration));
        Assert.False(await guarded.TryConfirmFinalAsync(Trajectory().Sample(10), default));
        receipt.AbortBeforeAssay();
        Assert.False(clock.IsSuspended);
        producer.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            guarded.TryApplyTrajectoryAsync(Trajectory(), clock, [], default));
    }
}
