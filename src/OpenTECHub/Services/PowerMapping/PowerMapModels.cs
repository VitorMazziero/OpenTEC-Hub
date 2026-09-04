using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using OpenTECHub.Services.PowerTesting;

namespace OpenTECHub.Services.PowerMapping;

/// <summary>
/// Root persistent document for a power synthesis map under <c>Mapas-Potencia/{FolderName}/mapa-potencia.json</c>.
/// Integrates 2D interpolated surfaces over (N, Qg), continuous flooding boundaries, and kLa coupling.
/// </summary>
public sealed record PowerMapDocument
{
    public Guid MapId { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = "";

    public string FolderName { get; init; } = "";

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>IDs of the underlying power tests used as sources for this map.</summary>
    public IReadOnlyList<Guid> SourceTestIds { get; init; } = [];

    /// <summary>Human-readable names of the source power tests.</summary>
    public IReadOnlyList<string> SourceTestNames { get; init; } = [];

    /// <summary>Fluid reference properties used for non-dimensionalization and power predictions.</summary>
    public FluidProperties Fluid { get; init; } = new() { DensityKgM3 = 1000.0, ViscosityPaS = 0.001 };

    /// <summary>Geometrical setup of the reference vessel and impeller(s).</summary>
    public PowerGeometry Geometry { get; init; } = new();

    /// <summary>Reconstructed 2D multi-layer grid (N x Qg) for P_net, P/V, and PG/P0.</summary>
    public PowerMapSurfaceData? SurfaceData { get; init; }

    /// <summary>Continuous flooding boundary over (N, Qg) comparing experimental knee to Nienow theory.</summary>
    public PowerMapFloodingBoundary? FloodingBoundary { get; init; }

    /// <summary>ID of the linked kLa experiment document if kLa-P/V coupling is active.</summary>
    public Guid? LinkedKlaMapId { get; init; }

    /// <summary>Name of the linked kLa map document.</summary>
    public string? LinkedKlaMapName { get; init; }

    /// <summary>Paired points between power assays and kLa determinations.</summary>
    public IReadOnlyList<KlaPowerPair> KlaPairs { get; init; } = [];

    /// <summary>Adjusted multivariable van 't Riet correlation result kLa = K * (P/V)^alpha * (vs)^beta.</summary>
    public KlaCorrelationResult? KlaCorrelation { get; init; }

    /// <summary>Operator notes or observations on this power map synthesis.</summary>
    public string Notes { get; init; } = "";
}

/// <summary>
/// Computational settings for 2D surface reconstruction and interpolation.
/// </summary>
public sealed record PowerMapAlgorithmSettings
{
    public int ResolutionN { get; init; } = 150;

    public int ResolutionQg { get; init; } = 150;

    public double MinRpm { get; init; } = 15.0;

    public double MaxRpm { get; init; } = 1000.0;

    public double MinFlowLpm { get; init; } = 0.0;

    public double MaxFlowLpm { get; init; } = 20.0;
}

/// <summary>
/// 2D regular grid containing interpolated scientific surfaces and experimental anchors.
/// </summary>
public sealed record PowerMapSurfaceData
{
    public int ResolutionN { get; init; } = 150;

    public int ResolutionQg { get; init; } = 150;

    public double MinRpm { get; init; }

    public double MaxRpm { get; init; }

    public double MinFlowLpm { get; init; }

    public double MaxFlowLpm { get; init; }

    /// <summary>1D array of agitation grid coordinates (length ResolutionN).</summary>
    public double[] RpmGrid { get; init; } = [];

    /// <summary>1D array of gas flow grid coordinates (length ResolutionQg).</summary>
    public double[] FlowGrid { get; init; } = [];

    /// <summary>
    /// Layer 1: Net shaft power P_net(N, Qg) [W] flattened array [ResolutionN * ResolutionQg].
    /// null for points outside convex hull.
    /// </summary>
    public double?[] PNetSurface { get; init; } = [];

    /// <summary>
    /// Layer 1: Volumetric specific power P/V(N, Qg) [W/m3] flattened array [ResolutionN * ResolutionQg].
    /// </summary>
    public double?[] PVolumetricSurface { get; init; } = [];

    /// <summary>
    /// Layer 2: Aeration power ratio PG/P0(N, Qg) [-] flattened array [ResolutionN * ResolutionQg].
    /// </summary>
    public double?[] PowerRatioSurface { get; init; } = [];

    /// <summary>Experimental anchor points embedded on the surface.</summary>
    public IReadOnlyList<PowerMapAnchorPoint> AnchorPoints { get; init; } = [];

    public int GetIndex(int indexN, int indexQg) => (indexN * ResolutionQg) + indexQg;
}

/// <summary>
/// One experimental anchor point mapped into the (N, Qg) plane.
/// </summary>
public sealed record PowerMapAnchorPoint
{
    public Guid RunId { get; init; } = Guid.NewGuid();

    public Guid SourceTestId { get; init; }

    public string SourceTestName { get; init; } = "";

    public double AgitationRpm { get; init; }

    public double GasFlowLpm { get; init; }

    public double GasSuperficialVelocityMs { get; init; }

    public double NetPowerW { get; init; }

    public double VolumetricPowerWm3 { get; init; }

    public double? PowerRatio { get; init; }

    public double? GasFlowNumber { get; init; }

    public double? FroudeNumber { get; init; }

    public double? ReynoldsNumber { get; init; }

    public bool IsFlooded { get; init; }

