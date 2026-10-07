using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class KlaRecipeOperationalProfileTests
{
    internal static KlaRecipeOperationalProfile Profile(TestClock clock, KlaAssayProtocol protocol = KlaAssayProtocol.Abiotic)
    {
        var request = RecipeExecutionContractTests.Request(protocol);
        return new()
        {
            Capabilities = new() { InstallationId = "test-installation", ProfileId = request.Quality.ProfileId,
                ProfileVersion = request.Quality.Version, Protocols = [protocol], EvidenceId = "isolated-profile", IsIsolatedSimulation = true },
            Template = request.Definition with { SequenceLimits = null, Settings = request.Definition.Settings with
                { MaxDegassingTimeMinutes = .5, MaxPrestageSeconds = 5 } },
            Quality = request.Quality, MaximumRetry = request.Retry,
            RecoveryCriteria = new() { MaximumTelemetryAgeSeconds = 5,
                MinimumOxygenPercent = protocol == KlaAssayProtocol.Biotic ? 20 : null,
                MaximumOxygenPercent = protocol == KlaAssayProtocol.Biotic ? 100 : null,
                MaximumOxygenSlopePercentPerSecond = protocol == KlaAssayProtocol.Biotic ? .05 : null },
            MaximumRecoverySeconds = 120, RecoveryStabilitySeconds = 10, AgitationToleranceRpm = 10, FlowToleranceLpm = .1,
            ReservationTimeoutSeconds = 30, DispatchToleranceSeconds = 1,
            QualifiedUtc = clock.GetUtcNow(), ValidUntilUtc = clock.GetUtcNow().AddHours(24),
            NitrogenSourceConfirmedUtc = clock.GetUtcNow(),
            NitrogenIsolationConfirmedUtc = protocol == KlaAssayProtocol.Biotic ? clock.GetUtcNow() : null
        };
    }
    private static RecipeKlaBlockConfiguration Configuration(KlaAssayProtocol protocol = KlaAssayProtocol.Abiotic)
    {
        var node = RecipeAutonomousBlockConfigurationTests.ConfiguredKla();
        node.Set("protocol", protocol.ToString()); node.Set("minimumIntervalSeconds", 30);
        return RecipeAutonomousBlockConfiguration.ReadKla(node);
    }

    [Theory]
    [InlineData(KlaAssayProtocol.Abiotic)] [InlineData(KlaAssayProtocol.Biotic)]
    public void Resolves_an_explicit_profile_and_returns_independent_snapshots(KlaAssayProtocol protocol)
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch); var profile = Profile(clock, protocol);
        var registry = new KlaRecipeOperationalProfileRegistry("test-installation", clock, true);
        registry.Register(profile); registry.Register(profile);
        var copy = registry.Resolve(Configuration(protocol));
        Assert.NotSame(profile, copy); Assert.Equal(profile, copy with { Template = profile.Template,
            Capabilities = profile.Capabilities, Quality = profile.Quality, MaximumRetry = profile.MaximumRetry });
        Assert.Single(registry.AvailableProfiles);
        clock.Advance(TimeSpan.FromHours(25));
        Assert.Empty(registry.AvailableProfiles);
        Assert.Throws<ArgumentException>(() => registry.Resolve(Configuration(protocol)));
    }

    [Theory]
    [InlineData("physical")] [InlineData("installation")]
    [InlineData("changed-version")] [InlineData("wrong-protocol")]
    public void Cannot_promote_or_relabel_profile_evidence(string scenario)
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch); var profile = Profile(clock);
        var registry = new KlaRecipeOperationalProfileRegistry(scenario == "installation" ? "other-installation" : "test-installation",
            clock, scenario != "physical");
        if (scenario is "physical" or "installation")
            Assert.Throws<InvalidOperationException>(() => registry.Register(profile));
        else
        {
            registry.Register(profile);
            if (scenario == "changed-version")
                Assert.Throws<InvalidOperationException>(() => registry.Register(profile with { DispatchToleranceSeconds = 2 }));
            else Assert.Throws<InvalidOperationException>(() => registry.Resolve(Configuration(KlaAssayProtocol.Biotic)));
        }
    }

    [Theory]
    [InlineData("attempts")] [InlineData("interval")] [InlineData("exposure")] [InlineData("too-short-exposure")]
    [InlineData("block-time")] [InlineData("retry-reason")]
    public void Recipe_cannot_exceed_or_undercut_the_qualified_protocol(string scenario)
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var registry = new KlaRecipeOperationalProfileRegistry("test-installation", clock, true); registry.Register(Profile(clock));
        var configuration = Configuration();
        var retry = configuration.Retry;
        retry = scenario switch
        {
            "attempts" => retry with { MaximumAttemptsPerCultivation = 11 },
            "interval" => retry with { MinimumInterAssaySeconds = 0 },
            "exposure" => retry with { MaximumCumulativeGasOffSecondsPerCultivation = 301 },
            "too-short-exposure" => retry with { MaximumGasOffSecondsPerAttempt = 30 },
            "block-time" => retry with { MaximumBlockSeconds = 601 },
            _ => retry with { RecoverableReasons = [KlaRetryReason.ExcessiveNoise] }
        };
        Assert.Throws<InvalidOperationException>(() => registry.Resolve(configuration with { Retry = retry }));
    }

    [Fact]
    public void Biotic_mounting_and_recovery_protection_must_be_explicit()
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch); var profile = Profile(clock, KlaAssayProtocol.Biotic);
        Assert.Throws<ArgumentException>(() => (profile with { NitrogenIsolationConfirmedUtc = null }).Validate(clock.GetUtcNow()));
        Assert.Throws<ArgumentException>(() => (profile with { RecoveryCriteria = new() { MaximumTelemetryAgeSeconds = 5 } }).Validate(clock.GetUtcNow()));
        Assert.Throws<ArgumentException>(() => (profile with { QualifiedUtc = clock.GetUtcNow().AddDays(1) }).Validate(clock.GetUtcNow()));
        Assert.Throws<ArgumentException>(() => (profile with { RecoveryCriteria = profile.RecoveryCriteria with
            { MinimumOxygenPercent = 10 } }).Validate(clock.GetUtcNow()));
    }

    [Theory]
    [InlineData("oxygen")] [InlineData("duration")] [InlineData("legacy")] [InlineData("return")]
    public void Operational_profile_cannot_hide_invalid_or_competing_protocol_settings(string scenario)
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch); var profile = Profile(clock);
        var definition = scenario switch
        {
            "oxygen" => profile.Template with { Settings = profile.Template.Settings with { DOMaxPercent = 101 } },
            "duration" => profile.Template with { Settings = profile.Template.Settings with { MaxDegassingTimeMinutes = 5 } },
            "legacy" => profile.Template with { Settings = profile.Template.Settings with { AutoAcceptRuns = true } },
            _ => profile.Template with { ProtocolSettings = profile.Template.ProtocolSettings with { ReturnAgitationRpm = 300 } }
        };
        Assert.Throws<ArgumentException>(() => (profile with { Template = definition }).Validate(clock.GetUtcNow()));
    }
}
