using OpenTECHub.Protocol;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// Decoding the ASDA-B2 servo node, where presence and routing are orthogonal.
/// </summary>
/// <remarks>
/// <para>
/// The whole point of the v9 contract is that <c>ServoOnline</c> and
/// <c>ServoCommEnabled</c> answer different questions, and that all four combinations
/// are legitimate. Only one is a failure - a node that is absent while routing is on.
/// Collapsing the other three into it produces an alarm that can never be cleared,
/// which is exactly what the bench module would raise forever.
/// </para>
/// <para>
/// The frames here are the shapes the real Hub emitted on 2026-09-02, not invented ones.
/// </para>
/// </remarks>
public class ServoTelemetryParserTests
{
    /// <summary>Node present, routing on: the four flags plus all ten measurements.</summary>
    private const string OperatingFrame =
        """
        {"ServoOnline":true,"ServoCommEnabled":true,"ServoCommandPending":false,
         "ServoCommandQueueDepth":0,"ServoRpm":92.7,"ServoTorquePct":1.4,
         "ServoTorqueNm":0.0178,"ServoLoadPct":1,"ServoPowerW":0.17,
         "ServoEnergyWh":0.020717,"ServoState":2,"ServoAlarm":0,
         "ServoCommOk":255,"ServoCommErr":1}
        """;

    /// <summary>Routing switched off with the node still pushing: no measurements.</summary>
    private const string RoutingOffFrame =
        """{"ServoOnline":true,"ServoCommEnabled":false,"ServoCommandPending":false,"ServoCommandQueueDepth":0}""";

    /// <summary>Node gone, routing still on. The one combination that is a failure.</summary>
    private const string NodeMissingFrame =
        """{"ServoOnline":false,"ServoCommEnabled":true,"ServoCommandPending":false,"ServoCommandQueueDepth":0}""";

    /// <summary>The bench module, told once it has no servo. Permanent and correct.</summary>
    private const string NoServoModuleFrame =
        """{"ServoOnline":false,"ServoCommEnabled":false,"ServoCommandPending":false,"ServoCommandQueueDepth":0}""";

    [Fact]
    public void An_operating_frame_lands_every_value()
    {
        var parser = new TelemetryParser();

        Assert.Equal(ParseOutcome.Updated, parser.Parse(OperatingFrame));

        var r = parser.Readings;
        Assert.True(r.HasServoTelemetry);
        Assert.True(r.ServoOnline);
        Assert.True(r.ServoCommEnabled);
        Assert.False(r.ServoCommandPending);
        Assert.Equal(0, r.ServoCommandQueueDepth);

        Assert.Equal(92.7, r.ServoRpm);
        Assert.Equal(1.4, r.ServoTorquePct);
        Assert.Equal(0.0178, r.ServoTorqueNm);
        Assert.Equal(1.0, r.ServoLoadPct);
        Assert.Equal(0.17, r.ServoPowerW);
        Assert.Equal(0.020717, r.ServoEnergyWh);
        Assert.Equal(2, r.ServoState);
        Assert.Equal(0, r.ServoAlarm);
        Assert.Equal(255, r.ServoCommOk);
        Assert.Equal(1, r.ServoCommErr);
    }

    /// <summary>
    /// Routing off is not absence: presence stays true and only the values leave.
    /// </summary>
    /// <remarks>
    /// This is the case an offline check alone would miss. The node is still pushing, so
    /// nothing ages out, and holding the last sample would leave a live-looking reading
    /// on screen for as long as routing stayed off. Exercised against the real Hub on
    /// 2026-09-02 with <c>{"servoComm":0}</c>.
    /// </remarks>
    [Fact]
    public void Routing_off_keeps_presence_and_drops_only_the_measurements()
    {
        var parser = new TelemetryParser();
        parser.Parse(OperatingFrame);

        parser.Parse(RoutingOffFrame);

        var r = parser.Readings;
        Assert.True(r.ServoOnline);
        Assert.False(r.ServoCommEnabled);

        Assert.Equal(SensorReadings.NotReceived, r.ServoRpm);
        Assert.Equal(SensorReadings.NotReceived, r.ServoTorquePct);
        Assert.Equal(SensorReadings.NotReceived, r.ServoPowerW);
        Assert.Equal(SensorReadings.NotReceived, r.ServoEnergyWh);
        Assert.Equal(-1, r.ServoState);
        Assert.Equal(-1, r.ServoCommOk);
    }

