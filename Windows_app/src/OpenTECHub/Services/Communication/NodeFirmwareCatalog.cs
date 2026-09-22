using System.Globalization;
using OpenTECHub.Protocol;

namespace OpenTECHub.Services.Communication;

/// <summary>
/// The external-node firmware versions this build of the app was validated against.
/// </summary>
/// <remarks>
/// <para>
/// A <b>set per device, not a minimum</b>. The nodes version themselves differently
/// (<c>v10</c>, <c>3.8</c>, <c>rev-h</c>) and the strings do not order, so the only honest
/// question is "is this one of the versions we ran the bench with?". A version outside the
/// set is an <i>advisory</i> - a newer node may be perfectly fine - which is why nothing
/// here feeds an alarm. See D-051.
/// </para>
/// <para>
/// Keyed by the wire name the Hub uses in <c>/nodeHello</c> (<c>distance</c>,
/// <c>agitator</c>, <c>pump</c>, <c>flowmeter</c>, <c>biomass</c>). Versions are the
/// <c>ver=</c> each firmware sends today (<c>External-Devices/*/firmware</c>, 2026-09-12).
/// </para>
/// </remarks>
public static class NodeFirmwareCatalog
{
    public const string Distance = "distance";
    public const string Agitator = "agitator";
    public const string Pump = "pump";
    public const string Flowmeter = "flowmeter";
    public const string Biomass = "biomass";
    public const string Bath = "bath";

    /// <summary>Every wire name, in the Hub's registry order.</summary>
    public static readonly IReadOnlyList<string> Devices = [Distance, Agitator, Pump, Flowmeter, Biomass, Bath];

    private static readonly Dictionary<string, HashSet<string>> Validated = new(StringComparer.OrdinalIgnoreCase)
    {
        [Distance] = new(StringComparer.OrdinalIgnoreCase) { "v11" },
        [Agitator] = new(StringComparer.OrdinalIgnoreCase) { "v10" },
        [Pump] = new(StringComparer.OrdinalIgnoreCase) { "3.9", "3.10", "3.12" },
        [Flowmeter] = new(StringComparer.OrdinalIgnoreCase) { "v11", "v11.0", "v12.0" },
        [Biomass] = new(StringComparer.OrdinalIgnoreCase) { "v11", "v11.1" },
        [Bath] = new(StringComparer.OrdinalIgnoreCase) { "r3.2" },
    };

    /// <summary>Versions this build was validated with for <paramref name="device"/>; empty for an unknown name.</summary>
    public static IReadOnlyCollection<string> ValidatedVersions(string device)
        => Validated.TryGetValue(device, out var set) ? set : [];

    /// <summary>True when the version is in the validated set. Null version is not judged (returns null).</summary>
    public static bool? IsValidated(string device, string? version)
        => version is null ? null : Validated.TryGetValue(device, out var set) && set.Contains(version);

    /// <summary>
    /// One pt-BR sentence when the version is outside the validated set; null when it is
    /// inside, unknown, or the device is not catalogued.
    /// </summary>
    public static string? Advisory(string device, string? version)
        => IsValidated(device, version) is false
            ? $"Firmware {version} não validado com esta versão do app (validado: {string.Join(", ", ValidatedVersions(device))})."
            : null;

    /// <summary>Wire name → the identity in a snapshot, so callers iterate instead of switching.</summary>
    public static ExternalNodeIdentity IdentityOf(SensorSnapshot snapshot, string device) => device switch
    {
        Distance => snapshot.DistanceNode,
        Agitator => snapshot.AgitatorNode,
        Pump => snapshot.PumpNode,
        Flowmeter => snapshot.FlowmeterNode,
        Biomass => snapshot.BiomassNode,
        Bath => snapshot.BathNode,
        _ => ExternalNodeIdentity.Empty,
    };

