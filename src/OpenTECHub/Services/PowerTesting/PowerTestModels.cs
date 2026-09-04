using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenTECHub.Services.PowerTesting;

// Domain for the impeller power assay (aba "Potência"). Mirrors the KlaTesting domain
// in shape and conventions; the science lives in docs/PLANO_ENSAIOS_POTENCIA_IMPELIDOR.md.
// All rotations are rpm, torque is a signed % of motor nominal (and its N·m image),
// power is the mechanical shaft estimate P = τ·ω — NOT electrical draw (§4.1, §4.8).

public enum PowerTestStatus
{
    Draft,
    Running,
    Interrupted,
    Completed,
}

/// <summary>
/// The per-condition state machine (§12). Simpler than the kLa runner: no deoxygenation
/// or nitrogen. Capture is two-gate adaptive — <see cref="SettlingTorque"/> is stationarity
/// (Porta 1) and <see cref="AccumulatingToTarget"/> is precision (Porta 2, §12.1).
/// </summary>
public enum PowerRunPhase
{
    Idle,
    Preflight,
    PreparingCondition,
    SettingSpeed,
    VentStabilizing,
    OpeningGas,
    SettlingTorque,
    AccumulatingToTarget,
    PausedForMeasurement,
    HoldingForManualEnergy,
    Captured,
    StoppingRun,
    Reviewing,
    Accepted,
    Rejected,
    PreparingNextRun,
    Completed,
    Aborting,
    Faulted,
}

/// <summary>What a condition row asks the runner to measure (§2, §7.3). P_G is never forced.</summary>
public enum PowerGasMode
{
    Ungassed,
    Gassed,
    Both,
    SinglePoint,
}

/// <summary>Preloaded impeller families (§8). <see cref="Custom"/> is anything else.</summary>
public enum ImpellerType
{
    RushtonFlatBlade,
    MarinePropeller,
    ElephantEar,
    SmithConcaveBlade,
    Custom,
}

public enum PowerConditionOrigin
{
    Manual,
    Map,
}

public enum PowerConditionStatus
{
    Pending,
    InProgress,
    Completed,
    Skipped,
}

/// <summary>Why a capture ended (§12.1). Only <see cref="Target"/> is a clean stop.</summary>
public enum PowerStopReason
{
    Target,
    Tmax,
    NotConverged,
    Aborted,
}

/// <summary>How the operator entered a condition's gas flow (§11). L/min is always the stored primary.</summary>
public enum FlowInputUnit
{
    Lpm,
    Vvm,
}

/// <summary>Liquid properties, one value per test with the measured temperature logged per point (§8, §13).</summary>
public sealed record FluidProperties
{
    public double DensityKgM3 { get; init; } = 998.0;   // água ~20-25 °C
    public double ViscosityPaS { get; init; } = 0.001;  // água ~20 °C
    public double TemperatureC { get; init; } = 25.0;
    public string? PresetName { get; init; } = "Água";
}

/// <summary>
/// One impeller on the shaft. A test carries a <see cref="PowerGeometry.Impellers"/> list because
/// mixed configurations (different diameters per stage) are the normal case here (§4.3, §8).
/// </summary>
public sealed class Impeller
{
    public ImpellerType Type { get; set; } = ImpellerType.RushtonFlatBlade;
    public string Label { get; set; } = "";
    public double DiameterM { get; set; }
    public int BladeCount { get; set; }
    public double ClearanceM { get; set; }

    /// <summary>Position on the shaft, 0 = bottom.</summary>
    public int StageIndex { get; set; }

    /// <summary>Reference power number for the literature overlay (§15). Editable; not a measurement.</summary>
    public double? LiteratureNp { get; set; }

    public Impeller Clone() => new()
    {
        Type = Type,
        Label = Label,
        DiameterM = DiameterM,
        BladeCount = BladeCount,
        ClearanceM = ClearanceM,
        StageIndex = StageIndex,
        LiteratureNp = LiteratureNp,
    };
}

public sealed class PowerGeometry
{
    public List<Impeller> Impellers { get; set; } = [];
    /// <summary>Vessel / tank inner diameter T [m] (§8, §16). Defaults to 0.190 m (190 mm), editable.</summary>
    public double VesselDiameterM { get; set; } = 0.190;

    /// <summary>Working liquid volume. Required when a condition's flow is given in vvm (§11).</summary>
    public double LiquidVolumeM3 { get; set; }
    public bool Baffled { get; set; } = true;

    public PowerGeometry Clone() => new()
    {
        Impellers = Impellers.Select(i => i.Clone()).ToList(),
        VesselDiameterM = VesselDiameterM,
        LiquidVolumeM3 = LiquidVolumeM3,
        Baffled = Baffled,
    };
}

