using System.IO;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>Author decisions of 08/10/2026: D-060 (return retries), D-061 (no execution gates), D-062 (operator profile and folders).</summary>
public sealed class KlaOperatorDecisionTests
{
    private static RecipeAssayRecoveryResult Result(KlaRestorationState state, bool emergency = false, string? reason = null)
        => new(Guid.Empty, state, DateTimeOffset.UnixEpoch, emergency, reason, state == KlaRestorationState.Confirmed ? "{}" : null);

    [Fact]
    public async Task Return_is_resent_up_to_three_times_and_reports_every_failed_attempt()
    {
        var calls = 0;
        var confirmed = await RecipeAssayRestoration.WithRetriesAsync(() => Task.FromResult(++calls < 3
            ? Result(KlaRestorationState.Failed, reason: "fluxômetro offline") : Result(KlaRestorationState.Confirmed)), () => true, 3);
        Assert.Equal(3, calls);
        Assert.Equal(KlaRestorationState.Confirmed, confirmed.Restoration);
        Assert.Contains("tentativa 3/3", confirmed.Reason);
        Assert.Contains("tentativa 2/3: fluxômetro offline", confirmed.Reason);

        calls = 0;
        var failed = await RecipeAssayRestoration.WithRetriesAsync(() => { calls++; return Task.FromResult(Result(KlaRestorationState.Failed, reason: "sem eco")); },
            () => true, 3);
        Assert.Equal(3, calls);
        Assert.Equal(KlaRestorationState.Failed, failed.Restoration);
        Assert.Contains("tentativa 3/3: sem eco", failed.Reason);
    }

    [Theory]
    [InlineData(true, true)] [InlineData(false, false)]
    public async Task Emergency_or_revoked_authority_is_never_used_to_resend_the_return(bool emergency, bool authority)
    {
        var calls = 0;
        var result = await RecipeAssayRestoration.WithRetriesAsync(() => { calls++; return Task.FromResult(Result(KlaRestorationState.Failed, emergency)); },
            () => authority, 3);
        Assert.Equal(1, calls);
        Assert.Equal(KlaRestorationState.Failed, result.Restoration);
    }

    [Theory]
    [InlineData(KlaAssayProtocol.Abiotic)] [InlineData(KlaAssayProtocol.Biotic)]
    public void New_block_runs_with_the_operator_profile_from_the_kla_settings(KlaAssayProtocol protocol)
    {
        var clock = new TestClock(DateTimeOffset.Parse("2026-10-08T12:00:00Z"));
        var settings = new KlaTestSettings { DOMinPercent = 20, DOMaxPercent = 85, DegassingAgitationRpm = 700,
            MaxDegassingTimeMinutes = 5, MaxReoxygenationTimeMinutes = 10 };
        var registry = new KlaRecipeOperationalProfileRegistry("bench", clock, true, () => settings);
        var node = RecipeNode.Create(NodeType.KlaAssay); node.Set("protocol", protocol.ToString());
        var configuration = registry.Normalize(RecipeAutonomousBlockConfiguration.ReadKla(node));
        var profile = registry.Resolve(configuration);

        Assert.Equal(KlaRecipeOperatorProfile.Id, profile.Capabilities.ProfileId);
        Assert.True(profile.Capabilities.IsIsolatedSimulation);
        Assert.Equal(700, profile.Template.ProtocolSettings.OxygenRemovalAgitationRpm);
        Assert.Equal(20, profile.Template.ProtocolSettings.RemovalTargetDoPercent);
        Assert.Equal(600, profile.MaximumRecoverySeconds);
        Assert.Equal(30, configuration.Retry.MinimumInterAssaySeconds);
        Assert.Equal(KlaRecipeFailurePolicy.ContinueWithoutResultAfterRestoration, configuration.FailurePolicy);
        Assert.Equal(KlaRecipeOperatorProfile.NotApplicableSeconds, configuration.Retry.MaximumCumulativeGasOffSecondsPerCultivation);
        if (protocol == KlaAssayProtocol.Biotic)
        {
            Assert.Equal(5, profile.Template.ProtocolSettings.AerationReturn.MinimumDoPercent);
            Assert.Equal(300, configuration.Retry.MaximumGasOffSecondsPerAttempt);
            Assert.Equal(5, profile.RecoveryCriteria.MinimumOxygenPercent);
        }
        else Assert.Equal(300 + settings.MaxPrestageSeconds, configuration.Retry.MaximumGasOffSecondsPerAttempt);
        Assert.Contains(registry.AvailableProfiles, p => p.Capabilities.ProfileId == KlaRecipeOperatorProfile.Id && p.Template.Protocol == protocol);
    }

    [Fact]
    public void Execution_is_authorized_in_the_physical_application()
    {
        Assert.True(App.OperatorAuthorizedKlaExecution);
        Assert.True(new KlaActuationRelease(App.OperatorAuthorizedKlaExecution).AllowsBioticActuation);
    }

    [Fact]
    public void Recipe_sessions_get_readable_unique_folders_and_import_as_editable_copies()
    {
        var root = Path.Combine(Path.GetTempPath(), "kla-operator-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var writer = new BackgroundFileWriter(synchronous: true);
            var store = new KlaTestStore(root, writer);
            var request = RecipeExecutionContractTests.Request(KlaAssayProtocol.Biotic);
            var local = new DateTimeOffset(2026, 10, 8, 14, 30, 5, TimeSpan.FromHours(-3));
            var name = KlaRecipeAutonomousWorkSource.SessionName(request, local);
            Assert.StartsWith("2026-10-08_14h30m05s_Biotico_", name);
            Assert.Contains(request.Definition.Conditions.Length == 1 ? "_Unico_N" : "_Matriz_", name);
            Assert.EndsWith("_02", KlaRecipeAutonomousWorkSource.SessionName(request, local, 2));

            var session = store.CreateAutomaticTest(name, request.Definition);
            Assert.Equal(Path.Combine(KlaTestFileContracts.AutomaticSessionsDirectoryName, name), session.FolderName);
            Assert.True(Directory.Exists(Path.Combine(root, KlaTestFileContracts.AutomaticSessionsDirectoryName, name)));
            session.RecipeRequest = request;
            store.SaveTestManifest(session);
            Assert.True(KlaTestFileContracts.IsSessionFolder(session.FolderName));
            Assert.False(KlaTestFileContracts.IsSessionFolder(Path.Combine("..", name)));

            var listed = Assert.Single(store.ListTests());
            Assert.Equal(session.FolderName, listed.FolderName);
            Assert.NotNull(store.LoadTest(session.FolderName)!.RecipeRequest);

            var copy = store.ImportTestFolder(Path.Combine(root, session.FolderName));
            var editable = store.LoadTest(copy)!;
            Assert.Null(editable.RecipeRequest);
            Assert.NotEqual(session.TestId, editable.TestId);
            Assert.EndsWith("_edicao", editable.Name);
            Assert.NotNull(store.LoadTest(session.FolderName)!.RecipeRequest);
            Assert.Equal(2, store.ListTests().Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
