using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.KlaTesting;

namespace OpenTECHub.Services.Recipes;

// Safety: claiming the wire on start (which deactivates manual control), and the safe-stop plus
// ownership release that every run exit goes through.
public sealed partial class RecipeEngine
{
    /// <summary>Set when a run ended with an assay whose return to the previous state was never confirmed.</summary>
    public string? AssayReturnAlarm { get; private set; }

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
    /// <remarks>
    /// An assay whose return could not be confirmed after its retries (D-060) raises an alarm. A biotic
    /// assay leaves the last commands in place, because the Hub keeps them and stopping agitation and gas
    /// would starve the culture; an abiotic assay still commands the explicit N/Q/O₂ stop.
    /// </remarks>
    private void SafeStopAndRelease(string reason)
    {
        var unreturned = Resources?.UnreturnedAssayBlocks(ExecutionId) ?? [];
        if (unreturned.Count > 0)
        {
            var biotic = unreturned.Any(IsBioticAssayBlock);
            AssayReturnAlarm = biotic
                ? "Retorno do ensaio biótico não confirmado após as tentativas. Os últimos comandos foram mantidos; " +
                  "confira agitação, vazão e rota de ar no reator."
                : "Retorno do ensaio abiótico não confirmado após as tentativas; agitação, gás e O₂ foram parados.";
            Log(RecipeLogSeverity.Error, AssayReturnAlarm);
            if (!biotic)
            {
                var stop = CommandBuilders.MotorSetpoint(0)
                    .Merge(CommandBuilders.FlowSafeStop(_settings.Current.Setpoints.MaxFlowLitresPerMinute))
                    .Set(CommandKeys.OxygenMonitor, 0.0);
                _arbiter.DispatchSafety(stop, reason);
            }
            WaitingChanged?.Invoke();
        }
        _arbiter.Release(CommandOwner.Recipe, reason);
    }

    private bool IsBioticAssayBlock(string blockId)
    {
        try
        {
            return Current?.Node(blockId) is { Type: NodeType.KlaAssay } node &&
                RecipeAutonomousBlockConfiguration.ReadKla(node).Protocol == KlaAssayProtocol.Biotic;
        }
        catch (ArgumentException)
        {
            // An unreadable block cannot prove it is abiotic: keep the culture's last commands.
            return true;
        }
    }

    /// <summary>Clears the return alarm once the operator starts another run.</summary>
    private void ClearAssayReturnAlarm()
    {
        if (AssayReturnAlarm is null) return;
        AssayReturnAlarm = null;
        WaitingChanged?.Invoke();
    }
}