    [Fact]
    public void Routing_back_on_restores_the_values_from_the_next_frame()
    {
        var parser = new TelemetryParser();
        parser.Parse(OperatingFrame);
        parser.Parse(RoutingOffFrame);

        parser.Parse(OperatingFrame);

        Assert.True(parser.Readings.ServoCommEnabled);
        Assert.Equal(92.7, parser.Readings.ServoRpm);
    }

    [Fact]
    public void A_missing_node_with_routing_on_is_the_failure_case()
    {
        var parser = new TelemetryParser();
        parser.Parse(OperatingFrame);

        parser.Parse(NodeMissingFrame);

        Assert.True(parser.Readings.HasServoTelemetry);
        Assert.False(parser.Readings.ServoOnline);
        Assert.True(parser.Readings.ServoCommEnabled);
        Assert.Equal(SensorReadings.NotReceived, parser.Readings.ServoRpm);
    }

    /// <summary>
    /// Both flags false is a module without a servo, and must stay distinguishable.
    /// </summary>
    /// <remarks>
    /// The bench module was configured this way on 2026-09-02 with
    /// <c>{"servoComm":0}</c>, persisted in NVS and confirmed across a reboot. Anything
    /// that reads it as a failure raises an event that can never be resolved.
    /// </remarks>
    [Fact]
    public void A_module_without_a_servo_reports_both_flags_false()
    {
        var parser = new TelemetryParser();

        parser.Parse(NoServoModuleFrame);

        Assert.True(parser.Readings.HasServoTelemetry);
        Assert.False(parser.Readings.ServoOnline);
        Assert.False(parser.Readings.ServoCommEnabled);
    }

    /// <summary>
    /// A Hub that predates the keys has claimed nothing, which is not the same as offline.
    /// </summary>
    [Fact]
    public void An_older_hub_leaves_the_servo_unclaimed()
    {
        var parser = new TelemetryParser();

        Assert.Equal(ParseOutcome.Updated, parser.Parse("""{"Tempval":30.2,"Time":12.5}"""));

        Assert.False(parser.Readings.HasServoTelemetry);
        Assert.False(parser.Readings.ServoOnline);

        // Null, not false: no claim was made about routing either way.
        Assert.Null(parser.Readings.ServoCommEnabled);
        Assert.Null(parser.Readings.ServoCommandPending);
    }

    /// <summary>
    /// Zero rpm is a stopped motor, not missing data.
    /// </summary>
    /// <remarks>
    /// If this collapsed to the sentinel the UI would show a dash for a motor that is
    /// genuinely stopped, and the operator would read it as a dead node.
    /// </remarks>
    [Fact]
    public void Zero_rpm_is_a_reading_and_not_an_absence()
    {
        var parser = new TelemetryParser();

        parser.Parse(
            """
            {"ServoOnline":true,"ServoCommEnabled":true,"ServoRpm":0,"ServoTorquePct":0,
             "ServoTorqueNm":0,"ServoLoadPct":0,"ServoPowerW":0,"ServoEnergyWh":0,
             "ServoState":1,"ServoAlarm":0,"ServoCommOk":9,"ServoCommErr":0}
            """);

        Assert.Equal(0.0, parser.Readings.ServoRpm);
        Assert.Equal(0.0, parser.Readings.ServoPowerW);
        Assert.Equal(1, parser.Readings.ServoState);
        Assert.NotEqual(SensorReadings.NotReceived, parser.Readings.ServoRpm);
    }

    /// <summary>Braking torque is negative, and the sentinel must not swallow it.</summary>
    /// <remarks>
    /// Observed on the bench during deceleration, and the reason validity is decided by
    /// presence rather than by comparing against <c>NotReceived</c>: a torque of exactly
    /// -1.0 % is a real reading that a sentinel test would discard.
    /// </remarks>
    [Fact]
    public void Negative_torque_survives_the_sentinel()
    {
        var parser = new TelemetryParser();

        parser.Parse(
            """
            {"ServoOnline":true,"ServoCommEnabled":true,"ServoRpm":-0.3,"ServoTorquePct":-1.0,
             "ServoPowerW":-0.02,"ServoState":2,"ServoCommOk":12,"ServoCommErr":0}
            """);

        Assert.Equal(-0.3, parser.Readings.ServoRpm);
        Assert.Equal(-1.0, parser.Readings.ServoTorquePct);
        Assert.Equal(-0.02, parser.Readings.ServoPowerW);
    }

