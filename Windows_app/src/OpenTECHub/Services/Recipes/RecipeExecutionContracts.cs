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
