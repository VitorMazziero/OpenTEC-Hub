namespace OpenTECHub.Services.Telemetry;

/// <summary>
/// Display-only Hampel filter for chart series: a sample further than a robust threshold from the
/// median of its neighbours is drawn at that median. Isolated spikes (a servo frame reporting 0 rpm)
/// disappear; real steps survive because the window median follows them within half a window.
/// Recorded and exported data are never altered.
/// </summary>
public static class ChartOutlierFilter
{
    /// <summary>Neighbours on each side; spikes up to this many samples wide are removed.</summary>
    public const int HalfWindow = 3;

    /// <summary>Threshold in robust standard deviations (1.4826 × MAD).</summary>
    public const double Sigmas = 4;

    /// <summary>
    /// Minimum spread, as a fraction of the local median, so a quiet signal (MAD near zero) does not
    /// turn ordinary noise into outliers.
    /// </summary>
    public const double RelativeFloor = 0.02;

    public static double[] Apply(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = values.ToArray();
        if (values.Count < 2 * HalfWindow + 1) return result;
        var finite = values.Where(double.IsFinite).ToArray();
        if (finite.Length == 0) return result;
        var rangeFloor = 0.01 * (finite.Max() - finite.Min());
        var window = new List<double>(2 * HalfWindow + 1);
        var deviations = new List<double>(2 * HalfWindow + 1);
        for (var i = 0; i < values.Count; i++)
        {
            if (!double.IsFinite(values[i])) continue;
            window.Clear();
            for (var j = Math.Max(0, i - HalfWindow); j <= Math.Min(values.Count - 1, i + HalfWindow); j++)
                if (j != i && double.IsFinite(values[j])) window.Add(values[j]);
            if (window.Count < HalfWindow + 1) continue;
            var median = Median(window);
            deviations.Clear();
            foreach (var value in window) deviations.Add(Math.Abs(value - median));
            var spread = Math.Max(1.4826 * Median(deviations), Math.Max(RelativeFloor * Math.Abs(median), rangeFloor));
            if (spread > 0 && Math.Abs(values[i] - median) > Sigmas * spread) result[i] = median;
        }
        return result;
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        var middle = values.Count / 2;
        return values.Count % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2;
    }
}
