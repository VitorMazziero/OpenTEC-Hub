using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;

namespace OpenTECHub.Services.Recipes;

public enum RecipeDecisionAuthor { AutomaticPolicy, Operator }
public enum SetpointStartSource { CurrentConfirmed, Explicit }
public enum RampOxygenTarget { MonitorReference, ActiveCascadeReference }
public enum RampCancellationPolicy { HoldLastReferences, RestoreSnapshot }
public enum MissedPeriodicSlotPolicy { Skip }
public enum RecipeActuatorHandoffPhase
{
    Requested, CascadeQuiesced, AssayActive, Restoring, CascadeResumed,
    RestorationFailed, EmergencyStopped
}

/// <summary>
/// Journalled handshake between concurrent recipe branches. Only the coordinator advances it;
/// a target block cannot claim the wire merely by publishing a notification.
/// </summary>
public sealed record RecipeActuatorHandoff
{
    public required Guid HandoffId { get; init; }
    public required string ControllerNodeId { get; init; }
    public required string TargetNodeId { get; init; }
    public required Guid ReturnSnapshotId { get; init; }
    public required ImmutableArray<ActuatorId> Resources { get; init; }
    public required RecipeActuatorHandoffPhase Phase { get; init; }
    public required int Revision { get; init; }
    public required DateTimeOffset UpdatedUtc { get; init; }
    public required string AcknowledgedBy { get; init; }
    public string? Reason { get; init; }

    public void Validate()
    {
        if (HandoffId == Guid.Empty || ReturnSnapshotId == Guid.Empty || Revision < 0 || UpdatedUtc == default)
            throw new ArgumentException("Handoff não identificado.");
        ContractGuard.Text(ControllerNodeId); ContractGuard.Text(TargetNodeId);
        ContractGuard.Text(AcknowledgedBy); ContractGuard.Defined(Phase);
        if (ControllerNodeId == TargetNodeId || Resources.IsDefaultOrEmpty ||
            Resources.Distinct().Count() != Resources.Length)
            throw new ArgumentException("Recursos ou participantes do handoff inválidos.");
        foreach (var resource in Resources) ContractGuard.Defined(resource);
        if (!Resources.Contains(ActuatorId.Agitation) || !Resources.Contains(ActuatorId.Aeration))
            throw new ArgumentException("Ensaio de kLa requer cessão de agitação e aeração.");
        if (Phase is RecipeActuatorHandoffPhase.RestorationFailed or RecipeActuatorHandoffPhase.EmergencyStopped &&
            string.IsNullOrWhiteSpace(Reason))
            throw new ArgumentException("Falha de handoff requer motivo.");
        var expectedRevision = Phase switch
        {
            RecipeActuatorHandoffPhase.Requested => 0,
            RecipeActuatorHandoffPhase.CascadeQuiesced => 1,
            RecipeActuatorHandoffPhase.AssayActive => 2,
            RecipeActuatorHandoffPhase.Restoring => 3,
            RecipeActuatorHandoffPhase.CascadeResumed => 4,
            _ => -1,
        };
        if (expectedRevision >= 0 && Revision != expectedRevision)
            throw new ArgumentException("Revisão de handoff incompatível com a fase.");
        if (Phase is RecipeActuatorHandoffPhase.CascadeQuiesced or RecipeActuatorHandoffPhase.CascadeResumed &&
            AcknowledgedBy != ControllerNodeId ||
            Phase == RecipeActuatorHandoffPhase.AssayActive && AcknowledgedBy != TargetNodeId)
            throw new ArgumentException("Confirmação de handoff pertence ao bloco errado.");
    }

    public RecipeActuatorHandoff Advance(RecipeActuatorHandoffPhase next, DateTimeOffset atUtc,
        string acknowledgedBy, string? reason = null)
    {
        Validate(); ContractGuard.Defined(next); ContractGuard.Text(acknowledgedBy);
        if (atUtc < UpdatedUtc || !CanAdvance(Phase, next))
            throw new InvalidOperationException("Transição de handoff fora de ordem.");
        var updated = this with { Phase = next, Revision = checked(Revision + 1),
            UpdatedUtc = atUtc, AcknowledgedBy = acknowledgedBy, Reason = reason };
        updated.Validate();
        return updated;
    }

