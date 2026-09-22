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

    /// <summary>
    /// Biomass node sentinel: the blank for the current gear is 0 or saturated (-99.0 AU).
    /// Anything at or below <see cref="BiomassBlankInvalidThreshold"/> reads as this state.
    /// </summary>
    public const double BiomassBlankInvalid = -99.0;
    public const double BiomassBlankInvalidThreshold = -90.0;

    /// <summary>
    /// Biomass node sentinel: the detector read zero counts, i.e. dark or blocked (9.9 AU).
    /// Anything at or above <see cref="BiomassDarkThreshold"/> reads as this state.
    /// </summary>
    public const double BiomassDark = 9.9;
    public const double BiomassDarkThreshold = 9.0;

    /// <summary>True when an absorbance is a real reading rather than absent or a sentinel.</summary>
    public static bool IsBiomassAbsorbanceMeasured(double absorbance)
        => absorbance > NotReceived &&
           absorbance > BiomassBlankInvalidThreshold &&
           absorbance < BiomassDarkThreshold;

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

    /// <summary>
    /// The flowmeter node's own reconnect switch, mirrored by Hub v10. Defaults to true
    /// so a v05 node, which never reports it, is not shown as having reconnection off.
    /// </summary>
    public bool FlowmeterReconnectWifi { get; set; } = true;
    public bool FlowCommandPending { get; set; }
    public string FlowCommandSource { get; set; } = "unknown";

    public long FlowCommandId { get; set; }
    public long FlowCommandAck { get; set; }

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

    // ---- External bath / thermal cascade (Hub 10.5.1) --------------------
    public bool HasBathTelemetry { get; set; }
    public bool BathOnline { get; set; }
    public bool? BathCommEnabled { get; set; }
    public bool? BathCommandPending { get; set; }
    public long BathCommandId { get; set; }
    public long BathCommandAck { get; set; }
    public int BathTempControlMode { get; set; } = -1;
    public bool? TempControlViaBath { get; set; }
    public bool? BathCascadeEnabled { get; set; }
    public string BathCascadeState { get; set; } = "";
    public bool BathCommandLatestWins { get; set; }
    public bool BathCommandCompletionPending { get; set; }
    public long BathCommandLastSentId { get; set; }
    public long BathCommandLastDoneId { get; set; }
    public int BathCommandCompletionAgeMs { get; set; }
    public double? TempSetpoint { get; set; }
    public double? BathSp { get; set; }
    public double? BathTarget { get; set; }
    public double? BathPv { get; set; }
    public double? BathDisplaySp { get; set; }
    public string BathState { get; set; } = "";
    public string BathPhase { get; set; } = "";
    public string BathError { get; set; } = "";
    public int? BathMode { get; set; }
    public string BathGuard { get; set; } = "";
    public double? BathDeviation { get; set; }
    public int? BathSpSource { get; set; }
    public double? BathCommandSetpoint { get; set; }
    public double? BathCommandConfirmed { get; set; }
    public string BathIP => BathNode.Ip ?? "";
    public string? BathNodeVer => BathNode.FirmwareVersion;
    public string? BathNodeMac => BathNode.Mac;
    public ExternalNodeIdentity BathNode { get; set; } = ExternalNodeIdentity.Empty;
    public double? BathCascadeError { get; set; }
    public double? BathCascadePvFiltered { get; set; }
    public double? BathCascadeP { get; set; }
    public double? BathCascadeI { get; set; }
    public bool BathCascadeSaturated { get; set; }
    public string BathCascadePausedReason { get; set; } = "";
    public long BathCascadeLastUpdateMs { get; set; }

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

    /// <inheritdoc cref="SensorSnapshot.HubFirmwareVersion"/>
    public string? HubFirmwareVersion { get; set; }

    /// <inheritdoc cref="SensorSnapshot.HubProtocolVersion"/>
    public int HubProtocolVersion { get; set; } = -1;

    /// <inheritdoc cref="SensorSnapshot.DistanceNode"/>
    public ExternalNodeIdentity DistanceNode { get; set; } = ExternalNodeIdentity.Empty;

    /// <inheritdoc cref="SensorSnapshot.AgitatorNode"/>
    public ExternalNodeIdentity AgitatorNode { get; set; } = ExternalNodeIdentity.Empty;

    /// <inheritdoc cref="SensorSnapshot.PumpNode"/>
    public ExternalNodeIdentity PumpNode { get; set; } = ExternalNodeIdentity.Empty;

    /// <inheritdoc cref="SensorSnapshot.FlowmeterNode"/>
    public ExternalNodeIdentity FlowmeterNode { get; set; } = ExternalNodeIdentity.Empty;

    /// <inheritdoc cref="SensorSnapshot.BiomassNode"/>
    public ExternalNodeIdentity BiomassNode { get; set; } = ExternalNodeIdentity.Empty;

    /// <summary>The Hub has reported on the servo drive node at least once this session.</summary>
    public bool HasServoTelemetry { get; set; }

    /// <inheritdoc cref="SensorSnapshot.HasServoSample"/>
    public bool HasServoSample { get; set; }

    public bool ServoOnline { get; set; }

    /// <inheritdoc cref="SensorSnapshot.ServoCommEnabled"/>
    public bool? ServoCommEnabled { get; set; }

    /// <inheritdoc cref="SensorSnapshot.ServoCommandPending"/>
    public bool? ServoCommandPending { get; set; }

    /// <inheritdoc cref="SensorSnapshot.ServoCommandQueueDepth"/>
    public int ServoCommandQueueDepth { get; set; } = -1;

    public bool? MotorControlViaModbus { get; set; }

    public int ServoMotorRouteAck { get; set; } = -1;

    public double ServoRpm { get; set; } = NotReceived;

    /// <inheritdoc cref="SensorSnapshot.ServoTorquePct"/>
    public double ServoTorquePct { get; set; } = NotReceived;

    public double ServoTorqueNm { get; set; } = NotReceived;
    public double ServoLoadPct { get; set; } = NotReceived;
    public double ServoPowerW { get; set; } = NotReceived;
    public double ServoEnergyWh { get; set; } = NotReceived;

    /// <inheritdoc cref="SensorSnapshot.ServoState"/>
    public int ServoState { get; set; } = -1;

    /// <inheritdoc cref="SensorSnapshot.ServoAlarm"/>
    public int ServoAlarm { get; set; } = -1;

    /// <inheritdoc cref="SensorSnapshot.ServoCommOk"/>
    public long ServoCommOk { get; set; } = -1;

    /// <inheritdoc cref="SensorSnapshot.ServoCommErr"/>
    public long ServoCommErr { get; set; } = -1;

    // ---- External-node configuration echoes (Hub 10.2) -------------------
    public double? DistanceOffsetMm { get; set; }
    public int? DistanceSamplePeriodMs { get; set; }
    public int? DistanceSendPeriodMs { get; set; }
    public bool? DistanceCommandPending { get; set; }

    public double? FlowKp { get; set; }
    public double? FlowKi { get; set; }
    public double? FlowFfGain { get; set; }
    public double? FlowFfOffset { get; set; }
    public double? FlowRampRate { get; set; }
    public double? FlowOutput { get; set; }
    public double? FlowSetpointCorrected { get; set; }
    public long? FlowmeterBootId { get; set; }
    public double? FlowTransitionVoltage { get; set; }
    public long? FlowmeterCalCrc { get; set; }

    public double? PumpPidKp { get; set; }
    public double? PumpPidKi { get; set; }
    public double? PumpPidKd { get; set; }
    public bool? PumpPotEnabled { get; set; }
    public double? PumpCycleVolume { get; set; }
    public double? PumpTransitionSpeed { get; set; }
    public double? PumpA1 { get; set; }
    public double? PumpB1 { get; set; }
    public double? PumpK1 { get; set; }
    public double? PumpF1 { get; set; }
    public double? PumpC1 { get; set; }
    public double? PumpK2 { get; set; }
    public double? PumpF2 { get; set; }
    public double? PumpC2 { get; set; }
    public long? PumpCalCrc { get; set; }

    public int? BiomassGear { get; set; }
    public double? BiomassEma { get; set; }
    public int? BiomassProbePeriodMs { get; set; }

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

    /// <summary>When a frame last carried servo values. Null before the first one.</summary>
    internal DateTimeOffset? ServoLastSeenAt { get; set; }

    /// <summary>When a frame last carried bath telemetry.</summary>
    internal DateTimeOffset? BathLastSeenAt { get; set; }

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
        FlowmeterReconnectWifi = FlowmeterReconnectWifi,
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
        HasBathTelemetry = HasBathTelemetry,
        BathOnline = BathOnline,
        BathCommEnabled = BathCommEnabled,
        BathCommandPending = BathCommandPending,
        BathCommandId = BathCommandId,
        BathCommandAck = BathCommandAck,
        BathTempControlMode = BathTempControlMode,
        TempControlViaBath = TempControlViaBath,
        BathCascadeEnabled = BathCascadeEnabled,
        BathCascadeState = BathCascadeState,
        BathCommandLatestWins = BathCommandLatestWins,
        BathCommandCompletionPending = BathCommandCompletionPending,
        BathCommandLastSentId = BathCommandLastSentId,
        BathCommandLastDoneId = BathCommandLastDoneId,
        BathCommandCompletionAgeMs = BathCommandCompletionAgeMs,
        TempSetpoint = TempSetpoint,
        BathSp = BathSp,
        BathTarget = BathTarget,
        BathPv = BathPv,
        BathDisplaySp = BathDisplaySp,
        BathState = BathState,
        BathPhase = BathPhase,
        BathError = BathError,
        BathMode = BathMode,
        BathGuard = BathGuard,
        BathDeviation = BathDeviation,
        BathSpSource = BathSpSource,
        BathCommandSetpoint = BathCommandSetpoint,
        BathCommandConfirmed = BathCommandConfirmed,
        BathNode = BathNode,
        BathCascadeError = BathCascadeError,
        BathCascadePvFiltered = BathCascadePvFiltered,
        BathCascadeP = BathCascadeP,
        BathCascadeI = BathCascadeI,
        BathCascadeSaturated = BathCascadeSaturated,
        BathCascadePausedReason = BathCascadePausedReason,
        BathCascadeLastUpdateMs = BathCascadeLastUpdateMs,
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
        HubFirmwareVersion = HubFirmwareVersion,
        HubProtocolVersion = HubProtocolVersion,
        DistanceNode = DistanceNode,
        AgitatorNode = AgitatorNode,
        PumpNode = PumpNode,
        FlowmeterNode = FlowmeterNode,
        BiomassNode = BiomassNode,
        HasServoTelemetry = HasServoTelemetry,
        HasServoSample = HasServoSample,
        ServoOnline = ServoOnline,
        ServoCommEnabled = ServoCommEnabled,
        ServoCommandPending = ServoCommandPending,
        ServoCommandQueueDepth = ServoCommandQueueDepth,
        MotorControlViaModbus = MotorControlViaModbus,
        ServoMotorRouteAck = ServoMotorRouteAck,
        ServoRpm = ServoRpm,
        ServoTorquePct = ServoTorquePct,
        ServoTorqueNm = ServoTorqueNm,
        ServoLoadPct = ServoLoadPct,
        ServoPowerW = ServoPowerW,
        ServoEnergyWh = ServoEnergyWh,
        ServoState = ServoState,
        ServoAlarm = ServoAlarm,
        ServoCommOk = ServoCommOk,
        ServoCommErr = ServoCommErr,
        DistanceOffsetMm = DistanceOffsetMm,
        DistanceSamplePeriodMs = DistanceSamplePeriodMs,
        DistanceSendPeriodMs = DistanceSendPeriodMs,
        DistanceCommandPending = DistanceCommandPending,
        FlowKp = FlowKp,
        FlowKi = FlowKi,
        FlowFfGain = FlowFfGain,
        FlowFfOffset = FlowFfOffset,
        FlowRampRate = FlowRampRate,
        FlowOutput = FlowOutput,
        FlowSetpointCorrected = FlowSetpointCorrected,
        FlowmeterBootId = FlowmeterBootId,
        FlowTransitionVoltage = FlowTransitionVoltage,
        FlowmeterCalCrc = FlowmeterCalCrc,
        PumpPidKp = PumpPidKp,
        PumpPidKi = PumpPidKi,
        PumpPidKd = PumpPidKd,
        PumpPotEnabled = PumpPotEnabled,
        PumpCycleVolume = PumpCycleVolume,
        PumpTransitionSpeed = PumpTransitionSpeed,
        PumpA1 = PumpA1,
        PumpB1 = PumpB1,
        PumpK1 = PumpK1,
        PumpF1 = PumpF1,
        PumpC1 = PumpC1,
        PumpK2 = PumpK2,
        PumpF2 = PumpF2,
        PumpC2 = PumpC2,
        PumpCalCrc = PumpCalCrc,
        BiomassGear = BiomassGear,
        BiomassEma = BiomassEma,
        BiomassProbePeriodMs = BiomassProbePeriodMs,
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
    public bool FlowmeterReconnectWifi { get; init; } = true;
    public bool FlowCommandPending { get; init; }
    public string FlowCommandSource { get; init; } = "unknown";
    public long FlowCommandId { get; init; }
    public long FlowCommandAck { get; init; }
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

    // ---- External bath / thermal cascade (Hub 10.5.1) --------------------
    public bool HasBathTelemetry { get; init; }
    public bool BathOnline { get; init; }
    public bool? BathCommEnabled { get; init; }
    public bool? BathCommandPending { get; init; }
    public long BathCommandId { get; init; }
    public long BathCommandAck { get; init; }
    public int BathTempControlMode { get; init; } = -1;
    public bool? TempControlViaBath { get; init; }
    public bool? BathCascadeEnabled { get; init; }
    public string BathCascadeState { get; init; } = "";
    public bool BathCommandLatestWins { get; init; }
    public bool BathCommandCompletionPending { get; init; }
    public long BathCommandLastSentId { get; init; }
    public long BathCommandLastDoneId { get; init; }
    public int BathCommandCompletionAgeMs { get; init; }
    public double? TempSetpoint { get; init; }
    public double? BathSp { get; init; }
    public double? BathTarget { get; init; }
    public double? BathPv { get; init; }
    public double? BathDisplaySp { get; init; }
    public string BathState { get; init; } = "";
    public string BathPhase { get; init; } = "";
    public string BathError { get; init; } = "";
    public int? BathMode { get; init; }
    public string BathGuard { get; init; } = "";
    public double? BathDeviation { get; init; }
    public int? BathSpSource { get; init; }
    public double? BathCommandSetpoint { get; init; }
    public double? BathCommandConfirmed { get; init; }
    public ExternalNodeIdentity BathNode { get; init; } = ExternalNodeIdentity.Empty;
    public double? BathCascadeError { get; init; }
    public double? BathCascadePvFiltered { get; init; }
    public double? BathCascadeP { get; init; }
    public double? BathCascadeI { get; init; }
    public bool BathCascadeSaturated { get; init; }
    public string BathCascadePausedReason { get; init; } = "";
    public long BathCascadeLastUpdateMs { get; init; }

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

    // ---- ASDA-B2 servo drive (Hub v9) ------------------------------------
    //
    // Presence and routing are orthogonal here, and collapsing them is the likeliest
    // mistake this type can invite. All four combinations are legitimate and only one
    // is a failure:
    //
    //   Online  CommEnabled
    //   true    true          operating
    //   true    false         node present, routing off - a choice, not a fault
    //   false   true          node missing with routing on - THIS is the failure
    //   false   false         this module has no servo - do not alarm
    //
    // The last one is the bench module's permanent, correct state.

    /// <summary>Hub firmware build, or null against a Hub that does not publish it.</summary>
    /// <remarks>
    /// Recorded in the session sidecar's header. Firmware 9.1.0-dev inverts the CN1's
    /// calibration before commanding the module, so a run logged under it and one logged
    /// under 9.0.0-dev differ in what the shaft actually did for the same setpoint - and
    /// nothing else in the file would say which.
    /// </remarks>
    public string? HubFirmwareVersion { get; init; }

    /// <summary>Wire contract version; -1 against a Hub that predates the key.</summary>
    public int HubProtocolVersion { get; init; }

    // ---- External-node identity (Hub 10.1) --------------------------------
    // Sticky within the link, like HubFirmwareVersion: a node that said who it is has
    // not stopped being that node. Empty against a Hub older than the keys, or before
    // the node registers. Presence is *not* here - that stays with the *Online flags.

    /// <summary>Network identity of the distance-sensor node, as the Hub registered it.</summary>
    public ExternalNodeIdentity DistanceNode { get; init; } = ExternalNodeIdentity.Empty;

    /// <summary>Network identity of the flask-agitator node.</summary>
    public ExternalNodeIdentity AgitatorNode { get; init; } = ExternalNodeIdentity.Empty;

    /// <summary>Network identity of the external peristaltic-pump node.</summary>
    public ExternalNodeIdentity PumpNode { get; init; } = ExternalNodeIdentity.Empty;

    /// <summary>Network identity of the flowmeter node.</summary>
    public ExternalNodeIdentity FlowmeterNode { get; init; } = ExternalNodeIdentity.Empty;

    /// <summary>Network identity of the biomass (absorbance) node.</summary>
    public ExternalNodeIdentity BiomassNode { get; init; } = ExternalNodeIdentity.Empty;

    /// <inheritdoc cref="HasBiomassTelemetry"/>
    public bool HasServoTelemetry { get; init; }

    /// <summary>
    /// This frame carried the servo's ten measurements. When false, every one of them
    /// holds its sentinel and nothing may be read from them.
    /// </summary>
    /// <remarks>
    /// It exists so consumers never have to test a reading against the sentinel to find
    /// out whether it is one. That test is subtly wrong here: <c>NotReceived</c> is
    /// <c>-1.0</c>, and rpm and torque are legitimately negative - the drive reports small
    /// negative speeds at rest, and torque goes negative under braking. A reading of
    /// exactly -1.0 rpm would be discarded as "missing" by a sentinel test and charted as
    /// a gap. This flag answers the question directly instead.
    /// </remarks>
    public bool HasServoSample { get; init; }

    /// <summary>A valid push reached the Hub inside its 6000 ms window.</summary>
    public bool ServoOnline { get; init; }

    /// <inheritdoc cref="BiomassCommEnabled"/>
    public bool? ServoCommEnabled { get; init; }

    /// <inheritdoc cref="BiomassCommandPending"/>
    public bool? ServoCommandPending { get; init; }

    /// <summary>Depth of the Hub's fixed queue of eight; -1 before any frame.</summary>
    /// <remarks>
    /// With no acknowledgement on this link, this and <see cref="ServoCommandPending"/>
    /// are the only way to watch a command enter the queue and be consumed. A full queue
    /// drains at one per 2 s pull, so nothing should be called failed inside sixteen
    /// seconds.
    /// </remarks>
    public int ServoCommandQueueDepth { get; init; }

    /// <summary>Selected Hub route; null when the Hub predates protocol 10.</summary>
    public bool? MotorControlViaModbus { get; init; }

    /// <summary>Route confirmed by the driver: -1 unknown, 0 UART/CN1, 1 Modbus.</summary>
    public int ServoMotorRouteAck { get; init; } = -1;

    // The ten below hold NotReceived / -1 whenever there is no publishable sample. The
    // sentinel is what the UI renders as a dash: zero is a real reading here, and
    // showing it for missing data would claim a measurement that was never taken.

    /// <summary>Measured shaft speed. Zero is a legitimate reading, not missing data.</summary>
    public double ServoRpm { get; init; }

    /// <summary>Instantaneous torque, signed, as a percentage of rated torque.</summary>
    /// <remarks>
    /// Legitimately negative during braking, which is why validity is decided by
    /// <see cref="HasServoTelemetry"/> and <see cref="ServoOnline"/> rather than by
    /// testing against the sentinel: a torque of exactly -1.0 % is a real reading.
    /// </remarks>
    public double ServoTorquePct { get; init; }

    /// <summary>Torque in N·m, derived from rated torque on the node.</summary>
    public double ServoTorqueNm { get; init; }

    /// <summary>Average load rate, whole percent. A different quantity from torque.</summary>
    public double ServoLoadPct { get; init; }

    /// <summary>Estimated mechanical shaft power. Not electrical draw, and must be labelled so.</summary>
    public double ServoPowerW { get; init; }

    /// <summary>Mechanical energy integrated on the node. Can decrease.</summary>
    public double ServoEnergyWh { get; init; }

    /// <summary>0 OFF, 1 READY, 2 SON, 3 ALARM; -1 before any frame.</summary>
    public int ServoState { get; init; }

    /// <summary>Raw P0-01 alarm code, whose hex digits mirror the drive panel. -1 before any frame.</summary>
    public int ServoAlarm { get; init; }

    /// <summary>Successful Modbus transactions this node session; -1 before any frame.</summary>
    public long ServoCommOk { get; init; }

    /// <summary>Failed Modbus samples; -1 before any frame. Judge the rate, never the total.</summary>
    public long ServoCommErr { get; init; }

    public double TimeRawSeconds { get; init; }
    public double TimeMinutes { get; init; }

    // ---- External-node configuration echoes (Hub 10.2) -------------------
    public double? DistanceOffsetMm { get; init; }
    public int? DistanceSamplePeriodMs { get; init; }
    public int? DistanceSendPeriodMs { get; init; }
    public bool? DistanceCommandPending { get; init; }

    public double? FlowKp { get; init; }
    public double? FlowKi { get; init; }
    public double? FlowFfGain { get; init; }
    public double? FlowFfOffset { get; init; }
    public double? FlowRampRate { get; init; }
    public double? FlowOutput { get; init; }
    public double? FlowSetpointCorrected { get; init; }
    public long? FlowmeterBootId { get; init; }
    public double? FlowTransitionVoltage { get; init; }
    public long? FlowmeterCalCrc { get; init; }


    /// <summary>PID gains the pump node reports running (3.10+); null on a 3.9 node.</summary>
    public double? PumpPidKp { get; init; }
    public double? PumpPidKi { get; init; }
    public double? PumpPidKd { get; init; }

    /// <summary>True when the bench potentiometers drive the motor; false after <c>pump_speed</c> or <c>pump_pot:0</c>; null on a 3.9 node.</summary>
    public bool? PumpPotEnabled { get; init; }

    /// <summary>Volume delivered by the current profile cycle, mL; <see cref="PumpVolume"/> is the session total.</summary>
    public double? PumpCycleVolume { get; init; }

    /// <summary>Polynomial curve transition speed echo.</summary>
    public double? PumpTransitionSpeed { get; init; }

    /// <summary>Pump 3.12 polynomial calibration echoes (Hub 10.4).</summary>
    public double? PumpA1 { get; init; }
    public double? PumpB1 { get; init; }
    public double? PumpK1 { get; init; }
    public double? PumpF1 { get; init; }
    public double? PumpC1 { get; init; }
    public double? PumpK2 { get; init; }
    public double? PumpF2 { get; init; }
    public double? PumpC2 { get; init; }

    /// <summary>Dual-range calibration CRC32 echo (pump 3.11, Hub 10.3).</summary>
    public long? PumpCalCrc { get; init; }

    public int? BiomassGear { get; init; }
    public double? BiomassEma { get; init; }
    public int? BiomassProbePeriodMs { get; init; }
}
