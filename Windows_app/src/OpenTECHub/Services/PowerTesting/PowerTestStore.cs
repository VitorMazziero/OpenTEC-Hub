using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.Services.PowerTesting;

/// <summary>
/// On-disk store for self-contained power assays under <c>Testes-Potencia/</c>. Each test owns
/// its folder and files; nothing here depends on a cultivation session (§6). Mirrors
/// <see cref="KlaTesting.KlaTestStore"/> in shape.
/// </summary>
public sealed class PowerTestStore : IPowerTestStore
{
    internal const string TrashDirectoryName = ".Lixeira";
    private readonly string _rootDirectory;
    private readonly object _ioLock = new();

    public PowerTestStore(string? rootDirectory = null)
    {
        _rootDirectory = rootDirectory ?? AppPaths.PowerTestsDirectory;
        Directory.CreateDirectory(_rootDirectory);
    }

    public string RootDirectory => _rootDirectory;

    public bool ValidateTestName(string name, out string? error)
    {
        if (!PowerTestFileContracts.ValidateTestName(name, out error))
        {
            return false;
        }

        if (IsReservedFolderName(name.Trim()))
        {
            error = "Esse nome é reservado pela pasta de ensaios (lixeira interna, biblioteca de taras ou pontos únicos).";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Folders the store owns itself, which are neither assays nor available as assay names.
    /// </summary>
    private static bool IsReservedFolderName(string folderName) =>
        string.Equals(folderName, TrashDirectoryName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(folderName, PowerTestFileContracts.TareProfilesDirectoryName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(folderName, PowerTestFileContracts.SinglePointDirectoryName, StringComparison.OrdinalIgnoreCase);

    public bool TestExists(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return Directory.Exists(Path.Combine(_rootDirectory, name.Trim()));
    }

    public IReadOnlyList<PowerTestSummary> ListTests()
    {
        lock (_ioLock)
        {
            if (!Directory.Exists(_rootDirectory))
            {
                return [];
            }

            var list = new List<PowerTestSummary>();
            foreach (var dir in Directory.GetDirectories(_rootDirectory))
            {
                var folderName = Path.GetFileName(dir);
                if (IsReservedFolderName(folderName))
                {
                    continue;
                }
                var manifestPath = Path.Combine(dir, PowerTestFileContracts.TestManifestFileName);

                if (File.Exists(manifestPath))
                {
                    try
                    {
                        var doc = PowerTestFileContracts.DeserializeTestDocument(File.ReadAllText(manifestPath));
                        if (doc is not null)
                        {
                            var completed = doc.Runs.Count(r => r.Phase is PowerRunPhase.Accepted or PowerRunPhase.Rejected);
                            var accepted = doc.Runs.Count(r => r.Phase == PowerRunPhase.Accepted);
                            list.Add(new PowerTestSummary(
                                folderName,
                                doc.Name,
                                doc.TestId,
                                doc.Status,
                                doc.CreatedUtc,
                                doc.CompletedUtc,
                                doc.RelativeMode,
                                doc.Conditions.Count,
                                completed,
                                accepted));
                            continue;
                        }
                    }
                    catch
                    {
                        // Fall through to the directory-only summary.
                    }
                }

                list.Add(new PowerTestSummary(
                    folderName,
                    folderName,
                    Guid.Empty,
                    PowerTestStatus.Interrupted,
                    Directory.GetCreationTimeUtc(dir),
                    null,
                    false,
                    0,
                    0,
                    0));
            }

            return list.OrderByDescending(t => t.CreatedUtc).ToList();
        }
    }

    public PowerTestDocument? LoadTest(string folderName)
    {
        lock (_ioLock)
        {
            var folderPath = Path.Combine(_rootDirectory, folderName);
            var manifestPath = Path.Combine(folderPath, PowerTestFileContracts.TestManifestFileName);
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            var doc = PowerTestFileContracts.DeserializeTestDocument(File.ReadAllText(manifestPath));
            if (doc is null)
            {
                return null;
            }

            doc.FolderName = folderName;

            // A test left "Running" means the app died mid-assay; recover it as interrupted.
            if (doc.Status == PowerTestStatus.Running)
            {
                doc.Status = PowerTestStatus.Interrupted;
                doc.InterruptionReason = "Execução anterior encerrada sem fechamento registrado.";
                doc.LastModifiedUtc = DateTimeOffset.UtcNow;
                WriteAllTextAtomic(manifestPath, PowerTestFileContracts.SerializeTestDocument(doc));
            }

            var conditionsPath = Path.Combine(folderPath, PowerTestFileContracts.ConditionTableFileName);
            if (File.Exists(conditionsPath))
            {
                try
                {
                    var conditions = PowerTestFileContracts.DeserializeConditionTable(File.ReadAllText(conditionsPath));
                    if (conditions is { Count: > 0 })
                    {
                        doc.Conditions = conditions;
                    }
                }
                catch
                {
                    // Keep the manifest's conditions.
                }
            }

            if (doc.Status != PowerTestStatus.Running)
            {
                foreach (var cond in doc.Conditions.Where(c => c.Status == PowerConditionStatus.InProgress))
                {
                    cond.Status = cond.AcceptedReplicates >= cond.RequestedReplicates
                        ? PowerConditionStatus.Completed
                        : PowerConditionStatus.Pending;
                }
            }

            doc.Tare = LoadTare(folderName) ?? doc.Tare;
            doc.Calibration = LoadCalibration(folderName) ?? doc.Calibration;

            DemoteAcceptedRunsWithoutCapture(folderName, folderPath, manifestPath, doc);

            return doc;
        }
    }

    /// <summary>
    /// Manifests written before D-050 may hold runs accepted with no capture (n = 0, P = 0 W) —
    /// the bench of 2026-09-11 had two. They are demoted to <c>Rejected</c> once, on load, the
    /// condition is reopened so the sequence returns to it, the summary is regenerated and the
    /// migration is journalled. Runs stay in the manifest: nothing measured is discarded.
    /// </summary>
    private void DemoteAcceptedRunsWithoutCapture(string folderName, string folderPath, string manifestPath, PowerTestDocument doc)
    {
        var demoted = new List<PowerRunSummary>();
        for (var i = 0; i < doc.Runs.Count; i++)
        {
            var run = doc.Runs[i];
            if (run.Phase == PowerRunPhase.Accepted && PowerTestFileContracts.IsRunWithoutCapture(run))
            {
                doc.Runs[i] = run with { Phase = PowerRunPhase.Rejected };
                demoted.Add(run);
            }
        }

        if (demoted.Count == 0)
        {
            return;
        }

        foreach (var condition in doc.Conditions)
        {
            if (demoted.All(r => r.ConditionId != condition.ConditionId))
            {
                continue;
            }

            condition.AcceptedReplicates = doc.Runs.Count(r => r.ConditionId == condition.ConditionId && r.Phase == PowerRunPhase.Accepted);
            condition.RejectedReplicates = doc.Runs.Count(r => r.ConditionId == condition.ConditionId && r.Phase == PowerRunPhase.Rejected);
            if (condition.Status == PowerConditionStatus.Completed && condition.AcceptedReplicates < condition.RequestedReplicates)
            {
                condition.Status = PowerConditionStatus.Pending;
            }
        }

        if (doc.Status == PowerTestStatus.Completed && doc.Conditions.Any(c => c.Status == PowerConditionStatus.Pending))
        {
            doc.Status = PowerTestStatus.Interrupted;
            doc.InterruptionReason = "Corridas aceitas sem captura foram rejeitadas na migração; condições reabertas.";
        }

        doc.LastModifiedUtc = DateTimeOffset.UtcNow;
        WriteAllTextAtomic(manifestPath, PowerTestFileContracts.SerializeTestDocument(doc));
        WriteAllTextAtomic(
            Path.Combine(folderPath, PowerTestFileContracts.ConditionTableFileName),
            PowerTestFileContracts.SerializeConditionTable(doc.Conditions));
        UpdateResultsSummaryCore(folderName, doc);
        foreach (var run in demoted)
        {
            var reason = $"sem captura (migração): {run.FolderName} estava aceita com n = {run.SampleCount}";
            File.AppendAllText(
                Path.Combine(folderPath, PowerTestFileContracts.EventLogFileName),
                PowerTestFileContracts.FormatEventLogLine(new PowerTestEventLogEntry(DateTimeOffset.UtcNow, "RunRejected", reason)) + Environment.NewLine,
                Encoding.UTF8);
        }
    }

    public PowerTestDocument CreateTest(
        string name,
        FluidProperties fluid,
        PowerGeometry geometry,
        PowerTestSettings settings,
        IReadOnlyList<PowerCondition>? initialConditions = null,
        PowerMapReference? linkedMap = null)
    {
        if (!ValidateTestName(name, out var error))
        {
            throw new ArgumentException(error ?? "Nome de ensaio inválido.", nameof(name));
        }

        var trimmedName = name.Trim();
        var folderPath = Path.Combine(_rootDirectory, trimmedName);

        lock (_ioLock)
        {
            if (Directory.Exists(folderPath))
            {
                throw new InvalidOperationException($"Já existe um ensaio com o nome '{trimmedName}'.");
            }

            Directory.CreateDirectory(folderPath);
            Directory.CreateDirectory(Path.Combine(folderPath, PowerTestFileContracts.RunsDirectoryName));

            var conditions = initialConditions?.Select((c, idx) =>
            {
                var clone = c.Clone();
                clone.OrderIndex = idx;
                return clone;
            }).ToList() ?? [];

            var doc = new PowerTestDocument
            {
                TestId = Guid.NewGuid(),
                Name = trimmedName,
                FolderName = trimmedName,
                Status = PowerTestStatus.Draft,
                CreatedUtc = DateTimeOffset.UtcNow,
                LastModifiedUtc = DateTimeOffset.UtcNow,
                Fluid = fluid,
                Geometry = geometry,
                Settings = settings,
                SettingsRevision = 1,
                RelativeMode = true, // até haver tara/calibração, o ensaio é relativo (§9, §20.5)
                LinkedMap = linkedMap,
                AppVersion = typeof(PowerTestStore).Assembly.GetName().Version?.ToString() ?? "desconhecida",
                Conditions = conditions,
                Runs = [],
            };

            WriteAllTextAtomic(
                Path.Combine(folderPath, PowerTestFileContracts.TestManifestFileName),
                PowerTestFileContracts.SerializeTestDocument(doc));
            WriteAllTextAtomic(
                Path.Combine(folderPath, PowerTestFileContracts.ConditionTableFileName),
                PowerTestFileContracts.SerializeConditionTable(conditions));

            File.WriteAllText(
                Path.Combine(folderPath, PowerTestFileContracts.GlobalSeriesFileName),
                PowerTestFileContracts.FormatGlobalSeriesHeader() + Environment.NewLine, Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(folderPath, PowerTestFileContracts.EventLogFileName), "", Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(folderPath, PowerTestFileContracts.ResultsSummaryFileName),
                PowerTestFileContracts.FormatResultsSummaryHeader() + Environment.NewLine, Encoding.UTF8);

            return doc;
        }
    }

    public PowerTestDocument RenameTest(string folderName, string newName)
    {
        if (!ValidateTestName(newName, out var error))
        {
            throw new ArgumentException(error ?? "Nome de ensaio inválido.", nameof(newName));
        }

        var trimmedName = newName.Trim();
        lock (_ioLock)
        {
            var sourcePath = ResolveImmediateTestFolder(folderName);
            if (!Directory.Exists(sourcePath))
            {
                throw new DirectoryNotFoundException($"O ensaio '{folderName}' não foi encontrado.");
            }

            var destinationPath = ResolveImmediateTestFolder(trimmedName);
            if (!string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(destinationPath))
            {
                throw new InvalidOperationException($"Já existe um ensaio com o nome '{trimmedName}'.");
            }

            var manifestPath = Path.Combine(sourcePath, PowerTestFileContracts.TestManifestFileName);
            var doc = File.Exists(manifestPath)
                ? PowerTestFileContracts.DeserializeTestDocument(File.ReadAllText(manifestPath))
                : null;
            if (doc is null)
            {
                throw new InvalidOperationException("O manifesto do ensaio não pôde ser lido.");
            }

            if (!string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Move(sourcePath, destinationPath);
            }

            try
            {
                doc.Name = trimmedName;
                doc.FolderName = trimmedName;
                doc.LastModifiedUtc = DateTimeOffset.UtcNow;
                WriteAllTextAtomic(
                    Path.Combine(destinationPath, PowerTestFileContracts.TestManifestFileName),
                    PowerTestFileContracts.SerializeTestDocument(doc));
                return doc;
            }
            catch
            {
                if (!string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(destinationPath) && !Directory.Exists(sourcePath))
                {
                    Directory.Move(destinationPath, sourcePath);
                }
                throw;
            }
        }
    }

    public bool DeleteTest(string folderName)
    {
        lock (_ioLock)
        {
            var sourcePath = ResolveImmediateTestFolder(folderName);
            if (!Directory.Exists(sourcePath))
            {
                return false;
            }

            var trashRoot = Path.GetFullPath(Path.Combine(_rootDirectory, TrashDirectoryName));
            Directory.CreateDirectory(trashRoot);
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmssfff", CultureInfo.InvariantCulture);
            var destinationName = $"{Path.GetFileName(sourcePath)}_{timestamp}_{Guid.NewGuid():N}";
            var destinationPath = Path.GetFullPath(Path.Combine(trashRoot, destinationName));
            if (!string.Equals(Path.GetDirectoryName(destinationPath), trashRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Destino inválido para a lixeira interna.");
            }

            Directory.Move(sourcePath, destinationPath);
            return true;
        }
    }

    public void SaveTestManifest(PowerTestDocument doc)
    {
        lock (_ioLock)
        {
            var folderPath = Path.Combine(_rootDirectory, doc.FolderName);
            Directory.CreateDirectory(folderPath);

            doc.LastModifiedUtc = DateTimeOffset.UtcNow;
            WriteAllTextAtomic(
                Path.Combine(folderPath, PowerTestFileContracts.TestManifestFileName),
                PowerTestFileContracts.SerializeTestDocument(doc));
        }
    }

    public IReadOnlyList<PowerCondition> LoadConditionsTable(string testFolderName)
    {
        lock (_ioLock)
        {
            var condPath = Path.Combine(_rootDirectory, testFolderName, PowerTestFileContracts.ConditionTableFileName);
            if (!File.Exists(condPath))
            {
                return [];
            }

            return PowerTestFileContracts.DeserializeConditionTable(File.ReadAllText(condPath)) ?? [];
        }
    }

    public void SaveConditionsTable(string testFolderName, IReadOnlyList<PowerCondition> conditions)
    {
        lock (_ioLock)
        {
            var folderPath = Path.Combine(_rootDirectory, testFolderName);
            Directory.CreateDirectory(folderPath);
            WriteAllTextAtomic(
                Path.Combine(folderPath, PowerTestFileContracts.ConditionTableFileName),
                PowerTestFileContracts.SerializeConditionTable(conditions));
        }
    }

    public void SaveTare(string testFolderName, TareCurve tare)
    {
        lock (_ioLock)
        {
            var folderPath = Path.Combine(_rootDirectory, testFolderName);
            Directory.CreateDirectory(folderPath);
            WriteAllTextAtomic(
                Path.Combine(folderPath, PowerTestFileContracts.TareFileName),
                PowerTestFileContracts.SerializeTare(tare));
        }
    }

    public void ClearTare(string testFolderName)
    {
        lock (_ioLock)
        {
            File.Delete(Path.Combine(_rootDirectory, testFolderName, PowerTestFileContracts.TareFileName));
        }
    }

    public TareCurve? LoadTare(string testFolderName)
    {
        lock (_ioLock)
        {
            var path = Path.Combine(_rootDirectory, testFolderName, PowerTestFileContracts.TareFileName);
            return File.Exists(path) ? PowerTestFileContracts.DeserializeTare(File.ReadAllText(path)) : null;
        }
    }

    // ---- Tare profile library ------------------------------------------------

    private string TareProfilesDirectory =>
        Path.Combine(_rootDirectory, PowerTestFileContracts.TareProfilesDirectoryName);

    public IReadOnlyList<TareProfileSummary> ListTareProfiles()
    {
        lock (_ioLock)
        {
            var dir = TareProfilesDirectory;
            if (!Directory.Exists(dir))
            {
                return [];
            }

            var list = new List<TareProfileSummary>();
            foreach (var path in Directory.GetFiles(dir, "*.json"))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                try
                {
                    var curve = PowerTestFileContracts.DeserializeTare(File.ReadAllText(path));
                    if (curve is not null)
                    {
                        list.Add(new TareProfileSummary(
                            name,
                            curve.Points.Count,
                            curve.MeasuredUtc,
                            curve.ImpellerSetHash));
                    }
                }
                catch (Exception)
                {
                    // A corrupt profile must not hide the healthy ones; it is simply not listed.
                }
            }

            return [.. list.OrderByDescending(p => p.MeasuredUtc)];
        }
    }

    public TareCurve? LoadTareProfile(string profileName)
    {
        if (!PowerTestFileContracts.ValidateTareProfileName(profileName, out _))
        {
            return null;
        }

        lock (_ioLock)
        {
            var path = Path.Combine(
                TareProfilesDirectory, PowerTestFileContracts.TareProfileFileName(profileName));
            if (!File.Exists(path))
            {
                return null;
            }

            var curve = PowerTestFileContracts.DeserializeTare(File.ReadAllText(path));

            // The name lives in the file name, so a profile written by an older build - or
            // renamed on disk - still comes back carrying the name it was filed under.
            return curve is null ? null : curve with { ProfileName = profileName.Trim() };
        }
    }

    public void SaveTareProfile(string profileName, TareCurve tare)
    {
        if (!PowerTestFileContracts.ValidateTareProfileName(profileName, out var error))
        {
            throw new ArgumentException(error, nameof(profileName));
        }

        lock (_ioLock)
        {
            Directory.CreateDirectory(TareProfilesDirectory);
            WriteAllTextAtomic(
                Path.Combine(TareProfilesDirectory, PowerTestFileContracts.TareProfileFileName(profileName)),
                PowerTestFileContracts.SerializeTare(tare with { ProfileName = profileName.Trim() }));
        }
    }

    public bool DeleteTareProfile(string profileName)
    {
        if (!PowerTestFileContracts.ValidateTareProfileName(profileName, out _))
        {
            return false;
        }

        lock (_ioLock)
        {
            var path = Path.Combine(
                TareProfilesDirectory, PowerTestFileContracts.TareProfileFileName(profileName));
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
    }

    public void SaveCalibration(string testFolderName, TorqueCalibration calibration)
    {
        lock (_ioLock)
        {
            var folderPath = Path.Combine(_rootDirectory, testFolderName);
            Directory.CreateDirectory(folderPath);
            WriteAllTextAtomic(
                Path.Combine(folderPath, PowerTestFileContracts.CalibrationFileName),
                PowerTestFileContracts.SerializeCalibration(calibration));
        }
    }

    public TorqueCalibration? LoadCalibration(string testFolderName)
    {
        lock (_ioLock)
        {
            var path = Path.Combine(_rootDirectory, testFolderName, PowerTestFileContracts.CalibrationFileName);
            return File.Exists(path) ? PowerTestFileContracts.DeserializeCalibration(File.ReadAllText(path)) : null;
        }
    }

    public string InitializeRunFolder(string testFolderName, PowerRun run)
    {
        lock (_ioLock)
        {
            var baseName = PowerTestFileContracts.FormatRunFolderName(run.AgitationRpm, run.GasFlowLpm, run.ReplicateNumber);
            var runsPath = Path.Combine(_rootDirectory, testFolderName, PowerTestFileContracts.RunsDirectoryName);
            Directory.CreateDirectory(runsPath);

            var runFolder = baseName;
            var runPath = Path.Combine(runsPath, runFolder);
            var attempt = 2;
            while (Directory.Exists(runPath))
            {
                runFolder = $"{baseName}_Tentativa{attempt:D2}";
                runPath = Path.Combine(runsPath, runFolder);
                attempt++;
            }

            run.FolderName = runFolder;
            Directory.CreateDirectory(runPath);
            WriteAllTextAtomic(
                Path.Combine(runPath, PowerTestFileContracts.RunRawDataFileName),
                PowerTestFileContracts.FormatRawDataHeader() + Environment.NewLine);

            return runFolder;
        }
    }

    // ---- Raw tare readings ---------------------------------------------------

    public string BeginTareRawCapture(string testFolderName, DateTimeOffset startedUtc)
    {
        lock (_ioLock)
        {
            var directory = Path.Combine(
                _rootDirectory, testFolderName, PowerTestFileContracts.TareRawDirectoryName);
            Directory.CreateDirectory(directory);

            var fileName = PowerTestFileContracts.TareRawFileName(startedUtc);
            var path = Path.Combine(directory, fileName);

            // Two sweeps started inside the same second would otherwise share a file and
            // interleave their rungs, which no reader could separate afterwards.
            var attempt = 2;
            while (File.Exists(path))
            {
                fileName = Path.ChangeExtension(
                    PowerTestFileContracts.TareRawFileName(startedUtc), null) + $"_{attempt:D2}.csv";
                path = Path.Combine(directory, fileName);
                attempt++;
            }

            File.WriteAllText(
                path, PowerTestFileContracts.FormatTareRawHeader() + Environment.NewLine, Encoding.UTF8);
            return fileName;
        }
    }

    public void AppendTareRawSample(string testFolderName, string rawFileName, TareSample sample, int pointIndex)
    {
        lock (_ioLock)
        {
            var path = GetTareRawDataPath(testFolderName, rawFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                File.AppendAllText(
                    path, PowerTestFileContracts.FormatTareRawHeader() + Environment.NewLine, Encoding.UTF8);
            }

            File.AppendAllText(
                path,
                PowerTestFileContracts.FormatTareRawRow(sample, pointIndex) + Environment.NewLine,
                Encoding.UTF8);
        }
    }

    public string GetTareRawDataPath(string testFolderName, string rawFileName) =>
        Path.Combine(_rootDirectory, testFolderName, PowerTestFileContracts.TareRawDirectoryName, rawFileName);

    public IReadOnlyList<TareSample> LoadTareRawData(string testFolderName, string rawFileName)
    {
        lock (_ioLock)
        {
            var path = GetTareRawDataPath(testFolderName, rawFileName);
            if (!File.Exists(path))
            {
                return [];
            }

            var list = new List<TareSample>();
            var lines = File.ReadAllLines(path);
            for (var i = 1; i < lines.Length; i++)
            {
                var parts = lines[i].Trim().Split(',');
                if (parts.Length < 9 ||
                    !DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts) ||
                    !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var elapsed) ||
                    !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var targetRpm) ||
                    !double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var rpm) ||
                    !double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var torque))
                {
                    continue;
                }

                if (!Enum.TryParse<TareCapturePhase>(parts[6], out var phase))
                {
                    phase = TareCapturePhase.StabilizingSpeed;
                }

                var counted = parts[7] is "1" or "True";
                if (!int.TryParse(parts[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var attempt))
                {
                    attempt = 1;
                }

                list.Add(new TareSample(ts, elapsed, targetRpm, rpm, torque, phase, counted, attempt));
            }

            return list;
        }
    }

    // ---- Single-point checks -------------------------------------------------

    public string BeginSinglePointCapture(string? testFolderName, SinglePointSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_ioLock)
        {
            var directory = SinglePointDirectory(testFolderName);
            Directory.CreateDirectory(directory);

            var fileName = PowerTestFileContracts.SinglePointFileName(
                session.StartedUtc, session.TargetRpm, session.GasFlowSetpointLpm);
            var path = Path.Combine(directory, fileName);
            var attempt = 2;
            while (File.Exists(path))
            {
                fileName = Path.ChangeExtension(
                    PowerTestFileContracts.SinglePointFileName(
                        session.StartedUtc, session.TargetRpm, session.GasFlowSetpointLpm), null) + $"_{attempt:D2}.csv";
                path = Path.Combine(directory, fileName);
                attempt++;
            }

            File.WriteAllText(
                path, PowerTestFileContracts.FormatRawDataHeader() + Environment.NewLine, Encoding.UTF8);

            // The manifest is written up front, not at the end: a capture cut short by a crash
            // or a lost shaft still has to say what speed and flow its rows were taken under.
            WriteSinglePointManifest(directory, session with { RawDataFileName = fileName });
            return fileName;
        }
    }

