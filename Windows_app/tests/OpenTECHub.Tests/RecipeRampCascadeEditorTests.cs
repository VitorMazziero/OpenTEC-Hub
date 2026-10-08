using OpenTECHub.Services.Recipes;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipeRampCascadeEditorTests
{
    [Fact]
    public void AssociationAppearsOnlyForCascadeOxygenAndUsesRecipeBlocksInsteadOfFreeText()
    {
        var node = RecipeNode.Create(NodeType.LinearSetpointRamp);
        var vm = new RecipeNodeViewModel(node);
        var lines = vm.Fields.Single(field => field.Key == "lines");
        lines.AddRowCommand.Execute(null);
        var row = lines.Rows.Single();
        var cascade = RecipeNode.Create(NodeType.CascadeControl, id: "cascade-a");
        vm.RefreshRampCascadeChoices([node, cascade, RecipeNode.Create(NodeType.Timer)]);
        Assert.False(vm.UsesRampCascadeReference);
        Assert.DoesNotContain(vm.VisibleFields, field => field.Key == "cascadeNodeId");
        Assert.Null(vm.SelectedRampCascade);
        var variable = row.Fields.Single(field => field.Key == "variable");
        variable.SelectedOption = variable.Options.Single(option => option.Value == "Oxygen");
        Assert.True(vm.UsesRampCascadeReference);
        Assert.Single(vm.AvailableRampCascades);
        vm.SelectedRampCascade = vm.AvailableRampCascades.Single();
        Assert.Equal("cascade-a", RecipeRampBlockConfiguration.Read(node).CascadeNodeId);
        var target = row.Fields.Single(field => field.Key == "oxygenTarget");
        Assert.Single(target.Options);
        Assert.DoesNotContain(target.Options, option => option.Value == "MonitorReference");
        variable.SelectedOption = variable.Options.Single(option => option.Value == "Temperature");
        Assert.False(vm.UsesRampCascadeReference);
        Assert.Null(RecipeRampBlockConfiguration.Read(node).CascadeNodeId);
        Assert.Equal("cascade-a", node.Text("cascadeNodeId"));
        variable.SelectedOption = variable.Options.Single(option => option.Value == "Oxygen");
        Assert.Equal("cascade-a", vm.SelectedRampCascade!.Value);
        vm.RefreshRampCascadeChoices([node]);
        Assert.Null(vm.SelectedRampCascade);
        Assert.Equal("cascade-a", node.Text("cascadeNodeId"));
        Assert.Contains("Adicione", vm.RampCascadeStatus);
        variable.SelectedOption = variable.Options.Single(option => option.Value == "Temperature");
        Assert.False(vm.UsesRampCascadeReference);
        Assert.Null(RecipeRampBlockConfiguration.Read(node).CascadeNodeId);
    }
}
