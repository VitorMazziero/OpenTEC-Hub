using OpenTECHub.Services.Recipes;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipeKlaEditorGuidanceTests
{
    [Fact]
    public void GroupsPreserveEveryPublicFieldAndRetryChoicesFollowTheProfileToggle()
    {
        var vm = new RecipeNodeViewModel(RecipeNode.Create(NodeType.KlaAssay));
        Assert.DoesNotContain(vm.VisibleFields, f => f.Key is "profileId" or "profileVersion");
        var groups = vm.KlaProcedureFields.Concat(vm.KlaRetryFields).Concat(vm.KlaSafetyFields).ToArray();
        Assert.Equal(vm.VisibleFields.Count(), groups.Length);
        Assert.Equal(groups.Length, groups.Select(f => f.Key).Distinct().Count());
        Assert.Contains(vm.KlaProcedureFields, f => f.Key == "failurePolicy");
        Assert.DoesNotContain(vm.KlaRetryFields, f => f.Key == "retryExcessiveNoise");
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        vm.Fields.Single(f => f.Key == "useProfileRetryReasons").BoolValue = false;
        Assert.Contains(vm.KlaRetryFields, f => f.Key == "retryExcessiveNoise");
        Assert.Contains(nameof(vm.KlaRetryFields), notifications);
        Assert.All(vm.KlaSafetyFields, f => Assert.True(f.HasHelpText));
        Assert.Contains("(s)", vm.Fields.Single(f => f.Key == "maximumBlockSeconds").EditorLabel);
    }
}
