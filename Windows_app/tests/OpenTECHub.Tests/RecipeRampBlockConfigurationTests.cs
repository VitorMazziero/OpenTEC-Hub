using System.Text.Json.Nodes;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipeRampBlockConfigurationTests
{
    [Fact]
    public void CompletionCriteriaRoundtripPreservesUserValuesAndRejectsInvalidLimits()
    {
        var node = RecipeNode.Create(NodeType.LinearSetpointRamp);
        node.Set("lines", new JsonArray(new JsonObject { ["variable"] = "Temperature", ["startSource"] = "Explicit",
            ["initialSetpoint"] = 25, ["finalSetpoint"] = 30, ["endAfterSeconds"] = 60 }));
        node.Set("temperatureTolerance", .2);
        node.Set("confirmationStabilitySeconds", 12);
        node.Set("confirmationTimeoutSeconds", 120);
        var recipe = new RecipeDocument(); recipe.Nodes.Add(node);
        var reopened = RecipeSerializer.Deserialize(RecipeSerializer.Serialize(recipe)).Nodes.Single();
        var configuration = RecipeRampBlockConfiguration.Read(reopened);
        Assert.Equal(.2, configuration.CompletionCriteria!.TemperatureToleranceCelsius);
        Assert.Equal(12, configuration.CompletionCriteria.StabilitySeconds);
        Assert.Equal(120, configuration.CompletionCriteria.TimeoutSeconds);
        Assert.Equal(RecipeRampCompletionCriteria.OperationalDefaults.PressureToleranceKilopascals, configuration.CompletionCriteria.PressureToleranceKilopascals);
        reopened.Set("temperatureTolerance", -1);
        Assert.Throws<ArgumentException>(() => RecipeRampBlockConfiguration.Read(reopened));
        reopened.Set("temperatureTolerance", .2);
        reopened.Set("confirmationTimeoutSeconds", 12);
        Assert.Throws<ArgumentException>(() => RecipeRampBlockConfiguration.Read(reopened));
        reopened.Set("confirmationTimeoutSeconds", 120);
        reopened.Set("maximumTelemetryGapSeconds", 0);
        Assert.Throws<ArgumentException>(() => RecipeRampBlockConfiguration.Read(reopened));
        reopened.Set("maximumTelemetryGapSeconds", 1e100);
        Assert.Throws<ArgumentException>(() => RecipeRampBlockConfiguration.Read(reopened));
    }

    [Fact]
    public void EditorShowsOnlyToleranceForSelectedParameters()
    {
        var node = RecipeNode.Create(NodeType.LinearSetpointRamp);
        node.Set("lines", new JsonArray(new JsonObject { ["variable"] = "Temperature", ["startSource"] = "Explicit",
            ["initialSetpoint"] = 25, ["finalSetpoint"] = 30, ["endAfterSeconds"] = 60 }));
        var editor = new OpenTECHub.ViewModels.RecipeNodeViewModel(node);
        Assert.Contains(editor.VisibleFields, field => field.Key == "temperatureTolerance");
        Assert.DoesNotContain(editor.VisibleFields, field => field.Key is "agitationTolerance" or "flowTolerance" or "phTolerance" or "pressureTolerance");
        Assert.Contains(editor.VisibleFields, field => field.Key == "confirmationStabilitySeconds");
        node.Set("lines", new JsonArray(new JsonObject { ["variable"] = "Oxygen", ["oxygenTarget"] = "ActiveCascadeReference",
            ["startSource"] = "Explicit", ["initialSetpoint"] = 30, ["finalSetpoint"] = 40, ["endAfterSeconds"] = 60 }));
        Assert.DoesNotContain(editor.VisibleFields, field => field.Key.EndsWith("Tolerance", StringComparison.Ordinal));
        Assert.DoesNotContain(editor.VisibleFields, field => field.Key == "confirmationStabilitySeconds");
    }

    [Fact]
    public void StoredMonitorRampIsRejectedWithoutSilentCascadeMigration()
    {
        var node = RecipeNode.Create(NodeType.LinearSetpointRamp);
        node.Set("cascadeNodeId", "cascade");
        node.Set("lines", new JsonArray(new JsonObject { ["variable"] = "Oxygen", ["startSource"] = "Explicit",
            ["initialSetpoint"] = 30, ["finalSetpoint"] = 40, ["endAfterSeconds"] = 120,
            ["oxygenTarget"] = "MonitorReference" }));
        var recipe = new RecipeDocument(); recipe.Nodes.Add(node);
        var reopened = RecipeSerializer.Deserialize(RecipeSerializer.Serialize(recipe)).Nodes.Single();
        Assert.Throws<ArgumentException>(() => RecipeRampBlockConfiguration.Read(reopened));
        Assert.Equal("MonitorReference", reopened.Rows("lines")[0]!["oxygenTarget"]!.GetValue<string>());
        Assert.Equal("cascade", reopened.Text("cascadeNodeId"));
        var editor = new OpenTECHub.ViewModels.RecipeNodeViewModel(reopened);
        var destination = editor.Fields.Single(field => field.Key == "lines").Rows.Single().Fields.Single(field => field.Key == "oxygenTarget");
        Assert.Null(destination.SelectedOption);
        Assert.Equal("MonitorReference", destination.TextValue);
        Assert.Throws<ArgumentException>(() => RecipeRampBlockConfiguration.Read(reopened));
    }
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
