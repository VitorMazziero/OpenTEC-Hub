namespace OpenTECHub.Services.Recipes;

// Link hold (D-065): while the PC–Hub link is down the recipe keeps its ownership, starts no new block,
// freezes ramp time and lets its cascade rebase once the link returns. The Hub keeps the last commands.
public sealed partial class RecipeEngine
{
    private RecipeDeviceWait? _linkWait;
    private TaskCompletionSource _linkRestored = CompletedLinkSignal();

    /// <summary>True while the recipe waits for the PC–Hub link with its ownership held.</summary>
    public bool IsLinkHeld => _arbiter.IsLinkHeld;

    private static TaskCompletionSource CompletedLinkSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    private void OnLinkHoldChanged(bool held)
    {
        lock (_lock)
        {
            if (held && _linkRestored.Task.IsCompleted) _linkRestored = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!held) _linkRestored.TrySetResult();
        }
        if (State is RecipeRunState.Running or RecipeRunState.Paused)
        {
            if (held && Waiting is null)
            {
                _linkWait = new("enlace", "Hub", "Enlace PC–Hub perdido; a receita aguarda a reconexão e o Hub mantém os últimos comandos.",
                    _time.GetUtcNow());
                SetWaiting(_linkWait);
                Log(RecipeLogSeverity.Warning, "Enlace com o Hub perdido: receita mantida, aguardando reconexão.");
            }
            else if (!held)
            {
                if (_linkWait is not null && ReferenceEquals(Waiting, _linkWait)) SetWaiting(null);
                _linkWait = null;
                Log(RecipeLogSeverity.Info, "Enlace com o Hub restabelecido: receita retomada.");
            }
        }
        StateChanged?.Invoke(); // ramp clocks observe the hold through the run state
    }

    /// <summary>Completes immediately while the link is up; otherwise when it returns or the run is cancelled.</summary>
    private Task WaitForLinkAsync(CancellationToken ct)
    {
        Task signal;
        lock (_lock) signal = _linkRestored.Task;
        return _arbiter.IsLinkHeld ? signal.WaitAsync(ct) : Task.CompletedTask;
    }
}
