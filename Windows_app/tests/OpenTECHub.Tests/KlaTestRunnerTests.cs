using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Persistence;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// The kLa state machine on the A/B/C rig (plan §3.4, Etapa 4): N₂ through B, air pre-staged
/// through C on the same output while the N₂ still strips, and one frame — B/C off, A on —
/// that is <c>t = 0</c>. Wire pairs below are the default wiring: B/C = valve_1, A = valve_2.
/// </summary>
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
        _testRoot = Path.Combine(Path.GetTempPath(), $"opentechub-klarunner-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
        _overrideScope = AppPaths.OverrideForTests(_testRoot);

        _device = new RecordingDeviceService();
        _clock = new TestClock(DateTimeOffset.UtcNow, manualWatchdog: true);
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
            _clock, actuationRelease: new(isIsolatedSimulation: true));
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
    public async Task E7_PhysicalBioticReleaseIsBlockedBeforeAnyActuatorCommand()
    {
        var (doc, condition) = await StartTestAsync("Biotic release gate", 80);
        doc.Protocol = KlaAssayProtocol.Biotic;
        using var physicalRunner = new KlaTestRunner(_device, _arbiter, _store, _analysisEngine, _settings, _clock);
        await physicalRunner.StartTestAsync(doc);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => physicalRunner.StartRunAsync(condition, 1));
        Assert.Contains("validação em bancada", error.Message);
        Assert.Empty(_device.Sent);
    }

    [Theory]
    [InlineData(KlaAssayProtocol.Abiotic)] [InlineData(KlaAssayProtocol.Biotic)]
    public async Task E7_Reopening_saved_history_preserves_raw_analysis_and_never_starts_actuators(KlaAssayProtocol protocol)
    {
        var (doc, condition) = await StartTestAsync("Reopen history", 80);
        doc.Protocol = protocol;
        var run = new KlaTestRun { TestId = doc.TestId, ConditionId = condition.ConditionId, ReplicateNumber = 1,
            AgitationRpm = condition.AgitationRpm, AirflowLpm = condition.AirflowLpm, CurrentPhase = RunPhase.Accepted,
            Definition = KlaRunDefinition.Create(doc, condition, 1) };
        run.FolderName = _store.InitializeRunFolder(doc.FolderName, run);
        var outcome = new KlaRunOutcome { KlaQuality = KlaScientificQuality.Conditional,
            OperatorDecision = KlaOperatorDecision.Accepted,
            Restoration = protocol == KlaAssayProtocol.Biotic ? KlaRestorationState.Confirmed : KlaRestorationState.NotRequired };
        var analysis = new KlaAnalysisRevision { RevisionNumber = 1, KlaPerHour = 40,
            Quality = DecisionQuality.AcceptableWithWarning, Outcome = outcome };
        _store.SaveRunRawData(doc.FolderName, run.FolderName,
            [new(_clock.GetUtcNow(), 0, RunPhase.Reoxygenating, 12345, 40, 3, 3, 450, false, true, false)]);
        _store.SaveRunAnalysis(doc.FolderName, run.FolderName, analysis);
        _store.SaveRunResult(doc.FolderName, run.FolderName, run, analysis);
        doc.Runs.Add(new() { RunId = run.RunId, ConditionId = condition.ConditionId, ReplicateNumber = 1,
            FolderName = run.FolderName, Phase = RunPhase.Accepted, Outcome = outcome,
            Decision = DecisionQuality.AcceptableWithWarning, KlaPerHour = 40, Definition = run.Definition });
        _store.SaveTestManifest(doc); await _store.FlushAsync();
        var rawPath = _store.GetRunRawDataPath(doc.FolderName, run.FolderName);
        var before = File.ReadAllBytes(rawPath);
        var loaded = _store.LoadTest(doc.FolderName)!;
        _runner.PrepareTest(loaded);
        Assert.Equal(RunPhase.Idle, _runner.Phase); Assert.Null(_runner.CurrentRun);
        Assert.Equal(protocol, loaded.EffectiveProtocol); Assert.Single(loaded.Runs);
        Assert.Equal(40, _store.LoadRunAnalysis(doc.FolderName, run.FolderName)!.KlaPerHour);
        Assert.Equal(1, _store.LoadRunAnalysis(doc.FolderName, run.FolderName)!.RevisionNumber);
        Assert.Equal(before, File.ReadAllBytes(rawPath)); Assert.Empty(_device.Sent);
    }

    [Fact]
    public async Task BioticSession_DoesNotFallThroughToAbioticGasCommands()
    {
        var (doc, condition) = await StartTestAsync("Biotic E1", 10);
        doc.Protocol = KlaAssayProtocol.Biotic;
        var commandCount = _device.Sent.Count;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _runner.StartRunAsync(condition, 1));
        Assert.Contains("biótica", error.Message);
        Assert.Equal(commandCount, _device.Sent.Count);
        Assert.Null(_runner.CurrentRun);
    }

    [Theory]
    [InlineData("attempt")] [InlineData("restoration")] [InlineData("exposure")]
    public async Task E5_Queue_limits_block_the_real_runner_before_any_command(string reason)
    {
        var (doc, condition) = await StartTestAsync("Queue E5", 10);
        doc.SequenceLimits = reason == "exposure" ? new() { MaximumRemovalSeconds = 1 }
            : new() { MaximumAttemptsPerReplicate = 1 };
        if (reason != "exposure") doc.Runs.Add(new()
        {
            ConditionId = condition.ConditionId, ReplicateNumber = 1, Phase = RunPhase.Rejected,
            Decision = DecisionQuality.Inconclusive, RemovalSeconds = 20,
            Outcome = new() { OperatorDecision = KlaOperatorDecision.Rejected,
                Restoration = reason == "restoration" ? KlaRestorationState.Failed : KlaRestorationState.Confirmed },
        });
        var commandCount = _device.Sent.Count;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _runner.StartRunAsync(condition, 1));
        Assert.Contains(reason switch { "attempt" => "tentativas", "restoration" => "retomada", _ => "acumulado" }, error.Message);
        Assert.Equal(commandCount, _device.Sent.Count);
        Assert.Null(_runner.CurrentRun);
    }

    [Fact]
    public async Task SingleSession_UsesExistingRunPipelineAndPersistsFrozenDefinition()
    {
        var (doc, condition) = await StartTestAsync("Single E1", 10);
        doc.CaptureMode = KlaCaptureMode.Single;
        condition.RequestedReplicates = 1;
        await _runner.StartRunAsync(condition, 1);
        Assert.Equal(RunPhase.ClosingAllGas, _runner.Phase);
        var run = _runner.CurrentRun!;
        Assert.NotNull(run.Definition);
        Assert.Equal(KlaCaptureMode.Single, run.Definition.CaptureMode);
        Assert.Equal(run.Definition, _store.LoadRunDefinition(doc.FolderName, run.FolderName));
        PushGas(10, 0, false, false, true, 1);
        await _runner.StopRunAndReviewAsync();
        PushGas(10, 0, false, false, true, 2);
        var input = new KlaAnalysisRevision { Quality = DecisionQuality.Acceptable, KlaPerHour = 72 };
        await _runner.AcceptRunAsync(input);
        var historical = Assert.Single(run.AnalysisHistory);
        Assert.NotEmpty(historical.RawDataSha256);
        Assert.Equal(run.LatestAnalysis!.RawDataSha256, historical.RawDataSha256);
        input.KlaPerHour = 999;
        run.LatestAnalysis.KlaPerHour = 144;
        Assert.Equal(72, historical.KlaPerHour);
    }

    /// <summary>Short stability windows so a frame per second gets through the criteria quickly.</summary>
    private static KlaTestSettings FastSettings(double lead = 0.0) => new()
    {
        DOMinPercent = 10.0,
        DOMaxPercent = 85.0,
        StabilityDerivativeSpanSeconds = 2.0,
        StabilityDerivativeThresholdPercentPerSecond = 0.05,
        StabilityRequiredSamples = 3,
        PrestageFlowToleranceLpm = 0.2,
        PrestageFlowStableSamples = 3,
        PrestageFlowStabilityStdDevLpm = 0.0, // band only: the settled criterion is covered by FlowSettlingTests
        AirPrestageLeadPercent = lead,
        MaxPrestageSeconds = 60.0,
    };

    private async Task<(KlaTestDocument Doc, KlaTestCondition Cond)> StartTestAsync(
        string name, double initialDo, KlaTestSettings? settings = null, bool confirmNitrogen = true)
    {
        _device.PushState(ConnectionState.Connected);
        _device.PushTelemetry(new SensorSnapshot
        {
            OxygenCalibrated = initialDo,
            OxygenRaw = initialDo,
            FlowmeterOnline = true,
            FlowCommandPending = false,
        });

        var doc = _store.CreateTest(name, settings ?? FastSettings());
        if (confirmNitrogen)
        {
            doc.NitrogenSourceConfirmedUtc = _clock.GetUtcNow();
        }
        var cond = new KlaTestCondition { AgitationRpm = 450, AirflowLpm = 3.0, RequestedReplicates = 2 };
        doc.Conditions.Add(cond);
        _store.SaveConditionsTable(doc.FolderName, doc.Conditions);

        await _runner.StartTestAsync(doc);
        return (doc, cond);
    }

    private void PushGas(
        double dissolvedOxygen,
        double flow,
        bool valve1,
        bool valve2,
        bool mainClosed,
        int commandId,
        double? measured = null,
        bool advanceClock = true, bool oxygenUpdated = true, bool servo = false)
    {
        if (advanceClock)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        _device.PushTelemetry(new SensorSnapshot
        {
            OxygenCalibrated = dissolvedOxygen,
            OxygenRaw = dissolvedOxygen,
            OxygenUpdated = oxygenUpdated,
            FlowControlEnabled = true,
            HasServoTelemetry = servo, HasServoSample = servo, ServoOnline = servo, ServoRpm = 450,
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

    /// <summary>Frames on the B/C output with the assay airflow: air out of C, N₂ still in through B.</summary>
    private void PushPrestage(double dissolvedOxygen, double measuredFlow, int commandId)
        => PushGas(dissolvedOxygen, flow: 3.0, valve1: true, valve2: false, mainClosed: false, commandId, measuredFlow);

    private string LastFlowCommand() => _device.Sent.Last(j => j.Contains("flowSetpoint"));

    private async Task<KlaTestDocument> StartBioticAsync()
    {
        var (doc, cond) = await StartTestAsync("Biotic E2", 80);
        doc.Protocol = KlaAssayProtocol.Biotic;
        doc.NitrogenIsolationConfirmedUtc = _clock.GetUtcNow();
        doc.ProtocolSettings = new()
        {
            OperatingRange = KlaOperatingRange.CurrentCultivation,
            RemovalTargetDoPercent = 10, ReturnAgitationRpm = 450,
            InitialStabilitySeconds = 2, RecoveryStabilitySeconds = 2,
            CommandConfirmationTimeoutSeconds = 2, OxygenSampleTimeoutSeconds = 4,
            AerationReturn = new() { MaximumGasOffSeconds = 30, MaximumRecoverySeconds = 20 },
        };
        for (var i = 0; i < 3; i++)
        {
            PushGas(80, 3, false, true, false, 1, servo: true);
        }
        await _runner.StartRunAsync(cond, 1);
        return doc;
    }

    [Fact]
    public async Task E2_BioticKeepsFlowOnAndRestoresBeforeReview()
    {
        var doc = await StartBioticAsync();
        Assert.Equal(RunPhase.DivertingAir, _runner.Phase);
        Assert.Contains("\"v_Flow\":0", LastFlowCommand());
        Assert.Contains("\"flowSetpoint\":3", LastFlowCommand());
        PushGas(80, 3, true, false, false, 2, servo: true);
        Assert.Equal(RunPhase.MeasuringConsumption, _runner.Phase);
        PushGas(10, 3, true, false, false, 2, servo: true);
        Assert.Equal(RunPhase.SwitchingToReactor, _runner.Phase);
        PushGas(20, 3, false, true, false, 3, servo: true);
        Assert.Equal(RunPhase.Reoxygenating, _runner.Phase);
        await _runner.StopRunAndReviewAsync();
        Assert.Equal(RunPhase.RestoringCultivation, _runner.Phase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _runner.AcceptRunAsync(new()));
        for (var i = 0; i < 7; i++)
        {
            PushGas(80, 3, false, true, false, 4, servo: true);
        }
        Assert.Equal(RunPhase.Reviewing, _runner.Phase);
        Assert.Equal(KlaRestorationState.Confirmed, _runner.CurrentRun!.Outcome!.Restoration);
        Assert.Equal(CommandOwner.Manual, _arbiter.OwnerOf(ActuatorId.Aeration));
        Assert.True(File.Exists(Path.Combine(_store.RootDirectory, doc.FolderName, "Corridas", _runner.CurrentRun.FolderName, "aquisicao.json")));
        Assert.DoesNotContain(_device.Sent.Where(x => x.Contains("flowSetpoint")), x => x.Contains("\"v_Flow\":1"));
    }

    [Fact]
    public async Task E2_FramesWithoutNewOxygenDoNotAddPointsOrExtendOxygenDeadline()
    {
        await StartBioticAsync();
        PushGas(80, 3, true, false, false, 2, servo: true);
        var count = _runner.CurrentRunPoints.Count;
        for (var i = 0; i < 5; i++)
        {
            PushGas(80, 3, true, false, false, 2, oxygenUpdated: false, servo: true);
        }
        Assert.Equal(count, _runner.CurrentRunPoints.Count);
        _runner.CheckWatchdog();
        Assert.Equal(RunPhase.RestoringCultivation, _runner.Phase);
        Assert.Contains("oxigênio", _runner.StatusMessage);
    }

    [Fact]
    public async Task E2_MissingRestorationEchoFailsExplicitlyAndBlocksNextRun()
    {
        var doc = await StartBioticAsync();
        await _runner.AbortTestAsync("Cancelar");
        Assert.Equal(RunPhase.RestoringCultivation, _runner.Phase);
        _clock.Advance(TimeSpan.FromSeconds(21));
        _runner.CheckWatchdog();
        Assert.Equal(RunPhase.Faulted, _runner.Phase);
        Assert.Equal(KlaRestorationState.Failed, _runner.CurrentRun!.Outcome!.Restoration);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _runner.StartRunAsync(doc.Conditions[0], 2));
    }

    [Fact]
    public async Task E2_LinkLossNeverClaimsRestorationSucceeded()
    {
        await StartBioticAsync();
        _device.PushState(ConnectionState.Disconnected);
        Assert.Equal(RunPhase.Faulted, _runner.Phase);
        Assert.Equal(KlaRestorationState.Failed, _runner.CurrentRun!.Outcome!.Restoration);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task E2_CancelAtEachBioticStageRequestsAirInsteadOfClosingFlow(int stage)
    {
        await StartBioticAsync();
        if (stage >= 1)
        {
            PushGas(80, 3, true, false, false, 2, servo: true);
        }
        if (stage >= 2)
        {
            PushGas(10, 3, true, false, false, 2, servo: true);
        }
        if (stage >= 3)
        {
            PushGas(20, 3, false, true, false, 3, servo: true);
        }
        await _runner.AbortTestAsync("Cancelamento");
        Assert.Equal(RunPhase.RestoringCultivation, _runner.Phase);
        Assert.Contains("\"v_Flow\":0", LastFlowCommand());
        Assert.Contains("\"valve_2\":1", LastFlowCommand());
        for (var i = 0; i < 7; i++)
        {
            PushGas(80, 3, false, true, false, 4, servo: true);
        }
        Assert.Equal(RunPhase.Reviewing, _runner.Phase);
        Assert.Equal(KlaRestorationState.Confirmed, _runner.CurrentRun!.Outcome!.Restoration);
    }

    [Fact]
    public async Task E2_RestorationFailurePersistsAcrossReload()
    {
        var doc = await StartBioticAsync();
        _device.PushState(ConnectionState.Disconnected);
        await _store.FlushAsync();
        var reloaded = _store.LoadTest(doc.FolderName)!;
        Assert.Contains(reloaded.Runs, r => r.Outcome?.Restoration == KlaRestorationState.Failed);
        _runner.PrepareTest(reloaded);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _runner.StartRunAsync(reloaded.Conditions[0], 2));
    }

    [Fact]
    public async Task E2_RecipeOwnershipIsNotTakenOver()
    {
        var (_, cond) = await StartTestAsync("Recipe conflict", 80);
        _arbiter.Claim(CommandOwner.Recipe, [ActuatorId.Aeration], "Recipe");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _runner.StartRunAsync(cond, 1));
        Assert.Equal(CommandOwner.Recipe, _arbiter.OwnerOf(ActuatorId.Aeration));
        Assert.Null(_runner.CurrentRun);
    }

    [Fact]
    public async Task E2_AbioticHonoursEditableRemovalAgitation()
    {
        var (doc, cond) = await StartTestAsync("Editable removal", 80);
        doc.ProtocolSettings = new() { OxygenRemovalAgitationRpm = 123 };
        await _runner.StartRunAsync(cond, 1);
        PushGas(80, 0, false, false, true, 1);
        Assert.Contains(CommandBuilders.MotorSetpoint(123).ToJson(), _device.Sent);
        PushGas(60, 0, true, false, true, 2);
        Assert.Equal(123, _runner.CurrentRunPoints[^1].AgitationSetpoint);
    }

    [Fact]
    public async Task Full_run_strips_with_N2_prestages_air_on_C_and_switches_to_A_in_one_frame()
    {
        var (doc, cond) = await StartTestAsync("Ensaio Runner 1", initialDo: 80.0);
        Assert.Equal(KlaTestStatus.Running, _runner.CurrentTest?.Status);
        Assert.NotNull(doc.GasRig);

        // Initial DO 80% > 10% → the run needs the nitrogen.
        await _runner.StartRunAsync(cond, 1);
        Assert.Equal(CommandOwner.KlaAssay, _arbiter.OwnerOf(ActuatorId.Agitation));
        Assert.Equal(CommandOwner.KlaAssay, _arbiter.OwnerOf(ActuatorId.Aeration));
        Assert.Equal(RunPhase.ClosingAllGas, _runner.Phase);
        Assert.False(_runner.CurrentRun?.SkippedNitrogen);

        // The interlock waits for the acknowledged closed state.
        _device.PushTelemetry(new SensorSnapshot { OxygenCalibrated = 80.0, OxygenRaw = 80.0, FlowmeterOnline = true, FlowValveMain = 1, FlowCommandId = 1, FlowCommandAck = 0 });
        Assert.Equal(RunPhase.ClosingAllGas, _runner.Phase);
        PushGas(80.0, flow: 0.0, valve1: false, valve2: false, mainClosed: true, commandId: 1);
        Assert.Equal(RunPhase.OpeningNitrogen, _runner.Phase);

        // N₂ = B/C output with setpoint 0: valve_1 on the default wiring, nothing on A.
        Assert.Equal("""{"flowSetpoint":0.0,"maxFlow":50.0,"valve_1":1,"valve_2":0,"v_Flow":1}""", LastFlowCommand());
        Assert.Contains("\"motorSetpoint\":700", _device.Sent.Last(j => j.Contains("motorSetpoint")));

        PushGas(75.0, flow: 0.0, valve1: true, valve2: false, mainClosed: true, commandId: 2);
        Assert.Equal(RunPhase.Deoxygenating, _runner.Phase);

        // Stripping: nothing changes on the wire until the floor.
        PushGas(40.0, flow: 0.0, valve1: true, valve2: false, mainClosed: true, commandId: 2);
        Assert.Equal(RunPhase.Deoxygenating, _runner.Phase);

        // Floor reached (8% ≤ 10%): the assay airflow is requested on the SAME route — the air
        // leaves through C, the N₂ keeps flowing through B, nothing reaches the reactor.
        PushGas(8.0, flow: 0.0, valve1: true, valve2: false, mainClosed: true, commandId: 2);
        Assert.Equal(RunPhase.PrestagingAir, _runner.Phase);
        Assert.Equal("""{"flowSetpoint":3.0,"maxFlow":50.0,"valve_1":1,"valve_2":0,"v_Flow":0}""", LastFlowCommand());
        Assert.DoesNotContain(_device.Sent, j => j.Contains("\"valve_2\":1"));

        // Frames before the echo count for nothing — the meter is not on the new setpoint yet.
        PushGas(7.9, flow: 0.0, valve1: true, valve2: false, mainClosed: true, commandId: 2, measured: 0.0);
        Assert.Equal(0, _runner.PrestageFlowStableCount);
        Assert.Equal(0, _runner.StabilityConfirmationCount);

        // Echo confirmed: the start-up pulse is out of band, the DO derivative window is forming.
        PushPrestage(7.8, measuredFlow: 6.4, commandId: 3);
        Assert.Equal(RunPhase.PrestagingAir, _runner.Phase);
        Assert.Equal(0, _runner.PrestageFlowStableCount);

        // Flow settles while the DO still drifts down: the flow count fills, the probe count does not.
        PushPrestage(7.4, measuredFlow: 3.1, commandId: 3);
        PushPrestage(7.0, measuredFlow: 2.9, commandId: 3);
        PushPrestage(6.6, measuredFlow: 3.0, commandId: 3);
        Assert.Equal(3, _runner.PrestageFlowStableCount);
        Assert.Equal(0, _runner.StabilityConfirmationCount);
        Assert.Equal(RunPhase.PrestagingAir, _runner.Phase);

        // A flow excursion restarts the flow count: the band must hold consecutively.
        PushPrestage(6.4, measuredFlow: 3.4, commandId: 3);
        Assert.Equal(0, _runner.PrestageFlowStableCount);

        // Both still at the same time → the switch. Derivative window is 2 s at one frame per
        // second, so the third flat frame is the first probe confirmation; three are required.
        PushPrestage(6.4, measuredFlow: 3.0, commandId: 3);
        PushPrestage(6.4, measuredFlow: 3.05, commandId: 3);
        PushPrestage(6.4, measuredFlow: 2.95, commandId: 3);
        Assert.Equal(3, _runner.PrestageFlowStableCount);
        Assert.Equal(2, _runner.StabilityConfirmationCount);
        Assert.Equal(RunPhase.PrestagingAir, _runner.Phase);
        PushPrestage(6.4, measuredFlow: 3.0, commandId: 3);
        Assert.Equal(RunPhase.SwitchingToReactor, _runner.Phase);

        // One frame: B/C off, A on, same setpoint. The condition's rotation arrives with it.
        Assert.Equal("""{"flowSetpoint":3.0,"maxFlow":50.0,"valve_1":0,"valve_2":1,"v_Flow":0}""", LastFlowCommand());
        Assert.Equal("{\"motorSetpoint\":450}", _device.Sent.Last(j => j.Contains("motorSetpoint")));

        // A stale echo (still B/C) is not the confirmation.
        PushGas(6.4, flow: 3.0, valve1: true, valve2: false, mainClosed: false, commandId: 3);
        Assert.Equal(RunPhase.SwitchingToReactor, _runner.Phase);

        // The confirming frame is t = 0: flow and DO of that frame are the run's provenance.
        PushGas(6.6, flow: 3.0, valve1: false, valve2: true, mainClosed: false, commandId: 4, measured: 2.98);
        Assert.Equal(RunPhase.Reoxygenating, _runner.Phase);
        var run = _runner.CurrentRun!;
        Assert.Equal(2.98, run.SwitchFlowRateLpm);
        Assert.Equal(6.6, run.SwitchDoPercent);
        Assert.NotNull(run.SwitchRelativeSeconds);
        Assert.True(run.SwitchRelativeSeconds > 10.0, $"t=0 is measured from the run start: {run.SwitchRelativeSeconds}");

        // No assay point ever has B/C open; every pre-switch point was recorded.
        var points = _runner.CurrentRunPoints;
        Assert.Contains(points, p => p.Phase == RunPhase.PrestagingAir && p.Valve1);
        Assert.DoesNotContain(points.Where(p => p.Phase == RunPhase.Reoxygenating), p => p.Valve1);

        // DO 88% ≥ 85% → safe stop → review.
        PushGas(88.0, flow: 3.0, valve1: false, valve2: true, mainClosed: false, commandId: 4);
        Assert.Equal(RunPhase.StoppingRun, _runner.Phase);
        Assert.Equal("""{"flowSetpoint":0.0,"maxFlow":50.0,"valve_1":0,"valve_2":0,"v_Flow":1}""", LastFlowCommand());
        PushGas(88.0, flow: 0.0, valve1: false, valve2: false, mainClosed: true, commandId: 5);
        Assert.Equal(RunPhase.Reviewing, _runner.Phase);

        await _runner.AcceptRunAsync(new KlaAnalysisRevision
        {
            CeqPercent = 98.0, TStartSeconds = 0, TEndSeconds = 100, KlaPerHour = 52.4, AnalysisR2 = 0.995, Quality = DecisionQuality.Acceptable,
        });
        Assert.Equal(RunPhase.Accepted, _runner.Phase);
        var summary = Assert.Single(doc.Runs);
        Assert.Equal(2.98, summary.SwitchFlowRateLpm);
        Assert.Equal(6.6, summary.SwitchDoPercent);
        Assert.Equal(run.SwitchRelativeSeconds, summary.SwitchRelativeSeconds);
        Assert.Equal(1, cond.AcceptedReplicates);
    }

    /// <summary>
    /// DO already at the floor: no nitrogen phase, no source confirmation needed — but the air
    /// still goes out through C first, and the switch still waits for BOTH the flow and the DO
    /// to be still. "Low" alone is not "low and stable".
    /// </summary>
    [Fact]
    public async Task Run_starting_at_the_floor_skips_N2_but_still_prestages_on_C_until_flow_and_DO_are_still()
    {
        var (_, cond) = await StartTestAsync("Ensaio Piso", initialDo: 4.0, confirmNitrogen: false);

        await _runner.StartRunAsync(cond, 1);
        Assert.Equal(RunPhase.ClosingAllGas, _runner.Phase);
        Assert.True(_runner.CurrentRun?.SkippedNitrogen);

        PushGas(4.0, flow: 0.0, valve1: false, valve2: false, mainClosed: true, commandId: 1);
        Assert.Equal(RunPhase.PrestagingAir, _runner.Phase);
        Assert.Equal("""{"flowSetpoint":3.0,"maxFlow":50.0,"valve_1":1,"valve_2":0,"v_Flow":0}""", LastFlowCommand());
        Assert.DoesNotContain(_device.Sent, j => j.Contains("\"valve_2\":1"));
        // Degassing rotation is held during the pre-stage: the assay rotation would re-aerate the surface.
        Assert.Contains("\"motorSetpoint\":700", _device.Sent.Last(j => j.Contains("motorSetpoint")));

        // Flow stable immediately, but the DO is still rising (someone had it aerating): no switch.
        PushPrestage(4.0, 3.0, commandId: 2);
        PushPrestage(4.5, 3.0, commandId: 2);
        PushPrestage(5.0, 3.0, commandId: 2);
        PushPrestage(5.5, 3.0, commandId: 2);
        PushPrestage(6.0, 3.0, commandId: 2);
        Assert.True(_runner.PrestageFlowStableCount >= 3);
        Assert.Equal(0, _runner.StabilityConfirmationCount);
        Assert.Equal(RunPhase.PrestagingAir, _runner.Phase);

        // Then flat at the floor → switch.
        PushPrestage(6.0, 3.0, commandId: 2);
        PushPrestage(6.0, 3.0, commandId: 2);
        PushPrestage(6.0, 3.0, commandId: 2);
        PushPrestage(6.0, 3.0, commandId: 2);
        Assert.Equal(RunPhase.SwitchingToReactor, _runner.Phase);
        Assert.Equal("""{"flowSetpoint":3.0,"maxFlow":50.0,"valve_1":0,"valve_2":1,"v_Flow":0}""", LastFlowCommand());
    }

    /// <summary>A lead pre-stages the air before the floor; the switch still waits for the floor itself.</summary>
    [Fact]
    public async Task Lead_prestages_early_but_never_switches_above_DOMin()
    {
        var (_, cond) = await StartTestAsync("Ensaio Lead", initialDo: 60.0, settings: FastSettings(lead: 5.0));
        await _runner.StartRunAsync(cond, 1);
        PushGas(60.0, flow: 0.0, valve1: false, valve2: false, mainClosed: true, commandId: 1);
        PushGas(58.0, flow: 0.0, valve1: true, valve2: false, mainClosed: true, commandId: 2);
        Assert.Equal(RunPhase.Deoxygenating, _runner.Phase);

        // 14% ≤ 10 + 5: pre-stage starts while the N₂ is still stripping.
        PushGas(14.0, flow: 0.0, valve1: true, valve2: false, mainClosed: true, commandId: 2);
        Assert.Equal(RunPhase.PrestagingAir, _runner.Phase);

        // DO flat but above the floor: flow ready, floor not reached → no switch.
        for (var i = 0; i < 6; i++)
        {
            PushPrestage(12.0, 3.0, commandId: 3);
        }
        Assert.True(_runner.PrestageFlowStableCount >= 3);
        Assert.Equal(0, _runner.StabilityConfirmationCount);
        Assert.Equal(RunPhase.PrestagingAir, _runner.Phase);

        // Floor reached and flat → switch.
        for (var i = 0; i < 6; i++)
        {
            PushPrestage(9.5, 3.0, commandId: 3);
        }
        Assert.Equal(RunPhase.SwitchingToReactor, _runner.Phase);
    }

    /// <summary>The pre-stage has a ceiling: an unsettled line is never switched into the vessel.</summary>
    [Fact]
    public async Task Prestage_timeout_stops_the_run_for_review_without_switching()
    {
        var (_, cond) = await StartTestAsync("Ensaio Teto", initialDo: 4.0, confirmNitrogen: false);
        await _runner.StartRunAsync(cond, 1);
        PushGas(4.0, flow: 0.0, valve1: false, valve2: false, mainClosed: true, commandId: 1);
        Assert.Equal(RunPhase.PrestagingAir, _runner.Phase);
        // Flow never enters the band (6.0 against 3.0 ± 0.2) while the DO sits flat at the floor.
        for (var i = 0; i < 65; i++)
        {
            PushPrestage(4.0, 6.0, commandId: 2);
        }
        Assert.Equal(RunPhase.PrestagingAir, _runner.Phase);
        _runner.CheckWatchdog();

        Assert.Equal(RunPhase.StoppingRun, _runner.Phase);
        Assert.DoesNotContain(_device.Sent, j => j.Contains("\"valve_2\":1"));
        Assert.Equal("""{"flowSetpoint":0.0,"maxFlow":50.0,"valve_1":0,"valve_2":0,"v_Flow":1}""", LastFlowCommand());
    }

    /// <summary>An unacknowledged pre-stage frame is a command timeout, like any other frame.</summary>
    [Fact]
    public async Task Prestage_frame_without_echo_times_out_like_any_command()
    {
        var (_, cond) = await StartTestAsync("Ensaio Eco", initialDo: 4.0, confirmNitrogen: false);
        await _runner.StartRunAsync(cond, 1);
        PushGas(4.0, flow: 0.0, valve1: false, valve2: false, mainClosed: true, commandId: 1);
        Assert.Equal(RunPhase.PrestagingAir, _runner.Phase);

        // The meter keeps echoing the closed state (command 1) — the pre-stage frame (2) never lands.
        for (var i = 0; i < 11; i++)
        {
            PushGas(4.0, flow: 0.0, valve1: false, valve2: false, mainClosed: true, commandId: 1);
        }
        _runner.CheckWatchdog();
        Assert.Equal(RunPhase.Aborting, _runner.Phase);
    }

    [Fact]
    public async Task StartRun_refuses_to_open_N2_without_the_source_confirmation()
    {
        var (doc, cond) = await StartTestAsync("Ensaio Sem Confirmacao", initialDo: 60.0, confirmNitrogen: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _runner.StartRunAsync(cond, 1));
        Assert.Contains("N₂", ex.Message, StringComparison.Ordinal);
        Assert.Equal(RunPhase.Idle, _runner.Phase);
        Assert.Equal(CommandOwner.Manual, _arbiter.OwnerOf(ActuatorId.Aeration));

        doc.NitrogenSourceConfirmedUtc = _clock.GetUtcNow();
        await _runner.StartRunAsync(cond, 1);
        Assert.Equal(RunPhase.ClosingAllGas, _runner.Phase);
    }

    [Fact]
    public async Task StartRun_refuses_a_legacy_manifest_and_a_rig_that_changed_since_the_assay_started()
    {
        _device.PushState(ConnectionState.Connected);
        _device.PushTelemetry(new SensorSnapshot { OxygenCalibrated = 60.0, OxygenRaw = 60.0, FlowmeterOnline = true });

        // Recorded before the rig existed: reviewable, never continued.
        var legacy = _store.CreateTest("Ensaio Legado", FastSettings());
        legacy.Status = KlaTestStatus.Interrupted;
        legacy.StartedUtc = _clock.GetUtcNow();
        legacy.NitrogenSourceConfirmedUtc = _clock.GetUtcNow();
        var cond = new KlaTestCondition { AgitationRpm = 300, AirflowLpm = 2.0 };
        legacy.Conditions.Add(cond);
        Assert.True(legacy.IsLegacyRig);
        _runner.PrepareTest(legacy);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _runner.StartRunAsync(cond, 1));
        Assert.Contains("A/B/C", ex.Message, StringComparison.Ordinal);

        // Started on one wiring, continued on another: refused with both spelled out.
        var (doc, cond2) = await StartTestAsync("Ensaio Rig Mudou", initialDo: 60.0);
        Assert.Equal(GasInput.Input2, doc.GasRig!.AirInletInput);
        _settings.Update(s => s with { GasRig = new GasRigSettings { AirInletInput = GasInput.Input1 } });
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _runner.StartRunAsync(cond2, 1));
        Assert.Contains("A na entrada 1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("A na entrada 2", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>On the other wiring the same sequence lands on the other outputs.</summary>
    [Fact]
    public async Task Sequence_follows_the_configured_wiring()
    {
        _settings.Update(s => s with { GasRig = new GasRigSettings { AirInletInput = GasInput.Input1 } });
        var (doc, cond) = await StartTestAsync("Ensaio A na 1", initialDo: 4.0, confirmNitrogen: false);
        Assert.Equal(GasInput.Input1, doc.GasRig!.AirInletInput);

        await _runner.StartRunAsync(cond, 1);
        PushGas(4.0, flow: 0.0, valve1: false, valve2: false, mainClosed: true, commandId: 1);
        Assert.Equal(RunPhase.PrestagingAir, _runner.Phase);
        Assert.Equal("""{"flowSetpoint":3.0,"maxFlow":50.0,"valve_1":0,"valve_2":1,"v_Flow":0}""", LastFlowCommand());

        for (var i = 0; i < 6; i++)
        {
            PushGas(4.0, flow: 3.0, valve1: false, valve2: true, mainClosed: false, commandId: 2);
        }
        Assert.Equal(RunPhase.SwitchingToReactor, _runner.Phase);
        Assert.Equal("""{"flowSetpoint":3.0,"maxFlow":50.0,"valve_1":1,"valve_2":0,"v_Flow":0}""", LastFlowCommand());
    }

    /// <summary>
    /// Stopping while the air is going out of C must still close the flowmeter, otherwise the
    /// operator is left venting into the room with the run marked finished.
    /// </summary>
    [Fact]
    public async Task StopRun_during_prestage_closes_the_flowmeter()
    {
        var (_, cond) = await StartTestAsync("Ensaio Parada C", initialDo: 4.0, confirmNitrogen: false);
        await _runner.StartRunAsync(cond, 1);
        PushGas(4.0, flow: 0.0, valve1: false, valve2: false, mainClosed: true, commandId: 1);
        PushPrestage(4.0, 3.0, commandId: 2);
        Assert.Equal(RunPhase.PrestagingAir, _runner.Phase);

        await _runner.StopRunAndReviewAsync("Parada durante a pré-estabilização");

        Assert.Equal(RunPhase.StoppingRun, _runner.Phase);
        Assert.Equal("""{"flowSetpoint":0.0,"maxFlow":50.0,"valve_1":0,"valve_2":0,"v_Flow":1}""", LastFlowCommand());
        PushGas(4.0, flow: 0.0, valve1: false, valve2: false, mainClosed: true, commandId: 3);
        Assert.Equal(RunPhase.Reviewing, _runner.Phase);
    }

    [Fact]
    public async Task AbortTest_Dispatches_SafeStop_And_Releases_Ownership()
    {
        var (_, cond) = await StartTestAsync("Ensaio Runner 2", initialDo: 50.0);
        await _runner.StartRunAsync(cond, 1);

        Assert.Equal(CommandOwner.KlaAssay, _arbiter.OwnerOf(ActuatorId.Aeration));

        await _runner.AbortTestAsync("Operador cancelou");

        Assert.Equal(RunPhase.Aborting, _runner.Phase);
        Assert.Equal(CommandOwner.KlaAssay, _arbiter.OwnerOf(ActuatorId.Aeration));
        PushGas(50.0, flow: 0.0, valve1: false, valve2: false, mainClosed: true, commandId: 2);
        Assert.Equal(RunPhase.Faulted, _runner.Phase);
        Assert.Equal(KlaTestStatus.Interrupted, _runner.CurrentTest?.Status);
        Assert.Equal(CommandOwner.Manual, _arbiter.OwnerOf(ActuatorId.Aeration));
        Assert.Equal(CommandOwner.Manual, _arbiter.OwnerOf(ActuatorId.Agitation));
    }

    [Fact]
    public void PrepareTest_AllowsHistoricalReview_WithoutChangingCompletedStatus()
    {
        var doc = _store.CreateTest("Ensaio histórico", new KlaTestSettings());
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
