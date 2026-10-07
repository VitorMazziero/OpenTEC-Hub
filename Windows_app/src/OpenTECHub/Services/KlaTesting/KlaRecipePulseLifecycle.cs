using OpenTECHub.Services.Recipes;

namespace OpenTECHub.Services.KlaTesting;

public sealed record KlaRecipePulseLifecycleResult(KlaRecipeAcquisitionResult Acquisition,
    RecipeAssayRecoveryResult Recovery, string? RecoveryRecordingError = null);

/// <summary>Runs acquisition then mandatory bounded recovery; it never releases resources or selects a result.</summary>
public sealed class KlaRecipePulseLifecycle(KlaTestRunner runner, RecipeAssayResourceLease lease,
    RecipeAssayRestoration restoration, TimeProvider time)
{
    private int _started;
    public async Task<KlaRecipePulseLifecycleResult> ExecuteAsync(KlaTestDocument document, KlaTestCondition condition,
        int replicateNumber, KlaRecipeRestorationContract contract, RecipeAssayRecoveryCriteria criteria,
        CancellationToken acquisitionCancellation, Func<CancellationToken, Task>? beforeRun = null)
    {
        if (!ReferenceEquals(runner.RecipeAuthorityLease, lease))
            throw new ArgumentException("Runner e recuperação devem usar a mesma reserva.");
        contract.Validate(); criteria.Validate(); lease.ValidateRecoverySnapshot(contract.BeforeAssay);
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("Ciclo de pulso já executado.");
        KlaRecipeAcquisitionResult acquisition;
        try
        {
            acquisition = await new KlaRecipeAcquisition(runner, beforeRun).ExecuteAsync(document, condition, replicateNumber,
                acquisitionCancellation).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            acquisition = new(KlaRecipeAcquisitionState.Failed, runner.Phase, ex.Message);
            // Acquisition seals enqueues before any persistence operation that might throw.
        }
        RecipeAssayRecoveryResult recovery;
        try
        {
            // Cancellation belongs to acquisition. Restoration owns its independent deadline.
            recovery = await restoration.RestoreAsync(lease, contract, criteria, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            recovery = new(contract.BeforeAssay.SnapshotId, KlaRestorationState.Failed, time.GetUtcNow(),
                !lease.IsAssayAuthorityCurrent, ex.Message, null);
        }
        string? recordingError = null;
        try
        {
            if (runner.CurrentRun is not null) runner.RecordRecipeRecovery(recovery);
        }
        catch (Exception ex)
        {
            // Preserve physical knowledge even when its recording fails; no ownership is returned here.
            recordingError = ex.Message;
        }
        finally
        {
            if (recovery.Restoration != KlaRestorationState.Confirmed) lease.Fail();
        }
        return new(acquisition, recovery, recordingError);
    }
}
