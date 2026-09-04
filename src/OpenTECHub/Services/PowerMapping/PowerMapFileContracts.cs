using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OpenTECHub.Services.PowerMapping;

/// <summary>
/// File names, JSON/CSV serialization, atomic writes and schema contracts for Power Maps.
/// </summary>
public static class PowerMapFileContracts
{
    public const string MapManifestFileName = "mapa-potencia.json";
    public const string ImpellerComparisonFileName = "comparacao-impelidores.json";
    public const string KlaCorrelationSummaryFileName = "correlacao-kla.csv";
    public const string SurfaceGridCsvFileName = "malha-superficie.csv";

    public const string MapExtension = ".pmap.json";
    public const string ComparisonExtension = ".pcomp.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    public static bool ValidateMapName(string? name, out string? error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "O nome do mapa não pode ficar em branco.";
            return false;
        }

        var trimmed = name.Trim();
        if (trimmed.Length is < 3 or > 80)
        {
            error = "O nome do mapa deve ter entre 3 e 80 caracteres.";
            return false;
        }

        if (trimmed.IndexOfAny(InvalidFileNameChars) >= 0)
        {
            error = "O nome do mapa contém caracteres inválidos para o sistema de arquivos.";
            return false;
        }

        if (trimmed.EndsWith('.') || trimmed.EndsWith(' '))
        {
            error = "O nome do mapa não pode terminar com ponto ou espaço.";
            return false;
        }

        if (ReservedNames.Contains(trimmed))
        {
            error = "O nome do mapa utiliza uma palavra reservada do sistema.";
            return false;
        }

        error = null;
        return true;
    }

    public static string SanitizeFolderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Mapa_" + DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss");
        }

        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Trim())
        {
            if (InvalidFileNameChars.Contains(ch) || ch is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(ch);
            }
        }

        var result = sb.ToString().Trim(' ', '.');
        return string.IsNullOrWhiteSpace(result)
            ? "Mapa_" + DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss")
            : result;
    }

    public static string SerializeMapDocument(PowerMapDocument document) =>
        JsonSerializer.Serialize(document, JsonOptions);

    public static PowerMapDocument? DeserializeMapDocument(string json) =>
        JsonSerializer.Deserialize<PowerMapDocument>(json, JsonOptions);

    public static string SerializeComparisonDocument(ImpellerComparisonDocument document) =>
        JsonSerializer.Serialize(document, JsonOptions);

    public static ImpellerComparisonDocument? DeserializeComparisonDocument(string json) =>
        JsonSerializer.Deserialize<ImpellerComparisonDocument>(json, JsonOptions);

    public static string ComputeSha256(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static void WriteAllTextAtomic(string targetPath, string content)
    {
        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = Path.Combine(directory ?? ".", $".tmp_{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tempPath, content, new UTF8Encoding(false));

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tempPath, targetPath, overwrite: true);
                break;
            }
            catch (IOException) when (attempt < 5)
            {
                System.Threading.Thread.Sleep(20);
            }
            catch
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                }

                throw;
            }
        }
    }

    public static string BuildKlaCorrelationCsv(KlaCorrelationResult result, IReadOnlyList<KlaPowerPair> pairs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Modelo van 't Riet: kLa = K * (P/V)^alpha * (vs)^beta");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"# Ajuste_valido={result.HasFit.ToString().ToLowerInvariant()}; motivo={result.FailureReason ?? ""}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"# K={result.K:F6}; alpha={result.Alpha:F4}; beta={result.Beta:F4}; R2={result.R2:F4}; RMSE={result.RootMeanSquareError:F4}"));
        sb.AppendLine("Agitacao_rpm,Vazao_Lpm,vs_m_s,P_liq_W,P_V_W_m3,kLa_medido_h1,IC95_h1,kLa_previsto_h1,Residuo_h1,ErroRelativo_pct");

        foreach (var p in pairs)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{p.AgitationRpm:F1},{p.GasFlowLpm:F2},{p.SuperficialVelocityMs:F5},{p.NetPowerW:F3},{p.VolumetricPowerWm3:F2},{p.KlaPerHour:F3},{p.ConfidenceInterval95:F3},{(p.PredictedKlaPerHour.HasValue ? p.PredictedKlaPerHour.Value.ToString("F3", CultureInfo.InvariantCulture) : "")},{(p.Residual.HasValue ? p.Residual.Value.ToString("F3", CultureInfo.InvariantCulture) : "")},{(p.RelativeErrorFraction.HasValue ? (p.RelativeErrorFraction.Value * 100.0).ToString("F2", CultureInfo.InvariantCulture) : "")}"));
        }

        return sb.ToString();
    }

    public static string BuildSurfaceGridCsv(PowerMapSurfaceData surface)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Agitacao_rpm,Vazao_Lpm,P_liq_W,P_V_W_m3,PG_P0");

        for (var iN = 0; iN < surface.ResolutionN; iN++)
        {
            var rpm = surface.RpmGrid[iN];
            for (var iQ = 0; iQ < surface.ResolutionQg; iQ++)
            {
                var q = surface.FlowGrid[iQ];
                var idx = surface.GetIndex(iN, iQ);

                var pNet = idx < surface.PNetSurface.Length ? surface.PNetSurface[idx] : null;
                var pv = idx < surface.PVolumetricSurface.Length ? surface.PVolumetricSurface[idx] : null;
                var pRatio = idx < surface.PowerRatioSurface.Length ? surface.PowerRatioSurface[idx] : null;

                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"{rpm:F1},{q:F2},{(pNet.HasValue ? pNet.Value.ToString("F3", CultureInfo.InvariantCulture) : "")},{(pv.HasValue ? pv.Value.ToString("F2", CultureInfo.InvariantCulture) : "")},{(pRatio.HasValue ? pRatio.Value.ToString("F4", CultureInfo.InvariantCulture) : "")}"));
            }
        }

        return sb.ToString();
    }
}
