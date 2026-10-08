using System.Collections.Immutable;
using OpenTECHub.Protocol;

namespace OpenTECHub.Services.Recipes;

public sealed partial class RecipeEngine
{
    /// <summary>Applies a complete ramp frame using current routes. The caller holds the ramp producer lease.</summary>
    public bool TryApplyRampFrame(ImmutableArray<LinearRampSample> references, string? cascadeNodeId,
        double phInactiveBand, Guid? expectedExecutionId = null)
    {
        if (references.IsDefaultOrEmpty || references.Select(sample => sample.Variable).Distinct().Count() != references.Length)
            throw new ArgumentException("Quadro de rampa vazio ou com parâmetros repetidos.", nameof(references));
        lock (_lock)
        {
            if (State != RecipeRunState.Running || expectedExecutionId is { } execution && execution != ExecutionId) return false;
            var commands = new RecipeRampDirectCommands(MaxFlow, _routeCoordinator.IsUartFallback,
                _settings.Current.GasRig.ToConfiguration(), phInactiveBand);
            var combined = OpenTECCommand.Create();
            LinearRampSample? cascadeReference = null;
            foreach (var sample in references)
            {
                // Build every direct command before sending anything; no partial frame on validation failure.
                if (sample.Variable == SetpointVariable.Oxygen && sample.OxygenTarget == RampOxygenTarget.ActiveCascadeReference)
                {
                    commands.Quantize(sample.Variable, sample.Reference);
                    cascadeReference = sample;
                }
                else
                {
                    if (_liveCascades.Count != 0 && sample.Variable is SetpointVariable.Agitation or SetpointVariable.Flow or SetpointVariable.Oxygen)
                        throw new InvalidOperationException("Rampa direta conflita com os atuadores da cascata ativa.");
                    combined.Merge(commands.Build(sample));
                }
            }

            using var cascadeStep = cascadeReference is not null && cascadeNodeId is not null &&
                _cascadeGates.TryGetValue(cascadeNodeId, out var gate) ? gate.TryEnterStep() : null;
            Control.CascadeController? controller = null;
            if (cascadeReference is not null && (cascadeStep is null || cascadeNodeId is null ||
                !_liveCascades.TryGetValue(cascadeNodeId, out controller))) return false;
            if (cascadeReference is not null && new[] { ActuatorId.Agitation, ActuatorId.Aeration, ActuatorId.Oxygen }
                .Any(resource => _arbiter.OwnerOf(resource) != Communication.CommandOwner.Recipe)) return false;
            // The arbiter accepts or refuses all direct keys together; update the local reference only on acceptance.
            if (!combined.IsEmpty && !_arbiter.Dispatch(Communication.CommandOwner.Recipe, combined).Accepted) return false;
            if (cascadeReference is not null) controller!.OxygenSetpoint = cascadeReference.Reference;
            return true;
        }
    }
}
