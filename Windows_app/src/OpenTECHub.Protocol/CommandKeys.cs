namespace OpenTECHub.Protocol;

/// <summary>
/// Every JSON key the firmware understands, as constants.
/// </summary>
/// <remarks>
/// Names deliberately mirror the wire spelling, warts included (<c>V_Flow</c> for
/// <c>v_Flow</c>, <c>PHCal</c> for <c>pHCal</c>), so that grepping for a key seen in
/// a device log lands on the code that emits it.
/// <para>See <c>docs/PROTOCOL.md</c> section 3.</para>
/// </remarks>
public static class CommandKeys
{
    // ---- System ----------------------------------------------------------
    public const string ComTest = "comTest";
    public const string DataDelay = "dataDelay";
    public const string ResetVariables = "resetVariables";
    public const string Restart = "restart";

    // ---- Core loop (Phase 1) ---------------------------------------------
    public const string TempSetpoint = "tempSetpoint";

    // ---- External bath / thermal cascade (Hub 10.5.1) --------------------
    public const string TempControlMode = "tempControlMode";
    public const string BathComm = "bathComm";
    public const string BathMode = "bathMode";
    public const string BathSync = "bathSync";
    public const string BathAbort = "bathAbort";
    public const string BathCascadeReset = "bathCascadeReset";
    public const string BathCascadeKp = "bathCascadeKp";
    public const string BathCascadeTiS = "bathCascadeTiS";
    public const string BathCascadeBiasC = "bathCascadeBiasC";
    public const string BathCascadePeriodMs = "bathCascadePeriodMs";
    public const string BathCascadeFilterS = "bathCascadeFilterS";
    public const string BathCascadeCommandMinMs = "bathCascadeCommandMinMs";
    public const string BathCascadeCommandBandC = "bathCascadeCommandBandC";
    public const string BathCascadeSlewCMin = "bathCascadeSlewCMin";
    public const string BathCascadeOffsetHighC = "bathCascadeOffsetHighC";
    public const string BathCascadeOffsetLowC = "bathCascadeOffsetLowC";
    public const string BathCascadeOutputMinC = "bathCascadeOutputMinC";
    public const string BathCascadeOutputMaxC = "bathCascadeOutputMaxC";

    /// <summary>Agitation reference, 0-1000 rpm.</summary>
    /// <remarks>
    /// <para>
    /// Hub 10 routes this one reference either directly to P1-09 over Modbus or unchanged
    /// to the original module over UART/CN1, according to <see cref="MotorControlMode"/>.
    /// </para>
    /// <para>
    /// <b>Zero is not "stop", it is "disable".</b> The module's UART vocabulary uses
    /// <c>V</c> as the motor-enable flag: a zero reference sends <c>0V</c> and the module
    /// latches disabled - its own keypad will not bring the motor back until a non-zero
    /// setpoint arrives. Anything offering this as a stop control must say so.
    /// </para>
    /// </remarks>
    public const string MotorSetpoint = "motorSetpoint";

    /// <summary>Setpoint route: 0 = original module over UART/CN1; 1 = direct Modbus.</summary>
    /// <remarks>Changing it always disables agitation and requires a new setpoint.</remarks>
    public const string MotorControlMode = "motorControlMode";

    public const string OxygenMonitor = "oxygenMonitor";
    public const string PressureReference = "pressureReference";

    public const string FlowmeterComm = "flowmeterComm";
    public const string FlowSetpoint = "flowSetpoint";
    public const string MaxFlow = "maxFlow";
    public const string Valve1 = "valve_1";
    public const string Valve2 = "valve_2";

    /// <summary>
    /// Vent valve. <b>Inverted:</b> 1 when the flow setpoint is zero, else 0.
    /// See <c>docs/PROTOCOL.md</c> section 3.1.
    /// </summary>
    public const string V_Flow = "v_Flow";
    public const string ReconnectWifi = "reconnectWifi";

