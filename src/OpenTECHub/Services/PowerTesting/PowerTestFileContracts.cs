using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OpenTECHub.Services.PowerTesting;

/// <summary>
/// File names, name validation, JSON/CSV (de)serialization for a self-contained power assay.
/// Mirrors <c>KlaTestFileContracts</c> so the two subsystems read the same way (§3.3, §6).
/// </summary>
public static class PowerTestFileContracts
{
    private static readonly Regex RunFolderPattern = new(
        @"^N(?<rpm>\d{4})_(?:Seco|Q(?<qint>\d{2})p(?<qdec>\d{2}))_Rep(?<rep>\d{2})(?:_Tentativa\d{2})?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public const string TestManifestFileName = "ensaio.json";
    public const string ConditionTableFileName = "tabela-condicoes.json";
    public const string TareFileName = "tara.json";
    public const string CalibrationFileName = "calibracao-torque.json";
    public const string EventLogFileName = "eventos.jsonl";
    public const string GlobalSeriesFileName = "serie-global.csv";
    public const string ResultsSummaryFileName = "resumo-resultados.csv";
    public const string RunsDirectoryName = "Corridas";
    public const string RunRawDataFileName = "dados-brutos.csv";
    public const string RunResultFileName = "resultado.csv";

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

    public static bool ValidateTestName(string? name, out string? error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "O nome do ensaio não pode ser vazio.";
            return false;
        }

        if (name.EndsWith('.') || name.EndsWith(' ') || name.StartsWith(' '))
        {
            error = "O nome do ensaio não pode iniciar ou terminar com ponto ou espaço.";
            return false;
        }

        var trimmed = name.Trim();
        if (trimmed.Length is < 2 or > 100)
        {
            error = "O nome do ensaio deve ter entre 2 e 100 caracteres.";
            return false;
        }

        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            trimmed.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0)
        {
            error = "O nome do ensaio contém caracteres inválidos para pastas.";
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

    /// <summary>Ungassed runs fold to <c>Seco</c>; gassed carry the flow to hundredths of L/min.</summary>
    public static string FormatRunFolderName(double agitationRpm, double? gasFlowLpm, int replicateNumber)
    {
        var rpm = (int)Math.Round(Math.Clamp(agitationRpm, 0, 9999));
        var rep = Math.Clamp(replicateNumber, 1, 99);

        if (gasFlowLpm is null)
        {
            return $"N{rpm:D4}_Seco_Rep{rep:D2}";
        }

        var qTotalHundredths = (int)Math.Round(Math.Clamp(gasFlowLpm.Value, 0, 99.99) * 100);
        var qInt = qTotalHundredths / 100;
        var qDec = qTotalHundredths % 100;
        return $"N{rpm:D4}_Q{qInt:D2}p{qdec_local(qDec)}_Rep{rep:D2}";

        static string qdec_local(int qDec) => qDec.ToString("D2", CultureInfo.InvariantCulture);
    }

    public static bool TryParseRunFolderName(string? folderName, out double agitationRpm, out double? gasFlowLpm, out int replicateNumber)
    {
        agitationRpm = 0;
        gasFlowLpm = null;
        replicateNumber = 0;
        var match = RunFolderPattern.Match(folderName ?? "");
        if (!match.Success ||
            !int.TryParse(match.Groups["rpm"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rpm) ||
            !int.TryParse(match.Groups["rep"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rep))
        {
            return false;
        }

        agitationRpm = rpm;
        replicateNumber = rep;

        if (match.Groups["qint"].Success &&
            int.TryParse(match.Groups["qint"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var qInt) &&
            int.TryParse(match.Groups["qdec"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var qDec))
        {
            gasFlowLpm = qInt + qDec / 100.0;
        }

        return true;
    }

    public static string SerializeTestDocument(PowerTestDocument doc) =>
        JsonSerializer.Serialize(doc, JsonOptions);

    public static PowerTestDocument? DeserializeTestDocument(string json) =>
        JsonSerializer.Deserialize<PowerTestDocument>(json, JsonOptions);

    public static string SerializeConditionTable(IReadOnlyList<PowerCondition> conditions) =>
        JsonSerializer.Serialize(conditions, JsonOptions);

    public static List<PowerCondition>? DeserializeConditionTable(string json) =>
        JsonSerializer.Deserialize<List<PowerCondition>>(json, JsonOptions);

    public static string SerializeTare(TareCurve tare) =>
        JsonSerializer.Serialize(tare, JsonOptions);

    public static TareCurve? DeserializeTare(string json) =>
        JsonSerializer.Deserialize<TareCurve>(json, JsonOptions);

    public static string SerializeCalibration(TorqueCalibration calibration) =>
        JsonSerializer.Serialize(calibration, JsonOptions);

    public static TorqueCalibration? DeserializeCalibration(string json) =>
        JsonSerializer.Deserialize<TorqueCalibration>(json, JsonOptions);

    public static string FormatGlobalSeriesHeader() =>
        "TimestampUtc,MonotonicSeconds,TestId,RunId,ConditionId,Replicate,Phase,RpmMeasured,TorquePercent,TorqueNm,ShaftPowerW,FlowLpm,TemperatureC,RunningMeanPowerW,RunningCi95PowerW,SampleCount,SettingsRevision,EventCode,EventDetail";

    public static string FormatGlobalSeriesRow(PowerGlobalSeriesSample s)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:O},{1:F3},{2},{3},{4},{5},{6},{7:F1},{8:F3},{9:F5},{10:F4},{11:F2},{12:F2},{13:F4},{14:F4},{15},{16},{17},{18}",
            s.TimestampUtc,
            s.MonotonicSeconds,
            s.TestId,
            s.RunId.HasValue ? s.RunId.Value.ToString() : "",
            s.ConditionId.HasValue ? s.ConditionId.Value.ToString() : "",
            s.Replicate.HasValue ? s.Replicate.Value.ToString(CultureInfo.InvariantCulture) : "",
            s.Phase,
            s.RpmMeasured,
            s.TorquePercent,
            s.TorqueNm,
            s.ShaftPowerW,
            s.FlowLpm,
            s.TemperatureC,
            s.RunningMeanPowerW,
            s.RunningCi95PowerW,
            s.SampleCount,
            s.SettingsRevision,
            EscapeCsv(s.EventCode),
            EscapeCsv(s.EventDetail));
    }

    public static string FormatRawDataHeader() =>
        "TimestampUtc,RelativeSeconds,Phase,RpmMeasured,TorquePercent,TorqueNm,ShaftPowerW,FlowLpm,Counted";

    public static string FormatRawDataRow(PowerDataPoint p)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:O},{1:F3},{2},{3:F1},{4:F3},{5:F5},{6:F4},{7:F2},{8}",
            p.TimestampUtc,
            p.RelativeSeconds,
            p.Phase,
            p.RpmMeasured,
            p.TorquePercent,
            p.TorqueNm,
            p.ShaftPowerW,
            p.FlowLpm,
            p.Counted ? 1 : 0);
    }

    public static string FormatResultsSummaryHeader() =>
        "ConditionId,AgitationRpm,GasFlowLpm,GasMode,RequestedReplicates,CompletedReplicates,AcceptedReplicates,MeanNetPowerW,StdDevNetPowerW";

    public static string FormatEventLogLine(PowerTestEventLogEntry entry) =>
        JsonSerializer.Serialize(entry, JsonOptions);

    public static string ComputeStringSha256(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Order-independent hash of the impeller set, to detect a mismatched tare (§9.2).</summary>
    public static string ComputeImpellerSetHash(PowerGeometry geometry)
    {
        var parts = new List<string>();
        foreach (var i in geometry.Impellers)
        {
            parts.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1:F4}|{2}|{3:F4}",
                i.Type, i.DiameterM, i.BladeCount, i.ClearanceM));
        }
        parts.Sort(StringComparer.Ordinal);
        return ComputeStringSha256(string.Join(";", parts));
    }

    private static string EscapeCsv(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        if (text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r'))
        {
            return $"\"{text.Replace("\"", "\"\"")}\"";
        }

        return text;
    }
}
