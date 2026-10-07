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
    private readonly Task[] _tasks;
    public CancellationToken StopToken => _stop.Token;

    public RecipeCascadePeriodicGroup(IReadOnlyList<RecipePeriodicWork> work, TimeProvider time,
        CancellationToken cancellation, bool paused)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        _schedulers = work.Select(_ => new RecipePeriodicExecutor(time)).ToArray();
        foreach (var scheduler in _schedulers) scheduler.SetPaused(paused);
        _tasks = work.Select(Run).ToArray();

        async Task Run(RecipePeriodicWork item, int index)
        {
            try { await _schedulers[index].RunAsync(item.Identity, item.Execute, item.Record,
                _stop.Token, item.DispatchTolerance).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch { _stop.Cancel(); throw; }
        }
    }

    public void SetPaused(bool paused)
    {
        foreach (var scheduler in _schedulers) scheduler.SetPaused(paused);
    }

    public async Task StopAndWaitAsync()
    {
        _stop.Cancel();
        await Task.WhenAll(_tasks).ConfigureAwait(false);
    }
    public void Dispose() => _stop.Dispose();
}
