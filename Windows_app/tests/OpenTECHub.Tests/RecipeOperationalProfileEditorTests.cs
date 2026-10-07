using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Recipes;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipeOperationalProfileEditorTests
{
    private readonly TestClock _clock = new(DateTimeOffset.UnixEpoch);
    private KlaRecipeOperationalProfileRegistry Registry()
    {
        var registry = new KlaRecipeOperationalProfileRegistry("test-installation", _clock, true);
        registry.RegisterMany([KlaRecipeOperationalProfileTests.Profile(_clock),
            KlaRecipeOperationalProfileTests.Profile(_clock, KlaAssayProtocol.Biotic)]);
        return registry;
    }

    [Fact]
    public void Selection_is_explicit_preserves_user_budgets_and_round_trips_with_recipe()
    {
        var registry = Registry(); var node = RecipeNode.Create(NodeType.KlaAssay, id: "kla");
        node.Set("maximumBlockSeconds", 120);
        var vm = new RecipeNodeViewModel(node, operationalProfiles: registry);
        Assert.Single(vm.AvailableOperationalProfiles); Assert.Null(vm.SelectedOperationalProfile);
        Assert.Equal("", node.Text("profileId"));
        var changes = 0; vm.Changed += () => changes++;
        vm.SelectedOperationalProfile = vm.AvailableOperationalProfiles.Single();
        Assert.True(changes > 0); Assert.Equal(120, node.Number("maximumBlockSeconds"));
        Assert.DoesNotContain(vm.VisibleFields, f => f.Key is "profileId" or "profileVersion");
        Assert.Contains("Perfil de simulação", vm.OperationalProfileStatus);
        var recipe = new RecipeDocument(); recipe.Nodes.Add(node);
        var reopened = RecipeSerializer.Deserialize(RecipeSerializer.Serialize(recipe));
        var reopenedVm = new RecipeNodeViewModel(reopened.Nodes.Single(), operationalProfiles: registry);
        Assert.Equal(vm.SelectedOperationalProfile, reopenedVm.SelectedOperationalProfile);
        Assert.Equal(node.Parameters.ToJsonString(), reopenedVm.Model.Parameters.ToJsonString());
    }

    [Fact]
    public void Expiry_or_missing_version_retains_recipe_identity_without_fallback()
    {
        var registry = Registry(); var node = RecipeAutonomousBlockConfigurationTests.ConfiguredKla();
        node.Set("profileVersion", "missing");
        var vm = new RecipeNodeViewModel(node, operationalProfiles: registry);
        Assert.Single(vm.AvailableOperationalProfiles); Assert.Null(vm.SelectedOperationalProfile);
        Assert.Contains("indisponível", vm.OperationalProfileStatus); Assert.Equal("missing", node.Text("profileVersion"));
        vm.SelectedOperationalProfile = vm.AvailableOperationalProfiles.Single();
        var identity = node.Text("profileId"); var version = node.Text("profileVersion");
        _clock.Advance(TimeSpan.FromHours(25)); vm.RefreshOperationalProfiles();
        Assert.Empty(vm.AvailableOperationalProfiles); Assert.Null(vm.SelectedOperationalProfile);
        Assert.Equal(identity, node.Text("profileId")); Assert.Equal(version, node.Text("profileVersion"));
        Assert.Contains("vencido", vm.OperationalProfileStatus);
    }

    [Fact]
    public void Protocol_switch_updates_choices_and_context_fields_without_promoting_foreign_choice()
    {
        var node = RecipeNode.Create(NodeType.KlaAssay); var vm = new RecipeNodeViewModel(node, operationalProfiles: Registry());
        var abiotic = vm.AvailableOperationalProfiles.Single();
        Assert.DoesNotContain(vm.VisibleFields, f => f.Key == "requireValidOur");
        vm.Fields.Single(f => f.Key == "protocol").TextValue = nameof(KlaAssayProtocol.Biotic);
        Assert.Equal(KlaAssayProtocol.Biotic, vm.AvailableOperationalProfiles.Single().Protocol);
        Assert.Contains(vm.VisibleFields, f => f.Key == "requireValidOur");
        vm.SelectedOperationalProfile = abiotic;
        Assert.Null(vm.SelectedOperationalProfile); Assert.Equal("", node.Text("profileId"));
        vm.SelectedOperationalProfile = vm.AvailableOperationalProfiles.Single();
        Assert.Equal(KlaAssayProtocol.Biotic, vm.SelectedOperationalProfile!.Protocol);
        vm.Fields.Single(f => f.Key == "conditionsMode").TextValue = nameof(RecipeKlaConditionMode.Multiple);
        Assert.Contains(vm.VisibleFields, f => f.Key == "conditions");
        Assert.DoesNotContain(vm.VisibleFields, f => f.Key is "agitationRpm" or "airflowLpm");
    }

    [Fact]
    public void Unavailable_host_shows_saved_reference_without_inventing_capability()
    {
        var node = RecipeAutonomousBlockConfigurationTests.ConfiguredKla();
        var vm = new RecipeNodeViewModel(node);
        Assert.Empty(vm.AvailableOperationalProfiles); Assert.Null(vm.SelectedOperationalProfile);
        Assert.Contains(node.Text("profileId"), vm.OperationalProfileStatus);
        var before = node.Parameters.ToJsonString();
        vm.SelectedOperationalProfile = new("invented", "1", KlaAssayProtocol.Abiotic);
        Assert.Equal(before, node.Parameters.ToJsonString());
        Assert.Null(vm.SelectedOperationalProfile);
    }
}
