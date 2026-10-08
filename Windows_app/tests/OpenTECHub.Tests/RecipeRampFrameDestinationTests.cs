using System.Collections.Immutable;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipeRampFrameDestinationTests
{
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
