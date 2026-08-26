using TecnalHub.Protocol;

namespace TecnalHub.Services.Control;

/// <summary>
/// A complete external-pump profile: the mode, its operating time window and every mode's
/// parameters, gathered into one value the preview and the wire builder both consume.
/// </summary>
/// <remarks>
/// Times are in minutes, flows in mL/min — the same units v.6's <c>pump_mode_window</c> uses.
/// Only the fields the selected <see cref="Mode"/> needs are read; the rest are ignored, so a
/// ViewModel can keep every mode's last-entered values staged at once.
/// </remarks>
public sealed record PumpProfileSpec(
    PumpProfileMode Mode,
    double InitMinutes,
    double FinalMinutes,
    double Lambda,
    double Phi,
    IReadOnlyList<double> PolynomialCoefficients,
    IReadOnlyList<double> PiecewiseTimes,
    IReadOnlyList<double> PiecewiseFlows);

/// <summary>Sampled flow and accumulated-volume curves for the profile preview.</summary>
public sealed record PumpPreview(
    IReadOnlyList<double> Minutes,
    IReadOnlyList<double> FlowMlPerMin,
    IReadOnlyList<double> VolumeMl,
    double PeakFlowMlPerMin,
    double TotalVolumeMl);

/// <summary>
/// Pure flow-profile mathematics, ported from v.6 <c>pump_mode_window.run_simulation</c>.
/// </summary>
/// <remarks>
/// The wire builders live in <see cref="CommandBuilders"/>; this adds the two things the wire
/// cannot: the flow value at a time (for the live preview) and the mode dispatch that turns a
/// <see cref="PumpProfileSpec"/> into the exact v.6 frame. It has no UI dependency and is
/// exercised headlessly.
/// </remarks>
public static class PumpProfileMath
{
    /// <summary>
    /// Flow in mL/min at absolute time <paramref name="tMinutes"/>, honouring the profile's
    /// start (flow is zero before <see cref="PumpProfileSpec.InitMinutes"/>) and never negative,
    /// exactly as the v.6 simulation clamps it.
    /// </summary>
    public static double FlowAt(PumpProfileSpec spec, double tMinutes)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (tMinutes < spec.InitMinutes)
        {
            return 0.0;
        }

        var t = tMinutes - spec.InitMinutes; // relative time t'
        var q = spec.Mode switch
        {
            PumpProfileMode.Constant => spec.Lambda,
            PumpProfileMode.Linear => spec.Lambda + (spec.Phi * t),
            PumpProfileMode.Exponential => spec.Lambda * Math.Exp(spec.Phi * t),
            PumpProfileMode.Polynomial => Horner(spec.PolynomialCoefficients, t),
            PumpProfileMode.Piecewise => Interpolate(spec.PiecewiseTimes, spec.PiecewiseFlows, t),
            _ => 0.0,
        };

        return double.IsFinite(q) ? Math.Max(q, 0.0) : 0.0;
    }

    /// <summary>
    /// Samples flow and cumulative volume over <c>[0, final]</c> at <paramref name="count"/> points.
    /// </summary>
    /// <remarks>
    /// Volume is the trapezoidal integral of the flow: <c>∫ Q[mL/min] dt[min] = mL</c>, matching
    /// v.6's cumulative-trapezoid volume (its per-second scaling cancels in the integral).
    /// </remarks>
    public static PumpPreview Sample(PumpProfileSpec spec, int count = 240)
    {
        ArgumentNullException.ThrowIfNull(spec);
        count = Math.Max(count, 2);

        var horizon = spec.FinalMinutes > 0 ? spec.FinalMinutes : Math.Max(spec.InitMinutes, 1.0);
        var minutes = new double[count];
        var flow = new double[count];
        var volume = new double[count];

        var peak = 0.0;
        for (var i = 0; i < count; i++)
        {
            var t = horizon * i / (count - 1);
            minutes[i] = t;
            flow[i] = FlowAt(spec, t);
            peak = Math.Max(peak, flow[i]);

            if (i > 0)
            {
                var dt = minutes[i] - minutes[i - 1];
                volume[i] = volume[i - 1] + (0.5 * (flow[i] + flow[i - 1]) * dt);
            }
        }

        return new PumpPreview(minutes, flow, volume, peak, volume[count - 1]);
    }

    /// <summary>Turns a spec into the exact v.6 wire frame for its mode.</summary>
    public static TecnalCommand BuildCommand(PumpProfileSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        return spec.Mode switch
        {
            PumpProfileMode.Constant =>
                CommandBuilders.PumpConstant(spec.InitMinutes, spec.FinalMinutes, spec.Lambda),
            PumpProfileMode.Linear =>
                CommandBuilders.PumpLinear(spec.InitMinutes, spec.FinalMinutes, spec.Lambda, spec.Phi),
            PumpProfileMode.Exponential =>
                CommandBuilders.PumpExponential(spec.InitMinutes, spec.FinalMinutes, spec.Lambda, spec.Phi),
            PumpProfileMode.Polynomial =>
                CommandBuilders.PumpPolynomial(spec.InitMinutes, spec.FinalMinutes, spec.PolynomialCoefficients),
            PumpProfileMode.Piecewise =>
                CommandBuilders.PumpPiecewise(
                    spec.InitMinutes, spec.FinalMinutes, spec.PiecewiseTimes, spec.PiecewiseFlows),
            _ => throw new ArgumentOutOfRangeException(nameof(spec), spec.Mode, "Idle is not an operating profile."),
        };
    }

    private static double Horner(IReadOnlyList<double> coefficients, double t)
    {
        if (coefficients is null || coefficients.Count == 0)
        {
            return 0.0;
        }

        var acc = coefficients[^1];
        for (var i = coefficients.Count - 2; i >= 0; i--)
        {
            acc = (acc * t) + coefficients[i];
        }

        return acc;
    }

    private static double Interpolate(IReadOnlyList<double> times, IReadOnlyList<double> flows, double t)
    {
        if (times is null || flows is null || times.Count == 0 || times.Count != flows.Count)
        {
            return 0.0;
        }

        // np.interp semantics: clamp to the endpoints outside the sampled range.
        if (t <= times[0])
        {
            return flows[0];
        }

        if (t >= times[^1])
        {
            return flows[^1];
        }

        for (var i = 1; i < times.Count; i++)
        {
            if (t <= times[i])
            {
                var span = times[i] - times[i - 1];
                if (span <= 0)
                {
                    return flows[i];
                }

                var fraction = (t - times[i - 1]) / span;
                return flows[i - 1] + (fraction * (flows[i] - flows[i - 1]));
            }
        }

        return flows[^1];
    }
}