    private static bool CanAdvance(RecipeActuatorHandoffPhase current, RecipeActuatorHandoffPhase next)
        => (current, next) switch
        {
            (RecipeActuatorHandoffPhase.Requested, RecipeActuatorHandoffPhase.CascadeQuiesced) => true,
            (RecipeActuatorHandoffPhase.CascadeQuiesced, RecipeActuatorHandoffPhase.AssayActive) => true,
            (RecipeActuatorHandoffPhase.AssayActive, RecipeActuatorHandoffPhase.Restoring) => true,
            (RecipeActuatorHandoffPhase.Restoring, RecipeActuatorHandoffPhase.CascadeResumed) => true,
            (_, RecipeActuatorHandoffPhase.RestorationFailed) when current is not
                (RecipeActuatorHandoffPhase.CascadeResumed or RecipeActuatorHandoffPhase.EmergencyStopped) => true,
            (_, RecipeActuatorHandoffPhase.EmergencyStopped) when current is not
                (RecipeActuatorHandoffPhase.CascadeResumed or RecipeActuatorHandoffPhase.RestorationFailed) => true,
            _ => false,
        };
}

/// <summary>
/// Periodic trigger on a recipe branch. Slot zero falls after InitialDelaySeconds;
/// later slots retain their original cadence even when a target block takes time.
/// </summary>
public sealed record PeriodicBlockSchedule
{
    public required double InitialDelaySeconds { get; init; }
    public required double PeriodSeconds { get; init; }
    public MissedPeriodicSlotPolicy MissedSlotPolicy { get; init; } = MissedPeriodicSlotPolicy.Skip;

    public void Validate()
    {
        ContractGuard.NonNegative(InitialDelaySeconds);
        ContractGuard.Positive(PeriodSeconds);
        ContractGuard.Defined(MissedSlotPolicy);
    }

    /// <summary>Elapsed real time from entry into the periodic block, measured by a monotonic clock.</summary>
    public double DueAfterSeconds(long slotIndex)
    {
        Validate();
        if (slotIndex < 0) throw new ArgumentOutOfRangeException(nameof(slotIndex));
        var due = InitialDelaySeconds + slotIndex * PeriodSeconds;
        if (!double.IsFinite(due)) throw new ArgumentOutOfRangeException(nameof(slotIndex));
        return due;
    }

    /// <summary>First slot still due in the future; missed slots are not queued for catch-up.</summary>
    public long FirstFutureSlot(double elapsedSeconds)
    {
        Validate(); ContractGuard.NonNegative(elapsedSeconds);
        if (elapsedSeconds < InitialDelaySeconds) return 0;
        var index = Math.Floor((elapsedSeconds - InitialDelaySeconds) / PeriodSeconds) + 1;
        if (!double.IsFinite(index) || index > long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        return (long)index;
    }
}

/// <summary>Scheduling provenance of a due block; one invocation per slot, never a catch-up burst.</summary>
public sealed record PeriodicBlockInvocation
{
    public required Guid ScheduleRunId { get; init; }
    public required string SchedulerNodeId { get; init; }
    public required string TargetNodeId { get; init; }
    public string? CoordinatedCascadeNodeId { get; init; }
    public required long SlotIndex { get; init; }
    public required PeriodicBlockSchedule Schedule { get; init; }
    public void Validate()
    {
        if (ScheduleRunId == Guid.Empty || SlotIndex < 0)
            throw new ArgumentException("Identidade do ciclo periódico inválida.");
        ContractGuard.Text(SchedulerNodeId); ContractGuard.Text(TargetNodeId);
        if (SchedulerNodeId == TargetNodeId || CoordinatedCascadeNodeId is not null &&
            (string.IsNullOrWhiteSpace(CoordinatedCascadeNodeId) || CoordinatedCascadeNodeId == SchedulerNodeId ||
             CoordinatedCascadeNodeId == TargetNodeId))
            throw new ArgumentException("Blocos da agenda e do controle devem ser distintos.");
        ArgumentNullException.ThrowIfNull(Schedule); Schedule.Validate();
        _ = Schedule.DueAfterSeconds(SlotIndex);
    }
}

/// <summary>Stable identity of one invocation, distinct from an assay attempt or a transport retry.</summary>
public sealed record RecipeInvocationContext
{
    public required Guid RecipeRunId { get; init; }
    public required string NodeId { get; init; }
    public required Guid InvocationId { get; init; }
    public required string CultivationId { get; init; }
    public required string RecipeSha256 { get; init; }
    public int Cycle { get; init; }

