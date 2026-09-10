using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OpenTECHub.Services.KlaTesting;

public static class KlaTestFileContracts
{
    private static readonly Regex RunFolderPattern = new(
        @"^N(?<rpm>\d{4})_Q(?<qint>\d{2})p(?<qdec>\d{2})_Rep(?<rep>\d{2})(?:_Tentativa\d{2})?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    public const string TestManifestFileName = "teste.json";
    public const string ConditionTableFileName = "tabela-condicoes.json";
    public const string EventLogFileName = "eventos.jsonl";
    public const string GlobalSeriesFileName = "serie-global.csv";
    public const string ResultsSummaryFileName = "resumo-resultados.csv";
    public const string RunsDirectoryName = "Corridas";
    public const string RunRawDataFileName = "dados-brutos.csv";
    public const string RunAnalysisFileName = "analise.json";
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
            error = "O nome do teste não pode ser vazio.";
            return false;
        }

        if (name.EndsWith('.') || name.EndsWith(' ') || name.StartsWith(' '))
        {
            error = "O nome do teste não pode iniciar ou terminar com ponto ou espaço.";
            return false;
        }

        var trimmed = name.Trim();
        if (trimmed.Length is < 2 or > 100)
        {
            error = "O nome do teste deve ter entre 2 e 100 caracteres.";
            return false;
        }

        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            trimmed.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0)
        {
            error = "O nome do teste contém caracteres inválidos para pastas.";
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

    public static string FormatRunFolderName(double agitationRpm, double airflowLpm, int replicateNumber)
    {
        var rpm = (int)Math.Round(Math.Clamp(agitationRpm, 0, 9999));
        var qTotalHundredths = (int)Math.Round(Math.Clamp(airflowLpm, 0, 99.99) * 100);
        var qInt = qTotalHundredths / 100;
        var qDec = qTotalHundredths % 100;
        var rep = Math.Clamp(replicateNumber, 1, 99);

        return $"N{rpm:D4}_Q{qInt:D2}p{qDec:D2}_Rep{rep:D2}";
    }

    public static bool TryParseRunFolderName(string? folderName, out double agitationRpm, out double airflowLpm, out int replicateNumber)
    {
        agitationRpm = 0;
        airflowLpm = 0;
        replicateNumber = 0;
        var match = RunFolderPattern.Match(folderName ?? "");
        if (!match.Success ||
            !int.TryParse(match.Groups["rpm"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rpm) ||
            !int.TryParse(match.Groups["qint"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var qInt) ||
            !int.TryParse(match.Groups["qdec"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var qDec) ||
            !int.TryParse(match.Groups["rep"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rep))
        {
            return false;
        }

        agitationRpm = rpm;
        airflowLpm = qInt + qDec / 100.0;
        replicateNumber = rep;
        return true;
    }

    public static string SerializeTestDocument(KlaTestDocument doc) =>
        JsonSerializer.Serialize(doc, JsonOptions);

    public static KlaTestDocument? DeserializeTestDocument(string json) =>
        JsonSerializer.Deserialize<KlaTestDocument>(json, JsonOptions);

    public static string SerializeConditionTable(IReadOnlyList<KlaTestCondition> conditions) =>
        JsonSerializer.Serialize(conditions, JsonOptions);

    public static List<KlaTestCondition>? DeserializeConditionTable(string json) =>
        JsonSerializer.Deserialize<List<KlaTestCondition>>(json, JsonOptions);

    public static string SerializeAnalysis(KlaAnalysisRevision analysis) =>
        JsonSerializer.Serialize(analysis, JsonOptions);

    public static KlaAnalysisRevision? DeserializeAnalysis(string json) =>
        JsonSerializer.Deserialize<KlaAnalysisRevision>(json, JsonOptions);

    /// <summary>
    /// Column list of <c>serie-global.csv</c>.
    /// </summary>
    /// <remarks>
    /// <c>TemperatureC</c> and <c>RpmMeasured</c> were appended in schema 2. Appending, rather
    /// than inserting them beside the readings they belong with, is what keeps every column a
    /// v1 file already had at the index its readers use.
    /// </remarks>
    public static string FormatGlobalSeriesHeader() =>
        "TimestampUtc,MonotonicSeconds,TestId,RunId,ConditionId,Replicate,Phase,DORaw,DOFiltered,DOMin,DOMax,FlowMeasured,FlowSetpoint,AgitationSetpoint,Valve1,Valve2,VFlow,CommandId,CommandAck,CommandPending,SettingsRevision,EventCode,EventDetail,TemperatureC,RpmMeasured";

    public static string FormatGlobalSeriesRow(KlaGlobalSeriesSample s)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:O},{1:F3},{2},{3},{4},{5},{6},{7:F2},{8:F2},{9:F1},{10:F1},{11:F2},{12:F2},{13:F0},{14},{15},{16},{17},{18},{19},{20},{21},{22},{23},{24}",
            s.TimestampUtc,
            s.MonotonicSeconds,
            s.TestId,
            s.RunId.HasValue ? s.RunId.Value.ToString() : "",
            s.ConditionId.HasValue ? s.ConditionId.Value.ToString() : "",
            s.Replicate.HasValue ? s.Replicate.Value.ToString(CultureInfo.InvariantCulture) : "",
            s.Phase,
            s.DORaw,
            s.DOFiltered,
            s.DOMin,
            s.DOMax,
            s.FlowMeasured,
            s.FlowSetpoint,
            s.AgitationSetpoint,
            s.Valve1 ? 1 : 0,
            s.Valve2 ? 1 : 0,
            s.VFlow ? 1 : 0,
            s.CommandId.HasValue ? s.CommandId.Value.ToString(CultureInfo.InvariantCulture) : "",
            s.CommandAck.HasValue ? s.CommandAck.Value.ToString(CultureInfo.InvariantCulture) : "",
            s.CommandPending ? 1 : 0,
            s.SettingsRevision,
            EscapeCsv(s.EventCode),
            EscapeCsv(s.EventDetail),
            FormatOptional(s.TemperatureC, "F2"),
            FormatOptional(s.RpmMeasured, "F1"));
    }

    /// <inheritdoc cref="FormatGlobalSeriesHeader"/>
    public static string FormatRawDataHeader() =>
        "TimestampUtc,RelativeSeconds,Phase,DORaw,DOFiltered,FlowMeasured,FlowSetpoint,AgitationSetpoint,Valve1,Valve2,VFlow,TemperatureC,RpmMeasured";

    public static string FormatRawDataRow(KlaRawDataPoint p)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:O},{1:F3},{2},{3:F2},{4:F2},{5:F2},{6:F2},{7:F0},{8},{9},{10},{11},{12}",
            p.TimestampUtc,
            p.RelativeSeconds,
            p.Phase,
            p.DORaw,
            p.DOFiltered,
            p.FlowMeasured,
            p.FlowSetpoint,
            p.AgitationSetpoint,
            p.Valve1 ? 1 : 0,
            p.Valve2 ? 1 : 0,
            p.VFlow ? 1 : 0,
            FormatOptional(p.TemperatureC, "F2"),
            FormatOptional(p.RpmMeasured, "F1"));
    }

    public static string FormatResultsSummaryHeader() =>
        "ConditionId,AgitationRpm,AirflowLpm,RequestedReplicates,CompletedReplicates,AcceptedReplicates,MeanKlaPerHour,StdDevKlaPerHour,MeanR2";

    public static string FormatRunResultHeader() =>
        "RunId,ConditionId,Replicate,AgitationRpm,AirflowLpm,CeqPercent,TStartSeconds,TEndSeconds,KlaPerHour,SlopeStandardError,Confidence95Low,Confidence95High,R2,RMSE,Quality,WarningJustification,RawDataSha256";

    public static string FormatRunResultRow(KlaTestRun run, KlaAnalysisRevision a)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0},{1},{2},{3:F0},{4:F2},{5:F2},{6:F2},{7:F2},{8:F2},{9:F4},{10:F2},{11:F2},{12:F4},{13:F4},{14},{15},{16}",
            run.RunId,
            run.ConditionId,
            run.ReplicateNumber,
            run.AgitationRpm,
            run.AirflowLpm,
            a.CeqPercent,
            a.TStartSeconds,
            a.TEndSeconds,
            a.KlaPerHour,
            a.SlopeStandardError,
            a.ConfidenceInterval95Low,
            a.ConfidenceInterval95High,
            a.AnalysisR2,
            a.AnalysisRmse,
            a.Quality,
            EscapeCsv(a.WarningJustification ?? ""),
            a.RawDataSha256);
    }

    public static string FormatEventLogLine(KlaTestEventLogEntry entry) =>
        JsonSerializer.Serialize(entry, JsonOptions);

    public static string ComputeFileSha256(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return "";
        }

        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexStringLower(hash);
    }

    public static string ComputeStringSha256(string content)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Writes an optional reading, or an empty cell when it was never taken.
    /// </summary>
    /// <remarks>
    /// The empty cell is the point: a zero here would read as a real 0 °C or a stopped shaft,
    /// and the analysis has no way to tell that apart from an absent probe afterwards.
    /// </remarks>
    private static string FormatOptional(double? value, string format) =>
        value.HasValue && double.IsFinite(value.Value)
            ? value.Value.ToString(format, CultureInfo.InvariantCulture)
            : "";

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
