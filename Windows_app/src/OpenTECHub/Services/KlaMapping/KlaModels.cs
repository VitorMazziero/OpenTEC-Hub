using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenTECHub.Services.KlaMapping;

/// <summary>The scientific lifecycle of an operator-created kLa mapping experiment.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<KlaWorkflowStage>))]
public enum KlaWorkflowStage
{
    Draft,
    SurfaceEstimated,
    PathValid,
    Published,
}

/// <summary>One measured anchor. These are observations, never bundled production defaults.</summary>
public sealed record KlaAnchor(double AirflowLpm, double AgitationRpm, double KlaPerHour);

public sealed record KlaImportedMeasurement
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public KlaTesting.KlaMeasurementContext? Context { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public KlaTesting.KlaAssayProtocol? Protocol { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? AcquiredAtUtc { get; init; }
    public Guid MeasurementId { get; init; } = Guid.NewGuid();
    public Guid SourceTestId { get; init; }
    public Guid SourceRunId { get; init; }
    public int SourceAnalysisRevision { get; init; }
    public double AirflowLpm { get; init; }
    public double AgitationRpm { get; init; }
    public double KlaPerHour { get; init; }
    public double SlopeStandardError { get; init; }
    public double ConfidenceInterval95Low { get; init; }
    public double ConfidenceInterval95High { get; init; }
    public double AnalysisR2 { get; init; }
    public string RawRelativePath { get; init; } = "";
    public string AnalysisRelativePath { get; init; } = "";
    public string RawSha256 { get; init; } = "";
    public DateTimeOffset ImportedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool Included { get; init; } = true;
    public string? ExclusionReason { get; init; }
}

/// <summary>
/// Exact editor text for a draft row. A blank or temporarily invalid measurement is valid
/// draft state, but is never admitted to a numerical snapshot.
/// </summary>
public sealed record KlaAnchorDraft(string Airflow, string Agitation, string Kla);

/// <summary>Physical actuator domain used to normalize all scientific calculations.</summary>
public sealed record KlaDomain(
    double AirflowMinimumLpm,
    double AirflowMaximumLpm,
    double AgitationMinimumRpm,
    double AgitationMaximumRpm)
{
    public double NormalizeAirflow(double value) =>
        (value - AirflowMinimumLpm) / (AirflowMaximumLpm - AirflowMinimumLpm);

    public double NormalizeAgitation(double value) =>
        (value - AgitationMinimumRpm) / (AgitationMaximumRpm - AgitationMinimumRpm);

    public double DenormalizeAirflow(double value) =>
        AirflowMinimumLpm + (value * (AirflowMaximumLpm - AirflowMinimumLpm));

    public double DenormalizeAgitation(double value) =>
        AgitationMinimumRpm + (value * (AgitationMaximumRpm - AgitationMinimumRpm));
}

/// <summary>
/// Numerical parameters published with every result. Defaults reproduce the current
/// paper analysis; changing one makes the run custom and is visible in the receipt.
/// </summary>
public sealed record KlaAlgorithmSettings
{
    public int SurfaceGridResolution { get; init; } = 300;

    public double GaussianSigmaGridCells { get; init; } = 5.0;

    public double CloughTocherGradientTolerance { get; init; } = 1e-6;

    public int CloughTocherMaximumIterations { get; init; } = 400;

    public int CandidateGridResolution { get; init; } = 150;

    public double CandidateMinimum { get; init; } = 0.0001;

    public double CandidateMaximum { get; init; } = 0.9999;

    public double OdeMaximumStep { get; init; } = 0.05;

    public double OdeRelativeTolerance { get; init; } = 1e-5;

    public double OdeAbsoluteTolerance { get; init; } = 1e-7;

    public double GradientTermination { get; init; } = 1e-4;

    public double IntegrationHorizon { get; init; } = 10.0;

    [JsonIgnore]
    public bool IsPaperReference =>
        SurfaceGridResolution == 300 &&
        GaussianSigmaGridCells == 5.0 &&
        CloughTocherGradientTolerance == 1e-6 &&
        CloughTocherMaximumIterations == 400 &&
        CandidateGridResolution == 150 &&
        CandidateMinimum == 0.0001 &&
        CandidateMaximum == 0.9999 &&
        OdeMaximumStep == 0.05 &&
        OdeRelativeTolerance == 1e-5 &&
        OdeAbsoluteTolerance == 1e-7 &&
        GradientTermination == 1e-4 &&
        IntegrationHorizon == 10.0;

    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        if (SurfaceGridResolution < 20 || SurfaceGridResolution > 500)
        {
            issues.Add("A malha da superfície deve ficar entre 20 e 500 pontos por eixo.");
        }

        if (!double.IsFinite(GaussianSigmaGridCells) || GaussianSigmaGridCells < 0 ||
            GaussianSigmaGridCells > 50)
        {
            issues.Add("O sigma gaussiano deve ficar entre 0 e 50 células.");
        }

        if (!double.IsFinite(CloughTocherGradientTolerance) ||
            CloughTocherGradientTolerance <= 0 || CloughTocherGradientTolerance > 0.1)
        {
            issues.Add("A tolerância Clough–Tocher deve ser finita e ficar em (0; 0,1].");
        }

        if (CloughTocherMaximumIterations < 10 || CloughTocherMaximumIterations > 10_000)
        {
            issues.Add("O limite Clough–Tocher deve ficar entre 10 e 10.000 iterações.");
        }

        if (CandidateGridResolution < 3 || CandidateGridResolution > 200)
        {
            issues.Add("A busca de condições iniciais deve usar entre 3 e 200 pontos por eixo.");
        }

        if (!double.IsFinite(CandidateMinimum) || !double.IsFinite(CandidateMaximum) ||
            CandidateMinimum is < 0 or >= 1 || CandidateMaximum is <= 0 or > 1 ||
            CandidateMinimum >= CandidateMaximum)
        {
            issues.Add("Os limites normalizados da busca devem formar um intervalo dentro de [0,1].");
        }

        if (!double.IsFinite(OdeMaximumStep) || !double.IsFinite(OdeRelativeTolerance) ||
            !double.IsFinite(OdeAbsoluteTolerance) || !double.IsFinite(GradientTermination) ||
            !double.IsFinite(IntegrationHorizon) ||
            !(OdeMaximumStep > 0) || !(OdeRelativeTolerance > 0) ||
            !(OdeAbsoluteTolerance > 0) || !(GradientTermination > 0) ||
            !(IntegrationHorizon > 0))
        {
            issues.Add("Passo, tolerâncias, limiar de gradiente e horizonte devem ser positivos.");
        }

        if (double.IsFinite(OdeMaximumStep) && double.IsFinite(IntegrationHorizon) &&
            OdeMaximumStep > IntegrationHorizon)
        {
            issues.Add("O passo máximo RK45 não pode exceder o horizonte de integração.");
        }

        return issues;
    }
}

