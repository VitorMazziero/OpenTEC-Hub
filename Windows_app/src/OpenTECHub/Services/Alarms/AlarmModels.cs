namespace OpenTECHub.Services.Alarms;

/// <summary>The system alarms of the Phase 2 WP4 safety kernel.</summary>
/// <remarks>
/// These are the system (non-variable) alarms from
/// <c>docs/UI_DESIGN.md</c> §5.4.3. Per-variable HH/H/L/LL limits and the persistent-foam
/// process alarm are Phase 5 and build on this same engine.
/// </remarks>
public enum AlarmId
{
    /// <summary>The connection state left Connected.</summary>
    LinkLost,

    /// <summary>The ESP32 reports its sensor-module UART unhealthy (<c>SensorCommOK</c> false).</summary>
    ModuleOffline,

    /// <summary>The flowmeter reports offline while flow control is enabled.</summary>
    FlowmeterOffline,

    /// <summary>The flowmeter went offline with its gas path open, and is still holding it.</summary>
    /// <remarks>
    /// <para>
    /// The flowmeter node is deliberately <b>fail-in-place</b>: losing the link to the Hub
    /// does not close its valves or zero its setpoint. Its local PI loop keeps running and
    /// the gas keeps flowing, which is the right behaviour for a live culture that would
    /// otherwise be starved of oxygen by a Wi-Fi glitch.
    /// </para>
    /// <para>
    /// The cost of that choice is this alarm. Gas is entering the reactor, nothing in the
    /// app can stop it, and the Hub is no longer being told what the valves are doing.
    /// Critical rather than a warning, and separate from <see cref="FlowmeterOffline"/>:
    /// that one says a node is missing, this one says the process is still running
    /// unattended and the operator has to walk to the bench.
    /// </para>
    /// </remarks>
    UnsupervisedGasFlow,

    /// <summary>No accepted telemetry frame for more than three emission periods.</summary>
    FrozenData,

    /// <summary>A required probe reads its "never received" sentinel beyond a grace period.</summary>
    SensorAbsent,

    /// <summary>A dispatched command was never accepted by the transport (timed out).</summary>
    UnacknowledgedCommand,

    /// <summary>A recipe block is holding because an external device never confirmed its command.</summary>
    RecipeAwaitingDevice,

    /// <summary>The biomass node stopped answering the Hub while its routing is on.</summary>
    BiomassOffline,

    /// <summary>The biomass node was actively acquiring, but fresh samples stopped arriving while online (silent reboot to IDLE).</summary>
    BiomassAcquisitionStalled,

    /// <summary>The external pump node stopped answering the Hub while its routing is on.</summary>
    ExternalPumpOffline,

    /// <summary>The level/foam node stopped answering the Hub while its routing is on.</summary>
    DistanceSensorOffline,

    /// <summary>The flask agitator node stopped answering the Hub.</summary>
    FlaskAgitatorOffline,

    /// <summary>The ASDA-B2 servo node stopped answering the Hub while its routing is on.</summary>
    /// <remarks>
    /// Qualified by the routing echo, and that qualification is the whole alarm. A module
    /// with no servo reports both flags false for ever, and an alarm that ignored the echo
    /// would raise an event on it that nothing could ever clear.
    /// </remarks>
    ServoDriveOffline,

    /// <summary>The servo drive itself is in alarm, or reporting a fault code.</summary>
    /// <remarks>
    /// Critical rather than a warning: unlike the offline alarms, this is the drive saying
    /// something is wrong with the machine rather than with a link. The bench raised a real
    /// one on 2026-09-02 by powering the drive with the motor disconnected.
    /// </remarks>
    ServoDriveAlarm,

    /// <summary>
    /// The Hub's routing flag for a device disagrees with the operator's switch.
    /// </summary>
    /// <remarks>
    /// Not a device failure, which is why it is separate from the four above. The Hub
    /// persists its routing flags in NVS and the app persists the switches on the PC, so a
    /// Hub reboot can leave them disagreeing — and from then on the Hub drops that device's
    /// sub-commands in silence. It has to be an alarm rather than a badge alone, because
    /// nothing else in the app would ever notice.
    /// </remarks>
    DeviceRoutingMismatch,

    /// <summary>
    /// The flowmeter echoes a setpoint above zero with both inputs closed: the line has no way
    /// out (A/B/C plan §3.3). The router cannot command this; Avançado and a stale state can.
    /// </summary>
    GasDeadEnd,

