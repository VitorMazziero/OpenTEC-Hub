using OpenTECHub.Protocol;
using OpenTECHub.Simulator;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// The seven servo shapes the app has to handle, emitted by the simulator and decoded
/// back by the real parser.
/// </summary>
/// <remarks>
/// <para>
/// Round-tripping is the point. Asserting on the simulator's own properties would only
/// prove the simulator agrees with itself; running its frames through
/// <see cref="TelemetryParser"/> proves the two sides of the contract agree, which is what
/// makes the simulator usable for building the card against instead of hardware.
/// </para>
/// <para>
/// The seven are the ones the bench exercised on 2026-09-02. Scenarios 3 to 5 and 7 are
/// operating states reached by commanding, not fault injection - which is itself the point:
/// routing off with the node present is a configuration, not a failure.
/// </para>
/// </remarks>
public class ServoSimulatorTests
{
    private static (DeviceModel Model, AcceleratedClock Clock) Build(int rpm = 600)
    {
        var clock = new AcceleratedClock();
        var model = new DeviceModel(clock: clock, randomSeed: 20260902) { MotorRpm = rpm };
        return (model, clock);
    }

    private static SensorReadings Decode(DeviceModel model)
    {
        var parser = new TelemetryParser();
        Assert.Equal(ParseOutcome.Updated, parser.Parse(WireCodec.BuildTelemetry(model)));
        return parser.Readings;
    }

    private static void Settle(DeviceModel model, double seconds = 60.0)
    {
        for (var elapsed = 0.0; elapsed < seconds; elapsed += 0.5)
        {
            model.Tick(0.5);
        }
    }

    // ── 1. A Hub from before the servo contract ──────────────────────────────

    [Fact]
    public void A_legacy_hub_emits_no_servo_key_at_all()
    {
        var (model, _) = Build();
        model.Scenario = Scenario.LegacyHub;

        var frame = WireCodec.BuildTelemetry(model);
        Assert.DoesNotContain("Servo", frame, StringComparison.Ordinal);

        // And the app reads it as "nothing claimed", never as a failed device.
        var readings = Decode(model);
        Assert.False(readings.HasServoTelemetry);
        Assert.Null(readings.ServoCommEnabled);
    }

    // ── 2. Node absent, routing still on: the one real failure ───────────────

    [Fact]
    public void An_absent_node_reports_offline_with_routing_still_on()
    {
        var (model, _) = Build();
        model.Scenario = Scenario.NodeDropout;

        var readings = Decode(model);

        Assert.True(readings.HasServoTelemetry);
        Assert.False(readings.ServoOnline);
        Assert.True(readings.ServoCommEnabled);
        Assert.False(readings.HasServoSample);
        Assert.Equal(SensorReadings.NotReceived, readings.ServoRpm);
    }

    // ── 3. Node present, motor stopped: zeros that are measurements ──────────

    [Fact]
    public void A_stopped_motor_reports_zeros_that_are_real_readings()
    {
        var (model, _) = Build(rpm: 0);
        model.Tick(1.0);

        var readings = Decode(model);

        Assert.True(readings.ServoOnline);
        Assert.True(readings.HasServoSample);
        Assert.Equal(0.0, readings.ServoRpm);
        Assert.Equal(0.0, readings.ServoPowerW);
        Assert.Equal(1, readings.ServoState); // READY, energised but not turning
        Assert.Equal(0, readings.ServoAlarm);
    }

    [Fact]
    public void Changing_to_uart_cn1_forces_zero_and_echoes_the_effective_route()
    {
        var (model, _) = Build(rpm: 600);

        Assert.True(WireCodec.ApplyCommand(
            model, """{"motorControlMode":0,"motorSetpoint":600}""", out _));

        var readings = Decode(model);
        Assert.Equal(0, model.MotorRpm);
        Assert.False(readings.MotorControlViaModbus);
        Assert.Equal(0, readings.ServoMotorRouteAck);
    }

    // ── 4. Node present and turning: the ten values, mutually coherent ───────

