using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenTECHub.Services.KlaTesting;

public enum KlaMeasurementSource { Unknown, Physical, Simulation }

/// <summary>Explicit grouping context. Acquisition dates remain on each run, not in a mean N/Q anchor.</summary>
public sealed record KlaMeasurementContext
{
    public string Medium { get; init; } = "";
    public string CultivationId { get; init; } = "";
    public string TimeWindow { get; init; } = "";
    public KlaMeasurementSource Source { get; init; } = KlaMeasurementSource.Unknown;

    public bool IsDefinedFor(KlaAssayProtocol protocol) => !string.IsNullOrWhiteSpace(Medium) &&
        Enum.IsDefined(Source) && Source != KlaMeasurementSource.Unknown &&
        (protocol == KlaAssayProtocol.Abiotic || (!string.IsNullOrWhiteSpace(CultivationId) && !string.IsNullOrWhiteSpace(TimeWindow)));

    public bool CompatibleWith(KlaMeasurementContext other) => Source == other.Source &&
        Same(Medium, other.Medium) && Same(CultivationId, other.CultivationId) && Same(TimeWindow, other.TimeWindow);

    private static bool Same(string a, string b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}

public sealed record KlaSequenceLimits
{
    public int MaximumAttemptsPerReplicate { get; init; } = 3;
    public int MaximumRuns { get; init; } = 100;
    public double MaximumRemovalSeconds { get; init; } = 3600;
    public void Validate()
    {
        if (MaximumAttemptsPerReplicate < 1 || MaximumRuns < 1 ||
            !double.IsFinite(MaximumRemovalSeconds) || MaximumRemovalSeconds <= 0)
            throw new ArgumentException("Limites da fila devem ser positivos e finitos.");
    }
}

public sealed record KlaQueueItem(Guid ConditionId, int ReplicateNumber, int AttemptNumber);
public sealed record KlaQueueReadiness(bool CanStart, string Reason, double WaitSeconds = 0);

/// <summary>Scheduling never acts on hardware. One replicate can have multiple immutable attempts.</summary>
public static class KlaSequence
{
    public static bool IsAccepted(KlaTestRunSummary run) => run.Phase == RunPhase.Accepted &&
        run.EffectiveOutcome.OperatorDecision == KlaOperatorDecision.Accepted &&
        run.Decision is DecisionQuality.Acceptable or DecisionQuality.AcceptableWithWarning;

    public static IReadOnlyList<KlaQueueItem> Pending(KlaTestDocument doc, IEnumerable<KlaTestCondition> conditions)
        => conditions.OrderBy(c => c.OrderIndex).SelectMany(c => Enumerable.Range(1, c.RequestedReplicates)
            .Where(rep => !doc.Runs.Any(r => r.ConditionId == c.ConditionId && r.ReplicateNumber == rep && IsAccepted(r)))
            .Select(rep => new KlaQueueItem(c.ConditionId, rep,
                doc.Runs.Count(r => r.ConditionId == c.ConditionId && r.ReplicateNumber == rep) + 1))).ToArray();

    public static double RemovalExposure(IEnumerable<KlaRawDataPoint> points)
    {
        var samples = points.ToArray();
        double seconds = 0;
        for (var i = 0; i + 1 < samples.Length; i++)
            if (samples[i].Phase is RunPhase.DivertingAir or RunPhase.MeasuringConsumption or RunPhase.OpeningNitrogen or
                RunPhase.Deoxygenating or RunPhase.PrestagingAir or RunPhase.SwitchingToReactor)
                seconds += Math.Max(0, samples[i + 1].RelativeSeconds - samples[i].RelativeSeconds);
        return seconds;
    }

