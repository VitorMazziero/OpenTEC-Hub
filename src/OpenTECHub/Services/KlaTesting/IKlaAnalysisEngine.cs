using System;
using System.Collections.Generic;

namespace OpenTECHub.Services.KlaTesting;

public sealed record CeqFitResult(
    double CeqPercent,
    bool IsManual,
    double? Kappa,
    double? AmplitudeA,
    double? R2,
    double? StandardError,
    bool Converged);

public sealed record InstantaneousKlaPoint(
    double RelativeSeconds,
    double DORaw,
    double DOFiltered,
    double? KlaRaw,
    double? KlaFiltered);

public sealed record LogLinearPoint(
    double RelativeSeconds,
    double DORaw,
    double LnDrivingForce,
    double FittedLnDrivingForce,
    double Residual,
    bool IsInAnalysisRegion);

public interface IKlaAnalysisEngine
{
    CeqFitResult EstimateCeq(
        IReadOnlyList<double> timeSeconds,
        IReadOnlyList<double> doValues,
        double? manualCeq = null,
        double maxCeqBound = 110.0);

    IReadOnlyList<InstantaneousKlaPoint> CalculateInstantaneousKlaSeries(
        IReadOnlyList<double> timeSeconds,
        IReadOnlyList<double> doRawValues,
        double ceqPercent,
        int smoothingWindow = 5);

    IReadOnlyList<LogLinearPoint> ComputeLogLinearPoints(
        IReadOnlyList<double> timeSeconds,
        IReadOnlyList<double> doRawValues,
        double ceqPercent,
        double tStartSeconds,
        double tEndSeconds);

    KlaAnalysisRevision PerformLogLinearAnalysis(
        IReadOnlyList<double> timeSeconds,
        IReadOnlyList<double> doRawValues,
        double ceqPercent,
        bool isCeqManual,
        double tStartSeconds,
        double tEndSeconds,
        CeqFitResult? ceqFit = null,
        string rawDataSha256 = "");
}
