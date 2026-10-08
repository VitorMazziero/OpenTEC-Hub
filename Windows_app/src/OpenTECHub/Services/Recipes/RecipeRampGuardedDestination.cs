using System.Collections.Immutable;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;

namespace OpenTECHub.Services.Recipes;

/// <summary>Sampling, complete-frame application and final confirmation share the assay handoff barrier.</summary>
public sealed class RecipeRampGuardedDestination : IRecipeRampDestination
{
    private readonly RecipeRampResourceProducer _producer;
    private readonly RecipeRampActiveClock _clock;
    private readonly IRecipeRampDestination _destination;
    private readonly TimeProvider _time;
    private readonly TimeSpan _confirmationTimeout;
    private readonly ICommandAuthorityArbiter? _arbiter;
    private readonly Guid _executionId;
    private readonly TimeSpan _reservationTimeout;

    public RecipeRampGuardedDestination(RecipeRampResourceProducer producer, RecipeRampActiveClock clock,
        IRecipeRampDestination destination, TimeProvider time, TimeSpan confirmationTimeout,
        ICommandAuthorityArbiter? arbiter = null, Guid executionId = default, TimeSpan? reservationTimeout = null)
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
        if (arbiter is not null && (executionId == Guid.Empty || destination is not IRecipeRampReservedDestination))
            throw new ArgumentException("Reserva da rampa exige execução e destino compatível.");
        if (arbiter is null && (executionId != Guid.Empty || reservationTimeout is not null))
            throw new ArgumentException("Configuração de reserva sem árbitro.");
        _reservationTimeout = reservationTimeout ?? confirmationTimeout;
        if (_reservationTimeout <= TimeSpan.Zero || _reservationTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(reservationTimeout));
        _arbiter = arbiter; _executionId = executionId;
    }

    public async Task<ImmutableArray<LinearRampSample>> TryApplyTrajectoryAsync(LinearSetpointRampTrajectory trajectory,
        RecipeRampActiveClock clock, ImmutableArray<LinearRampSample> last, CancellationToken cancellation)
    {
        if (!ReferenceEquals(clock, _clock)) throw new ArgumentException("Relógio diferente do produtor da rampa.", nameof(clock));
        cancellation.ThrowIfCancellationRequested();
        _producer.StopToken.ThrowIfCancellationRequested();
        using var step = _producer.TryEnterStep();
        if (step is null || _clock.IsSuspended) return [];
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _producer.StopToken);
        var samples = trajectory.Sample(_clock.ActiveSeconds);
        if (samples.SequenceEqual(last)) return samples;
        if (_arbiter is null) return await _destination.TryApplyAsync(samples, linked.Token).ConfigureAwait(false) ? samples : [];
        var resources = samples.Where(sample => sample.Variable != SetpointVariable.Oxygen)
            .Select(sample => CommandActuators.ForKey(RecipeRampInitialState.KeyFor(sample.Variable))!.Value).Distinct().ToArray();
        var required = samples.SelectMany(sample => sample.Variable == SetpointVariable.Oxygen
            ? new[] { ActuatorId.Agitation, ActuatorId.Aeration, ActuatorId.Oxygen }
            : new[] { CommandActuators.ForKey(RecipeRampInitialState.KeyFor(sample.Variable))!.Value });
        if (required.Any(resource => !_producer.Resources.Contains(resource)))
            throw new InvalidOperationException("Produtor da rampa não protege todos os destinos.");
        // A cascade reference is local controller state. Reserving N/Q here would suppress
        // the cascade's own output. Its shared producer barrier coordinates the assay instead.
        if (resources.Length == 0) return await _destination.TryApplyAsync(samples, linked.Token).ConfigureAwait(false) ? samples : [];
        CommandAuthorityLease? authority = null;
        var waiting = $"reservation:{Guid.NewGuid():N}";
        _clock.Suspend(waiting);
        try
        {
            authority = await _arbiter.ReserveAsync(CommandOwner.Recipe, _executionId, _producer.NodeId,
                resources, _reservationTimeout, linked.Token).ConfigureAwait(false);
            using var drainDeadline = new CancellationTokenSource(_reservationTimeout, _time);
            using var drainToken = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, drainDeadline.Token);
            await _arbiter.DrainReservedCommandsAsync(authority, drainToken.Token).ConfigureAwait(false);
        }
        catch
        {
            // Once acquired, failed drainage leaves commands unresolved. Keep that reservation
            // closed until recovery or the engine's safe stop revokes it.
            if (authority is not null) _producer.Dispose();
            throw;
        }
        finally { _clock.Resume(waiting); }
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            if (_clock.IsSuspended) return [];
            samples = trajectory.Sample(_clock.ActiveSeconds);
            return await ((IRecipeRampReservedDestination)_destination).TryApplyReservedAsync(samples, authority,
                linked.Token).ConfigureAwait(false) ? samples : [];
        }
        finally
        {
            if (_arbiter.IsCurrent(authority))
            {
                // Drain accepted commands with an independent bound before releasing this step.
                using var drainDeadline = new CancellationTokenSource(_reservationTimeout, _time);
                try
                {
                    await _arbiter.DrainReservedCommandsAsync(authority, drainDeadline.Token).ConfigureAwait(false);
                    _arbiter.ReleaseReservation(authority);
                }
                catch { _producer.Dispose(); throw; }
            }
        }
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
            var confirmed = await _destination.TryConfirmFinalAsync(references, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            return confirmed && !_clock.IsSuspended;
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
