namespace OpenTECHub.Services.Recipes;

/// <summary>A group cannot finish until every member has completed its independent recovery.</summary>
public static class RecipeParallelGroup
{
    public static async Task RunAsync(IReadOnlyList<Func<CancellationToken, Task>> members,
        CancellationToken cancellation, int? lifetimeOwner = null)
    {
        if (members.Count == 0 || lifetimeOwner is < 0 || lifetimeOwner >= members.Count)
            throw new ArgumentException("Grupo paralelo inválido.");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var tasks = members.Select((member, index) => RunMember(member, index)).ToArray();
        // WhenAll observes every fault and waits for recovery, including a sibling cancelled by a fault.
        await Task.WhenAll(tasks).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();

        async Task RunMember(Func<CancellationToken, Task> member, int index)
        {
            try { await member(stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested && !cancellation.IsCancellationRequested) { }
            catch { stop.Cancel(); throw; }
            finally { if (index == lifetimeOwner) stop.Cancel(); }
        }
    }
}
