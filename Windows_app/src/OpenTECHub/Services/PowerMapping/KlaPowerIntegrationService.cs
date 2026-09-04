using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenTECHub.Services.KlaMapping;
using OpenTECHub.Services.PowerTesting;

namespace OpenTECHub.Services.PowerMapping;

/// <summary>
/// Coordinates bidirectional integration between kLa profiles and power testing.
/// Never mutates the original source kLa document; exports create enriched clones with full audit provenance.
/// </summary>
public sealed class KlaPowerIntegrationService : IKlaPowerIntegrationService
{
    private readonly IKlaProfileStore _klaStore;
    private readonly IPowerTestStore _powerStore;

    public KlaPowerIntegrationService(IKlaProfileStore klaStore, IPowerTestStore powerStore)
    {
        _klaStore = klaStore ?? throw new ArgumentNullException(nameof(klaStore));
        _powerStore = powerStore ?? throw new ArgumentNullException(nameof(powerStore));
    }

    public async Task<IReadOnlyList<PowerCondition>> ImportConditionsFromKlaAsync(
        Guid klaMapId,
        int requestedReplicates = 1,
        CancellationToken cancellationToken = default)
    {
        var experiments = await _klaStore.LoadExperimentsAsync(cancellationToken).ConfigureAwait(false);
        var doc = experiments.FirstOrDefault(e => e.Snapshot.Id == klaMapId);
        if (doc is null)
        {
            throw new InvalidOperationException($"Experimento de kLa com ID {klaMapId} não foi encontrado.");
        }

        var (_, _, conditions) = PowerMapImportHelper.ImportConditionsFromKlaMap(doc, requestedReplicates);
        return conditions;
    }

    public async Task<KlaPowerEnrichedExportResult> ExportPowerResultsToKlaMapAsync(
        Guid powerTestId,
        Guid klaMapId,
        string? nameSuffix = " + Potência",
        CancellationToken cancellationToken = default)
    {
        var experiments = await _klaStore.LoadExperimentsAsync(cancellationToken).ConfigureAwait(false);
        var klaDoc = experiments.FirstOrDefault(e => e.Snapshot.Id == klaMapId);
        if (klaDoc is null)
        {
            return new KlaPowerEnrichedExportResult
            {
                Success = false,
                Message = $"Experimento de kLa com ID {klaMapId} não foi encontrado.",
            };
        }

        var powerSummary = _powerStore.ListTests().FirstOrDefault(t => t.TestId == powerTestId);
        if (powerSummary is null)
        {
            return new KlaPowerEnrichedExportResult
            {
                Success = false,
                Message = $"Ensaio de potência com ID {powerTestId} não foi encontrado.",
            };
        }

        var powerDoc = _powerStore.LoadTest(powerSummary.FolderName);
        if (powerDoc is null)
        {
            return new KlaPowerEnrichedExportResult
            {
                Success = false,
                Message = $"Não foi possível carregar os dados do ensaio de potência '{powerSummary.Name}'.",
            };
        }

        // Match points
        var matchedPairs = PowerMapImportHelper.MatchPowerTestToKlaMap(powerDoc, klaDoc);
        if (matchedPairs.Count == 0)
        {
            return new KlaPowerEnrichedExportResult
            {
                Success = false,
                Message = "Nenhuma condição operacional correspondente (N, Qg) foi encontrada entre o ensaio e o mapa kLa.",
                OriginalMapId = klaDoc.Snapshot.Id,
                SourcePowerTestId = powerDoc.TestId,
            };
        }

        // Create enriched clone
        var newId = Guid.NewGuid();
        var originalName = klaDoc.Snapshot.Name;
        var suffix = string.IsNullOrWhiteSpace(nameSuffix) ? " + Potência" : nameSuffix;
        var newName = $"{originalName}{suffix}";

        var powerJson = PowerTestFileContracts.SerializeTestDocument(powerDoc);
        var powerSha256 = PowerMapFileContracts.ComputeSha256(powerJson);
        var klaFingerprint = klaDoc.Snapshot.ScientificFingerprint();

        var auditNote = $"[Acoplamento Potência]: Vinculado ao ensaio '{powerDoc.Name}' (ID: {powerDoc.TestId}) em {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC. {matchedPairs.Count} pontos com P/V medido.";

        var enrichedSnapshot = klaDoc.Snapshot with
        {
            Id = newId,
            Name = newName,
        };

        var enrichedDoc = klaDoc with
        {
            Snapshot = enrichedSnapshot,
            ReviewNote = string.IsNullOrWhiteSpace(klaDoc.ReviewNote)
                ? auditNote
                : $"{klaDoc.ReviewNote}\n{auditNote}",
        };

        await _klaStore.SaveExperimentAsync(enrichedDoc, cancellationToken).ConfigureAwait(false);

        return new KlaPowerEnrichedExportResult
        {
            Success = true,
            Message = $"Mapa enriquecido com sucesso com {matchedPairs.Count} pontos de potência.",
            OriginalMapId = klaDoc.Snapshot.Id,
            EnrichedMapId = newId,
            EnrichedMapName = newName,
            MatchedPairsCount = matchedPairs.Count,
            SourcePowerTestId = powerDoc.TestId,
            SourcePowerTestName = powerDoc.Name,
            SourcePowerTestSha256 = powerSha256,
            SourceKlaMapFingerprint = klaFingerprint,
            MatchedPairs = matchedPairs,
        };
    }
}
