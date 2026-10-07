using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class KlaRecipePulseTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "recipe-pulse-" + Guid.NewGuid());
    private string Journal => Path.Combine(_folder, "journal.json");

    private static KlaRecipeRequest Invocation(KlaAssayProtocol protocol = KlaAssayProtocol.Abiotic,
        KlaCaptureMode capture = KlaCaptureMode.Single)
    {
        var request = RecipeExecutionContractTests.Request(protocol, capture);
        return request with { Definition = request.Definition with { Settings = request.Definition.Settings with
            { MaxDegassingTimeMinutes = 0.5, MaxPrestageSeconds = 15 } } };
    }

    private static KlaAssayApiRequest Pulse(KlaRecipeRequest? invocation = null, int replicate = 1, int attempt = 1)
    {
        var request = invocation ?? Invocation();
        return KlaRecipePulseMapper.Create(request, "simulator-A", request.Definition.Conditions[0].ConditionId,
            replicate, attempt, request.Restoration.BeforeAssay.CapturedUtc.AddMinutes(1));
    }

    private sealed class Executor : IKlaAssayExecution
    {
        public bool IsValidated => true;
        public KlaAssayExecutionCapabilities? Capabilities { get; set; } = new()
        { InstallationId = "simulator-A", ProfileId = "qualified-test-profile", ProfileVersion = "1",
            Protocols = [KlaAssayProtocol.Abiotic, KlaAssayProtocol.Biotic], EvidenceId = "test-isolation",
            IsIsolatedSimulation = true };
        public int Calls;
        public bool Cancelled;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<KlaAssayApiResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<KlaAssayApiResult> ExecuteWithRecoveryAsync(KlaAssayApiRequest request, CancellationToken token)
        {
            Calls++;
            using var registration = token.Register(() => Cancelled = true);
            Entered.TrySetResult();
            return await Result.Task;
        }
    }

    private static KlaAssayApiResult Result(KlaAssayApiRequest pulse, KlaScientificQuality quality = KlaScientificQuality.Valid)
        => new(new() { Restoration = KlaRestorationState.Confirmed, KlaQuality = quality }, 40)
        { ReturnSnapshotId = pulse.RecipePulse!.Invocation.Restoration.BeforeAssay.SnapshotId,
            PersistenceReceiptId = "durable-run-1" };

    [Theory]
    [InlineData(KlaAssayProtocol.Abiotic)]
    [InlineData(KlaAssayProtocol.Biotic)]
    public void Matrix_maps_to_stable_single_pulses_without_interactive_defaults(KlaAssayProtocol protocol)
    {
        var request = Invocation(protocol, KlaCaptureMode.Multiple);
        var pulses = request.Definition.Conditions.SelectMany(c => Enumerable.Range(1, c.RequestedReplicates)
            .Select(r => KlaRecipePulseMapper.Create(request, "simulator-A", c.ConditionId, r, 1,
                request.Restoration.BeforeAssay.CapturedUtc.AddMinutes(1)))).ToArray();
        Assert.Equal(3, pulses.Length);
        Assert.Equal(3, pulses.Select(p => p.RequestId).Distinct().Count());
        Assert.All(pulses, p =>
        {
            Assert.Equal(KlaCaptureMode.Single, p.Definition.CaptureMode);
            Assert.Equal(1, p.Definition.Conditions.Single().RequestedReplicates);
            Assert.Null(p.Definition.SequenceLimits);
            Assert.Equal(request.Context, p.RecipePulse!.Invocation.Context);
            Assert.Equal(RecipeContractSerializer.Fingerprint(request), p.RecipePulse.InvocationSha256);
            Assert.Equal(request.Restoration.BeforeAssay.SnapshotId, p.RecipePulse.Invocation.Restoration.BeforeAssay.SnapshotId);
            Assert.NotSame(request, p.RecipePulse.Invocation);
        });
        Assert.Equal(pulses[0].RequestId, Pulse(request).RequestId);
        Assert.NotEqual(pulses[0].RequestId, Pulse(request, attempt: 2).RequestId);
        Assert.Throws<ArgumentException>(() => Pulse(request, attempt: 3));
        Assert.Throws<ArgumentException>(() => Pulse(request, replicate: 3));
    }

    [Fact]
    public void Changed_payload_keeps_identity_but_cannot_change_a_registered_pulse()
    {
        var invocation = Invocation();
        var pulse = Pulse(invocation);
        var changed = Pulse(invocation with { Quality = invocation.Quality with { Version = "2" } });
        Assert.Equal(pulse.RequestId, changed.RequestId);
        using var api = new KlaAssayApi(Journal, new Executor());
        api.Create(pulse);
        Assert.Throws<InvalidOperationException>(() => api.Create(changed));
        Assert.Throws<ArgumentException>(() => (pulse with { Definition = pulse.Definition with
            { Conditions = [pulse.Definition.Conditions.Single() with { AirflowLpm = 3 }] } }).Validate());
        Assert.Throws<ArgumentException>(() => (pulse with { RecipePulse = pulse.RecipePulse! with
            { InvocationSha256 = new string('a', 64) } }).Validate());
        Assert.Throws<ArgumentException>(() => (pulse with { DeadlineUtc = invocation.AcquisitionDeadlineUtc.AddSeconds(1) }).Validate());
    }

    [Fact]
    public void Removal_budget_is_checked_instead_of_silently_shortening_the_protocol()
    {
        var request = Invocation();
        Assert.Throws<ArgumentException>(() => Pulse(request with { Definition = request.Definition with
            { Settings = request.Definition.Settings with { MaxDegassingTimeMinutes = 5 } } }));
    }

    [Theory]
    [InlineData("installation")]
    [InlineData("profile")]
    [InlineData("protocol")]
    [InlineData("missing")]
    public async Task Capability_mismatch_is_refused_before_budget_or_actuation(string mismatch)
    {
        var executor = new Executor();
        executor.Capabilities = mismatch switch
        {
            "installation" => executor.Capabilities! with { InstallationId = "other" },
            "profile" => executor.Capabilities! with { ProfileVersion = "other" },
            "protocol" => executor.Capabilities! with { Protocols = [KlaAssayProtocol.Biotic] },
            _ => null,
        };
        using var api = new KlaAssayApi(Journal, executor);
        var pulse = Pulse(); api.Create(pulse);
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.StartAsync(pulse.RequestId));
        Assert.Equal(KlaAssayApiState.Created, api.Observe(pulse.RequestId).State);
        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public void Declared_physical_capability_does_not_bypass_E7_biotic_gate()
    {
        var capabilities = new Executor().Capabilities! with { IsIsolatedSimulation = false };
        Assert.Throws<InvalidOperationException>(() => capabilities.EnsureAllows(Pulse(Invocation(KlaAssayProtocol.Biotic))));
    }

    [Theory]
    [InlineData(false, false, KlaAssayApiState.Inconclusive)]
    [InlineData(true, false, KlaAssayApiState.Completed)]
    [InlineData(true, true, KlaAssayApiState.Inconclusive)]
    public async Task Conditional_quality_and_OUR_follow_frozen_policy(bool allowed, bool requireOur, KlaAssayApiState expected)
    {
        var request = Invocation(KlaAssayProtocol.Biotic);
        request = request with { Quality = request.Quality with { RequireValidOur = requireOur,
            AllowedConditionalReasonCodes = allowed ? ["probe-unknown"] : [] } };
        var pulse = Pulse(request);
        var executor = new Executor(); using var api = new KlaAssayApi(Journal, executor);
        api.Create(pulse); await api.StartAsync(pulse.RequestId);
        executor.Result.SetResult(Result(pulse, KlaScientificQuality.Conditional) with { ReasonCodes = ["probe-unknown"] });
        var result = await api.WaitForCompletionAsync(pulse.RequestId).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(expected, result.State);
        Assert.Equal(expected == KlaAssayApiState.Completed, result.MayContinueRecipe);
        Assert.Equal(KlaOperatorDecision.Pending, result.Result!.Outcome.OperatorDecision);
    }

    [Theory]
    [InlineData("return", KlaAssayApiState.RestorationFailed)]
    [InlineData("snapshot", KlaAssayApiState.RestorationFailed)]
    [InlineData("persistence", KlaAssayApiState.PersistenceFailed)]
    public async Task Completion_requires_exact_return_and_persistence(string missing, KlaAssayApiState expected)
    {
        var pulse = Pulse(); var executor = new Executor(); using var api = new KlaAssayApi(Journal, executor);
        api.Create(pulse); await api.StartAsync(pulse.RequestId);
        var result = Result(pulse);
        result = missing switch
        {
            "return" => result with { Outcome = result.Outcome with { Restoration = KlaRestorationState.NotRequired } },
            "snapshot" => result with { ReturnSnapshotId = Guid.NewGuid() },
            _ => result with { PersistenceReceiptId = null },
        };
        executor.Result.SetResult(result);
        var observation = await api.WaitForCompletionAsync(pulse.RequestId).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(expected, observation.State); Assert.False(observation.MayContinueRecipe);
        var another = Pulse(attempt: 2); api.Create(another);
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.StartAsync(another.RequestId));
        if (missing == "persistence") Assert.Equal(KlaRestorationState.Confirmed, observation.Result!.Outcome.Restoration);
    }

    [Fact]
    public async Task Terminal_disk_failure_preserves_confirmed_physical_return_and_blocks_new_actuation()
    {
        var pulse = Pulse(); var executor = new Executor(); using var api = new KlaAssayApi(Journal, executor);
        api.Create(pulse); await api.StartAsync(pulse.RequestId);
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var wait = api.WaitForCompletionAsync(pulse.RequestId);
        File.Delete(Journal); Directory.CreateDirectory(Journal); // fail atomic destination replacement
        executor.Result.SetResult(Result(pulse));
        var error = await Record.ExceptionAsync(() => wait);
        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        var observation = api.Observe(pulse.RequestId);
        Assert.Equal(KlaAssayApiState.PersistenceFailed, observation.State);
        Assert.Equal(KlaRestorationState.Confirmed, observation.Result!.Outcome.Restoration);
        Assert.False(observation.MayContinueRecipe);
    }

    [Fact]
    public void Legacy_journal_is_migrated_without_inventing_recipe_identity_or_return_evidence()
    {
        Directory.CreateDirectory(_folder);
        var pulse = Pulse() with { RecipePulse = null };
        var legacy = new KlaAssayApiObservation(pulse, KlaAssayApiState.Completed,
            Result: new(new() { Restoration = KlaRestorationState.NotRequired, KlaQuality = KlaScientificQuality.Valid }, 40));
        File.WriteAllText(Journal, JsonSerializer.Serialize(new[] { legacy }));
        using var api = new KlaAssayApi(Journal, new Executor());
        Assert.Null(api.Observe(pulse.RequestId).Request.RecipePulse);
        Assert.False(api.Observe(pulse.RequestId).MayContinueRecipe);
        using var json = JsonDocument.Parse(File.ReadAllText(Journal));
        Assert.Equal(2, json.RootElement.GetProperty("SchemaVersion").GetInt32());
    }

    [Fact]
    public void Unknown_journal_version_is_refused_without_overwriting_evidence()
    {
        Directory.CreateDirectory(_folder);
        var original = "{\"SchemaVersion\":999,\"Requests\":[]}";
        File.WriteAllText(Journal, original);
        Assert.Throws<InvalidDataException>(() => new KlaAssayApi(Journal, new Executor()));
        Assert.Equal(original, File.ReadAllText(Journal));
    }

    [Fact]
    public async Task Cancelling_observation_does_not_cancel_acquisition_or_release_the_reservation()
    {
        var pulse = Pulse(); var executor = new Executor(); using var api = new KlaAssayApi(Journal, executor);
        api.Create(pulse); await api.StartAsync(pulse.RequestId);
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var wait = api.WaitForCompletionAsync(pulse.RequestId, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.False(executor.Cancelled);
        Assert.Equal(KlaAssayApiState.Running, api.Observe(pulse.RequestId).State);
        executor.Result.SetResult(Result(pulse));
        Assert.Equal(KlaAssayApiState.Completed, (await api.WaitForCompletionAsync(pulse.RequestId)).State);
    }

    private sealed class DeadlineClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public ControlledTimer? Timer { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => Timer = new(callback, state, dueTime);
        public sealed class ControlledTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            public TimeSpan Due => due;
            public bool Disposed { get; private set; }
            public void Fire() { if (!Disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    [Fact]
    public async Task Deadline_uses_injected_clock_and_still_waits_for_recovery()
    {
        var pulse = Pulse(); var executor = new Executor();
        var clock = new DeadlineClock(pulse.RecipePulse!.Invocation.Restoration.BeforeAssay.CapturedUtc);
        using var api = new KlaAssayApi(Journal, executor, clock);
        api.Create(pulse); await api.StartAsync(pulse.RequestId);
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var wait = api.WaitForCompletionAsync(pulse.RequestId);
        Assert.Equal(pulse.DeadlineUtc - clock.GetUtcNow(), clock.Timer!.Due);
        clock.Timer.Fire();
        Assert.True(executor.Cancelled); Assert.False(wait.IsCompleted);
        executor.Result.SetResult(Result(pulse));
        Assert.Equal(KlaAssayApiState.Cancelled, (await wait).State);
        Assert.True(clock.Timer.Disposed);
    }

    public void Dispose() { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }
}
