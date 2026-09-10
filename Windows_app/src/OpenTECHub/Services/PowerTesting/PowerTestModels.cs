using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTECHub.Services.Persistence;

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
    PausedByOperator,
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

public enum P0Provenance
{
    None,
    PlateauFit,
    MeasuredUngassed,
}

public enum FloodingDetectionMethod
{
    Automatic,
    ManualAdjusted,
}

public enum PowerVentValve
{
    Valve1 = 1,
    Valve2 = 2,
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
public sealed class Impeller : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private ImpellerType _type = ImpellerType.RushtonFlatBlade;
    public ImpellerType Type
    {
        get => _type;
        set { if (_type != value) { _type = value; OnPropertyChanged(); } }
    }

    private string _label = "";
    public string Label
    {
        get => _label;
        set { if (_label != value) { _label = value; OnPropertyChanged(); } }
    }

    private double _diameterM;
    public double DiameterM
    {
        get => _diameterM;
        set { if (!double.Equals(_diameterM, value)) { _diameterM = value; OnPropertyChanged(); } }
    }

    private int _bladeCount;
    public int BladeCount
    {
        get => _bladeCount;
        set { if (_bladeCount != value) { _bladeCount = value; OnPropertyChanged(); } }
    }

    private double _clearanceM;
    public double ClearanceM
    {
        get => _clearanceM;
        set { if (!double.Equals(_clearanceM, value)) { _clearanceM = value; OnPropertyChanged(); } }
    }

    /// <summary>Position on the shaft, 0 = bottom.</summary>
    private int _stageIndex;
    public int StageIndex
    {
        get => _stageIndex;
        set { if (_stageIndex != value) { _stageIndex = value; OnPropertyChanged(); } }
    }

    /// <summary>Reference power number for the literature overlay (§15). Editable; not a measurement.</summary>
    private double? _literatureNp;
    public double? LiteratureNp
    {
        get => _literatureNp;
        set { if (!Nullable.Equals(_literatureNp, value)) { _literatureNp = value; OnPropertyChanged(); } }
    }

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

    // Vazão de ar (Qg) — faixa padrão 0 (sem aeração) a 20 L/min, passo 0.5 L/min.
    public double MinFlowLpm { get; init; } = 0.0;
    public double MaxFlowLpm { get; init; } = 20.0;
    public double DefaultStepFlowLpm { get; init; } = 0.5;

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
    public PowerVentValve SelectedVentValve { get; init; } = PowerVentValve.Valve2;
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
public sealed class PowerCondition : INotifyPropertyChanged
{
    private Func<double>? _liquidVolumeLProvider;
    private Action<string>? _onVvmValidationFailed;

