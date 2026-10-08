using System.IO;
using System.Text.Json;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.Services.Recipes;

public sealed record RecipeRampStartCheckpoint(int SchemaVersion, Guid InvocationId,
    RecipeRampBlockConfiguration Configuration, RecipeRampInitialState InitialState);

/// <summary>Durable start receipt. A saved receipt never authorizes automatic restart or actuation.</summary>
public sealed partial class RecipeRampCheckpointStore(string rootDirectory, BackgroundFileWriter writer)
{
    private readonly string _root = Path.GetFullPath(rootDirectory);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<RecipeRampStartCheckpoint> PersistStartAsync(RecipeRampStartCheckpoint checkpoint,
        CancellationToken cancellation = default)
    {
        // Freeze the caller's payload before awaiting or queueing any writes.
        var json = JsonSerializer.Serialize(checkpoint);
        var frozen = Parse(json);
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var directory = DirectoryFor(frozen.InitialState.ExecutionId, frozen.InvocationId);
            Directory.CreateDirectory(directory);
            using var lease = new FileStream(Path.Combine(directory, "checkpoint.lease"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            await writer.FlushDurableAsync(directory).ConfigureAwait(false);
            var path = Path.Combine(directory, "start.json");
            if (File.Exists(path))
            {
                if (File.ReadAllText(path) != json)
                    throw new InvalidOperationException("Invocação da rampa já possui outra captura inicial.");
            }
            else writer.WriteAllTextAtomic(path, json);
            // Once queued, finish durability even if cancellation arrives: no ambiguous successful receipt.
            await writer.FlushDurableAsync(directory).ConfigureAwait(false);
            if (File.ReadAllText(path) != json) throw new IOException("Captura inicial não confirmada na leitura.");
            return Parse(File.ReadAllText(path));
        }
        finally { _gate.Release(); }
    }

    public RecipeRampStartCheckpoint? ReadStart(Guid executionId, Guid invocationId)
    {
        var path = Path.Combine(DirectoryFor(executionId, invocationId), "start.json");
        if (!File.Exists(path)) return null;
        var checkpoint = Parse(File.ReadAllText(path));
        if (checkpoint.InvocationId != invocationId || checkpoint.InitialState.ExecutionId != executionId)
            throw new InvalidDataException("Captura inicial pertence a outra execução da rampa.");
        return checkpoint;
    }

    private string DirectoryFor(Guid executionId, Guid invocationId)
    {
        if (executionId == Guid.Empty || invocationId == Guid.Empty) throw new ArgumentException("Execução ou invocação ausente.");
        return Path.Combine(_root, executionId.ToString("N"), invocationId.ToString("N"));
    }

    private static RecipeRampStartCheckpoint Parse(string json)
    {
        var checkpoint = JsonSerializer.Deserialize<RecipeRampStartCheckpoint>(json)
            ?? throw new InvalidDataException("Captura inicial vazia.");
        var initial = checkpoint.InitialState ?? throw new InvalidDataException("Estado inicial ausente.");
        var configuration = checkpoint.Configuration ?? throw new InvalidDataException("Configuração ausente.");
        if (checkpoint.SchemaVersion != 1 || checkpoint.InvocationId == Guid.Empty || initial.SnapshotId == Guid.Empty ||
            initial.ExecutionId == Guid.Empty || string.IsNullOrWhiteSpace(initial.NodeId) || initial.References.IsDefault || initial.Commands.IsDefault)
            throw new InvalidDataException("Identificação ou versão da captura inicial inválida.");
        configuration.Definition.Validate();
        var required = configuration.Definition.Lines.Where(line => line.StartSource == SetpointStartSource.CurrentConfirmed ||
            configuration.Definition.CancellationPolicy == RampCancellationPolicy.RestoreSnapshot).ToArray();
        if (initial.References.Length != required.Length || initial.References.Select(r => r.Variable).Distinct().Count() != required.Length)
            throw new InvalidDataException("Referências iniciais incompletas ou duplicadas.");
        var direct = required.Where(line => line.OxygenTarget != RampOxygenTarget.ActiveCascadeReference).ToArray();
        if (initial.Commands.Length != direct.Length || initial.Commands.Select(command => command.Actuator).Distinct().Count() != direct.Length)
            throw new InvalidDataException("Comandos iniciais incompletos ou duplicados.");
        foreach (var line in required)
        {
            var reference = initial.References.SingleOrDefault(r => r.Variable == line.Variable);
            var controller = line.OxygenTarget == RampOxygenTarget.ActiveCascadeReference;
            if (reference is null || reference.OxygenTarget != line.OxygenTarget || !double.IsFinite(reference.Reference) ||
                !DeviceRanges.Accepts(line.Variable, reference.Reference) || reference.RecordedUtc > initial.CapturedUtc ||
                reference.Evidence != (controller ? RampReferenceEvidence.ControllerReference : RampReferenceEvidence.TransportAccepted))
                throw new InvalidDataException("Referência inicial sem evidência válida.");
            if (controller && (initial.Controller is null || initial.Controller.ControllerId != configuration.CascadeNodeId || !initial.Controller.WasActive))
                throw new InvalidDataException("Snapshot da cascata associada ausente.");
            if (controller)
            {
                initial.Controller!.Validate();
                using var state = JsonDocument.Parse(initial.Controller.StateJson);
                if (initial.Controller.StateVersion != "recipe-cascade-v1" ||
                    !state.RootElement.TryGetProperty("Pid", out var pid) || !pid.TryGetProperty("Setpoint", out var setpoint) ||
                    !setpoint.TryGetDouble(out var value) || value != reference.Reference)
                    throw new InvalidDataException("Referência inicial diverge do snapshot da cascata.");
            }
            else
            {
                var key = RecipeRampInitialState.KeyFor(line.Variable);
                var command = initial.Commands.SingleOrDefault(item => item.Actuator == CommandActuators.ForKey(key));
                if (command is null || command.Owner != CommandOwner.Recipe ||
                    command.DesiredUpdatedUtc > initial.CapturedUtc || command.TransportUpdatedUtc != reference.RecordedUtc ||
                    RecipeAssayReturnState.Number(OpenTECCommand.Parse(command.DesiredCommandJson), key) != reference.Reference ||
                    RecipeAssayReturnState.Number(OpenTECCommand.Parse(command.TransportAcceptedCommandJson), key) != reference.Reference)
                    throw new InvalidDataException("Referência inicial diverge dos comandos aceitos.");
            }
        }
        if (!required.Any(line => line.OxygenTarget == RampOxygenTarget.ActiveCascadeReference) && initial.Controller is not null)
            throw new InvalidDataException("Snapshot de cascata não utilizado pela rampa.");
        initial.Controller?.Validate();
        _ = new LinearSetpointRampTrajectory(configuration.Definition, initial.ConfirmedStarts, RecipeRampReferenceQuantization.Quantize);
        return checkpoint;
    }
}
