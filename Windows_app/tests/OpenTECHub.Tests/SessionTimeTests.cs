using OpenTECHub.Protocol;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// The operator session clock: a local display/log offset that never touches the device
/// clock and never rewrites samples already logged — exactly v.6's behaviour.
/// </summary>
public sealed class SessionTimeTests
{
    [Fact]
    public void Zeroing_rebases_reported_minutes_without_changing_the_device_clock()
    {
        var readings = new SensorReadings { TimeRawSeconds = 600 }; // 10 min since boot

        Assert.Equal(10.0, readings.TimeMinutes);

        readings.ZeroTime();

        Assert.Equal(0.0, readings.TimeMinutes);
        Assert.Equal(600, readings.TimeRawSeconds); // the device clock is untouched

        // A minute of real time later, elapsed reads one minute — measured from the zero.
        readings.TimeRawSeconds = 660;
        Assert.Equal(1.0, readings.TimeMinutes);
        Assert.Equal(660, readings.TimeRawSeconds);
    }

    [Fact]
    public void The_offset_travels_on_the_published_snapshot()
    {
        var readings = new SensorReadings { TimeRawSeconds = 600 };
        readings.ZeroTime();
        readings.TimeRawSeconds = 720; // two minutes past the zero

        Assert.Equal(2.0, readings.Snapshot().TimeMinutes);
    }
}
