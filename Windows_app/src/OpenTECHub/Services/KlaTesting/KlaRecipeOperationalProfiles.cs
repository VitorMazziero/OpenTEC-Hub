using System.Text.Json;
using OpenTECHub.Services.Recipes;

namespace OpenTECHub.Services.KlaTesting;

/// <summary>Installation evidence and operating limits are supplied explicitly, never inferred from a recipe.</summary>
public sealed record KlaRecipeOperationalProfile
{
    public required KlaAssayExecutionCapabilities Capabilities { get; init; }
    public required KlaAssayDefinition Template { get; init; }
    public required KlaAutomaticQualityPolicy Quality { get; init; }
    public required KlaAutomaticRetryPolicy MaximumRetry { get; init; }
    public required RecipeAssayRecoveryCriteria RecoveryCriteria { get; init; }
    public required double MaximumRecoverySeconds { get; init; }
    public required double RecoveryStabilitySeconds { get; init; }
    public required double AgitationToleranceRpm { get; init; }
    public required double FlowToleranceLpm { get; init; }
    public required double ReservationTimeoutSeconds { get; init; }
    public required double DispatchToleranceSeconds { get; init; }
    public required DateTimeOffset QualifiedUtc { get; init; }
    public required DateTimeOffset ValidUntilUtc { get; init; }
    public required DateTimeOffset NitrogenSourceConfirmedUtc { get; init; }
    public DateTimeOffset? NitrogenIsolationConfirmedUtc { get; init; }

    public void Validate(DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(Capabilities); ArgumentNullException.ThrowIfNull(Template);
        ArgumentNullException.ThrowIfNull(Quality); ArgumentNullException.ThrowIfNull(MaximumRetry);
        ArgumentNullException.ThrowIfNull(RecoveryCriteria);
        Template.Validate(); Quality.Validate(Template.Protocol); MaximumRetry.Validate(); RecoveryCriteria.Validate();
        ContractGuard.NonNegative(Template.Settings.DOMinPercent); ContractGuard.Positive(Template.Settings.DOMaxPercent);
        ContractGuard.Positive(Template.Settings.MaxDegassingTimeMinutes); ContractGuard.NonNegative(Template.Settings.MaxPrestageSeconds);
        ContractGuard.Positive(Template.Settings.MaxReoxygenationTimeMinutes);
        if (Template.Settings.DOMinPercent >= Template.Settings.DOMaxPercent || Template.Settings.DOMaxPercent > 100)
            throw new ArgumentException("Perfil exige limiares de OD dentro da faixa física.");
        if (!Capabilities.IsIsolatedSimulation || string.IsNullOrWhiteSpace(Capabilities.InstallationId) ||
            string.IsNullOrWhiteSpace(Capabilities.EvidenceId) || Capabilities.Protocols.IsDefaultOrEmpty ||
            Capabilities.Protocols.Any(p => !Enum.IsDefined(p)) ||
            Capabilities.Protocols.Distinct().Count() != Capabilities.Protocols.Length ||
            !Capabilities.Protocols.Contains(Template.Protocol) || Quality.ProfileId != Capabilities.ProfileId ||
            Quality.Version != Capabilities.ProfileVersion)
            throw new ArgumentException("Perfil não corresponde à instalação, protocolo e evidência isolada.");
        if (QualifiedUtc == default || QualifiedUtc > now || ValidUntilUtc <= now ||
            NitrogenSourceConfirmedUtc == default || NitrogenSourceConfirmedUtc > QualifiedUtc ||
            NitrogenIsolationConfirmedUtc is { } isolation && (isolation == default || isolation > QualifiedUtc) ||
            Template.Protocol == KlaAssayProtocol.Biotic && NitrogenIsolationConfirmedUtc is null)
            throw new ArgumentException("Qualificação vencida ou confirmações de montagem ausentes/inválidas.");
        if (Template.Settings.AutoAcceptRuns || Template.SequenceLimits is not null ||
            Template.ProtocolSettings.ReturnAgitationRpm is not null ||
            Template.Conditions.Any(c => c.Origin == ConditionOrigin.Map || c.SourceMapId is not null))
            throw new ArgumentException("Perfil automático não admite aceitação legada, mapa ou retorno fixo.");
        foreach (var value in new[] { MaximumRecoverySeconds, RecoveryStabilitySeconds, AgitationToleranceRpm,
            FlowToleranceLpm, ReservationTimeoutSeconds }) ContractGuard.Positive(value);
        ContractGuard.NonNegative(DispatchToleranceSeconds);
        if (MaximumRecoverySeconds < RecoveryStabilitySeconds)
            throw new ArgumentException("Prazo de recuperação menor que a estabilidade exigida.");
        if (!double.IsFinite(RemovalSeconds) || RemovalSeconds <= 0 || RemovalSeconds > MaximumRetry.MaximumGasOffSecondsPerAttempt)
            throw new ArgumentException("Protocolo excede o orçamento de exposição do perfil.");
        if (Template.Protocol == KlaAssayProtocol.Biotic)
        {
            var limits = Template.ProtocolSettings.AerationReturn;
            if (limits.MinimumDoPercent is null || limits.MaximumDoDropPoints is null ||
                limits.MaximumGasOffSeconds is null || limits.MaximumRecoverySeconds is null ||
                limits.MinimumInterAssaySeconds is null || RecoveryCriteria.MinimumOxygenPercent is null ||
                RecoveryCriteria.MinimumOxygenPercent < limits.MinimumDoPercent ||
                MaximumRetry.MaximumGasOffSecondsPerAttempt > limits.MaximumGasOffSeconds ||
                MaximumRecoverySeconds > limits.MaximumRecoverySeconds ||
                MaximumRetry.MinimumInterAssaySeconds < limits.MinimumInterAssaySeconds)
                throw new ArgumentException("Perfil biótico exige proteção e recuperação completas e compatíveis.");
        }
    }