    /// <summary>
    /// Attaches the UI-specific liquid-volume context used only for editing the L/min/vvm echo.
    /// The callbacks are deliberately instance-scoped: conditions can belong to different tests,
    /// windows, or test fixtures at the same time without overwriting each other's conversion state.
    /// They are runtime helpers and are not part of the persisted assay contract.
    /// </summary>
    public void ConfigureFlowConversion(
        Func<double>? liquidVolumeLProvider,
        Action<string>? onVvmValidationFailed = null)
    {
        _liquidVolumeLProvider = liquidVolumeLProvider;
        _onVvmValidationFailed = onVvmValidationFailed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public Guid ConditionId { get; set; } = Guid.NewGuid();

    private int _orderIndex;
    public int OrderIndex
    {
        get => _orderIndex;
        set { if (_orderIndex != value) { _orderIndex = value; OnPropertyChanged(); } }
    }

    private double _agitationRpm;
    public double AgitationRpm
    {
        get => _agitationRpm;
        set { if (!double.Equals(_agitationRpm, value)) { _agitationRpm = value; OnPropertyChanged(); } }
    }

    private double? _gasFlowLpm;
    /// <summary>Stored primary gas flow, always L/min (§11). 0 or null for ungassed rows.</summary>
    public double? GasFlowLpm
    {
        get => _gasFlowLpm;
        set
        {
            if (!Nullable.Equals(_gasFlowLpm, value))
            {
                _gasFlowLpm = value;
                OnPropertyChanged();
                if (value.HasValue && value.Value > 0.0001)
                {
                    if (GasMode == PowerGasMode.Ungassed)
                    {
                        GasMode = PowerGasMode.Gassed;
                    }
                }
                else if (!value.HasValue || value.Value <= 0.0001)
                {
                    if (GasMode == PowerGasMode.Gassed)
                    {
                        // A flow edit to zero is still a measured/entered zero. Update the
                        // mode without routing through its setter, which intentionally clears
                        // flow values when the operator explicitly selects "Ungassed".
                        _gasMode = PowerGasMode.Ungassed;
                        OnPropertyChanged(nameof(GasMode));
                    }
                }
                var vol = _liquidVolumeLProvider?.Invoke() ?? 0;
                if (vol > 0 && value.HasValue)
                {
                    _gasFlowVvm = Math.Round(value.Value / vol, 4);
                    OnPropertyChanged(nameof(GasFlowVvm));
                }
                else if (!value.HasValue)
                {
                    _gasFlowVvm = null;
                    OnPropertyChanged(nameof(GasFlowVvm));
                }
            }
        }
    }

    private double? _gasFlowVvm;
    /// <summary>Convenience echo when the row was entered in vvm; L/min stays authoritative.</summary>
    public double? GasFlowVvm
    {
        get => _gasFlowVvm;
        set
        {
            if (value.HasValue && _liquidVolumeLProvider is not null && _liquidVolumeLProvider.Invoke() <= 0)
            {
                _onVvmValidationFailed?.Invoke("Volume útil não preenchido: não é possível converter vvm em L/min sem o volume do líquido.");
            }

            if (!Nullable.Equals(_gasFlowVvm, value))
            {
                _gasFlowVvm = value;
                OnPropertyChanged();
                if (value.HasValue && value.Value > 0.0001)
                {
                    if (GasMode == PowerGasMode.Ungassed)
                    {
                        GasMode = PowerGasMode.Gassed;
                    }
                }
                else if (!value.HasValue || value.Value <= 0.0001)
                {
                    if (GasMode == PowerGasMode.Gassed)
                    {
                        _gasMode = PowerGasMode.Ungassed;
                        OnPropertyChanged(nameof(GasMode));
                    }
                }
                var vol = _liquidVolumeLProvider?.Invoke() ?? 0;
                if (vol > 0 && value.HasValue)
                {
                    _gasFlowLpm = Math.Round(value.Value * vol, 3);
                    OnPropertyChanged(nameof(GasFlowLpm));
                }
                else if (!value.HasValue && FlowUnit == FlowInputUnit.Vvm)
                {
                    _gasFlowLpm = null;
                    OnPropertyChanged(nameof(GasFlowLpm));
                }
            }
        }
    }

    private FlowInputUnit _flowUnit = FlowInputUnit.Lpm;
    public FlowInputUnit FlowUnit
    {
        get => _flowUnit;
        set { if (_flowUnit != value) { _flowUnit = value; OnPropertyChanged(); } }
    }

    private PowerGasMode _gasMode = PowerGasMode.Ungassed;
    public PowerGasMode GasMode
    {
        get => _gasMode;
        set
        {
            if (_gasMode != value)
            {
                _gasMode = value;
                OnPropertyChanged();
                if (value == PowerGasMode.Ungassed)
                {
                    _gasFlowLpm = null;
                    _gasFlowVvm = null;
                    OnPropertyChanged(nameof(GasFlowLpm));
                    OnPropertyChanged(nameof(GasFlowVvm));
                }
            }
        }
    }

    private int _requestedReplicates = 1;
    public int RequestedReplicates
    {
        get => _requestedReplicates;
        set
        {
            if (_requestedReplicates != value)
            {
                _requestedReplicates = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ReplicatesDisplay));
            }
        }
    }

