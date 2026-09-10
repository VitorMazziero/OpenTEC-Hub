using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.PowerTesting;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// Guards the four places where a measurement used to be taken and never written down.
/// </summary>
/// <remarks>
/// Every one of these is a data-loss test, not a formatting test: the assertions are about a
/// reading reaching disk at all, and about it staying readable afterwards. They are grouped in
/// one file because they answer a single question - does the app record everything it measures -
/// and because a regression in any of them looks identical from the bench: an assay that ran
/// fine and left nothing to analyse.
/// </remarks>
[Collection("AppPaths")]
public sealed class RawDataIntegrityTests : IDisposable
{
    private readonly string _testRoot;
    private readonly IDisposable _overrideScope;

    public RawDataIntegrityTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"opentechub-rawdata-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
        _overrideScope = AppPaths.OverrideForTests(_testRoot);
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

    // =====================================================================
    // kLa: broth temperature and measured shaft speed
    // =====================================================================

    [Fact]
    public void Kla_raw_row_carries_temperature_and_measured_rpm()
    {
        var header = KlaTestFileContracts.FormatRawDataHeader().Split(',');
        Assert.Equal("TemperatureC", header[^2]);
        Assert.Equal("RpmMeasured", header[^1]);

        var row = KlaTestFileContracts.FormatRawDataRow(new KlaRawDataPoint(
            DateTimeOffset.UtcNow, 12.5, RunPhase.Reoxygenating,
            40.0, 40.2, 3.0, 3.0, 450, false, false, false,
            TemperatureC: 30.4, RpmMeasured: 447.5)).Split(',');

        Assert.Equal(header.Length, row.Length);
        Assert.Equal("30.40", row[^2]);
        Assert.Equal("447.5", row[^1]);
    }

    [Fact]
    public void Kla_raw_row_leaves_an_absent_reading_empty_rather_than_zero()
    {
        // A module with no servo and a probe that never reported must not produce a row
        // claiming 0 °C and a stopped shaft - those are legitimate values elsewhere.
        var row = KlaTestFileContracts.FormatRawDataRow(new KlaRawDataPoint(
            DateTimeOffset.UtcNow, 1.0, RunPhase.Deoxygenating,
            40.0, 40.0, 0.0, 0.0, 700, false, false, true)).Split(',');

        Assert.Equal("", row[^2]);
        Assert.Equal("", row[^1]);
    }

    [Fact]
    public void Kla_global_series_row_carries_temperature_and_measured_rpm()
    {
        var header = KlaTestFileContracts.FormatGlobalSeriesHeader().Split(',');
        Assert.Equal("TemperatureC", header[^2]);
        Assert.Equal("RpmMeasured", header[^1]);

        var row = KlaTestFileContracts.FormatGlobalSeriesRow(new KlaGlobalSeriesSample(
            DateTimeOffset.UtcNow, 5.0, Guid.NewGuid(), null, null, null, RunPhase.Preflight,
            40.0, 40.0, 15.0, 85.0, 0.0, 0.0, 0, false, false, true,
            null, null, false, 1, "Preflight", "", TemperatureC: 29.9, RpmMeasured: 0.0)).Split(',');

        Assert.Equal(header.Length, row.Length);
        Assert.Equal("29.90", row[^2]);

        // 0 rpm is a measurement here - the servo answered and the shaft was stopped.
        Assert.Equal("0.0", row[^1]);
    }

    [Fact]
    public void Kla_store_round_trips_the_new_columns()
    {
        var store = new KlaTestStore(AppPaths.KlaTestsDirectory);
        var doc = store.CreateTest("Ensaio Colunas", new KlaTestSettings(), NitrogenValve.Valve1);
        var run = new KlaTestRun { TestId = doc.TestId, AgitationRpm = 450, AirflowLpm = 3.0 };
        var runFolder = store.InitializeRunFolder(doc.FolderName, run);

        store.SaveRunRawData(doc.FolderName, runFolder, [
            new KlaRawDataPoint(DateTimeOffset.UtcNow, 0.0, RunPhase.Reoxygenating,
                20.0, 20.0, 3.0, 3.0, 450, false, false, false, 31.2, 449.0),
            new KlaRawDataPoint(DateTimeOffset.UtcNow, 2.0, RunPhase.Reoxygenating,
                25.0, 25.0, 3.0, 3.0, 450, false, false, false, null, null),
        ]);

        var loaded = store.LoadRunRawData(doc.FolderName, runFolder);
        Assert.Equal(2, loaded.Count);
        Assert.Equal(31.2, loaded[0].TemperatureC!.Value, 3);
        Assert.Equal(449.0, loaded[0].RpmMeasured!.Value, 3);
        Assert.Null(loaded[1].TemperatureC);
        Assert.Null(loaded[1].RpmMeasured);
    }