    /// <summary>
    /// Turning, with torque, N·m and watts that reproduce each other by hand.
    /// </summary>
    /// <remarks>
    /// The arithmetic is the node's, from section 1.2 of the plan:
    /// <c>torqueNm = torque% / 100 × 1.27</c> and <c>W = torqueNm × rpm × 2π/60</c>. If the
    /// simulator emitted numbers that did not close, the card would be built against
    /// physics that cannot happen and the discrepancy would first appear on hardware.
    /// </remarks>
    [Fact]
    public void A_turning_motor_reports_ten_values_that_agree_with_each_other()
    {
        var (model, _) = Build(rpm: 600);
        Settle(model);

        var r = Decode(model);

        Assert.True(r.HasServoSample);
        Assert.Equal(2, r.ServoState); // SON
        Assert.InRange(r.ServoRpm, 599.0, 601.0);

        // The power-assay model adds the liquid load to the dry-running tare.
        Assert.InRange(r.ServoTorquePct, 10.0, 13.5);

        var expectedNm = r.ServoTorquePct / 100.0 * 1.27;
        Assert.Equal(expectedNm, r.ServoTorqueNm, precision: 3);

        var expectedW = r.ServoTorqueNm * r.ServoRpm * 2.0 * Math.PI / 60.0;
        Assert.Equal(expectedW, r.ServoPowerW, precision: 1);

        Assert.True(r.ServoCommOk > 0);
    }

    // ── 5. Routing off with the node present ─────────────────────────────────

    /// <summary>
    /// The distinction the v8 contract could not express, and the one an offline check misses.
    /// </summary>
    [Fact]
    public void Routing_off_keeps_the_node_present_and_removes_only_the_values()
    {
        var (model, _) = Build();
        model.Tick(1.0);
        Assert.True(Decode(model).HasServoSample);

        WireCodec.ApplyCommand(model, """{"servoComm":0}""", out _);
        model.Tick(1.0);

        var r = Decode(model);
        Assert.True(r.ServoOnline);          // still pushing
        Assert.False(r.ServoCommEnabled);    // but not routed
        Assert.False(r.HasServoSample);
        Assert.Equal(SensorReadings.NotReceived, r.ServoPowerW);
    }

    [Fact]
    public void Routing_back_on_restores_the_values()
    {
        var (model, _) = Build();
        WireCodec.ApplyCommand(model, """{"servoComm":0}""", out _);
        model.Tick(1.0);

        WireCodec.ApplyCommand(model, """{"servoComm":1}""", out _);
        model.Tick(1.0);

        Assert.True(Decode(model).HasServoSample);
    }

    // ── 6. Alarm ─────────────────────────────────────────────────────────────

    [Fact]
    public void An_alarm_carries_the_panel_code_and_stops_the_shaft()
    {
        var (model, _) = Build();
        model.Scenario = Scenario.ServoAlarm;
        model.Tick(1.0);

        var r = Decode(model);

        Assert.Equal(3, r.ServoState);
        Assert.Equal(0x0011, r.ServoAlarm); // AL011 on the drive's panel
        Assert.Equal(0.0, r.ServoRpm);
    }

    // ── 7. Energy: reset by command, and backwards after a node reboot ───────

    /// <summary>
    /// The reset is queued, collected on the node's next pull, and only then observable.
    /// </summary>
    /// <remarks>
    /// There is no acknowledgement on this link, so the fall in <c>ServoEnergyWh</c> is the
    /// confirmation. The delay is real and has to be reproduced: an app that expected the
    /// value to drop in the same frame would report the command as lost.
    /// </remarks>
    [Fact]
    public void The_energy_reset_is_queued_then_collected_then_observable()
    {
        var (model, _) = Build();
        for (var i = 0; i < 30; i++)
        {
            model.Tick(1.0);
        }

        var accumulated = Decode(model).ServoEnergyWh;
        Assert.True(accumulated > 0, "energy should have accumulated while turning");

        WireCodec.ApplyCommand(model, """{"resetServoEnergy":1}""", out _);

        var queued = Decode(model);
        Assert.True(queued.ServoCommandPending);
        Assert.Equal(1, queued.ServoCommandQueueDepth);

        // Two seconds later the node pulls it and acts.
        model.Tick(2.0);

        var after = Decode(model);
        Assert.False(after.ServoCommandPending);
        Assert.Equal(0, after.ServoCommandQueueDepth);
        Assert.True(after.ServoEnergyWh < accumulated);
    }

    /// <summary>Zero is not a command, so nothing is queued.</summary>
    [Fact]
    public void A_zero_reset_queues_nothing()
    {
        var (model, _) = Build();

        WireCodec.ApplyCommand(model, """{"resetServoEnergy":0}""", out _);

        Assert.Equal(0, Decode(model).ServoCommandQueueDepth);
    }

