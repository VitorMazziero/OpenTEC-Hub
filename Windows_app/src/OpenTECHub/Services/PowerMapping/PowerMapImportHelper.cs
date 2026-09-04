using System;
using System.Collections.Generic;
using System.Linq;
using OpenTECHub.Services.KlaMapping;
using OpenTECHub.Services.PowerTesting;

namespace OpenTECHub.Services.PowerMapping;

/// <summary>
/// Bidirectional conversion and pairing between kLa mapping experiments and power assays.
/// </summary>
public static class PowerMapImportHelper
{
    public const double DefaultRpmTolerance = 1.0;
    public const double DefaultFlowToleranceLpm = 0.05;

    /// <summary>
    /// Direction 1: kLa -> Power. Extracts anchor coordinates (N, Qg) from a KlaExperimentDocument,
    /// deduplicates within numerical tolerance, sorts canonically (N asc, then Qg asc),
    /// and generates planned PowerCondition rows with Origin = PowerConditionOrigin.Map.
    /// </summary>
    public static (Guid SourceMapId, string SourceMapName, IReadOnlyList<PowerCondition> Conditions) ImportConditionsFromKlaMap(
        KlaExperimentDocument klaDoc,
        int defaultReplicates = 1)
    {
        ArgumentNullException.ThrowIfNull(klaDoc);
        ArgumentNullException.ThrowIfNull(klaDoc.Snapshot);

        var mapId = klaDoc.Snapshot.Id;
        var mapName = string.IsNullOrWhiteSpace(klaDoc.Snapshot.Name) ? "Mapa kLa" : klaDoc.Snapshot.Name.Trim();

        var rawAnchors = klaDoc.Snapshot.Anchors;
        if (rawAnchors is null || rawAnchors.Length == 0)
        {
            return (mapId, mapName, []);
        }

        // Deduplicate coordinates within tolerance: N +/- 0.5 RPM, Qg +/- 0.02 LPM
        var uniqueConditions = new List<(double N, double Q)>();
        foreach (var a in rawAnchors)
        {
            var n = Math.Round(a.AgitationRpm, 1);
            var q = Math.Round(a.AirflowLpm, 2);

            var exists = uniqueConditions.Any(c => Math.Abs(c.N - n) < 0.5 && Math.Abs(c.Q - q) < 0.02);
            if (!exists)
            {
                uniqueConditions.Add((n, q));
            }
        }

        // Canonical ordering: N ascending, then Q ascending
        var sorted = uniqueConditions
            .OrderBy(c => c.N)
            .ThenBy(c => c.Q)
            .ToList();

        var conditions = sorted.Select((c, idx) => new PowerCondition
        {
            ConditionId = Guid.NewGuid(),
            OrderIndex = idx,
            AgitationRpm = c.N,
            GasFlowLpm = c.Q,
            GasMode = c.Q > 0 ? PowerGasMode.Gassed : PowerGasMode.Ungassed,
            RequestedReplicates = Math.Max(1, defaultReplicates),
            Origin = PowerConditionOrigin.Map,
            SourceMapId = mapId,
            SourceMapName = mapName,
            Status = PowerConditionStatus.Pending,
        }).ToList();

        return (mapId, mapName, conditions);
    }

    /// <summary>
    /// Matches executed power assay runs to experimental kLa anchors within specified tolerances.
    /// Produces KlaPowerPair items with volumetric power (P/V) and superficial gas velocity (vs).
    /// </summary>
    public static IReadOnlyList<KlaPowerPair> MatchPowerTestToKlaMap(
        PowerTestDocument powerDoc,
        KlaExperimentDocument klaDoc,
        double rpmTolerance = DefaultRpmTolerance,
        double flowToleranceLpm = DefaultFlowToleranceLpm)
    {
        ArgumentNullException.ThrowIfNull(powerDoc);
        ArgumentNullException.ThrowIfNull(klaDoc);
        ArgumentNullException.ThrowIfNull(klaDoc.Snapshot);

        var anchors = klaDoc.Snapshot.Anchors;
        if (anchors is null || anchors.Length == 0 || powerDoc.Runs.Count == 0)
        {
            return [];
        }

        var vesselDiameterM = powerDoc.Geometry.VesselDiameterM > 0 ? powerDoc.Geometry.VesselDiameterM : 0.190;
        var liquidVolumeM3 = powerDoc.Geometry.LiquidVolumeM3;

        var acceptedRuns = powerDoc.Runs
            .Where(r => r.Phase == PowerRunPhase.Accepted)
            .ToList();

        var pairs = new List<KlaPowerPair>();

        foreach (var anchor in anchors)
        {
            var matchingRuns = acceptedRuns.Where(r =>
            {
                var matchRpm = Math.Abs(r.AgitationRpm - anchor.AgitationRpm) <= rpmTolerance;
                var runFlow = r.GasMode == PowerGasMode.Gassed ? (r.GasFlowLpm ?? 0.0) : 0.0;
                var matchFlow = Math.Abs(runFlow - anchor.AirflowLpm) <= flowToleranceLpm;
                return matchRpm && matchFlow;
            }).ToList();

            if (matchingRuns.Count == 0)
            {
                continue;
            }

            // If multiple replicates exist, select the one with best confidence (lowest CI95) or latest
            var bestRun = matchingRuns.OrderBy(r => r.Ci95PowerW ?? double.MaxValue).First();
            var netPower = bestRun.NetPowerW ?? 0.0;

            var vs = PowerCalc.GasSuperficialVelocity(anchor.AirflowLpm, vesselDiameterM);
            var pv = liquidVolumeM3 > 0 ? PowerCalc.VolumetricPower(netPower, liquidVolumeM3) : double.NaN;

            // Look up anchor CI95 if present in ImportedMeasurements
            var meas = klaDoc.ImportedMeasurements.FirstOrDefault(m =>
                Math.Abs(m.AgitationRpm - anchor.AgitationRpm) <= rpmTolerance &&
                Math.Abs(m.AirflowLpm - anchor.AirflowLpm) <= flowToleranceLpm);

            var ci95 = meas is not null && meas.ConfidenceInterval95High > meas.ConfidenceInterval95Low
                ? (meas.ConfidenceInterval95High - meas.ConfidenceInterval95Low) / 2.0
                : anchor.KlaPerHour * 0.05; // 5% default uncertainty if unavailable

            pairs.Add(new KlaPowerPair
            {
                PairId = Guid.NewGuid(),
                SourcePowerTestId = powerDoc.TestId,
                SourceKlaTestId = klaDoc.Snapshot.Id,
                AgitationRpm = anchor.AgitationRpm,
                GasFlowLpm = anchor.AirflowLpm,
                SuperficialVelocityMs = vs,
                NetPowerW = netPower,
                VolumetricPowerWm3 = pv,
                KlaPerHour = anchor.KlaPerHour,
                ConfidenceInterval95 = ci95,
            });
        }

        return pairs;
    }
}
