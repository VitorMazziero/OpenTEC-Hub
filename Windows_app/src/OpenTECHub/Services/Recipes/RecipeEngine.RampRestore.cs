using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;

namespace OpenTECHub.Services.Recipes;

public sealed partial class RecipeEngine
{
    /// <summary>Dispatch captured direct state under an already quiescent recovery reservation.
    /// Transport acceptance is not a recovery confirmation.</summary>
    internal bool TryDispatchRampRestoration(RecipeRampRestoreFrame frame, Guid executionId,
        CommandAuthorityLease authority, RampTemperatureRoute? temperatureRoute, out RecipeRampMeasuredRoute route)
    {
        var command = OpenTECCommand.Parse(frame.CommandJson);
        lock (_lock)
        {
            route = new(_routeCoordinator.IsUartFallback, _hubRoutesTemperatureToBath, _settings.Current.GasRig.ToConfiguration());
            if (State is not (RecipeRunState.Running or RecipeRunState.Paused) || ExecutionId != executionId ||
                authority.ExecutionId != executionId || authority.Owner != CommandOwner.Recipe ||
                _arbiter is not ICommandAuthorityArbiter arbiter || !arbiter.IsCurrent(authority) ||
                temperatureRoute is { } expected && expected != RampTemperatureRoute)
                return false;
            if (frame.References.Any(target => target.Variable == SetpointVariable.Oxygen))
                throw new InvalidOperationException("Estado da cascata deve ser recuperado pela operação coordenada.");
            if (_liveCascades.Count != 0 && frame.References.Any(target => target.Variable is SetpointVariable.Agitation or SetpointVariable.Flow))
                return false;
            if (command.Keys.Any(key => CommandActuators.ForKey(key) is not { } actuator || !authority.Resources.Contains(actuator)))
                throw new InvalidOperationException("Comando de retorno fora da reserva.");
            if (command.Contains(CommandKeys.MotorControlMode) &&
                RecipeAssayReturnState.Flag(command, CommandKeys.MotorControlMode) == _routeCoordinator.IsUartFallback ||
                command.Contains(CommandKeys.TempControlMode) &&
                RecipeAssayReturnState.Flag(command, CommandKeys.TempControlMode) != _hubRoutesTemperatureToBath)
                return false;
            return !command.IsEmpty && arbiter.DispatchReserved(authority, command).Accepted;
        }
    }
}
