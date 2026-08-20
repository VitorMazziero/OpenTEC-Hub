using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using TecnalHub.Protocol;
using TecnalHub.Services.Persistence;

namespace TecnalHub.Services.Telemetry;

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
                    AutoFlush = true,
                };

                if (isNew)
                {
                    _writer.WriteLine(SessionLogFormat.Header);
                }

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
                _rowsWritten++;
                changed = true;
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
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }
}
