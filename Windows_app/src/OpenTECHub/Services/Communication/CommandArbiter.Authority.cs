using System.Collections.Immutable;
using OpenTECHub.Protocol;

namespace OpenTECHub.Services.Communication;

/// <summary>Execution authority for a whole resource set. Every handoff invalidates the previous generation.</summary>
public sealed record CommandAuthorityLease(Guid ReservationId, Guid ExecutionId, string BlockId,
    CommandOwner Owner, long Generation, ImmutableArray<ActuatorId> Resources);

public interface ICommandAuthorityArbiter : ICommandArbiter
{
    Task<CommandAuthorityLease> ReserveAsync(CommandOwner expectedOwner, Guid executionId, string blockId,
        IReadOnlyList<ActuatorId> resources, TimeSpan timeout, CancellationToken ct = default);
    bool IsCurrent(CommandAuthorityLease authority);
    CommandDispatchResult DispatchReserved(CommandAuthorityLease authority, OpenTECCommand command, bool separateFrame = false);
    CommandAuthorityLease TransferReserved(CommandAuthorityLease authority, CommandOwner to, string reason);
    void ReleaseReservation(CommandAuthorityLease authority);
    Task DrainReservedCommandsAsync(CommandAuthorityLease authority, CancellationToken ct = default);
    IReadOnlyList<ReservedDesiredState> CaptureReservedDesiredState(CommandAuthorityLease authority);
    IReadOnlyList<ReservedDesiredState> CaptureReservedDesiredState(CommandAuthorityLease authority, IReadOnlyList<ActuatorId> resources);
}

/// <summary>Requested state and transport evidence are separate; neither is a physical reading.</summary>
public sealed record ReservedDesiredState(ActuatorId Actuator, CommandOwner Owner, string DesiredCommandJson,
    string TransportAcceptedCommandJson, DateTimeOffset DesiredUpdatedUtc, DateTimeOffset TransportUpdatedUtc);

public sealed partial class CommandArbiter
{
    private readonly Dictionary<Guid, CommandAuthorityLease> _reservations = new();
    private readonly Dictionary<ActuatorId, Guid> _reservedActuators = new();
    private readonly Dictionary<Guid, long> _drainedReservations = new();
    private TaskCompletionSource _reservationChanged = NewReservationSignal();

