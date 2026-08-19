using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TecnalHub.Protocol;

/// <summary>Settings for the connection lifecycle.</summary>
public sealed record ConnectionOptions
{
    /// <summary>Automatically retry, cycling through the configured media.</summary>
    public bool BackupEnabled { get; init; } = true;

    /// <summary>Pause between reconnect attempts.</summary>
    public TimeSpan BackupDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Liveness probe period while connected.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How often the device is polled for telemetry.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How long USB may stay silent before the link is presumed lost.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Must be comfortably larger than the device's telemetry period (<c>dataDelay</c>,
    /// 2000 ms in the field). The default allows four missed frames.
    /// </para>
    /// <para>
    /// <b>Expressed as a duration on purpose.</b> v.6 counted consecutive empty reads
    /// instead, which silently couples the detector to the poll rate: polling faster
    /// makes the link look dead sooner. Bench testing against a real ESP32-S3 showed
    /// a 250 ms poll with a count of 4 declaring loss after 1 s, against a device
    /// emitting every 2 s - a guaranteed false positive, and on USB every reconnect
    /// re-pulses DTR/RTS and therefore reboots the board.
    /// </para>
    /// </remarks>
    public TimeSpan UsbSilenceTimeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>Consecutive malformed frames before the link is presumed lost.</summary>
    public int ParseFailuresBeforeLinkLost { get; init; } = 3;

    /// <summary>Same-port failures tolerated before re-probing for a moved device.</summary>
    public int FailuresBeforeReprobe { get; init; } = 3;
}

/// <summary>
/// Owns the link: drives connection state, polls telemetry, and flushes commands.
/// </summary>
/// <remarks>
/// <para>
/// Port of v.6's <c>ConnectionManager</c>, whose request-queue and state-machine
/// design is sound and is kept. What changes: Qt signals become .NET events, the
/// worker thread becomes an async loop, the alarm and file-logging concerns that had
/// accumulated here are gone, and cancellation is threaded throughout.
/// </para>
/// <para>
/// <b>This is the only type permitted to touch an <see cref="ITransport"/>.</b>
/// Callers buffer commands with <see cref="SendCommand"/> and observe events.
/// See <c>docs/ARCHITECTURE.md</c> section 2.
/// </para>
/// </remarks>
public sealed class ConnectionManager : IAsyncDisposable
{
    private readonly ConnectionOptions _options;
    private readonly ILogger _log;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly TelemetryParser _parser;

    private readonly Channel<Request> _requests =
        Channel.CreateUnbounded<Request>(new UnboundedChannelOptions { SingleReader = true });

    private readonly SemaphoreSlim _transportGate = new(1, 1);
    private readonly Lock _bufferLock = new();
    private readonly Lock _stateLock = new();

    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;

    private ITransport? _transport;
    private TecnalCommand _pending = TecnalCommand.Create();

    private SerialTransportConfig? _serialConfig;
    private HttpTransportConfig? _httpConfig;
    private int _backupIndex = -1;

    private ConnectionState _state = ConnectionState.Disconnected;
    private TransportMedium? _medium;
    private string _endpoint = "";

    private long _lastTrafficTicks;
    private int _parseFailureStreak;
    private int _samePortFailures;

    // Diagnostics counters.
    private int _framesReceived;
    private int _commandsSent;
    private int _parseFailures;
    private int _deviceLogLines;
    private int _commandAcks;
    private int _attemptsUsb;
    private int _attemptsWiFi;
    private double? _lastRoundTripMs;
    private string _lastError = "";
    private DateTimeOffset? _lastFrameAt;

    public ConnectionManager(
        ConnectionOptions? options = null,
        ParserConfig? parserConfig = null,
        ILoggerFactory? loggerFactory = null)
    {
        _options = options ?? new ConnectionOptions();
        _loggerFactory = loggerFactory;
        _log = loggerFactory?.CreateLogger<ConnectionManager>() ?? NullLogger<ConnectionManager>.Instance;

        _parser = new TelemetryParser(parserConfig);
        _parser.PHCalibrationChanged += OnPHCalibrationChanged;

        _worker = Task.Run(() => WorkerLoopAsync(_shutdown.Token));
    }

    /// <summary>Raised on every state transition.</summary>
    public event Action<ConnectionStateChange>? StateChanged;

