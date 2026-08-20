using System.Globalization;
using System.IO;
using System.Text;
using TecnalHub.Protocol;
using TecnalHub.Services.Persistence;

namespace TecnalHub.Services.Telemetry;

/// <summary>Metadata and format evidence for one persisted cultivation session.</summary>
public sealed record SessionFileSummary(
    string Path,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long SizeBytes,
    int RowCount,
    bool HeaderValid,
    double DurationMinutes,
    string ConnectionMedia,
    string FirstRow,
    string LastRow)
{
    public string DateRangeText => CreatedAt.Date == UpdatedAt.Date
        ? $"{CreatedAt:dd/MM/yyyy HH:mm}–{UpdatedAt:HH:mm}"
        : $"{CreatedAt:dd/MM/yyyy HH:mm}–{UpdatedAt:dd/MM/yyyy HH:mm}";

    public string DurationText => double.IsFinite(DurationMinutes)
        ? TimeSpan.FromMinutes(Math.Max(0, DurationMinutes)).ToString(@"hh\:mm\:ss")
        : "—";

    public string SizeText => SizeBytes switch
    {
        >= 1024 * 1024 => $"{SizeBytes / 1024d / 1024d:F1} MB",
        >= 1024 => $"{SizeBytes / 1024d:F1} kB",
        _ => $"{SizeBytes} B",
    };

    public string HeaderStatus => HeaderValid
        ? "Cabeçalho compatível com SessionLogFormat.Header"
        : "Cabeçalho incompatível — não carregar nos gráficos";
}

/// <summary>Parsed session data that the dedicated dual-chart page can display.</summary>
public sealed class SessionFileData(
    SessionFileSummary summary,
    IReadOnlyDictionary<TelemetryChannel, ChannelSeries> series)
{
    public SessionFileSummary Summary { get; } = summary;

    public IReadOnlyDictionary<TelemetryChannel, ChannelSeries> Series { get; } = series;

    public ChannelSeries GetSeries(TelemetryChannel channel, TimeSpan? window, int maxPoints)
    {
        if (!Series.TryGetValue(channel, out var source) || source.Count == 0)
        {
            return ChannelSeries.Empty;
        }

        maxPoints = Math.Max(2, maxPoints);
        var start = 0;
        if (window is { } span)
        {
            var cutoff = source.Minutes[^1] - span.TotalMinutes;
            while (start < source.Count && source.Minutes[start] < cutoff)
            {
                start++;
            }
        }

        var available = source.Count - start;
        if (available <= 0)
        {
            return ChannelSeries.Empty;
        }

        var stride = Math.Max(1, (int)Math.Ceiling(available / (double)maxPoints));
        var length = (available + stride - 1) / stride;
        var minutes = new double[length];
        var values = new double[length];

        for (var i = 0; i < length; i++)
        {
            var sourceIndex = start + (i * stride);
            minutes[i] = source.Minutes[sourceIndex];
            values[i] = source.Values[sourceIndex];
        }

        return new ChannelSeries(minutes, values);
    }
}

public interface ISessionFileService
{
    IReadOnlyList<SessionFileSummary> Discover(AppSettings settings);

    SessionFileData Load(SessionFileSummary summary);

    void ExportCsv(SessionFileSummary summary, string destinationPath);
}

/// <summary>Inspects and converts the frozen v.6-compatible session-log format.</summary>
public sealed class SessionFileService : ISessionFileService
{
    private static readonly IReadOnlyDictionary<TelemetryChannel, int> ChannelColumns =
        new Dictionary<TelemetryChannel, int>
        {
            [TelemetryChannel.Temperature] = 1,
            [TelemetryChannel.MotorRpm] = 2,
            [TelemetryChannel.PH] = 3,
            [TelemetryChannel.Antifoam] = 4,
            [TelemetryChannel.Pressure] = 5,
            [TelemetryChannel.Oxygen] = 6,
            [TelemetryChannel.Flow] = 7,
            [TelemetryChannel.Distance] = 8,
            [TelemetryChannel.Biomass] = 10,
            [TelemetryChannel.PumpVolume] = 11,
            [TelemetryChannel.PumpFlow] = 12,
        };

