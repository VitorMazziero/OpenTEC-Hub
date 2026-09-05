using OpenTECHub.Protocol;
using OpenTECHub.Services.Telemetry;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// The six servo series: what they record, where the gaps fall, and what stays off the
/// default panels.
/// </summary>
public class ServoChartChannelTests
{
    private static SensorSnapshot Frame(double minutes, bool hasSample) => new()
    {
        TimeMinutes = minutes,
        HasServoTelemetry = true,
        HasServoSample = hasSample,
        ServoOnline = hasSample,
        ServoRpm = hasSample ? 600.5 : SensorReadings.NotReceived,
        ServoTorquePct = hasSample ? 1.98 : SensorReadings.NotReceived,
        ServoTorqueNm = hasSample ? 0.0251 : SensorReadings.NotReceived,
        ServoLoadPct = hasSample ? 2.0 : SensorReadings.NotReceived,
        ServoPowerW = hasSample ? 1.58 : SensorReadings.NotReceived,
        ServoEnergyWh = hasSample ? 0.42 : SensorReadings.NotReceived,
    };

    private static readonly TelemetryChannel[] ServoChannels =
    [
        TelemetryChannel.ServoRpm,
        TelemetryChannel.ServoTorquePct,
        TelemetryChannel.ServoTorqueNm,
        TelemetryChannel.ServoLoadPct,
        TelemetryChannel.ServoPowerW,
        TelemetryChannel.ServoEnergyWh,
    ];

    [Fact]
    public void All_six_channels_record_their_reading()
    {
        var history = new TelemetryHistory(capacity: 16);

        history.Add(Frame(0.0, hasSample: true), commandedRpm: 600);

        Assert.Equal(600.5, Single(history, TelemetryChannel.ServoRpm));
        Assert.Equal(1.98, Single(history, TelemetryChannel.ServoTorquePct));
        Assert.Equal(0.0251, Single(history, TelemetryChannel.ServoTorqueNm));
        Assert.Equal(2.0, Single(history, TelemetryChannel.ServoLoadPct));
        Assert.Equal(1.58, Single(history, TelemetryChannel.ServoPowerW));
        Assert.Equal(0.42, Single(history, TelemetryChannel.ServoEnergyWh));
    }

    /// <summary>
    /// The six arrive and leave together, because the Hub publishes them as a set.
    /// </summary>
    [Fact]
    public void All_six_go_to_a_gap_together_when_the_sample_stops()
    {
        var history = new TelemetryHistory(capacity: 16);

        history.Add(Frame(0.0, hasSample: true), commandedRpm: 600);
        history.Add(Frame(1.0, hasSample: false), commandedRpm: 600);

        foreach (var channel in ServoChannels)
        {
            var series = history.GetSeries(channel, null, 10);
            Assert.False(double.IsNaN(series.Values[0]), $"{channel} deveria ter valor no primeiro quadro");
            Assert.True(double.IsNaN(series.Values[1]), $"{channel} deveria ser lacuna no segundo");
        }
    }

    /// <summary>
    /// Zero is charted, because a stopped shaft is a measurement.
    /// </summary>
    /// <remarks>
    /// The commanded channel does the opposite on purpose: a commanded zero means the loop
    /// was not driving, which is an absence of command rather than a reading of zero.
    /// </remarks>
    [Fact]
    public void Measured_zeros_are_charted_rather_than_dropped()
    {
        var history = new TelemetryHistory(capacity: 16);

        history.Add(
            Frame(0.0, hasSample: true) with
            {
                ServoRpm = 0.0,
                ServoTorquePct = 0.0,
                ServoPowerW = 0.0,
            },
            commandedRpm: 0);

        Assert.Equal(0.0, Single(history, TelemetryChannel.ServoRpm));
        Assert.Equal(0.0, Single(history, TelemetryChannel.ServoPowerW));
        Assert.True(double.IsNaN(Single(history, TelemetryChannel.MotorRpm)));
    }

    /// <summary>
    /// Negative torque survives, where a sentinel test would have discarded it.
    /// </summary>
    /// <remarks>
    /// <c>NotReceived</c> is -1.0 and braking torque goes below it. Charting that as a gap
    /// would erase exactly the part of a deceleration worth looking at.
    /// </remarks>
    [Fact]
    public void Negative_torque_is_charted_and_not_read_as_the_sentinel()
    {
        var history = new TelemetryHistory(capacity: 16);

        history.Add(Frame(0.0, hasSample: true) with { ServoTorquePct = -1.0 }, commandedRpm: 600);

        Assert.Equal(-1.0, Single(history, TelemetryChannel.ServoTorquePct));
    }

