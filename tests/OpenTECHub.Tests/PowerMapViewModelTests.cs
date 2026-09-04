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
                    new KlaAnchor(10.0, 300.0, 6.533),
                    new KlaAnchor(5.0, 500.0, 12.538),
                    new KlaAnchor(10.0, 500.0, 15.008),
                ],
            },
        };
        await _klaStore.SaveExperimentAsync(klaDoc);

        using var vm = new PowerMapViewModel(_testStore, _mapStore, _engine, _klaStore, _integrationService);
        await vm.InitializeAsync();

        // Select the power test
        vm.AvailablePowerTests[0].IsSelected = true;
        // Select the kLa map
        vm.SelectedKlaMapOption = vm.AvailableKlaMaps.First(k => k.Id == klaMapId);

        var eventFired = false;
        vm.VisualizationChanged += () => eventFired = true;

        await vm.LinkKlaMapAndFitAsync();

        Assert.True(eventFired);
        Assert.NotNull(vm.CurrentCorrelation);
        Assert.Equal(4, vm.MatchedPairsCount);
        Assert.True(vm.CurrentCorrelation.R2 > 0.95);
        Assert.Contains("kLa =", vm.VanTRietFormulaText);
        Assert.NotEqual("—", vm.VanTRietAlphaText);
        Assert.NotEqual("—", vm.VanTRietBetaText);
        Assert.NotEqual("—", vm.VanTRietKText);
    }
}
