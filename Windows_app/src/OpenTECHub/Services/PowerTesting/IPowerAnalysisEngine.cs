using System.Collections.Generic;

namespace OpenTECHub.Services.PowerTesting;

/// <summary>
/// The single home of the power assay's science (§4). Pure and stateless, so a point can be
/// re-analysed in review after ρ/μ/D/tare/calibration change (§16), and every number is testable
/// without hardware.
/// </summary>
public interface IPowerAnalysisEngine
{
    /// <summary>Turns one captured window (mean torque + its CI, at a measured rpm) into a full point result.</summary>
    PowerPointResult AnalyzePoint(PowerPointInput input);

    /// <summary>Fits the turbulent-plateau power number over the accepted points above <paramref name="reCutoff"/>.</summary>
    PlateauFitResult FitPlateau(
        IEnumerable<(double ReynoldsNumber, double PowerNumber, double PowerNumberCi95)> points,
        double reCutoff);

    /// <summary>Fits the affine mechanical→electrical correlation P_elec ≈ a·P_mec + b (§4.8).</summary>
    EnergyCorrelationResult FitEnergyCorrelation(IEnumerable<(double MechanicalW, double ElectricalW)> pairs);

    /// <summary>
    /// Resolves baseline P₀(N) using the 3-step hierarchy (§4.5):
    /// 1. Fitted plateau reconstruction across stages
    /// 2. Fallback to measured ungassed point at same rpm (±1 rpm)
    /// 3. null / None
    /// </summary>
    (double? P0W, double? Ci95P0W, P0Provenance Provenance) ResolveReferenceP0(
        double rpm,
        PowerTestDocument doc,
        PlateauFitResult? plateauFit = null);

    /// <summary>
    /// Automatically detects the flooding transition point from a series of gassed runs (§4.5, §16).
    /// </summary>
    FloodingAnalysisResult? DetectFlooding(
        IReadOnlyList<PowerRunSummary> runs,
        PowerGeometry geometry,
        int referenceStageIndex = 0);

    /// <summary>
    /// Generates theoretical Nienow flooding boundary points over an rpm range (§4.5).
    /// </summary>
    IReadOnlyList<(double Rpm, double FlowLpm, double FlG, double Fr)> GenerateNienowBoundary(
        PowerGeometry geometry,
        double minRpm,
        double maxRpm,
        int stepCount = 20,
        int referenceStageIndex = 0);
}
