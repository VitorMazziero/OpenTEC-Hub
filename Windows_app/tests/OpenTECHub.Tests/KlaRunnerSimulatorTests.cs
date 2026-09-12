using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Simulator;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// The kLa runner closed on the Etapa 3 simulator: every frame the runner sends goes through
/// <see cref="WireCodec.ApplyCommand"/>, the model ticks, and its telemetry comes back through
/// the real <see cref="TelemetryParser"/>. This is the plan's "simulador completa uma corrida"
/// check (§7.2) without a bench: N₂ strips through B, the air settles on C, one frame opens A,
/// and the reactor re-aerates to the end-of-run DO.
/// </summary>
[Collection("AppPaths")]
public sealed class KlaRunnerSimulatorTests : IDisposable
{
    private static readonly ServoPowerModelOptions Quiet = ServoPowerModelOptions.Default with
    {
        SpeedTimeConstantSeconds = 0.0,
        TorqueTimeConstantSeconds = 0.0,
    };

    private static readonly CultivationProfile Abiotic = new("abiotic",
        [new CultivationPhase("Water", double.PositiveInfinity, 0.0, 12.0, 0.0, 0.0)]);

    private readonly string _testRoot;
    private readonly IDisposable _overrideScope;
    private readonly RecordingDeviceService _device;
    private readonly TestClock _clock;
    private readonly CommandArbiter _arbiter;
    private readonly KlaTestStore _store;
    private readonly KlaTestRunner _runner;
    private readonly DeviceModel _model;
    private readonly TelemetryParser _parser;
    private int _applied;

    public KlaRunnerSimulatorTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"opentechub-klasim-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
        _overrideScope = AppPaths.OverrideForTests(_testRoot);

        _device = new RecordingDeviceService();
        _clock = new TestClock(DateTimeOffset.UtcNow);
        _arbiter = new CommandArbiter(_device, _clock);
        _store = new KlaTestStore(AppPaths.KlaTestsDirectory);
        _runner = new KlaTestRunner(_device, _arbiter, _store, new KlaAnalysisEngine(), new MemorySettingsService(), _clock);

