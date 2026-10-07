using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Serialization;

namespace OpenTECHub.Services.KlaTesting;

public enum KlaAssayProtocol { Abiotic, Biotic }
public enum KlaCaptureMode { Single, Multiple }
public enum KlaGasRemovalMode { NitrogenStripping, Respiration }
public enum KlaScientificQuality { NotEvaluated, Valid, Conditional, Inconclusive, NotApplicable }
public enum KlaOperatorDecision { Pending, Accepted, Rejected }
public enum KlaRestorationState { NotRecorded, NotRequired, Pending, Confirmed, Failed }
public enum KlaRemovalGasRoute { NitrogenToReactor, AirToVent }

/// <summary>Routing contract independent of condition scheduling; enacted by the later runner policy.</summary>
public sealed record KlaProtocolPolicy(KlaRemovalGasRoute RemovalRoute,
    bool KeepFlowmeterRunningDuringRemoval, bool NitrogenOpenDuringRemoval,
    bool RequireStableDoBeforeAirSwitch, bool RequireAirPrestage)
{
    public static KlaProtocolPolicy For(KlaAssayProtocol protocol) => protocol switch
    {
        KlaAssayProtocol.Abiotic => new(KlaRemovalGasRoute.NitrogenToReactor, false, true, true, true),
        KlaAssayProtocol.Biotic => new(KlaRemovalGasRoute.AirToVent, true, false, false, false),
        _ => throw new ArgumentException("Protocolo desconhecido.", nameof(protocol)),
    };
}

/// <summary>Probe metadata, never a restriction to a particular sensor technology.</summary>
[JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)]
public sealed record KlaProbeDescription
{
    public string? Technology { get; init; }
    public string? Model { get; init; }
    public string? ResponseDescription { get; init; }
    public double? ResponseTimeSeconds { get; init; }
}

/// <summary>Usual operation range, distinct from the criteria for restoring aeration.</summary>
public sealed record KlaOperatingRange(
    double MinimumAgitationRpm, double MaximumAgitationRpm,
    double MinimumAirflowLpm, double MaximumAirflowLpm,
    double MinimumOperatingDoPercent, double MaximumOperatingDoPercent)
{
    public static KlaOperatingRange CurrentCultivation { get; } = new(50, 1000, 0.5, 16, 30, 100);

    public void Validate()
    {
        Check(MinimumAgitationRpm, MaximumAgitationRpm, "agitação", positive: true);
        Check(MinimumAirflowLpm, MaximumAirflowLpm, "vazão", positive: true);
        Check(MinimumOperatingDoPercent, MaximumOperatingDoPercent, "OD", positive: false);
    }

    private static void Check(double min, double max, string name, bool positive)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max) || min > max || min < 0 || (positive && min == 0))
        {
            throw new ArgumentException($"Faixa de {name} inválida.");
        }
    }
}

/// <summary>Configured later by the operational protocol; missing values remain unknown.</summary>
public sealed record KlaAerationReturnCriteria
{
    public double? MinimumDoPercent { get; init; }
    public double? MaximumDoDropPoints { get; init; }
    public double? MaximumGasOffSeconds { get; init; }
    public double? MaximumRecoverySeconds { get; init; }
    public double? MinimumInterAssaySeconds { get; init; }
}

public sealed record KlaProtocolSettings
{
    public KlaOperatingRange? OperatingRange { get; init; }
    public double OxygenRemovalAgitationRpm { get; init; } = 100;
    public double MinimumRemovalTargetDoPercent { get; init; } = 5;
    public double MaximumRemovalTargetDoPercent { get; init; } = 20;
    public double? RemovalTargetDoPercent { get; init; }
    public KlaProbeDescription Probe { get; init; } = new();
    public KlaAerationReturnCriteria AerationReturn { get; init; } = new();
    public double OxygenSampleTimeoutSeconds { get; init; } = 10;
    public double CommandConfirmationTimeoutSeconds { get; init; } = 10;
    public double? ReturnAgitationRpm { get; init; }
    public double ReturnAgitationToleranceRpm { get; init; } = 25;
    public double ReturnFlowToleranceLpm { get; init; } = 0.3;
    public double InitialStabilitySeconds { get; init; } = 10;
    public double RecoveryStabilitySeconds { get; init; } = 10;
}

