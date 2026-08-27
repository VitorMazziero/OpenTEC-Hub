using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using TecnalHub.Services.Persistence;

namespace TecnalHub.Services.KlaTesting;

public sealed class KlaTestStore : IKlaTestStore
{
    private readonly string _rootDirectory;
    private readonly object _ioLock = new();

    public KlaTestStore(string? rootDirectory = null)
    {
        _rootDirectory = rootDirectory ?? AppPaths.KlaTestsDirectory;
        Directory.CreateDirectory(_rootDirectory);
    }

    public string RootDirectory => _rootDirectory;

    public bool ValidateTestName(string name, out string? error) =>
        KlaTestFileContracts.ValidateTestName(name, out error);

    public bool TestExists(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var folderPath = Path.Combine(_rootDirectory, name.Trim());
        return Directory.Exists(folderPath);
    }

    public IReadOnlyList<KlaTestSummary> ListTests()
    {
        lock (_ioLock)
        {
            if (!Directory.Exists(_rootDirectory))
            {
                return [];
            }

            var list = new List<KlaTestSummary>();
            var dirs = Directory.GetDirectories(_rootDirectory);

            foreach (var dir in dirs)
            {
                var folderName = Path.GetFileName(dir);
                var manifestPath = Path.Combine(dir, KlaTestFileContracts.TestManifestFileName);

                if (File.Exists(manifestPath))
                {
                    try
                    {
                        var json = File.ReadAllText(manifestPath);
                        var doc = KlaTestFileContracts.DeserializeTestDocument(json);
                        if (doc is not null)
                        {
                            var completedRuns = doc.Runs.Count(r => r.Phase == RunPhase.Accepted || r.Phase == RunPhase.Rejected);
                            var acceptedRuns = doc.Runs.Count(r => r.Decision == DecisionQuality.Acceptable || r.Decision == DecisionQuality.AcceptableWithWarning);

                            list.Add(new KlaTestSummary(
                                folderName,
                                doc.Name,
                                doc.TestId,
                                doc.Status,
                                doc.CreatedUtc,
                                doc.CompletedUtc,
                                doc.LinkedMap?.MapName,
                                doc.Conditions.Count,
                                completedRuns,
                                acceptedRuns));
                            continue;
                        }
                    }
                    catch
                    {
                        // Fallback to directory summary if manifest read fails
                    }
                }

                list.Add(new KlaTestSummary(
                    folderName,
                    folderName,
                    Guid.Empty,
                    KlaTestStatus.Interrupted,
                    Directory.GetCreationTimeUtc(dir),
                    null,
                    null,
                    0,
                    0,
                    0));
            }

            return list.OrderByDescending(t => t.CreatedUtc).ToList();
        }
    }

    public KlaTestDocument? LoadTest(string folderName)
    {
        lock (_ioLock)
        {
            var folderPath = Path.Combine(_rootDirectory, folderName);
            var manifestPath = Path.Combine(folderPath, KlaTestFileContracts.TestManifestFileName);

            if (!File.Exists(manifestPath))
            {
                return null;
            }

            var json = File.ReadAllText(manifestPath);
            var doc = KlaTestFileContracts.DeserializeTestDocument(json);
            if (doc is null)
            {
                return null;
            }

            if (doc.Status == KlaTestStatus.Running)
            {
                doc.Status = KlaTestStatus.Interrupted;
                doc.InterruptionReason = "Execução anterior encerrada sem fechamento registrado.";
                doc.LastModifiedUtc = DateTimeOffset.UtcNow;
                WriteAllTextAtomic(manifestPath, KlaTestFileContracts.SerializeTestDocument(doc));
            }

            // Sync conditions from tabela-condicoes.json if available
            var conditionsPath = Path.Combine(folderPath, KlaTestFileContracts.ConditionTableFileName);
            if (File.Exists(conditionsPath))
            {
                try
                {
                    var condJson = File.ReadAllText(conditionsPath);
                    var conditions = KlaTestFileContracts.DeserializeConditionTable(condJson);
                    if (conditions is not null && conditions.Count > 0)
                    {
                        doc.Conditions = conditions;
                    }
                }
                catch
                {
                    // Keep manifest conditions
                }
            }

            // If not actively running, ensure conditions are not stuck in InProgress
            if (doc.Status != KlaTestStatus.Running)
            {
                foreach (var cond in doc.Conditions)
                {
                    if (cond.Status == ConditionStatus.InProgress)
                    {
                        cond.Status = cond.AcceptedReplicates >= cond.RequestedReplicates
                            ? ConditionStatus.Completed
                            : ConditionStatus.Pending;
                    }
                }
            }

            return doc;
        }
    }