    /// <summary>
    /// A node that comes back has rebooted, so its accumulator starts over.
    /// </summary>
    /// <remarks>
    /// The energy stepping <i>down</i> is normal and the chart has to draw it. A series that
    /// enforced monotonicity would hide a node restart completely.
    /// </remarks>
    [Fact]
    public void Energy_starts_over_when_the_node_comes_back()
    {
        var (model, _) = Build();
        for (var i = 0; i < 30; i++)
        {
            model.Tick(1.0);
        }

        var before = Decode(model).ServoEnergyWh;
        Assert.True(before > 0);

        model.Scenario = Scenario.NodeDropout;
        model.Tick(1.0);

        model.Scenario = Scenario.Normal;
        model.Tick(1.0);

        Assert.True(Decode(model).ServoEnergyWh < before);
    }

    // ── The queue's back-pressure ────────────────────────────────────────────

    /// <summary>
    /// Eight fit; the ninth is refused without overwriting any of them.
    /// </summary>
    /// <remarks>
    /// Refusal is back-pressure, not an error - the real Hub answers
    /// <c>503 Command queue full</c> and the app is expected to retry. Overwriting would be
    /// worse than refusing: a command would vanish with the frame reported as accepted.
    /// </remarks>
    [Fact]
    public void The_queue_holds_eight_and_refuses_the_ninth()
    {
        var (model, _) = Build();

        for (var i = 0; i < 8; i++)
        {
            Assert.True(model.EnqueueServoCommand("reset_energy"));
        }

        Assert.False(model.EnqueueServoCommand("reset_energy"));
        Assert.Equal(8, model.ServoCommandQueueDepth);
    }

    /// <summary>A full queue takes sixteen seconds to drain, at one per 2 s pull.</summary>
    [Fact]
    public void A_full_queue_needs_sixteen_seconds_to_empty()
    {
        var (model, _) = Build();
        for (var i = 0; i < 8; i++)
        {
            model.EnqueueServoCommand("reset_energy");
        }

        for (var i = 0; i < 7; i++)
        {
            model.Tick(2.0);
        }

        Assert.Equal(1, model.ServoCommandQueueDepth);

        model.Tick(2.0);
        Assert.Equal(0, model.ServoCommandQueueDepth);
    }

    // ── Poll interval ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(250)]
    [InlineData(2000)]
    [InlineData(10000)]
    public void A_poll_interval_inside_the_band_is_accepted(int pollMs)
    {
        var (model, _) = Build();

        WireCodec.ApplyCommand(model, $$"""{"servoPollMs":{{pollMs}}}""", out _);

        Assert.Equal(pollMs, model.ServoPollMs);
        Assert.Equal(1, model.ServoCommandQueueDepth);
    }

    [Theory]
    [InlineData(249)]
    [InlineData(10001)]
    public void A_poll_interval_outside_the_band_changes_nothing(int pollMs)
    {
        var (model, _) = Build();

        WireCodec.ApplyCommand(model, $$"""{"servoPollMs":{{pollMs}}}""", out _);

        Assert.Equal(1000, model.ServoPollMs); // the default still stands
        Assert.Equal(0, model.ServoCommandQueueDepth);
    }

    /// <summary>
    /// A longer interval slows the counter, which is the only observable effect.
    /// </summary>
    /// <remarks>
    /// With no acknowledgement, the growth rate of <c>ServoCommOk</c> is how the app can tell
    /// a poll-interval command took. The bench watched it halve from +6 to +3 per 2 s frame.
    /// </remarks>
    [Fact]
    public void A_longer_interval_slows_the_transaction_counter()
    {
        var (fast, _) = Build();
        fast.Tick(10.0);
        var fastCount = Decode(fast).ServoCommOk;

        var (slow, _) = Build();
        WireCodec.ApplyCommand(slow, """{"servoPollMs":2000}""", out _);
        slow.Tick(10.0);
        var slowCount = Decode(slow).ServoCommOk;

        Assert.True(slowCount < fastCount, $"expected {slowCount} < {fastCount}");
    }

    /// <summary>
    /// The routing echo diverges after a Hub reboot, for the servo like every other device.
    /// </summary>
    [Fact]
    public void Routing_drift_shows_the_hub_disagreeing_with_the_switch()
    {
        var (model, _) = Build();
        model.Scenario = Scenario.RoutingDrift;
        model.Tick(1.0);

        // The operator's switch says on; the Hub's NVS says otherwise.
        Assert.True(model.ServoEnabled);
        Assert.False(Decode(model).ServoCommEnabled);
    }
}
