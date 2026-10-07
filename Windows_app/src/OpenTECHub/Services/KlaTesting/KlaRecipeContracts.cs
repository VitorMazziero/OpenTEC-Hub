using System.Collections.Immutable;
using OpenTECHub.Services.Recipes;

namespace OpenTECHub.Services.KlaTesting;

public enum KlaRecipeFailurePolicy { StopAfterRestoration, ContinueWithoutResultAfterRestoration }
public enum KlaRecipeTerminalStatus
{
    Completed, CompletedWithWarnings, Inconclusive, Cancelled,
    OperationalFailure, PersistenceFailure, RestorationFailure
}
public enum KlaAutomaticDecision { Selected, Retry, NotSelected, Aborted }
public enum KlaRetryReason { InsufficientWindow, ExcessiveNoise, UnstableCondition }

public sealed record KlaAutomaticQualityPolicy
{
    public required string ProfileId { get; init; }
    public required string Version { get; init; }
    public KlaDeterministicConfig Analysis { get; init; } = new();
    public bool RequireValidOur { get; init; }
    public ImmutableArray<string> AllowedConditionalReasonCodes { get; init; } = [];
    public void Validate(KlaAssayProtocol protocol)
    {
        ContractGuard.Text(ProfileId); ContractGuard.Text(Version);
        ArgumentNullException.ThrowIfNull(Analysis); Analysis.Validate();
        if (protocol == KlaAssayProtocol.Abiotic && RequireValidOur)
        {
            throw new ArgumentException("OUR não se aplica ao protocolo abiótico.");
        }

        if (AllowedConditionalReasonCodes.IsDefault ||
            AllowedConditionalReasonCodes.Any(string.IsNullOrWhiteSpace) ||
            AllowedConditionalReasonCodes.Distinct(StringComparer.Ordinal).Count() != AllowedConditionalReasonCodes.Length)
        {
            throw new ArgumentException("Motivos condicionais inválidos ou duplicados.");
        }
    }
}

/// <summary>No universal exposure defaults: a qualified cultivation profile must supply all budgets.</summary>
public sealed record KlaAutomaticRetryPolicy
{
    public required int MaximumAttemptsPerReplicate { get; init; }
    public required int MaximumAttemptsPerCultivation { get; init; }
    public required double MinimumInterAssaySeconds { get; init; }
    public required double MaximumBlockSeconds { get; init; }
    public required double MaximumGasOffSecondsPerAttempt { get; init; }
    public required double MaximumCumulativeGasOffSecondsPerCultivation { get; init; }
    public ImmutableArray<KlaRetryReason> RecoverableReasons { get; init; } = [];
    public void Validate()
    {
        if (MaximumAttemptsPerReplicate < 1 || MaximumAttemptsPerCultivation < MaximumAttemptsPerReplicate)
        {
            throw new ArgumentException("Orçamento de tentativas inválido.");
        }

        ContractGuard.NonNegative(MinimumInterAssaySeconds);
        ContractGuard.Positive(MaximumBlockSeconds); ContractGuard.Positive(MaximumGasOffSecondsPerAttempt);
        ContractGuard.Positive(MaximumCumulativeGasOffSecondsPerCultivation);
        if (MaximumCumulativeGasOffSecondsPerCultivation < MaximumGasOffSecondsPerAttempt || RecoverableReasons.IsDefault)
        {
            throw new ArgumentException("Orçamento de exposição ou motivos ausentes.");
        }

        foreach (var reason in RecoverableReasons)
        {
            ContractGuard.Defined(reason);
        }

        if (RecoverableReasons.Distinct().Count() != RecoverableReasons.Length)
        {
            throw new ArgumentException("Motivo de repetição duplicado.");
        }
    }
}

