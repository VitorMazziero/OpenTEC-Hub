using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace OpenTECHub.Services.KlaTesting;

public static class KlaWindowSelector
{
    public static KlaWindowAssessment Assess(ImmutableArray<KlaObservation> samples, double ceq,
        KlaIndexWindow window, KlaDeterministicConfig cfg, double ceqSe = 0)
    {
        var reasons = ImmutableArray.CreateBuilder<string>();
        KlaWindowAssessment Refuse(string reason) => new(window, false, null, null, null, null, null, null, [reason]);
        if (window.Start < 0 || window.End >= samples.Length || window.Count < cfg.MinimumPoints || string.IsNullOrWhiteSpace(window.Origin))
        {
            return Refuse("invalid_or_short_window");
        }
        var selected = samples.Skip(window.Start).Take(window.Count).ToArray();
        if (selected.Any(x => !x.IsValid || !x.IsNewOxygenSample || !x.ConditionStable || !double.IsFinite(x.CalibratedDoPercent)))
        {
            return Refuse("invalid_or_disturbed_observation_in_window");
        }
        if (!double.IsFinite(ceq) || selected.Any(x => ceq - x.CalibratedDoPercent <= cfg.MinimumDrivingForcePercent))
        {
            return Refuse("driving_force_at_or_below_floor_in_window");
        }
        var t = selected.Select(x => x.Seconds).ToArray();
        if (t[^1] - t[0] < cfg.MinimumSpanSeconds || t.Skip(1).Where((x, i) =>
            !double.IsFinite(x) || x <= t[i] || x - t[i] > cfg.MaximumSampleGapSeconds).Any())
        {
            return Refuse("short_span_or_noncontinuous_time");
        }
        var y = selected.Select(x => x.CalibratedDoPercent).ToArray();
        var log = y.Select(x => Math.Log(ceq - x)).ToArray();
        var fit = KlaNumerics.Ols(t, log);
        if (fit is null || !double.IsFinite(fit.Slope) || fit.Slope >= 0)
        {
            return Refuse("nonpositive_or_undefined_kla");
        }
        var middle = selected.Length / 2;
        var first = KlaNumerics.Ols(t.Take(middle + 1).ToArray(), log.Take(middle + 1).ToArray());
        var last = KlaNumerics.Ols(t.Skip(middle).ToArray(), log.Skip(middle).ToArray());
        var slopeChange = first is null || last is null ? double.PositiveInfinity
            : Math.Abs(first.Slope - last.Slope) / Math.Abs(fit.Slope);
        double? Sensitivity(double alternativeCeq)
        {
            if (y.Any(x => alternativeCeq - x <= cfg.MinimumDrivingForcePercent))
            {
                return null;
            }
            return KlaNumerics.Ols(t, y.Select(x => Math.Log(alternativeCeq - x)).ToArray())?.Slope;
        }
        var step = Math.Max(cfg.CeqSensitivityStepPercent, ceqSe);
        var low = Sensitivity(ceq - step); var high = Sensitivity(ceq + step);
        var sensitivity = low is null || high is null ? double.PositiveInfinity
            : Math.Max(Math.Abs(low.Value - fit.Slope), Math.Abs(high.Value - fit.Slope)) / Math.Abs(fit.Slope);
        var trim = Math.Max(1, selected.Length / 20);
        var trimmedStart = KlaNumerics.Ols(t.Skip(trim).ToArray(), log.Skip(trim).ToArray());
        var trimmedEnd = KlaNumerics.Ols(t.Take(t.Length - trim).ToArray(), log.Take(t.Length - trim).ToArray());
        var endpointSensitivity = trimmedStart is null || trimmedEnd is null ? double.PositiveInfinity :
            Math.Max(Math.Abs(trimmedStart.Slope - fit.Slope), Math.Abs(trimmedEnd.Slope - fit.Slope)) / Math.Abs(fit.Slope);
        // Transform log residuals back to DO scale to assess signal against actual observation noise.
        var residualDo = y.Select((v, i) => v - (ceq - Math.Exp(fit.Intercept + fit.Slope * t[i]))).ToArray();
        var noise = Math.Max(cfg.NoiseFloorPercent, Math.Sqrt(residualDo.Sum(x => x * x) / (y.Length - 2)));
        var signal = Math.Abs(y[^1] - y[0]) / (Math.Sqrt(2) * noise);
        if (fit.R2 < cfg.MinimumR2) { reasons.Add("log_fit_r2_below_threshold"); }
        if (signal < cfg.MinimumSignalToNoise) { reasons.Add("insufficient_signal_to_noise"); }
        if (fit.SlopeStandardError / Math.Abs(fit.Slope) > cfg.MaximumRelativeSlopeError) { reasons.Add("slope_uncertain"); }
        if (Math.Abs(fit.Lag1Autocorrelation) > cfg.MaximumResidualAutocorrelation) { reasons.Add("correlated_log_residuals"); }
        if (slopeChange > cfg.MaximumSlopeChange) { reasons.Add("nonconstant_rate_in_subwindows"); }
        if (sensitivity > cfg.MaximumCeqSensitivity) { reasons.Add("ceq_sensitive_or_invalid_deficit"); }
        if (endpointSensitivity > cfg.MaximumSlopeChange) { reasons.Add("endpoint_sensitive"); }
        return new(window, reasons.Count == 0, fit, -3600 * fit.Slope, signal,
            double.IsFinite(slopeChange) ? slopeChange : null,
            double.IsFinite(sensitivity) ? sensitivity : null,
            double.IsFinite(endpointSensitivity) ? endpointSensitivity : null, reasons.ToImmutable());
    }

    public static ImmutableArray<KlaWindowAssessment> Search(KlaDeterministicRequest request,
        ImmutableArray<KlaPhasePoint> phases, double ceq, double ceqSe)
    {
        if (request.RecoveryWindow is { } manual)
        {
            return [AssessPhaseWindow(request, phases, ceq, ceqSe, manual)];
        }
        var indices = phases.Where(p => p.Phase == KlaScientificPhase.GasOnRecovery).Select(p => p.Index).ToArray();
        var result = ImmutableArray.CreateBuilder<KlaWindowAssessment>();
        if (indices.Length < request.Config.MinimumPoints)
        {
            return [];
        }
        var count = Math.Min(indices.Length, request.Config.MaximumBoundaryCandidates);
        var boundaries = Enumerable.Range(0, count).Select(i => indices[(int)Math.Round((indices.Length - 1.0) * i / (count - 1))]).Distinct().ToArray();
        foreach (var start in boundaries)
        {
            foreach (var end in boundaries)
            {
                if (end - start + 1 < request.Config.MinimumPoints ||
                    request.Samples[end].Seconds - request.Samples[start].Seconds < request.Config.MinimumSpanSeconds)
                {
                    continue;
                }
                result.Add(AssessPhaseWindow(request, phases, ceq, ceqSe,
                    new(start, end, "deterministic_bounded_search_v1")));
            }
        }
        return result.ToImmutable();
    }

    private static KlaWindowAssessment AssessPhaseWindow(KlaDeterministicRequest request,
        ImmutableArray<KlaPhasePoint> phases, double ceq, double se, KlaIndexWindow window)
    {
        if (window.Start < 0 || window.End >= phases.Length || window.End < window.Start ||
            phases.Skip(window.Start).Take(window.Count).Any(p => p.Phase != KlaScientificPhase.GasOnRecovery))
        {
            return new(window, false, null, null, null, null, null, null, ["window_outside_recovery_phase"]);
        }
        return Assess(request.Samples, ceq, window, request.Config, se);
    }
}
