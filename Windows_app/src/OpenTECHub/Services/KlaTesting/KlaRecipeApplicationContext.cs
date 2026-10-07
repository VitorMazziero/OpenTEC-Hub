using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.Services.KlaTesting;

/// <summary>Stable installation identity and explicit cultivation; neither is a qualification.</summary>
public sealed class KlaRecipeApplicationContext
{
    private sealed record Document(int SchemaVersion, string InstallationId, string? CultivationId);
    private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly string _path;
    private readonly BackgroundFileWriter _writer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _pendingWrites;
    public bool IsChangingCultivation => Volatile.Read(ref _pendingWrites) > 0;
    public string InstallationId { get; }
    public string? CultivationId { get; private set; }

    public KlaRecipeApplicationContext(string root, BackgroundFileWriter writer)
    {
        _writer = writer;
        Directory.CreateDirectory(root);
        _path = Path.Combine(Path.GetFullPath(root), "context.json");
        using var lease = new FileStream(_path + ".lease", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var document = File.Exists(_path) ? Read() : new Document(1, Guid.NewGuid().ToString("D"), null);
        Validate(document);
        if (!File.Exists(_path))
        {
            writer.WriteAllTextAtomic(_path, JsonSerializer.Serialize(document, Options));
            writer.FlushDurableAsync(Path.GetDirectoryName(_path)!).GetAwaiter().GetResult();
            if (Read() != document) throw new IOException("Identidade da instalação não foi gravada.");
        }
        InstallationId = document.InstallationId;
        CultivationId = document.CultivationId;
    }

    public async Task SetCultivationAsync(string? cultivationId)
    {
        var next = new Document(1, InstallationId, string.IsNullOrWhiteSpace(cultivationId) ? null : cultivationId.Trim());
        Validate(next);
        Interlocked.Increment(ref _pendingWrites);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var lease = new FileStream(_path + ".lease", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var current = Read(); Validate(current);
            if (current.InstallationId != InstallationId || current.CultivationId != CultivationId)
                throw new InvalidOperationException("O contexto do cultivo mudou em outra instância; reabra antes de alterá-lo.");
            _writer.WriteAllTextAtomic(_path, JsonSerializer.Serialize(next, Options));
            await _writer.FlushDurableAsync(Path.GetDirectoryName(_path)!).ConfigureAwait(false);
            if (Read() != next) throw new IOException("Identificação do cultivo não foi gravada.");
            CultivationId = next.CultivationId;
        }
        finally { _gate.Release(); Interlocked.Decrement(ref _pendingWrites); }
    }

    private Document Read()
    {
        var json = File.ReadAllText(_path);
        using var parsed = JsonDocument.Parse(json);
        var properties = parsed.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        if (properties.Distinct(StringComparer.Ordinal).Count() != properties.Length)
            throw new InvalidDataException("Contexto possui campos duplicados.");
        return JsonSerializer.Deserialize<Document>(json, Options)
            ?? throw new InvalidDataException("Contexto de ensaio automático vazio.");
    }
    private static void Validate(Document document)
    {
        if (document.SchemaVersion != 1 || !Guid.TryParseExact(document.InstallationId, "D", out var id) || id == Guid.Empty ||
            document.CultivationId is { } cultivation && (string.IsNullOrWhiteSpace(cultivation) || cultivation.Length > 200 || cultivation.Any(char.IsControl)))
            throw new InvalidDataException("Contexto de instalação/cultivo inválido.");
    }
}
