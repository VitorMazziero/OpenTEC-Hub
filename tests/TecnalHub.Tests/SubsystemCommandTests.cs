using TecnalHub.Protocol;
using Xunit;

namespace TecnalHub.Tests;

/// <summary>
/// The commands the Phase 1 subsystems put on the wire.
/// </summary>
/// <remarks>
/// The ViewModel layer lives in the WPF assembly, which this project deliberately does
/// not reference - so these pin the builders the subsystems are wired to, which is
/// where a range or an encoding would actually go wrong.
/// </remarks>
public class SubsystemCommandTests
{
    [Fact]
    public void Temperature_off_is_zero_not_an_omitted_key()
        => Assert.Equal("""{"tempSetpoint":0.0}""",
            TecnalCommand.Create().Set(CommandKeys.TempSetpoint, 0.0).ToJson());

    [Fact]
    public void Motor_off_is_zero_and_bypasses_the_50_rpm_floor()
        => Assert.Equal("""{"motorSetpoint":0}""", CommandBuilders.MotorSetpoint(0).ToJson());

    [Fact]
    public void Oxygen_monitor_off_is_zero()
        => Assert.Equal("""{"oxygenMonitor":0.0}""",
            TecnalCommand.Create().Set(CommandKeys.OxygenMonitor, 0.0).ToJson());

    /// <summary>
    /// Disabling flow must be the safe-stop, not merely a zero setpoint: both valves
    /// are forced closed, because leaving nitrogen open through a stop is a hazard.
    /// </summary>
    [Fact]
    public void Disabling_flow_closes_both_valves()
    {
        var json = CommandBuilders.FlowSafeStop(50.0).ToJson();

        Assert.Equal(
            """{"flowSetpoint":0.0,"maxFlow":50.0,"valve_1":0,"valve_2":0,"v_Flow":1}""",
            json);
    }

    [Fact]
    public void Pressure_reference_off_is_zero()
        => Assert.Equal("""{"pressureReference":0.0}""",
            TecnalCommand.Create().Set(CommandKeys.PressureReference, 0.0).ToJson());

    /// <summary>
    /// A pt-BR operator types "30,5"; the wire must still carry "30.5". The UI parses
    /// the comma, and TecnalCommand formats invariantly - the two must compose.
    /// </summary>
    [Fact]
    public void Comma_entry_reaches_the_wire_as_a_point()
    {
        var parsed = double.Parse(
            "30,5".Replace(',', '.'),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal("""{"tempSetpoint":30.5}""",
            TecnalCommand.Create().Set(CommandKeys.TempSetpoint, parsed).ToJson());
    }
}
