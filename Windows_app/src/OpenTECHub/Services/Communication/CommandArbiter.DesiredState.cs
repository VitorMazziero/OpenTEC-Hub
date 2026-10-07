using OpenTECHub.Protocol;

namespace OpenTECHub.Services.Communication;

public sealed partial class CommandArbiter
{
    private readonly Dictionary<ActuatorId, OpenTECCommand> _desiredCommands = new();
    private readonly Dictionary<ActuatorId, OpenTECCommand> _acceptedCommands = new();
    private readonly Dictionary<ActuatorId, DateTimeOffset> _desiredUpdated = new();
    private readonly Dictionary<ActuatorId, DateTimeOffset> _acceptedUpdated = new();

    // Called at the final authorized enqueue boundary, before the transport can callback synchronously.
    private void RecordDesiredUnderLock(OpenTECCommand command)
    {
        foreach (var actuator in CommandActuators.ActuatorsIn(command))
        {
            if (!_desiredCommands.TryGetValue(actuator, out var state))
                _desiredCommands.Add(actuator, state = OpenTECCommand.Create());
            state.Merge(command.SelectKeys(key => CommandActuators.ForKey(key) == actuator));
            _desiredUpdated[actuator] = _time.GetUtcNow();
        }
    }

    private void RecordAcceptedUnderLock(string json)
    {
        OpenTECCommand command;
        try { command = OpenTECCommand.Parse(json); }
        catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException) { return; }
        foreach (var actuator in CommandActuators.ActuatorsIn(command))
        {
            if (!_acceptedCommands.TryGetValue(actuator, out var state))
                _acceptedCommands.Add(actuator, state = OpenTECCommand.Create());
            state.Merge(command.SelectKeys(key => CommandActuators.ForKey(key) == actuator));
            _acceptedUpdated[actuator] = _time.GetUtcNow();
        }
    }

    public IReadOnlyList<ReservedDesiredState> CaptureReservedDesiredState(CommandAuthorityLease authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        lock (_gate)
        {
            if (!IsCurrentUnderLock(authority) ||
                !_drainedReservations.TryGetValue(authority.ReservationId, out var generation) || generation != authority.Generation)
                throw new InvalidOperationException("Captura exige reserva atual e barreira de transporte confirmada.");
            var captured = new List<ReservedDesiredState>();
            foreach (var actuator in authority.Resources)
            {
                if (!DesiredIsAcceptedUnderLock(actuator))
                    throw new InvalidOperationException($"Estado comandado de {actuator} ausente ou não aceito pelo transporte.");
                var desired = _desiredCommands[actuator]; var accepted = _acceptedCommands[actuator];
                captured.Add(new(actuator, authority.Owner, desired.ToJson(), accepted.ToJson(),
                    _desiredUpdated[actuator], _acceptedUpdated[actuator]));
            }
            return captured.ToArray();
        }
    }

    private static bool ValuesEqual(string? desired, string? accepted)
        => desired == accepted || TryParseValue(desired, out var first) &&
            TryParseValue(accepted, out var second) && first == second;

    private bool DesiredIsAcceptedUnderLock(ActuatorId actuator)
        => _desiredCommands.TryGetValue(actuator, out var desired) && _acceptedCommands.TryGetValue(actuator, out var accepted) &&
            desired.Keys.All(key => ValuesEqual(desired.GetRawValue(key), accepted.GetRawValue(key)));
}
