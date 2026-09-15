using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OpenTECHub.Services.KlaMapping;
using OpenTECHub.Services.PowerMapping;
using OpenTECHub.Services.PowerTesting;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class PowerMapViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IPowerTestStore _testStore;
    private readonly IPowerMapStore _mapStore;
    private readonly IPowerMapEngine _engine;
    private readonly IKlaProfileStore _klaStore;
    private readonly IKlaPowerIntegrationService _integrationService;

    public PowerMapViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "opentec_vm_test_" + Guid.NewGuid().ToString("N"));
        var testDir = Path.Combine(_tempDir, "Testes-Potencia");
        var mapDir = Path.Combine(_tempDir, "Mapas-Potencia");
        var klaDir = Path.Combine(_tempDir, "Perfis-Kla");

        Directory.CreateDirectory(testDir);
        Directory.CreateDirectory(mapDir);
        Directory.CreateDirectory(klaDir);

        _testStore = new PowerTestStore(testDir);
        _mapStore = new PowerMapStore(mapDir);
        _engine = new PowerMapEngine();
        _klaStore = new KlaProfileStore(klaDir);
        _integrationService = new KlaPowerIntegrationService(_klaStore, _testStore);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public async Task InitializeAsync_Loads_Available_Maps_PowerTests_And_KlaExperiments()
    {
        // Setup initial documents
        _mapStore.CreateMap("Mapa_Alpha", []);
        var testDoc = _testStore.CreateTest("Ensaio_Inicial", new FluidProperties(), new PowerGeometry(), new PowerTestSettings());

        var klaDoc = new KlaExperimentDocument
        {
            Snapshot = new KlaExperimentSnapshot
            {
                Id = Guid.NewGuid(),
                Name = "Kla_Biomassa",
                Domain = new KlaDomain(0, 15, 15, 1000),
                Anchors = [new KlaAnchor(5.0, 300.0, 40.0)],
            },
        };
        await _klaStore.SaveExperimentAsync(klaDoc);

        using var vm = new PowerMapViewModel(
            _testStore,
            _mapStore,
            _engine,
            _klaStore,
            _integrationService);

        await vm.InitializeAsync();

        Assert.Single(vm.AvailableMaps);
        Assert.Equal("Mapa_Alpha", vm.AvailableMaps[0].Name);

        Assert.Single(vm.AvailablePowerTests);
        Assert.Equal("Ensaio_Inicial", vm.AvailablePowerTests[0].Summary.Name);

        Assert.Single(vm.AvailableKlaMaps);
        Assert.Equal("Kla_Biomassa", vm.AvailableKlaMaps[0].Name);

        Assert.NotNull(vm.CurrentDocument);
        Assert.Equal("Mapa_Alpha", vm.CurrentDocument.Name);
    }

    [Fact]
    public void CreateMap_And_SaveMap_And_DeleteMap_Lifecycle()
    {
        using var vm = new PowerMapViewModel(_testStore, _mapStore, _engine, _klaStore, _integrationService);

        vm.NewMapName = "Mapa_Ciclo";
        vm.CreateMap();

        Assert.NotNull(vm.CurrentDocument);
        Assert.Equal("Mapa_Ciclo", vm.CurrentDocument.Name);
        Assert.Single(vm.AvailableMaps);

        // Edit and save
        vm.MapName = "Mapa_Ciclo_Editado";
        vm.Notes = "Observações de teste";
        vm.SaveMap();

        Assert.Equal("Mapa_Ciclo_Editado", vm.CurrentDocument.Name);
        Assert.Equal("Observações de teste", vm.CurrentDocument.Notes);

        // Delete without dialog confirmation mock (dialogs is null, so it deletes directly or we test DeleteMap)
        vm.DeleteMap();

        Assert.Null(vm.CurrentDocument);
        Assert.Empty(vm.AvailableMaps);
    }

    [Fact]
    public async Task ReconstructSurfaceAsync_Computes_Mesh_And_Flooding_And_Raises_Event()
    {
        // 1. Create a power test with 4 corner points
        var conditions = new List<PowerCondition>
        {
            new() { ConditionId = Guid.NewGuid(), AgitationRpm = 200, GasFlowLpm = 2, GasMode = PowerGasMode.Gassed },
            new() { ConditionId = Guid.NewGuid(), AgitationRpm = 200, GasFlowLpm = 10, GasMode = PowerGasMode.Gassed },
            new() { ConditionId = Guid.NewGuid(), AgitationRpm = 600, GasFlowLpm = 2, GasMode = PowerGasMode.Gassed },
            new() { ConditionId = Guid.NewGuid(), AgitationRpm = 600, GasFlowLpm = 10, GasMode = PowerGasMode.Gassed },
        };

        var powerDoc = _testStore.CreateTest(
            "Ensaio_Superficie",
            new FluidProperties { DensityKgM3 = 1000.0, ViscosityPaS = 0.001 },
            new PowerGeometry
            {
                Impellers = [new Impeller { Type = ImpellerType.RushtonFlatBlade, DiameterM = 0.060 }],
                VesselDiameterM = 0.190,
                LiquidVolumeM3 = 0.010,
            },
            new PowerTestSettings(),
            conditions);

        powerDoc.Runs =
        [
            new PowerRunSummary { RunId = Guid.NewGuid(), AgitationRpm = 200, GasFlowLpm = 2, NetPowerW = 1.0, PowerRatio = 0.85, Phase = PowerRunPhase.Accepted },
            new PowerRunSummary { RunId = Guid.NewGuid(), AgitationRpm = 200, GasFlowLpm = 10, NetPowerW = 0.8, PowerRatio = 0.70, Phase = PowerRunPhase.Accepted },
            new PowerRunSummary { RunId = Guid.NewGuid(), AgitationRpm = 600, GasFlowLpm = 2, NetPowerW = 25.0, PowerRatio = 0.90, Phase = PowerRunPhase.Accepted },
            new PowerRunSummary { RunId = Guid.NewGuid(), AgitationRpm = 600, GasFlowLpm = 10, NetPowerW = 18.0, PowerRatio = 0.65, Phase = PowerRunPhase.Accepted },
        ];
        _testStore.SaveTestManifest(powerDoc);

        using var vm = new PowerMapViewModel(_testStore, _mapStore, _engine, _klaStore, _integrationService);
        await vm.InitializeAsync();

        // Select the power test
        Assert.Single(vm.AvailablePowerTests);
        vm.AvailablePowerTests[0].IsSelected = true;

        vm.GridResolution = 60; // quick resolution for test

        var eventFired = false;
        vm.VisualizationChanged += () => eventFired = true;

        await vm.ReconstructSurfaceAsync();

        Assert.True(eventFired);
        Assert.NotNull(vm.CurrentSurfaceData);
        Assert.Equal(60, vm.CurrentSurfaceData.ResolutionN);
        Assert.Equal(60, vm.CurrentSurfaceData.ResolutionQg);
        Assert.Equal(4, vm.CurrentSurfaceData.AnchorPoints.Count);

        Assert.NotNull(vm.CurrentFloodingBoundary);
        Assert.NotEmpty(vm.CurrentFloodingBoundary.NienowTheoreticalPoints);
    }

    [Fact]
    public void UpdateCursorInspection_Calculates_Hydrodynamics_And_Interpolates_Values()
    {
        using var vm = new PowerMapViewModel(_testStore, _mapStore, _engine, _klaStore, _integrationService);

        var doc = _mapStore.CreateMap("Mapa_Inspecao", []);
        var updated = doc with
        {
            Geometry = new PowerGeometry
            {
                Impellers = [new Impeller { Type = ImpellerType.RushtonFlatBlade, DiameterM = 0.060 }],
                VesselDiameterM = 0.190,
                LiquidVolumeM3 = 0.010,
            },
        };
        _mapStore.SaveMap(updated);
        vm.LoadMap(updated.FolderName);

        // Manually assign simple surface data: 2x2
        var surface = new PowerMapSurfaceData
        {
            ResolutionN = 2,
            ResolutionQg = 2,
            MinRpm = 200,
            MaxRpm = 600,
            MinFlowLpm = 2,
            MaxFlowLpm = 10,
            RpmGrid = [200, 600],
            FlowGrid = [2, 10],
            PNetSurface = [1.0, 0.8, 25.0, 18.0],
            PVolumetricSurface = [100.0, 80.0, 2500.0, 1800.0],
            PowerRatioSurface = [0.85, 0.70, 0.90, 0.65],
        };
        vm.CurrentSurfaceData = surface;

        // Inspect at midpoint: N=400, Qg=6
        vm.SelectedLayer = PowerMapLayer.VolumetricPower;
        vm.UpdateCursorInspection(400, 6);

        Assert.Equal(400, vm.InspectedAgitationRpm);
        Assert.Equal(6, vm.InspectedGasFlowLpm);
        Assert.True(vm.InspectedSuperficialVelocityMs > 0);
        Assert.True(vm.InspectedGasFlowVvm > 0);

        // Bilinear midpoint of [100, 80, 2500, 1800] is (100 + 80 + 2500 + 1800) / 4 = 4480 / 4 = 1120.0
        Assert.NotNull(vm.InspectedLayerValue);
        Assert.Equal(1120.0, vm.InspectedLayerValue.Value, precision: 1);
        Assert.Contains("W/m³", vm.InspectedLayerValueFormatted);

        // Check hydrodynamic regime
        Assert.False(string.IsNullOrWhiteSpace(vm.InspectedFlowRegime));
    }

    [Fact]
    public async Task LinkKlaMapAndFitAsync_Fits_VanTRiet_Model_And_Populates_Properties()
    {
        // 1. Create a power test with 3 points
        var powerDoc = _testStore.CreateTest(
            "Ensaio_P",
            new FluidProperties(),
            new PowerGeometry { VesselDiameterM = 0.190, LiquidVolumeM3 = 0.010 },
            new PowerTestSettings());

        powerDoc.Runs =
        [
            new PowerRunSummary { RunId = Guid.NewGuid(), AgitationRpm = 300, GasMode = PowerGasMode.Gassed, GasFlowLpm = 5, NetPowerW = 5.0, Phase = PowerRunPhase.Accepted },
            new PowerRunSummary { RunId = Guid.NewGuid(), AgitationRpm = 300, GasMode = PowerGasMode.Gassed, GasFlowLpm = 10, NetPowerW = 4.5, Phase = PowerRunPhase.Accepted },
            new PowerRunSummary { RunId = Guid.NewGuid(), AgitationRpm = 500, GasMode = PowerGasMode.Gassed, GasFlowLpm = 5, NetPowerW = 20.0, Phase = PowerRunPhase.Accepted },
            new PowerRunSummary { RunId = Guid.NewGuid(), AgitationRpm = 500, GasMode = PowerGasMode.Gassed, GasFlowLpm = 10, NetPowerW = 18.0, Phase = PowerRunPhase.Accepted },
        ];
        _testStore.SaveTestManifest(powerDoc);

        // 2. Create matching kLa map with exact power law values: kLa = 1.0 * (P/V)^0.60 * (vs)^0.35
        var klaMapId = Guid.NewGuid();
        var klaDoc = new KlaExperimentDocument
        {
            Snapshot = new KlaExperimentSnapshot
            {
                Id = klaMapId,
                Name = "Mapa_Kla_Ref",
                Domain = new KlaDomain(0, 15, 15, 1000),
                Anchors =
                [
                    new KlaAnchor(5.0, 300.0, 5.457),
                    new KlaAnchor(7.5, 300.0, 6.0),
                    new KlaAnchor(10.0, 300.0, 6.533),
                    new KlaAnchor(5.0, 400.0, 8.8),
                    new KlaAnchor(7.5, 400.0, 9.8),
                    new KlaAnchor(10.0, 400.0, 10.8),
                    new KlaAnchor(5.0, 500.0, 12.538),
                    new KlaAnchor(7.5, 500.0, 13.7),
                    new KlaAnchor(10.0, 500.0, 15.008),
                ],
            },
        };
        await _klaStore.SaveExperimentAsync(klaDoc);

        using var vm = new PowerMapViewModel(_testStore, _mapStore, _engine, _klaStore, _integrationService);
        await vm.InitializeAsync();

        vm.NewMapName = "Mapa_Por_Mapas";
        vm.AvailablePowerTests.Single(t => t.Summary.TestId == powerDoc.TestId).IsSelected = true;
        vm.CreateMap();
        var mapSurface = new PowerMapSurfaceData
        {
            ResolutionN = 2,
            ResolutionQg = 2,
            MinRpm = 300,
            MaxRpm = 500,
            MinFlowLpm = 5,
            MaxFlowLpm = 10,
            RpmGrid = [300, 500],
            FlowGrid = [5, 10],
            PVolumetricSurface = [500, 450, 2000, 1800],
            PNetSurface = [5, 4.5, 20, 18],
            PowerRatioSurface = [0.8, 0.75, 0.8, 0.75],
        };
        vm.CurrentSurfaceData = mapSurface;
        vm.CurrentDocument = vm.CurrentDocument! with { SurfaceData = mapSurface };
        _mapStore.SaveMap(vm.CurrentDocument);

        // Select the power test
        // Select the kLa map
        vm.SelectedKlaMapOption = vm.AvailableKlaMaps.First(k => k.Id == klaMapId);

        var eventFired = false;
        vm.VisualizationChanged += () => eventFired = true;

        await vm.LinkKlaMapAndFitAsync();

        Assert.True(eventFired);
        Assert.NotNull(vm.CurrentCorrelation);
        Assert.Equal(4, vm.MatchedPairsCount);
        Assert.Equal("Intersecção de superfícies (mapa kLa × mapa de potência)", vm.CurrentCorrelation!.DataBasis);
        Assert.True(vm.CurrentCorrelation.R2 > 0.95);
        Assert.Contains("kLa =", vm.VanTRietFormulaText);
        Assert.NotEqual("—", vm.VanTRietAlphaText);
        Assert.NotEqual("—", vm.VanTRietBetaText);
        Assert.NotEqual("—", vm.VanTRietKText);
    }

    [Fact]
    public async Task RefreshOnEnter_picks_up_new_assays_without_losing_the_open_map_or_the_ticked_ones()
    {
        var first = _testStore.CreateTest("Ensaio_A", new FluidProperties(), new PowerGeometry(), new PowerTestSettings());
        _testStore.SaveTestManifest(first);

        using var vm = new PowerMapViewModel(_testStore, _mapStore, _engine, _klaStore, _integrationService);
        await vm.RefreshOnEnterCommand.ExecuteAsync(null);

        Assert.Single(vm.AvailablePowerTests);

        vm.NewMapName = "Mapa aberto";
        vm.AvailablePowerTests[0].IsSelected = true;
        vm.CreateMap();

        var openMapId = vm.CurrentDocument!.MapId;

        // An assay finished elsewhere while this page was open in another tab.
        var second = _testStore.CreateTest("Ensaio_B", new FluidProperties(), new PowerGeometry(), new PowerTestSettings());
        _testStore.SaveTestManifest(second);

        await vm.RefreshOnEnterCommand.ExecuteAsync(null);

        // The new assay is offered...
        Assert.Equal(2, vm.AvailablePowerTests.Count);
        Assert.Contains(vm.AvailablePowerTests, t => t.Summary.Name == "Ensaio_B");

        // ...without dropping the open map or the tick the operator had made.
        Assert.Equal(openMapId, vm.CurrentDocument!.MapId);
        Assert.True(vm.AvailablePowerTests.Single(t => t.Summary.Name == "Ensaio_A").IsSelected);
        Assert.False(vm.AvailablePowerTests.Single(t => t.Summary.Name == "Ensaio_B").IsSelected);

        // Re-reading the workspace is not an edit: the mesh must not be flagged stale by it.
        Assert.False(vm.IsSurfaceStale);

        // The benchmarking picker sees the same assays.
        Assert.Equal(0, vm.Comparison.AvailableTests.Count(t => t.IsSelected));
    }

    [Fact]
    public async Task Deleting_the_open_map_discards_the_grid_instead_of_leaving_it_on_screen()
    {
        var powerDoc = BuildAssayWithAcceptedRuns("Ensaio_para_excluir");

        using var vm = new PowerMapViewModel(_testStore, _mapStore, _engine, _klaStore, _integrationService);
        await vm.RefreshOnEnterCommand.ExecuteAsync(null);

        vm.NewMapName = "Mapa a excluir";
        vm.AvailablePowerTests.Single(t => t.Summary.TestId == powerDoc.TestId).IsSelected = true;
        vm.CreateMap();
        vm.AvailablePowerTests.Single(t => t.Summary.TestId == powerDoc.TestId).IsSelected = true;
        vm.GridResolution = 50;

        await vm.ReconstructSurfaceAsync();
        Assert.NotNull(vm.CurrentSurfaceData);

        vm.DeleteMap();

        // With the map gone there is nothing for the surface to belong to.
        Assert.Null(vm.CurrentDocument);
        Assert.Null(vm.CurrentSurfaceData);
        Assert.Null(vm.CurrentFloodingBoundary);
        Assert.False(vm.IsBusy);
    }

    /// <summary>An assay with four accepted points spanning two rotations and two gas flows.</summary>
    private PowerTestDocument BuildAssayWithAcceptedRuns(string name)
    {
        var doc = _testStore.CreateTest(
            name,
            new FluidProperties { DensityKgM3 = 998, ViscosityPaS = 0.001 },
            new PowerGeometry
            {
                Impellers = [new Impeller { StageIndex = 0, DiameterM = 0.060 }],
                VesselDiameterM = 0.190,
                LiquidVolumeM3 = 0.010,
            },
            new PowerTestSettings());

        foreach (var (rpm, flow, power) in new[]
                 {
                     (300.0, 0.0, 2.0), (300.0, 5.0, 1.5),
                     (500.0, 0.0, 9.0), (500.0, 5.0, 7.0),
                 })
        {
            doc.Runs.Add(new PowerRunSummary
            {
                RunId = Guid.NewGuid(),
                AgitationRpm = rpm,
                MeanRpmMeasured = rpm,
                GasFlowLpm = flow,
                GasMode = flow > 0 ? PowerGasMode.Gassed : PowerGasMode.Ungassed,
                Phase = PowerRunPhase.Accepted,
                NetPowerW = power,
                PowerRatio = flow > 0 ? 0.78 : 1.0,
                StartedUtc = DateTimeOffset.UtcNow,
            });
        }

        _testStore.SaveTestManifest(doc);
        return doc;
    }

    [Fact]
    public async Task An_empty_layer_says_which_data_is_missing_rather_than_blaming_collinearity()
    {
        // An ungassed-only assay: two rotations at Qg = 0. P/V is fine, P_G/P0 has nothing.
        var doc = _testStore.CreateTest(
            "Somente_sem_gas",
            new FluidProperties { DensityKgM3 = 998, ViscosityPaS = 0.001 },
            new PowerGeometry
            {
                Impellers = [new Impeller { StageIndex = 0, DiameterM = 0.060 }],
                VesselDiameterM = 0.190,
                LiquidVolumeM3 = 0.010,
            },
            new PowerTestSettings());

        foreach (var (rpm, power) in new[] { (300.0, 2.0), (400.0, 5.0), (500.0, 9.0) })
        {
            doc.Runs.Add(new PowerRunSummary
            {
                RunId = Guid.NewGuid(),
                AgitationRpm = rpm,
                MeanRpmMeasured = rpm,
                GasMode = PowerGasMode.Ungassed,
                Phase = PowerRunPhase.Accepted,
                NetPowerW = power,
                StartedUtc = DateTimeOffset.UtcNow,
            });
        }

        _testStore.SaveTestManifest(doc);

        using var vm = new PowerMapViewModel(_testStore, _mapStore, _engine, _klaStore, _integrationService);
        await vm.RefreshOnEnterCommand.ExecuteAsync(null);

        vm.NewMapName = "Mapa sem gás";
        vm.AvailablePowerTests.Single(t => t.Summary.TestId == doc.TestId).IsSelected = true;
        vm.CreateMap();
        vm.AvailablePowerTests.Single(t => t.Summary.TestId == doc.TestId).IsSelected = true;
        vm.GridResolution = 50;

        await vm.ReconstructSurfaceAsync();

        // All anchors sit on Qg = 0, so no layer is drawable - and the message must name that,
        // not send the operator looking for gassed points that were never planned.
        vm.SelectedLayer = PowerMapLayer.PowerRatio;
        Assert.False(vm.TryBuildLayerField(out _, out _, out _));
        Assert.Contains("gaseificado", vm.DescribeUndrawableLayer(), StringComparison.OrdinalIgnoreCase);

        vm.SelectedLayer = PowerMapLayer.VolumetricPower;
        Assert.Contains("colineares", vm.DescribeUndrawableLayer(), StringComparison.OrdinalIgnoreCase);

        // And the reconstruction itself explains why nothing was interpolated.
        Assert.Contains("colineares", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Contrast_narrows_the_colour_range_and_a_manual_range_overrides_it()
    {
        using var vm = new PowerMapViewModel(_testStore, _mapStore, _engine, _klaStore, _integrationService);

        // Full contrast keeps the data range untouched.
        vm.AutoScale = true;
        vm.ContrastPercent = 100;
        Assert.Equal((0.0, 100.0), vm.ResolveDisplayRange(0, 100));

        // Half contrast squeezes the range around its midpoint, saturating the extremes.
        vm.ContrastPercent = 50;
        var (min, max) = vm.ResolveDisplayRange(0, 100);
        Assert.Equal(25.0, min, 9);
        Assert.Equal(75.0, max, 9);

        // A manual range wins outright.
        vm.AutoScale = false;
        vm.ManualScaleMin = 10;
        vm.ManualScaleMax = 20;
        Assert.Equal((10.0, 20.0), vm.ResolveDisplayRange(0, 100));

        // An inverted or incomplete manual range falls back to the data instead of drawing nothing.
        vm.ManualScaleMin = 30;
        vm.ManualScaleMax = 20;
        Assert.Equal((25.0, 75.0), vm.ResolveDisplayRange(0, 100));
    }

    [Fact]
    public async Task FitVanTRietModel_When_Points_Are_Collinear_Displays_Clear_Refusal_In_UI()
    {
        // 4 matched conditions with identical RPM and Qg -> collinear in P/V and vs
        var testDoc = _testStore.CreateTest(
            "Ensaio_Colinear",
            new FluidProperties { DensityKgM3 = 1000, ViscosityPaS = 0.001 },
            new PowerGeometry
            {
                Impellers = [new Impeller { StageIndex = 0, DiameterM = 0.060 }],
                VesselDiameterM = 0.190,
                LiquidVolumeM3 = 0.010,
            },
            new PowerTestSettings());

        for (var i = 0; i < 4; i++)
        {
            testDoc.Runs.Add(new PowerRunSummary
            {
                RunId = Guid.NewGuid(),
                AgitationRpm = 300,
                GasFlowLpm = 5.0,
                MeanRpmMeasured = 300,
                GasMode = PowerGasMode.Gassed,
                Phase = PowerRunPhase.Accepted,
                NetPowerW = 5.0,
                GasFlowVvm = 0.5,
                GasFlowNumber = 0.02,
                FroudeNumber = 0.15,
                GassedPowerW = 5.0,
                PowerRatio = 0.8,
            });
        }
        _testStore.SaveTestManifest(testDoc);

        var klaDoc = new KlaExperimentDocument
        {
            Snapshot = new KlaExperimentSnapshot
            {
                Id = Guid.NewGuid(),
                Name = "Kla_Colinear",
                Domain = new KlaDomain(0, 15, 15, 1000),
                Anchors =
                [
                    new KlaAnchor(5.0, 300.0, 20.0),
                    new KlaAnchor(7.5, 300.0, 20.0),
                    new KlaAnchor(10.0, 300.0, 20.0),
                    new KlaAnchor(5.0, 400.0, 20.0),
                    new KlaAnchor(7.5, 400.0, 20.0),
                    new KlaAnchor(10.0, 400.0, 20.0),
                    new KlaAnchor(5.0, 500.0, 20.0),
                    new KlaAnchor(7.5, 500.0, 20.0),
                    new KlaAnchor(10.0, 500.0, 20.0),
                ],
            },
        };
        await _klaStore.SaveExperimentAsync(klaDoc);

        using var vm = new PowerMapViewModel(_testStore, _mapStore, _engine, _klaStore, _integrationService);
        await vm.InitializeAsync();

        vm.AvailablePowerTests.Single(t => t.Summary.TestId == testDoc.TestId).IsSelected = true;
        vm.NewMapName = "Mapa_Colinear";
        vm.CreateMap();
        var surface = new PowerMapSurfaceData
        {
            ResolutionN = 2,
            ResolutionQg = 2,
            MinRpm = 300,
            MaxRpm = 500,
            MinFlowLpm = 5,
            MaxFlowLpm = 10,
            RpmGrid = [300, 500],
            FlowGrid = [5, 10],
            PVolumetricSurface = [100, 100, 100, 100],
            PNetSurface = [1, 1, 1, 1],
            PowerRatioSurface = [0.8, 0.8, 0.8, 0.8],
        };
        vm.CurrentSurfaceData = surface;
        vm.CurrentDocument = vm.CurrentDocument! with { SurfaceData = surface };
        _mapStore.SaveMap(vm.CurrentDocument);
        vm.SelectedKlaMapOption = vm.AvailableKlaMaps.Single(m => m.Id == klaDoc.Snapshot.Id);

        await vm.LinkKlaMapAndFitAsync();

        Assert.Contains("indisponível", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Ajuste recusado", vm.VanTRietFormulaText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("—", vm.VanTRietKText);
        Assert.Equal("—", vm.VanTRietR2Text);
    }
}
