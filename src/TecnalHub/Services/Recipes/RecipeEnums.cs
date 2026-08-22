namespace TecnalHub.Services.Recipes;

/// <summary>
/// The nineteen recipe block types, taken from ReceitasTECNAL's <c>NodeType</c> enum and
/// re-targeted to the ESP32-S3 protocol.
/// </summary>
/// <remarks>
/// <para>
/// Canonical names are English (code is English, UI is pt-BR — see <c>docs/CONVENTIONS.md</c>).
/// The names are the stable serialization tag written into recipe JSON; ReceitasTECNAL's
/// historical spellings are tolerated only on load (<see cref="RecipeNodeTypes"/>), never
/// written, so hand-written and migrated recipes still open.
/// </para>
/// <para>
/// Three ReceitasTECNAL concepts change meaning here: <see cref="SetLoop"/> replaces the
/// Modbus control-word toggle (there is no control word on the wire), <see cref="ResetVariables"/>
/// replaces <c>ResetAccumulator</c> (register 20 becomes <c>resetVariables:1</c>), and the
/// pump blocks map to the ESP32 dosing keys rather than to a VNC screen driver.
/// </para>
/// </remarks>
public enum NodeType
{
    /// <summary>Exactly one per recipe. The single entry point.</summary>
    Start,

    /// <summary>At least one per recipe. Successful termination.</summary>
    End,

    /// <summary>Waits a fixed duration before continuing.</summary>
    Timer,

    /// <summary>Holds until a measured variable satisfies a comparison, or a timeout elapses.</summary>
    MonitorVariable,

    /// <summary>Holds in standby until an operator releases the recipe.</summary>
    ManualIntervention,

    /// <summary>Join: all inbound branches must complete before continuing.</summary>
    And,

    /// <summary>Join: the first inbound branch to complete wins.</summary>
    Or,

    /// <summary>The oxygen kLa cascade. The scientific core. See <c>docs/UI_DESIGN.md</c> §5.3.7.</summary>
    CascadeControl,

    /// <summary>Writes one subsystem setpoint in engineering units.</summary>
    SetSetpoint,

    /// <summary>Writes several setpoints as one combined command object.</summary>
    MultiSetpoint,

    /// <summary>Enables or disables one control loop (subsystem enable / setpoint 0 = off).</summary>
    SetLoop,

    /// <summary>Enables or disables several loops in one object.</summary>
    MultiLoop,

    /// <summary>Configures the pH dosing pump (acid/base).</summary>
    PhPump,

    /// <summary>Configures the antifoam dosing pump. Renamed from ReceitasTECNAL's "Bomba Espuma".</summary>
    AntifoamPump,

    /// <summary>Configures the nutrient dosing pump (cycle-only).</summary>
    NutrientPump,

    /// <summary>Timed on/off control of the external feed pump.</summary>
    PumpControl,

    /// <summary>Marks a labelled data-acquisition window in the session log.</summary>
    DataAcquisition,

    /// <summary>Writes a message to Eventos.</summary>
    LogEvent,

    /// <summary>Resets the module's process variables (<c>resetVariables:1</c>). Confirms first.</summary>
    ResetVariables,
}

/// <summary>Where a node is in its execution lifecycle. Mirrors ReceitasTECNAL's <c>NodeState</c>.</summary>
public enum NodeState
{
    /// <summary>Not yet reached by execution.</summary>
    Waiting,

    /// <summary>Currently executing / evaluating.</summary>
    Evaluating,

    /// <summary>Finished successfully.</summary>
    Completed,

    /// <summary>Faulted.</summary>
    Error,
}

/// <summary>The six block categories, with their header colours from <c>docs/UI_DESIGN.md</c> §5.3.6.</summary>
public enum BlockCategory
{
    /// <summary>Início · Fim. Slate.</summary>
    Flow,

    /// <summary>Sincronizar · Qualquer · Controle Cascata O₂. Violet.</summary>
    Logic,

    /// <summary>Temporizador · Monitorar Variável · Intervenção Manual. Blue.</summary>
    Triggers,

    /// <summary>Setpoint and loop actions. Orange.</summary>
    Actions,

    /// <summary>Dosing and feed pumps. Teal.</summary>
    Pumps,

    /// <summary>Aquisição · Registrar Evento · Zerar Variáveis. Slate.</summary>
    Utilities,
}