/// <summary>An immutable numerical input captured before background work starts.</summary>
public sealed record KlaExperimentSnapshot
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public KlaTesting.KlaMeasurementContext? MeasurementContext { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public KlaTesting.KlaAssayProtocol? MeasurementProtocol { get; init; }
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = "";

    public string Broth { get; init; } = "";

    public string RunCode { get; init; } = "";

    public string Notes { get; init; } = "";

    public KlaDomain Domain { get; init; } = new(2, 12, 200, 800);

    public KlaAnchor[] Anchors { get; init; } = [];

    public string MeasurementFingerprint { get; init; } = "";

    public KlaAlgorithmSettings Algorithm { get; init; } = new();

    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string ScientificFingerprint() => MeasurementContext is not null || MeasurementProtocol is not null
        ? KlaFingerprint.ForObject(new
        {
            Domain, Anchors = Anchors.OrderByDescending(anchor => anchor.AgitationRpm).ThenBy(anchor => anchor.AirflowLpm).ToArray(),
            MeasurementFingerprint, Algorithm, MeasurementContext, MeasurementProtocol,
        }) : KlaFingerprint.ForObject(new
    {
        Domain,
        Anchors = Anchors
            .OrderByDescending(anchor => anchor.AgitationRpm)
            .ThenBy(anchor => anchor.AirflowLpm)
            .ToArray(),
        MeasurementFingerprint,
        Algorithm,
    });
}

