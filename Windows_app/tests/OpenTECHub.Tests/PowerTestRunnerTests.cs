using System.IO;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;
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
    public async Task Preflight_refuses_a_gassed_condition_while_the_flowmeter_is_offline()
    {
        using var h = new Harness();
        var doc = h.CreateDocument(FastSettings(), gasMode: PowerGasMode.Gassed);
        h.Push(0, 0);

        Assert.False(h.Runner.CanStart(doc, out var reason));
        Assert.Contains("fluxômetro precisa estar online", reason, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Runner.StartTestAsync(doc));
        Assert.Empty(h.Device.Sent);
    }

    [Fact]
    public async Task Preflight_closes_the_gas_path_itself_instead_of_asking_the_operator()
    {
        using var h = new Harness();
        var doc = h.CreateDocument(FastSettings(), gasMode: PowerGasMode.Both);
        // Gas left open from a previous point, flowmeter online: the assay must shut it, not refuse.
        h.PushGas(0, 0, flowRate: 1.2, flowSetpoint: 5, valve1: 1, valveMain: 0);

        Assert.True(h.Runner.CanStart(doc, out var reason), reason);
        await h.Runner.StartTestAsync(doc);

        Assert.Contains(h.Device.Sent, sent => sent.Contains("\"flowSetpoint\":0", StringComparison.Ordinal));
        Assert.Equal(CommandOwner.PowerAssay, h.Arbiter.OwnerOf(ActuatorId.Aeration));
    }

    [Fact]
    public void Preflight_does_not_confuse_residual_sensor_flow_with_an_open_path()
    {
        using var h = new Harness();
        var doc = h.CreateDocument(FastSettings(), gasMode: PowerGasMode.Ungassed);
        // Every valve shut and setpoint zero, but the sensor still reads a residual rate.
        h.PushGas(0, 0, flowRate: 0.4, flowSetpoint: 0, valve1: 0, valve2: 0, valveMain: 1);

        Assert.True(h.Runner.CanStart(doc, out var reason), reason);
    }

    [Fact]
    public void Preflight_lets_an_ungassed_point_start_while_the_flowmeter_is_offline()
    {
        using var h = new Harness();
        var doc = h.CreateDocument(FastSettings(), gasMode: PowerGasMode.Ungassed);
        h.Push(0, 0);

        Assert.True(h.Runner.CanStart(doc, out var reason), reason);
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
            FastSettings() with { PrestageAgitationRpm = 14 },
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
    public async Task Preflight_refuses_a_tare_made_with_a_different_torque_calibration()
    {
        using var h = new Harness();
        var doc = h.CreateDocument(FastSettings());
        h.Push(0, 0);
        doc.RelativeMode = false;
        doc.Calibration = new TorqueCalibration { Scale = 1.1, MotorRatedTorqueNm = 1.27 };
        doc.Tare = new TareCurve
        {
            ImpellerSetHash = PowerTestFileContracts.ComputeImpellerSetHash(doc.Geometry),
            CalibrationHash = PowerTestFileContracts.ComputeTorqueCalibrationHash(
                new TorqueCalibration { Scale = 1.0, MotorRatedTorqueNm = 1.27 },
                doc.MotorRatedTorqueNm),
            Points = { new TarePoint(300, 0.5, 0.05) },
        };

        Assert.False(h.Runner.CanStart(doc, out var reason));
        Assert.Contains("calibração de torque mudou", reason, StringComparison.OrdinalIgnoreCase);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Runner.StartTestAsync(doc));
        Assert.Contains("refaça a tara", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(h.Device.Sent);
    }

    /// <summary>
    /// Every telemetry frame the page refreshes on <c>StateChanged</c>; a frame that raised it two
    /// or three times (SetPhase + the explicit raise) cost two or three refreshes on the UI thread.
    /// Frames with a valid servo measurement raise it exactly once, after all of the frame's
    /// mutations; a frame without one raises nothing unless it paused the run.
    /// </summary>
    [Fact]
    public async Task A_telemetry_frame_raises_StateChanged_at_most_once_and_after_the_data_point()
    {
        using var h = new Harness();
        var doc = h.CreateDocument(FastSettings());
        var raisesInFrame = 0;
        var order = new List<string>();
        h.Runner.StateChanged += () => { raisesInFrame++; order.Add("state"); };
        h.Runner.DataPointAdded += _ => order.Add("point");
        h.Push(0, 0);

        await h.Runner.StartTestAsync(doc);
        var phases = new List<PowerRunPhase>();
        var frames = 0;
        while (h.Runner.Phase is PowerRunPhase.SettingSpeed or PowerRunPhase.SettlingTorque or PowerRunPhase.AccumulatingToTarget && frames < 500)
        {
            raisesInFrame = 0;
            order.Clear();
            h.Push(300, 2.0);
            frames++;
            phases.Add(h.Runner.Phase);
            Assert.True(raisesInFrame <= 1, $"frame {frames} ({h.Runner.Phase}) raised StateChanged {raisesInFrame} times");
            Assert.Equal(1, raisesInFrame);
            if (order.Contains("point"))
            {
                Assert.Equal("state", order[^1]);
            }
        }

        Assert.Contains(PowerRunPhase.SettlingTorque, phases);
        Assert.Contains(PowerRunPhase.AccumulatingToTarget, phases);
        Assert.Equal(PowerRunPhase.Reviewing, h.Runner.Phase);
    }

    /// <summary>
    /// D-048, file equivalence: the same assay driven through the synchronous store and through the
    /// queued store leaves byte-identical raw data, global series, journal and summary.
    /// </summary>
    [Fact]
    public async Task The_queued_store_writes_the_same_assay_files_as_the_synchronous_one()
    {
        using var queued = new BackgroundFileWriter();
        using var a = new Harness();
        using var b = new Harness(writer: queued);
        var name = "runner-equivalencia";

        static async Task<PowerTestDocument> Drive(Harness h, string name)
        {
            var geometry = new PowerGeometry
            {
                Impellers = [new Impeller { StageIndex = 0, DiameterM = 0.060 }],
                VesselDiameterM = 0.20,
                LiquidVolumeM3 = 0.010,
            };
            var doc = h.Store.CreateTest(name, new FluidProperties { DensityKgM3 = 998, ViscosityPaS = 0.001 }, geometry,
                FastSettings() with { AutoAcceptRuns = true }, [new PowerCondition { AgitationRpm = 300 }, new PowerCondition { AgitationRpm = 400 }]);
            h.Push(0, 0);
            await h.Runner.StartTestAsync(doc);
            var frames = 0;
            while (h.Runner.Phase != PowerRunPhase.Completed && frames++ < 400)
            {
                var rpm = h.Runner.CurrentRun?.AgitationRpm ?? 15;
                h.Push(h.Runner.Phase == PowerRunPhase.PreparingNextRun ? 15 : rpm, 2.0 + 0.001 * (frames % 7));
            }
            Assert.Equal(PowerRunPhase.Completed, h.Runner.Phase);
            return doc;
        }

        var docA = await Drive(a, name);
        var docB = await Drive(b, name);
        await b.Store.FlushAsync();

        // Ids are minted per assay; everything else — clock-driven timestamps included — must match.
        static string Normalised(string path) => System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(path), "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", "<id>");

        foreach (var file in new[] { PowerTestFileContracts.GlobalSeriesFileName, PowerTestFileContracts.EventLogFileName, PowerTestFileContracts.ResultsSummaryFileName })
        {
            Assert.Equal(
                Normalised(Path.Combine(a.Store.RootDirectory, docA.FolderName, file)),
                Normalised(Path.Combine(b.Store.RootDirectory, docB.FolderName, file)));
        }
        Assert.Equal(2, docA.Runs.Count);
        foreach (var run in docA.Runs)
        {
            var rawA = a.Store.GetRunRawDataPath(docA.FolderName, run.FolderName);
            var rawB = b.Store.GetRunRawDataPath(docB.FolderName, run.FolderName);
            Assert.Equal(File.ReadAllBytes(rawA), File.ReadAllBytes(rawB));
            var resultA = Path.Combine(Path.GetDirectoryName(rawA)!, PowerTestFileContracts.RunResultFileName);
            var resultB = Path.Combine(Path.GetDirectoryName(rawB)!, PowerTestFileContracts.RunResultFileName);
            Assert.Equal(Normalised(resultA), Normalised(resultB));
        }
    }

    /// <summary>D-048: a queued write that fails does not stop the assay, but the runner says the record has a hole.</summary>
    [Fact]
    public async Task A_failed_queued_write_marks_the_recording_as_compromised_and_the_run_goes_on()
    {
        using var queued = new BackgroundFileWriter();
        using var h = new Harness(writer: queued);
        var doc = h.CreateDocument(FastSettings());
        // The journal's path is taken by a directory: every append to it fails on the writer's thread.
        var eventsPath = Path.Combine(h.Store.RootDirectory, doc.FolderName, PowerTestFileContracts.EventLogFileName);
        File.Delete(eventsPath);
        Directory.CreateDirectory(eventsPath);
        var stateChanges = 0;
        h.Runner.StateChanged += () => stateChanges++;
        h.Push(0, 0);

        await h.Runner.StartTestAsync(doc);
        await h.Store.FlushAsync();
        // The event is raised on the writer's thread; give the runner's handler a moment.
        for (var i = 0; i < 50 && !h.Runner.IsStorageCompromised; i++) { await Task.Delay(20); }

        Assert.True(h.Runner.IsStorageCompromised);
        Assert.StartsWith("⚠ Gravação comprometida", h.Runner.StatusMessage, StringComparison.Ordinal);
        Assert.True(h.Runner.IsRunning);
        h.Push(300, 2.0);
        h.Push(300, 2.0);
        h.Push(300, 2.0);
        Assert.True(h.Runner.IsRunning || h.Runner.IsInReview);
        await h.Store.FlushAsync(); // the harness deletes its folder on dispose
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
    public async Task Auto_accept_resumes_a_measurement_pause_without_the_operator()
    {
        using var h = new Harness();
        var doc = h.CreateDocument(FastSettings() with { AutoAcceptRuns = true });
        h.Push(0, 0);
        await h.Runner.StartTestAsync(doc);
        h.DriveToAccumulating();

        h.Device.Push(new SensorSnapshot
        {
            HasServoTelemetry = true,
            HasServoSample = false,
            ServoOnline = false,
            ServoCommEnabled = true,
        });
        Assert.True(h.Runner.IsPausedForMeasurement);

        // Telemetry comes back: an unattended assay picks itself up instead of waiting.
        h.Push(300, 2.0);

        Assert.False(h.Runner.IsPausedForMeasurement);
        Assert.Equal(PowerRunPhase.SettingSpeed, h.Runner.Phase);
    }

    [Fact]
    public async Task Measurement_pause_still_waits_for_the_operator_without_auto_accept()
    {
        using var h = new Harness();
        var doc = h.CreateDocument(FastSettings());
        h.Push(0, 0);
        await h.Runner.StartTestAsync(doc);
        h.DriveToAccumulating();

        h.Device.Push(new SensorSnapshot
        {
            HasServoTelemetry = true,
            HasServoSample = false,
            ServoOnline = false,
            ServoCommEnabled = true,
        });
        h.Push(300, 2.0);

        Assert.True(h.Runner.IsPausedForMeasurement);
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
    public async Task Both_condition_is_phase1_P0_and_shuts_the_gas_itself()
    {
        using var h = new Harness();
        var doc = h.CreateDocument(FastSettings(), gasMode: PowerGasMode.Both);
        h.PushGas(0, 0, flowRate: 0, flowSetpoint: 0, valveMain: 1);

        await h.Runner.StartTestAsync(doc);

        Assert.Equal(PowerGasMode.Ungassed, h.Runner.CurrentRun!.GasMode);
        Assert.Null(h.Runner.CurrentRun.GasFlowLpm);
        // Subphase 1 measures P0, which is only valid with the path shut, so the assay owns the
        // aeration loop from the start and commands the close instead of asking the operator.
        Assert.Contains(h.Device.Sent, json => json.Contains("\"flowSetpoint\":0", StringComparison.Ordinal));
        Assert.Equal(CommandOwner.PowerAssay, h.Arbiter.OwnerOf(ActuatorId.Agitation));
        Assert.Equal(CommandOwner.PowerAssay, h.Arbiter.OwnerOf(ActuatorId.Aeration));
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
            PrestageAgitationRpm = 15.0,
            PrestageFlowToleranceLpm = 0.2,
            PrestageFlowStableSamples = 2,
            MaxPrestageSeconds = 30.0,
        };
        var doc = h.CreateDocument(settings, gasMode: PowerGasMode.Gassed);
        doc.Conditions[0].GasFlowLpm = 4.0;
        h.PushGas(0, 0, flowRate: 0.0, flowSetpoint: 0.0, valve1: 0, valve2: 0, valveMain: 1, commandId: 0, commandAck: 0, flowmeterOnline: true);

        await h.Runner.StartTestAsync(doc);

        Assert.Equal(PowerRunPhase.PrestagingFlow, h.Runner.Phase);
        Assert.Equal(CommandOwner.PowerAssay, h.Arbiter.OwnerOf(ActuatorId.Agitation));
        Assert.Equal(CommandOwner.PowerAssay, h.Arbiter.OwnerOf(ActuatorId.Aeration));
        Assert.Contains(h.Device.Sent, json => json.Contains("\"motorSetpoint\":15"));
        // The vent is C, on the B/C output — valve_1 on the default A/B/C wiring (plan §1.3.1).
        Assert.Contains(h.Device.Sent, json => json.Contains("\"valve_1\":1,\"valve_2\":0"));

        // Push telemetry confirming the vent state with flow stabilizing
        h.PushGas(15, 0.5, flowRate: 4.1, flowSetpoint: 4.0, valve1: 1, valve2: 0, valveMain: 0, commandId: 1, commandAck: 1);
        Assert.Equal(PowerRunPhase.PrestagingFlow, h.Runner.Phase);

        // 2nd stable sample -> one-frame switch to the reactor: B/C close, A (valve_2) opens
        h.PushGas(15, 0.5, flowRate: 4.05, flowSetpoint: 4.0, valve1: 1, valve2: 0, valveMain: 0, commandId: 1, commandAck: 1);
        Assert.Equal(PowerRunPhase.OpeningGas, h.Runner.Phase);
        Assert.Contains(h.Device.Sent, json => json.Contains("\"valve_1\":0,\"valve_2\":1"));

        // Confirm reactor gas state (A open, B/C closed, main open)
        h.PushGas(15, 0.5, flowRate: 4.0, flowSetpoint: 4.0, valve1: 0, valve2: 1, valveMain: 0, commandId: 2, commandAck: 2);
        Assert.Equal(PowerRunPhase.SettingSpeed, h.Runner.Phase);
        Assert.Contains(h.Device.Sent, json => json.Contains("\"motorSetpoint\":300"));

        // Confirm speed
        h.PushGas(300, 2.0, flowRate: 4.0, flowSetpoint: 4.0, valve1: 0, valve2: 1, valveMain: 0, commandId: 2, commandAck: 2);
        h.PushGas(300, 2.0, flowRate: 4.0, flowSetpoint: 4.0, valve1: 0, valve2: 1, valveMain: 0, commandId: 2, commandAck: 2);
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
            PrestageAgitationRpm = 15.0,
            PrestageFlowToleranceLpm = 0.1,
            PrestageFlowStableSamples = 5,
            MaxPrestageSeconds = 2.0,
        };
        var doc = h.CreateDocument(settings, gasMode: PowerGasMode.Gassed);
        doc.Conditions[0].GasFlowLpm = 5.0;
        h.PushGas(0, 0, flowRate: 0.0, flowSetpoint: 0.0, valve1: 0, valve2: 0, valveMain: 1, commandId: 0, commandAck: 0, flowmeterOnline: true);

        await h.Runner.StartTestAsync(doc);
        Assert.Equal(PowerRunPhase.PrestagingFlow, h.Runner.Phase);

        // Advance past 2s with flow out of tolerance
        for (var i = 0; i < 10; i++)
        {
            h.PushGas(15, 0.5, flowRate: 1.0, flowSetpoint: 5.0, valve1: 1, valve2: 0, valveMain: 0, commandId: 1, commandAck: 1);
        }

        Assert.Equal(PowerRunPhase.Reviewing, h.Runner.Phase);
        Assert.Equal(PowerStopReason.Tmax, h.Runner.CurrentRun!.StopReason);
    }

    /// <summary>D-050: a vent time-out reaches review with n = 0; that run has no result to accept.</summary>
    [Fact]
    public async Task A_run_that_timed_out_before_capturing_cannot_be_accepted_only_rejected_or_repeated()
    {
        using var h = new Harness();
        var settings = FastSettings() with
        {
            PrestageAgitationRpm = 15.0,
            PrestageFlowToleranceLpm = 0.1,
            PrestageFlowStableSamples = 5,
            MaxPrestageSeconds = 2.0,
        };
        var doc = h.CreateDocument(settings, gasMode: PowerGasMode.Gassed);
        doc.Conditions[0].GasFlowLpm = 5.0;
        h.PushGas(0, 0, flowRate: 0.0, flowSetpoint: 0.0, valve1: 0, valve2: 0, valveMain: 1, commandId: 0, commandAck: 0, flowmeterOnline: true);
        await h.Runner.StartTestAsync(doc);
        for (var i = 0; i < 10; i++)
        {
            h.PushGas(15, 0.5, flowRate: 1.0, flowSetpoint: 5.0, valve1: 1, valve2: 0, valveMain: 0, commandId: 1, commandAck: 1);
        }
        Assert.Equal(PowerRunPhase.Reviewing, h.Runner.Phase);
        Assert.Equal(0, h.Runner.CurrentRun!.SampleCount);
        Assert.False(PowerTestRunner.HasCapture(h.Runner.CurrentRun));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Runner.AcceptRunAsync());
        Assert.Equal(PowerTestRunner.NoCaptureMessage, ex.Message);
        Assert.Equal(PowerRunPhase.Reviewing, h.Runner.Phase);
        Assert.Equal(0, doc.Conditions[0].AcceptedReplicates);
        Assert.DoesNotContain(doc.Runs, r => r.Phase == PowerRunPhase.Accepted);

        await h.Runner.RejectRunAsync("sem captura");
        Assert.Equal(PowerRunPhase.Rejected, h.Runner.Phase);
        Assert.Equal(1, doc.Conditions[0].RejectedReplicates);
        Assert.Equal(PowerConditionStatus.Pending, doc.Conditions[0].Status);
    }

    /// <summary>§I.1: with auto-accept and RetryThenSkip, a vent time-out retries the condition once, then skips it and moves on.</summary>
    [Fact]
    public async Task Unattended_sequence_failure_retries_once_then_skips_the_condition()
    {
        using var h = new Harness();
        var settings = FastSettings() with
        {
            AutoAcceptRuns = true,
            UnattendedFailurePolicy = UnattendedFailurePolicy.RetryThenSkip,
            PrestageAgitationRpm = 15.0,
            PrestageFlowToleranceLpm = 0.1,
            PrestageFlowStableSamples = 5,
            PrestageFlowStabilityStdDevLpm = 0.0, // stability exit disabled so the time-out is what happens
            MaxPrestageSeconds = 2.0,
        };
        var doc = h.CreateDocumentWithConditions(settings,
        [
            new PowerCondition { OrderIndex = 0, AgitationRpm = 300, GasFlowLpm = 5.0, GasMode = PowerGasMode.Gassed },
            new PowerCondition { OrderIndex = 1, AgitationRpm = 300, GasMode = PowerGasMode.Ungassed },
        ]);
        var gassed = doc.Conditions[0];
        var dry = doc.Conditions[1];
        h.PushGas(0, 0, flowRate: 0.0, flowSetpoint: 0.0, valve1: 0, valve2: 0, valveMain: 1, commandId: 0, commandAck: 0, flowmeterOnline: true);

        await h.Runner.StartTestAsync(doc);
        Assert.Equal(PowerRunPhase.PrestagingFlow, h.Runner.Phase);

        // First failure: the flow never settles - rejected, and the same condition is retried.
        for (var i = 0; i < 10 && h.Runner.Phase == PowerRunPhase.PrestagingFlow; i++)
        {
            h.PushGas(15, 0.5, flowRate: 1.0, flowSetpoint: 5.0, valve1: 1, valve2: 0, valveMain: 0, commandId: 1, commandAck: 1);
        }
        Assert.Equal(PowerRunPhase.PreparingNextRun, h.Runner.Phase);
        Assert.Equal(1, gassed.RejectedReplicates);
        Assert.Equal(PowerConditionStatus.Pending, gassed.Status);
        Assert.Equal(0, gassed.AcceptedReplicates);
        Assert.DoesNotContain(doc.Runs, r => r.Phase == PowerRunPhase.Accepted);

        // Back at minimum speed the runner starts the retry of the same condition.
        h.PushGas(15, 0.5, flowRate: 0.0, flowSetpoint: 0.0, valve1: 0, valve2: 0, valveMain: 1, commandId: 1, commandAck: 1);
        h.PushGas(15, 0.5, flowRate: 0.0, flowSetpoint: 0.0, valve1: 0, valve2: 0, valveMain: 1, commandId: 1, commandAck: 1);
        Assert.Equal(PowerRunPhase.PrestagingFlow, h.Runner.Phase);
        Assert.Same(gassed, h.Runner.CurrentCondition);

        // Second failure: skipped, and the sequence goes on to the dry condition.
        for (var i = 0; i < 10 && h.Runner.Phase == PowerRunPhase.PrestagingFlow; i++)
        {
            h.PushGas(15, 0.5, flowRate: 1.0, flowSetpoint: 5.0, valve1: 1, valve2: 0, valveMain: 0, commandId: 2, commandAck: 2);
        }
        Assert.Equal(PowerConditionStatus.Skipped, gassed.Status);
        Assert.Equal(2, gassed.RejectedReplicates);
        Assert.Equal(PowerRunPhase.PreparingNextRun, h.Runner.Phase);
        h.PushGas(15, 0.5, flowRate: 0.0, flowSetpoint: 0.0, valve1: 0, valve2: 0, valveMain: 1, commandId: 2, commandAck: 2);
        h.PushGas(15, 0.5, flowRate: 0.0, flowSetpoint: 0.0, valve1: 0, valve2: 0, valveMain: 1, commandId: 2, commandAck: 2);
        Assert.Same(dry, h.Runner.CurrentCondition);
        Assert.True(h.Runner.IsRunning);
        Assert.DoesNotContain(doc.Runs, r => r.Phase == PowerRunPhase.Accepted);
    }

    /// <summary>§I.1: the default policy still parks for review.</summary>
    [Fact]
    public async Task Unattended_default_policy_still_stops_for_review()
    {
        using var h = new Harness();
        var settings = FastSettings() with
        {
            AutoAcceptRuns = true,
            PrestageAgitationRpm = 15.0,
            PrestageFlowToleranceLpm = 0.1,
            PrestageFlowStableSamples = 5,
            PrestageFlowStabilityStdDevLpm = 0.0,
            MaxPrestageSeconds = 2.0,
        };
        var doc = h.CreateDocument(settings, gasMode: PowerGasMode.Gassed);
        doc.Conditions[0].GasFlowLpm = 5.0;
        h.PushGas(0, 0, flowRate: 0.0, flowSetpoint: 0.0, valve1: 0, valve2: 0, valveMain: 1, commandId: 0, commandAck: 0, flowmeterOnline: true);
        await h.Runner.StartTestAsync(doc);
        for (var i = 0; i < 10 && h.Runner.Phase == PowerRunPhase.PrestagingFlow; i++)
        {
            h.PushGas(15, 0.5, flowRate: 1.0, flowSetpoint: 5.0, valve1: 1, valve2: 0, valveMain: 0, commandId: 1, commandAck: 1);
        }
        Assert.Equal(PowerRunPhase.Reviewing, h.Runner.Phase);
    }

    /// <summary>§I.2: a flow that settled just outside the band (the controller's +0.08 offset) leaves the vent phase.</summary>
    [Fact]
    public async Task Vent_phase_exits_when_the_flow_is_stable_even_if_just_outside_the_band()
    {
        using var h = new Harness();
        var settings = FastSettings() with
        {
            PrestageAgitationRpm = 15.0,
            PrestageFlowToleranceLpm = 0.05,
            PrestageFlowStableSamples = 5,
            PrestageFlowStabilityStdDevLpm = 0.05,
            PrestageFlowStabilityMaxErrorLpm = 0.3,
            MaxPrestageSeconds = 500.0,
        };
        var doc = h.CreateDocument(settings, gasMode: PowerGasMode.Gassed);
        doc.Conditions[0].GasFlowLpm = 2.0;
        h.PushGas(0, 0, flowRate: 0.0, flowSetpoint: 0.0, valve1: 0, valve2: 0, valveMain: 1, commandId: 0, commandAck: 0, flowmeterOnline: true);
        await h.Runner.StartTestAsync(doc);
        Assert.Equal(PowerRunPhase.PrestagingFlow, h.Runner.Phase);

        // 2.08 +/- 0.01 L/min: outside the 0.05 band, but flat.
        foreach (var flow in new[] { 2.09, 2.07, 2.08, 2.08, 2.07, 2.09 })
        {
            if (h.Runner.Phase != PowerRunPhase.PrestagingFlow)
            {
                break;
            }
            h.PushGas(15, 0.5, flowRate: flow, flowSetpoint: 2.0, valve1: 1, valve2: 0, valveMain: 0, commandId: 1, commandAck: 1);
        }

        Assert.Equal(PowerRunPhase.OpeningGas, h.Runner.Phase);
        var events = File.ReadAllText(Path.Combine(h.Store.RootDirectory, doc.FolderName, PowerTestFileContracts.EventLogFileName));
        Assert.Contains("PrestageFlowStable", events, StringComparison.Ordinal);
        // The journal escapes non-ASCII, so "est\u00E1vel" is stored as est\u00E1vel.
        Assert.Contains(@"(est\u00E1vel", events, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { 2.09, 2.07, 2.08, 2.08, 2.07 }, 2.0, true)]   // flat, +0.08 offset
    [InlineData(new[] { 2.5, 2.3, 2.2, 2.1, 2.05 }, 2.0, false)]      // still decaying
    [InlineData(new[] { 2.4, 2.4, 2.4, 2.4, 2.4 }, 2.0, false)]       // flat but 0.4 off (> 0.3)
    [InlineData(new[] { 2.09, 2.07 }, 2.0, false)]                    // not enough samples
    public void VentFlowHasSettled_requires_low_spread_and_a_mean_near_the_target(double[] window, double target, bool expected)
    {
        var settings = new PowerTestSettings { PrestageFlowStableSamples = 5, PrestageFlowStabilityStdDevLpm = 0.05, PrestageFlowStabilityMaxErrorLpm = 0.3 };
        Assert.Equal(expected, PowerTestRunner.PrestageFlowHasSettled(window, target, settings, out _));
    }

    [Fact]
    public async Task Both_condition_sequences_P0_ungassed_first_then_PG_gassed_with_paired_reference()
    {
        using var h = new Harness();
        var settings = FastSettings() with
        {
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
        while (h.Runner.Phase != PowerRunPhase.PrestagingFlow)
        {
            h.PushGas(300, 3.0, flowRate: 0.0, flowSetpoint: 0.0, valve1: 0, valve2: 0, valveMain: 1, commandId: 0, commandAck: 0);
        }

        // Subphase 1 completed, immediately started Subphase 2: the gas goes out of C first
        // (B/C = valve_1 on the default wiring), never straight into the vessel.
        Assert.Equal(PowerRunPhase.PrestagingFlow, h.Runner.Phase);
        Assert.Equal(PowerGasMode.Gassed, h.Runner.CurrentRun!.GasMode);
        Assert.True(h.Runner.CurrentRun.UsedVentStabilization);
        Assert.Equal(P0Provenance.MeasuredUngassed, h.Runner.CurrentRun.P0Provenance);
        Assert.NotNull(h.Runner.CurrentRun.ReferenceP0W);
        var p0Captured = h.Runner.CurrentRun.ReferenceP0W.Value;
        Assert.True(p0Captured > 0);
        Assert.Contains("\"valve_1\":1,\"valve_2\":0", h.Device.Sent.Last(j => j.Contains("flowSetpoint")));

        // Settle on C (commandId 1: the first flow command was dispatched in Subphase 2), then the
        // one-frame switch and its confirmation on A = valve_2.
        h.PushGas(15, 0.5, flowRate: 3.0, flowSetpoint: 3.0, valve1: 1, valve2: 0, valveMain: 0, commandId: 1, commandAck: 1);
        h.PushGas(15, 0.5, flowRate: 3.0, flowSetpoint: 3.0, valve1: 1, valve2: 0, valveMain: 0, commandId: 1, commandAck: 1);
        Assert.Equal(PowerRunPhase.OpeningGas, h.Runner.Phase);
        Assert.Contains("\"valve_1\":0,\"valve_2\":1", h.Device.Sent.Last(j => j.Contains("flowSetpoint")));
        h.PushGas(15, 0.5, flowRate: 3.0, flowSetpoint: 3.0, valve1: 0, valve2: 1, valveMain: 0, commandId: 2, commandAck: 2);
        Assert.Equal(PowerRunPhase.SettingSpeed, h.Runner.Phase);

        // Speed confirmation for Subphase 2
        h.PushGas(300, 2.0, flowRate: 3.0, flowSetpoint: 3.0, valve1: 0, valve2: 1, valveMain: 0, commandId: 2, commandAck: 2);
        h.PushGas(300, 2.0, flowRate: 3.0, flowSetpoint: 3.0, valve1: 0, valve2: 1, valveMain: 0, commandId: 2, commandAck: 2);
        Assert.Equal(PowerRunPhase.SettlingTorque, h.Runner.Phase);

        // Drive Subphase 2 to completion
        while (h.Runner.Phase is PowerRunPhase.SettlingTorque or PowerRunPhase.AccumulatingToTarget)
        {
            h.PushGas(300, 2.0, flowRate: 3.0, flowSetpoint: 3.0, valve1: 0, valve2: 1, valveMain: 0, commandId: 2, commandAck: 2);
        }

        // Both runs completed!
        Assert.True(h.Runner.CurrentRun.PowerRatio is > 0 and < 1.0);
        Assert.Equal(P0Provenance.MeasuredUngassed, h.Runner.CurrentRun.P0Provenance);
    }

    /// <summary>
    /// The rig is recorded when the assay starts; a manifest older than the rig is reviewable
    /// but never continued, and a wiring change mid-assay is refused with both spelled out (plan §3.5).
    /// </summary>
    [Fact]
    public async Task StartTest_records_the_rig_and_refuses_a_legacy_manifest_or_a_changed_wiring()
    {
        using var h = new Harness();
        h.PushGas(0, 0, flowRate: 0.0, flowSetpoint: 0.0, valve1: 0, valve2: 0, valveMain: 1, commandId: 0, commandAck: 0, flowmeterOnline: true);

        var legacy = h.CreateDocument(FastSettings(), gasMode: PowerGasMode.Gassed);
        legacy.Status = PowerTestStatus.Interrupted;
        Assert.True(legacy.IsLegacyRig);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Runner.StartTestAsync(legacy));
        Assert.Contains("A/B/C", ex.Message, StringComparison.Ordinal);
        Assert.Equal(PowerRunPhase.Idle, h.Runner.Phase);

        var doc = h.CreateDocument(FastSettings(), gasMode: PowerGasMode.Gassed);
        Assert.False(doc.IsLegacyRig);
        await h.Runner.StartTestAsync(doc);
        Assert.Equal(GasInput.Input2, doc.GasRig!.AirInletInput);
        Assert.Equal(PowerRunPhase.PrestagingFlow, h.Runner.Phase);
        await h.Runner.AbortTestAsync("fim");

        h.Rig = new GasRigConfiguration(GasInput.Input1);
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Runner.StartTestAsync(doc));
        Assert.Contains("A na entrada 1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("A na entrada 2", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>On the other wiring the pre-stage lands on valve_2 and the reactor on valve_1.</summary>
    [Fact]
    public async Task Gassed_sequence_follows_the_configured_wiring()
    {
        using var h = new Harness { Rig = new GasRigConfiguration(GasInput.Input1) };
        var doc = h.CreateDocument(FastSettings(), gasMode: PowerGasMode.Gassed);
        doc.Conditions[0].GasFlowLpm = 4.0;
        h.PushGas(0, 0, flowRate: 0.0, flowSetpoint: 0.0, valve1: 0, valve2: 0, valveMain: 1, commandId: 0, commandAck: 0, flowmeterOnline: true);

        await h.Runner.StartTestAsync(doc);
        Assert.Equal(PowerRunPhase.PrestagingFlow, h.Runner.Phase);
        Assert.Contains("\"valve_1\":0,\"valve_2\":1", h.Device.Sent.Last(j => j.Contains("flowSetpoint")));

        h.PushGas(15, 0.5, flowRate: 4.0, flowSetpoint: 4.0, valve1: 0, valve2: 1, valveMain: 0, commandId: 1, commandAck: 1);
        h.PushGas(15, 0.5, flowRate: 4.0, flowSetpoint: 4.0, valve1: 0, valve2: 1, valveMain: 0, commandId: 1, commandAck: 1);
        Assert.Equal(PowerRunPhase.OpeningGas, h.Runner.Phase);
        Assert.Contains("\"valve_1\":1,\"valve_2\":0", h.Device.Sent.Last(j => j.Contains("flowSetpoint")));
        Assert.DoesNotContain(h.Device.Sent, j => j.Contains("\"valve_1\":1,\"valve_2\":1"));
    }

    [Fact]
    public async Task Gassed_assay_abort_parks_motor_at_minimum_and_zeros_flow_closing_valves()
    {
        using var h = new Harness();
        var settings = FastSettings();
        var doc = h.CreateDocument(settings, gasMode: PowerGasMode.Gassed);
        doc.Conditions[0].GasFlowLpm = 4.0;
        h.PushGas(0, 0, flowRate: 0.0, flowSetpoint: 0.0, valve1: 0, valve2: 0, valveMain: 1, commandId: 0, commandAck: 0, flowmeterOnline: true);

        await h.Runner.StartTestAsync(doc);
        // Every gassed condition starts on C; an abort there must still close the meter.
        Assert.Equal(PowerRunPhase.PrestagingFlow, h.Runner.Phase);

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
        var settings = FastSettings();
        var doc = h.CreateDocument(settings, gasMode: PowerGasMode.Gassed);
        doc.Conditions[0].GasFlowLpm = 4.0;
        h.PushGas(0, 0, flowRate: 0.0, flowSetpoint: 0.0, valve1: 0, valve2: 0, valveMain: 1, commandId: 0, commandAck: 0, flowmeterOnline: true);

        await h.Runner.StartTestAsync(doc);
        // Settle on C (B/C = valve_1), switch, confirm A = valve_2 (default wiring, plan §1.3.1)
        h.PushGas(15, 0.5, flowRate: 4.0, flowSetpoint: 4.0, valve1: 1, valve2: 0, valveMain: 0, commandId: 1, commandAck: 1);
        h.PushGas(15, 0.5, flowRate: 4.0, flowSetpoint: 4.0, valve1: 1, valve2: 0, valveMain: 0, commandId: 1, commandAck: 1);
        Assert.Equal(PowerRunPhase.OpeningGas, h.Runner.Phase);
        h.PushGas(15, 0.5, flowRate: 4.0, flowSetpoint: 4.0, valve1: 0, valve2: 1, valveMain: 0, commandId: 2, commandAck: 2);
        Assert.Equal(PowerRunPhase.SettingSpeed, h.Runner.Phase);

        // Confirm speed
        h.PushGas(300, 2.0, flowRate: 4.0, flowSetpoint: 4.0, valve1: 0, valve2: 1, valveMain: 0, commandId: 2, commandAck: 2);
        h.PushGas(300, 2.0, flowRate: 4.0, flowSetpoint: 4.0, valve1: 0, valve2: 1, valveMain: 0, commandId: 2, commandAck: 2);
        Assert.Equal(PowerRunPhase.SettlingTorque, h.Runner.Phase);

        // Flowmeter drops offline!
        h.PushGas(300, 2.0, flowRate: 4.0, flowSetpoint: 4.0, valve1: 0, valve2: 1, valveMain: 0, commandId: 2, commandAck: 2, flowmeterOnline: false);

        Assert.True(h.Runner.IsPausedForMeasurement);
    }

    [Fact]
    public async Task End_to_end_gassed_assay_with_simulator_prestages_on_C_then_captures_in_the_reactor()
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
            PrestageFlowStableSamples = 2,
            PrestageFlowToleranceLpm = 0.3,
            PrestageAgitationRpm = 15.0,
            MaxPrestageSeconds = 15.0,
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

        var sawPrestaging = false;
        var sawSettingSpeed = false;
        var sawAccumulating = false;

        for (var tick = 0; tick < 1000 && h.Runner.Phase != PowerRunPhase.Completed; tick++)
        {
            if (h.Runner.Phase == PowerRunPhase.PrestagingFlow)
            {
                sawPrestaging = true;
            }

            if (h.Runner.Phase == PowerRunPhase.SettingSpeed)
            {
                sawSettingSpeed = true;
            }

            if (h.Runner.Phase == PowerRunPhase.AccumulatingToTarget)
            {
                sawAccumulating = true;
            }

            h.AdvanceSimulator(0.2);
        }

        Assert.Equal(PowerRunPhase.Completed, h.Runner.Phase);
        Assert.True(sawPrestaging, "Expected to pass through PrestagingFlow phase");
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
        // Two settled frames on C are enough for the scripted sequences (bench default is 5).
        PrestageFlowStableSamples = 2,
    };

    private sealed class Harness : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "PowerRunnerTests_" + Guid.NewGuid().ToString("N"));
        private readonly TestClock _clock = new(new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));

        public Harness(string? blockReason = null, bool useSimulator = false, DeviceModel? customSimulator = null, BackgroundFileWriter? writer = null)
        {
            Device = new RunnerDeviceService(customSimulator ?? (useSimulator ? new DeviceModel(randomSeed: 20260904) : null));
            Arbiter = new CommandArbiter(Device, _clock);
            Store = new PowerTestStore(_root, writer);
            Runner = new PowerTestRunner(
                Arbiter,
                Arbiter,
                Store,
                new PowerAnalysisEngine(),
                new StubInterlock(blockReason),
                _clock,
                gasRig: () => Rig);
        }

        /// <summary>The A/B/C wiring the runner reads at each dispatch; tests swap it to exercise the other wiring.</summary>
        public GasRigConfiguration Rig { get; set; } = GasRigConfiguration.Default;

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