/// <summary>
/// A <b>measured</b> telemetry variable a <see cref="NodeType.MonitorVariable"/> block may watch.
/// </summary>
/// <remarks>
/// Widened from ReceitasTECNAL (which allowed only Temperatura, pH, O₂): TECNAL-Hub adds
/// Pressão, Vazão, Nível and Biomassa — all real telemetry. <b>Actuation variables are
/// deliberately absent:</b> agitation has no feedback key on the wire at all, so monitoring
/// it would wait forever — see <c>docs/UI_DESIGN.md</c> §5.3.6. The validator rejects any
/// hand-edited recipe that monitors an actuation variable.
/// </remarks>
public enum MeasuredVariable
{
    Temperature,
    Ph,
    Oxygen,
    Pressure,
    Flow,
    Level,
    Biomass,
}

/// <summary>
/// A subsystem whose setpoint a <see cref="NodeType.SetSetpoint"/> block may write.
/// </summary>
/// <remarks>
/// The controllable subsystems TECNAL-Hub exposes on Controle. Unlike ReceitasTECNAL, an
/// <see cref="Oxygen"/> setpoint writes <c>oxygenMonitor</c> only — it does <b>not</b> engage
/// the nitrogen/gas-mixer enrichment path, which is deferred (<c>docs/ROADMAP.md</c>,
/// <i>Explicitly deferred</i>).
/// </remarks>
public enum SetpointVariable
{
    Temperature,
    Agitation,
    Oxygen,
    Flow,
    Pressure,
    Ph,
}

/// <summary>
/// A control loop a <see cref="NodeType.SetLoop"/> block may enable or disable.
/// </summary>
/// <remarks>
/// Re-targeted from ReceitasTECNAL's Modbus control-word bits: there is no control word on
/// the wire, so enabling a loop means the subsystem's own enable (<c>flowmeterComm:1</c>) or
/// a setpoint of <c>0</c> to disable. The gas mixer is present for shape but its enrichment
/// path ships disabled.
/// </remarks>
public enum ControlLoop
{
    /// <summary>Aeration / flow loop — <c>flowmeterComm</c>.</summary>
    Aeration,

    /// <summary>pH dosing loop — intensity 0 disables.</summary>
    Ph,

    /// <summary>Antifoam dosing loop.</summary>
    Antifoam,

    /// <summary>Nutrient dosing loop.</summary>
    Nutrient,

    /// <summary>Gas mixer (nitrogen enrichment) — deferred; disabled.</summary>
    GasMixer,
}

/// <summary>Comparison operators for <see cref="NodeType.MonitorVariable"/>.</summary>
public enum ComparisonOperator
{
    GreaterThan,
    LessThan,
    GreaterOrEqual,
    LessOrEqual,
    Equal,
}

/// <summary>Time units for durations.</summary>
public enum TimeUnit
{
    Seconds,
    Minutes,
    Hours,
}

/// <summary>Whether a data-acquisition window runs for a fixed time or until the operator stops it.</summary>
public enum AcquisitionMode
{
    FixedTime,
    ManualStop,
}

/// <summary>Loop enable/disable operation.</summary>
public enum LoopOperation
{
    Enable,
    Disable,
}

/// <summary>Manual-intervention gate: hold in standby, or pass through.</summary>
public enum ManualGateOperation
{
    /// <summary>Hold the recipe here until the operator releases it.</summary>
    Hold,

    /// <summary>Complete immediately and continue.</summary>
    Pass,
}

/// <summary>Which pH dosing pump a <see cref="NodeType.PhPump"/> block targets.</summary>
public enum PhPumpTarget
{
    Acid,
    Base,
}

/// <summary>What a pump block does. ReceitasTECNAL's <c>Dosagem</c> mode was removed upstream.</summary>
public enum PumpOperation
{
    /// <summary>Turn the pump on or off directly (see <see cref="PumpManualAction"/>).</summary>
    Manual,

    /// <summary>Zero the accumulated dosing volume.</summary>
    ResetVolume,

    /// <summary>Configure the on/off dosing cycle.</summary>
    ConfigureCycle,
}

/// <summary>The on/off choice for a pump's manual operation.</summary>
public enum PumpManualAction
{
    On,
    Off,
}