    /// <summary>Both flowmeter inputs echoed open at once: A and B + C together, the air splits.</summary>
    GasBothOpen,

    /// <summary>The external bath is selected and routed but no longer reports online.</summary>
    ExternalBathOffline,
    /// <summary>The reactor temperature input needed by the bath cascade is invalid or stale.</summary>
    ExternalBathReactorPvInvalid,
    /// <summary>A bath command remains pending beyond its completion budget.</summary>
    ExternalBathCommandTimeout,
    /// <summary>The C404 sequence or guard reports a fault/abort/suspension.</summary>
    ExternalBathSequenceFault,
    /// <summary>The bath setpoint/confirmation disagrees with the requested cascade state.</summary>
    ExternalBathSetpointMismatch,
    /// <summary>The Hub reports persistent output saturation in the bath cascade.</summary>
    ExternalBathCascadeSaturated,
    /// <summary>The cascade error remains outside the operator-safe band.</summary>
    ExternalBathReactorDeviation,
    /// <summary>The C404 PV is outside its absolute engineering limits.</summary>
    ExternalBathPvLimit,
    /// <summary>The bath and reactor readings differ by an implausible amount.</summary>
    ExternalBathImplausibleDelta,
    /// <summary>Both the original and external temperature paths appear active.</summary>
    ExternalBathDualActuation,
}

/// <summary>Alarm severity — the colour and the audit level it maps to.</summary>
public enum AlarmSeverity
{
    /// <summary>Amber. Maps to <see cref="OpenTECHub.Services.Telemetry.AuditSeverity.Warning"/>.</summary>
    Warning,

    /// <summary>Red. Maps to <see cref="OpenTECHub.Services.Telemetry.AuditSeverity.Error"/>.</summary>
    Critical,
}

/// <summary>
/// Static definition of a system alarm: its identity, severity and debounce timing.
/// </summary>
/// <param name="Id">Which alarm.</param>
/// <param name="Title">pt-BR title, shown on the banner and in the journal.</param>
/// <param name="Severity">Amber or red.</param>
/// <param name="OnDelay">The condition must hold this long before the alarm latches.</param>
/// <param name="OffDeadband">
/// The condition must stay clear this long, after acknowledgement, before the alarm clears.
/// Stops a value sitting on the boundary from chattering.
/// </param>
public sealed record AlarmDefinition(
    AlarmId Id,
    string Title,
    AlarmSeverity Severity,
    TimeSpan OnDelay,
    TimeSpan OffDeadband);

/// <summary>An immutable view of one alarm's current state, for the UI and the journal.</summary>
/// <param name="ConditionActive">True while the raw trigger condition holds.</param>
/// <param name="Latched">
/// True once raised; stays true — even if the condition clears — until it has been both
/// acknowledged and clear for the deadband. An alarm nobody saw is the one worth keeping.
/// </param>
/// <param name="Acknowledged">True once the operator has acknowledged this occurrence.</param>
public sealed record AlarmSnapshot(
    AlarmId Id,
    string Title,
    AlarmSeverity Severity,
    bool ConditionActive,
    bool Latched,
    bool Acknowledged,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset? AcknowledgedAt,
    string Detail)
{
    /// <summary>Latched and not yet acknowledged — this is what sounds the annunciator.</summary>
    public bool IsAnnunciating => Latched && !Acknowledged;

    /// <summary>Returned to normal but never acknowledged — kept in the list deliberately.</summary>
    public bool IsReturnedUnacknowledged => Latched && !Acknowledged && !ConditionActive;

    /// <summary>The three operator-facing states from §5.4.1.</summary>
    public string StateLabel => (Latched, Acknowledged, ConditionActive) switch
    {
        (true, false, false) => "Normalizado, não reconhecido",
        (true, false, true) => "Não reconhecido",
        (true, true, _) => "Reconhecido",
        _ => "Normal",
    };
}

/// <summary>
/// The audible annunciator behind an interface, so tests never actually make noise and the
/// timed-silence policy can be exercised deterministically.
/// </summary>
public interface IAlarmAnnunciator
{
    /// <summary>
    /// Called whenever the audible state changes. <paramref name="sounding"/> is true when at
    /// least one alarm is annunciating and audio is not silenced.
    /// </summary>
    void SetSounding(bool sounding);
}
