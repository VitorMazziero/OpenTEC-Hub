using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.Services.Recipes;

/// <summary>Immutable per-slot transitions. A reopened Started slot is history, never permission to redispatch.</summary>
public sealed class RecipePeriodicJournal(string root, Guid recipeRunId, string recipeSha256,
    BackgroundFileWriter writer, TimeProvider time)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private sealed record Entry(int SchemaVersion, Guid RecipeRunId, string RecipeSha256,
        DateTimeOffset SavedUtc, RecipePeriodicSlotRecord Record);
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    private string Folder(Guid scheduleId)
    {
        if (recipeRunId == Guid.Empty || scheduleId == Guid.Empty) throw new ArgumentException("Execução sem identidade.");
        if (recipeSha256 is null || recipeSha256.Length != 64 || !recipeSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Hash da receita inválido.");
        return Path.Combine(Path.GetFullPath(root), recipeRunId.ToString("N"), scheduleId.ToString("N"));
    }

    public async Task RecordAsync(RecipePeriodicSlotRecord record)
    {
        record.Invocation.Validate(); ContractGuard.Defined(record.State); ContractGuard.NonNegative(record.ElapsedSeconds);
        if (record.ElapsedSeconds < record.Invocation.Schedule.DueAfterSeconds(record.Invocation.SlotIndex))
            throw new ArgumentException("Slot registrado antes do prazo.");
        var folder = Folder(record.Invocation.ScheduleRunId);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(folder);
            using var lease = new FileStream(Path.Combine(folder, "periodic.lease"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            await writer.FlushDurableAsync(folder).ConfigureAwait(false);
            var entries = ReadEntries(record.Invocation.ScheduleRunId);
            foreach (var previous in entries)
                if (JsonSerializer.Serialize(previous.Record.Invocation with { SlotIndex = 0 }, Options) !=
                    JsonSerializer.Serialize(record.Invocation with { SlotIndex = 0 }, Options))
                    throw new InvalidDataException("Definição da agenda mudou durante execução.");
            var existing = entries.SingleOrDefault(e => e.Record.Invocation.SlotIndex == record.Invocation.SlotIndex && e.Record.State == record.State);
            if (existing is not null)
            {
                if (JsonSerializer.Serialize(existing.Record, Options) != JsonSerializer.Serialize(record, Options))
                    throw new InvalidDataException("Slot já possui outro registro para esta transição.");
                if (record.State == RecipePeriodicSlotState.Started)
                    throw new InvalidOperationException("Slot já iniciado; histórico não autoriza novo despacho.");
                return;
            }
            var history = entries.Where(e => e.Record.Invocation.SlotIndex == record.Invocation.SlotIndex).ToArray();
            if (record.State is RecipePeriodicSlotState.Started or RecipePeriodicSlotState.Skipped ? history.Length != 0 :
                history.Length != 1 || history[0].Record.State != RecipePeriodicSlotState.Started ||
                history[0].Record.ElapsedSeconds > record.ElapsedSeconds)
                throw new InvalidDataException("Transição periódica fora de ordem.");
            var path = Path.Combine(folder, $"slot-{record.Invocation.SlotIndex:D20}-{record.State}.json");
            var entry = new Entry(1, recipeRunId, recipeSha256, time.GetUtcNow(), record);
            var json = JsonSerializer.Serialize(entry, Options);
            writer.WriteAllTextAtomic(path, json);
            await writer.FlushDurableAsync(folder).ConfigureAwait(false);
            if (JsonSerializer.Serialize(ReadEntry(path), Options) != json) throw new IOException("Slot não confirmado na leitura.");
        }
        finally { _gate.Release(); }
    }

    public IReadOnlyList<RecipePeriodicSlotRecord> Read(Guid scheduleId) => ReadEntries(scheduleId)
        .OrderBy(e => e.Record.Invocation.SlotIndex).ThenBy(e => e.Record.State == RecipePeriodicSlotState.Started ? 0 : 1)
        .Select(e => e.Record).ToArray();

    private Entry[] ReadEntries(Guid scheduleId)
    {
        var folder = Folder(scheduleId);
        if (!Directory.Exists(folder)) return [];
        var entries = Directory.GetFiles(folder, "slot-*.json").Select(path =>
        {
            var entry = ReadEntry(path);
            if (entry.Record.Invocation.ScheduleRunId != scheduleId ||
                Path.GetFileName(path) != $"slot-{entry.Record.Invocation.SlotIndex:D20}-{entry.Record.State}.json")
                throw new InvalidDataException("Arquivo pertence a outro slot.");
            return entry;
        }).ToArray();
        foreach (var group in entries.GroupBy(e => e.Record.Invocation.SlotIndex))
        {
            var records = group.Select(e => e.Record).ToArray();
            var start = records.SingleOrDefault(r => r.State == RecipePeriodicSlotState.Started);
            if (records.Length > 2 || records.Select(r => r.State).Distinct().Count() != records.Length ||
                start is null && (records.Length != 1 || records[0].State != RecipePeriodicSlotState.Skipped) ||
                start is not null && records.Any(r => r.State == RecipePeriodicSlotState.Skipped || r.ElapsedSeconds < start.ElapsedSeconds))
                throw new InvalidDataException("Histórico do slot inválido.");
        }
        if (entries.Select(e => JsonSerializer.Serialize(e.Record.Invocation with { SlotIndex = 0 }, Options)).Distinct().Count() > 1)
            throw new InvalidDataException("Histórico mistura definições de agenda.");
        return entries;
    }

    private Entry ReadEntry(string path)
    {
        var json = File.ReadAllText(path);
        using (var document = JsonDocument.Parse(json)) RejectDuplicates(document.RootElement);
        var entry = JsonSerializer.Deserialize<Entry>(json, Options) ?? throw new InvalidDataException("Slot vazio.");
        if (entry.SchemaVersion != 1 || entry.RecipeRunId != recipeRunId || entry.RecipeSha256 != recipeSha256)
            throw new InvalidDataException("Slot pertence a outra versão ou execução.");
        entry.Record.Invocation.Validate(); ContractGuard.Defined(entry.Record.State);
        ContractGuard.NonNegative(entry.Record.ElapsedSeconds);
        if (entry.Record.ElapsedSeconds < entry.Record.Invocation.Schedule.DueAfterSeconds(entry.Record.Invocation.SlotIndex))
            throw new InvalidDataException("Slot registrado antes do prazo.");
        return entry;
    }
    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Propriedade duplicada no slot.");
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
}
