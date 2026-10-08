using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Control;

namespace OpenTECHub.Services.Recipes;

public sealed partial class RecipeEngine
{
    /// <summary>Dispatch captured direct state under an already quiescent recovery reservation.
    /// Transport acceptance is not a recovery confirmation.</summary>
    internal bool TryDispatchRampRestoration(RecipeRampRestoreFrame frame, Guid executionId,
        CommandAuthorityLease authority, RampTemperatureRoute? temperatureRoute, out RecipeRampMeasuredRoute route,
        ControllerReturnSnapshot? controllerSnapshot = null)
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
            var oxygen = frame.References.SingleOrDefault(target => target.Variable == SetpointVariable.Oxygen);
            Action? restoreController = null;
            if (oxygen is not null)
            {
                if (controllerSnapshot is null || !controllerSnapshot.WasActive ||
                    controllerSnapshot.StateVersion != "recipe-cascade-v1" ||
                    oxygen.OxygenTarget != RampOxygenTarget.ActiveCascadeReference || !oxygen.AtFinalTarget ||
                    !_cascadeGates.TryGetValue(controllerSnapshot.ControllerId, out var gate) || !gate.IsQuiescentPaused ||
                    !_liveCascades.TryGetValue(controllerSnapshot.ControllerId, out var controller) ||
                    new[] { ActuatorId.Agitation, ActuatorId.Aeration, ActuatorId.Oxygen }
                        .Any(resource => !authority.Resources.Contains(resource))) return false;
                var validation = CascadeController.CreateDefault(oxygen.Reference);
                validation.RestoreStateJson(controllerSnapshot.StateJson);
                if (validation.OxygenSetpoint != oxygen.Reference) return false;
                restoreController = controller.PrepareStateRestoration(controllerSnapshot.StateJson);
            }
            else if (controllerSnapshot is not null)
                throw new InvalidOperationException("Estado da cascata sem referência de O₂ no retorno.");
            if (_liveCascades.Count != 0 && frame.References.Any(target => target.Variable is SetpointVariable.Agitation or SetpointVariable.Flow))
                return false;
            if (command.Keys.Any(key => CommandActuators.ForKey(key) is not { } actuator || !authority.Resources.Contains(actuator)))
                throw new InvalidOperationException("Comando de retorno fora da reserva.");
            if (command.Contains(CommandKeys.MotorControlMode) &&
                RecipeAssayReturnState.Flag(command, CommandKeys.MotorControlMode) == _routeCoordinator.IsUartFallback ||
                command.Contains(CommandKeys.TempControlMode) &&
                RecipeAssayReturnState.Flag(command, CommandKeys.TempControlMode) != _hubRoutesTemperatureToBath)
                return false;
            if (command.IsEmpty && restoreController is null ||
                !command.IsEmpty && !arbiter.DispatchReserved(authority, command).Accepted) return false;
            restoreController?.Invoke();
            return true;
        }
    }
}
