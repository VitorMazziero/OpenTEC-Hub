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
}
