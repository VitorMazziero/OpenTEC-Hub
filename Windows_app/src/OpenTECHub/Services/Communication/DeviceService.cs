using System.Collections.Concurrent;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.Services.Communication;

/// <summary>
/// The application's view of the controller link.
/// </summary>
/// <remarks>
/// Wraps <see cref="ConnectionManager"/> and marshals its events onto the UI thread,
/// so ViewModels can subscribe without touching a <see cref="Dispatcher"/>. The
/// protocol layer stays free of any UI dependency.
/// </remarks>
public interface IDeviceService
{
    /// <summary>Current link state. Raised on the UI thread by <see cref="StateChanged"/>.</summary>
    ConnectionState State { get; }

    TransportMedium? Medium { get; }

    string Endpoint { get; }

    /// <summary>Most recent telemetry, or null before the first frame.</summary>
    SensorSnapshot? Latest { get; }

    LinkDiagnostics Diagnostics { get; }

    /// <summary>Raised on the UI thread when the link state changes.</summary>
    event Action<ConnectionStateChange>? StateChanged;

    /// <summary>Raised on the UI thread for each telemetry frame.</summary>
    event Action<SensorSnapshot>? TelemetryReceived;

    event Action<string>? RawTelemetryReceived;

    /// <summary>Raised on the UI thread for device log lines.</summary>
    event Action<string>? DeviceLogReceived;

    /// <summary>Cached node-health response received over the Hub USB serial stream.</summary>
    event Action<string>? NodeDiagReceived
    {
        add { }
        remove { }
    }

    /// <summary>Exact merged JSON successfully written to the active transport.</summary>
    event Action<string>? CommandSent;

    /// <summary>
    /// Raised on the UI thread after the operator zeroes the session clock, carrying the
    /// new offset in minutes.
    /// </summary>
    event Action<double>? SessionTimeZeroed;

    /// <summary>Connects using the persisted preference. Safe to call when already connected.</summary>
    void Connect();

    /// <summary>Connects over USB to a specific port, remembering it.</summary>
    void ConnectUsb(string portName);

    /// <summary>Connects over Wi-Fi to a specific address, remembering it.</summary>
    void ConnectWiFi(string ipAddress);

    void Disconnect();

    /// <summary>Queues a command. Merged with anything already buffered.</summary>
    void Send(OpenTECCommand command);

    /// <summary>
    /// Queues a command as its own frame, after everything already buffered.
    /// </summary>
    /// <remarks>
    /// Only for key pairs the firmware's parse order makes interact - see
    /// <c>ConnectionManager.SendCommandAfterCurrentFrame</c>. The default merges, which is
    /// right for any transport that is not the real link.
    /// </remarks>
    void SendAfterCurrentFrame(OpenTECCommand command) => Send(command);

    Task DrainCommandsAsync(CancellationToken ct = default)
        => Task.FromException(new NotSupportedException("Dispositivo não oferece barreira de transporte."));

    /// <summary>Requests diagnostics without claiming ownership of any actuator.</summary>
    void RequestNodeDiag(string device) => Send(CommandBuilders.NodeDiag(device));

    /// <summary>
    /// Zeroes the operator session clock: stores a local display/log offset without ever
    /// resetting the device clock or rewriting samples already logged.
    /// </summary>
    void ZeroSessionTime();

    /// <summary>Finds the controller on any serial port, preferring the remembered one.</summary>
    Task<string?> DiscoverUsbPortAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IDeviceService"/>
public sealed class DeviceService : IDeviceService, IAsyncDisposable
{
    private readonly ConnectionManager _manager;
    private readonly ISettingsService _settings;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger<DeviceService> _log;
    private readonly Dispatcher _dispatcher;

