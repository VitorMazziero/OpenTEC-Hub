using OpenTECHub.Protocol;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Simulator;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

public class SimulatorNodeConfigTests
{
    [Fact]
    public void TelemetryParser_parses_all_external_node_echoes_when_present()
    {
        var json = """
        {
            "Time": 10.0,
            "Tempval": 37.0,
            "Oxyval": 500.0,
            "pHval": 600.0,
            "Pressure": 1.0,
            "FlowRate": 2.5,
            "FlowmeterOnline": true,
            "FlowControlEnabled": true,
            "FlowKp": 0.85,
            "FlowKi": 0.045,
            "FlowFfGain": 1.25,
            "FlowFfOffset": 0.15,
            "FlowRampRate": 5.0,
            "FlowOutput": 1.234,
            "FlowSetpointCorrected": 2.45,
            "FlowmeterBootId": 42,
            "DistanceOnline": true,
            "DistanceCommEnabled": true,
            "DistanceCommandPending": true,
            "Distance": 120.0,
            "DistanceOffsetMm": 25.5,
            "DistanceSamplePeriodMs": 250,
            "DistanceSendPeriodMs": 1500,
            "PumpOnline": true,
            "PumpCommEnabled": true,
            "PumpCommandPending": false,
            "PumpFlow": 1.5,
            "PumpVol": 10.0,
            "PumpSlope": 1.2345,
            "PumpIntercept": 0.0543,
            "FlowTransitionVoltage": 0.0545,
            "PumpSlopeLow": 0.003906,
            "PumpSlopeHigh": 0.004470,
            "PumpTransitionSpeed": 200.0,
            "PumpTransitionFlow": 0.7812,
            "PumpCalCrc": 3867625571,
            "BiomassOnline": true,
            "BiomassCommEnabled": true,
            "BiomassCommandPending": false,
            "BiomassAbs": 0.456,
            "BiomassGear": 3,
            "BiomassEma": 0.25,
            "BiomassProbePeriodMs": 500
        }
        """;

        var parser = new TelemetryParser();
        var outcome = parser.Parse(json);

        Assert.Equal(ParseOutcome.Updated, outcome);
        var readings = parser.Readings;
        var snapshot = readings.Snapshot();

        // Distance echoes
        Assert.Equal(25.5, snapshot.DistanceOffsetMm);
        Assert.Equal(250, snapshot.DistanceSamplePeriodMs);
        Assert.Equal(1500, snapshot.DistanceSendPeriodMs);
        Assert.True(snapshot.DistanceCommandPending);

        // Flowmeter echoes
        Assert.Equal(0.85, snapshot.FlowKp);
        Assert.Equal(0.045, snapshot.FlowKi);
        Assert.Equal(1.25, snapshot.FlowFfGain);
        Assert.Equal(0.15, snapshot.FlowFfOffset);
        Assert.Equal(5.0, snapshot.FlowRampRate);
        Assert.Equal(1.234, snapshot.FlowOutput);
        Assert.Equal(2.45, snapshot.FlowSetpointCorrected);
        Assert.Equal(0.0545, snapshot.FlowTransitionVoltage);
        Assert.Equal(42L, snapshot.FlowmeterBootId);

        // Pump echoes
        Assert.Equal(1.2345, snapshot.PumpSlope);
        Assert.Equal(0.0543, snapshot.PumpIntercept);
        Assert.Equal(0.003906, snapshot.PumpSlopeLow);
        Assert.Equal(0.004470, snapshot.PumpSlopeHigh);
        Assert.Equal(200.0, snapshot.PumpTransitionSpeed);
        Assert.Equal(0.7812, snapshot.PumpTransitionFlow);
        Assert.Equal(3867625571L, snapshot.PumpCalCrc);

        // Biomass echoes
        Assert.Equal(3, snapshot.BiomassGear);
        Assert.Equal(0.25, snapshot.BiomassEma);
        Assert.Equal(500, snapshot.BiomassProbePeriodMs);
    }

