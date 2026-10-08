using System.Collections.Immutable;

namespace OpenTECHub.Services.Recipes;

public sealed record LinearRampSample(SetpointVariable Variable, RampOxygenTarget? OxygenTarget,
    double Reference, bool AtFinalTarget);

/// <summary>Frozen trajectory in active seconds. Destination adapters supply their actual wire quantization.</summary>
public sealed class LinearSetpointRampTrajectory
{
    private sealed record Resolved(LinearSetpointRampLine Line, double Start, double Final);
    private readonly ImmutableArray<Resolved> _lines;
    private readonly Func<SetpointVariable, double, double> _quantize;

    public LinearSetpointRampTrajectory(LinearSetpointRampDefinition definition,
        IReadOnlyDictionary<SetpointVariable, double> confirmedStarts,
        Func<SetpointVariable, double, double> quantize)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(confirmedStarts);
        ArgumentNullException.ThrowIfNull(quantize);
        definition.Validate();
        _quantize = quantize;
        var resolved = ImmutableArray.CreateBuilder<Resolved>();
        foreach (var line in definition.Lines)
        {
            var start = line.StartSource == SetpointStartSource.Explicit ? line.InitialSetpoint!.Value :
                confirmedStarts.TryGetValue(line.Variable, out var captured) ? captured :
                throw new ArgumentException($"Referência confirmada ausente: {line.Variable}.");
            line.ValidateResolvedStart(start);
            var final = Quantized(line.Variable, line.FinalSetpoint);
            // Quantization must also keep the whole continuous path inside the operating envelope.
            var quantizedStart = Quantized(line.Variable, start);
            (line with { StartSource = SetpointStartSource.Explicit, InitialSetpoint = quantizedStart,
                FinalSetpoint = final }).Validate();
            resolved.Add(new(line, start, final));
        }
        _lines = resolved.ToImmutable();
        DurationSeconds = _lines.Max(line => line.Line.EndAfterSeconds);
    }

    public double DurationSeconds { get; }

    public ImmutableArray<LinearRampSample> Sample(double activeSeconds)
    {
        if (!double.IsFinite(activeSeconds) || activeSeconds < 0) throw new ArgumentOutOfRangeException(nameof(activeSeconds));
        return _lines.Select(resolved =>
        {
            var finished = activeSeconds >= resolved.Line.EndAfterSeconds;
            var fraction = Math.Min(1, activeSeconds / resolved.Line.EndAfterSeconds);
            var value = finished ? resolved.Final : Quantized(resolved.Line.Variable,
                resolved.Start + (resolved.Line.FinalSetpoint - resolved.Start) * fraction);
            return new LinearRampSample(resolved.Line.Variable, resolved.Line.OxygenTarget, value, finished);
        }).ToImmutableArray();
    }

    private double Quantized(SetpointVariable variable, double value)
    {
        var quantized = _quantize(variable, value);
        if (!double.IsFinite(quantized) || !DeviceRanges.Accepts(variable, quantized))
            throw new ArgumentException($"Referência quantizada fora da faixa: {variable}.");
        return quantized;
    }
}
