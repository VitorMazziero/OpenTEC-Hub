using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenTECHub.Services.KlaTesting;

public sealed record KlaRecipeSelectionCheckpoint
{
    public required KlaAssayApiObservation Observation { get; init; }
    public required KlaRecipeAttemptResult Decision { get; init; }
    public ImmutableArray<KlaRecipeAttemptResult> History { get; init; } = [];
    public required int RemainingCultivationAttempts { get; init; }
    public required double ElapsedBlockSeconds { get; init; }

    public void Validate()
    {
        if (History.IsDefault) throw new ArgumentException("Histórico ausente.");
        var expected = KlaRecipeAttemptDecider.Decide(Observation, History, RemainingCultivationAttempts,
            ElapsedBlockSeconds, Decision.DecidedUtc);
        if (JsonSerializer.Serialize(expected) != JsonSerializer.Serialize(Decision))
            throw new ArgumentException("Decisão diverge da política automática executada.");
        KlaAttemptPersistenceCheckpoint.Folder(Observation.Result!.TestFolder!);
        KlaAttemptPersistenceCheckpoint.Folder(Observation.Result.RunFolder!);
    }
}

public sealed record KlaRecipeSelectionReceipt(int SchemaVersion, string ReceiptId, Guid AttemptId,
    string TerminalReceiptId, string SelectionSha256);

public sealed partial class KlaTestStore
{
    private sealed record SelectionEnvelope(int SchemaVersion, KlaRecipeSelectionCheckpoint Checkpoint,
        KlaRecipeSelectionReceipt Receipt);

    public async Task<KlaRecipeSelectionReceipt> PersistRecipeSelectionAsync(KlaRecipeSelectionCheckpoint checkpoint,
        CancellationToken ct = default)
    {
        checkpoint = JsonSerializer.Deserialize<KlaRecipeSelectionCheckpoint>(JsonSerializer.Serialize(checkpoint), CheckpointReadOptions)
            ?? throw new InvalidDataException("Decisão ausente.");
        checkpoint.Validate();
        await _recipeCheckpointGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = checkpoint.Observation.Result!;
            var directory = AttemptDirectory(result.TestFolder!, result.RunFolder!);
            using var lease = new FileStream(Path.Combine(directory, "receita-checkpoint.lease"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var terminal = VerifySelectionTerminal(checkpoint);
            foreach (var previous in checkpoint.History)
            {
                var saved = ReadRecipeSelection(result.TestFolder!, previous.RunFolder, previous.AttemptId);
                if (saved is null || JsonSerializer.Serialize(saved.Decision) != JsonSerializer.Serialize(previous))
                    throw new InvalidDataException("Histórico de seleção não corresponde à decisão gravada.");
            }
            var path = SelectionPath(directory, checkpoint.Decision.AttemptId);
            var hash = SelectionHash(checkpoint);
            await _writer.FlushDurableAsync(Path.Combine(RootDirectory, result.TestFolder!)).ConfigureAwait(false);
            SelectionEnvelope envelope;
            if (File.Exists(path))
            {
                envelope = ReadSelectionEnvelope(path);
                if (envelope.Receipt.SelectionSha256 != hash || envelope.Receipt.TerminalReceiptId != terminal.ReceiptId)
                    throw new InvalidOperationException("Tentativa já possui decisão com outro payload.");
            }
            else
            {
                envelope = new(1, checkpoint, new(1, Guid.NewGuid().ToString("N"), checkpoint.Decision.AttemptId,
                    terminal.ReceiptId, hash));
                _writer.WriteAllTextAtomic(path, JsonSerializer.Serialize(envelope));
                await _writer.FlushDurableAsync(Path.Combine(RootDirectory, result.TestFolder!)).ConfigureAwait(false);
                if (JsonSerializer.Serialize(ReadSelectionEnvelope(path)) != JsonSerializer.Serialize(envelope))
                    throw new IOException("Decisão gravada diverge da solicitada.");
            }
            // The immutable decision is authoritative. A retry of this write repairs a stale projection.
            var document = LoadTest(result.TestFolder!) ?? throw new InvalidDataException("Sessão comum ausente.");
            var index = document.Runs.FindIndex(r => r.RunId == terminal.RunId);
            if (index < 0) throw new InvalidDataException("Corrida não pertence ao manifesto comum.");
            var run = document.Runs[index];
            if (run.ConditionId != checkpoint.Decision.ConditionId || run.ReplicateNumber != checkpoint.Decision.ReplicateNumber ||
                run.AttemptNumber != checkpoint.Decision.AttemptNumber)
                throw new InvalidDataException("Decisão e réplica comum divergem.");
            document.Runs[index] = run with { AutomaticDecision = checkpoint.Decision };
            foreach (var condition in document.Conditions) KlaSequence.RefreshCounters(document, condition);
            SaveTestManifest(document);
            SaveConditionsTable(document.FolderName, document.Conditions);
            UpdateResultsSummary(document.FolderName, document);
            await _writer.FlushDurableAsync(Path.Combine(RootDirectory, result.TestFolder!)).ConfigureAwait(false);
            VerifySelectionTerminal(checkpoint);
            return envelope.Receipt;
        }
        finally { _recipeCheckpointGate.Release(); }
    }

    public KlaRecipeSelectionCheckpoint? ReadRecipeSelection(string testFolder, string runFolder, Guid requestId)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("Identidade ausente.");
        var path = SelectionPath(AttemptDirectory(testFolder, runFolder), requestId);
        if (!File.Exists(path)) return null;
        var envelope = ReadSelectionEnvelope(path);
        var checkpoint = envelope.Checkpoint;
        if (checkpoint.Decision.AttemptId != requestId || checkpoint.Observation.Result!.TestFolder != testFolder ||
            checkpoint.Observation.Result.RunFolder != runFolder ||
            VerifySelectionTerminal(checkpoint).ReceiptId != envelope.Receipt.TerminalReceiptId)
            throw new InvalidDataException("Decisão pertence a outra tentativa ou recibo.");
        return checkpoint;
    }

