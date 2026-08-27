using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TecnalHub.Protocol;
using TecnalHub.Services.Communication;
using TecnalHub.Services.KlaTesting;
using TecnalHub.Services.Persistence;
using Xunit;

namespace TecnalHub.Tests;

[Collection("AppPaths")]
public sealed class KlaTestRunnerTests : IDisposable
{
    private readonly string _testRoot;
    private readonly IDisposable _overrideScope;
    private readonly RecordingDeviceService _device;
    private readonly TestClock _clock;
    private readonly CommandArbiter _arbiter;
    private readonly KlaTestStore _store;
    private readonly KlaAnalysisEngine _analysisEngine;
    private readonly MemorySettingsService _settings;
    private readonly KlaTestRunner _runner;

    public KlaTestRunnerTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"tecnalhub-klarunner-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
        _overrideScope = AppPaths.OverrideForTests(_testRoot);

        _device = new RecordingDeviceService();
        _clock = new TestClock(DateTimeOffset.UtcNow);
        _arbiter = new CommandArbiter(_device, _clock);
        _store = new KlaTestStore(AppPaths.KlaTestsDirectory);
        _analysisEngine = new KlaAnalysisEngine();
        _settings = new MemorySettingsService();

        _runner = new KlaTestRunner(
            _device,
            _arbiter,
            _store,
            _analysisEngine,
            _settings,
            _clock);
    }

    public void Dispose()
    {
        _runner.Dispose();
        _arbiter.Dispose();
        _overrideScope.Dispose();

        if (Directory.Exists(_testRoot))
        {
            try
            {
                Directory.Delete(_testRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    [Fact]
    public async Task StartTest_And_StartRun_Claims_Actuators_And_Enters_Deoxygenation()
    {
        // 1. Setup active connection and high initial DO
        _device.PushState(ConnectionState.Connected);
        _device.PushTelemetry(new SensorSnapshot
        {
            OxygenCalibrated = 80.0,
            OxygenRaw = 80.0,
            FlowmeterOnline = true,
            FlowCommandPending = false,
        });

        var doc = _store.CreateTest("Ensaio Runner 1", new KlaTestSettings
        {
            DOMinPercent = 10.0,
            DOMaxPercent = 85.0,
            PostNitrogenMinimumDelaySeconds = 2.0,
            StabilityDerivativeSpanSeconds = 2.0,
            StabilityDerivativeThresholdPercentPerSecond = 0.05,
            StabilityRequiredSamples = 3,
        }, NitrogenValve.Valve1);
        var cond = new KlaTestCondition { AgitationRpm = 450, AirflowLpm = 3.0, RequestedReplicates = 2 };
        doc.Conditions.Add(cond);
        _store.SaveConditionsTable(doc.FolderName, doc.Conditions);

        await _runner.StartTestAsync(doc);
        Assert.Equal(KlaTestStatus.Running, _runner.CurrentTest?.Status);

        // 2. Start Run: Initial DO is 80% > 10% -> OpeningNitrogen
        await _runner.StartRunAsync(cond, 1);

        Assert.Equal(CommandOwner.KlaAssay, _arbiter.OwnerOf(ActuatorId.Agitation));
        Assert.Equal(CommandOwner.KlaAssay, _arbiter.OwnerOf(ActuatorId.Aeration));
        Assert.Equal(RunPhase.ClosingAllGas, _runner.Phase);

        // The interlock must first confirm zero flow and both auxiliary valves closed.
        _device.PushTelemetry(new SensorSnapshot
        {
            OxygenCalibrated = 80.0,
            OxygenRaw = 80.0,
            FlowmeterOnline = true,
            FlowValveMain = 1,
            FlowSetpoint = 0.0,
            FlowCommandId = 1,
            FlowCommandAck = 0,
            FlowCommandPending = false,
        });
        Assert.Equal(RunPhase.ClosingAllGas, _runner.Phase);

        _device.PushTelemetry(new SensorSnapshot
        {
            OxygenCalibrated = 80.0,
            OxygenRaw = 80.0,
            FlowmeterOnline = true,
            FlowValve1 = 0,
            FlowValve2 = 0,
            FlowValveMain = 1,
            FlowSetpoint = 0.0,
            FlowCommandId = 1,
            FlowCommandAck = 1,
            FlowCommandPending = false,
        });
        Assert.Equal(RunPhase.OpeningNitrogen, _runner.Phase);

        // Confirm N2 gas state
        _device.PushTelemetry(new SensorSnapshot
        {
            OxygenCalibrated = 75.0,
            OxygenRaw = 75.0,
            FlowValve1 = 1,
            FlowValve2 = 0,
            FlowValveMain = 1,
            FlowSetpoint = 0.0,
            FlowmeterOnline = true,
            FlowCommandId = 2,
            FlowCommandAck = 2,
            FlowCommandPending = false,
        });

        Assert.Equal(RunPhase.Deoxygenating, _runner.Phase);

        // 3. DO reaches 8.0% <= 10.0% -> triggers transition to ClosingNitrogen
        _device.PushTelemetry(new SensorSnapshot
        {
            OxygenCalibrated = 8.0,
            OxygenRaw = 8.0,
            FlowValve1 = 1,
            FlowValve2 = 0,
            FlowValveMain = 1,
            FlowSetpoint = 0.0,
            FlowmeterOnline = true,
            FlowCommandId = 2,
            FlowCommandAck = 2,
            FlowCommandPending = false,
        });

        Assert.Equal(RunPhase.ClosingNitrogen, _runner.Phase);

        // A zero-flow target must not accept a stale non-zero echoed setpoint.
        _device.PushTelemetry(new SensorSnapshot
        {
            OxygenCalibrated = 7.5,
            OxygenRaw = 7.5,
            FlowmeterOnline = true,
            FlowValveMain = 1,
            FlowSetpoint = 9.0,
            FlowCommandId = 3,
            FlowCommandAck = 3,
            FlowCommandPending = false,
        });
        Assert.Equal(RunPhase.ClosingNitrogen, _runner.Phase);

        // Confirm ClosingNitrogen (all closed) -> wait with no gas before opening Air
        _device.PushTelemetry(new SensorSnapshot
        {
            OxygenCalibrated = 7.5,
            OxygenRaw = 7.5,
            FlowValve1 = 0,
            FlowValve2 = 0,
            FlowValveMain = 1,
            FlowSetpoint = 0.0,
            FlowmeterOnline = true,
            FlowCommandId = 3,
            FlowCommandAck = 3,
            FlowCommandPending = false,
        });

        Assert.Equal(RunPhase.WaitingForDOStability, _runner.Phase);

        // Residual N2 / probe lag: a continuing fall must not open air.
        _clock.Advance(TimeSpan.FromSeconds(1));
        PushClosedGasTelemetry(7.2, 3);
        _clock.Advance(TimeSpan.FromSeconds(1));
        PushClosedGasTelemetry(6.8, 3);
        Assert.Equal(RunPhase.WaitingForDOStability, _runner.Phase);

        // Stable derivative over the configured window and consecutive confirmations.
        foreach (var stableDo in Enumerable.Repeat(6.80, 8))
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            PushClosedGasTelemetry(stableDo, 3);
        }
        Assert.Equal(3, _runner.StabilityConfirmationCount);
        Assert.Equal(RunPhase.OpeningAir, _runner.Phase);

        // Confirm Air opened (Flow=3.0, FlowValveMain=0) -> Reoxygenating
        _device.PushTelemetry(new SensorSnapshot
        {
            OxygenCalibrated = 12.0,
            OxygenRaw = 12.0,
            FlowValve1 = 0,
            FlowValve2 = 0,
            FlowValveMain = 0,
            FlowRate = 3.0,
            FlowSetpoint = 3.0,
            FlowmeterOnline = true,
            FlowCommandId = 4,
            FlowCommandAck = 4,
            FlowCommandPending = false,
        });

        Assert.Equal(RunPhase.Reoxygenating, _runner.Phase);

        // 4. DO reaches 88.0% >= 85.0% -> auto stops and opens Review
        _device.PushTelemetry(new SensorSnapshot
        {
            OxygenCalibrated = 88.0,
            OxygenRaw = 88.0,
            FlowValve1 = 0,
            FlowValve2 = 0,
            FlowValveMain = 0,
            FlowRate = 3.0,
            FlowSetpoint = 3.0,
            FlowmeterOnline = true,
            FlowCommandId = 4,
            FlowCommandAck = 4,
            FlowCommandPending = false,
        });

        Assert.Equal(RunPhase.StoppingRun, _runner.Phase);
        _device.PushTelemetry(new SensorSnapshot
        {
            OxygenCalibrated = 88.0,
            OxygenRaw = 88.0,
            FlowmeterOnline = true,
            FlowValve1 = 0,
            FlowValve2 = 0,
            FlowValveMain = 1,
            FlowSetpoint = 0.0,
            FlowCommandId = 5,
            FlowCommandAck = 5,
            FlowCommandPending = false,
        });
        Assert.Equal(RunPhase.Reviewing, _runner.Phase);
        Assert.True(_runner.CurrentRunPoints.Count >= 5);

        // 5. Accept Run
        var analysis = new KlaAnalysisRevision
        {
            CeqPercent = 98.0,
            TStartSeconds = 0,
            TEndSeconds = 100,
            KlaPerHour = 52.4,
            AnalysisR2 = 0.995,
            Quality = DecisionQuality.Acceptable,
        };

        await _runner.AcceptRunAsync(analysis);

        Assert.Equal(RunPhase.Accepted, _runner.Phase);
        Assert.Equal(1, cond.CompletedReplicates);
        Assert.Equal(1, cond.AcceptedReplicates);
    }

    private void PushClosedGasTelemetry(double dissolvedOxygen, int commandId)
    {
        _device.PushTelemetry(new SensorSnapshot
        {
            OxygenCalibrated = dissolvedOxygen,
            OxygenRaw = dissolvedOxygen,
            FlowValve1 = 0,
            FlowValve2 = 0,
            FlowValveMain = 1,
            FlowSetpoint = 0.0,
            FlowmeterOnline = true,
            FlowCommandId = commandId,
            FlowCommandAck = commandId,
            FlowCommandPending = false,
        });
    }

    [Fact]
    public async Task AbortTest_Dispatches_SafeStop_And_Releases_Ownership()
    {
        _device.PushState(ConnectionState.Connected);
        _device.PushTelemetry(new SensorSnapshot
        {
            OxygenCalibrated = 50.0,
            OxygenRaw = 50.0,
            FlowmeterOnline = true,
            FlowCommandPending = false,
        });

        var doc = _store.CreateTest("Ensaio Runner 2", new KlaTestSettings(), NitrogenValve.Valve1);
        var cond = new KlaTestCondition { AgitationRpm = 300, AirflowLpm = 2.0, RequestedReplicates = 1 };
        doc.Conditions.Add(cond);

        await _runner.StartTestAsync(doc);
        await _runner.StartRunAsync(cond, 1);

        Assert.Equal(CommandOwner.KlaAssay, _arbiter.OwnerOf(ActuatorId.Aeration));

        await _runner.AbortTestAsync("Operador cancelou");

        Assert.Equal(RunPhase.Aborting, _runner.Phase);
        Assert.Equal(CommandOwner.KlaAssay, _arbiter.OwnerOf(ActuatorId.Aeration));
        _device.PushTelemetry(new SensorSnapshot
        {
            OxygenCalibrated = 50,
            OxygenRaw = 50,
            FlowmeterOnline = true,
            FlowSetpoint = 0,
            FlowValveMain = 1,
            FlowCommandId = 2,
            FlowCommandAck = 2,
            FlowCommandPending = false,
        });
        Assert.Equal(RunPhase.Faulted, _runner.Phase);
        Assert.Equal(KlaTestStatus.Interrupted, _runner.CurrentTest?.Status);
        Assert.Equal(CommandOwner.Manual, _arbiter.OwnerOf(ActuatorId.Aeration));
        Assert.Equal(CommandOwner.Manual, _arbiter.OwnerOf(ActuatorId.Agitation));
    }

    [Fact]
    public void PrepareTest_AllowsHistoricalReview_WithoutChangingCompletedStatus()
    {
        var doc = _store.CreateTest("Ensaio histórico", new KlaTestSettings(), NitrogenValve.Valve1);
        doc.Status = KlaTestStatus.Completed;
        doc.CompletedUtc = DateTimeOffset.UtcNow;
        _store.SaveTestManifest(doc);

        _runner.PrepareTest(doc);

        Assert.Same(doc, _runner.CurrentTest);
        Assert.Equal(RunPhase.Idle, _runner.Phase);
        Assert.Equal(KlaTestStatus.Completed, doc.Status);
        Assert.False(_runner.IsRunning);
    }
}
