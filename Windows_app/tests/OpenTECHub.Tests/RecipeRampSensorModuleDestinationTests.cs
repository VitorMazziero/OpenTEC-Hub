using System.Collections.Immutable;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipeRampSensorModuleDestinationTests
{
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
    private static SensorSnapshot Feedback() => new()
    {
        SensorCommOk = true, SensorCommUpdated = true, PHUpdated = true, PHCalibrated = 6.8, PHSetpoint = 6.8,
        PHError = .15, PHCommandPending = false, PHControlActive = true,
        Pressure = 100, PressureUpdated = true, PressureReference = 100, PressureCommandPending = false, PressureControlActive = true
    };

    [Theory]
    [InlineData(SetpointVariable.Ph, 6.8)]
    [InlineData(SetpointVariable.Pressure, 100)]
    public async Task PositiveTargetRequiresCurrentReferenceActiveControlAndStableMeasuredProcess(SetpointVariable variable, double reference)
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch, virtualTimers: true);
        var device = new RecordingDeviceService(); using var arbiter = new CommandArbiter(device, clock);
        await using var engine = new RecipeEngine(arbiter, arbiter, new MemorySettingsService(), clock);
        await engine.StartAsync(WaitingRecipe());
        using var destination = new RecipeRampSensorModuleDestination(engine, variable,
            new(.1, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(20)), variable == SetpointVariable.Ph ? .15 : null);
        ImmutableArray<LinearRampSample> target = [new(variable, null, reference, true)];
        device.PushTelemetry(Feedback());
        Assert.True(await destination.TryApplyAsync(target, default));
        if (variable == SetpointVariable.Ph)
            Assert.Equal(.15, RecipeAssayReturnState.Number(OpenTECCommand.Parse(device.Sent.Last()), CommandKeys.PHError));
        var confirming = destination.TryConfirmFinalAsync(target, default);
        Assert.False(confirming.IsCompleted);
        foreach (var invalid in new[] { Feedback() with { SensorCommUpdated = false }, Feedback() with { SensorCommOk = false },
            Feedback() with { PHCommandPending = true, PressureCommandPending = true },
            Feedback() with { PHSetpoint = null, PressureReference = null }, Feedback() with { PHUpdated = false, PressureUpdated = false },
            Feedback() with { PHCalibrated = 8, Pressure = 200 }, Feedback() with { PHControlActive = false, PressureControlActive = false } })
        {
            device.PushTelemetry(invalid); await Task.Delay(10); Assert.False(confirming.IsCompleted);
        }
        if (variable == SetpointVariable.Ph)
        {
            device.PushTelemetry(Feedback() with { PHError = 0 }); await Task.Delay(10); Assert.False(confirming.IsCompleted);
        }
        device.PushTelemetry(Feedback()); await Task.Delay(10);
        clock.Advance(TimeSpan.FromSeconds(2)); device.PushTelemetry(Feedback());
        Assert.True(await confirming.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(RecipeRampConfirmationEvidence.ProcessFeedback, Assert.Single(destination.FinalConfirmations).Evidence);
        await engine.StopAsync("module confirmation verified");
    }

    [Theory]
    [InlineData(SetpointVariable.Ph)]
    [InlineData(SetpointVariable.Pressure)]
    public async Task OffRequiresExactReferenceAndReportedInactiveControl(SetpointVariable variable)
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch, virtualTimers: true);
        var device = new RecordingDeviceService(); using var arbiter = new CommandArbiter(device, clock);
        await using var engine = new RecipeEngine(arbiter, arbiter, new MemorySettingsService(), clock);
        await engine.StartAsync(WaitingRecipe());
        using var destination = new RecipeRampSensorModuleDestination(engine, variable,
            new(.1, TimeSpan.Zero, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10)), variable == SetpointVariable.Ph ? .15 : null);
        ImmutableArray<LinearRampSample> target = [new(variable, null, 0, true)];
        Assert.True(await destination.TryApplyAsync(target, default));
        var confirming = destination.TryConfirmFinalAsync(target, default);
        device.PushTelemetry(Feedback() with { PHSetpoint = .001, PressureReference = .001, PHControlActive = false, PressureControlActive = false });
        await Task.Delay(10); Assert.False(confirming.IsCompleted);
        device.PushTelemetry(Feedback() with { PHSetpoint = 0, PressureReference = 0 });
        await Task.Delay(10); Assert.False(confirming.IsCompleted);
        device.PushTelemetry(Feedback() with { PHSetpoint = 0, PressureReference = 0, PHControlActive = false, PressureControlActive = false });
        Assert.True(await confirming.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(RecipeRampConfirmationEvidence.DeviceReferenceReadback, Assert.Single(destination.FinalConfirmations).Evidence);
        await engine.StopAsync("module stop echo verified");
    }

    [Fact]
    public async Task CompleteFramePreservesPhBandAndConfirmsBothModuleParameters()
    {
        var device = new RecordingDeviceService(); using var arbiter = new CommandArbiter(device, TimeProvider.System);
        await using var engine = new RecipeEngine(arbiter, arbiter, new MemorySettingsService(), TimeProvider.System);
        await engine.StartAsync(WaitingRecipe());
        var configuration = new RecipeRampBlockConfiguration(new() { Lines = [
            new() { Variable = SetpointVariable.Ph, FinalSetpoint = 6.8, EndAfterSeconds = 60 },
            new() { Variable = SetpointVariable.Pressure, FinalSetpoint = 100, EndAfterSeconds = 90 }] }, null);
        var policy = new RecipeRampMeasuredConfirmationPolicy(.1, TimeSpan.Zero, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10));
        var motor = new RecipeRampMotorConfirmationPolicy(2, policy.Stability, policy.MaximumSampleGap, policy.Timeout);
        var temperature = new RecipeRampTemperatureConfirmationPolicy(.5, policy.Stability, policy.MaximumSampleGap, policy.Timeout);
        var flow = new RecipeRampFlowConfirmationPolicy(.1, policy.Stability, policy.MaximumSampleGap, policy.Timeout);
        Assert.Throws<ArgumentException>(() => new RecipeRampFrameDestination(engine, configuration, motor, temperature, flow, policy, policy));
        using var destination = new RecipeRampFrameDestination(engine, configuration, motor, temperature, flow, policy, policy, .15);
        ImmutableArray<LinearRampSample> target = [new(SetpointVariable.Ph, null, 6.8, true), new(SetpointVariable.Pressure, null, 100, true)];
        var count = device.Sent.Count;
        Assert.True(await destination.TryApplyAsync(target, default));
        Assert.Equal(count + 1, device.Sent.Count);
        Assert.Equal(.15, RecipeAssayReturnState.Number(OpenTECCommand.Parse(device.Sent.Last()), CommandKeys.PHError));
        var confirming = destination.TryConfirmFinalAsync(target, default);
        device.PushTelemetry(Feedback());
        Assert.True(await confirming.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(2, destination.FinalConfirmations.Length);
        await engine.StopAsync("mixed module frame verified");
    }
}
