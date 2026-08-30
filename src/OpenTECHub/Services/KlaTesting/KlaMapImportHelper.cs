using System;
using System.Collections.Generic;
using System.Linq;
using OpenTECHub.Services.KlaMapping;

namespace OpenTECHub.Services.KlaTesting;

public static class KlaMapImportHelper
{
    public static (KlaMapReference Reference, IReadOnlyList<KlaTestCondition> Conditions) ImportConditionsFromMap(
        KlaExperimentDocument mapDoc,
        int defaultReplicates = 1)
    {
        var mapRef = new KlaMapReference
        {
            MapId = mapDoc.Snapshot.Id,
            MapName = string.IsNullOrWhiteSpace(mapDoc.Snapshot.Name) ? "Mapa sem nome" : mapDoc.Snapshot.Name,
            MapFingerprint = mapDoc.Snapshot.ScientificFingerprint(),
            LinkedAtUtc = DateTimeOffset.UtcNow,
        };

        var rawAnchors = mapDoc.Snapshot.Anchors;
        if (rawAnchors is null || rawAnchors.Length == 0)
        {
            return (mapRef, []);
        }

        // Deduplicate within tolerance: Agitation +/- 0.1 RPM, Airflow +/- 0.01 LPM
        var uniqueConditions = new List<(double N, double Q)>();
        foreach (var a in rawAnchors)
        {
            var n = Math.Round(a.AgitationRpm, 0);
            var q = Math.Round(a.AirflowLpm, 2);

            var exists = uniqueConditions.Any(c => Math.Abs(c.N - n) < 0.1 && Math.Abs(c.Q - q) < 0.01);
            if (!exists)
            {
                uniqueConditions.Add((n, q));
            }
        }

        // Sort by N ascending, then Q ascending
        var sorted = uniqueConditions
            .OrderBy(c => c.N)
            .ThenBy(c => c.Q)
            .ToList();

        var conditions = sorted.Select((c, idx) => new KlaTestCondition
        {
            ConditionId = Guid.NewGuid(),
            OrderIndex = idx,
            AgitationRpm = c.N,
            AirflowLpm = c.Q,
            RequestedReplicates = defaultReplicates,
            CompletedReplicates = 0,
            AcceptedReplicates = 0,
            RejectedReplicates = 0,
            Origin = ConditionOrigin.Map,
            SourceMapId = mapDoc.Snapshot.Id,
            SourceMapName = mapDoc.Snapshot.Name,
            SourceMapFingerprint = mapRef.MapFingerprint,
            Status = ConditionStatus.Pending,
        }).ToList();

        return (mapRef, conditions);
    }
}