/// <summary>The return target is always the snapshot; tolerances qualify evidence, not substitute targets.</summary>
public sealed record KlaRecipeRestorationContract
{
    public required KlaReturnSnapshot BeforeAssay { get; init; }
    public required double MaximumRecoverySeconds { get; init; }
    public required double AgitationToleranceRpm { get; init; }
    public required double FlowToleranceLpm { get; init; }
    public required double StabilitySeconds { get; init; }
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(BeforeAssay); BeforeAssay.Validate();
        ContractGuard.Positive(MaximumRecoverySeconds); ContractGuard.Positive(StabilitySeconds);
        ContractGuard.Positive(AgitationToleranceRpm); ContractGuard.Positive(FlowToleranceLpm);
    }
}

public sealed record KlaRecipeRequest
{
    public required RecipeInvocationContext Context { get; init; }
    public PeriodicBlockInvocation? PeriodicInvocation { get; init; }
    public required KlaAssayDefinition Definition { get; init; }
    public required KlaAutomaticQualityPolicy Quality { get; init; }
    public required KlaAutomaticRetryPolicy Retry { get; init; }
    public required KlaRecipeRestorationContract Restoration { get; init; }
    public required DateTimeOffset AcquisitionDeadlineUtc { get; init; }
    public KlaRecipeFailurePolicy FailurePolicy { get; init; } = KlaRecipeFailurePolicy.StopAfterRestoration;

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Context); Context.Validate();
        PeriodicInvocation?.Validate();
        if (PeriodicInvocation is { } periodic && periodic.TargetNodeId != Context.NodeId)
            throw new ArgumentException("A agenda aponta para outro bloco de kLa.");
        ArgumentNullException.ThrowIfNull(Definition);
        if (!Definition.Conditions.IsDefault && Definition.Conditions.Any(c => c is null))
        {
            throw new ArgumentException("Condição ausente.");
        }
        Definition.Validate();
        ArgumentNullException.ThrowIfNull(Quality); Quality.Validate(Definition.Protocol);
        ArgumentNullException.ThrowIfNull(Retry); Retry.Validate();
        ArgumentNullException.ThrowIfNull(Restoration); Restoration.Validate();
        ContractGuard.Defined(FailurePolicy);
        if (AcquisitionDeadlineUtc <= Restoration.BeforeAssay.CapturedUtc)
        {
            throw new ArgumentException("Deadline deve suceder o snapshot anterior ao teste.");
        }

        var p = Definition.ProtocolSettings;
        var settings = Definition.Settings;
        ContractGuard.NonNegative(settings.DOMinPercent);
        ContractGuard.Positive(settings.DOMaxPercent);
        ContractGuard.Positive(settings.MaxDegassingTimeMinutes);
        ContractGuard.Positive(settings.MaxReoxygenationTimeMinutes);
        if (settings.DOMinPercent >= settings.DOMaxPercent || settings.DOMaxPercent > 100 || settings.AutoAcceptRuns)
        {
            throw new ArgumentException("Limiares de OD inválidos ou aceitação legada concorrente habilitada.");
        }
        foreach (var condition in Definition.Conditions)
        {
            if (!DeviceRanges.Accepts(SetpointVariable.Agitation, condition.AgitationRpm) ||
                !DeviceRanges.Accepts(SetpointVariable.Flow, condition.AirflowLpm))
            {
                throw new ArgumentException("Condição fora dos limites do dispositivo.");
            }
        }
        // Legacy explicit targets cannot silently override the mandatory pre-assay return.
        if (p.ReturnAgitationRpm is { } rpm && rpm != Restoration.BeforeAssay.AgitationSetpointRpm)
        {
            throw new ArgumentException("Agitação de retorno diverge do estado anterior.");
        }

        if (Definition.Conditions.Sum(c => (long)c.RequestedReplicates) > Retry.MaximumAttemptsPerCultivation)
        {
            throw new ArgumentException("Réplicas planejadas excedem orçamento do cultivo.");
        }

        if (Definition.Protocol == KlaAssayProtocol.Biotic)
        {
            var criteria = p.AerationReturn;
            if (criteria.MinimumDoPercent is null || criteria.MaximumDoDropPoints is null ||
                criteria.MaximumGasOffSeconds is null || criteria.MaximumRecoverySeconds is null ||
                criteria.MinimumInterAssaySeconds is null)
            {
                throw new ArgumentException("Receita biótica requer critérios de proteção e recuperação completos.");
            }

            if (Retry.MaximumGasOffSecondsPerAttempt > criteria.MaximumGasOffSeconds ||
                Restoration.MaximumRecoverySeconds > criteria.MaximumRecoverySeconds ||
                Retry.MinimumInterAssaySeconds < criteria.MinimumInterAssaySeconds)
            {
                throw new ArgumentException("Política automática excede os limites do protocolo biótico.");
            }
        }
    }
}