/// <summary>An immutable planned condition; counters and decisions belong to the session.</summary>
public sealed record KlaAssayCondition(Guid ConditionId, int OrderIndex, double AgitationRpm,
    double AirflowLpm, int RequestedReplicates)
{
    public ConditionOrigin Origin { get; init; } = ConditionOrigin.Manual;
    public Guid? SourceMapId { get; init; }
    public string? SourceMapName { get; init; }
    public string? SourceMapFingerprint { get; init; }
    public static KlaAssayCondition From(KlaTestCondition condition) => new(condition.ConditionId,
        condition.OrderIndex, condition.AgitationRpm, condition.AirflowLpm, condition.RequestedReplicates)
    {
        Origin = condition.Origin, SourceMapId = condition.SourceMapId,
        SourceMapName = condition.SourceMapName, SourceMapFingerprint = condition.SourceMapFingerprint,
    };

    public KlaTestCondition ToSessionCondition() => new()
    {
        ConditionId = ConditionId, OrderIndex = OrderIndex, AgitationRpm = AgitationRpm,
        AirflowLpm = AirflowLpm, RequestedReplicates = RequestedReplicates,
        Origin = Origin, SourceMapId = SourceMapId, SourceMapName = SourceMapName,
        SourceMapFingerprint = SourceMapFingerprint,
    };
}

/// <summary>Protocol and scheduling are independent. Both modes use the same run unit.</summary>
public sealed record KlaAssayDefinition
{
    public KlaMeasurementContext? Context { get; init; }
    public KlaSequenceLimits? SequenceLimits { get; init; } = new();
    public KlaAssayProtocol Protocol { get; init; } = KlaAssayProtocol.Abiotic;
    public KlaCaptureMode CaptureMode { get; init; } = KlaCaptureMode.Multiple;
    public KlaTestSettings Settings { get; init; } = new();
    public KlaProtocolSettings ProtocolSettings { get; init; } = new();
    public ImmutableArray<KlaAssayCondition> Conditions { get; init; } = [];

    [JsonIgnore]
    public KlaGasRemovalMode GasRemoval => Protocol == KlaAssayProtocol.Biotic
        ? KlaGasRemovalMode.Respiration : KlaGasRemovalMode.NitrogenStripping;

    [JsonIgnore]
    public KlaProtocolPolicy OperationalPolicy => KlaProtocolPolicy.For(Protocol);

    public static KlaAssayDefinition FromDocument(KlaTestDocument document) => new()
    {
        Protocol = document.EffectiveProtocol, CaptureMode = document.EffectiveCaptureMode,
        Context = document.Context, SequenceLimits = document.SequenceLimits,
        Settings = document.Settings,
        ProtocolSettings = document.ProtocolSettings ?? new()
        {
            OxygenRemovalAgitationRpm = document.Settings.DegassingAgitationRpm,
        },
        Conditions = document.Conditions.Select(KlaAssayCondition.From).ToImmutableArray(),
    };

