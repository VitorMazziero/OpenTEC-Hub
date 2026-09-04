using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenTECHub.Services.KlaMapping;
using OpenTECHub.Services.PowerTesting;

namespace OpenTECHub.Services.PowerMapping;

/// <summary>
/// Audit and provenance receipt for an enriched kLa map export containing coupled power assay data.
/// </summary>
public sealed record KlaPowerEnrichedExportResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public Guid OriginalMapId { get; init; }
    public Guid EnrichedMapId { get; init; }
    public string EnrichedMapName { get; init; } = "";
    public int MatchedPairsCount { get; init; }
    public Guid SourcePowerTestId { get; init; }
    public string SourcePowerTestName { get; init; } = "";
    public string SourcePowerTestSha256 { get; init; } = "";
    public string SourceKlaMapFingerprint { get; init; } = "";
    public IReadOnlyList<KlaPowerPair> MatchedPairs { get; init; } = [];
}

/// <summary>
/// Service coordinating bidirectional data exchange between kLa mapping and power testing.
/// </summary>
public interface IKlaPowerIntegrationService
{
    Task<IReadOnlyList<PowerCondition>> ImportConditionsFromKlaAsync(
        Guid klaMapId,
        int requestedReplicates = 1,
        CancellationToken cancellationToken = default);

    Task<KlaPowerEnrichedExportResult> ExportPowerResultsToKlaMapAsync(
        Guid powerTestId,
        Guid klaMapId,
        string? nameSuffix = " + Potência",
        CancellationToken cancellationToken = default);
}
