using System;
using System.Collections.Generic;
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
    public void CreateTest_Initializes_Expected_File_Structure()
    {
        var doc = CreateSampleTest("Ensaio Estrutura");

        var folderPath = Path.Combine(_store.RootDirectory, doc.FolderName);
        Assert.True(File.Exists(Path.Combine(folderPath, PowerTestFileContracts.TestManifestFileName)));
        Assert.True(File.Exists(Path.Combine(folderPath, PowerTestFileContracts.ConditionTableFileName)));
        Assert.True(File.Exists(Path.Combine(folderPath, PowerTestFileContracts.GlobalSeriesFileName)));
        Assert.True(File.Exists(Path.Combine(folderPath, PowerTestFileContracts.ResultsSummaryFileName)));
        Assert.True(Directory.Exists(Path.Combine(folderPath, PowerTestFileContracts.RunsDirectoryName)));

        // Brand new tests are relative until a tare/calibration lands (§9, §20.5).
        Assert.True(doc.RelativeMode);
    }

    [Fact]
    public void CreateThenLoad_Round_Trips_Geometry_Fluid_And_Conditions()
    {
        var created = CreateSampleTest("Round Trip");

        var loaded = _store.LoadTest(created.FolderName);

        Assert.NotNull(loaded);
        Assert.Equal(created.TestId, loaded!.TestId);
        Assert.Equal("Round Trip", loaded.Name);

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
    public void SaveTare_And_Calibration_Round_Trip_And_Attach_On_Load()
    {
        var created = CreateSampleTest("Tara e Calibração");

        var tare = new TareCurve
        {
            ImpellerSetHash = PowerTestFileContracts.ComputeImpellerSetHash(created.Geometry),
            Points =
            {
                new TarePoint(300, 0.61, 0.62),
                new TarePoint(600, 1.51, 0.41),
            },
        };
        _store.SaveTare(created.FolderName, tare);

        var calibration = new TorqueCalibration { Scale = 1.02, Offset = 0, ReferenceNm = 0.5, MotorRatedTorqueNm = 1.27 };
        _store.SaveCalibration(created.FolderName, calibration);

        var loadedTare = _store.LoadTare(created.FolderName);
        Assert.NotNull(loadedTare);
        Assert.Equal(2, loadedTare!.Points.Count);
        Assert.Equal(0.62, loadedTare.Points[0].SigmaTauPercent, 4);
        Assert.Equal(tare.ImpellerSetHash, loadedTare.ImpellerSetHash);

        var loadedDoc = _store.LoadTest(created.FolderName);
        Assert.NotNull(loadedDoc!.Tare);
        Assert.NotNull(loadedDoc.Calibration);
        Assert.Equal(1.02, loadedDoc.Calibration!.Scale, 4);
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
        // (timestamp, relSec, phase, rpm, torque%, torqueNm, shaftW, flowLpm, counted)
        _store.AppendRunRawDataPoint(created.FolderName, runFolder,
            new PowerDataPoint(t0, 0.0, PowerRunPhase.SettlingTorque, 299.6, 1.57, 0.0199, 0.624, 0.0, false));
        _store.AppendRunRawDataPoint(created.FolderName, runFolder,
            new PowerDataPoint(t0.AddSeconds(0.5), 0.5, PowerRunPhase.AccumulatingToTarget, 300.1, 1.60, 0.0203, 0.638, 0.0, true));

        var points = _store.LoadRunRawData(created.FolderName, runFolder);
        Assert.Equal(2, points.Count);
        Assert.Equal(PowerRunPhase.AccumulatingToTarget, points[1].Phase);
        Assert.True(points[1].Counted);
        Assert.False(points[0].Counted);
        Assert.Equal(300.1, points[1].RpmMeasured, 1);
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
