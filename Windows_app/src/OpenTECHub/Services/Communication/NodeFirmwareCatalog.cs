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

    /// <summary>Every wire name, in the Hub's registry order.</summary>
    public static readonly IReadOnlyList<string> Devices = [Distance, Agitator, Pump, Flowmeter, Biomass];

    private static readonly Dictionary<string, HashSet<string>> Validated = new(StringComparer.OrdinalIgnoreCase)
    {
        [Distance] = new(StringComparer.OrdinalIgnoreCase) { "v11" },
        [Agitator] = new(StringComparer.OrdinalIgnoreCase) { "v10" },
        [Pump] = new(StringComparer.OrdinalIgnoreCase) { "3.9" },
        [Flowmeter] = new(StringComparer.OrdinalIgnoreCase) { "v11" },
        [Biomass] = new(StringComparer.OrdinalIgnoreCase) { "v11" },
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
        _ => device,
    };
}
