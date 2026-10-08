using System.IO;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipeExampleTests
{
    [Theory]
    [InlineData("kla-abiotico-unico.recipe.json")]
    [InlineData("kla-biotico-unico.recipe.json")]
    [InlineData("kla-abiotico-matriz.recipe.json")]
    [InlineData("kla-biotico-matriz.recipe.json")]
    [InlineData("kla-periodico-2h-4h.recipe.json")]
    [InlineData("rampas-tempos-distintos.recipe.json")]
    [InlineData("cascata-kla-periodico-rampa.recipe.json")]
    public void PublishedExampleLoadsValidatesAndPreservesItsExecutionContract(string fileName)
    {
        var recipe = RecipeSerializer.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "RecipeExamples", fileName)));
        var validation = RecipeValidator.Validate(recipe);
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
        var canonical = RecipeSerializer.Serialize(recipe);
        Assert.Equal(canonical, RecipeSerializer.Serialize(RecipeSerializer.Deserialize(canonical)));
        foreach (var assay in recipe.Nodes.Where(node => node.Type == NodeType.KlaAssay))
        {
            var configuration = RecipeAutonomousBlockConfiguration.ReadKla(assay);
            Assert.Equal("selecionar-perfil-qualificado", configuration.ProfileId);
            if (configuration.ConditionsMode == RecipeKlaConditionMode.Multiple)
            {
                Assert.Equal(2, configuration.Conditions.Length);
                Assert.Equal(4, configuration.Conditions.Sum(condition => condition.Replicates));
            }
        }
        foreach (var periodic in recipe.Nodes.Where(node => node.Type == NodeType.Periodic))
        {
            var configuration = RecipeAutonomousBlockConfiguration.ReadPeriodic(periodic);
            Assert.Equal(7200, configuration.Schedule.InitialDelaySeconds);
            Assert.Equal(14400, configuration.Schedule.PeriodSeconds);
            Assert.Equal("casc", configuration.CoordinatedCascadeId);
        }
        foreach (var ramp in recipe.Nodes.Where(node => node.Type == NodeType.LinearSetpointRamp))
        {
            var configuration = RecipeRampBlockConfiguration.Read(ramp);
            Assert.NotNull(configuration.CompletionCriteria);
            if (configuration.CascadeNodeId is not null)
                Assert.Equal(NodeType.CascadeControl, recipe.Node(configuration.CascadeNodeId)!.Type);
            else
                Assert.Equal(3, configuration.Definition.Lines.Select(line => line.EndAfterSeconds).Distinct().Count());
        }
    }
}
