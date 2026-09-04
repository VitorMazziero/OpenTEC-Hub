using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OpenTECHub.Services.KlaMapping;
using OpenTECHub.Services.PowerMapping;
using OpenTECHub.Services.PowerTesting;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class PowerMapImportHelperTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _klaRoot;
    private readonly string _powerRoot;
    private readonly KlaProfileStore _klaStore;
    private readonly PowerTestStore _powerStore;
    private readonly KlaPowerIntegrationService _service;

    public PowerMapImportHelperTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "PowerImportHelperTests_" + Guid.NewGuid().ToString("N"));
        _klaRoot = Path.Combine(_tempRoot, "Mapas");
        _powerRoot = Path.Combine(_tempRoot, "Testes-Potencia");

        Directory.CreateDirectory(_klaRoot);
        Directory.CreateDirectory(_powerRoot);

        _klaStore = new KlaProfileStore(_klaRoot);
        _powerStore = new PowerTestStore(_powerRoot);
        _service = new KlaPowerIntegrationService(_klaStore, _powerStore);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            try
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
            catch
            {
                // Cleanup
            }
        }
    }

    [Fact]
    public void ImportConditionsFromKlaMap_With_3x3_Factorial_Imports_9_Sorted_Conditions()
    {
        // 3x3 anchors: N in {200, 400, 600}, Qg in {2, 5, 10}
        var rawAnchors = new List<KlaAnchor>
        {
            new(10.0, 600.0, 75.0),
            new(2.0, 200.0, 15.0),
            new(5.0, 400.0, 40.0),
            new(5.0, 200.0, 20.0),
            new(10.0, 200.0, 28.0),
            new(2.0, 400.0, 30.0),
            new(10.0, 400.0, 52.0),
            new(2.0, 600.0, 45.0),
            new(5.0, 600.0, 60.0),
        };

        var mapId = Guid.NewGuid();
        var snapshot = new KlaExperimentSnapshot
        {
            Id = mapId,
            Name = "Mapa kLa 3x3",
            Domain = new KlaDomain(0, 15, 15, 1000),
            Anchors = rawAnchors.ToArray(),
        };

        var doc = new KlaExperimentDocument { Snapshot = snapshot };

        var (sourceId, sourceName, conditions) = PowerMapImportHelper.ImportConditionsFromKlaMap(doc, defaultReplicates: 2);

        Assert.Equal(mapId, sourceId);
        Assert.Equal("Mapa kLa 3x3", sourceName);
        Assert.Equal(9, conditions.Count);

        // Verify canonical ordering: N ascending (200, 200, 200, 400, 400, 400, 600, 600, 600), then Q ascending
        for (var i = 0; i < conditions.Count; i++)
        {
            Assert.Equal(i, conditions[i].OrderIndex);
            Assert.Equal(PowerConditionOrigin.Map, conditions[i].Origin);
            Assert.Equal(mapId, conditions[i].SourceMapId);
            Assert.Equal("Mapa kLa 3x3", conditions[i].SourceMapName);
            Assert.Equal(2, conditions[i].RequestedReplicates);
            Assert.Equal(PowerGasMode.Gassed, conditions[i].GasMode);
        }

        // Check first and last conditions
        Assert.Equal(200.0, conditions[0].AgitationRpm);
        Assert.Equal(2.0, conditions[0].GasFlowLpm);

        Assert.Equal(600.0, conditions[^1].AgitationRpm);
        Assert.Equal(10.0, conditions[^1].GasFlowLpm);
    }

    [Fact]
    public void ImportConditionsFromKlaMap_Deduplicates_Duplicate_Anchors_Within_Tolerances()
    {
        var rawAnchors = new List<KlaAnchor>
        {
            new(5.0, 300.0, 25.0),
            new(5.01, 300.1, 26.0), // Near-duplicate of (5.0, 300.0)
            new(10.0, 300.0, 40.0),
        };

        var snapshot = new KlaExperimentSnapshot
        {
            Id = Guid.NewGuid(),
            Name = "Mapa Com Duplicatas",
            Domain = new KlaDomain(0, 15, 15, 1000),
            Anchors = rawAnchors.ToArray(),
        };

        var doc = new KlaExperimentDocument { Snapshot = snapshot };

        var (_, _, conditions) = PowerMapImportHelper.ImportConditionsFromKlaMap(doc);

        // Duplicates merged -> 2 unique conditions
        Assert.Equal(2, conditions.Count);
        Assert.Equal(5.0, conditions[0].GasFlowLpm);
        Assert.Equal(10.0, conditions[1].GasFlowLpm);
    }

    [Fact]
    public void MatchPowerTestToKlaMap_Matches_Runs_To_Anchors_And_Computes_Pv_And_Vs()
    {
        var powerDoc = new PowerTestDocument
        {
            TestId = Guid.NewGuid(),
            Name = "Ensaio Rushton Agua",
            Geometry = new PowerGeometry
            {
                Impellers = [new Impeller { Type = ImpellerType.RushtonFlatBlade, DiameterM = 0.060 }],
                VesselDiameterM = 0.190,
                LiquidVolumeM3 = 0.010, // 10 L
            },
            Runs = [
                new PowerRunSummary
                {
                    RunId = Guid.NewGuid(),
                    AgitationRpm = 300.0,
                    GasMode = PowerGasMode.Gassed,
                    GasFlowLpm = 5.0,
                    Phase = PowerRunPhase.Accepted,
                    NetPowerW = 5.0,
                    Ci95PowerW = 0.15,
                },
                new PowerRunSummary
                {
                    RunId = Guid.NewGuid(),
                    AgitationRpm = 500.0,
                    GasMode = PowerGasMode.Gassed,
                    GasFlowLpm = 10.0,
                    Phase = PowerRunPhase.Accepted,
                    NetPowerW = 20.0,
                    Ci95PowerW = 0.30,
                },
                new PowerRunSummary
                {
                    RunId = Guid.NewGuid(),
                    AgitationRpm = 300.0,
                    GasMode = PowerGasMode.Ungassed,
                    Phase = PowerRunPhase.Accepted,
                    NetPowerW = 6.5,
                    Ci95PowerW = 0.20,
                }
            ],
        };

        var klaDoc = new KlaExperimentDocument
        {
            Snapshot = new KlaExperimentSnapshot
            {
                Id = Guid.NewGuid(),
                Name = "Mapa Referencia",
                Domain = new KlaDomain(0, 15, 15, 1000),
                Anchors =
                [
                    new KlaAnchor(5.0, 300.0, 35.0),
                    new KlaAnchor(10.0, 500.0, 80.0),
                    new KlaAnchor(2.0, 300.0, 22.0), // Unmatched in power runs
                ],
            },
        };

        var pairs = PowerMapImportHelper.MatchPowerTestToKlaMap(powerDoc, klaDoc);

        // 2 matched pairs
        Assert.Equal(2, pairs.Count);

        var p1 = pairs.First(p => Math.Abs(p.AgitationRpm - 300.0) < 0.1);
        Assert.Equal(300.0, p1.AgitationRpm);
        Assert.Equal(5.0, p1.GasFlowLpm);
        Assert.Equal(5.0, p1.NetPowerW);
        Assert.Equal(500.0, p1.VolumetricPowerWm3); // 5.0 W / 0.010 m³
        Assert.Equal(35.0, p1.KlaPerHour);
        Assert.True(p1.SuperficialVelocityMs > 0.0029 && p1.SuperficialVelocityMs < 0.0030);

        var p2 = pairs.First(p => Math.Abs(p.AgitationRpm - 500.0) < 0.1);
        Assert.Equal(500.0, p2.AgitationRpm);
        Assert.Equal(10.0, p2.GasFlowLpm);
        Assert.Equal(20.0, p2.NetPowerW);
        Assert.Equal(2000.0, p2.VolumetricPowerWm3); // 20.0 W / 0.010 m³
        Assert.Equal(80.0, p2.KlaPerHour);
    }

    [Fact]
    public void MatchPowerTestToKlaMap_Picks_Lowest_Variance_Accepted_Replicate_And_Ignores_Rejected()
    {
        var powerDoc = new PowerTestDocument
        {
            TestId = Guid.NewGuid(),
            Name = "Ensaio Replicas",
            Geometry = new PowerGeometry { VesselDiameterM = 0.190, LiquidVolumeM3 = 0.010 },
            Runs = [
                // Rep 1: Rejected
                new PowerRunSummary
                {
                    RunId = Guid.NewGuid(),
                    AgitationRpm = 300.0,
                    GasMode = PowerGasMode.Gassed,
                    GasFlowLpm = 5.0,
                    Phase = PowerRunPhase.Rejected,
                    NetPowerW = 99.0,
                    Ci95PowerW = 5.0,
                },
                // Rep 2: Accepted, higher variance
                new PowerRunSummary
                {
                    RunId = Guid.NewGuid(),
                    AgitationRpm = 300.0,
                    GasMode = PowerGasMode.Gassed,
                    GasFlowLpm = 5.0,
                    Phase = PowerRunPhase.Accepted,
                    NetPowerW = 5.2,
                    Ci95PowerW = 0.50,
                },
                // Rep 3: Accepted, lowest variance (best estimate)
                new PowerRunSummary
                {
                    RunId = Guid.NewGuid(),
                    AgitationRpm = 300.0,
                    GasMode = PowerGasMode.Gassed,
                    GasFlowLpm = 5.0,
                    Phase = PowerRunPhase.Accepted,
                    NetPowerW = 5.0,
                    Ci95PowerW = 0.08,
                },
            ],
        };

        var klaDoc = new KlaExperimentDocument
        {
            Snapshot = new KlaExperimentSnapshot
            {
                Id = Guid.NewGuid(),
                Name = "Mapa kLa",
                Domain = new KlaDomain(0, 15, 15, 1000),
                Anchors = [new KlaAnchor(5.0, 300.0, 35.0)],
            },
        };

        var pairs = PowerMapImportHelper.MatchPowerTestToKlaMap(powerDoc, klaDoc);

        Assert.Single(pairs);
        // Best run has NetPower = 5.0 (lowest CI95)
        Assert.Equal(5.0, pairs[0].NetPowerW);
    }

    [Fact]
    public async Task KlaPowerIntegrationService_End_To_End_Import_And_Enriched_Export()
    {
        // 1. Save a kLa experiment document into KlaProfileStore
        var klaMapId = Guid.NewGuid();
        var klaSnapshot = new KlaExperimentSnapshot
        {
            Id = klaMapId,
            Name = "Perfil Oxigenacao Fermentador",
            Domain = new KlaDomain(0, 15, 15, 1000),
            Anchors =
            [
                new KlaAnchor(5.0, 300.0, 45.0),
                new KlaAnchor(10.0, 500.0, 95.0),
            ],
        };

        var originalKlaDoc = new KlaExperimentDocument
        {
            Snapshot = klaSnapshot,
            ReviewNote = "Revisão experimental original.",
        };
        await _klaStore.SaveExperimentAsync(originalKlaDoc);

        // 2. Import conditions from kLa map
        var importedConditions = await _service.ImportConditionsFromKlaAsync(klaMapId);
        Assert.Equal(2, importedConditions.Count);
        Assert.Equal(300.0, importedConditions[0].AgitationRpm);
        Assert.Equal(5.0, importedConditions[0].GasFlowLpm);

        // 3. Create and save matching power test document
        var powerDoc = _powerStore.CreateTest(
            "Ensaio_Potencia_Acoplado",
            new FluidProperties { DensityKgM3 = 1000.0, ViscosityPaS = 0.001 },
            new PowerGeometry { VesselDiameterM = 0.190, LiquidVolumeM3 = 0.010 },
            new PowerTestSettings(),
            importedConditions);

        // Add accepted run results
        var run1 = new PowerRunSummary
        {
            RunId = Guid.NewGuid(),
            ConditionId = importedConditions[0].ConditionId,
            AgitationRpm = 300.0,
            GasMode = PowerGasMode.Gassed,
            GasFlowLpm = 5.0,
            Phase = PowerRunPhase.Accepted,
            NetPowerW = 4.8,
            Ci95PowerW = 0.12,
        };

        var run2 = new PowerRunSummary
        {
            RunId = Guid.NewGuid(),
            ConditionId = importedConditions[1].ConditionId,
            AgitationRpm = 500.0,
            GasMode = PowerGasMode.Gassed,
            GasFlowLpm = 10.0,
            Phase = PowerRunPhase.Accepted,
            NetPowerW = 21.5,
            Ci95PowerW = 0.25,
        };

        powerDoc.Runs = [run1, run2];
        powerDoc.Status = PowerTestStatus.Completed;
        _powerStore.SaveTestManifest(powerDoc);

        // 4. Export power results to kLa map
        var exportRes = await _service.ExportPowerResultsToKlaMapAsync(powerDoc.TestId, klaMapId);

        Assert.True(exportRes.Success);
        Assert.Equal(2, exportRes.MatchedPairsCount);
        Assert.Equal(klaMapId, exportRes.OriginalMapId);
        Assert.NotEqual(klaMapId, exportRes.EnrichedMapId);
        Assert.Equal("Perfil Oxigenacao Fermentador + Potência", exportRes.EnrichedMapName);
        Assert.False(string.IsNullOrWhiteSpace(exportRes.SourcePowerTestSha256));
        Assert.False(string.IsNullOrWhiteSpace(exportRes.SourceKlaMapFingerprint));

        // 5. Verify both documents in KlaProfileStore
        var allKlaExperiments = await _klaStore.LoadExperimentsAsync();
        Assert.Equal(2, allKlaExperiments.Count);

        var originalInStore = allKlaExperiments.First(e => e.Snapshot.Id == klaMapId);
        Assert.Equal("Perfil Oxigenacao Fermentador", originalInStore.Snapshot.Name);
        Assert.Equal("Revisão experimental original.", originalInStore.ReviewNote); // UNCHANGED!

        var enrichedInStore = allKlaExperiments.First(e => e.Snapshot.Id == exportRes.EnrichedMapId);
        Assert.Equal("Perfil Oxigenacao Fermentador + Potência", enrichedInStore.Snapshot.Name);
        Assert.Contains("[Acoplamento Potência]", enrichedInStore.ReviewNote);
    }
}
