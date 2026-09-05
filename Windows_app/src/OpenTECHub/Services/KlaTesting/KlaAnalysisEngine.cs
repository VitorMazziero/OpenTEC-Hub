using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenTECHub.Services.KlaTesting;

public sealed class KlaAnalysisEngine : IKlaAnalysisEngine
{
    public CeqFitResult EstimateCeq(
        IReadOnlyList<double> timeSeconds,
        IReadOnlyList<double> doValues,
        double? manualCeq = null,
        double maxCeqBound = 110.0)
    {
        if (manualCeq.HasValue)
        {
            if (!double.IsFinite(manualCeq.Value) || manualCeq.Value <= 0 || manualCeq.Value > maxCeqBound)
            {
                return new CeqFitResult(manualCeq.Value, true, null, null, null, null, false);
            }

            return new CeqFitResult(
                CeqPercent: manualCeq.Value,
                IsManual: true,
                Kappa: null,
                AmplitudeA: null,
                R2: null,
                StandardError: null,
                Converged: true);
        }

        if (timeSeconds.Count < 5 || doValues.Count < 5 || timeSeconds.Count != doValues.Count)
        {
            var fallback = Math.Min(maxCeqBound, doValues.Count > 0 ? Math.Max(100.0, doValues.Max()) : 100.0);
            return new CeqFitResult(
                CeqPercent: fallback,
                IsManual: false,
                Kappa: null,
                AmplitudeA: null,
                R2: null,
                StandardError: null,
                Converged: false);
        }

        var t0 = timeSeconds[0];
        var n = timeSeconds.Count;

        // Subsample or use full points
        var tNorm = new double[n];
        for (var i = 0; i < n; i++)
        {
            tNorm[i] = timeSeconds[i] - t0;
        }

        var meanDo = doValues.Average();
        var sst = doValues.Sum(v => Math.Pow(v - meanDo, 2));
        if (sst <= 1e-9)
        {
            return new CeqFitResult(doValues[0], false, null, null, null, null, false);
        }

        // Grid search over kappa in [0.0001, 0.5] (s^-1)
        var bestKappa = 0.01;
        var bestSse = double.MaxValue;
        var bestCeq = 100.0;
        var bestA = 90.0;

        var gridSteps = 200;
        var logKappaMin = Math.Log(0.0001);
        var logKappaMax = Math.Log(0.5);

        for (var step = 0; step <= gridSteps; step++)
        {
            var kappa = Math.Exp(logKappaMin + (step * (logKappaMax - logKappaMin) / gridSteps));
            var (ceq, a, sse) = SolveLinearCeqA(tNorm, doValues, kappa, maxCeqBound);

            if (sse < bestSse && ceq > 0 && a > 0 && ceq <= maxCeqBound + 1e-3)
            {
                bestSse = sse;
                bestKappa = kappa;
                bestCeq = ceq;
                bestA = a;
            }
        }

        // Golden section refinement around bestKappa
        var leftKappa = Math.Max(0.00005, bestKappa * 0.5);
        var rightKappa = Math.Min(0.8, bestKappa * 2.0);
        var gr = (Math.Sqrt(5) - 1) / 2;

        var c = rightKappa - (gr * (rightKappa - leftKappa));
        var d = leftKappa + (gr * (rightKappa - leftKappa));

        for (var iter = 0; iter < 25; iter++)
        {
            var (_, _, sseC) = SolveLinearCeqA(tNorm, doValues, c, maxCeqBound);
            var (_, _, sseD) = SolveLinearCeqA(tNorm, doValues, d, maxCeqBound);

            if (sseC < sseD)
            {
                rightKappa = d;
                d = c;
                c = rightKappa - (gr * (rightKappa - leftKappa));
            }
            else
            {
                leftKappa = c;
                c = d;
                d = leftKappa + (gr * (rightKappa - leftKappa));
            }
        }

        var optKappa = (leftKappa + rightKappa) / 2.0;
        var (optCeq, optA, optSse) = SolveLinearCeqA(tNorm, doValues, optKappa, maxCeqBound);

        if (optSse < bestSse && optCeq > 0 && optA > 0 && optCeq <= maxCeqBound + 1e-3)
        {
            bestSse = optSse;
            bestKappa = optKappa;
            bestCeq = optCeq;
            bestA = optA;
        }

        var r2 = Math.Max(0.0, 1.0 - (bestSse / sst));
        var sigma2 = bestSse / Math.Max(1, n - 3);

        // Approximate SE(Ceq) from Gauss-Newton Jacobian
        var j00 = 0.0;
        var j01 = 0.0;
        var j11 = 0.0;
        for (var i = 0; i < n; i++)
        {
            var xi = Math.Exp(-bestKappa * tNorm[i]);
            j00 += 1.0;
            j01 += -xi;
            j11 += xi * xi;
        }

        var detJ = (j00 * j11) - (j01 * j01);
        double? seCeq = null;
        if (detJ > 1e-9)
        {
            var varCeq = (j11 / detJ) * sigma2;
            if (varCeq >= 0)
            {
                seCeq = Math.Sqrt(varCeq);
            }
        }

        var converged = r2 >= 0.90 && bestCeq > 0 && bestA > 0 && bestCeq <= maxCeqBound;

        return new CeqFitResult(
            CeqPercent: bestCeq,
            IsManual: false,
            Kappa: bestKappa,
            AmplitudeA: bestA,
            R2: r2,
            StandardError: seCeq,
            Converged: converged);
    }

