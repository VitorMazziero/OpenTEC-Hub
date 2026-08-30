namespace OpenTECHub.Protocol;

/// <summary>
/// Snapshot of the most recent good value for every telemetry channel.
/// </summary>
/// <remarks>
/// <para>
/// Mutated in place by <see cref="TelemetryParser"/> once per frame. Floats default
/// to <see cref="NotReceived"/> rather than zero, because zero is a legitimate
/// reading for several channels and "no data yet" must be distinguishable from it.
/// </para>
/// <para>
/// Not thread-safe by itself. <c>ConnectionManager</c> owns it and publishes an
/// immutable copy via <see cref="Snapshot"/>.
/// </para>
/// </remarks>
public sealed class SensorReadings
{
    /// <summary>Sentinel meaning "never received". Matches v.6's -1.0.</summary>
    public const double NotReceived = -1.0;

    public double Temperature { get; set; } = NotReceived;

    /// <summary>Accepted raw ADC count for oxygen, after spike filtering.</summary>
    public double OxygenRaw { get; set; } = NotReceived;

    /// <summary>Calibrated dissolved oxygen.</summary>
    public double OxygenCalibrated { get; set; } = NotReceived;

    /// <summary>Accepted raw ADC count for pH, after spike filtering.</summary>
    public double PHRaw { get; set; } = NotReceived;

    /// <summary>Calibrated pH in display units.</summary>
    public double PHCalibrated { get; set; } = NotReceived;

    public double Pressure { get; set; } = NotReceived;
    public double FlowRate { get; set; } = NotReceived;
    public double FlowSetpoint { get; set; } = NotReceived;
    public double FlowVoltage { get; set; } = NotReceived;
    public double Antifoam { get; set; } = NotReceived;
    public double Distance { get; set; } = NotReceived;

    public int FlowValve1 { get; set; } = -1;
    public int FlowValve2 { get; set; } = -1;
    public int FlowValveMain { get; set; } = -1;

    public bool FlowmeterOnline { get; set; }
    public bool FlowControlEnabled { get; set; }
    public bool FlowCommandPending { get; set; }
    public string FlowCommandSource { get; set; } = "unknown";

    public int FlowCommandId { get; set; }
    public int FlowCommandAck { get; set; }

    /// <summary>Parsed by v.6 but never displayed. Surfaced in the connection popover.</summary>
    public int FlowCommandDeliveries { get; set; }

    /// <summary>Parsed by v.6 but never displayed. Surfaced in the connection popover.</summary>
    public int FlowCommandAgeMs { get; set; }

    public int HubStations { get; set; }

    public double BiomassAbsorbance { get; set; } = NotReceived;
    public int BiomassRaw { get; set; }
    public int BiomassIntegrationTimeMs { get; set; }
    public double BiomassPwmPercent { get; set; }

    /// <summary>The Hub has reported on the biomass node at least once this session.</summary>
    public bool HasBiomassTelemetry { get; set; }

    /// <summary>The Hub is receiving biomass samples inside its window.</summary>
    public bool BiomassOnline { get; set; }

    /// <inheritdoc cref="SensorSnapshot.BiomassCommEnabled"/>
    public bool? BiomassCommEnabled { get; set; }

    /// <inheritdoc cref="SensorSnapshot.BiomassCommandPending"/>
    public bool? BiomassCommandPending { get; set; }

    /// <summary>True while the ESP32 reports its internal sensor-module UART healthy.</summary>
    public bool SensorCommOk { get; set; } = true;

    public double PumpFlow { get; set; } = NotReceived;
    public double PumpVolume { get; set; } = NotReceived;

    /// <summary>Profile mode the node reports running; -1 before any frame.</summary>
    public int PumpMode { get; set; } = -1;

    public double PumpPwm { get; set; } = NotReceived;
    public double PumpSpeed { get; set; } = NotReceived;
    public double PumpTargetVolume { get; set; } = NotReceived;
    public bool PumpActive { get; set; }
    public bool PumpWaiting { get; set; }

    /// <summary>The Hub has reported on the external pump at least once this session.</summary>
    public bool HasPumpTelemetry { get; set; }

    public bool PumpOnline { get; set; }

    /// <inheritdoc cref="SensorSnapshot.PumpCommEnabled"/>
    public bool? PumpCommEnabled { get; set; }

    /// <inheritdoc cref="SensorSnapshot.PumpCommandPending"/>
    public bool? PumpCommandPending { get; set; }

    /// <summary>The Hub has reported on the level/foam sensor at least once this session.</summary>
    public bool HasDistanceTelemetry { get; set; }

    public bool DistanceOnline { get; set; }

    /// <inheritdoc cref="SensorSnapshot.DistanceCommEnabled"/>
    public bool? DistanceCommEnabled { get; set; }