    // ---- Flow calibration: two-segment curve split at 0.0545 V ------------
    // The low segment is quartic: a1/b1 opt the firmware into the x⁴/x³ terms, and
    // omitting them means the legacy quadratic model (see flowmeter_OpenTECHUB_V05.ino).
    public const string A1 = "a1";
    public const string B1 = "b1";
    public const string K1 = "k1";
    public const string F1 = "f1";
    public const string C1 = "c1";
    public const string K2 = "k2";
    public const string F2 = "f2";
    public const string C2 = "c2";

    // ---- pH (Phase 2) ----------------------------------------------------
    public const string PHSetpoint = "pHSetpoint";
    public const string PHError = "pHError";
    public const string PHOperation = "pHOperation";
    public const string PHMix = "pHMix";
    public const string PHIntensity = "pHIntensity";

    /// <summary>
    /// Calibrated pH echoed back to the device as a <b>quoted string</b> with two
    /// decimals. See <c>docs/PROTOCOL.md</c> section 2.2.
    /// </summary>
    public const string PHCal = "pHCal";

    // ---- Nutrient / antifoam / foam (Phase 2) ----------------------------
    public const string NutriOperation = "nutriOperation";
    public const string NutriMix = "nutriMix";
    public const string NutriOpCycle = "nutriOpCycle";
    public const string NutriMixCycle = "nutriMixCycle";
    public const string NutriIntensity = "nutriIntensity";

    public const string AntifoamOperation = "antifoamOperation";
    public const string AntifoamMix = "antifoamMix";
    public const string AntifoamIntensity = "antifoamIntensity";

    public const string DistanceSensorComm = "distanceSensorComm";
    public const string DistanceSensorReference = "distanceSensorReference";
    public const string FoamStartDelaySeconds = "foamStartDelay_s";
    public const string FoamPulseSeconds = "foamPulse_s";
    public const string FoamIntervalSeconds = "foamInterval_s";

    // ---- Agitator flask (Phase 2) ----------------------------------------
    public const string AgitatorAuto = "agitatorAuto";
    public const string AgitatorReEnablePot = "agitatorReEnablePot";
    public const string AgitatorPercent = "agitatorPercent";
    public const string AgitatorDir = "agitatorDir";
    public const string AgitatorOn = "agitatorOn";

    // ---- Biomass (Phase 3 WP1) -------------------------------------------
    public const string BiomassComm = "biomassComm";

    /// <summary>Momentary: capture the blank (zero-absorbance) reference.</summary>
    public const string Blank = "blank";
    /// <summary>Momentary: start the biomass acquisition loop.</summary>
    public const string BiomassStart = "start";

    /// <summary>Momentary: stop the biomass acquisition loop.</summary>
    public const string BiomassStop = "stop";

    public const string Low = "low";
    public const string High = "high";
    public const string Opt = "opt";

    // ---- ASDA-B2 servo drive telemetry node (Hub v9) ----------------------
    // Servo telemetry/configuration plus the mutually exclusive motor route selector.

    /// <summary>Routing for the servo node, persisted in the Hub's NVS.</summary>
    /// <remarks>
    /// The only routing flag that is born <c>true</c>, so the node comes up on its own
    /// when energised. A module that has no servo is told so once, with <c>0</c>, and
    /// remembers it - which is what keeps it from reporting a permanent phantom failure.
    /// </remarks>
    public const string ServoComm = "servoComm";

    /// <summary>Zeroes the energy accumulator kept on the node. Only the value 1 counts.</summary>
    public const string ResetServoEnergy = "resetServoEnergy";

    /// <summary>Modbus sampling interval on the node, 250-10000 ms.</summary>
    public const string ServoPollMs = "servoPollMs";

    // ---- External pump (Phase 3 WP2) -------------------------------------
    public const string PumpComm = "pumpComm";
    public const string Mode = "mode";

    /// <summary>
    /// Vestigial in the disable frame: v.6 sends <c>speed:0</c> on disable, but the
    /// firmware forwards only <c>pump_speed</c>, so it is a no-op the firmware ignores.
    /// Reproduced for byte-parity with v.6. See <c>docs/PROTOCOL.md</c> §3.5.
    /// </summary>
    public const string Speed = "speed";