/// <summary>
/// Tunables for the sweep, the two-gate adaptive stop and safety (§12.1, §14). Immutable record;
/// a change bumps <see cref="PowerTestDocument.SettingsRevision"/>.
/// </summary>
public sealed record PowerTestSettings
{
    // Rotação — contrato atual do Hub/CN1: 15-1000 rpm; zero desabilita o motor.
    public double MinRpm { get; init; } = 15.0;
    public double MaxRpm { get; init; } = 1000.0;
    public double DefaultStepRpm { get; init; } = 50.0;
    public double MinStepRpm { get; init; } = 5.0;

    /// <summary>Measured-speed band and confirmation count before torque settling begins.</summary>
    public double SpeedToleranceRpm { get; init; } = 5.0;
    public int SpeedStableSamples { get; init; } = 3;
    public double MaxSpeedSettlingSeconds { get; init; } = 120.0;

    /// <summary>Torque guard as % of nominal; exceeding it interrupts the condition (§14).</summary>
    public double MaxTorquePercent { get; init; } = 90.0;

    // Porta 1 — estacionariedade (§12.1): a média de τ parou de derivar.
    public double StationarityWindowSeconds { get; init; } = 20.0;
    public double StationaritySlopeTolerancePercentPerSecond { get; init; } = 0.5;
    public int StationarityRequiredSamples { get; init; } = 5;

    // Porta 2 — precisão (§12.1): IC95 ≤ max(relativo, piso do σ_τ), o que vier primeiro.
    public double RelativeCiFraction { get; init; } = 0.02;   // ±2 % da média
    public double CiFloorSigmaMultiple { get; init; } = 1.0;  // piso absoluto ~ σ_τ da tara
    public int MinSamples { get; init; } = 60;                // n_min
    public double MaxCaptureSeconds { get; init; } = 300.0;   // t_max
    public int MaxTries { get; init; } = 3;

    /// <summary>Servo-node polling used during a capture and restored at every terminal path.</summary>
    public int CaptureServoPollMs { get; init; } = 250;
    public int RestoreServoPollMs { get; init; } = 1000;

    /// <summary>Maximum age of the last valid servo frame before the run pauses.</summary>
    public double MeasurementTimeoutSeconds { get; init; } = 5.0;

    /// <summary>SNR gate (§7.2): a net power below this multiple of the tare noise floor is "below noise".</summary>
    public double SnrFloorMultiple { get; init; } = 3.0;

    // Estabilização no alívio (§13), montagem opcional.
    public bool VentStabilizationEnabled { get; init; }
    public double VentFlowToleranceLpm { get; init; } = 0.2;
    public int VentFlowStableSamples { get; init; } = 5;
    public double VentAgitationRpm { get; init; } = 15.0;
    public double MaxVentStabilizationSeconds { get; init; } = 120.0;

    /// <summary>Hold each captured point for a manual mains-wattmeter reading (§4.8, §12.3).</summary>
    public bool ManualEnergyCaptureEnabled { get; init; }

    /// <summary>Auto-accept a condition when replicate spread is within the CIs (§16).</summary>
    public bool AutoAcceptRuns { get; init; }
}

/// <summary>One planned row of the conditions table (§7.3). Gas is optional and per-row.</summary>
public sealed class PowerCondition
{
    public Guid ConditionId { get; set; } = Guid.NewGuid();
    public int OrderIndex { get; set; }
    public double AgitationRpm { get; set; }

    /// <summary>Stored primary gas flow, always L/min (§11). Null for ungassed rows.</summary>
    public double? GasFlowLpm { get; set; }

    /// <summary>Convenience echo when the row was entered in vvm; L/min stays authoritative.</summary>
    public double? GasFlowVvm { get; set; }
    public FlowInputUnit FlowUnit { get; set; } = FlowInputUnit.Lpm;
    public PowerGasMode GasMode { get; set; } = PowerGasMode.Ungassed;
    public int RequestedReplicates { get; set; } = 1;
    public int CompletedReplicates { get; set; }
    public int AcceptedReplicates { get; set; }
    public int RejectedReplicates { get; set; }
    public PowerConditionOrigin Origin { get; set; } = PowerConditionOrigin.Manual;
    public Guid? SourceMapId { get; set; }
    public string? SourceMapName { get; set; }
    public PowerConditionStatus Status { get; set; } = PowerConditionStatus.Pending;
    public bool HasReplicateDisagreement { get; set; }
    public string? ReproducibilityWarning { get; set; }