    [Fact]
    public void TelemetryParser_yields_null_for_absent_echoes()
    {
        var legacyJson = """
        {
            "Time": 10.0,
            "Tempval": 37.0,
            "Oxyval": 500.0,
            "pHval": 600.0,
            "Pressure": 1.0,
            "FlowRate": 2.5
        }
        """;

        var parser = new TelemetryParser();
        parser.Parse(legacyJson);
        var snapshot = parser.Readings.Snapshot();

        Assert.Null(snapshot.DistanceOffsetMm);
        Assert.Null(snapshot.DistanceSamplePeriodMs);
        Assert.Null(snapshot.DistanceSendPeriodMs);
        Assert.Null(snapshot.DistanceCommandPending);

        Assert.Null(snapshot.FlowKp);
        Assert.Null(snapshot.FlowKi);
        Assert.Null(snapshot.FlowFfGain);
        Assert.Null(snapshot.FlowFfOffset);
        Assert.Null(snapshot.FlowRampRate);
        Assert.Null(snapshot.FlowOutput);
        Assert.Null(snapshot.FlowSetpointCorrected);
        Assert.Null(snapshot.FlowTransitionVoltage);
        Assert.Null(snapshot.FlowmeterBootId);

        Assert.Null(snapshot.PumpSlope);
        Assert.Null(snapshot.PumpIntercept);
        Assert.Null(snapshot.PumpSlopeLow);
        Assert.Null(snapshot.PumpSlopeHigh);
        Assert.Null(snapshot.PumpTransitionSpeed);
        Assert.Null(snapshot.PumpTransitionFlow);
        Assert.Null(snapshot.PumpCalCrc);

        Assert.Null(snapshot.BiomassGear);
        Assert.Null(snapshot.BiomassEma);
        Assert.Null(snapshot.BiomassProbePeriodMs);
    }

    [Fact]
    public void TelemetryParser_echoes_are_strictly_non_sticky_except_flowmeter_boot_id()
    {
        var parser = new TelemetryParser();

        var withEchoes = """
        {
            "Time": 1.0,
            "FlowKp": 1.0,
            "FlowmeterBootId": 1234,
            "FlowTransitionVoltage": 0.0545,
            "DistanceOffsetMm": 20.0,
            "DistanceCommandPending": true,
            "PumpSlope": 1.5,
            "PumpSlopeLow": 0.003906,
            "PumpSlopeHigh": 0.004470,
            "PumpTransitionSpeed": 200.0,
            "PumpTransitionFlow": 0.7812,
            "PumpCalCrc": 3867625571,
            "BiomassGear": 2
        }
        """;

        parser.Parse(withEchoes);
        Assert.Equal(1.0, parser.Readings.FlowKp);
        Assert.Equal(1234L, parser.Readings.FlowmeterBootId);
        Assert.Equal(0.0545, parser.Readings.FlowTransitionVoltage);
        Assert.Equal(20.0, parser.Readings.DistanceOffsetMm);
        Assert.True(parser.Readings.DistanceCommandPending);
        Assert.Equal(1.5, parser.Readings.PumpSlope);
        Assert.Equal(0.003906, parser.Readings.PumpSlopeLow);
        Assert.Equal(0.004470, parser.Readings.PumpSlopeHigh);
        Assert.Equal(200.0, parser.Readings.PumpTransitionSpeed);
        Assert.Equal(0.7812, parser.Readings.PumpTransitionFlow);
        Assert.Equal(3867625571L, parser.Readings.PumpCalCrc);
        Assert.Equal(2, parser.Readings.BiomassGear);

        // Next frame omits echoes
        var withoutEchoes = """
        {
            "Time": 2.0,
            "Tempval": 36.5
        }
        """;

        parser.Parse(withoutEchoes);
        var snapshot = parser.Readings.Snapshot();

        // Non-sticky echoes reverted to null
        Assert.Null(snapshot.FlowKp);
        Assert.Null(snapshot.FlowTransitionVoltage);
        Assert.Null(snapshot.DistanceOffsetMm);
        Assert.Null(snapshot.DistanceCommandPending);
        Assert.Null(snapshot.PumpSlope);
        Assert.Null(snapshot.PumpSlopeLow);
        Assert.Null(snapshot.PumpSlopeHigh);
        Assert.Null(snapshot.PumpTransitionSpeed);
        Assert.Null(snapshot.PumpTransitionFlow);
        Assert.Null(snapshot.PumpCalCrc);
        Assert.Null(snapshot.BiomassGear);

        // FlowmeterBootId is sticky: persists across frames
        Assert.Equal(1234L, snapshot.FlowmeterBootId);
    }