    private static (double Ceq, double A, double Sse) SolveLinearCeqA(
        double[] tNorm,
        IReadOnlyList<double> doValues,
        double kappa,
        double maxCeqBound)
    {
        var n = tNorm.Length;
        var sumW = 0.0;
        var sumX = 0.0;
        var sumXX = 0.0;
        var sumY = 0.0;
        var sumXY = 0.0;

        for (var i = 0; i < n; i++)
        {
            var xi = Math.Exp(-kappa * tNorm[i]);
            var yi = doValues[i];

            // Linear weights placing emphasis on tail
            var w = 1.0 + (tNorm[i] / (tNorm[n - 1] + 1e-6));

            sumW += w;
            sumX += w * xi;
            sumXX += w * xi * xi;
            sumY += w * yi;
            sumXY += w * xi * yi;
        }

        var det = (sumW * sumXX) - (sumX * sumX);
        if (det <= 1e-12)
        {
            return (100.0, 90.0, double.MaxValue);
        }

        // C_i = Ceq - A * x_i => C_i = Ceq + u * x_i with u = -A
        var ceq = ((sumY * sumXX) - (sumX * sumXY)) / det;
        var u = ((sumW * sumXY) - (sumX * sumY)) / det;
        var a = -u;

        if (ceq > maxCeqBound)
        {
            ceq = maxCeqBound;
            // Solve for A with fixed Ceq: min sum w*(Ceq - A*x_i - y_i)^2
            // d/dA = sum 2*w*x_i*(Ceq - A*x_i - y_i) * (-1) = 0 => A = sum(w*x_i*(Ceq - y_i)) / sum(w*x_i^2)
            var numA = 0.0;
            var denA = 0.0;
            for (var i = 0; i < n; i++)
            {
                var xi = Math.Exp(-kappa * tNorm[i]);
                var yi = doValues[i];
                var w = 1.0 + (tNorm[i] / (tNorm[n - 1] + 1e-6));
                numA += w * xi * (ceq - yi);
                denA += w * xi * xi;
            }
            a = denA > 1e-12 ? numA / denA : 0.0;
        }

        var sse = 0.0;
        for (var i = 0; i < n; i++)
        {
            var xi = Math.Exp(-kappa * tNorm[i]);
            var pred = ceq - (a * xi);
            sse += Math.Pow(doValues[i] - pred, 2);
        }

        return (ceq, a, sse);
    }

