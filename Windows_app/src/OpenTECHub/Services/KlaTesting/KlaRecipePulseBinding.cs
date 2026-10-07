using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenTECHub.Services.Recipes;

namespace OpenTECHub.Services.KlaTesting;

/// <summary>Frozen recipe provenance of one E6 pulse. Resending is not another scientific attempt.</summary>
public sealed record KlaRecipePulseBinding
{
    public required KlaRecipeRequest Invocation { get; init; }
    public required string InvocationSha256 { get; init; }
    public required string InstallationId { get; init; }
    public required Guid ConditionId { get; init; }
    public required int ReplicateNumber { get; init; }
    public required int AttemptNumber { get; init; }

    public Guid AttemptId => Identity(Invocation.Context, ConditionId, ReplicateNumber, AttemptNumber);

    public void ValidateAgainst(KlaAssayApiRequest pulse)
    {
        ArgumentNullException.ThrowIfNull(Invocation);
        Invocation.Validate();
        if (string.IsNullOrWhiteSpace(InstallationId) ||
            InvocationSha256 != RecipeContractSerializer.Fingerprint(Invocation) ||
            AttemptNumber < 1 || AttemptNumber > Invocation.Retry.MaximumAttemptsPerReplicate || ReplicateNumber < 1)
            throw new ArgumentException("Vínculo da tentativa com a receita inválido.");
        var condition = Invocation.Definition.Conditions.SingleOrDefault(c => c.ConditionId == ConditionId);
        if (condition is null || ReplicateNumber > condition.RequestedReplicates || pulse.RequestId != AttemptId ||
            pulse.CultivationId != Invocation.Context.CultivationId || pulse.ConditionSelection != KlaConditionSelection.Explicit ||
            pulse.FailurePolicy != KlaAssayFailurePolicy.HaltRecipe ||
            pulse.DeadlineUtc > Invocation.AcquisitionDeadlineUtc ||
            pulse.StartDeadlineUtc < Invocation.Restoration.BeforeAssay.CapturedUtc ||
            JsonSerializer.Serialize(pulse.Definition) != JsonSerializer.Serialize(PulseDefinition(Invocation, condition)) ||
            pulse.Limits != Limits(Invocation))
            throw new ArgumentException("Pulso diverge da invocação congelada.");
        var removal = pulse.Definition.Protocol == KlaAssayProtocol.Biotic
            ? pulse.Definition.ProtocolSettings.AerationReturn.MaximumGasOffSeconds!.Value
            : pulse.Definition.Settings.MaxDegassingTimeMinutes * 60 + pulse.Definition.Settings.MaxPrestageSeconds;
        if (removal > Invocation.Retry.MaximumGasOffSecondsPerAttempt)
            throw new ArgumentException("Prazo de remoção excede o orçamento da tentativa.");
    }

    internal static KlaAssayDefinition PulseDefinition(KlaRecipeRequest request, KlaAssayCondition condition)
        => request.Definition with { CaptureMode = KlaCaptureMode.Single, SequenceLimits = null,
            Conditions = [condition with { RequestedReplicates = 1 }] };

    internal static KlaCultivationAssayLimits Limits(KlaRecipeRequest request)
        => new(request.Retry.MaximumAttemptsPerCultivation,
            request.Retry.MaximumCumulativeGasOffSecondsPerCultivation, request.Retry.MinimumInterAssaySeconds);

    internal static Guid Identity(RecipeInvocationContext context, Guid conditionId, int replicate, int attempt)
    {
        // Payload is deliberately excluded: changed parameters keep the ID and are rejected by E6.
        var key = $"{context.IdempotencyKey}/{conditionId:N}/{replicate}/{attempt}";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return new Guid(digest.AsSpan(0, 16));
    }
}

public static class KlaRecipePulseMapper
{
    public static KlaAssayApiRequest Create(KlaRecipeRequest invocation, string installationId,
        Guid conditionId, int replicateNumber, int attemptNumber, DateTimeOffset startDeadlineUtc,
        DateTimeOffset? acquisitionDeadlineUtc = null)
    {
        var frozen = RecipeContractSerializer.Snapshot(invocation);
        var condition = frozen.Definition.Conditions.Single(c => c.ConditionId == conditionId);
        var binding = new KlaRecipePulseBinding { Invocation = frozen,
            InvocationSha256 = RecipeContractSerializer.Fingerprint(frozen), InstallationId = installationId,
            ConditionId = conditionId, ReplicateNumber = replicateNumber, AttemptNumber = attemptNumber };
        var pulse = new KlaAssayApiRequest(binding.AttemptId, frozen.Context.CultivationId,
            KlaRecipePulseBinding.PulseDefinition(frozen, condition), KlaConditionSelection.Explicit,
            startDeadlineUtc, acquisitionDeadlineUtc ?? frozen.AcquisitionDeadlineUtc,
            KlaRecipePulseBinding.Limits(frozen)) { RecipePulse = binding };
        pulse.Validate();
        return pulse;
    }
}

/// <summary>Capabilities belong to an installation/profile, not a global approval of every protocol.</summary>
public sealed record KlaAssayExecutionCapabilities
{
    public required string InstallationId { get; init; }
    public required string ProfileId { get; init; }
    public required string ProfileVersion { get; init; }
    public required ImmutableArray<KlaAssayProtocol> Protocols { get; init; }
    public required string EvidenceId { get; init; }
    public bool IsIsolatedSimulation { get; init; }

    public void EnsureAllows(KlaAssayApiRequest request)
    {
        if (string.IsNullOrWhiteSpace(InstallationId) || string.IsNullOrWhiteSpace(ProfileId) ||
            string.IsNullOrWhiteSpace(ProfileVersion) || string.IsNullOrWhiteSpace(EvidenceId) ||
            Protocols.IsDefaultOrEmpty || Protocols.Distinct().Count() != Protocols.Length ||
            Protocols.Any(p => !Enum.IsDefined(p)) || !Protocols.Contains(request.Definition.Protocol))
            throw new InvalidOperationException("Protocolo sem capacidade operacional qualificada.");
        if (request.RecipePulse is { } pulse && (InstallationId != pulse.InstallationId ||
            ProfileId != pulse.Invocation.Quality.ProfileId || ProfileVersion != pulse.Invocation.Quality.Version))
            throw new InvalidOperationException("Capacidade pertence a outra instalação ou perfil.");
        // Preserve the E7 physical biotic gate; declaring a capability does not bypass it.
        new KlaActuationRelease(IsIsolatedSimulation).EnsureCanRun(request.Definition.Protocol);
    }
}