    [Fact]
    public void Simulator_applies_commands_and_emits_echoes()
    {
        var model = new DeviceModel(randomSeed: 1)
        {
            PumpEnabled = true,
            DistanceSensorEnabled = true,
            BiomassEnabled = true,
            FlowmeterEnabled = true,
        };

        // 1. Distance command
        var distCmd = CommandBuilders.DistanceConfig(offsetMm: 35.0, samplePeriodMs: 300, sendPeriodMs: 1200);
        Assert.True(WireCodec.ApplyCommand(model, distCmd.ToJson(), out _));
        Assert.Equal(35.0, model.DistanceOffsetMm);
        Assert.Equal(300, model.DistanceSamplePeriodMs);
        Assert.Equal(1200, model.DistanceSendPeriodMs);
        Assert.True(model.DistanceCommandPending);

        // Telemetry emits DistanceCommandPending: true and clears pending
        var frame1 = WireCodec.BuildTelemetry(model);
        Assert.Contains("\"DistanceCommandPending\":true", frame1);
        Assert.Contains("\"DistanceOffsetMm\":35.00", frame1);
        Assert.Contains("\"DistanceSamplePeriodMs\":300", frame1);
        Assert.Contains("\"DistanceSendPeriodMs\":1200", frame1);

        // Subsequent telemetry emits DistanceCommandPending: false
        var frame2 = WireCodec.BuildTelemetry(model);
        Assert.Contains("\"DistanceCommandPending\":false", frame2);

        // Distance Reset NVS
        var resetCmd = CommandBuilders.DistanceResetNvs();
        Assert.True(WireCodec.ApplyCommand(model, resetCmd.ToJson(), out _));
        Assert.Equal(20.0, model.DistanceOffsetMm);
        Assert.Equal(500, model.DistanceSamplePeriodMs);
        Assert.Equal(1000, model.DistanceSendPeriodMs);

        // 2. Flow tuning
        var flowCmd = CommandBuilders.FlowTuning(kp: 1.2, ki: 0.08, ffGain: 0.5, ffOffset: 0.2, rampRate: 3.5);
        Assert.True(WireCodec.ApplyCommand(model, flowCmd.ToJson(), out _));
        Assert.Equal(1.2, model.FlowKp);
        Assert.Equal(0.08, model.FlowKi);
        Assert.Equal(0.5, model.FlowFfGain);
        Assert.Equal(0.2, model.FlowFfOffset);
        Assert.Equal(3.5, model.FlowRampRate);

        var frameFlow = WireCodec.BuildTelemetry(model);
        Assert.Contains("\"FlowKp\":1.200", frameFlow);
        Assert.Contains("\"FlowKi\":0.0800", frameFlow);
        Assert.Contains("\"FlowFfGain\":0.500", frameFlow);
        Assert.Contains("\"FlowFfOffset\":0.200", frameFlow);
        Assert.Contains("\"FlowRampRate\":3.50", frameFlow);

        // 3. Pump calibration and reset volume
        var pumpCal = CommandBuilders.PumpCalibration(slope: 1.45, intercept: -0.12);
        Assert.True(WireCodec.ApplyCommand(model, pumpCal.ToJson(), out _));
        Assert.Equal(1.45, model.PumpSlope);
        Assert.Equal(-0.12, model.PumpIntercept);

        var pumpPid = CommandBuilders.PumpPid(kp: 2.0, ki: 0.5, kd: 0.02);
        Assert.True(WireCodec.ApplyCommand(model, pumpPid.ToJson(), out _));
        Assert.Equal(2.0, model.PumpPidKp);
        Assert.Equal(0.5, model.PumpPidKi);
        Assert.Equal(0.02, model.PumpPidKd);

        var resetVol = CommandBuilders.PumpResetVolume();
        Assert.True(WireCodec.ApplyCommand(model, resetVol.ToJson(), out _));
        Assert.True(model.PumpVolume < 0.001);

        var framePump = WireCodec.BuildTelemetry(model);
        Assert.Contains("\"PumpSlope\":1.4500", framePump);
        Assert.Contains("\"PumpIntercept\":-0.1200", framePump);

        // 4. Biomass tuning
        var bioIt = CommandBuilders.BiomassIt(3);
        Assert.True(WireCodec.ApplyCommand(model, bioIt.ToJson(), out _));
        Assert.Equal(200, model.BiomassIntegrationTimeMs);

        var bioPwm = CommandBuilders.BiomassPwm(80.0);
        Assert.True(WireCodec.ApplyCommand(model, bioPwm.ToJson(), out _));
        Assert.Equal(80.0, model.BiomassPwmPercent);

        var bioGear = CommandBuilders.BiomassGear(4);
        Assert.True(WireCodec.ApplyCommand(model, bioGear.ToJson(), out _));
        Assert.Equal(4, model.BiomassGear);

        var bioEma = CommandBuilders.BiomassEma(0.35);
        Assert.True(WireCodec.ApplyCommand(model, bioEma.ToJson(), out _));
        Assert.Equal(0.35, model.BiomassEma);

        var bioProbe = CommandBuilders.BiomassProbePeriod(750);
        Assert.True(WireCodec.ApplyCommand(model, bioProbe.ToJson(), out _));
        Assert.Equal(750, model.BiomassProbePeriodMs);

        var frameBio = WireCodec.BuildTelemetry(model);
        Assert.Contains("\"BiomassGear\":4", frameBio);
        Assert.Contains("\"BiomassEma\":0.350", frameBio);
        Assert.Contains("\"BiomassProbePeriodMs\":750", frameBio);
    }