    /// <summary>
    /// Without <c>ServoOnline</c>, presence is aged out locally against the fallback window
    /// - but the measurements go as soon as a frame arrives without them.
    /// </summary>
    /// <remarks>
    /// The two decay on different clocks on purpose. <b>Presence</b> is a judgement about
    /// the node and gets the benefit of the window, because a single frame without values
    /// is not evidence the node died. <b>The values</b> get no such benefit: the Hub
    /// publishes them exactly when they are publishable, so a frame without them is the
    /// Hub saying there is nothing to publish, and keeping the old sample would put a
    /// stale number on screen looking current.
    /// </remarks>
    [Fact]
    public void An_older_hub_ages_presence_out_but_drops_values_immediately()
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var parser = new TelemetryParser(
            new ParserConfig { ServoTimeout = TimeSpan.FromSeconds(8) },
            clock);

        parser.Parse("""{"ServoRpm":600.5,"ServoPowerW":0.9,"ServoTorquePct":2.1,"ServoState":2}""");
        Assert.True(parser.Readings.ServoOnline);
        Assert.Equal(600.5, parser.Readings.ServoRpm);

        // Inside the window the node is still believed present, but the reading is gone.
        clock.Advance(TimeSpan.FromSeconds(6));
        parser.Parse("""{"Time":6}""");
        Assert.True(parser.Readings.ServoOnline);
        Assert.Equal(SensorReadings.NotReceived, parser.Readings.ServoRpm);

