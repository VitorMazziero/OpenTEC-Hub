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

    public async Task ExecuteAsync(LinearSetpointRampTrajectory trajectory, RecipeRampActiveClock activeClock,
        IRecipeRampDestination destination, TimeSpan minimumDispatchInterval, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(trajectory);
        ArgumentNullException.ThrowIfNull(activeClock);
        ArgumentNullException.ThrowIfNull(destination);
        if (minimumDispatchInterval <= TimeSpan.Zero || minimumDispatchInterval.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(minimumDispatchInterval));
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Executor de rampa já utilizado.");
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
