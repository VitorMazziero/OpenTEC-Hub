using System.Collections.Immutable;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipeRampFrameDestinationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DirectReturnRestoresCapturedSettingsAndConfirmsPreviousReferences(bool paused, bool revoke)
    {
        var time = new TestClock(DateTimeOffset.UnixEpoch, virtualTimers: true);
        var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, time);
        await using var engine = new RecipeEngine(arbiter, arbiter, new MemorySettingsService(new AppSettings()), time);
        await engine.StartAsync(WaitingRecipe());
        var configuration = Configuration() with { Definition = Configuration().Definition with {
            CancellationPolicy = RampCancellationPolicy.RestoreSnapshot }, TemperatureRoute = RampTemperatureRoute.NativeModule };
        var resources = RecipeRampInitialState.ResourcesFor(configuration.Definition);
        arbiter.Dispatch(CommandOwner.Recipe, OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, 25));
        arbiter.Dispatch(CommandOwner.Recipe, CommandBuilders.MotorSetpoint(200));
        arbiter.Dispatch(CommandOwner.Recipe, CommandBuilders.FlowRoute(1, 10, GasRoute.Reactor, GasRigConfiguration.Default)
            .Set(CommandKeys.FlowKp, .3));
        var authority = await arbiter.ReserveAsync(CommandOwner.Recipe, engine.ExecutionId, "ramp", resources, TimeSpan.FromSeconds(5));
        await arbiter.DrainReservedCommandsAsync(authority);
        var start = engine.CaptureRampStartCheckpoint(configuration, authority, Guid.NewGuid());
        arbiter.ReleaseReservation(authority);
        using var destination = Destination(engine);
        Assert.True(await destination.TryApplyAsync(Targets, default));
        var restoring = await arbiter.ReserveAsync(CommandOwner.Recipe, engine.ExecutionId, "ramp", resources, TimeSpan.FromSeconds(5));
        await arbiter.DrainReservedCommandsAsync(restoring);
        if (paused) engine.Pause();
        var before = device.Sent.Count;
        Assert.True(await destination.TryRestoreDirectAsync(start, restoring, default));
        Assert.Equal(before + 1, device.Sent.Count);
        var restoredCommand = OpenTECCommand.Parse(device.Sent.Last());
        Assert.Equal("0.3", restoredCommand.GetRawValue(CommandKeys.FlowKp));
        var frame = RecipeRampRestoreCommands.Build(start, restoring);
        if (revoke)
        {
            await arbiter.DrainReservedCommandsAsync(restoring);
            arbiter.ReleaseReservation(restoring);
            await Assert.ThrowsAsync<InvalidOperationException>(() => destination.TryConfirmFinalAsync(frame.References, default));
            Assert.Empty(destination.FinalConfirmations);
            await engine.StopAsync("revoked restoration rejected");
            return;
        }
        var confirming = destination.TryConfirmFinalAsync(frame.References, default);
        Assert.False(confirming.IsCompleted);
        device.PushTelemetry(Feedback(25, 200) with { TempSetpoint = 25, FlowRate = 1, FlowSetpoint = 1 });
        Assert.True(await confirming.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(new[] { 25d, 200d, 1d }, destination.FinalConfirmations.Select(proof => proof.Reference));
        await arbiter.DrainReservedCommandsAsync(restoring);
        arbiter.ReleaseReservation(restoring);
        await engine.StopAsync("direct restoration verified");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedDrainageStopsProducerAndKeepsReservationClosed(bool failAfterDispatch)
    {
        var time = new TestClock(DateTimeOffset.UnixEpoch, virtualTimers: true);
        var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, time);
        await using var engine = new RecipeEngine(arbiter, arbiter, new MemorySettingsService(new AppSettings()), time);
        await engine.StartAsync(WaitingRecipe());
        using var destination = Destination(engine);
        var configuration = Configuration();
        var clock = new RecipeRampActiveClock(time);
        using var producer = new RecipeRampResourceProducer("ramp", RecipeRampInitialState.ResourcesFor(configuration.Definition), clock);
        var guarded = new RecipeRampGuardedDestination(producer, clock, destination, time, TimeSpan.FromSeconds(10), arbiter, engine.ExecutionId);
        var trajectory = new LinearSetpointRampTrajectory(configuration.Definition,
            new Dictionary<SetpointVariable, double> { [SetpointVariable.Temperature] = 25,
                [SetpointVariable.Agitation] = 200, [SetpointVariable.Flow] = 1 }, (_, value) => value);
        var count = device.Sent.Count;
        if (failAfterDispatch) device.CommandSent += _ => device.RaiseCommandSentOnSend = false;
        else device.RaiseCommandSentOnSend = false;
        await Assert.ThrowsAsync<System.IO.IOException>(() => guarded.TryApplyTrajectoryAsync(trajectory, clock, [], default));
        Assert.Equal(count + (failAfterDispatch ? 1 : 0), device.Sent.Count);
        Assert.True(producer.StopToken.IsCancellationRequested);
        Assert.True(clock.IsSuspended);
        Assert.False(arbiter.Dispatch(CommandOwner.Recipe, OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, 25)).Accepted);
        device.RaiseCommandSentOnSend = true;
        await engine.StopAsync("unresolved drainage verified");
    }

    [Fact]
    public async Task ReservedFrameUsesMatchingAuthorityAndReleasesItBeforeAssaySuspensionCompletes()
    {
        var time = new TestClock(DateTimeOffset.UnixEpoch, virtualTimers: true);
        var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, time);
        await using var engine = new RecipeEngine(arbiter, arbiter, new MemorySettingsService(new AppSettings()), time);
        await engine.StartAsync(WaitingRecipe());
        var configuration = Configuration();
        var resources = RecipeRampInitialState.ResourcesFor(configuration.Definition);
        var blocking = await arbiter.ReserveAsync(CommandOwner.Recipe, engine.ExecutionId, "other", resources, TimeSpan.FromSeconds(10));
        await arbiter.DrainReservedCommandsAsync(blocking);
        using var destination = Destination(engine);
        Assert.False(await destination.TryApplyAsync(Targets, default));
        var clock = new RecipeRampActiveClock(time);
        using var producer = new RecipeRampResourceProducer("ramp", resources, clock);
        var guarded = new RecipeRampGuardedDestination(producer, clock, destination, time, TimeSpan.FromSeconds(10),
            arbiter, engine.ExecutionId, TimeSpan.FromSeconds(10));
        var trajectory = new LinearSetpointRampTrajectory(configuration.Definition,
            new Dictionary<SetpointVariable, double> { [SetpointVariable.Temperature] = 25,
                [SetpointVariable.Agitation] = 200, [SetpointVariable.Flow] = 1 }, (_, value) => value);
        var applying = guarded.TryApplyTrajectoryAsync(trajectory, clock, [], default);
        Assert.False(applying.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(0, clock.ActiveSeconds);
        arbiter.ReleaseReservation(blocking);
        var frame = await applying.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new[] { 25d, 200d, 1d }, frame.Select(sample => sample.Reference));
        var suspended = await producer.SuspendAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
        var next = await arbiter.ReserveAsync(CommandOwner.Recipe, engine.ExecutionId, "assay", resources, TimeSpan.FromSeconds(1));
        await arbiter.DrainReservedCommandsAsync(next);
        arbiter.ReleaseReservation(next);
        suspended.Resume();
        await engine.StopAsync("reserved frame verified");
    }

    [Fact]
    public async Task ReservedFrameRejectsAuthorityFromAnotherExecutionWithoutDispatch()
    {
        var time = new TestClock(DateTimeOffset.UnixEpoch, virtualTimers: true);
        var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, time);
        await using var engine = new RecipeEngine(arbiter, arbiter, new MemorySettingsService(new AppSettings()), time);
        await engine.StartAsync(WaitingRecipe());
        using var destination = Destination(engine);
        var authority = await arbiter.ReserveAsync(CommandOwner.Recipe, Guid.NewGuid(), "wrong",
            RecipeRampInitialState.ResourcesFor(Configuration().Definition), TimeSpan.FromSeconds(2));
        await arbiter.DrainReservedCommandsAsync(authority);
        var count = device.Sent.Count;
        Assert.False(await destination.TryApplyReservedAsync(Targets, authority, default));
        Assert.Equal(count, device.Sent.Count);
        arbiter.ReleaseReservation(authority);
        await engine.StopAsync("foreign reservation rejected");
    }

    [Fact]
    public async Task FasterComponentsRemainValidWhileASeparateStabilityWindowFinishes()
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch, virtualTimers: true);
        var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, clock);
        await using var engine = new RecipeEngine(arbiter, arbiter, new MemorySettingsService(new AppSettings()), clock);
        await engine.StartAsync(WaitingRecipe());
        using var destination = new RecipeRampFrameDestination(engine, Configuration(),
            new(2, TimeSpan.Zero, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15)),
            new(.5, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15)),
            new(.1, TimeSpan.Zero, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15)));
        Assert.True(await destination.TryApplyAsync(Targets, default));
        var confirming = destination.TryConfirmFinalAsync(Targets, default);
        device.PushTelemetry(Feedback()); await Task.Delay(30);
        Assert.False(confirming.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(2));
        device.PushTelemetry(Feedback(30, 301));
        Assert.True(await confirming.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(301, destination.FinalConfirmations.Single(receipt => receipt.Variable == SetpointVariable.Agitation).ObservedValue);
        Assert.All(destination.FinalConfirmations, receipt => Assert.Equal(clock.GetUtcNow(), receipt.RecordedUtc));
        await engine.StopAsync("independent windows verified");
    }
    private static RecipeDocument WaitingRecipe()
    {
        var recipe = new RecipeDocument();
        var timer = RecipeNode.Create(NodeType.Timer, id: "timer");
        timer.Set("duracao", 600); timer.Set("unidade", nameof(TimeUnit.Seconds));
        recipe.Nodes.AddRange([RecipeNode.Create(NodeType.Start, id: "start"), timer, RecipeNode.Create(NodeType.End, id: "end")]);
        recipe.Connections.Add(new("start", ConnectorNames.Out, "timer", ConnectorNames.In));
        recipe.Connections.Add(new("timer", ConnectorNames.Out, "end", ConnectorNames.In));
        return recipe;
    }

    private static RecipeRampBlockConfiguration Configuration() => new(new() { Lines = [
        new() { Variable = SetpointVariable.Temperature, FinalSetpoint = 30, EndAfterSeconds = 60 },
        new() { Variable = SetpointVariable.Agitation, FinalSetpoint = 300, EndAfterSeconds = 90 },
        new() { Variable = SetpointVariable.Flow, FinalSetpoint = 2, EndAfterSeconds = 120 }] }, null);
    private static ImmutableArray<LinearRampSample> Targets => [new(SetpointVariable.Temperature, null, 30, true),
        new(SetpointVariable.Agitation, null, 300, true), new(SetpointVariable.Flow, null, 2, true)];
    private static RecipeRampFrameDestination Destination(RecipeEngine engine) => new(engine, Configuration(),
        new(2, TimeSpan.Zero, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15)),
        new(.5, TimeSpan.Zero, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15)),
        new(.1, TimeSpan.Zero, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15)));
    private static SensorSnapshot Feedback(double temperature = 30, double rpm = 300) => new()
    {
        Temperature = temperature, TemperatureUpdated = true, TemperatureValid = true, TemperatureAgeMs = 0,
        SensorCommOk = true, TempSetpoint = 30, TempSetpointCommanded = true, TempControlViaBath = false,
        HasServoTelemetry = true, HasServoSample = true, ServoOnline = true, ServoCommEnabled = true,
        ServoCommandPending = false, ServoMotorRouteAck = 1, MotorControlViaModbus = true, ServoRpm = rpm,
        FlowRate = 2, FlowSetpoint = 2, FlowmeterOnline = true, FlowRateUpdated = true, FlowFeedbackUpdated = true,
        FlowCommandId = 7, FlowCommandAck = 7, FlowValve1 = 0, FlowValve2 = 1
    };

    [Fact]
    public async Task AppliesOneCompleteCommandAndNeverCombinesConfirmationsFromDifferentFrames()
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch, virtualTimers: true);
        var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, clock);
        await using var engine = new RecipeEngine(arbiter, arbiter, new MemorySettingsService(new AppSettings()), clock);
        await engine.StartAsync(WaitingRecipe());
        using var destination = Destination(engine);
        Assert.False(await destination.TryConfirmFinalAsync(Targets, default));
        var count = device.Sent.Count;
        Assert.True(await destination.TryApplyAsync(Targets, default));
        Assert.Equal(count + 1, device.Sent.Count);
        var command = OpenTECCommand.Parse(device.Sent.Last());
        Assert.True(command.Contains(CommandKeys.TempSetpoint)); Assert.True(command.Contains(CommandKeys.MotorSetpoint));
        Assert.True(command.Contains(CommandKeys.FlowSetpoint));
        var confirming = destination.TryConfirmFinalAsync(Targets, default);
        device.PushTelemetry(Feedback(25)); await Task.Delay(30);
        Assert.False(confirming.IsCompleted);
        device.PushTelemetry(Feedback(30, 900));
        Assert.False(await confirming.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Empty(destination.FinalConfirmations);
        confirming = destination.TryConfirmFinalAsync(Targets, default);
        device.PushTelemetry(Feedback());
        Assert.True(await confirming.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(3, destination.FinalConfirmations.Length);
        Assert.All(destination.FinalConfirmations, receipt => Assert.Equal(RecipeRampConfirmationEvidence.ProcessFeedback, receipt.Evidence));
        await engine.StopAsync("complete frame verified");
    }

    [Fact]
    public async Task InvalidWholeFrameNeverDispatchesItsValidPrefix()
    {
        var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, TimeProvider.System);
        await using var engine = new RecipeEngine(arbiter, arbiter, new MemorySettingsService(new AppSettings()), TimeProvider.System);
        await engine.StartAsync(WaitingRecipe());
        using var destination = Destination(engine);
        var count = device.Sent.Count;
        await Assert.ThrowsAsync<ArgumentException>(() => destination.TryApplyAsync(Targets.SetItem(1, Targets[1] with { Reference = 300.5 }), default));
        await Assert.ThrowsAsync<ArgumentException>(() => destination.TryApplyAsync(Targets.SetItem(2, Targets[2] with { Reference = 26 }), default));
        await Assert.ThrowsAsync<ArgumentException>(() => destination.TryApplyAsync([Targets[0]], default));
        Assert.Equal(count, device.Sent.Count); Assert.Empty(destination.FinalConfirmations);
        await engine.StopAsync("frame validation verified");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseOrCancellationCannotPublishPartialReceipts(bool cancel)
    {
        var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, TimeProvider.System);
        await using var engine = new RecipeEngine(arbiter, arbiter, new MemorySettingsService(new AppSettings()), TimeProvider.System);
        await engine.StartAsync(WaitingRecipe());
        using var destination = Destination(engine);
        using var cancellation = new CancellationTokenSource();
        Assert.True(await destination.TryApplyAsync(Targets, default));
        var confirming = destination.TryConfirmFinalAsync(Targets, cancellation.Token);
        device.PushTelemetry(Feedback(25)); await Task.Delay(30);
        if (cancel)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => confirming.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        else
        {
            engine.Pause();
            Assert.False(await confirming.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        Assert.Empty(destination.FinalConfirmations);
        await engine.StopAsync("frame interruption verified");
    }
}