        _model = new DeviceModel(clock: new AcceleratedClock(), profile: Abiotic, probeDeadTime: TimeSpan.Zero, randomSeed: 11, servoPowerModel: Quiet)
        {
            MotorRpm = 400,
            NitrogenSourceOpen = true,
        };
        _parser = new TelemetryParser(timeProvider: _clock);
    }

    public void Dispose()
    {
        _runner.Dispose();
        _arbiter.Dispose();
        _overrideScope.Dispose();
        try { Directory.Delete(_testRoot, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>One simulated second: apply what the runner sent, tick the rig, echo its frame.</summary>
    private SensorSnapshot Step()
    {
        for (; _applied < _device.Sent.Count; _applied++)
        {
            WireCodec.ApplyCommand(_model, _device.Sent[_applied], out _);
        }

        _model.Tick(1.0);
        _clock.Advance(TimeSpan.FromSeconds(1));
        _parser.Parse(WireCodec.BuildTelemetry(_model));
        var snapshot = _parser.Readings.Snapshot();
        _device.PushTelemetry(snapshot);
        return snapshot;
    }

    private int RunUntil(RunPhase phase, int maxSeconds)
    {
        for (var t = 0; t < maxSeconds; t++)
        {
            if (_runner.Phase == phase)
            {
                return t;
            }
            Step();
        }
        Assert.Fail($"never reached {phase} in {maxSeconds} s; phase {_runner.Phase}, status: {_runner.StatusMessage}");
        return -1;
    }

    [Fact]
    public async Task A_run_on_the_simulated_rig_strips_prestages_switches_and_reaerates()
    {
        _device.PushState(ConnectionState.Connected);
        var first = Step();
        Assert.True(first.FlowmeterOnline, "the simulated flowmeter must be online for the runner to start");

        var doc = _store.CreateTest("Ensaio Simulador", new KlaTestSettings
        {
            DOMinPercent = 15.0,
            DOMaxPercent = 70.0,
            DegassingAgitationRpm = 400.0,
            StabilityDerivativeSpanSeconds = 6.0,
            StabilityDerivativeThresholdPercentPerSecond = 0.05,
            StabilityRequiredSamples = 5,
            PrestageFlowToleranceLpm = 0.2,
            PrestageFlowStableSamples = 5,
            MaxPrestageSeconds = 180.0,
        });
        doc.NitrogenSourceConfirmedUtc = _clock.GetUtcNow();
        var cond = new KlaTestCondition { AgitationRpm = 450, AirflowLpm = 3.0, RequestedReplicates = 1 };
        doc.Conditions.Add(cond);
        _store.SaveConditionsTable(doc.FolderName, doc.Conditions);
        await _runner.StartTestAsync(doc);

        Assert.True(_runner.CurrentDO > 50.0, $"the broth starts aerated: {_runner.CurrentDO:F1}%");
        await _runner.StartRunAsync(cond, 1);

        // Interlock, then N₂ through B — the model must actually strip.
        RunUntil(RunPhase.Deoxygenating, 20);
        Assert.Equal(ObservedGasRoute.VentAndNitrogen, _model.ObservedRoute);
        Assert.True(_model.NitrogenFlowing);
        Assert.Equal(0.0, _model.ReadReactorFlow());

        // The floor triggers the pre-stage: same route, assay setpoint, nothing in the reactor yet.
        var tPrestage = RunUntil(RunPhase.PrestagingAir, 1800);
        Assert.True(_runner.CurrentDO <= 15.0 + 0.5, $"pre-stage at the floor: {_runner.CurrentDO:F1}%");
        Step();
        Assert.Equal(ObservedGasRoute.VentAndNitrogen, _model.ObservedRoute);
        Assert.Equal(3.0, _model.FlowSetpoint);
        Assert.Equal(0.0, _model.ReadReactorFlow());

        // Flow settles on C and the floor holds (the N₂ is still stripping) → one frame to A.
        var tSwitch = RunUntil(RunPhase.SwitchingToReactor, 200);
        Assert.True(tSwitch >= 5, $"the switch waits for the criteria, it is not instant: {tSwitch} s");
        var switchFrame = _device.Sent.Last(j => j.Contains("flowSetpoint"));
        Assert.Equal("""{"flowSetpoint":3.0,"maxFlow":50.0,"valve_1":0,"valve_2":1,"v_Flow":0}""", switchFrame);
        Assert.DoesNotContain(_device.Sent, j => j.Contains("\"valve_1\":1,\"valve_2\":1"));

        RunUntil(RunPhase.Reoxygenating, 10);
        Assert.Equal(ObservedGasRoute.Reactor, _model.ObservedRoute);
        Assert.False(_model.NitrogenFlowing);
        var run = _runner.CurrentRun!;
        // The confirming frame carries the sparger-head dip of the C→A step (the transient the
        // bench measures, plan §8): below the setpoint, but nowhere near zero.
        Assert.InRange(run.SwitchFlowRateLpm!.Value, 2.4, 3.3);
        Assert.InRange(run.SwitchDoPercent!.Value, 0.0, 16.0);
        Assert.True(run.SwitchRelativeSeconds > tPrestage, "t = 0 is the confirmed switch, after the pre-stage began");

        // Air through A re-aerates the broth up to DOMax; the run closes the meter and goes to review.
        var doAtSwitch = _runner.CurrentDO;
        RunUntil(RunPhase.StoppingRun, 3600);
        Assert.True(_runner.CurrentDO >= 70.0, $"DOMax reached: {_runner.CurrentDO:F1}%");
        RunUntil(RunPhase.Reviewing, 10);
        Assert.Equal(ObservedGasRoute.Closed, _model.ObservedRoute);

        // The reoxygenation is monotonic enough to be an assay: every point after the switch is
        // at or above the floor, and the DO rose by more than half the span.
        var points = _runner.CurrentRunPoints;
        var recovery = points.Where(p => p.Phase == RunPhase.Reoxygenating).ToList();
        Assert.True(recovery.Count >= 30, $"reoxygenation points: {recovery.Count}");
        Assert.DoesNotContain(recovery, p => p.Valve1);
        Assert.All(recovery, p => Assert.True(p.Valve2));
        Assert.True(recovery[^1].DORaw - doAtSwitch > 27.0, $"DO rose {doAtSwitch:F1} → {recovery[^1].DORaw:F1}");
        Assert.True(recovery[^1].RelativeSeconds > recovery[0].RelativeSeconds, "time stays monotonic in the file");
    }

    /// <summary>With the source shut at the wall, the runner waits for a floor that never comes and gives up cleanly.</summary>
    [Fact]
    public async Task A_shut_N2_source_never_reaches_the_floor_and_the_degassing_ceiling_aborts()
    {
        _model.NitrogenSourceOpen = false;
        _device.PushState(ConnectionState.Connected);
        Step();

        var doc = _store.CreateTest("Ensaio Fonte Fechada", new KlaTestSettings { DOMinPercent = 15.0, DOMaxPercent = 70.0, MaxDegassingTimeMinutes = 2.0 });
        doc.NitrogenSourceConfirmedUtc = _clock.GetUtcNow();
        var cond = new KlaTestCondition { AgitationRpm = 450, AirflowLpm = 3.0 };
        doc.Conditions.Add(cond);
        await _runner.StartTestAsync(doc);
        await _runner.StartRunAsync(cond, 1);

        RunUntil(RunPhase.Deoxygenating, 20);
        var start = _runner.CurrentDO;
        for (var t = 0; t < 130; t++)
        {
            Step();
        }
        Assert.Equal(RunPhase.Deoxygenating, _runner.Phase);
        Assert.InRange(_runner.CurrentDO, start - 3.0, start + 3.0);

        _runner.CheckWatchdog();
        Assert.Equal(RunPhase.Aborting, _runner.Phase);
        Assert.Equal("""{"flowSetpoint":0.0,"maxFlow":50.0,"valve_1":0,"valve_2":0,"v_Flow":1}""", _device.Sent.Last(j => j.Contains("flowSetpoint")));
    }
}
