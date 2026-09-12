using OpenTECHub.Protocol;
using OpenTECHub.Simulator;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>The phase-1 power-assay plant behind the existing servo wire contract.</summary>
public sealed class PowerServoSimulatorTests
{
    private static readonly IReadOnlyList<ServoTorqueNoisePoint> NoNoise =
        [new(0.0, 0.0)];

    [Fact]
    public void Steady_torque_sums_each_stage_power_number_and_tare()
    {
        var options = new ServoPowerModelOptions
        {
            LiquidDensityKgM3 = 1000.0,
            MotorRatedTorqueNm = 2.0,
            SpeedTimeConstantSeconds = 0.0,
            TorqueTimeConstantSeconds = 0.0,
            TorqueNoiseCurve = NoNoise,
            Impellers =
            [
                new("estágio A", 0.10, 4.0, 0.4, 0.001),
                new("estágio B", 0.08, 2.0, 0.6, 0.002),
            ],
        };
        var model = new DeviceModel(
            clock: new AcceleratedClock(), randomSeed: 1, servoPowerModel: options)
        {
            MotorRpm = 600,
        };

        model.Tick(0.5);

        const double rpm = 600.0;
        var n = rpm / 60.0;
        var omega = rpm * 2.0 * Math.PI / 60.0;
        var liquidPower = 1000.0 * 4.0 * Math.Pow(n, 3) * Math.Pow(0.10, 5)
                          + 1000.0 * 2.0 * Math.Pow(n, 3) * Math.Pow(0.08, 5);
        var tarePercent = (0.4 + 0.001 * rpm) + (0.6 + 0.002 * rpm);
        var expectedTorqueNm = liquidPower / omega + tarePercent / 100.0 * 2.0;

        Assert.Equal(expectedTorqueNm, model.ServoTorqueNm, precision: 10);
        Assert.Equal(
            expectedTorqueNm * model.ServoRpm * 2.0 * Math.PI / 60.0,
            model.ServoPowerW,
            precision: 10);
    }

    [Fact]
    public void Speed_and_torque_settle_in_first_order_after_a_new_motor_setpoint()
    {
        var options = ServoPowerModelOptions.Default with
        {
            SpeedTimeConstantSeconds = 2.0,
            TorqueTimeConstantSeconds = 5.0,
            TorqueNoiseCurve = NoNoise,
        };
        var model = new DeviceModel(
            clock: new AcceleratedClock(), randomSeed: 2, servoPowerModel: options)
        {
            MotorRpm = 300,
        };
        Advance(model, 60.0);
        var torqueAt300 = model.ServoTorquePct;

        WireCodec.ApplyCommand(model, """{"motorSetpoint":600}""", out _);
        model.Tick(1.0);

        Assert.InRange(model.ServoRpm, 417.0, 419.0);
        Assert.True(model.ServoTorquePct > torqueAt300);

        Advance(model, 60.0);
        Assert.InRange(model.ServoRpm, 599.0, 601.0);
    }

    [Fact]
    public void Gas_flow_change_has_a_settling_response_without_adding_the_phase_2_knee()
    {
        var options = ServoPowerModelOptions.Default with
        {
            SpeedTimeConstantSeconds = 0.0,
            TorqueTimeConstantSeconds = 6.0,
            TorqueNoiseCurve = NoNoise,
            ReferenceGasFlowLpm = 5.0,
            GassedLiquidPowerRatio = 0.80,
        };
        var model = new DeviceModel(
            clock: new AcceleratedClock(), randomSeed: 3, servoPowerModel: options);

        Assert.True(WireCodec.ApplyCommand(model, """{"motorSetpoint":600}""", out _));
        Advance(model, 60.0);
        var ungassed = model.ServoTorquePct;

        // A/B/C rig: gas needs a destination — A is valve_2 on the default wiring (plan §1.3.1).
        Assert.True(WireCodec.ApplyCommand(model, """{"flowSetpoint":5,"valve_2":1}""", out _));
        model.Tick(1.0);
        var transient = model.ServoTorquePct;
        Advance(model, 90.0);
        var gassed = model.ServoTorquePct;

        Assert.InRange(transient, gassed, ungassed);
        Assert.True(gassed < ungassed);
    }

