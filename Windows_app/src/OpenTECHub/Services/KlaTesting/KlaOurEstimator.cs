using System;
using System.Collections.Immutable;
using System.Linq;

namespace OpenTECHub.Services.KlaTesting;

public static class KlaOurEstimator
{
    public static KlaOurEstimate Estimate(KlaDeterministicRequest request, KlaIndexWindow? window)
    {
        var reasons = ImmutableArray.CreateBuilder<string>();
        KlaOurEstimate Refuse(string reason, KlaScientificQuality quality = KlaScientificQuality.Inconclusive)
            => new(null, null, quality, window, null, null, null, [reason]);
        if (request.RemovalMode == KlaGasRemovalMode.NitrogenStripping)
        {
            return Refuse("our_not_applicable_to_nitrogen_stripping", KlaScientificQuality.NotApplicable);
        }
        if (window is null || window.Start < 0 || window.End >= request.Samples.Length ||
            window.Count < request.Config.MinimumOurPoints || string.IsNullOrWhiteSpace(window.Origin))
        {
            return Refuse("insufficient_respiratory_window");
        }
        var samples = request.Samples.Skip(window.Start).Take(window.Count).ToArray();
        if (samples.Any(x => !x.IsValid || !x.IsNewOxygenSample || !x.ConditionStable || !double.IsFinite(x.CalibratedDoPercent)))
        {
            return Refuse("invalid_or_disturbed_respiratory_window");
        }
        var t = samples.Select(x => x.Seconds).ToArray(); var y = samples.Select(x => x.CalibratedDoPercent).ToArray();
        if (t[^1] - t[0] < request.Config.MinimumSpanSeconds || t.Skip(1).Where((x, i) =>
            x <= t[i] || x - t[i] > request.Config.MaximumSampleGapSeconds).Any())
        {
            return Refuse("short_or_noncontinuous_respiratory_window");
        }
        var fit = KlaNumerics.Ols(t, y);
        if (fit is null || fit.Slope >= 0)
        {
            return Refuse("nonpositive_or_undefined_our");
        }
        var middle = t.Length / 2;
        var a = KlaNumerics.Ols(t.Take(middle + 1).ToArray(), y.Take(middle + 1).ToArray());
        var b = KlaNumerics.Ols(t.Skip(middle).ToArray(), y.Skip(middle).ToArray());
        var noise = Math.Max(request.Config.NoiseFloorPercent, fit.ResidualSd);
        if (fit.R2 < request.Config.MinimumR2) { reasons.Add("respiratory_fit_r2_below_threshold"); }
        if (Math.Abs(y[^1] - y[0]) / (Math.Sqrt(2) * noise) < request.Config.MinimumSignalToNoise) { reasons.Add("respiratory_signal_too_small"); }
        if (a is null || b is null || Math.Abs(a.Slope - b.Slope) / Math.Abs(fit.Slope) > request.Config.MaximumSlopeChange)
        {
            reasons.Add("respiration_curved_or_oxygen_limited");
        }
        if (fit.SlopeStandardError / Math.Abs(fit.Slope) > request.Config.MaximumRelativeSlopeError) { reasons.Add("our_slope_uncertain"); }
        if (Math.Abs(fit.Lag1Autocorrelation) > request.Config.MaximumResidualAutocorrelation) { reasons.Add("correlated_respiratory_residuals"); }
        if (reasons.Count > 0)
        {
            return new(null, null, KlaScientificQuality.Inconclusive, window, fit, null, null, reasons.ToImmutable());
        }
        var quality = KlaScientificQuality.Valid;
        if (request.Probe.ResponseTimeSeconds is null && !request.ProbeResponseNegligibleIndependentlyVerified)
        {
            quality = KlaScientificQuality.Conditional;
            reasons.Add("respiratory_transient_probe_response_not_independently_verified");
        }
        if (!request.ResidualTransferNegligible)
        {
            quality = KlaScientificQuality.Conditional;
            reasons.Add("apparent_consumption_residual_transfer_not_verified");
        }
        var value = -3600 * fit.Slope;
        var half = KlaNumerics.Student95(t.Length - 2) * fit.SlopeStandardError * 3600;
        double? concentration = null;
        if (request.ReferenceConcentrationMmolPerL is { } reference && reference > 0 &&
            double.IsFinite(reference) && !string.IsNullOrWhiteSpace(request.ReferenceConcentrationSource))
        {
            concentration = value * reference / 100;
        }
        else
        {
            reasons.Add("concentration_conversion_unavailable_without_reference");
        }
        return new(value, concentration, quality, window, fit, value - half, value + half, reasons.ToImmutable());
    }
}