    [JsonIgnore]
    public string IdempotencyKey => $"{RecipeRunId:N}/{NodeId}/{InvocationId:N}/{Cycle}";

    public void Validate()
    {
        if (RecipeRunId == Guid.Empty || InvocationId == Guid.Empty || Cycle < 0)
        {
            throw new ArgumentException("Identidade da execução inválida.");
        }

        ContractGuard.Text(NodeId); ContractGuard.Text(CultivationId);
        if (RecipeSha256 is null || RecipeSha256.Length != 64 || !RecipeSha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Hash SHA-256 da receita inválido.");
        }
    }
}

/// <summary>Exact desired configuration captured before the assay; readings are not substituted for references.</summary>
public sealed record ActuatorReturnSnapshot
{
    public required ActuatorId Actuator { get; init; }
    public required CommandOwner Owner { get; init; }
    public required string OwnerExecutionId { get; init; }
    // Includes loop enable, routing, pump configuration and references, not just a scalar setpoint.
    public required string DesiredCommandJson { get; init; }
    public required string ConfirmationChannel { get; init; }
    public void Validate()
    {
        ContractGuard.Defined(Actuator); ContractGuard.Defined(Owner);
        if (Owner is CommandOwner.KlaAssay or CommandOwner.PowerAssay)
        {
            throw new ArgumentException("Ensaio concorrente não pode ser proprietário de retorno.");
        }

        ContractGuard.Text(OwnerExecutionId); ContractGuard.Text(ConfirmationChannel);
        ContractGuard.JsonObject(DesiredCommandJson);
    }
}

public sealed record ControllerReturnSnapshot
{
    public required string ControllerId { get; init; }
    public required bool WasActive { get; init; }
    // Configuration, references and resumable internal state, versioned by the controller adapter.
    public required string StateJson { get; init; }
    public required string StateVersion { get; init; }
    public void Validate()
    {
        ContractGuard.Text(ControllerId); ContractGuard.Text(StateVersion);
        ContractGuard.JsonObject(StateJson);
    }
}

/// <summary>Mandatory return to the pre-assay state. No fallback to fixed N/Q or Manual ownership.</summary>
public sealed record KlaReturnSnapshot
{
    public required Guid SnapshotId { get; init; }
    public required DateTimeOffset CapturedUtc { get; init; }
    public required double AgitationSetpointRpm { get; init; }
    public required double AirflowSetpointLpm { get; init; }
    public required GasRoute GasRoute { get; init; }
    public required GasInput AirInletInput { get; init; }
    public required ImmutableArray<ActuatorReturnSnapshot> Actuators { get; init; }
    public required ImmutableArray<ControllerReturnSnapshot> Controllers { get; init; }

    public void Validate()
    {
        if (SnapshotId == Guid.Empty || CapturedUtc == default)
        {
            throw new ArgumentException("Snapshot de retorno não identificado.");
        }

        ContractGuard.NonNegative(AgitationSetpointRpm); ContractGuard.NonNegative(AirflowSetpointLpm);
        ContractGuard.Defined(GasRoute); ContractGuard.Defined(AirInletInput);
        if (GasRoute == GasRoute.Closed && AirflowSetpointLpm > 0)
        {
            throw new ArgumentException("Vazão positiva com rota fechada não é estado de retorno válido.");
        }

        if (Actuators.IsDefaultOrEmpty || Controllers.IsDefault)
        {
            throw new ArgumentException("Estado dos atuadores/controladores ausente.");
        }

        foreach (var item in Actuators) { ArgumentNullException.ThrowIfNull(item); item.Validate(); }
        foreach (var item in Controllers) { ArgumentNullException.ThrowIfNull(item); item.Validate(); }
        if (Actuators.Select(a => a.Actuator).Distinct().Count() != Actuators.Length ||
            Controllers.Select(c => c.ControllerId).Distinct(StringComparer.Ordinal).Count() != Controllers.Length)
        {
            throw new ArgumentException("Itens de retorno duplicados.");
        }

        foreach (var actuator in new[] { ActuatorId.Agitation, ActuatorId.Aeration })
        {
            if (!Actuators.Any(a => a.Actuator == actuator))
            {
                throw new ArgumentException("Snapshot requer agitação e aeração.");
            }
        }
    }
}

public sealed record LinearSetpointRampLine
{
    public required SetpointVariable Variable { get; init; }
    public SetpointStartSource StartSource { get; init; } = SetpointStartSource.CurrentConfirmed;
    public double? InitialSetpoint { get; init; }
    public required double FinalSetpoint { get; init; }
    // All durations are relative to the common block start, in active (unpaused) seconds.
    public required double EndAfterSeconds { get; init; }
    public RampOxygenTarget? OxygenTarget { get; init; }