    [Fact]
    public void Simulator_node_dropout_drops_echoes()
    {
        var model = new DeviceModel(randomSeed: 1)
        {
            PumpEnabled = true,
            DistanceSensorEnabled = true,
            BiomassEnabled = true,
            FlowmeterEnabled = true,
            Scenario = Scenario.NodeDropout,
        };

        var frame = WireCodec.BuildTelemetry(model);

        Assert.DoesNotContain("DistanceOffsetMm", frame);
        Assert.DoesNotContain("FlowKp", frame);
        Assert.DoesNotContain("PumpSlope", frame);
        Assert.DoesNotContain("BiomassGear", frame);
    }

    [Fact]
    public void Simulator_legacy_hub_drops_echoes()
    {
        var model = new DeviceModel(randomSeed: 1)
        {
            PumpEnabled = true,
            DistanceSensorEnabled = true,
            BiomassEnabled = true,
            FlowmeterEnabled = true,
            Scenario = Scenario.LegacyHub,
        };

        var frame = WireCodec.BuildTelemetry(model);

        Assert.DoesNotContain("DistanceOffsetMm", frame);
        Assert.DoesNotContain("DistanceCommandPending", frame);
        Assert.DoesNotContain("FlowKp", frame);
        Assert.DoesNotContain("FlowTransitionVoltage", frame);
        Assert.DoesNotContain("PumpSlope", frame);
        Assert.DoesNotContain("PumpSlopeLow", frame);
        Assert.DoesNotContain("PumpSlopeHigh", frame);
        Assert.DoesNotContain("PumpTransitionSpeed", frame);
        Assert.DoesNotContain("PumpTransitionFlow", frame);
        Assert.DoesNotContain("PumpCalCrc", frame);
        Assert.DoesNotContain("BiomassGear", frame);
    }