/// <summary>A partial/failed attempt retains scientific and physical outcomes independently.</summary>
public sealed record KlaRecipeAttemptResult
{
    public required Guid AttemptId { get; init; }
    public required Guid ConditionId { get; init; }
    public required int ReplicateNumber { get; init; }
    public required int AttemptNumber { get; init; }
    public required string RunFolder { get; init; }
    public required KlaScientificQuality KlaQuality { get; init; }
    public required KlaScientificQuality OurQuality { get; init; }
    public double? KlaPerHour { get; init; }
    public double? ConditionalCi95LowPerHour { get; init; }
    public double? ConditionalCi95HighPerHour { get; init; }
    public double? OurPercentPointsPerHour { get; init; }
    public double? OurMmolPerLPerHour { get; init; }
    public required KlaRestorationState Restoration { get; init; }
    public required Guid ReturnSnapshotId { get; init; }
    public required bool PersistenceConfirmed { get; init; }
    public required RecipeDecisionAuthor DecisionAuthor { get; init; }
    public required KlaAutomaticDecision Decision { get; init; }
    public required string PolicyVersion { get; init; }
    public required DateTimeOffset DecidedUtc { get; init; }
    public ImmutableArray<string> ReasonCodes { get; init; } = [];

    public void Validate()
    {
        if (AttemptId == Guid.Empty || ConditionId == Guid.Empty || ReturnSnapshotId == Guid.Empty ||
            ReplicateNumber < 1 || AttemptNumber < 1 || DecidedUtc == default)
        {
            throw new ArgumentException("Identidade de tentativa inválida.");
        }

        ContractGuard.Text(RunFolder); ContractGuard.Text(PolicyVersion);
        ContractGuard.Defined(KlaQuality); ContractGuard.Defined(OurQuality);
        ContractGuard.Defined(Restoration); ContractGuard.Defined(DecisionAuthor); ContractGuard.Defined(Decision);
        foreach (var value in new[] { KlaPerHour, OurPercentPointsPerHour, OurMmolPerLPerHour })
        {
            if (value.HasValue)
            {
                ContractGuard.NonNegative(value.Value);
            }
        }

        if (ConditionalCi95LowPerHour.HasValue != ConditionalCi95HighPerHour.HasValue ||
            ConditionalCi95LowPerHour is { } low && (!double.IsFinite(low) ||
                ConditionalCi95HighPerHour is not { } high || !double.IsFinite(high) || low > high || KlaPerHour is null))
        {
            throw new ArgumentException("Intervalo de confiança inválido.");
        }

        if (KlaQuality is KlaScientificQuality.Valid or KlaScientificQuality.Conditional && KlaPerHour is null)
        {
            throw new ArgumentException("Qualidade de kLa requer resultado numérico.");
        }

        if (ReasonCodes.IsDefault || ReasonCodes.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Motivos inválidos.");
        }

        if (Decision is KlaAutomaticDecision.Selected or KlaAutomaticDecision.Retry &&
            (Restoration != KlaRestorationState.Confirmed || !PersistenceConfirmed))
        {
            throw new ArgumentException("Decisão requer retorno ao snapshot e persistência confirmados.");
        }

        if (Decision == KlaAutomaticDecision.Selected &&
            KlaQuality is not (KlaScientificQuality.Valid or KlaScientificQuality.Conditional))
        {
            throw new ArgumentException("Tentativa selecionada sem qualidade científica aceitável.");
        }
    }
}

