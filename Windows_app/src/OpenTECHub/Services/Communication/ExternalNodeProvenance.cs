using OpenTECHub.Protocol;

namespace OpenTECHub.Services.Communication;

/// <summary>
/// What an assay or session manifest records about one external node: which firmware
/// produced the data, and where the node answered at the time.
/// </summary>
/// <remarks>
/// Sits next to <c>HubFirmwareVersion</c> in the same headers. The flowmeter and the
/// servo are actors in the manuscript's assays; a receipt that names the Hub build but not
/// the node builds says half of what it should. Plain settable properties so the JSON
/// manifests round-trip; an old manifest without the field loads with an empty map.
/// </remarks>
public sealed class ExternalNodeProvenance
{
    public string? FirmwareVersion { get; set; }
    public string? Ip { get; set; }
    public string? Mac { get; set; }

    /// <summary>Every node the Hub had an identity for, keyed by wire name; empty on a Hub before 10.1.</summary>
    public static Dictionary<string, ExternalNodeProvenance> From(SensorSnapshot? snapshot)
    {
        var map = new Dictionary<string, ExternalNodeProvenance>(StringComparer.OrdinalIgnoreCase);
        if (snapshot is null)
        {
            return map;
        }

        foreach (var device in NodeFirmwareCatalog.Devices)
        {
            var id = NodeFirmwareCatalog.IdentityOf(snapshot, device);
            if (id.IsKnown)
            {
                map[device] = new ExternalNodeProvenance { FirmwareVersion = id.FirmwareVersion, Ip = id.Ip, Mac = id.Mac };
            }
        }

        return map;
    }

    /// <summary>One line for a text header: <c>pump=3.8@192.168.4.3 flowmeter=v10@192.168.4.4</c>, or <c>desconhecidos</c>.</summary>
    public static string Describe(IReadOnlyDictionary<string, ExternalNodeProvenance>? nodes)
    {
        if (nodes is null || nodes.Count == 0)
        {
            return "desconhecidos";
        }

        return string.Join(" ", NodeFirmwareCatalog.Devices
            .Where(nodes.ContainsKey)
            .Select(d => $"{d}={nodes[d].FirmwareVersion ?? "?"}@{nodes[d].Ip ?? "?"}"));
    }
}
