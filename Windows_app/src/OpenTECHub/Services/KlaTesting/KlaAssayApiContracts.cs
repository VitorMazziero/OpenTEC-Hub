using System.Collections.Immutable;
using OpenTECHub.Services.Recipes;

namespace OpenTECHub.Services.KlaTesting;

public enum KlaAssayApiState { Created, Running, Completed, Inconclusive, Cancelled, RestorationFailed, Skipped, Interrupted, PersistenceFailed }
public enum KlaAssayFailurePolicy { HaltRecipe, ContinueAfterConfirmedReturn }
public enum KlaConditionSelection { Explicit, Current }

/// <summary>Read-only projection of the authoritative journal; reading does not reserve a pulse.</summary>
public sealed record KlaCultivationAssayBudget(int RemainingAttempts, double RemainingRemovalSeconds,
    double WaitSeconds, string? BlockedReason)
{
    public bool CanStart => RemainingAttempts > 0 && WaitSeconds <= 0 && BlockedReason is null;
}

public sealed record KlaCultivationAssayLimits(int MaximumRuns, double MaximumReservedRemovalSeconds,
    double MinimumIntervalSeconds)
{
    public void Validate()
    {
        if (MaximumRuns < 1 || !double.IsFinite(MaximumReservedRemovalSeconds) || MaximumReservedRemovalSeconds <= 0 ||
            !double.IsFinite(MinimumIntervalSeconds) || MinimumIntervalSeconds < 0)
            throw new ArgumentException("Limites por cultivo inválidos.");
    }
}

/// <summary>Read-only budget lookup before reserving actuators or freezing the current condition.</summary>
public sealed record KlaAssayBudgetQuery(string CultivationId, KlaCultivationAssayLimits Limits,
    double ReservedRemovalSeconds, double MinimumIntervalSeconds)
{
    public void Validate()
    {
        ContractGuard.Text(CultivationId); ArgumentNullException.ThrowIfNull(Limits); Limits.Validate();
        ContractGuard.Positive(ReservedRemovalSeconds); ContractGuard.NonNegative(MinimumIntervalSeconds);
    }
}

