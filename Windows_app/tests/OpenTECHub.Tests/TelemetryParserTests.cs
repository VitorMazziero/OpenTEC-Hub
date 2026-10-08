using OpenTECHub.Protocol;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// Telemetry decoding, including the sentinel and stickiness rules that make a
/// missing key mean "no update" rather than zero.
/// </summary>
/// <remarks>See <c>docs/PROTOCOL.md</c> section 2.</remarks>
public class TelemetryParserTests
{
    [Fact]
    public void SensorModuleReferencesAreCurrentFrameOnlyAndPressureFreshnessDoesNotReuseRetainedValues()
    {
        var parser = new TelemetryParser();
        parser.Parse("""{"SensorCommOK":true,"Pressure":100,"PHSetpoint":6.8,"PHError":0.15,"PHControlActive":true,"PHCommandPending":false,"PressureReference":100,"PressureControlActive":true,"PressureCommandPending":false}""");
        var current = parser.Readings.Snapshot();
        Assert.True(current.SensorCommUpdated); Assert.True(current.PressureUpdated);
        Assert.Equal(6.8, current.PHSetpoint); Assert.Equal(.15, current.PHError);
        Assert.Equal(100, current.PressureReference);
        parser.Parse("""{"Time":2}""");
        var partial = parser.Readings.Snapshot();
        Assert.True(partial.SensorCommOk); Assert.False(partial.SensorCommUpdated); Assert.False(partial.PressureUpdated);
        Assert.Equal(100, partial.Pressure);
        Assert.Null(partial.PHSetpoint); Assert.Null(partial.PHError); Assert.Null(partial.PHControlActive); Assert.Null(partial.PHCommandPending);
        Assert.Null(partial.PressureReference); Assert.Null(partial.PressureControlActive); Assert.Null(partial.PressureCommandPending);
        parser.Parse("""{"Pressure":-1,"PHSetpoint":"NaN"}""");
        Assert.False(parser.Readings.Snapshot().PressureUpdated); Assert.Null(parser.Readings.Snapshot().PHSetpoint);
    }
    [Fact]
    public void FlowConfirmationRequiresCurrentCommandAndValveEchoes()
    {
        var parser = new TelemetryParser();
        const string complete = """{"FlowmeterOnline":true,"FlowRate":2,"FlowSetpoint":2,"FlowCommandPending":false,"FlowCommandId":7,"FlowCommandAck":7,"Valve1":0,"Valve2":1}""";
        parser.Parse(complete);
        Assert.True(parser.Readings.Snapshot().FlowFeedbackUpdated);
        foreach (var key in new[] { "FlowmeterOnline", "FlowRate", "FlowSetpoint", "FlowCommandPending", "FlowCommandId", "FlowCommandAck", "Valve1", "Valve2" })
        {
            var frame = System.Text.Json.Nodes.JsonNode.Parse(complete)!.AsObject();
            frame.Remove(key);
            parser.Parse(frame.ToJsonString());
            Assert.False(parser.Readings.Snapshot().FlowFeedbackUpdated);
        }
        parser.Parse(complete.Replace("\"Valve1\":0", "\"Valve1\":-1"));
        Assert.False(parser.Readings.Snapshot().FlowFeedbackUpdated);
    }
    [Fact]
    public void FlowFreshnessDoesNotPromoteRetainedOrOfflineReadings()
    {
        var parser = new TelemetryParser();
        parser.Parse("""{"FlowmeterOnline":true,"FlowRate":2}""");
        Assert.True(parser.Readings.Snapshot().FlowRateUpdated);
        parser.Parse("""{"Time":2}""");
        Assert.False(parser.Readings.Snapshot().FlowRateUpdated);
        Assert.Equal(2, parser.Readings.FlowRate);
        parser.Parse("""{"FlowRate":-1}""");
        Assert.False(parser.Readings.Snapshot().FlowRateUpdated);
        parser.Parse("""{"FlowRate":2,"FlowmeterOnline":false}""");
        Assert.False(parser.Readings.Snapshot().FlowRateUpdated);
        Assert.Equal(SensorReadings.NotReceived, parser.Readings.FlowRate);
        parser.Parse("""{"FlowmeterOnline":true,"FlowRate":0}""");
        Assert.True(parser.Readings.Snapshot().FlowRateUpdated);
        Assert.Equal(0, parser.Readings.FlowRate);
    }
    [Fact]
    public void TemperatureFreshnessDoesNotPromoteRetainedOrRejectedReadings()
    {
        var parser = new TelemetryParser();
        parser.Parse("{\"Tempval\":37}");
        Assert.True(parser.Readings.Snapshot().TemperatureUpdated);
        foreach (var frame in new[] { "{\"Time\":2}", "{\"Tempval\":-1}", "{\"Tempval\":10}", "{\"Tempval\":100}" })
        {
            parser.Parse(frame);
            Assert.False(parser.Readings.Snapshot().TemperatureUpdated);
            Assert.Equal(37, parser.Readings.Temperature);
        }
        parser.Parse("{\"Tempval\":38}");
        Assert.True(parser.Readings.Snapshot().TemperatureUpdated);
        Assert.Equal(38, parser.Readings.Temperature);
    }
    /// <summary>
    /// A real frame captured from the bench ESP32-S3 on 2026-08-19, with no
    /// bioreactor module attached - hence the sentinels and <c>SensorCommOK:false</c>.
    /// </summary>
    private const string BenchFrame =
        """{"Tempval":-1,"Oxyval":0,"pHval":0,"Pressure":0,"FlowRate":-1,"Time":2.8,"SensorCommOK":false}""";

