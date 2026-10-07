using System.IO;
using System.Collections.Immutable;
using System.Text.Json;
using OpenTECHub.Services.KlaTesting;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class KlaAssayApiTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "kla-e6-" + Guid.NewGuid());
    private string Journal => Path.Combine(_directory, "requests.json");
    private static KlaAssayApiRequest Request() => new(Guid.NewGuid(), "cultivo-A", new()
    {
        Protocol = KlaAssayProtocol.Biotic, CaptureMode = KlaCaptureMode.Single,
        Settings = new() { MaxDegassingTimeMinutes = 1 },
        ProtocolSettings = new() { AerationReturn = new() { MaximumGasOffSeconds = 60 } },
        Conditions = ImmutableArray.Create(new KlaAssayCondition(Guid.NewGuid(), 1, 400, 3, 1))
    }, KlaConditionSelection.Explicit, DateTimeOffset.UtcNow.AddMinutes(1), DateTimeOffset.UtcNow.AddMinutes(10), new(3, 240, 0));

    private sealed class Executor : IKlaAssayExecution
    {
        public bool IsValidated { get; set; } = true;
        public int Calls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<KlaAssayApiResult> Recovery { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SawCancellation;
        public async Task<KlaAssayApiResult> ExecuteWithRecoveryAsync(KlaAssayApiRequest request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            using var registration = token.Register(() => SawCancellation = true);
            Entered.TrySetResult();
            return await Recovery.Task; // recovery intentionally ignores acquisition cancellation
        }
    }
    private static KlaAssayApiResult Result(KlaRestorationState restoration = KlaRestorationState.Confirmed,
        KlaScientificQuality quality = KlaScientificQuality.Valid) => new(new() { Restoration = restoration, KlaQuality = quality }, 40);
    private static async Task<KlaAssayApiObservation> Finish(KlaAssayApi api, Guid id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (api.Observe(id).State == KlaAssayApiState.Running) await Task.Delay(10, deadline.Token);
        return api.Observe(id);
    }

    [Fact]
    public async Task Duplicate_id_never_dispatches_twice_and_completed_reconnect_does_not_restart()
    {
        var executor = new Executor(); var request = Request();
        using (var api = new KlaAssayApi(Journal, executor))
        {
            api.Create(request); api.Create(request);
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => api.StartAsync(request.RequestId)));
            await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, executor.Calls);
            Assert.Throws<InvalidOperationException>(() => api.Create(request with { CultivationId = "outro" }));
            executor.Recovery.SetResult(Result());
            Assert.Equal(KlaAssayApiState.Completed, (await Finish(api, request.RequestId)).State);
            Assert.NotNull(api.GetResult(request.RequestId));
        }
        using var reopened = new KlaAssayApi(Journal, executor);
        await reopened.StartAsync(request.RequestId);
        Assert.Equal(1, executor.Calls);
        Assert.Equal(KlaOperatorDecision.Pending, reopened.GetResult(request.RequestId)!.Outcome.OperatorDecision);
    }

    [Fact]
    public async Task Cancellation_waits_for_recovery_and_blocks_a_second_pulse()
    {
        var executor = new Executor(); using var api = new KlaAssayApi(Journal, executor);
        var request = Request(); api.Create(request); await api.StartAsync(request.RequestId);
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cancel = api.CancelWithRecoveryAsync(request.RequestId);
        Assert.False(cancel.IsCompleted); Assert.True(executor.SawCancellation);
        var other = Request(); api.Create(other);
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.StartAsync(other.RequestId));
        Assert.Throws<InvalidOperationException>(() => api.Dispose());
        executor.Recovery.SetResult(Result());
        Assert.Equal(KlaAssayApiState.Cancelled, (await cancel).State);
        Assert.False(api.Observe(request.RequestId).MayContinueRecipe);
    }

    [Theory]
    [InlineData(KlaRestorationState.Failed)]
    [InlineData(KlaRestorationState.NotRequired)]
    [InlineData(KlaRestorationState.Pending)]
    public async Task Biotic_unconfirmed_recovery_overrides_valid_science_and_blocks_dispatch(KlaRestorationState restoration)
    {
        var executor = new Executor(); using var api = new KlaAssayApi(Journal, executor);
        var request = Request(); api.Create(request); await api.StartAsync(request.RequestId);
        executor.Recovery.SetResult(Result(restoration));
        var observation = await Finish(api, request.RequestId);
        Assert.Equal(KlaAssayApiState.RestorationFailed, observation.State);
        Assert.False(observation.MayContinueRecipe);
        var other = Request(); api.Create(other);
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.StartAsync(other.RequestId));
        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public async Task Inconclusive_can_continue_only_under_explicit_policy_after_return()
    {
        var executor = new Executor(); using var api = new KlaAssayApi(Journal, executor);
        var request = Request() with { FailurePolicy = KlaAssayFailurePolicy.ContinueAfterConfirmedReturn };
        api.Create(request); await api.StartAsync(request.RequestId);
        executor.Recovery.SetResult(Result(quality: KlaScientificQuality.Inconclusive));
        var observation = await Finish(api, request.RequestId);
        Assert.Equal(KlaAssayApiState.Inconclusive, observation.State);
        Assert.True(observation.MayContinueRecipe);
    }

    [Fact]
    public async Task Missed_start_is_skipped_and_unvalidated_execution_is_refused_before_dispatch()
    {
        var executor = new Executor { IsValidated = false }; using var api = new KlaAssayApi(Journal, executor);
        var expired = Request() with { StartDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(-1) };
        api.Create(expired);
        Assert.Equal(KlaAssayApiState.Skipped, (await api.StartAsync(expired.RequestId)).State);
        var request = Request(); api.Create(request);
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.StartAsync(request.RequestId));
        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public async Task Cultivation_limits_cannot_be_loosened_and_survive_reopening()
    {
        var executor = new Executor(); var request = Request() with { Limits = new(1, 80, 0) };
        using (var api = new KlaAssayApi(Journal, executor))
        {
            api.Create(request); await api.StartAsync(request.RequestId); executor.Recovery.SetResult(Result());
            await Finish(api, request.RequestId);
        }
        using var reopened = new KlaAssayApi(Journal, executor);
        var other = Request() with { Limits = new(100, 10000, 0) }; reopened.Create(other);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.StartAsync(other.RequestId));
        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public async Task Interval_defers_without_consuming_or_dispatching_a_request()
    {
        var executor = new Executor(); using var api = new KlaAssayApi(Journal, executor);
        var request = Request() with { Limits = new(3, 240, 300) };
        api.Create(request); await api.StartAsync(request.RequestId); executor.Recovery.SetResult(Result());
        await Finish(api, request.RequestId);
        var other = Request(); api.Create(other);
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.StartAsync(other.RequestId));
        Assert.Equal(KlaAssayApiState.Created, api.Observe(other.RequestId).State);
        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public async Task Reopening_a_reserved_request_never_actuates_and_blocks_new_pulses()
    {
        Directory.CreateDirectory(_directory);
        var request = Request();
        File.WriteAllText(Journal, JsonSerializer.Serialize(new[] { new KlaAssayApiObservation(request,
            KlaAssayApiState.Running, DateTimeOffset.UtcNow) }));
        var executor = new Executor(); using var api = new KlaAssayApi(Journal, executor);
        Assert.Equal(KlaAssayApiState.Interrupted, (await api.StartAsync(request.RequestId)).State);
        var other = Request(); api.Create(other);
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.StartAsync(other.RequestId));
        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public void Journal_has_exclusive_owner_and_corruption_fails_closed()
    {
        using (var api = new KlaAssayApi(Journal, new Executor()))
            Assert.Throws<IOException>(() => new KlaAssayApi(Journal, new Executor()));
        File.WriteAllText(Journal, "invalid");
        Assert.Throws<JsonException>(() => new KlaAssayApi(Journal, new Executor()));
    }

    [Fact]
    public async Task Closed_api_cannot_cancel_a_record_after_another_instance_takes_the_journal()
    {
        var executor = new Executor(); var request = Request();
        var closed = new KlaAssayApi(Journal, executor);
        closed.Create(request); closed.Dispose();
        using var owner = new KlaAssayApi(Journal, executor);
        var before = File.ReadAllBytes(Journal);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => closed.CancelWithRecoveryAsync(request.RequestId));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => closed.StartAsync(request.RequestId));
        Assert.Throws<ObjectDisposedException>(() => closed.Create(Request()));
        Assert.Equal(before, File.ReadAllBytes(Journal));
        Assert.Equal(KlaAssayApiState.Created, owner.Observe(request.RequestId).State);
        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public void Periodicity_skips_old_occurrences_instead_of_producing_a_burst()
    {
        var start = DateTimeOffset.UnixEpoch;
        var result = KlaPeriodicSchedule.Latest(start, TimeSpan.FromMinutes(10), start.AddMinutes(35));
        Assert.Equal(start.AddMinutes(30), result.DueUtc);
        Assert.Equal(start.AddMinutes(40), result.NextDueUtc); Assert.Equal(3, result.SkippedOccurrences);
        Assert.Null(KlaPeriodicSchedule.Latest(result.NextDueUtc, TimeSpan.FromMinutes(10), start.AddMinutes(35)).DueUtc);
    }

    [Fact]
    public void Current_condition_is_frozen_and_checked_against_configured_range()
    {
        var template = Request();
        var request = KlaAssayApiRequest.AtCurrentCondition(template, 800, 12);
        request.Validate(); Assert.Equal(800, request.Definition.Conditions[0].AgitationRpm);
        Assert.Equal(400, template.Definition.Conditions[0].AgitationRpm);
        request = request with { Definition = request.Definition with { ProtocolSettings = new() { OperatingRange = KlaOperatingRange.CurrentCultivation } } };
        Assert.Throws<ArgumentException>(() => KlaAssayApiRequest.AtCurrentCondition(request, 1100, 12).Validate());
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
