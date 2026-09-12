using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.PowerTesting;
using Xunit;

namespace OpenTECHub.Tests;

[Collection("AppPaths")]
public sealed class PowerTestStoreTests : IDisposable
{
    private readonly string _testRoot;
    private readonly IDisposable _overrideScope;
    private readonly PowerTestStore _store;

    public PowerTestStoreTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"opentechub-powerstore-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
        _overrideScope = AppPaths.OverrideForTests(_testRoot);
        _store = new PowerTestStore(AppPaths.PowerTestsDirectory);
    }

    public void Dispose()
    {
        _overrideScope.Dispose();
        if (Directory.Exists(_testRoot))
        {
            try
            {
                Directory.Delete(_testRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    [Theory]
    [InlineData("Rushton D6 água", true)]
    [InlineData("Ensaio-Potencia-01", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("Teste.", false)]
    [InlineData("CON", false)]
    [InlineData("Teste/Invalido", false)]
    public void ValidateTestName_Matches_Expected_Rules(string name, bool expectedValid)
    {
        var isValid = _store.ValidateTestName(name, out var error);
        Assert.Equal(expectedValid, isValid);
        if (!expectedValid)
        {
            Assert.NotNull(error);
        }
    }

    [Fact]
    public void PowerTestsDirectory_Ends_With_Testes_Potencia()
    {
        Assert.EndsWith("Testes-Potencia", AppPaths.PowerTestsDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateTest_Stores_Result_Under_Selected_Workspace()
    {
        var created = CreateSampleTest("No Workspace");
        var expectedRoot = Path.GetFullPath(Path.Combine(_testRoot, "Testes-Potencia"));
        var testFolder = Path.GetFullPath(Path.Combine(_store.RootDirectory, created.FolderName));

        Assert.Equal(expectedRoot, Path.GetFullPath(_store.RootDirectory));
        Assert.StartsWith(expectedRoot + Path.DirectorySeparatorChar, testFolder, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(testFolder));
    }

    [Fact]
    public void CreateTest_Initializes_Expected_File_Structure()
    {
        var doc = CreateSampleTest("Ensaio Estrutura");

        var folderPath = Path.Combine(_store.RootDirectory, doc.FolderName);
        Assert.True(File.Exists(Path.Combine(folderPath, PowerTestFileContracts.TestManifestFileName)));
        Assert.True(File.Exists(Path.Combine(folderPath, PowerTestFileContracts.ConditionTableFileName)));
        Assert.True(File.Exists(Path.Combine(folderPath, PowerTestFileContracts.GlobalSeriesFileName)));
        Assert.True(File.Exists(Path.Combine(folderPath, PowerTestFileContracts.ResultsSummaryFileName)));
        Assert.True(Directory.Exists(Path.Combine(folderPath, PowerTestFileContracts.RunsDirectoryName)));
        Assert.Empty(Directory.GetFiles(folderPath, "*.tmp-*", SearchOption.AllDirectories));

        // Brand new tests are relative until a tare/calibration lands (§9, §20.5).
        Assert.True(doc.RelativeMode);
    }

    [Fact]
    public void CreateThenLoad_Round_Trips_Geometry_Fluid_And_Conditions()
    {
        var created = CreateSampleTest("Round Trip");
        created.HubFirmwareVersion = "9.1.0-test";
        created.HubProtocolVersion = 9;
        _store.SaveTestManifest(created);

        var loaded = _store.LoadTest(created.FolderName);

        Assert.NotNull(loaded);
        Assert.Equal(created.TestId, loaded!.TestId);
        Assert.Equal("Round Trip", loaded.Name);
        Assert.Equal("9.1.0-test", loaded.HubFirmwareVersion);
        Assert.Equal(9, loaded.HubProtocolVersion);

        // Mixed impeller set survives (§4.3, §8).
        Assert.Equal(2, loaded.Geometry.Impellers.Count);
        Assert.Equal(ImpellerType.RushtonFlatBlade, loaded.Geometry.Impellers[0].Type);
        Assert.Equal(0.06, loaded.Geometry.Impellers[0].DiameterM, 6);
        Assert.Equal(ImpellerType.ElephantEar, loaded.Geometry.Impellers[1].Type);
        Assert.Equal(0.08, loaded.Geometry.Impellers[1].DiameterM, 6);

        Assert.Equal(998.0, loaded.Fluid.DensityKgM3, 6);
        Assert.Equal(0.001, loaded.Fluid.ViscosityPaS, 6);

        Assert.Equal(2, loaded.Conditions.Count);
        Assert.Equal(PowerGasMode.Ungassed, loaded.Conditions[0].GasMode);
        Assert.Equal(300, loaded.Conditions[0].AgitationRpm, 3);
        Assert.Equal(PowerGasMode.Both, loaded.Conditions[1].GasMode);
        Assert.Equal(5.0, loaded.Conditions[1].GasFlowLpm!.Value, 3);
    }

    [Fact]
    public void RenameTest_moves_the_complete_folder_and_updates_the_manifest()
    {
        var created = CreateSampleTest("Nome Antigo");
        var oldFolder = Path.Combine(_store.RootDirectory, created.FolderName);
        var marker = Path.Combine(oldFolder, "arquivo-do-operador.txt");
        File.WriteAllText(marker, "preservar");

        var renamed = _store.RenameTest(created.FolderName, "Nome Novo");

        Assert.False(Directory.Exists(oldFolder));
        var newFolder = Path.Combine(_store.RootDirectory, "Nome Novo");
        Assert.True(Directory.Exists(newFolder));
        Assert.Equal("preservar", File.ReadAllText(Path.Combine(newFolder, "arquivo-do-operador.txt")));
        Assert.Equal("Nome Novo", renamed.Name);
        Assert.Equal("Nome Novo", renamed.FolderName);
        Assert.Equal("Nome Novo", Assert.Single(_store.ListTests()).Name);
    }

    [Fact]
    public void DeleteTest_moves_the_folder_to_internal_trash_and_hides_it_from_the_list()
    {
        var created = CreateSampleTest("Descartar");
        var source = Path.Combine(_store.RootDirectory, created.FolderName);

        Assert.True(_store.DeleteTest(created.FolderName));

        Assert.False(Directory.Exists(source));
        Assert.Empty(_store.ListTests());
        var trash = Path.Combine(_store.RootDirectory, PowerTestStore.TrashDirectoryName);
        var recoveredFolder = Assert.Single(Directory.GetDirectories(trash));
        Assert.True(File.Exists(Path.Combine(recoveredFolder, PowerTestFileContracts.TestManifestFileName)));
    }

    [Fact]
    public void SaveTare_And_Calibration_Round_Trip_And_Attach_On_Load()
    {
        var created = CreateSampleTest("Tara e Calibração");

        var tare = new TareCurve
        {
            SchemaVersion = 2,
            ImpellerSetHash = PowerTestFileContracts.ComputeImpellerSetHash(created.Geometry),
            CalibrationHash = PowerTestFileContracts.ComputeTorqueCalibrationHash(null, created.MotorRatedTorqueNm),
            Points =
            {
                new TarePoint(300, 0.61, 0.62)
                {
                    SampleCount = 120,
                    MeanRpmMeasured = 299.8,
                    RpmStandardDeviation = 0.4,
                    RpmCi95 = 0.08,
                    MeanTorquePercent = 1.53,
                    TorqueCi95Percent = 0.04,
                    PVoidCi95W = 0.02,
                    ElapsedSeconds = 52.5,
                    Attempts = 1,
                },
                new TarePoint(600, 1.51, 0.41),
            },
            AcquisitionSettings = new PowerTestSettings { MinSamples = 120 },
            Samples =
            {
                new TareSample(DateTimeOffset.UnixEpoch, 1.25, 300, 299.8, 1.53, TareCapturePhase.Accumulating, true, 1),
            },
        };
        _store.SaveTare(created.FolderName, tare);

        var calibration = new TorqueCalibration { Scale = 1.02, Offset = 0, ReferenceNm = 0.5, MotorRatedTorqueNm = 1.27 };
        _store.SaveCalibration(created.FolderName, calibration);

        var loadedTare = _store.LoadTare(created.FolderName);
        Assert.NotNull(loadedTare);
        Assert.Equal(2, loadedTare!.Points.Count);
        Assert.Equal(0.62, loadedTare.Points[0].SigmaTauPercent, 4);
        Assert.Equal(120, loadedTare.Points[0].SampleCount);
        Assert.Equal(0.04, loadedTare.Points[0].TorqueCi95Percent, 4);
        // The readings are not embedded any more (D-048): tara.json names the sidecar in Taras-Brutas/.
        Assert.Empty(loadedTare.Samples);
        Assert.NotEmpty(loadedTare.RawSamplesFileName);
        var sidecar = _store.LoadTareRawData(created.FolderName, loadedTare.RawSamplesFileName);
        Assert.Single(sidecar);
        Assert.True(sidecar[0].Counted);
        Assert.Equal(300, sidecar[0].TargetRpm);
        var tareJson = File.ReadAllText(Path.Combine(_store.RootDirectory, created.FolderName, PowerTestFileContracts.TareFileName));
        Assert.DoesNotContain("\"targetRpm\"", tareJson, StringComparison.Ordinal);
        Assert.Equal(120, loadedTare.AcquisitionSettings!.MinSamples);
        Assert.Equal(tare.ImpellerSetHash, loadedTare.ImpellerSetHash);
        Assert.Equal(tare.CalibrationHash, loadedTare.CalibrationHash);

        var loadedDoc = _store.LoadTest(created.FolderName);
        Assert.NotNull(loadedDoc!.Tare);
        Assert.NotNull(loadedDoc.Calibration);
        Assert.Equal(1.02, loadedDoc.Calibration!.Scale, 4);
    }

    [Fact]
    public void Legacy_tare_json_without_statistical_fields_remains_readable()
    {
        const string legacyJson = """
            {
              "points": [
                { "rpm": 300, "pVoidW": 0.61, "sigmaTauPercent": 0.62 }
              ],
              "impellerSetHash": "legacy",
              "measuredUtc": "2026-01-01T00:00:00+00:00"
            }
            """;

        var tare = PowerTestFileContracts.DeserializeTare(legacyJson);

        Assert.NotNull(tare);
        Assert.Equal(1, tare!.SchemaVersion);
        Assert.Single(tare.Points);
        Assert.Equal(0.61, tare.Points[0].PVoidW, 4);
        Assert.Equal(0, tare.Points[0].SampleCount);
        Assert.Empty(tare.Samples);
        Assert.Null(tare.AcquisitionSettings);
    }

    [Fact]
    public void RunFolder_And_RawData_Round_Trip()
    {
        var created = CreateSampleTest("Corrida");
        var run = new PowerRun
        {
            TestId = created.TestId,
            ConditionId = created.Conditions[0].ConditionId,
            ReplicateNumber = 1,
            AgitationRpm = 300,
            GasFlowLpm = null, // ungassed → "Seco" folder
            GasMode = PowerGasMode.Ungassed,
        };

        var runFolder = _store.InitializeRunFolder(created.FolderName, run);
        Assert.StartsWith("N0300_Seco_Rep01", runFolder);

        var t0 = DateTimeOffset.UtcNow;
        // (timestamp, relSec, phase, rpm, torque%, torqueNm, shaftW, flowLpm, counted, attempt)
        _store.AppendRunRawDataPoint(created.FolderName, runFolder,
            new PowerDataPoint(t0, 0.0, PowerRunPhase.SettlingTorque, 299.6, 1.57, 0.0199, 0.624, null, false));
        _store.AppendRunRawDataPoint(created.FolderName, runFolder,
            new PowerDataPoint(t0.AddSeconds(0.5), 0.5, PowerRunPhase.AccumulatingToTarget, 300.1, 1.60, 0.0203, 0.638, null, true, 2));

        var points = _store.LoadRunRawData(created.FolderName, runFolder);
        Assert.Equal(2, points.Count);
        Assert.Equal(PowerRunPhase.AccumulatingToTarget, points[1].Phase);
        Assert.True(points[1].Counted);
        Assert.False(points[0].Counted);
        Assert.Equal(300.1, points[1].RpmMeasured, 1);
        Assert.Null(points[0].FlowLpm);
        Assert.Equal(2, points[1].Attempt);
    }

    [Fact]
    public void SaveRunResult_Writes_Integrity_Hash_And_Aggregated_Summary()
    {
        var created = CreateSampleTest("Resultado");
        var condition = created.Conditions[0];
        var run = new PowerRun
        {
            TestId = created.TestId,
            ConditionId = condition.ConditionId,
            ReplicateNumber = 1,
            AgitationRpm = condition.AgitationRpm,
            GasMode = PowerGasMode.Ungassed,
            CurrentPhase = PowerRunPhase.Accepted,
            StopReason = PowerStopReason.Target,
            SampleCount = 60,
            MeanRpmMeasured = 300.2,
            MeanTorquePercent = 1.6,
            MeanTorqueNm = 0.02032,
            MeanShaftPowerW = 0.6388,
            NetPowerW = 0.42,
            TorqueCi95Percent = 0.03,
            Ci95PowerW = 0.012,
            StartedUtc = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero),
            CompletedUtc = new DateTimeOffset(2026, 9, 3, 12, 1, 0, TimeSpan.Zero),
            Analysis = new PowerPointResult
            {
                AssemblyPowerNumber = 4.95,
                AssemblyReynoldsNumber = 179_640,
                AssemblyPowerNumberCi95 = 0.14,
            },
        };
        var runFolder = _store.InitializeRunFolder(created.FolderName, run);
        _store.AppendRunRawDataPoint(created.FolderName, runFolder,
            new PowerDataPoint(run.StartedUtc, 0, PowerRunPhase.AccumulatingToTarget,
                300.2, 1.6, 0.02032, 0.6388, null, true));

        _store.SaveRunResult(created.FolderName, run);
        created.Runs.Add(new PowerRunSummary
        {
            RunId = run.RunId,
            ConditionId = run.ConditionId,
            ReplicateNumber = run.ReplicateNumber,
            FolderName = run.FolderName,
            AgitationRpm = run.AgitationRpm,
            GasMode = run.GasMode,
            Phase = PowerRunPhase.Accepted,
            StopReason = run.StopReason,
            SampleCount = run.SampleCount,
            NetPowerW = run.NetPowerW,
            Analysis = run.Analysis,
        });
        _store.UpdateResultsSummary(created.FolderName, created);

        var resultPath = Path.Combine(_store.RootDirectory, created.FolderName,
            PowerTestFileContracts.RunsDirectoryName, runFolder, PowerTestFileContracts.RunResultFileName);
        var result = File.ReadAllText(resultPath);
        var summary = File.ReadAllText(Path.Combine(_store.RootDirectory, created.FolderName,
            PowerTestFileContracts.ResultsSummaryFileName));

        Assert.False(string.IsNullOrWhiteSpace(run.RawDataSha256));
        Assert.Equal(64, run.RawDataSha256!.Length);
        Assert.Contains("2026-09-03T12:00:00.0000000+00:00", result);
        Assert.Contains("2026-09-03T12:01:00.0000000+00:00", result);
        Assert.Contains("0.4200000", summary);
        Assert.Contains("4.9500000000000002", summary);
        Assert.Contains("179640", summary);
    }

    [Fact]
    public void Csv_Uses_Invariant_Decimals_Under_PtBr_Culture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("pt-BR");
            var created = CreateSampleTest("Cultura");
            var run = new PowerRun
            {
                TestId = created.TestId,
                ConditionId = created.Conditions[0].ConditionId,
                AgitationRpm = 300,
                GasMode = PowerGasMode.Ungassed,
            };
            var runFolder = _store.InitializeRunFolder(created.FolderName, run);
            _store.AppendRunRawDataPoint(created.FolderName, runFolder,
                new PowerDataPoint(DateTimeOffset.UtcNow, 1.25, PowerRunPhase.AccumulatingToTarget,
                    300.5, 1.6, 0.02032, 0.6388, null, true));

            var csv = File.ReadAllText(_store.GetRunRawDataPath(created.FolderName, runFolder));
            var loaded = _store.LoadRunRawData(created.FolderName, runFolder);

            Assert.Contains(",1.250,", csv);
            Assert.Contains(",300.5,1.600,", csv);
            Assert.Single(loaded);
            Assert.Equal(1.25, loaded[0].RelativeSeconds, 3);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void ListTests_Returns_Created_Test()
    {
        CreateSampleTest("Listável");
        var summaries = _store.ListTests();
        Assert.Contains(summaries, s => s.Name == "Listável" && s.ConditionCount == 2);
    }

    [Fact]
    public void LoadTest_Recovers_Running_As_Interrupted()
    {
        var created = CreateSampleTest("Interrompido");
        created.Status = PowerTestStatus.Running;
        _store.SaveTestManifest(created);

        var loaded = _store.LoadTest(created.FolderName);
        Assert.NotNull(loaded);
        Assert.Equal(PowerTestStatus.Interrupted, loaded!.Status);
        Assert.NotNull(loaded.InterruptionReason);
    }

    [Fact]
    public void LoadTest_Returns_Null_For_Missing_Folder()
    {
        Assert.Null(_store.LoadTest("nao-existe"));
    }

    [Fact]
    public void Gassed_Conditions_And_Vent_Settings_Round_Trip()
    {
        var settings = new PowerTestSettings
        {
            PrestageFlowToleranceLpm = 0.15,
            PrestageAgitationRpm = 20.0,
            MaxPrestageSeconds = 90.0,
        };
        var conditions = new List<PowerCondition>
        {
            new()
            {
                AgitationRpm = 450,
                GasMode = PowerGasMode.Gassed,
                GasFlowLpm = 3.5,
                GasFlowVvm = 0.7,
                FlowUnit = FlowInputUnit.Vvm,
                RequestedReplicates = 2,
            },
            new()
            {
                AgitationRpm = 600,
                GasMode = PowerGasMode.Both,
                GasFlowLpm = 5.0,
                GasFlowVvm = 1.0,
                FlowUnit = FlowInputUnit.Lpm,
                RequestedReplicates = 3,
            }
        };

        var created = _store.CreateTest("Gás e Alívio", new FluidProperties(), new PowerGeometry { VesselDiameterM = 0.190 }, settings, conditions);
        var loaded = _store.LoadTest(created.FolderName);

        Assert.NotNull(loaded);
        Assert.Equal(0.15, loaded!.Settings.PrestageFlowToleranceLpm, 3);
        Assert.Equal(20.0, loaded.Settings.PrestageAgitationRpm, 1);
        Assert.Equal(90.0, loaded.Settings.MaxPrestageSeconds, 1);
        Assert.Equal(0.190, loaded.Geometry.VesselDiameterM, 3);

        Assert.Equal(2, loaded.Conditions.Count);
        Assert.Equal(PowerGasMode.Gassed, loaded.Conditions[0].GasMode);
        Assert.Equal(3.5, loaded.Conditions[0].GasFlowLpm!.Value, 2);
        Assert.Equal(0.7, loaded.Conditions[0].GasFlowVvm!.Value, 2);
        Assert.Equal(FlowInputUnit.Vvm, loaded.Conditions[0].FlowUnit);

        Assert.Equal(PowerGasMode.Both, loaded.Conditions[1].GasMode);
        Assert.Equal(5.0, loaded.Conditions[1].GasFlowLpm!.Value, 2);
    }

    [Fact]
    public void FloodingAnalysisResult_Saves_And_Round_Trips()
    {
        var created = CreateSampleTest("Flooding Test");
        var flooding = new FloodingAnalysisResult
        {
            ExperimentalFlG = 0.0325,
            ExperimentalRpm = 450.0,
            ExperimentalFlowLpm = 4.2,
            TheoreticalFlGNienow = 0.0305,
            RelativeDeviationPercent = 6.56,
            ReferenceStageIndex = 0,
            ReferenceImpellerType = ImpellerType.RushtonFlatBlade,
            Method = FloodingDetectionMethod.Automatic,
            Notes = "Transição observada no mínimo da razão PG/P0",
        };

        _store.SaveFlooding(created.FolderName, flooding);

        var loaded = _store.LoadTest(created.FolderName);
        Assert.NotNull(loaded);
        Assert.NotNull(loaded!.Flooding);
        Assert.Equal(0.0325, loaded.Flooding!.ExperimentalFlG, 4);
        Assert.Equal(450.0, loaded.Flooding.ExperimentalRpm, 1);
        Assert.Equal(4.2, loaded.Flooding.ExperimentalFlowLpm, 1);
        Assert.Equal(0.0305, loaded.Flooding.TheoreticalFlGNienow, 4);
        Assert.Equal(6.56, loaded.Flooding.RelativeDeviationPercent, 2);
        Assert.Equal(0, loaded.Flooding.ReferenceStageIndex);
        Assert.Equal(ImpellerType.RushtonFlatBlade, loaded.Flooding.ReferenceImpellerType);
        Assert.Equal(FloodingDetectionMethod.Automatic, loaded.Flooding.Method);
        Assert.Equal("Transição observada no mínimo da razão PG/P0", loaded.Flooding.Notes);
    }

    [Fact]
    public void Gassed_PowerRun_Result_And_GlobalSeries_Round_Trip()
    {
        var created = CreateSampleTest("Gassed Run Results");
        var cond = created.Conditions[1]; // Both
        var run = new PowerRun
        {
            TestId = created.TestId,
            ConditionId = cond.ConditionId,
            ReplicateNumber = 1,
            AgitationRpm = cond.AgitationRpm,
            GasFlowLpm = 5.0,
            GasFlowVvm = 1.0,
            GasMode = PowerGasMode.Gassed,
            UsedVentStabilization = true,
            CurrentPhase = PowerRunPhase.Accepted,
            StopReason = PowerStopReason.Target,
            SampleCount = 65,
            MeanRpmMeasured = 300.1,
            MeanTorquePercent = 1.25,
            MeanTorqueNm = 0.01587,
            MeanShaftPowerW = 0.4988,
            NetPowerW = 0.35,
            GassedPowerW = 0.35,
            ReferenceP0W = 0.50,
            ReferenceP0Ci95W = 0.015,
            P0Provenance = P0Provenance.PlateauFit,
            PowerRatio = 0.70,
            PowerRatioCi95 = 0.035,
            GasFlowNumber = 0.0245,
            FroudeNumber = 0.115,
            TorqueCi95Percent = 0.02,
            Ci95PowerW = 0.008,
            StartedUtc = new DateTimeOffset(2026, 9, 4, 1, 0, 0, TimeSpan.Zero),
            CompletedUtc = new DateTimeOffset(2026, 9, 4, 1, 1, 30, TimeSpan.Zero),
            Analysis = new PowerPointResult
            {
                AssemblyPowerNumber = 3.5,
                AssemblyReynoldsNumber = 179_600,
                AssemblyPowerNumberCi95 = 0.12,
                GasFlowLpm = 5.0,
                GasFlowVvm = 1.0,
                GasFlowNumber = 0.0245,
                FroudeNumber = 0.115,
                GassedPowerW = 0.35,
                ReferenceP0W = 0.50,
                ReferenceP0Ci95W = 0.015,
                P0Provenance = P0Provenance.PlateauFit,
                PowerRatio = 0.70,
                PowerRatioCi95 = 0.035,
            },
        };

        var runFolder = _store.InitializeRunFolder(created.FolderName, run);
        Assert.Contains("N0300_Q05p00_Rep01", runFolder);

        _store.AppendRunRawDataPoint(created.FolderName, runFolder,
            new PowerDataPoint(run.StartedUtc, 0.0, PowerRunPhase.AccumulatingToTarget,
                300.1, 1.25, 0.01587, 0.4988, 5.0, true));

        _store.SaveRunResult(created.FolderName, run);

        var resultCsv = File.ReadAllText(Path.Combine(_store.RootDirectory, created.FolderName,
            PowerTestFileContracts.RunsDirectoryName, runFolder, PowerTestFileContracts.RunResultFileName));
        Assert.Contains("PlateauFit", resultCsv);
        Assert.Contains("PlateauFit", resultCsv);
        Assert.Contains(",1.0000,", resultCsv);
        Assert.Contains("0.0245", resultCsv);
        Assert.Contains("0.115", resultCsv);

        // Global series event logging verification
        var now = DateTimeOffset.UtcNow;
        _store.AppendGlobalSeriesSample(created.FolderName, new PowerGlobalSeriesSample(
            now, 10.5, created.TestId, run.RunId, cond.ConditionId, 1, PowerRunPhase.PrestagingFlow,
            300.0, 1.2, 0.015, 0.47, 5.0, 24.5, 0.47, 0.01, 10, 1,
            PowerTestEventCodes.VentOpened, "Válvula de alívio aberta para assentamento de fluxo"));

        _store.AppendGlobalSeriesSample(created.FolderName, new PowerGlobalSeriesSample(
            now.AddSeconds(5), 15.5, created.TestId, run.RunId, cond.ConditionId, 1, PowerRunPhase.OpeningGas,
            300.0, 1.25, 0.0158, 0.49, 5.0, 24.5, 0.49, 0.01, 20, 1,
            PowerTestEventCodes.GasOpened, "Gás transferido ao vaso do reator"));

        _store.AppendGlobalSeriesSample(created.FolderName, new PowerGlobalSeriesSample(
            now.AddSeconds(60), 75.5, created.TestId, run.RunId, cond.ConditionId, 1, PowerRunPhase.Accepted,
            300.1, 1.25, 0.01587, 0.4988, 5.0, 24.5, 0.4988, 0.008, 65, 1,
            PowerTestEventCodes.FloodingDetected, "Flooding identificado a Fl_G = 0.0245"));

        var globalCsv = File.ReadAllText(Path.Combine(_store.RootDirectory, created.FolderName,
            PowerTestFileContracts.GlobalSeriesFileName));
        Assert.Contains("VentOpened", globalCsv);
        Assert.Contains("GasOpened", globalCsv);
        Assert.Contains("FloodingDetected", globalCsv);
    }

    [Fact]
    public void Two_Shaft_Tare_Profiles_Coexist_And_Attach_To_Different_Tests()
    {
        // The bench runs two bioreactors with different shafts. Both tares have to be on
        // hand at once: measuring the second must not cost the first.
        var singleHole = TareOf(300, 0.61);
        var doubleHole = TareOf(300, 0.94);

        _store.SaveTareProfile("eixo_furo_unico", singleHole);
        _store.SaveTareProfile("eixo_furo_duplo", doubleHole);

        var profiles = _store.ListTareProfiles();
        Assert.Equal(2, profiles.Count);
        Assert.Contains(profiles, p => p.Name == "eixo_furo_unico");
        Assert.Contains(profiles, p => p.Name == "eixo_furo_duplo");

        Assert.Equal(0.61, _store.LoadTareProfile("eixo_furo_unico")!.Points[0].PVoidW, 4);
        Assert.Equal(0.94, _store.LoadTareProfile("eixo_furo_duplo")!.Points[0].PVoidW, 4);

        // Each assay attaches the profile of the shaft it is actually running.
        var testA = CreateSampleTest("Ensaio Eixo A");
        var testB = CreateSampleTest("Ensaio Eixo B");
        _store.SaveTare(testA.FolderName, _store.LoadTareProfile("eixo_furo_unico")!);
        _store.SaveTare(testB.FolderName, _store.LoadTareProfile("eixo_furo_duplo")!);

        Assert.Equal(0.61, _store.LoadTest(testA.FolderName)!.Tare!.Points[0].PVoidW, 4);
        Assert.Equal(0.94, _store.LoadTest(testB.FolderName)!.Tare!.Points[0].PVoidW, 4);
    }

    [Fact]
    public void Tare_Profile_Carries_Its_Name_And_Is_Replaced_On_Resave()
    {
        _store.SaveTareProfile("eixo_furo_unico", TareOf(300, 0.61));
        Assert.Equal("eixo_furo_unico", _store.LoadTareProfile("eixo_furo_unico")!.ProfileName);

        // Re-measuring the same shaft overwrites that shaft's curve, never adds a second one.
        _store.SaveTareProfile("eixo_furo_unico", TareOf(300, 0.72));

        Assert.Single(_store.ListTareProfiles());
        Assert.Equal(0.72, _store.LoadTareProfile("eixo_furo_unico")!.Points[0].PVoidW, 4);
    }

    [Fact]
    public void Deleting_One_Tare_Profile_Leaves_The_Other_Intact()
    {
        _store.SaveTareProfile("eixo_furo_unico", TareOf(300, 0.61));
        _store.SaveTareProfile("eixo_furo_duplo", TareOf(300, 0.94));

        Assert.True(_store.DeleteTareProfile("eixo_furo_duplo"));
        Assert.False(_store.DeleteTareProfile("eixo_furo_duplo"));

        var remaining = Assert.Single(_store.ListTareProfiles());
        Assert.Equal("eixo_furo_unico", remaining.Name);
    }

    [Fact]
    public void Tare_Profile_Library_Is_Not_Listed_Or_Claimable_As_An_Assay()
    {
        _store.SaveTareProfile("eixo_furo_unico", TareOf(300, 0.61));

        Assert.Empty(_store.ListTests());
        Assert.False(_store.ValidateTestName(PowerTestFileContracts.TareProfilesDirectoryName, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Invalid_Tare_Profile_Names_Are_Rejected_Before_Touching_Disk()
    {
        Assert.Throws<ArgumentException>(() => _store.SaveTareProfile("eixo/furo", TareOf(300, 0.61)));
        Assert.Throws<ArgumentException>(() => _store.SaveTareProfile("  ", TareOf(300, 0.61)));

        Assert.Null(_store.LoadTareProfile("eixo/furo"));
        Assert.Empty(_store.ListTareProfiles());
    }

    private static TareCurve TareOf(double rpm, double pVoidW) => new()
    {
        SchemaVersion = 2,
        Points = { new TarePoint(rpm, pVoidW, 0.5) { SampleCount = 120 } },
    };

    private PowerTestDocument CreateSampleTest(string name)
    {
        var geometry = new PowerGeometry
        {
            VesselDiameterM = 0.20,
            LiquidVolumeM3 = 0.005,
            Baffled = true,
            Impellers =
            {
                new Impeller { Type = ImpellerType.RushtonFlatBlade, Label = "Rushton", DiameterM = 0.06, BladeCount = 6, StageIndex = 0, LiteratureNp = 5.0 },
                new Impeller { Type = ImpellerType.ElephantEar, Label = "Orelha", DiameterM = 0.08, BladeCount = 3, StageIndex = 1, LiteratureNp = 1.3 },
            },
        };

        var conditions = new List<PowerCondition>
        {
            new() { AgitationRpm = 300, GasMode = PowerGasMode.Ungassed, RequestedReplicates = 3 },
            new() { AgitationRpm = 300, GasMode = PowerGasMode.Both, GasFlowLpm = 5.0, RequestedReplicates = 2 },
        };

        return _store.CreateTest(name, new FluidProperties(), geometry, new PowerTestSettings(), conditions);
    }
}
