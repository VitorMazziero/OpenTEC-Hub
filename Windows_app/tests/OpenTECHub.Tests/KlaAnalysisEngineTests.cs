using System;
using System.Collections.Generic;
using OpenTECHub.Services.KlaTesting;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class KlaAnalysisEngineTests
{
    private readonly KlaAnalysisEngine _engine = new();

    [Fact]
    public void EstimateCeq_And_LogLinearAnalysis_Recover_Synthetic_GassingOut_Curve()
    {
        // Ground truth: Ceq = 100.0%, kLa = 72.0 h^-1 (kappa = 72/3600 = 0.02 s^-1), C0 = 5.0%
        var trueCeq = 100.0;
        var trueKla = 72.0;
        var kappa = trueKla / 3600.0; // 0.02 s^-1
        var c0 = 5.0;

        var times = new List<double>();
        var dos = new List<double>();

        // Sample every 2 seconds from t = 0 to t = 200 s
        for (var t = 0.0; t <= 200.0; t += 2.0)
        {
            var c = trueCeq - ((trueCeq - c0) * Math.Exp(-kappa * t));
            times.Add(t);
            dos.Add(c);
        }

        // 1. Estimate Ceq
        var ceqFit = _engine.EstimateCeq(times, dos);
        Assert.True(ceqFit.Converged);
        Assert.InRange(ceqFit.CeqPercent, 99.0, 101.0);
        Assert.NotNull(ceqFit.Kappa);
        Assert.InRange(ceqFit.Kappa.Value, 0.019, 0.021);
        Assert.True(ceqFit.R2 > 0.999);

        // 2. Perform Log-Linear Analysis on region t in [10, 120] (DO roughly 20% to 90%)
        var analysis = _engine.PerformLogLinearAnalysis(
            times,
            dos,
            ceqPercent: ceqFit.CeqPercent,
            isCeqManual: false,
            tStartSeconds: 10.0,
            tEndSeconds: 120.0,
            ceqFit: ceqFit);

        Assert.Equal(DecisionQuality.Acceptable, analysis.Quality);
        Assert.InRange(analysis.KlaPerHour, 71.5, 72.5); // Accurately recovers 72.0 h^-1
        Assert.True(analysis.AnalysisR2 > 0.9999);
        Assert.True(analysis.ConfidenceInterval95Low <= analysis.KlaPerHour &&
                    analysis.KlaPerHour <= analysis.ConfidenceInterval95High);
    }

    [Fact]
    public void ManualCeq_Override_Applies_Directly()
    {
        var times = new List<double> { 0, 10, 20, 30, 40, 50, 60 };
        var dos = new List<double> { 5, 25, 45, 60, 72, 80, 85 };

        var ceqFit = _engine.EstimateCeq(times, dos, manualCeq: 95.0);
        Assert.True(ceqFit.IsManual);
        Assert.Equal(95.0, ceqFit.CeqPercent);

        var analysis = _engine.PerformLogLinearAnalysis(
            times,
            dos,
            ceqPercent: 95.0,
            isCeqManual: true,
            tStartSeconds: 10.0,
            tEndSeconds: 50.0);

        Assert.True(analysis.IsCeqManual);
        Assert.Equal(95.0, analysis.CeqPercent);
        Assert.True(analysis.KlaPerHour > 0);
    }

    [Fact]
    public void Insufficient_Or_Inverted_Points_Yields_Inconclusive_Quality()
    {
        // Decreasing DO (e.g. during deoxygenation or sensor error) -> beta1 > 0 -> Inconclusive
        var times = new List<double> { 0, 10, 20, 30, 40 };
        var dos = new List<double> { 80, 60, 40, 20, 10 };

        var analysis = _engine.PerformLogLinearAnalysis(
            times,
            dos,
            ceqPercent: 100.0,
            isCeqManual: true,
            tStartSeconds: 0.0,
            tEndSeconds: 40.0);

        Assert.Equal(DecisionQuality.Inconclusive, analysis.Quality);
        Assert.NotNull(analysis.RejectionReason);
    }

    [Fact]
    public void InstantaneousKlaSeries_Calculates_Valid_Diagnostic_Points()
    {
        var times = new List<double> { 0, 2, 4, 6, 8, 10, 12, 14, 16 };
        var dos = new List<double> { 10, 15, 22, 30, 39, 48, 56, 63, 69 };

        var points = _engine.CalculateInstantaneousKlaSeries(times, dos, ceqPercent: 100.0, smoothingWindow: 3);

        Assert.Equal(9, points.Count);
        Assert.NotNull(points[4].KlaRaw);
        Assert.NotNull(points[4].KlaFiltered);
        Assert.True(points[4].KlaRaw!.Value > 0);
    }
}
