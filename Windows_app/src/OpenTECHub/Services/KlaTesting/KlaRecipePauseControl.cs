namespace OpenTECHub.Services.KlaTesting;

/// <summary>Invocation-local pause epochs. Resume opens future dispatches; it never revives an old epoch.</summary>
public sealed class KlaRecipePauseControl : IDisposable
{
    private readonly object _gate = new();
    private readonly List<CancellationTokenSource> _epochs = [];
    private CancellationTokenSource _epoch = new();
    private TaskCompletionSource _resumed = CompletedGate();
    private bool _paused, _disposed;
    private int _cancelling;
    private CancellationTokenSource[]? _deferredDisposal;

    public bool IsPaused { get { lock (_gate) return _paused; } }

    public void Pause()
    {
        CancellationTokenSource epoch;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_paused) return;
            _paused = true;
            _resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            epoch = _epoch;
            _cancelling++;
        }
        // Callbacks may acquire resources or inspect this controller: never invoke them under its lock.
        try { epoch.Cancel(); }
        finally
        {
            CancellationTokenSource[]? dispose = null;
            lock (_gate)
            {
                if (--_cancelling == 0)
                {
                    dispose = _deferredDisposal;
                    _deferredDisposal = null;
                }
            }
            if (dispose is not null) foreach (var source in dispose) source.Dispose();
        }
    }

    public void Resume()
    {
        TaskCompletionSource resumed;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_paused) return;
            _epochs.Add(_epoch);
            _epoch = new();
            _paused = false;
            resumed = _resumed;
        }
        resumed.TrySetResult();
    }

    public Task WaitUntilResumedAsync(CancellationToken cancellation)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _resumed.Task.WaitAsync(cancellation);
        }
    }

    /// <summary>Serializes synchronous create/start dispatch against pause. The token remains valid until disposal.</summary>
    public bool TryDispatch(Action<CancellationToken> dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_paused) return false;
            dispatch(_epoch.Token);
            return true;
        }
    }

    public void Dispose()
    {
        CancellationTokenSource[] epochs;
        TaskCompletionSource resumed;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            epochs = [.. _epochs, _epoch];
            if (_cancelling > 0)
            {
                _deferredDisposal = epochs;
                epochs = [];
            }
            resumed = _resumed;
        }
        resumed.TrySetCanceled();
        foreach (var epoch in epochs) epoch.Dispose();
    }

    private static TaskCompletionSource CompletedGate()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.SetResult();
        return gate;
    }
}
