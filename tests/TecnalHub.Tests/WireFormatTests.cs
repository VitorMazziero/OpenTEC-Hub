using System.Globalization;
using TecnalHub.Protocol;
using Xunit;

namespace TecnalHub.Tests;

/// <summary>
/// Golden-string assertions for the ESP32-S3 wire contract.
/// </summary>
/// <remarks>
/// These are the executable form of <c>docs/PROTOCOL.md</c> section 4. The firmware
/// is frozen, so any change that alters a byte here is a regression until the
/// document says otherwise. Add the test before the command, never after.
/// </remarks>
public class WireFormatTests
{
    [Fact]
    public void Handshake_matches_v6_exactly()
        => Assert.Equal("""{"comTest":1}""", CommandBuilders.Handshake().ToJson());

    [Fact]
    public void Motor_setpoint_is_an_integer()
        => Assert.Equal("""{"motorSetpoint":790}""", CommandBuilders.MotorSetpoint(790).ToJson());

    [Theory]
    [InlineData(0, 0)]        // stop passes through unclamped
    [InlineData(10, 50)]      // below the valid band
    [InlineData(5000, 1000)]  // above the valid band
    [InlineData(300, 300)]
    public void Motor_setpoint_clamps_to_the_valid_band(int requested, int expected)
        => Assert.Equal(
            $$"""{"motorSetpoint":{{expected}}}""",
            CommandBuilders.MotorSetpoint(requested).ToJson());

    [Fact]
    public void Flow_setpoint_carries_the_inverted_vent_flag()
    {
        // v_Flow is 0 while flow is commanded...
        Assert.Equal(
            """{"flowmeterComm":1,"flowSetpoint":2.5,"maxFlow":50.0,"valve_1":0,"valve_2":0,"v_Flow":0}""",
            CommandBuilders.FlowSetpoint(2.5, maxFlow: 50.0).ToJson());

        // ...and 1 when the setpoint is zero.
        Assert.Equal(
            """{"flowmeterComm":1,"flowSetpoint":0.0,"maxFlow":50.0,"valve_1":0,"valve_2":0,"v_Flow":1}""",
            CommandBuilders.FlowSetpoint(0.0, maxFlow: 50.0).ToJson());
    }

    [Fact]
    public void Flow_setpoint_clamps_to_max_flow()
        => Assert.Equal(
            """{"flowmeterComm":1,"flowSetpoint":50.0,"maxFlow":50.0,"valve_1":0,"valve_2":0,"v_Flow":0}""",
            CommandBuilders.FlowSetpoint(999.0, maxFlow: 50.0).ToJson());

    /// <summary>
    /// Safe-stop must close both valves rather than preserving a manual selection.
    /// Leaving nitrogen open through a stop is a hazard; v.6 forces them shut and so
    /// do we.
    /// </summary>
    [Fact]
    public void Flow_safe_stop_forces_both_valves_closed()
        => Assert.Equal(
            """{"flowmeterComm":0,"flowSetpoint":0.0,"maxFlow":50.0,"valve_1":0,"valve_2":0,"v_Flow":1}""",
            CommandBuilders.FlowSafeStop(maxFlow: 50.0).ToJson());

    [Fact]
    public void Valve_control_sends_complete_state_and_derives_the_inverted_vent_flag()
        => Assert.Equal(
            """{"flowmeterComm":1,"flowSetpoint":0.0,"maxFlow":50.0,"valve_1":1,"valve_2":1,"v_Flow":1}""",
            CommandBuilders.FlowSetpoint(0.0, maxFlow: 50.0, valve1: true, valve2: true).ToJson());

    [Fact]
    public void Core_safe_stop_is_one_complete_command_with_both_gas_valves_closed()
        => Assert.Equal(
            """{"tempSetpoint":0.0,"motorSetpoint":0,"oxygenMonitor":0.0,"flowmeterComm":0,"flowSetpoint":0.0,"maxFlow":50.0,"valve_1":0,"valve_2":0,"v_Flow":1,"pressureReference":0.0}""",
            CommandBuilders.CoreSafeStop(maxFlow: 50.0).ToJson());

    [Fact]
    public void Cascade_actuation_keeps_v6_key_order()
        => Assert.Equal(
            """{"flowSetpoint":2.5,"flowmeterComm":1,"valve_1":0,"valve_2":0,"v_Flow":0,"oxygenMonitor":40.0,"motorSetpoint":300}""",
            CommandBuilders.CascadeActuation(2.5, 40.0, 300).ToJson());

    /// <summary>pH is echoed back as a quoted string with two decimals, not a number.</summary>
    [Fact]
    public void PH_calibration_is_a_quoted_two_decimal_string()
        => Assert.Equal("""{"pHCal":"6.98"}""", CommandBuilders.PHCalibration(6.98).ToJson());

    [Theory]
    [InlineData(7.0, """{"pHCal":"7.00"}""")]
    [InlineData(6.987, """{"pHCal":"6.99"}""")]
    [InlineData(0.0, """{"pHCal":"0.00"}""")]
    public void PH_calibration_always_carries_two_decimals(double value, string expected)
        => Assert.Equal(expected, CommandBuilders.PHCalibration(value).ToJson());

