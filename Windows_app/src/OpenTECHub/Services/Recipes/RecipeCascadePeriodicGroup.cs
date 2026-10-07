namespace OpenTECHub.Services.Recipes;

/// <summary>Resolved recipe work; editor parsing and assay construction are supplied by the host.</summary>
public sealed record RecipePeriodicWork(PeriodicBlockInvocation Identity, TimeSpan DispatchTolerance,
    Func<PeriodicBlockInvocation, CancellationToken, Task> Execute,
    Func<RecipePeriodicSlotRecord, Task> Record);

public interface IRecipePeriodicWorkSource
{
    IReadOnlyList<RecipePeriodicWork> CreateWork(RecipeDocument recipe, Guid executionId,
        RecipeResourceCoordinator? resources);
}

/// <summary>Stops followers while the cascade's producer and captured controller still exist.</summary>
public sealed class RecipeCascadePeriodicGroup : IDisposable
{
    private readonly CancellationTokenSource _stop;
    private readonly RecipePeriodicExecutor[] _schedulers;
    private readonly IReadOnlyList<RecipePeriodicWork> _work;
    private readonly TaskCompletionSource[] _completions;
    private readonly bool[] _entered;
    private readonly object _gate = new();
    private readonly bool _deferred;
    private bool _cascadeReady;
    private bool _stopped;
    public CancellationToken StopToken => _stop.Token;

    public RecipeCascadePeriodicGroup(IReadOnlyList<RecipePeriodicWork> work, TimeProvider time,
        CancellationToken cancellation, bool paused, bool startAtSchedulerEntry = false)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        _work = work.ToArray();
        _deferred = startAtSchedulerEntry;
        _cascadeReady = !startAtSchedulerEntry;
        _schedulers = work.Select(_ => new RecipePeriodicExecutor(time)).ToArray();
        _completions = work.Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        _entered = new bool[work.Count];
        foreach (var scheduler in _schedulers) scheduler.SetPaused(paused);
        if (!startAtSchedulerEntry)
            for (var index = 0; index < work.Count; index++) Start(index);
    }

    public void ActivateCascade()
    {
        lock (_gate) if (!_stopped) _cascadeReady = true;
    }

    public Task EnterSchedulerAsync(string schedulerId)
    {
        lock (_gate)
        {
            var index = Array.FindIndex(_work.ToArray(), w => w.Identity.SchedulerNodeId == schedulerId);
            if (index < 0) throw new ArgumentException("Agenda não pertence ao grupo.");
            if (_entered[index]) throw new InvalidOperationException("Agenda já recebeu entrada no fluxo.");
            if (!_stopped) Start(index);
            return _completions[index].Task;
        }
    }

    private void Start(int index)
    {
        _entered[index] = true;
        _ = Run(index); // Run always observes faults and completes the corresponding member.
    }

    private async Task Run(int index)
    {
        var item = _work[index];
        try
        {
            await _schedulers[index].RunAsync(item.Identity, item.Execute, item.Record,
                _stop.Token, item.DispatchTolerance, () =>
                {
                    lock (_gate) return !_deferred || item.Identity.CoordinatedCascadeNodeId is null || _cascadeReady;
                }).ConfigureAwait(false);
            _completions[index].TrySetResult();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { _completions[index].TrySetResult(); }
        catch (Exception error)
        {
            _stop.Cancel();
            _completions[index].TrySetException(error);
        }
    }

    public void SetPaused(bool paused)
    {
        foreach (var scheduler in _schedulers) scheduler.SetPaused(paused);
    }

    public void RequestStop()
    {
        lock (_gate)
        {
            _stopped = true;
            for (var index = 0; index < _entered.Length; index++)
                if (!_entered[index]) _completions[index].TrySetResult();
        }
        _stop.Cancel();
    }

    public async Task StopAndWaitAsync()
    {
        RequestStop();
        await Task.WhenAll(_completions.Select(c => c.Task)).ConfigureAwait(false);
    }
    public void Dispose() => _stop.Dispose();
}
