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
public static class PowerTestEventCodes
{
    public const string PhaseChanged = "PhaseChanged";
    public const string SpeedSet = "SpeedSet";
    public const string SpeedSettled = "SpeedSettled";
    public const string VentOpened = "VentOpened";
    public const string VentStabilized = "VentStabilized";
    public const string GasOpened = "GasOpened";
    public const string GasClosed = "GasClosed";
    public const string SettlingStarted = "SettlingStarted";
    public const string AccumulatingStarted = "AccumulatingStarted";
    public const string TargetReached = "TargetReached";
    public const string TmaxReached = "TmaxReached";
    public const string RunCompleted = "RunCompleted";
    public const string RunAborted = "RunAborted";
    public const string FloodingDetected = "FloodingDetected";
}

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

    /// <summary>
    /// Store-wide library of named tare profiles, one JSON file per shaft.
    /// </summary>
    /// <remarks>
    /// Sits beside the assay folders rather than inside one, because a tare outlives the
    /// assay that measured it: the same shaft is used across many assays, and a bench with
    /// more than one shaft needs more than one valid tare on hand at the same time.
    /// </remarks>
    public const string TareProfilesDirectoryName = "Taras";
    public const string RunRawDataFileName = "dados-brutos.csv";
    public const string RunResultFileName = "resultado.csv";

    /// <summary>
    /// Per-sweep raw tare readings, one file per sweep inside the assay folder.
    /// </summary>
    /// <remarks>
    /// A sweep that does not converge - a rung that times out, an operator who cancels, a torque
    /// limit that trips - produces no <c>tara.json</c>, and everything it measured used to end
    /// there. One file per sweep, written as the samples arrive, means a failed attempt is still
    /// on disk to be looked at, and a second attempt never overwrites the first.
    /// </remarks>
    public const string TareRawDirectoryName = "Taras-Brutas";

    /// <summary>
    /// Ad-hoc single-point checks, one file per activation.
    /// </summary>
    /// <remarks>
    /// The single-point panel drives the same shaft with the same instrument as an assay run, so
    /// what it measures is data. It lives beside the assays rather than inside one because the
    /// panel can be used with no assay open at all.
    /// </remarks>
    public const string SinglePointDirectoryName = "Pontos-Unicos";
    public const string SinglePointManifestSuffix = ".json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// The persisted form of a model object, for equality checks on classes that have none
    /// (geometry, conditions). Two objects that would write the same JSON are the same setup.
    /// </summary>
    public static string Fingerprint<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    /// <summary>
    /// A summary row that stopped before capturing anything: no samples <em>and</em> no usable
    /// power (D-050). Both are required so that a row from an older manifest that never recorded
    /// <c>sampleCount</c> but does carry a measured power is not mistaken for one.
    /// </summary>
    public static bool IsRunWithoutCapture(PowerRunSummary run) =>
        run.SampleCount == 0 && !(run.NetPowerW is { } p && double.IsFinite(p) && p != 0.0);

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

    /// <summary>
    /// Validates a tare profile name, which becomes a file name in the profile library.
    /// </summary>
    /// <remarks>
    /// Same rules as an assay name: the name reaches the file system directly, so the
    /// reserved device names and the invalid path characters have to be rejected here
    /// rather than surfacing as an <c>IOException</c> when the operator hits save.
    /// </remarks>
    public static bool ValidateTareProfileName(string? name, out string? error)
    {
        if (!ValidateTestName(name, out error))
        {
            error = error?.Replace("do ensaio", "da tara", StringComparison.Ordinal);
            return false;
        }

        return true;
    }

    /// <summary>File name holding the profile <paramref name="name"/> in the tare library.</summary>
    public static string TareProfileFileName(string name) => name.Trim() + ".json";

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

    public static string SerializeSinglePointSession(SinglePointSession session) =>
        JsonSerializer.Serialize(session, JsonOptions);

    public static SinglePointSession? DeserializeSinglePointSession(string json) =>
        JsonSerializer.Deserialize<SinglePointSession>(json, JsonOptions);

    /// <summary>Base name of one tare sweep's raw file, unique per sweep by its start instant.</summary>
    public static string TareRawFileName(DateTimeOffset startedUtc) =>
        $"tara-{startedUtc.UtcDateTime:yyyyMMdd-HHmmss}.csv";

    /// <summary>Base name of one single-point capture, carrying its speed and flow in the clear.</summary>
    public static string SinglePointFileName(DateTimeOffset startedUtc, double targetRpm, double? gasFlowLpm)
    {
        var rpm = (int)Math.Round(Math.Clamp(targetRpm, 0, 9999));
        var stamp = startedUtc.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        if (gasFlowLpm is not { } flow || flow <= 0)
        {
            return $"ponto-{stamp}_N{rpm:D4}_Seco.csv";
        }

        var qTotalHundredths = (int)Math.Round(Math.Clamp(flow, 0, 99.99) * 100);
        return $"ponto-{stamp}_N{rpm:D4}_Q{qTotalHundredths / 100:D2}p{qTotalHundredths % 100:D2}.csv";
    }

    public static string FormatTareRawHeader() =>
        "TimestampUtc,ElapsedSeconds,PointIndex,TargetRpm,RpmMeasured,TorquePercent,Phase,Counted,Attempt";

    public static string FormatTareRawRow(TareSample sample, int pointIndex)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:O},{1:F3},{2},{3:F1},{4:F1},{5:F3},{6},{7},{8}",
            sample.TimestampUtc,
            sample.ElapsedSeconds,
            pointIndex,
            sample.TargetRpm,
            sample.RpmMeasured,
            sample.TorquePercent,
            sample.Phase,
            sample.Counted ? 1 : 0,
            sample.Attempt);
    }

    public static string FormatGlobalSeriesHeader() =>
        "TimestampUtc,MonotonicSeconds,TestId,RunId,ConditionId,Replicate,Phase,RpmMeasured,TorquePercent,TorqueNm,ShaftPowerW,FlowLpm,TemperatureC,RunningMeanPowerW,RunningCi95PowerW,SampleCount,SettingsRevision,EventCode,EventDetail,Attempt";

    public static string FormatGlobalSeriesRow(PowerGlobalSeriesSample s)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:O},{1:F3},{2},{3},{4},{5},{6},{7:F1},{8:F3},{9:F5},{10:F4},{11:F2},{12:F2},{13:F4},{14:F4},{15},{16},{17},{18},{19}",
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
            s.FlowLpm?.ToString("F2", CultureInfo.InvariantCulture) ?? "",
            s.TemperatureC?.ToString("F2", CultureInfo.InvariantCulture) ?? "",
            s.RunningMeanPowerW?.ToString("F4", CultureInfo.InvariantCulture) ?? "",
            s.RunningCi95PowerW?.ToString("F4", CultureInfo.InvariantCulture) ?? "",
            s.SampleCount,
            s.SettingsRevision,
            EscapeCsv(s.EventCode),
            EscapeCsv(s.EventDetail),
            s.Attempt);
    }

    public static string FormatRawDataHeader() =>
        "TimestampUtc,RelativeSeconds,Phase,RpmMeasured,TorquePercent,TorqueNm,ShaftPowerW,FlowLpm,Counted,Attempt";

    public static string FormatRawDataRow(PowerDataPoint p)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:O},{1:F3},{2},{3:F1},{4:F3},{5:F5},{6:F4},{7:F2},{8},{9}",
            p.TimestampUtc,
            p.RelativeSeconds,
            p.Phase,
            p.RpmMeasured,
            p.TorquePercent,
            p.TorqueNm,
            p.ShaftPowerW,
            p.FlowLpm?.ToString("F2", CultureInfo.InvariantCulture) ?? "",
            p.Counted ? 1 : 0,
            p.Attempt);
    }

    public static string FormatRunResultHeader() =>
        "RunId,ConditionId,Replicate,Phase,AgitationRpm,GasFlowLpm,GasMode,SampleCount,MeanRpmMeasured,MeanTorquePercent,MeanTorqueNm,MeanShaftPowerW,NetPowerW,TorqueCi95Percent,Ci95PowerW,AssemblyNp,AssemblyRe,AssemblyNpCi95,BelowNoiseFloor,StopReason,Tries,IsRelative,StartedUtc,CompletedUtc,RawDataPath,RawDataSha256,ManualElectricalW,ManualInstrument,ManualNote,GasFlowVvm,GasFlowNumber,FroudeNumber,GassedPowerW,ReferenceP0W,ReferenceP0Ci95W,P0Provenance,PowerRatio,PowerRatioCi95,UsedVentStabilization";

    public static string FormatRunResultRow(PowerRun run) => string.Format(
        CultureInfo.InvariantCulture,
        "{0},{1},{2},{3},{4:F1},{5},{6},{7},{8:F3},{9:F5},{10:F7},{11:F7},{12:F7},{13:F6},{14:F7},{15},{16},{17},{18},{19},{20},{21},{22:O},{23},{24},{25},{26},{27},{28},{29},{30},{31},{32},{33},{34},{35},{36},{37},{38}",
        run.RunId,
        run.ConditionId,
        run.ReplicateNumber,
        run.CurrentPhase,
        run.AgitationRpm,
        run.GasFlowLpm?.ToString("F3", CultureInfo.InvariantCulture) ?? "",
        run.GasMode,
        run.SampleCount,
        run.MeanRpmMeasured,
        run.MeanTorquePercent,
        run.MeanTorqueNm,
        run.MeanShaftPowerW,
        run.NetPowerW,
        run.TorqueCi95Percent,
        run.Ci95PowerW,
        run.Analysis?.AssemblyPowerNumber.ToString("G17", CultureInfo.InvariantCulture) ?? "",
        run.Analysis?.AssemblyReynoldsNumber.ToString("G17", CultureInfo.InvariantCulture) ?? "",
        run.Analysis?.AssemblyPowerNumberCi95.ToString("G17", CultureInfo.InvariantCulture) ?? "",
        run.Analysis?.BelowNoiseFloor == true ? 1 : 0,
        run.StopReason,
        run.Tries,
        run.IsRelative ? 1 : 0,
        run.StartedUtc,
        run.CompletedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "",
        EscapeCsv(run.RawDataPath ?? ""),
        run.RawDataSha256 ?? "",
        run.ManualElec?.PowerElectricalW.ToString("F4", CultureInfo.InvariantCulture) ?? "",
        EscapeCsv(run.ManualElec?.Instrument ?? ""),
        EscapeCsv(run.ManualElec?.Note ?? ""),
        run.GasFlowVvm?.ToString("F4", CultureInfo.InvariantCulture) ?? "",
        run.GasFlowNumber?.ToString("G17", CultureInfo.InvariantCulture) ?? "",
        run.FroudeNumber?.ToString("G17", CultureInfo.InvariantCulture) ?? "",
        run.GassedPowerW?.ToString("F7", CultureInfo.InvariantCulture) ?? "",
        run.ReferenceP0W?.ToString("F7", CultureInfo.InvariantCulture) ?? "",
        run.ReferenceP0Ci95W?.ToString("F7", CultureInfo.InvariantCulture) ?? "",
        run.P0Provenance,
        run.PowerRatio?.ToString("G17", CultureInfo.InvariantCulture) ?? "",
        run.PowerRatioCi95?.ToString("G17", CultureInfo.InvariantCulture) ?? "",
        run.UsedVentStabilization ? 1 : 0);

    public static string FormatResultsSummaryHeader() =>
        "ConditionId,AgitationRpm,GasFlowLpm,GasMode,RequestedReplicates,CompletedReplicates,AcceptedReplicates,MeanNetPowerW,StdDevNetPowerW,MeanAssemblyNp,StdDevAssemblyNp,MeanAssemblyRe,MeanPowerRatio,StdDevPowerRatio,MeanGasFlowNumber,MeanFroudeNumber";

    public static string FormatResultsSummaryRow(
        PowerCondition condition,
        double? meanNetPowerW,
        double? stdDevNetPowerW,
        double? meanAssemblyNp,
        double? stdDevAssemblyNp,
        double? meanAssemblyRe,
        double? meanPowerRatio = null,
        double? stdDevPowerRatio = null,
        double? meanGasFlowNumber = null,
        double? meanFroudeNumber = null) => string.Format(
        CultureInfo.InvariantCulture,
        "{0},{1:F1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14},{15}",
        condition.ConditionId,
        condition.AgitationRpm,
        condition.GasFlowLpm?.ToString("F3", CultureInfo.InvariantCulture) ?? "",
        condition.GasMode,
        condition.RequestedReplicates,
        condition.CompletedReplicates,
        condition.AcceptedReplicates,
        meanNetPowerW?.ToString("F7", CultureInfo.InvariantCulture) ?? "",
        stdDevNetPowerW?.ToString("F7", CultureInfo.InvariantCulture) ?? "",
        meanAssemblyNp?.ToString("G17", CultureInfo.InvariantCulture) ?? "",
        stdDevAssemblyNp?.ToString("G17", CultureInfo.InvariantCulture) ?? "",
        meanAssemblyRe?.ToString("G17", CultureInfo.InvariantCulture) ?? "",
        meanPowerRatio?.ToString("G17", CultureInfo.InvariantCulture) ?? "",
        stdDevPowerRatio?.ToString("G17", CultureInfo.InvariantCulture) ?? "",
        meanGasFlowNumber?.ToString("G17", CultureInfo.InvariantCulture) ?? "",
        meanFroudeNumber?.ToString("G17", CultureInfo.InvariantCulture) ?? "");

    public static string FormatEventLogLine(PowerTestEventLogEntry entry) =>
        JsonSerializer.Serialize(entry, JsonOptions);

    public static string ComputeStringSha256(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    public static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
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

    /// <summary>
    /// Hashes only the calibration terms that affect torque conversion. Acquisition time and the
    /// reference fixture do not invalidate a tare when scale, offset and rated torque are equal.
    /// </summary>
    public static string ComputeTorqueCalibrationHash(TorqueCalibration? calibration, double motorRatedTorqueNm)
    {
        var identity = calibration is { } value
            ? string.Format(
                CultureInfo.InvariantCulture,
                "calibrated|{0:G17}|{1:G17}|{2:G17}",
                value.Scale,
                value.Offset,
                value.MotorRatedTorqueNm)
            : string.Format(CultureInfo.InvariantCulture, "nominal|{0:G17}", motorRatedTorqueNm);
        return ComputeStringSha256(identity);
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
