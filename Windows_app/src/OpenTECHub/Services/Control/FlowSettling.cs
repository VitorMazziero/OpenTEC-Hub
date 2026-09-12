using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenTECHub.Services.Control;

/// <summary>
/// "Has the flow settled?" — the one criterion both assays use while the air is going out of
/// the vent (C) before the switch to the reactor (A).
/// </summary>
/// <remarks>
/// A tolerance band alone holds a run hostage to the controller's steady offset: the bench
/// settles at +0.07…+0.10 L/min, on the edge of a 0.1 band, and the reading only "falls" inside
/// it by noise. What the switch needs is a flow that is no longer moving and is near the target;
/// the value it actually delivers is measured again in the reactor. Pure, so the criterion is
/// testable on its own and identical in the power and kLa runners.
/// </remarks>
public static class FlowSettling
{
    /// <summary>
    /// True when <paramref name="window"/> holds at least <paramref name="requiredSamples"/> readings
    /// whose sample standard deviation is at most <paramref name="maxStdDevLpm"/> and whose mean is
    /// within <paramref name="maxErrorLpm"/> of <paramref name="targetFlow"/>.
    /// </summary>
    public static bool HasSettled(
        IReadOnlyList<double> window,
        double targetFlow,
        int requiredSamples,
        double maxStdDevLpm,
        double maxErrorLpm,
        out double standardDeviation)
    {
        standardDeviation = double.NaN;
        var required = Math.Max(2, requiredSamples);
        if (window.Count < required || maxStdDevLpm <= 0)
        {
            return false;
        }

        var mean = window.Average();
        standardDeviation = Math.Sqrt(window.Sum(v => (v - mean) * (v - mean)) / (window.Count - 1));
        return standardDeviation <= maxStdDevLpm && Math.Abs(mean - targetFlow) <= maxErrorLpm;
    }

    /// <summary>Keeps <paramref name="window"/> to its last <paramref name="requiredSamples"/> (at least 2) readings.</summary>
    public static void Push(List<double> window, double reading, int requiredSamples)
    {
        window.Add(reading);
        var keep = Math.Max(2, requiredSamples);
        while (window.Count > keep)
        {
            window.RemoveAt(0);
        }
    }
}