public sealed record KlaSurfaceData(
    KlaSurfaceDiagnostics Diagnostics,
    string Fingerprint);

public sealed record KlaPathData(
    KlaPathPoint[] Path,
    KlaAllocationSample[] Allocation,
    double[] HeadroomScores,
    int HeadroomResolution,
    KlaPathDiagnostics Diagnostics,
    string SourceSurfaceFingerprint,
    string Fingerprint);

/// <summary>Persisted experiment document containing raw observations and computed data.</summary>
public sealed record KlaExperimentDocument
{
    public required KlaExperimentSnapshot Snapshot { get; init; }

    /// <summary>Raw rows preserve blank cells and culture-specific decimal text in drafts.</summary>
    public KlaAnchorDraft[] DraftRows { get; init; } = [];

    public KlaWorkflowStage Stage { get; init; } = KlaWorkflowStage.Draft;

    public bool IsAvailableForControl { get; init; }

    public DateTimeOffset? LastPublishedAtUtc { get; init; }

    public KlaPathData? PathData { get; init; }

    public KlaSurfaceData? SurfaceData { get; init; }

    /// <summary>Legacy receipt fingerprint if migrated from old format.</summary>
    public string? LatestReceiptFingerprint { get; init; }

    public string ReviewNote { get; init; } = "";

    public KlaImportedMeasurement[] ImportedMeasurements { get; init; } = [];
}

public readonly record struct KlaSurfaceValue(double Value, double Dq, double Dn)
{
    public double GradientMagnitude => Math.Sqrt((Dq * Dq) + (Dn * Dn));
}

public sealed record KlaSurfaceDiagnostics(
    double MinimumKlaPerHour,
    double MaximumKlaPerHour,
    double AnchorResidualRmse,
    double AnchorResidualMaximumAbsolute,
    double ConvexHullCoveragePercent,
    int NearestFilledNodes,
    bool GradientEstimatorConverged,
    IReadOnlyList<string> Warnings);

/// <summary>The reconstructed continuous surface and the grid used to audit it.</summary>
public sealed class KlaSurface
{
    private readonly BicubicSurface _spline;

    internal KlaSurface(
        KlaExperimentSnapshot input,
        double[] smoothedGrid,
        BicubicSurface spline,
        KlaSurfaceDiagnostics diagnostics,
        string fingerprint)
    {
        Input = input;
        SmoothedGrid = smoothedGrid;
        _spline = spline;
        Diagnostics = diagnostics;
        Fingerprint = fingerprint;
    }

    public KlaExperimentSnapshot Input { get; }

    public int Resolution => Input.Algorithm.SurfaceGridResolution;

    public IReadOnlyList<double> SmoothedGrid { get; }

    public KlaSurfaceDiagnostics Diagnostics { get; }

    public string Fingerprint { get; }

    public KlaSurfaceValue EvaluateNormalized(double q, double n) =>
        _spline.Evaluate(Math.Clamp(q, 0, 1), Math.Clamp(n, 0, 1));
}

public sealed record KlaPathPoint(
    double NormalizedAirflow,
    double NormalizedAgitation,
    double AirflowLpm,
    double AgitationRpm,
    double KlaPerHour,
    double Headroom);

/// <summary>One strictly increasing sample used by the real-time allocator in WP6.</summary>
public sealed record KlaAllocationSample(double KlaPerHour, double AirflowLpm, double AgitationRpm);

public sealed record KlaPathDiagnostics(
    double SelectedStartAirflowNormalized,
    double SelectedStartAgitationNormalized,
    double SelectedStartAirflowLpm,
    double SelectedStartAgitationRpm,
    double MeanHeadroom,
    double EvaluatedMaximumHeadroom,
    double NormalizedPathLength,
    double MinimumKlaPerHour,
    double MaximumKlaPerHour,
    int CandidateCount,
    int AllocationSampleCount,
    IReadOnlyList<string> Warnings);