    /// <summary>Wire name → the Hub's presence flag in a snapshot.</summary>
    public static bool IsOnline(SensorSnapshot snapshot, string device) => device switch
    {
        Distance => snapshot.DistanceOnline,
        Agitator => snapshot.AgitatorOnline,
        Pump => snapshot.PumpOnline,
        Flowmeter => snapshot.FlowmeterOnline,
        Biomass => snapshot.BiomassOnline,
        Bath => snapshot.BathOnline,
        _ => false,
    };

    /// <summary>Wire name → whether the Hub has said anything about the node this link.</summary>
    public static bool HasTelemetry(SensorSnapshot snapshot, string device) => device switch
    {
        Distance => snapshot.HasDistanceTelemetry,
        Agitator => snapshot.HasAgitatorTelemetry,
        Pump => snapshot.HasPumpTelemetry,
        Flowmeter => true,
        Biomass => snapshot.HasBiomassTelemetry,
        Bath => snapshot.HasBathTelemetry,
        _ => false,
    };

    /// <summary>Wire name → the display name the rest of the app uses.</summary>
    public static string DisplayName(string device) => device switch
    {
        Distance => ViewModels.DeviceNames.Distance,
        Agitator => ViewModels.DeviceNames.FlaskAgitator,
        Pump => ViewModels.DeviceNames.ExternalPump,
        Flowmeter => ViewModels.DeviceNames.Airflow,
        Biomass => ViewModels.DeviceNames.Absorbance,
        Bath => "Banho externo C404",
        _ => device,
    };

    /// <summary>Formats the device-specific tail of a proxied <c>/diag</c> response.</summary>
    public static string DescribeDiag(string device, IReadOnlyDictionary<string, string> extra)
    {
        var parts = new List<string>();
        switch (device)
        {
            case Distance:
                AddNumber(parts, extra, "distance", "distância", "mm", 0);
                AddNumber(parts, extra, "sample_time", "tempo da amostra", "s", 1);
                AddNumber(parts, extra, "offset_mm", "offset", "mm", 1);
                break;
            case Agitator:
                AddNumber(parts, extra, "duty", "comando", "%", 1);
                AddText(parts, extra, "dir", "direção");
                AddBool(parts, extra, "pot", "potenciômetro");
                break;
            case Pump:
                AddNumber(parts, extra, "flow", "vazão", "mL/min", 3);
                AddNumber(parts, extra, "vol", "volume", "mL", 2);
                AddText(parts, extra, "mode", "modo");
                break;
            case Flowmeter:
                AddNumber(parts, extra, "flow_rate", "vazão", "L/min", 3);
                AddNumber(parts, extra, "flow_sp", "setpoint", "L/min", 3);
                break;
            case Biomass:
                AddNumber(parts, extra, "absorbance", "absorbância", "", 3);
                AddText(parts, extra, "raw", "raw");
                AddText(parts, extra, "state", "estado");
                break;
        }

        if (parts.Count == 0)
        {
            parts.AddRange(extra.Take(3).Select(item => $"{item.Key} {item.Value}"));
        }
        return parts.Count == 0 ? "Sem métricas específicas" : string.Join(" · ", parts);
    }

    private static void AddNumber(List<string> parts, IReadOnlyDictionary<string, string> extra, string key, string label, string unit, int decimals)
    {
        if (extra.TryGetValue(key, out var raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            var suffix = string.IsNullOrEmpty(unit) ? "" : " " + unit;
            parts.Add($"{label} {value.ToString($"F{decimals}", CultureInfo.CurrentCulture)}{suffix}");
        }
    }

    private static void AddText(List<string> parts, IReadOnlyDictionary<string, string> extra, string key, string label)
    {
        if (extra.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            parts.Add($"{label} {value}");
        }
    }

    private static void AddBool(List<string> parts, IReadOnlyDictionary<string, string> extra, string key, string label)
    {
        if (extra.TryGetValue(key, out var value) && bool.TryParse(value, out var enabled))
        {
            parts.Add($"{label} {(enabled ? "ativo" : "inativo")}");
        }
    }
}