    public KlaTestDocument CreateTest(
        string name,
        KlaTestSettings settings,
        NitrogenValve n2Valve,
        KlaMapReference? linkedMap = null,
        IReadOnlyList<KlaTestCondition>? initialConditions = null)
    {
        if (!ValidateTestName(name, out var error))
        {
            throw new ArgumentException(error ?? "Nome de teste inválido.", nameof(name));
        }

        var trimmedName = name.Trim();
        var folderPath = Path.Combine(_rootDirectory, trimmedName);

        lock (_ioLock)
        {
            if (Directory.Exists(folderPath))
            {
                throw new InvalidOperationException($"Já existe um teste com o nome '{trimmedName}'.");
            }

            Directory.CreateDirectory(folderPath);
            var runsPath = Path.Combine(folderPath, KlaTestFileContracts.RunsDirectoryName);
            Directory.CreateDirectory(runsPath);

            var conditions = initialConditions?.Select((c, idx) =>
            {
                var clone = c.Clone();
                clone.OrderIndex = idx;
                return clone;
            }).ToList() ?? [];

            var doc = new KlaTestDocument
            {
                TestId = Guid.NewGuid(),
                Name = trimmedName,
                FolderName = trimmedName,
                Status = KlaTestStatus.Draft,
                CreatedUtc = DateTimeOffset.UtcNow,
                LastModifiedUtc = DateTimeOffset.UtcNow,
                Nature = "Abiotico",
                LinkedMap = linkedMap,
                SelectedNitrogenValve = n2Valve,
                Settings = settings,
                SettingsRevision = 1,
                AppVersion = typeof(KlaTestStore).Assembly.GetName().Version?.ToString() ?? "desconhecida",
                Conditions = conditions,
                Runs = [],
            };

            // Write teste.json
            var manifestJson = KlaTestFileContracts.SerializeTestDocument(doc);
            var manifestPath = Path.Combine(folderPath, KlaTestFileContracts.TestManifestFileName);
            WriteAllTextAtomic(manifestPath, manifestJson);

            // Write tabela-condicoes.json
            var condJson = KlaTestFileContracts.SerializeConditionTable(conditions);
            var condPath = Path.Combine(folderPath, KlaTestFileContracts.ConditionTableFileName);
            WriteAllTextAtomic(condPath, condJson);

            // Initialize serie-global.csv
            var globalSeriesPath = Path.Combine(folderPath, KlaTestFileContracts.GlobalSeriesFileName);
            File.WriteAllText(globalSeriesPath, KlaTestFileContracts.FormatGlobalSeriesHeader() + Environment.NewLine, Encoding.UTF8);

            // Initialize eventos.jsonl
            var eventsPath = Path.Combine(folderPath, KlaTestFileContracts.EventLogFileName);
            File.WriteAllText(eventsPath, "", Encoding.UTF8);

            // Initialize resumo-resultados.csv
            var summaryPath = Path.Combine(folderPath, KlaTestFileContracts.ResultsSummaryFileName);
            File.WriteAllText(summaryPath, KlaTestFileContracts.FormatResultsSummaryHeader() + Environment.NewLine, Encoding.UTF8);

            return doc;
        }
    }

    public void SaveTestManifest(KlaTestDocument doc)
    {
        lock (_ioLock)
        {
            var folderPath = Path.Combine(_rootDirectory, doc.FolderName);
            Directory.CreateDirectory(folderPath);

            doc.LastModifiedUtc = DateTimeOffset.UtcNow;
            var manifestJson = KlaTestFileContracts.SerializeTestDocument(doc);
            var manifestPath = Path.Combine(folderPath, KlaTestFileContracts.TestManifestFileName);
            WriteAllTextAtomic(manifestPath, manifestJson);
        }
    }

    public IReadOnlyList<KlaTestCondition> LoadConditionsTable(string testFolderName)
    {
        lock (_ioLock)
        {
            var condPath = Path.Combine(_rootDirectory, testFolderName, KlaTestFileContracts.ConditionTableFileName);
            if (!File.Exists(condPath))
            {
                return [];
            }

            var json = File.ReadAllText(condPath);
            return KlaTestFileContracts.DeserializeConditionTable(json) ?? [];
        }
    }

    public void SaveConditionsTable(string testFolderName, IReadOnlyList<KlaTestCondition> conditions)
    {
        lock (_ioLock)
        {
            var folderPath = Path.Combine(_rootDirectory, testFolderName);
            Directory.CreateDirectory(folderPath);

            var condJson = KlaTestFileContracts.SerializeConditionTable(conditions);
            var condPath = Path.Combine(folderPath, KlaTestFileContracts.ConditionTableFileName);
            WriteAllTextAtomic(condPath, condJson);
        }
    }

