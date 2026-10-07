using System.IO;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class KlaRecipeProfileRouterTests
{
    private sealed class Scope(KlaAssayExecutionCapabilities capabilities) : IKlaAssayExecution
    {
        public bool IsValidated => true;
        public KlaAssayExecutionCapabilities Capabilities => capabilities;
        public Task<KlaAssayApiResult> ExecuteWithRecoveryAsync(KlaAssayApiRequest request, CancellationToken ct)
            => Task.FromResult(new KlaAssayApiResult(new() { KlaQuality = KlaScientificQuality.Valid,
                Restoration = KlaRestorationState.Confirmed }, 40)
                { ReturnSnapshotId = request.RecipePulse!.Invocation.Restoration.BeforeAssay.SnapshotId, PersistenceReceiptId = "fixture" });
    }
    [Fact]
    public async Task Different_profiles_share_the_same_conservative_cultivation_budget_and_reopen_history()
    {
        var root = Path.Combine(Path.GetTempPath(), "recipe-profile-router-" + Guid.NewGuid().ToString("N"));
        using var fixture = new RecipeAssayRestorationTests.Fixture(); await fixture.Initialize();
        var clock = fixture.Clock; var first = KlaRecipeOperationalProfileTests.Profile(clock);
        var second = first with { Capabilities = first.Capabilities with { ProfileId = "second-profile" },
            Quality = first.Quality with { ProfileId = "second-profile" } };
        var registry = new KlaRecipeOperationalProfileRegistry("test-installation", clock, true);
        registry.Register(first); registry.Register(second); var router = new KlaRecipeExecutionRouter(registry);
        KlaAssayBudgetQuery? query = null;
        try
        {
            using (var api = new KlaAssayApi(Path.Combine(root, "api.json"), router, clock))
            {
                foreach (var profile in new[] { first, second })
                {
                    var node = RecipeAutonomousBlockConfigurationTests.ConfiguredKla(); node.Set("minimumIntervalSeconds", 30);
                    node.Set("profileId", profile.Quality.ProfileId);
                    node.Set("maximumAttemptsPerCultivation", profile == first ? 2 : 10);
                    var context = RecipeExecutionContractTests.Request().Context with { RecipeRunId = fixture.Lease.Authority.ExecutionId };
                    var request = KlaRecipeRequestBuilder.Build(context, RecipeAutonomousBlockConfiguration.ReadKla(node), profile,
                        fixture.Snapshot, clock.GetUtcNow());
                    var pulse = KlaRecipePulseMapper.Create(request, registry.InstallationId, request.Definition.Conditions[0].ConditionId,
                        1, 1, clock.GetUtcNow().AddSeconds(5));
                    using var registration = router.Register(pulse, new Scope(profile.Capabilities));
                    api.Create(pulse); await api.StartAsync(pulse.RequestId); await api.WaitForCompletionAsync(pulse.RequestId);
                    query = new(pulse.CultivationId, pulse.Limits, pulse.ReservedRemovalSeconds,
                        pulse.Definition.ProtocolSettings.AerationReturn.MinimumInterAssaySeconds ?? 0);
                    Assert.Equal(api.ReadCultivationBudget(pulse), api.ReadCultivationBudget(query));
                    Assert.Equal(profile == first ? 1 : 0, api.ReadCultivationBudget(query).RemainingAttempts);
                    clock.Advance(TimeSpan.FromSeconds(31));
                }
            }
            using var reopened = new KlaAssayApi(Path.Combine(root, "api.json"), router, clock);
            Assert.Equal(0, reopened.ReadCultivationBudget(query!).RemainingAttempts);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Registered_profile_refuses_forged_evidence_or_changed_scientific_settings()
    {
        using var fixture = new RecipeAssayRestorationTests.Fixture(); await fixture.Initialize();
        var profile = KlaRecipeOperationalProfileTests.Profile(fixture.Clock);
        var registry = new KlaRecipeOperationalProfileRegistry("test-installation", fixture.Clock, true); registry.Register(profile);
        var node = RecipeAutonomousBlockConfigurationTests.ConfiguredKla(); node.Set("minimumIntervalSeconds", 30);
        var context = RecipeExecutionContractTests.Request().Context with { RecipeRunId = fixture.Lease.Authority.ExecutionId };
        var request = KlaRecipeRequestBuilder.Build(context, RecipeAutonomousBlockConfiguration.ReadKla(node), profile,
            fixture.Snapshot, fixture.Clock.GetUtcNow());
        var router = new KlaRecipeExecutionRouter(registry);
        KlaAssayApiRequest Pulse(KlaRecipeRequest invocation) => KlaRecipePulseMapper.Create(invocation, registry.InstallationId,
            invocation.Definition.Conditions[0].ConditionId, 1, 1, fixture.Clock.GetUtcNow().AddSeconds(5));
        Assert.Throws<InvalidOperationException>(() => router.Register(Pulse(request), new Scope(profile.Capabilities with { EvidenceId = "forged" })));
        var changed = request with { Definition = request.Definition with { Settings = request.Definition.Settings with { DOMaxPercent = 90 } } };
        Assert.Throws<InvalidOperationException>(() => router.Register(Pulse(changed), new Scope(profile.Capabilities)));
    }
}