    public IReadOnlyList<SessionFileSummary> Discover(AppSettings settings)
    {
        var configured = settings.Logging.SessionLogPath;
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(AppPaths.DataDirectory, "sessions"),
        };

        if (!string.IsNullOrWhiteSpace(configured) &&
            Path.GetDirectoryName(configured) is { Length: > 0 } configuredDirectory)
        {
            directories.Add(configuredDirectory);
        }

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var pattern in new[] { "*.txt", "*.tsv", "*.csv" })
            {
                foreach (var path in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
                {
                    candidates.Add(path);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            candidates.Add(configured);
        }

        return candidates
            .Select(Inspect)
            .OrderByDescending(file => file.UpdatedAt)
            .ToArray();
    }

    public SessionFileData Load(SessionFileSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (!summary.HeaderValid)
        {
            throw new InvalidDataException("The session header does not match SessionLogFormat.Header.");
        }

        var minutes = new List<double>();
        var values = ChannelColumns.Keys.ToDictionary(channel => channel, _ => new List<double>());

        using var reader = new StreamReader(summary.Path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        _ = reader.ReadLine();
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var fields = line.Split('\t');
            if (!TryValue(fields, 0, out var minute))
            {
                continue;
            }

            minutes.Add(minute);
            foreach (var (channel, column) in ChannelColumns)
            {
                values[channel].Add(TryValue(fields, column, out var value) && value > SensorReadings.NotReceived
                    ? value
                    : double.NaN);
            }
        }

        var timeArray = minutes.ToArray();
        var series = values.ToDictionary(
            pair => pair.Key,
            pair => new ChannelSeries(timeArray, pair.Value.ToArray()));
        return new SessionFileData(summary, series);
    }

    public void ExportCsv(SessionFileSummary summary, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        using var reader = new StreamReader(summary.Path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        using var writer = new StreamWriter(destinationPath, append: false, new UTF8Encoding(false));

        while (reader.ReadLine() is { } line)
        {
            var fields = line.Split('\t');
            writer.WriteLine(string.Join(',', fields.Select(EscapeCsv)));
        }
    }

    internal static SessionFileSummary Inspect(string path)
    {
        var info = new FileInfo(path);
        var headerValid = false;
        var rowCount = 0;
        var firstRow = "";
        var lastRow = "";
        var firstMinute = double.NaN;
        var lastMinute = double.NaN;
        var media = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            headerValid = string.Equals(reader.ReadLine(), SessionLogFormat.Header, StringComparison.Ordinal);
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                firstRow = rowCount == 0 ? line : firstRow;
                lastRow = line;
                rowCount++;

                var fields = line.Split('\t');
                if (TryValue(fields, 0, out var minute))
                {
                    if (!double.IsFinite(firstMinute))
                    {
                        firstMinute = minute;
                    }

                    lastMinute = minute;
                }

                if (fields.Length > 13 && !string.IsNullOrWhiteSpace(fields[13]))
                {
                    media.Add(fields[13].Trim());
                }
            }
        }
        catch (IOException)
        {
            headerValid = false;
        }

        var created = new DateTimeOffset(info.CreationTime);
        var updated = new DateTimeOffset(info.LastWriteTime);
        var duration = double.IsFinite(firstMinute) && double.IsFinite(lastMinute)
            ? Math.Max(0, lastMinute - firstMinute)
            : double.NaN;

        return new SessionFileSummary(
            path,
            info.Name,
            created,
            updated,
            info.Exists ? info.Length : 0,
            rowCount,
            headerValid,
            duration,
            media.Count == 0 ? "—" : string.Join(" / ", media.Order()),
            firstRow,
            lastRow);
    }

    private static bool TryValue(string[] fields, int index, out double value)
    {
        value = default;
        return index < fields.Length &&
               double.TryParse(fields[index], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string EscapeCsv(string field)
    {
        var escaped = field.Replace("\"", "\"\"");
        return escaped.IndexOfAny([',', '\"', '\r', '\n']) >= 0
            ? $"\"{escaped}\""
            : escaped;
    }
}
