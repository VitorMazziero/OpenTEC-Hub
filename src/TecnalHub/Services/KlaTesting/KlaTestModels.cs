using System;
using System.Collections.Generic;

namespace TecnalHub.Services.KlaTesting;

public enum KlaTestStatus
{
    Draft,
    Running,
    Interrupted,
    Completed,
}

public enum RunPhase
{
    Idle,
    Preflight,
    ClosingAllGas,
    OpeningNitrogen,
    Deoxygenating,
    ClosingNitrogen,
    OpeningAir,
    Reoxygenating,
    StoppingRun,
    Reviewing,
    Accepted,
    Rejected,
    PreparingNextRun,
    Completed,
    Aborting,
    Faulted,
}

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
    public Guid RunId { get; set; } = Guid.NewGuid();
    public Guid TestId { get; set; }
    public Guid ConditionId { get; set; }
    public int ReplicateNumber { get; set; } = 1;
    public string FolderName { get; set; } = "";
    public double AgitationRpm { get; set; }
    public double AirflowLpm { get; set; }
    public NitrogenValve NitrogenValve { get; set; } = NitrogenValve.Valve1;
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
}

public sealed class KlaTestDocument
{
    public int SchemaVersion { get; set; } = 1;
    public Guid TestId { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string FolderName { get; set; } = "";
    public KlaTestStatus Status { get; set; } = KlaTestStatus.Draft;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? LastModifiedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public string Nature { get; set; } = "Abiotico";
    public KlaMapReference? LinkedMap { get; set; }
    public NitrogenValve SelectedNitrogenValve { get; set; } = NitrogenValve.Valve1;
    public KlaTestSettings Settings { get; set; } = new();
    public int SettingsRevision { get; set; } = 1;
    public string AppVersion { get; set; } = "";
    public string ProtocolVersion { get; set; } = "TECNAL_ESP32_v7 + flowmeter_TECNALHUB_V05";
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
    bool VFlow);

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
    int? CommandId,
    int? CommandAck,
    bool CommandPending,
    int SettingsRevision,
    string EventCode,
    string EventDetail);

public sealed record KlaTestEventLogEntry(
    DateTimeOffset TimestampUtc,
    string EventType,
    string Message,
    string? Details = null);
