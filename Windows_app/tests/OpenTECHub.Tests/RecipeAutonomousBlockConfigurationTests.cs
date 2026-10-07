using System.Text.Json.Nodes;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Recipes;
using OpenTECHub.Services.Communication;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipeAutonomousBlockConfigurationTests
{
    [Theory]
    [InlineData("missing", false)]
    [InlineData("timer", false)]
    [InlineData("cascade", false)]
    [InlineData("cascade", true)]
    [InlineData("", false)]
    public void Periodic_cascade_binding_requires_an_existing_reachable_cascade(string binding, bool reachable)
    {
        var recipe = new RecipeDocument();
        var periodic = RecipeNode.Create(NodeType.Periodic, id: "periodic");
        periodic.Set("coordinatedCascadeId", binding);
        recipe.Nodes.AddRange([RecipeNode.Create(NodeType.Start, id: "start"),
            RecipeNode.Create(NodeType.End, id: "end"), periodic,
            RecipeNode.Create(NodeType.Timer, id: "timer"),
            RecipeNode.Create(NodeType.CascadeControl, id: "cascade"), ConfiguredKla()]);
        recipe.Connections.Add(new RecipeConnection("start", ConnectorNames.Out, "periodic", ConnectorNames.In));
        recipe.Connections.Add(new RecipeConnection("periodic", ConnectorNames.Out, "kla", ConnectorNames.In));
        recipe.Connections.Add(new RecipeConnection("start", ConnectorNames.Out, "end", ConnectorNames.In));
        if (reachable)
            recipe.Connections.Add(new RecipeConnection("start", ConnectorNames.Out, "cascade", ConnectorNames.In));
        var errors = RecipeValidator.Validate(recipe).Errors.Where(e => e.NodeId == periodic.Id);
        if (binding.Length == 0 || reachable) Assert.Empty(errors);
        else Assert.Single(errors);
    }

    internal static RecipeNode ConfiguredKla()
    {
        var node = RecipeNode.Create(NodeType.KlaAssay, id: "kla");
        node.Set("profileId", "qualified-test-profile"); node.Set("profileVersion", "1");
        node.Set("maximumBlockSeconds", 600); node.Set("maximumGasOffSeconds", 60);
        node.Set("maximumCultivationGasOffSeconds", 300);
        return node;
    }

    [Theory]
    [InlineData(KlaAssayProtocol.Abiotic, RecipeKlaConditionMode.SingleAtCurrentCondition)]
    [InlineData(KlaAssayProtocol.Biotic, RecipeKlaConditionMode.SingleAtCurrentCondition)]
    [InlineData(KlaAssayProtocol.Abiotic, RecipeKlaConditionMode.SingleExplicit)]
    [InlineData(KlaAssayProtocol.Biotic, RecipeKlaConditionMode.SingleExplicit)]
    [InlineData(KlaAssayProtocol.Abiotic, RecipeKlaConditionMode.Multiple)]
    [InlineData(KlaAssayProtocol.Biotic, RecipeKlaConditionMode.Multiple)]
    public void Protocol_and_conditions_survive_roundtrip_with_contextual_fields(KlaAssayProtocol protocol, RecipeKlaConditionMode mode)
    {
        var node = ConfiguredKla(); node.Set("protocol", protocol.ToString()); node.Set("conditionsMode", mode.ToString());
        node.Set("requireValidOur", true);
        node.Set("conditions", new JsonArray(new JsonObject { ["agitationRpm"] = 300, ["airflowLpm"] = 2, ["replicates"] = 2 },
            new JsonObject { ["agitationRpm"] = 400, ["airflowLpm"] = 3, ["replicates"] = 1 }));
        var recipe = new RecipeDocument(); recipe.Nodes.Add(node);
        var back = RecipeSerializer.Deserialize(RecipeSerializer.Serialize(recipe)).Nodes.Single();
        var configured = RecipeAutonomousBlockConfiguration.ReadKla(back);
        Assert.Equal(protocol, configured.Protocol); Assert.Equal(mode, configured.ConditionsMode);
        Assert.Equal(protocol == KlaAssayProtocol.Biotic, configured.RequireValidOur);
        Assert.Equal(mode == RecipeKlaConditionMode.Multiple ? 2 : mode == RecipeKlaConditionMode.SingleExplicit ? 1 : 0, configured.Conditions.Length);
        if (mode == RecipeKlaConditionMode.Multiple) Assert.Equal(2, configured.Conditions[0].Replicates);
        var vm = new RecipeNodeViewModel(back);
        Assert.Equal(mode == RecipeKlaConditionMode.SingleExplicit, vm.Fields.Single(f => f.Parameter.Key == "agitationRpm").IsVisible);
        Assert.Equal(mode == RecipeKlaConditionMode.Multiple, vm.Fields.Single(f => f.Parameter.Key == "conditions").IsVisible);
        Assert.Equal(protocol == KlaAssayProtocol.Biotic, vm.Fields.Single(f => f.Parameter.Key == "requireValidOur").IsVisible);
    }

    [Theory]
    [InlineData("protocol")] [InlineData("conditionsMode")] [InlineData("failurePolicy")]
    public void Unknown_or_numeric_options_are_refused(string key)
    {
        var node = ConfiguredKla(); node.Set(key, "unknown");
        Assert.Throws<ArgumentException>(() => RecipeAutonomousBlockConfiguration.ReadKla(node));
        node.Set(key, 0); Assert.Throws<ArgumentException>(() => RecipeAutonomousBlockConfiguration.ReadKla(node));
        node.Set(key, "0"); Assert.Throws<ArgumentException>(() => RecipeAutonomousBlockConfiguration.ReadKla(node));
    }

    [Fact]
    public void Explicit_budgets_and_integer_replicates_are_required()
    {
        Assert.Throws<ArgumentException>(() => RecipeAutonomousBlockConfiguration.ReadKla(RecipeNode.Create(NodeType.KlaAssay)));
        var node = ConfiguredKla(); node.Set("conditionsMode", nameof(RecipeKlaConditionMode.Multiple));
        node.Set("conditions", new JsonArray(new JsonObject { ["agitationRpm"] = 300, ["airflowLpm"] = 2, ["replicates"] = 1.5 }));
        Assert.Throws<ArgumentException>(() => RecipeAutonomousBlockConfiguration.ReadKla(node));
        node.Set("conditionsMode", nameof(RecipeKlaConditionMode.SingleAtCurrentCondition));
        Assert.Empty(RecipeAutonomousBlockConfiguration.ReadKla(node).Conditions);
        node.Set("maximumAttemptsPerReplicate", 2);
        Assert.Throws<ArgumentException>(() => RecipeAutonomousBlockConfiguration.ReadKla(node));
    }

    [Fact]
    public async Task New_blocks_without_runtime_cannot_claim_or_command_devices()
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch); var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, clock);
        using var engine = new RecipeEngine(arbiter, arbiter, new MemorySettingsService(), clock);
        var recipe = new RecipeDocument();
        recipe.Nodes.AddRange([RecipeNode.Create(NodeType.Start, id: "start"), ConfiguredKla(), RecipeNode.Create(NodeType.End, id: "end")]);
        recipe.Connections.AddRange([new("start", ConnectorNames.Out, "kla", ConnectorNames.In), new("kla", ConnectorNames.Out, "end", ConnectorNames.In)]);
        Assert.True(RecipeValidator.Validate(recipe).IsValid);
        Assert.False(engine.CanStart(recipe, out var reason)); Assert.Contains("qualificado", reason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartAsync(recipe));
        Assert.Empty(device.Sent);
    }

    [Fact]
    public void Periodic_defaults_map_to_two_six_ten_hours_and_reject_invalid_units_or_zero_period()
    {
        var node = RecipeNode.Create(NodeType.Periodic);
        var configuration = RecipeAutonomousBlockConfiguration.ReadPeriodic(node);
        Assert.Equal(new double[] { 7200, 21600, 36000 }, Enumerable.Range(0, 3).Select(i => configuration.Schedule.DueAfterSeconds(i)));
        node.Set("periodUnit", "days"); Assert.Throws<ArgumentException>(() => RecipeAutonomousBlockConfiguration.ReadPeriodic(node));
        node.Set("periodUnit", nameof(TimeUnit.Hours)); node.Set("period", 0);
        Assert.Throws<ArgumentException>(() => RecipeAutonomousBlockConfiguration.ReadPeriodic(node));
    }
}