    [Fact]
    public void Bench_frame_from_real_hardware_parses()
    {
        var parser = new TelemetryParser();

        Assert.Equal(ParseOutcome.Updated, parser.Parse(BenchFrame));
        Assert.Equal(2.8, parser.Readings.TimeRawSeconds);
        Assert.False(parser.Readings.SensorCommOk);

        // Out-of-range temperature and zero-sentinel probes must leave the channels
        // at "never received" rather than showing -1 as a reading.
        Assert.Equal(SensorReadings.NotReceived, parser.Readings.Temperature);
        Assert.Equal(SensorReadings.NotReceived, parser.Readings.OxygenCalibrated);
        Assert.Equal(SensorReadings.NotReceived, parser.Readings.PHCalibrated);
    }

    /// <summary>
    /// The device interleaves human-readable log lines with telemetry. Confirmed on
    /// hardware; treating them as corruption would tear down a healthy link.
    /// </summary>
    [Fact]
    public void Device_log_lines_are_recognised_not_treated_as_corruption()
    {
        var parser = new TelemetryParser();

        Assert.Equal(
            ParseOutcome.DeviceLog,
            parser.Parse("[ESP32_AVISO]: Falha de leitura UART do Módulo OpenTEC após comando 'b'"));

        Assert.Equal(
            ParseOutcome.DeviceLog,
            parser.Parse("[ESP32_AVISO]: Limite de falhas UART atingido; reinicializando UART e iniciando cooldown de 5s"));
    }

    /// <summary>
    /// On USB the command acknowledgement shares the stream with telemetry.
    /// Confirmed on hardware 2026-08-19.
    /// </summary>
    [Fact]
    public void Command_acknowledgement_is_not_a_parse_failure()
    {
        var parser = new TelemetryParser();

        Assert.Equal(ParseOutcome.CommandAck, parser.Parse("OK"));
        Assert.Equal(ParseOutcome.CommandAck, parser.Parse("  OK  "));
        Assert.Equal(ParseOutcome.Malformed, parser.Parse("OKAY"));
    }

