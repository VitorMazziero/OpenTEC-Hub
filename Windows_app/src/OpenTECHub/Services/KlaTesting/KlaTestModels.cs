using System;
using System.Collections.Generic;

namespace OpenTECHub.Services.KlaTesting;

public enum KlaTestStatus
{
    Draft,
    Running,
    Interrupted,
    Completed,
}

/// <summary>
/// Phases of one kLa run on the A/B/C rig. Gas goes in through A (air to the reactor) or
/// through the shared B/C output (N₂ into the vessel, air out of the vent); there is no
/// "N₂ off, wait" step any more: the air is pre-staged through C while the nitrogen is still
/// stripping, and the single frame that closes B/C and opens A is <c>t = 0</c>.
/// </summary>
public enum RunPhase
{
    Idle,
    Preflight,
    ClosingAllGas,
    OpeningNitrogen,
    Deoxygenating,

    /// <summary>B/C open with the assay airflow: the meter settles on C while N₂ keeps the floor.</summary>
    PrestagingAir,

    /// <summary>One frame on the wire — B/C closed, A open — awaiting the flowmeter's echo.</summary>
    SwitchingToReactor,
    Reoxygenating,
    StoppingRun,
    Reviewing,
    Accepted,
    Rejected,
    PreparingNextRun,
    Completed,
    Aborting,
    Faulted,
    DivertingAir,
    MeasuringConsumption,
    RestoringCultivation,
}

/// <summary>
/// One of the flowmeter's two auxiliary valve outputs. Legacy: assays recorded before the
/// A/B/C rig declared which output carried the N₂ line and which the vent. Kept so those
/// manifests still open; routing now comes from <see cref="Persistence.GasRigSettings"/>.
/// </summary>
public enum NitrogenValve
{
    Valve1 = 1,
    Valve2 = 2,
}

public enum ConditionOrigin
{
    Manual,
    Map,
}

public enum ConditionStatus
{
    Pending,
    InProgress,
    Completed,
    Skipped,
}

public enum DecisionQuality
{
    Acceptable,
    AcceptableWithWarning,
    Inconclusive,
}

public sealed record KlaMapReference
{
    public Guid MapId { get; init; }
    public string MapName { get; init; } = "";
    public string? MapFingerprint { get; init; }
    public DateTimeOffset LinkedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record KlaTestSettings
{
    public double DOMinPercent { get; init; } = 15.0;
    public double DOMaxPercent { get; init; } = 85.0;
    public double DegassingAgitationRpm { get; init; } = 700.0;
    public int SmoothingWindowSize { get; init; } = 5;
    public double MaxDegassingTimeMinutes { get; init; } = 30.0;
    public double MaxReoxygenationTimeMinutes { get; init; } = 60.0;

    /// <summary>
    /// Probe-stability criterion, applied to the DO floor during <see cref="RunPhase.PrestagingAir"/>:
    /// the slope of DO over <see cref="StabilityDerivativeSpanSeconds"/> must stay within
    /// <see cref="StabilityDerivativeThresholdPercentPerSecond"/> for <see cref="StabilityRequiredSamples"/>
    /// consecutive frames.
    /// </summary>
    public double StabilityDerivativeSpanSeconds { get; init; } = 6.0;
    public double StabilityDerivativeThresholdPercentPerSecond { get; init; } = 0.05;
    public int StabilityRequiredSamples { get; init; } = 5;

    /// <summary>
    /// How early the air is pre-staged through C, in DO percentage points above
    /// <see cref="DOMinPercent"/>. 0 = start pre-staging when the floor is reached; larger values
    /// let the meter settle while the last few percent are still being stripped.
    /// </summary>
    public double AirPrestageLeadPercent { get; init; } = 0.0;

    /// <summary>Measured flow must sit this close to the requested airflow while it settles on C.</summary>
    public double PrestageFlowToleranceLpm { get; init; } = 0.2;

    /// <summary>Consecutive in-tolerance readings required before the switch to A.</summary>
    public int PrestageFlowStableSamples { get; init; } = 5;

    /// <summary>
    /// Second way out of the flow wait: the flow is <em>stable</em> — the standard deviation of the
    /// last <see cref="PrestageFlowStableSamples"/> readings is below this — and within
    /// <see cref="PrestageFlowStabilityMaxErrorLpm"/> of the target, even if outside the tolerance
    /// band. The same criterion the power assay uses (<c>FlowSettling</c>): the controller's steady
    /// offset must not hold the run hostage when the reading has clearly settled.
    /// </summary>
    public double PrestageFlowStabilityStdDevLpm { get; init; } = 0.05;
    public double PrestageFlowStabilityMaxErrorLpm { get; init; } = 0.3;

    /// <summary>Ceiling on the pre-staging wait. Exceeding it stops the run for review instead of switching an unsettled line.</summary>
    public double MaxPrestageSeconds { get; init; } = 180.0;

    public double DefaultCeqPercent { get; init; } = 100.0;
    public bool AutoAcceptRuns { get; init; } = false;
    public double AutoLinearStartPercent { get; init; } = 45.0;
    public double AutoLinearEndPercent { get; init; } = 70.0;
}

public sealed class KlaTestCondition
{
    public Guid ConditionId { get; set; } = Guid.NewGuid();
    public int OrderIndex { get; set; }
    public double AgitationRpm { get; set; }
    public double AirflowLpm { get; set; }
    public int RequestedReplicates { get; set; } = 1;
    public int CompletedReplicates { get; set; }
    public int AcceptedReplicates { get; set; }
    public int RejectedReplicates { get; set; }
    public ConditionOrigin Origin { get; set; } = ConditionOrigin.Manual;
    public Guid? SourceMapId { get; set; }
    public string? SourceMapName { get; set; }
    public string? SourceMapFingerprint { get; set; }
    public ConditionStatus Status { get; set; } = ConditionStatus.Pending;

