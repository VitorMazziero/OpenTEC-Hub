using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace OpenTECHub.Services.Recipes;

public sealed record RecipeRampBlockConfiguration(LinearSetpointRampDefinition Definition, string? CascadeNodeId)
{
    public static RecipeRampBlockConfiguration Read(RecipeNode node)
    {
        if (node.Type != NodeType.LinearSetpointRamp) throw new ArgumentException("Bloco não é rampa.");
        if (node.Parameters["lines"] is not JsonArray rows || rows.Count == 0) throw new ArgumentException("Rampa requer parâmetros.");
        var lines = ImmutableArray.CreateBuilder<LinearSetpointRampLine>();
        foreach (var entry in rows)
        {
            if (entry is not JsonObject row) throw new ArgumentException("Linha de rampa inválida.");
            var variable = Choice<SetpointVariable>(row, "variable");
            var source = Choice<SetpointStartSource>(row, "startSource");
            lines.Add(new() { Variable = variable, StartSource = source,
                InitialSetpoint = source == SetpointStartSource.Explicit ? Number(row, "initialSetpoint") : null,
                FinalSetpoint = Number(row, "finalSetpoint"), EndAfterSeconds = Number(row, "endAfterSeconds"),
                OxygenTarget = variable == SetpointVariable.Oxygen ? Choice<RampOxygenTarget>(row, "oxygenTarget") : null });
        }
        var definition = new LinearSetpointRampDefinition { Lines = lines.ToImmutable(),
            CancellationPolicy = Choice<RampCancellationPolicy>(node.Parameters, "cancellationPolicy") };
        definition.Validate();
        var needsCascade = definition.Lines.Any(line => line.OxygenTarget == RampOxygenTarget.ActiveCascadeReference);
        var cascade = needsCascade ? node.Text("cascadeNodeId") : null;
        if (needsCascade && string.IsNullOrWhiteSpace(cascade))
            throw new ArgumentException("Rampa da referência de O₂ requer controle associado.");
        return new(definition, string.IsNullOrWhiteSpace(cascade) ? null : cascade);
    }

    private static double Number(JsonObject row, string key) => row[key] is JsonValue value &&
        RecipeNode.TryReadNumber(value, out var number) ? number : throw new ArgumentException($"Número ausente: {key}.");
    private static T Choice<T>(JsonObject row, string key) where T : struct, Enum =>
        row[key] is JsonValue value && value.TryGetValue<string>(out var text) &&
        Enum.TryParse<T>(text, out var parsed) && Enum.IsDefined(parsed) && parsed.ToString() == text
            ? parsed : throw new ArgumentException($"Opção inválida: {key}.");
}
