using TecnalHub.Protocol;
using TecnalHub.Services.Communication;

namespace TecnalHub.Services.Recipes;

// Safety: claiming the wire on start (which deactivates manual control), and the safe-stop plus
// ownership release that every run exit goes through.
public sealed partial class RecipeEngine
{
    /// <summary>
    /// Claims every actuator for the recipe.
    /// </summary>
    /// <remarks>
    /// This is the mechanism behind "all control parameters deactivated on start" (§5.3.3): once
    /// the recipe owns every actuator, the arbiter refuses any Manual dispatch, so the manual
    /// control surfaces go inert and only the recipe writes to the wire.
    /// </remarks>
    private void ClaimAllActuators(string reason)
        => _arbiter.Claim(CommandOwner.Recipe, CommandActuators.All, reason);

    /// <summary>
    /// Safe-stops the subsystems the recipe could have driven, then returns every actuator it still
    /// holds to Manual. Resilient: if a safe-abort already revoked ownership, the stop frame is
    /// simply refused and the release is a no-op.
    /// </summary>
    private void SafeStopAndRelease(string reason)
    {
        var maxFlow = _settings.Current.Setpoints.MaxFlowLitresPerMinute;

        // Core loop safe-stop (temp/motor/oxygen/flow/pressure) plus dosing pumps to zero
        // intensity. Sent as one frame; the arbiter refuses harmlessly if we no longer own it.
        var stop = CommandBuilders.CoreSafeStop(maxFlow)
            .Set(CommandKeys.PHIntensity, 0.0)
            .Set(CommandKeys.NutriIntensity, 0.0)
            .Set(CommandKeys.AntifoamIntensity, 0.0);

        if (_arbiter.OwnerOf(ActuatorId.Temperature) == CommandOwner.Recipe)
        {
            _arbiter.Dispatch(CommandOwner.Recipe, stop);
        }

        _arbiter.Release(CommandOwner.Recipe, reason);
    }
}
