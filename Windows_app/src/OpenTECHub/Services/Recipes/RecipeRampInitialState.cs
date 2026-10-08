using System.Collections.Immutable;
using System.Text.Json;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;

namespace OpenTECHub.Services.Recipes;

public enum RampReferenceEvidence { TransportAccepted, ControllerReference }

public sealed record RampCapturedReference(SetpointVariable Variable, RampOxygenTarget? OxygenTarget,
    double Reference, RampReferenceEvidence Evidence, DateTimeOffset RecordedUtc);

/// <summary>Frozen references and their evidence. Transport acceptance is never a physical measurement.</summary>
public sealed record RecipeRampInitialState(Guid SnapshotId, Guid ExecutionId, string NodeId,
    DateTimeOffset CapturedUtc, ImmutableArray<RampCapturedReference> References,
    ImmutableArray<ReservedDesiredState> Commands, ControllerReturnSnapshot? Controller)
{
    public ImmutableDictionary<SetpointVariable, double> ConfirmedStarts
        => References.ToImmutableDictionary(reference => reference.Variable, reference => reference.Reference);

    public static ImmutableArray<ActuatorId> ResourcesFor(LinearSetpointRampDefinition definition)
    {
        definition.Validate();
        return definition.Lines.SelectMany(line => line.OxygenTarget == RampOxygenTarget.ActiveCascadeReference
            ? new[] { ActuatorId.Agitation, ActuatorId.Aeration, ActuatorId.Oxygen }
            : new[] { CommandActuators.ForKey(KeyFor(line.Variable))!.Value }).Distinct().Order().ToImmutableArray();
    }

    public static RecipeRampInitialState Capture(RecipeRampBlockConfiguration configuration,
        ICommandAuthorityArbiter arbiter, CommandAuthorityLease authority, TimeProvider time,
        ControllerReturnSnapshot? controller = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(time);
        configuration.Definition.Validate();
        if (authority.Owner != CommandOwner.Recipe || !arbiter.IsCurrent(authority) ||
            ResourcesFor(configuration.Definition).Any(resource => !authority.Resources.Contains(resource)))
            throw new InvalidOperationException("Captura da rampa exige reserva atual dos destinos pela receita.");
        var required = configuration.Definition.Lines.Where(line => line.StartSource == SetpointStartSource.CurrentConfirmed ||
            configuration.Definition.CancellationPolicy == RampCancellationPolicy.RestoreSnapshot).ToArray();
        var directResources = required.Where(line => line.OxygenTarget != RampOxygenTarget.ActiveCascadeReference)
            .Select(line => CommandActuators.ForKey(KeyFor(line.Variable))!.Value).Distinct().ToArray();
        // Even an empty subset verifies that the generation was drained. Explicit starts do not
        // invent a prior desired state for a device that has never received a reference.
        var commands = arbiter.CaptureReservedDesiredState(authority, directResources).ToImmutableArray();
        var now = time.GetUtcNow();
        var references = ImmutableArray.CreateBuilder<RampCapturedReference>();
        foreach (var line in required)
        {
            double value;
            RampReferenceEvidence evidence;
            DateTimeOffset recorded;
            if (line.OxygenTarget == RampOxygenTarget.ActiveCascadeReference)
            {
                if (controller is null || !controller.WasActive || controller.ControllerId != configuration.CascadeNodeId ||
                    controller.StateVersion != "recipe-cascade-v1")
                    throw new InvalidOperationException("Referência inicial da cascata associada indisponível.");
                controller.Validate();
                using var json = JsonDocument.Parse(controller.StateJson);
                if (!json.RootElement.TryGetProperty("Pid", out var pid) || !pid.TryGetProperty("Setpoint", out var setpoint) ||
                    !setpoint.TryGetDouble(out value)) throw new InvalidOperationException("Snapshot sem referência da cascata.");
                evidence = RampReferenceEvidence.ControllerReference;
                recorded = now;
            }
            else
            {
                var key = KeyFor(line.Variable);
                var state = commands.Single(command => command.Actuator == CommandActuators.ForKey(key));
                value = RecipeAssayReturnState.Number(OpenTECCommand.Parse(state.DesiredCommandJson), key);
                var accepted = RecipeAssayReturnState.Number(OpenTECCommand.Parse(state.TransportAcceptedCommandJson), key);
                if (value != accepted) throw new InvalidOperationException("Referência inicial não aceita pelo transporte.");
                evidence = RampReferenceEvidence.TransportAccepted;
                recorded = state.TransportUpdatedUtc;
            }
            if (!double.IsFinite(value) || !DeviceRanges.Accepts(line.Variable, value))
                throw new InvalidOperationException("Referência capturada fora do envelope da rampa.");
            if (line.StartSource == SetpointStartSource.CurrentConfirmed) line.ValidateResolvedStart(value);
            references.Add(new(line.Variable, line.OxygenTarget, value, evidence, recorded));
        }
        return new(Guid.NewGuid(), authority.ExecutionId, authority.BlockId, now, references.ToImmutable(), commands,
            required.Any(line => line.OxygenTarget == RampOxygenTarget.ActiveCascadeReference) ? controller : null);
    }

    internal static string KeyFor(SetpointVariable variable) => variable switch
    {
        SetpointVariable.Temperature => CommandKeys.TempSetpoint,
        SetpointVariable.Agitation => CommandKeys.MotorSetpoint,
        SetpointVariable.Flow => CommandKeys.FlowSetpoint,
        SetpointVariable.Oxygen => CommandKeys.OxygenMonitor,
        SetpointVariable.Pressure => CommandKeys.PressureReference,
        SetpointVariable.Ph => CommandKeys.PHSetpoint,
        _ => throw new ArgumentException("Parâmetro desconhecido.", nameof(variable))
    };
}