    public static KlaQueueReadiness Check(KlaTestDocument doc, KlaQueueItem item, DateTimeOffset now)
    {
        var limits = doc.SequenceLimits ?? new();
        limits.Validate();
        if (item.ReplicateNumber < 1 || item.AttemptNumber < 1)
            return new(false, "Réplica ou tentativa inválida.");
        var condition = doc.Conditions.FirstOrDefault(c => c.ConditionId == item.ConditionId);
        if (condition is null || item.ReplicateNumber > condition.RequestedReplicates)
            return new(false, "A réplica não pertence à fila planejada.");
        if (doc.Runs.Any(r => r.Phase is not (RunPhase.Accepted or RunPhase.Rejected or RunPhase.Completed or RunPhase.Faulted) &&
            r.EffectiveOutcome.OperatorDecision == KlaOperatorDecision.Pending))
            return new(false, "Revise e finalize a corrida anterior antes de continuar a fila.");
        if (doc.Runs.Any(r => r.EffectiveOutcome.Restoration is KlaRestorationState.Pending or KlaRestorationState.Failed))
            return new(false, "Retorno físico pendente ou falhou; a fila permanece bloqueada.");
        if (doc.EffectiveProtocol == KlaAssayProtocol.Biotic && doc.Runs.Any(r => r.EffectiveOutcome.Restoration != KlaRestorationState.Confirmed))
            return new(false, "O retorno de todas as corridas bióticas precisa estar confirmado.");
        if (doc.Runs.Count >= limits.MaximumRuns)
            return new(false, "Limite de corridas desta sessão atingido.");
        if (doc.Runs.Any(r => r.RemovalSeconds is not { } seconds || !double.IsFinite(seconds) || seconds < 0))
            return new(false, "Exposição anterior desconhecida; verifique o histórico antes de continuar.");
        var attempts = doc.Runs.Count(r => r.ConditionId == item.ConditionId && r.ReplicateNumber == item.ReplicateNumber);
        if (attempts >= limits.MaximumAttemptsPerReplicate || item.AttemptNumber > limits.MaximumAttemptsPerReplicate)
            return new(false, "Limite de tentativas desta réplica atingido.");
        var reserved = doc.EffectiveProtocol == KlaAssayProtocol.Biotic
            ? doc.ProtocolSettings?.AerationReturn.MaximumGasOffSeconds ?? doc.Settings.MaxDegassingTimeMinutes * 60
            : doc.Settings.MaxDegassingTimeMinutes * 60 + doc.Settings.MaxPrestageSeconds;
        reserved += 2 * (doc.ProtocolSettings?.CommandConfirmationTimeoutSeconds ?? 10);
        if (doc.Runs.Sum(r => r.RemovalSeconds ?? 0) + reserved > limits.MaximumRemovalSeconds)
            return new(false, "Limite acumulado de remoção/consumo atingido.");
        var interval = doc.ProtocolSettings?.AerationReturn.MinimumInterAssaySeconds ?? 0;
        var end = doc.Runs.Where(r => r.CompletedUtc.HasValue).Select(r => r.CompletedUtc!.Value).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
        var remaining = end == DateTimeOffset.MinValue ? 0 : interval - (now - end).TotalSeconds;
        return remaining > 0 ? new(false, "Aguarde o intervalo mínimo entre ensaios.", remaining) : new(true, "Pronto para a próxima corrida.");
    }

    public static void RefreshCounters(KlaTestDocument doc, KlaTestCondition condition)
    {
        var runs = doc.Runs.Where(r => r.ConditionId == condition.ConditionId).ToArray();
        condition.CompletedReplicates = runs.Where(r => r.Phase is RunPhase.Accepted or RunPhase.Rejected)
            .Select(r => r.ReplicateNumber).Distinct().Count();
        condition.AcceptedReplicates = runs.Where(IsAccepted).Select(r => r.ReplicateNumber).Distinct().Count();
        condition.RejectedReplicates = runs.Where(r => r.Phase == RunPhase.Rejected &&
            !runs.Any(a => a.ReplicateNumber == r.ReplicateNumber && IsAccepted(a)))
            .Select(r => r.ReplicateNumber).Distinct().Count();
        condition.Status = condition.AcceptedReplicates >= condition.RequestedReplicates ? ConditionStatus.Completed : ConditionStatus.Pending;
    }
}
