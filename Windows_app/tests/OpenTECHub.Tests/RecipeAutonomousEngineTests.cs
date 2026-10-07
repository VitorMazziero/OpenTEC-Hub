using System.Security.Cryptography;
using System.Text;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipeAutonomousEngineTests
{
    private sealed class Source : IRecipeAutonomousWorkSource
    {
        public Func<RecipeDocument, Guid, RecipeAutonomousExecutionPlan>? Build { get; init; }
        public bool Available { get; init; } = true;
        public bool CanExecute(RecipeDocument recipe, out string? reason)
        { reason = Available ? null : "perfil indisponível"; return Available; }
        public RecipeAutonomousExecutionPlan CreateWork(RecipeDocument recipe, Guid executionId, RecipeResourceCoordinator resources)
            => Build!(recipe, executionId);
    }
    private static KlaAssayExecutionCapabilities Capability => new()
    {
        InstallationId = "isolated-engine", ProfileId = "qualified-test-profile", ProfileVersion = "1",
        Protocols = [KlaAssayProtocol.Abiotic], EvidenceId = "engine-fixture", IsIsolatedSimulation = true
    };
    private static KlaRecipeResult Result(RecipeDocument recipe, Guid runId, PeriodicBlockInvocation? invocation,
        KlaRecipeTerminalStatus status = KlaRecipeTerminalStatus.Inconclusive) => new()
    {
        Context = RecipeExecutionContractTests.Request().Context with { RecipeRunId = runId, NodeId = "kla",
            InvocationId = Guid.NewGuid(), RecipeSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RecipeSerializer.Serialize(recipe)))).ToLowerInvariant() },
        PeriodicInvocation = invocation, SessionId = Guid.NewGuid(), SessionFolder = "session",
        Status = status, Attempts = [], PreAssayStateRestored = true, PersistenceConfirmed = true
    };
    private static RecipeDocument Recipe(bool periodic)
    {
        var recipe = new RecipeDocument();
        var assay = RecipeAutonomousBlockConfigurationTests.ConfiguredKla();
        assay.Set("failurePolicy", nameof(KlaRecipeFailurePolicy.ContinueWithoutResultAfterRestoration));
        recipe.Nodes.AddRange([RecipeNode.Create(NodeType.Start, id: "start"), assay, RecipeNode.Create(NodeType.End, id: "end")]);
        if (!periodic)
            recipe.Connections.AddRange([new("start", ConnectorNames.Out, "kla", ConnectorNames.In),
                new("kla", ConnectorNames.Out, "end", ConnectorNames.In)]);
        else
        {
            var schedule = RecipeNode.Create(NodeType.Periodic, id: "periodic");
            schedule.Set("coordinatedCascadeId", "cascade");
            schedule.Set("initialDelay", 2); schedule.Set("initialDelayUnit", nameof(TimeUnit.Seconds));
            schedule.Set("period", 4); schedule.Set("periodUnit", nameof(TimeUnit.Seconds));
            recipe.Nodes.AddRange([schedule, RecipeNode.Create(NodeType.CascadeControl, id: "cascade"),
                RecipeNode.Create(NodeType.ManualIntervention, id: "gate")]);
            recipe.Connections.AddRange([new("start", ConnectorNames.Out, "periodic", ConnectorNames.In),
                new("start", ConnectorNames.Out, "cascade", ConnectorNames.In),
                new("periodic", ConnectorNames.Out, "kla", ConnectorNames.In),
                new("cascade", ConnectorNames.LoopOut, "gate", ConnectorNames.In),
                new("cascade", ConnectorNames.Out, "end", ConnectorNames.In)]);
        }
        return recipe;
    }
    private static RecipeAutonomousExecutionPlan Plan(RecipeDocument recipe, Guid runId,
        Func<PeriodicBlockInvocation?, CancellationToken, Task<KlaRecipeResult>> execute,
        List<RecipePeriodicSlotRecord>? records = null)
        => new([new("kla", Capability, execute)], recipe.Nodes.Where(n => n.Type == NodeType.Periodic).Select(node =>
            new RecipePeriodicDefinition(new() { ScheduleRunId = Guid.NewGuid(), SchedulerNodeId = node.Id,
                TargetNodeId = "kla", CoordinatedCascadeNodeId = "cascade", SlotIndex = 0,
                Schedule = RecipeAutonomousBlockConfiguration.ReadPeriodic(node).Schedule }, TimeSpan.FromSeconds(.5),
                record => { records?.Add(record); return Task.CompletedTask; })).ToArray());
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }

    [Theory]
    [InlineData(KlaRecipeTerminalStatus.Inconclusive, true)]
    [InlineData(KlaRecipeTerminalStatus.Inconclusive, false)]
    [InlineData(KlaRecipeTerminalStatus.RestorationFailure, true)]
    [InlineData(KlaRecipeTerminalStatus.PersistenceFailure, true)]
    [InlineData(KlaRecipeTerminalStatus.OperationalFailure, true)]
    public async Task Single_assay_applies_failure_policy_only_after_result_and_restoration(KlaRecipeTerminalStatus status, bool allowContinue)
    {
        var recipe = Recipe(false);
        if (!allowContinue) recipe.Node("kla")!.Set("failurePolicy", nameof(KlaRecipeFailurePolicy.StopAfterRestoration));
        var clock = new TestClock(DateTimeOffset.UnixEpoch); var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, clock);
        var source = new Source { Build = (document, id) => Plan(document, id, (_, _) =>
            Task.FromResult(Result(document, id, null, status) with {
                PreAssayStateRestored = status != KlaRecipeTerminalStatus.RestorationFailure,
                PersistenceConfirmed = status != KlaRecipeTerminalStatus.PersistenceFailure })) };
        using var engine = new RecipeEngine(arbiter, arbiter, new MemorySettingsService(), clock, autonomousWorkSource: source);
        await engine.StartAsync(recipe); await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(status == KlaRecipeTerminalStatus.Inconclusive && allowContinue ? RecipeRunState.Completed : RecipeRunState.Failed, engine.State);
        Assert.Single(engine.AutonomousResults);
        Assert.Equal(engine.State == RecipeRunState.Completed, engine.WasTraversed(recipe.Connections.Last()));
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("physical")]
    [InlineData("missing")]
    [InlineData("schedule")]
    public async Task Invalid_resolved_plan_is_rejected_before_any_device_command(string mismatch)
    {
        var recipe = Recipe(true); var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var device = new RecordingDeviceService(); using var arbiter = new CommandArbiter(device, clock);
        var source = new Source { Build = (document, id) =>
        {
            var plan = Plan(document, id, (invocation, _) => Task.FromResult(Result(document, id, invocation)));
            return mismatch switch
            {
                "profile" => plan with { Assays = [plan.Assays[0] with { Capabilities = Capability with { ProfileVersion = "wrong" } }] },
                "physical" => plan with { Assays = [plan.Assays[0] with { Capabilities = Capability with { IsIsolatedSimulation = false } }] },
                "missing" => plan with { Assays = [] },
                _ => plan with { Schedules = [plan.Schedules[0] with { Identity = plan.Schedules[0].Identity with { TargetNodeId = "wrong" } }] }
            };
        } };
        using var engine = new RecipeEngine(arbiter, arbiter, new MemorySettingsService(), clock, autonomousWorkSource: source);
        await Assert.ThrowsAnyAsync<Exception>(() => engine.StartAsync(recipe)); Assert.Empty(device.Sent);
    }

    [Fact]
    public async Task Async_application_disposal_awaits_in_flight_assay_recovery_before_releasing_dependencies()
    {
        var recipe = Recipe(false); var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var device = new RecordingDeviceService(); using var arbiter = new CommandArbiter(device, clock);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source { Build = (document, id) => Plan(document, id, async (_, ct) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }
            recovering.SetResult();
            await recovered.Task;
            return Result(document, id, null, KlaRecipeTerminalStatus.Cancelled);
        }) };
        var engine = new RecipeEngine(arbiter, arbiter, new MemorySettingsService(), clock, autonomousWorkSource: source);
        await engine.StartAsync(recipe);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var disposal = engine.DisposeAsync().AsTask();
        await recovering.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(disposal.IsCompleted);
        Assert.False(engine.Completion.IsCompleted);
        recovered.SetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(engine.Completion.IsCompleted);
        Assert.Single(engine.AutonomousResults);
        Assert.Equal(RecipeRunState.Stopped, engine.State);
        engine.Dispose();
    }

    [Fact]
    public async Task Periodic_branch_runs_at_two_six_ten_and_does_not_execute_target_after_exit()
    {
        var recipe = Recipe(true); var clock = new TestClock(DateTimeOffset.UnixEpoch, virtualTimers: true);
        var device = new RecordingDeviceService(); using var arbiter = new CommandArbiter(device, clock);
        var records = new List<RecipePeriodicSlotRecord>(); var invocations = new List<long>();
        var source = new Source { Build = (document, id) => Plan(document, id, (invocation, _) =>
        { invocations.Add(invocation!.SlotIndex); return Task.FromResult(Result(document, id, invocation)); }, records) };
        using var engine = new RecipeEngine(arbiter, arbiter, new MemorySettingsService(), clock, autonomousWorkSource: source);
        await engine.StartAsync(recipe); device.PushTelemetry(new SensorSnapshot { OxygenCalibrated = 25, OxygenRaw = 25 });
        await Until(() => engine.CascadeTermsFor("cascade") is not null && clock.PendingTimers > 0);
        for (var index = 0; index < 3; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(index == 0 ? 2 : 4));
            await Until(() => records.Count(r => r.State == RecipePeriodicSlotState.Completed) == index + 1);
        }
        recipe.Node("gate")!.Set("operacao", nameof(ManualGateOperation.Pass));
        device.PushTelemetry(new SensorSnapshot { OxygenCalibrated = 25, OxygenRaw = 25 });
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Completed, engine.State); Assert.Equal(new long[] { 0, 1, 2 }, invocations);
        Assert.Equal(3, engine.AutonomousResults.Count);
        Assert.True(engine.WasTraversed(recipe.Connections.Single(c => c.SourceNodeId == "periodic")));
    }

    [Fact]
    public async Task Result_from_another_invocation_cannot_advance_the_graph()
    {
        var recipe = Recipe(false); var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var device = new RecordingDeviceService(); using var arbiter = new CommandArbiter(device, clock);
        var source = new Source { Build = (document, id) => Plan(document, id, (_, _) =>
            Task.FromResult(Result(document, Guid.NewGuid(), null))) };
        using var engine = new RecipeEngine(arbiter, arbiter, new MemorySettingsService(), clock, autonomousWorkSource: source);
        await engine.StartAsync(recipe); await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Failed, engine.State); Assert.Empty(engine.AutonomousResults);
        Assert.False(engine.WasTraversed(recipe.Connections.Last()));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Agenda_measures_from_its_own_entry_and_skips_slots_before_cascade_entry(bool delayAgenda)
    {
        var recipe = Recipe(true); var clock = new TestClock(DateTimeOffset.UnixEpoch, virtualTimers: true);
        var device = new RecordingDeviceService(); using var arbiter = new CommandArbiter(device, clock);
        var delayNode = RecipeNode.Create(NodeType.Timer, id: "delay"); delayNode.Set("duracao", 3);
        recipe.Nodes.Add(delayNode);
        var delayed = delayAgenda ? "periodic" : "cascade";
        recipe.Connections.RemoveAll(c => c.SourceNodeId == "start" && c.TargetNodeId == delayed);
        recipe.Connections.AddRange([new("start", ConnectorNames.Out, "delay", ConnectorNames.In),
            new("delay", ConnectorNames.Out, delayed, ConnectorNames.In)]);
        var records = new List<RecipePeriodicSlotRecord>();
        var source = new Source { Build = (document, id) => Plan(document, id,
            (invocation, _) => Task.FromResult(Result(document, id, invocation)), records) };
        using var engine = new RecipeEngine(arbiter, arbiter, new MemorySettingsService(), clock,
            delay: (duration, ct) => Task.Delay(duration, clock, ct), autonomousWorkSource: source);
        await engine.StartAsync(recipe); device.PushTelemetry(new SensorSnapshot { OxygenCalibrated = 25, OxygenRaw = 25 });
        await Until(() => clock.PendingTimers >= (delayAgenda ? 1 : 2));
        if (!delayAgenda)
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            await Until(() => records.Count == 1);
            Assert.Equal(RecipePeriodicSlotState.Skipped, records[0].State);
            Assert.Contains("ainda não está ativa", records[0].Reason);
            Assert.Empty(engine.AutonomousResults);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        else clock.Advance(TimeSpan.FromSeconds(3));
        await Until(() => engine.NodeStateOf("periodic") == NodeState.Evaluating && engine.CascadeTermsFor("cascade") is not null);
        clock.Advance(TimeSpan.FromSeconds(delayAgenda ? 1 : 2));
        await Task.Delay(10);
        Assert.Empty(engine.AutonomousResults);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Until(() => engine.AutonomousResults.Count == 1);
        Assert.Equal(delayAgenda ? 2 : 6, records.Single(r => r.State == RecipePeriodicSlotState.Started).ElapsedSeconds);
        recipe.Node("gate")!.Set("operacao", nameof(ManualGateOperation.Pass));
        device.PushTelemetry(new SensorSnapshot { OxygenCalibrated = 25, OxygenRaw = 25 });
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Completed, engine.State);
    }

    [Fact]
    public async Task Sibling_failure_before_cascade_entry_stops_and_awaits_the_agenda()
    {
        var recipe = Recipe(true); var clock = new TestClock(DateTimeOffset.UnixEpoch, virtualTimers: true);
        var device = new RecordingDeviceService(); using var arbiter = new CommandArbiter(device, clock);
        recipe.Nodes.Add(RecipeNode.Create(NodeType.Timer, id: "failure"));
        recipe.Connections.RemoveAll(c => c.SourceNodeId == "start" && c.TargetNodeId == "cascade");
        recipe.Connections.AddRange([new("start", ConnectorNames.Out, "failure", ConnectorNames.In),
            new("failure", ConnectorNames.Out, "cascade", ConnectorNames.In)]);
        var source = new Source { Build = (document, id) => Plan(document, id,
            (invocation, _) => Task.FromResult(Result(document, id, invocation))) };
        using var engine = new RecipeEngine(arbiter, arbiter, new MemorySettingsService(), clock,
            delay: (_, _) => Task.FromException(new System.IO.IOException("sibling failure")), autonomousWorkSource: source);
        await engine.StartAsync(recipe); await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Failed, engine.State); Assert.Contains("sibling failure", engine.StatusReason);
        Assert.Empty(engine.AutonomousResults); Assert.Equal(0, clock.PendingTimers);
    }
}