    /// <summary>Raised for each telemetry frame successfully parsed.</summary>
    public event Action<SensorSnapshot>? TelemetryReceived;

    /// <summary>Raised for log lines the device emits (prefixed <c>[ESP32_</c>).</summary>
    public event Action<string>? DeviceLogReceived;

    /// <summary>Current state. Safe to read from any thread.</summary>
    public ConnectionState State
    {
        get { lock (_stateLock) { return _state; } }
    }

    /// <summary>Live readings. Prefer the snapshot delivered by <see cref="TelemetryReceived"/>.</summary>
    public SensorReadings Readings => _parser.Readings;

    /// <summary>Counters for the connection popover.</summary>
    public LinkDiagnostics Diagnostics => new()
    {
        FramesReceived = Volatile.Read(ref _framesReceived),
        CommandsSent = Volatile.Read(ref _commandsSent),
        ParseFailures = Volatile.Read(ref _parseFailures),
        DeviceLogLines = Volatile.Read(ref _deviceLogLines),
        CommandAcks = Volatile.Read(ref _commandAcks),
        ConnectAttemptsUsb = Volatile.Read(ref _attemptsUsb),
        ConnectAttemptsWiFi = Volatile.Read(ref _attemptsWiFi),
        LastRoundTripMs = _lastRoundTripMs,
        LastError = _lastError,
        LastFrameAt = _lastFrameAt,
    };

    // ==================================================================
    // Public API
    // ==================================================================

    /// <summary>Connects over USB, remembering the settings for auto-reconnect.</summary>
    public void ConnectUsb(SerialTransportConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _serialConfig = config;
        Post(new ConnectRequest(TransportMedium.Usb));
    }

    /// <summary>Connects over Wi-Fi, remembering the settings for auto-reconnect.</summary>
    public void ConnectWiFi(HttpTransportConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _httpConfig = config;
        Post(new ConnectRequest(TransportMedium.WiFi));
    }

    /// <summary>Disconnects and cancels any retry loop.</summary>
    public void Disconnect() => Post(new DisconnectRequest());

    /// <summary>
    /// Merges <paramref name="command"/> into the outgoing buffer and wakes the worker.
    /// </summary>
    /// <remarks>
    /// Buffering rather than sending directly is what lets several setpoint changes
    /// in one UI gesture leave as a single frame - which matters on the shared UART.
    /// Safe to call from any thread.
    /// </remarks>
    public void SendCommand(TecnalCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.IsEmpty)
        {
            return;
        }

        lock (_bufferLock)
        {
            _pending.Merge(command);
        }