    [JsonIgnore]
    public string SetpointUnit => Variable switch
    {
        SetpointVariable.Temperature => "°C",
        SetpointVariable.Agitation => "rpm",
        SetpointVariable.Flow => "L/min",
        SetpointVariable.Oxygen => "%",
        SetpointVariable.Ph => "pH",
        SetpointVariable.Pressure => "kPa",
        _ => throw new ArgumentException("Variável desconhecida."),
    };

    public void Validate()
    {
        ContractGuard.Defined(Variable); ContractGuard.Defined(StartSource);
        ContractGuard.Positive(EndAfterSeconds);
        if (!DeviceRanges.Accepts(Variable, FinalSetpoint))
        {
            throw new ArgumentException("Alvo de rampa fora da faixa.");
        }

        if (StartSource == SetpointStartSource.Explicit)
        {
            if (InitialSetpoint is not { } initial || !DeviceRanges.Accepts(Variable, initial))
            {
                throw new ArgumentException("Setpoint inicial explícito inválido.");
            }
        }
        else if (InitialSetpoint.HasValue)
        {
            throw new ArgumentException("Origem atual não permite valor inicial explícito.");
        }

        if (Variable == SetpointVariable.Oxygen)
        {
            if (OxygenTarget is not { } target)
            {
                throw new ArgumentException("Defina o destino da referência de O₂.");
            }

            ContractGuard.Defined(target);
        }
        else if (OxygenTarget.HasValue)
        {
            throw new ArgumentException("Destino de O₂ não se aplica a esta variável.");
        }
        if (InitialSetpoint is { } start)
        {
            ValidateTrajectory(start);
        }
    }

    /// <summary>Called again at runtime once a CurrentConfirmed starting reference is captured.</summary>
    public void ValidateResolvedStart(double initialSetpoint)
    {
        Validate();
        if (StartSource == SetpointStartSource.Explicit && initialSetpoint != InitialSetpoint)
        {
            throw new ArgumentException("Início capturado difere da referência explícita.");
        }
        ValidateTrajectory(initialSetpoint);
    }

    private void ValidateTrajectory(double initialSetpoint)
    {
        if (!DeviceRanges.Accepts(Variable, initialSetpoint))
        {
            throw new ArgumentException("Referência inicial fora do envelope do dispositivo.");
        }
        // Zero is an OFF sentinel for some devices, not a continuous part of their operating band.
        if (DeviceRanges.For(Variable).Min > 0 && initialSetpoint != FinalSetpoint &&
            (initialSetpoint == 0 || FinalSetpoint == 0))
        {
            throw new ArgumentException("Rampa atravessaria faixa inválida entre desligado e operação.");
        }
    }
}

public sealed record LinearSetpointRampDefinition
{
    public required ImmutableArray<LinearSetpointRampLine> Lines { get; init; }
    public RampCancellationPolicy CancellationPolicy { get; init; } = RampCancellationPolicy.HoldLastReferences;
    public void Validate()
    {
        ContractGuard.Defined(CancellationPolicy);
        if (Lines.IsDefaultOrEmpty)
        {
            throw new ArgumentException("Rampa sem parâmetros.");
        }

        foreach (var line in Lines) { ArgumentNullException.ThrowIfNull(line); line.Validate(); }
        if (Lines.Select(l => l.Variable).Distinct().Count() != Lines.Length)
        {
            throw new ArgumentException("Parâmetro duplicado na rampa.");
        }
    }
}

internal static class ContractGuard
{
    public static void Text(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Identificador obrigatório ausente.");
        }
    }
    public static void Defined<T>(T value) where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentException("Valor de enum desconhecido.");
        }
    }
    public static void Positive(double value)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentException("Valor deve ser positivo e finito.");
        }
    }
    public static void NonNegative(double value)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentException("Valor deve ser não negativo e finito.");
        }
    }
    public static void JsonObject(string value)
    {
        Text(value);
        using var document = JsonDocument.Parse(value);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Estado deve ser objeto JSON.");
        }
    }
}
