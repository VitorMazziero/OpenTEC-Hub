using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OpenTECHub.Protocol;

/// <summary>One cached <c>/diag</c> response proxied by Hub 10.2.</summary>
public sealed record HubNodeDiag(
    string Device,
    int Code,
    TimeSpan? Age,
    int? Rssi,
    long? FreeHeap,
    long? UptimeS,
    int? HubFailStreak,
    bool? Ota,
    IReadOnlyDictionary<string, string> Extra);

/// <summary>A complete HTTP response, or one serial <c>NodeDiag</c> entry.</summary>
public sealed record HubNodeDiagDirectory(long? HubTimeMs, IReadOnlyList<HubNodeDiag> Nodes)
{
    public HubNodeDiag? Find(string device)
        => Nodes.FirstOrDefault(n => string.Equals(n.Device, device, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Reads and parses the Hub 10.2 diagnostics cache without affecting telemetry.</summary>
public sealed class HubNodeDiagClient : IDisposable
{
    private static readonly HashSet<string> CommonKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "device", "version", "uptime_s", "free_heap", "wifi_status", "ssid", "rssi",
        "ip", "mac", "hub_fail_streak", "ota"
    };

    private readonly HttpClient _client;
    private readonly ILogger _log;

    public HubNodeDiagClient(TimeSpan? timeout = null, ILogger<HubNodeDiagClient>? logger = null)
    {
        _client = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(1) };
        _log = logger ?? NullLogger<HubNodeDiagClient>.Instance;
    }

    public async Task<HubNodeDiagDirectory?> FetchAsync(string hubIp, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(hubIp))
        {
            return null;
        }
        try
        {
            using var response = await _client.GetAsync($"http://{hubIp}/nodeDiag", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            return Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            _log.LogDebug(ex, "GET /nodeDiag on {Hub} failed", hubIp);
            return null;
        }
    }

    /// <summary>Accepts the HTTP document and the serial <c>{"NodeDiag":{...}}</c> envelope.</summary>
    public static HubNodeDiagDirectory? Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("NodeDiag", out var serialEntry))
            {
                var parsed = ParseEntry(serialEntry);
                return parsed is null ? null : new HubNodeDiagDirectory(null, [parsed]);
            }

            if (!root.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var parsedNodes = new List<HubNodeDiag>();
            foreach (var entry in nodes.EnumerateArray())
            {
                if (ParseEntry(entry) is { } parsed)
                {
                    parsedNodes.Add(parsed);
                }
            }

            long? hubTime = TryInt64(root, "hub_time_ms");
            return new HubNodeDiagDirectory(hubTime, parsedNodes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static HubNodeDiag? ParseEntry(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object ||
            !entry.TryGetProperty("dev", out var dev) || dev.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var ageMs = TryInt64(entry, "age_ms");
        TimeSpan? age = ageMs is null or >= HubNodeEntry.NeverSeenAgeMs
            ? null
            : TimeSpan.FromMilliseconds(Math.Max(0, ageMs.Value));
        var code = (int)(TryInt64(entry, "code") ?? 0);
        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        int? rssi = null;
        long? freeHeap = null;
        long? uptime = null;
        int? failStreak = null;
        bool? ota = null;
        if (entry.TryGetProperty("diag", out var diag) && diag.ValueKind == JsonValueKind.Object)
        {
            rssi = TryInt32(diag, "rssi");
            freeHeap = TryInt64(diag, "free_heap");
            uptime = TryInt64(diag, "uptime_s");
            failStreak = TryInt32(diag, "hub_fail_streak");
            ota = TryBool(diag, "ota");
            foreach (var property in diag.EnumerateObject())
            {
                if (!CommonKeys.Contains(property.Name))
                {
                    extra[property.Name] = PrimitiveText(property.Value);
                }
            }
        }

        return new HubNodeDiag(dev.GetString()!, code, age, rssi, freeHeap, uptime, failStreak, ota, extra);
    }

    private static int? TryInt32(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : null;

    private static long? TryInt64(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed) ? parsed : null;

    private static bool? TryBool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static string PrimitiveText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
        JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
        JsonValueKind.Null => "",
        _ => value.GetRawText(),
    };

    public void Dispose() => _client.Dispose();
}
