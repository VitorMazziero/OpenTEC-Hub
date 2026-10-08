using System.Collections.Immutable;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipeRampFlowDestinationTests
{
    private static SensorSnapshot Feedback(double target, double measured) => new()
    {
        FlowRate = measured, FlowSetpoint = target, FlowmeterOnline = true,
        FlowRateUpdated = true, FlowFeedbackUpdated = true, FlowCommandId = 7, FlowCommandAck = 7,
        FlowValve1 = 0, FlowValve2 = target > 0 ? 1 : 0
    };

    [Theory]
    [InlineData(2)]
    [InlineData(0)]
    public async Task RequiresAcknowledgedRouteAndFreshStableMeasuredFlow(double value)
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch, virtualTimers: true);
        var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, clock);
        await using var engine = new RecipeEngine(arbiter, arbiter, new MemorySettingsService(new AppSettings()), clock);
        var document = new RecipeDocument();
        var start = RecipeNode.Create(NodeType.Start, id: "start");
        var timer = RecipeNode.Create(NodeType.Timer, id: "timer");
        timer.Set("duracao", 600); timer.Set("unidade", nameof(TimeUnit.Seconds));
        document.Nodes.AddRange([start, timer, RecipeNode.Create(NodeType.End, id: "end")]);
        document.Connections.Add(new("start", ConnectorNames.Out, "timer", ConnectorNames.In));
        document.Connections.Add(new("timer", ConnectorNames.Out, "end", ConnectorNames.In));
        await engine.StartAsync(document);
        using var destination = new RecipeRampFlowDestination(engine,
            new(0.1, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(20)));
        ImmutableArray<LinearRampSample> target = [new(SetpointVariable.Flow, null, value, true)];
        device.PushTelemetry(Feedback(value, value));
        Assert.True(await destination.TryApplyAsync(target, default));
        var confirming = destination.TryConfirmFinalAsync(target, default);
        Assert.False(confirming.IsCompleted);
        foreach (var invalid in new[] { Feedback(value, value + 1), Feedback(value, value) with { FlowRateUpdated = false },
            Feedback(value, value) with { FlowFeedbackUpdated = false }, Feedback(value, value) with { FlowmeterOnline = false },
            Feedback(value, value) with { FlowCommandPending = true }, Feedback(value, value) with { FlowCommandAck = 6 },
            Feedback(value, value) with { FlowValve1 = 1 }, Feedback(value, value) with { FlowSetpoint = value + 0.2 } })
        {
            device.PushTelemetry(invalid); await Task.Delay(10);
            Assert.False(confirming.IsCompleted);
        }
        device.PushTelemetry(Feedback(value, value + 0.05)); await Task.Delay(10);
        clock.Advance(TimeSpan.FromSeconds(2));
        device.PushTelemetry(Feedback(value, value + 0.02));
        Assert.True(await confirming.WaitAsync(TimeSpan.FromSeconds(2)));
        var receipt = Assert.Single(destination.FinalConfirmations);
        Assert.Equal(value + 0.02, receipt.ObservedValue);
        Assert.Equal(RecipeRampConfirmationEvidence.ProcessFeedback, receipt.Evidence);
        await engine.StopAsync("flow confirmation verified");
    }
}