    [Fact]
    public void Kla_store_still_reads_a_file_written_before_the_new_columns()
    {
        var store = new KlaTestStore(AppPaths.KlaTestsDirectory);
        var doc = store.CreateTest("Ensaio Legado", new KlaTestSettings(), NitrogenValve.Valve1);
        var run = new KlaTestRun { TestId = doc.TestId, AgitationRpm = 300, AirflowLpm = 1.5 };
        var runFolder = store.InitializeRunFolder(doc.FolderName, run);

        // Exactly the schema-1 layout: eleven columns, nothing after VFlow.
        var path = store.GetRunRawDataPath(doc.FolderName, runFolder);
        File.WriteAllText(
            path,
            "TimestampUtc,RelativeSeconds,Phase,DORaw,DOFiltered,FlowMeasured,FlowSetpoint,AgitationSetpoint,Valve1,Valve2,VFlow" +
            Environment.NewLine +
            $"{DateTimeOffset.UtcNow:O},1.500,Reoxygenating,42.00,42.10,1.50,1.50,300,0,0,0" + Environment.NewLine);

        var loaded = store.LoadRunRawData(doc.FolderName, runFolder);
        var point = Assert.Single(loaded);
        Assert.Equal(42.0, point.DORaw, 3);
        Assert.Equal(300, point.AgitationSetpoint, 3);
        Assert.Null(point.TemperatureC);
        Assert.Null(point.RpmMeasured);
    }

    [Fact]
    public async Task Kla_run_records_temperature_and_measured_rpm_from_telemetry()
    {
        var device = new RecordingDeviceService();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        using var arbiter = new CommandArbiter(device, clock);
        var store = new KlaTestStore(AppPaths.KlaTestsDirectory);
        using var runner = new KlaTestRunner(
            device, arbiter, store, new KlaAnalysisEngine(), new MemorySettingsService(), clock);

        device.PushState(ConnectionState.Connected);
        device.PushTelemetry(RunningFrame(temperature: 30.5, servoRpm: 448.0));

        var doc = store.CreateTest("Ensaio Telemetria", new KlaTestSettings(), NitrogenValve.Valve1);
        var condition = new KlaTestCondition { AgitationRpm = 450, AirflowLpm = 3.0, RequestedReplicates = 1 };
        doc.Conditions.Add(condition);
        store.SaveConditionsTable(doc.FolderName, doc.Conditions);

        await runner.StartTestAsync(doc);
        await runner.StartRunAsync(condition, 1);
        device.PushTelemetry(RunningFrame(temperature: 30.5, servoRpm: 448.0));

        var runFolder = runner.CurrentRun!.FolderName;
        var rows = File.ReadAllLines(store.GetRunRawDataPath(doc.FolderName, runFolder));
        var lastCells = rows[^1].Split(',');
        Assert.Equal("30.50", lastCells[^2]);
        Assert.Equal("448.0", lastCells[^1]);

        var globalRows = File.ReadAllLines(
            Path.Combine(AppPaths.KlaTestsDirectory, doc.FolderName, KlaTestFileContracts.GlobalSeriesFileName));
        Assert.Equal("448.0", globalRows[^1].Split(',')[^1]);
    }

    [Fact]
    public async Task Kla_run_records_no_speed_on_a_bench_without_a_servo()
    {
        var device = new RecordingDeviceService();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        using var arbiter = new CommandArbiter(device, clock);
        var store = new KlaTestStore(AppPaths.KlaTestsDirectory);
        using var runner = new KlaTestRunner(
            device, arbiter, store, new KlaAnalysisEngine(), new MemorySettingsService(), clock);

        device.PushState(ConnectionState.Connected);
        var noServo = RunningFrame(temperature: 28.0, servoRpm: 0.0) with
        {
            HasServoTelemetry = false,
            HasServoSample = false,
            ServoOnline = false,
            ServoRpm = SensorReadings.NotReceived,
        };
        device.PushTelemetry(noServo);

        var doc = store.CreateTest("Ensaio Sem Servo", new KlaTestSettings(), NitrogenValve.Valve1);
        var condition = new KlaTestCondition { AgitationRpm = 450, AirflowLpm = 3.0, RequestedReplicates = 1 };
        doc.Conditions.Add(condition);
        store.SaveConditionsTable(doc.FolderName, doc.Conditions);

        await runner.StartTestAsync(doc);
        await runner.StartRunAsync(condition, 1);
        device.PushTelemetry(noServo);

        var rows = File.ReadAllLines(
            store.GetRunRawDataPath(doc.FolderName, runner.CurrentRun!.FolderName));
        var lastCells = rows[^1].Split(',');
        Assert.Equal("28.00", lastCells[^2]);
        Assert.Equal("", lastCells[^1]);
    }

