namespace OpenTECHub.Services.Recipes;

/// <summary>
/// Prevents a recipe cascade step from racing an assay handoff. A step lease covers both
/// controller.Update and command dispatch. Pause returns only when every lease has ended.
/// This gate does not transfer actuator ownership or claim physical restoration.
/// </summary>
public sealed class RecipeCascadeSuspensionGate(Func<long>? latestObservationVersion = null) : IDisposable
{
    private readonly object _sync = new();
    private int _activeSteps;
    private bool _paused;
    private bool _stopped;
    private TaskCompletionSource? _quiesced;
    private TaskCompletionSource? _resumed;
    private PauseReceipt? _receipt;
    private long _resumeVersion;
    private long _ignoreFramesThrough = -1;
    private readonly CancellationTokenSource _stop = new();
    public CancellationToken StopToken => _stop.Token;

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
            return new StepLease(this, _resumeVersion, _ignoreFramesThrough);
        }
    }

    /// <summary>Seals the step boundary and waits for an in-flight dispatch to finish.</summary>
    public async Task<PauseReceipt> PauseAsync(CancellationToken cancellationToken = default)
    {
        Task pending;
        PauseReceipt receipt;
        lock (_sync)
        {
            if (_stopped) throw new InvalidOperationException("A cascata já foi encerrada.");
            if (_paused) throw new InvalidOperationException("A cascata já está suspensa.");
            _paused = true;
            receipt = _receipt = new PauseReceipt(this);
            _resumed = NewSignal();
            _quiesced = _activeSteps == 0 ? null : NewSignal();
            pending = _quiesced?.Task ?? Task.CompletedTask;
        }

        try
        {
            await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                if (_stopped) throw new OperationCanceledException("A cascata foi encerrada.");
                receipt.Ready = true;
            }
            return receipt;
        }
        catch (OperationCanceledException)
        {
            // No ownership transfer took place; release this reservation on timeout/cancellation.
            Unpause(receipt, requireQuiescence: false);
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
    private void Unpause(PauseReceipt receipt, bool requireQuiescence)
    {
        TaskCompletionSource? signal;
        lock (_sync)
        {
            if (_stopped || !_paused) return;
            if (!ReferenceEquals(_receipt, receipt) || requireQuiescence && (!receipt.Ready || _activeSteps != 0))
                throw new InvalidOperationException("Recibo de pausa antigo ou passo ainda ativo.");
            _paused = false;
            _resumeVersion = checked(_resumeVersion + 1);
            _ignoreFramesThrough = latestObservationVersion?.Invoke() ?? -1;
            _receipt = null;
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
        _stop.Cancel();
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
        public long ResumeVersion { get; }
        public long IgnoreFramesThrough { get; }
        internal StepLease(RecipeCascadeSuspensionGate gate, long resumeVersion, long ignoreFramesThrough)
        { _gate = gate; ResumeVersion = resumeVersion; IgnoreFramesThrough = ignoreFramesThrough; }
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.ExitStep();
    }

    public sealed class PauseReceipt : IRecipeResourceSuspension
    {
        private readonly RecipeCascadeSuspensionGate _gate;
        internal bool Ready;
        internal PauseReceipt(RecipeCascadeSuspensionGate gate) => _gate = gate;
        public bool CanResume
        {
            get { lock (_gate._sync) return !_gate._stopped && _gate._paused && ReferenceEquals(_gate._receipt, this) && Ready; }
        }
        public void Resume() => _gate.Unpause(this, requireQuiescence: true);
        public void Stop() => _gate.Stop();
    }

    public void Dispose() { Stop(); _stop.Dispose(); }
}
