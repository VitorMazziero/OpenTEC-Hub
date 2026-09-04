using System;
using System.Collections.Generic;
using OpenTECHub.Services.PowerTesting;

namespace OpenTECHub.Services.PowerMapping;

/// <summary>
/// On-disk persistence interface for power synthesis maps and multi-impeller comparisons.
/// Rooted under <c>Mapas-Potencia/</c> in the active workspace.
/// </summary>
public interface IPowerMapStore
{
    string RootDirectory { get; }

    bool ValidateMapName(string name, out string? error);

    bool MapExists(string name);

    IReadOnlyList<PowerMapSummary> ListMaps();

    PowerMapDocument CreateMap(
        string name,
        IReadOnlyList<Guid> sourceTestIds,
        IReadOnlyList<string>? sourceTestNames = null,
        FluidProperties? fluid = null,
        PowerGeometry? geometry = null,
        string? notes = null);

    PowerMapDocument? LoadMap(string nameOrId);

    void SaveMap(PowerMapDocument document);

    bool DeleteMap(string nameOrId);

    IReadOnlyList<ImpellerComparisonDocument> ListComparisons();

    ImpellerComparisonDocument CreateComparison(
        string name,
        IReadOnlyList<Guid> selectedTestIds,
        IReadOnlyList<ImpellerComparisonItem> items,
        string? notes = null);

    ImpellerComparisonDocument? LoadComparison(string nameOrId);

    void SaveComparison(ImpellerComparisonDocument document);

    bool DeleteComparison(string nameOrId);
}