    public KlaTestCondition Clone() => new()
    {
        ConditionId = ConditionId,
        OrderIndex = OrderIndex,
        AgitationRpm = AgitationRpm,
        AirflowLpm = AirflowLpm,
        RequestedReplicates = RequestedReplicates,
        CompletedReplicates = CompletedReplicates,
        AcceptedReplicates = AcceptedReplicates,
        RejectedReplicates = RejectedReplicates,
        Origin = Origin,
        SourceMapId = SourceMapId,
        SourceMapName = SourceMapName,
        SourceMapFingerprint = SourceMapFingerprint,
        Status = Status,
    };
}

public sealed class KlaAnalysisRevision
{
    public KlaRunOutcome? Outcome { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public KlaRunOutcome EffectiveOutcome => Outcome ?? KlaRunOutcome.FromLegacy(Quality);
    public int RevisionNumber { get; set; } = 1;
    public DateTimeOffset AnalyzedUtc { get; set; } = DateTimeOffset.UtcNow;
    public double CeqPercent { get; set; } = 100.0;
    public bool IsCeqManual { get; set; }
    public double? FittedKappa { get; set; }
    public double? FittedA { get; set; }
    public double? CeqFitR2 { get; set; }
    public double CeqTStartSeconds { get; set; }
    public double CeqTEndSeconds { get; set; }
    public double TStartSeconds { get; set; }
    public double TEndSeconds { get; set; }
    public int TotalPoints { get; set; }
    public int UsedPoints { get; set; }
    public double KlaPerHour { get; set; }
    public double SlopeBeta1 { get; set; }
    public double InterceptBeta0 { get; set; }
    public double SlopeStandardError { get; set; }
    public double ConfidenceInterval95Low { get; set; }
    public double ConfidenceInterval95High { get; set; }
    public double AnalysisR2 { get; set; }
    public double AnalysisRmse { get; set; }
    public double KlaSensitivityLow { get; set; }
    public double KlaSensitivityHigh { get; set; }
    public DecisionQuality Quality { get; set; } = DecisionQuality.Acceptable;
    public string? WarningJustification { get; set; }
    public string? RejectionReason { get; set; }
    public string RawDataSha256 { get; set; } = "";
    public string AnalysisMethod { get; set; } = "OLS log-linear sem remoção de pontos";
    public string CeqMethod { get; set; } = "Ceq - A exp(-kappa t), pesos lineares na cauda";
}

public sealed class KlaTestRun
{
    public KlaAcquisitionMetadata? Acquisition { get; set; }
    public List<KlaGasEvent> GasEvents { get; set; } = [];
    public KlaRunDefinition? Definition { get; set; }
    public KlaRunOutcome? Outcome { get; set; }
    public Guid RunId { get; set; } = Guid.NewGuid();
    public Guid TestId { get; set; }
    public Guid ConditionId { get; set; }
    public int ReplicateNumber { get; set; } = 1;
    public string FolderName { get; set; } = "";
    public double AgitationRpm { get; set; }
    public double AirflowLpm { get; set; }

