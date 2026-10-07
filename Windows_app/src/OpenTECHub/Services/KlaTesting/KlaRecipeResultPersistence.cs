using System.IO;
using System.Text.Json;

namespace OpenTECHub.Services.KlaTesting;

public sealed partial class KlaTestStore
{
    private sealed record RecipeResultEnvelope(int SchemaVersion, KlaRecipeRequest Request, KlaRecipeResult Result);

    public async Task PersistRecipeResultAsync(KlaRecipeRequest request, KlaRecipeResult result, CancellationToken ct = default)
    {
        var envelope = JsonSerializer.Deserialize<RecipeResultEnvelope>(JsonSerializer.Serialize(new RecipeResultEnvelope(1, request, result)),
            CheckpointReadOptions) ?? throw new InvalidDataException("Resultado vazio.");
        envelope.Result.ValidateAgainst(envelope.Request);
        KlaAttemptPersistenceCheckpoint.Folder(envelope.Result.SessionFolder);
        await _recipeCheckpointGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var directory = Path.Combine(RootDirectory, result.SessionFolder);
            using var lease = new FileStream(Path.Combine(directory, "receita-orquestracao.lease"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            await _writer.FlushDurableAsync(directory).ConfigureAwait(false);
            VerifyRecipeResultSelections(envelope);
            var path = RecipeResultPath(result.SessionFolder, request.Context.InvocationId);
            var json = JsonSerializer.Serialize(envelope);
            if (File.Exists(path))
            {
                if (File.ReadAllText(path) != json) throw new InvalidOperationException("Invocação já possui outro resultado terminal.");
            }
            else _writer.WriteAllTextAtomic(path, json);
            var document = LoadTest(result.SessionFolder) ?? throw new InvalidDataException("Sessão comum ausente.");
            if (document.TestId != result.SessionId) throw new InvalidDataException("Resultado pertence a outra sessão.");
            document.Status = result.Status is KlaRecipeTerminalStatus.Completed or KlaRecipeTerminalStatus.CompletedWithWarnings or KlaRecipeTerminalStatus.Inconclusive
                ? KlaTestStatus.Completed : KlaTestStatus.Interrupted;
            document.InterruptionReason = result.Reason;
            document.CompletedUtc = result.Attempts.LastOrDefault()?.DecidedUtc ?? DateTimeOffset.UtcNow;
            SaveTestManifest(document);
            UpdateResultsSummary(document.FolderName, document);
            await _writer.FlushDurableAsync(directory).ConfigureAwait(false);
            if (JsonSerializer.Serialize(ReadRecipeResultEnvelope(path)) != json)
                throw new IOException("Resultado da matriz não foi confirmado na leitura.");
        }
        finally { _recipeCheckpointGate.Release(); }
    }

    public KlaRecipeResult? ReadRecipeResult(string testFolder, Guid invocationId)
    {
        var path = RecipeResultPath(testFolder, invocationId);
        if (!File.Exists(path)) return null;
        var envelope = ReadRecipeResultEnvelope(path);
        if (envelope.Result.SessionFolder != testFolder || envelope.Request.Context.InvocationId != invocationId)
            throw new InvalidDataException("Resultado pertence a outra invocação.");
        VerifyRecipeResultSelections(envelope);
        return envelope.Result;
    }

    private void VerifyRecipeResultSelections(RecipeResultEnvelope envelope)
    {
        var manifest = Path.Combine(RootDirectory, envelope.Result.SessionFolder, KlaTestFileContracts.TestManifestFileName);
        if (!File.Exists(manifest) || KlaTestFileContracts.DeserializeTestDocument(File.ReadAllText(manifest))?.TestId != envelope.Result.SessionId)
            throw new InvalidDataException("Resultado não corresponde à sessão comum.");
        var document = KlaTestFileContracts.DeserializeTestDocument(File.ReadAllText(manifest))!;
        if (document.RecipeRequest is not null &&
            Recipes.RecipeContractSerializer.Fingerprint(document.RecipeRequest) != Recipes.RecipeContractSerializer.Fingerprint(envelope.Request))
            throw new InvalidDataException("Origem automática do manifesto diverge da solicitação executada.");
        foreach (var attempt in envelope.Result.Attempts)
        {
            var saved = ReadRecipeSelection(envelope.Result.SessionFolder, attempt.RunFolder, attempt.AttemptId)
                ?? throw new InvalidDataException("Resultado sem seleção durável correspondente.");
            var pulse = envelope.Result.Pulses.SingleOrDefault(p => p.RequestId == attempt.AttemptId);
            if (pulse is null || JsonSerializer.Serialize(pulse) != JsonSerializer.Serialize(saved.Observation.Request) ||
                JsonSerializer.Serialize(attempt) != JsonSerializer.Serialize(saved.Decision))
                throw new InvalidDataException("Resultado e seleções da matriz divergem.");
        }
    }

    private string RecipeResultPath(string testFolder, Guid invocationId)
    {
        KlaAttemptPersistenceCheckpoint.Folder(testFolder);
        if (invocationId == Guid.Empty) throw new ArgumentException("Invocação ausente.");
        return Path.Combine(RootDirectory, testFolder, $"receita-{invocationId:N}-Result.json");
    }
    private static RecipeResultEnvelope ReadRecipeResultEnvelope(string path)
    {
        var json = File.ReadAllText(path);
        using (var document = JsonDocument.Parse(json)) RejectDuplicateProperties(document.RootElement);
        var envelope = JsonSerializer.Deserialize<RecipeResultEnvelope>(json, CheckpointReadOptions)
            ?? throw new InvalidDataException("Resultado vazio.");
        if (envelope.SchemaVersion != 1) throw new InvalidDataException("Versão de resultado desconhecida.");
        envelope.Result.ValidateAgainst(envelope.Request);
        return envelope;
    }
}
