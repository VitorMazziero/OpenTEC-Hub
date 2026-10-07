using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;

namespace OpenTECHub.Services.Recipes;

/// <summary>Captures only an authorized, drained desired state; never guesses a missing reference from telemetry.</summary>
public static class RecipeAssayReturnState
{
    public static KlaReturnSnapshot Capture(ICommandAuthorityArbiter arbiter, CommandAuthorityLease authority,
        GasRigConfiguration rig, TimeProvider time, IEnumerable<ControllerReturnSnapshot> controllers,
        SensorSnapshot? observation, DateTimeOffset? observationReceivedUtc)
    {
        if (authority.Owner != CommandOwner.Recipe || !Enum.IsDefined(rig.AirInletInput))
            throw new InvalidOperationException("Captura exige autoridade da receita e arranjo de gás válido.");
        var states = arbiter.CaptureReservedDesiredState(authority);
        var snapshots = states.Select(state => new ActuatorReturnSnapshot
        {
            Actuator = state.Actuator, Owner = state.Owner, OwnerExecutionId = authority.ExecutionId.ToString(),
            DesiredCommandJson = state.DesiredCommandJson, TransportAcceptedCommandJson = state.TransportAcceptedCommandJson,
            DesiredUpdatedUtc = state.DesiredUpdatedUtc, TransportUpdatedUtc = state.TransportUpdatedUtc,
            ConfirmationChannel = state.Actuator == ActuatorId.Aeration ? "eco de gás / FlowCommandAck" :
                state.Actuator == ActuatorId.Agitation ? "aceitação de transporte; rota e velocidade verificadas separadamente" : "aceitação de transporte"
        }).ToImmutableArray();
        var commands = ValidateCommands(snapshots);
        var motor = commands[ActuatorId.Agitation]; var gas = commands[ActuatorId.Aeration];
        var rpm = Number(motor, CommandKeys.MotorSetpoint);
        var flow = Number(gas, CommandKeys.FlowSetpoint);
        var observedRoute = GasRouting.Interpret(Flag(gas, CommandKeys.Valve1), Flag(gas, CommandKeys.Valve2), flow, rig);
        if (!GasRouting.IsNominal(observedRoute)) throw new InvalidOperationException("Rota desejada de gás inválida.");
        var snapshot = new KlaReturnSnapshot
        {
            SnapshotId = Guid.NewGuid(), CapturedUtc = time.GetUtcNow(), AgitationSetpointRpm = rpm,
            AirflowSetpointLpm = flow, GasRoute = (GasRoute)observedRoute, AirInletInput = rig.AirInletInput,
            Actuators = snapshots, Controllers = controllers.ToImmutableArray(),
            Observation = observation is not null && observationReceivedUtc.HasValue ? new()
            {
                ReceivedUtc = observationReceivedUtc.Value,
                EchoJson = JsonSerializer.Serialize(new { observation.FlowSetpoint, observation.FlowControlEnabled,
                    observation.FlowValve1, observation.FlowValve2, observation.FlowValveMain, observation.FlowCommandAck,
                    observation.FlowCommandId, observation.FlowCommandPending, observation.MotorControlViaModbus,
                    observation.ServoMotorRouteAck, observation.ServoCommandPending, observation.FlowKp, observation.FlowKi,
                    observation.FlowFfGain, observation.FlowFfOffset, observation.FlowRampRate }),
                MeasuredAgitationRpm = observation.HasServoSample && observation.ServoOnline && double.IsFinite(observation.ServoRpm) ? observation.ServoRpm : null,
                MeasuredFlowLpm = observation.FlowmeterOnline && double.IsFinite(observation.FlowRate) ? observation.FlowRate : null,
                MeasuredOxygenPercent = observation.OxygenUpdated && double.IsFinite(observation.OxygenCalibrated) && observation.OxygenCalibrated >= 0 ? observation.OxygenCalibrated : null
            } : null
        };
        Validate(snapshot);
        return snapshot;
    }

    public static IReadOnlyDictionary<ActuatorId, OpenTECCommand> Validate(KlaReturnSnapshot snapshot)
    {
        snapshot.Validate();
        var commands = ValidateCommands(snapshot.Actuators);
        var motor = commands[ActuatorId.Agitation]; var gas = commands[ActuatorId.Aeration];
        if (Number(motor, CommandKeys.MotorSetpoint) != snapshot.AgitationSetpointRpm ||
            Number(gas, CommandKeys.FlowSetpoint) != snapshot.AirflowSetpointLpm ||
            GasRouting.Interpret(Flag(gas, CommandKeys.Valve1), Flag(gas, CommandKeys.Valve2), snapshot.AirflowSetpointLpm,
                new(snapshot.AirInletInput)) != (ObservedGasRoute)snapshot.GasRoute)
            throw new InvalidOperationException("Referências ou rota não correspondem ao comando congelado.");
        return commands;
    }

    private static Dictionary<ActuatorId, OpenTECCommand> ValidateCommands(IEnumerable<ActuatorReturnSnapshot> states)
    {
        var commands = new Dictionary<ActuatorId, OpenTECCommand>();
        foreach (var state in states)
        {
            var command = OpenTECCommand.Parse(state.DesiredCommandJson);
            if (command.IsEmpty || command.Keys.Any(key => CommandActuators.ForKey(key) != state.Actuator) ||
                state.Actuator is not (ActuatorId.Agitation or ActuatorId.Aeration or ActuatorId.Oxygen))
                throw new InvalidOperationException("Snapshot contém comando desconhecido ou fora do escopo de kLa.");
            commands.Add(state.Actuator, command);
        }
        if (!commands.TryGetValue(ActuatorId.Agitation, out var motor) || !commands.TryGetValue(ActuatorId.Aeration, out var gas))
            throw new InvalidOperationException("Estado desejado N/Q ausente.");
        var rpm = Number(motor, CommandKeys.MotorSetpoint); var modbus = Flag(motor, CommandKeys.MotorControlMode);
        if (rpm != Math.Truncate(rpm) || rpm != 0 && (rpm < MotorRouteCoordinator.MinRpm ||
            rpm > (modbus ? MotorRouteCoordinator.ModbusMaxRpm : MotorRouteCoordinator.UartFallbackMaxRpm)))
            throw new InvalidOperationException("Referência de motor incompatível com a rota.");
        var flow = Number(gas, CommandKeys.FlowSetpoint); var maxFlow = Number(gas, CommandKeys.MaxFlow);
        if (flow < 0 || maxFlow <= 0 || flow > maxFlow) throw new InvalidOperationException("Vazão desejada inválida.");
        Flag(gas, CommandKeys.FlowmeterComm); Flag(gas, CommandKeys.Valve1); Flag(gas, CommandKeys.Valve2); Flag(gas, CommandKeys.V_Flow);
        return commands;
    }

    internal static double Number(OpenTECCommand command, string key)
        => double.TryParse(command.GetRawValue(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value : throw new InvalidOperationException($"Estado desejado de {key} ausente ou inválido.");

    internal static bool Flag(OpenTECCommand command, string key)
        => Number(command, key) switch { 0 => false, 1 => true, _ => throw new InvalidOperationException($"Flag {key} inválida.") };
}
