using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.Services.Telemetry;

/// <summary>Writes the tab-separated session log v.6's analysis scripts read.</summary>
public interface ISessionLogger : IAsyncDisposable
{
    /// <summary>Raised when recording state, path or row count changes.</summary>
    event Action? StatusChanged;

    /// <summary>True while rows are being written.</summary>
    bool IsLogging { get; }

    /// <summary>File currently being written, or null.</summary>
    string? CurrentPath { get; }

    /// <summary>Rows written this session.</summary>
    int RowsWritten { get; }

    /// <summary>Opens <paramref name="path"/> for logging, writing a header if new.</summary>
    void Start(string path);

    /// <summary>Stops writing and closes the file.</summary>
    void Stop();

    /// <summary>Appends one row. No-op when not logging.</summary>
    void Write(SensorSnapshot snapshot, double commandedRpm, string connectionStatus);
}

/// <summary>
/// Appends telemetry rows in v.6's exact format.
/// </summary>
/// <remarks>
/// <para>
/// <b>The format is a contract, not a preference.</b> Existing analysis scripts read
/// these files, so the column set, their order, the tab separator and the decimal
/// places are all fixed. Columns outside the Phase 1 scope are still emitted, carrying
/// the not-received sentinel, so the column count never changes between versions of
/// this app and a script never has to ask which version wrote a file.
/// </para>
/// <para>
/// Numbers are written with <see cref="CultureInfo.InvariantCulture"/>. The file is
/// data for downstream tools, not text for a person - a pt-BR decimal comma here would
/// silently break every consumer, in the same way it would on the wire.
/// </para>
/// </remarks>
public sealed class SessionLogger(ILogger<SessionLogger> log) : ISessionLogger
{
    private readonly Lock _gate = new();

    private StreamWriter? _writer;
    private int _rowsWritten;