    /// <summary>
    /// Runs the pump motor at a fixed internal speed <c>S</c> (0..1000) with no profile:
    /// <c>{"pump_speed":S}</c>. The Hub strips the prefix and the node takes <c>speed</c>
    /// as "idle mode, motor at S until told otherwise" — <c>0</c> stops it. This is the
    /// one command the volumetric calibration needs: hold S for a timed run, then measure.
    /// </summary>
    public const string PumpManualSpeed = "pump_speed";

    /// <summary>
    /// Optional deadline for <see cref="PumpManualSpeed"/>, in ms: the node stops the motor
    /// by itself when it elapses (pump 3.10+). A 3.9 node ignores it.
    /// </summary>
    public const string PumpManualSpeedMs = "pump_speed_ms";

    /// <summary>
    /// <c>{"pump_pot":1}</c> hands the motor back to the bench potentiometers and forgets any
    /// manual speed; <c>0</c> locks them out (pump 3.10+).
    /// </summary>
    public const string PumpPotentiometers = "pump_pot";

    /// <summary>Absolute start time of the profile, in minutes (<c>t' = t − init_t</c>).</summary>
    public const string InitT = "init_t";

    /// <summary>Absolute end time of the profile, in minutes.</summary>
    public const string FinalT = "final_t";

    public const string LambdaConst = "lambda_const";
    public const string LambdaLinear = "lambda_linear";
    public const string PhiLinear = "phi_linear";
    public const string LambdaExp = "lambda_exp";
    public const string PhiExp = "phi_exp";
    public const string NumSegments = "num_segments";

    /// <summary>Largest polynomial coefficient index the firmware forwards: <c>p0..p20</c>.</summary>
    public const int MaxPolynomialCoefficientIndex = 20;

    /// <summary>Largest piecewise segment count the firmware forwards: <c>t0..t99</c>/<c>q0..q99</c>.</summary>
    public const int MaxPiecewiseSegments = 100;

    /// <summary>Polynomial coefficient key <c>p{index}</c> (Phase 3 WP2, mode 4).</summary>
    public static string PolynomialCoefficient(int index) => "p" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Piecewise time-point key <c>t{index}</c>, in minutes (Phase 3 WP2, mode 5).</summary>
    public static string PiecewiseTime(int index) => "t" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Piecewise flow-point key <c>q{index}</c>, in mL/min (Phase 3 WP2, mode 5).</summary>
    public static string PiecewiseFlow(int index) => "q" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ---- External-node configuration (Hub 10.2) --------------------------
    // The Hub translates these keys before enqueuing to each node's reliable mailbox.
    public const string DistanceOffsetMm = "distanceOffsetMm";
    public const string DistanceSamplePeriodMs = "distanceSamplePeriodMs";
    public const string DistanceSendPeriodMs = "distanceSendPeriodMs";
    public const string DistanceResetNvs = "distanceResetNvs";

    public const string FlowKp = "flowKp";
    public const string FlowKi = "flowKi";
    public const string FlowFfGain = "flowFfGain";
    public const string FlowFfOffset = "flowFfOffset";
    public const string FlowRampRate = "flowRampRate";
    public const string FlowTransitionVoltage = "flowTransitionVoltage";

    /// <summary>
    /// Commands forwarded to the pump node's <c>command</c> field:
    /// <c>"start"</c>, <c>"stop"</c>, <c>"reset_volume"</c>, etc.
    /// Renamed to <c>command</c> by the Hub.
    /// </summary>
    public const string PumpCommand = "pump_command";
    public const string PumpPidKp = "pumpPidKp";
    public const string PumpPidKi = "pumpPidKi";
    public const string PumpPidKd = "pumpPidKd";
    public const string PumpTransitionSpeed = "pumpTransitionSpeed";
    public const string PumpA1 = "pumpA1";
    public const string PumpB1 = "pumpB1";
    public const string PumpK1 = "pumpK1";
    public const string PumpF1 = "pumpF1";
    public const string PumpC1 = "pumpC1";
    public const string PumpK2 = "pumpK2";
    public const string PumpF2 = "pumpF2";
    public const string PumpC2 = "pumpC2";