    /// <summary>The Hub has reported on the flask agitator at least once this session.</summary>
    /// <remarks>
    /// False against every Hub built before the agitator push handler existed, which is
    /// honest: that Hub genuinely knows nothing about the agitator.
    /// </remarks>
    public bool HasAgitatorTelemetry { get; set; }

    public bool AgitatorOnline { get; set; }

    /// <inheritdoc cref="SensorSnapshot.AgitatorCommandPending"/>
    public bool? AgitatorCommandPending { get; set; }

    public double AgitatorPercent { get; set; } = NotReceived;

    /// <summary>1 clockwise, 0 counter-clockwise, -1 before any frame.</summary>
    public int AgitatorDirection { get; set; } = -1;

    /// <inheritdoc cref="SensorSnapshot.AgitatorPotActive"/>
    public bool AgitatorPotActive { get; set; }

    /// <inheritdoc cref="SensorSnapshot.AgitatorSource"/>
    public string AgitatorSource { get; set; } = "unknown";

    /// <summary>Seconds since controller boot, as reported by the device.</summary>
    public double TimeRawSeconds { get; set; }

    /// <summary>User-zeroed offset in minutes, subtracted from the device clock.</summary>
    public double TimeOffsetMinutes { get; set; }

    /// <summary>Elapsed run time in minutes, after the user's zero offset.</summary>
    public double TimeMinutes => Math.Round((TimeRawSeconds / 60.0) - TimeOffsetMinutes, 2);

    /// <summary>When a frame last carried a Distance key. Null before the first one.</summary>
    internal DateTimeOffset? DistanceLastSeenAt { get; set; }

    /// <summary>When a frame last carried biomass values. Null before the first one.</summary>
    /// <remarks>
    /// Only consulted against a Hub that does not publish the explicit presence flag; with
    /// the flag present the Hub's own window is authoritative.
    /// </remarks>
    internal DateTimeOffset? BiomassLastSeenAt { get; set; }

    /// <summary>When a frame last carried pump values. Null before the first one.</summary>
    internal DateTimeOffset? PumpLastSeenAt { get; set; }

    /// <summary>When a frame last carried agitator values. Null before the first one.</summary>
    internal DateTimeOffset? AgitatorLastSeenAt { get; set; }

    /// <summary>Treats the current device clock as the run's zero point.</summary>
    public void ZeroTime() => TimeOffsetMinutes = TimeRawSeconds / 60.0;

    /// <summary>Immutable copy, safe to hand to the UI thread.</summary>
    public SensorSnapshot Snapshot() => new()
    {
        Temperature = Temperature,
        OxygenRaw = OxygenRaw,
        OxygenCalibrated = OxygenCalibrated,
        PHRaw = PHRaw,
        PHCalibrated = PHCalibrated,
        Pressure = Pressure,
        FlowRate = FlowRate,
        FlowSetpoint = FlowSetpoint,
        FlowVoltage = FlowVoltage,
        Antifoam = Antifoam,
        Distance = Distance,
        FlowValve1 = FlowValve1,
        FlowValve2 = FlowValve2,
        FlowValveMain = FlowValveMain,
        FlowmeterOnline = FlowmeterOnline,
        FlowControlEnabled = FlowControlEnabled,
        FlowCommandPending = FlowCommandPending,
        FlowCommandSource = FlowCommandSource,
        FlowCommandId = FlowCommandId,
        FlowCommandAck = FlowCommandAck,
        FlowCommandDeliveries = FlowCommandDeliveries,
        FlowCommandAgeMs = FlowCommandAgeMs,
        HubStations = HubStations,
        BiomassAbsorbance = BiomassAbsorbance,
        BiomassRaw = BiomassRaw,
        BiomassIntegrationTimeMs = BiomassIntegrationTimeMs,
        BiomassPwmPercent = BiomassPwmPercent,
        HasBiomassTelemetry = HasBiomassTelemetry,
        BiomassOnline = BiomassOnline,
        BiomassCommEnabled = BiomassCommEnabled,
        BiomassCommandPending = BiomassCommandPending,
        SensorCommOk = SensorCommOk,
        PumpFlow = PumpFlow,
        PumpVolume = PumpVolume,
        PumpMode = PumpMode,
        PumpPwm = PumpPwm,
        PumpSpeed = PumpSpeed,
        PumpTargetVolume = PumpTargetVolume,
        PumpActive = PumpActive,
        PumpWaiting = PumpWaiting,
        HasPumpTelemetry = HasPumpTelemetry,
        PumpOnline = PumpOnline,
        PumpCommEnabled = PumpCommEnabled,
        PumpCommandPending = PumpCommandPending,
        HasDistanceTelemetry = HasDistanceTelemetry,
        DistanceOnline = DistanceOnline,
        DistanceCommEnabled = DistanceCommEnabled,
        HasAgitatorTelemetry = HasAgitatorTelemetry,
        AgitatorOnline = AgitatorOnline,
        AgitatorCommandPending = AgitatorCommandPending,
        AgitatorPercent = AgitatorPercent,
        AgitatorDirection = AgitatorDirection,
        AgitatorPotActive = AgitatorPotActive,
        AgitatorSource = AgitatorSource,
        TimeRawSeconds = TimeRawSeconds,
        TimeMinutes = TimeMinutes,
    };
}

