using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.PowerMapping;
using OpenTECHub.Services.PowerTesting;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class PowerMapStoreTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly PowerMapStore _store;

    public PowerMapStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "PowerMapStoreTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _store = new PowerMapStore(_tempRoot);
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
                // Best-effort cleanup
            }
        }
    }

    [Fact]
    public void AppPaths_PowerMapsDirectory_Points_To_Mapas_Potencia()
    {
        Assert.EndsWith("Mapas-Potencia", AppPaths.PowerMapsDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PowerMapFileContracts_Validates_Map_Names_Correctly()
    {
        Assert.True(PowerMapFileContracts.ValidateMapName("Mapa Rushton 2026", out var err1));
        Assert.Null(err1);

        Assert.True(PowerMapFileContracts.ValidateMapName("Escala Piloto", out _));

        // Invalid: whitespace / empty
        Assert.False(PowerMapFileContracts.ValidateMapName("", out var errEmpty));
        Assert.NotNull(errEmpty);

        Assert.False(PowerMapFileContracts.ValidateMapName("   ", out _));

        // Too short (< 3)
        Assert.False(PowerMapFileContracts.ValidateMapName("AB", out var errShort));
        Assert.Contains("3 e 80", errShort);

        // Invalid characters
        Assert.False(PowerMapFileContracts.ValidateMapName("Mapa:Rushton?", out var errChar));
        Assert.Contains("inválidos", errChar);

        // Ends with dot or space
        Assert.False(PowerMapFileContracts.ValidateMapName("Mapa Rushton.", out var errDot));
        Assert.Contains("ponto", errDot);

        // Reserved names
        Assert.False(PowerMapFileContracts.ValidateMapName("CON", out var errRes));
        Assert.Contains("reservada", errRes);
        Assert.False(PowerMapFileContracts.ValidateMapName("nul", out _));
    }

    [Fact]
    public void PowerMapDocument_RoundTrip_Serialization_Preserves_All_Fields()
    {
        var mapId = Guid.NewGuid();
        var testId1 = Guid.NewGuid();
        var testId2 = Guid.NewGuid();

        var doc = new PowerMapDocument
        {
            MapId = mapId,
            Name = "Mapa Sintese 2D",
            FolderName = "Mapa_Sintese_2D",
            CreatedAtUtc = new DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.Zero),
            UpdatedAtUtc = new DateTimeOffset(2026, 9, 4, 11, 0, 0, TimeSpan.Zero),
            SourceTestIds = [testId1, testId2],
            SourceTestNames = ["Ensaio 1", "Ensaio 2"],
            Fluid = new FluidProperties { DensityKgM3 = 1050.0, ViscosityPaS = 0.0012 },
            Geometry = new PowerGeometry
            {
                Impellers = [new Impeller { Type = ImpellerType.RushtonFlatBlade, DiameterM = 0.060, BladeCount = 6 }],
                VesselDiameterM = 0.190,
                LiquidVolumeM3 = 0.010,
            },
            SurfaceData = new PowerMapSurfaceData
            {
                ResolutionN = 2,
                ResolutionQg = 2,
                MinRpm = 100,
                MaxRpm = 500,
                MinFlowLpm = 0,
                MaxFlowLpm = 10,
                RpmGrid = [100.0, 500.0],
                FlowGrid = [0.0, 10.0],
                PNetSurface = [1.2, 12.5, null, 8.4],
                PVolumetricSurface = [120.0, 1250.0, null, 840.0],
                PowerRatioSurface = [1.0, 0.72, null, 0.65],
                AnchorPoints = [
                    new PowerMapAnchorPoint
                    {
                        RunId = Guid.NewGuid(),
                        SourceTestId = testId1,
                        SourceTestName = "Ensaio 1",
                        AgitationRpm = 300,
                        GasFlowLpm = 5,
                        GasSuperficialVelocityMs = 0.00294,
                        NetPowerW = 4.5,
                        VolumetricPowerWm3 = 450,
                        PowerRatio = 0.70,
                        GasFlowNumber = 0.035,
                        FroudeNumber = 0.15,
                        ReynoldsNumber = 18000,
                        IsFlooded = false,
                    }
                ],
            },
            FloodingBoundary = new PowerMapFloodingBoundary
            {
                ImpellerType = ImpellerType.RushtonFlatBlade,
                ImpellerDiameterM = 0.060,
                VesselDiameterM = 0.190,
                ExperimentalPoints = [new FloodingPoint { AgitationRpm = 300, GasFlowLpm = 8.5, GasFlowNumber = 0.06, FroudeNumber = 0.15 }],
                NienowTheoreticalPoints = [new FloodingPoint { AgitationRpm = 300, GasFlowLpm = 9.1, GasFlowNumber = 0.064, FroudeNumber = 0.15 }],
            },
            LinkedKlaMapId = Guid.NewGuid(),
            LinkedKlaMapName = "Mapa kLa Oxigenacao",
            KlaPairs = [
                new KlaPowerPair
                {
                    PairId = Guid.NewGuid(),
                    SourcePowerTestId = testId1,
                    AgitationRpm = 300,
                    GasFlowLpm = 5.0,
                    SuperficialVelocityMs = 0.00294,
                    NetPowerW = 4.5,
                    VolumetricPowerWm3 = 450.0,
                    KlaPerHour = 45.2,
                    ConfidenceInterval95 = 2.1,
                    PredictedKlaPerHour = 44.8,
                    Residual = 0.4,
                    RelativeErrorFraction = 0.0088,
                }
            ],
            KlaCorrelation = new KlaCorrelationResult
            {
                FittedAtUtc = DateTimeOffset.UtcNow,
                K = 0.026,
                Alpha = 0.55,
                Beta = 0.38,
                StdErrorK = 0.003,
                StdErrorAlpha = 0.04,
                StdErrorBeta = 0.05,
                R2 = 0.982,
                AdjustedR2 = 0.978,
                RootMeanSquareError = 1.25,
                CovarianceMatrix = [[0.0001, 0.00002, 0.00001], [0.00002, 0.0002, 0.00003], [0.00001, 0.00003, 0.0003]],
                ValidPointsCount = 9,
                DegreesOfFreedom = 6,
                ModelFormula = "kLa = 0.0260 * (P/V)^0.5500 * (vs)^0.3800",
                ExcludedPointsNotes = [],
            },
            Notes = "Observações de teste para o mapa de síntese.",
        };

        var json = PowerMapFileContracts.SerializeMapDocument(doc);
        var deserialized = PowerMapFileContracts.DeserializeMapDocument(json);

        Assert.NotNull(deserialized);
        Assert.Equal(doc.MapId, deserialized.MapId);
        Assert.Equal("Mapa Sintese 2D", deserialized.Name);
        Assert.Equal("Mapa_Sintese_2D", deserialized.FolderName);
        Assert.Equal(2, deserialized.SourceTestIds.Count);
        Assert.Equal(1050.0, deserialized.Fluid.DensityKgM3);
        Assert.Equal(0.190, deserialized.Geometry.VesselDiameterM);

        Assert.NotNull(deserialized.SurfaceData);
        Assert.Equal(2, deserialized.SurfaceData.ResolutionN);
        Assert.Equal(4, deserialized.SurfaceData.PNetSurface.Length);
        Assert.Equal(1.2, deserialized.SurfaceData.PNetSurface[0]);
        Assert.Null(deserialized.SurfaceData.PNetSurface[2]);
        Assert.Single(deserialized.SurfaceData.AnchorPoints);
        Assert.Equal(4.5, deserialized.SurfaceData.AnchorPoints[0].NetPowerW);

        Assert.NotNull(deserialized.FloodingBoundary);
        Assert.Single(deserialized.FloodingBoundary.ExperimentalPoints);
        Assert.Equal(8.5, deserialized.FloodingBoundary.ExperimentalPoints[0].GasFlowLpm);

        Assert.Single(deserialized.KlaPairs);
        Assert.Equal(450.0, deserialized.KlaPairs[0].VolumetricPowerWm3);

        Assert.NotNull(deserialized.KlaCorrelation);
        Assert.Equal(0.026, deserialized.KlaCorrelation.K);
        Assert.Equal(0.55, deserialized.KlaCorrelation.Alpha);
        Assert.Equal(0.982, deserialized.KlaCorrelation.R2);
        Assert.Equal("Observações de teste para o mapa de síntese.", deserialized.Notes);
    }

    [Fact]
    public void ImpellerComparisonDocument_RoundTrip_Serialization_Preserves_All_Fields()
    {
        var testId = Guid.NewGuid();
        var comp = new ImpellerComparisonDocument
        {
            ComparisonId = Guid.NewGuid(),
            Name = "Rushton vs Pitched 2026",
            FolderName = "Comp_Rushton_vs_Pitched",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            SelectedTestIds = [testId],
            Items = [
                new ImpellerComparisonItem
                {
                    SourceTestId = testId,
                    TestName = "Ensaio Rushton",
                    TestDateUtc = DateTimeOffset.UtcNow,
                    ImpellerType = ImpellerType.RushtonFlatBlade,
                    ImpellerDiameterM = 0.060,
                    VesselDiameterM = 0.190,
                    LiquidVolumeM3 = 0.010,
                    LiquidDensityKgM3 = 1000,
                    TurbulentNpMean = 5.2,
                    TurbulentNpCi95 = 0.12,
                    ExperimentalFloodingFlG = 0.045,
                    NienowFloodingFlG = 0.048,
                    AverageSpecificPowerWm3 = 500,
                    ParasiticPowerZeroSpeedW = 0.15,
                    GasDispersionEfficiencyRatio = 0.85,
                    PowerRatioCurve = [new PowerRatioPoint(0.02, 0.80, 300, 3)],
                    PowerNumberReynoldsCurve = [new NpRePoint(20000, 5.2, 0.12)],
                }
            ],
            IsCompatibleGeometry = true,
            CompatibilityNotes = ["Mesmo vaso e mesmo volume de líquido"],
        };

        var json = PowerMapFileContracts.SerializeComparisonDocument(comp);
        var deserialized = PowerMapFileContracts.DeserializeComparisonDocument(json);

        Assert.NotNull(deserialized);
        Assert.Equal(comp.ComparisonId, deserialized.ComparisonId);
        Assert.Equal("Rushton vs Pitched 2026", deserialized.Name);
        Assert.Single(deserialized.Items);
        var item = deserialized.Items[0];
        Assert.Equal(ImpellerType.RushtonFlatBlade, item.ImpellerType);
        Assert.Equal(5.2, item.TurbulentNpMean);
        Assert.Equal(0.060 / 0.190, item.DiameterRatioDt, precision: 4);
        Assert.Single(item.PowerRatioCurve);
        Assert.Single(item.PowerNumberReynoldsCurve);
    }

    [Fact]
    public void PowerMapStore_Create_Load_List_Save_Delete_Map_Lifecycle()
    {
        var testId1 = Guid.NewGuid();
        var testId2 = Guid.NewGuid();

        // 1. Validate non-existent
        Assert.False(_store.MapExists("Mapa Piloto"));
        Assert.Empty(_store.ListMaps());

        // 2. Create
        var doc = _store.CreateMap("Mapa Piloto", [testId1, testId2], ["T1", "T2"], notes: "Ensaio piloto inicial");
        Assert.NotNull(doc);
        Assert.Equal("Mapa Piloto", doc.Name);
        Assert.True(_store.MapExists("Mapa Piloto"));

        var manifestPath = Path.Combine(_store.RootDirectory, doc.FolderName, PowerMapFileContracts.MapManifestFileName);
        Assert.True(File.Exists(manifestPath));

        // 3. List
        var summaries = _store.ListMaps();
        Assert.Single(summaries);
        Assert.Equal("Mapa Piloto", summaries[0].Name);
        Assert.Equal(2, summaries[0].SourceTestsCount);
        Assert.False(summaries[0].HasSurface);

        // 4. Load by Name, Folder, ID
        var loadedByName = _store.LoadMap("Mapa Piloto");
        Assert.NotNull(loadedByName);
        Assert.Equal(doc.MapId, loadedByName.MapId);

        var loadedByFolder = _store.LoadMap(doc.FolderName);
        Assert.NotNull(loadedByFolder);

        var loadedById = _store.LoadMap(doc.MapId.ToString());
        Assert.NotNull(loadedById);

        // 5. Update with surface and save
        var surface = new PowerMapSurfaceData
        {
            ResolutionN = 2,
            ResolutionQg = 2,
            MinRpm = 100,
            MaxRpm = 300,
            MinFlowLpm = 0,
            MaxFlowLpm = 5,
            RpmGrid = [100, 300],
            FlowGrid = [0, 5],
            PNetSurface = [1.0, 2.0, 3.0, 4.0],
            PVolumetricSurface = [100, 200, 300, 400],
            PowerRatioSurface = [1.0, 0.9, 0.8, 0.7],
        };

        var klaPairs = new List<KlaPowerPair>
        {
            new()
            {
                AgitationRpm = 300,
                GasFlowLpm = 5,
                SuperficialVelocityMs = 0.003,
                NetPowerW = 4.0,
                VolumetricPowerWm3 = 400,
                KlaPerHour = 35.0,
                ConfidenceInterval95 = 1.5,
            }
        };

        var klaCorr = new KlaCorrelationResult
        {
            K = 0.02,
            Alpha = 0.5,
            Beta = 0.3,
            R2 = 0.95,
        };

        var updatedDoc = loadedByName with
        {
            SurfaceData = surface,
            KlaPairs = klaPairs,
            KlaCorrelation = klaCorr,
        };

        _store.SaveMap(updatedDoc);

        // Verify CSV sidecars were created
        var surfaceCsv = Path.Combine(_store.RootDirectory, doc.FolderName, PowerMapFileContracts.SurfaceGridCsvFileName);
        var klaCsv = Path.Combine(_store.RootDirectory, doc.FolderName, PowerMapFileContracts.KlaCorrelationSummaryFileName);
        Assert.True(File.Exists(surfaceCsv));
        Assert.True(File.Exists(klaCsv));

        // Verify summary reflects updated content
        summaries = _store.ListMaps();
        Assert.Single(summaries);
        Assert.True(summaries[0].HasSurface);
        Assert.True(summaries[0].HasKlaCorrelation);
        Assert.Equal(0.95, summaries[0].R2);

        // 6. Delete
        var deleted = _store.DeleteMap("Mapa Piloto");
        Assert.True(deleted);
        Assert.False(_store.MapExists("Mapa Piloto"));
        Assert.Empty(_store.ListMaps());
        Assert.False(Directory.Exists(Path.Combine(_store.RootDirectory, doc.FolderName)));
    }

    [Fact]
    public void PowerMapStore_Create_Load_List_Save_Delete_Comparison_Lifecycle()
    {
        var testId = Guid.NewGuid();
        var items = new List<ImpellerComparisonItem>
        {
            new()
            {
                SourceTestId = testId,
                TestName = "Rushton 6-blades",
                ImpellerType = ImpellerType.RushtonFlatBlade,
                ImpellerDiameterM = 0.060,
                VesselDiameterM = 0.190,
                TurbulentNpMean = 5.0,
                TurbulentNpCi95 = 0.1,
            }
        };

        // 1. Create
        var comp = _store.CreateComparison("Comparacao 1", [testId], items, "Nota comparativa");
        Assert.NotNull(comp);
        Assert.Equal("Comparacao 1", comp.Name);

        // 2. List
        var list = _store.ListComparisons();
        Assert.Single(list);
        Assert.Equal("Comparacao 1", list[0].Name);

        // 3. Load
        var loaded = _store.LoadComparison("Comparacao 1");
        Assert.NotNull(loaded);
        Assert.Equal(comp.ComparisonId, loaded.ComparisonId);

        // 4. Save updated
        var updated = loaded with
        {
            CompatibilityNotes = ["Nota 1", "Nota 2"],
        };
        _store.SaveComparison(updated);

        var reloaded = _store.LoadComparison(comp.ComparisonId.ToString());
        Assert.NotNull(reloaded);
        Assert.Equal(2, reloaded.CompatibilityNotes.Count);

        // 5. Delete
        var deleted = _store.DeleteComparison("Comparacao 1");
        Assert.True(deleted);
        Assert.Empty(_store.ListComparisons());
    }

    [Fact]
    public async Task BackupService_Exports_And_Imports_Power_Tests_And_Maps()
    {
        var dataRoot = Path.Combine(_tempRoot, "backup_test_data");
        Directory.CreateDirectory(dataRoot);

        var powerTestsDir = Path.Combine(dataRoot, "Testes-Potencia");
        var powerMapsDir = Path.Combine(dataRoot, "Mapas-Potencia");
        Directory.CreateDirectory(powerTestsDir);
        Directory.CreateDirectory(powerMapsDir);

        // Create sample power test
        var sampleTestDir = Path.Combine(powerTestsDir, "Ensaio_Sample");
        Directory.CreateDirectory(sampleTestDir);
        await File.WriteAllTextAsync(Path.Combine(sampleTestDir, "ensaio.json"), "{\"name\":\"Ensaio Sample\"}");
        await File.WriteAllTextAsync(Path.Combine(sampleTestDir, "resumo-resultados.csv"), "N,P\n300,5.0\n");

        // Create sample power map
        var sampleMapDir = Path.Combine(powerMapsDir, "Mapa_Sample");
        Directory.CreateDirectory(sampleMapDir);
        await File.WriteAllTextAsync(Path.Combine(sampleMapDir, "mapa-potencia.json"), "{\"name\":\"Mapa Sample\"}");

        var settingsService = new MemorySettingsService(new AppSettings());
        var backupService = new BackupService(settingsService, NullLogger<BackupService>.Instance, dataRoot);
        var zipPath = Path.Combine(_tempRoot, "power_backup.tecbkp");

        // Export
        var exportRes = await backupService.ExportBackupAsync(zipPath);
        Assert.True(exportRes.Success);
        Assert.True(File.Exists(zipPath));

        // Verify zip contains power tests and power maps
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            Assert.Contains(archive.Entries, e => e.FullName.Replace('/', '\\').StartsWith("Testes-Potencia\\"));
            Assert.Contains(archive.Entries, e => e.FullName.Replace('/', '\\').StartsWith("Mapas-Potencia\\"));
        }

        // Restore into fresh directory
        var restoreRoot = Path.Combine(_tempRoot, "backup_restored_data");
        Directory.CreateDirectory(restoreRoot);
        var restoreBackupService = new BackupService(settingsService, NullLogger<BackupService>.Instance, restoreRoot);

        var importRes = await restoreBackupService.ImportBackupAsync(zipPath);
        Assert.True(importRes.Success);

        Assert.True(File.Exists(Path.Combine(restoreRoot, "Testes-Potencia", "Ensaio_Sample", "ensaio.json")));
        Assert.True(File.Exists(Path.Combine(restoreRoot, "Testes-Potencia", "Ensaio_Sample", "resumo-resultados.csv")));
        Assert.True(File.Exists(Path.Combine(restoreRoot, "Mapas-Potencia", "Mapa_Sample", "mapa-potencia.json")));
    }
}
