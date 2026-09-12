using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OpenTECHub.Protocol;

/// <summary>One row of the Hub's node directory (<c>GET /nodes</c>).</summary>
/// <param name="Device">Wire name: <c>distance</c>, <c>agitator</c>, <c>pump</c>, <c>flowmeter</c>, <c>biomass</c>.</param>
/// <param name="Identity">IP, MAC and firmware as the Hub registered them (nulls for unknown).</param>
/// <param name="Online">The Hub considers the node present, by the same window the aggregate frame uses.</param>
/// <param name="Registered">The node has sent a <c>/nodeHello</c>. Null against a Hub 10.0 that does not say.</param>
/// <param name="LastHelloMs">Hub <c>millis()</c> of the last hello, 0 = never. Null on Hub 10.0.</param>
/// <param name="LastDataMs">Hub <c>millis()</c> of the last data push, 0 = never. Null on Hub 10.0.</param>
/// <param name="AgeMs">The Hub's own freshness figure; 999999 is its "never" sentinel.</param>
public sealed record HubNodeEntry(
    string Device,
    ExternalNodeIdentity Identity,
    bool Online,
    bool? Registered,
    long? LastHelloMs,
    long? LastDataMs,
    long AgeMs)
{
    /// <summary>The Hub's "never seen" freshness sentinel.</summary>
    public const long NeverSeenAgeMs = 999999;

    /// <summary>Time since the Hub last heard from the node, or null when it never has.</summary>
    /// <remarks>
    /// Computed from the raw timestamps against <paramref name="hubTimeMs"/> when the Hub
    /// publishes them (10.1), so a clock skew between two polls cannot age a node twice;
    /// falls back to the Hub's <see cref="AgeMs"/> on 10.0.
    /// </remarks>
    public TimeSpan? SinceLastSeen(long? hubTimeMs)
    {
        if (hubTimeMs is { } now && (LastHelloMs is not null || LastDataMs is not null))
        {
            var last = Math.Max(LastHelloMs ?? 0, LastDataMs ?? 0);
            return last <= 0 ? null : TimeSpan.FromMilliseconds(Math.Max(0, now - last));
        }

        return AgeMs >= NeverSeenAgeMs ? null : TimeSpan.FromMilliseconds(AgeMs);
    }
}

/// <summary>The Hub's node directory as one document.</summary>
/// <param name="HubTimeMs">Hub <c>millis()</c> when the answer was built; null on Hub 10.0.</param>
public sealed record HubNodeDirectory(long? HubTimeMs, IReadOnlyList<HubNodeEntry> Nodes)
{
    public HubNodeEntry? Find(string device)
        => Nodes.FirstOrDefault(n => string.Equals(n.Device, device, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Reads <c>GET /nodes</c> from the Hub: the registry of external nodes with their addresses.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not part of <see cref="ITransport"/>.</b> The transport moves telemetry
/// and commands and has exactly one owner, <c>ConnectionManager</c>; this is diagnostics,
/// polled rarely, and must never queue behind - or hold up - a telemetry read. It keeps its
/// own <see cref="HttpClient"/> and a short timeout.
/// </para>
/// <para>
/// Only meaningful when the PC is on the Hub's Wi-Fi: over USB the app still learns each
/// node's identity from the aggregate frame, but cannot reach <c>/nodes</c>. Every failure
/// returns null rather than throwing - a missing directory is not a link fault.
/// </para>
/// </remarks>
public sealed class HubNodeDirectoryClient : IDisposable
{
    private readonly HttpClient _client;
    private readonly ILogger _log;

    public HubNodeDirectoryClient(TimeSpan? timeout = null, ILogger<HubNodeDirectoryClient>? logger = null)
    {
        _client = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(1) };
        _log = logger ?? NullLogger<HubNodeDirectoryClient>.Instance;
    }

    /// <summary>Fetches and parses the directory, or null when the Hub did not answer usefully.</summary>
    public async Task<HubNodeDirectory?> FetchAsync(string hubIp, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(hubIp))
        {
            return null;
        }

        try
        {
            using var response = await _client
                .GetAsync($"http://{hubIp}/nodes", cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Parse(body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            _log.LogDebug(ex, "GET /nodes on {Hub} failed", hubIp);
            return null;
        }
    }

    /// <summary>Parses one <c>/nodes</c> body. Tolerates Hub 10.0 (no registration fields) and returns null for anything that is not the directory.</summary>
    public static HubNodeDirectory? Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("nodes", out var nodes) ||
                nodes.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            long? hubTime = root.TryGetProperty("hub_time_ms", out var t) && t.TryGetInt64(out var tv) ? tv : null;
            var list = new List<HubNodeEntry>();
            foreach (var n in nodes.EnumerateArray())
            {
                if (n.ValueKind != JsonValueKind.Object || !n.TryGetProperty("dev", out var dev) ||
                    dev.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                list.Add(new HubNodeEntry(
                    dev.GetString()!,
                    new ExternalNodeIdentity(
                        ExternalNodeIdentity.Normalize(Str(n, "ip")),
                        ExternalNodeIdentity.Normalize(Str(n, "mac")),
                        ExternalNodeIdentity.Normalize(Str(n, "version"))),
                    Bool(n, "online") ?? false,
                    Bool(n, "registered"),
                    Long(n, "last_hello_ms"),
                    Long(n, "last_data_ms"),
                    Long(n, "age_ms") ?? HubNodeEntry.NeverSeenAgeMs));
            }

            return new HubNodeDirectory(hubTime, list);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static long? Long(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;

    public void Dispose() => _client.Dispose();
}
