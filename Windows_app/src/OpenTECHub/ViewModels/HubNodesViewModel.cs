using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;

namespace OpenTECHub.ViewModels;

/// <summary>One row of the "Nós na rede do Hub" table.</summary>
public sealed partial class HubNodeRowViewModel(string device) : ObservableObject
{
    public string Device { get; } = device;

    public string DisplayName { get; } = NodeFirmwareCatalog.DisplayName(device);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IpText))]
    [NotifyPropertyChangedFor(nameof(MacText))]
    [NotifyPropertyChangedFor(nameof(FirmwareText))]
    [NotifyPropertyChangedFor(nameof(FirmwareAdvisoryText))]
    public partial ExternalNodeIdentity Identity { get; set; } = ExternalNodeIdentity.Empty;

    /// <summary>Presence as the aggregate frame says it, or null before the Hub said anything.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    public partial bool? Online { get; set; }

    /// <summary>From <c>/nodes</c> (Wi-Fi only): the node sent its hello. Null when not consulted or on Hub 10.0.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    public partial bool? Registered { get; set; }

    /// <summary>When the Hub last heard the node, as this app observed it; null when never.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SeenText))]
    public partial DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>Freshness reported by <c>/nodes</c>, when the directory was consulted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SeenText))]
    public partial TimeSpan? HubReportedAge { get; set; }

    public string IpText => Identity.Ip ?? "—";

    public string MacText => Identity.Mac ?? "—";

    public string FirmwareText => Identity.FirmwareVersion ?? "—";

    public string? FirmwareAdvisoryText => NodeFirmwareCatalog.Advisory(Device, Identity.FirmwareVersion);

    public string StateText => Online switch
    {
        true => "Online",
        false when Registered is false => "Nunca se registrou",
        false => "Offline",
        null => "Aguardando telemetria",
    };

    /// <summary>"Visto há": the Hub's own figure when available, else the app's observation.</summary>
    public string SeenText
    {
        get
        {
            if (HubReportedAge is { } age)
            {
                return Format(age);
            }

            return LastSeenAt is { } at ? Format(DateTimeOffset.UtcNow - at) : "—";
        }
    }

    internal static string Format(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        return age.TotalSeconds < 60
            ? $"{age.TotalSeconds.ToString("F0", CultureInfo.CurrentCulture)} s"
            : age.TotalMinutes < 60
                ? $"{age.TotalMinutes.ToString("F0", CultureInfo.CurrentCulture)} min"
                : $"{age.TotalHours.ToString("F1", CultureInfo.CurrentCulture)} h";
    }
}

/// <summary>
/// The "Nós na rede do Hub" panel: who is on the Hub's Wi-Fi, where, with which firmware.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two sources, one table.</b> The primary source is the aggregate frame, which works
/// over USB and Wi-Fi alike and already carries IP, firmware and MAC (Hub 10.1). When the
/// PC is on the Hub's Wi-Fi the table is <i>enriched</i> from <c>GET /nodes</c> - whether the
/// node ever registered, and the Hub's own freshness figure - polled every ten seconds while
/// the panel is showing and never inside the telemetry transport's lock. A failed poll
/// changes nothing: the frame is the truth, the directory only adds to it.
/// </para>
/// <para>
/// Against a Hub older than 10.1 the identity columns stay "—" and one notice at the top
/// says why, so an empty table is never mistaken for an empty network.
/// </para>
/// </remarks>
public sealed partial class HubNodesViewModel : ObservableObject, IDisposable
{
    /// <summary>The Hub firmware that first published node identity in the aggregate frame.</summary>
    public static readonly Version IdentityFirmware = new(10, 1);

    private readonly IDeviceService _device;
    private readonly Func<string, CancellationToken, Task<HubNodeDirectory?>> _fetchDirectory;
    private readonly TimeProvider _time;
    private readonly IDisposable? _clientLifetime;
    private CancellationTokenSource? _polling;

