using System.Collections.Immutable;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipeRampTemperatureDestinationTests
{
    [Fact]
    public void ChangedTemperatureToleranceIsValidatedByTheSharedConfirmationPolicy()
    {
        var policy = new RecipeRampTemperatureConfirmationPolicy(0.5, TimeSpan.Zero, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
        Assert.Throws<ArgumentException>(() => (policy with { ToleranceC = -1 }).Validate());
        Assert.Equal(1, (policy with { ToleranceC = 1 }).Tolerance);
    }
    private static SensorSnapshot Temperature(bool bath, double reactor) => new()
    {
        HasBathTelemetry = true, TempControlViaBath = bath, BathOnline = true, BathCommEnabled = true,
        BathCommandPending = false, BathOwned = true, BathCascadeActive = true,
        TempSetpoint = 37, TempSetpointCommanded = true, TempModuleActuatorOn = true,
        Temperature = reactor, TemperatureUpdated = true, TemperatureValid = true, TemperatureAgeMs = 0,
        SensorCommOk = true, BathPv = 37, BathSp = 37, BathTarget = 37
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmsTheReactorThroughBothRoutesAndRejectsPartialStaleOrPendingFeedback(bool bath)
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch, virtualTimers: true);
        var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, clock);
        await using var engine = new RecipeEngine(arbiter, arbiter, new MemorySettingsService(new AppSettings()), clock);
        device.PushTelemetry(Temperature(bath, 37));
        await engine.StartAsync(WaitingRecipe());
        using var destination = new RecipeRampTemperatureDestination(engine,
            new(0.5, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(20)));
        ImmutableArray<LinearRampSample> target = [new(SetpointVariable.Temperature, null, 37, true)];
        Assert.True(await destination.TryApplyAsync(target, default));
        var confirming = destination.TryConfirmFinalAsync(target, default);
        Assert.False(confirming.IsCompleted);
        foreach (var invalid in new[] { Temperature(bath, 25), Temperature(bath, 37) with { TemperatureUpdated = false },
            Temperature(bath, 37) with { TemperatureAgeMs = 5000 }, Temperature(bath, 37) with { TemperatureValid = false },
            Temperature(bath, 37) with { TempSetpoint = 36 } })
        {
            device.PushTelemetry(invalid); await Task.Delay(10);
            Assert.False(confirming.IsCompleted);
        }
        if (bath)
        {
            device.PushTelemetry(Temperature(true, 37) with { BathCommandCompletionPending = true });
            await Task.Delay(10); Assert.False(confirming.IsCompleted);
        }
        device.PushTelemetry(Temperature(bath, 37.2)); await Task.Delay(10);
        clock.Advance(TimeSpan.FromSeconds(2));
        device.PushTelemetry(Temperature(bath, 37.1));
        Assert.True(await confirming.WaitAsync(TimeSpan.FromSeconds(2)));
        var receipt = Assert.Single(destination.FinalConfirmations);
        Assert.Equal(37.1, receipt.ObservedValue); Assert.Equal(0.5, receipt.Tolerance);
        Assert.Equal(RecipeRampConfirmationEvidence.ProcessFeedback, receipt.Evidence);
        await engine.StopAsync("temperature confirmation verified");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OffConfirmsDisabledActuationAndRouteChangeRefusesConfirmation(bool bath)
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch, virtualTimers: true);
        var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, clock);
        await using var engine = new RecipeEngine(arbiter, arbiter, new MemorySettingsService(new AppSettings()), clock);
        device.PushTelemetry(Temperature(bath, 25));
        await engine.StartAsync(WaitingRecipe());
        using var destination = new RecipeRampTemperatureDestination(engine,
            new(0.5, TimeSpan.Zero, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10)));
        ImmutableArray<LinearRampSample> off = [new(SetpointVariable.Temperature, null, 0, true)];
        Assert.True(await destination.TryApplyAsync(off, default));
        var stopped = destination.TryConfirmFinalAsync(off, default);
        var stop = Temperature(bath, 25) with { TempSetpoint = 0, TempSetpointCommanded = false, TempModuleActuatorOn = false,
            BathOwned = false, BathCascadeActive = false, BathCommandLastSentId = 7, BathCommandLastDoneId = 7 };
        device.PushTelemetry(stop with { TempSetpoint = 0.01 });
        await Task.Delay(10); Assert.False(stopped.IsCompleted);
        if (bath)
        {
            device.PushTelemetry(stop with { BathCommandLastDoneId = 6 });
            await Task.Delay(10); Assert.False(stopped.IsCompleted);
        }
        device.PushTelemetry(stop);
        Assert.True(await stopped.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(RecipeRampConfirmationEvidence.DeviceReferenceReadback, Assert.Single(destination.FinalConfirmations).Evidence);
        ImmutableArray<LinearRampSample> target = [new(SetpointVariable.Temperature, null, 37, true)];
        Assert.True(await destination.TryApplyAsync(target, default));
        var changed = destination.TryConfirmFinalAsync(target, default);
        device.PushTelemetry(Temperature(!bath, 37));
        await Assert.ThrowsAsync<InvalidOperationException>(() => changed);
        Assert.Empty(destination.FinalConfirmations);
        await engine.StopAsync("temperature route verified");
    }
}
