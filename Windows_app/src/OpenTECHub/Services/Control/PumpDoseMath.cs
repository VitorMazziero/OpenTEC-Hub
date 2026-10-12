using OpenTECHub.Protocol;

namespace OpenTECHub.Services.Control;

/// <summary>The dose point the pulses run on: one fixed speed and the flow measured there.</summary>
/// <param name="Speed">Motor speed S during a pulse.</param>
/// <param name="RateMlMin">Flow measured at <paramref name="Speed"/>, mL/min.</param>
/// <param name="DeadMs">Start-up dead time t₀ from the pulse check, ms; null = not measured (sent as 0).</param>
/// <param name="RecommendedMinOnMs">Shortest delivering time that meets the precision target, from the pulse check; null = not measured.</param>
public sealed record PumpDoseSource(double Speed, double RateMlMin, double? DeadMs = null, double? RecommendedMinOnMs = null);

/// <summary>Operator limits on the slider.</summary>
/// <param name="MaxIntervalS">Longest time between pulses (bolus size), s. Default 30 min (D2).</param>
/// <param name="UnverifiedMinOnMs">Recommended minimum delivering time when no pulse check exists, ms.</param>
public sealed record PumpDoseLimits(double MaxIntervalS = 1800.0, double UnverifiedMinOnMs = 2000.0);

/// <summary>Where a pulse length falls.</summary>
public enum PumpDoseZone
{
    /// <summary>Below the firmware minimum: the node would refuse it.</summary>
    Unavailable,

    /// <summary>Allowed, but shorter than the recommended minimum: start-up error may dominate.</summary>
    LowPrecision,

    Good,
}

/// <summary>The span of periods the slider can reach for one mean flow and dose point.</summary>
public sealed record PumpDoseRange(
    double MinPeriodS,
    double MaxPeriodS,
    double RecommendedMinPeriodS,
    double Duty,
    bool Feasible,
    string? Reason);

/// <summary>One concrete pulse schedule, exactly what would be sent.</summary>
public sealed record PumpDosePlan(
    double MeanFlowMlMin,
    double PeriodS,
    double VolumeMl,
    double DeliveryMs,
    double OnMs,
    double Duty,
    double PulsesPerHour,
    double? TotalPulses,
    double? StartErrorFraction,
    PumpDoseZone Zone,
    PumpDoseParameters Parameters);

/// <summary>Sampled pulse train and delivered-volume staircase for the preview charts.</summary>
public sealed record PumpDosePreview(
    IReadOnlyList<double> TrainMinutes,
    IReadOnlyList<double> TrainFlowMlMin,
    IReadOnlyList<double> VolumeMinutes,
    IReadOnlyList<double> VolumeMl);

/// <summary>
/// Pulse-dosing arithmetic for the card's granularity slider (plan 2026-10-11 §4).
/// </summary>
/// <remarks>
/// With the mean flow <c>Q̄</c> and the dose point <c>Q_d</c> fixed, the fraction of time the
/// motor runs, <c>d = Q̄/Q_d</c>, is fixed too. The only freedom left is how to split the same
/// volume in time, so one slider is enough: it picks the period <c>T</c> on a log scale and
/// everything else follows (<c>v = Q̄·T/60</c>, <c>t_on = 60·v/Q_d + t₀</c>). The limits are
/// the firmware's (<see cref="PumpDoseRules"/>) plus the operator's longest interval.
/// </remarks>
public static class PumpDoseMath
{
    /// <summary>Above this fraction of time running, pulses stop making sense.</summary>
    public const double MaxDuty = 0.5;

