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