    public DeviceService(
        ISettingsService settings,
        ILogger<DeviceService> log,
        ILoggerFactory? loggerFactory = null,
        Dispatcher? dispatcher = null)
    {
        _settings = settings;
        _log = log;
        _loggerFactory = loggerFactory;
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;

        _manager = new ConnectionManager(
            new ConnectionOptions { BackupEnabled = settings.Current.Connection.BackupEnabled },
            settings.Current.ToParserConfig(),
            loggerFactory);

        _manager.StateChanged += OnStateChanged;
        _manager.TelemetryReceived += OnTelemetryReceived;
        _manager.RawTelemetryReceived += OnRawTelemetryReceived;
        _manager.DeviceLogReceived += OnDeviceLogReceived;
        _manager.NodeDiagReceived += OnNodeDiagReceived;
        _manager.CommandSent += OnCommandSent;
        _manager.SessionTimeZeroed += OnSessionTimeZeroed;

        // Recalibration must reach the running parser, or the operator calibrates a
        // probe and nothing changes on screen.
        _settings.Changed += OnSettingsChanged;
    }

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public TransportMedium? Medium { get; private set; }

    public string Endpoint { get; private set; } = "";

    public SensorSnapshot? Latest { get; private set; }

    public LinkDiagnostics Diagnostics => _manager.Diagnostics;

    public event Action<ConnectionStateChange>? StateChanged;

    public event Action<SensorSnapshot>? TelemetryReceived;

    public event Action<string>? RawTelemetryReceived;

    public event Action<string>? DeviceLogReceived;

    public event Action<string>? NodeDiagReceived;

    public event Action<string>? CommandSent;

    public event Action<double>? SessionTimeZeroed;

    public void Connect()
    {
        var connection = _settings.Current.Connection;

        if (connection.PreferredMedium == TransportMedium.WiFi)
        {
            ConnectWiFi(connection.IpAddress);
            return;
        }

        if (!string.IsNullOrWhiteSpace(connection.LastKnownPort))
        {
            ConnectUsb(connection.LastKnownPort);
            return;
        }

        // No remembered port: discover first, then connect. Runs off the UI thread.
        _ = Task.Run(async () =>
        {
            var port = await DiscoverUsbPortAsync().ConfigureAwait(false);
            if (port is not null)
            {
                ConnectUsb(port);
            }
            else
            {
                _log.LogWarning("No controller found on any serial port");
            }
        });
    }

    public void ConnectUsb(string portName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);

        _settings.Update(s => s with
        {
            Connection = s.Connection with
            {
                PreferredMedium = TransportMedium.Usb,
                LastKnownPort = portName,
                LastSessionConnected = true,
            },
        });

        _manager.ConnectUsb(new SerialTransportConfig { PortName = portName });
    }

    public void ConnectWiFi(string ipAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ipAddress);

        _settings.Update(s => s with
        {
            Connection = s.Connection with
            {
                PreferredMedium = TransportMedium.WiFi,
                IpAddress = ipAddress,
                LastSessionConnected = true,
            },
        });

