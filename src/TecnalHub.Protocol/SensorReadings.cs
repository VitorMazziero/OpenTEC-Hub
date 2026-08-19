namespace TecnalHub.Protocol;

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
    public double FlowVoltage { get; set; }
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

    /// <summary>True while the ESP32 reports its internal sensor-module UART healthy.</summary>
    public bool SensorCommOk { get; set; } = true;

    public double PumpFlow { get; set; } = NotReceived;
    public double PumpVolume { get; set; } = NotReceived;

    /// <summary>Seconds since controller boot, as reported by the device.</summary>
    public double TimeRawSeconds { get; set; }

    /// <summary>User-zeroed offset in minutes, subtracted from the device clock.</summary>
    public double TimeOffsetMinutes { get; set; }

    /// <summary>Elapsed run time in minutes, after the user's zero offset.</summary>
    public double TimeMinutes => Math.Round((TimeRawSeconds / 60.0) - TimeOffsetMinutes, 2);

    /// <summary>When a frame last carried a Distance key. Null before the first one.</summary>
    internal DateTimeOffset? DistanceLastSeenAt { get; set; }

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
        SensorCommOk = SensorCommOk,
        PumpFlow = PumpFlow,
        PumpVolume = PumpVolume,
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
    public bool SensorCommOk { get; init; }
    public double PumpFlow { get; init; }
    public double PumpVolume { get; init; }
    public double TimeRawSeconds { get; init; }
    public double TimeMinutes { get; init; }
}