    public void Validate(bool requireConditions = true)
    {
        if (!Enum.IsDefined(Protocol) || !Enum.IsDefined(CaptureMode))
        {
            throw new ArgumentException("Protocolo ou modo de captura desconhecido.");
        }

        ArgumentNullException.ThrowIfNull(Settings);
        SequenceLimits?.Validate();
        ArgumentNullException.ThrowIfNull(ProtocolSettings);
        ArgumentNullException.ThrowIfNull(ProtocolSettings.Probe);
        ArgumentNullException.ThrowIfNull(ProtocolSettings.AerationReturn);
        foreach (var value in new[] { ProtocolSettings.OxygenSampleTimeoutSeconds,
            ProtocolSettings.CommandConfirmationTimeoutSeconds, ProtocolSettings.ReturnAgitationToleranceRpm,
            ProtocolSettings.ReturnFlowToleranceLpm, ProtocolSettings.InitialStabilitySeconds,
            ProtocolSettings.RecoveryStabilitySeconds })
        {
            if (!double.IsFinite(value) || value <= 0)
            {
                throw new ArgumentException("Critérios operacionais devem ser positivos e finitos.");
            }
        }
        if (ProtocolSettings.ReturnAgitationRpm is { } returnRpm && (!double.IsFinite(returnRpm) || returnRpm <= 0))
        {
            throw new ArgumentException("Agitação de retorno inválida.");
        }
        if (Conditions.IsDefault)
        {
            throw new ArgumentException("Condições não inicializadas.");
        }

        if (requireConditions && Conditions.IsEmpty)
        {
            throw new ArgumentException("Defina uma condição para o ensaio.");
        }

        if (CaptureMode == KlaCaptureMode.Single && (Conditions.Length != 1 || Conditions[0].RequestedReplicates != 1))
        {
            throw new ArgumentException("Captura única requer uma condição e uma corrida planejada.");
        }

        if (Conditions.Select(c => c.ConditionId).Distinct().Count() != Conditions.Length)
        {
            throw new ArgumentException("IDs de condição duplicados.");
        }

        ProtocolSettings.OperatingRange?.Validate();
        if (!double.IsFinite(ProtocolSettings.OxygenRemovalAgitationRpm) || ProtocolSettings.OxygenRemovalAgitationRpm <= 0 ||
            !double.IsFinite(ProtocolSettings.MinimumRemovalTargetDoPercent) ||
            !double.IsFinite(ProtocolSettings.MaximumRemovalTargetDoPercent) ||
            ProtocolSettings.MinimumRemovalTargetDoPercent < 0 ||
            ProtocolSettings.MinimumRemovalTargetDoPercent > ProtocolSettings.MaximumRemovalTargetDoPercent)
        {
            throw new ArgumentException("Agitação ou faixa de OD de remoção inválida.");
        }

        if (ProtocolSettings.RemovalTargetDoPercent is { } target &&
            (!double.IsFinite(target) || target < ProtocolSettings.MinimumRemovalTargetDoPercent ||
             target > ProtocolSettings.MaximumRemovalTargetDoPercent))
        {
            throw new ArgumentException("OD final de remoção fora da faixa definida.");
        }

        foreach (var c in Conditions)
        {
            if (c.ConditionId == Guid.Empty || !double.IsFinite(c.AgitationRpm) || c.AgitationRpm <= 0 ||
                !double.IsFinite(c.AirflowLpm) || c.AirflowLpm <= 0 || c.RequestedReplicates < 1)
            {
                throw new ArgumentException("Condição inválida: N, Q e réplicas devem ser positivos.");
            }

            if (ProtocolSettings.OperatingRange is { } range &&
                (c.AgitationRpm < range.MinimumAgitationRpm || c.AgitationRpm > range.MaximumAgitationRpm ||
                 c.AirflowLpm < range.MinimumAirflowLpm || c.AirflowLpm > range.MaximumAirflowLpm))
            {
                throw new ArgumentException("Condição fora da faixa de operação definida para esta sessão.");
            }
        }
        if (ProtocolSettings.Probe.ResponseTimeSeconds is { } tau && (!double.IsFinite(tau) || tau <= 0))
        {
            throw new ArgumentException("Tempo de resposta informado deve ser positivo.");
        }

        var criteria = ProtocolSettings.AerationReturn;
        foreach (var value in new[] { criteria.MaximumDoDropPoints, criteria.MaximumGasOffSeconds, criteria.MaximumRecoverySeconds })
        {
            if (value.HasValue && (!double.IsFinite(value.Value) || value.Value <= 0))
            {
                throw new ArgumentException("Critério de retomada informado deve ser positivo.");
            }
        }

        if (criteria.MinimumDoPercent is { } floor && (!double.IsFinite(floor) || floor < 0))
        {
            throw new ArgumentException("OD mínimo informado inválido.");
        }

        if (criteria.MinimumInterAssaySeconds is { } interval && (!double.IsFinite(interval) || interval < 0))
        {
            throw new ArgumentException("Intervalo entre ensaios inválido.");
        }
        // Unknown response time and return criteria do not prevent preparing/persisting a session.
    }
}