    // =====================================================================
    // Power: raw tare readings
    // =====================================================================

    [Fact]
    public void Tare_raw_capture_round_trips_through_the_store()
    {
        var store = new PowerTestStore(AppPaths.PowerTestsDirectory);
        var doc = store.CreateTest("Ensaio Tara Bruta", new FluidProperties(), new PowerGeometry(), new PowerTestSettings());
        var started = DateTimeOffset.UtcNow;

        var fileName = store.BeginTareRawCapture(doc.FolderName, started);
        store.AppendTareRawSample(doc.FolderName, fileName,
            new TareSample(started, 0.5, 300, 299.2, 3.11, TareCapturePhase.StabilizingSpeed, false), 1);
        store.AppendTareRawSample(doc.FolderName, fileName,
            new TareSample(started.AddSeconds(1), 1.5, 300, 300.1, 3.09, TareCapturePhase.Accumulating, true, 2), 1);

        var loaded = store.LoadTareRawData(doc.FolderName, fileName);
        Assert.Equal(2, loaded.Count);
        Assert.False(loaded[0].Counted);
        Assert.Equal(TareCapturePhase.StabilizingSpeed, loaded[0].Phase);
        Assert.True(loaded[1].Counted);
        Assert.Equal(2, loaded[1].Attempt);
        Assert.Equal(300.1, loaded[1].RpmMeasured, 3);
        Assert.Equal(3.09, loaded[1].TorquePercent, 3);
    }

    [Fact]
    public void A_second_tare_sweep_never_overwrites_the_first()
    {
        var store = new PowerTestStore(AppPaths.PowerTestsDirectory);
        var doc = store.CreateTest("Ensaio Duas Taras", new FluidProperties(), new PowerGeometry(), new PowerTestSettings());
        var started = DateTimeOffset.UtcNow;

        var first = store.BeginTareRawCapture(doc.FolderName, started);
        store.AppendTareRawSample(doc.FolderName, first,
            new TareSample(started, 0.5, 300, 299.0, 3.10, TareCapturePhase.Accumulating, true), 1);

        // Same instant: a retry started inside the same second must still get its own file.
        var second = store.BeginTareRawCapture(doc.FolderName, started);

        Assert.NotEqual(first, second);
        Assert.Single(store.LoadTareRawData(doc.FolderName, first));
        Assert.Empty(store.LoadTareRawData(doc.FolderName, second));
    }

    [Fact]
    public async Task A_cancelled_tare_sweep_keeps_the_readings_it_took()
    {
        var store = new PowerTestStore(AppPaths.PowerTestsDirectory);
        var doc = store.CreateTest("Ensaio Tara Cancelada", new FluidProperties(), new PowerGeometry(), new PowerTestSettings());
        var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, TimeProvider.System);
        using var vm = new PowerTestViewModel(store, device, arbiter);

        vm.SelectedTest = vm.Tests.First(t => t.Name == doc.Name);
        vm.LoadSelectedTestCommand.Execute(null);
        vm.TareStartRpm = 300;
        vm.TareEndRpm = 300;
        vm.TareStepRpm = 50;

        var sweep = vm.StartTareSweepCommand.ExecuteAsync(null);
        Assert.True(vm.IsTareRunning);

        for (var i = 0; i < 5; i++)
        {
            device.PushTelemetry(ServoFrame(rpm: 299.5 + (i * 0.1), torquePercent: 3.2));
        }

        vm.CancelTareSweepCommand.Execute(null);
        await sweep;

        // No curve was produced - and that is exactly the case where the readings used to vanish.
        Assert.Null(store.LoadTare(doc.FolderName));