    /// <summary>
    /// Rows are buffered and flushed at most once per <see cref="FlushInterval"/> (§F.1). With
    /// <c>AutoFlush</c> every row was a write syscall on the UI thread, one per telemetry frame;
    /// a bounded buffer costs at most one interval of rows if the process dies — the crash
    /// reporter covers that case — and flushes on stop and close as before.
    /// </summary>
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);
    private long _lastFlushTimestamp;

    /// <summary>The servo sidecar, opened and closed with the main log.</summary>
    /// <remarks>
    /// Derived from the main path rather than passed in, so every caller of
    /// <see cref="Start"/> - and there are several - gets the companion file without
    /// knowing it exists. The two can then never be started out of step.
    /// </remarks>
    private StreamWriter? _servoWriter;

    /// <summary>The preamble is written on the first row, once the Hub has identified itself.</summary>
    /// <remarks>
    /// It cannot be written at <see cref="Start"/>: the firmware and protocol versions come
    /// from telemetry, and no frame has arrived yet. Deferring it by one row is what lets
    /// the header state which Hub produced the file instead of leaving it unknown.
    /// </remarks>
    private bool _servoPreambleWritten;

    public event Action? StatusChanged;

    public bool IsLogging
    {
        get { lock (_gate) { return _writer is not null; } }
    }

    public string? CurrentPath { get; private set; }

    public int RowsWritten
    {
        get { lock (_gate) { return _rowsWritten; } }
    }

    public void Start(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        lock (_gate)
        {
            CloseWriter();

            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var isNew = !File.Exists(path) || new FileInfo(path).Length == 0;

                // UTF-8 without a BOM: v.6 writes none, and a BOM would appear as
                // stray characters in the first column for anything reading the file
                // as plain text.
                _writer = new StreamWriter(path, append: true, new UTF8Encoding(false))
                {
                    AutoFlush = false,
                };
                _lastFlushTimestamp = Stopwatch.GetTimestamp();

                if (isNew)
                {
                    _writer.WriteLine(SessionLogFormat.Header);
                    _writer.Flush();
                }

                OpenServoSidecar(path);

                CurrentPath = path;
                _rowsWritten = 0;
                log.LogInformation("Session log open: {Path}", path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Losing the log is bad; losing control of the reactor because the log
                // could not be opened would be worse.
                _writer = null;
                CurrentPath = null;
                log.LogError(ex, "Could not open session log at {Path}; continuing without it", path);
            }
        }

        StatusChanged?.Invoke();
    }

    public void Stop()
    {
        lock (_gate)
        {
            CloseWriter();
            CurrentPath = null;
        }

        StatusChanged?.Invoke();
    }

    public void Write(SensorSnapshot snapshot, double commandedRpm, string connectionStatus)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var changed = false;

        lock (_gate)
        {
            if (_writer is null)
            {
                return;
            }

            try
            {
                _writer.WriteLine(BuildRow(snapshot, commandedRpm, connectionStatus));
                WriteServoRow(snapshot);
                _rowsWritten++;
                changed = true;
                FlushIfDue();
            }
            catch (IOException ex)
            {
                // A disconnected network drive or a full disk must not take the app
                // down mid-run. Stop logging, keep controlling.
                log.LogError(ex, "Session log write failed; logging stopped");
                CloseWriter();
                changed = true;
            }
        }

        if (changed)
        {
            StatusChanged?.Invoke();
        }
    }

    /// <summary>
    /// Formats one row exactly as v.6 does.
    /// </summary>
    /// <remarks>
    /// Column order and decimal places match
    /// <see cref="SessionLogFormat.Header"/> and
    /// <see cref="SessionLogFormat.Decimals"/>. Pressure is written without a fixed
    /// precision because v.6 emits it raw.
    /// </remarks>
    internal static string BuildRow(SensorSnapshot s, double commandedRpm, string connectionStatus)
    {
        var row = new StringBuilder(160);

        Append(row, s.TimeMinutes, 2);
        Append(row, s.Temperature, 2);
        Append(row, commandedRpm, 3);
        Append(row, s.PHCalibrated, 2);
        Append(row, s.Antifoam, 3);

        // v.6 emits pressure with no precision specifier; kept identical.
        row.Append(s.Pressure.ToString(CultureInfo.InvariantCulture)).Append('\t');

        Append(row, s.OxygenCalibrated, 3);
        Append(row, s.FlowRate, 3);
        Append(row, s.Distance, 2);

        // Not yet computed in Phase 1: OUR arrives with the soft sensor in Phase 2.
        Append(row, SensorReadings.NotReceived, 5);

        Append(row, s.BiomassAbsorbance, 4);
        Append(row, s.PumpVolume, 3);
        Append(row, s.PumpFlow, 3);

        row.Append(connectionStatus);
        return row.ToString();
    }

    private static void Append(StringBuilder row, double value, int decimals)
        => row.Append(value.ToString(
                "F" + decimals.ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture))
              .Append('\t');

    /// <summary>Flushes both files once <see cref="FlushInterval"/> has passed since the last flush.</summary>
    private void FlushIfDue()
    {
        if (Stopwatch.GetElapsedTime(_lastFlushTimestamp) < FlushInterval)
        {
            return;
        }

        _writer?.Flush();
        _servoWriter?.Flush();
        _lastFlushTimestamp = Stopwatch.GetTimestamp();
    }

    private void CloseWriter()
    {
        if (_writer is null)
        {
            return;
        }

        try
        {
            _writer.Flush();
            _writer.Dispose();
            log.LogInformation("Session log closed after {Rows} rows", _rowsWritten);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Error closing the session log");
        }
        finally
        {
            _writer = null;
        }

        CloseServoSidecar();
    }

    /// <summary>Derives the sidecar's path from the main log's.</summary>
    internal static string ServoSidecarPath(string sessionPath)
    {
        var directory = Path.GetDirectoryName(sessionPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(sessionPath);
        return Path.Combine(directory, name + ServoSessionLogFormat.FileSuffix);
    }

    private void OpenServoSidecar(string sessionPath)
    {
        var path = ServoSidecarPath(sessionPath);

        try
        {
            var isNew = !File.Exists(path) || new FileInfo(path).Length == 0;
            _servoWriter = new StreamWriter(path, append: true, new UTF8Encoding(false))
            {
                AutoFlush = false,
            };

            // Appending to a file that already has its preamble must not write a second one.
            _servoPreambleWritten = !isNew;
            if (isNew)
            {
                _servoWriter.WriteLine(ServoSessionLogFormat.Header);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The sidecar is secondary to the main log, which is itself secondary to keeping
            // the reactor under control. Losing it costs the servo history and nothing else.
            _servoWriter = null;
            log.LogWarning(ex, "Could not open the servo sidecar at {Path}; continuing without it", path);
        }
    }

    /// <summary>
    /// Appends one servo row, or a row of blanks when the frame carried no sample.
    /// </summary>
    /// <remarks>
    /// A row is written for <b>every</b> frame, sample or not, so the sidecar and the main
    /// log stay aligned line for line and can be read side by side without matching on
    /// <c>time_min</c>. The blanks are what say "no reading here" - a zero would be a
    /// stopped motor, and nothing later could tell the two apart.
    /// </remarks>
    private void WriteServoRow(SensorSnapshot snapshot)
    {
        if (_servoWriter is null)
        {
            return;
        }

        if (!_servoPreambleWritten)
        {
            _servoWriter.WriteLine(ServoSessionLogFormat.BuildPreamble(
                snapshot.HubFirmwareVersion,
                snapshot.HubProtocolVersion,
                AppVersionText));
            _servoPreambleWritten = true;
        }

        _servoWriter.WriteLine(BuildServoRow(snapshot));
    }

    /// <summary>Formats one sidecar row. Internal so the format can be tested directly.</summary>
    internal static string BuildServoRow(SensorSnapshot s)
    {
        var row = new StringBuilder(120);

        // Time is always present: it comes from the aggregate frame, not from the node.
        row.Append(s.TimeMinutes.ToString("F2", CultureInfo.InvariantCulture)).Append('\t');
        row.Append(s.HasServoTelemetry ? (s.ServoOnline ? "1" : "0") : string.Empty).Append('\t');

        AppendServo(row, s, s.ServoRpm, 1);
        AppendServo(row, s, s.ServoTorquePct, 1);
        AppendServo(row, s, s.ServoTorqueNm, 4);
        AppendServo(row, s, s.ServoLoadPct, 0);
        AppendServo(row, s, s.ServoPowerW, 3);
        AppendServo(row, s, s.ServoEnergyWh, 6);

        AppendServoInt(row, s, s.ServoState);
        AppendServoInt(row, s, s.ServoAlarm);
        AppendServoInt(row, s, s.ServoCommOk);
        AppendServoInt(row, s, s.ServoCommErr, last: true);

        return row.ToString();
    }

    /// <summary>
    /// Writes a servo value, or nothing at all when the frame carried no sample.
    /// </summary>
    /// <remarks>
    /// Gated on <see cref="SensorSnapshot.HasServoSample"/> rather than on the sentinel:
    /// rpm reads a few tenths below zero at rest and torque goes negative under braking, so
    /// a value test would blank real readings.
    /// </remarks>
    private static void AppendServo(StringBuilder row, SensorSnapshot s, double value, int decimals)
    {
        if (s.HasServoSample)
        {
            row.Append(value.ToString(
                "F" + decimals.ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture));
        }

        row.Append('\t');
    }

    private static void AppendServoInt(StringBuilder row, SensorSnapshot s, long value, bool last = false)
    {
        if (s.HasServoSample)
        {
            row.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        if (!last)
        {
            row.Append('\t');
        }
    }

    private void CloseServoSidecar()
    {
        if (_servoWriter is null)
        {
            return;
        }

        try
        {
            _servoWriter.Flush();
            _servoWriter.Dispose();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Error closing the servo sidecar");
        }
        finally
        {
            _servoWriter = null;
            _servoPreambleWritten = false;
        }
    }

    /// <summary>Informational version stamped into the sidecar's header.</summary>
    private static string AppVersionText { get; } =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "Dev";

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }
}
