using System.Collections.Immutable;
using System.IO;
using System.Text.Json;

namespace OpenTECHub.Services.Recipes;

public enum RecipeRampTerminalStatus { Completed, Cancelled, Faulted, EmergencyStopped }
public enum RecipeRampReturnOutcome { NotRequired, HeldLastReferences, RestoredSnapshot, Failed, SuppressedForEmergency }
public enum RecipeRampConfirmationEvidence { TransportAccepted, DeviceReferenceReadback, ControllerReference, ProcessFeedback }

/// <summary>Reference actually confirmed by a destination. Transport acceptance alone cannot complete a ramp.</summary>
public sealed record RecipeRampFinalConfirmation(SetpointVariable Variable, RampOxygenTarget? OxygenTarget,
    double Reference, RecipeRampConfirmationEvidence Evidence, DateTimeOffset RecordedUtc,
    double? ObservedValue = null, double? Tolerance = null);

public sealed record RecipeRampTerminalCheckpoint(int SchemaVersion, Guid ExecutionId, Guid InvocationId,
    Guid SnapshotId, string NodeId, RecipeRampTerminalStatus Status, RecipeRampReturnOutcome ReturnOutcome,
    double ActiveSeconds, DateTimeOffset EndedUtc, string? Reason,
    ImmutableArray<RecipeRampFinalConfirmation> FinalConfirmations);

public sealed partial class RecipeRampCheckpointStore
{
    public async Task<RecipeRampTerminalCheckpoint> PersistTerminalAsync(RecipeRampTerminalCheckpoint checkpoint,
        CancellationToken cancellation = default)
    {
        var json = JsonSerializer.Serialize(checkpoint);
        var frozen = JsonSerializer.Deserialize<RecipeRampTerminalCheckpoint>(json)
            ?? throw new InvalidDataException("Resultado terminal vazio.");
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var directory = DirectoryFor(frozen.ExecutionId, frozen.InvocationId);
            // Do not create an orphan result when no durable start exists.
            var initial = ReadStart(frozen.ExecutionId, frozen.InvocationId)
                ?? throw new InvalidDataException("Resultado da rampa sem captura inicial durável.");
            using var lease = new FileStream(Path.Combine(directory, "checkpoint.lease"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            await writer.FlushDurableAsync(directory).ConfigureAwait(false);
            ValidateTerminal(frozen, initial);
            var path = Path.Combine(directory, "terminal.json");
            if (File.Exists(path))
            {
                if (File.ReadAllText(path) != json)
                    throw new InvalidOperationException("Invocação da rampa já possui outro resultado terminal.");
            }
            else writer.WriteAllTextAtomic(path, json);
            await writer.FlushDurableAsync(directory).ConfigureAwait(false);
            if (File.ReadAllText(path) != json) throw new IOException("Resultado terminal não confirmado na leitura.");
            return ReadTerminal(frozen.ExecutionId, frozen.InvocationId)!;
        }
        finally { _gate.Release(); }
    }

    public RecipeRampTerminalCheckpoint? ReadTerminal(Guid executionId, Guid invocationId)
    {
        var path = Path.Combine(DirectoryFor(executionId, invocationId), "terminal.json");
        if (!File.Exists(path)) return null;
        var initial = ReadStart(executionId, invocationId) ?? throw new InvalidDataException("Resultado sem captura inicial.");
        var result = JsonSerializer.Deserialize<RecipeRampTerminalCheckpoint>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Resultado terminal vazio.");
        ValidateTerminal(result, initial);
        return result;
    }

    private static void ValidateTerminal(RecipeRampTerminalCheckpoint result, RecipeRampStartCheckpoint start)
    {
        if (result.SchemaVersion != 1 || result.ExecutionId != start.InitialState.ExecutionId ||
            result.InvocationId != start.InvocationId || result.SnapshotId != start.InitialState.SnapshotId ||
            result.NodeId != start.InitialState.NodeId || !Enum.IsDefined(result.Status) || !Enum.IsDefined(result.ReturnOutcome) ||
            !double.IsFinite(result.ActiveSeconds) || result.ActiveSeconds < 0 || result.EndedUtc == default || result.FinalConfirmations.IsDefault)
            throw new InvalidDataException("Resultado terminal incompatível com a captura inicial.");
        if (result.FinalConfirmations.Select(item => item.Variable).Distinct().Count() != result.FinalConfirmations.Length)
            throw new InvalidDataException("Confirmações finais duplicadas.");
        foreach (var confirmation in result.FinalConfirmations)
        {
            var line = start.Configuration.Definition.Lines.SingleOrDefault(item => item.Variable == confirmation.Variable);
            var controller = line?.OxygenTarget == RampOxygenTarget.ActiveCascadeReference;
            if (line is null || confirmation.OxygenTarget != line.OxygenTarget || !Enum.IsDefined(confirmation.Evidence) ||
                !double.IsFinite(confirmation.Reference) || confirmation.Reference !=
                    RecipeRampReferenceQuantization.Quantize(line.Variable, line.FinalSetpoint) ||
                confirmation.RecordedUtc == default ||
                controller && confirmation.Evidence != RecipeRampConfirmationEvidence.ControllerReference ||
                !controller && confirmation.Evidence == RecipeRampConfirmationEvidence.ControllerReference)
                throw new InvalidDataException("Confirmação final incompatível com o destino ou alvo da rampa.");
            if (confirmation.Evidence == RecipeRampConfirmationEvidence.ProcessFeedback &&
                (confirmation.ObservedValue is not { } observed || !double.IsFinite(observed) ||
                 confirmation.Tolerance is not { } tolerance || !double.IsFinite(tolerance) || tolerance < 0 ||
                 Math.Abs(observed - confirmation.Reference) > tolerance))
                throw new InvalidDataException("Confirmação física exige leitura e tolerância coerentes.");
        }
        if (result.Status == RecipeRampTerminalStatus.Completed)
        {
            if (result.ReturnOutcome != RecipeRampReturnOutcome.NotRequired ||
                result.ActiveSeconds < start.Configuration.Definition.Lines.Max(line => line.EndAfterSeconds) ||
                result.FinalConfirmations.Length != start.Configuration.Definition.Lines.Length ||
                result.FinalConfirmations.Any(item => item.Evidence == RecipeRampConfirmationEvidence.TransportAccepted))
                throw new InvalidDataException("Conclusão exige tempo ativo completo e confirmação de todos os destinos.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(result.Reason)) throw new InvalidDataException("Interrupção exige motivo.");
            if (result.Status == RecipeRampTerminalStatus.EmergencyStopped)
            {
                if (result.ReturnOutcome != RecipeRampReturnOutcome.SuppressedForEmergency)
                    throw new InvalidDataException("Emergência não permite restaurar comandos da rampa.");
            }
            else if (result.ReturnOutcome != RecipeRampReturnOutcome.Failed && result.ReturnOutcome !=
                (start.Configuration.Definition.CancellationPolicy == RampCancellationPolicy.RestoreSnapshot
                    ? RecipeRampReturnOutcome.RestoredSnapshot : RecipeRampReturnOutcome.HeldLastReferences))
                throw new InvalidDataException("Retorno da rampa incompatível com a política de cancelamento.");
        }
    }
}