    public static PumpDoseRange Range(double meanFlowMlMin, PumpDoseSource source, PumpDoseLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        limits ??= new PumpDoseLimits();

        if (!double.IsFinite(meanFlowMlMin) || meanFlowMlMin <= 0.0)
        {
            return Infeasible(0.0, "Defina uma vazão média maior que zero.");
        }

        if (!double.IsFinite(source.RateMlMin) || source.RateMlMin <= 0.0)
        {
            return Infeasible(0.0, "Falta a vazão medida no ponto de dosagem.");
        }

        var duty = meanFlowMlMin / source.RateMlMin;
        if (duty > MaxDuty)
        {
            return Infeasible(duty,
                "A vazão média passa da metade da vazão do ponto de dosagem: calibre um ponto com velocidade maior ou use o modo contínuo.");
        }

        var dead = source.DeadMs ?? 0.0;
        var minPeriod = Math.Max(PumpDoseRules.MinPeriodS, PumpDoseRules.MinOnMs / 1000.0 / duty);
        minPeriod = Math.Max(minPeriod, (dead + PumpDoseRules.GapMs) / (1000.0 * (1.0 - duty)));
        var maxPeriod = Math.Min(limits.MaxIntervalS, PumpDoseRules.MaxPeriodS);
        maxPeriod = Math.Min(maxPeriod, 60.0 * PumpDoseRules.MaxVolumeMl / meanFlowMlMin);

        minPeriod = RoundUp(minPeriod);
        maxPeriod = RoundDown(maxPeriod);
        if (minPeriod > maxPeriod)
        {
            return Infeasible(duty, "Nenhum período cabe entre o pulso mínimo e o intervalo máximo; aumente o intervalo máximo.");
        }

        var recommendedOnMs = source.RecommendedMinOnMs ?? limits.UnverifiedMinOnMs;
        var recommended = Math.Clamp(RoundUp(recommendedOnMs / 1000.0 / duty), minPeriod, maxPeriod);
        return new PumpDoseRange(minPeriod, maxPeriod, recommended, duty, true, null);
    }

    /// <summary>
    /// The schedule for <paramref name="periodS"/>, clamped into the range and rounded to a
    /// readable step; volume and pulse length are recomputed from the rounded period.
    /// </summary>
    public static PumpDosePlan? Plan(
        double meanFlowMlMin,
        double totalMl,
        PumpDoseSource source,
        double periodS,
        PumpDoseLimits? limits = null)
    {
        var range = Range(meanFlowMlMin, source, limits);
        if (!range.Feasible)
        {
            return null;
        }

        var period = Math.Clamp(RoundPeriod(periodS), range.MinPeriodS, range.MaxPeriodS);
        var volume = meanFlowMlMin * period / 60.0;
        var dead = source.DeadMs ?? 0.0;
        var deliveryMs = PumpDoseRules.DeliveryMs(volume, source.RateMlMin);
        var parameters = new PumpDoseParameters(
            Math.Round(volume, 4), period, source.Speed, source.RateMlMin,
            Math.Max(0.0, totalMl), Math.Round(dead));

        var recommendedOnMs = source.RecommendedMinOnMs ?? (limits ?? new PumpDoseLimits()).UnverifiedMinOnMs;
        var zone = PumpDoseRules.Validate(parameters) is not null || deliveryMs < PumpDoseRules.MinOnMs
            ? PumpDoseZone.Unavailable
            : deliveryMs < recommendedOnMs ? PumpDoseZone.LowPrecision : PumpDoseZone.Good;

        return new PumpDosePlan(
            meanFlowMlMin,
            period,
            parameters.VolumeMl,
            deliveryMs,
            deliveryMs + dead,
            range.Duty,
            3600.0 / period,
            totalMl > 0.0 ? TotalPulses(totalMl, parameters.VolumeMl, source.RateMlMin) : null,
            source.DeadMs is { } t0 && t0 > 0.0 ? t0 / (deliveryMs + t0) : null,
            zone,
            parameters);
    }

    /// <summary>Slider position 0..1 → period, logarithmic between the range ends.</summary>
    public static double SliderToPeriod(double position, PumpDoseRange range)
    {
        ArgumentNullException.ThrowIfNull(range);
        var p = Math.Clamp(position, 0.0, 1.0);
        if (range.MaxPeriodS <= range.MinPeriodS)
        {
            return range.MinPeriodS;
        }

        var period = range.MinPeriodS * Math.Pow(range.MaxPeriodS / range.MinPeriodS, p);
        return Math.Clamp(RoundPeriod(period), range.MinPeriodS, range.MaxPeriodS);
    }

