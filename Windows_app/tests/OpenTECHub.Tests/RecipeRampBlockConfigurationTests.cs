using System.Text.Json.Nodes;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipeRampBlockConfigurationTests
{
    [Fact]
    public void RoundtripPreservesIndependentTimesAndIgnoresInactiveInputs()
    {
        var node = RecipeNode.Create(NodeType.LinearSetpointRamp);
        node.Set("lines", new JsonArray(
            new JsonObject { ["variable"] = "Agitation", ["startSource"] = "CurrentConfirmed",
                ["initialSetpoint"] = "inactive-invalid", ["finalSetpoint"] = 400, ["endAfterSeconds"] = 60,
                ["oxygenTarget"] = "inactive-invalid" },
            new JsonObject { ["variable"] = "Oxygen", ["startSource"] = "Explicit",
                ["initialSetpoint"] = 30, ["finalSetpoint"] = 40, ["endAfterSeconds"] = 120,
                ["oxygenTarget"] = "ActiveCascadeReference" }));
        Assert.Throws<ArgumentException>(() => RecipeRampBlockConfiguration.Read(node));
        node.Set("cascadeNodeId", "cascade");
        var recipe = new RecipeDocument(); recipe.Nodes.Add(node);
        var reopened = RecipeSerializer.Deserialize(RecipeSerializer.Serialize(recipe)).Nodes.Single();
        var configuration = RecipeRampBlockConfiguration.Read(reopened);
        Assert.Equal("cascade", configuration.CascadeNodeId);
        Assert.Null(configuration.Definition.Lines[0].InitialSetpoint);
        Assert.Null(configuration.Definition.Lines[0].OxygenTarget);
        Assert.Equal(new double[] { 60, 120 }, configuration.Definition.Lines.Select(line => line.EndAfterSeconds));
        ((JsonObject)reopened.Rows("lines")[1]!)["endAfterSeconds"] = 0;
        Assert.Throws<ArgumentException>(() => RecipeRampBlockConfiguration.Read(reopened));
    }
}