    public HubNodesViewModel(IDeviceService device, TimeProvider? time = null)
        : this(device, CreateClient(out var client), time)
    {
        _clientLifetime = client;
    }

    /// <param name="fetchDirectory">The <c>/nodes</c> reader; injectable so tests need no socket.</param>
    public HubNodesViewModel(
        IDeviceService device,
        Func<string, CancellationToken, Task<HubNodeDirectory?>> fetchDirectory,
        TimeProvider? time = null)
    {
        _device = device;
        _fetchDirectory = fetchDirectory;
        _time = time ?? TimeProvider.System;
        Nodes = new ObservableCollection<HubNodeRowViewModel>(
            NodeFirmwareCatalog.Devices.Select(d => new HubNodeRowViewModel(d)));

        _device.TelemetryReceived += OnTelemetryReceived;
        _device.StateChanged += OnStateChanged;
        ApplyLink(_device.State, _device.Medium, _device.Endpoint);
        if (_device.Latest is { } latest)
        {
            ApplySnapshot(latest);
        }
    }

    private static Func<string, CancellationToken, Task<HubNodeDirectory?>> CreateClient(out HubNodeDirectoryClient client)
    {
        var c = new HubNodeDirectoryClient();
        client = c;
        return c.FetchAsync;
    }

    /// <summary>Five rows, in the Hub's registry order, never removed.</summary>
    public ObservableCollection<HubNodeRowViewModel> Nodes { get; }

    /// <summary>The telemetry link is Wi-Fi, so <c>/nodes</c> is reachable.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirectoryAvailabilityText))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsHubOnWiFi { get; set; }

    /// <summary>The panel is on screen; polling runs only then.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>Hub firmware as the frame reports it; null before the first frame or on an old Hub.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLegacyHubNotice))]
    [NotifyPropertyChangedFor(nameof(LegacyHubNoticeText))]
    public partial string? HubFirmwareVersion { get; set; }

    /// <summary>Nodes with an address in the aggregate frame, out of five.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RegisteredCountText))]
    public partial int RegisteredCount { get; set; }

    [ObservableProperty]
    public partial string LastDirectoryText { get; set; } = "Diretório do Hub não consultado.";

    public string RegisteredCountText => $"{RegisteredCount}/{Nodes.Count} nós com endereço";

    public string DirectoryAvailabilityText => IsHubOnWiFi
        ? "A tabela é enriquecida por GET /nodes a cada 10 s enquanto esta seção estiver aberta."
        : "Consulta a /nodes disponível apenas por Wi-Fi; por USB a tabela usa o quadro de telemetria.";

    /// <summary>The Hub said its version and it predates the identity keys.</summary>
    public bool ShowLegacyHubNotice => HubFirmwareVersion is { } v && !PublishesIdentity(v);

    public string LegacyHubNoticeText => ShowLegacyHubNotice
        ? $"O Hub {HubFirmwareVersion} não publica a identidade dos nós; atualize para {IdentityFirmware.Major}.{IdentityFirmware.Minor} ou superior."
        : "";

    /// <summary>Parses the leading <c>major.minor</c> of a firmware string such as <c>10.1.0-dev</c>.</summary>
    public static bool PublishesIdentity(string firmware)
    {
        var span = firmware.AsSpan().Trim();
        var end = 0;
        while (end < span.Length && (char.IsAsciiDigit(span[end]) || span[end] == '.'))
        {
            end++;
        }

        var numeric = span[..end].TrimEnd('.');
        if (numeric.Length == 0 || !numeric.Contains('.'))
        {
            return int.TryParse(numeric, NumberStyles.None, CultureInfo.InvariantCulture, out var major) && major > IdentityFirmware.Major;
        }

        return Version.TryParse(numeric, out var v) && new Version(v.Major, Math.Max(0, v.Minor)) >= IdentityFirmware;
    }

