using System;
using System.Linq;

namespace OpenTECHub.Services.KlaTesting;

/// <summary>Weighted variable-projection NLS. Rate is auxiliary, never the reported kLa.</summary>
public static class KlaEquilibriumEstimator
{
    public static KlaEquilibriumEstimate Fit(KlaDeterministicRequest request, KlaIndexWindow window)
    {
        var cfg = request.Config;
        if (window.Start < 0 || window.End >= request.Samples.Length || window.Count < cfg.MinimumPoints)
        {
            return Refuse("insufficient_equilibrium_points", window);
        }
        var samples = request.Samples.Skip(window.Start).Take(window.Count).ToArray();
        if (samples.Any(x => !x.IsValid || !x.IsNewOxygenSample || !double.IsFinite(x.CalibratedDoPercent)))
        {
            return Refuse("invalid_equilibrium_observation", window);
        }
        if (request.ManualEquilibriumPercent is { } supplied)
        {
            return double.IsFinite(supplied) && supplied > samples.Min(x => x.CalibratedDoPercent) && supplied <= cfg.MaximumCeqPercent
                ? new(supplied, null, null, null, null, true, "operator_supplied_equilibrium", "manual_reference", window)
                : Refuse("invalid_manual_equilibrium", window);
        }
        var t = samples.Select(x => x.Seconds - samples[0].Seconds).ToArray();
        var y = samples.Select(x => x.CalibratedDoPercent).ToArray();
        var variance = y.Sum(x => Math.Pow(x - y.Average(), 2));
        if (variance < 1e-12 || t[^1] < cfg.MinimumSpanSeconds)
        {
            return Refuse("equilibrium_signal_not_identifiable", window);
        }
        // Python's linear_ramp supplies sigma (5 -> 1), so the objective uses 1/sigma².
        var weights = Enumerable.Range(0, t.Length).Select(i =>
            1 / Math.Pow(cfg.EquilibriumWeightRatio - (cfg.EquilibriumWeightRatio - 1) * i / (t.Length - 1), 2)).ToArray();

        (double Ceq, double A, double Objective, double Sse) At(double logRate)
        {
            var k = Math.Exp(logRate);
            double sw = 0, sx = 0, sxx = 0, sy = 0, sxy = 0;
            for (var i = 0; i < t.Length; i++)
            {
                var e = Math.Exp(-k * t[i]); var w = weights[i];
                sw += w; sx += w * e; sxx += w * e * e; sy += w * y[i]; sxy += w * e * y[i];
            }
            var det = sw * sxx - sx * sx;
            if (det <= 1e-16)
            {
                return (0, 0, double.PositiveInfinity, 0);
            }
            var ceq = (sy * sxx - sx * sxy) / det;
            var amplitude = -(sw * sxy - sx * sy) / det;
            if (ceq <= y.Min() || ceq >= cfg.MaximumCeqPercent || amplitude <= 0)
            {
                return (ceq, amplitude, double.PositiveInfinity, 0);
            }
            double objective = 0, sse = 0;
            for (var i = 0; i < t.Length; i++)
            {
                var residual = y[i] - (ceq - amplitude * Math.Exp(-k * t[i]));
                objective += weights[i] * residual * residual; sse += residual * residual;
            }
            return (ceq, amplitude, objective, sse);
        }

        var lower = Math.Log(cfg.MinimumRatePerSecond); var upper = Math.Log(cfg.MaximumRatePerSecond);
        const int grid = 320;
        var bestLog = lower; var best = At(lower);
        for (var i = 1; i <= grid; i++)
        {
            var log = lower + (upper - lower) * i / grid;
            var candidate = At(log);
            if (candidate.Objective < best.Objective)
            {
                best = candidate; bestLog = log;
            }
        }
        if (!double.IsFinite(best.Objective))
        {
            return Refuse("no_interior_exponential_solution", window);
        }
        var step = (upper - lower) / grid;
        var left = Math.Max(lower, bestLog - step); var right = Math.Min(upper, bestLog + step);
        const double golden = 0.6180339887498949;
        for (var i = 0; i < 80; i++)
        {
            var c = right - golden * (right - left); var d = left + golden * (right - left);
            if (At(c).Objective < At(d).Objective)
            {
                right = d;
            }
            else
            {
                left = c;
            }
        }
        var refined = (left + right) / 2;
        if (At(refined).Objective < best.Objective)
        {
            best = At(refined); bestLog = refined;
        }
        var rate = Math.Exp(bestLog);
        if (bestLog <= lower + step / 10 || bestLog >= upper - step / 10 ||
            best.Ceq >= cfg.MaximumCeqPercent - cfg.CeqSensitivityStepPercent)
        {
            return Refuse("equilibrium_fit_at_configured_boundary", window);
        }
        // All three free parameters enter the covariance (including the rate).
        var normal = new double[3, 3];
        var residuals = new double[t.Length];
        for (var i = 0; i < t.Length; i++)
        {
            var e = Math.Exp(-rate * t[i]);
            var jacobian = new[] { 1.0, -e, best.A * t[i] * e };
            residuals[i] = y[i] - (best.Ceq - best.A * e);
            for (var row = 0; row < 3; row++)
            {
                for (var col = 0; col < 3; col++)
                {
                    normal[row, col] += weights[i] * jacobian[row] * jacobian[col];
                }
            }
        }
        var inverse = KlaNumerics.Inverse00(normal);
        if (inverse is null)
        {
            return Refuse("equilibrium_covariance_singular", window);
        }
        var rho = Math.Clamp(KlaNumerics.Lag1(residuals), 0, .95);
        var effective = Math.Max(3, t.Length * (1 - rho) / (1 + rho));
        var se = Math.Sqrt(inverse.Value * best.Objective / (t.Length - 3)) * Math.Sqrt(t.Length / effective);
        var r2 = 1 - best.Sse / variance;
        var converged = double.IsFinite(se) && r2 >= cfg.MinimumR2;
        return new(best.Ceq, se, rate, best.A, r2, converged,
            "weighted_raw_exponential_variable_projection_v1", converged ? "ok" : "equilibrium_fit_low_information", window);
    }

    private static KlaEquilibriumEstimate Refuse(string reason, KlaIndexWindow window)
        => new(null, null, null, null, null, false, "none", reason, window);
}