        Post(new FlushRequest());
    }

    /// <summary>Applies new calibration and filter tuning.</summary>
    public void Reconfigure(ParserConfig parserConfig) => _parser.Reconfigure(parserConfig);

    // ==================================================================
    // Worker
    // ==================================================================

    private async Task WorkerLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                // Wake on a request, or on the poll tick, whichever comes first.
                var request = await WaitForRequestAsync(_options.PollInterval, token).ConfigureAwait(false);

                if (request is not null)
                {
                    await HandleRequestAsync(request, token).ConfigureAwait(false);
                }

                if (State == ConnectionState.Connected)
                {
                    await FlushCommandsAsync(token).ConfigureAwait(false);
                    await PollTelemetryAsync(token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Connection worker terminated unexpectedly");
        }
        finally
        {
            await TeardownTransportAsync().ConfigureAwait(false);
        }
    }

    private async Task<Request?> WaitForRequestAsync(TimeSpan timeout, CancellationToken token)
    {
        if (_requests.Reader.TryRead(out var immediate))
        {
            return immediate;
        }

        using var delay = CancellationTokenSource.CreateLinkedTokenSource(token);
        delay.CancelAfter(timeout);

        try
        {
            return await _requests.Reader.ReadAsync(delay.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return null; // poll tick
        }
    }

    private async Task HandleRequestAsync(Request request, CancellationToken token)
    {
        switch (request)
        {
            case ConnectRequest connect:
                await HandleConnectAsync(connect.Medium, token).ConfigureAwait(false);
                break;

            case DisconnectRequest:
                await HandleDisconnectAsync().ConfigureAwait(false);
                break;

            case FlushRequest when State == ConnectionState.Connected:
                await FlushCommandsAsync(token).ConfigureAwait(false);
                break;

            case LinkLostRequest lost when State == ConnectionState.Connected:
                await HandleLinkLostAsync(lost.Reason, token).ConfigureAwait(false);
                break;

            default:
                break;
        }
    }

    private async Task HandleConnectAsync(TransportMedium medium, CancellationToken token)
    {
        await TeardownTransportAsync().ConfigureAwait(false);

        CountAttempt(medium);
        var transport = BuildTransport(medium);
        if (transport is null)
        {
            _log.LogWarning("{Medium}: no configuration available", medium);
            await OnConnectFailedAsync(medium, "sem configuração", token).ConfigureAwait(false);
            return;
        }

        Transition(ConnectionState.Connecting, medium, transport.Endpoint);

        bool connected;
        try
        {
            connected = await transport.ConnectAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _log.LogWarning(ex, "{Medium}: connect threw", medium);
            connected = false;
        }

        if (connected)
        {
            await _transportGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                _transport = transport;
            }
            finally
            {
                _transportGate.Release();
            }

            ResetLinkCounters();
            Transition(ConnectionState.Connected, medium, transport.Endpoint);
            return;
        }

        await transport.DisposeAsync().ConfigureAwait(false);
        await OnConnectFailedAsync(medium, "handshake falhou", token).ConfigureAwait(false);
    }

    private async Task HandleDisconnectAsync()
    {
        await TeardownTransportAsync().ConfigureAwait(false);
        Transition(ConnectionState.Disconnected, _medium, _endpoint, "desconectado pelo usuário");
    }

    private async Task HandleLinkLostAsync(string reason, CancellationToken token)
    {
        _log.LogWarning("{Medium}: link lost ({Reason})", _medium, reason);
        _lastError = reason;
        await TeardownTransportAsync().ConfigureAwait(false);
        await EnterRecoveryAsync(reason, token).ConfigureAwait(false);
    }

    private Task OnConnectFailedAsync(TransportMedium medium, string reason, CancellationToken token)
    {
        _lastError = reason;
        if (medium == TransportMedium.Usb)
        {
            _samePortFailures++;
        }

        return EnterRecoveryAsync(reason, token);
    }

    private async Task EnterRecoveryAsync(string reason, CancellationToken token)
    {
        if (!_options.BackupEnabled)
        {
            Transition(ConnectionState.Faulted, _medium, _endpoint, reason);
            return;
        }

        Transition(ConnectionState.Reconnecting, _medium, _endpoint, reason);
        await RunReconnectCycleAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    /// Retry loop: cycles the configured media until one answers, the user
    /// intervenes, or shutdown.
    /// </summary>
    private async Task RunReconnectCycleAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && State == ConnectionState.Reconnecting)
        {
            // A user request outranks the retry loop.
            while (_requests.Reader.TryRead(out var pending))
            {
                switch (pending)
                {
                    case DisconnectRequest:
                        await HandleDisconnectAsync().ConfigureAwait(false);
                        return;

                    case ConnectRequest connect:
                        await HandleConnectAsync(connect.Medium, token).ConfigureAwait(false);
                        return;

                    default:
                        break;
                }
            }

            if (NextMedium() is not { } medium)
            {
                await Task.Delay(_options.BackupDelay, token).ConfigureAwait(false);
                continue;
            }

            CountAttempt(medium);
            var transport = BuildTransport(medium);

            if (transport is not null)
            {
                var connected = false;
                try
                {
                    connected = await transport.ConnectAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await transport.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                }

                if (connected)
                {
                    await _transportGate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        _transport = transport;
                    }
                    finally
                    {
                        _transportGate.Release();
                    }

                    ResetLinkCounters();
                    Transition(ConnectionState.Connected, medium, transport.Endpoint, "reconectado");
                    return;
                }

                await transport.DisposeAsync().ConfigureAwait(false);

                if (medium == TransportMedium.Usb)
                {
                    _samePortFailures++;
                    await TryReprobeUsbAsync(token).ConfigureAwait(false);
                }
            }

            await Task.Delay(_options.BackupDelay, token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// After repeated failures on the remembered port, look for the device elsewhere -
    /// it may have enumerated on a different COM number.
    /// </summary>
    private async Task TryReprobeUsbAsync(CancellationToken token)
    {
        if (_serialConfig is not { } config || _samePortFailures < _options.FailuresBeforeReprobe)
        {
            return;
        }

        _log.LogInformation("USB: re-probing after {Failures} failures on {Port}",
            _samePortFailures, config.PortName);

        var found = await SerialTransport
            .ProbePortsAsync(config, preferredPort: null, _loggerFactory, token)
            .ConfigureAwait(false);

        if (found is not null && !string.Equals(found, config.PortName, StringComparison.OrdinalIgnoreCase))
        {
            _log.LogInformation("USB: device moved {Old} -> {New}", config.PortName, found);
            _serialConfig = config with { PortName = found };
            _samePortFailures = 0;
        }
    }

    // ==================================================================
    // Telemetry and commands
    // ==================================================================

    private async Task PollTelemetryAsync(CancellationToken token)
    {
        var transport = await GetTransportAsync(token).ConfigureAwait(false);
        if (transport is null)
        {
            return;
        }

        string? line;
        try
        {
            line = await transport.ReadAsync(token).ConfigureAwait(false);
        }
        catch (TransportFaultException ex)
        {
            _lastError = ex.Message;
            Post(new LinkLostRequest(ex.Message));
            return;
        }

        if (string.IsNullOrEmpty(line))
        {
            // Wi-Fi returns null on every 304 and between poll ticks, so only USB
            // silence is evidence of a dead link.
            if (transport.Medium == TransportMedium.Usb &&
                Environment.TickCount64 - _lastTrafficTicks > (long)_options.UsbSilenceTimeout.TotalMilliseconds)
            {
                Post(new LinkLostRequest(FormattableString.Invariant(
                    $"sem dados USB por {_options.UsbSilenceTimeout.TotalSeconds:F0}s")));
            }

            return;
        }

        // Any line at all proves the link is alive - including a device log line,
        // which is not telemetry but is certainly traffic.
        _lastTrafficTicks = Environment.TickCount64;

        switch (_parser.Parse(line))
        {
            case ParseOutcome.Updated:
                _parseFailureStreak = 0;
                Interlocked.Increment(ref _framesReceived);
                _lastFrameAt = DateTimeOffset.Now;
                TelemetryReceived?.Invoke(_parser.Readings.Snapshot());
                break;

            case ParseOutcome.DeviceLog:
                Interlocked.Increment(ref _deviceLogLines);
                DeviceLogReceived?.Invoke(line);
                break;

            case ParseOutcome.CommandAck:
                // Expected traffic; already counted as activity above.
                _parseFailureStreak = 0;
                Interlocked.Increment(ref _commandAcks);
                break;

            case ParseOutcome.Malformed:
                Interlocked.Increment(ref _parseFailures);
                if (transport.Medium == TransportMedium.Usb &&
                    ++_parseFailureStreak >= _options.ParseFailuresBeforeLinkLost)
                {
                    Post(new LinkLostRequest("falhas consecutivas de parse"));
                }

                break;

            case ParseOutcome.Empty:
            default:
                break;
        }
    }

    private async Task FlushCommandsAsync(CancellationToken token)
    {
        TecnalCommand payload;
        lock (_bufferLock)
        {
            if (_pending.IsEmpty)
            {
                return;
            }

            payload = _pending;
            _pending = TecnalCommand.Create();
        }

        var transport = await GetTransportAsync(token).ConfigureAwait(false);
        if (transport is null)
        {
            Requeue(payload);
            return;
        }

        var json = payload.ToJson();
        var stopwatch = Stopwatch.StartNew();

        bool sent;
        try
        {
            sent = await transport.WriteAsync(json, token).ConfigureAwait(false);
        }
        catch (TransportFaultException ex)
        {
            _lastError = ex.Message;
            Requeue(payload);
            Post(new LinkLostRequest(ex.Message));
            return;
        }

        if (!sent)
        {
            Requeue(payload);
            Post(new LinkLostRequest("falha ao enviar comando"));
            return;
        }

        _lastRoundTripMs = stopwatch.Elapsed.TotalMilliseconds;
        Interlocked.Increment(ref _commandsSent);
        _log.LogDebug("TX {Payload}", json);

        // Record that the pH echo actually went out, so it is not resent every frame.
        if (payload.GetRawValue(CommandKeys.PHCal) is { } raw &&
            double.TryParse(raw.Trim('"'), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var ph))
        {
            _parser.MarkPHSent(ph);
        }
    }

    /// <summary>
    /// Puts an unsent payload back at the front of the buffer.
    /// </summary>
    /// <remarks>
    /// Anything buffered while the send was in flight is newer and must win on key
    /// conflicts, so the failed payload is merged <i>under</i> it - matching v.6's
    /// <c>payload | replay</c>.
    /// </remarks>
    private void Requeue(TecnalCommand payload)
    {
        lock (_bufferLock)
        {
            _pending = payload.Merge(_pending);
        }
    }

    private void OnPHCalibrationChanged(double calibratedPH)
        => SendCommand(TecnalCommand.Create().SetFixedString(CommandKeys.PHCal, calibratedPH, 2));

    // ==================================================================
    // Plumbing
    // ==================================================================

    private void Post(Request request) => _requests.Writer.TryWrite(request);

    private ITransport? BuildTransport(TransportMedium medium) => medium switch
    {
        TransportMedium.Usb when _serialConfig is { } cfg =>
            new SerialTransport(cfg, _loggerFactory?.CreateLogger<SerialTransport>()),
        TransportMedium.WiFi when _httpConfig is { } cfg =>
            new HttpTransport(cfg, _loggerFactory?.CreateLogger<HttpTransport>()),
        _ => null,
    };

    private async Task<ITransport?> GetTransportAsync(CancellationToken token)
    {
        await _transportGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return _transport;
        }
        finally
        {
            _transportGate.Release();
        }
    }

    private async Task TeardownTransportAsync()
    {
        await _transportGate.WaitAsync().ConfigureAwait(false);
        ITransport? transport;
        try
        {
            transport = _transport;
            _transport = null;
        }
        finally
        {
            _transportGate.Release();
        }

        if (transport is not null)
        {
            try
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Transport teardown threw");
            }
        }
    }

    private TransportMedium? NextMedium()
    {
        var available = new List<TransportMedium>(2);
        if (_httpConfig is not null)
        {
            available.Add(TransportMedium.WiFi);
        }

        if (_serialConfig is not null)
        {
            available.Add(TransportMedium.Usb);
        }

        if (available.Count == 0)
        {
            return null;
        }

        _backupIndex = (_backupIndex + 1) % available.Count;
        return available[_backupIndex];
    }

    private void CountAttempt(TransportMedium medium)
    {
        if (medium == TransportMedium.Usb)
        {
            Interlocked.Increment(ref _attemptsUsb);
        }
        else
        {
            Interlocked.Increment(ref _attemptsWiFi);
        }
    }

    private void ResetLinkCounters()
    {
        // Seed the silence clock at connect time; without this the first poll after
        // a slow handshake could look like a link that has been quiet for ages.
        _lastTrafficTicks = Environment.TickCount64;
        _parseFailureStreak = 0;
        _samePortFailures = 0;
        _lastError = "";
    }

    private void Transition(ConnectionState state, TransportMedium? medium, string endpoint, string reason = "")
    {
        lock (_stateLock)
        {
            if (_state == state && _medium == medium && _endpoint == endpoint)
            {
                return;
            }

            _state = state;
            _medium = medium;
            _endpoint = endpoint;
        }

        _log.LogInformation("State -> {State} [{Medium} {Endpoint}] {Reason}",
            state, medium, endpoint, reason);

        StateChanged?.Invoke(new ConnectionStateChange(state, medium, endpoint, reason));
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);

        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        await TeardownTransportAsync().ConfigureAwait(false);

        _parser.PHCalibrationChanged -= OnPHCalibrationChanged;
        _shutdown.Dispose();
        _transportGate.Dispose();
    }

    // ==================================================================
    // Requests posted to the worker
    // ==================================================================

    private abstract record Request;

    private sealed record ConnectRequest(TransportMedium Medium) : Request;

    private sealed record DisconnectRequest : Request;

    private sealed record FlushRequest : Request;

    private sealed record LinkLostRequest(string Reason) : Request;
}
