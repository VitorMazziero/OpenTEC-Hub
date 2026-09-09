using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Control;
using OpenTECHub.Services.KlaMapping;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;
using OpenTECHub.Services.Safety;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class SafetyCoordinatorTests
{
    private static Task CappedDelay(TimeSpan requested, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(requested.TotalMilliseconds, 0, 5)), ct);

    private static RecipeDocument HoldingRecipe()
    {
        var recipe = new RecipeDocument { Name = "Holding" };
        var start = RecipeNode.Create(NodeType.Start, id: "start");
        var monitor = RecipeNode.Create(NodeType.MonitorVariable, id: "mon");
        monitor.Set("variavel", MeasuredVariable.Temperature.ToString());
        monitor.Set("condicao", ComparisonOperator.GreaterOrEqual.ToString());
        monitor.Set("valorAlvo", 100000.0);
        var end = RecipeNode.Create(NodeType.End, id: "end");

        recipe.Nodes.AddRange([start, monitor, end]);
        recipe.Connections.Add(new RecipeConnection("start", ConnectorNames.Out, "mon", ConnectorNames.In));
        recipe.Connections.Add(new RecipeConnection("mon", ConnectorNames.Out, "end", ConnectorNames.In));
        return recipe;
    }

    [Fact]
    public async Task SafeStop_WithActiveRecipe_StopsRecipe_SendsFrames_AndReturnsOwnershipToManual()
    {
        var device = new RecordingDeviceService();
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var arbiter = new CommandArbiter(device, clock);
        var settings = new MemorySettingsService(new AppSettings());
        var engine = new RecipeEngine(arbiter, arbiter, settings, clock, journal: null, delay: CappedDelay);

        var coordinator = new SafetyCoordinator(arbiter, device, recipeEngine: engine);

        // 1. Start a recipe: it claims all actuators under CommandOwner.Recipe
        await engine.StartAsync(HoldingRecipe());
        Assert.All(CommandActuators.All, a => Assert.Equal(CommandOwner.Recipe, arbiter.OwnerOf(a)));
        Assert.Equal(RecipeRunState.Running, engine.State);

        // A regular manual dispatch would be refused by the arbiter (AUD-001)
        var manualAttempt = arbiter.Dispatch(CommandOwner.Manual, CommandBuilders.CoreSafeStop(50.0));
        Assert.False(manualAttempt.Accepted);

        var safeFrame = CommandBuilders.CoreSafeStop(50.0);
        var pumpDisable = OpenTECCommand.Create().Set(CommandKeys.PumpComm, 0);

        device.Sent.Clear();
        var result = await coordinator.ExecuteGlobalSafeStopAsync(safeFrame, pumpDisable, "emergência de ensaio");

        // 3. Verify safe stop succeeded and wire frames were delivered
        Assert.True(result.Accepted);
        Assert.Equal(RecipeRunState.Stopped, engine.State);
        Assert.All(CommandActuators.All, a => Assert.Equal(CommandOwner.Manual, arbiter.OwnerOf(a)));

        Assert.Equal(2, device.Sent.Count);
        Assert.Contains("tempSetpoint", device.Sent[0]);
        Assert.Equal("""{"pumpComm":0}""", device.Sent[1]);
    }

    [Fact]
    public async Task SafeStop_WithEngagedCascade_DisengagesCascade_AndRestoresManualOwnership()
    {
        var device = new RecordingDeviceService();
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var arbiter = new CommandArbiter(device, clock);
        var settings = new MemorySettingsService(new AppSettings());
        var cascade = new CascadeService(arbiter, arbiter, settings, new FakeKlaProfileStore(), clock);

        var coordinator = new SafetyCoordinator(arbiter, device, cascade: cascade);

        // Cascade claims agitation & aeration when engaged
        cascade.Engage(300.0, 2.0);
        Assert.True(cascade.IsEngaged);
        Assert.Equal(CommandOwner.Automatic, arbiter.OwnerOf(ActuatorId.Agitation));
        Assert.Equal(CommandOwner.Automatic, arbiter.OwnerOf(ActuatorId.Aeration));

        // Execute global safe stop
        var result = await coordinator.ExecuteGlobalSafeStopAsync(CommandBuilders.CoreSafeStop(50.0), null, "parada segura");

        Assert.True(result.Accepted);
        Assert.False(cascade.IsEngaged);
        Assert.All(CommandActuators.All, a => Assert.Equal(CommandOwner.Manual, arbiter.OwnerOf(a)));
        Assert.NotEmpty(device.Sent);
    }

    [Fact]
    public async Task SafeStop_WhenDisconnected_ReturnsTransportFailure_AndAvoidsFalsePositive()
    {
        var device = new RecordingDeviceService();
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var arbiter = new CommandArbiter(device, clock);
        var coordinator = new SafetyCoordinator(arbiter, device);

        device.PushState(ConnectionState.Disconnected);

        var result = await coordinator.ExecuteGlobalSafeStopAsync(CommandBuilders.CoreSafeStop(50.0), null, "teste desconectado");

        Assert.False(result.Accepted);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("desconectado", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(device.Sent);
        Assert.All(CommandActuators.All, a => Assert.Equal(CommandOwner.Manual, arbiter.OwnerOf(a)));
    }

    [Fact]
    public void DispatchSafety_OnArbiter_OverridesRecipeOwnership_AndRevokesClaims()
    {
        var device = new RecordingDeviceService();
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var arbiter = new CommandArbiter(device, clock);

        arbiter.Claim(CommandOwner.Recipe, CommandActuators.All, "receita ativa");
        Assert.All(CommandActuators.All, a => Assert.Equal(CommandOwner.Recipe, arbiter.OwnerOf(a)));

        bool revokedRaised = false;
        arbiter.OwnershipRevoked += _ => revokedRaised = true;

        var dispatchResult = arbiter.DispatchSafety(CommandBuilders.CoreSafeStop(50.0), "parada forçada", returnToManual: true);

        Assert.True(dispatchResult.Accepted);
        Assert.True(revokedRaised);
        Assert.All(CommandActuators.All, a => Assert.Equal(CommandOwner.Manual, arbiter.OwnerOf(a)));
        Assert.Single(device.Sent);
    }
}