/// <summary>
/// Immutable telemetry snapshot published to consumers.
/// </summary>
public sealed record SensorSnapshot
{
    public double Temperature { get; init; }
    public double OxygenRaw { get; init; }
    public double OxygenCalibrated { get; init; }
    public double PHRaw { get; init; }
    public double PHCalibrated { get; init; }
    public double Pressure { get; init; }
    public double FlowRate { get; init; }
    public double FlowSetpoint { get; init; }
    public double FlowVoltage { get; init; }
    public double Antifoam { get; init; }
    public double Distance { get; init; }
    public int FlowValve1 { get; init; }
    public int FlowValve2 { get; init; }
    public int FlowValveMain { get; init; }
    public bool FlowmeterOnline { get; init; }
    public bool FlowControlEnabled { get; init; }
    public bool FlowCommandPending { get; init; }
    public string FlowCommandSource { get; init; } = "unknown";
    public int FlowCommandId { get; init; }
    public int FlowCommandAck { get; init; }
    public int FlowCommandDeliveries { get; init; }
    public int FlowCommandAgeMs { get; init; }
    public int HubStations { get; init; }
    public double BiomassAbsorbance { get; init; }
    public int BiomassRaw { get; init; }
    public int BiomassIntegrationTimeMs { get; init; }
    public double BiomassPwmPercent { get; init; }

    /// <summary>The Hub has reported on the biomass node at least once this session.</summary>
    /// <remarks>
    /// False means "no evidence either way" and must render as <i>awaiting telemetry</i>,
    /// never as <i>offline</i> - the same distinction the flowmeter row already draws.
    /// </remarks>
    public bool HasBiomassTelemetry { get; init; }

    public bool BiomassOnline { get; init; }

    /// <summary>
    /// The Hub's own biomass routing flag, echoed back. Null when the Hub does not publish
    /// it, which is the only honest answer for a Hub that predates the key.
    /// </summary>
    public bool? BiomassCommEnabled { get; init; }

    /// <summary>
    /// A command is queued for the node and not yet acknowledged. Null when the Hub
    /// has no acknowledgement channel for this device, which is not the same as
    /// "nothing pending" and must not be read as one.
    /// </summary>
    public bool? BiomassCommandPending { get; init; }

    public bool SensorCommOk { get; init; }
    public double PumpFlow { get; init; }
    public double PumpVolume { get; init; }
    public int PumpMode { get; init; }
    public double PumpPwm { get; init; }
    public double PumpSpeed { get; init; }
    public double PumpTargetVolume { get; init; }

    /// <summary>The node is inside its operating window and dosing.</summary>
    public bool PumpActive { get; init; }

    /// <summary>The node has a profile loaded but has not reached its start time yet.</summary>
    public bool PumpWaiting { get; init; }

    public bool HasPumpTelemetry { get; init; }
    public bool PumpOnline { get; init; }

    /// <inheritdoc cref="BiomassCommEnabled"/>
    public bool? PumpCommEnabled { get; init; }

    /// <summary>
    /// A command is queued for the node and not yet acknowledged. Null when the Hub
    /// has no acknowledgement channel for this device, which is not the same as
    /// "nothing pending" and must not be read as one.
    /// </summary>
    public bool? PumpCommandPending { get; init; }

    public bool HasDistanceTelemetry { get; init; }
    public bool DistanceOnline { get; init; }

    /// <inheritdoc cref="BiomassCommEnabled"/>
    public bool? DistanceCommEnabled { get; init; }

    public bool HasAgitatorTelemetry { get; init; }
    public bool AgitatorOnline { get; init; }
    /// <summary>
    /// A command is queued for the node and not yet acknowledged. Null when the Hub
    /// has no acknowledgement channel for this device, which is not the same as
    /// "nothing pending" and must not be read as one.
    /// </summary>
    public bool? AgitatorCommandPending { get; init; }
    public double AgitatorPercent { get; init; }

    /// <summary>1 clockwise, 0 counter-clockwise, -1 before any frame.</summary>
    public int AgitatorDirection { get; init; }

    /// <summary>
    /// The node's potentiometer is live. While it is, the bench knob outranks anything the
    /// app commanded, so the row must say so rather than show a setpoint it does not hold.
    /// </summary>
    public bool AgitatorPotActive { get; init; }

    /// <summary>What last moved the agitator: Pot, Hub, Wi-Fi or USB.</summary>
    public string AgitatorSource { get; init; } = "unknown";
    public double TimeRawSeconds { get; init; }
    public double TimeMinutes { get; init; }
}