    public double RemovalSeconds => Template.Protocol == KlaAssayProtocol.Biotic
        ? Template.ProtocolSettings.AerationReturn.MaximumGasOffSeconds ?? double.NaN
        : Template.Settings.MaxDegassingTimeMinutes * 60 + Template.Settings.MaxPrestageSeconds;
}

public sealed class KlaRecipeOperationalProfileRegistry(string installationId, TimeProvider time,
    bool isIsolatedEnvironment = false)
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Id, string Version, KlaAssayProtocol Protocol), KlaRecipeOperationalProfile> _profiles = [];
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    public void Register(KlaRecipeOperationalProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile = Copy(profile); profile.Validate(time.GetUtcNow());
        if (!isIsolatedEnvironment || string.IsNullOrWhiteSpace(installationId) || profile.Capabilities.InstallationId != installationId)
            throw new InvalidOperationException("Ambiente ou instalação não corresponde ao perfil qualificado.");
        var key = (profile.Capabilities.ProfileId, profile.Capabilities.ProfileVersion, profile.Template.Protocol);
        lock (_gate)
        {
            if (_profiles.TryGetValue(key, out var existing) && JsonSerializer.Serialize(existing) != JsonSerializer.Serialize(profile))
                throw new InvalidOperationException("Uma versão de perfil já registrada não pode mudar de conteúdo.");
            _profiles[key] = profile;
        }
    }

    public KlaRecipeOperationalProfile Resolve(RecipeKlaBlockConfiguration configuration)
    {
        KlaRecipeOperationalProfile profile;
        lock (_gate) profile = _profiles.GetValueOrDefault((configuration.ProfileId, configuration.ProfileVersion, configuration.Protocol))
            ?? throw new InvalidOperationException("Perfil operacional indisponível para esta instalação e protocolo.");
        profile.Validate(time.GetUtcNow()); configuration.Retry.Validate();
        ValidateConfiguration(profile, configuration);
        return Copy(profile);
    }

    internal static void ValidateConfiguration(KlaRecipeOperationalProfile profile, RecipeKlaBlockConfiguration configuration)
    {
        configuration.Retry.Validate();
        if (configuration.ProfileId != profile.Capabilities.ProfileId || configuration.ProfileVersion != profile.Capabilities.ProfileVersion ||
            configuration.Protocol != profile.Template.Protocol)
            throw new InvalidOperationException("Configuração pertence a outro perfil ou protocolo.");
        var requested = configuration.Retry; var limit = profile.MaximumRetry;
        if (requested.MaximumAttemptsPerReplicate > limit.MaximumAttemptsPerReplicate ||
            requested.MaximumAttemptsPerCultivation > limit.MaximumAttemptsPerCultivation ||
            requested.MaximumBlockSeconds > limit.MaximumBlockSeconds ||
            requested.MaximumGasOffSecondsPerAttempt > limit.MaximumGasOffSecondsPerAttempt ||
            requested.MaximumCumulativeGasOffSecondsPerCultivation > limit.MaximumCumulativeGasOffSecondsPerCultivation ||
            requested.MinimumInterAssaySeconds < limit.MinimumInterAssaySeconds ||
            requested.RecoverableReasons.Any(r => !limit.RecoverableReasons.Contains(r)) ||
            requested.MaximumGasOffSecondsPerAttempt < profile.RemovalSeconds ||
            configuration.Conditions.Sum(c => (long)c.Replicates) > requested.MaximumAttemptsPerCultivation)
            throw new InvalidOperationException("Configuração da receita excede os limites do perfil operacional.");
    }

    public IReadOnlyList<KlaRecipeOperationalProfile> AvailableProfiles
    {
        get
        {
            lock (_gate)
            {
                var now = time.GetUtcNow();
                return _profiles.Values.Where(p => p.QualifiedUtc <= now && p.ValidUntilUtc > now).Select(Copy).ToArray();
            }
        }
    }
}