    public PowerCondition Clone() => new()
    {
        ConditionId = ConditionId,
        OrderIndex = OrderIndex,
        AgitationRpm = AgitationRpm,
        GasFlowLpm = GasFlowLpm,
        GasFlowVvm = GasFlowVvm,
        FlowUnit = FlowUnit,
        GasMode = GasMode,
        RequestedReplicates = RequestedReplicates,
        CompletedReplicates = CompletedReplicates,
        AcceptedReplicates = AcceptedReplicates,
        RejectedReplicates = RejectedReplicates,
        Origin = Origin,
        SourceMapId = SourceMapId,
        SourceMapName = SourceMapName,
        Status = Status,
        HasReplicateDisagreement = HasReplicateDisagreement,
        ReproducibilityWarning = ReproducibilityWarning,
    };
}

/// <summary>
/// Static torque calibration (§9.1). Phase 1 uses one point (scale only, offset 0); the
/// <see cref="Offset"/> field is kept for a future two-point calibration.
/// </summary>
public sealed record TorqueCalibration
{
    public double Scale { get; init; } = 1.0;
    public double Offset { get; init; }
    public double ReferenceNm { get; init; }
    public double ReferenceMassKg { get; init; }
    public double LeverArmM { get; init; }
    public double MotorRatedTorqueNm { get; init; } = 1.27;   // ECMA-C20604ES, placa
    public DateTimeOffset CalibratedUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>One rung of the tare curve: void power and the torque noise floor at that rpm (§4.2, §9.2).</summary>
public sealed record TarePoint(double Rpm, double PVoidW, double SigmaTauPercent);

public sealed record TareCurve
{
    public List<TarePoint> Points { get; init; } = [];