        // Poll the device at its configured emission period (dataDelay) rather than the 1 s
        // default, so the Wi-Fi transport matches the telemetry cadence instead of polling the
        // shared response buffer faster than it refreshes. A floor keeps a tiny value sane.
        var pollMs = Math.Max(_settings.Current.Connection.DataDelayMs, 250);
        _manager.ConnectWiFi(new HttpTransportConfig
        {
            IpAddress = ipAddress,
            MinimumPollPeriod = TimeSpan.FromMilliseconds(pollMs),
        });
    }

    public void Disconnect()
    {
        // An explicit operator disconnect is a standing intent to stay offline: record it
        // so the next launch does not auto-reconnect. A link that merely drops never
        // reaches here (it is handled inside the manager), so an unattended reboot resumes.
        _settings.Update(s => s with
        {
            Connection = s.Connection with { LastSessionConnected = false },
        });

        _manager.Disconnect();
    }

    public void Send(OpenTECCommand command) => _manager.SendCommand(command);

    public void SendAfterCurrentFrame(OpenTECCommand command)
        => _manager.SendCommandAfterCurrentFrame(command);

    public Task DrainCommandsAsync(CancellationToken ct = default) => _manager.DrainCommandsAsync(ct);

    public void RequestNodeDiag(string device) => _manager.SendCommand(CommandBuilders.NodeDiag(device));

    public void ZeroSessionTime() => _manager.ZeroSessionTime();

    public Task<string?> DiscoverUsbPortAsync(CancellationToken cancellationToken = default)
        => SerialTransport.ProbePortsAsync(
            new SerialTransportConfig { PortName = "DISCOVER" },
            _settings.Current.Connection.LastKnownPort,
            _loggerFactory,
            cancellationToken);

    private void OnSettingsChanged(AppSettings settings)
        => _manager.Reconfigure(settings.ToParserConfig());

    // ------------------------------------------------------------------
    // Event marshalling
    // ------------------------------------------------------------------

    private void OnStateChanged(ConnectionStateChange change) => ToUi(() =>
    {
        State = change.State;
        Medium = change.Medium;
        Endpoint = change.Endpoint;
        StateChanged?.Invoke(change);
    });

    /// <summary>
    /// Telemetry is published at <see cref="DispatcherPriority.Background"/> (§D, D-048): below
    /// <c>Input</c> and <c>Render</c>, so a frame's ~25 subscribers never run ahead of a pending
    /// click, hover or layout. It used to go at <c>DataBind</c>, which is above both — the frame
    /// was processed first and the window answered the mouse only when it was done. The runners'
    /// watchdogs are 1 s; the few milliseconds this can add are nothing to them.
    /// </summary>
    private void OnTelemetryReceived(SensorSnapshot snapshot) => ToUi(() =>
    {
        Latest = snapshot;
        TelemetryReceived?.Invoke(snapshot);
    }, DispatcherPriority.Background);

    private void OnDeviceLogReceived(string line) => QueueLine(isRaw: false, line);

    private void OnNodeDiagReceived(string json)
        => ToUi(() => NodeDiagReceived?.Invoke(json), DispatcherPriority.Background);

    private void OnRawTelemetryReceived(string line) => QueueLine(isRaw: true, line);

    private void OnCommandSent(string json) => ToUi(() => CommandSent?.Invoke(json));

    private void OnSessionTimeZeroed(double offsetMinutes)
        => ToUi(() => SessionTimeZeroed?.Invoke(offsetMinutes));

    // Raw and log lines of one frame are batched into a single dispatcher item rather than one
    // per line; the subscribers still see them one at a time, in arrival order.
    private readonly ConcurrentQueue<(bool IsRaw, string Line)> _pendingLines = new();
    private int _lineDrainScheduled;

    private void QueueLine(bool isRaw, string line)
    {
        _pendingLines.Enqueue((isRaw, line));
        if (Interlocked.Exchange(ref _lineDrainScheduled, 1) == 0)
        {
            ToUi(DrainLines, DispatcherPriority.Background);
        }
    }

    private void DrainLines()
    {
        Interlocked.Exchange(ref _lineDrainScheduled, 0);
        while (_pendingLines.TryDequeue(out var item))
        {
            if (item.IsRaw)
            {
                RawTelemetryReceived?.Invoke(item.Line);
            }
            else
            {
                DeviceLogReceived?.Invoke(item.Line);
            }
        }
    }

    /// <remarks>
    /// <see cref="Dispatcher.BeginInvoke(Delegate, object[])"/> rather than
    /// <c>Invoke</c>: the protocol worker must never block waiting on the UI thread.
    /// Telemetry is a stream of the latest truth, so a frame dropped during a busy
    /// render is not worth stalling the link for. State and command echoes go at
    /// <see cref="DispatcherPriority.Normal"/>; the telemetry stream at <c>Background</c>.
    /// </remarks>
    private void ToUi(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.BeginInvoke(action, priority);
    }

    public async ValueTask DisposeAsync()
    {
        _settings.Changed -= OnSettingsChanged;
        _manager.StateChanged -= OnStateChanged;
        _manager.TelemetryReceived -= OnTelemetryReceived;
        _manager.DeviceLogReceived -= OnDeviceLogReceived;
        _manager.NodeDiagReceived -= OnNodeDiagReceived;
        _manager.CommandSent -= OnCommandSent;
        _manager.SessionTimeZeroed -= OnSessionTimeZeroed;

        await _manager.DisposeAsync().ConfigureAwait(false);
    }
}
