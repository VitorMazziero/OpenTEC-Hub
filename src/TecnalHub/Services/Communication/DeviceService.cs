using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using TecnalHub.Protocol;
using TecnalHub.Services.Persistence;

namespace TecnalHub.Services.Communication;

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

    /// <summary>Raised on the UI thread for device log lines.</summary>
    event Action<string>? DeviceLogReceived;

    /// <summary>Exact merged JSON successfully written to the active transport.</summary>
    event Action<string>? CommandSent;

    /// <summary>Connects using the persisted preference. Safe to call when already connected.</summary>
    void Connect();

    /// <summary>Connects over USB to a specific port, remembering it.</summary>
    void ConnectUsb(string portName);

    /// <summary>Connects over Wi-Fi to a specific address, remembering it.</summary>
    void ConnectWiFi(string ipAddress);

    void Disconnect();

    /// <summary>Queues a command. Merged with anything already buffered.</summary>
    void Send(TecnalCommand command);

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
        _manager.DeviceLogReceived += OnDeviceLogReceived;
        _manager.CommandSent += OnCommandSent;

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

    public event Action<string>? DeviceLogReceived;

    public event Action<string>? CommandSent;

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
            },
        });

        _manager.ConnectWiFi(new HttpTransportConfig { IpAddress = ipAddress });
    }

    public void Disconnect() => _manager.Disconnect();

    public void Send(TecnalCommand command) => _manager.SendCommand(command);

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

    private void OnTelemetryReceived(SensorSnapshot snapshot) => ToUi(() =>
    {
        Latest = snapshot;
        TelemetryReceived?.Invoke(snapshot);
    });

    private void OnDeviceLogReceived(string line) => ToUi(() => DeviceLogReceived?.Invoke(line));

    private void OnCommandSent(string json) => ToUi(() => CommandSent?.Invoke(json));

    /// <remarks>
    /// <see cref="Dispatcher.BeginInvoke(Delegate, object[])"/> rather than
    /// <c>Invoke</c>: the protocol worker must never block waiting on the UI thread.
    /// Telemetry is a stream of the latest truth, so a frame dropped during a busy
    /// render is not worth stalling the link for.
    /// </remarks>
    private void ToUi(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.BeginInvoke(action, DispatcherPriority.DataBind);
    }

    public async ValueTask DisposeAsync()
    {
        _settings.Changed -= OnSettingsChanged;
        _manager.StateChanged -= OnStateChanged;
        _manager.TelemetryReceived -= OnTelemetryReceived;
        _manager.DeviceLogReceived -= OnDeviceLogReceived;
        _manager.CommandSent -= OnCommandSent;

        await _manager.DisposeAsync().ConfigureAwait(false);
    }
}
