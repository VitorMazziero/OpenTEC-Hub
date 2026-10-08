namespace OpenTECHub.Services.Recipes;

public sealed partial class RecipeEngine
{
    private readonly Dictionary<Guid, RampLifecycle> _rampLifecycles = [];
    private readonly HashSet<string> _closingRampCascades = [];

    internal RampLifecycle TrackRampLifecycle(string? cascadeNodeId)
    {
        lock (_lock)
        {
            if (cascadeNodeId is not null && _closingRampCascades.Contains(cascadeNodeId))
                throw new InvalidOperationException("Controle de O₂ está encerrando; não aceita nova rampa.");
            var id = Guid.NewGuid();
            var lifecycle = new RampLifecycle(cascadeNodeId, () =>
            {
                lock (_lock) _rampLifecycles.Remove(id);
            });
            _rampLifecycles.Add(id, lifecycle);
            return lifecycle;
        }
    }

    private async Task CloseCascadeRampsAsync(string cascadeNodeId)
    {
        RampLifecycle[] active;
        lock (_lock)
        {
            _closingRampCascades.Add(cascadeNodeId);
            active = _rampLifecycles.Values.Where(item => item.CascadeNodeId == cascadeNodeId).ToArray();
        }
        foreach (var lifecycle in active) lifecycle.RequestStop();
        // Keep the controller and its producer available for independently bounded recovery.
        await Task.WhenAll(active.Select(item => item.Completion)).ConfigureAwait(false);
    }

    internal sealed class RampLifecycle(string? cascadeNodeId, Action unregister) : IDisposable
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _disposed;
        public string? CascadeNodeId { get; } = cascadeNodeId;
        public CancellationToken StopToken => _stop.Token;
        public Task Completion => _completion.Task;
        public void RequestStop()
        {
            lock (_sync)
                if (!_disposed) _stop.Cancel();
        }
        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                _stop.Dispose();
            }
            unregister();
            _completion.TrySetResult();
        }
    }
}