    /// <summary>
    /// Energy is allowed to step down: the node restarts its accumulator.
    /// </summary>
    [Fact]
    public void The_energy_series_may_step_backwards()
    {
        var history = new TelemetryHistory(capacity: 16);

        history.Add(Frame(0.0, hasSample: true) with { ServoEnergyWh = 12.5 }, commandedRpm: 600);
        history.Add(Frame(1.0, hasSample: true) with { ServoEnergyWh = 0.0 }, commandedRpm: 600);

        var series = history.GetSeries(TelemetryChannel.ServoEnergyWh, null, 10);
        Assert.Equal(12.5, series.Values[0]);
        Assert.Equal(0.0, series.Values[1]);
    }

    /// <summary>
    /// Appended after the existing members, never inserted among them.
    /// </summary>
    /// <remarks>
    /// <c>SessionFileService</c> maps channels to log columns, and anything that persisted
    /// a channel by ordinal would be silently reinterpreted by an insertion.
    /// </remarks>
    [Fact]
    public void The_six_channels_were_appended_to_the_enum()
    {
        foreach (var channel in ServoChannels)
        {
            Assert.True(
                (int)channel > (int)TelemetryChannel.CascadeKlaDemand,
                $"{channel} foi inserido no meio do enum");
        }

        Assert.Equal(5, (int)TelemetryChannel.MotorRpm);
    }

    // ── The picker ───────────────────────────────────────────────────────────

    private static ChartsViewModel Charts()
        => new(new TelemetryHistory(capacity: 16), new MemorySettingsService());

    [Fact]
    public void Every_servo_channel_is_offered_with_its_unit()
    {
        var charts = Charts();

        var expected = new Dictionary<TelemetryChannel, string>
        {
            [TelemetryChannel.ServoRpm] = "rpm",
            [TelemetryChannel.ServoTorquePct] = "%",
            [TelemetryChannel.ServoTorqueNm] = "N·m",
            [TelemetryChannel.ServoLoadPct] = "%",
            [TelemetryChannel.ServoPowerW] = "W",
            [TelemetryChannel.ServoEnergyWh] = "Wh",
        };

        foreach (var (channel, unit) in expected)
        {
            var option = Assert.Single(charts.Channels, c => c.Channel == channel);
            Assert.Equal(unit, option.Unit);
        }
    }

    /// <summary>
    /// Power and energy say "estimada" in the picker, not only on the card.
    /// </summary>
    /// <remarks>
    /// A chart is exported and pasted into a report, where the card's qualifier does not
    /// travel with it. The series name is the only label that survives that trip.
    /// </remarks>
    [Fact]
    public void Power_and_energy_are_named_as_estimated_mechanical()
    {
        var charts = Charts();

        Assert.Contains("mecânica estimada",
            Assert.Single(charts.Channels, c => c.Channel == TelemetryChannel.ServoPowerW).Title,
            StringComparison.Ordinal);

        Assert.Contains("mecânica acumulada",
            Assert.Single(charts.Channels, c => c.Channel == TelemetryChannel.ServoEnergyWh).Title,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// No two channels share a title.
    /// </summary>
    /// <remarks>
    /// The picker renders a channel through its title alone, so duplicates would be
    /// indistinguishable in the list - and the synoptic resolves a saved panel by title
    /// first, so it would silently pick whichever came first. The two torque series are the
    /// case that forced this: both are torque, and only the unit tells them apart.
    /// </remarks>
    [Fact]
    public void No_two_channels_share_a_title()
    {
        var titles = Charts().Channels.Select(c => c.Title).ToList();

        Assert.Equal(titles.Count, titles.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The default panels are untouched: the servo has to be chosen deliberately.
    /// </summary>
    /// <remarks>
    /// Six new series shown by default would rewrite the page every operator already knows.
    /// </remarks>
    [Fact]
    public void No_servo_channel_appears_in_the_four_default_panels()
    {
        var charts = Charts();

        var defaults = new[]
        {
            charts.LeftChannel.Channel,
            charts.RightChannel!.Channel,
            charts.BottomLeftChannel!.Channel,
            charts.BottomRightChannel!.Channel,
        };

        Assert.Equal(
            [TelemetryChannel.Temperature, TelemetryChannel.Oxygen, TelemetryChannel.PH, TelemetryChannel.Flow],
            defaults);

        Assert.DoesNotContain(defaults, d => ServoChannels.Contains(d));
        Assert.DoesNotContain(defaults, d => d == TelemetryChannel.MotorRpm);
    }

    private static double Single(ITelemetryHistory history, TelemetryChannel channel)
        => Assert.Single(history.GetSeries(channel, null, 10).Values);
}
