using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;

namespace OpenTECHub.Services.Recipes;

internal enum RecipeRampDestinationAvailability { Available, Suspended, Unavailable }

public sealed partial class RecipeEngine
{
    internal IDeviceService RampTelemetryDevice => _device;
    internal TimeProvider RampTimeProvider => _time;

    internal bool TryApplyRampMotorReference(LinearRampSample target, Guid executionId, out bool uart)
    {
        lock (_lock)
        {
            uart = _routeCoordinator.IsUartFallback;
            return TryApplyRampFrame([target], null, 0, executionId);
        }
    }

    internal RecipeRampDestinationAvailability RampMotorAvailability(Guid executionId, bool uart)
    {
        lock (_lock)
        {
            if (ExecutionId != executionId || _liveCascades.Count != 0 || _routeCoordinator.IsUartFallback != uart ||
                _arbiter.OwnerOf(ActuatorId.Agitation) != CommandOwner.Recipe)
                return RecipeRampDestinationAvailability.Unavailable;
            return State switch { RecipeRunState.Running => RecipeRampDestinationAvailability.Available,
                RecipeRunState.Paused => RecipeRampDestinationAvailability.Suspended,
                _ => RecipeRampDestinationAvailability.Unavailable };
        }
    }
}
