using System.IO;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipePeriodicJournalTests
{
    [Fact]
    public async Task Reopens_immutable_slot_history_and_refuses_replay_or_changed_recipe()
    {
        var root = Path.Combine(Path.GetTempPath(), "periodic-journal-" + Guid.NewGuid().ToString("N"));
        var runId = Guid.NewGuid(); var clock = new TestClock(DateTimeOffset.UnixEpoch);
        using var writer = new BackgroundFileWriter(synchronous: true);
        var invocation = new PeriodicBlockInvocation { ScheduleRunId = Guid.NewGuid(), SchedulerNodeId = "periodic",
            TargetNodeId = "kla", CoordinatedCascadeNodeId = "cascade", SlotIndex = 0,
            Schedule = new() { InitialDelaySeconds = 7200, PeriodSeconds = 14400 } };
        try
        {
            var journal = new RecipePeriodicJournal(root, runId, new string('a', 64), writer, clock);
            var started = new RecipePeriodicSlotRecord(invocation, RecipePeriodicSlotState.Started, 7200);
            await journal.RecordAsync(started);
            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.RecordAsync(started));
            await journal.RecordAsync(started with { State = RecipePeriodicSlotState.Completed, ElapsedSeconds = 7260 });
            await journal.RecordAsync(new(invocation with { SlotIndex = 1 }, RecipePeriodicSlotState.Skipped, 21600, "paused"));
            var reopened = new RecipePeriodicJournal(root, runId, new string('a', 64), writer, clock);
            Assert.Equal(3, reopened.Read(invocation.ScheduleRunId).Count);
            await Assert.ThrowsAsync<InvalidDataException>(() => reopened.RecordAsync(started with { ElapsedSeconds = 7201 }));
            await Assert.ThrowsAsync<InvalidDataException>(() => reopened.RecordAsync(started with { State = RecipePeriodicSlotState.Cancelled }));
            await Assert.ThrowsAsync<InvalidDataException>(() => reopened.RecordAsync(started with { Invocation = invocation with { SlotIndex = 1 }, ElapsedSeconds = 21600 }));
            Assert.Throws<InvalidDataException>(() => new RecipePeriodicJournal(root, runId, new string('b', 64), writer, clock).Read(invocation.ScheduleRunId));
            var path = Directory.GetFiles(root, "*Completed.json", SearchOption.AllDirectories).Single();
            File.Delete(path);
            // An unfinished Started record stays visible; it is never silently converted to a new invocation.
            Assert.Equal(2, reopened.Read(invocation.ScheduleRunId).Count);
            await Assert.ThrowsAsync<FileNotFoundException>(() => reopened.RecordAsync(started with { ElapsedSeconds = 7202 }));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Reopened_started_slot_cannot_dispatch_target_again()
    {
        var root = Path.Combine(Path.GetTempPath(), "periodic-replay-" + Guid.NewGuid().ToString("N"));
        var runId = Guid.NewGuid(); var clock = new TestClock(DateTimeOffset.UnixEpoch, virtualTimers: true);
        using var writer = new BackgroundFileWriter(synchronous: true);
        var invocation = new PeriodicBlockInvocation { ScheduleRunId = Guid.NewGuid(), SchedulerNodeId = "periodic", TargetNodeId = "kla",
            SlotIndex = 0, Schedule = new() { InitialDelaySeconds = 1, PeriodSeconds = 10 } };
        try
        {
            await new RecipePeriodicJournal(root, runId, new string('a', 64), writer, clock)
                .RecordAsync(new(invocation, RecipePeriodicSlotState.Started, 1));
            var reopened = new RecipePeriodicJournal(root, runId, new string('a', 64), writer, clock);
            var dispatched = 0;
            var execution = new RecipePeriodicExecutor(clock).RunAsync(invocation,
                (_, _) => { dispatched++; return Task.CompletedTask; }, reopened.RecordAsync,
                CancellationToken.None, TimeSpan.Zero);
            clock.Advance(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAsync<InvalidOperationException>(() => execution); Assert.Equal(0, dispatched);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Write_failure_is_sticky_and_prevents_confirmed_slot()
    {
        var root = Path.Combine(Path.GetTempPath(), "periodic-journal-" + Guid.NewGuid().ToString("N"));
        var runId = Guid.NewGuid(); var scheduleId = Guid.NewGuid();
        using var writer = new BackgroundFileWriter(synchronous: true);
        try
        {
            var folder = Path.Combine(root, runId.ToString("N"), scheduleId.ToString("N"));
            Directory.CreateDirectory(Path.Combine(folder, "slot-00000000000000000000-Started.json"));
            var journal = new RecipePeriodicJournal(root, runId, new string('a', 64), writer, new TestClock(DateTimeOffset.UnixEpoch));
            var invocation = new PeriodicBlockInvocation { ScheduleRunId = scheduleId, SchedulerNodeId = "periodic", TargetNodeId = "kla",
                SlotIndex = 0, Schedule = new() { InitialDelaySeconds = 1, PeriodSeconds = 10 } };
            var failure = await Assert.ThrowsAsync<AggregateException>(() => journal.RecordAsync(new(invocation, RecipePeriodicSlotState.Started, 1)));
            Assert.IsType<UnauthorizedAccessException>(Assert.Single(failure.InnerExceptions));
            Assert.Empty(journal.Read(scheduleId));
            await Assert.ThrowsAsync<AggregateException>(() => journal.RecordAsync(new(invocation, RecipePeriodicSlotState.Started, 1)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