public sealed class KlaPathResult
{
    internal KlaPathResult(
        IReadOnlyList<KlaPathPoint> path,
        IReadOnlyList<KlaAllocationSample> allocation,
        double[] headroomScores,
        int headroomResolution,
        KlaPathDiagnostics diagnostics,
        string sourceSurfaceFingerprint,
        string fingerprint)
    {
        Path = path;
        Allocation = allocation;
        HeadroomScores = headroomScores;
        HeadroomResolution = headroomResolution;
        Diagnostics = diagnostics;
        SourceSurfaceFingerprint = sourceSurfaceFingerprint;
        Fingerprint = fingerprint;
    }

    public IReadOnlyList<KlaPathPoint> Path { get; }

    public IReadOnlyList<KlaAllocationSample> Allocation { get; }

    public IReadOnlyList<double> HeadroomScores { get; }

    public int HeadroomResolution { get; }

    public KlaPathDiagnostics Diagnostics { get; }

    public string SourceSurfaceFingerprint { get; }

    public string Fingerprint { get; }

    public KlaAllocationSample Allocate(double requestedKla)
    {
        if (Allocation.Count < 2)
        {
            throw new InvalidOperationException("A trajetória não contém uma relação alocável.");
        }

        if (requestedKla <= Allocation[0].KlaPerHour)
        {
            return Allocation[0];
        }

        if (requestedKla >= Allocation[^1].KlaPerHour)
        {
            return Allocation[^1];
        }

        var lower = 0;
        var upper = Allocation.Count - 1;
        while (upper - lower > 1)
        {
            var middle = (lower + upper) / 2;
            if (Allocation[middle].KlaPerHour <= requestedKla)
            {
                lower = middle;
            }
            else
            {
                upper = middle;
            }
        }

        var left = Allocation[lower];
        var right = Allocation[upper];
        var fraction = (requestedKla - left.KlaPerHour) /
                       (right.KlaPerHour - left.KlaPerHour);
        return new KlaAllocationSample(
            requestedKla,
            left.AirflowLpm + (fraction * (right.AirflowLpm - left.AirflowLpm)),
            left.AgitationRpm + (fraction * (right.AgitationRpm - left.AgitationRpm)));
    }
}

public sealed record KlaSearchProgress(
    int CompletedCandidates,
    int TotalCandidates,
    int CompletedRows,
    int TotalRows,
    double BestHeadroom,
    double BestAirflowNormalized,
    double BestAgitationNormalized);

/// <summary>Fingerprint-bearing immutable scientific payload stored in a receipt.</summary>
public sealed record KlaPublicationPayload
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public KlaTesting.KlaMeasurementContext? MeasurementContext { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public KlaTesting.KlaAssayProtocol? MeasurementProtocol { get; init; }
    public int FormatVersion { get; init; } = 1;

    public required Guid ProfileId { get; init; }

    public required Guid ExperimentId { get; init; }

    public required int Version { get; init; }

    public required DateTimeOffset PublishedAtUtc { get; init; }

    public required string Name { get; init; }

    public string Broth { get; init; } = "";

    public string RunCode { get; init; } = "";

    public string Notes { get; init; } = "";

    public string ReviewNote { get; init; } = "";

    public required KlaDomain Domain { get; init; }

    public required KlaAnchor[] Anchors { get; init; }

    public required KlaAlgorithmSettings Algorithm { get; init; }

    public required string AlgorithmIdentity { get; init; }

    public required string SurfaceFingerprint { get; init; }

    public required string PathFingerprint { get; init; }

    public required KlaSurfaceDiagnostics SurfaceDiagnostics { get; init; }

    public required KlaPathDiagnostics PathDiagnostics { get; init; }

    public required KlaAllocationSample[] Allocation { get; init; }
}

public sealed record KlaPublishedProfile
{
    public required KlaPublicationPayload Payload { get; init; }

    public required string ReceiptFingerprint { get; init; }

    public string Name => Payload.Name;

    public override string ToString() => Name;
}

internal static class KlaFingerprint
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string ForObject<T>(T value)
        => ForBytes(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));

    public static string ForDoubles(object metadata, IEnumerable<double> values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions));
        Span<byte> buffer = stackalloc byte[sizeof(double)];
        foreach (var value in values)
        {
            BitConverter.TryWriteBytes(buffer, value);
            hash.AppendData(buffer);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string ForBytes(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string Short(string fingerprint) =>
        fingerprint.Length <= 12 ? fingerprint : fingerprint[..12];
}