    public const string BiomassIt = "biomassIt";
    public const string BiomassPwm = "biomassPwm";
    public const string BiomassGear = "biomassGear";
    public const string BiomassEma = "biomassEma";
    public const string BiomassProbePeriodMs = "biomassProbePeriodMs";
    public const string BiomassAutoRange = "biomassAutoRange";

    /// <summary>Requests one cached node diagnostic or <c>all</c>; system/read-only command.</summary>
    public const string NodeDiag = "nodeDiag";
}

/// <summary>
/// Every JSON key the device sends in a telemetry frame.
/// See <c>docs/PROTOCOL.md</c> section 2.
/// </summary>
public static class TelemetryKeys
{
    public const string Temperature = "Tempval";
    public const string OxygenRaw = "Oxyval";
    public const string PHRaw = "pHval";
    public const string Pressure = "Pressure";
    public const string FlowRate = "FlowRate";
    public const string FlowSetpoint = "FlowSetpoint";
    public const string FlowVoltage = "FlowVoltage";
    public const string Antifoam = "Antifoam";
    public const string Distance = "Distance";
    public const string SensorCommOk = "SensorCommOK";

    public const string FlowmeterOnline = "FlowmeterOnline";
    public const string FlowControlEnabled = "FlowControlEnabled";

    /// <summary>
    /// The flowmeter node's own "keep looking for a hub" switch, mirrored by Hub v10.
    /// A node with it off looks exactly like one that is merely absent, which is the
    /// hardest flowmeter fault to diagnose from here.
    /// </summary>
    public const string FlowmeterReconnectWifi = "FlowmeterReconnectWifi";
    public const string FlowCommandPending = "FlowCommandPending";
    public const string FlowCommandSource = "FlowCommandSource";
    public const string Valve1 = "Valve1";
    public const string Valve2 = "Valve2";
    public const string ValveFlow = "ValveFlow";
    public const string FlowCommandId = "FlowCommandId";
    public const string FlowCommandAck = "FlowCommandAck";
    public const string FlowCommandDeliveries = "FlowCommandDeliveries";
    public const string FlowCommandAgeMs = "FlowCommandAgeMs";
    public const string HubStations = "HubStations";

    public const string BiomassAbs = "BiomassAbs";
    public const string BiomassRaw = "BiomassRaw";
    public const string BiomassIntegrationTime = "BiomassIT";
    public const string BiomassPwm = "BiomassPWM";

    public const string PumpFlow = "PumpFlow";
    public const string PumpVolume = "PumpVol";

    /// <summary>Profile mode the pump node reports running (1-5, 0 = idle).</summary>
    public const string PumpMode = "PumpMode";

    public const string PumpPwm = "PumpPWM";
    public const string PumpSpeed = "PumpSpeed";

    /// <summary>Volume the node's own profile integral expects by now, mL.</summary>
    public const string PumpTargetVolume = "PumpTargetVol";

    /// <summary>The node is inside its operating window and dosing.</summary>
    public const string PumpActive = "PumpActive";

    /// <summary>The node has a profile but is still before <c>init_t</c>.</summary>
    public const string PumpWaiting = "PumpWaiting";

    // ---- External-device presence and routing ----------------------------
    // Every external node gets the three states the flowmeter already publishes, so
    // "the operator switched it off", "the Hub is not routing to it" and "the node is
    // not there" stay distinguishable. See docs/PLANO_DISPOSITIVOS_EXTERNOS.md §2.
    //
    // A Hub that predates these keys simply omits them; TelemetryParser then falls back
    // to ageing out the device's value keys, so the app keeps working unflashed.

    /// <summary>Hub saw biomass telemetry inside its window.</summary>
    public const string BiomassOnline = "BiomassOnline";

    /// <summary>Hub echo of the <c>biomassComm</c> routing flag it persisted.</summary>
    public const string BiomassCommEnabled = "BiomassCommEnabled";