    public void AppendSinglePointSample(string? testFolderName, string rawFileName, PowerDataPoint point)
    {
        lock (_ioLock)
        {
            var path = GetSinglePointDataPath(testFolderName, rawFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                File.AppendAllText(
                    path, PowerTestFileContracts.FormatRawDataHeader() + Environment.NewLine, Encoding.UTF8);
            }

            File.AppendAllText(
                path, PowerTestFileContracts.FormatRawDataRow(point) + Environment.NewLine, Encoding.UTF8);
        }
    }

    public void CompleteSinglePointCapture(string? testFolderName, SinglePointSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_ioLock)
        {
            var directory = SinglePointDirectory(testFolderName);
            Directory.CreateDirectory(directory);
            WriteSinglePointManifest(directory, session);
        }
    }

    public string GetSinglePointDataPath(string? testFolderName, string rawFileName) =>
        Path.Combine(SinglePointDirectory(testFolderName), rawFileName);

    /// <summary>
    /// Where a single-point capture is filed: inside the assay when one is open, otherwise in
    /// the store's own folder, so the panel records with or without an assay.
    /// </summary>
    private string SinglePointDirectory(string? testFolderName) =>
        string.IsNullOrWhiteSpace(testFolderName)
            ? Path.Combine(_rootDirectory, PowerTestFileContracts.SinglePointDirectoryName)
            : Path.Combine(_rootDirectory, testFolderName.Trim(), PowerTestFileContracts.SinglePointDirectoryName);

    private static void WriteSinglePointManifest(string directory, SinglePointSession session)
    {
        var manifestPath = Path.Combine(
            directory,
            Path.ChangeExtension(session.RawDataFileName, null) + PowerTestFileContracts.SinglePointManifestSuffix);
        WriteAllTextAtomic(manifestPath, PowerTestFileContracts.SerializeSinglePointSession(session));
    }

    public string GetRunRawDataPath(string testFolderName, string runFolderName) =>
        Path.Combine(_rootDirectory, testFolderName, PowerTestFileContracts.RunsDirectoryName, runFolderName, PowerTestFileContracts.RunRawDataFileName);

    public void AppendRunRawDataPoint(string testFolderName, string runFolderName, PowerDataPoint point)
    {
        lock (_ioLock)
        {
            var filePath = GetRunRawDataPath(testFolderName, runFolderName);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            if (!File.Exists(filePath) || new FileInfo(filePath).Length == 0)
            {
                File.AppendAllText(filePath, PowerTestFileContracts.FormatRawDataHeader() + Environment.NewLine, Encoding.UTF8);
            }
            File.AppendAllText(filePath, PowerTestFileContracts.FormatRawDataRow(point) + Environment.NewLine, Encoding.UTF8);
        }
    }

    public IReadOnlyList<PowerDataPoint> LoadRunRawData(string testFolderName, string runFolderName)
    {
        lock (_ioLock)
        {
            var filePath = GetRunRawDataPath(testFolderName, runFolderName);
            if (!File.Exists(filePath))
            {
                return [];
            }

            var list = new List<PowerDataPoint>();
            var lines = File.ReadAllLines(filePath);
            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var parts = line.Split(',');
                if (parts.Length < 9)
                {
                    continue;
                }

                if (!DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts) ||
                    !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var relSec))
                {
                    continue;
                }
                if (!Enum.TryParse<PowerRunPhase>(parts[2], out var phase))
                {
                    phase = PowerRunPhase.Idle;
                }
                _ = double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var rpm);
                _ = double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var torquePct);
                _ = double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var torqueNm);
                _ = double.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var powerW);
                double? flow = double.TryParse(parts[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedFlow)
                    ? parsedFlow
                    : null;
                var counted = parts[8] is "1" or "True" or "true";
                var attempt = parts.Length >= 10 && int.TryParse(parts[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedAttempt)
                    ? Math.Max(1, parsedAttempt)
                    : 1;

                list.Add(new PowerDataPoint(ts, relSec, phase, rpm, torquePct, torqueNm, powerW, flow, counted, attempt));
            }

            return list;
        }
    }

    public void SaveRunResult(string testFolderName, PowerRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (string.IsNullOrWhiteSpace(run.FolderName))
        {
            throw new ArgumentException("A corrida ainda não possui pasta.", nameof(run));
        }

        lock (_ioLock)
        {
            var runPath = Path.Combine(
                _rootDirectory, testFolderName, PowerTestFileContracts.RunsDirectoryName, run.FolderName);
            Directory.CreateDirectory(runPath);

            var rawPath = Path.Combine(runPath, PowerTestFileContracts.RunRawDataFileName);
            if (File.Exists(rawPath))
            {
                run.RawDataPath = Path.Combine(
                        PowerTestFileContracts.RunsDirectoryName, run.FolderName, PowerTestFileContracts.RunRawDataFileName)
                    .Replace('\\', '/');
                run.RawDataSha256 = PowerTestFileContracts.ComputeFileSha256(rawPath);
            }

            WriteAllTextAtomic(
                Path.Combine(runPath, PowerTestFileContracts.RunResultFileName),
                PowerTestFileContracts.FormatRunResultHeader() + Environment.NewLine +
                PowerTestFileContracts.FormatRunResultRow(run) + Environment.NewLine);
        }
    }

    public void AppendGlobalSeriesSample(string testFolderName, PowerGlobalSeriesSample sample)
    {
        lock (_ioLock)
        {
            var filePath = Path.Combine(_rootDirectory, testFolderName, PowerTestFileContracts.GlobalSeriesFileName);
            File.AppendAllText(filePath, PowerTestFileContracts.FormatGlobalSeriesRow(sample) + Environment.NewLine, Encoding.UTF8);
        }
    }

    public void AppendEventLog(string testFolderName, PowerTestEventLogEntry entry)
    {
        lock (_ioLock)
        {
            var filePath = Path.Combine(_rootDirectory, testFolderName, PowerTestFileContracts.EventLogFileName);
            File.AppendAllText(filePath, PowerTestFileContracts.FormatEventLogLine(entry) + Environment.NewLine, Encoding.UTF8);
        }
    }

    public void SaveFlooding(string testFolderName, FloodingAnalysisResult flooding)
    {
        ArgumentNullException.ThrowIfNull(flooding);
        lock (_ioLock)
        {
            var doc = LoadTest(testFolderName);
            if (doc is not null)
            {
                doc.Flooding = flooding;
                SaveTestManifest(doc);
            }
        }
    }

    public void UpdateResultsSummary(string testFolderName, PowerTestDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        lock (_ioLock)
        {
            UpdateResultsSummaryCore(testFolderName, doc);
        }
    }

    private void UpdateResultsSummaryCore(string testFolderName, PowerTestDocument doc)
    {
        var rows = new List<string> { PowerTestFileContracts.FormatResultsSummaryHeader() };
        foreach (var condition in doc.Conditions.OrderBy(c => c.OrderIndex))
        {
            var accepted = doc.Runs
                .Where(r => r.ConditionId == condition.ConditionId &&
                            r.Phase == PowerRunPhase.Accepted &&
                            !PowerTestFileContracts.IsRunWithoutCapture(r) &&
                            r.NetPowerW is { } value && double.IsFinite(value))
                .Select(r => r.NetPowerW!.Value)
                .ToArray();
            double? mean = accepted.Length > 0 ? accepted.Average() : null;
            double? standardDeviation = null;
            if (accepted.Length > 1 && mean is { } average)
            {
                standardDeviation = Math.Sqrt(
                    accepted.Sum(value => Math.Pow(value - average, 2)) / (accepted.Length - 1));
            }

            var npValues = doc.Runs
                .Where(r => r.ConditionId == condition.ConditionId &&
                            r.Phase == PowerRunPhase.Accepted &&
                            r.Analysis is { } analysis && double.IsFinite(analysis.AssemblyPowerNumber))
                .Select(r => r.Analysis!.AssemblyPowerNumber)
                .ToArray();
            var reValues = doc.Runs
                .Where(r => r.ConditionId == condition.ConditionId &&
                            r.Phase == PowerRunPhase.Accepted &&
                            r.Analysis is { } analysis && double.IsFinite(analysis.AssemblyReynoldsNumber))
                .Select(r => r.Analysis!.AssemblyReynoldsNumber)
                .ToArray();
            double? meanNp = npValues.Length > 0 ? npValues.Average() : null;
            double? standardDeviationNp = null;
            if (npValues.Length > 1 && meanNp is { } averageNp)
            {
                standardDeviationNp = Math.Sqrt(
                    npValues.Sum(value => Math.Pow(value - averageNp, 2)) / (npValues.Length - 1));
            }
            double? meanRe = reValues.Length > 0 ? reValues.Average() : null;

            var ratioValues = doc.Runs
                .Where(r => r.ConditionId == condition.ConditionId &&
                            r.Phase == PowerRunPhase.Accepted &&
                            r.PowerRatio is { } ratio && double.IsFinite(ratio))
                .Select(r => r.PowerRatio!.Value)
                .ToArray();
            double? meanRatio = ratioValues.Length > 0 ? ratioValues.Average() : null;
            double? stdDevRatio = null;
            if (ratioValues.Length > 1 && meanRatio is { } avgRatio)
            {
                stdDevRatio = Math.Sqrt(ratioValues.Sum(v => Math.Pow(v - avgRatio, 2)) / (ratioValues.Length - 1));
            }

            var flGValues = doc.Runs
                .Where(r => r.ConditionId == condition.ConditionId &&
                            r.Phase == PowerRunPhase.Accepted &&
                            r.GasFlowNumber is { } flg && double.IsFinite(flg))
                .Select(r => r.GasFlowNumber!.Value)
                .ToArray();
            double? meanFlG = flGValues.Length > 0 ? flGValues.Average() : null;

            var frValues = doc.Runs
                .Where(r => r.ConditionId == condition.ConditionId &&
                            r.Phase == PowerRunPhase.Accepted &&
                            r.FroudeNumber is { } fr && double.IsFinite(fr))
                .Select(r => r.FroudeNumber!.Value)
                .ToArray();
            double? meanFr = frValues.Length > 0 ? frValues.Average() : null;

            rows.Add(PowerTestFileContracts.FormatResultsSummaryRow(
                condition, mean, standardDeviation, meanNp, standardDeviationNp, meanRe,
                meanRatio, stdDevRatio, meanFlG, meanFr));
        }

        WriteAllTextAtomic(
            Path.Combine(_rootDirectory, testFolderName, PowerTestFileContracts.ResultsSummaryFileName),
            string.Join(Environment.NewLine, rows) + Environment.NewLine);
    }

    private static void WriteAllTextAtomic(string path, string contents)
    {
        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tempPath, contents, Encoding.UTF8);
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    File.Move(tempPath, path, overwrite: true);
                    return;
                }
                catch (Exception) when (attempt < 5)
                {
                    Thread.Sleep(20);
                }
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    private string ResolveImmediateTestFolder(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName) ||
            !string.Equals(Path.GetFileName(folderName), folderName, StringComparison.Ordinal) ||
            string.Equals(folderName, TrashDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Pasta de ensaio inválida.", nameof(folderName));
        }

        var root = Path.GetFullPath(_rootDirectory);
        var candidate = Path.GetFullPath(Path.Combine(root, folderName));
        if (!string.Equals(Path.GetDirectoryName(candidate), root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A pasta precisa estar diretamente na raiz de ensaios.", nameof(folderName));
        }

        return candidate;
    }
}