    private KlaAttemptPersistenceReceipt VerifySelectionTerminal(KlaRecipeSelectionCheckpoint checkpoint)
    {
        var observation = checkpoint.Observation;
        var result = observation.Result!;
        var receipt = ReadRecipeAttemptReceipt(result.TestFolder!, result.RunFolder!, observation.Request.RequestId,
            KlaAttemptPersistencePhase.Terminal) ?? throw new InvalidDataException("Decisão sem recibo terminal.");
        var terminal = ReadRecipeAttemptCheckpoint(result.TestFolder!, result.RunFolder!, observation.Request.RequestId,
            KlaAttemptPersistencePhase.Terminal)!;
        if (receipt.ReceiptId != result.PersistenceReceiptId ||
            JsonSerializer.Serialize(terminal.Request) != JsonSerializer.Serialize(observation.Request) ||
            JsonSerializer.Serialize(terminal.Result) != JsonSerializer.Serialize(result with { PersistenceReceiptId = null }))
            throw new InvalidDataException("Decisão diverge do resultado e request terminais.");
        return receipt;
    }

    private static string SelectionPath(string directory, Guid attempt) => Path.Combine(directory, $"receita-{attempt:N}-Selection.json");

    private KlaRecipeAttemptResult? ReadSelectionForRun(string testFolder, string runFolder)
    {
        var paths = Directory.GetFiles(AttemptDirectory(testFolder, runFolder), "receita-*-Selection.json");
        if (paths.Length > 1) throw new InvalidDataException("Corrida contém mais de uma decisão automática.");
        if (paths.Length == 0) return null;
        var envelope = ReadSelectionEnvelope(paths[0]);
        return ReadRecipeSelection(testFolder, runFolder, envelope.Receipt.AttemptId)!.Decision;
    }
    private static string SelectionHash(KlaRecipeSelectionCheckpoint checkpoint) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(checkpoint)));
    private static SelectionEnvelope ReadSelectionEnvelope(string path)
    {
        var json = File.ReadAllText(path);
        using (var document = JsonDocument.Parse(json)) RejectDuplicateProperties(document.RootElement);
        var envelope = JsonSerializer.Deserialize<SelectionEnvelope>(json, CheckpointReadOptions)
            ?? throw new InvalidDataException("Decisão vazia.");
        envelope.Checkpoint.Validate();
        if (envelope.SchemaVersion != 1 || envelope.Receipt.SchemaVersion != 1 ||
            string.IsNullOrWhiteSpace(envelope.Receipt.ReceiptId) || string.IsNullOrWhiteSpace(envelope.Receipt.TerminalReceiptId) ||
            envelope.Receipt.AttemptId != envelope.Checkpoint.Decision.AttemptId ||
            envelope.Receipt.SelectionSha256 != SelectionHash(envelope.Checkpoint))
            throw new InvalidDataException("Identidade ou integridade da seleção inválida.");
        return envelope;
    }
}