    public DateTimeOffset MeasuredAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Continuous flooding boundary separating dispersed and flooded operational regimes in (N, Qg).
/// </summary>
public sealed record PowerMapFloodingBoundary
{
    public ImpellerType ImpellerType { get; init; } = ImpellerType.RushtonFlatBlade;

    public double ImpellerDiameterM { get; init; }

    public double VesselDiameterM { get; init; }

    /// <summary>Experimental flooding points detected from P_G/P0 knee transitions.</summary>
    public IReadOnlyList<FloodingPoint> ExperimentalPoints { get; init; } = [];

    /// <summary>Theoretical Nienow boundary points: Qg,F(N) = 30 * (D/T)^3.5 * (N^3 D^4 / g).</summary>
    public IReadOnlyList<FloodingPoint> NienowTheoreticalPoints { get; init; } = [];
}

/// <summary>
/// Point along a flooding boundary in (N, Qg) with corresponding dimensionless numbers.
/// </summary>
public sealed record FloodingPoint
{
    public double AgitationRpm { get; init; }

    public double GasFlowLpm { get; init; }

    public double GasFlowNumber { get; init; }

    public double FroudeNumber { get; init; }
}

/// <summary>
/// Paired record matching a power assay operating point to an experimental kLa determination.
/// </summary>
public sealed record KlaPowerPair
{
    public Guid PairId { get; init; } = Guid.NewGuid();

    public Guid? SourcePowerTestId { get; init; }

    public Guid? SourceKlaTestId { get; init; }

    public double AgitationRpm { get; init; }

    public double GasFlowLpm { get; init; }

    public double SuperficialVelocityMs { get; init; }

    public double NetPowerW { get; init; }

    public double VolumetricPowerWm3 { get; init; }

    public double KlaPerHour { get; init; }

    public double ConfidenceInterval95 { get; init; }

    public double? PredictedKlaPerHour { get; init; }

    public double? Residual { get; init; }

    public double? RelativeErrorFraction { get; init; }
}

/// <summary>
/// Result of the multivariable OLS regression of the van 't Riet model:
/// ln(kLa) = ln(K) + alpha * ln(P/V) + beta * ln(vs)
/// </summary>
public sealed record KlaCorrelationResult
{
    public DateTimeOffset FittedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public double K { get; init; }

    public double Alpha { get; init; }

    public double Beta { get; init; }

    public double StdErrorK { get; init; }

    public double StdErrorAlpha { get; init; }

    public double StdErrorBeta { get; init; }

    public double R2 { get; init; }

    public double AdjustedR2 { get; init; }

    public double RootMeanSquareError { get; init; }

    /// <summary>3x3 covariance matrix of parameter estimators [ln(K), alpha, beta].</summary>
    public double[][] CovarianceMatrix { get; init; } = [];

    public int ValidPointsCount { get; init; }

    public int DegreesOfFreedom { get; init; }

    public string ModelFormula { get; init; } = "kLa = K * (P/V)^alpha * (vs)^beta";

    public IReadOnlyList<string> ExcludedPointsNotes { get; init; } = [];
}

/// <summary>
/// Benchmarking item for one impeller assembly across multiple power assays.
/// </summary>
public sealed record ImpellerComparisonItem
{
    public Guid SourceTestId { get; init; }

    public string TestName { get; init; } = "";

    public DateTimeOffset TestDateUtc { get; init; }

    public ImpellerType ImpellerType { get; init; }

    public double ImpellerDiameterM { get; init; }

    public double VesselDiameterM { get; init; }

    public double DiameterRatioDt => VesselDiameterM > 0 ? ImpellerDiameterM / VesselDiameterM : 0;

    public double LiquidVolumeM3 { get; init; }

    public double LiquidDensityKgM3 { get; init; }

    public double TurbulentNpMean { get; init; }

    public double TurbulentNpCi95 { get; init; }

    public double? ExperimentalFloodingFlG { get; init; }

    public double? NienowFloodingFlG { get; init; }

    public double? AverageSpecificPowerWm3 { get; init; }

    public double? ParasiticPowerZeroSpeedW { get; init; }

    public double? GasDispersionEfficiencyRatio { get; init; }

    public IReadOnlyList<PowerRatioPoint> PowerRatioCurve { get; init; } = [];

    public IReadOnlyList<NpRePoint> PowerNumberReynoldsCurve { get; init; } = [];
}

public sealed record PowerRatioPoint(double GasFlowNumber, double PowerRatio, double AgitationRpm, double GasFlowLpm);

public sealed record NpRePoint(double Reynolds, double PowerNumber, double Uncertainty95);

/// <summary>
/// Persistent document containing a collection of compared impeller tests for benchmarking.
/// </summary>
public sealed record ImpellerComparisonDocument
{
    public Guid ComparisonId { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = "";

    public string FolderName { get; init; } = "";

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<Guid> SelectedTestIds { get; init; } = [];

    public IReadOnlyList<ImpellerComparisonItem> Items { get; init; } = [];

    public bool IsCompatibleGeometry { get; init; } = true;

    public IReadOnlyList<string> CompatibilityNotes { get; init; } = [];
}

/// <summary>
/// Lightweight summary for workspace lists of power maps.
/// </summary>
public sealed record PowerMapSummary(
    string FolderName,
    string Name,
    Guid MapId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int SourceTestsCount,
    bool HasSurface,
    bool HasKlaCorrelation,
    double? R2);