    [Fact]
    public void Node_diagnostic_envelope_is_not_telemetry_or_a_parse_failure()
    {
        var parser = new TelemetryParser();
        var before = parser.Readings.Snapshot();

        Assert.Equal(ParseOutcome.NodeDiag, parser.Parse(
            """{"NodeDiag":{"dev":"pump","code":200,"age_ms":400,"diag":{"rssi":-61}}}"""));
        Assert.Equal(before, parser.Readings.Snapshot());
    }

    [Fact]
    public void Malformed_and_empty_lines_are_distinguished()
    {
        var parser = new TelemetryParser();

        Assert.Equal(ParseOutcome.Empty, parser.Parse(""));
        Assert.Equal(ParseOutcome.Empty, parser.Parse("   "));
        Assert.Equal(ParseOutcome.Empty, parser.Parse(null));
        Assert.Equal(ParseOutcome.Malformed, parser.Parse("{not json"));
        Assert.Equal(ParseOutcome.Malformed, parser.Parse("[1,2,3]")); // array, not object
    }

    [Fact]
    public void Missing_keys_leave_previous_values_untouched()
    {
        var parser = new TelemetryParser();

        parser.Parse("""{"Tempval":30.5,"Pressure":3.2}""");
        parser.Parse("""{"Time":10}"""); // carries neither temperature nor pressure

        Assert.Equal(30.5, parser.Readings.Temperature);
        Assert.Equal(3.2, parser.Readings.Pressure);
    }

    [Theory]
    [InlineData(9.9)]    // below the plausible band
    [InlineData(100.1)]  // above it
    [InlineData(-1)]     // device sentinel
    public void Implausible_temperatures_are_rejected(double reading)
    {
        var parser = new TelemetryParser();

        parser.Parse("""{"Tempval":30.0}""");
        parser.Parse($$"""{"Tempval":{{reading}}}""");

        Assert.Equal(30.0, parser.Readings.Temperature); // held, not overwritten
    }

    [Fact]
    public void FlowCommandPending_is_not_sticky_but_FlowmeterOnline_is()
    {
        var parser = new TelemetryParser();

        parser.Parse("""{"FlowCommandPending":true,"FlowmeterOnline":true}""");
        Assert.True(parser.Readings.FlowCommandPending);
        Assert.True(parser.Readings.FlowmeterOnline);

        parser.Parse("""{"Time":5}"""); // neither key present

        Assert.False(parser.Readings.FlowCommandPending); // resets
        Assert.True(parser.Readings.FlowmeterOnline);     // holds
    }

    [Fact]
    public void Flow_command_identifiers_preserve_full_unsigned_32_bit_range()
    {
        var parser = new TelemetryParser();

        Assert.Equal(ParseOutcome.Updated, parser.Parse(
            """{"FlowCommandId":4240449281,"FlowCommandAck":4240449281}"""));

        Assert.Equal(4_240_449_281L, parser.Readings.FlowCommandId);
        Assert.Equal(4_240_449_281L, parser.Readings.FlowCommandAck);
    }

    [Fact]
    public void FlowmeterReconnectWifi_defaults_to_on_and_holds_when_the_key_is_absent()
    {
        var parser = new TelemetryParser();

        // A v05 flowmeter never reports the key: absence must not read as "switched off".
        parser.Parse("""{"FlowmeterOnline":true}""");
        Assert.True(parser.Readings.FlowmeterReconnectWifi);

        parser.Parse("""{"FlowmeterOnline":true,"FlowmeterReconnectWifi":false}""");
        Assert.False(parser.Readings.FlowmeterReconnectWifi);

        // The hub omits it while the node is offline; the last known value stands.
        parser.Parse("""{"FlowmeterOnline":false}""");
        Assert.False(parser.Readings.FlowmeterReconnectWifi);

        parser.Parse("""{"FlowmeterOnline":true,"FlowmeterReconnectWifi":true}""");
        Assert.True(parser.Readings.FlowmeterReconnectWifi);
    }

