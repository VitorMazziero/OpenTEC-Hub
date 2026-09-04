using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.KlaMapping;
using OpenTECHub.Services.PowerMapping;
using OpenTECHub.Services.PowerTesting;
using OpenTECHub.Simulator;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// Phase 3 gate (§18.3 step 8.2): kLa map imported as power conditions, the sweep executed against
/// the simulator through the real runner, the surface synthesised, P/V exported back into an
/// enriched kLa map, and the van 't Riet correlation fitted.
/// </summary>
public sealed class PowerPhase3EndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PowerPhase3E2E_" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));

    private readonly KlaProfileStore _klaStore;
    private readonly PowerTestStore _testStore;
    private readonly PowerMapStore _mapStore;
    private readonly KlaPowerIntegrationService _integration;
    private readonly E2EDeviceService _device;
    private readonly CommandArbiter _arbiter;
    private readonly PowerTestRunner _runner;
    private readonly DeviceModel _simulator;

    public PowerPhase3EndToEndTests()
    {
        Directory.CreateDirectory(_root);
        _klaStore = new KlaProfileStore(Path.Combine(_root, "Mapas"));
        _testStore = new PowerTestStore(Path.Combine(_root, "Testes-Potencia"));
        _mapStore = new PowerMapStore(Path.Combine(_root, "Mapas-Potencia"));
        _integration = new KlaPowerIntegrationService(_klaStore, _testStore);

        _simulator = new DeviceModel(randomSeed: 20260904);
        _device = new E2EDeviceService(_simulator);
        _arbiter = new CommandArbiter(_device, _clock);
        _runner = new PowerTestRunner(
            _arbiter,
            _arbiter,
            _testStore,
            new PowerAnalysisEngine(),
            new AlwaysReadyInterlock(),
            _clock);
    }

    public void Dispose()
    {
        _runner.Dispose();
        _arbiter.Dispose();
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // Best effort cleanup on Windows file locks.
            }
        }
    }

    [Fact]
    public async Task Kla_map_to_power_sweep_to_enriched_map_to_correlation_to_scale_up()
    {
        // ---- 1. A kLa map already measured on this rig -----------------------------------
        var klaDocument = await PersistKlaMapAsync();

        // ---- 2. kLa -> Power: the assay runs at exactly the points kLa was measured at -----
        var imported = await _integration.ImportConditionsFromKlaAsync(klaDocument.Snapshot.Id);
        Assert.Equal(4, imported.Count);
        Assert.All(imported, c => Assert.Equal(PowerConditionOrigin.Map, c.Origin));
        Assert.All(imported, c => Assert.Equal(klaDocument.Snapshot.Id, c.SourceMapId));

        // The operator adds the ungassed references the gassed points need for P_G/P0 (§4.5).
        var conditions = BuildPlanWithUngassedReferences(imported);

        var document = _testStore.CreateTest(
            "e2e-fase3",
            new FluidProperties { DensityKgM3 = 998, ViscosityPaS = 0.001 },
            new PowerGeometry
            {
                Impellers = [new Impeller { StageIndex = 0, Type = ImpellerType.RushtonFlatBlade, DiameterM = 0.060 }],
                VesselDiameterM = 0.190,
                LiquidVolumeM3 = 0.010,
                Baffled = true,
            },
            FastSettings(),
            conditions);

        // ---- 3. The sweep, executed by the real runner against the simulator --------------
        Advance(0.5);
        await _runner.StartTestAsync(document);

        for (var tick = 0; tick < 6000 && _runner.Phase != PowerRunPhase.Completed; tick++)
        {
            Advance(0.2);
        }

        Assert.Equal(PowerRunPhase.Completed, _runner.Phase);

        var accepted = document.Runs.Where(r => r.Phase == PowerRunPhase.Accepted).ToList();
        Assert.Equal(conditions.Count, accepted.Count);
        Assert.All(accepted, r => Assert.True(r.NetPowerW is > 0, "every accepted point must carry a net power"));

        var gassed = accepted.Where(r => r.GasMode == PowerGasMode.Gassed).ToList();
        Assert.Equal(4, gassed.Count);
        Assert.All(gassed, r => Assert.NotNull(r.PowerRatio));

        // The motor is parked at the floor, never at zero (§2.6, §19).
        Assert.Equal(15, _simulator.MotorRpm);
        Assert.Equal(0, _simulator.FlowSetpoint);

        // ---- 4. Synthesis: the map view model reconstructs the 2D surface ------------------
        var mapViewModel = new PowerMapViewModel(_testStore, _mapStore, new PowerMapEngine(), _klaStore, _integration);
        await mapViewModel.InitializeAsync();

        mapViewModel.NewMapName = "Mapa E2E";
        var source = Assert.Single(mapViewModel.AvailablePowerTests);
        source.IsSelected = true;
        mapViewModel.CreateMap();

        Assert.NotNull(mapViewModel.CurrentDocument);
        mapViewModel.AvailablePowerTests.Single().IsSelected = true;
        mapViewModel.GridResolution = 60;

        await mapViewModel.ReconstructSurfaceAsync();

        var surface = mapViewModel.CurrentSurfaceData;
        Assert.NotNull(surface);
        Assert.Equal(60, surface!.ResolutionN);
        Assert.Equal(accepted.Count, surface.AnchorPoints.Count);
        Assert.Contains(surface.PNetSurface, cell => cell.HasValue);
        Assert.Contains(surface.PVolumetricSurface, cell => cell.HasValue);
        Assert.False(mapViewModel.IsSurfaceStale);

        // The grid follows the data, not the machine envelope: no wasted empty decades.
        Assert.Equal(accepted.Min(r => r.AgitationRpm), surface.MinRpm, 3);
        Assert.Equal(accepted.Max(r => r.AgitationRpm), surface.MaxRpm, 3);

        // The plan is a full factorial, so its convex hull is exactly the bounding box and every
        // cell is defined. Outside the domain there is still nothing to read: the inspector says so
        // rather than extrapolating (§19). Undefined cells inside a non-convex sweep are covered by
        // PowerMapEngineTests.
        Assert.All(surface.PNetSurface, cell => Assert.True(cell.HasValue));

        mapViewModel.UpdateCursorInspection(surface.MaxRpm + 50, surface.MaxFlowLpm + 5);
        Assert.Null(mapViewModel.InspectedLayerValue);
        Assert.Equal("Fora do domínio interpolado", mapViewModel.InspectedLayerValueFormatted);

        Assert.NotNull(mapViewModel.CurrentFloodingBoundary);
        Assert.NotEmpty(mapViewModel.CurrentFloodingBoundary!.NienowTheoreticalPoints);

        // ---- 5. Cursor inspection reads the layer under an anchor -------------------------
        var probe = surface.AnchorPoints.OrderBy(a => a.AgitationRpm).ThenBy(a => a.GasFlowLpm).ElementAt(1);
        mapViewModel.UpdateCursorInspection(probe.AgitationRpm, probe.GasFlowLpm);
        Assert.Equal(probe.AgitationRpm, mapViewModel.InspectedAgitationRpm);
        Assert.NotNull(mapViewModel.InspectedLayerValue);
        Assert.Contains("Zona", mapViewModel.InspectedFlowRegime, StringComparison.Ordinal);

        // Every layer must produce a drawable field.
        foreach (var layer in Enum.GetValues<PowerMapLayer>())
        {
            mapViewModel.SelectedLayer = layer;
            Assert.True(
                mapViewModel.TryBuildLayerField(out _, out var min, out var max),
                $"layer {layer} produced no field");
            Assert.True(max >= min);
        }

        // ---- 6. Power -> kLa: link the map and fit the multivariable correlation ----------
        mapViewModel.SelectedKlaMapOption = Assert.Single(mapViewModel.AvailableKlaMaps);
        await mapViewModel.LinkKlaMapAndFitAsync();

        var correlation = mapViewModel.CurrentCorrelation;
        Assert.NotNull(correlation);
        Assert.Equal(4, correlation!.ValidPointsCount);
        Assert.True(correlation.K > 0, "the fitted K must be positive");
        Assert.Equal(4, mapViewModel.MatchedPairsCount);
        Assert.True(mapViewModel.HasMatchedPairs);

        // Each pair carries the measured P/V and the model's prediction, ready for the parity plot.
        Assert.All(mapViewModel.MatchedPairs, p =>
        {
            Assert.True(p.VolumetricPowerWm3 > 0);
            Assert.True(p.SuperficialVelocityMs > 0);
            Assert.NotNull(p.PredictedKlaPerHour);
        });

        // ---- 7. The enriched kLa map is a new revision; the original is untouched ----------
        var beforeExport = (await _klaStore.LoadExperimentsAsync()).Count;
        await mapViewModel.ExportEnrichedKlaMapAsync();
        var afterExport = await _klaStore.LoadExperimentsAsync();

        Assert.Contains("exportado com sucesso", mapViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(beforeExport + 1, afterExport.Count);
        Assert.Contains(afterExport, e => e.Snapshot.Name.Contains("Potência", StringComparison.Ordinal));
        var original = afterExport.Single(e => e.Snapshot.Id == klaDocument.Snapshot.Id);
        Assert.Equal(klaDocument.Snapshot.Anchors.Length, original.Snapshot.Anchors.Length);

        // ---- 8. Impeller benchmarking reads the same finished assay -----------------------
        var comparison = mapViewModel.Comparison;
        comparison.ReloadTests();
        Assert.Single(comparison.AvailableTests);
        comparison.AvailableTests[0].IsSelected = true;
        comparison.BuildComparison();

        var row = Assert.Single(comparison.Rows);
        Assert.Equal("e2e-fase3", row.TestName);
        Assert.True(comparison.IsCompatible, "a single assay compares against itself as equivalent");
        Assert.Contains("benchmark;e2e-fase3", comparison.BuildCsvContent(), StringComparison.Ordinal);

        // ---- 9. The map survives a save/reload round trip --------------------------------
        mapViewModel.SaveMap();
        var reopened = new PowerMapViewModel(_testStore, _mapStore, new PowerMapEngine(), _klaStore, _integration);
        await reopened.InitializeAsync();

        Assert.NotNull(reopened.CurrentDocument);
        Assert.Equal("Mapa E2E", reopened.CurrentDocument!.Name);
        Assert.NotNull(reopened.CurrentSurfaceData);
        Assert.Equal(surface.AnchorPoints.Count, reopened.CurrentSurfaceData!.AnchorPoints.Count);
        Assert.NotNull(reopened.CurrentCorrelation);
        Assert.Equal(correlation.K, reopened.CurrentCorrelation!.K, 9);

        mapViewModel.Dispose();
        reopened.Dispose();
    }

    /// <summary>A 2x2 factorial kLa map: two rotations at two gas flows.</summary>
    private async Task<KlaExperimentDocument> PersistKlaMapAsync()
    {
        var anchors = new[]
        {
            new KlaAnchor(2.0, 300.0, 18.0),
            new KlaAnchor(5.0, 300.0, 26.0),
            new KlaAnchor(2.0, 500.0, 34.0),
            new KlaAnchor(5.0, 500.0, 48.0),
        };

        var document = new KlaExperimentDocument
        {
            Snapshot = new KlaExperimentSnapshot
            {
                Id = Guid.NewGuid(),
                Name = "Mapa kLa E2E",
                Domain = new KlaDomain(0, 15, 15, 1000),
                Anchors = anchors,
            },
        };

        await _klaStore.SaveExperimentAsync(document);
        return document;
    }

    /// <summary>
    /// The imported gassed points plus one ungassed reference per rotation, which is what the
    /// power-ratio hierarchy needs before P_G/P₀ means anything (§4.5).
    /// </summary>
    private static List<PowerCondition> BuildPlanWithUngassedReferences(IReadOnlyList<PowerCondition> imported)
    {
        var plan = new List<PowerCondition>();
        var order = 0;

        foreach (var rpm in imported.Select(c => c.AgitationRpm).Distinct().OrderBy(r => r))
        {
            plan.Add(new PowerCondition
            {
                ConditionId = Guid.NewGuid(),
                OrderIndex = order++,
                AgitationRpm = rpm,
                GasMode = PowerGasMode.Ungassed,
                RequestedReplicates = 1,
                Status = PowerConditionStatus.Pending,
            });
        }

        foreach (var condition in imported.OrderBy(c => c.AgitationRpm).ThenBy(c => c.GasFlowLpm))
        {
            condition.OrderIndex = order++;
            plan.Add(condition);
        }

        return plan;
    }

    private static PowerTestSettings FastSettings() => new()
    {
        MinRpm = 15,
        MaxRpm = 1000,
        SpeedToleranceRpm = 3,
        SpeedStableSamples = 2,
        MaxSpeedSettlingSeconds = 20,
        StationarityWindowSeconds = 1,
        StationarityRequiredSamples = 1,
        StationaritySlopeTolerancePercentPerSecond = 100,
        MinSamples = 4,
        RelativeCiFraction = 0.50,
        CiFloorSigmaMultiple = 1,
        MaxCaptureSeconds = 30,
        MaxTries = 2,
        MaxTorquePercent = 90,
        MeasurementTimeoutSeconds = 5,
        CaptureServoPollMs = 250,
        RestoreServoPollMs = 1000,
        AutoAcceptRuns = true,
    };

    private void Advance(double seconds)
    {
        _clock.Advance(TimeSpan.FromSeconds(seconds));
        _device.AdvanceSimulator(seconds);
    }

    private sealed class AlwaysReadyInterlock : IPowerTestInterlock
    {
        public bool CanStart(out string? reason)
        {
            reason = null;
            return true;
        }
    }

    private sealed class E2EDeviceService(DeviceModel simulator) : IDeviceService
    {
        private readonly TelemetryParser _parser = new();

        public ConnectionState State => ConnectionState.Connected;
        public TransportMedium? Medium => TransportMedium.Usb;
        public string Endpoint => "SIMULATOR";
        public SensorSnapshot? Latest { get; private set; }
        public LinkDiagnostics Diagnostics => new();

        public event Action<ConnectionStateChange>? StateChanged;
        public event Action<SensorSnapshot>? TelemetryReceived;
        public event Action<string>? RawTelemetryReceived;
        public event Action<string>? DeviceLogReceived;
        public event Action<string>? CommandSent;
        public event Action<double>? SessionTimeZeroed;

        public void Send(OpenTECCommand command)
        {
            var json = command.ToJson();
            Assert.True(WireCodec.ApplyCommand(simulator, json, out _));
            CommandSent?.Invoke(json);
        }

        public void AdvanceSimulator(double seconds)
        {
            simulator.Tick(seconds);
            Assert.Equal(ParseOutcome.Updated, _parser.Parse(WireCodec.BuildTelemetry(simulator)));
            Latest = _parser.Readings.Snapshot();
            TelemetryReceived?.Invoke(Latest);
        }

        public void Connect() { }
        public void ConnectUsb(string portName) { }
        public void ConnectWiFi(string ipAddress) { }
        public void Disconnect() { }
        public void ZeroSessionTime() => SessionTimeZeroed?.Invoke(0);
        public Task<string?> DiscoverUsbPortAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }
}
