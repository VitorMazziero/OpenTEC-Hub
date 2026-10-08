using System.Collections.Immutable;

namespace OpenTECHub.Services.Recipes;

/// <summary>Sampling, complete-frame application and final confirmation share the assay handoff barrier.</summary>
public sealed class RecipeRampGuardedDestination : IRecipeRampDestination
{
    private readonly RecipeRampResourceProducer _producer;
    private readonly RecipeRampActiveClock _clock;
    private readonly IRecipeRampDestination _destination;
    private readonly TimeProvider _time;
    private readonly TimeSpan _confirmationTimeout;

    public RecipeRampGuardedDestination(RecipeRampResourceProducer producer, RecipeRampActiveClock clock,
        IRecipeRampDestination destination, TimeProvider time, TimeSpan confirmationTimeout)
    {
        ArgumentNullException.ThrowIfNull(producer);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(time);
        if (!ReferenceEquals(producer.ActiveClock, clock))
            throw new ArgumentException("Relógio diferente do produtor da rampa.", nameof(clock));
        if (confirmationTimeout <= TimeSpan.Zero || confirmationTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(confirmationTimeout));
        _producer = producer; _clock = clock; _destination = destination; _time = time;
        _confirmationTimeout = confirmationTimeout;
    }

    public async Task<ImmutableArray<LinearRampSample>> TryApplyTrajectoryAsync(LinearSetpointRampTrajectory trajectory,
        RecipeRampActiveClock clock, ImmutableArray<LinearRampSample> last, CancellationToken cancellation)
    {
        if (!ReferenceEquals(clock, _clock)) throw new ArgumentException("Relógio diferente do produtor da rampa.", nameof(clock));
        cancellation.ThrowIfCancellationRequested();
        _producer.StopToken.ThrowIfCancellationRequested();
        using var step = _producer.TryEnterStep();
        if (step is null || _clock.IsSuspended) return [];
        var samples = trajectory.Sample(_clock.ActiveSeconds);
        if (samples.SequenceEqual(last)) return samples;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _producer.StopToken);
        return await _destination.TryApplyAsync(samples, linked.Token).ConfigureAwait(false) ? samples : [];
    }

    // Reject precomputed frames: callers must calculate the reference within the barrier.
    public Task<bool> TryApplyAsync(ImmutableArray<LinearRampSample> references, CancellationToken cancellation)
        => throw new InvalidOperationException("Destino protegido exige amostragem dentro da barreira.");

    public async Task<bool> TryConfirmFinalAsync(ImmutableArray<LinearRampSample> references, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        _producer.StopToken.ThrowIfCancellationRequested();
        using var step = _producer.TryEnterStep();
        if (step is null || _clock.IsSuspended) return false;
        if (references.IsEmpty || references.Any(sample => !sample.AtFinalTarget))
            throw new ArgumentException("Confirmação exige alvos finais de todos os parâmetros.", nameof(references));
        using var deadline = new CancellationTokenSource(_confirmationTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _producer.StopToken, deadline.Token);
        try
        {
            // Keep the lease until the destination has actually exited; an abandoned task could
            // otherwise race the next assay. Destination confirmation must observe cancellation.
            await _destination.ConfirmFinalAsync(references, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            return true;
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested &&
            !cancellation.IsCancellationRequested && !_producer.StopToken.IsCancellationRequested)
        {
            throw new TimeoutException("Prazo de confirmação final da rampa esgotado.", error);
        }
    }

    public async Task ConfirmFinalAsync(ImmutableArray<LinearRampSample> references, CancellationToken cancellation)
    {
        if (!await TryConfirmFinalAsync(references, cancellation).ConfigureAwait(false))
            throw new InvalidOperationException("Rampa suspensa; confirmação deve aguardar devolução dos recursos.");
    }
}