    /// <summary>A biomass command is queued and not yet acknowledged by the node.</summary>
    public const string BiomassCommandPending = "BiomassCommandPending";

    public const string PumpOnline = "PumpOnline";
    public const string PumpCommEnabled = "PumpCommEnabled";
    public const string PumpCommandPending = "PumpCommandPending";

    public const string DistanceOnline = "DistanceOnline";
    public const string DistanceCommEnabled = "DistanceCommEnabled";

    public const string AgitatorOnline = "AgitatorOnline";
    public const string AgitatorCommandPending = "AgitatorCommandPending";

    /// <summary>Magnitude the agitator node is actually driving, 0-100 %.</summary>
    public const string AgitatorPercent = "AgitatorPercent";

    /// <summary>Direction the node is actually driving: 1 CW, 0 CCW.</summary>
    public const string AgitatorDirection = "AgitatorDir";

    /// <summary>
    /// The node's potentiometer is live and can override whatever the app commanded.
    /// </summary>
    public const string AgitatorPotActive = "AgitatorPotActive";

    /// <summary>What last moved the agitator: <c>Pot</c>, <c>Hub</c>, <c>Wi-Fi</c> or <c>USB</c>.</summary>
    public const string AgitatorSource = "AgitatorSource";

    // ---- External-node identity (Hub 10.1) --------------------------------
    // Who is on the other end of each Wi-Fi link, as the Hub's node registry knows it:
    // the IP it extracted from the node's TCP connection, and the firmware version and
    // MAC the node declared in its /nodeHello. Additive keys; the protocol stays 10.
    //
    // *IP is in every frame from Hub 10.0.1 on, "0.0.0.0" meaning never seen. *NodeVer
    // and *NodeMac appear only once the node has registered, so their absence means
    // "not registered yet" (or a Hub older than 10.1), never "empty". TelemetryParser
    // keeps all three sticky within the link, like HubFirmwareVersion.

    public const string DistanceIP = "DistanceIP";
    public const string DistanceNodeVer = "DistanceNodeVer";
    public const string DistanceNodeMac = "DistanceNodeMac";

    public const string AgitatorIP = "AgitatorIP";
    public const string AgitatorNodeVer = "AgitatorNodeVer";
    public const string AgitatorNodeMac = "AgitatorNodeMac";

    public const string PumpIP = "PumpIP";
    public const string PumpNodeVer = "PumpNodeVer";
    public const string PumpNodeMac = "PumpNodeMac";

    public const string FlowmeterIP = "FlowmeterIP";
    public const string FlowmeterNodeVer = "FlowmeterNodeVer";
    public const string FlowmeterNodeMac = "FlowmeterNodeMac";

    public const string BiomassIP = "BiomassIP";
    public const string BiomassNodeVer = "BiomassNodeVer";
    public const string BiomassNodeMac = "BiomassNodeMac";
    public const string BathIP = "BathIP";
    public const string BathNodeVer = "BathNodeVer";
    public const string BathNodeMac = "BathNodeMac";

    // ---- External bath / thermal cascade (Hub 10.5.1) --------------------
    public const string BathOnline = "BathOnline";
    public const string BathCommEnabled = "BathCommEnabled";
    public const string BathCommandPending = "BathCommandPending";
    public const string BathCommandId = "BathCommandId";
    public const string BathCommandAck = "BathCommandAck";
    public const string TempControlMode = "TempControlMode";
    public const string TempControlViaBath = "TempControlViaBath";
    public const string BathCascadeEnabled = "BathCascadeEnabled";
    public const string BathCascadeState = "BathCascadeState";
    public const string BathCommandLatestWins = "BathCommandLatestWins";
    public const string BathCommandCompletionPending = "BathCommandCompletionPending";
    public const string BathCommandLastSentId = "BathCommandLastSentId";
    public const string BathCommandLastDoneId = "BathCommandLastDoneId";
    public const string BathCommandCompletionAgeMs = "BathCommandCompletionAgeMs";
    public const string TempSetpoint = "TempSetpoint";
    public const string BathSp = "BathSp";
    public const string BathTarget = "BathTarget";
    public const string BathPv = "BathPv";
    public const string BathDisplaySp = "BathDisplaySp";
    public const string BathState = "BathState";
    public const string BathPhase = "BathPhase";
    public const string BathError = "BathError";
    public const string BathMode = "BathMode";
    public const string BathGuard = "BathGuard";
    public const string BathDeviation = "BathDeviation";
    public const string BathSpSource = "BathSpSource";
    public const string BathCommandSetpoint = "BathCommandSetpoint";
    public const string BathCommandConfirmed = "BathCommandConfirmed";
    public const string BathCascadeError = "BathCascadeError";
    public const string BathCascadePvFiltered = "BathCascadePvFiltered";
    public const string BathCascadeP = "BathCascadeP";
    public const string BathCascadeI = "BathCascadeI";
    public const string BathCascadeSaturated = "BathCascadeSaturated";
    public const string BathCascadePausedReason = "BathCascadePausedReason";
    public const string BathCascadeLastUpdateMs = "BathCascadeLastUpdateMs";

