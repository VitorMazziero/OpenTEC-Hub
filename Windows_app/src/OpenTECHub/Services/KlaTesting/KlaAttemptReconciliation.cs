using System.IO;
using System.Text.Json;

namespace OpenTECHub.Services.KlaTesting;

public enum KlaAttemptReconciliationState
{
    NotPrepared, PreparationOnly, TerminalPersisted
}

/// <summary>Historical persistence evidence; never authorizes another pulse or physical restart.</summary>
public sealed record KlaAttemptReconciliationResult(KlaAttemptReconciliationState State,
    bool ChargeReservedBudget, bool RequiresRecoveryVerification, KlaAssayApiResult? PersistedResult);

public static class KlaAttemptReconciliation
{
    public static KlaAttemptReconciliationResult Read(IKlaTestStore store, KlaAssayApiObservation observation,
        string testFolder, string runFolder)
    {
        var requestId = observation.Request.RequestId;
        var before = store.ReadRecipeAttemptCheckpoint(testFolder, runFolder, requestId, KlaAttemptPersistencePhase.BeforeActuation);
        var terminal = store.ReadRecipeAttemptCheckpoint(testFolder, runFolder, requestId, KlaAttemptPersistencePhase.Terminal);
        var receipt = store.ReadRecipeAttemptReceipt(testFolder, runFolder, requestId, KlaAttemptPersistencePhase.Terminal);
        if (terminal is not null && (before is null || receipt is null))
            throw new InvalidDataException("Resultado terminal sem preparação e recibo correspondentes.");
        foreach (var checkpoint in new[] { before, terminal })
        {
            if (checkpoint is null) continue;
            checkpoint.Validate();
            if (JsonSerializer.Serialize(checkpoint.Request) != JsonSerializer.Serialize(observation.Request))
                throw new InvalidDataException("Diário E6 e sessão comum possuem requests diferentes.");
        }
        if (before is not null && terminal is not null &&
            (before.TestId != terminal.TestId || before.RunId != terminal.RunId ||
             before.Authority.ReservationId != terminal.Authority.ReservationId))
            throw new InvalidDataException("Identidades de preparação e conclusão divergem.");
        if (terminal is not null)
        {
            var result = terminal.Result! with { PersistenceReceiptId = receipt!.ReceiptId };
            if (observation.Result is { } journalResult &&
                JsonSerializer.Serialize(journalResult with { PersistenceReceiptId = null }) !=
                JsonSerializer.Serialize(terminal.Result))
                throw new InvalidDataException("Resultado do diário E6 diverge da sessão comum.");
            return new(KlaAttemptReconciliationState.TerminalPersisted, true,
                observation.State is KlaAssayApiState.Interrupted or KlaAssayApiState.Running or KlaAssayApiState.PersistenceFailed ||
                result.Outcome.Restoration != KlaRestorationState.Confirmed, result);
        }
        return new(before is null ? KlaAttemptReconciliationState.NotPrepared : KlaAttemptReconciliationState.PreparationOnly,
            before is not null || observation.StartedUtc.HasValue, before is not null || observation.StartedUtc.HasValue, null);
    }
}
