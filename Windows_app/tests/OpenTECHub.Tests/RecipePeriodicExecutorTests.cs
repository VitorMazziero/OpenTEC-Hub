using System.Collections.Concurrent;
using System.IO;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipePeriodicExecutorTests
{
    private static PeriodicBlockInvocation Identity() => new() { ScheduleRunId = Guid.NewGuid(),
        SchedulerNodeId = "periodic", TargetNodeId = "kla", CoordinatedCascadeNodeId = "cascade",
        SlotIndex = 0, Schedule = new() { InitialDelaySeconds = 7200, PeriodSeconds = 14400 } };
    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(1, deadline.Token);
    }

    [Fact]
    public async Task Runs_at_two_six_ten_hours_despite_utc_changes()
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch, virtualTimers: true);
        var scheduler = new RecipePeriodicExecutor(clock);
        using var stop = new CancellationTokenSource();
        var slots = new ConcurrentQueue<long>(); var records = new ConcurrentQueue<RecipePeriodicSlotRecord>();
        var run = scheduler.RunAsync(Identity(), (item, _) => { slots.Enqueue(item.SlotIndex); return Task.CompletedTask; },
            item => { records.Enqueue(item); return Task.CompletedTask; }, stop.Token, TimeSpan.FromSeconds(1));
        for (var i = 0; i < 3; i++)
        {
            await Until(() => clock.PendingTimers > 0);
            clock.ShiftUtc(TimeSpan.FromDays(i % 2 == 0 ? 3 : -7));
            clock.Advance(TimeSpan.FromHours(i == 0 ? 2 : 4));
            await Until(() => records.Count(r => r.State == RecipePeriodicSlotState.Completed) == i + 1);
        }
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(new long[] { 0, 1, 2 }, slots.ToArray());
        Assert.Equal(new double[] { 7200, 21600, 36000 }, records.Where(r => r.State == RecipePeriodicSlotState.Started).Select(r => r.ElapsedSeconds));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Pause_and_delayed_wakeup_skip_slots_without_catchup(bool paused)
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch, virtualTimers: true);
        var scheduler = new RecipePeriodicExecutor(clock); scheduler.SetPaused(paused);
        using var stop = new CancellationTokenSource();
        var slots = new ConcurrentQueue<long>(); var records = new ConcurrentQueue<RecipePeriodicSlotRecord>();
        var run = scheduler.RunAsync(Identity(), (item, _) => { slots.Enqueue(item.SlotIndex); return Task.CompletedTask; },
            item => { records.Enqueue(item); return Task.CompletedTask; }, stop.Token, TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromHours(7));
        await Until(() => records.Count(r => r.State == RecipePeriodicSlotState.Skipped) == 2);
        Assert.Empty(slots);
        scheduler.SetPaused(false);
        await Until(() => clock.PendingTimers > 0);
        clock.Advance(TimeSpan.FromHours(3));
        await Until(() => records.Any(r => r.State == RecipePeriodicSlotState.Completed));
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(new long[] { 2 }, slots.ToArray());
    }

    [Fact]
    public async Task Target_crossing_slot_keeps_original_cadence_and_never_overlaps()
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch, virtualTimers: true);
        var scheduler = new RecipePeriodicExecutor(clock);
        using var stop = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slots = new ConcurrentQueue<long>(); var records = new ConcurrentQueue<RecipePeriodicSlotRecord>();
        var run = scheduler.RunAsync(Identity(), async (item, ct) => { slots.Enqueue(item.SlotIndex); await release.Task.WaitAsync(ct); },
            item => { records.Enqueue(item); return Task.CompletedTask; }, stop.Token, TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromHours(2)); await Until(() => slots.Count == 1);
        clock.Advance(TimeSpan.FromHours(5)); Assert.Single(slots);
        release.SetResult(); await Until(() => records.Any(r => r.State == RecipePeriodicSlotState.Skipped));
        await Until(() => clock.PendingTimers > 0); clock.Advance(TimeSpan.FromHours(3));
        await Until(() => slots.Count == 2); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(new long[] { 0, 2 }, slots.ToArray());
    }

    [Fact]
    public async Task Failed_start_record_prevents_target_dispatch()
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch, virtualTimers: true);
        var scheduler = new RecipePeriodicExecutor(clock); var count = 0;
        var run = scheduler.RunAsync(Identity(), (_, _) => { count++; return Task.CompletedTask; },
            _ => Task.FromException(new IOException("journal unavailable")), CancellationToken.None, TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromHours(2));
        await Assert.ThrowsAsync<IOException>(() => run); Assert.Equal(0, count);
    }

    [Fact]
    public async Task Cancellation_record_is_written_only_after_target_recovery()
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch, virtualTimers: true);
        var scheduler = new RecipePeriodicExecutor(clock);
        using var stop = new CancellationTokenSource();
        var acquiring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var records = new ConcurrentQueue<RecipePeriodicSlotRecord>();
        var run = scheduler.RunAsync(Identity(), async (_, ct) => { acquiring.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { recovering.SetResult(); await returned.Task; } },
            item => { records.Enqueue(item); return Task.CompletedTask; }, stop.Token, TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromHours(2)); await acquiring.Task;
        stop.Cancel(); await recovering.Task;
        Assert.False(run.IsCompleted); Assert.Single(records);
        returned.SetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(RecipePeriodicSlotState.Cancelled, records.Last().State);
    }

    [Fact]
    public async Task Pause_during_target_recovers_before_parking_until_next_slot()
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch, virtualTimers: true);
        var scheduler = new RecipePeriodicExecutor(clock);
        using var stop = new CancellationTokenSource();
        var acquiring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var records = new ConcurrentQueue<RecipePeriodicSlotRecord>(); var slots = new ConcurrentQueue<long>();
        var run = scheduler.RunAsync(Identity(), async (item, ct) => { slots.Enqueue(item.SlotIndex);
            if (item.SlotIndex > 0) return;
            acquiring.SetResult(); try { await Task.Delay(Timeout.Infinite, ct); }
            finally { recovering.SetResult(); await returned.Task; } },
            item => { records.Enqueue(item); return Task.CompletedTask; }, stop.Token, TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromHours(2)); await acquiring.Task;
        scheduler.SetPaused(true); await recovering.Task;
        Assert.Single(records); returned.SetResult();
        await Until(() => records.Any(r => r.State == RecipePeriodicSlotState.Cancelled));
        await Until(() => clock.PendingTimers > 0); clock.Advance(TimeSpan.FromHours(4));
        await Until(() => records.Any(r => r.State == RecipePeriodicSlotState.Skipped));
        scheduler.SetPaused(false); await Until(() => clock.PendingTimers > 0);
        clock.Advance(TimeSpan.FromHours(4)); await Until(() => slots.Count == 2);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(new long[] { 0, 2 }, slots.ToArray());
    }

    [Fact]
    public async Task Group_owner_exit_cancels_target_and_waits_for_recovery()
    {
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquiring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = RecipeParallelGroup.RunAsync([
            _ => exit.Task,
            async ct => { acquiring.SetResult(); try { await Task.Delay(Timeout.Infinite, ct); }
                finally { recovering.SetResult(); await returned.Task; } }
        ], CancellationToken.None, lifetimeOwner: 0);
        await acquiring.Task; exit.SetResult(); await recovering.Task;
        Assert.False(run.IsCompleted); returned.SetResult(); await run;
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Group_fault_or_emergency_waits_for_independent_recovery(bool emergency)
    {
        using var stop = new CancellationTokenSource();
        var fail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquiring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = RecipeParallelGroup.RunAsync([
            async ct => { await fail.Task.WaitAsync(ct); throw new InvalidOperationException("failure"); },
            async ct => { acquiring.SetResult(); try { await Task.Delay(Timeout.Infinite, ct); }
                finally { recovering.SetResult(); await returned.Task; } }
        ], stop.Token);
        await acquiring.Task; if (emergency) stop.Cancel(); else fail.SetResult();
        await recovering.Task; Assert.False(run.IsCompleted); returned.SetResult();
        if (emergency) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => run);
    }
}