    /// <summary>True when the run started at the DO floor and skipped the nitrogen phase.</summary>
    public bool SkippedNitrogen { get; set; }

    /// <summary>
    /// The assay's <c>t = 0</c>: the frame in which the flowmeter confirmed B/C closed and A open.
    /// <see cref="KlaRawDataPoint.RelativeSeconds"/> keeps counting from the run's start so the
    /// file and the live chart stay monotonic; this is where the reoxygenation begins on that axis.
    /// </summary>
    public double? SwitchRelativeSeconds { get; set; }

    /// <summary>Flow and DO at the switch: what the reactor actually received at <c>t = 0</c>.</summary>
    public double? SwitchFlowRateLpm { get; set; }
    public double? SwitchDoPercent { get; set; }

    public RunPhase CurrentPhase { get; set; } = RunPhase.Idle;
    public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedUtc { get; set; }
    public string? RawDataPath { get; set; }
    public string? RawDataSha256 { get; set; }
    public KlaAnalysisRevision? LatestAnalysis { get; set; }
    public List<KlaAnalysisRevision> AnalysisHistory { get; set; } = [];
}

public sealed record KlaTestRunSummary
{
    public KlaRunDefinition? Definition { get; init; }
    public KlaRunOutcome? Outcome { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public KlaRunOutcome EffectiveOutcome => Outcome ?? KlaRunOutcome.FromLegacy(Decision, Phase);
    public Guid RunId { get; init; }
    public Guid ConditionId { get; init; }
    public int ReplicateNumber { get; init; }
    public string FolderName { get; init; } = "";
    public double AgitationRpm { get; init; }
    public double AirflowLpm { get; init; }
    public RunPhase Phase { get; init; }
    public DecisionQuality? Decision { get; init; }
    public double? KlaPerHour { get; init; }
    public double? AnalysisR2 { get; init; }
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; init; }

