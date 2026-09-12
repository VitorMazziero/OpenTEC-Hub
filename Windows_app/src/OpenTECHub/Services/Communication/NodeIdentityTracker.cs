using OpenTECHub.Protocol;

namespace OpenTECHub.Services.Communication;

/// <summary>What changed about one node's network identity between two frames.</summary>
public enum NodeIdentityChangeKind
{
    /// <summary>First time this link the Hub reported an address for the node.</summary>
    Registered,

    /// <summary>The Hub now sees the node at a different address (DHCP renumbering after a reboot, typically).</summary>
    IpChanged,

    /// <summary>The node reports a different firmware version than before.</summary>
    FirmwareChanged,

    /// <summary>A different radio answers under this device name: the board was swapped.</summary>
    MacChanged,
}

/// <param name="Device">Wire name (<c>pump</c>, …).</param>
/// <param name="DisplayName">The name the app shows for it.</param>
public sealed record NodeIdentityChange(
    string Device,
    string DisplayName,
    NodeIdentityChangeKind Kind,
    ExternalNodeIdentity Before,
    ExternalNodeIdentity After);

/// <summary>
/// Turns the stream of snapshots into the few identity events worth writing down.
/// </summary>
/// <remarks>
/// <para>
/// Pure and synchronous: feed it every snapshot, it returns the changes since the last one
/// (usually none). It is what makes the Link Watchdog's reassociations and the SoftAP's
/// DHCP renumbering visible after the fact in <c>eventos.jsonl</c>, instead of only to
/// whoever happened to be watching a serial monitor.
/// </para>
/// <para>
/// <b>Loss of identity is not an event.</b> When the Hub reboots it sends <c>0.0.0.0</c>
/// again and the address goes back to unknown; that is already "offline" on the presence
/// side and would only duplicate it here. The next real address is a fresh
/// <see cref="NodeIdentityChangeKind.Registered"/>.
/// </para>
/// </remarks>
public sealed class NodeIdentityTracker
{
    private readonly Dictionary<string, ExternalNodeIdentity> _last =
        NodeFirmwareCatalog.Devices.ToDictionary(d => d, _ => ExternalNodeIdentity.Empty, StringComparer.OrdinalIgnoreCase);

    /// <summary>The identity last observed per device.</summary>
    public ExternalNodeIdentity Current(string device)
        => _last.TryGetValue(device, out var id) ? id : ExternalNodeIdentity.Empty;

    /// <summary>Compares one snapshot with the previous state and returns what changed.</summary>
    public IReadOnlyList<NodeIdentityChange> Observe(SensorSnapshot snapshot)
    {
        List<NodeIdentityChange>? changes = null;
        foreach (var device in NodeFirmwareCatalog.Devices)
        {
            var after = NodeFirmwareCatalog.IdentityOf(snapshot, device);
            var before = _last[device];
            if (ReferenceEquals(after, before) || after == before)
            {
                continue;
            }

            _last[device] = after;
            var name = NodeFirmwareCatalog.DisplayName(device);

            if (after.Ip is not null && before.Ip is null)
            {
                Add(ref changes, new(device, name, NodeIdentityChangeKind.Registered, before, after));
            }
            else if (after.Ip is not null && before.Ip is not null && after.Ip != before.Ip)
            {
                Add(ref changes, new(device, name, NodeIdentityChangeKind.IpChanged, before, after));
            }

            if (after.FirmwareVersion is not null && before.FirmwareVersion is not null &&
                !string.Equals(after.FirmwareVersion, before.FirmwareVersion, StringComparison.Ordinal))
            {
                Add(ref changes, new(device, name, NodeIdentityChangeKind.FirmwareChanged, before, after));
            }

            if (after.Mac is not null && before.Mac is not null &&
                !string.Equals(after.Mac, before.Mac, StringComparison.OrdinalIgnoreCase))
            {
                Add(ref changes, new(device, name, NodeIdentityChangeKind.MacChanged, before, after));
            }
        }

        return changes ?? [];
    }

    /// <summary>Forgets everything, so the next address is a registration again (new link).</summary>
    public void Reset()
    {
        foreach (var device in NodeFirmwareCatalog.Devices)
        {
            _last[device] = ExternalNodeIdentity.Empty;
        }
    }

    private static void Add(ref List<NodeIdentityChange>? list, NodeIdentityChange change)
        => (list ??= []).Add(change);
}
