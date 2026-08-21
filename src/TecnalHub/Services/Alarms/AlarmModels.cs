namespace TecnalHub.Services.Alarms;

/// <summary>The six system alarms of the Phase 2 WP4 safety kernel.</summary>
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

    /// <summary>No accepted telemetry frame for more than three emission periods.</summary>
    FrozenData,

    /// <summary>A required probe reads its "never received" sentinel beyond a grace period.</summary>
    SensorAbsent,

    /// <summary>A dispatched command was never accepted by the transport (timed out).</summary>
    UnacknowledgedCommand,
}

/// <summary>Alarm severity — the colour and the audit level it maps to.</summary>
public enum AlarmSeverity
{
    /// <summary>Amber. Maps to <see cref="TecnalHub.Services.Telemetry.AuditSeverity.Warning"/>.</summary>
    Warning,

    /// <summary>Red. Maps to <see cref="TecnalHub.Services.Telemetry.AuditSeverity.Error"/>.</summary>
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