    /// <summary>Identifies the impeller set the tare belongs to; a mismatch warns before reuse (§9.2).</summary>
    public string ImpellerSetHash { get; init; } = "";
    public DateTimeOffset MeasuredUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>An operator-entered mains-wattmeter reading paired to a captured point (§4.8, §12.3).</summary>
public sealed record ManualElecReading
{
    public double PowerElectricalW { get; init; }
    public double PowerMechanicalWAtReading { get; init; }
    public double Rpm { get; init; }
    public bool GasOpen { get; init; }
    public string? Instrument { get; init; }
    public string? Note { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Reference to a kLa map for the phase-3 P/V cross-link (§4.7). Kept minimal for now.</summary>
public sealed record PowerMapReference
{
    public Guid MapId { get; init; }
    public string MapName { get; init; } = "";
    public string? MapFingerprint { get; init; }
    public DateTimeOffset LinkedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>One replicate capture at one condition, with its adaptive-stop summary (§12.1).</summary>
public sealed class PowerRun
{
    public Guid RunId { get; set; } = Guid.NewGuid();
    public Guid TestId { get; set; }
    public Guid ConditionId { get; set; }
    public int ReplicateNumber { get; set; } = 1;
    public string FolderName { get; set; } = "";
    public double AgitationRpm { get; set; }
    public double? GasFlowLpm { get; set; }
    public PowerGasMode GasMode { get; set; } = PowerGasMode.Ungassed;
    public bool UsedVentStabilization { get; set; }
    public PowerRunPhase CurrentPhase { get; set; } = PowerRunPhase.Idle;

    // Resumo da captura — a média em regime e a precisão atingida.
    public int SampleCount { get; set; }
    public double MeanRpmMeasured { get; set; }
    public double MeanTorquePercent { get; set; }
    public double MeanTorqueNm { get; set; }
    public double MeanShaftPowerW { get; set; }

    /// <summary>Shaft power minus the tare P_void(N); equals shaft power in relative mode (§4.2).</summary>
    public double NetPowerW { get; set; }
    public double TorqueCi95Percent { get; set; }
    public double Ci95PowerW { get; set; }
    public PowerPointResult? Analysis { get; set; }
    public PowerStopReason StopReason { get; set; } = PowerStopReason.Aborted;
    public int Tries { get; set; } = 1;

    /// <summary>True when run without calibration/tare — results are relative, not absolute (§9).</summary>
    public bool IsRelative { get; set; }
    public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedUtc { get; set; }
    public string? RawDataPath { get; set; }
    public string? RawDataSha256 { get; set; }
    public ManualElecReading? ManualElec { get; set; }
}

public sealed record PowerRunSummary
{
    public Guid RunId { get; init; }
    public Guid ConditionId { get; init; }
    public int ReplicateNumber { get; init; }
    public string FolderName { get; init; } = "";
    public double AgitationRpm { get; init; }
    public double? GasFlowLpm { get; init; }
    public PowerGasMode GasMode { get; init; }
    public PowerRunPhase Phase { get; init; }
    public PowerStopReason StopReason { get; init; }
    public int SampleCount { get; init; }
    public double MeanRpmMeasured { get; init; }
    public double MeanTorquePercent { get; init; }
    public double MeanTorqueNm { get; init; }
    public double MeanShaftPowerW { get; init; }
    public double? NetPowerW { get; init; }
    public double? TorqueCi95Percent { get; init; }
    public double? Ci95PowerW { get; init; }
    public PowerPointResult? Analysis { get; init; }
    public int Tries { get; init; }
    public bool IsRelative { get; init; }
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; init; }
}

/// <summary>The per-test manifest (ensaio.json). Self-contained; independent of any cultivation session (§6).</summary>
public sealed class PowerTestDocument
{
    public int SchemaVersion { get; set; } = 1;
    public Guid TestId { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string FolderName { get; set; } = "";
    public PowerTestStatus Status { get; set; } = PowerTestStatus.Draft;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? LastModifiedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }

    public FluidProperties Fluid { get; set; } = new();
    public PowerGeometry Geometry { get; set; } = new();
    public PowerTestSettings Settings { get; set; } = new();
    public int SettingsRevision { get; set; } = 1;

    public TorqueCalibration? Calibration { get; set; }
    public TareCurve? Tare { get; set; }

    /// <summary>Run without tare/calibration; results labelled relative, not absolute (§9, §20.5).</summary>
    public bool RelativeMode { get; set; }
    public PowerMapReference? LinkedMap { get; set; }

    public string AppVersion { get; set; } = "";
    public string ProtocolVersion { get; set; } = "OpenTEC_ESP32_v9 + servo ASDA-B2";
    public string? HubFirmwareVersion { get; set; }
    public int? HubProtocolVersion { get; set; }
    public string AlgorithmVersion { get; set; } = "Np_plateau_v1";

    /// <summary>Motor nominal torque assumed for the N·m image; needed to reinterpret the data later (§6).</summary>
    public double MotorRatedTorqueNm { get; set; } = 1.27;

    public List<PowerCondition> Conditions { get; set; } = [];
    public List<PowerRunSummary> Runs { get; set; } = [];
    public string? InterruptionReason { get; set; }
    public Dictionary<string, string> FileHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record PowerTestSummary(
    string FolderName,
    string Name,
    Guid TestId,
    PowerTestStatus Status,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? CompletedUtc,
    bool RelativeMode,
    int ConditionCount,
    int CompletedRunCount,
    int AcceptedRunCount);

/// <summary>One raw sample inside a run's dados-brutos.csv (§6).</summary>
public sealed record PowerDataPoint(
    DateTimeOffset TimestampUtc,
    double RelativeSeconds,
    PowerRunPhase Phase,
    double RpmMeasured,
    double TorquePercent,
    double TorqueNm,
    double ShaftPowerW,
    double? FlowLpm,
    bool Counted,
    int Attempt = 1);

/// <summary>One row of the test-wide serie-global.csv, live during the whole assay (§6).</summary>
public sealed record PowerGlobalSeriesSample(
    DateTimeOffset TimestampUtc,
    double MonotonicSeconds,
    Guid TestId,
    Guid? RunId,
    Guid? ConditionId,
    int? Replicate,
    PowerRunPhase Phase,
    double RpmMeasured,
    double TorquePercent,
    double TorqueNm,
    double ShaftPowerW,
    double? FlowLpm,
    double? TemperatureC,
    double? RunningMeanPowerW,
    double? RunningCi95PowerW,
    int SampleCount,
    int SettingsRevision,
    string EventCode,
    string EventDetail,
    int Attempt = 1);

public sealed record PowerTestEventLogEntry(
    DateTimeOffset TimestampUtc,
    string EventType,
    string Message,
    string? Details = null);

/// <summary>The four impeller families the registry ships with (§8). Np values are editable references.</summary>
public static class PowerImpellerCatalog
{
    public static IReadOnlyList<Impeller> Defaults => new[]
    {
        new Impeller { Type = ImpellerType.RushtonFlatBlade, Label = "Rushton (pás planas)", BladeCount = 6, LiteratureNp = 5.0 },
        new Impeller { Type = ImpellerType.MarinePropeller, Label = "Hélice marinha", BladeCount = 3, LiteratureNp = 0.35 },
        new Impeller { Type = ImpellerType.ElephantEar, Label = "Orelha de elefante", BladeCount = 3, LiteratureNp = 1.3 },
        new Impeller { Type = ImpellerType.SmithConcaveBlade, Label = "Smith (pás côncavas / CD-6)", BladeCount = 6, LiteratureNp = 4.1 },
    };

    public static Impeller Create(ImpellerType type) =>
        Defaults.FirstOrDefault(i => i.Type == type)?.Clone()
        ?? new Impeller { Type = ImpellerType.Custom, Label = "Personalizado" };
}
