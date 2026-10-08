using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using System.Collections.Immutable;

namespace OpenTECHub.Services.Recipes;

internal enum RecipeRampDestinationAvailability { Available, Suspended, Unavailable }

public sealed partial class RecipeEngine
{
    internal IDeviceService RampTelemetryDevice => _device;
    internal TimeProvider RampTimeProvider => _time;
    internal RampTemperatureRoute RampTemperatureRoute
    {
        get { lock (_lock) return _hubRoutesTemperatureToBath ? Recipes.RampTemperatureRoute.ExternalBath : Recipes.RampTemperatureRoute.NativeModule; }
    }

    internal bool TryApplyRampMeasuredReference(LinearRampSample target, Guid executionId, out RecipeRampMeasuredRoute route)
        => TryApplyRampMeasuredFrame([target], null, 0, executionId, out route);

    internal bool TryApplyRampMeasuredFrame(ImmutableArray<LinearRampSample> targets, string? cascadeNodeId, double phInactiveBand,
        Guid executionId, out RecipeRampMeasuredRoute route, RampTemperatureRoute? expectedTemperatureRoute = null)
    {
        lock (_lock)
        {
            route = new(_routeCoordinator.IsUartFallback, _hubRoutesTemperatureToBath, _settings.Current.GasRig.ToConfiguration());
            if (expectedTemperatureRoute is { } expected && expected != RampTemperatureRoute) return false;
            return TryApplyRampFrame(targets, cascadeNodeId, phInactiveBand, executionId);
        }
    }

    internal RecipeRampDestinationAvailability RampMeasuredAvailability(Guid executionId, SetpointVariable variable,
        RecipeRampMeasuredRoute route)
    {
        lock (_lock)
        {
            if (ExecutionId != executionId ||
                _liveCascades.Count != 0 && variable is SetpointVariable.Agitation or SetpointVariable.Flow or SetpointVariable.Oxygen ||
                variable == SetpointVariable.Agitation && _routeCoordinator.IsUartFallback != route.MotorViaUart ||
                variable == SetpointVariable.Temperature && _hubRoutesTemperatureToBath != route.TemperatureViaBath ||
                variable == SetpointVariable.Flow && _settings.Current.GasRig.ToConfiguration() != route.GasRig ||
                _arbiter.OwnerOf(CommandActuators.ForKey(RecipeRampInitialState.KeyFor(variable))!.Value) != CommandOwner.Recipe)
                return RecipeRampDestinationAvailability.Unavailable;
            return State switch { RecipeRunState.Running => RecipeRampDestinationAvailability.Available,
                RecipeRunState.Paused => RecipeRampDestinationAvailability.Suspended,
                _ => RecipeRampDestinationAvailability.Unavailable };
        }
    }
}