    private int _completedReplicates;
    public int CompletedReplicates
    {
        get => _completedReplicates;
        set
        {
            if (_completedReplicates != value)
            {
                _completedReplicates = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ReplicatesDisplay));
            }
        }
    }

    /// <summary>Formatted as &lt;completed&gt;/&lt;requested&gt; (e.g. 0/1, 1/1).</summary>
    public string ReplicatesDisplay
    {
        get => $"{CompletedReplicates}/{RequestedReplicates}";
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var parts = value.Split('/');
            var targetStr = parts.Length > 1 ? parts[1] : parts[0];
            if (int.TryParse(targetStr.Trim(), out var r) && r >= 1)
            {
                RequestedReplicates = r;
            }
        }
    }

    private int _acceptedReplicates;
    public int AcceptedReplicates
    {
        get => _acceptedReplicates;
        set { if (_acceptedReplicates != value) { _acceptedReplicates = value; OnPropertyChanged(); } }
    }

    private int _rejectedReplicates;
    public int RejectedReplicates
    {
        get => _rejectedReplicates;
        set { if (_rejectedReplicates != value) { _rejectedReplicates = value; OnPropertyChanged(); } }
    }

    public PowerConditionOrigin Origin { get; set; } = PowerConditionOrigin.Manual;
    public Guid? SourceMapId { get; set; }
    public string? SourceMapName { get; set; }

    private PowerConditionStatus _status = PowerConditionStatus.Pending;
    public PowerConditionStatus Status
    {
        get => _status;
        set { if (_status != value) { _status = value; OnPropertyChanged(); } }
    }

    public bool HasReplicateDisagreement { get; set; }
    public string? ReproducibilityWarning { get; set; }

    public PowerCondition Clone() => new()
    {
        ConditionId = ConditionId,
        OrderIndex = OrderIndex,
        AgitationRpm = AgitationRpm,
        FlowUnit = FlowUnit,
        GasFlowLpm = GasFlowLpm,
        GasFlowVvm = GasFlowVvm,
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
public sealed record TarePoint(double Rpm, double PVoidW, double SigmaTauPercent)
{
    public int SampleCount { get; init; }
    public double MeanRpmMeasured { get; init; }
    public double RpmStandardDeviation { get; init; }
    public double RpmCi95 { get; init; }
    public double MeanTorquePercent { get; init; }
    public double TorqueCi95Percent { get; init; }
    public double PVoidCi95W { get; init; }
    public double ElapsedSeconds { get; init; }
    public int Attempts { get; init; }
    public PowerStopReason StopReason { get; init; }
}

public sealed record TareCurve
{
    public int SchemaVersion { get; init; } = 1;
    public List<TarePoint> Points { get; init; } = [];
    public List<TareSample> Samples { get; init; } = [];
    public PowerTestSettings? AcquisitionSettings { get; init; }
    public string ImpellerSetHash { get; init; } = "";
    public string? CalibrationHash { get; init; }
    public DateTimeOffset MeasuredUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Name of the shaft profile this curve belongs to, empty when it was never filed.
    /// </summary>
    /// <remarks>
    /// A tare is a property of the shaft and its seal, not of the assay. A bench running
    /// two shafts (say <c>eixo_furo_unico</c> and <c>eixo_furo_duplo</c>) has two valid
    /// tares at once, and each new assay on either shaft should reuse the matching one
    /// rather than re-measuring it in the air. The name is what lets an operator tell
    /// them apart; the store keeps one file per name under
    /// <see cref="PowerTestFileContracts.TareProfilesDirectoryName"/>.
    /// </remarks>
    public string ProfileName { get; init; } = "";

    /// <summary>
    /// File name, inside the assay's <c>Taras-Brutas/</c>, holding every reading of this sweep.
    /// </summary>
    /// <remarks>
    /// <see cref="Samples"/> keeps the same readings inside this document for a sweep that
    /// converged; the file is the copy that also survives one that did not, and it is written
    /// while the sweep runs rather than after it succeeds. Empty on a curve filed before the
    /// raw file existed, or imported from a profile measured elsewhere.
    /// </remarks>
    public string RawSamplesFileName { get; init; } = "";
}

/// <summary>
/// One activation of the single-point panel: the metadata its raw CSV cannot carry.
/// </summary>
/// <remarks>
/// The CSV holds the readings; this holds what they were taken under - the commanded speed and
/// flow, and the rated torque the watts column was computed with. Without the last one the
/// power column is not reproducible, because the conversion from the servo's torque percentage
/// is a property of the motor, not of the reading.
/// </remarks>
public sealed record SinglePointSession
{
    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedUtc { get; init; }
    public double TargetRpm { get; init; }
    public double? GasFlowSetpointLpm { get; init; }
    public double MotorRatedTorqueNm { get; init; }
    public int SampleCount { get; init; }

    /// <summary>Assay this check was taken under, empty when the panel ran with none open.</summary>
    public string TestFolderName { get; init; } = "";

    /// <summary>Raw readings file this manifest describes.</summary>
    public string RawDataFileName { get; init; } = "";

    /// <summary>How the capture ended: stopped by the operator, or interrupted by a fault.</summary>
    public string StopReason { get; init; } = "";
}

/// <summary>A filed tare profile as listed for the operator, without loading its samples.</summary>
public sealed record TareProfileSummary(
    string Name,
    int PointCount,
    DateTimeOffset MeasuredUtc,
    string ImpellerSetHash);

public enum TareCapturePhase
{
    StabilizingSpeed,
    StabilizingTorque,
    Accumulating,
}

public sealed record TareSample(
    DateTimeOffset TimestampUtc,
    double ElapsedSeconds,
    double TargetRpm,
    double RpmMeasured,
    double TorquePercent,
    TareCapturePhase Phase,
    bool Counted,
    int Attempt = 1)
{
    public double MeasuredRpm => RpmMeasured;
    public double MeasuredTorquePercent => TorquePercent;
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

/// <summary>
/// Item de comparação e eficiência de transferência de oxigênio entre o ensaio de potência e o mapa de kLa interpolado.
/// </summary>
public sealed record KlaEfficiencyComparisonItem
{
    public double AgitationRpm { get; init; }
    public double GasFlowLpm { get; init; }
    public double? KlaInterpolatedPerHour { get; init; }
    public double? NetPowerW { get; init; }
    public double? VolumetricPowerWm3 { get; init; }
    public double? SpecificEfficiency { get; init; }
    public bool IsInControlRegion { get; init; }
    public string RegionLabel => IsInControlRegion ? "Intersecção" : "Extrapolação";
}

/// <summary>Summary of flooding analysis (Nienow comparison and transition point, §4.5, §16).</summary>
public sealed record FloodingAnalysisResult
{
    public double ExperimentalFlG { get; init; }
    public double ExperimentalRpm { get; init; }
    public double ExperimentalFlowLpm { get; init; }
    public double TheoreticalFlGNienow { get; init; }
    public double RelativeDeviationPercent { get; init; }
    public int ReferenceStageIndex { get; init; }
    public ImpellerType ReferenceImpellerType { get; init; }
    public FloodingDetectionMethod Method { get; init; } = FloodingDetectionMethod.Automatic;
    public DateTimeOffset DeterminedUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? Notes { get; init; }
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

    // Gassed and flooding fields (§4.5, §11, §16)
    public double? GasFlowVvm { get; set; }
    public double? GasFlowNumber { get; set; }
    public double? FroudeNumber { get; set; }
    public double? GassedPowerW { get; set; }
    public double? ReferenceP0W { get; set; }
    public double? ReferenceP0Ci95W { get; set; }
    public P0Provenance P0Provenance { get; set; } = P0Provenance.None;
    public double? PowerRatio { get; set; }
    public double? PowerRatioCi95 { get; set; }
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
    public ManualElecReading? ManualElec { get; init; }

    // Gassed and flooding fields (§4.5, §11, §16)
    public double? GasFlowVvm { get; init; }
    public double? GasFlowNumber { get; init; }
    public double? FroudeNumber { get; init; }
    public double? GassedPowerW { get; init; }
    public double? ReferenceP0W { get; init; }
    public double? ReferenceP0Ci95W { get; init; }
    public P0Provenance P0Provenance { get; init; } = P0Provenance.None;
    public double? PowerRatio { get; init; }
    public double? PowerRatioCi95 { get; init; }
    public bool UsedVentStabilization { get; init; }
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
    public FloodingAnalysisResult? Flooding { get; set; }

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

/// <summary>The impeller catalog/library. Persisted to impellers.json in AppPaths.ConfigDirectory.</summary>
public static class PowerImpellerCatalog
{
    public static string CatalogFilePath => Path.Combine(AppPaths.ConfigDirectory, "impellers.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static IReadOnlyList<Impeller> Defaults => new[]
    {
        new Impeller { Type = ImpellerType.RushtonFlatBlade, Label = "Rushton (pás planas)", DiameterM = 0.065, BladeCount = 6, ClearanceM = 0.065, LiteratureNp = 5.0 },
        new Impeller { Type = ImpellerType.MarinePropeller, Label = "Hélice marinha", DiameterM = 0.065, BladeCount = 3, ClearanceM = 0.065, LiteratureNp = 0.35 },
        new Impeller { Type = ImpellerType.ElephantEar, Label = "Orelha de elefante", DiameterM = 0.065, BladeCount = 3, ClearanceM = 0.065, LiteratureNp = 1.3 },
        new Impeller { Type = ImpellerType.SmithConcaveBlade, Label = "Smith (pás côncavas / CD-6)", DiameterM = 0.065, BladeCount = 6, ClearanceM = 0.065, LiteratureNp = 4.1 },
    };

    public static Impeller Create(ImpellerType type) =>
        LoadCatalog().FirstOrDefault(i => i.Type == type)?.Clone()
        ?? Defaults.FirstOrDefault(i => i.Type == type)?.Clone()
        ?? new Impeller { Type = ImpellerType.Custom, Label = "Personalizado", DiameterM = 0.065, BladeCount = 6, ClearanceM = 0.065 };

    public static Impeller CreateByName(string name)
    {
        var catalog = LoadCatalog();
        var match = catalog.FirstOrDefault(i => string.Equals(i.Label, name, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            return match.Clone();
        }

        return new Impeller
        {
            Type = ImpellerType.Custom,
            Label = !string.IsNullOrWhiteSpace(name) ? name : "Novo Impelidor",
            DiameterM = 0.065,
            BladeCount = 6,
            ClearanceM = 0.065,
            LiteratureNp = 1.0,
        };
    }

    public static List<Impeller> LoadCatalog()
    {
        try
        {
            if (File.Exists(CatalogFilePath))
            {
                var json = File.ReadAllText(CatalogFilePath);
                var items = JsonSerializer.Deserialize<List<Impeller>>(json, JsonOpts);
                if (items is not null)
                {
                    return items;
                }
            }
        }
        catch
        {
            // Fall back to defaults on read/deserialization failure
        }

        return Defaults.Select(i => i.Clone()).ToList();
    }

    public static void SaveCatalog(IEnumerable<Impeller> impellers)
    {
        ArgumentNullException.ThrowIfNull(impellers);
        Directory.CreateDirectory(AppPaths.ConfigDirectory);
        var list = impellers.ToList();
        var json = JsonSerializer.Serialize(list, JsonOpts);
        File.WriteAllText(CatalogFilePath, json);
    }
}