    /// <summary>Consults <c>/nodes</c> once. Available only on Wi-Fi.</summary>
    [RelayCommand(CanExecute = nameof(IsHubOnWiFi))]
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var hub = _device.Endpoint;
        if (!IsHubOnWiFi || string.IsNullOrWhiteSpace(hub))
        {
            return;
        }

        HubNodeDirectory? directory;
        try
        {
            directory = await _fetchDirectory(hub, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (directory is null)
        {
            LastDirectoryText = "O Hub não respondeu a /nodes.";
            return;
        }

        foreach (var row in Nodes)
        {
            if (directory.Find(row.Device) is not { } entry)
            {
                continue;
            }

            row.Registered = entry.Registered;
            row.HubReportedAge = entry.SinceLastSeen(directory.HubTimeMs);
            if (entry.Identity.IsKnown)
            {
                row.Identity = Merge(row.Identity, entry.Identity);
            }
        }

        LastDirectoryText = $"Diretório consultado às {_time.GetLocalNow().ToString("HH:mm:ss", CultureInfo.CurrentCulture)}.";
    }

    partial void OnIsActiveChanged(bool value) => UpdatePolling();

    partial void OnIsHubOnWiFiChanged(bool value) => UpdatePolling();

    private void UpdatePolling()
    {
        var shouldPoll = IsActive && IsHubOnWiFi;
        if (shouldPoll && _polling is null)
        {
            _polling = new CancellationTokenSource();
            _ = PollAsync(_polling.Token);
        }
        else if (!shouldPoll && _polling is { } cts)
        {
            _polling = null;
            cts.Cancel();
            cts.Dispose();
        }
    }

    private async Task PollAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await RefreshAsync(token).ConfigureAwait(true);
                await Task.Delay(TimeSpan.FromSeconds(10), _time, token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnStateChanged(ConnectionStateChange change)
        => ApplyLink(change.State, change.Medium, change.Endpoint);

    private void ApplyLink(ConnectionState state, TransportMedium? medium, string endpoint)
    {
        IsHubOnWiFi = state == ConnectionState.Connected && medium == TransportMedium.WiFi;
        if (state != ConnectionState.Connected)
        {
            foreach (var row in Nodes)
            {
                row.Online = null;
                row.Registered = null;
                row.HubReportedAge = null;
            }

            RegisteredCount = 0;
        }
    }

    private void OnTelemetryReceived(SensorSnapshot snapshot) => ApplySnapshot(snapshot);

    private void ApplySnapshot(SensorSnapshot snapshot)
    {
        HubFirmwareVersion = snapshot.HubFirmwareVersion;
        var now = _time.GetUtcNow();
        var withAddress = 0;
        foreach (var row in Nodes)
        {
            var identity = NodeFirmwareCatalog.IdentityOf(snapshot, row.Device);
            if (identity != row.Identity)
            {
                row.Identity = identity;
            }

            if (identity.Ip is not null)
            {
                withAddress++;
            }

            var hasTelemetry = NodeFirmwareCatalog.HasTelemetry(snapshot, row.Device);
            var online = NodeFirmwareCatalog.IsOnline(snapshot, row.Device);
            row.Online = hasTelemetry ? online : null;
            if (online)
            {
                row.LastSeenAt = now;
                row.HubReportedAge = null;
            }
        }

        RegisteredCount = withAddress;
    }

    private static ExternalNodeIdentity Merge(ExternalNodeIdentity frame, ExternalNodeIdentity directory)
        => new(frame.Ip ?? directory.Ip, frame.Mac ?? directory.Mac, frame.FirmwareVersion ?? directory.FirmwareVersion);

    public void Dispose()
    {
        _device.TelemetryReceived -= OnTelemetryReceived;
        _device.StateChanged -= OnStateChanged;
        _polling?.Cancel();
        _polling?.Dispose();
        _polling = null;
        _clientLifetime?.Dispose();
    }
}