    [Theory]
    [InlineData(0, 0.06)]
    [InlineData(300, 0.62)]
    [InlineData(600, 0.41)]
    public void Torque_noise_matches_the_measured_bench_curve(int rpm, double expectedSigma)
    {
        var options = ServoPowerModelOptions.Default with
        {
            SpeedTimeConstantSeconds = 0.0,
            TorqueTimeConstantSeconds = 0.0,
        };
        var model = new DeviceModel(
            clock: new AcceleratedClock(), randomSeed: 20260903, servoPowerModel: options)
        {
            MotorRpm = rpm,
        };
        var samples = new double[20_000];

        for (var i = 0; i < samples.Length; i++)
        {
            model.Tick(0.5);
            samples[i] = model.ServoTorquePct;
        }

        var mean = samples.Average();
        var sigma = Math.Sqrt(samples.Sum(value => Math.Pow(value - mean, 2)) / (samples.Length - 1));
        Assert.InRange(sigma, expectedSigma * 0.97, expectedSigma * 1.03);
    }

    [Fact]
    public void Commands_reach_the_power_model_through_the_real_telemetry_contract()
    {
        var options = ServoPowerModelOptions.Default with
        {
            SpeedTimeConstantSeconds = 2.0,
            TorqueTimeConstantSeconds = 4.0,
            TorqueNoiseCurve = NoNoise,
        };
        var model = new DeviceModel(
            clock: new AcceleratedClock(), randomSeed: 4, servoPowerModel: options);

        Assert.True(WireCodec.ApplyCommand(model, """{"motorSetpoint":600,"flowSetpoint":0}""", out _));
        model.Tick(1.0);
        var transient = Decode(model);
        Assert.InRange(transient.ServoRpm, 235.0, 237.0);

        Advance(model, 60.0);
        var settled = Decode(model);
        Assert.InRange(settled.ServoRpm, 599.0, 601.0);
        Assert.True(settled.ServoTorqueNm > 0.0);
        Assert.Equal(
            settled.ServoTorqueNm * settled.ServoRpm * 2.0 * Math.PI / 60.0,
            settled.ServoPowerW,
            precision: 1);
    }

    // ---- Gas, relief valve and flooding dynamics (Phase 2 Step 3) --------

    /// <summary>A/B/C rig, default wiring: C (vent) shares valve_1 with B; A (reactor) is valve_2.</summary>
    [Fact]
    public void Vent_C_purges_gas_externally_without_dropping_reactor_torque()
    {
        var options = ServoPowerModelOptions.Default with
        {
            SpeedTimeConstantSeconds = 0.0,
            TorqueTimeConstantSeconds = 0.0,
            TorqueNoiseCurve = NoNoise,
        };
        var model = new DeviceModel(
            clock: new AcceleratedClock(), randomSeed: 10, servoPowerModel: options)
        {
            MotorRpm = 600,
            NitrogenSourceOpen = false,
        };

        Advance(model, 10.0);
        var ungassedTorque = model.ServoTorquePct;

        // Open C (B/C output = valve_1 on the default wiring), keep A shut, command 5 L/min
        WireCodec.ApplyCommand(model, "{\"flowSetpoint\":5.0,\"Valve1\":1,\"Valve2\":0}", out _);
        Advance(model, 10.0);

        // Gas is flowing through flowmeter
        Assert.True(model.ReadFlow() > 4.5);
        // Gas does not enter reactor
        Assert.Equal(0.0, model.ReadReactorFlow());
        // Torque in the vessel did NOT drop
        Assert.Equal(ungassedTorque, model.ServoTorquePct, precision: 4);
    }

    [Fact]
    public void Switching_from_C_to_A_delivers_flow_and_reduces_power()
    {
        var options = ServoPowerModelOptions.Default with
        {
            SpeedTimeConstantSeconds = 0.0,
            TorqueTimeConstantSeconds = 0.0,
            TorqueNoiseCurve = NoNoise,
            GassedLiquidPowerRatio = 0.75,
        };
        var model = new DeviceModel(
            clock: new AcceleratedClock(), randomSeed: 11, servoPowerModel: options)
        {
            MotorRpm = 600,
            NitrogenSourceOpen = false,
        };

        // 1. Settle on the vent (C = valve_1)
        WireCodec.ApplyCommand(model, "{\"flowSetpoint\":5.0,\"Valve1\":1,\"Valve2\":0}", out _);
        Advance(model, 10.0);
        var ungassedTorque = model.ServoTorquePct;

        // 2. One-frame switchover: close B/C (valve_1=0), open A (valve_2=1)
        WireCodec.ApplyCommand(model, "{\"Valve1\":0,\"Valve2\":1}", out _);
        Advance(model, 10.0);

        // Gas enters reactor
        Assert.True(model.ReadReactorFlow() > 4.5);
        // Liquid torque dropped
        Assert.True(model.ServoTorquePct < ungassedTorque);
    }

