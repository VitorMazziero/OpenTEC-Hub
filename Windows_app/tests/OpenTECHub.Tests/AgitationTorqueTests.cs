using System.IO;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Telemetry;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// Torque riding along with the speed, on the one card measured by two instruments.
/// </summary>
/// <remarks>
/// Speed alone does not say whether the shaft is turning freely or against a load, and the
/// torque that answers it comes off the same shaft. Separating them onto different cards
/// would leave the operator to hold one number in their head while looking at the other.
/// </remarks>
public sealed class AgitationTorqueTests
{
    private static ProcessVariableViewModel Motor() => new(
        "motor", "Agitação", "rpm", decimals: 1, channel: TelemetryChannel.ServoRpm);

    [Fact]
    public void A_variable_has_no_second_reading_by_default()
    {
        var temperature = new ProcessVariableViewModel("temperature", "Temperatura", "°C");

        Assert.False(temperature.HasSecondaryReading);
        Assert.Null(temperature.SecondaryText);
    }

    [Fact]
    public void Setting_the_second_reading_makes_it_visible()
    {
        var motor = Motor();

        motor.SecondaryText = "1,9";
        motor.SecondaryLabel = "% torque";

        Assert.True(motor.HasSecondaryReading);
        Assert.Equal("1,9", motor.SecondaryText);
    }

    /// <summary>
    /// Clearing it hides it, so a dead node does not leave a stale torque beside a live speed.
    /// </summary>
    [Fact]
    public void Clearing_the_second_reading_hides_it_again()
    {
        var motor = Motor();
        motor.SecondaryText = "1,9";

        motor.SecondaryText = null;

        Assert.False(motor.HasSecondaryReading);
    }

    [Fact]
    public void An_empty_second_reading_counts_as_absent()
    {
        var motor = Motor();

        motor.SecondaryText = "";

        Assert.False(motor.HasSecondaryReading);
    }
}

/// <summary>
/// The synoptic and control surfaces for agitation, pinned against the markup.
/// </summary>
public sealed class AgitationSurfaceContractTests
{
    private static string Read(string view) => File.ReadAllText(
        Path.Combine(TestPaths.RepositoryRoot, "src", "OpenTECHub", "Views", view));

    /// <summary>
    /// The synoptic tile renders the second reading, and only when there is one.
    /// </summary>
    [Fact]
    public void The_instrument_tile_shows_a_second_reading_when_present()
    {
        var xaml = Read("SynopticView.xaml");

        Assert.Contains("{Binding SecondaryText}", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding SecondaryLabel}", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{Binding HasSecondaryReading, Converter={StaticResource BoolToVis}}\"",
            xaml,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Dropping agitation on a plot asks which series, rather than guessing.
    /// </summary>
    /// <remarks>
    /// Seven series sit behind that one card - a commanded speed from this application and
    /// six measurements from the drive - and each answers a different question. Picking one
    /// silently would be picking wrong six times out of seven, which is why the pump already
    /// asks for its two.
    /// </remarks>
    [Fact]
    public void Dropping_agitation_on_a_plot_offers_every_series_behind_it()
    {
        var code = Read("SynopticView.xaml.cs");

        Assert.Contains("ShowAgitationSubMenu(dropTarget, panelIndex)", code, StringComparison.Ordinal);

        foreach (var channel in new[]
                 {
                     "TelemetryChannel.ServoRpm",
                     "TelemetryChannel.MotorRpm",
                     "TelemetryChannel.ServoTorquePct",
                     "TelemetryChannel.ServoTorqueNm",
                     "TelemetryChannel.ServoLoadPct",
                     "TelemetryChannel.ServoPowerW",
                     "TelemetryChannel.ServoEnergyWh",
                 })
        {
            Assert.Contains(channel, code, StringComparison.Ordinal);
        }

        // Measured before commanded: the card shows the measurement, and offering the command
        // first would invite charting what was asked for in place of what happened.
        var measured = code.IndexOf("\"Rotação medida (rpm)\"", StringComparison.Ordinal);
        var commanded = code.IndexOf("\"Rotação comandada (rpm)\"", StringComparison.Ordinal);
        Assert.True(measured > 0 && commanded > measured);
    }

    /// <summary>
    /// The servo is no longer listed among the external devices.
    /// </summary>
    /// <remarks>
    /// It is internal to the module and nobody switches it on - on the TECNAL module 2 it
    /// simply runs. A row in Dispositivos Externos, with its own enable switch, offered a
    /// choice that does not exist.
    /// </remarks>
    [Fact]
    public void The_servo_is_not_an_external_device_row()
    {
        var xaml = Read("ControlView.xaml");

        Assert.DoesNotContain("<!-- 13. Servo Drive", xaml, StringComparison.Ordinal);

        var externals = xaml.IndexOf("Text=\"Dispositivos Externos\"", StringComparison.Ordinal);
        Assert.True(externals > 0);
        Assert.DoesNotContain("ServoDrive", xaml[externals..], StringComparison.Ordinal);
    }
}
