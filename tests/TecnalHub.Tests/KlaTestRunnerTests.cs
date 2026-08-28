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

    /// <summary>
    /// With the vent line declared, the flowmeter must reach its airflow outside the vessel.
    /// The run only becomes an assay once the measured flow sits inside the tolerance band and
    /// the vent closes — the whole point being that the meter's start-up pulse never enters the
    /// bioreactor.
    /// </summary>
    [Fact]
    public async Task VentStabilization_Vents_The_Flow_Pulse_And_Starts_The_Assay_Only_Once_It_Settles()
    {
        _device.PushState(ConnectionState.Connected);
        _device.PushTelemetry(new SensorSnapshot
        {
            OxygenCalibrated = 4.0,
            OxygenRaw = 4.0,
            FlowmeterOnline = true,
            FlowCommandPending = false,
        });

        var doc = _store.CreateTest("Ensaio Alivio", new KlaTestSettings
        {
            DOMinPercent = 10.0,
            DOMaxPercent = 85.0,
            VentStabilizationEnabled = true,
            VentAgitationRpm = 50.0,
            VentFlowToleranceLpm = 0.2,
            VentFlowStableSamples = 3,
        }, NitrogenValve.Valve1, ventValve: NitrogenValve.Valve2);
        var cond = new KlaTestCondition { AgitationRpm = 450, AirflowLpm = 3.0, RequestedReplicates = 1 };
        doc.Conditions.Add(cond);

        await _runner.StartTestAsync(doc);

        // Initial DO of 4% is already below the 10% cut-off, so the run skips nitrogen.
        await _runner.StartRunAsync(cond, 1);
        Assert.Equal(RunPhase.ClosingAllGas, _runner.Phase);

        PushGas(4.0, flow: 0.0, valve1: false, valve2: false, mainClosed: true, commandId: 1);
        Assert.Equal(RunPhase.OpeningVent, _runner.Phase);

        // The vent output carries the requested airflow with the main path open.
        var ventCommand = _device.Sent.Last(j => j.Contains("flowSetpoint"));
        Assert.Contains("\"flowSetpoint\":3", ventCommand);
        Assert.Contains("\"valve_1\":0", ventCommand);
        Assert.Contains("\"valve_2\":1", ventCommand);
        Assert.Contains("\"v_Flow\":0", ventCommand);

        // The vessel is not being sparged yet, so the assay rotation must not be running:
        // it would re-oxygenate the broth by surface aeration and spoil C₀.
        Assert.Equal(
            "{\"motorSetpoint\":50}",
            _device.Sent.Last(j => j.Contains("motorSetpoint")));

        PushGas(4.0, flow: 3.0, valve1: false, valve2: true, mainClosed: false, commandId: 2, measured: 6.4);
        Assert.Equal(RunPhase.StabilizingVentFlow, _runner.Phase);
        Assert.True(_runner.CurrentRun?.UsedVentStabilization);

        // The pulse itself is out of band and must not be counted.
        Assert.Equal(0, _runner.VentFlowStableCount);
        PushGas(4.0, flow: 3.0, valve1: false, valve2: true, mainClosed: false, commandId: 2, measured: 3.5);
        Assert.Equal(0, _runner.VentFlowStableCount);
        Assert.Equal(RunPhase.StabilizingVentFlow, _runner.Phase);

        // Two in-band readings are still one short of the three required.
        PushGas(4.0, flow: 3.0, valve1: false, valve2: true, mainClosed: false, commandId: 2, measured: 3.1);
        PushGas(4.0, flow: 3.0, valve1: false, valve2: true, mainClosed: false, commandId: 2, measured: 2.85);
        Assert.Equal(2, _runner.VentFlowStableCount);
        Assert.Equal(RunPhase.StabilizingVentFlow, _runner.Phase);

        // A single excursion restarts the count: the band must hold consecutively.
        PushGas(4.0, flow: 3.0, valve1: false, valve2: true, mainClosed: false, commandId: 2, measured: 3.4);
        Assert.Equal(0, _runner.VentFlowStableCount);

        PushGas(4.0, flow: 3.0, valve1: false, valve2: true, mainClosed: false, commandId: 2, measured: 3.05);
        PushGas(4.0, flow: 3.0, valve1: false, valve2: true, mainClosed: false, commandId: 2, measured: 2.95);
        Assert.Equal(RunPhase.StabilizingVentFlow, _runner.Phase);
        PushGas(4.0, flow: 3.0, valve1: false, valve2: true, mainClosed: false, commandId: 2, measured: 3.0);
        Assert.Equal(RunPhase.OpeningAir, _runner.Phase);

        // Closing the vent keeps the settled setpoint: no second pulse reaches the vessel.
        var admitCommand = _device.Sent.Last(j => j.Contains("flowSetpoint"));
        Assert.Contains("\"flowSetpoint\":3", admitCommand);
        Assert.Contains("\"valve_1\":0", admitCommand);
        Assert.Contains("\"valve_2\":0", admitCommand);
        Assert.Contains("\"v_Flow\":0", admitCommand);

        // The assay rotation arrives with the gas, not before it.
        Assert.Equal(
            "{\"motorSetpoint\":450}",
            _device.Sent.Last(j => j.Contains("motorSetpoint")));

        PushGas(4.0, flow: 3.0, valve1: false, valve2: false, mainClosed: false, commandId: 3, measured: 3.0);
        Assert.Equal(RunPhase.Reoxygenating, _runner.Phase);

        // The venting samples are recorded, but none of them is assay data.
        var points = _runner.CurrentRunPoints;
        Assert.Contains(points, p => p.Phase == RunPhase.StabilizingVentFlow);
        Assert.DoesNotContain(
            points.Where(p => p.Phase == RunPhase.Reoxygenating),
            p => p.Valve1 || p.Valve2);
    }

    /// <summary>
    /// Venting through the nitrogen output would open the N₂ line while the runner believed it
    /// was dumping air. The run is refused before anything is claimed.
    /// </summary>
    [Fact]
    public async Task StartRun_Refuses_A_Vent_Valve_That_Collides_With_Nitrogen()
    {
        _device.PushState(ConnectionState.Connected);
        _device.PushTelemetry(new SensorSnapshot
        {
            OxygenCalibrated = 60.0,
            OxygenRaw = 60.0,
            FlowmeterOnline = true,
            FlowCommandPending = false,
        });

        var doc = _store.CreateTest("Ensaio Alivio Colidido", new KlaTestSettings
        {
            VentStabilizationEnabled = true,
        }, NitrogenValve.Valve1, ventValve: NitrogenValve.Valve1);
        var cond = new KlaTestCondition { AgitationRpm = 300, AirflowLpm = 2.0, RequestedReplicates = 1 };
        doc.Conditions.Add(cond);

        await _runner.StartTestAsync(doc);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _runner.StartRunAsync(cond, 1));
        Assert.Equal(RunPhase.Idle, _runner.Phase);
        Assert.Equal(CommandOwner.Manual, _arbiter.OwnerOf(ActuatorId.Aeration));
    }

    /// <summary>
    /// Stopping while the gas is going out of the vent must still close the flowmeter, otherwise
    /// the operator is left venting into the room with the run marked finished.
    /// </summary>
    [Fact]
    public async Task StopRun_During_Venting_Closes_The_Flowmeter()
    {
        _device.PushState(ConnectionState.Connected);
        _device.PushTelemetry(new SensorSnapshot
        {
            OxygenCalibrated = 4.0,
            OxygenRaw = 4.0,
            FlowmeterOnline = true,
            FlowCommandPending = false,
        });

        var doc = _store.CreateTest("Ensaio Alivio Parada", new KlaTestSettings
        {
            DOMinPercent = 10.0,
            VentStabilizationEnabled = true,
            VentFlowStableSamples = 3,
        }, NitrogenValve.Valve1, ventValve: NitrogenValve.Valve2);
        var cond = new KlaTestCondition { AgitationRpm = 450, AirflowLpm = 3.0, RequestedReplicates = 1 };
        doc.Conditions.Add(cond);

        await _runner.StartTestAsync(doc);
        await _runner.StartRunAsync(cond, 1);
        PushGas(4.0, flow: 0.0, valve1: false, valve2: false, mainClosed: true, commandId: 1);
        PushGas(4.0, flow: 3.0, valve1: false, valve2: true, mainClosed: false, commandId: 2, measured: 3.0);
        Assert.Equal(RunPhase.StabilizingVentFlow, _runner.Phase);

        await _runner.StopRunAndReviewAsync("Parada durante o alívio");

        Assert.Equal(RunPhase.StoppingRun, _runner.Phase);
        var stop = _device.Sent.Last(j => j.Contains("flowSetpoint"));
        Assert.Contains("\"flowSetpoint\":0", stop);
        Assert.Contains("\"valve_1\":0", stop);
        Assert.Contains("\"valve_2\":0", stop);
        Assert.Contains("\"v_Flow\":1", stop);

        PushGas(4.0, flow: 0.0, valve1: false, valve2: false, mainClosed: true, commandId: 3);
        Assert.Equal(RunPhase.Reviewing, _runner.Phase);
    }

    private void PushGas(
        double dissolvedOxygen,
        double flow,
        bool valve1,
        bool valve2,
        bool mainClosed,
        int commandId,
        double? measured = null)
    {
        _device.PushTelemetry(new SensorSnapshot
        {
            OxygenCalibrated = dissolvedOxygen,
            OxygenRaw = dissolvedOxygen,
            FlowValve1 = valve1 ? 1 : 0,
            FlowValve2 = valve2 ? 1 : 0,
            FlowValveMain = mainClosed ? 1 : 0,
            FlowRate = measured ?? flow,
            FlowSetpoint = flow,
            FlowmeterOnline = true,
            FlowCommandId = commandId,
            FlowCommandAck = commandId,
            FlowCommandPending = false,
        });
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
