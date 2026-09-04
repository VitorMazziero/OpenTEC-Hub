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

        Assert.True(WireCodec.ApplyCommand(model, """{"flowSetpoint":5}""", out _));
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