    [Fact]
    public void Offline_flowmeter_invalidates_values_omitted_by_hub_v7()
    {
        var parser = new TelemetryParser();

        parser.Parse("""{"FlowmeterOnline":true,"FlowRate":0.18,"FlowSetpoint":1.0,"FlowVoltage":0.42,"Valve1":1,"Valve2":0,"ValveFlow":0}""");
        parser.Parse("""{"FlowmeterOnline":false}""");

        Assert.Equal(SensorReadings.NotReceived, parser.Readings.FlowRate);
        Assert.Equal(SensorReadings.NotReceived, parser.Readings.FlowSetpoint);
        Assert.Equal(SensorReadings.NotReceived, parser.Readings.FlowVoltage);
        Assert.Equal(-1, parser.Readings.FlowValve1);
        Assert.Equal(-1, parser.Readings.FlowValve2);
        Assert.Equal(-1, parser.Readings.FlowValveMain);
    }

    [Fact]
    public void Oxygen_calibration_is_applied_and_floored_at_zero()
    {
        var parser = new TelemetryParser(new ParserConfig
        {
            OxygenCalibrationA = 0.03,
            OxygenCalibrationB = -25.0,
            // Neutralise the spike filter so a single frame lands.
            OxygenFilter = new SpikeFilterConfig(double.MaxValue, double.MaxValue, 1),
        });

        parser.Parse("""{"Oxyval":2000}""");
        Assert.Equal(35.0, parser.Readings.OxygenCalibrated, 4); // 0.03*2000 - 25

        parser.Parse("""{"Oxyval":100}""");
        Assert.Equal(0.0, parser.Readings.OxygenCalibrated); // would be negative, floored
    }

    [Fact]
    public void Probe_sentinels_are_ignored()
    {
        var parser = new TelemetryParser();

        parser.Parse("""{"Oxyval":0,"pHval":0}""");
        Assert.Equal(SensorReadings.NotReceived, parser.Readings.OxygenRaw);
        Assert.Equal(SensorReadings.NotReceived, parser.Readings.PHRaw);
    }

    [Fact]
    public void PH_calibration_change_is_raised_once_per_new_value()
    {
        var parser = new TelemetryParser(new ParserConfig
        {
            PHSlope = 0.001,
            PHIntercept = 0.0,
            PHFilter = new SpikeFilterConfig(double.MaxValue, double.MaxValue, 1),
        });

        var raised = new List<double>();
        parser.PHCalibrationChanged += raised.Add;

        parser.Parse("""{"pHval":6980}""");
        Assert.Single(raised);
        Assert.Equal(6.98, raised[0], 2);

        // Until the send is acknowledged the parser keeps asking.
        parser.Parse("""{"pHval":6980}""");
        Assert.Equal(2, raised.Count);

        parser.MarkPHSent(6.98);
        parser.Parse("""{"pHval":6980}""");
        Assert.Equal(2, raised.Count); // quiet now
    }

    [Fact]
    public void Numeric_strings_are_accepted_for_numeric_keys()
    {
        var parser = new TelemetryParser();

        // The firmware is not consistent about quoting; both forms must work.
        parser.Parse("""{"Tempval":"30.5","Valve1":"1"}""");

        Assert.Equal(30.5, parser.Readings.Temperature);
        Assert.Equal(1, parser.Readings.FlowValve1);
    }

    [Fact]
    public void Distance_is_held_until_the_timeout_then_aged_out()
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var parser = new TelemetryParser(
            new ParserConfig { DistanceTimeout = TimeSpan.FromSeconds(3) },
            clock);

        parser.Parse("""{"Distance":150}""");
        Assert.Equal(150, parser.Readings.Distance);

        // Silence inside the window: the last height stays on screen.
        clock.Advance(TimeSpan.FromSeconds(2));
        parser.Parse("""{"Time":5}""");
        Assert.Equal(150, parser.Readings.Distance);