    [Fact]
    public void PH_control_and_safe_stop_are_complete_atomic_v6_frames()
    {
        Assert.Equal(
            """{"pHSetpoint":6.8,"pHError":0.17,"pHOperation":5.0,"pHMix":20.0,"pHIntensity":500.0}""",
            CommandBuilders.PHControl(6.8, 0.17, 5, 20, 50).ToJson());
        Assert.Equal(
            """{"pHSetpoint":0.0,"pHError":0.17,"pHOperation":5.0,"pHMix":20.0,"pHIntensity":0.0}""",
            CommandBuilders.PHControlSafeStop(0.17, 5, 20).ToJson());
    }

    [Fact]
    public void Flow_calibration_frames_match_v6_key_order()
    {
        Assert.Equal(
            """{"flowmeterComm":1,"flowSetpoint":1.5,"valve_1":0,"valve_2":0,"v_Flow":0}""",
            CommandBuilders.FlowCalibrationSetpoint(1.5).ToJson());
        Assert.Equal(
            """{"k1":2.0,"f1":3.0,"c1":4.0,"k2":0.0,"f2":5.0,"c2":1.0}""",
            CommandBuilders.FlowCalibrationLow(2, 3, 4)
                .Merge(CommandBuilders.FlowCalibrationHigh(0, 5, 1))
                .ToJson());
    }

    [Fact]
    public void System_commands_match_v6()
    {
        Assert.Equal("""{"dataDelay":2000}""", CommandBuilders.DataDelay(2000).ToJson());
        Assert.Equal("""{"resetVariables":1}""", CommandBuilders.ResetVariables().ToJson());
        Assert.Equal("""{"restart":1}""", CommandBuilders.Restart().ToJson());
    }

    // ── Biomass (Phase 3 WP1) ────────────────────────────────────────────────

    [Fact]
    public void Biomass_enable_and_momentary_actions_match_v6()
    {
        Assert.Equal("""{"biomassComm":1}""", CommandBuilders.BiomassComm(true).ToJson());
        Assert.Equal("""{"biomassComm":0}""", CommandBuilders.BiomassComm(false).ToJson());
        Assert.Equal("""{"blank":1}""", CommandBuilders.BiomassBlank().ToJson());
        Assert.Equal("""{"start":1}""", CommandBuilders.BiomassStart().ToJson());
        Assert.Equal("""{"stop":1}""", CommandBuilders.BiomassStop().ToJson());
    }

    [Fact]
    public void Biomass_thresholds_are_one_atomic_integer_frame()
        => Assert.Equal(
            """{"low":10000,"high":40000,"opt":25000}""",
            CommandBuilders.BiomassThresholds(10000, 40000, 25000).ToJson());

    // ── External pump (Phase 3 WP2) ──────────────────────────────────────────

    [Fact]
    public void Pump_enable_and_safe_disable_match_v6()
    {
        Assert.Equal("""{"pumpComm":1}""", CommandBuilders.PumpEnable().ToJson());
        // v.6's send_extern_pump_comm(false): the safe frame with the vestigial speed:0.
        Assert.Equal("""{"pumpComm":0,"mode":0,"speed":0}""", CommandBuilders.PumpDisable().ToJson());
    }

    [Fact]
    public void Pump_profile_frames_match_v6_key_order()
    {
        Assert.Equal(
            """{"mode":1,"init_t":0.0,"final_t":60.0,"lambda_const":1.5}""",
            CommandBuilders.PumpConstant(0, 60, 1.5).ToJson());

        Assert.Equal(
            """{"mode":2,"init_t":0.0,"final_t":60.0,"lambda_linear":1.0,"phi_linear":0.5}""",
            CommandBuilders.PumpLinear(0, 60, 1.0, 0.5).ToJson());

        Assert.Equal(
            """{"mode":3,"init_t":0.0,"final_t":60.0,"lambda_exp":1.0,"phi_exp":0.1}""",
            CommandBuilders.PumpExponential(0, 60, 1.0, 0.1).ToJson());

        Assert.Equal(
            """{"mode":4,"init_t":0.0,"final_t":60.0,"p0":1.0,"p1":0.5,"p2":0.1}""",
            CommandBuilders.PumpPolynomial(0, 60, [1.0, 0.5, 0.1]).ToJson());

        // Piecewise interleaves t0,q0,t1,q1,… exactly as v.6's send loop does.
        Assert.Equal(
            """{"mode":5,"init_t":0.0,"final_t":60.0,"num_segments":3,"t0":0.0,"q0":1.0,"t1":30.0,"q1":2.0,"t2":60.0,"q2":3.0}""",
            CommandBuilders.PumpPiecewise(0, 60, [0.0, 30.0, 60.0], [1.0, 2.0, 3.0]).ToJson());
    }

