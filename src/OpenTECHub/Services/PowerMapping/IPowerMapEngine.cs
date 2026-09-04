using System;
using System.Collections.Generic;
using OpenTECHub.Services.PowerTesting;

namespace OpenTECHub.Services.PowerMapping;

/// <summary>
/// Scientific computation engine for 2D power surfaces, flooding boundaries, and kLa multivariate regressions.
/// </summary>
public interface IPowerMapEngine
{
    /// <summary>
    /// Reconstructs continuous C1 2D surfaces over regular (N x Qg) grid using CloughTocher2D.
    /// Returns grid with Layer 1 (P_net, P/V) and Layer 2 (PG/P0). Points outside convex hull are null.
    /// </summary>
    PowerMapSurfaceData ReconstructSurface(
        IReadOnlyList<PowerMapAnchorPoint> anchors,
        PowerGeometry geometry,
        FluidProperties fluid,
        PowerMapAlgorithmSettings? settings = null);

    /// <summary>
    /// Computes continuous flooding boundary over (N, Qg), incorporating Nienow theoretical line
    /// and experimental knee points.
    /// </summary>
    PowerMapFloodingBoundary ComputeFloodingBoundary(
        PowerGeometry geometry,
        IReadOnlyList<FloodingPoint>? experimentalPoints = null,
        double minRpm = 50.0,
        double maxRpm = 1000.0,
        int pointsCount = 50);

    /// <summary>
    /// Fits the van 't Riet mass transfer model: ln(kLa) = ln(K) + alpha * ln(P/V) + beta * ln(vs).
    /// Computes parameter standard errors, covariance matrix, R2, and predicted residuals for each pair.
    /// </summary>
    KlaCorrelationResult FitVanTRietModel(
        IReadOnlyList<KlaPowerPair> pairs,
        out IReadOnlyList<KlaPowerPair> updatedPairs);

    /// <summary>
    /// Calculates volumetric specific power P/V [W/m3] required for a given target kLa and superficial gas velocity.
    /// </summary>
    double EstimateSpecificPowerForKla(
        double targetKla,
        double superficialVelocityMs,
        KlaCorrelationResult correlation);
}
