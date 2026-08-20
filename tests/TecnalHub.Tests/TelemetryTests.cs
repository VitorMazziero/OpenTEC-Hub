using System.Globalization;
using TecnalHub.Protocol;
using TecnalHub.Services.Persistence;
using TecnalHub.Services.Telemetry;
using Xunit;

namespace TecnalHub.Tests;

/// <summary>
/// Ring-buffer behaviour of the chart history.
/// </summary>
/// <remarks>
/// The roadmap's target is flat memory across a 24 h run, so the buffer must overwrite
/// rather than grow. Wraparound is where an off-by-one hides, and a chart drawn from a
/// mis-wrapped buffer would show the run's history in the wrong order.
/// </remarks>
public class TelemetryHistoryTests
{
    private static SensorSnapshot Frame(double minutes, double temperature)
        => new()
        {
            TimeRawSeconds = minutes * 60.0,
            TimeMinutes = minutes,
            Temperature = temperature,
            OxygenCalibrated = SensorReadings.NotReceived,
            PHCalibrated = SensorReadings.NotReceived,
            FlowRate = SensorReadings.NotReceived,
            Pressure = 0,
            Antifoam = SensorReadings.NotReceived,
            Distance = SensorReadings.NotReceived,
            BiomassAbsorbance = SensorReadings.NotReceived,
            PumpFlow = SensorReadings.NotReceived,
            PumpVolume = SensorReadings.NotReceived,
        };

    [Fact]
    public void Samples_are_returned_in_order()
    {
        var history = new TelemetryHistory(capacity: 100);

        for (var i = 0; i < 10; i++)
        {
            history.Add(Frame(i, 20 + i), commandedRpm: 0);
        }

        var series = history.GetSeries(TelemetryChannel.Temperature, null, 1000);

        Assert.Equal(10, series.Count);
        Assert.Equal(0, series.Minutes[0]);
        Assert.Equal(9, series.Minutes[9]);
        Assert.Equal(20, series.Values[0]);
        Assert.Equal(29, series.Values[9]);
    }

    [Fact]
    public void Capacity_is_a_hard_limit_and_the_oldest_samples_are_dropped()
    {
        var history = new TelemetryHistory(capacity: 10);

        for (var i = 0; i < 25; i++)
        {
            history.Add(Frame(i, i), commandedRpm: 0);
        }

        var series = history.GetSeries(TelemetryChannel.Temperature, null, 1000);

        Assert.Equal(10, history.Count);
        Assert.Equal(10, series.Count);

        // The last ten written are 15..24, still in order across the wrap.
        Assert.Equal(15, series.Minutes[0]);
        Assert.Equal(24, series.Minutes[9]);
        Assert.Equal(24, history.LatestMinutes);
    }

    [Fact]
    public void A_window_restricts_the_series_to_recent_samples()
    {
        var history = new TelemetryHistory(capacity: 1000);

        for (var i = 0; i < 60; i++)
        {
            history.Add(Frame(i, i), commandedRpm: 0);
        }

        // Newest sample is at minute 59, so a 10-minute window starts at 49.
        var series = history.GetSeries(
            TelemetryChannel.Temperature, TimeSpan.FromMinutes(10), 1000);

        Assert.Equal(49, series.Minutes[0]);
        Assert.Equal(59, series.Minutes[^1]);
    }

    [Fact]
    public void Downsampling_respects_the_point_budget_and_keeps_the_range()
    {
        var history = new TelemetryHistory(capacity: 10_000);

        for (var i = 0; i < 5000; i++)
        {
            history.Add(Frame(i, i), commandedRpm: 0);
        }

        var series = history.GetSeries(TelemetryChannel.Temperature, null, 100);

        Assert.True(series.Count <= 100, $"expected at most 100 points, got {series.Count}");
        Assert.Equal(0, series.Minutes[0]);
        Assert.True(series.Minutes[^1] > 4800, "downsampling dropped the newest data");
    }