        // Past the window: the sensor is presumed gone.
        clock.Advance(TimeSpan.FromSeconds(2));
        parser.Parse("""{"Time":7}""");
        Assert.Equal(SensorReadings.NotReceived, parser.Readings.Distance);
    }

    [Fact]
    public void Distance_arriving_again_refreshes_the_window()
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var parser = new TelemetryParser(
            new ParserConfig { DistanceTimeout = TimeSpan.FromSeconds(3) },
            clock);

        parser.Parse("""{"Distance":150}""");
        clock.Advance(TimeSpan.FromSeconds(2));
        parser.Parse("""{"Distance":160}""");

        clock.Advance(TimeSpan.FromSeconds(2)); // 2 s since the newest reading
        parser.Parse("""{"Time":9}""");

        Assert.Equal(160, parser.Readings.Distance);
    }

    [Theory]
    [InlineData(-1)]     // device sentinel
    [InlineData(1000)]   // at the exclusive upper bound
    [InlineData(5000)]   // well beyond it
    public void Implausible_distances_are_rejected(double reading)
    {
        var parser = new TelemetryParser();

        parser.Parse("""{"Distance":150}""");
        parser.Parse($$"""{"Distance":{{reading}}}""");

        Assert.Equal(150, parser.Readings.Distance); // held, not overwritten
    }
}

/// <summary>
/// The spike filter's contract: a lone outlier never displaces a good value, but a
/// sustained change is followed.
/// </summary>
/// <remarks>See <c>docs/PROTOCOL.md</c> section 2.1.</remarks>
public class SpikeFilterTests
{
    private static SpikeFilter Filter() => new(new SpikeFilterConfig(100.0, 50.0, 3));

    [Fact]
    public void First_reading_is_accepted_unconditionally()
    {
        var filter = Filter();
        Assert.Equal(5000.0, filter.Update(5000.0));
    }

    [Fact]
    public void Small_movements_pass_straight_through()
    {
        var filter = Filter();
        filter.Update(5000.0);

        Assert.Equal(5050.0, filter.Update(5050.0));
        Assert.Equal(5100.0, filter.Update(5100.0));
    }

    [Fact]
    public void A_lone_outlier_is_rejected_and_the_good_value_held()
    {
        var filter = Filter();
        filter.Update(5000.0);

        Assert.Equal(5000.0, filter.Update(9999.0)); // spike, held
        Assert.Equal(5010.0, filter.Update(5010.0)); // back to normal
    }

    [Fact]
    public void A_sustained_step_is_promoted_after_the_confirmation_runs()
    {
        var filter = Filter();
        filter.Update(5000.0);

        Assert.Equal(5000.0, filter.Update(6000.0)); // candidate, run 1
        Assert.Equal(5000.0, filter.Update(6001.0)); // run 2
        Assert.Equal(6002.0, filter.Update(6002.0)); // run 3: promoted
    }

    [Fact]
    public void An_inconsistent_candidate_restarts_candidacy()
    {
        var filter = Filter();
        filter.Update(5000.0);

        Assert.Equal(5000.0, filter.Update(6000.0)); // candidate A
        Assert.Equal(5000.0, filter.Update(8000.0)); // too far from A: restart
        Assert.Equal(5000.0, filter.Update(8001.0)); // candidate B, run 2
        Assert.Equal(8002.0, filter.Update(8002.0)); // run 3: promoted
    }

    [Fact]
    public void Null_readings_hold_the_last_good_value()
    {
        var filter = Filter();
        filter.Update(5000.0);

        Assert.Equal(5000.0, filter.Update(null));
        Assert.Null(new SpikeFilter(new SpikeFilterConfig(1, 1, 1)).Update(null));
    }

    [Fact]
    public void Reset_clears_all_state()
    {
        var filter = Filter();
        filter.Update(5000.0);
        filter.Reset();

        Assert.Null(filter.LastGood);
        Assert.Equal(9999.0, filter.Update(9999.0)); // bootstraps again
    }
}
