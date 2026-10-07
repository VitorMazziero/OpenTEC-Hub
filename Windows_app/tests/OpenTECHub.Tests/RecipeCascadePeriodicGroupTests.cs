using OpenTECHub.Protocol;
using System.IO;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipeCascadePeriodicGroupTests
{
    private sealed class Source(Func<RecipeResourceCoordinator, RecipePeriodicWork> create) : IRecipePeriodicWorkSource
    {
        public IReadOnlyList<RecipePeriodicWork> CreateWork(RecipeDocument recipe, Guid executionId, RecipeResourceCoordinator? resources)
            => [create(resources!)];
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Unreturned_assay_authority_is_explicitly_stopped_and_cannot_report_success(bool throwFailure)
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch, virtualTimers: true);
        var device = new RecordingDeviceService(); using var arbiter = new CommandArbiter(device, clock);
        arbiter.Dispatch(CommandOwner.Manual, CommandBuilders.MotorControlMode(true));
        arbiter.Dispatch(CommandOwner.Manual, CommandBuilders.FlowmeterLoopEnabled(true));
        arbiter.Dispatch(CommandOwner.Manual, CommandBuilders.FlowRoute(2, 10, GasRoute.Reactor, GasRigConfiguration.Default));
        arbiter.Dispatch(CommandOwner.Manual, OpenTECCommand.Create().Set(CommandKeys.OxygenMonitor, 30));
        RecipeEngine? engine = null; RecipeAssayResourceLease? failedLease = null;
        var source = new Source(resources => new(new() { ScheduleRunId = Guid.NewGuid(), SchedulerNodeId = "periodic", TargetNodeId = "kla",
            CoordinatedCascadeNodeId = "casc", SlotIndex = 0, Schedule = new() { InitialDelaySeconds = 2, PeriodSeconds = 10 } },
            TimeSpan.FromSeconds(1), async (_, ct) =>
            {
                var context = RecipeExecutionContractTests.Request().Context with { RecipeRunId = engine!.ExecutionId };
                failedLease = await resources.ReserveForAssayAsync(context,
                    [ActuatorId.Agitation, ActuatorId.Aeration, ActuatorId.Oxygen], TimeSpan.FromSeconds(5), ct);
                failedLease.BeginAssay(failedLease.CaptureReturnSnapshot(GasRigConfiguration.Default, clock));
                failedLease.Fail();
                if (throwFailure) throw new IOException("terminal receipt failure");
            }, _ => Task.CompletedTask));
        using var run = engine = new RecipeEngine(arbiter, arbiter, new MemorySettingsService(), clock, periodicWorkSource: source);
        var recipe = new RecipeDocument { Name = "unreturned lease" };
        recipe.Nodes.AddRange([RecipeNode.Create(NodeType.Start, id: "start"), RecipeNode.Create(NodeType.CascadeControl, id: "casc"),
            RecipeNode.Create(NodeType.ManualIntervention, id: "gate"), RecipeNode.Create(NodeType.End, id: "end")]);
        recipe.Connections.AddRange([new("start", ConnectorNames.Out, "casc", ConnectorNames.In),
            new("casc", ConnectorNames.LoopOut, "gate", ConnectorNames.In), new("gate", ConnectorNames.Out, "casc", ConnectorNames.LoopIn),
            new("casc", ConnectorNames.Out, "end", ConnectorNames.In)]);
        await engine.StartAsync(recipe); device.PushTelemetry(new SensorSnapshot { OxygenCalibrated = 25, OxygenRaw = 25 });
        await Until(() => engine.CascadeTermsFor("casc") is not null); clock.Advance(TimeSpan.FromSeconds(2));
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Failed, engine.State); Assert.NotNull(failedLease);
        Assert.False(failedLease.DispatchAssay(CommandBuilders.MotorSetpoint(300)).Accepted);
        Assert.Equal(CommandOwner.Manual, arbiter.OwnerOf(ActuatorId.Agitation));
        Assert.Equal(CommandOwner.Manual, arbiter.OwnerOf(ActuatorId.Aeration));
        var stop = OpenTECCommand.Parse(device.Sent.Last());
        Assert.Equal("0", stop.GetRawValue(CommandKeys.MotorSetpoint));
        Assert.Equal(0, double.Parse(stop.GetRawValue(CommandKeys.FlowSetpoint)!, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Journal_failure_stops_real_cascade_even_while_recipe_is_paused(bool paused)
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch, virtualTimers: true);
        var device = new RecordingDeviceService(); using var arbiter = new CommandArbiter(device, clock);
        var dispatched = 0;
        var source = new Source(_ => new(new() { ScheduleRunId = Guid.NewGuid(), SchedulerNodeId = "periodic",
            TargetNodeId = "kla", CoordinatedCascadeNodeId = "casc", SlotIndex = 0,
            Schedule = new() { InitialDelaySeconds = 2, PeriodSeconds = 10 } }, TimeSpan.FromSeconds(1),
            (_, _) => { dispatched++; return Task.CompletedTask; }, _ => Task.FromException(new IOException("journal unavailable"))));
        using var engine = new RecipeEngine(arbiter, arbiter, new MemorySettingsService(), clock, periodicWorkSource: source);
        var recipe = new RecipeDocument { Name = "cascade journal fault" };
        recipe.Nodes.AddRange([RecipeNode.Create(NodeType.Start, id: "start"),
            RecipeNode.Create(NodeType.CascadeControl, id: "casc"),
            RecipeNode.Create(NodeType.ManualIntervention, id: "gate"), RecipeNode.Create(NodeType.End, id: "end")]);
        recipe.Connections.AddRange([new("start", ConnectorNames.Out, "casc", ConnectorNames.In),
            new("casc", ConnectorNames.LoopOut, "gate", ConnectorNames.In),
            new("gate", ConnectorNames.Out, "casc", ConnectorNames.LoopIn), new("casc", ConnectorNames.Out, "end", ConnectorNames.In)]);
        await engine.StartAsync(recipe); device.PushTelemetry(new SensorSnapshot { OxygenCalibrated = 25, OxygenRaw = 25 });
        await Until(() => engine.CascadeTermsFor("casc") is not null);
        if (paused) engine.Pause();
        clock.Advance(TimeSpan.FromSeconds(2));
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Failed, engine.State); Assert.Contains("journal unavailable", engine.StatusReason);
        Assert.Equal(0, dispatched); Assert.Null(engine.CascadeTermsFor("casc"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Engine_cascade_exit_or_pause_keeps_controller_until_follower_returns(bool pause)
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch, virtualTimers: true);
        var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, clock);
        var settings = new MemorySettingsService();
        var active = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RecipeEngine? engine = null;
        var source = new Source(resources => new(new() { ScheduleRunId = Guid.NewGuid(), SchedulerNodeId = "periodic",
            TargetNodeId = "kla", CoordinatedCascadeNodeId = "casc", SlotIndex = 0,
            Schedule = new() { InitialDelaySeconds = 2, PeriodSeconds = 10 } }, TimeSpan.FromSeconds(1),
            async (_, ct) =>
            {
                var context = RecipeExecutionContractTests.Request().Context with { RecipeRunId = engine!.ExecutionId };
                var lease = await resources.ReserveForAssayAsync(context, [ActuatorId.Agitation, ActuatorId.Aeration, ActuatorId.Oxygen], TimeSpan.FromSeconds(5), ct);
                var before = engine.CascadeTermsFor("casc"); active.SetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                finally
                {
                    recovering.SetResult(); await returned.Task;
                    Assert.NotNull(engine.CascadeTermsFor("casc")); Assert.Equal(before, engine.CascadeTermsFor("casc"));
                    lease.AbortBeforeAssay();
                }
            }, _ => Task.CompletedTask));
        using var run = engine = new RecipeEngine(arbiter, arbiter, settings, clock, periodicWorkSource: source);
        var recipe = new RecipeDocument { Name = "periodic cascade" };
        var start = RecipeNode.Create(NodeType.Start, id: "start");
        var cascade = RecipeNode.Create(NodeType.CascadeControl, id: "casc");
        var gate = RecipeNode.Create(NodeType.ManualIntervention, id: "gate");
        var end = RecipeNode.Create(NodeType.End, id: "end");
        recipe.Nodes.AddRange([start, cascade, gate, end]);
        recipe.Connections.AddRange([new("start", ConnectorNames.Out, "casc", ConnectorNames.In),
            new("casc", ConnectorNames.LoopOut, "gate", ConnectorNames.In),
            new("gate", ConnectorNames.Out, "casc", ConnectorNames.LoopIn),
            new("casc", ConnectorNames.Out, "end", ConnectorNames.In)]);
        await engine.StartAsync(recipe);
        device.PushTelemetry(new SensorSnapshot { OxygenCalibrated = 25, OxygenRaw = 25 });
        await Until(() => engine.CascadeTermsFor("casc") is not null);
        clock.Advance(TimeSpan.FromSeconds(2)); await active.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (pause) engine.Pause();
        else { gate.Set("operacao", nameof(ManualGateOperation.Pass)); device.PushTelemetry(new SensorSnapshot { OxygenCalibrated = 25 }); }
        await recovering.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(engine.Completion.IsCompleted); Assert.NotNull(engine.CascadeTermsFor("casc"));
        returned.SetResult();
        if (pause)
        {
            await Until(() => clock.PendingTimers > 0);
            Assert.Equal(RecipeRunState.Paused, engine.State);
            gate.Set("operacao", nameof(ManualGateOperation.Pass)); engine.Resume();
            device.PushTelemetry(new SensorSnapshot { OxygenCalibrated = 25 });
        }
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Completed, engine.State);
        Assert.Null(engine.CascadeTermsFor("casc"));
    }
}