        var sweepDirectory = Path.Combine(
            AppPaths.PowerTestsDirectory, doc.FolderName, PowerTestFileContracts.TareRawDirectoryName);
        var rawFile = Assert.Single(Directory.GetFiles(sweepDirectory, "*.csv"));
        var samples = store.LoadTareRawData(doc.FolderName, Path.GetFileName(rawFile));
        Assert.NotEmpty(samples);
        Assert.All(samples, s => Assert.Equal(300, s.TargetRpm, 3));
        Assert.Contains(PowerTestFileContracts.TareRawDirectoryName, vm.TareProgressMessage);
    }

    // =====================================================================
    // Power: single-point checks
    // =====================================================================

    [Fact]
    public async Task A_single_point_check_is_recorded_with_its_manifest()
    {
        var store = new PowerTestStore(AppPaths.PowerTestsDirectory);
        var doc = store.CreateTest("Ensaio Ponto Unico", new FluidProperties(), new PowerGeometry(), new PowerTestSettings());
        var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, TimeProvider.System);
        using var vm = new PowerTestViewModel(store, device, arbiter);

        vm.SelectedTest = vm.Tests.First(t => t.Name == doc.Name);
        vm.LoadSelectedTestCommand.Execute(null);
        vm.SinglePointRpm = 300;
        vm.SinglePointFlowLpm = 0;

        await vm.StartSinglePointCommand.ExecuteAsync(null);
        Assert.True(vm.IsSinglePointActive);

        device.PushTelemetry(ServoFrame(rpm: 299.4, torquePercent: 4.10));
        device.PushTelemetry(ServoFrame(rpm: 300.2, torquePercent: 4.14));

        await vm.StopSinglePointCommand.ExecuteAsync(null);

        var directory = Path.Combine(
            AppPaths.PowerTestsDirectory, doc.FolderName, PowerTestFileContracts.SinglePointDirectoryName);
        var csv = Assert.Single(Directory.GetFiles(directory, "*.csv"));
        var lines = File.ReadAllLines(csv);
        Assert.Equal(PowerTestFileContracts.FormatRawDataHeader(), lines[0]);
        Assert.Equal(3, lines.Length);

        var manifestPath = Path.ChangeExtension(csv, null) + PowerTestFileContracts.SinglePointManifestSuffix;
        var manifest = PowerTestFileContracts.DeserializeSinglePointSession(File.ReadAllText(manifestPath))!;
        Assert.Equal(300, manifest.TargetRpm, 3);
        Assert.Null(manifest.GasFlowSetpointLpm);
        Assert.Equal(2, manifest.SampleCount);
        Assert.NotNull(manifest.CompletedUtc);
        Assert.Equal(doc.FolderName, manifest.TestFolderName);

        // Without the rated torque the watts column cannot be rebuilt from the percentage.
        Assert.True(manifest.MotorRatedTorqueNm > 0);

        var firstRow = lines[1].Split(',');
        Assert.Equal(299.4, double.Parse(firstRow[3], CultureInfo.InvariantCulture), 1);
        Assert.Equal(4.10, double.Parse(firstRow[4], CultureInfo.InvariantCulture), 2);
    }

    [Fact]
    public async Task A_single_point_check_with_no_assay_open_is_still_recorded()
    {
        var store = new PowerTestStore(AppPaths.PowerTestsDirectory);
        var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, TimeProvider.System);
        using var vm = new PowerTestViewModel(store, device, arbiter);

        Assert.Null(vm.CurrentTest);
        vm.SinglePointRpm = 250;
        vm.SinglePointFlowLpm = 1.5;

        await vm.StartSinglePointCommand.ExecuteAsync(null);
        device.PushTelemetry(ServoFrame(rpm: 249.7, torquePercent: 3.80));
        await vm.StopSinglePointCommand.ExecuteAsync(null);

        var directory = Path.Combine(
            AppPaths.PowerTestsDirectory, PowerTestFileContracts.SinglePointDirectoryName);
        var csv = Assert.Single(Directory.GetFiles(directory, "*.csv"));
        Assert.Equal(2, File.ReadAllLines(csv).Length);
        Assert.Contains("Q01p50", Path.GetFileName(csv), StringComparison.Ordinal);

        // The folder the store now owns must not read back as an assay.
        Assert.DoesNotContain(store.ListTests(), t => t.FolderName == PowerTestFileContracts.SinglePointDirectoryName);
        Assert.False(store.ValidateTestName(PowerTestFileContracts.SinglePointDirectoryName, out _));
    }

    // =====================================================================

    private static SensorSnapshot RunningFrame(double temperature, double servoRpm) => new()
    {
        Temperature = temperature,
        OxygenCalibrated = 80.0,
        OxygenRaw = 80.0,
        FlowmeterOnline = true,
        FlowCommandPending = false,
        HasServoTelemetry = true,
        HasServoSample = true,
        ServoOnline = true,
        ServoCommEnabled = true,
        ServoRpm = servoRpm,
        ServoTorquePct = 5.0,
    };

    private static SensorSnapshot ServoFrame(double rpm, double torquePercent) => new()
    {
        Temperature = 25.0,
        FlowmeterOnline = true,
        HasServoTelemetry = true,
        HasServoSample = true,
        ServoOnline = true,
        ServoCommEnabled = true,
        ServoRpm = rpm,
        ServoTorquePct = torquePercent,
    };
}
