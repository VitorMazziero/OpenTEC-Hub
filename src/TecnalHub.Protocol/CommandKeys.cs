namespace TecnalHub.Protocol;

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
    public const string MotorSetpoint = "motorSetpoint";
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

    // ---- Flow calibration: two-segment curve split at 0.0545 V ------------
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

    // ---- Biomass (Phase 3) -----------------------------------------------
    public const string BiomassComm = "biomassComm";
    public const string Blank = "blank";
    public const string Low = "low";
    public const string High = "high";
    public const string Opt = "opt";

    // ---- External pump (Phase 3) -----------------------------------------
    public const string PumpComm = "pumpComm";
    public const string Mode = "mode";
    public const string Speed = "speed";
    public const string LambdaConst = "lambda_const";
    public const string LambdaLinear = "lambda_linear";
    public const string PhiLinear = "phi_linear";
    public const string LambdaExp = "lambda_exp";
    public const string PhiExp = "phi_exp";
    public const string NumSegments = "num_segments";
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

    public const string Time = "Time";
}