    // ---- Hub identity (v9) -----------------------------------------------

    /// <summary>Hub firmware build, e.g. <c>9.1.0-dev</c>. Diagnostic only.</summary>
    /// <remarks>
    /// Never negotiate on this. It changes for reasons that do not touch the wire - the
    /// CN1 correction in 9.1.0 altered what the module receives without moving a single
    /// key - so it belongs in a log header, not in a branch.
    /// </remarks>
    public const string HubFirmwareVersion = "HubFirmwareVersion";

    /// <summary>Wire contract version. <b>This</b> is the negotiation key.</summary>
    public const string HubProtocolVersion = "HubProtocolVersion";

    // ---- ASDA-B2 servo drive (Hub v9) ------------------------------------
    // These split into two groups that behave differently, and the split governs the
    // whole parser.
    //
    // The four below are published in EVERY frame, node present or not. They are what
    // lets "this module has no servo" be told apart from "the servo went missing".

    /// <summary>A valid push arrived inside the Hub's 6000 ms window.</summary>
    public const string ServoOnline = "ServoOnline";

    /// <inheritdoc cref="BiomassCommEnabled"/>
    public const string ServoCommEnabled = "ServoCommEnabled";

    public const string ServoCommandPending = "ServoCommandPending";

    /// <summary>Depth of the Hub's fixed FIFO of eight, 0-8.</summary>
    /// <remarks>
    /// The servo command link has no acknowledgement, so this and
    /// <see cref="ServoCommandPending"/> are the only observation of a command's life:
    /// it entering the queue and being consumed. A full queue drains at one per 2 s
    /// pull, so sixteen seconds is the floor before concluding anything failed.
    /// </remarks>
    public const string ServoCommandQueueDepth = "ServoCommandQueueDepth";

    /// <summary>Hub echo of motorControlMode: true = Modbus; false = UART/CN1.</summary>
    public const string MotorControlViaModbus = "MotorControlViaModbus";

    /// <summary>Route confirmed by the driver: -1 unknown, 0 UART/CN1, 1 Modbus.</summary>
    public const string ServoMotorRouteAck = "ServoMotorRouteAck";

    // The ten below appear ONLY when there is a publishable sample - fresh presence AND
    // routing on. Otherwise the keys are simply absent from the JSON, and absence is not
    // zero: ServoRpm 0.0 is a stopped motor, a missing ServoRpm is no data at all.

    /// <summary>Measured shaft speed. Zero is a legitimate reading.</summary>
    public const string ServoRpm = "ServoRpm";

    /// <summary>Instantaneous torque feedback, signed, as a percentage of rated.</summary>
    public const string ServoTorquePct = "ServoTorquePct";

    /// <summary>
    /// Torque in N·m, derived on the node as <c>torque% × rated torque</c>.
    /// </summary>
    /// <remarks>
    /// The drive does not measure N·m. It reports a fraction of rated torque, and the
    /// node multiplies by the motor's nameplate figure - 1.27 N·m for the ECMA-C20604ES.
    /// Every N·m and watt scales linearly with that constant, so a motor swap without a
    /// firmware change produces plausible wrong numbers with no error anywhere.
    /// </remarks>
    public const string ServoTorqueNm = "ServoTorqueNm";