    public IReadOnlyList<InstantaneousKlaPoint> CalculateInstantaneousKlaSeries(
        IReadOnlyList<double> timeSeconds,
        IReadOnlyList<double> doRawValues,
        double ceqPercent,
        int smoothingWindow = 5)
    {
        var n = Math.Min(timeSeconds.Count, doRawValues.Count);
        if (n == 0)
        {
            return [];
        }

        var result = new List<InstantaneousKlaPoint>(n);
        var doFiltered = MovingAverage(doRawValues, Math.Max(1, smoothingWindow));
        var rawKla = new double?[n];

        for (var i = 0; i < n; i++)
        {
            var t = timeSeconds[i];
            var c = doRawValues[i];
            var df = ceqPercent - c;

            if (df > 0.5 && n >= 3)
            {
                double dC_dt;
                if (i == 0)
                {
                    var dt = timeSeconds[1] - timeSeconds[0];
                    dC_dt = dt > 1e-6 ? (doRawValues[1] - doRawValues[0]) / dt : 0;
                }
                else if (i == n - 1)
                {
                    var dt = timeSeconds[n - 1] - timeSeconds[n - 2];
                    dC_dt = dt > 1e-6 ? (doRawValues[n - 1] - doRawValues[n - 2]) / dt : 0;
                }
                else
                {
                    var dt = timeSeconds[i + 1] - timeSeconds[i - 1];
                    dC_dt = dt > 1e-6 ? (doRawValues[i + 1] - doRawValues[i - 1]) / dt : 0;
                }

                if (dC_dt > 0)
                {
                    var kla = 3600.0 * (dC_dt / df);
                    if (double.IsFinite(kla) && kla is > 0 and < 3000)
                    {
                        rawKla[i] = kla;
                    }
                }
            }
        }

        var halfWindow = Math.Max(1, smoothingWindow / 2);
        for (var i = 0; i < n; i++)
        {
            double? filteredKla = null;
            if (rawKla[i].HasValue)
            {
                var validSamples = new List<double>();
                for (var j = Math.Max(0, i - halfWindow); j <= Math.Min(n - 1, i + halfWindow); j++)
                {
                    if (rawKla[j].HasValue)
                    {
                        validSamples.Add(rawKla[j]!.Value);
                    }
                }

                if (validSamples.Count > 0)
                {
                    filteredKla = validSamples.Average();
                }
            }

            result.Add(new InstantaneousKlaPoint(
                RelativeSeconds: timeSeconds[i],
                DORaw: doRawValues[i],
                DOFiltered: doFiltered[i],
                KlaRaw: rawKla[i],
                KlaFiltered: filteredKla));
        }

        return result;
    }

    public IReadOnlyList<LogLinearPoint> ComputeLogLinearPoints(
        IReadOnlyList<double> timeSeconds,
        IReadOnlyList<double> doRawValues,
        double ceqPercent,
        double tStartSeconds,
        double tEndSeconds)
    {
        var n = Math.Min(timeSeconds.Count, doRawValues.Count);
        if (n == 0)
        {
            return [];
        }

        // Fit OLS on region first to get regression line parameters
        var regionTimes = new List<double>();
        var regionYs = new List<double>();

        for (var i = 0; i < n; i++)
        {
            var t = timeSeconds[i];
            var c = doRawValues[i];
            var df = ceqPercent - c;

            if (t >= tStartSeconds && t <= tEndSeconds)
            {
                if (!double.IsFinite(t) || !double.IsFinite(c) || df <= 0.05)
                {
                    continue;
                }
                regionTimes.Add(t);
                regionYs.Add(Math.Log(df));
            }
        }

        var (beta0, beta1, _, _, _) = FitOls(regionTimes, regionYs);

        var list = new List<LogLinearPoint>(n);
        for (var i = 0; i < n; i++)
        {
            var t = timeSeconds[i];
            var c = doRawValues[i];
            var df = ceqPercent - c;

            if (df <= 0.01)
            {
                continue;
            }

            var lnDf = Math.Log(df);
            var isInRegion = t >= tStartSeconds && t <= tEndSeconds;
            var fitted = beta0 + (beta1 * t);
            var res = lnDf - fitted;

            list.Add(new LogLinearPoint(t, c, lnDf, fitted, res, isInRegion));
        }

        return list;
    }

