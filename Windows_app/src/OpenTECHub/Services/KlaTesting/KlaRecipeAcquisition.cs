namespace OpenTECHub.Services.KlaTesting;

public enum KlaRecipeAcquisitionState { Completed, Cancelled, Failed }

public sealed record KlaRecipeAcquisitionResult(KlaRecipeAcquisitionState State, RunPhase AcquisitionPhase,
    string? Reason = null);

/// <summary>Observes the common runner without human acceptance; recovery remains an independent operation.</summary>
public sealed class KlaRecipeAcquisition
{
    private readonly KlaTestRunner _runner;
    private int _started;
    private readonly Func<CancellationToken, Task>? _beforeRun;
    public KlaRecipeAcquisition(KlaTestRunner runner, Func<CancellationToken, Task>? beforeRun = null)
    {
        if (!runner.UsesRecipeAuthority) throw new ArgumentException("Aquisição autônoma requer autoridade de receita.");
        _runner = runner;
        _beforeRun = beforeRun;
    }

    public async Task<KlaRecipeAcquisitionResult> ExecuteAsync(KlaTestDocument document, KlaTestCondition condition,
        int replicateNumber, CancellationToken acquisitionCancellation)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("Objeto de aquisição já executado; tentativa não pode ser repetida.");
        var terminal = new TaskCompletionSource<KlaRecipeAcquisitionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe()
        {
            var phase = _runner.Phase;
            if (phase is RunPhase.Reviewing or RunPhase.Completed)
                terminal.TrySetResult(new(KlaRecipeAcquisitionState.Completed, phase));
            else if (phase is RunPhase.Faulted or RunPhase.Accepted or RunPhase.Rejected)
                terminal.TrySetResult(new(KlaRecipeAcquisitionState.Failed, phase, _runner.StatusMessage));
        }
        _runner.StateChanged += Observe;
        try
        {
            await _runner.StartTestAsync(document, acquisitionCancellation).ConfigureAwait(false);
            if (_beforeRun is not null) await _beforeRun(acquisitionCancellation).ConfigureAwait(false);
            await _runner.StartRunAsync(condition, replicateNumber, acquisitionCancellation).ConfigureAwait(false);
            Observe();
            return await terminal.Task.WaitAsync(acquisitionCancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (acquisitionCancellation.IsCancellationRequested)
        {
            return new(KlaRecipeAcquisitionState.Cancelled, _runner.Phase, "Aquisição cancelada; recuperação independente obrigatória.");
        }
        catch (Exception ex)
        {
            return new(KlaRecipeAcquisitionState.Failed, _runner.Phase, ex.Message);
        }
        finally
        {
            _runner.StateChanged -= Observe;
            _runner.SealRecipeAcquisitionForRecovery();
        }
    }
}
