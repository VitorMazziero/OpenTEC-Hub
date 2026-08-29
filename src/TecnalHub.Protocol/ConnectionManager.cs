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

    /// <summary>
    /// How long telemetry may be silent before an active liveness probe starts.
    /// </summary>
    /// <remarks>
    /// While frames arrive normally no probe is sent at all, so a healthy link costs
    /// nothing extra. Once telemetry goes quiet for this long the transport is
    /// pinged on this cadence, which detects a dead link well before
    /// <see cref="TelemetrySilenceTimeout"/> would.
    /// </remarks>
    public TimeSpan LivenessProbeAfterSilence { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>How often the device is polled for telemetry.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How long telemetry may be silent before the link is presumed lost.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Must be comfortably larger than the device's telemetry period (<c>dataDelay</c>,
    /// 2000 ms in the field). The default allows four missed frames. If
    /// <c>dataDelay</c> is raised, raise this with it - the relationship is not
    /// enforced automatically.
    /// </para>
    /// <para>
    /// <b>Applies to both transports.</b> An earlier version checked USB only, on the
    /// reasoning that Wi-Fi returns null for every 304 and so cannot distinguish
    /// silence from "unchanged". That was wrong, and dangerously so: if the ESP32's
    /// web server stays up while telemetry stalls, it answers 304 forever and the app
    /// would show frozen readings behind a healthy "Connected" indicator, with no
    /// timeout to catch it. Stale data presented as live is worse than an honest
    /// disconnect.
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
    public TimeSpan TelemetrySilenceTimeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>Consecutive malformed frames before the link is presumed lost.</summary>
    public int ParseFailuresBeforeLinkLost { get; init; } = 3;

    /// <summary>Same-port failures tolerated before re-probing for a moved device.</summary>
    public int FailuresBeforeReprobe { get; init; } = 3;

    /// <summary>
    /// Consecutive USB handshake failures before escalating to a hardware reset.
    /// </summary>
    /// <remarks>
    /// Connects normally leave the board running, which preserves its process state.
    /// That cannot recover a hung firmware, though - only pulsing DTR/RTS can. So
    /// after this many failures one attempt is made with the reset pulse enabled,
    /// accepting the loss of device state as the price of getting the link back.
    /// </remarks>
    public int FailuresBeforeHardwareReset { get; init; } = 2;
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
    private readonly Func<TransportMedium, ITransport?>? _transportFactory;

    private readonly Channel<Request> _requests =
        Channel.CreateUnbounded<Request>(new UnboundedChannelOptions { SingleReader = true });

    private readonly SemaphoreSlim _transportGate = new(1, 1);
    private readonly Lock _bufferLock = new();
    private readonly Lock _stateLock = new();

    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;

    private ITransport? _transport;
    private TecnalCommand _pending = TecnalCommand.Create();

    /// <summary>
    /// Frames that must leave on their own, after whatever is currently buffered.
    /// </summary>
    /// <remarks>
    /// Merging is normally what we want - one UI gesture, one frame on the shared UART.
    /// It is wrong when the firmware's own parse order makes two keys interact: the Hub
    /// drops an external device's sub-commands once that device's routing flag is clear,
    /// so <c>{"mode":0,"pumpComm":0}</c> stops nothing. Those pairs have to arrive as two
    /// frames, in order, and this is the queue that guarantees it.
    /// </remarks>
    private readonly List<TecnalCommand> _frames = [];

    private SerialTransportConfig? _serialConfig;
    private HttpTransportConfig? _httpConfig;
    private int _backupIndex = -1;

    private ConnectionState _state = ConnectionState.Disconnected;
    private TransportMedium? _medium;
    private string _endpoint = "";

    private long _lastTelemetryTicks;
    private long _lastProbeTicks;
    private int _parseFailureStreak;
    private int _samePortFailures;

    // Diagnostics counters.
    private int _framesReceived;
    private int _commandsSent;
    private int _parseFailures;
    private int _deviceLogLines;
    private int _commandAcks;
    private int _livenessProbes;
    private int _attemptsUsb;
    private int _attemptsWiFi;
    private double? _lastWriteMs;
    private string _lastError = "";
    private DateTimeOffset? _lastFrameAt;

    /// <param name="options">Lifecycle tuning.</param>
    /// <param name="parserConfig">Calibration and filter tuning.</param>
    /// <param name="loggerFactory">Optional logging.</param>
    /// <param name="transportFactory">
    /// Overrides how transports are created. Exists so the state machine can be
    /// tested against a fake link - reconnect cycling, silence detection and command
    /// requeue are impossible to exercise reliably against real hardware. Null uses
    /// the real serial and HTTP transports.
    /// </param>
    public ConnectionManager(
        ConnectionOptions? options = null,
        ParserConfig? parserConfig = null,
        ILoggerFactory? loggerFactory = null,
        Func<TransportMedium, ITransport?>? transportFactory = null)
    {
        _options = options ?? new ConnectionOptions();
        _loggerFactory = loggerFactory;
        _transportFactory = transportFactory;
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

    /// <summary>
    /// Raised only after a command frame was successfully written, carrying the exact
    /// merged JSON sent by the transport.
    /// </summary>
    public event Action<string>? CommandSent;

    /// <summary>
    /// Raised after the operator zeroes the session clock, carrying the new offset in
    /// minutes. Purely a local display/log rebase — the device clock is untouched.
    /// </summary>
    public event Action<double>? SessionTimeZeroed;

    /// <summary>Current state. Safe to read from any thread.</summary>
    public ConnectionState State
    {
        get { lock (_stateLock) { return _state; } }
    }

    /// <summary>
    /// The live mutable readings, kept <b>internal</b> so no caller outside the protocol can mutate
    /// the parser's state. Consumers receive the immutable <see cref="SensorSnapshot"/> delivered by
    /// <see cref="TelemetryReceived"/>, or ask for one on demand via <see cref="Snapshot"/>.
    /// </summary>
    internal SensorReadings Readings => _parser.Readings;

    /// <summary>An immutable copy of the current readings, safe to hand to another thread.</summary>
    public SensorSnapshot Snapshot() => _parser.Readings.Snapshot();

    /// <summary>Counters for the connection popover.</summary>
    public LinkDiagnostics Diagnostics => new()
    {
        FramesReceived = Volatile.Read(ref _framesReceived),
        CommandsSent = Volatile.Read(ref _commandsSent),
        ParseFailures = Volatile.Read(ref _parseFailures),
        DeviceLogLines = Volatile.Read(ref _deviceLogLines),
        CommandAcks = Volatile.Read(ref _commandAcks),
        LivenessProbes = Volatile.Read(ref _livenessProbes),
        ConnectAttemptsUsb = Volatile.Read(ref _attemptsUsb),
        ConnectAttemptsWiFi = Volatile.Read(ref _attemptsWiFi),
        LastWriteMs = _lastWriteMs,
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

    /// <summary>
    /// Queues <paramref name="command"/> as its own frame, sent after everything already
    /// buffered.
    /// </summary>
    /// <remarks>
    /// Use only where merging would change the meaning of the command - see
    /// <see cref="_frames"/>. Everything else should keep using <see cref="SendCommand"/>,
    /// because one frame per gesture is cheaper on the shared bus.
    /// </remarks>
    public void SendCommandAfterCurrentFrame(TecnalCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.IsEmpty)
        {
            return;
        }

        lock (_bufferLock)
        {
            _frames.Add(command);
        }

        Post(new FlushRequest());
    }

    /// <summary>Applies new calibration and filter tuning.</summary>
    public void Reconfigure(ParserConfig parserConfig) => _parser.Reconfigure(parserConfig);

    /// <summary>
    /// Treats the current device clock as the run's zero point.
    /// </summary>
    /// <remarks>
    /// Marshalled onto the worker rather than mutating <see cref="SensorReadings"/>
    /// from the caller's thread — the readings are the worker's to own, and this is
    /// exactly how v.6 rebased its display: a local offset, never a write to the device
    /// clock and never a rewrite of samples already logged.
    /// </remarks>
    public void ZeroSessionTime() => Post(new ZeroTimeRequest());

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
                    await CheckLivenessAsync(token).ConfigureAwait(false);
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

            case ZeroTimeRequest:
                HandleZeroSessionTime();
                break;

            default:
                break;
        }
    }

    /// <summary>Rebases the session clock on the worker thread and announces the offset.</summary>
    private void HandleZeroSessionTime()
    {
        _parser.Readings.ZeroTime();
        var offsetMinutes = _parser.Readings.TimeOffsetMinutes;
        _log.LogInformation("Session clock zeroed at device {Seconds:F0}s (offset {Offset:F2} min)",
            _parser.Readings.TimeRawSeconds, offsetMinutes);
        SessionTimeZeroed?.Invoke(offsetMinutes);
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
        // Announce the operator's intent before a broken physical transport has finished
        // disposing.  This stops recovery and any link-loss annunciation immediately.
        Transition(ConnectionState.Disconnected, _medium, _endpoint, "desconectado pelo usuário",
            ConnectionTransitionCause.UserDisconnect);
        await TeardownTransportAsync().ConfigureAwait(false);
    }

    private async Task HandleLinkLostAsync(string reason, CancellationToken token)
    {
        _log.LogWarning("{Medium}: link lost ({Reason})", _medium, reason);
        _lastError = reason;
        await TeardownTransportAsync().ConfigureAwait(false);
        await EnterRecoveryAsync(reason, ConnectionTransitionCause.LinkLost, token).ConfigureAwait(false);
    }

    private Task OnConnectFailedAsync(TransportMedium medium, string reason, CancellationToken token)
    {
        _lastError = reason;
        if (medium == TransportMedium.Usb)
        {
            _samePortFailures++;
        }

        return EnterRecoveryAsync(reason, ConnectionTransitionCause.ConnectFailed, token);
    }

    private async Task EnterRecoveryAsync(string reason, ConnectionTransitionCause cause, CancellationToken token)
    {
        if (!_options.BackupEnabled)
        {
            Transition(ConnectionState.Faulted, _medium, _endpoint, reason, cause);
            return;
        }

        Transition(ConnectionState.Reconnecting, _medium, _endpoint, reason, cause);
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

    /// <summary>
    /// Decides whether the link is still alive, in two stages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stage 1, once telemetry has been quiet for
    /// <see cref="ConnectionOptions.LivenessProbeAfterSilence"/>: actively ping the
    /// transport. A failed probe drops the link immediately, rather than waiting out
    /// the full silence timeout.
    /// </para>
    /// <para>
    /// Stage 2, at <see cref="ConnectionOptions.TelemetrySilenceTimeout"/>: drop the
    /// link even if the probe still succeeds. This is the case that matters on Wi-Fi -
    /// the ESP32's web server can happily answer <c>/ping</c> and serve 304s while the
    /// telemetry task is stalled. The link is "up" and the data is stale, which for a
    /// control application is the worse failure of the two.
    /// </para>
    /// <para>
    /// While frames arrive normally neither stage does any work, so a healthy link
    /// costs no extra traffic.
    /// </para>
    /// </remarks>
    private async Task CheckLivenessAsync(CancellationToken token)
    {
        var silentFor = Environment.TickCount64 - _lastTelemetryTicks;

        if (silentFor <= (long)_options.LivenessProbeAfterSilence.TotalMilliseconds)
        {
            return;
        }

        if (silentFor > (long)_options.TelemetrySilenceTimeout.TotalMilliseconds)
        {
            Post(new LinkLostRequest(FormattableString.Invariant(
                $"sem telemetria por {_options.TelemetrySilenceTimeout.TotalSeconds:F0}s")));
            return;
        }

        // Rate-limit the probe itself to the same cadence.
        var sinceProbe = Environment.TickCount64 - _lastProbeTicks;
        if (sinceProbe < (long)_options.LivenessProbeAfterSilence.TotalMilliseconds)
        {
            return;
        }

        _lastProbeTicks = Environment.TickCount64;

        var transport = await GetTransportAsync(token).ConfigureAwait(false);
        if (transport is null)
        {
            return;
        }

        bool alive;
        try
        {
            alive = await transport.TestConnectionAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            alive = false;
        }

        Interlocked.Increment(ref _livenessProbes);

        if (!alive)
        {
            Post(new LinkLostRequest("sonda de atividade falhou"));
        }
    }

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
            // Nothing to read is normal - between poll ticks on either transport,
            // and on every Wi-Fi 304. Silence is judged by the clock, in
            // CheckLivenessAsync, not by counting empty reads here.
            return;
        }

        switch (_parser.Parse(line))
        {
            case ParseOutcome.Updated:
                _parseFailureStreak = 0;
                _lastTelemetryTicks = Environment.TickCount64;
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
        var sequenced = false;
        bool more;
        lock (_bufferLock)
        {
            // The merging buffer drains first, then one queued frame per pass. That order
            // is what makes "stop the profile, then stop routing" arrive as two frames in
            // the sequence the firmware needs.
            if (!_pending.IsEmpty)
            {
                payload = _pending;
                _pending = TecnalCommand.Create();
            }
            else if (_frames.Count > 0)
            {
                payload = _frames[0];
                _frames.RemoveAt(0);
                sequenced = true;
            }
            else
            {
                return;
            }

            more = !_pending.IsEmpty || _frames.Count > 0;
        }

        if (more)
        {
            // Wake ourselves for the next frame; a queued frame must not wait for the next
            // unrelated command to push the loop along.
            Post(new FlushRequest());
        }

        var transport = await GetTransportAsync(token).ConfigureAwait(false);
        if (transport is null)
        {
            Requeue(payload, sequenced);
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
            Requeue(payload, sequenced);
            Post(new LinkLostRequest(ex.Message));
            return;
        }

        if (!sent)
        {
            Requeue(payload, sequenced);
            Post(new LinkLostRequest("falha ao enviar comando"));
            return;
        }

        _lastWriteMs = stopwatch.Elapsed.TotalMilliseconds;
        Interlocked.Increment(ref _commandsSent);
        _log.LogDebug("TX {Payload}", json);
        CommandSent?.Invoke(json);

        // Record that the pH echo actually went out, so it is not resent every frame.
        if (payload.GetRawValue(CommandKeys.PHCal) is { } raw &&
            double.TryParse(raw.Trim('"'), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var ph))
        {
            _parser.MarkPHSent(ph);
        }

        // If dataDelay was updated, keep HttpTransport's poll period aligned.
        if (transport is HttpTransport http &&
            payload.GetRawValue(CommandKeys.DataDelay) is { } rawDelay &&
            int.TryParse(rawDelay, System.Globalization.NumberStyles.Integer,
                         System.Globalization.CultureInfo.InvariantCulture, out var delayMs) &&
            delayMs > 0)
        {
            http.SetPollPeriod(TimeSpan.FromMilliseconds(delayMs));
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
    /// <param name="sequenced">
    /// True when the payload came from the ordered frame queue. Such a frame goes back to
    /// the front of that queue rather than into the merging buffer - merging it is exactly
    /// what it was queued to avoid.
    /// </param>
    private void Requeue(TecnalCommand payload, bool sequenced = false)
    {
        lock (_bufferLock)
        {
            if (sequenced)
            {
                _frames.Insert(0, payload);
                return;
            }

            _pending = payload.Merge(_pending);
        }
    }

    private void OnPHCalibrationChanged(double calibratedPH)
        => SendCommand(TecnalCommand.Create().SetFixedString(CommandKeys.PHCal, calibratedPH, 2));

    // ==================================================================
    // Plumbing
    // ==================================================================

    private void Post(Request request) => _requests.Writer.TryWrite(request);

    private ITransport? BuildTransport(TransportMedium medium)
        => _transportFactory is not null
            ? _transportFactory(medium)
            : BuildRealTransport(medium);

    private ITransport? BuildRealTransport(TransportMedium medium) => medium switch
    {
        TransportMedium.Usb when _serialConfig is { } cfg =>
            new SerialTransport(ApplyResetEscalation(cfg), _loggerFactory?.CreateLogger<SerialTransport>()),
        TransportMedium.WiFi when _httpConfig is { } cfg =>
            new HttpTransport(cfg, _loggerFactory?.CreateLogger<HttpTransport>()),
        _ => null,
    };

    /// <summary>
    /// Enables the hardware reset once repeated handshakes have failed.
    /// </summary>
    /// <remarks>
    /// A normal connect deliberately leaves the board running so its setpoints
    /// survive. If the firmware has hung, that will never succeed - only a DTR/RTS
    /// pulse recovers it. This is the escape hatch, and it is deliberately the last
    /// resort rather than the default.
    /// </remarks>
    private SerialTransportConfig ApplyResetEscalation(SerialTransportConfig config)
    {
        if (config.PulseResetOnConnect || _samePortFailures < _options.FailuresBeforeHardwareReset)
        {
            return config;
        }

        _log.LogWarning(
            "USB {Port}: {Failures} handshake failures - escalating to a hardware reset. " +
            "The board will reboot and lose its process state.",
            config.PortName, _samePortFailures);

        return config with { PulseResetOnConnect = true };
    }

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
        // Seed both clocks at connect time; without this the first poll after a slow
        // handshake looks like a link that has been quiet for ages.
        _lastTelemetryTicks = Environment.TickCount64;
        _lastProbeTicks = Environment.TickCount64;
        _parseFailureStreak = 0;
        _samePortFailures = 0;
        _lastError = "";
    }

    private void Transition(ConnectionState state, TransportMedium? medium, string endpoint, string reason = "",
        ConnectionTransitionCause cause = ConnectionTransitionCause.Routine)
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

        StateChanged?.Invoke(new ConnectionStateChange(state, medium, endpoint, reason, cause));
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

    private sealed record ZeroTimeRequest : Request;
}