    public KlaAnalysisRevision PerformLogLinearAnalysis(
        IReadOnlyList<double> timeSeconds,
        IReadOnlyList<double> doRawValues,
        double ceqPercent,
        bool isCeqManual,
        double tStartSeconds,
        double tEndSeconds,
        CeqFitResult? ceqFit = null,
        string rawDataSha256 = "")
    {
        var n = Math.Min(timeSeconds.Count, doRawValues.Count);
        var selectedCount = 0;
        var invalidDrivingForce = false;
        var regionTimes = new List<double>();
        var regionYs = new List<double>();

        for (var i = 0; i < n; i++)
        {
            var t = timeSeconds[i];
            var c = doRawValues[i];
            var df = ceqPercent - c;

            if (t >= tStartSeconds && t <= tEndSeconds)
            {
                selectedCount++;
                if (!double.IsFinite(t) || !double.IsFinite(c) || df <= 0.05)
                {
                    invalidDrivingForce = true;
                    continue;
                }
                regionTimes.Add(t);
                regionYs.Add(Math.Log(df));
            }
        }

        var usedCount = regionTimes.Count;
        if (!double.IsFinite(ceqPercent) || ceqPercent <= 0 || ceqPercent > 110 ||
            tEndSeconds <= tStartSeconds || invalidDrivingForce || usedCount < 3)
        {
            return new KlaAnalysisRevision
            {
                CeqPercent = ceqPercent,
                IsCeqManual = isCeqManual,
                FittedKappa = ceqFit?.Kappa,
                FittedA = ceqFit?.AmplitudeA,
                CeqFitR2 = ceqFit?.R2,
                TStartSeconds = tStartSeconds,
                TEndSeconds = tEndSeconds,
                TotalPoints = selectedCount,
                UsedPoints = usedCount,
                KlaPerHour = 0.0,
                Quality = DecisionQuality.Inconclusive,
                RejectionReason = invalidDrivingForce
                    ? "A região contém ponto com Ceq − C ≤ 0,05%. Ajuste Ceq ou a região; nenhum ponto foi removido silenciosamente."
                    : "Região inválida ou com menos de 3 pontos.",
                RawDataSha256 = rawDataSha256,
            };
        }

        var (beta0, beta1, r2, rmse, seBeta1) = FitOls(regionTimes, regionYs);
        var kla = -3600.0 * beta1;
        var seKla = 3600.0 * seBeta1;

        // Approximate Student-t critical value for 95% CI (two-tailed)
        var dfDegrees = Math.Max(1, usedCount - 2);
        var tCrit = GetStudentTCriticalValue(dfDegrees);
        var ci95Low = kla - (tCrit * seKla);
        var ci95High = kla + (tCrit * seKla);

        // Sensitivity to Ceq +/- SE(Ceq)
        var seCeq = ceqFit?.StandardError ?? 0.0;
        var klaSensLow = ComputeSensitivityKla(regionTimes, timeSeconds, doRawValues, ceqPercent - seCeq, tStartSeconds, tEndSeconds);
        var klaSensHigh = ComputeSensitivityKla(regionTimes, timeSeconds, doRawValues, ceqPercent + seCeq, tStartSeconds, tEndSeconds);
        ci95Low = Math.Min(ci95Low, Math.Min(klaSensLow, klaSensHigh));
        ci95High = Math.Max(ci95High, Math.Max(klaSensLow, klaSensHigh));

        var quality = EvaluateQuality(usedCount, beta1, r2, kla, out var warningReason, out var rejectionReason);
        if (!isCeqManual && ceqFit is { Converged: false })
        {
            quality = DecisionQuality.Inconclusive;
            rejectionReason = "O ajuste automático de Ceq não convergiu com R² ≥ 0,90 na região escolhida.";
        }

        return new KlaAnalysisRevision
        {
            CeqPercent = ceqPercent,
            IsCeqManual = isCeqManual,
            FittedKappa = ceqFit?.Kappa,
            FittedA = ceqFit?.AmplitudeA,
            CeqFitR2 = ceqFit?.R2,
            TStartSeconds = tStartSeconds,
            TEndSeconds = tEndSeconds,
            TotalPoints = selectedCount,
            UsedPoints = usedCount,
            KlaPerHour = kla,
            SlopeBeta1 = beta1,
            InterceptBeta0 = beta0,
            SlopeStandardError = seBeta1,
            ConfidenceInterval95Low = ci95Low,
            ConfidenceInterval95High = ci95High,
            AnalysisR2 = r2,
            AnalysisRmse = rmse,
            KlaSensitivityLow = klaSensLow,
            KlaSensitivityHigh = klaSensHigh,
            Quality = quality,
            WarningJustification = warningReason,
            RejectionReason = rejectionReason,
            RawDataSha256 = rawDataSha256,
        };
    }