    private static TaskCompletionSource NewReservationSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<CommandAuthorityLease> ReserveAsync(CommandOwner expectedOwner, Guid executionId, string blockId,
        IReadOnlyList<ActuatorId> resources, TimeSpan timeout, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (!Enum.IsDefined(expectedOwner) || executionId == Guid.Empty || string.IsNullOrWhiteSpace(blockId) ||
            resources.Count == 0 || resources.Distinct().Count() != resources.Count ||
            resources.Any(a => !Enum.IsDefined(a)) || timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentException("Reserva precisa de identidade, recursos únicos e prazo finito.");
        var ordered = resources.OrderBy(a => a).ToImmutableArray();
        var started = _time.GetTimestamp();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Task changed;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (ordered.All(a => !_reservedActuators.ContainsKey(a)))
                {
                    if (ordered.Any(a => _ownership.GetValueOrDefault(a, CommandOwner.Manual) != expectedOwner))
                        throw new InvalidOperationException("Proprietário mudou antes da reserva.");
                    var lease = new CommandAuthorityLease(Guid.NewGuid(), executionId, blockId, expectedOwner, 0, ordered);
                    _reservations.Add(lease.ReservationId, lease);
                    foreach (var resource in ordered) _reservedActuators.Add(resource, lease.ReservationId);
                    return lease;
                }
                changed = _reservationChanged.Task;
            }
            var remaining = timeout - _time.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("Tempo de espera pelos recursos esgotado.");
            await changed.WaitAsync(remaining, _time, ct).ConfigureAwait(false);
        }
    }

    public bool IsCurrent(CommandAuthorityLease authority)
    {
        lock (_gate) return IsCurrentUnderLock(authority);
    }

    private bool IsCurrentUnderLock(CommandAuthorityLease authority)
        => !_disposed && _reservations.TryGetValue(authority.ReservationId, out var current) && current == authority &&
            authority.Resources.All(a => _ownership.GetValueOrDefault(a, CommandOwner.Manual) == authority.Owner &&
                _reservedActuators.GetValueOrDefault(a) == authority.ReservationId);

    private bool CanDispatchUnderLock(CommandOwner requester, ActuatorId actuator, CommandAuthorityLease? authority)
        => !_disposed && _ownership.GetValueOrDefault(actuator, CommandOwner.Manual) == requester &&
            (authority is null ? !_reservedActuators.ContainsKey(actuator) :
                IsCurrentUnderLock(authority) && authority.Owner == requester && authority.Resources.Contains(actuator));

    public CommandDispatchResult DispatchReserved(CommandAuthorityLease authority, OpenTECCommand command, bool separateFrame = false)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(command);
        if (!IsCurrent(authority) || command.Keys.Any(key => CommandActuators.ForKey(key) is null))
            return new(false, CommandActuators.ActuatorsIn(command).ToArray(), authority.Owner);
        return Dispatch(authority.Owner, command, separateFrame, authority);
    }

    public async Task DrainReservedCommandsAsync(CommandAuthorityLease authority, CancellationToken ct = default)
    {
        if (!IsCurrent(authority)) throw new InvalidOperationException("Reserva revogada antes da barreira.");
        await _inner.DrainCommandsAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            if (!IsCurrentUnderLock(authority)) throw new InvalidOperationException("Reserva revogada durante a barreira.");
            _drainedReservations[authority.ReservationId] = authority.Generation;
        }
    }

    public Task DrainCommandsAsync(CancellationToken ct = default) => _inner.DrainCommandsAsync(ct);

    public CommandAuthorityLease TransferReserved(CommandAuthorityLease authority, CommandOwner to, string reason)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (!Enum.IsDefined(to) || to == CommandOwner.Manual || string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Cessão reservada exige proprietário ativo e motivo.");
        lock (_gate)
        {
            if (!IsCurrentUnderLock(authority)) throw new InvalidOperationException("Reserva revogada ou geração antiga.");
            if (!_drainedReservations.TryGetValue(authority.ReservationId, out var drained) || drained != authority.Generation)
                throw new InvalidOperationException("Transferência exige barreira confirmada dos comandos anteriores.");
            var next = authority with { Owner = to, Generation = checked(authority.Generation + 1) };
            _drainedReservations.Remove(authority.ReservationId);
            _reservations[authority.ReservationId] = next;
            // Transfer needs the old owner only for this atomic transition; it validates the new token below.
            foreach (var resource in next.Resources) _ownership[resource] = to;
            Transfer(to, next.Resources, reason, isSafeAbort: false, next);
            return next;
        }
    }

    public void ReleaseReservation(CommandAuthorityLease authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        lock (_gate)
        {
            if (!IsCurrentUnderLock(authority)) throw new InvalidOperationException("Reserva revogada ou geração antiga.");
            RevokeReservationsUnderLock(authority.Resources);
        }
    }

    private void RevokeReservationsUnderLock(IReadOnlyList<ActuatorId> resources)
    {
        var ids = resources.Where(_reservedActuators.ContainsKey).Select(a => _reservedActuators[a]).Distinct().ToArray();
        foreach (var id in ids)
        {
            _drainedReservations.Remove(id);
            if (_reservations.Remove(id, out var lease))
                foreach (var resource in lease.Resources) _reservedActuators.Remove(resource);
        }
        if (ids.Length > 0)
        {
            var signal = _reservationChanged;
            _reservationChanged = NewReservationSignal();
            signal.TrySetResult();
        }
    }
}
