using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenTECHub.Services.Calibration;

/// <summary>
/// File naming rules, JSON codec, and disk contracts for pump calibration profiles.
/// </summary>
public static class PumpProfileFileContracts
{
    public const string ProfileExtension = ".json";

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Validates a hose profile name before it reaches the file system.
    /// </summary>
    /// <remarks>
    /// Rejects empty names, leading/trailing whitespace, trailing dots, path traversal tokens (..),
    /// directory separators (/ and \), invalid filename characters, and Windows reserved device names.
    /// </remarks>
    public static bool ValidateProfileName(string? name, out string? error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "O nome do perfil de mangueira não pode ser vazio.";
            return false;
        }

        if (name.StartsWith(' ') || name.EndsWith(' ') || name.EndsWith('.'))
        {
            error = "O nome do perfil de mangueira não pode iniciar com espaço nem terminar com ponto ou espaço.";
            return false;
        }

        var trimmed = name.Trim();
        if (trimmed.Length is < 2 or > 100)
        {
            error = "O nome do perfil de mangueira deve ter entre 2 e 100 caracteres.";
            return false;
        }

        if (trimmed.Contains("..", StringComparison.Ordinal) ||
            trimmed.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0 ||
            trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "O nome do perfil contém caracteres inválidos ou tentativa de navegação de diretório.";
            return false;
        }

        if (ReservedNames.Contains(trimmed))
        {
            error = $"'{trimmed}' é um nome reservado pelo sistema operacional.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>Returns the filename for the given profile name on disk.</summary>
    public static string ProfileFileName(string name) => name.Trim() + ProfileExtension;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string SerializeProfile(PumpCalibrationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return JsonSerializer.Serialize(profile, JsonOptions);
    }

    public static PumpCalibrationProfile? DeserializeProfile(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<PumpCalibrationProfile>(json, JsonOptions);
    }
}