    /// <summary>Average load rate, whole percent. Not the same quantity as torque.</summary>
    public const string ServoLoadPct = "ServoLoadPct";

    /// <summary>Estimated mechanical shaft power, <c>T·ω</c>. Not electrical draw.</summary>
    public const string ServoPowerW = "ServoPowerW";

    /// <summary>Mechanical energy integrated on the node. May go backwards.</summary>
    /// <remarks>
    /// Integration lives on the node because it needs the continuous 1 Hz series. It
    /// resets when the node reboots and on <see cref="CommandKeys.ResetServoEnergy"/>,
    /// and it refuses to integrate across gaps rather than inventing energy that was
    /// never measured. Charts must survive a series that steps down.
    /// </remarks>
    public const string ServoEnergyWh = "ServoEnergyWh";

    /// <summary>Drive state from P0-46: 0 OFF, 1 READY, 2 SON, 3 ALARM.</summary>
    public const string ServoState = "ServoState";

    /// <summary>Raw P0-01 alarm code.</summary>
    /// <remarks>
    /// The hexadecimal digits mirror the number on the drive's panel: <c>0x0011</c> is
    /// the panel's <c>AL011</c>. It is not a decimal code, and rendering it as one
    /// produces a number that matches nothing in the manual. Verified on the bench
    /// against a real AL011 (encoder disconnected).
    /// </remarks>
    public const string ServoAlarm = "ServoAlarm";

    /// <summary>Successful Modbus transactions; grows by three per accepted sample.</summary>
    public const string ServoCommOk = "ServoCommOk";

    /// <summary>Failed Modbus samples. Alarm on the rate, never on the total.</summary>
    public const string ServoCommErr = "ServoCommErr";

    // ---- External-node configuration echoes (Hub 10.2) -------------------
    public const string DistanceOffsetMm = "DistanceOffsetMm";
    public const string DistanceSamplePeriodMs = "DistanceSamplePeriodMs";
    public const string DistanceSendPeriodMs = "DistanceSendPeriodMs";
    public const string DistanceCommandPending = "DistanceCommandPending";

    public const string FlowKp = "FlowKp";
    public const string FlowKi = "FlowKi";
    public const string FlowFfGain = "FlowFfGain";
    public const string FlowFfOffset = "FlowFfOffset";
    public const string FlowRampRate = "FlowRampRate";
    public const string FlowOutput = "FlowOutput";
    public const string FlowSetpointCorrected = "FlowSetpointCorrected";
    public const string FlowmeterBootId = "FlowmeterBootId";
    public const string FlowTransitionVoltage = "FlowTransitionVoltage";
    public const string FlowmeterCalCrc = "FlowmeterCalCrc";


    // Pump 3.10 echoes (Hub 2026-09-12): PID gains, whether the bench potentiometers are in
    // command, and the volume delivered by the current profile cycle.
    public const string PumpPidKp = "PumpPidKp";
    public const string PumpPidKi = "PumpPidKi";
    public const string PumpPidKd = "PumpPidKd";
    public const string PumpPotEnabled = "PumpPotEnabled";
    public const string PumpCycleVolume = "PumpCycleVol";

    public const string PumpTransitionSpeed = "PumpTransitionSpeed";
    public const string PumpCalCrc = "PumpCalCrc";
    public const string PumpA1 = "PumpA1";
    public const string PumpB1 = "PumpB1";
    public const string PumpK1 = "PumpK1";
    public const string PumpF1 = "PumpF1";
    public const string PumpC1 = "PumpC1";
    public const string PumpK2 = "PumpK2";
    public const string PumpF2 = "PumpF2";
    public const string PumpC2 = "PumpC2";

    public const string BiomassGear = "BiomassGear";
    public const string BiomassEma = "BiomassEma";
    public const string BiomassProbePeriodMs = "BiomassProbePeriodMs";

    public const string Time = "Time";
}