        // Past it, presence drops too.
        clock.Advance(TimeSpan.FromSeconds(4));
        parser.Parse("""{"Time":10}""");
        Assert.False(parser.Readings.ServoOnline);
    }

    /// <summary>
    /// The fallback window has to be looser than the Hub's 6 s, or the two race.
    /// </summary>
    [Fact]
    public void The_local_window_gives_the_hub_room_to_decide_first()
        => Assert.True(new ParserConfig().ServoTimeout > TimeSpan.FromSeconds(6));

    [Theory]
    [InlineData("\"NaN\"")]
    [InlineData("\"Infinity\"")]
    [InlineData("\"-Infinity\"")]
    [InlineData("\"abc\"")]
    [InlineData("null")]
    public void Non_finite_and_junk_values_are_refused_rather_than_stored(string raw)
    {
        var parser = new TelemetryParser();
        parser.Parse(OperatingFrame);

        parser.Parse($$"""{"ServoOnline":true,"ServoCommEnabled":true,"ServoRpm":{{raw}},"ServoPowerW":0.17}""");

        // The good reading is kept; the junk never lands.
        Assert.Equal(92.7, parser.Readings.ServoRpm);
        Assert.True(double.IsFinite(parser.Readings.ServoRpm));
    }

    /// <summary>Quoted numbers are normal on this wire and must decode under pt-BR.</summary>
    [Fact]
    public void Quoted_numbers_decode_with_an_invariant_decimal_point()
    {
        var parser = new TelemetryParser();

        parser.Parse(
            """{"ServoOnline":true,"ServoCommEnabled":true,"ServoRpm":"92.7","ServoPowerW":"0.17"}""");

        Assert.Equal(92.7, parser.Readings.ServoRpm);
        Assert.Equal(0.17, parser.Readings.ServoPowerW);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(-1)]
    [InlineData(99)]
    public void A_state_outside_the_contract_is_refused(int state)
    {
        var parser = new TelemetryParser();
        parser.Parse(OperatingFrame);

        parser.Parse(
            $$"""{"ServoOnline":true,"ServoCommEnabled":true,"ServoRpm":92.7,"ServoState":{{state}}}""");

        // Still the last state the contract allowed - 3 means ALARM, and a nonsense
        // value must not be able to reach that comparison.
        Assert.Equal(2, parser.Readings.ServoState);
    }

    [Fact]
    public void The_alarm_code_is_kept_raw_for_the_panel_encoding()
    {
        var parser = new TelemetryParser();

        parser.Parse(
            """{"ServoOnline":true,"ServoCommEnabled":true,"ServoRpm":0,"ServoState":3,"ServoAlarm":17}""");

        // 17 decimal is 0x0011, which the drive's panel shows as AL011. The parser keeps
        // the raw code; the hex-to-panel reading belongs to the view model.
        Assert.Equal(0x0011, parser.Readings.ServoAlarm);
        Assert.Equal(3, parser.Readings.ServoState);
    }

    [Fact]
    public void Counters_take_the_whole_unsigned_32_bit_range()
    {
        var parser = new TelemetryParser();

        parser.Parse(
            """{"ServoOnline":true,"ServoCommEnabled":true,"ServoRpm":0,"ServoCommOk":4294967295,"ServoCommErr":0}""");

        Assert.Equal(4294967295L, parser.Readings.ServoCommOk);
        Assert.Equal(0L, parser.Readings.ServoCommErr);
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(4294967296)]
    public void A_counter_outside_the_unsigned_range_is_refused(double bogus)
    {
        var parser = new TelemetryParser();
        parser.Parse(OperatingFrame);

        parser.Parse(
            $$"""{"ServoOnline":true,"ServoCommEnabled":true,"ServoRpm":92.7,"ServoCommOk":{{bogus}}}""");

        Assert.Equal(255L, parser.Readings.ServoCommOk);
    }

    /// <summary>
    /// Queue depth and pending are read even when there is nothing else to report.
    /// </summary>
    /// <remarks>
    /// This link has no acknowledgement, so these two are the only way to watch a command
    /// enter the Hub's queue and be consumed. If they were behind the same early return
    /// as the measurements, a command sent to an offline node would be invisible.
    /// </remarks>
    [Fact]
    public void Queue_state_is_read_even_with_the_node_absent()
    {
        var parser = new TelemetryParser();

        parser.Parse(
            """{"ServoOnline":false,"ServoCommEnabled":true,"ServoCommandPending":true,"ServoCommandQueueDepth":8}""");

        Assert.True(parser.Readings.ServoCommandPending);
        Assert.Equal(8, parser.Readings.ServoCommandQueueDepth);
        Assert.False(parser.Readings.ServoOnline);
    }

    /// <summary>The full command life cycle, frame by frame, as the bench saw it.</summary>
    /// <remarks>
    /// Captured on 2026-09-02 while resetting the energy accumulator with the motor
    /// turning at 600 rpm: queued, consumed on the node's next pull, then the energy
    /// observably falling. With no acknowledgement on this link, that fall <i>is</i> the
    /// confirmation.
    /// </remarks>
    [Fact]
    public void The_reset_is_confirmed_by_the_energy_falling()
    {
        var parser = new TelemetryParser();

        parser.Parse(
            """{"ServoOnline":true,"ServoCommEnabled":true,"ServoCommandPending":true,"ServoCommandQueueDepth":1,"ServoRpm":600.5,"ServoEnergyWh":0.019873}""");
        Assert.True(parser.Readings.ServoCommandPending);
        Assert.Equal(1, parser.Readings.ServoCommandQueueDepth);

        parser.Parse(
            """{"ServoOnline":true,"ServoCommEnabled":true,"ServoCommandPending":false,"ServoCommandQueueDepth":0,"ServoRpm":600.5,"ServoEnergyWh":0.020717}""");
        Assert.False(parser.Readings.ServoCommandPending);

        parser.Parse(
            """{"ServoOnline":true,"ServoCommEnabled":true,"ServoCommandPending":false,"ServoCommandQueueDepth":0,"ServoRpm":600.5,"ServoEnergyWh":0.000588}""");
        Assert.Equal(0.000588, parser.Readings.ServoEnergyWh);
    }

    /// <summary>
    /// Energy that steps backwards is normal and must not be filtered out.
    /// </summary>
    /// <remarks>
    /// It is integrated on the node, so it zeroes on a node reboot and on a reset, and it
    /// refuses to integrate across gaps rather than inventing energy. A parser that
    /// enforced monotonicity would hide a node restart entirely.
    /// </remarks>
    [Fact]
    public void Energy_is_allowed_to_go_backwards()
    {
        var parser = new TelemetryParser();

        parser.Parse("""{"ServoOnline":true,"ServoCommEnabled":true,"ServoRpm":600.5,"ServoEnergyWh":12.5}""");
        parser.Parse("""{"ServoOnline":true,"ServoCommEnabled":true,"ServoRpm":600.5,"ServoEnergyWh":0.0}""");

        Assert.Equal(0.0, parser.Readings.ServoEnergyWh);
    }

    /// <summary>Every servo field survives the copy into the immutable snapshot.</summary>
    [Fact]
    public void The_snapshot_carries_the_parsed_frame()
    {
        var parser = new TelemetryParser();
        parser.Parse(OperatingFrame);

        var snapshot = parser.Readings.Snapshot();

        Assert.True(snapshot.HasServoTelemetry);
        Assert.True(snapshot.ServoOnline);
        Assert.True(snapshot.ServoCommEnabled);
        Assert.Equal(92.7, snapshot.ServoRpm);
        Assert.Equal(0.0178, snapshot.ServoTorqueNm);
        Assert.Equal(2, snapshot.ServoState);
        Assert.Equal(255, snapshot.ServoCommOk);
    }
}
