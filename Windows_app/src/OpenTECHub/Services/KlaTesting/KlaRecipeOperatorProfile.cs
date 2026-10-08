using System.Collections.Immutable;
using OpenTECHub.Services.Recipes;

namespace OpenTECHub.Services.KlaTesting;

/// <summary>
/// The operator's own settings as the profile of an autonomous kLa block (D-061/D-062). There is no
/// qualification record: the protocol comes from the Determinar kLa settings and the block's limits,
/// and the frozen request keeps what was actually used.
/// </summary>
public static class KlaRecipeOperatorProfile
{
    public const string Id = "operador";
    public const string Version = "atual";

    /// <summary>A limit the operator declared not applicable; large but finite for the budget journal.</summary>
    public const double NotApplicableSeconds = 1e9;
    public const int NotApplicableAttempts = 1_000_000;

    public static bool Applies(string profileId) => string.IsNullOrWhiteSpace(profileId) || profileId == Id;

    /// <summary>Seconds without air to the reactor that one attempt can take, from the kLa settings.</summary>
    public static double RemovalSeconds(KlaAssayProtocol protocol, KlaTestSettings settings)
        => protocol == KlaAssayProtocol.Biotic
            ? settings.MaxDegassingTimeMinutes * 60
            : settings.MaxDegassingTimeMinutes * 60 + settings.MaxPrestageSeconds;

    /// <summary>Fills not-applicable limits and aligns the exposure limits with the configured removal time.</summary>
    public static RecipeKlaBlockConfiguration Normalize(RecipeKlaBlockConfiguration configuration, KlaTestSettings settings)
    {
        if (!Applies(configuration.ProfileId)) return configuration;
        var removal = RemovalSeconds(configuration.Protocol, settings);
        var retry = configuration.Retry;
        // Abiotic removal is bounded by the kLa settings; a biotic block may cap its time without air below that.
        if (configuration.Protocol == KlaAssayProtocol.Biotic && retry.MaximumGasOffSecondsPerAttempt < NotApplicableSeconds)
            removal = retry.MaximumGasOffSecondsPerAttempt;
        var cumulative = retry.MaximumCumulativeGasOffSecondsPerCultivation >= NotApplicableSeconds
            ? NotApplicableSeconds : Math.Max(retry.MaximumCumulativeGasOffSecondsPerCultivation, removal);
        return configuration with
        {
            ProfileId = Id, ProfileVersion = Version,
            Retry = retry with { MaximumGasOffSecondsPerAttempt = removal, MaximumCumulativeGasOffSecondsPerCultivation = cumulative }
        };
    }

    public static KlaRecipeOperationalProfile Build(RecipeKlaBlockConfiguration configuration, KlaTestSettings settings,
        string installationId, DateTimeOffset now)
    {
        configuration = Normalize(configuration, settings);
        var biotic = configuration.Protocol == KlaAssayProtocol.Biotic;
        var defaults = new KlaProtocolSettings();
        var minimumDo = configuration.MinimumDoPercent;
        var recovery = configuration.ReturnSeconds;
        var protocol = defaults with
        {
            OxygenRemovalAgitationRpm = settings.DegassingAgitationRpm,
            MinimumRemovalTargetDoPercent = Math.Min(defaults.MinimumRemovalTargetDoPercent, settings.DOMinPercent),
            MaximumRemovalTargetDoPercent = Math.Max(defaults.MaximumRemovalTargetDoPercent, settings.DOMinPercent),
            RemovalTargetDoPercent = settings.DOMinPercent,
            AerationReturn = biotic ? new()
            {
                MinimumDoPercent = minimumDo, MaximumDoDropPoints = 100,
                MaximumGasOffSeconds = configuration.Retry.MaximumGasOffSecondsPerAttempt,
                MaximumRecoverySeconds = recovery, MinimumInterAssaySeconds = configuration.Retry.MinimumInterAssaySeconds
            } : new()
        };
        var qualified = now.AddSeconds(-1);
        return new()
        {
            Capabilities = new()
            {
                InstallationId = installationId, ProfileId = Id, ProfileVersion = Version,
                Protocols = [configuration.Protocol], EvidenceId = "decisao-do-operador", IsIsolatedSimulation = true
            },
            Template = new()
            {
                Protocol = configuration.Protocol, CaptureMode = KlaCaptureMode.Single, SequenceLimits = null,
                Settings = settings with { AutoAcceptRuns = false }, ProtocolSettings = protocol,
                Conditions = [new(Guid.NewGuid(), 0, 300, 1, 1)]
            },
            Quality = new() { ProfileId = Id, Version = Version },
            MaximumRetry = configuration.Retry with
            {
                RecoverableReasons = [KlaRetryReason.InsufficientWindow, KlaRetryReason.ExcessiveNoise, KlaRetryReason.UnstableCondition]
            },
            RecoveryCriteria = new()
            {
                MaximumTelemetryAgeSeconds = defaults.OxygenSampleTimeoutSeconds,
                MinimumOxygenPercent = biotic ? minimumDo : null,
                MaximumOxygenPercent = biotic ? 100 : null,
                MaximumOxygenSlopePercentPerSecond = biotic ? settings.StabilityDerivativeThresholdPercentPerSecond : null
            },
            MaximumRecoverySeconds = recovery, RecoveryStabilitySeconds = defaults.RecoveryStabilitySeconds,
            AgitationToleranceRpm = defaults.ReturnAgitationToleranceRpm, FlowToleranceLpm = defaults.ReturnFlowToleranceLpm,
            ReservationTimeoutSeconds = 120, DispatchToleranceSeconds = 30,
            QualifiedUtc = qualified, ValidUntilUtc = now.AddYears(10),
            NitrogenSourceConfirmedUtc = qualified, NitrogenIsolationConfirmedUtc = biotic ? qualified : null
        };
    }
}
