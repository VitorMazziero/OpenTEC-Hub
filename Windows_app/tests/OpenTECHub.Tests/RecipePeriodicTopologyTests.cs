using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipePeriodicTopologyTests
{
    private static RecipeDocument ParallelRecipe()
    {
        var recipe = new RecipeDocument();
        var periodic = RecipeNode.Create(NodeType.Periodic, id: "periodic");
        periodic.Set("coordinatedCascadeId", "cascade");
        recipe.Nodes.AddRange([RecipeNode.Create(NodeType.Start, id: "start"), periodic,
            RecipeAutonomousBlockConfigurationTests.ConfiguredKla(),
            RecipeNode.Create(NodeType.CascadeControl, id: "cascade"),
            RecipeNode.Create(NodeType.End, id: "end")]);
        recipe.Connections.AddRange([new("start", ConnectorNames.Out, "periodic", ConnectorNames.In),
            new("start", ConnectorNames.Out, "cascade", ConnectorNames.In),
            new("periodic", ConnectorNames.Out, "kla", ConnectorNames.In),
            new("cascade", ConnectorNames.Out, "end", ConnectorNames.In)]);
        return recipe;
    }

    [Fact]
    public void Parallel_schedule_roundtrips_with_a_single_owned_target()
    {
        var recipe = RecipeSerializer.Deserialize(RecipeSerializer.Serialize(ParallelRecipe()));
        Assert.True(RecipeValidator.Validate(recipe).IsValid);
        Assert.Equal(new RecipePeriodicBinding("periodic", "kla", "cascade"),
            RecipePeriodicTopology.ReadBinding(recipe, recipe.Node("periodic")!));
        var port = recipe.Node("periodic")!.Definition.Ports.Single(p => p.Direction == PortDirection.Out);
        Assert.Equal("Alvo periódico", port.Label);
        Assert.False(port.Multiple);
    }

    [Theory]
    [InlineData("missing-target")]
    [InlineData("two-targets")]
    [InlineData("wrong-target")]
    [InlineData("shared-target")]
    [InlineData("continuation")]
    [InlineData("loop-port")]
    [InlineData("after-cascade")]
    [InlineData("before-cascade")]
    [InlineData("join-before-branches")]
    public void Unsafe_topology_is_rejected_before_execution(string scenario)
    {
        var recipe = ParallelRecipe();
        switch (scenario)
        {
            case "missing-target": recipe.Connections.RemoveAll(c => c.SourceNodeId == "periodic"); break;
            case "two-targets": recipe.Connections.Add(new("periodic", ConnectorNames.Out, "end", ConnectorNames.In)); break;
            case "wrong-target":
                recipe.Connections.RemoveAll(c => c.SourceNodeId == "periodic");
                recipe.Connections.Add(new("periodic", ConnectorNames.Out, "end", ConnectorNames.In)); break;
            case "shared-target": recipe.Connections.Add(new("start", ConnectorNames.Out, "kla", ConnectorNames.In)); break;
            case "continuation": recipe.Connections.Add(new("kla", ConnectorNames.Out, "end", ConnectorNames.In)); break;
            case "loop-port":
                recipe.Connections.RemoveAll(c => c.SourceNodeId == "periodic");
                recipe.Connections.Add(new("periodic", ConnectorNames.LoopOut, "kla", ConnectorNames.In)); break;
            case "after-cascade":
                recipe.Connections.RemoveAll(c => c.TargetNodeId == "periodic");
                recipe.Connections.Add(new("cascade", ConnectorNames.Out, "periodic", ConnectorNames.In)); break;
            case "before-cascade": recipe.Connections.Add(new("kla", ConnectorNames.Out, "cascade", ConnectorNames.In)); break;
            case "join-before-branches":
                recipe.Nodes.Add(RecipeNode.Create(NodeType.And, id: "join"));
                recipe.Connections.RemoveAll(c => c.TargetNodeId == "periodic");
                recipe.Connections.AddRange([new("start", ConnectorNames.Out, "join", ConnectorNames.In),
                    new("cascade", ConnectorNames.Out, "join", ConnectorNames.In),
                    new("join", ConnectorNames.Out, "periodic", ConnectorNames.In)]); break;
        }
        Assert.Contains(RecipeValidator.Validate(recipe).Errors, e => e.NodeId == "periodic");
        Assert.Throws<ArgumentException>(() => RecipePeriodicTopology.ReadBinding(recipe, recipe.Node("periodic")!));
    }

    [Fact]
    public void Legacy_exit_condition_cannot_be_used_as_periodic_branch()
    {
        var recipe = ParallelRecipe();
        recipe.Connections.RemoveAll(c => c.TargetNodeId == "periodic");
        recipe.Connections.Add(new("cascade", ConnectorNames.LoopOut, "periodic", ConnectorNames.In));
        Assert.Contains(RecipeValidator.Validate(recipe).Errors, e => e.NodeId == "periodic");
    }

    [Fact]
    public void A_join_within_the_periodic_branch_preserves_parallelism()
    {
        var recipe = ParallelRecipe();
        recipe.Connections.RemoveAll(c => c.TargetNodeId == "periodic");
        recipe.Nodes.AddRange([RecipeNode.Create(NodeType.Timer, id: "left"),
            RecipeNode.Create(NodeType.Timer, id: "right"), RecipeNode.Create(NodeType.And, id: "join")]);
        recipe.Connections.AddRange([new("start", ConnectorNames.Out, "left", ConnectorNames.In),
            new("start", ConnectorNames.Out, "right", ConnectorNames.In),
            new("left", ConnectorNames.Out, "join", ConnectorNames.In),
            new("right", ConnectorNames.Out, "join", ConnectorNames.In),
            new("join", ConnectorNames.Out, "periodic", ConnectorNames.In)]);
        Assert.True(RecipeValidator.Validate(recipe).IsValid);
        Assert.Equal("kla", RecipePeriodicTopology.ReadBinding(recipe, recipe.Node("periodic")!).TargetNodeId);
    }
}
