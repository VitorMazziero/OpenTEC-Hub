using System.Collections.Immutable;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipeRampMotorDestinationTests
{
    [Fact]
    public void ChangedMotorToleranceIsValidatedByTheSharedConfirmationPolicy()
    {
        var policy = new RecipeRampMotorConfirmationPolicy(2, TimeSpan.Zero, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
        Assert.Throws<ArgumentException>(() => (policy with { ToleranceRpm = -1 }).Validate());
        Assert.Equal(4, (policy with { ToleranceRpm = 4 }).Tolerance);
    }
    private static SensorSnapshot Motor(double rpm) => new()
    {
        HasServoTelemetry = true, HasServoSample = true, ServoOnline = true, ServoCommEnabled = true,
        ServoCommandPending = false, ServoMotorRouteAck = 1, MotorControlViaModbus = true, ServoRpm = rpm
    };

    private static RecipeDocument WaitingRecipe()
    {
        var document = new RecipeDocument();
        var start = RecipeNode.Create(NodeType.Start, id: "start");
        var timer = RecipeNode.Create(NodeType.Timer, id: "timer");
        timer.Set("duracao", 600); timer.Set("unidade", nameof(TimeUnit.Seconds));
        var end = RecipeNode.Create(NodeType.End, id: "end");
        document.Nodes.AddRange([start, timer, end]);
        document.Connections.Add(new("start", ConnectorNames.Out, "timer", ConnectorNames.In));
        document.Connections.Add(new("timer", ConnectorNames.Out, "end", ConnectorNames.In));
        return document;
    }

    [Fact]
    public async Task RequiresNewStableMotorSamplesAndPreservesMeasuredEvidence()
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch, virtualTimers: true);
        var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, clock);
        await using var engine = new RecipeEngine(arbiter, arbiter, new MemorySettingsService(new AppSettings()), clock);
        await engine.StartAsync(WaitingRecipe());
        using var destination = new RecipeRampMotorDestination(engine,
            new(2, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15)));
        ImmutableArray<LinearRampSample> target = [new(SetpointVariable.Agitation, null, 300, true)];
        device.PushTelemetry(Motor(300));
        Assert.False(await destination.TryConfirmFinalAsync(target, default));
        Assert.True(await destination.TryApplyAsync(target, default));
        var confirming = destination.TryConfirmFinalAsync(target, default);
        Assert.False(confirming.IsCompleted);
        // A partial publish cannot recycle the prior measured speed.
        device.PushTelemetry(Motor(300) with { HasServoSample = false });
        await Task.Delay(10);
        device.PushTelemetry(Motor(299));
        await Task.Delay(10);
        clock.Advance(TimeSpan.FromSeconds(1));
        device.PushTelemetry(Motor(300) with { ServoCommandPending = true });
        await Task.Delay(10);
        clock.Advance(TimeSpan.FromSeconds(1));
        device.PushTelemetry(Motor(299));
        await Task.Delay(10);
        Assert.False(confirming.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(2));
        device.PushTelemetry(Motor(301));
        Assert.True(await confirming.WaitAsync(TimeSpan.FromSeconds(2)));
        var confirmation = Assert.Single(destination.FinalConfirmations);
        Assert.Equal(RecipeRampConfirmationEvidence.ProcessFeedback, confirmation.Evidence);
        Assert.Equal(300, confirmation.Reference); Assert.Equal(301, confirmation.ObservedValue); Assert.Equal(2, confirmation.Tolerance);
        await engine.StopAsync("motor confirmation verified");
    }

    [Fact]
    public async Task MissingFeedbackTimesOutAndCancellationDoesNotInventAReceipt()
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch, virtualTimers: true);
        var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, clock);
        await using var engine = new RecipeEngine(arbiter, arbiter, new MemorySettingsService(new AppSettings()), clock);
        await engine.StartAsync(WaitingRecipe());
        using var destination = new RecipeRampMotorDestination(engine,
            new(2, TimeSpan.Zero, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5)));
        ImmutableArray<LinearRampSample> target = [new(SetpointVariable.Agitation, null, 300, true)];
        Assert.True(await destination.TryApplyAsync(target, default));
        var confirming = destination.TryConfirmFinalAsync(target, default);
        device.PushTelemetry(Motor(300) with { ServoOnline = false });
        await Task.Delay(10);
        clock.Advance(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<TimeoutException>(() => confirming);
        Assert.Empty(destination.FinalConfirmations);
        using var cancel = new CancellationTokenSource();
        var cancelled = destination.TryConfirmFinalAsync(target, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Empty(destination.FinalConfirmations);
        var paused = destination.TryConfirmFinalAsync(target, default);
        engine.Pause();
        Assert.False(await paused.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Empty(destination.FinalConfirmations);
        engine.Resume();
        var resumed = destination.TryConfirmFinalAsync(target, default);
        device.PushTelemetry(Motor(300));
        Assert.True(await resumed.WaitAsync(TimeSpan.FromSeconds(2)));
        await engine.StopAsync("motor timeout verified");
    }

    [Fact]
    public async Task SampleGapRestartsStabilityAndAnotherApplicationInvalidatesPendingConfirmation()
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch, virtualTimers: true);
        var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, clock);
        await using var engine = new RecipeEngine(arbiter, arbiter, new MemorySettingsService(new AppSettings()), clock);
        await engine.StartAsync(WaitingRecipe());
        using var destination = new RecipeRampMotorDestination(engine,
            new(2, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15)));
        ImmutableArray<LinearRampSample> target = [new(SetpointVariable.Agitation, null, 300, true)];
        Assert.True(await destination.TryApplyAsync(target, default));
        var confirming = destination.TryConfirmFinalAsync(target, default);
        device.PushTelemetry(Motor(300)); await Task.Delay(10);
        clock.Advance(TimeSpan.FromSeconds(5));
        device.PushTelemetry(Motor(300)); await Task.Delay(10);
        Assert.False(confirming.IsCompleted);
        Assert.True(await destination.TryApplyAsync(target, default));
        Assert.False(await confirming.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Empty(destination.FinalConfirmations);
        var current = destination.TryConfirmFinalAsync(target, default);
        device.PushTelemetry(Motor(299)); await Task.Delay(10);
        clock.Advance(TimeSpan.FromSeconds(2));
        device.PushTelemetry(Motor(300));
        Assert.True(await current.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(await destination.TryApplyAsync(target, default));
        var ownershipLost = destination.TryConfirmFinalAsync(target, default);
        arbiter.Claim(CommandOwner.Manual, [ActuatorId.Agitation], "manual-takeover");
        device.PushTelemetry(Motor(300));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ownershipLost);
        Assert.Empty(destination.FinalConfirmations);
        await engine.StopAsync("motor gap and ownership verified");
    }
}
