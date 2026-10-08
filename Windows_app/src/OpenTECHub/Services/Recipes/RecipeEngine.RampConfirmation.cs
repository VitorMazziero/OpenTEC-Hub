using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;

namespace OpenTECHub.Services.Recipes;

public sealed partial class RecipeEngine
{
    /// <summary>Confirms the live cascade reference under its handoff barrier, never the measured DO or monitor.</summary>
    public RecipeRampFinalConfirmation? TryConfirmRampCascadeReference(string cascadeNodeId, LinearRampSample target,
        Guid? expectedExecutionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cascadeNodeId);
        ArgumentNullException.ThrowIfNull(target);
        if (target.Variable != SetpointVariable.Oxygen || target.OxygenTarget != RampOxygenTarget.ActiveCascadeReference ||
            !target.AtFinalTarget || !double.IsFinite(target.Reference) || !DeviceRanges.Accepts(SetpointVariable.Oxygen, target.Reference))
            throw new ArgumentException("Confirmação da cascata exige uma referência final válida de O₂.", nameof(target));
        lock (_lock)
        {
            if (State != RecipeRunState.Running || expectedExecutionId is { } execution && execution != ExecutionId) return null;
            using var step = _cascadeGates.TryGetValue(cascadeNodeId, out var gate) ? gate.TryEnterStep() : null;
            if (step is null || !_liveCascades.TryGetValue(cascadeNodeId, out var controller) ||
                new[] { ActuatorId.Agitation, ActuatorId.Aeration, ActuatorId.Oxygen }
                    .Any(resource => _arbiter.OwnerOf(resource) != CommandOwner.Recipe) ||
                controller.OxygenSetpoint != target.Reference) return null;
            return new(SetpointVariable.Oxygen, RampOxygenTarget.ActiveCascadeReference, controller.OxygenSetpoint,
                RecipeRampConfirmationEvidence.ControllerReference, _time.GetUtcNow());
        }
    }
}
