using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.Services.KlaTesting;

/// <summary>Explicit, immutable qualification records. Loading a file never creates qualification.</summary>
public sealed class KlaRecipeOperationalProfileStore(string root, string installationId,
    bool isIsolatedEnvironment, TimeProvider time, BackgroundFileWriter writer)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _root = Path.GetFullPath(root);
    private sealed record Envelope(int SchemaVersion, string ProfileSha256, KlaRecipeOperationalProfile Profile);
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public async Task SaveAsync(KlaRecipeOperationalProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile = JsonSerializer.Deserialize<KlaRecipeOperationalProfile>(JsonSerializer.Serialize(profile, Options), Options)!;
        ValidateInstallation(profile, time.GetUtcNow());
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_root);
            using var lease = AcquireLease();
            await writer.FlushDurableAsync(_root).ConfigureAwait(false);
            // Reject a corrupt catalog before adding another operational capability.
            ReadAllUnlocked();
            var path = Path.Combine(_root, FileName(profile));
            var envelope = new Envelope(1, Hash(JsonSerializer.Serialize(profile, Options)), profile);
            var json = JsonSerializer.Serialize(envelope, Options);
            if (File.Exists(path))
            {
                if (JsonSerializer.Serialize(ReadEntry(path), Options) != json)
                    throw new InvalidOperationException("Uma versão de perfil gravada não pode mudar de conteúdo.");
                return;
            }
            ct.ThrowIfCancellationRequested();
            writer.WriteAllTextAtomic(path, json);
            // Once queued, confirm completion independently of caller cancellation.
            await writer.FlushDurableAsync(_root).ConfigureAwait(false);
            if (JsonSerializer.Serialize(ReadEntry(path), Options) != json)
                throw new IOException("Gravação do perfil operacional não confirmada.");
        }
        finally { _gate.Release(); }
    }

    /// <summary>Includes expired history; callers must resolve current capability through the registry.</summary>
    public IReadOnlyList<KlaRecipeOperationalProfile> ReadAll()
    {
        _gate.Wait();
        try
        {
            if (!Directory.Exists(_root)) return [];
            using var lease = AcquireLease();
            return ReadAllUnlocked();
        }
        finally { _gate.Release(); }
    }

    public void RegisterAvailable(KlaRecipeOperationalProfileRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (registry.InstallationId != installationId || registry.IsIsolatedEnvironment != isIsolatedEnvironment)
            throw new InvalidOperationException("Registro e armazenamento pertencem a ambientes distintos.");
        var profiles = ReadAll(); // Validate every record, including expired history, before publishing.
        var now = time.GetUtcNow();
        registry.RegisterMany(profiles.Where(p => p.QualifiedUtc <= now && p.ValidUntilUtc > now));
    }

    private FileStream AcquireLease() => new(Path.Combine(_root, "profiles.lease"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private KlaRecipeOperationalProfile[] ReadAllUnlocked()
        => Directory.GetFiles(_root, "profile-*.json").Order(StringComparer.Ordinal)
            .Select(path => ReadEntry(path).Profile).ToArray();

    private Envelope ReadEntry(string path)
    {
        var json = File.ReadAllText(path);
        using (var document = JsonDocument.Parse(json)) RejectDuplicates(document.RootElement);
        var entry = JsonSerializer.Deserialize<Envelope>(json, Options) ?? throw new InvalidDataException("Perfil vazio.");
        if (entry.SchemaVersion != 1 || entry.Profile is null ||
            entry.ProfileSha256 != Hash(JsonSerializer.Serialize(entry.Profile, Options)))
            throw new InvalidDataException("Versão ou integridade do perfil operacional inválida.");
        // Expiry removes availability, not the historical evidence record.
        ValidateInstallation(entry.Profile, entry.Profile.QualifiedUtc);
        if (Path.GetFileName(path) != FileName(entry.Profile))
            throw new InvalidDataException("Arquivo não corresponde à identidade do perfil.");
        return entry;
    }

    private void ValidateInstallation(KlaRecipeOperationalProfile profile, DateTimeOffset now)
    {
        profile.Validate(now);
        if (!isIsolatedEnvironment || string.IsNullOrWhiteSpace(installationId) ||
            profile.Capabilities.InstallationId != installationId)
            throw new InvalidOperationException("Perfil não qualificado para o ambiente desta instalação.");
    }

    private static string FileName(KlaRecipeOperationalProfile profile)
        => "profile-" + Hash(JsonSerializer.Serialize(new[] { profile.Capabilities.InstallationId,
            profile.Capabilities.ProfileId, profile.Capabilities.ProfileVersion, profile.Template.Protocol.ToString() })) + ".json";
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Propriedade duplicada no perfil operacional.");
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
}
