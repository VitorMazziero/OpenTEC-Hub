using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.Services.Calibration;

/// <summary>
/// File-backed store for pump hose calibration profiles, written to
/// <c>&lt;Workspace&gt;\Calibracoes\BombaExterna\Perfis\&lt;nome&gt;.json</c>.
/// </summary>
public sealed class PumpCalibrationProfileStore : IPumpCalibrationProfileStore
{
    public const string DefaultProfileName = "Padrão";

    private readonly string? _configuredDirectory;
    private readonly ILogger<PumpCalibrationProfileStore>? _logger;
    private readonly Lock _ioLock = new();

    public string ProfilesDirectory => _configuredDirectory ?? AppPaths.PumpProfilesDirectory;

    public PumpCalibrationProfileStore(
        string? profilesDirectory = null,
        ILogger<PumpCalibrationProfileStore>? logger = null)
    {
        _configuredDirectory = profilesDirectory;
        _logger = logger;
    }

    public IReadOnlyList<PumpCalibrationProfileSummary> ListProfiles()
    {
        lock (_ioLock)
        {
            var dir = ProfilesDirectory;
            if (!Directory.Exists(dir))
            {
                return [];
            }

            var list = new List<PumpCalibrationProfileSummary>();
            foreach (var file in Directory.GetFiles(dir, "*" + PumpProfileFileContracts.ProfileExtension))
            {
                var nameWithoutExt = Path.GetFileNameWithoutExtension(file);
                try
                {
                    var text = File.ReadAllText(file);
                    using var doc = JsonDocument.Parse(text);
                    var root = doc.RootElement;

                    var schemaVersion = root.TryGetProperty("schemaVersion", out var svElem) && svElem.ValueKind == JsonValueKind.Number && svElem.TryGetInt32(out var sv)
                        ? sv
                        : 1;

                    var isCompatible = schemaVersion == PumpCalibrationProfile.CurrentSchemaVersion;

                    var profileId = root.TryGetProperty("profileId", out var idElem) && idElem.ValueKind == JsonValueKind.String && idElem.GetString() is { } id
                        ? id
                        : Guid.NewGuid().ToString("D");

                    var name = root.TryGetProperty("name", out var nameElem) && nameElem.ValueKind == JsonValueKind.String && nameElem.GetString() is { } n
                        ? n
                        : nameWithoutExt;

                    var createdUtc = root.TryGetProperty("createdUtc", out var crElem) && crElem.ValueKind == JsonValueKind.String && crElem.TryGetDateTimeOffset(out var cr)
                        ? cr
                        : File.GetCreationTimeUtc(file);

                    var modifiedUtc = root.TryGetProperty("modifiedUtc", out var modElem) && modElem.ValueKind == JsonValueKind.String && modElem.TryGetDateTimeOffset(out var mod)
                        ? mod
                        : File.GetLastWriteTimeUtc(file);

                    var pointCount = root.TryGetProperty("calibrationPoints", out var ptsElem) && ptsElem.ValueKind == JsonValueKind.Array
                        ? ptsElem.GetArrayLength()
                        : 0;

                    var transFlow = root.TryGetProperty("transitionFlowMlMin", out var qElem) && qElem.ValueKind == JsonValueKind.Number && qElem.TryGetDouble(out var q) ? q : 0.0;
                    var transSpeed = root.TryGetProperty("transitionSpeedUnits", out var sElem) && sElem.ValueKind == JsonValueKind.Number && sElem.TryGetDouble(out var s) ? s : 0.0;
                    var lowSlope = root.TryGetProperty("lowSlope", out var lsElem) && lsElem.ValueKind == JsonValueKind.Number && lsElem.TryGetDouble(out var ls) ? ls : 0.0;
                    var highSlope = root.TryGetProperty("highSlope", out var hsElem) && hsElem.ValueKind == JsonValueKind.Number && hsElem.TryGetDouble(out var hs) ? hs : 0.0;

                    DateTimeOffset? lastAppliedUtc = root.TryGetProperty("lastAppliedUtc", out var laElem) && laElem.ValueKind == JsonValueKind.String && laElem.TryGetDateTimeOffset(out var la)
                        ? la
                        : null;

                    list.Add(new PumpCalibrationProfileSummary(
                        name,
                        profileId,
                        createdUtc,
                        modifiedUtc,
                        pointCount,
                        transFlow,
                        transSpeed,
                        lowSlope,
                        highSlope,
                        schemaVersion,
                        isCompatible,
                        lastAppliedUtc));
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Falha ao ler perfil de calibração '{File}'. O arquivo corrompido foi ignorado na listagem.", file);
                }
            }

            return [.. list.OrderByDescending(p => p.ModifiedUtc)];
        }
    }

