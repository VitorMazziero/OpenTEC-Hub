using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;

namespace OpenTECHub.Services.Recipes;

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
    /// Returns every actuator the recipe holds back to Manual without altering what the recipe configured.
    /// </summary>
    private void SafeStopAndRelease(string reason)
    {
        if (Resources?.HasUnreturnedAssayAuthority(ExecutionId) == true)
        {
            Log(RecipeLogSeverity.Error, "Retorno do ensaio não confirmado/gravado; aplicando parada de N/Q/O₂.");
            var stop = CommandBuilders.MotorSetpoint(0)
                .Merge(CommandBuilders.FlowSafeStop(_settings.Current.Setpoints.MaxFlowLitresPerMinute))
                .Set(CommandKeys.OxygenMonitor, 0.0);
            _arbiter.DispatchSafety(stop, reason);
        }
        _arbiter.Release(CommandOwner.Recipe, reason);
    }
}
