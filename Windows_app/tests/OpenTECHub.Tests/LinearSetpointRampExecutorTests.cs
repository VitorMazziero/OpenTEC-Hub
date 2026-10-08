using System.Collections.Immutable;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class LinearSetpointRampExecutorTests
{
    private sealed class Destination : IRecipeRampDestination
    {
        public List<ImmutableArray<LinearRampSample>> Frames { get; } = [];
        public bool Available = true;
        public TaskCompletionSource Confirmation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Confirming;
        public Task<bool> TryApplyAsync(ImmutableArray<LinearRampSample> references, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!Available) return Task.FromResult(false);
            Frames.Add(references);
            return Task.FromResult(true);
        }
        public async Task ConfirmFinalAsync(ImmutableArray<LinearRampSample> references, CancellationToken cancellation)
        {
            Assert.All(references, sample => Assert.True(sample.AtFinalTarget));
            Confirming = true;
            await Confirmation.Task.WaitAsync(cancellation);
        }
    }

    private static LinearSetpointRampTrajectory Trajectory() => new(new() { Lines = [
        new() { Variable = SetpointVariable.Agitation, StartSource = SetpointStartSource.Explicit,
            InitialSetpoint = 300, FinalSetpoint = 400, EndAfterSeconds = 10 },
        new() { Variable = SetpointVariable.Flow, StartSource = SetpointStartSource.Explicit,
            InitialSetpoint = 2, FinalSetpoint = 1, EndAfterSeconds = 20 }] },
        new Dictionary<SetpointVariable, double>(), (_, value) => value);

    private static async Task Until(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!ready()) await Task.Delay(1, timeout.Token);
    }

    [Fact]
    public async Task CadenceSkipsOverdueValuesFreezesOnPauseAndAwaitsFinalConfirmation()
    {
        var time = new TestClock(DateTimeOffset.UnixEpoch, virtualTimers: true);
        var clock = new RecipeRampActiveClock(time);
        var destination = new Destination();
        using var stop = new CancellationTokenSource();
        var executor = new LinearSetpointRampExecutor(time);
        var task = executor.ExecuteAsync(Trajectory(), clock, destination, TimeSpan.FromSeconds(1), stop.Token);
        try
        {
            Assert.Single(destination.Frames);
            time.Advance(TimeSpan.FromSeconds(5));
            await Until(() => destination.Frames.Count == 2 && time.PendingTimers > 0);
            Assert.Equal(350, destination.Frames[1][0].Reference);
            clock.Suspend("recipe");
            time.Advance(TimeSpan.FromHours(2));
            await Until(() => time.PendingTimers > 0);
            Assert.Equal(2, destination.Frames.Count);
            clock.Resume("recipe");
            time.Advance(TimeSpan.FromSeconds(15));
            await Until(() => destination.Confirming);
            Assert.Equal(3, destination.Frames.Count);
            Assert.Equal(new double[] { 400, 1 }, destination.Frames[^1].Select(sample => sample.Reference));
            Assert.False(task.IsCompleted);
            destination.Confirmation.SetResult();
            await task.WaitAsync(TimeSpan.FromSeconds(3));
            await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(Trajectory(), clock,
                destination, TimeSpan.FromSeconds(1), CancellationToken.None));
        }
        finally { stop.Cancel(); await task; }
    }

    [Fact]
    public async Task UnavailableFrameDoesNotCountAsAppliedAndCancellationStopsConfirmation()
    {
        var time = new TestClock(DateTimeOffset.UnixEpoch, virtualTimers: true);
        var clock = new RecipeRampActiveClock(time);
        var destination = new Destination { Available = false };
        using var stop = new CancellationTokenSource();
        var task = new LinearSetpointRampExecutor(time).ExecuteAsync(Trajectory(), clock, destination,
            TimeSpan.FromSeconds(1), stop.Token);
        Assert.Empty(destination.Frames);
        time.Advance(TimeSpan.FromSeconds(20));
        await Until(() => time.PendingTimers > 0);
        Assert.False(destination.Confirming);
        destination.Available = true;
        time.Advance(TimeSpan.FromSeconds(1));
        await Until(() => destination.Confirming);
        Assert.Single(destination.Frames);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }
}
