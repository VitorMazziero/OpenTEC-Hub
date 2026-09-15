using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using OpenTECHub.Services.KlaMapping;
using OpenTECHub.Services.PowerTesting;

namespace OpenTECHub.Services.PowerMapping;

/// <summary>
/// Evaluates two already-built maps on a common physical grid. Raw assay documents are
/// intentionally absent from this API: they belong to the map-construction stage only.
/// </summary>
public sealed class SurfaceIntersectionService
{
    public SurfaceIntersectionResult Intersect(
        PowerMapDocument powerMap,
        KlaSurface klaSurface)
    {
        ArgumentNullException.ThrowIfNull(powerMap);
        ArgumentNullException.ThrowIfNull(klaSurface);

        var powerSurface = powerMap.SurfaceData;
        if (powerSurface is null || powerSurface.ResolutionN < 1 || powerSurface.ResolutionQg < 1)
        {
            return Empty(powerMap.MapId, klaSurface, "O mapa de potência ainda não possui uma superfície reconstruída.");
        }

        var klaDomain = klaSurface.Input.Domain;
        var minRpm = Math.Max(powerSurface.MinRpm, klaDomain.AgitationMinimumRpm);
        var maxRpm = Math.Min(powerSurface.MaxRpm, klaDomain.AgitationMaximumRpm);
        var minFlow = Math.Max(powerSurface.MinFlowLpm, klaDomain.AirflowMinimumLpm);
        var maxFlow = Math.Min(powerSurface.MaxFlowLpm, klaDomain.AirflowMaximumLpm);
        var warnings = new List<string>();

        if (minRpm >= maxRpm || minFlow >= maxFlow)
        {
            return Empty(powerMap.MapId, klaSurface,
                "Os domínios dos mapas não se sobrepõem em N e Qg.");
        }

        var rows = powerSurface.ResolutionN;
        var cols = powerSurface.ResolutionQg;
        var total = rows * cols;
        var klaValues = new double?[total];
        var pvValues = new double?[total];
        var vsValues = new double?[total];
        var efficiencyValues = new double?[total];
        var validMask = new bool[total];
        var validCells = new List<EfficiencyCell>();
        var candidateCount = 0;
        var invalidPowerCount = 0;
        var invalidKlaCount = 0;
        var vesselD = powerMap.Geometry.VesselDiameterM;
        var hasVessel = double.IsFinite(vesselD) && vesselD > 0;

        if (!hasVessel)
        {
            warnings.Add("O diâmetro do vaso não é válido; v_s não pôde ser calculada.");
        }

        var klaHull = BuildConvexHull(klaSurface.Input.Anchors
            .Where(a => double.IsFinite(a.AirflowLpm) && double.IsFinite(a.AgitationRpm))
            .Select(a => new Point(a.AirflowLpm, a.AgitationRpm)));

        for (var i = 0; i < rows; i++)
        {
            if (i >= powerSurface.RpmGrid.Length)
            {
                break;
            }

            var rpm = powerSurface.RpmGrid[i];
            if (rpm < minRpm - 1e-9 || rpm > maxRpm + 1e-9)
            {
                continue;
            }

            for (var j = 0; j < cols; j++)
            {
                if (j >= powerSurface.FlowGrid.Length)
                {
                    break;
                }

                var flow = powerSurface.FlowGrid[j];
                if (flow < minFlow - 1e-9 || flow > maxFlow + 1e-9)
                {
                    continue;
                }

                candidateCount++;
                var index = powerSurface.GetIndex(i, j);
                if (index >= powerSurface.PVolumetricSurface.Length ||
                    powerSurface.PVolumetricSurface[index] is not { } pv ||
                    !double.IsFinite(pv) || pv <= 0)
                {
                    invalidPowerCount++;
                    continue;
                }

                if (!IsInsideConvexHull(klaHull, new Point(flow, rpm)))
                {
                    invalidKlaCount++;
                    continue;
                }

                var normalizedFlow = klaDomain.NormalizeAirflow(flow);
                var normalizedRpm = klaDomain.NormalizeAgitation(rpm);
                var kla = klaSurface.EvaluateNormalized(normalizedFlow, normalizedRpm).Value;
                if (!double.IsFinite(kla) || kla <= 0)
                {
                    invalidKlaCount++;
                    continue;
                }

                var vs = hasVessel ? PowerCalc.GasSuperficialVelocity(flow, vesselD) : double.NaN;
                if (!double.IsFinite(vs) || vs <= 0)
                {
                    invalidKlaCount++;
                    continue;
                }

                var efficiency = kla / pv;
                if (!double.IsFinite(efficiency) || efficiency <= 0)
                {
                    invalidKlaCount++;
                    continue;
                }

                klaValues[index] = kla;
                pvValues[index] = pv;
                vsValues[index] = vs;
                efficiencyValues[index] = efficiency;
                validMask[index] = true;
                validCells.Add(new EfficiencyCell(rpm, flow, vs, kla, pv, efficiency));
            }
        }

        if (invalidPowerCount > 0)
        {
            warnings.Add($"{invalidPowerCount} ponto(s) comum(ns) sem P/V válido; lacunas foram preservadas.");
        }

        if (invalidKlaCount > 0)
        {
            warnings.Add($"{invalidKlaCount} ponto(s) comum(ns) fora da região válida do mapa kLa ou sem valor finito.");
        }

        if (klaSurface.Diagnostics.NearestFilledNodes > 0)
        {
            warnings.Add("A reconstrução kLa contém nós preenchidos pelo vizinho mais próximo; eles foram aceitos somente dentro do fecho convexo das âncoras.");
        }

        var efficiencies = validCells.Select(c => c.Efficiency).ToArray();
        var maximum = validCells.OrderByDescending(c => c.Efficiency).FirstOrDefault();
        var powerFingerprint = PowerMapFileContracts.ComputeSha256(JsonSerializer.Serialize(powerSurface));

        return new SurfaceIntersectionResult
        {
            PowerMapId = powerMap.MapId,
            PowerMapFingerprint = powerFingerprint,
            KlaMapId = klaSurface.Input.Id,
            KlaSurfaceFingerprint = klaSurface.Fingerprint,
            ResolutionN = rows,
            ResolutionQg = cols,
            MinRpm = minRpm,
            MaxRpm = maxRpm,
            MinFlowLpm = minFlow,
            MaxFlowLpm = maxFlow,
            RpmGrid = powerSurface.RpmGrid.ToArray(),
            FlowGrid = powerSurface.FlowGrid.ToArray(),
            KlaSurface = klaValues,
            VolumetricPowerSurface = pvValues,
            SuperficialVelocitySurface = vsValues,
            EfficiencySurface = efficiencyValues,
            ValidMask = validMask,
            CandidatePointCount = candidateCount,
            ValidPointCount = validCells.Count,
            CoveragePercent = candidateCount > 0 ? validCells.Count * 100.0 / candidateCount : 0.0,
            EfficiencyMinimum = efficiencies.Length > 0 ? efficiencies.Min() : null,
            EfficiencyMean = efficiencies.Length > 0 ? efficiencies.Average() : null,
            EfficiencyMaximum = efficiencies.Length > 0 ? efficiencies.Max() : null,
            MaximumEfficiencyRpm = maximum?.AgitationRpm,
            MaximumEfficiencyFlowLpm = maximum?.GasFlowLpm,
            Warnings = warnings,
        };
    }

