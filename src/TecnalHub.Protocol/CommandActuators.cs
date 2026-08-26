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
/// The list is the Phase 1/2 core loop, pH, the WP7 cultivation dosing pumps
/// (nutrient, antifoam and the flask agitator), and the Phase 3 subsystems: the biomass
/// sensor (WP1) and the external pump (WP2). Keys that no controller contends for —
/// device/system commands, calibration coefficients, and the foam/level <b>sensor</b>
/// configuration — map to <c>null</c> and are unowned. They pass the arbiter freely.
/// </para>
/// <para>
/// The foam-control keys (<c>distanceSensorComm</c>, <c>distanceSensorReference</c> and the
/// <c>foam*</c> timers) are deliberately <b>unowned</b>. They configure the level/foam
/// sensor and its automatic response, not an actuator held against another owner — the
/// same treatment calibration and the <c>pHCal</c> echo get. A safe-stop stops the
/// antifoam <i>pump</i>; it must not blind foam monitoring by disabling the sensor.
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

    /// <summary>Nutrient dosing pump: operation/mix timing, cycle counts and intensity.</summary>
    Nutrient,

    /// <summary>Antifoam dosing pump: operation/mix timing and intensity.</summary>
    Antifoam,

    /// <summary>
    /// The separate flask agitator (a bench device): on/off, automatic mode, magnitude,
    /// direction and the potentiometer re-enable. Not the reactor impeller.
    /// </summary>
    FlaskAgitator,

    /// <summary>
    /// The biomass optical sensor (WP1): enable, blank capture, start/stop and the
    /// low/high/optimal integration thresholds. A measurement, so a safe-stop leaves it
    /// running rather than blinding it; owned so a recipe cannot fight the operator over it.
    /// </summary>
    Biomass,

    /// <summary>
    /// The external peristaltic feed pump (WP2): enable, the five firmware profile modes
    /// and their parameters. Its proportional-gas coupling drives <see cref="Aeration"/>,
    /// not this actuator, so it is arbitrated as flow rather than as the pump.
    /// </summary>
    ExternalPump,
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

            [CommandKeys.NutriOperation] = ActuatorId.Nutrient,
            [CommandKeys.NutriMix] = ActuatorId.Nutrient,
            [CommandKeys.NutriOpCycle] = ActuatorId.Nutrient,
            [CommandKeys.NutriMixCycle] = ActuatorId.Nutrient,
            [CommandKeys.NutriIntensity] = ActuatorId.Nutrient,

            [CommandKeys.AntifoamOperation] = ActuatorId.Antifoam,
            [CommandKeys.AntifoamMix] = ActuatorId.Antifoam,
            [CommandKeys.AntifoamIntensity] = ActuatorId.Antifoam,

            // distanceSensor* and foam* configure the level/foam sensor and its automatic
            // response, not an actuator — they stay unowned, like the calibration keys.

            [CommandKeys.AgitatorOn] = ActuatorId.FlaskAgitator,
            [CommandKeys.AgitatorAuto] = ActuatorId.FlaskAgitator,
            [CommandKeys.AgitatorPercent] = ActuatorId.FlaskAgitator,
            [CommandKeys.AgitatorDir] = ActuatorId.FlaskAgitator,
            [CommandKeys.AgitatorReEnablePot] = ActuatorId.FlaskAgitator,

            // Biomass (WP1): enable, blank/start/stop and the integration thresholds.
            [CommandKeys.BiomassComm] = ActuatorId.Biomass,
            [CommandKeys.Blank] = ActuatorId.Biomass,
            [CommandKeys.BiomassStart] = ActuatorId.Biomass,
            [CommandKeys.BiomassStop] = ActuatorId.Biomass,
            [CommandKeys.Low] = ActuatorId.Biomass,
            [CommandKeys.High] = ActuatorId.Biomass,
            [CommandKeys.Opt] = ActuatorId.Biomass,

            // External pump (WP2): enable and the profile frame. The dynamic p{i}/t{i}/q{i}
            // coefficient keys are matched by pattern in ForKey rather than enumerated here.
            [CommandKeys.PumpComm] = ActuatorId.ExternalPump,
            [CommandKeys.Mode] = ActuatorId.ExternalPump,
            [CommandKeys.Speed] = ActuatorId.ExternalPump,
            [CommandKeys.InitT] = ActuatorId.ExternalPump,
            [CommandKeys.FinalT] = ActuatorId.ExternalPump,
            [CommandKeys.LambdaConst] = ActuatorId.ExternalPump,
            [CommandKeys.LambdaLinear] = ActuatorId.ExternalPump,
            [CommandKeys.PhiLinear] = ActuatorId.ExternalPump,
            [CommandKeys.LambdaExp] = ActuatorId.ExternalPump,
            [CommandKeys.PhiExp] = ActuatorId.ExternalPump,
            [CommandKeys.NumSegments] = ActuatorId.ExternalPump,
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
        ActuatorId.Nutrient,
        ActuatorId.Antifoam,
        ActuatorId.FlaskAgitator,
        ActuatorId.Biomass,
        ActuatorId.ExternalPump,
    ];

    /// <summary>
    /// The actuator a key drives, or null for a configuration, system or calibration
    /// key that no controller owns.
    /// </summary>
    /// <remarks>
    /// The external pump's polynomial (<c>p0..p20</c>) and piecewise (<c>t0..t99</c>,
    /// <c>q0..q99</c>) coefficient keys are variable in number, so they are matched by
    /// pattern rather than enumerated in the static map.
    /// </remarks>
    public static ActuatorId? ForKey(string key)
    {
        if (KeyToActuator.TryGetValue(key, out var id))
        {
            return id;
        }

        return IsPumpCoefficientKey(key) ? ActuatorId.ExternalPump : null;
    }

    /// <summary>True for a pump coefficient key: <c>p</c>/<c>t</c>/<c>q</c> followed by digits.</summary>
    private static bool IsPumpCoefficientKey(string key)
    {
        if (key.Length < 2 || key[0] is not ('p' or 't' or 'q'))
        {
            return false;
        }

        for (var i = 1; i < key.Length; i++)
        {
            if (!char.IsAsciiDigit(key[i]))
            {
                return false;
            }
        }

        return true;
    }

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
        ActuatorId.Nutrient => "dosagem de nutriente",
        ActuatorId.Antifoam => "dosagem de antiespumante",
        ActuatorId.FlaskAgitator => "agitador de frasco",
        ActuatorId.Biomass => "sensor de biomassa",
        ActuatorId.ExternalPump => "bomba externa",
        _ => actuator.ToString(),
    };
}