    [Fact]
    public void Pump_profile_rejects_out_of_range_coefficient_counts()
    {
        Assert.Throws<ArgumentException>(() => CommandBuilders.PumpPolynomial(0, 60, []));
        Assert.Throws<ArgumentException>(
            () => CommandBuilders.PumpPolynomial(0, 60, [.. Enumerable.Repeat(1.0, 22)]));
        Assert.Throws<ArgumentException>(() => CommandBuilders.PumpPiecewise(0, 60, [0.0], [1.0]));
        Assert.Throws<ArgumentException>(
            () => CommandBuilders.PumpPiecewise(0, 60, [0.0, 1.0], [1.0]));
    }

    /// <summary>
    /// Python floats always serialise with a decimal point. Matching that keeps a
    /// captured v.6 trace byte-comparable against ours, which is the Phase 0 exit
    /// criterion.
    /// </summary>
    [Fact]
    public void Whole_doubles_keep_a_decimal_point()
    {
        Assert.Equal("""{"tempSetpoint":30.0}""",
            TecnalCommand.Create().Set(CommandKeys.TempSetpoint, 30.0).ToJson());

        Assert.Equal("""{"tempSetpoint":30}""",
            TecnalCommand.Create().Set(CommandKeys.TempSetpoint, 30).ToJson());
    }

    [Fact]
    public void Booleans_serialise_as_one_and_zero()
        => Assert.Equal("""{"valve_1":1,"valve_2":0}""",
            TecnalCommand.Create()
                .Set(CommandKeys.Valve1, true)
                .Set(CommandKeys.Valve2, false)
                .ToJson());

    [Fact]
    public void Merge_updates_in_place_without_reordering()
    {
        var command = TecnalCommand.Create()
            .Set(CommandKeys.MotorSetpoint, 100)
            .Set(CommandKeys.TempSetpoint, 30.0);

        command.Merge(TecnalCommand.Create()
            .Set(CommandKeys.MotorSetpoint, 500)   // existing key: value changes, position holds
            .Set(CommandKeys.OxygenMonitor, 40.0)); // new key: appended

        Assert.Equal(
            """{"motorSetpoint":500,"tempSetpoint":30.0,"oxygenMonitor":40.0}""",
            command.ToJson());
    }

    [Fact]
    public void Non_finite_values_are_rejected_rather_than_serialised()
    {
        var command = TecnalCommand.Create();
        Assert.Throws<ArgumentOutOfRangeException>(() => command.Set(CommandKeys.FlowSetpoint, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => command.Set(CommandKeys.FlowSetpoint, double.PositiveInfinity));
    }
}

/// <summary>
/// The wire format must not depend on the machine's locale.
/// </summary>
/// <remarks>
/// <para>
/// The lab machines run pt-BR, where the decimal separator is a comma. Naive .NET
/// formatting would emit <c>6,98</c> and the firmware parse would fail - a bug that
/// is invisible on an en-US developer machine and breaks only in the lab.
/// </para>
/// <para>
/// These tests force the hostile culture explicitly rather than trusting the CI
/// machine to have it. See <c>docs/PROTOCOL.md</c> section 2.2.
/// </para>
/// </remarks>
public class CultureInvarianceTests : IDisposable
{
    private readonly CultureInfo _originalCulture = CultureInfo.CurrentCulture;

    public CultureInvarianceTests()
    {
        // pt-BR: decimal comma, thousands point - maximally hostile to naive formatting.
        var hostile = new CultureInfo("pt-BR");
        CultureInfo.CurrentCulture = hostile;
        CultureInfo.CurrentUICulture = hostile;
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _originalCulture;
        CultureInfo.CurrentUICulture = _originalCulture;
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Sanity_check_the_hostile_culture_is_actually_active()
    {
        Assert.Equal(",", CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator);

        // If this ever stops holding, the tests below prove nothing.
        Assert.Equal("2,5", 2.5.ToString("R", CultureInfo.CurrentCulture));
    }

    [Fact]
    public void Doubles_use_a_decimal_point_under_pt_BR()
        => Assert.Equal(
            """{"flowmeterComm":1,"flowSetpoint":2.5,"maxFlow":50.0,"valve_1":0,"valve_2":0,"v_Flow":0}""",
            CommandBuilders.FlowSetpoint(2.5, maxFlow: 50.0).ToJson());

    [Fact]
    public void PH_echo_uses_a_decimal_point_under_pt_BR()
        => Assert.Equal("""{"pHCal":"6.98"}""", CommandBuilders.PHCalibration(6.98).ToJson());

    [Fact]
    public void Cascade_actuation_is_culture_invariant()
        => Assert.Equal(
            """{"flowSetpoint":2.5,"flowmeterComm":1,"valve_1":0,"valve_2":0,"v_Flow":0,"oxygenMonitor":40.5,"motorSetpoint":300}""",
            CommandBuilders.CascadeActuation(2.5, 40.5, 300).ToJson());

    [Fact]
    public void Telemetry_with_decimal_points_parses_under_pt_BR()
    {
        var parser = new TelemetryParser();

        Assert.Equal(ParseOutcome.Updated, parser.Parse("""{"Tempval":30.25,"Pressure":3.5,"Time":123.75}"""));

        Assert.Equal(30.25, parser.Readings.Temperature);
        Assert.Equal(3.5, parser.Readings.Pressure);
        Assert.Equal(123.75, parser.Readings.TimeRawSeconds);
    }
}
