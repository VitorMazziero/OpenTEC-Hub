using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Telemetry;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// The servo session sidecar: a companion file that leaves the frozen log untouched.
/// </summary>
/// <remarks>
/// The main log is read positionally by analysis scripts that predate this application, so
/// its columns cannot move. Everything the servo records goes into a separate file opened
/// and closed with it - and every session recorded before the node existed has to keep
/// opening exactly as it did.
/// </remarks>
public sealed class ServoSidecarTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "opentec-servo-" + Guid.NewGuid().ToString("N"));

    private string SessionPath => Path.Combine(_directory, "sessao.txt");

    private string SidecarPath => Path.Combine(_directory, "sessao" + ServoSessionLogFormat.FileSuffix);

    public ServoSidecarTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private static SensorSnapshot Sample(double minutes, bool hasSample = true) => new()
    {
        TimeMinutes = minutes,
        HubFirmwareVersion = "9.1.0-dev",
        HubProtocolVersion = 9,
        HasServoTelemetry = true,
        HasServoSample = hasSample,
        ServoOnline = hasSample,
        ServoCommEnabled = true,
        ServoRpm = hasSample ? 600.5 : SensorReadings.NotReceived,
        ServoTorquePct = hasSample ? 1.9 : SensorReadings.NotReceived,
        ServoTorqueNm = hasSample ? 0.0241 : SensorReadings.NotReceived,
        ServoLoadPct = hasSample ? 2.0 : SensorReadings.NotReceived,
        ServoPowerW = hasSample ? 1.517 : SensorReadings.NotReceived,
        ServoEnergyWh = hasSample ? 0.421 : SensorReadings.NotReceived,
        ServoState = hasSample ? 2 : -1,
        ServoAlarm = hasSample ? 0 : -1,
        ServoCommOk = hasSample ? 900 : -1,
        ServoCommErr = hasSample ? 1 : -1,
    };

    private SessionLogger Start()
    {
        var logger = new SessionLogger(NullLogger<SessionLogger>.Instance);
        logger.Start(SessionPath);
        return logger;
    }

    // ── The file itself ──────────────────────────────────────────────────────

    [Fact]
    public void Starting_the_session_opens_the_sidecar_beside_it()
    {
        var logger = Start();
        logger.Stop();

        Assert.True(File.Exists(SidecarPath));
        Assert.Equal(ServoSessionLogFormat.Header, File.ReadAllLines(SidecarPath)[0]);
    }

    /// <summary>
    /// The preamble waits for the first frame, because the Hub identifies itself in telemetry.
    /// </summary>
    /// <remarks>
    /// Written at <c>Start</c> it could only say "unknown", which is precisely the thing a
    /// later reader needs to know: firmware 9.1.0-dev corrects the CN1 command and 9.0.0-dev
    /// does not, so the same setpoint produced different shaft speeds under each.
    /// </remarks>
    [Fact]
    public void The_preamble_records_which_hub_produced_the_file()
    {
        var logger = Start();
        logger.Write(Sample(0.0), commandedRpm: 600, "USB");
        logger.Stop();

        var text = File.ReadAllText(SidecarPath);

        Assert.Contains("# opentec-servo-power v1", text, StringComparison.Ordinal);
        Assert.Contains("# hub_firmware: 9.1.0-dev", text, StringComparison.Ordinal);
        Assert.Contains("# hub_protocol: 9", text, StringComparison.Ordinal);
        Assert.Contains("MECANICAS ESTIMADAS", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_older_hub_leaves_the_versions_marked_unknown()
    {
        var logger = Start();
        logger.Write(
            Sample(0.0) with { HubFirmwareVersion = null, HubProtocolVersion = -1 },
            commandedRpm: 600,
            "USB");
        logger.Stop();

        var text = File.ReadAllText(SidecarPath);

        Assert.Contains("# hub_firmware: desconhecido", text, StringComparison.Ordinal);
        Assert.Contains("# hub_protocol: desconhecido", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_preamble_is_written_once_and_not_per_row()
    {
        var logger = Start();
        logger.Write(Sample(0.0), commandedRpm: 600, "USB");
        logger.Write(Sample(1.0), commandedRpm: 600, "USB");
        logger.Write(Sample(2.0), commandedRpm: 600, "USB");
        logger.Stop();

        var preambleLines = File.ReadAllLines(SidecarPath).Count(l => l.StartsWith('#'));
        Assert.Equal(ServoSessionLogFormat.BuildPreamble("x", 9, "y").Split('\n').Length, preambleLines);
    }

    // ── Row content ──────────────────────────────────────────────────────────

    [Fact]
    public void A_sample_row_carries_all_twelve_columns_with_a_decimal_point()
    {
        var row = SessionLogger.BuildServoRow(Sample(1.5));
        var fields = row.Split('\t');

        Assert.Equal(12, fields.Length);
        Assert.Equal("1.50", fields[0]);
        Assert.Equal("1", fields[1]);
        Assert.Equal("600.5", fields[2]);
        // One decimal on torque, which is the drive's own resolution: P0-44 reports
        // in 0.1 %, and a second decimal would claim precision the drive never had.
        Assert.Equal("1.9", fields[3]);
        Assert.Equal("0.0241", fields[4]);
        Assert.Equal("2", fields[5]);
        Assert.Equal("1.517", fields[6]);
        Assert.Equal("0.421000", fields[7]);
        Assert.Equal("2", fields[8]);
        Assert.Equal("0", fields[9]);
        Assert.Equal("900", fields[10]);
        Assert.Equal("1", fields[11]);
    }

    /// <summary>
    /// A frame without a sample writes blanks, never zeros.
    /// </summary>
    /// <remarks>
    /// A zero here would be indistinguishable from a stopped motor, and no later reader
    /// could undo that. The time column still goes out, so the file stays aligned with the
    /// main log line for line.
    /// </remarks>
    [Fact]
    public void A_frame_without_a_sample_writes_blanks_and_keeps_the_time()
    {
        var fields = SessionLogger.BuildServoRow(Sample(2.0, hasSample: false)).Split('\t');

        Assert.Equal("2.00", fields[0]);
        Assert.Equal("0", fields[1]); // presence is known: the node is absent
        foreach (var index in Enumerable.Range(2, 10))
        {
            Assert.Equal(string.Empty, fields[index]);
        }
    }

    [Fact]
    public void A_measured_zero_is_written_as_zero()
    {
        var fields = SessionLogger
            .BuildServoRow(Sample(3.0) with { ServoRpm = 0.0, ServoPowerW = 0.0 })
            .Split('\t');

        Assert.Equal("0.0", fields[2]);
        Assert.Equal("0.000", fields[6]);
    }

    /// <summary>
    /// Against an older Hub the presence column is blank, not zero.
    /// </summary>
    /// <remarks>
    /// A Hub that predates the contract has claimed nothing about the node. Writing 0 would
    /// record "the servo was absent" for a run where nobody ever asked.
    /// </remarks>
    [Fact]
    public void An_unclaimed_servo_leaves_the_presence_column_blank()
    {
        var fields = SessionLogger
            .BuildServoRow(new SensorSnapshot { TimeMinutes = 1.0 })
            .Split('\t');

        Assert.Equal(string.Empty, fields[1]);
    }

    [Fact]
    public void The_row_count_matches_the_main_log()
    {
        var logger = Start();
        logger.Write(Sample(0.0), commandedRpm: 600, "USB");
        logger.Write(Sample(1.0, hasSample: false), commandedRpm: 600, "USB");
        logger.Write(Sample(2.0), commandedRpm: 600, "USB");
        logger.Stop();

        var mainRows = File.ReadAllLines(SessionPath).Length - 1; // minus the header
        var sidecarRows = File.ReadAllLines(SidecarPath).Count(l => !l.StartsWith('#') && !l.StartsWith("time_min"));

        Assert.Equal(3, mainRows);
        Assert.Equal(mainRows, sidecarRows);
    }

    // ── The main log is untouched ────────────────────────────────────────────

    /// <summary>
    /// Nothing about the frozen format moved.
    /// </summary>
    /// <remarks>
    /// The whole reason for a separate file. A servo column inserted into the main log would
    /// shift every column after it while the file still looked the same to a script reading
    /// it positionally.
    /// </remarks>
    [Fact]
    public void The_main_log_header_and_column_count_are_unchanged()
    {
        var logger = Start();
        logger.Write(Sample(0.0), commandedRpm: 600, "USB");
        logger.Stop();

        var lines = File.ReadAllLines(SessionPath);
        Assert.Equal(SessionLogFormat.Header, lines[0]);
        Assert.Equal(14, lines[0].Split('\t').Length);
        Assert.Equal(14, lines[1].Split('\t').Length);
        Assert.DoesNotContain("Servo", lines[0], StringComparison.Ordinal);
    }

    // ── Reading it back ──────────────────────────────────────────────────────

    [Fact]
    public void A_session_with_a_sidecar_is_reported_as_having_one()
    {
        {
            var logger = Start();
            logger.Write(Sample(0.0), commandedRpm: 600, "USB");
            logger.Stop();
        }

        var summary = SessionFileService.Inspect(SessionPath);

        Assert.True(summary.HasServoSidecar);
        Assert.Equal(SidecarPath, summary.ServoSidecarPath);
        Assert.Contains("Com telemetria", summary.ServoStatus, StringComparison.Ordinal);
    }

    /// <summary>
    /// A session without a sidecar opens exactly as it always did.
    /// </summary>
    /// <remarks>
    /// Every run recorded before the node existed is in this shape, and so is any run on a
    /// module without a servo. Absence is normal and must never surface as an error.
    /// </remarks>
    [Fact]
    public void A_session_without_a_sidecar_still_loads()
    {
        File.WriteAllLines(SessionPath,
        [
            SessionLogFormat.Header,
            string.Join('\t', "0.00", "30.00", "600.000", "6.98", "0.000", "101.3",
                "95.000", "2.000", "150.00", "-1.00000", "0.5000", "0.000", "0.000", "USB"),
        ]);

        var summary = SessionFileService.Inspect(SessionPath);
        Assert.False(summary.HasServoSidecar);
        Assert.Contains("Sem telemetria", summary.ServoStatus, StringComparison.Ordinal);

        var data = new SessionFileService().Load(summary);
        Assert.True(data.Series.ContainsKey(TelemetryChannel.Temperature));
        Assert.False(data.Series.ContainsKey(TelemetryChannel.ServoRpm));
    }

    [Fact]
    public void A_loaded_session_charts_the_servo_series_from_the_sidecar()
    {
        {
            var logger = Start();
            logger.Write(Sample(0.0), commandedRpm: 600, "USB");
            logger.Write(Sample(1.0, hasSample: false), commandedRpm: 600, "USB");
            logger.Write(Sample(2.0), commandedRpm: 600, "USB");
            logger.Stop();
        }

        var data = new SessionFileService().Load(SessionFileService.Inspect(SessionPath));

        var rpm = data.Series[TelemetryChannel.ServoRpm];
        Assert.Equal(3, rpm.Count);
        Assert.Equal(600.5, rpm.Values[0]);
        Assert.True(double.IsNaN(rpm.Values[1]), "a linha sem amostra deve virar lacuna");
        Assert.Equal(600.5, rpm.Values[2]);

        Assert.Equal(1.517, data.Series[TelemetryChannel.ServoPowerW].Values[0]);
        Assert.Equal(0.421, data.Series[TelemetryChannel.ServoEnergyWh].Values[0], precision: 3);
    }

    /// <summary>
    /// The sidecar is not offered in the session list as a session of its own.
    /// </summary>
    [Fact]
    public void Discovery_does_not_list_the_sidecar_as_a_session()
    {
        {
            var logger = Start();
            logger.Write(Sample(0.0), commandedRpm: 600, "USB");
            logger.Stop();
        }

        var settings = new AppSettings { Logging = new LoggingSettings { SessionLogPath = SessionPath } };
        var found = new SessionFileService().Discover(settings);

        Assert.DoesNotContain(found, f => f.Path.EndsWith(ServoSessionLogFormat.FileSuffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Reopening an existing session appends without a second preamble.</summary>
    [Fact]
    public void Reopening_a_session_does_not_repeat_the_header()
    {
        {
            var logger = Start();
            logger.Write(Sample(0.0), commandedRpm: 600, "USB");
            logger.Stop();
        }

        {
            var logger = Start();
            logger.Write(Sample(1.0), commandedRpm: 600, "USB");
            logger.Stop();
        }

        var lines = File.ReadAllLines(SidecarPath);
        Assert.Equal(1, lines.Count(l => l.StartsWith("time_min", StringComparison.Ordinal)));
        Assert.Equal(1, lines.Count(l => l.StartsWith("# opentec-servo-power", StringComparison.Ordinal)));
    }
}