    private static SurfaceIntersectionResult Empty(Guid powerMapId, KlaSurface klaSurface, string warning) =>
        new()
        {
            PowerMapId = powerMapId,
            KlaMapId = klaSurface.Input.Id,
            KlaSurfaceFingerprint = klaSurface.Fingerprint,
            Warnings = [warning],
        };

    private readonly record struct Point(double X, double Y);

    private static List<Point> BuildConvexHull(IEnumerable<Point> source)
    {
        var points = source.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (points.Count <= 2)
        {
            return points;
        }

        var lower = new List<Point>();
        foreach (var point in points)
        {
            while (lower.Count >= 2 && Cross(lower[^2], lower[^1], point) <= 1e-12)
            {
                lower.RemoveAt(lower.Count - 1);
            }

            lower.Add(point);
        }

        var upper = new List<Point>();
        foreach (var point in points.AsEnumerable().Reverse())
        {
            while (upper.Count >= 2 && Cross(upper[^2], upper[^1], point) <= 1e-12)
            {
                upper.RemoveAt(upper.Count - 1);
            }

            upper.Add(point);
        }

        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower;
    }

    private static bool IsInsideConvexHull(IReadOnlyList<Point> hull, Point point)
    {
        if (hull.Count == 0)
        {
            return false;
        }

        if (hull.Count == 1)
        {
            return DistanceSquared(hull[0], point) <= 1e-12;
        }

        if (hull.Count == 2)
        {
            return Math.Abs(Cross(hull[0], hull[1], point)) <= 1e-9 &&
                   point.X >= Math.Min(hull[0].X, hull[1].X) - 1e-9 &&
                   point.X <= Math.Max(hull[0].X, hull[1].X) + 1e-9 &&
                   point.Y >= Math.Min(hull[0].Y, hull[1].Y) - 1e-9 &&
                   point.Y <= Math.Max(hull[0].Y, hull[1].Y) + 1e-9;
        }

        for (var i = 0; i < hull.Count; i++)
        {
            var sign = Cross(hull[i], hull[(i + 1) % hull.Count], point);
            if (sign < -1e-9)
            {
                return false;
            }
        }

        return true;
    }

    private static double Cross(Point a, Point b, Point c) =>
        ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));

    private static double DistanceSquared(Point a, Point b) =>
        Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2);
}
