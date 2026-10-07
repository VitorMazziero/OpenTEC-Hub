namespace OpenTECHub.Services.Recipes;

/// <summary>
/// Prevents a recipe cascade step from racing an assay handoff. A step lease covers both
/// controller.Update and command dispatch. Pause returns only when every lease has ended.
/// This gate does not transfer actuator ownership or claim physical restoration.
/// </summary>
public sealed class RecipeCascadeSuspensionGate
{
    private readonly object _sync = new();
    private int _activeSteps;
    private bool _paused;
    private bool _stopped;
    private TaskCompletionSource? _quiesced;
    private TaskCompletionSource? _resumed;

    public bool IsPaused
    {
        get { lock (_sync) return _paused; }
    }

    /// <summary>Returns null while paused. Dispose the lease after the whole command dispatch.</summary>
    public StepLease? TryEnterStep()
    {
        lock (_sync)
        {
            if (_paused || _stopped) return null;
            _activeSteps++;
            return new StepLease(this);
        }
    }

    /// <summary>Seals the step boundary and waits for an in-flight dispatch to finish.</summary>
    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        Task pending;
        lock (_sync)
        {
            if (_stopped) throw new InvalidOperationException("A cascata já foi encerrada.");
            if (_paused) throw new InvalidOperationException("A cascata já está suspensa.");
            _paused = true;
            _resumed = NewSignal();
            _quiesced = _activeSteps == 0 ? null : NewSignal();
            pending = _quiesced?.Task ?? Task.CompletedTask;
        }

        try
        {
            await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // No ownership transfer took place; release this reservation on timeout/cancellation.
            Resume();
            throw;
        }
    }

    /// <summary>Used by the cascade loop while an assay holds its actuators.</summary>
    public Task WaitUntilResumedAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_stopped) throw new OperationCanceledException("A cascata foi encerrada.");
            return (_resumed?.Task ?? Task.CompletedTask).WaitAsync(cancellationToken);
        }
    }

    /// <summary>Called only after physical restoration and ownership return have been confirmed.</summary>
    public void Resume()
    {
        TaskCompletionSource? signal;
        lock (_sync)
        {
            if (_stopped || !_paused) return;
            _paused = false;
            signal = _resumed;
            _resumed = null;
            _quiesced = null;
        }
        signal?.TrySetResult();
    }

    /// <summary>Terminal emergency/fault path: a late completion cannot restart the cascade.</summary>
    public void Stop()
    {
        TaskCompletionSource? resumeSignal;
        TaskCompletionSource? pauseSignal;
        lock (_sync)
        {
            if (_stopped) return;
            _stopped = true;
            _paused = true;
            resumeSignal = _resumed;
            pauseSignal = _quiesced;
            _resumed = null;
            _quiesced = null;
        }
        resumeSignal?.TrySetCanceled();
        pauseSignal?.TrySetCanceled();
    }

    private void ExitStep()
    {
        TaskCompletionSource? signal = null;
        lock (_sync)
        {
            _activeSteps--;
            if (_activeSteps < 0) throw new InvalidOperationException("Lease de cascata duplicada.");
            if (_activeSteps == 0 && _paused) signal = _quiesced;
        }
        signal?.TrySetResult();
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public sealed class StepLease : IDisposable
    {
        private RecipeCascadeSuspensionGate? _gate;
        internal StepLease(RecipeCascadeSuspensionGate gate) => _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.ExitStep();
    }
}