    public string InitializeRunFolder(string testFolderName, KlaTestRun run)
    {
        lock (_ioLock)
        {
            var runFolder = KlaTestFileContracts.FormatRunFolderName(run.AgitationRpm, run.AirflowLpm, run.ReplicateNumber);
            run.FolderName = runFolder;

            var runsPath = Path.Combine(_rootDirectory, testFolderName, KlaTestFileContracts.RunsDirectoryName);
            var runPath = Path.Combine(runsPath, runFolder);
            var attempt = 2;
            while (Directory.Exists(runPath))
            {
                runFolder = $"{KlaTestFileContracts.FormatRunFolderName(run.AgitationRpm, run.AirflowLpm, run.ReplicateNumber)}_Tentativa{attempt:D2}";
                runPath = Path.Combine(runsPath, runFolder);
                attempt++;
            }
            run.FolderName = runFolder;
            Directory.CreateDirectory(runPath);
            WriteAllTextAtomic(Path.Combine(runPath, KlaTestFileContracts.RunRawDataFileName),
                KlaTestFileContracts.FormatRawDataHeader() + Environment.NewLine);

            return runFolder;
        }
    }

    public void SaveRunRawData(string testFolderName, string runFolderName, IEnumerable<KlaRawDataPoint> points)
    {
        lock (_ioLock)
        {
            var runPath = Path.Combine(_rootDirectory, testFolderName, KlaTestFileContracts.RunsDirectoryName, runFolderName);
            Directory.CreateDirectory(runPath);

            var filePath = Path.Combine(runPath, KlaTestFileContracts.RunRawDataFileName);
            var sb = new StringBuilder();
            sb.AppendLine(KlaTestFileContracts.FormatRawDataHeader());

            foreach (var p in points)
            {
                sb.AppendLine(KlaTestFileContracts.FormatRawDataRow(p));
            }

            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }
    }

    public void AppendRunRawDataPoint(string testFolderName, string runFolderName, KlaRawDataPoint point)
    {
        lock (_ioLock)
        {
            var filePath = GetRunRawDataPath(testFolderName, runFolderName);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            if (!File.Exists(filePath) || new FileInfo(filePath).Length == 0)
            {
                File.AppendAllText(filePath, KlaTestFileContracts.FormatRawDataHeader() + Environment.NewLine, Encoding.UTF8);
            }
            File.AppendAllText(filePath, KlaTestFileContracts.FormatRawDataRow(point) + Environment.NewLine, Encoding.UTF8);
        }
    }

    public string GetRunRawDataPath(string testFolderName, string runFolderName) =>
        Path.Combine(_rootDirectory, testFolderName, KlaTestFileContracts.RunsDirectoryName, runFolderName, KlaTestFileContracts.RunRawDataFileName);