    /// <summary>
    /// Sentinels must become NaN so a chart shows a gap. Plotting -1 would draw a line
    /// diving to a value that looks like a real measurement.
    /// </summary>
    [Fact]
    public void Sentinels_become_NaN_rather_than_minus_one()
    {
        var history = new TelemetryHistory(capacity: 10);

        history.Add(Frame(0, SensorReadings.NotReceived), commandedRpm: 0);
        history.Add(Frame(1, 25), commandedRpm: 0);

        var series = history.GetSeries(TelemetryChannel.Temperature, null, 100);

        Assert.True(double.IsNaN(series.Values[0]));
        Assert.Equal(25, series.Values[1]);
    }

    /// <summary>Agitation is charted from the commanded value; there is no feedback.</summary>
    [Fact]
    public void Commanded_rpm_is_recorded_and_zero_reads_as_no_data()
    {
        var history = new TelemetryHistory(capacity: 10);

        history.Add(Frame(0, 25), commandedRpm: 0);
        history.Add(Frame(1, 25), commandedRpm: 450);

        var series = history.GetSeries(TelemetryChannel.MotorRpm, null, 100);

        Assert.True(double.IsNaN(series.Values[0]));
        Assert.Equal(450, series.Values[1]);
    }

    [Fact]
    public void An_empty_history_returns_an_empty_series()
        => Assert.Equal(0, new TelemetryHistory(capacity: 10)
            .GetSeries(TelemetryChannel.Temperature, null, 100).Count);
}

/// <summary>
/// The session-log row format.
/// </summary>
/// <remarks>
/// Byte-compatible with v.6 because existing analysis scripts read these files. The
/// column set, order, tab separator and decimal places are a contract, not a
/// formatting preference - see <c>docs/MIGRATION.md</c>.
/// </remarks>
public class SessionLogFormatTests
{
    private static SensorSnapshot FullFrame() => new()
    {
        TimeMinutes = 12.34,
        Temperature = 30.25,
        PHCalibrated = 6.98,
        Antifoam = 1.5,
        Pressure = 3.5,
        OxygenCalibrated = 42.125,
        FlowRate = 2.5,
        Distance = 150.0,
        BiomassAbsorbance = 0.4567,
        PumpVolume = 12.5,
        PumpFlow = 0.75,
    };

    [Fact]
    public void Header_matches_v6_exactly_including_the_accented_final_column()
        => Assert.Equal(
            "Time (min)\tTemperature (°C)\tMotor (rpm)\tpH\tAntifoam\t" +
            "Pressure\tOxygen\tFlowmeter\tDistance\tOUR\tBiomass\tPump Volume\tPump Flow\tConexão",
            SessionLogFormat.Header);

    [Fact]
    public void A_row_has_fourteen_tab_separated_columns()
    {
        var row = SessionLogger.BuildRow(FullFrame(), commandedRpm: 790, "USB");

        Assert.Equal(14, row.Split('\t').Length);
        Assert.Equal(14, SessionLogFormat.Header.Split('\t').Length);
    }

    [Fact]
    public void Column_order_and_precision_match_v6()
    {
        var row = SessionLogger.BuildRow(FullFrame(), commandedRpm: 790, "USB");

        Assert.Equal(
            "12.34\t30.25\t790.000\t6.98\t1.500\t3.5\t42.125\t2.500\t150.00\t-1.00000\t0.4567\t12.500\t0.750\tUSB",
            row);
    }

    /// <summary>
    /// The file is data for downstream tools, not text for a person. A pt-BR decimal
    /// comma here would silently break every consumer, exactly as it would on the wire.
    /// </summary>
    [Fact]
    public void Numbers_are_invariant_even_under_a_pt_BR_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("pt-BR");

            var row = SessionLogger.BuildRow(FullFrame(), commandedRpm: 790, "USB");

            Assert.DoesNotContain(",", row, StringComparison.Ordinal);
            Assert.Contains("30.25", row, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>
    /// Columns outside the Phase 1 scope still appear, carrying the sentinel, so the
    /// column count never changes between versions of this app.
    /// </summary>
    [Fact]
    public void Not_yet_computed_columns_carry_the_sentinel()
    {
        var row = SessionLogger.BuildRow(FullFrame(), commandedRpm: 0, "USB");
        var columns = row.Split('\t');

        Assert.Equal("-1.00000", columns[9]); // OUR arrives with the Phase 2 soft sensor
    }
}
