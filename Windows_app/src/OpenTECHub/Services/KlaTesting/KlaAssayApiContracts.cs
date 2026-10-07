using System.Collections.Immutable;

namespace OpenTECHub.Services.KlaTesting;

public enum KlaAssayApiState { Created, Running, Completed, Inconclusive, Cancelled, RestorationFailed, Skipped, Interrupted }
public enum KlaAssayFailurePolicy { HaltRecipe, ContinueAfterConfirmedReturn }
public enum KlaConditionSelection { Explicit, Current }

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

/// <summary>One request is one pulse, never a matrix or an implicit retry. Current N/Q is frozen at creation.</summary>
public sealed record KlaAssayApiRequest(Guid RequestId, string CultivationId, KlaAssayDefinition Definition,
    KlaConditionSelection ConditionSelection, DateTimeOffset StartDeadlineUtc, DateTimeOffset DeadlineUtc,
    KlaCultivationAssayLimits Limits, KlaAssayFailurePolicy FailurePolicy = KlaAssayFailurePolicy.HaltRecipe)
{
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
    string? TestFolder = null, string? RunFolder = null, string? Reason = null);

public sealed record KlaAssayApiObservation(KlaAssayApiRequest Request, KlaAssayApiState State,
    DateTimeOffset? StartedUtc = null, DateTimeOffset? CompletedUtc = null,
    KlaAssayApiResult? Result = null, string? Reason = null)
{
    public bool MayContinueRecipe => State == KlaAssayApiState.Completed ||
        (Request.FailurePolicy == KlaAssayFailurePolicy.ContinueAfterConfirmedReturn &&
         State is KlaAssayApiState.Inconclusive or KlaAssayApiState.Cancelled &&
         Result?.Outcome.Restoration is KlaRestorationState.Confirmed or KlaRestorationState.NotRequired);
}

public interface IKlaAssayApi
{
    KlaAssayApiObservation Create(KlaAssayApiRequest request);
    Task<KlaAssayApiObservation> StartAsync(Guid requestId, CancellationToken ct = default);
    KlaAssayApiObservation Observe(Guid requestId);
    Task<KlaAssayApiObservation> CancelWithRecoveryAsync(Guid requestId);
    KlaAssayApiResult? GetResult(Guid requestId);
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
