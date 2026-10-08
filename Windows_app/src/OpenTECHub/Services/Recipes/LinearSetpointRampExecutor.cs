using System.Collections.Immutable;

namespace OpenTECHub.Services.Recipes;

public interface IRecipeRampDestination
{
    /// <summary>Revalidate authority/routes and apply the whole frame under the suspension barrier.
    /// False means no reference was applied; unavailable frames must never be partially dispatched.</summary>
    Task<bool> TryApplyAsync(ImmutableArray<LinearRampSample> references, CancellationToken cancellation);
    /// <summary>Confirm each final reference through its own destination, using a bounded deadline.</summary>
    Task ConfirmFinalAsync(ImmutableArray<LinearRampSample> references, CancellationToken cancellation);

    /// <summary>Destinations with a handoff barrier override this to sample inside the dispatch lease.</summary>
    async Task<ImmutableArray<LinearRampSample>> TryApplyTrajectoryAsync(LinearSetpointRampTrajectory trajectory,
        RecipeRampActiveClock clock, ImmutableArray<LinearRampSample> last, CancellationToken cancellation)
    {
        if (clock.IsSuspended) return [];
        var samples = trajectory.Sample(clock.ActiveSeconds);
        return samples.SequenceEqual(last) || await TryApplyAsync(samples, cancellation).ConfigureAwait(false) ? samples : [];
    }

    async Task<bool> TryConfirmFinalAsync(ImmutableArray<LinearRampSample> references, CancellationToken cancellation)
    {
        await ConfirmFinalAsync(references, cancellation).ConfigureAwait(false);
        return true;
    }
}

/// <summary>One cadence with no replay of overdue frames. Ownership and suspension are enforced by the destination.</summary>
public sealed class LinearSetpointRampExecutor(TimeProvider time)
{
    private int _started;

    /// <summary>Persist the frozen start before dispatch and return only a durable, validated completion.
    /// The caller retains reservations and handles recovery on cancellation or failure.</summary>
    public async Task<RecipeRampTerminalCheckpoint> ExecutePersistedAsync(RecipeRampStartCheckpoint start,
        RecipeRampCheckpointStore store, RecipeRampActiveClock activeClock, IRecipeRampDestination destination,
        IRecipeRampConfirmationSource confirmations, Func<SetpointVariable, double, double> quantize,
        TimeSpan minimumDispatchInterval, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(activeClock);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(confirmations);
        ArgumentNullException.ThrowIfNull(quantize);
        ValidateInterval(minimumDispatchInterval);
        ClaimExecution();
        var reason = $"initial-storage:{Guid.NewGuid():N}";
        activeClock.Suspend(reason);
        LinearSetpointRampTrajectory trajectory;
        RecipeRampStartCheckpoint persisted;
        try
        {
            persisted = await store.PersistStartAsync(start, cancellation).ConfigureAwait(false);
            trajectory = new(persisted.Configuration.Definition, persisted.InitialState.ConfirmedStarts, (variable, value) =>
            {
                var represented = quantize(variable, value);
                if (represented != RecipeRampReferenceQuantization.Quantize(variable, value, persisted.Configuration.TemperatureRoute))
                    throw new InvalidOperationException("Quantização do destino diverge da rota capturada.");
                return represented;
            });
            cancellation.ThrowIfCancellationRequested();
        }
        finally { activeClock.Resume(reason); }
        await ExecuteCoreAsync(trajectory, activeClock, destination, minimumDispatchInterval, cancellation).ConfigureAwait(false);
        var initial = persisted.InitialState;
        var terminal = new RecipeRampTerminalCheckpoint(1, initial.ExecutionId, persisted.InvocationId,
            initial.SnapshotId, initial.NodeId, RecipeRampTerminalStatus.Completed, RecipeRampReturnOutcome.NotRequired,
            activeClock.ActiveSeconds, time.GetUtcNow(), null, confirmations.FinalConfirmations);
        // Confirmation is already complete: finish the durable receipt even if cancellation arrives
        // while writing. A missing receipt must never authorize graph advancement.
        return await store.PersistTerminalAsync(terminal, CancellationToken.None).ConfigureAwait(false);
    }

    public async Task ExecuteAsync(LinearSetpointRampTrajectory trajectory, RecipeRampActiveClock activeClock,
        IRecipeRampDestination destination, TimeSpan minimumDispatchInterval, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(trajectory);
        ArgumentNullException.ThrowIfNull(activeClock);
        ArgumentNullException.ThrowIfNull(destination);
        ValidateInterval(minimumDispatchInterval);
        ClaimExecution();
        await ExecuteCoreAsync(trajectory, activeClock, destination, minimumDispatchInterval, cancellation).ConfigureAwait(false);
    }

    private static void ValidateInterval(TimeSpan minimumDispatchInterval)
    {
        if (minimumDispatchInterval <= TimeSpan.Zero || minimumDispatchInterval.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(minimumDispatchInterval));
    }

    private void ClaimExecution()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Executor de rampa já utilizado.");
    }

    private async Task ExecuteCoreAsync(LinearSetpointRampTrajectory trajectory, RecipeRampActiveClock activeClock,
        IRecipeRampDestination destination, TimeSpan minimumDispatchInterval, CancellationToken cancellation)
    {
        ImmutableArray<LinearRampSample> last = [];
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!activeClock.IsSuspended)
            {
                var samples = await destination.TryApplyTrajectoryAsync(trajectory, activeClock, last, cancellation).ConfigureAwait(false);
                if (!samples.IsEmpty)
                    last = samples;
                if (!samples.IsEmpty && samples.All(sample => sample.AtFinalTarget))
                {
                    if (await destination.TryConfirmFinalAsync(samples, cancellation).ConfigureAwait(false))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        return;
                    }
                }
            }
            // Delay from completion, rather than prior due time: slow dispatch cannot create a catch-up burst.
            await Task.Delay(minimumDispatchInterval, time, cancellation).ConfigureAwait(false);
        }
    }
}