    [Fact]
    public void Simulator_normal_and_node_dropout_scenarios_update_viewmodels()
    {
        var model = new DeviceModel(randomSeed: 1)
        {
            DistanceSensorEnabled = true,
            FlowmeterEnabled = true,
            Scenario = Scenario.Normal,
        };

        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        var dispatcher = new StubDispatcher();
        using var foam = new FoamControlViewModel(device, settings, dispatcher);
        var flow = new FlowControlViewModel(initialMaxFlow: 10.0, dispatcher: dispatcher, settings: settings);
        var parser = new TelemetryParser();

        // 1. Initial normal frame
        var frame1 = WireCodec.BuildTelemetry(model);
        parser.Parse(frame1);
        var snapshot1 = parser.Readings.Snapshot();
        device.PushTelemetry(snapshot1);
        flow.UpdateTelemetry(snapshot1);

        Assert.True(foam.CanEditNodeConfig);
        Assert.True(flow.CanEditTuning);
        Assert.Contains("20", foam.AppliedOffsetText);

        // 2. Offset 20 -> 25.5 sends command and changes echo in the next frame
        foam.OffsetMmText = "25.5";
        foam.SendNodeConfigCommand.Execute(null);
        Assert.Single(dispatcher.Sent);

        Assert.True(WireCodec.ApplyCommand(model, dispatcher.Sent[0], out _));

        var frame2 = WireCodec.BuildTelemetry(model);
        parser.Parse(frame2);
        var snapshot2 = parser.Readings.Snapshot();
        device.PushTelemetry(snapshot2);

        Assert.Contains("25.5", foam.AppliedOffsetText.Replace(',', '.'));

        // 3. Node-dropout scenario: fields appear disabled with descriptive text
        model.Scenario = Scenario.NodeDropout;
        var frameDropout = WireCodec.BuildTelemetry(model);
        parser.Parse(frameDropout);
        var snapshotDropout = parser.Readings.Snapshot();
        device.PushTelemetry(snapshotDropout);
        flow.UpdateTelemetry(snapshotDropout);

        Assert.False(foam.CanEditNodeConfig);
        Assert.False(foam.CanSendNodeConfig);
        Assert.NotNull(foam.NodeConfigUnavailableText);
        Assert.Contains("desconectado", foam.NodeConfigUnavailableText, StringComparison.OrdinalIgnoreCase);

        Assert.False(flow.CanEditTuning);
        Assert.False(flow.CanSendTuning);
        Assert.NotNull(flow.TuningUnavailableText);
        Assert.Contains("desconectado", flow.TuningUnavailableText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Simulator_applies_dual_range_calibration_and_emits_echoes()
    {
        var model = new DeviceModel(randomSeed: 1)
        {
            PumpEnabled = true,
            FlowmeterEnabled = true,
        };

        // 1. Flow transition voltage command
        var flowCmd = CommandBuilders.FlowCalibration(
            maxFlow: 50.0,
            a1: 0.0, b1: 0.0,
            k1: 0.001, f1: 0.0, c1: 0.0,
            k2: 0.002, f2: 0.0, c2: 0.0,
            transitionVoltage: 0.0650);
        Assert.True(WireCodec.ApplyCommand(model, flowCmd.ToJson(), out _));
        Assert.Equal(0.0650, model.FlowTransitionVoltage);

        var flowFrame = WireCodec.BuildTelemetry(model);
        Assert.Contains("\"FlowTransitionVoltage\":0.0650", flowFrame);

        // 2. Pump dual-range calibration command
        var pumpCmd = CommandBuilders.PumpDualRangeCalibration(
            a1: 0, b1: 0, k1: 0, f1: 0.0035, c1: 0,
            k2: 0, f2: 0.0035, c2: 0, transitionSpeed: 250.0);
        Assert.True(WireCodec.ApplyCommand(model, pumpCmd.ToJson(), out _));
        Assert.Equal(0.003500, model.PumpF1);
        Assert.Equal(0.003500, model.PumpF2);
        Assert.Equal(250.0, model.PumpTransitionSpeed);
        Assert.Equal(0.8750, model.PumpTransitionFlow);
        var expectedCrc = DeviceModel.CalculatePumpCalibrationCrc(0, 0, 0, 0.0035, 0, 0, 0.0035, 0, 250.0);
        Assert.Equal(expectedCrc, (uint)model.PumpCalCrc);
        Assert.True(model.PumpCommandPending);

        // Telemetry frame 1 emits echoes and PumpCommandPending: true, then clears pending flag
        var pumpFrame1 = WireCodec.BuildTelemetry(model);
        Assert.Contains("\"PumpCommandPending\":true", pumpFrame1);
        Assert.Contains("\"PumpF1\":0.003500000", pumpFrame1);
        Assert.Contains("\"PumpF2\":0.003500000", pumpFrame1);
        Assert.Contains("\"PumpTransitionSpeed\":250.00", pumpFrame1);
        Assert.Contains("\"PumpTransitionFlow\":0.8750", pumpFrame1);
        Assert.Contains($"\"PumpCalCrc\":{expectedCrc}", pumpFrame1);

        // Telemetry frame 2 has PumpCommandPending: false
        var pumpFrame2 = WireCodec.BuildTelemetry(model);
        Assert.Contains("\"PumpCommandPending\":false", pumpFrame2);
    }

    [Fact]
    public void Simulator_rejects_invalid_dual_range_calibration_and_preserves_previous()
    {
        var model = new DeviceModel(randomSeed: 1)
        {
            PumpEnabled = true,
            FlowmeterEnabled = true,
        };

        var initialFlowV = model.FlowTransitionVoltage;
        var initialLow = model.PumpSlopeLow;
        var initialHigh = model.PumpSlopeHigh;
        var initialSpeed = model.PumpTransitionSpeed;
        var initialFlow = model.PumpTransitionFlow;
        var initialCrc = model.PumpCalCrc;

        // Invalid flow transition voltage (>= 3.3 V or negative)
        WireCodec.ApplyCommand(model, "{\"flowTransitionVoltage\":3.5}", out _);
        Assert.Equal(initialFlowV, model.FlowTransitionVoltage);
        WireCodec.ApplyCommand(model, "{\"flowTransitionVoltage\":-0.1}", out _);
        Assert.Equal(initialFlowV, model.FlowTransitionVoltage);

        // Incomplete dual-range calibration (missing fields)
        WireCodec.ApplyCommand(model, "{\"pumpSlopeLow\":0.005}", out _);
        Assert.Equal(initialLow, model.PumpSlopeLow);
        Assert.False(model.PumpCommandPending);

        // Invalid pump transition speed (>= 1000 or negative)
        WireCodec.ApplyCommand(model, "{\"pumpSlopeLow\":0.003,\"pumpSlopeHigh\":0.004,\"pumpTransitionSpeed\":1000.0,\"pumpTransitionFlow\":1.0}", out _);
        Assert.Equal(initialLow, model.PumpSlopeLow);
        Assert.False(model.PumpCommandPending);

        // Physical constraint violation: transitionFlow - slopeLow * transitionSpeed < 0
        WireCodec.ApplyCommand(model, "{\"pumpSlopeLow\":0.01,\"pumpSlopeHigh\":0.01,\"pumpTransitionSpeed\":200.0,\"pumpTransitionFlow\":1.0}", out _);
        Assert.Equal(initialLow, model.PumpSlopeLow);
        Assert.Equal(initialHigh, model.PumpSlopeHigh);
        Assert.Equal(initialSpeed, model.PumpTransitionSpeed);
        Assert.Equal(initialFlow, model.PumpTransitionFlow);
        Assert.Equal(initialCrc, model.PumpCalCrc);
        Assert.False(model.PumpCommandPending);

        // A complete v3.12 block that is discontinuous at St is rejected atomically.
        WireCodec.ApplyCommand(model,
            "{\"pumpA1\":0,\"pumpB1\":0,\"pumpK1\":0,\"pumpF1\":0.0035,\"pumpC1\":0," +
            "\"pumpK2\":0,\"pumpF2\":0.0045,\"pumpC2\":0,\"pumpTransitionSpeed\":250}", out _);
        Assert.Equal(initialSpeed, model.PumpTransitionSpeed);
        Assert.Equal(initialCrc, model.PumpCalCrc);
        Assert.False(model.PumpCommandPending);
    }
}