    [Fact]
    public void Direct_flow_startup_exhibits_initial_overshoot_pulse()
    {
        var options = ServoPowerModelOptions.Default with
        {
            SpeedTimeConstantSeconds = 0.0,
            TorqueTimeConstantSeconds = 0.0,
            TorqueNoiseCurve = NoNoise,
            VentFlowPulseMagnitude = 3.0,
            VentFlowPulseDurationSeconds = 4.0,
        };
        var model = new DeviceModel(
            clock: new AcceleratedClock(), randomSeed: 12, servoPowerModel: options);

        // Command gas flow directly
        // A/B/C rig: the start-up pulse is exercised on the vent (C = valve_1, default wiring).
        WireCodec.ApplyCommand(model, "{\"flowSetpoint\":4.0,\"Valve1\":1}", out _);
        model.Tick(0.1);

        // Immediate read shows pulse overshoot
        var immediateFlow = model.ReadFlow();
        Assert.True(immediateFlow > 5.0, $"Expected overshoot > 5.0, got {immediateFlow}");

        // After pulse duration expires, settles back to setpoint
        Advance(model, 15.0);
        Assert.InRange(model.ReadFlow(), 3.9, 4.1);
    }

    [Fact]
    public void FlowCommand_loopback_and_pending_ack_confirmation()
    {
        var model = new DeviceModel(clock: new AcceleratedClock(), randomSeed: 13);

        var initialId = model.FlowCommandId;
        WireCodec.ApplyCommand(model, "{\"flowSetpoint\":2.5}", out _);

        // Immediately pending
        Assert.Equal(initialId + 1, model.FlowCommandId);
        Assert.True(model.FlowCommandPending);

        // Advance 0.3s past ACK delay (0.15s)
        model.Tick(0.3);

        Assert.False(model.FlowCommandPending);
        Assert.Equal(model.FlowCommandId, model.FlowCommandAck);

        // Verify decoded telemetry carries pending=false and matching ack
        var readings = Decode(model);
        Assert.False(readings.FlowCommandPending);
        Assert.Equal(model.FlowCommandId, readings.FlowCommandAck);
    }

    [Fact]
    public void Aerated_power_curve_simulates_flooding_minimum()
    {
        var options = ServoPowerModelOptions.Default with
        {
            SpeedTimeConstantSeconds = 0.0,
            TorqueTimeConstantSeconds = 0.0,
            TorqueNoiseCurve = NoNoise,
            SimulateFloodingKnee = true,
            GassedLiquidPowerRatio = 0.60,
            VesselDiameterM = 0.190,
            Impellers = [new("Rushton", 0.060, 5.0, 0.5, 0.001)],
        };
        var model = new DeviceModel(
            clock: new AcceleratedClock(), randomSeed: 14, servoPowerModel: options)
        {
            MotorRpm = 300,
        };

        // Ungassed torque
        Advance(model, 10.0);
        var ungassedTorque = model.ServoTorquePct;

        // Moderate aeration (below flooding) through A = valve_2 on the default wiring: torque drops
        WireCodec.ApplyCommand(model, "{\"flowSetpoint\":1.0,\"Valve1\":0,\"Valve2\":1}", out _);
        Advance(model, 10.0);
        var moderateTorque = model.ServoTorquePct;
        Assert.True(moderateTorque < ungassedTorque);

        // Flooding aeration: torque reaches lowest level
        WireCodec.ApplyCommand(model, "{\"flowSetpoint\":3.0}", out _);
        Advance(model, 10.0);
        var nearFloodingTorque = model.ServoTorquePct;
        Assert.True(nearFloodingTorque < moderateTorque);
    }

    private static SensorReadings Decode(DeviceModel model)
    {
        var parser = new TelemetryParser();
        Assert.Equal(ParseOutcome.Updated, parser.Parse(WireCodec.BuildTelemetry(model)));
        return parser.Readings;
    }

    private static void Advance(DeviceModel model, double seconds)
    {
        for (var elapsed = 0.0; elapsed < seconds; elapsed += 0.5)
        {
            model.Tick(Math.Min(0.5, seconds - elapsed));
        }
    }
}
