using System;
using System.Linq;
using OpenTECHub.Services.KlaMapping;
using OpenTECHub.Services.PowerMapping;
using OpenTECHub.Services.PowerTesting;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class SurfaceIntersectionServiceTests
{
    [Fact]
    public void Intersects_maps_on_common_physical_grid_and_preserves_local_power_gap()
    {
        var powerSurface = BuildSurface(
            [100, 450, 800],
            [1, 6, 12],
            [100, 100, 100, 200, 200, 200, 300, 300, null]);
        var powerMap = new PowerMapDocument
        {
            MapId = Guid.NewGuid(),
            Geometry = new PowerGeometry { VesselDiameterM = 0.190, LiquidVolumeM3 = 0.010 },
            SurfaceData = powerSurface,
        };
        var kla = ReconstructKla(
            new KlaDomain(1, 12, 100, 800),
            [
                new KlaAnchor(1, 100, 10), new KlaAnchor(6, 100, 12), new KlaAnchor(12, 100, 14),
                new KlaAnchor(1, 450, 20), new KlaAnchor(6, 450, 22), new KlaAnchor(12, 450, 24),
                new KlaAnchor(1, 800, 30), new KlaAnchor(6, 800, 32), new KlaAnchor(12, 800, 34),
            ]);

        var result = new SurfaceIntersectionService().Intersect(powerMap, kla);

        Assert.Equal(9, result.CandidatePointCount);
        Assert.Equal(8, result.ValidPointCount);
        Assert.True(result.CoveragePercent > 88 && result.CoveragePercent < 90);
        Assert.False(result.ValidMask[result.GetIndex(2, 2)]);
        Assert.NotEmpty(result.Warnings);
        Assert.NotEmpty(result.PowerMapFingerprint);
        Assert.NotEmpty(result.KlaSurfaceFingerprint);
    }

    [Fact]
    public void Efficiency_is_available_with_one_valid_common_point()
    {
        var surface = BuildSurface([300, 500], [5, 10], [100, null, null, null]);
        var map = new PowerMapDocument
        {
            Geometry = new PowerGeometry { VesselDiameterM = 0.190 },
            SurfaceData = surface,
        };
        var kla = ReconstructKla(
            new KlaDomain(5, 10, 300, 500),
            [
                new KlaAnchor(5, 300, 20), new KlaAnchor(7.5, 300, 20), new KlaAnchor(10, 300, 20),
                new KlaAnchor(5, 400, 20), new KlaAnchor(7.5, 400, 20), new KlaAnchor(10, 400, 20),
                new KlaAnchor(5, 500, 20), new KlaAnchor(7.5, 500, 20), new KlaAnchor(10, 500, 20),
            ]);

        var result = new SurfaceIntersectionService().Intersect(map, kla);

        Assert.Equal(1, result.ValidPointCount);
        Assert.Equal(20 / 100.0, result.EfficiencyMinimum!.Value, 8);
    }

    [Fact]
    public void Non_overlapping_domains_return_no_candidates()
    {
        var map = new PowerMapDocument
        {
            Geometry = new PowerGeometry { VesselDiameterM = 0.190 },
            SurfaceData = BuildSurface([100, 200], [1, 2], [100, 100, 100, 100]),
        };
        var kla = ReconstructKla(
            new KlaDomain(5, 10, 500, 600),
            [
                new KlaAnchor(5, 500, 20), new KlaAnchor(7.5, 500, 20), new KlaAnchor(10, 500, 20),
                new KlaAnchor(5, 550, 20), new KlaAnchor(7.5, 550, 20), new KlaAnchor(10, 550, 20),
                new KlaAnchor(5, 600, 20), new KlaAnchor(7.5, 600, 20), new KlaAnchor(10, 600, 20),
            ]);

        var result = new SurfaceIntersectionService().Intersect(map, kla);

        Assert.Equal(0, result.CandidatePointCount);
        Assert.Equal(0, result.ValidPointCount);
        Assert.Contains("não se sobrepõem", result.Warnings.Single(), StringComparison.OrdinalIgnoreCase);
    }

    private static PowerMapSurfaceData BuildSurface(double[] rpms, double[] flows, double?[] pv) => new()
    {
        ResolutionN = rpms.Length,
        ResolutionQg = flows.Length,
        MinRpm = rpms.Min(),
        MaxRpm = rpms.Max(),
        MinFlowLpm = flows.Min(),
        MaxFlowLpm = flows.Max(),
        RpmGrid = rpms,
        FlowGrid = flows,
        PVolumetricSurface = pv,
        PNetSurface = pv,
        PowerRatioSurface = pv.Select(v => v.HasValue ? (double?)0.8 : null).ToArray(),
    };

    private static KlaSurface ReconstructKla(KlaDomain domain, KlaAnchor[] anchors)
    {
        var snapshot = new KlaExperimentSnapshot
        {
            Id = Guid.NewGuid(),
            Name = "Mapa kLa de teste",
            Domain = domain,
            Anchors = anchors,
            Algorithm = new KlaAlgorithmSettings { SurfaceGridResolution = 30 },
        };
        return new KlaMappingEngine().Reconstruct(snapshot);
    }
}
