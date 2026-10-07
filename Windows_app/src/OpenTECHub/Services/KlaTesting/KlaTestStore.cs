using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.Services.KlaTesting;

/// <remarks>
/// Writes are formatted or serialised on the caller and queued to a <see cref="BackgroundFileWriter"/>
/// that executes them in order off the UI thread (D-048); reads flush the writer first. Without an
/// explicit writer the store writes inline (tests).
/// </remarks>
public sealed class KlaTestStore : IKlaTestStore
{
    private readonly string _rootDirectory;
    private readonly object _ioLock = new();
    private readonly BackgroundFileWriter _writer;
    private readonly Dictionary<string, string> _queuedRevisionContents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _latestRevisionNumbers = new(StringComparer.OrdinalIgnoreCase);

    public KlaTestStore(string? rootDirectory = null, BackgroundFileWriter? writer = null)
    {
        _rootDirectory = rootDirectory ?? AppPaths.KlaTestsDirectory;
        _writer = writer ?? new BackgroundFileWriter(synchronous: true);
        _writer.WriteFailed += (path, ex) => WriteFailed?.Invoke(path, ex);
        Directory.CreateDirectory(_rootDirectory);
    }

    public string RootDirectory => _rootDirectory;

    public event Action<string, Exception>? WriteFailed;

    public Task FlushAsync() => _writer.FlushAsync();
    public void SaveRunAcquisition(string testFolderName, string runFolderName, KlaAcquisitionMetadata metadata)
        => WriteAllTextAtomic(Path.Combine(RootDirectory, testFolderName, KlaTestFileContracts.RunsDirectoryName, runFolderName, "aquisicao.json"),
            KlaTestFileContracts.SerializeAcquisition(metadata));
    public void SaveRunPhysicalOutcome(string testFolderName, string runFolderName, KlaRunOutcome outcome)
        => WriteAllTextAtomic(Path.Combine(RootDirectory, testFolderName, KlaTestFileContracts.RunsDirectoryName, runFolderName, "estado-fisico.json"),
            KlaTestFileContracts.SerializePhysicalOutcome(outcome));
    public void SaveRunGasEvents(string testFolderName, string runFolderName, IReadOnlyList<KlaGasEvent> events)
        => WriteAllTextAtomic(Path.Combine(RootDirectory, testFolderName, KlaTestFileContracts.RunsDirectoryName, runFolderName, "transicoes-gas.json"),
            KlaTestFileContracts.SerializeGasEvents(events));
    public IReadOnlyList<KlaGasEvent> LoadRunGasEvents(string testFolderName, string runFolderName)
    {
        _writer.Flush();
        var path = Path.Combine(RootDirectory, testFolderName, KlaTestFileContracts.RunsDirectoryName, runFolderName, "transicoes-gas.json");
        return File.Exists(path) ? KlaTestFileContracts.DeserializeGasEvents(File.ReadAllText(path)) : [];
    }

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
        _writer.Flush();
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
        _writer.Flush();
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

            doc.FolderName = folderName;

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

            ReconcileRunsFromDisk(folderPath, doc);