    /// <summary>Provenance of <c>t = 0</c>: flow and DO in the frame that confirmed the switch to A.</summary>
    public double? SwitchRelativeSeconds { get; init; }
    public double? SwitchFlowRateLpm { get; init; }
    public double? SwitchDoPercent { get; init; }
}

public sealed class KlaTestDocument
{
    /// <summary>
    /// 1 = original; 2 = CSV temperature/measured RPM; 3 = protocol, capture mode and outcomes.
    /// </summary>
    /// <remarks>
    /// Only the writer changes with the version. Both readers accept either file, so a schema-1
    /// assay stays openable and re-analysable exactly as it was recorded.
    /// </remarks>
    public int SchemaVersion { get; set; } = KlaTestFileContracts.CurrentSchemaVersion;
    public Guid TestId { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string FolderName { get; set; } = "";
    public KlaTestStatus Status { get; set; } = KlaTestStatus.Draft;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? LastModifiedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public string Nature { get; set; } = "Abiotico";
    /// <summary>Null in legacy files; resolved in memory without rewriting historical metadata.</summary>
    public KlaAssayProtocol? Protocol { get; set; }
    public KlaCaptureMode? CaptureMode { get; set; }
    public KlaProtocolSettings? ProtocolSettings { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public KlaAssayProtocol EffectiveProtocol => Protocol ??
        (string.Equals(Nature, "Biotico", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(Nature, "Biótico", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(Nature, "Biotic", StringComparison.OrdinalIgnoreCase)
            ? KlaAssayProtocol.Biotic : KlaAssayProtocol.Abiotic);
    [System.Text.Json.Serialization.JsonIgnore]
    public KlaCaptureMode EffectiveCaptureMode => CaptureMode ?? KlaCaptureMode.Multiple;
    public KlaMapReference? LinkedMap { get; set; }

    /// <summary>
    /// The A/B/C wiring this assay ran on, recorded when it started. Null on a manifest written
    /// before the rig existed: such an assay opens for review but cannot be continued
    /// (<see cref="IsLegacyRig"/>).
    /// </summary>
    public Persistence.GasRigSettings? GasRig { get; set; }

    /// <summary>When the operator confirmed the N₂ open at the source (preflight); goes to the journal.</summary>
    public DateTimeOffset? NitrogenSourceConfirmedUtc { get; set; }
    /// <summary>Independent physical isolation of N₂; B and C share one electrical output.</summary>
    public DateTimeOffset? NitrogenIsolationConfirmedUtc { get; set; }

    /// <summary>Legacy (pre-A/B/C): which output carried the N₂ line. Read for display only.</summary>
    public NitrogenValve SelectedNitrogenValve { get; set; } = NitrogenValve.Valve1;

    /// <summary>Legacy (pre-A/B/C): which output carried the vent. Read for display only.</summary>
    public NitrogenValve SelectedVentValve { get; set; } = NitrogenValve.Valve2;

    /// <summary>
    /// An assay that already ran without a recorded rig was stripped and re-aerated on a
    /// different plumbing; its runs are reviewable, but a new run on the A/B/C rig would not be
    /// a replicate of them. A draft that never started has nothing to protect.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsLegacyRig => GasRig is null && Status != KlaTestStatus.Draft;

    public KlaTestSettings Settings { get; set; } = new();
    public int SettingsRevision { get; set; } = 1;
    public string AppVersion { get; set; } = "";
    public string ProtocolVersion { get; set; } = "OpenTEC_ESP32_v7 + flowmeter_OpenTECHUB_V05";

    /// <summary>Hub build and wire contract at start, as the frame reports them; null on an older Hub or before any frame.</summary>
    public string? HubFirmwareVersion { get; set; }
    public int? HubProtocolVersion { get; set; }

    /// <summary>External-node firmware/address at start, keyed by wire name (Hub 10.1); empty when the Hub did not say.</summary>
    public Dictionary<string, Services.Communication.ExternalNodeProvenance> ExternalNodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string AlgorithmVersion { get; set; } = "LogLinear_OLS_v2";
    public List<KlaTestCondition> Conditions { get; set; } = [];
    public List<KlaTestRunSummary> Runs { get; set; } = [];
    public string? InterruptionReason { get; set; }
    public Dictionary<string, string> FileHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record KlaTestSummary(
    string FolderName,
    string Name,
    Guid TestId,
    KlaTestStatus Status,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? CompletedUtc,
    string? LinkedMapName,
    int ConditionCount,
    int CompletedRunCount,
    int AcceptedRunCount);

/// <summary>
/// One telemetry frame of a kLa run, as written to <c>dados-brutos.csv</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TemperatureC"/> and <see cref="RpmMeasured"/> are nullable and sit at the end of
/// the record on purpose. They are <i>observations</i>, not commands: the broth temperature sets
/// C* and is what a kLa corrected to 20 °C is corrected from, and the measured shaft speed is the
/// only evidence that the agitation actually held the condition being reported. Both can be
/// legitimately absent - a bench module has no servo, and a probe can drop out mid-run - and a
/// missing reading is written as an empty cell, never as zero.
/// </para>
/// <para>
/// New columns are appended after the existing ones so a file written by an older version keeps
/// every column at the position its readers expect (see <c>KlaTestStore.LoadRunRawData</c>).
/// </para>
/// </remarks>
public sealed record KlaRawDataPoint(
    DateTimeOffset TimestampUtc,
    double RelativeSeconds,
    RunPhase Phase,
    double DORaw,
    double DOFiltered,
    double FlowMeasured,
    double FlowSetpoint,
    double AgitationSetpoint,
    bool Valve1,
    bool Valve2,
    bool VFlow,
    double? TemperatureC = null,
    double? RpmMeasured = null);

public sealed record KlaGlobalSeriesSample(
    DateTimeOffset TimestampUtc,
    double MonotonicSeconds,
    Guid TestId,
    Guid? RunId,
    Guid? ConditionId,
    int? Replicate,
    RunPhase Phase,
    double DORaw,
    double DOFiltered,
    double DOMin,
    double DOMax,
    double FlowMeasured,
    double FlowSetpoint,
    double AgitationSetpoint,
    bool Valve1,
    bool Valve2,
    bool VFlow,
    long? CommandId,
    long? CommandAck,
    bool CommandPending,
    int SettingsRevision,
    string EventCode,
    string EventDetail,
    double? TemperatureC = null,
    double? RpmMeasured = null);

public sealed record KlaTestEventLogEntry(
    DateTimeOffset TimestampUtc,
    string EventType,
    string Message,
    string? Details = null);
