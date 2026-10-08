using System.Collections.Immutable;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;

namespace OpenTECHub.Services.Recipes;

public sealed record RecipeRampRestoreFrame(ImmutableArray<LinearRampSample> References, string CommandJson);

/// <summary>Build an entire recovery frame from captured, transport-accepted state before dispatch.</summary>
public static class RecipeRampRestoreCommands
{
    private static bool IsTransient(string key) => key is CommandKeys.BathSync or CommandKeys.BathAbort or
        CommandKeys.BathCascadeReset or CommandKeys.TempSetpointExact;

    public static RecipeRampRestoreFrame Build(RecipeRampStartCheckpoint start, CommandAuthorityLease authority)
    {
        ArgumentNullException.ThrowIfNull(start); ArgumentNullException.ThrowIfNull(authority);
        var configuration = start.Configuration;
        var initial = start.InitialState;
        configuration.Definition.Validate();
        if (start.SchemaVersion != 1 || start.InvocationId == Guid.Empty || initial.SnapshotId == Guid.Empty ||
            initial.CapturedUtc == default || configuration.Definition.CancellationPolicy != RampCancellationPolicy.RestoreSnapshot ||
            initial.ExecutionId != authority.ExecutionId || initial.NodeId != authority.BlockId ||
            authority.Owner != CommandOwner.Recipe ||
            RecipeRampInitialState.ResourcesFor(configuration.Definition).Any(resource => !authority.Resources.Contains(resource)))
            throw new InvalidOperationException("Recuperação exige reserva da mesma execução e bloco.");
        if (initial.References.IsDefault || initial.Commands.IsDefault ||
            initial.References.Length != configuration.Definition.Lines.Length ||
            initial.References.Select(reference => reference.Variable).Distinct().Count() != initial.References.Length)
            throw new InvalidOperationException("Referências anteriores incompletas.");
        var direct = configuration.Definition.Lines.Where(line => line.OxygenTarget != RampOxygenTarget.ActiveCascadeReference).ToArray();
        if (initial.Commands.Length != direct.Length || initial.Commands.Select(command => command.Actuator).Distinct().Count() != direct.Length)
            throw new InvalidOperationException("Comandos anteriores incompletos.");
        var command = OpenTECCommand.Create();
        var references = ImmutableArray.CreateBuilder<LinearRampSample>();
        foreach (var line in configuration.Definition.Lines)
        {
            var before = initial.References.SingleOrDefault(reference => reference.Variable == line.Variable);
            if (before is null || before.OxygenTarget != line.OxygenTarget || !double.IsFinite(before.Reference) ||
                !DeviceRanges.Accepts(line.Variable, before.Reference) || before.RecordedUtc > initial.CapturedUtc ||
                before.Evidence != (line.OxygenTarget == RampOxygenTarget.ActiveCascadeReference
                    ? RampReferenceEvidence.ControllerReference : RampReferenceEvidence.TransportAccepted))
                throw new InvalidOperationException("Referência anterior incompatível com o destino.");
            var represented = RecipeRampReferenceQuantization.Quantize(line.Variable, before.Reference, configuration.TemperatureRoute);
            references.Add(new(line.Variable, line.OxygenTarget, represented, true));
            if (line.OxygenTarget == RampOxygenTarget.ActiveCascadeReference) continue;
            var key = RecipeRampInitialState.KeyFor(line.Variable);
            var actuator = CommandActuators.ForKey(key)!.Value;
            var state = initial.Commands.SingleOrDefault(item => item.Actuator == actuator);
            if (state is null || state.Owner != CommandOwner.Recipe) throw new InvalidOperationException("Estado anterior do atuador ausente.");
            var desired = OpenTECCommand.Parse(state.DesiredCommandJson);
            var accepted = OpenTECCommand.Parse(state.TransportAcceptedCommandJson);
            if (desired.IsEmpty || desired.Keys.Any(field => CommandActuators.ForKey(field) != actuator) ||
                accepted.Keys.Any(field => CommandActuators.ForKey(field) != actuator) ||
                RecipeAssayReturnState.Number(desired, key) != before.Reference ||
                RecipeAssayReturnState.Number(accepted, key) != before.Reference)
                throw new InvalidOperationException("Comando anterior fora do escopo ou sem referência aceita.");
            foreach (var field in desired.Keys.Where(field => !IsTransient(field)))
                if (!SameValue(desired.GetRawValue(field), accepted.GetRawValue(field)))
                    throw new InvalidOperationException("Configuração anterior não aceita pelo transporte.");
            command.Merge(desired.SelectKeys(field => !IsTransient(field)));
            command.Set(key, represented);
            if (line.Variable == SetpointVariable.Temperature) command.Set(CommandKeys.TempSetpointExact, true);
        }
        return new(references.ToImmutable(), command.ToJson());
    }

    private static bool SameValue(string? first, string? second)
    {
        if (first is null || second is null) return false;
        if (first == second) return true;
        using var left = System.Text.Json.JsonDocument.Parse(first);
        using var right = System.Text.Json.JsonDocument.Parse(second);
        return left.RootElement.ValueKind == System.Text.Json.JsonValueKind.Number &&
            right.RootElement.ValueKind == System.Text.Json.JsonValueKind.Number && left.RootElement.GetDouble() == right.RootElement.GetDouble() ||
            left.RootElement.ValueKind == System.Text.Json.JsonValueKind.String &&
            right.RootElement.ValueKind == System.Text.Json.JsonValueKind.String && left.RootElement.GetString() == right.RootElement.GetString();
    }
}