    /// <summary>Period → slider position 0..1 (inverse of <see cref="SliderToPeriod"/>).</summary>
    public static double PeriodToSlider(double periodS, PumpDoseRange range)
    {
        ArgumentNullException.ThrowIfNull(range);
        if (range.MaxPeriodS <= range.MinPeriodS)
        {
            return 0.0;
        }

        var period = Math.Clamp(periodS, range.MinPeriodS, range.MaxPeriodS);
        return Math.Log(period / range.MinPeriodS) / Math.Log(range.MaxPeriodS / range.MinPeriodS);
    }

    /// <summary>Rounds to 1 s below 2 min, 5 s up to 10 min and 15 s above, so what is shown is what is sent.</summary>
    public static double RoundPeriod(double periodS)
    {
        var step = Step(periodS);
        return Math.Max(step, Math.Round(periodS / step) * step);
    }

    /// <summary>
    /// Pulse train over the first <paramref name="detailMinutes"/> and the delivered-volume
    /// staircase over the whole run (or <paramref name="runMinutes"/> without a total).
    /// </summary>
    public static PumpDosePreview Preview(PumpDosePlan plan, double detailMinutes = 30.0, double runMinutes = 720.0)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var periodMin = plan.PeriodS / 60.0;
        var onMin = plan.OnMs / 60000.0;
        var rate = plan.Parameters.RateMlMin;

        var trainT = new List<double> { 0.0 };
        var trainQ = new List<double> { 0.0 };
        var window = Math.Max(detailMinutes, 6.0 * periodMin);
        for (var start = 0.0; start < window && trainT.Count < 4000; start += periodMin)
        {
            trainT.AddRange([start, start, start + onMin, start + onMin]);
            trainQ.AddRange([0.0, rate, rate, 0.0]);
        }

        trainT.Add(window);
        trainQ.Add(0.0);

        var total = plan.Parameters.TotalMl;
        var pulses = plan.TotalPulses ?? Math.Floor(runMinutes / periodMin);
        var stride = Math.Max(1, (int)Math.Ceiling(pulses / 1000.0)); // keep the staircase drawable
        var volT = new List<double> { 0.0 };
        var volV = new List<double> { 0.0 };
        var delivered = 0.0;
        for (var k = 0; k < pulses; k += stride)
        {
            var end = (k * periodMin) + onMin;
            volT.Add(end);
            volV.Add(delivered);
            delivered = Math.Min(total > 0.0 ? total : double.MaxValue, (k + stride) * plan.VolumeMl);
            volT.Add(end);
            volV.Add(delivered);
        }

        return new PumpDosePreview(trainT, trainQ, volT, volV);
    }

    /// <summary>
    /// Pulses until the total, as the node counts them: a last remainder shorter than the
    /// minimum pulse is not dosed (the firmware ends with "volume total atingido").
    /// </summary>
    public static double TotalPulses(double totalMl, double volumeMl, double rateMlMin)
    {
        var full = Math.Floor((totalMl + 1e-9) / volumeMl);
        var remainder = totalMl - (full * volumeMl);
        return full + (PumpDoseRules.DeliveryMs(remainder, rateMlMin) >= PumpDoseRules.MinOnMs ? 1 : 0);
    }

    private static PumpDoseRange Infeasible(double duty, string reason)
        => new(0.0, 0.0, 0.0, duty, false, reason);

    private static double Step(double periodS) => periodS < 120.0 ? 1.0 : periodS < 600.0 ? 5.0 : 15.0;

    private static double RoundUp(double periodS)
    {
        var step = Step(periodS);
        return Math.Ceiling((periodS - 1e-9) / step) * step;
    }

    private static double RoundDown(double periodS)
    {
        var step = Step(periodS);
        return Math.Floor((periodS + 1e-9) / step) * step;
    }
}