            return doc;
        }
    }

    public string ImportTestFolder(string sourceFolder)
    {
        if (string.IsNullOrWhiteSpace(sourceFolder))
        {
            throw new ArgumentException("Selecione uma pasta de ensaio válida.", nameof(sourceFolder));
        }

        var sourcePath = Path.GetFullPath(sourceFolder.Trim());
        var manifestPath = Path.Combine(sourcePath, KlaTestFileContracts.TestManifestFileName);
        if (!Directory.Exists(sourcePath) || !File.Exists(manifestPath))
        {
            throw new InvalidDataException($"A pasta selecionada não contém '{KlaTestFileContracts.TestManifestFileName}'.");
        }

        var sourceDoc = KlaTestFileContracts.DeserializeTestDocument(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException("O manifesto do ensaio não pôde ser lido.");

        _writer.Flush();
        lock (_ioLock)
        {
            foreach (var existing in ListTests())
            {
                if (sourceDoc.TestId != Guid.Empty && existing.TestId == sourceDoc.TestId)
                {
                    return existing.FolderName;
                }
            }

            var rootPath = Path.GetFullPath(_rootDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (sourcePath.TrimEnd(Path.DirectorySeparatorChar).Equals(rootPath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Selecione a pasta de um ensaio, não a pasta raiz Testes-kLa.");
            }

            var preferredName = ValidateTestName(sourceDoc.Name, out _) ? sourceDoc.Name.Trim() : Path.GetFileName(sourcePath);
            if (!ValidateTestName(preferredName, out var nameError))
            {
                throw new InvalidDataException(nameError ?? "O nome do ensaio importado é inválido.");
            }

            var targetName = preferredName;
            var suffix = 2;
            while (Directory.Exists(Path.Combine(_rootDirectory, targetName)))
            {
                targetName = $"{preferredName}_Importado_{suffix++:D2}";
            }

            var temporaryPath = Path.Combine(_rootDirectory, $".importacao-{Guid.NewGuid():N}");
            var targetPath = Path.Combine(_rootDirectory, targetName);
            try
            {
                CopyDirectoryWithoutLinks(sourcePath, temporaryPath);
                sourceDoc.FolderName = targetName;
                if (sourceDoc.Status == KlaTestStatus.Running)
                {
                    sourceDoc.Status = KlaTestStatus.Interrupted;
                    sourceDoc.InterruptionReason = "Ensaio importado de uma execução que não registrou encerramento.";
                }
                sourceDoc.LastModifiedUtc = DateTimeOffset.UtcNow;
                WriteAllTextAtomic(
                    Path.Combine(temporaryPath, KlaTestFileContracts.TestManifestFileName),
                    KlaTestFileContracts.SerializeTestDocument(sourceDoc));
                MoveDirectoryWithRetry(temporaryPath, targetPath);
                return targetName;
            }
            catch
            {
                if (Directory.Exists(temporaryPath))
                {
                    Directory.Delete(temporaryPath, recursive: true);
                }
                throw;
            }
        }
    }

    private static void MoveDirectoryWithRetry(string sourcePath, string targetPath, int maxRetries = 5)
    {
        for (var i = 0; i < maxRetries; i++)
        {
            try
            {
                Directory.Move(sourcePath, targetPath);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && i < maxRetries - 1)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static void CopyDirectoryWithoutLinks(string sourcePath, string targetPath)
    {
        Directory.CreateDirectory(targetPath);
        foreach (var directory in Directory.EnumerateDirectories(sourcePath, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"A importação não aceita atalhos ou links de diretório: {directory}");
            }
            Directory.CreateDirectory(Path.Combine(targetPath, Path.GetRelativePath(sourcePath, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"A importação não aceita atalhos ou links de arquivo: {file}");
            }
            var destination = Path.Combine(targetPath, Path.GetRelativePath(sourcePath, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: false);
        }
    }

    private void ReconcileRunsFromDisk(string testFolderPath, KlaTestDocument doc)
    {
        var runsPath = Path.Combine(testFolderPath, KlaTestFileContracts.RunsDirectoryName);
        if (!Directory.Exists(runsPath))
        {
            return;
        }

        var changed = false;
        foreach (var runPath in Directory.GetDirectories(runsPath))
        {
            var runFolder = Path.GetFileName(runPath);
            if (!KlaTestFileContracts.TryParseRunFolderName(runFolder, out var rpm, out var flow, out var replicate) ||
                !File.Exists(Path.Combine(runPath, KlaTestFileContracts.RunRawDataFileName)))
            {
                continue;
            }

            var condition = doc.Conditions.FirstOrDefault(c =>
                Math.Abs(c.AgitationRpm - rpm) < 0.5 && Math.Abs(c.AirflowLpm - flow) < 0.005);
            if (condition is null)
            {
                condition = new KlaTestCondition
                {
                    OrderIndex = doc.Conditions.Count,
                    AgitationRpm = rpm,
                    AirflowLpm = flow,
                    RequestedReplicates = Math.Max(1, replicate),
                    Origin = ConditionOrigin.Manual,
                };
                doc.Conditions.Add(condition);
                changed = true;
            }
            else if (condition.RequestedReplicates < replicate)
            {
                condition.RequestedReplicates = replicate;
                changed = true;
            }

            var analysis = LoadRunAnalysis(doc.FolderName, runFolder);
            var raw = LoadRunRawData(doc.FolderName, runFolder);
            var existingIndex = doc.Runs.FindIndex(r => r.FolderName.Equals(runFolder, StringComparison.OrdinalIgnoreCase));
            var existing = existingIndex >= 0 ? doc.Runs[existingIndex] : null;
            var summary = new KlaTestRunSummary
            {
                RunId = existing?.RunId ?? Guid.NewGuid(),
                ConditionId = condition.ConditionId,
                ReplicateNumber = replicate,
                FolderName = runFolder,
                AgitationRpm = rpm,
                AirflowLpm = flow,
                Phase = analysis?.Outcome is { } outcome
                    ? outcome.OperatorDecision switch
                    {
                        KlaOperatorDecision.Accepted => RunPhase.Accepted,
                        KlaOperatorDecision.Rejected => RunPhase.Rejected,
                        _ => RunPhase.Reviewing,
                    }
                    : analysis is not null
                    ? (analysis.Quality == DecisionQuality.Inconclusive ? RunPhase.Rejected : RunPhase.Accepted)
                    : existing?.Phase ?? RunPhase.Reviewing,
                Outcome = MergePhysicalOutcome(runPath, analysis?.Outcome ?? existing?.Outcome),
                Definition = LoadRunDefinition(doc.FolderName, runFolder) ?? existing?.Definition,
                AttemptNumber = LoadRunDefinition(doc.FolderName, runFolder)?.AttemptNumber ?? existing?.AttemptNumber ?? 1,
                Context = LoadRunDefinition(doc.FolderName, runFolder)?.Context ?? existing?.Context,
                RemovalSeconds = KlaSequence.RemovalExposure(raw),
                Decision = analysis?.Quality ?? existing?.Decision,
                KlaPerHour = analysis?.KlaPerHour ?? existing?.KlaPerHour,
                AnalysisR2 = analysis?.AnalysisR2 ?? existing?.AnalysisR2,
                StartedUtc = raw.FirstOrDefault()?.TimestampUtc ?? existing?.StartedUtc ?? Directory.GetCreationTimeUtc(runPath),
                CompletedUtc = existing?.CompletedUtc ?? (analysis is not null ? raw.LastOrDefault()?.TimestampUtc ?? analysis.AnalyzedUtc : null),
            };

            if (existingIndex >= 0)
            {
                if (existing != summary)
                {
                    doc.Runs[existingIndex] = summary;
                    changed = true;
                }
            }
            else
            {
                doc.Runs.Add(summary);
                changed = true;
            }
        }

        foreach (var condition in doc.Conditions)
        {
            if (doc.SequenceLimits is not null)
            {
                var before = (condition.CompletedReplicates, condition.AcceptedReplicates, condition.RejectedReplicates, condition.Status);
                KlaSequence.RefreshCounters(doc, condition);
                changed |= before != (condition.CompletedReplicates, condition.AcceptedReplicates, condition.RejectedReplicates, condition.Status);
                continue;
            }
            var runs = doc.Runs.Where(r => r.ConditionId == condition.ConditionId).ToList();
            var completed = runs.Count(r => r.Phase is RunPhase.Accepted or RunPhase.Rejected);
            var accepted = runs.Count(r => r.Phase == RunPhase.Accepted &&
                r.Decision is DecisionQuality.Acceptable or DecisionQuality.AcceptableWithWarning);
            var rejected = runs.Count(r => r.Phase == RunPhase.Rejected || r.Decision == DecisionQuality.Inconclusive);
            if (condition.CompletedReplicates != completed || condition.AcceptedReplicates != accepted || condition.RejectedReplicates != rejected)
            {
                condition.CompletedReplicates = completed;
                condition.AcceptedReplicates = accepted;
                condition.RejectedReplicates = rejected;
                changed = true;
            }
            var status = accepted >= condition.RequestedReplicates ? ConditionStatus.Completed : ConditionStatus.Pending;
            if (condition.Status != status)
            {
                condition.Status = status;
                changed = true;
            }
        }

        if (changed)
        {
            SaveConditionsTable(doc.FolderName, doc.Conditions);
            SaveTestManifest(doc);
        }
    }

    private static KlaRunOutcome? MergePhysicalOutcome(string runPath, KlaRunOutcome? scientific)
    {
        var path = Path.Combine(runPath, "estado-fisico.json");
        if (!File.Exists(path))
        {
            return scientific;
        }
        var physical = KlaTestFileContracts.DeserializePhysicalOutcome(File.ReadAllText(path));
        return physical is null ? scientific : (scientific ?? new()) with
        { Restoration = physical.Restoration, RestorationReason = physical.RestorationReason };
    }

    public KlaTestDocument CreateTest(
        string name,
        KlaTestSettings settings,
        KlaMapReference? linkedMap = null,
        IReadOnlyList<KlaTestCondition>? initialConditions = null)
        => CreateTestCore(name, settings, linkedMap, initialConditions, null);

    public KlaTestDocument CreateTest(string name, KlaAssayDefinition definition, KlaMapReference? linkedMap = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate(requireConditions: false);
        return CreateTestCore(name, definition.Settings, linkedMap,
            definition.Conditions.Select(c => c.ToSessionCondition()).ToArray(), definition);
    }

    private KlaTestDocument CreateTestCore(string name, KlaTestSettings settings,
        KlaMapReference? linkedMap, IReadOnlyList<KlaTestCondition>? initialConditions,
        KlaAssayDefinition? definition)
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
                Nature = definition?.Protocol == KlaAssayProtocol.Biotic ? "Biotico" : "Abiotico",
                Protocol = definition?.Protocol ?? KlaAssayProtocol.Abiotic,
                CaptureMode = definition?.CaptureMode ?? KlaCaptureMode.Multiple,
                ProtocolSettings = definition?.ProtocolSettings,
                Context = definition?.Context,
                SequenceLimits = definition?.SequenceLimits,
                LinkedMap = linkedMap,
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
            WriteAllTextAtomic(globalSeriesPath, KlaTestFileContracts.FormatGlobalSeriesHeader() + Environment.NewLine);

            // Initialize eventos.jsonl
            var eventsPath = Path.Combine(folderPath, KlaTestFileContracts.EventLogFileName);
            WriteAllTextAtomic(eventsPath, "");

            // Initialize resumo-resultados.csv
            var summaryPath = Path.Combine(folderPath, KlaTestFileContracts.ResultsSummaryFileName);
            WriteAllTextAtomic(summaryPath, KlaTestFileContracts.FormatResultsSummaryHeader() + Environment.NewLine);

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
        _writer.Flush();
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
            if (run.Definition is { } definition)
            {
                run.AttemptNumber = attempt - 1;
                run.Definition = definition = definition with { AttemptNumber = run.AttemptNumber };
                WriteAllTextAtomic(Path.Combine(runPath, KlaTestFileContracts.RunDefinitionFileName),
                    KlaTestFileContracts.SerializeRunDefinition(definition));
            }
            WriteAllTextAtomic(Path.Combine(runPath, KlaTestFileContracts.RunRawDataFileName),
                KlaTestFileContracts.FormatRawDataHeader() + Environment.NewLine);

            return runFolder;
        }
    }

    public string SaveRunRawData(string testFolderName, string runFolderName, IEnumerable<KlaRawDataPoint> points)
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

            var contents = sb.ToString();
            WriteAllTextAtomic(filePath, contents);
            return KlaTestFileContracts.ComputeUtf8FileContentSha256(contents);
        }
    }

    public KlaRunDefinition? LoadRunDefinition(string testFolderName, string runFolderName)
    {
        _writer.Flush();
        var path = Path.Combine(_rootDirectory, testFolderName, KlaTestFileContracts.RunsDirectoryName,
            runFolderName, KlaTestFileContracts.RunDefinitionFileName);
        return File.Exists(path) ? KlaTestFileContracts.DeserializeRunDefinition(File.ReadAllText(path)) : null;
    }

    public void AppendRunRawDataPoint(string testFolderName, string runFolderName, KlaRawDataPoint point)
    {
        lock (_ioLock)
        {
            var filePath = GetRunRawDataPath(testFolderName, runFolderName);
            _writer.AppendLine(filePath, KlaTestFileContracts.FormatRawDataRow(point), headerIfEmpty: KlaTestFileContracts.FormatRawDataHeader());
        }
    }

    public string GetRunRawDataPath(string testFolderName, string runFolderName) =>
        Path.Combine(_rootDirectory, testFolderName, KlaTestFileContracts.RunsDirectoryName, runFolderName, KlaTestFileContracts.RunRawDataFileName);

    /// <summary>Queued: temp file + move, executed in order by the background writer.</summary>
    private void WriteAllTextAtomic(string path, string contents) => _writer.WriteAllTextAtomic(path, contents);

    public IReadOnlyList<KlaRawDataPoint> LoadRunRawData(string testFolderName, string runFolderName)
    {
        _writer.Flush();
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
                    throw new InvalidDataException($"Linha {i + 1} incompleta no arquivo bruto de kLa.");
                }

                if (!DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts))
                {
                    throw new InvalidDataException($"Instante inválido na linha {i + 1} do arquivo bruto de kLa.");
                }
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var relSec) || !double.IsFinite(relSec))
                {
                    throw new InvalidDataException($"Tempo relativo inválido na linha {i + 1} do arquivo bruto de kLa.");
                }
                if (!Enum.TryParse<RunPhase>(parts[2], out var phase) || !Enum.IsDefined(phase))
                {
                    if (parts[2] == "OpeningVent") phase = RunPhase.LegacyOpeningVent;
                    else throw new InvalidDataException($"Fase inválida na linha {i + 1} do arquivo bruto de kLa.");
                }
                if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var doRaw))
                {
                    doRaw = double.NaN;
                }
                if (!double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var doFilt))
                {
                    // ADC has different units. Keep invalid observations for scientific refusal.
                    doFilt = double.NaN;
                }
                if (!double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var flowM))
                {
                    flowM = double.NaN;
                }
                if (!double.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var flowSp))
                {
                    flowSp = double.NaN;
                }
                if (!double.TryParse(parts[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var rpmSp))
                {
                    rpmSp = double.NaN;
                }
                var v1 = parts[8] is "1" or "True";
                var v2 = parts[9] is "1" or "True";
                var vFlow = parts[10] is "1" or "True";

                // Schema 1 files stop at VFlow. Reading the two trailing columns only when the
                // row actually carries them is what lets an assay recorded before schema 2 open
                // unchanged, with its temperature and measured speed honestly absent.
                var temperature = ReadOptionalCell(parts, 11);
                var rpmMeasured = ReadOptionalCell(parts, 12);

                list.Add(new KlaRawDataPoint(
                    ts, relSec, phase, doRaw, doFilt, flowM, flowSp, rpmSp, v1, v2, vFlow, temperature, rpmMeasured));
            }

            return list;
        }
    }

    /// <summary>Reads an optional trailing column, absent in schema 1 files and blank when unread.</summary>
    private static double? ReadOptionalCell(string[] parts, int index) =>
        index < parts.Length &&
        double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
        double.IsFinite(value)
            ? value
            : null;

    public void SaveRunAnalysis(string testFolderName, string runFolderName, KlaAnalysisRevision analysis)
    {
        if (analysis.RevisionNumber < 1)
        {
            throw new ArgumentException("Revisão de análise inválida.");
        }

        lock (_ioLock)
        {
            var runPath = Path.Combine(_rootDirectory, testFolderName, KlaTestFileContracts.RunsDirectoryName, runFolderName);
            Directory.CreateDirectory(runPath);

            var json = KlaTestFileContracts.SerializeAnalysis(analysis);
            var filePath = Path.Combine(runPath, KlaTestFileContracts.RunAnalysisFileName);
            var revisionPath = Path.Combine(runPath, $"analise-rev-{analysis.RevisionNumber:D3}.json");
            if ((_queuedRevisionContents.TryGetValue(revisionPath, out var queued) && queued != json) ||
                (File.Exists(revisionPath) && File.ReadAllText(revisionPath) != json))
            {
                throw new InvalidOperationException("Revisão já gravada. Salve a reanálise como uma nova revisão.");
            }
            var latestRevision = _latestRevisionNumbers.GetValueOrDefault(filePath);
            if (File.Exists(filePath))
            {
                latestRevision = Math.Max(latestRevision,
                    KlaTestFileContracts.DeserializeAnalysis(File.ReadAllText(filePath))?.RevisionNumber ?? 0);
            }
            if (analysis.RevisionNumber < latestRevision)
            {
                throw new InvalidOperationException("Não substitua a análise atual por uma revisão anterior.");
            }
            WriteAllTextAtomic(revisionPath, json);
            WriteAllTextAtomic(filePath, json);
            _queuedRevisionContents[revisionPath] = json;
            _latestRevisionNumbers[filePath] = analysis.RevisionNumber;
        }
    }

    public KlaAnalysisRevision? LoadRunAnalysis(string testFolderName, string runFolderName)
    {
        _writer.Flush();
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
            WriteAllTextAtomic(filePath, sb.ToString());
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
                               KlaSequence.IsAccepted(r) &&
                               r.KlaPerHour.HasValue)
                    .GroupBy(r => r.ReplicateNumber)
                    .Select(g => g.OrderByDescending(r => r.CompletedUtc).ThenByDescending(r => r.AttemptNumber).First())
                    .ToList();

                var completedCount = doc.Runs.Where(r => r.ConditionId == cond.ConditionId && (r.Phase == RunPhase.Accepted || r.Phase == RunPhase.Rejected))
                    .Select(r => r.ReplicateNumber).Distinct().Count();
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
            WriteAllTextAtomic(filePath, sb.ToString());
        }
    }

    public void AppendGlobalSeriesSample(string testFolderName, KlaGlobalSeriesSample sample)
    {
        lock (_ioLock)
        {
            var filePath = Path.Combine(_rootDirectory, testFolderName, KlaTestFileContracts.GlobalSeriesFileName);
            _writer.AppendLine(filePath, KlaTestFileContracts.FormatGlobalSeriesRow(sample));
        }
    }

    public void AppendEventLog(string testFolderName, KlaTestEventLogEntry entry)
    {
        lock (_ioLock)
        {
            var filePath = Path.Combine(_rootDirectory, testFolderName, KlaTestFileContracts.EventLogFileName);
            _writer.AppendLine(filePath, KlaTestFileContracts.FormatEventLogLine(entry));
        }
    }
}
