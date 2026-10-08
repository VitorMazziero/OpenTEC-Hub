using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Recipes;

namespace OpenTECHub.Services.KlaTesting;

public enum KlaAttemptPersistencePhase { BeforeActuation, Terminal }

/// <summary>Links the E6 reservation to the common session without creating another raw-data source.</summary>
public sealed record KlaAttemptPersistenceCheckpoint
{
    public required KlaAssayApiRequest Request { get; init; }
    public required CommandAuthorityLease Authority { get; init; }
    public required Guid TestId { get; init; }
    public required Guid RunId { get; init; }
    public required string TestFolder { get; init; }
    public required string RunFolder { get; init; }
    public required KlaAttemptPersistencePhase Phase { get; init; }
    public KlaAssayApiResult? Result { get; init; }
    public string? DecisionJson { get; init; }
    public void Validate()
    {
        Request.Validate();
        var binding = Request.RecipePulse ?? throw new ArgumentException("Checkpoint requer vínculo de receita.");
        if (TestId == Guid.Empty || RunId == Guid.Empty || !Enum.IsDefined(Phase) ||
            Authority.ExecutionId != binding.Invocation.Context.RecipeRunId || Authority.BlockId != binding.Invocation.Context.NodeId ||
            Authority.ReservationId == Guid.Empty || Authority.Generation < 0 || Authority.Resources.IsDefaultOrEmpty ||
            Authority.Resources.Distinct().Count() != Authority.Resources.Length ||
            Authority.Owner is not (CommandOwner.Recipe or CommandOwner.KlaAssay))
            throw new ArgumentException("Identidade da reserva/sessão inválida.");
        Folder(TestFolder); Folder(RunFolder);
        var snapshot = binding.Invocation.Restoration.BeforeAssay;
        if (snapshot.Actuators.Length != Authority.Resources.Length ||
            Authority.Resources.Any(a => !snapshot.Actuators.Any(s => s.Actuator == a &&
                s.OwnerExecutionId == Authority.ExecutionId.ToString() && s.Owner == CommandOwner.Recipe)))
            throw new ArgumentException("Snapshot e reserva divergem.");
        if (Phase == KlaAttemptPersistencePhase.BeforeActuation && (Result is not null || DecisionJson is not null))
            throw new ArgumentException("Preparação não pode conter resultado terminal.");
        if (Phase == KlaAttemptPersistencePhase.Terminal)
        {
            if (Authority.Owner != CommandOwner.KlaAssay || Result is null || Result.ReturnSnapshotId != snapshot.SnapshotId || Result.TestFolder != TestFolder || Result.RunFolder != RunFolder ||
                Result.PersistenceReceiptId is not null || string.IsNullOrWhiteSpace(DecisionJson))
                throw new ArgumentException("Resultado/decisão terminal ausentes ou divergentes.");
            using var decision = JsonDocument.Parse(DecisionJson);
            if (decision.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Decisão deve ser objeto JSON.");
        }
    }
    internal static void Folder(string value)
    {
        if (!KlaTestFileContracts.IsSessionFolder(value)) throw new ArgumentException("Nome de pasta inválido.");
    }
}

public sealed record KlaAttemptPersistenceReceipt(int SchemaVersion, string ReceiptId, Guid RequestId,
    Guid SnapshotId, Guid TestId, Guid RunId, KlaAttemptPersistencePhase Phase,
    DateTimeOffset PersistedUtc, string CheckpointSha256, string? RawDataSha256);

public sealed partial class KlaTestStore
{
    private static readonly JsonSerializerOptions CheckpointReadOptions = new()
    { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    private readonly SemaphoreSlim _recipeCheckpointGate = new(1, 1);
    private sealed record DurableAttemptEnvelope(int SchemaVersion, KlaAttemptPersistenceCheckpoint Checkpoint,
        KlaAttemptPersistenceReceipt Receipt);

    public async Task<KlaAttemptPersistenceReceipt> PersistRecipeAttemptAsync(KlaAttemptPersistenceCheckpoint checkpoint,
        CancellationToken ct = default)
    {
        checkpoint.Validate();
        // Freeze mutable session/result members before crossing an asynchronous boundary.
        checkpoint = JsonSerializer.Deserialize<KlaAttemptPersistenceCheckpoint>(JsonSerializer.Serialize(checkpoint), CheckpointReadOptions)
            ?? throw new InvalidDataException("Checkpoint vazio.");
        await _recipeCheckpointGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var directory = AttemptDirectory(checkpoint.TestFolder, checkpoint.RunFolder);
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Corrida comum deve ser criada antes do checkpoint.");
            using var sessionLease = new FileStream(Path.Combine(directory, "receita-checkpoint.lease"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var path = ReceiptPath(directory, checkpoint.Request.RequestId, checkpoint.Phase);
            var hash = CheckpointHash(checkpoint);
            await _writer.FlushDurableAsync(Path.Combine(RootDirectory, checkpoint.TestFolder)).ConfigureAwait(false);
            if (File.Exists(path))
            {
                var existing = ReadEnvelope(path);
                if (existing.Receipt.CheckpointSha256 != hash)
                    throw new InvalidOperationException("Mesma identidade de checkpoint com payload diferente.");
                VerifyRawData(directory, existing.Receipt);
                return existing.Receipt;
            }
            if (checkpoint.Phase == KlaAttemptPersistencePhase.Terminal)
            {
                var prepared = ReadEnvelope(ReceiptPath(directory, checkpoint.Request.RequestId, KlaAttemptPersistencePhase.BeforeActuation));
                if (prepared.Checkpoint.TestId != checkpoint.TestId || prepared.Checkpoint.RunId != checkpoint.RunId ||
                    prepared.Checkpoint.Request != checkpoint.Request && JsonSerializer.Serialize(prepared.Checkpoint.Request) != JsonSerializer.Serialize(checkpoint.Request) ||
                    prepared.Checkpoint.Authority.ReservationId != checkpoint.Authority.ReservationId ||
                    prepared.Checkpoint.Authority.ExecutionId != checkpoint.Authority.ExecutionId ||
                    prepared.Checkpoint.Authority.BlockId != checkpoint.Authority.BlockId ||
                    !prepared.Checkpoint.Authority.Resources.SequenceEqual(checkpoint.Authority.Resources) ||
                    checkpoint.Authority.Generation != prepared.Checkpoint.Authority.Generation +
                        (prepared.Checkpoint.Authority.Owner == CommandOwner.Recipe ? 1 : 0))
                    throw new InvalidOperationException("Terminal não corresponde à reserva persistida antes da atuação.");
            }
            var rawPath = GetRunRawDataPath(checkpoint.TestFolder, checkpoint.RunFolder);
            var rawHash = checkpoint.Phase == KlaAttemptPersistencePhase.Terminal
                ? KlaTestFileContracts.ComputeFileSha256(rawPath) : null;
            if (checkpoint.Phase == KlaAttemptPersistencePhase.Terminal && rawHash?.Length != 64)
                throw new InvalidDataException("Dados brutos ausentes; recibo terminal não pode ser emitido.");
            var receipt = new KlaAttemptPersistenceReceipt(1, Guid.NewGuid().ToString("N"), checkpoint.Request.RequestId,
                checkpoint.Request.RecipePulse!.Invocation.Restoration.BeforeAssay.SnapshotId, checkpoint.TestId, checkpoint.RunId,
                checkpoint.Phase, DateTimeOffset.UtcNow, hash, rawHash);
            _writer.WriteAllTextAtomic(path, JsonSerializer.Serialize(new DurableAttemptEnvelope(1, checkpoint, receipt)));
            await _writer.FlushDurableAsync(Path.Combine(RootDirectory, checkpoint.TestFolder)).ConfigureAwait(false);
            // Only return a receipt after reading back the exact committed identity and content.
            var committed = ReadEnvelope(path);
            if (committed.Receipt != receipt) throw new IOException("Recibo gravado diverge do solicitado.");
            VerifyRawData(directory, receipt);
            return receipt;
        }
        finally { _recipeCheckpointGate.Release(); }
    }

    public KlaAttemptPersistenceReceipt? ReadRecipeAttemptReceipt(string testFolder, string runFolder,
        Guid requestId, KlaAttemptPersistencePhase phase)
    {
        if (requestId == Guid.Empty || !Enum.IsDefined(phase)) throw new ArgumentException("Identidade de recibo inválida.");
        var directory = AttemptDirectory(testFolder, runFolder);
        var path = ReceiptPath(directory, requestId, phase);
        if (!File.Exists(path)) return null;
        var envelope = ReadEnvelope(path);
        if (envelope.Receipt.RequestId != requestId || envelope.Receipt.Phase != phase ||
            envelope.Checkpoint.TestFolder != testFolder || envelope.Checkpoint.RunFolder != runFolder)
            throw new InvalidDataException("Recibo pertence a outra corrida.");
        VerifyRawData(directory, envelope.Receipt);
        return envelope.Receipt;
    }

    public KlaAttemptPersistenceCheckpoint? ReadRecipeAttemptCheckpoint(string testFolder, string runFolder,
        Guid requestId, KlaAttemptPersistencePhase phase)
    {
        var receipt = ReadRecipeAttemptReceipt(testFolder, runFolder, requestId, phase);
        if (receipt is null) return null;
        var envelope = ReadEnvelope(ReceiptPath(AttemptDirectory(testFolder, runFolder), requestId, phase));
        if (envelope.Receipt != receipt) throw new IOException("Checkpoint mudou durante a leitura.");
        return envelope.Checkpoint;
    }

    private string AttemptDirectory(string testFolder, string runFolder)
    {
        KlaAttemptPersistenceCheckpoint.Folder(testFolder); KlaAttemptPersistenceCheckpoint.Folder(runFolder);
        return Path.Combine(RootDirectory, testFolder, KlaTestFileContracts.RunsDirectoryName, runFolder);
    }
    private static string ReceiptPath(string directory, Guid requestId, KlaAttemptPersistencePhase phase)
        => Path.Combine(directory, $"receita-{requestId:N}-{phase}.json");
    private static string CheckpointHash(KlaAttemptPersistenceCheckpoint checkpoint)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(checkpoint))));
    private static DurableAttemptEnvelope ReadEnvelope(string path)
    {
        var json = File.ReadAllText(path);
        using (var document = JsonDocument.Parse(json)) RejectDuplicateProperties(document.RootElement);
        var envelope = JsonSerializer.Deserialize<DurableAttemptEnvelope>(json, CheckpointReadOptions)
            ?? throw new InvalidDataException("Checkpoint vazio.");
        if (envelope.SchemaVersion != 1 || envelope.Receipt.SchemaVersion != 1)
            throw new InvalidDataException("Versão de checkpoint desconhecida.");
        envelope.Checkpoint.Validate();
        var receipt = envelope.Receipt; var checkpoint = envelope.Checkpoint;
        if (string.IsNullOrWhiteSpace(receipt.ReceiptId) || receipt.PersistedUtc == default ||
            receipt.CheckpointSha256 != CheckpointHash(checkpoint) || receipt.RequestId != checkpoint.Request.RequestId ||
            receipt.TestId != checkpoint.TestId || receipt.RunId != checkpoint.RunId || receipt.Phase != checkpoint.Phase ||
            receipt.SnapshotId != checkpoint.Request.RecipePulse!.Invocation.Restoration.BeforeAssay.SnapshotId)
            throw new InvalidDataException("Identidade ou integridade do checkpoint inválida.");
        return envelope;
    }
    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Chave duplicada no checkpoint.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }
    private static void VerifyRawData(string directory, KlaAttemptPersistenceReceipt receipt)
    {
        if (receipt.Phase == KlaAttemptPersistencePhase.Terminal)
        {
            var path = Path.Combine(directory, KlaTestFileContracts.RunRawDataFileName);
            if (receipt.RawDataSha256?.Length != 64 || KlaTestFileContracts.ComputeFileSha256(path) != receipt.RawDataSha256)
                throw new InvalidDataException("Dados brutos divergem do recibo terminal.");
        }
    }
}