/// <summary>One request is one pulse, never a matrix or an implicit retry. Current N/Q is frozen at creation.</summary>
public sealed record KlaAssayApiRequest(Guid RequestId, string CultivationId, KlaAssayDefinition Definition,
    KlaConditionSelection ConditionSelection, DateTimeOffset StartDeadlineUtc, DateTimeOffset DeadlineUtc,
    KlaCultivationAssayLimits Limits, KlaAssayFailurePolicy FailurePolicy = KlaAssayFailurePolicy.HaltRecipe)
{
    public KlaRecipePulseBinding? RecipePulse { get; init; }

    public void Validate()
    {
        if (RequestId == Guid.Empty || string.IsNullOrWhiteSpace(CultivationId) ||
            !Enum.IsDefined(ConditionSelection) || !Enum.IsDefined(FailurePolicy) || DeadlineUtc <= StartDeadlineUtc)
            throw new ArgumentException("Solicitação de ensaio inválida.");
        Definition.Validate();
        Limits.Validate();
        if (Definition.CaptureMode != KlaCaptureMode.Single || Definition.Conditions.Length != 1)
            throw new ArgumentException("A API executa uma condição e uma réplica por solicitação.");
        if (Definition.Context?.CultivationId?.Trim() is { Length: > 0 } cultivation &&
            !string.Equals(cultivation, CultivationId.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Contexto e solicitação devem identificar o mesmo cultivo.");
        if (!double.IsFinite(ReservedRemovalSeconds) || ReservedRemovalSeconds <= 0)
            throw new ArgumentException("O prazo de remoção precisa ser finito e positivo.");
        RecipePulse?.ValidateAgainst(this);
    }

    public double ReservedRemovalSeconds => (Definition.Protocol == KlaAssayProtocol.Biotic
        ? Definition.ProtocolSettings.AerationReturn.MaximumGasOffSeconds ?? Definition.Settings.MaxDegassingTimeMinutes * 60
        : Definition.Settings.MaxDegassingTimeMinutes * 60 + Definition.Settings.MaxPrestageSeconds)
        + 2 * Definition.ProtocolSettings.CommandConfirmationTimeoutSeconds;

    public static KlaAssayApiRequest AtCurrentCondition(KlaAssayApiRequest template, double rpm, double flow)
    {
        var condition = template.Definition.Conditions.Single();
        return template with { ConditionSelection = KlaConditionSelection.Current,
            Definition = template.Definition with { Conditions = ImmutableArray.Create(condition with { AgitationRpm = rpm, AirflowLpm = flow }) } };
    }
}

public sealed record KlaAssayApiResult(KlaRunOutcome Outcome, double? KlaPerHour,
    string? TestFolder = null, string? RunFolder = null, string? Reason = null)
{
    public ImmutableArray<string> ReasonCodes { get; init; } = [];
    public Guid? ReturnSnapshotId { get; init; }
    public string? PersistenceReceiptId { get; init; }
}

public sealed record KlaAssayApiObservation(KlaAssayApiRequest Request, KlaAssayApiState State,
    DateTimeOffset? StartedUtc = null, DateTimeOffset? CompletedUtc = null,
    KlaAssayApiResult? Result = null, string? Reason = null)
{
    public bool MayContinueRecipe => Result?.Outcome.Restoration == KlaRestorationState.Confirmed &&
        (Request.RecipePulse is null || Result.ReturnSnapshotId == Request.RecipePulse.Invocation.Restoration.BeforeAssay.SnapshotId &&
            !string.IsNullOrWhiteSpace(Result.PersistenceReceiptId)) &&
        (State == KlaAssayApiState.Completed && KlaRecipeQualityEvaluator.Accepts(Request, Result) ||
        (Request.FailurePolicy == KlaAssayFailurePolicy.ContinueAfterConfirmedReturn &&
         State is KlaAssayApiState.Inconclusive or KlaAssayApiState.Cancelled));
}

public static class KlaRecipeQualityEvaluator
{
    public static bool Accepts(KlaAssayApiRequest request, KlaAssayApiResult result)
    {
        if (result.KlaPerHour is not { } value || !double.IsFinite(value) || value <= 0) return false;
        var policy = request.RecipePulse?.Invocation.Quality;
        if (policy?.RequireValidOur == true && result.Outcome.OurQuality != KlaScientificQuality.Valid) return false;
        return result.Outcome.KlaQuality == KlaScientificQuality.Valid ||
            policy is not null && result.Outcome.KlaQuality == KlaScientificQuality.Conditional &&
            !result.ReasonCodes.IsDefaultOrEmpty && result.ReasonCodes.All(policy.AllowedConditionalReasonCodes.Contains);
    }
}

public interface IKlaAssayApi
{
    KlaCultivationAssayBudget ReadCultivationBudget(KlaAssayBudgetQuery query)
        => throw new NotSupportedException("Consulta prévia de orçamento não disponível.");
    KlaCultivationAssayBudget ReadCultivationBudget(KlaAssayApiRequest request)
        => throw new NotSupportedException("API sem leitura de orçamento persistido.");
    KlaAssayApiObservation Create(KlaAssayApiRequest request);
    Task<KlaAssayApiObservation> StartAsync(Guid requestId, CancellationToken ct = default);
    Task<KlaAssayApiObservation> WaitForCompletionAsync(Guid requestId, CancellationToken ct = default);
    KlaAssayApiObservation Observe(Guid requestId);
    Task<KlaAssayApiObservation> CancelWithRecoveryAsync(Guid requestId);
    KlaAssayApiResult? GetResult(Guid requestId);
    KlaAssayApiObservation ReconcileRecipeAttempt(Guid requestId, IKlaTestStore store, string testFolder, string runFolder)
        => throw new NotSupportedException("Este executor não suporta reconciliação persistida.");
}

/// <summary>
/// Adapter contract for E2. Must acquire the shared arbiter, run preflight, observe fresh oxygen,
/// and return only AFTER recovery succeeds/fails. Cancellation/deadline ends acquisition;
/// recovery uses its own bounded token. It never accepts a result on behalf of an operator.
/// No production adapter is registered until physical validation; the API cannot enable recipes itself.
/// </summary>
public interface IKlaAssayExecution
{
    bool IsValidated { get; }
    KlaAssayExecutionCapabilities? Capabilities => null;
    void EnsureAllows(KlaAssayApiRequest request) => Capabilities?.EnsureAllows(request);
    Task<KlaAssayApiResult> ExecuteWithRecoveryAsync(KlaAssayApiRequest request, CancellationToken acquisitionCancellation);
}

/// <summary>One latest due occurrence; old occurrences are skipped, never queued as a burst.</summary>
public sealed record KlaPeriodicOccurrence(DateTimeOffset? DueUtc, DateTimeOffset NextDueUtc, long SkippedOccurrences);

public static class KlaPeriodicSchedule
{
    public static KlaPeriodicOccurrence Latest(DateTimeOffset nextDueUtc, TimeSpan interval, DateTimeOffset now)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentException("Intervalo periódico deve ser positivo.");
        if (now < nextDueUtc) return new(null, nextDueUtc, 0);
        var skipped = (now - nextDueUtc).Ticks / interval.Ticks;
        var due = nextDueUtc.AddTicks(checked(skipped * interval.Ticks));
        return new(due, due.Add(interval), skipped);
    }
}
