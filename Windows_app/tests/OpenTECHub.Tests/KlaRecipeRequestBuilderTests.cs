using System.Text.Json.Nodes;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class KlaRecipeRequestBuilderTests
{
    [Fact]
    public async Task RetrySelectionPreservesLegacyInheritanceAndAllowsExplicitNoScientificRetries()
    {
        using var fixture = new RecipeAssayRestorationTests.Fixture(); await fixture.Initialize();
        var context = RecipeExecutionContractTests.Request().Context with { RecipeRunId = fixture.Lease.Authority.ExecutionId,
            NodeId = fixture.Lease.Authority.BlockId };
        var profile = KlaRecipeOperationalProfileTests.Profile(fixture.Clock);
        var node = RecipeAutonomousBlockConfigurationTests.ConfiguredKla();
        node.Set("minimumIntervalSeconds", 30);
        node.Set("maximumAttemptsPerReplicate", 2); node.Set("maximumAttemptsPerCultivation", 2);
        KlaRecipeRequest Build() => KlaRecipeRequestBuilder.Build(context, RecipeAutonomousBlockConfiguration.ReadKla(node),
            profile, fixture.Snapshot, fixture.Clock.GetUtcNow());
        var inherited = Build();
        foreach (var key in new[] { "useProfileRetryReasons", "retryInsufficientWindow", "retryExcessiveNoise", "retryUnstableCondition" })
            node.Parameters.Remove(key);
        Assert.Equal(RecipeContractSerializer.Serialize(inherited), RecipeContractSerializer.Serialize(Build()));
        node.Set("useProfileRetryReasons", false);
        Assert.Empty(Build().Retry.RecoverableReasons);
        node.Set("retryInsufficientWindow", true);
        Assert.Equal(KlaRetryReason.InsufficientWindow, Assert.Single(Build().Retry.RecoverableReasons));
        node.Set("retryExcessiveNoise", true);
        Assert.Throws<InvalidOperationException>(() => Build());
    }

    [Theory]
    [InlineData(KlaAssayProtocol.Abiotic, RecipeKlaConditionMode.SingleAtCurrentCondition)]
    [InlineData(KlaAssayProtocol.Biotic, RecipeKlaConditionMode.SingleAtCurrentCondition)]
    [InlineData(KlaAssayProtocol.Abiotic, RecipeKlaConditionMode.SingleExplicit)]
    [InlineData(KlaAssayProtocol.Biotic, RecipeKlaConditionMode.SingleExplicit)]
    [InlineData(KlaAssayProtocol.Abiotic, RecipeKlaConditionMode.Multiple)]
    [InlineData(KlaAssayProtocol.Biotic, RecipeKlaConditionMode.Multiple)]
    public async Task Builds_active_conditions_from_a_real_coordinated_snapshot_with_stable_ids(KlaAssayProtocol protocol, RecipeKlaConditionMode mode)
    {
        using var fixture = new RecipeAssayRestorationTests.Fixture(); await fixture.Initialize();
        var context = RecipeExecutionContractTests.Request().Context with { RecipeRunId = fixture.Lease.Authority.ExecutionId,
            NodeId = fixture.Lease.Authority.BlockId };
        var profile = KlaRecipeOperationalProfileTests.Profile(fixture.Clock, protocol);
        var node = RecipeAutonomousBlockConfigurationTests.ConfiguredKla();
        node.Set("protocol", protocol.ToString()); node.Set("conditionsMode", mode.ToString());
        node.Set("minimumIntervalSeconds", 30); node.Set("maximumAttemptsPerCultivation", 10);
        node.Set("agitationRpm", 400); node.Set("airflowLpm", 3);
        node.Set("conditions", new JsonArray(new JsonObject { ["agitationRpm"] = 450, ["airflowLpm"] = 4, ["replicates"] = 2 }));
        var configured = RecipeAutonomousBlockConfiguration.ReadKla(node);
        var before = fixture.Device.Sent.Count;
        var request = KlaRecipeRequestBuilder.Build(context, configured, profile, fixture.Snapshot, fixture.Clock.GetUtcNow());
        var again = KlaRecipeRequestBuilder.Build(context, configured, profile, fixture.Snapshot, fixture.Clock.GetUtcNow());
        Assert.Equal(RecipeContractSerializer.Serialize(request), RecipeContractSerializer.Serialize(again));
        Assert.Equal(mode == RecipeKlaConditionMode.SingleAtCurrentCondition ? 300 : mode == RecipeKlaConditionMode.SingleExplicit ? 400 : 450,
            request.Definition.Conditions[0].AgitationRpm);
        Assert.Equal(mode == RecipeKlaConditionMode.SingleAtCurrentCondition ? 2 : mode == RecipeKlaConditionMode.SingleExplicit ? 3 : 4,
            request.Definition.Conditions[0].AirflowLpm);
        Assert.Equal(mode == RecipeKlaConditionMode.Multiple ? 2 : 1, request.Definition.Conditions[0].RequestedReplicates);
        Assert.Equal(fixture.Snapshot, request.Restoration.BeforeAssay with { Actuators = fixture.Snapshot.Actuators, Controllers = fixture.Snapshot.Controllers });
        Assert.Equal(profile.MaximumRetry.RecoverableReasons.ToArray(), request.Retry.RecoverableReasons.ToArray());
        Assert.Equal(before, fixture.Device.Sent.Count);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Mismatched_execution_or_cultivation_cannot_build_an_assay(bool wrongCultivation)
    {
        using var fixture = new RecipeAssayRestorationTests.Fixture(); await fixture.Initialize();
        var context = RecipeExecutionContractTests.Request().Context with { RecipeRunId = fixture.Lease.Authority.ExecutionId };
        var profile = KlaRecipeOperationalProfileTests.Profile(fixture.Clock);
        if (wrongCultivation) profile = profile with { Template = profile.Template with
            { Context = new() { CultivationId = "another-cultivation" } } };
        else context = context with { RecipeRunId = Guid.NewGuid() };
        var node = RecipeAutonomousBlockConfigurationTests.ConfiguredKla(); node.Set("minimumIntervalSeconds", 30);
        Assert.Throws<ArgumentException>(() => KlaRecipeRequestBuilder.Build(context,
            RecipeAutonomousBlockConfiguration.ReadKla(node), profile, fixture.Snapshot, fixture.Clock.GetUtcNow()));
    }

    [Theory]
    [InlineData(KlaAssayProtocol.Abiotic)] [InlineData(KlaAssayProtocol.Biotic)]
    public async Task Qualification_expiry_bounds_acquisition_and_mandatory_our_cannot_be_disabled(KlaAssayProtocol protocol)
    {
        using var fixture = new RecipeAssayRestorationTests.Fixture(); await fixture.Initialize();
        var context = RecipeExecutionContractTests.Request().Context with { RecipeRunId = fixture.Lease.Authority.ExecutionId };
        var profile = KlaRecipeOperationalProfileTests.Profile(fixture.Clock, protocol);
        profile = profile with { ValidUntilUtc = fixture.Clock.GetUtcNow().AddSeconds(5),
            Quality = profile.Quality with { RequireValidOur = protocol == KlaAssayProtocol.Biotic } };
        var node = RecipeAutonomousBlockConfigurationTests.ConfiguredKla();
        node.Set("minimumIntervalSeconds", 30); node.Set("protocol", protocol.ToString());
        var request = KlaRecipeRequestBuilder.Build(context, RecipeAutonomousBlockConfiguration.ReadKla(node),
            profile, fixture.Snapshot, fixture.Clock.GetUtcNow());
        Assert.Equal(profile.ValidUntilUtc, request.AcquisitionDeadlineUtc);
        Assert.Equal(protocol == KlaAssayProtocol.Biotic, request.Quality.RequireValidOur);
    }
}