    private static void WriteAllTextAtomic(string path, string contents)
    {
        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tempPath, contents, Encoding.UTF8);
        File.Move(tempPath, path, overwrite: true);
    }

    public IReadOnlyList<KlaRawDataPoint> LoadRunRawData(string testFolderName, string runFolderName)
    {
        lock (_ioLock)
        {
            var filePath = Path.Combine(_rootDirectory, testFolderName, KlaTestFileContracts.RunsDirectoryName, runFolderName, KlaTestFileContracts.RunRawDataFileName);
            if (!File.Exists(filePath))
            {
                return [];
            }

            var list = new List<KlaRawDataPoint>();
            var lines = File.ReadAllLines(filePath);

            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var parts = line.Split(',');
                if (parts.Length < 11)
                {
                    continue;
                }

                if (!DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts))
                {
                    continue;
                }
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var relSec))
                {
                    continue;
                }
                if (!Enum.TryParse<RunPhase>(parts[2], out var phase))
                {
                    phase = RunPhase.Idle;
                }
                if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var doRaw))
                {
                    continue;
                }
                if (!double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var doFilt))
                {
                    doFilt = doRaw;
                }
                if (!double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var flowM))
                {
                    flowM = 0;
                }
                if (!double.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var flowSp))
                {
                    flowSp = 0;
                }
                if (!double.TryParse(parts[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var rpmSp))
                {
                    rpmSp = 0;
                }
                var v1 = parts[8] is "1" or "True";
                var v2 = parts[9] is "1" or "True";
                var vFlow = parts[10] is "1" or "True";

                list.Add(new KlaRawDataPoint(ts, relSec, phase, doRaw, doFilt, flowM, flowSp, rpmSp, v1, v2, vFlow));
            }

            return list;
        }
    }

    public void SaveRunAnalysis(string testFolderName, string runFolderName, KlaAnalysisRevision analysis)
    {
        lock (_ioLock)
        {
            var runPath = Path.Combine(_rootDirectory, testFolderName, KlaTestFileContracts.RunsDirectoryName, runFolderName);
            Directory.CreateDirectory(runPath);

            var json = KlaTestFileContracts.SerializeAnalysis(analysis);
            var filePath = Path.Combine(runPath, KlaTestFileContracts.RunAnalysisFileName);
            File.WriteAllText(filePath, json, Encoding.UTF8);
            var revisionPath = Path.Combine(runPath, $"analise-rev-{analysis.RevisionNumber:D3}.json");
            File.WriteAllText(revisionPath, json, Encoding.UTF8);
        }
    }

    public KlaAnalysisRevision? LoadRunAnalysis(string testFolderName, string runFolderName)
    {
        lock (_ioLock)
        {
            var filePath = Path.Combine(_rootDirectory, testFolderName, KlaTestFileContracts.RunsDirectoryName, runFolderName, KlaTestFileContracts.RunAnalysisFileName);
            if (!File.Exists(filePath))
            {
                return null;
            }

            var json = File.ReadAllText(filePath);
            return KlaTestFileContracts.DeserializeAnalysis(json);
        }
    }

    public void SaveRunResult(string testFolderName, string runFolderName, KlaTestRun run, KlaAnalysisRevision analysis)
    {
        lock (_ioLock)
        {
            var runPath = Path.Combine(_rootDirectory, testFolderName, KlaTestFileContracts.RunsDirectoryName, runFolderName);
            Directory.CreateDirectory(runPath);

            var sb = new StringBuilder();
            sb.AppendLine(KlaTestFileContracts.FormatRunResultHeader());
            sb.AppendLine(KlaTestFileContracts.FormatRunResultRow(run, analysis));

            var filePath = Path.Combine(runPath, KlaTestFileContracts.RunResultFileName);
            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }
    }

    public void UpdateResultsSummary(string testFolderName, KlaTestDocument doc)
    {
        lock (_ioLock)
        {
            var folderPath = Path.Combine(_rootDirectory, testFolderName);
            Directory.CreateDirectory(folderPath);

            var sb = new StringBuilder();
            sb.AppendLine(KlaTestFileContracts.FormatResultsSummaryHeader());

            foreach (var cond in doc.Conditions)
            {
                var acceptedRuns = doc.Runs
                    .Where(r => r.ConditionId == cond.ConditionId &&
                               (r.Decision == DecisionQuality.Acceptable || r.Decision == DecisionQuality.AcceptableWithWarning) &&
                               r.KlaPerHour.HasValue)
                    .ToList();

                var completedCount = doc.Runs.Count(r => r.ConditionId == cond.ConditionId && (r.Phase == RunPhase.Accepted || r.Phase == RunPhase.Rejected));
                var acceptedCount = acceptedRuns.Count;

                double meanKla = 0;
                double stdDevKla = 0;
                double? meanR2 = null;

                if (acceptedCount > 0)
                {
                    meanKla = acceptedRuns.Average(r => r.KlaPerHour!.Value);
                    if (acceptedCount > 1)
                    {
                        var sumSq = acceptedRuns.Sum(r => Math.Pow(r.KlaPerHour!.Value - meanKla, 2));
                        stdDevKla = Math.Sqrt(sumSq / (acceptedCount - 1));
                    }

                    var r2Values = acceptedRuns.Where(r => r.AnalysisR2.HasValue).Select(r => r.AnalysisR2!.Value).ToList();
                    if (r2Values.Count > 0)
                    {
                        meanR2 = r2Values.Average();
                    }
                }

                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "{0},{1:F0},{2:F2},{3},{4},{5},{6:F2},{7:F2},{8}\n",
                    cond.ConditionId,
                    cond.AgitationRpm,
                    cond.AirflowLpm,
                    cond.RequestedReplicates,
                    completedCount,
                    acceptedCount,
                    meanKla,
                    stdDevKla,
                    meanR2.HasValue ? meanR2.Value.ToString("F4", CultureInfo.InvariantCulture) : "");
            }

            var filePath = Path.Combine(folderPath, KlaTestFileContracts.ResultsSummaryFileName);
            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }
    }

    public void AppendGlobalSeriesSample(string testFolderName, KlaGlobalSeriesSample sample)
    {
        lock (_ioLock)
        {
            var filePath = Path.Combine(_rootDirectory, testFolderName, KlaTestFileContracts.GlobalSeriesFileName);
            var line = KlaTestFileContracts.FormatGlobalSeriesRow(sample);
            File.AppendAllText(filePath, line + Environment.NewLine, Encoding.UTF8);
        }
    }

    public void AppendEventLog(string testFolderName, KlaTestEventLogEntry entry)
    {
        lock (_ioLock)
        {
            var filePath = Path.Combine(_rootDirectory, testFolderName, KlaTestFileContracts.EventLogFileName);
            var line = KlaTestFileContracts.FormatEventLogLine(entry);
            File.AppendAllText(filePath, line + Environment.NewLine, Encoding.UTF8);
        }
    }
}
