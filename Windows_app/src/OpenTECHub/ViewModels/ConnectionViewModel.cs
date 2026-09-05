using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.ViewModels;

/// <summary>
/// Backs the title-bar connection chip and its popover.
/// </summary>
/// <remarks>
/// Connecting was the only reason most operators ever opened v.6's Configurations
/// window, so it stops being a destination: the chip shows the link, the popover
/// carries the few controls that matter, and everything else moves to Advanced
/// Settings. See <c>docs/UI_DESIGN.md</c> section 8.
/// </remarks>
public sealed partial class ConnectionViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceService _device;
    private readonly ISettingsService _settings;

    public ConnectionViewModel(IDeviceService device, ISettingsService settings)
    {
        _device = device;
        _settings = settings;

        IpAddress = settings.Current.Connection.IpAddress;
        UseWiFi = settings.Current.Connection.PreferredMedium == TransportMedium.WiFi;

        _device.StateChanged += OnStateChanged;
        _device.TelemetryReceived += OnTelemetryReceived;

        // The popover needs its port list, but this constructor runs on the first-frame
        // path and the hardware ranking costs a WMI query - ~1.1 s cold. So the list is
        // filled from the cheap device-map read first, and the ranking is folded in once
        // it arrives off-thread. The set of ports is identical either way; only the order
        // improves, and the operator never waits on it.
        ApplyPortList(SerialTransport.ListPortNames());
        _ = RankPortsInBackgroundAsync();
    }

    /// <summary>Serial ports offered in the popover.</summary>
    public ObservableCollection<string> AvailablePorts { get; } = [];

    [ObservableProperty]
    public partial string? SelectedPort { get; set; }

    [ObservableProperty]
    public partial string IpAddress { get; set; }

    /// <summary>Wi-Fi selected in the popover rather than USB.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseUsb))]
    public partial bool UseWiFi { get; set; }

    /// <summary>
    /// The USB radio button's state, kept as the strict inverse of
    /// <see cref="UseWiFi"/>.
    /// </summary>
    /// <remarks>
    /// A real two-way property rather than an inverting converter on
    /// <c>IsChecked</c>: a converter would make the pair look independent, and two
    /// radio buttons that can both be unchecked is a state the operator should never
    /// be able to reach.
    /// </remarks>
    public bool UseUsb
    {
        get => !UseWiFi;
        set
        {
            if (value != !UseWiFi)
            {
                UseWiFi = !value;
            }
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    [NotifyPropertyChangedFor(nameof(StateKey))]
    public partial ConnectionState State { get; set; } = ConnectionState.Disconnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string Endpoint { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial TransportMedium? Medium { get; set; }

    [ObservableProperty]
    public partial string LastError { get; set; } = "";

    [ObservableProperty]
    public partial int FramesReceived { get; set; }

    [ObservableProperty]
    public partial int CommandsSent { get; set; }

    [ObservableProperty]
    public partial string LatencyText { get; set; } = "—";

    [ObservableProperty]
    public partial bool IsDiscovering { get; set; }

    public bool IsConnected => State == ConnectionState.Connected;

    /// <summary>
    /// Drives the chip's colour. A string rather than a brush so the View keeps
    /// ownership of appearance.
    /// </summary>
    public string StateKey => State switch
    {
        ConnectionState.Connected => "Ok",
        ConnectionState.Connecting => "Actuating",
        ConnectionState.Reconnecting => "Warning",
        ConnectionState.Faulted => "Alarm",
        _ => "Idle",
    };

    /// <summary>pt-BR summary shown on the chip itself.</summary>
    public string StatusText
    {
        get
        {
            var where = Medium switch
            {
                TransportMedium.Usb => string.IsNullOrEmpty(Endpoint) ? "USB" : $"USB {Endpoint}",
                TransportMedium.WiFi => string.IsNullOrEmpty(Endpoint) ? "Wi-Fi" : $"Wi-Fi {Endpoint}",
                TransportMedium.Simulation => string.IsNullOrEmpty(Endpoint) ? "Simulação" : Endpoint,
                _ => "",
            };

            var label = State switch
            {
                ConnectionState.Connected => "Conectado",
                ConnectionState.Connecting => "Conectando…",
                ConnectionState.Reconnecting => "Reconectando…",
                ConnectionState.Faulted => "Falha na conexão",
                _ => "Desconectado",
            };

            return string.IsNullOrEmpty(where) ? label : $"{label} · {where}";
        }
    }

    [RelayCommand]
    private void Connect()
    {
        if (UseWiFi)
        {
            _device.ConnectWiFi(IpAddress);
            return;
        }

        if (!string.IsNullOrWhiteSpace(SelectedPort))
        {
            _device.ConnectUsb(SelectedPort);
            return;
        }

        _device.Connect(); // no port chosen: fall back to discovery
    }

    [RelayCommand]
    private void Disconnect() => _device.Disconnect();

    /// <summary>Explicit operator reconnect used by F5 and the command palette.</summary>
    [RelayCommand]
    private void Reconnect()
    {
        if (State != ConnectionState.Disconnected)
        {
            _device.Disconnect();
        }

        Connect();
    }

    [RelayCommand]
    private async Task RefreshPortsAsync()
    {
        // ConfigureAwait(true): the continuation mutates AvailablePorts, which is bound to
        // the popover, so it has to land back on the UI thread.
        ApplyPortList(await SerialTransport.ListCandidatePortsAsync().ConfigureAwait(true));
    }

    /// <summary>
    /// Folds the hardware ranking into the list the constructor already published.
    /// </summary>
    /// <remarks>
    /// Fire-and-forget from the constructor, so it swallows its own failures: a WMI
    /// service that is disabled or wedged must degrade to the unranked list the operator
    /// already has, not surface as an unobserved task exception.
    /// </remarks>
    private async Task RankPortsInBackgroundAsync()
    {
        try
        {
            await RefreshPortsAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // The naturally-sorted list stands; ranking is an optimisation, not a contract.
        }
    }

    private void ApplyPortList(IReadOnlyList<string> ports)
    {
        var previous = SelectedPort;

        AvailablePorts.Clear();
        foreach (var port in ports)
        {
            AvailablePorts.Add(port);
        }

        SelectedPort =
            previous is not null && AvailablePorts.Contains(previous) ? previous
            : _settings.Current.Connection.LastKnownPort is { } remembered
              && AvailablePorts.Contains(remembered) ? remembered
            : AvailablePorts.FirstOrDefault();
    }

    /// <summary>Probes every serial port for a controller and selects what it finds.</summary>
    [RelayCommand]
    private async Task DiscoverAsync()
    {
        if (IsDiscovering)
        {
            return;
        }

        IsDiscovering = true;
        try
        {
            var found = await _device.DiscoverUsbPortAsync().ConfigureAwait(true);
            await RefreshPortsAsync().ConfigureAwait(true);

            if (found is not null)
            {
                SelectedPort = found;
                UseWiFi = false;
            }
            else
            {
                LastError = "Nenhum controlador encontrado nas portas seriais.";
            }
        }
        finally
        {
            IsDiscovering = false;
        }
    }

    private void OnStateChanged(ConnectionStateChange change)
    {
        State = change.State;
        Endpoint = change.Endpoint;
        Medium = change.Medium;

        if (!string.IsNullOrEmpty(change.Reason))
        {
            LastError = change.Reason;
        }
        else if (change.State == ConnectionState.Connected)
        {
            LastError = "";
        }

        if (change.State == ConnectionState.Connected && change.Medium == TransportMedium.Usb)
        {
            Endpoint = change.Endpoint;
            SelectedPort = change.Endpoint;
        }

        if (change.State != ConnectionState.Connected)
        {
            LatencyText = "—";
        }
    }

    private void OnTelemetryReceived(SensorSnapshot _)
    {
        var diagnostics = _device.Diagnostics;
        FramesReceived = diagnostics.FramesReceived;
        CommandsSent = diagnostics.CommandsSent;

        // Only the measured round trip is shown. LastWriteMs is the time to hand the
        // frame to the USB driver - near zero, and not a latency the operator can act on.
        // Until the firmware has answered something, "—" is the honest reading.
        LatencyText = diagnostics.LastRoundTripMs is { } ms
            ? ms.ToString("F0", CultureInfo.CurrentCulture) + " ms"
            : "—";
    }

    public void Dispose()
    {
        _device.StateChanged -= OnStateChanged;
        _device.TelemetryReceived -= OnTelemetryReceived;
    }
}