/// <summary>One frozen run request, irrespective of single/multiple scheduling.</summary>
public sealed record KlaRunDefinition(KlaAssayProtocol Protocol, KlaCaptureMode CaptureMode,
    KlaTestSettings Settings, KlaProtocolSettings ProtocolSettings, KlaAssayCondition Condition,
    int ReplicateNumber)
{
    public int AttemptNumber { get; init; } = 1;
    public KlaMeasurementContext? Context { get; init; }
    public static KlaRunDefinition Create(KlaTestDocument document, KlaTestCondition condition, int replicateNumber)
    {
        if (replicateNumber < 1)
        {
            throw new ArgumentException("Réplica inválida.", nameof(replicateNumber));
        }

        var snapshot = KlaAssayDefinition.FromDocument(document);
        if (snapshot.CaptureMode == KlaCaptureMode.Single)
        {
            if (replicateNumber != 1) throw new ArgumentException("Captura única possui uma réplica; repetições são novas tentativas dessa réplica.");
            snapshot.Validate();
            if (snapshot.Conditions[0].ConditionId != condition.ConditionId ||
                snapshot.Conditions[0].AgitationRpm != condition.AgitationRpm ||
                snapshot.Conditions[0].AirflowLpm != condition.AirflowLpm)
            {
                throw new ArgumentException("Condição diferente da captura única planejada.");
            }
        }
        return new(snapshot.Protocol, snapshot.CaptureMode, snapshot.Settings,
            snapshot.ProtocolSettings, KlaAssayCondition.From(condition), replicateNumber)
        {
            Context = document.Context,
            AttemptNumber = document.Runs.Count(r => r.ConditionId == condition.ConditionId && r.ReplicateNumber == replicateNumber) + 1,
        };
    }
}

/// <summary>Scientific evaluation, operator choice and physical restoration are independent.</summary>
public sealed record KlaRunOutcome
{
    public KlaScientificQuality KlaQuality { get; init; } = KlaScientificQuality.NotEvaluated;
    public KlaScientificQuality OurQuality { get; init; } = KlaScientificQuality.NotEvaluated;
    public double? OurPercentPointsPerHour { get; init; }
    public double? OurMmolPerLPerHour { get; init; }
    public double? PhysicalSaturationPercent { get; init; }
    public KlaOperatorDecision OperatorDecision { get; init; } = KlaOperatorDecision.Pending;
    public KlaRestorationState Restoration { get; init; } = KlaRestorationState.NotRecorded;
    public string? ScientificReason { get; init; }
    public string? RestorationReason { get; init; }

    [JsonIgnore]
    public bool HasValidKla => KlaQuality == KlaScientificQuality.Valid;

    public static KlaRunOutcome FromLegacy(DecisionQuality? quality, RunPhase? phase = null) => new()
    {
        // Legacy acceptance is not retrospective validation under the new scientific contract.
        KlaQuality = quality == DecisionQuality.Inconclusive ? KlaScientificQuality.Inconclusive
            : quality.HasValue ? KlaScientificQuality.Conditional : KlaScientificQuality.NotEvaluated,
        OurQuality = KlaScientificQuality.NotEvaluated,
        OperatorDecision = phase == RunPhase.Accepted ? KlaOperatorDecision.Accepted
            : phase == RunPhase.Rejected ? KlaOperatorDecision.Rejected : KlaOperatorDecision.Pending,
        Restoration = KlaRestorationState.NotRecorded,
    };
}
