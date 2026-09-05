using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OpenTECHub.Protocol;

/// <summary>
/// Wi-Fi (HTTP) link settings for the ESP32 SoftAP.
/// </summary>
/// <remarks>See <c>docs/PROTOCOL.md</c> section 1.3.</remarks>
public sealed record HttpTransportConfig
{
    public string IpAddress { get; init; } = "192.168.4.1";

    public int HandshakeAttempts { get; init; } = 3;
    public TimeSpan HandshakeRetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromMilliseconds(750);
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Floor on polling <c>/readData</c>. v.6 applies a 0.98 factor so the poll
    /// stays just inside the device's own emission period.
    /// </summary>
    public TimeSpan MinimumPollPeriod { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// HTTP transport for the ESP32-S3 over its Wi-Fi access point.
/// </summary>
/// <remarks>
/// Port of <c>WiFiTransport</c> in v.6 <c>communication/transport.py</c>.
/// See <c>docs/PROTOCOL.md</c> sections 1.1 and 1.3.
/// </remarks>
public sealed class HttpTransport : ITransport
{
    private const string HandshakePayload = """{"comTest":1}""";
    private const double PollPeriodSafetyFactor = 0.98;

    private readonly HttpTransportConfig _config;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _ioGate = new(1, 1);

    private HttpClient? _client;
    private EntityTagHeaderValue? _etag;
    private long _nextPollTicks;
    private TimeSpan? _pollPeriodOverride;

    public HttpTransport(HttpTransportConfig config, ILogger<HttpTransport>? logger = null)
    {
        _config = config;
        _log = logger ?? NullLogger<HttpTransport>.Instance;
    }

    public TransportMedium Medium => TransportMedium.WiFi;

    public string Endpoint => _config.IpAddress;

    public bool IsConnected => _client is not null;

    private string BaseUrl => $"http://{_config.IpAddress}";

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        await DisconnectAsync().ConfigureAwait(false);

        var client = CreateClient();

        for (var attempt = 1; attempt <= _config.HandshakeAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/command")
                {
                    Content = new StringContent(HandshakePayload, Encoding.UTF8, "application/json"),
                };

                // Forces a fresh TCP handshake instead of reusing a socket the ESP32
                // may have dropped on its way through a reboot.
                request.Headers.ConnectionClose = true;

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_config.ConnectTimeout + _config.ReadTimeout);

                using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
                var body = (await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false)).Trim();

                // The Hub's HTTP /command handler queues the frame and answers 200 "Queued";
                // it does NOT run comTest inline the way the USB path does (that one prints
                // "OK" on the serial line). So a healthy Wi-Fi handshake is any success
                // status, not a specific body. The old body == "OK" check could never pass
                // against this firmware, which left Wi-Fi stuck reconnecting for ever while
                // USB worked. The queued comTest still lands: the command task sets the "OK"
                // sample the next /readData carries.
                if (response.IsSuccessStatusCode)
                {
                    _client = client;
                    _etag = null;
                    _nextPollTicks = 0;
                    _log.LogInformation("Wi-Fi {Ip}: handshake ok ({Status} '{Body}')",
                        _config.IpAddress, (int)response.StatusCode, body);
                    return true;
                }

                _log.LogTrace("Wi-Fi {Ip}: unexpected handshake reply {Status} '{Body}' (attempt {Attempt})",
                    _config.IpAddress, (int)response.StatusCode, body, attempt);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                client.Dispose();
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                _log.LogTrace(ex, "Wi-Fi {Ip}: handshake attempt {Attempt} failed",
                    _config.IpAddress, attempt);
            }

            await Task.Delay(_config.HandshakeRetryDelay, cancellationToken).ConfigureAwait(false);
        }

        client.Dispose();
        _log.LogWarning("Wi-Fi {Ip}: handshake failed after {Attempts} attempts",
            _config.IpAddress, _config.HandshakeAttempts);
        return false;
    }

    public async Task DisconnectAsync()
    {
        await _ioGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _client?.Dispose();
            _client = null;
            _etag = null;
            _nextPollTicks = 0;
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>
    /// Polls <c>/readData</c>, honouring the minimum poll period and the ETag.
    /// </summary>
    /// <remarks>
    /// Returns null both when the poll period has not elapsed and when the device
    /// answers 304 (nothing changed). A transport-level failure throws
    /// <see cref="TransportFaultException"/> instead of returning null - that
    /// distinction is the fix for v.6's silent link loss.
    /// </remarks>
    public async Task<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var now = Environment.TickCount64;
        if (now < _nextPollTicks)
        {
            return null;
        }

        var period = _pollPeriodOverride ?? _config.MinimumPollPeriod;
        _nextPollTicks = now + (long)(period.TotalMilliseconds * PollPeriodSafetyFactor);

        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = _client;
            if (client is null)
            {
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/readData");
            if (_etag is not null)
            {
                request.Headers.IfNoneMatch.Add(_etag);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_config.ConnectTimeout + _config.ReadTimeout);

            using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return null; // expected: telemetry has not changed
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new TransportFaultException(
                    $"Wi-Fi {_config.IpAddress}: /readData returned {(int)response.StatusCode}.");
            }

            if (response.Headers.ETag is { } etag)
            {
                _etag = etag;
            }

            var body = (await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false)).Trim();
            return body.Length == 0 ? null : body;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            throw new TransportFaultException($"Wi-Fi {_config.IpAddress}: read failed.", ex);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task<bool> WriteAsync(string payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = _client;
            if (client is null)
            {
                return false;
            }

            // No trailing newline here - that framing is USB-only.
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/command")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_config.WriteTimeout);

            using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);

            // A queued command answers 200 "Queued"; the firmware never echoes "OK" over
            // HTTP. Success is the 2xx — 4xx/5xx (queue full, bad JSON, too large) are the
            // real failures. Requiring an "OK" body here rejected every command the Hub
            // accepted, so each write looked like a link fault.
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            throw new TransportFaultException($"Wi-Fi {_config.IpAddress}: write failed.", ex);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var client = _client;
        if (client is null)
        {
            return false;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_config.PingTimeout);

            using var response = await client.GetAsync($"{BaseUrl}/ping", timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Overrides the poll period, e.g. to match a changed <c>dataDelay</c>.</summary>
    public void SetPollPeriod(TimeSpan period)
        => _pollPeriodOverride = period < TimeSpan.FromMilliseconds(100)
            ? TimeSpan.FromMilliseconds(100)
            : period;

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _ioGate.Dispose();
    }

    /// <remarks>
    /// A brand-new client per connect, with pooling effectively disabled and the
    /// system proxy bypassed. v.6 does the same, deliberately: after the ESP32
    /// reboots, a pooled socket points at a peer that no longer exists, and the OS
    /// happily hands it back.
    /// </remarks>
    private HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = _config.ConnectTimeout,
            PooledConnectionLifetime = TimeSpan.Zero,
            MaxConnectionsPerServer = 2,
            UseProxy = false,
            AllowAutoRedirect = false,
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            // Per-request linked tokens carry the real deadlines.
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }
}
