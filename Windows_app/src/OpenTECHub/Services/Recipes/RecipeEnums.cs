namespace OpenTECHub.Services.Recipes;

/// <summary>
/// The nineteen recipe block types, taken from ReceitasOpenTEC's <c>NodeType</c> enum and
/// re-targeted to the ESP32-S3 protocol.
/// </summary>
/// <remarks>
/// <para>
/// Canonical names are English (code is English, UI is pt-BR — see <c>docs/CONVENTIONS.md</c>).
/// The names are the stable serialization tag written into recipe JSON; ReceitasOpenTEC's
/// historical spellings are tolerated only on load (<see cref="RecipeNodeTypes"/>), never
/// written, so hand-written and migrated recipes still open.
/// </para>
/// <para>
/// Three ReceitasOpenTEC concepts change meaning here: <see cref="SetLoop"/> replaces the
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

    /// <summary>Configures the antifoam dosing pump. Renamed from ReceitasOpenTEC's "Bomba Espuma".</summary>
    AntifoamPump,

    /// <summary>Configures the nutrient dosing pump (cycle-only).</summary>
    NutrientPump,

    /// <summary>
    /// The external feed pump: routing enable, one of the five firmware profile modes, or a stop.
    /// </summary>
    /// <remarks>
    /// Was a placeholder that only logged a timed on/off intent while WP2 was outstanding. WP2 is
    /// done, so this now drives the same <c>PumpProfileMath.BuildCommand</c> path the manual card
    /// uses — one profile builder, not two that can drift.
    /// </remarks>
    PumpControl,

    /// <summary>The biomass optical sensor: routing, blank, start/stop and the integration thresholds.</summary>
    BiomassSensor,

    /// <summary>The separate flask agitator: run at a magnitude and direction, or stop.</summary>
    FlaskAgitator,

    /// <summary>Marks a labelled data-acquisition window in the session log.</summary>
    DataAcquisition,

    /// <summary>Writes a message to Eventos.</summary>
    LogEvent,

    /// <summary>Resets the module's process variables (<c>resetVariables:1</c>). Confirms first.</summary>
    ResetVariables,

    /// <summary>Unattended determination over the common kLa runner and store.</summary>
    KlaAssay,

    /// <summary>Monotonic periodic trigger; missed slots are skipped.</summary>
    Periodic,
}

public enum RecipeKlaConditionMode { SingleAtCurrentCondition, SingleExplicit, Multiple }

/// <summary>Where a node is in its execution lifecycle. Mirrors ReceitasOpenTEC's <c>NodeState</c>.</summary>
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

    /// <summary>Sincronizar · Qualquer · Controle de O₂. Violet.</summary>
    Logic,

    /// <summary>Temporizador · Monitorar Variável · Intervenção Manual. Blue.</summary>
    Triggers,

    /// <summary>Setpoint and loop actions. Orange.</summary>
    Actions,

    /// <summary>The module's own dosing pumps: pH, antifoam, nutrient. Teal.</summary>
    Pumps,

    /// <summary>
    /// The Wi-Fi nodes behind the Hub: external pump, biomass sensor, flask agitator. Cyan.
    /// </summary>
    /// <remarks>
    /// Split out from <see cref="Pumps"/> for the same reason the synoptic was regrouped: these
    /// devices are separate ESP32s on the Hub's SoftAP and can be absent on their own, while the
    /// dosing pumps live inside the OpenTEC module on its internal UART and cannot. A block that
    /// may hold waiting for a device to answer belongs beside the others that can.
    /// </remarks>
    ExternalDevices,

    /// <summary>Aquisição · Registrar Evento · Zerar Variáveis. Slate.</summary>
    Utilities,
}

/// <summary>
/// A <b>measured</b> telemetry variable a <see cref="NodeType.MonitorVariable"/> block may watch.
/// </summary>
/// <remarks>
/// Widened from ReceitasOpenTEC (which allowed only Temperatura, pH, O₂): OpenTEC-Hub adds
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
/// The controllable subsystems OpenTEC-Hub exposes on Controle. Unlike ReceitasOpenTEC, an
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
/// Re-targeted from ReceitasOpenTEC's Modbus control-word bits: there is no control word on
/// the wire. For aeration, Hub v7 routes an explicit flow desired-state frame; a setpoint of
/// <c>0</c> disables it.
/// </remarks>
public enum ControlLoop
{
    /// <summary>Aeration / flow loop — explicit setpoint and complete valve state.</summary>
    Aeration,

    /// <summary>pH dosing loop — intensity 0 disables.</summary>
    Ph,

    /// <summary>Antifoam dosing loop.</summary>
    Antifoam,

    /// <summary>Nutrient dosing loop.</summary>
    Nutrient,
}

/// <summary>What a <see cref="NodeType.PumpControl"/> block does to the external feed pump.</summary>
public enum ExternalPumpAction
{
    /// <summary>Switch the Hub's routing on, so the node receives commands at all.</summary>
    Enable,

    /// <summary>Send one of the five firmware profiles. Requires routing to already be on.</summary>
    SendProfile,

    /// <summary>Stop the profile and clear routing, in that order.</summary>
    Stop,
}

/// <summary>What a <see cref="NodeType.BiomassSensor"/> block does.</summary>
public enum BiomassAction
{
    /// <summary>Switch the Hub's routing on. Everything else needs this first.</summary>
    Enable,

    /// <summary>Capture the zero-absorbance reference. The sweep takes 20-40 s and is not confirmed; follow with a timer of at least 60 s.</summary>
    Blank,

    /// <summary>Start the acquisition loop.</summary>
    Start,

    /// <summary>Stop the acquisition loop, leaving routing on.</summary>
    Stop,

    /// <summary>Send the three integration-time thresholds together.</summary>
    Thresholds,

    /// <summary>Stop acquisition and clear routing, in that order.</summary>
    Disable,
}

/// <summary>What a <see cref="NodeType.FlaskAgitator"/> block does.</summary>
public enum FlaskAgitatorAction
{
    /// <summary>Run at the block's magnitude and direction.</summary>
    Run,

    /// <summary>
    /// Stop, <b>locking the bench potentiometer out</b>.
    /// </summary>
    /// <remarks>
    /// A recipe stop has to be deterministic. Leaving the potentiometer enabled means the node
    /// re-reads the knob on its next loop and a stop with the knob at 60 % restarts the motor at
    /// 60 % — and the recipe would then hold forever waiting for a zero that never comes.
    /// </remarks>
    Stop,
}

/// <summary>Flask-agitator direction, as the operator picks it.</summary>
public enum AgitatorDirection
{
    /// <summary>Clockwise — <c>agitatorDir:1</c> on the wire.</summary>
    Clockwise,

    /// <summary>Counter-clockwise — <c>agitatorDir:0</c>.</summary>
    CounterClockwise,
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

/// <summary>What a pump block does. ReceitasOpenTEC's <c>Dosagem</c> mode was removed upstream.</summary>
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