    public PumpCalibrationProfile? LoadProfile(string name)
    {
        if (!PumpProfileFileContracts.ValidateProfileName(name, out _))
        {
            return null;
        }

        lock (_ioLock)
        {
            var path = Path.Combine(ProfilesDirectory, PumpProfileFileContracts.ProfileFileName(name));
            if (!File.Exists(path))
            {
                return null;
            }

            var text = File.ReadAllText(path);
            try
            {
                using (var doc = JsonDocument.Parse(text))
                {
                    if (!doc.RootElement.TryGetProperty("schemaVersion", out var svElem) ||
                        !svElem.TryGetInt32(out var sv) ||
                        sv != PumpCalibrationProfile.CurrentSchemaVersion)
                    {
                        throw new InvalidOperationException(
                            $"O perfil '{name}' não pertence ao formato atual de calibração polinomial (schema {PumpCalibrationProfile.CurrentSchemaVersion}).");
                    }
                }

                var profile = PumpProfileFileContracts.DeserializeProfile(text);
                if (profile is null)
                {
                    return null;
                }

                profile = profile with { Name = name.Trim() };
                return profile;
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"O perfil '{name}' contém dados inválidos ou corrompidos.", ex);
            }
        }
    }

    public void SaveProfile(PumpCalibrationProfile profile, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (!PumpProfileFileContracts.ValidateProfileName(profile.Name, out var nameError))
        {
            throw new ArgumentException(nameError, nameof(profile));
        }

        if (profile.HasFittedCurve)
        {
            var curve = profile.ToCurve();
            if (!curve.Validate(out var curveError))
            {
                throw new ArgumentException($"Parâmetros da curva de calibração inválidos: {curveError}", nameof(profile));
            }
        }

        lock (_ioLock)
        {
            var dir = ProfilesDirectory;
            Directory.CreateDirectory(dir);
            var filePath = Path.Combine(dir, PumpProfileFileContracts.ProfileFileName(profile.Name));

            if (File.Exists(filePath))
            {
                if (!overwrite)
                {
                    throw new InvalidOperationException(
                        $"O perfil '{profile.Name}' já existe. Especifique overwrite=true para sobrescrevê-lo.");
                }

                // A first-deployment profile must belong to the one current schema.
                // Do not silently replace an older or otherwise incompatible format.
                try
                {
                    var existingText = File.ReadAllText(filePath);
                    using var doc = JsonDocument.Parse(existingText);
                    if (doc.RootElement.TryGetProperty("schemaVersion", out var svElem) &&
                        svElem.TryGetInt32(out var sv) &&
                        sv != PumpCalibrationProfile.CurrentSchemaVersion)
                    {
                        throw new InvalidOperationException(
                            $"Não é permitido sobrescrever o perfil '{profile.Name}' pois ele não pertence ao schema atual ({sv} != {PumpCalibrationProfile.CurrentSchemaVersion}).");
                    }
                }
                catch (JsonException)
                {
                    // If file is already corrupt, overwriting it with a valid profile is permitted when overwrite=true.
                }
            }

            var updatedProfile = profile with
            {
                Name = profile.Name.Trim(),
                ModifiedUtc = DateTimeOffset.UtcNow,
            };

            var json = PumpProfileFileContracts.SerializeProfile(updatedProfile);
            WriteAllTextAtomic(filePath, json);
        }
    }

    public bool DeleteProfile(string name)
    {
        if (!PumpProfileFileContracts.ValidateProfileName(name, out _))
        {
            return false;
        }

        lock (_ioLock)
        {
            var filePath = Path.Combine(ProfilesDirectory, PumpProfileFileContracts.ProfileFileName(name));
            if (!File.Exists(filePath))
            {
                return false;
            }

            File.Delete(filePath);
            return true;
        }
    }

    public bool ProfileExists(string name)
    {
        if (!PumpProfileFileContracts.ValidateProfileName(name, out _))
        {
            return false;
        }

        lock (_ioLock)
        {
            return File.Exists(Path.Combine(ProfilesDirectory, PumpProfileFileContracts.ProfileFileName(name)));
        }
    }

    private static void WriteAllTextAtomic(string targetPath, string content)
    {
        var dir = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(dir);
        var tempPath = targetPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tempPath, content, System.Text.Encoding.UTF8);

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(tempPath, targetPath, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < 5)
                {
                    Thread.Sleep(50 * attempt);
                }
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* best effort */ }
            }
        }
    }
}