public sealed record KlaRecipeResult
{
    public required RecipeInvocationContext Context { get; init; }
    public PeriodicBlockInvocation? PeriodicInvocation { get; init; }
    public required Guid SessionId { get; init; }
    public required string SessionFolder { get; init; }
    public required KlaRecipeTerminalStatus Status { get; init; }
    public required ImmutableArray<KlaRecipeAttemptResult> Attempts { get; init; }
    /// <summary>Individual frozen pulses carry the newly captured return state for each attempt.</summary>
    public ImmutableArray<KlaAssayApiRequest> Pulses { get; init; } = [];
    public required bool PreAssayStateRestored { get; init; }
    public required bool PersistenceConfirmed { get; init; }
    public string? Reason { get; init; }
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Context); Context.Validate();
        PeriodicInvocation?.Validate();
        if (SessionId == Guid.Empty || Attempts.IsDefault || Pulses.IsDefault)
        {
            throw new ArgumentException("Sessão inválida.");
        }

        ContractGuard.Text(SessionFolder); ContractGuard.Defined(Status);
        foreach (var attempt in Attempts) { ArgumentNullException.ThrowIfNull(attempt); attempt.Validate(); }
        foreach (var pulse in Pulses) { ArgumentNullException.ThrowIfNull(pulse); pulse.Validate(); }
        if (Pulses.Select(p => p.RequestId).Distinct().Count() != Pulses.Length ||
            !Pulses.IsEmpty && (Attempts.Any(a => !Pulses.Any(p => p.RequestId == a.AttemptId)) ||
                Status is KlaRecipeTerminalStatus.Completed or KlaRecipeTerminalStatus.CompletedWithWarnings && Pulses.Length != Attempts.Length))
            throw new ArgumentException("Pulsos e tentativas da matriz divergem.");
        if (Attempts.Select(a => a.AttemptId).Distinct().Count() != Attempts.Length ||
            Attempts.Select(a => (a.ConditionId, a.ReplicateNumber, a.AttemptNumber)).Distinct().Count() != Attempts.Length ||
            Attempts.Where(a => a.Decision == KlaAutomaticDecision.Selected)
                .GroupBy(a => (a.ConditionId, a.ReplicateNumber)).Any(g => g.Count() > 1))
        {
            throw new ArgumentException("Identidade ou seleção de tentativa duplicada.");
        }

        if (Status is KlaRecipeTerminalStatus.Completed or KlaRecipeTerminalStatus.CompletedWithWarnings &&
            (!PreAssayStateRestored || !PersistenceConfirmed || Attempts.IsEmpty ||
             Attempts.Any(a => !a.PersistenceConfirmed || a.Restoration != KlaRestorationState.Confirmed) ||
             !Attempts.Any(a => a.Decision == KlaAutomaticDecision.Selected)))
        {
            throw new ArgumentException("Conclusão requer resultados salvos e estado anterior restaurado.");
        }

        if (Status == KlaRecipeTerminalStatus.RestorationFailure && PreAssayStateRestored)
        {
            throw new ArgumentException("Falha de retorno não pode declarar restauração concluída.");
        }
    }

    /// <summary>Checks policy, coverage and provenance against the exact frozen request.</summary>
    public void ValidateAgainst(KlaRecipeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request); request.Validate(); Validate();
        if (Context != request.Context || PeriodicInvocation != request.PeriodicInvocation)
        {
            throw new ArgumentException("Resultado pertence a outra invocação.");
        }

        foreach (var pulse in Pulses)
        {
            var invocation = pulse.RecipePulse?.Invocation ?? throw new ArgumentException("Pulso sem contexto de receita.");
            // Only a newly captured return state and a tighter acquisition deadline may change.
            var normalized = invocation with { Restoration = invocation.Restoration with
                { BeforeAssay = request.Restoration.BeforeAssay }, AcquisitionDeadlineUtc = request.AcquisitionDeadlineUtc };
            if (invocation.AcquisitionDeadlineUtc > request.AcquisitionDeadlineUtc ||
                RecipeContractSerializer.Fingerprint(normalized) != RecipeContractSerializer.Fingerprint(request))
                throw new ArgumentException("Pulso alterou a definição ou política da matriz.");
        }

        foreach (var attempt in Attempts)
        {
            var binding = Pulses.IsEmpty ? null : Pulses.Single(p => p.RequestId == attempt.AttemptId).RecipePulse;
            var condition = request.Definition.Conditions.FirstOrDefault(c => c.ConditionId == attempt.ConditionId);
            if (condition is null || attempt.ReplicateNumber > condition.RequestedReplicates ||
                attempt.AttemptNumber > request.Retry.MaximumAttemptsPerReplicate ||
                binding is not null && (binding.ConditionId != attempt.ConditionId || binding.ReplicateNumber != attempt.ReplicateNumber ||
                    binding.AttemptNumber != attempt.AttemptNumber) ||
                attempt.ReturnSnapshotId != (Pulses.IsEmpty ? request.Restoration.BeforeAssay.SnapshotId :
                    Pulses.Single(p => p.RequestId == attempt.AttemptId).RecipePulse!.Invocation.Restoration.BeforeAssay.SnapshotId) ||
                attempt.PolicyVersion != request.Quality.Version || attempt.DecisionAuthor != RecipeDecisionAuthor.AutomaticPolicy)
            {
                throw new ArgumentException("Tentativa diverge do contrato executado.");
            }

            if (attempt.Decision == KlaAutomaticDecision.Selected)
            {
                if (request.Quality.RequireValidOur && attempt.OurQuality != KlaScientificQuality.Valid)
                {
                    throw new ArgumentException("OUR obrigatório não validado.");
                }

                if (attempt.KlaQuality == KlaScientificQuality.Conditional &&
                    (attempt.ReasonCodes.IsEmpty || attempt.ReasonCodes.Any(r => !request.Quality.AllowedConditionalReasonCodes.Contains(r))))
                {
                    throw new ArgumentException("Resultado condicional não autorizado pelo perfil.");
                }
            }
        }
        foreach (var group in Attempts.GroupBy(a => (a.ConditionId, a.ReplicateNumber)))
        {
            var ordered = group.OrderBy(a => a.AttemptNumber).ToArray();
            for (var i = 0; i < ordered.Length; i++)
            {
                if (ordered[i].AttemptNumber != i + 1 ||
                    i < ordered.Length - 1 && ordered[i].Decision != KlaAutomaticDecision.Retry)
                {
                    throw new ArgumentException("Histórico incompleto ou nova tentativa após réplica selecionada.");
                }
            }
        }
        if (Attempts.Length > request.Retry.MaximumAttemptsPerCultivation)
        {
            throw new ArgumentException("Resultado excede orçamento de tentativas.");
        }

        if (Status is KlaRecipeTerminalStatus.Completed or KlaRecipeTerminalStatus.CompletedWithWarnings)
        {
            foreach (var condition in request.Definition.Conditions)
            {
                for (var replica = 1; replica <= condition.RequestedReplicates; replica++)
                {
                    if (!Attempts.Any(a => a.ConditionId == condition.ConditionId && a.ReplicateNumber == replica &&
                        a.Decision == KlaAutomaticDecision.Selected))
                    {
                        throw new ArgumentException("Conclusão sem atender todas as réplicas planejadas.");
                    }
                }
            }
        }
    }
}
