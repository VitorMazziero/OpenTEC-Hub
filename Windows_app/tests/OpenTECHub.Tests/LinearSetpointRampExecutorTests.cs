using System.Collections.Immutable;
using System.IO;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class LinearSetpointRampExecutorTests
{
    private sealed class Destination : IRecipeRampDestination, IRecipeRampConfirmationSource
    {
        public ImmutableArray<RecipeRampFinalConfirmation> FinalConfirmations { get; set; } = [];
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

    [Fact]
    public async Task FailedStartStoragePreventsDispatchAndRemovesOnlyItsOwnClockSuspension()
    {
        var time = new TestClock(DateTimeOffset.UnixEpoch, virtualTimers: true);
        var clock = new RecipeRampActiveClock(time);
        clock.Suspend("recipe-paused");
        var destination = new Destination();
        var root = Path.Combine(Path.GetTempPath(), "ramp-start-blocked-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(root, "occupied");
        using var writer = new BackgroundFileWriter();
        var initial = new RecipeRampInitialState(Guid.NewGuid(), Guid.NewGuid(), "ramp", DateTimeOffset.UtcNow, [], [], null);
        var start = new RecipeRampStartCheckpoint(1, Guid.NewGuid(), new(new() { Lines = [new() {
            Variable = SetpointVariable.Agitation, StartSource = SetpointStartSource.Explicit,
            InitialSetpoint = 300, FinalSetpoint = 400, EndAfterSeconds = 10 }] }, null), initial);
        try
        {
            await Assert.ThrowsAnyAsync<IOException>(() => new LinearSetpointRampExecutor(time).ExecutePersistedAsync(start,
                new RecipeRampCheckpointStore(root, writer), clock, destination, destination,
                (_, value) => value, TimeSpan.FromSeconds(1), CancellationToken.None));
            Assert.Empty(destination.Frames);
            Assert.True(clock.IsSuspended);
            clock.Resume("recipe-paused");
            Assert.False(clock.IsSuspended);
        }
        finally { File.Delete(root); }
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PersistedExecutionRequiresConfirmedEvidenceAndReturnsOnlyReadableTerminal(bool validEvidence)
    {
        var time = new TestClock(DateTimeOffset.UnixEpoch, virtualTimers: true);
        var clock = new RecipeRampActiveClock(time);
        var destination = new Destination();
        var root = Path.Combine(Path.GetTempPath(), "ramp-execution-" + Guid.NewGuid().ToString("N"));
        using var writer = new BackgroundFileWriter();
        var store = new RecipeRampCheckpointStore(root, writer);
        var initial = new RecipeRampInitialState(Guid.NewGuid(), Guid.NewGuid(), "ramp", DateTimeOffset.UtcNow, [], [], null);
        var start = new RecipeRampStartCheckpoint(1, Guid.NewGuid(), new(new() { Lines = [new() {
            Variable = SetpointVariable.Agitation, StartSource = SetpointStartSource.Explicit,
            InitialSetpoint = 300, FinalSetpoint = 400, EndAfterSeconds = 10 }] }, null), initial);
        using var stop = new CancellationTokenSource();
        var running = new LinearSetpointRampExecutor(time).ExecutePersistedAsync(start, store, clock, destination,
            destination, (_, value) => value, TimeSpan.FromSeconds(1), stop.Token);
        try
        {
            await Until(() => destination.Frames.Count > 0 && time.PendingTimers > 0);
            Assert.NotNull(store.ReadStart(initial.ExecutionId, start.InvocationId));
            Assert.Null(store.ReadTerminal(initial.ExecutionId, start.InvocationId));
            time.Advance(TimeSpan.FromSeconds(10));
            await Until(() => destination.Confirming);
            Assert.False(running.IsCompleted);
            destination.FinalConfirmations = [new(SetpointVariable.Agitation, null, 400,
                validEvidence ? RecipeRampConfirmationEvidence.ProcessFeedback : RecipeRampConfirmationEvidence.TransportAccepted,
                time.GetUtcNow(), 400, 1)];
            destination.Confirmation.SetResult();
            if (validEvidence)
            {
                var terminal = await running.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Equal(RecipeRampTerminalStatus.Completed, terminal.Status);
                Assert.Equal(System.Text.Json.JsonSerializer.Serialize(terminal),
                    System.Text.Json.JsonSerializer.Serialize(store.ReadTerminal(initial.ExecutionId, start.InvocationId)));
            }
            else
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => running);
                Assert.Null(store.ReadTerminal(initial.ExecutionId, start.InvocationId));
            }
        }
        finally
        {
            stop.Cancel();
            try { await running; } catch (Exception) { }
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
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
