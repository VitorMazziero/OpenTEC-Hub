using OpenTECHub.Protocol;
using OpenTECHub.Services.Telemetry;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// The agitation tile after it stopped echoing the command back.
/// </summary>
/// <remarks>
/// Until the ASDA-B2 node existed there was no RPM feedback anywhere on the wire, so the
/// tile could only show what had been commanded and a zero there meant "loop idle". Both
/// of those are now false, and the two facts they used to hide - a stopped motor, and a
/// node that is not reporting - have to be told apart on screen.
/// </remarks>
public class MeasuredAgitationTests
{
    private static ProcessVariableViewModel Motor() => new(
        "motor", "Agitação", "rpm", decimals: 1, channel: TelemetryChannel.ServoRpm);

    /// <summary>
    /// Zero rpm is a stopped motor and must render as a number.
    /// </summary>
    /// <remarks>
    /// The tile used to blank a zero, because the value was the command. Keeping that
    /// would hide the one reading the servo node was installed to provide: proof that the
    /// shaft really has stopped, as opposed to nobody knowing.
    /// </remarks>
    [Fact]
    public void A_measured_zero_shows_as_zero_and_not_a_dash()
    {
        var motor = Motor();

        motor.Push((double?)0.0);

        Assert.Equal(0.0, motor.Value);
        Assert.Equal("0,0", motor.FormattedValue);
    }

    [Fact]
    public void No_sample_shows_a_dash_rather_than_a_number()
    {
        var motor = Motor();
        motor.Push((double?)92.7);

        motor.Push((double?)null);

        Assert.Null(motor.Value);
        Assert.Equal("—", motor.FormattedValue);
    }

    /// <summary>
    /// A negative reading survives, where the sentinel overload would have eaten it.
    /// </summary>
    /// <remarks>
    /// <c>NotReceived</c> is -1.0 and the drive reports a few tenths below zero at rest -
    /// the bench captured -0.30 rpm from <c>words=[0xFFFD 0xFFFF]</c>. Anything colder
    /// than -1.0 rpm would be discarded as "missing" by the value-inferring overload,
    /// which is why the caller passes its own presence flag instead.
    /// </remarks>
    [Theory]
    [InlineData(-0.3)]
    [InlineData(-1.0)]
    [InlineData(-2.5)]
    public void A_negative_reading_is_kept(double rpm)
    {
        var motor = Motor();

        motor.Push((double?)rpm);

        Assert.Equal(rpm, motor.Value);
    }

    [Fact]
    public void The_sentinel_overload_still_treats_not_received_as_absence()
    {
        var motor = Motor();
        motor.Push(92.7);

        motor.Push(SensorReadings.NotReceived);

        Assert.Null(motor.Value);
    }

    /// <summary>
    /// Command and measurement occupy different slots, and both stay readable.
    /// </summary>
    /// <remarks>
    /// The numbers are the bench's: 100 rpm commanded produced 96.6 at the shaft before
    /// the CN1 correction landed in Hub firmware 9.1.0-dev. Whatever the residual, the
    /// operator must be able to see both figures at once - showing one of them twice was
    /// the old behaviour.
    /// </remarks>
    [Fact]
    public void The_command_and_the_measurement_are_both_visible()
    {
        var motor = Motor();

        motor.Setpoint = 100.0;
        motor.Push((double?)96.6);

        Assert.Equal("96,6", motor.FormattedValue);
        Assert.Equal("100,0", motor.FormattedSetpoint);
    }

    // ── The history keeps them apart too ─────────────────────────────────────

    private static SensorSnapshot Frame(double minutes, bool hasSample, double servoRpm)
        => new()
        {
            TimeMinutes = minutes,
            HasServoTelemetry = true,
            HasServoSample = hasSample,
            ServoOnline = hasSample,
            ServoRpm = hasSample ? servoRpm : SensorReadings.NotReceived,
        };

    [Fact]
    public void The_two_agitation_channels_carry_different_numbers()
    {
        var history = new TelemetryHistory(capacity: 16);

        history.Add(Frame(0.0, hasSample: true, servoRpm: 96.6), commandedRpm: 100);

        var commanded = history.GetSeries(TelemetryChannel.MotorRpm, null, 10);
        var measured = history.GetSeries(TelemetryChannel.ServoRpm, null, 10);

        Assert.Equal(100.0, Assert.Single(commanded.Values));
        Assert.Equal(96.6, Assert.Single(measured.Values));
    }

    /// <summary>
    /// A missing sample charts as a gap, never as a line falling to zero.
    /// </summary>
    [Fact]
    public void A_frame_without_a_sample_charts_as_a_gap()
    {
        var history = new TelemetryHistory(capacity: 16);

        history.Add(Frame(0.0, hasSample: true, servoRpm: 600.5), commandedRpm: 600);
        history.Add(Frame(1.0, hasSample: false, servoRpm: 0), commandedRpm: 600);

        var measured = history.GetSeries(TelemetryChannel.ServoRpm, null, 10);

        Assert.Equal(600.5, measured.Values[0]);
        Assert.True(double.IsNaN(measured.Values[1]));

        // The command is unaffected: it never depended on the node being there.
        var commanded = history.GetSeries(TelemetryChannel.MotorRpm, null, 10);
        Assert.Equal(600.0, commanded.Values[1]);
    }

    /// <summary>
    /// A measured zero is charted, unlike a commanded zero.
    /// </summary>
    /// <remarks>
    /// The asymmetry is deliberate. A commanded zero means the loop was not driving, so
    /// it charts as a gap; a measured zero is a real observation of a stopped shaft.
    /// </remarks>
    [Fact]
    public void A_measured_zero_is_charted_while_a_commanded_zero_is_a_gap()
    {
        var history = new TelemetryHistory(capacity: 16);

        history.Add(Frame(0.0, hasSample: true, servoRpm: 0.0), commandedRpm: 0);

        var measured = history.GetSeries(TelemetryChannel.ServoRpm, null, 10);
        var commanded = history.GetSeries(TelemetryChannel.MotorRpm, null, 10);

        Assert.Equal(0.0, Assert.Single(measured.Values));
        Assert.True(double.IsNaN(Assert.Single(commanded.Values)));
    }

    /// <summary>
    /// The measured channel is appended after the existing members, not inserted.
    /// </summary>
    /// <remarks>
    /// <c>SessionFileService</c> maps channels to log columns by identity, and anything
    /// that persisted one by ordinal would be silently reinterpreted by an insertion.
    /// </remarks>
    [Fact]
    public void The_measured_channel_was_appended_to_the_enum()
    {
        Assert.True((int)TelemetryChannel.ServoRpm > (int)TelemetryChannel.CascadeKlaDemand);
        Assert.Equal(5, (int)TelemetryChannel.MotorRpm);
    }
}