    private static (double Beta0, double Beta1, double R2, double Rmse, double SeBeta1) FitOls(
        IReadOnlyList<double> x,
        IReadOnlyList<double> y)
    {
        var n = x.Count;
        if (n < 2)
        {
            return (0, 0, 0, 0, 0);
        }

        var meanX = x.Average();
        var meanY = y.Average();

        var ssXx = 0.0;
        var ssXy = 0.0;
        var ssYy = 0.0;

        for (var i = 0; i < n; i++)
        {
            var dx = x[i] - meanX;
            var dy = y[i] - meanY;
            ssXx += dx * dx;
            ssXy += dx * dy;
            ssYy += dy * dy;
        }

        if (ssXx <= 1e-12)
        {
            return (meanY, 0, 0, 0, 0);
        }

        var beta1 = ssXy / ssXx;
        var beta0 = meanY - (beta1 * meanX);

        var r2 = ssYy > 1e-12 ? Math.Clamp(Math.Pow(ssXy, 2) / (ssXx * ssYy), 0.0, 1.0) : 0.0;

        var sse = 0.0;
        for (var i = 0; i < n; i++)
        {
            var pred = beta0 + (beta1 * x[i]);
            sse += Math.Pow(y[i] - pred, 2);
        }

        var rmse = Math.Sqrt(sse / n);
        var s2 = n > 2 ? sse / (n - 2) : 0.0;
        var seBeta1 = Math.Sqrt(s2 / ssXx);

        return (beta0, beta1, r2, rmse, seBeta1);
    }

    private static double ComputeSensitivityKla(
        IReadOnlyList<double> regionTimes,
        IReadOnlyList<double> allTimes,
        IReadOnlyList<double> allDos,
        double perturbedCeq,
        double tStart,
        double tEnd)
    {
        var n = Math.Min(allTimes.Count, allDos.Count);
        var ys = new List<double>();
        var xs = new List<double>();

        for (var i = 0; i < n; i++)
        {
            var t = allTimes[i];
            var c = allDos[i];
            var df = perturbedCeq - c;

            if (t >= tStart && t <= tEnd && df > 0.01)
            {
                xs.Add(t);
                ys.Add(Math.Log(df));
            }
        }

        if (xs.Count < 2)
        {
            return 0.0;
        }

        var (_, beta1, _, _, _) = FitOls(xs, ys);
        return -3600.0 * beta1;
    }

    private static DecisionQuality EvaluateQuality(
        int usedPoints,
        double beta1,
        double r2,
        double kla,
        out string? warningReason,
        out string? rejectionReason)
    {
        warningReason = null;
        rejectionReason = null;

        if (usedPoints < 5)
        {
            rejectionReason = "Amostra insuficiente (menos de 5 pontos na região selecionada).";
            return DecisionQuality.Inconclusive;
        }

        if (beta1 >= 0 || kla <= 0 || !double.IsFinite(kla))
        {
            rejectionReason = "Inclinação log-linear não-negativa ou kLa inválido.";
            return DecisionQuality.Inconclusive;
        }

        if (r2 < 0.90)
        {
            rejectionReason = $"Ajuste insuficiente (R² = {r2:F3} abaixo do limiar de 0,90).";
            return DecisionQuality.Inconclusive;
        }

        if (r2 < 0.95 || usedPoints < 10)
        {
            var parts = new List<string>();
            if (r2 < 0.95)
            {
                parts.Add($"R² = {r2:F3} entre 0,90 e 0,95");
            }
            if (usedPoints < 10)
            {
                parts.Add($"região com apenas {usedPoints} pontos");
            }
            warningReason = $"Atenção: {string.Join(" e ", parts)}.";
            return DecisionQuality.AcceptableWithWarning;
        }

        return DecisionQuality.Acceptable;
    }

    private static double GetStudentTCriticalValue(int df)
    {
        // Approximations for two-tailed alpha = 0.05 (95% CI)
        return df switch
        {
            1 => 12.706,
            2 => 4.303,
            3 => 3.182,
            4 => 2.776,
            5 => 2.571,
            6 => 2.447,
            7 => 2.365,
            8 => 2.306,
            9 => 2.262,
            10 => 2.228,
            <= 12 => 2.201,
            <= 14 => 2.160,
            <= 16 => 2.131,
            <= 18 => 2.110,
            <= 20 => 2.093,
            <= 25 => 2.086,
            <= 30 => 2.064,
            <= 40 => 2.042,
            <= 60 => 2.021,
            <= 120 => 2.000,
            _ => 1.980,
        };
    }

    private static double[] MovingAverage(IReadOnlyList<double> values, int windowSize)
    {
        var n = values.Count;
        var result = new double[n];
        var half = windowSize / 2;

        for (var i = 0; i < n; i++)
        {
            var sum = 0.0;
            var count = 0;
            for (var j = Math.Max(0, i - half); j <= Math.Min(n - 1, i + half); j++)
            {
                sum += values[j];
                count++;
            }
            result[i] = count > 0 ? sum / count : values[i];
        }

        return result;
    }
}
