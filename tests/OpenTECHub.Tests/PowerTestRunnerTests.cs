using System.IO;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.PowerTesting;
using OpenTECHub.Simulator;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class PowerTestRunnerTests
{
    [Fact]
    public async Task Preflight_refuses_an_active_process_interlock()
    {
        using var h = new Harness(blockReason: "cascata ativa");
        var doc = h.CreateDocument(FastSettings());
        h.Push(0, 0);

        Assert.False(h.Runner.CanStart(doc, out var reason));
        Assert.Contains("cascata ativa", reason, StringComparison.Ordinal);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Runner.StartTestAsync(doc));
        Assert.Contains("cascata ativa", error.Message, StringComparison.Ordinal);
        Assert.Equal(CommandOwner.Manual, h.Arbiter.OwnerOf(ActuatorId.Agitation));
    }

    [Fact]
    public async Task Preflight_refuses_observed_gas_before_an_ungassed_point()
    {
        using var h = new Harness();
        var doc = h.CreateDocument(FastSettings(), gasMode: PowerGasMode.Both);
        h.Push(0, 0, flowRate: 1.2);

        Assert.False(h.Runner.CanStart(doc, out var reason));
        Assert.Contains("Feche a vazão", reason, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Runner.StartTestAsync(doc));
        Assert.Empty(h.Device.Sent);
    }

    [Fact]
    public async Task Preflight_refuses_non_finite_or_inconsistent_settings_without_sending_commands()
    {
        using var h = new Harness();
        h.Push(0, 0);
        var invalidSettings = new[]
        {
            FastSettings() with { MinRpm = double.NaN },
            FastSettings() with { MaxRpm = double.PositiveInfinity },
            FastSettings() with { StationaritySlopeTolerancePercentPerSecond = double.NaN },
            FastSettings() with { RelativeCiFraction = double.PositiveInfinity },
            FastSettings() with { DefaultStepRpm = 4, MinStepRpm = 5 },
            FastSettings() with { VentAgitationRpm = 14 },
        };

        foreach (var settings in invalidSettings)
        {
            var doc = h.CreateDocument(FastSettings());
            doc.Settings = settings;

            Assert.False(h.Runner.CanStart(doc, out var reason));
            Assert.False(string.IsNullOrWhiteSpace(reason));
            await Assert.ThrowsAsync<InvalidOperationException>(() => h.Runner.StartTestAsync(doc));
        }

        Assert.Empty(h.Device.Sent);
        Assert.Equal(CommandOwner.Manual, h.Arbiter.OwnerOf(ActuatorId.Agitation));
    }

    [Fact]
    public async Task Setting_speed_uses_measured_rpm_and_never_commands_zero()
    {
        using var h = new Harness();
        var doc = h.CreateDocument(FastSettings());
        h.Push(0, 0);

        await h.Runner.StartTestAsync(doc);

        Assert.Equal(PowerRunPhase.SettingSpeed, h.Runner.Phase);
        Assert.Contains(h.Device.Sent, json => json == "{\"servoPollMs\":250}");
        Assert.Contains(h.Device.Sent, json => json == "{\"motorSetpoint\":300}");
        h.Push(250, 2.0);
        h.Push(299, 2.0);
        Assert.Equal(PowerRunPhase.SettingSpeed, h.Runner.Phase);
        h.Push(301, 2.0);
        Assert.Equal(PowerRunPhase.SettlingTorque, h.Runner.Phase);
        Assert.DoesNotContain(h.Device.Sent, json => json.Contains("\"motorSetpoint\":0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_servo_sample_discards_capture_and_requires_explicit_resume()
    {
        using var h = new Harness();
        var doc = h.CreateDocument(FastSettings());
        h.Push(0, 0);
        await h.Runner.StartTestAsync(doc);
        h.DriveToAccumulating();
        h.Push(300, 2.0);
        h.Push(300, 2.1);
        Assert.Contains(h.Runner.CurrentRunPoints, point => point.Counted);

        h.Device.Push(new SensorSnapshot
        {
            HasServoTelemetry = true,
            HasServoSample = false,
            ServoOnline = false,
            ServoCommEnabled = true,
        });

        Assert.True(h.Runner.IsPausedForMeasurement);
        Assert.Equal(0, h.Runner.CurrentTorqueCi95Percent);
        h.Push(300, 2.0);
        Assert.True(h.Runner.IsPausedForMeasurement);
        await h.Runner.ResumeAfterMeasurementAsync();
        Assert.Equal(PowerRunPhase.SettingSpeed, h.Runner.Phase);
    }

    [Fact]
    public async Task Operator_pause_discards_partial_window_and_resume_restarts_both_gates()
    {
        using var h = new Harness();
        var doc = h.CreateDocument(FastSettings());
        h.Push(0, 0);
        await h.Runner.StartTestAsync(doc);
        h.DriveToAccumulating();
        h.Push(300, 2.0);
        h.Push(300, 2.2);
        Assert.True(h.Runner.CurrentTorqueCi95Percent > 0);

        await h.Runner.PauseAsync();

        Assert.True(h.Runner.IsPausedByOperator);
        Assert.Equal(0, h.Runner.CurrentTorqueCi95Percent);
        Assert.Equal(CommandOwner.PowerAssay, h.Arbiter.OwnerOf(ActuatorId.Agitation));
        h.Push(300, 4.0);
        Assert.Equal(PowerRunPhase.PausedByOperator, h.Runner.Phase);
        Assert.False(h.Runner.CurrentRunPoints[^1].Counted);

        await h.Runner.ResumeAsync();

        Assert.Equal(PowerRunPhase.SettingSpeed, h.Runner.Phase);
        Assert.Contains(h.Device.Sent, json => json == "{\"motorSetpoint\":300}");
    }

    [Fact]
    public async Task Skip_current_condition_parks_motor_and_persists_a_skipped_rejected_run()
    {
        using var h = new Harness();
        var doc = h.CreateDocument(FastSettings());
        h.Push(0, 0);
        await h.Runner.StartTestAsync(doc);

        await h.Runner.SkipCurrentConditionAsync();

        Assert.Equal(PowerRunPhase.Rejected, h.Runner.Phase);
        Assert.Equal(PowerConditionStatus.Skipped, doc.Conditions[0].Status);
        Assert.Equal(CommandOwner.Manual, h.Arbiter.OwnerOf(ActuatorId.Agitation));
        Assert.Contains(h.Device.Sent, json => json == "{\"motorSetpoint\":15}");
        Assert.Contains(doc.Runs, run => run.Phase == PowerRunPhase.Rejected && run.StopReason == PowerStopReason.Aborted);
        var reloaded = h.Store.LoadTest(doc.FolderName);
        Assert.NotNull(reloaded);
        Assert.Equal(PowerConditionStatus.Skipped, reloaded.Conditions[0].Status);
    }

    [Fact]
    public async Task Timeout_recaptures_in_place_then_marks_not_converged()
    {
        using var h = new Harness();
        var settings = FastSettings() with
        {
            MinSamples = 100,
            RelativeCiFraction = 0,
            CiFloorSigmaMultiple = 0,
            MaxCaptureSeconds = 3,
            MaxTries = 2,
        };
        var doc = h.CreateDocument(settings);
        h.Push(0, 0);
        await h.Runner.StartTestAsync(doc);
        h.DriveToAccumulating();

        for (var i = 0; i < 40 && !h.Runner.IsInReview; i++)
        {
            h.Push(300, i % 2 == 0 ? 1.0 : 3.0);
        }

        Assert.True(h.Runner.IsInReview);
        Assert.Equal(2, h.Runner.CurrentRun!.Tries);
        Assert.Equal(PowerStopReason.NotConverged, h.Runner.CurrentRun.StopReason);
        Assert.Contains(h.Runner.CurrentRunPoints, point => point.Attempt == 1 && point.Counted);
        Assert.Contains(h.Runner.CurrentRunPoints, point => point.Attempt == 2 && point.Counted);
    }

    [Fact]
    public async Task Manual_energy_hold_does_not_advance_until_the_reading_is_submitted()
    {
        using var h = new Harness();
        var doc = h.CreateDocument(FastSettings() with { ManualEnergyCaptureEnabled = true });
        h.Push(0, 0);
        await h.Runner.StartTestAsync(doc);
        h.DriveUntil(PowerRunPhase.HoldingForManualEnergy);

        var runId = h.Runner.CurrentRun!.RunId;
        for (var i = 0; i < 5; i++)
        {
            h.Push(300, 2.0);
        }
        Assert.Equal(PowerRunPhase.HoldingForManualEnergy, h.Runner.Phase);
        Assert.Equal(runId, h.Runner.CurrentRun.RunId);

        await h.Runner.SubmitManualEnergyAsync(42.5, "Wattímetro", "leitura estável");

        Assert.True(h.Runner.IsInReview);
        Assert.Equal(42.5, h.Runner.CurrentRun.ManualElec!.PowerElectricalW);
        Assert.Equal("Wattímetro", h.Runner.CurrentRun.ManualElec.Instrument);
    }

    [Fact]
    public async Task Both_condition_is_phase1_P0_and_sends_no_gas_command()
    {
        using var h = new Harness();
        var doc = h.CreateDocument(FastSettings(), gasMode: PowerGasMode.Both);
        h.Push(0, 0);

        await h.Runner.StartTestAsync(doc);

        Assert.Equal(PowerGasMode.Ungassed, h.Runner.CurrentRun!.GasMode);
        Assert.Null(h.Runner.CurrentRun.GasFlowLpm);
        Assert.DoesNotContain(h.Device.Sent, json => json.Contains("flowSetpoint", StringComparison.Ordinal));
        Assert.Equal(CommandOwner.PowerAssay, h.Arbiter.OwnerOf(ActuatorId.Agitation));
        Assert.Equal(CommandOwner.Manual, h.Arbiter.OwnerOf(ActuatorId.Aeration));
        Assert.Equal("test-hub", doc.HubFirmwareVersion);
        Assert.Equal(9, doc.HubProtocolVersion);
    }

    [Fact]
    public async Task Independent_replicates_return_to_minimum_before_reapproaching()
    {
        using var h = new Harness();
        var doc = h.CreateDocument(FastSettings() with { AutoAcceptRuns = true }, replicates: 2);
        h.Push(0, 0);
        await h.Runner.StartTestAsync(doc);
        h.DriveUntil(PowerRunPhase.PreparingNextRun);

        h.Push(15, 0.5);
        h.Push(15, 0.5);
        Assert.Equal(PowerRunPhase.SettingSpeed, h.Runner.Phase);
        h.DriveUntil(PowerRunPhase.Completed);

        Assert.Equal(PowerTestStatus.Completed, doc.Status);
        Assert.Equal(2, doc.Runs.Count(run => run.Phase == PowerRunPhase.Accepted));
        Assert.Equal(2, doc.Conditions[0].AcceptedReplicates);
        Assert.Equal(PowerRunPhase.Accepted, h.Runner.CurrentRun!.CurrentPhase);
        var motorCommands = h.Device.Sent.Where(json => json.Contains("motorSetpoint", StringComparison.Ordinal)).ToArray();
        Assert.Equal(
            ["{\"motorSetpoint\":300}", "{\"motorSetpoint\":15}", "{\"motorSetpoint\":300}", "{\"motorSetpoint\":15}"],
            motorCommands);
        Assert.Equal(CommandOwner.Manual, h.Arbiter.OwnerOf(ActuatorId.Agitation));
    }

    [Fact]
    public async Task A_second_run_cannot_replace_an_active_capture()
    {
        using var h = new Harness();
        var doc = h.CreateDocument(FastSettings());
        h.Push(0, 0);
        await h.Runner.StartTestAsync(doc);
        var activeRunId = h.Runner.CurrentRun!.RunId;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Runner.StartRunAsync(doc.Conditions[0], 2));

        Assert.Contains("já existe", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(activeRunId, h.Runner.CurrentRun!.RunId);
    }

    [Fact]
    public async Task Rejected_run_parks_and_releases_before_a_repeat()
    {
        using var h = new Harness();
        var doc = h.CreateDocument(FastSettings());
        h.Push(0, 0);
        await h.Runner.StartTestAsync(doc);
        h.DriveUntil(PowerRunPhase.Reviewing);

        await h.Runner.RejectRunAsync("instável");

        Assert.Equal(CommandOwner.Manual, h.Arbiter.OwnerOf(ActuatorId.Agitation));
        Assert.Equal(PowerRunPhase.Rejected, h.Runner.CurrentRun!.CurrentPhase);
        Assert.Contains("{\"motorSetpoint\":15}", h.Device.Sent);

        var rejectedRunId = h.Runner.CurrentRun.RunId;
        await h.Runner.RepeatRunAsync();
        Assert.NotEqual(rejectedRunId, h.Runner.CurrentRun!.RunId);
        Assert.Equal(PowerRunPhase.SettingSpeed, h.Runner.Phase);
    }

    [Fact]
    public async Task Accepted_run_is_not_rewritten_when_the_remaining_test_is_aborted()
    {
        using var h = new Harness();
        var doc = h.CreateDocument(FastSettings());
        h.Push(0, 0);
        await h.Runner.StartTestAsync(doc);
        h.DriveUntil(PowerRunPhase.Reviewing);
        await h.Runner.AcceptRunAsync();

        await h.Runner.AbortTestAsync("encerramento sem completar o ensaio");

        Assert.Equal(PowerTestStatus.Interrupted, doc.Status);
        Assert.Equal(PowerRunPhase.Accepted, h.Runner.CurrentRun!.CurrentPhase);
        Assert.Equal(PowerRunPhase.Accepted, Assert.Single(doc.Runs).Phase);
    }

    [Fact]
    public async Task Abort_parks_at_wire_minimum_restores_polling_and_releases_ownership()
    {
        using var h = new Harness();
        var doc = h.CreateDocument(FastSettings());
        h.Push(0, 0);
        await h.Runner.StartTestAsync(doc);

        await h.Runner.AbortTestAsync("operador");

        Assert.Equal(PowerRunPhase.Faulted, h.Runner.Phase);
        Assert.Equal(PowerTestStatus.Interrupted, doc.Status);
        Assert.Equal(CommandOwner.Manual, h.Arbiter.OwnerOf(ActuatorId.Agitation));
        Assert.Contains("{\"motorSetpoint\":15}", h.Device.Sent);
        Assert.Contains("{\"servoPollMs\":1000}", h.Device.Sent);
        Assert.DoesNotContain(h.Device.Sent, json => json.Contains("\"motorSetpoint\":0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Simulator_runs_an_absolute_point_end_to_end_through_wire_parser_and_store()
    {
        using var h = new Harness(useSimulator: true);
        var settings = FastSettings() with
        {
            AutoAcceptRuns = true,
            MinSamples = 20,
            RelativeCiFraction = 0.05,
            MaxCaptureSeconds = 120,
        };
        var geometry = new PowerGeometry
        {
            Impellers =
            [
                new Impeller { StageIndex = 0, Type = ImpellerType.RushtonFlatBlade, DiameterM = 0.060 },
                new Impeller { StageIndex = 1, Type = ImpellerType.RushtonFlatBlade, DiameterM = 0.060 },
            ],
            VesselDiameterM = 0.20,
            LiquidVolumeM3 = 0.010,
        };
        var doc = h.CreateDocument(settings, geometry: geometry);
        const double targetRpm = 300;
        var tareTorquePercent = 1.25 + 0.00122 * targetRpm;
        var tarePowerW = tareTorquePercent / 100.0 * 1.27 * PowerCalc.AngularVelocity(targetRpm);
        doc.Calibration = new TorqueCalibration { Scale = 1, MotorRatedTorqueNm = 1.27 };
        doc.Tare = new TareCurve
        {
            ImpellerSetHash = PowerTestFileContracts.ComputeImpellerSetHash(geometry),
            Points = [new TarePoint(targetRpm, tarePowerW, 0.62)],
        };
        doc.RelativeMode = false;
        h.Store.SaveCalibration(doc.FolderName, doc.Calibration);
        h.Store.SaveTare(doc.FolderName, doc.Tare);
        h.Store.SaveTestManifest(doc);
        h.AdvanceSimulator(0.5);

        await h.Runner.StartTestAsync(doc);
        for (var i = 0; i < 800 && h.Runner.Phase != PowerRunPhase.Completed; i++)
        {
            h.AdvanceSimulator(0.5);
        }

        Assert.Equal(PowerRunPhase.Completed, h.Runner.Phase);
        var run = Assert.Single(doc.Runs);
        Assert.Equal(PowerRunPhase.Accepted, run.Phase);
        Assert.False(run.IsRelative);
        Assert.NotNull(run.Analysis);
        Assert.InRange(run.Analysis!.AssemblyPowerNumber, 8.0, 12.0);
        Assert.InRange(run.Analysis.AssemblyReynoldsNumber, 15_000, 25_000);
        Assert.Equal(2, run.Analysis.Stages.Count);
        Assert.All(run.Analysis.Stages, stage => Assert.InRange(stage.PowerNumber, 4.0, 6.0));

        var rawPath = h.Store.GetRunRawDataPath(doc.FolderName, run.FolderName);
        Assert.True(File.Exists(rawPath));
        Assert.Equal(h.Runner.CurrentRun!.RawDataSha256, PowerTestFileContracts.ComputeFileSha256(rawPath));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(rawPath)!, PowerTestFileContracts.RunResultFileName)));
        var summary = File.ReadAllText(Path.Combine(h.Store.RootDirectory, doc.FolderName, PowerTestFileContracts.ResultsSummaryFileName));
        Assert.Contains("MeanAssemblyNp", summary, StringComparison.Ordinal);
        Assert.Contains(run.Analysis.AssemblyPowerNumber.ToString("G17", System.Globalization.CultureInfo.InvariantCulture), summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Capture_controller_uses_tare_sigma_instead_of_currently_observed_silence()
    {
        var settings = FastSettings() with
        {
            MinSamples = 10,
            RelativeCiFraction = 0,
            CiFloorSigmaMultiple = 1,
        };
        var capture = new PowerCaptureController(settings, tareSigmaTorquePercent: 0.60);

        for (var i = 0; i < 20 && !capture.IsDone; i++)
        {
            capture.Add(i * 0.5, i % 2 == 0 ? -0.02 : 0.02, 300);
        }

        Assert.Equal(CaptureState.Converged, capture.State);
        Assert.Equal(0.60 / Math.Sqrt(10), capture.CurrentTargetTorqueCi95Percent, 8);
    }

    [Fact]
    public void Capture_controller_times_out_even_when_stationarity_never_arrives()
    {
        var settings = FastSettings() with
        {
            StationaritySlopeTolerancePercentPerSecond = 0.001,
            MaxCaptureSeconds = 2,
        };
        var capture = new PowerCaptureController(settings);
        for (var i = 0; i < 10 && !capture.IsDone; i++)
        {
            capture.Add(i * 0.5, i * 2.0, 300);
        }

        Assert.Equal(CaptureState.TimedOut, capture.State);
        Assert.Equal(0, capture.SampleCount);
    }

    [Fact]
    public async Task Gassed_condition_with_vent_stabilization_routes_relief_and_switches_to_reactor()
    {
        using var h = new Harness();
        var settings = FastSettings() with
        {
            VentStabilizationEnabled = true,
            SelectedVentValve = PowerVentValve.Valve2,
            VentAgitationRpm = 15.0,
            VentFlowToleranceLpm = 0.2,
            VentFlowStableSamples = 2,
            MaxVentStabilizationSeconds = 30.0,
        };
        var doc = h.CreateDocument(settings, gasMode: PowerGasMode.Gassed);
        doc.Conditions[0].GasFlowLpm = 4.0;
        h.PushGas(0, 0, flowRate: 0.0, flowSetpoint: 0.0, valve1: 0, valve2: 0, valveMain: 1, commandId: 0, commandAck: 0, flowmeterOnline: true);

        await h.Runner.StartTestAsync(doc);

        Assert.Equal(PowerRunPhase.VentStabilizing, h.Runner.Phase);
        Assert.Equal(CommandOwner.PowerAssay, h.Arbiter.OwnerOf(ActuatorId.Agitation));
        Assert.Equal(CommandOwner.PowerAssay, h.Arbiter.OwnerOf(ActuatorId.Aeration));
        Assert.Contains(h.Device.Sent, json => json.Contains("\"motorSetpoint\":15"));
        Assert.Contains(h.Device.Sent, json => json.Contains("\"valve_2\":1"));

        // Push telemetry confirming relief state with flow stabilizing
        h.PushGas(15, 0.5, flowRate: 4.1, flowSetpoint: 4.0, valve1: 0, valve2: 1, valveMain: 0, commandId: 1, commandAck: 1);
        Assert.Equal(PowerRunPhase.VentStabilizing, h.Runner.Phase);

        // 2nd stable sample -> triggers switch to reactor (OpeningGas)
        h.PushGas(15, 0.5, flowRate: 4.05, flowSetpoint: 4.0, valve1: 0, valve2: 1, valveMain: 0, commandId: 1, commandAck: 1);
        Assert.Equal(PowerRunPhase.OpeningGas, h.Runner.Phase);
        Assert.Contains(h.Device.Sent, json => json.Contains("\"valve_2\":0"));

        // Confirm reactor gas state (both valves 0, main 0)
        h.PushGas(15, 0.5, flowRate: 4.0, flowSetpoint: 4.0, valve1: 0, valve2: 0, valveMain: 0, commandId: 2, commandAck: 2);
        Assert.Equal(PowerRunPhase.SettingSpeed, h.Runner.Phase);
        Assert.Contains(h.Device.Sent, json => json.Contains("\"motorSetpoint\":300"));

        // Confirm speed
        h.PushGas(300, 2.0, flowRate: 4.0, flowSetpoint: 4.0, valve1: 0, valve2: 0, valveMain: 0, commandId: 2, commandAck: 2);
        h.PushGas(300, 2.0, flowRate: 4.0, flowSetpoint: 4.0, valve1: 0, valve2: 0, valveMain: 0, commandId: 2, commandAck: 2);
        Assert.Equal(PowerRunPhase.SettlingTorque, h.Runner.Phase);

        // Accumulate and finish
        while (h.Runner.Phase is PowerRunPhase.SettlingTorque or PowerRunPhase.AccumulatingToTarget)
        {
            h.PushGas(300, 2.0, flowRate: 4.0, flowSetpoint: 4.0, valve1: 0, valve2: 0, valveMain: 0, commandId: 2, commandAck: 2);
        }

        Assert.Equal(PowerRunPhase.Reviewing, h.Runner.Phase);
        Assert.True(h.Runner.CurrentRun!.UsedVentStabilization);
        Assert.Equal(PowerGasMode.Gassed, h.Runner.CurrentRun.GasMode);
        Assert.NotNull(h.Runner.CurrentRun.GasFlowNumber);
        Assert.NotNull(h.Runner.CurrentRun.FroudeNumber);
    }

    [Fact]
    public async Task Vent_stabilization_times_out_when_flow_never_settles()
    {
        using var h = new Harness();
        var settings = FastSettings() with
        {
            VentStabilizationEnabled = true,
            SelectedVentValve = PowerVentValve.Valve1,
            VentAgitationRpm = 15.0,
            VentFlowToleranceLpm = 0.1,
            VentFlowStableSamples = 5,
            MaxVentStabilizationSeconds = 2.0,
        };
        var doc = h.CreateDocument(settings, gasMode: PowerGasMode.Gassed);
        doc.Conditions[0].GasFlowLpm = 5.0;
        h.PushGas(0, 0, flowRate: 0.0, flowSetpoint: 0.0, valve1: 0, valve2: 0, valveMain: 1, commandId: 0, commandAck: 0, flowmeterOnline: true);

        await h.Runner.StartTestAsync(doc);
        Assert.Equal(PowerRunPhase.VentStabilizing, h.Runner.Phase);

        // Advance past 2s with flow out of tolerance
        for (var i = 0; i < 10; i++)
        {
            h.PushGas(15, 0.5, flowRate: 1.0, flowSetpoint: 5.0, valve1: 1, valve2: 0, valveMain: 0, commandId: 1, commandAck: 1);
        }

        Assert.Equal(PowerRunPhase.Reviewing, h.Runner.Phase);
        Assert.Equal(PowerStopReason.Tmax, h.Runner.CurrentRun!.StopReason);
    }

    [Fact]
    public async Task Both_condition_sequences_P0_ungassed_first_then_PG_gassed_with_paired_reference()
    {
        using var h = new Harness();
        var settings = FastSettings() with
        {
            VentStabilizationEnabled = false,
            AutoAcceptRuns = true,
        };
        var doc = h.CreateDocument(settings, gasMode: PowerGasMode.Both);
        doc.Conditions[0].GasFlowLpm = 3.0;
        h.PushGas(0, 0, flowRate: 0.0, flowSetpoint: 0.0, valve1: 0, valve2: 0, valveMain: 1, commandId: 0, commandAck: 0, flowmeterOnline: true);

        await h.Runner.StartTestAsync(doc);

        // Subphase 1: Ungassed
        Assert.Equal(PowerRunPhase.SettingSpeed, h.Runner.Phase);
        Assert.Equal(PowerGasMode.Ungassed, h.Runner.CurrentRun!.GasMode);

        // Speed confirmation for Subphase 1
        h.PushGas(300, 3.0, flowRate: 0.0, flowSetpoint: 0.0, valve1: 0, valve2: 0, valveMain: 1, commandId: 0, commandAck: 0);
        h.PushGas(300, 3.0, flowRate: 0.0, flowSetpoint: 0.0, valve1: 0, valve2: 0, valveMain: 1, commandId: 0, commandAck: 0);
        Assert.Equal(PowerRunPhase.SettlingTorque, h.Runner.Phase);

        // Drive Subphase 1 to completion
        while (h.Runner.Phase != PowerRunPhase.OpeningGas)
        {
            h.PushGas(300, 3.0, flowRate: 0.0, flowSetpoint: 0.0, valve1: 0, valve2: 0, valveMain: 1, commandId: 0, commandAck: 0);
        }

        // Subphase 1 completed, immediately started Subphase 2 (OpeningGas)
        Assert.Equal(PowerRunPhase.OpeningGas, h.Runner.Phase);
        Assert.Equal(PowerGasMode.Gassed, h.Runner.CurrentRun!.GasMode);
        Assert.Equal(P0Provenance.MeasuredUngassed, h.Runner.CurrentRun.P0Provenance);
        Assert.NotNull(h.Runner.CurrentRun.ReferenceP0W);
        var p0Captured = h.Runner.CurrentRun.ReferenceP0W.Value;
        Assert.True(p0Captured > 0);

        // Confirm reactor gas for Subphase 2 (commandId 1 because first flow command was dispatched in Subphase 2)
        h.PushGas(300, 2.0, flowRate: 3.0, flowSetpoint: 3.0, valve1: 0, valve2: 0, valveMain: 0, commandId: 1, commandAck: 1);
        Assert.Equal(PowerRunPhase.SettingSpeed, h.Runner.Phase);

        // Speed confirmation for Subphase 2
        h.PushGas(300, 2.0, flowRate: 3.0, flowSetpoint: 3.0, valve1: 0, valve2: 0, valveMain: 0, commandId: 1, commandAck: 1);
        h.PushGas(300, 2.0, flowRate: 3.0, flowSetpoint: 3.0, valve1: 0, valve2: 0, valveMain: 0, commandId: 1, commandAck: 1);
        Assert.Equal(PowerRunPhase.SettlingTorque, h.Runner.Phase);

        // Drive Subphase 2 to completion
        while (h.Runner.Phase is PowerRunPhase.SettlingTorque or PowerRunPhase.AccumulatingToTarget)
        {
            h.PushGas(300, 2.0, flowRate: 3.0, flowSetpoint: 3.0, valve1: 0, valve2: 0, valveMain: 0, commandId: 1, commandAck: 1);
        }

        // Both runs completed!
        Assert.True(h.Runner.CurrentRun.PowerRatio is > 0 and < 1.0);
        Assert.Equal(P0Provenance.MeasuredUngassed, h.Runner.CurrentRun.P0Provenance);
    }

    [Fact]
    public async Task Gassed_assay_abort_parks_motor_at_minimum_and_zeros_flow_closing_valves()
    {
        using var h = new Harness();
        var settings = FastSettings() with { VentStabilizationEnabled = false };
        var doc = h.CreateDocument(settings, gasMode: PowerGasMode.Gassed);
        doc.Conditions[0].GasFlowLpm = 4.0;
        h.PushGas(0, 0, flowRate: 0.0, flowSetpoint: 0.0, valve1: 0, valve2: 0, valveMain: 1, commandId: 0, commandAck: 0, flowmeterOnline: true);

        await h.Runner.StartTestAsync(doc);
        Assert.Equal(PowerRunPhase.OpeningGas, h.Runner.Phase);

        await h.Runner.AbortTestAsync("Parada de emergência");

        Assert.Equal(PowerRunPhase.Faulted, h.Runner.Phase);
        Assert.Contains(h.Device.Sent, json => json.Contains("\"motorSetpoint\":15"));
        Assert.Contains(h.Device.Sent, json => json.Contains("\"flowSetpoint\":0") && json.Contains("\"v_Flow\":1"));
        Assert.Equal(CommandOwner.Manual, h.Arbiter.OwnerOf(ActuatorId.Agitation));
        Assert.Equal(CommandOwner.Manual, h.Arbiter.OwnerOf(ActuatorId.Aeration));
    }

    [Fact]
    public async Task Flowmeter_disconnect_or_offline_during_gassed_capture_pauses_for_measurement()
    {
        using var h = new Harness();
        var settings = FastSettings() with { VentStabilizationEnabled = false };
        var doc = h.CreateDocument(settings, gasMode: PowerGasMode.Gassed);
        doc.Conditions[0].GasFlowLpm = 4.0;
        h.PushGas(0, 0, flowRate: 0.0, flowSetpoint: 0.0, valve1: 0, valve2: 0, valveMain: 1, commandId: 0, commandAck: 0, flowmeterOnline: true);

        await h.Runner.StartTestAsync(doc);
        // Confirm gas state
        h.PushGas(15, 0.5, flowRate: 4.0, flowSetpoint: 4.0, valve1: 0, valve2: 0, valveMain: 0, commandId: 1, commandAck: 1);
        Assert.Equal(PowerRunPhase.SettingSpeed, h.Runner.Phase);

        // Confirm speed
        h.PushGas(300, 2.0, flowRate: 4.0, flowSetpoint: 4.0, valve1: 0, valve2: 0, valveMain: 0, commandId: 1, commandAck: 1);
        h.PushGas(300, 2.0, flowRate: 4.0, flowSetpoint: 4.0, valve1: 0, valve2: 0, valveMain: 0, commandId: 1, commandAck: 1);
        Assert.Equal(PowerRunPhase.SettlingTorque, h.Runner.Phase);

        // Flowmeter drops offline!
        h.PushGas(300, 2.0, flowRate: 4.0, flowSetpoint: 4.0, valve1: 0, valve2: 0, valveMain: 0, commandId: 1, commandAck: 1, flowmeterOnline: false);

        Assert.True(h.Runner.IsPausedForMeasurement);
    }

    [Fact]
    public async Task End_to_end_gassed_assay_with_simulator_both_with_and_without_relief_stabilization()
    {
        var powerOptions = new ServoPowerModelOptions
        {
            SpeedTimeConstantSeconds = 0.02,
            TorqueTimeConstantSeconds = 0.02,
            LiquidDensityKgM3 = 1000.0,
            MotorRatedTorqueNm = 1.27,
            TorqueNoiseCurve = [new(0.0, 0.0)],
            SimulateFloodingKnee = true,
            GassedLiquidPowerRatio = 0.70,
            VesselDiameterM = 0.190,
            Impellers = [new("Rushton", 0.060, 5.0, 0.4, 0.001)],
        };
        var sim = new DeviceModel(randomSeed: 20260904, servoPowerModel: powerOptions);

        using var h = new Harness(useSimulator: false, customSimulator: sim);
        var settings = FastSettings() with
        {
            AutoAcceptRuns = true,
            MinSamples = 4,
            RelativeCiFraction = 0.50,
            VentStabilizationEnabled = true,
            VentFlowStableSamples = 2,
            VentFlowToleranceLpm = 0.3,
            VentAgitationRpm = 15.0,
            MaxVentStabilizationSeconds = 15.0,
        };

        var doc = h.CreateDocumentWithConditions(
            settings,
            [
                // Condition 0: Ungassed P0 reference at 300 rpm
                new PowerCondition
                {
                    OrderIndex = 0,
                    AgitationRpm = 300,
                    GasMode = PowerGasMode.Ungassed,
                    RequestedReplicates = 1,
                },
                // Condition 1: Gassed with relief stabilization at 300 rpm, 5 L/min
                new PowerCondition
                {
                    OrderIndex = 1,
                    AgitationRpm = 300,
                    GasMode = PowerGasMode.Gassed,
                    GasFlowLpm = 5.0,
                    RequestedReplicates = 1,
                },
            ]);

        h.AdvanceSimulator(0.5);
        await h.Runner.StartTestAsync(doc);

        var sawVentStabilizing = false;
        var sawSettingSpeed = false;
        var sawAccumulating = false;

        for (var tick = 0; tick < 1000 && h.Runner.Phase != PowerRunPhase.Completed; tick++)
        {
            if (h.Runner.Phase == PowerRunPhase.VentStabilizing) sawVentStabilizing = true;
            if (h.Runner.Phase == PowerRunPhase.SettingSpeed) sawSettingSpeed = true;
            if (h.Runner.Phase == PowerRunPhase.AccumulatingToTarget) sawAccumulating = true;

            h.AdvanceSimulator(0.2);
        }

        Assert.Equal(PowerRunPhase.Completed, h.Runner.Phase);
        Assert.True(sawVentStabilizing, "Expected to pass through VentStabilizing phase");
        Assert.True(sawSettingSpeed, "Expected to pass through SettingSpeed phase");
        Assert.True(sawAccumulating, "Expected to pass through AccumulatingToTarget phase");

        // Verify runs
        Assert.Equal(2, doc.Runs.Count);
        var p0Run = doc.Runs[0];
        var gassedRun = doc.Runs[1];

        Assert.Equal(PowerRunPhase.Accepted, p0Run.Phase);
        Assert.Equal(PowerGasMode.Ungassed, p0Run.GasMode);
        Assert.True(p0Run.NetPowerW > 0);

        Assert.Equal(PowerRunPhase.Accepted, gassedRun.Phase);
        Assert.Equal(PowerGasMode.Gassed, gassedRun.GasMode);
        Assert.True(gassedRun.UsedVentStabilization);
        Assert.NotNull(gassedRun.PowerRatio);
        Assert.True(gassedRun.PowerRatio > 0 && gassedRun.PowerRatio < 1.0);
        Assert.NotNull(gassedRun.GasFlowNumber);
        Assert.NotNull(gassedRun.FroudeNumber);

        // Verify summary CSV
        var summaryPath = Path.Combine(h.Store.RootDirectory, doc.FolderName, PowerTestFileContracts.ResultsSummaryFileName);
        Assert.True(File.Exists(summaryPath));
        var csvLines = File.ReadAllLines(summaryPath);
        Assert.True(csvLines.Length >= 3); // Header + 2 condition rows

        // Verify motor parked at 15 rpm and gas setpoint cut to zero at completion
        Assert.Equal(15, sim.MotorRpm);
        Assert.Equal(0, sim.FlowSetpoint);
        h.AdvanceSimulator(10.0);
        Assert.InRange(sim.ReadFlow(), 0.0, 0.05);
    }

    private static PowerTestSettings FastSettings() => new()
    {
        MinRpm = 15,
        MaxRpm = 1000,
        SpeedToleranceRpm = 3,
        SpeedStableSamples = 2,
        MaxSpeedSettlingSeconds = 10,
        StationarityWindowSeconds = 1,
        StationarityRequiredSamples = 1,
        StationaritySlopeTolerancePercentPerSecond = 100,
        MinSamples = 4,
        RelativeCiFraction = 0.10,
        CiFloorSigmaMultiple = 1,
        MaxCaptureSeconds = 30,
        MaxTries = 2,
        MaxTorquePercent = 90,
        MeasurementTimeoutSeconds = 5,
        CaptureServoPollMs = 250,
        RestoreServoPollMs = 1000,
    };

    private sealed class Harness : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "PowerRunnerTests_" + Guid.NewGuid().ToString("N"));
        private readonly TestClock _clock = new(new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));

        public Harness(string? blockReason = null, bool useSimulator = false, DeviceModel? customSimulator = null)
        {
            Device = new RunnerDeviceService(customSimulator ?? (useSimulator ? new DeviceModel(randomSeed: 20260904) : null));
            Arbiter = new CommandArbiter(Device, _clock);
            Store = new PowerTestStore(_root);
            Runner = new PowerTestRunner(
                Arbiter,
                Arbiter,
                Store,
                new PowerAnalysisEngine(),
                new StubInterlock(blockReason),
                _clock);
        }

        public RunnerDeviceService Device { get; }
        public CommandArbiter Arbiter { get; }
        public PowerTestStore Store { get; }
        public PowerTestRunner Runner { get; }

        public PowerTestDocument CreateDocumentWithConditions(
            PowerTestSettings settings,
            IReadOnlyList<PowerCondition> conditions,
            PowerGeometry? geometry = null)
        {
            geometry ??= new PowerGeometry
            {
                Impellers = [new Impeller { Type = ImpellerType.RushtonFlatBlade, DiameterM = 0.06 }],
                VesselDiameterM = 0.190,
                LiquidVolumeM3 = 0.010,
            };
            return Store.CreateTest(
                "runner-" + Guid.NewGuid().ToString("N"),
                new FluidProperties { DensityKgM3 = 998, ViscosityPaS = 0.001 },
                geometry,
                settings,
                conditions);
        }

        public PowerTestDocument CreateDocument(
            PowerTestSettings settings,
            int replicates = 1,
            PowerGasMode gasMode = PowerGasMode.Ungassed,
            PowerGeometry? geometry = null)
        {
            geometry ??= new PowerGeometry
            {
                Impellers = [new Impeller { StageIndex = 0, DiameterM = 0.060 }],
                VesselDiameterM = 0.20,
                LiquidVolumeM3 = 0.010,
            };
            return Store.CreateTest(
                "runner-" + Guid.NewGuid().ToString("N"),
                new FluidProperties { DensityKgM3 = 998, ViscosityPaS = 0.001 },
                geometry,
                settings,
                [new PowerCondition
                {
                    OrderIndex = 0,
                    AgitationRpm = 300,
                    GasMode = gasMode,
                    GasFlowLpm = gasMode is PowerGasMode.Gassed or PowerGasMode.Both ? 5 : null,
                    RequestedReplicates = replicates,
                }]);
        }

        public void Push(double rpm, double torquePercent, double flowRate = SensorReadings.NotReceived)
        {
            _clock.Advance(TimeSpan.FromSeconds(0.5));
            Device.Push(new SensorSnapshot
            {
                HasServoTelemetry = true,
                HasServoSample = true,
                ServoOnline = true,
                ServoCommEnabled = true,
                ServoRpm = rpm,
                ServoTorquePct = torquePercent,
                ServoTorqueNm = torquePercent / 100.0 * 1.27,
                ServoPowerW = PowerCalc.ShaftPower(torquePercent / 100.0 * 1.27, rpm),
                FlowRate = flowRate,
                Temperature = 25,
                HubFirmwareVersion = "test-hub",
                HubProtocolVersion = 9,
            });
        }

        public void PushGas(
            double rpm,
            double torquePercent,
            double flowRate,
            double flowSetpoint,
            int valve1 = 0,
            int valve2 = 0,
            int valveMain = 0,
            int commandId = 1,
            int commandAck = 1,
            bool commandPending = false,
            bool flowmeterOnline = true)
        {
            _clock.Advance(TimeSpan.FromSeconds(0.5));
            Device.Push(new SensorSnapshot
            {
                HasServoTelemetry = true,
                HasServoSample = true,
                ServoOnline = true,
                ServoCommEnabled = true,
                ServoRpm = rpm,
                ServoTorquePct = torquePercent,
                ServoTorqueNm = torquePercent / 100.0 * 1.27,
                ServoPowerW = PowerCalc.ShaftPower(torquePercent / 100.0 * 1.27, rpm),
                FlowRate = flowRate,
                FlowSetpoint = flowSetpoint,
                FlowValve1 = valve1,
                FlowValve2 = valve2,
                FlowValveMain = valveMain,
                FlowCommandId = commandId,
                FlowCommandAck = commandAck,
                FlowCommandPending = commandPending,
                FlowmeterOnline = flowmeterOnline,
                Temperature = 25,
                HubFirmwareVersion = "test-hub",
                HubProtocolVersion = 9,
            });
        }

        public void AdvanceSimulator(double seconds)
        {
            _clock.Advance(TimeSpan.FromSeconds(seconds));
            Device.AdvanceSimulator(seconds);
        }

        public void DriveToAccumulating()
        {
            for (var i = 0; i < 30 && Runner.Phase != PowerRunPhase.AccumulatingToTarget; i++)
            {
                Push(300, 2.0);
            }
            Assert.Equal(PowerRunPhase.AccumulatingToTarget, Runner.Phase);
        }

        public void DriveUntil(PowerRunPhase phase)
        {
            for (var i = 0; i < 500 && Runner.Phase != phase; i++)
            {
                var rpm = Runner.Phase == PowerRunPhase.PreparingNextRun ? 15 : 300;
                Push(rpm, 2.0);
            }
            Assert.Equal(phase, Runner.Phase);
        }

        public void Dispose()
        {
            Runner.Dispose();
            Arbiter.Dispose();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class StubInterlock(string? blockReason) : IPowerTestInterlock
    {
        public bool CanStart(out string? reason)
        {
            reason = blockReason;
            return reason is null;
        }
    }

    private sealed class RunnerDeviceService(DeviceModel? simulator) : IDeviceService
    {
        private readonly TelemetryParser _parser = new();

        public List<string> Sent { get; } = [];
        public ConnectionState State { get; private set; } = ConnectionState.Connected;
        public TransportMedium? Medium => TransportMedium.Usb;
        public string Endpoint => simulator is null ? "FAKE" : "SIMULATOR";
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
            Sent.Add(json);
            if (simulator is not null)
            {
                Assert.True(WireCodec.ApplyCommand(simulator, json, out _));
            }
            CommandSent?.Invoke(json);
        }

        public void Push(SensorSnapshot snapshot)
        {
            Latest = snapshot;
            TelemetryReceived?.Invoke(snapshot);
        }

        public void AdvanceSimulator(double seconds)
        {
            Assert.NotNull(simulator);
            simulator.Tick(seconds);
            Assert.Equal(ParseOutcome.Updated, _parser.Parse(WireCodec.BuildTelemetry(simulator)));
            Push(_parser.Readings.Snapshot());
        }

        public void Connect() { }
        public void ConnectUsb(string portName) { }
        public void ConnectWiFi(string ipAddress) { }
        public void Disconnect() { }
        public void ZeroSessionTime() => SessionTimeZeroed?.Invoke(0);
        public Task<string?> DiscoverUsbPortAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }
}
