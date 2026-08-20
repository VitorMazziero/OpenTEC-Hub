namespace TecnalHub.Protocol;

/// <summary>
/// A physically distinct actuator group, for command ownership.
/// </summary>
/// <remarks>
/// <para>
/// Ownership is enforced <b>per actuator</b>, not per key: the aeration frame carries
/// six keys (<c>flowmeterComm</c>, <c>flowSetpoint</c>, <c>maxFlow</c>, both valves and
/// the inverted vent flag), and they are one indivisible thing to own. A cascade that
/// owns aeration owns the whole valve/flow group, or none of it.
/// </para>
/// <para>
/// The list is deliberately the Phase 1/2 core loop plus pH. Keys that no controller
/// contends for yet — device/system commands, calibration coefficients, and the
/// Phase 2 WP7/Phase 3 subsystems (nutrient, antifoam, foam, flask agitator, biomass,
/// external pump) — map to <c>null</c> and are unowned. They pass the arbiter freely
/// today; each gains an owner in the WP that adds its control surface.
/// </para>
/// </remarks>
public enum ActuatorId
{
    /// <summary>Jacket temperature setpoint.</summary>
    Temperature,

    /// <summary>Impeller / motor rpm.</summary>
    Agitation,

    /// <summary>Dissolved-oxygen monitor setpoint.</summary>
    Oxygen,

    /// <summary>Airflow, its ceiling, both gas valves and the vent flag — one group.</summary>
    Aeration,

    /// <summary>Head-space pressure reference.</summary>
    Pressure,

    /// <summary>pH dosing pump: reference, band, timing and intensity.</summary>
    PHDosing,
}

/// <summary>
/// Maps command keys to the actuator they drive, so a single arbiter can enforce
/// ownership per actuator rather than per key.
/// </summary>
/// <remarks>
/// This is protocol-domain knowledge — which wire keys are actuator setpoints and
/// which are configuration — so it lives beside <see cref="CommandKeys"/> and stays
/// free of any application type. See <c>docs/ARCHITECTURE.md</c>.
/// </remarks>
public static class CommandActuators
{
    private static readonly IReadOnlyDictionary<string, ActuatorId> KeyToActuator =
        new Dictionary<string, ActuatorId>(StringComparer.Ordinal)
        {
            [CommandKeys.TempSetpoint] = ActuatorId.Temperature,

            [CommandKeys.MotorSetpoint] = ActuatorId.Agitation,

            [CommandKeys.OxygenMonitor] = ActuatorId.Oxygen,

            // The whole aeration frame is one actuator group.
            [CommandKeys.FlowmeterComm] = ActuatorId.Aeration,
            [CommandKeys.FlowSetpoint] = ActuatorId.Aeration,
            [CommandKeys.MaxFlow] = ActuatorId.Aeration,
            [CommandKeys.Valve1] = ActuatorId.Aeration,
            [CommandKeys.Valve2] = ActuatorId.Aeration,
            [CommandKeys.V_Flow] = ActuatorId.Aeration,

            [CommandKeys.PressureReference] = ActuatorId.Pressure,

            [CommandKeys.PHSetpoint] = ActuatorId.PHDosing,
            [CommandKeys.PHError] = ActuatorId.PHDosing,
            [CommandKeys.PHOperation] = ActuatorId.PHDosing,
            [CommandKeys.PHMix] = ActuatorId.PHDosing,
            [CommandKeys.PHIntensity] = ActuatorId.PHDosing,

            // pHCal is a calibration echo the protocol reflects back on its own, not a
            // dosing setpoint, so it is intentionally unowned — it must never be blocked
            // by whoever happens to own pH dosing.
        };

    /// <summary>Every actuator an arbiter tracks, in synoptic order.</summary>
    public static IReadOnlyList<ActuatorId> All { get; } =
    [
        ActuatorId.Temperature,
        ActuatorId.Agitation,
        ActuatorId.Oxygen,
        ActuatorId.Aeration,
        ActuatorId.Pressure,
        ActuatorId.PHDosing,
    ];

    /// <summary>
    /// The actuator a key drives, or null for a configuration, system or calibration
    /// key that no controller owns.
    /// </summary>
    public static ActuatorId? ForKey(string key)
        => KeyToActuator.TryGetValue(key, out var id) ? id : null;

    /// <summary>The distinct actuators <paramref name="command"/> touches, unowned keys ignored.</summary>
    public static IReadOnlyCollection<ActuatorId> ActuatorsIn(TecnalCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var set = new HashSet<ActuatorId>();
        foreach (var key in command.Keys)
        {
            if (ForKey(key) is { } actuator)
            {
                set.Add(actuator);
            }
        }

        return set;
    }

    /// <summary>The pt-BR label for an actuator, for events and tooltips.</summary>
    public static string Label(ActuatorId actuator) => actuator switch
    {
        ActuatorId.Temperature => "temperatura",
        ActuatorId.Agitation => "agitação",
        ActuatorId.Oxygen => "oxigênio",
        ActuatorId.Aeration => "aeração",
        ActuatorId.Pressure => "pressão",
        ActuatorId.PHDosing => "dosagem de pH",
        _ => actuator.ToString(),
    };
}
