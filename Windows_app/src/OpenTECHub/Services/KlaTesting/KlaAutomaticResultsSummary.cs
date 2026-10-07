using System.Globalization;
using System.Text;

namespace OpenTECHub.Services.KlaTesting;

/// <summary>One row per attempt, retaining rejected/incomplete attempts and both scientific outcomes.</summary>
public static class KlaAutomaticResultsSummary
{
    public const string FileName = "resumo-receita-tentativas.csv";
    public static string Format(KlaTestDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var request = document.RecipeRequest;
        request?.Validate();
        var text = new StringBuilder("recipe_run_id,invocation_id,node_id,cultivation,profile_id,profile_version,protocol,periodic_slot,attempt_id,condition_id,replicate,attempt,agitation_rpm,airflow_lpm,decision,author,kla_quality,kla_per_hour,ci95_low_per_hour,ci95_high_per_hour,our_quality,our_pp_per_hour,our_mmol_per_l_per_hour,restoration,return_snapshot_id,persistence_confirmed,decided_utc,reason_codes,run_folder\n");
        foreach (var run in document.Runs.OrderBy(r => r.StartedUtc).ThenBy(r => r.AttemptNumber))
        {
            var decision = run.AutomaticDecision;
            decision?.Validate();
            if (decision is not null && (decision.ConditionId != run.ConditionId || decision.ReplicateNumber != run.ReplicateNumber ||
                decision.AttemptNumber != run.AttemptNumber || decision.RunFolder != run.FolderName))
                throw new ArgumentException("Decisão não corresponde à tentativa apresentada.");
            text.AppendLine(string.Join(',', new[]
            {
                Cell(request?.Context.RecipeRunId), Cell(request?.Context.InvocationId), Cell(request?.Context.NodeId),
                Cell(request?.Context.CultivationId ?? document.Context?.CultivationId), Cell(request?.Quality.ProfileId),
                Cell(request?.Quality.Version ?? decision?.PolicyVersion), Cell(document.EffectiveProtocol),
                Cell(request?.PeriodicInvocation?.SlotIndex), Cell(decision?.AttemptId), Cell(run.ConditionId),
                Cell(run.ReplicateNumber), Cell(run.AttemptNumber), Cell(run.AgitationRpm), Cell(run.AirflowLpm),
                Cell(decision?.Decision.ToString() ?? "Pending"), Cell(decision?.DecisionAuthor),
                Cell(decision?.KlaQuality ?? run.EffectiveOutcome.KlaQuality), Cell(decision is null ? run.KlaPerHour : decision.KlaPerHour),
                Cell(decision?.ConditionalCi95LowPerHour), Cell(decision?.ConditionalCi95HighPerHour),
                Cell(document.EffectiveProtocol == KlaAssayProtocol.Abiotic ? KlaScientificQuality.NotApplicable : decision?.OurQuality ?? run.EffectiveOutcome.OurQuality),
                Cell(decision?.OurPercentPointsPerHour), Cell(decision?.OurMmolPerLPerHour),
                Cell(decision?.Restoration ?? run.EffectiveOutcome.Restoration), Cell(decision?.ReturnSnapshotId),
                Cell(decision?.PersistenceConfirmed), Cell(decision?.DecidedUtc), Cell(string.Join('|', decision?.ReasonCodes ?? [])), Cell(run.FolderName)
            }));
        }
        return text.ToString();
    }

    private static string Cell(object? value)
    {
        if (value is null) return "";
        if (value is DateTimeOffset date) return date.ToString("O", CultureInfo.InvariantCulture);
        if (value is double number) return number.ToString("R", CultureInfo.InvariantCulture);
        if (value is int or long) return Convert.ToString(value, CultureInfo.InvariantCulture)!;
        if (value is bool flag) return flag ? "true" : "false";
        var text = value.ToString()!;
        if (value is string && text.TrimStart() is { Length: > 0 } trimmed && "=+-@".Contains(trimmed[0])) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
