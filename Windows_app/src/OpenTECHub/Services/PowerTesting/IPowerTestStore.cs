using System.Collections.Generic;

namespace OpenTECHub.Services.PowerTesting;

public interface IPowerTestStore
{
    string RootDirectory { get; }

    IReadOnlyList<PowerTestSummary> ListTests();

    PowerTestDocument? LoadTest(string folderName);

    PowerTestDocument CreateTest(
        string name,
        FluidProperties fluid,
        PowerGeometry geometry,
        PowerTestSettings settings,
        IReadOnlyList<PowerCondition>? initialConditions = null,
        PowerMapReference? linkedMap = null);

    PowerTestDocument RenameTest(string folderName, string newName);

    /// <summary>Moves an assay to the store's internal recovery folder instead of erasing it.</summary>
    bool DeleteTest(string folderName);

    void SaveTestManifest(PowerTestDocument doc);

    IReadOnlyList<PowerCondition> LoadConditionsTable(string testFolderName);

    void SaveConditionsTable(string testFolderName, IReadOnlyList<PowerCondition> conditions);

    void SaveTare(string testFolderName, TareCurve tare);
    void ClearTare(string testFolderName) => throw new NotSupportedException("Remoção de tara indisponível neste armazenamento.");

    TareCurve? LoadTare(string testFolderName);

    // ---- Tare profile library ------------------------------------------------
    // A tare belongs to a shaft, not to an assay. These keep one named curve per shaft
    // for the whole store, so a bench with two shafts can hold both and attach whichever
    // matches the one currently mounted.

    /// <summary>Filed tare profiles, newest measurement first, without loading their samples.</summary>
    IReadOnlyList<TareProfileSummary> ListTareProfiles();

    /// <summary>The filed curve for <paramref name="profileName"/>, or null when absent.</summary>
    TareCurve? LoadTareProfile(string profileName);

    /// <summary>Files <paramref name="tare"/> under <paramref name="profileName"/>, replacing any curve already there.</summary>
    void SaveTareProfile(string profileName, TareCurve tare);

    /// <summary>Removes a filed profile. Returns false when there was nothing to remove.</summary>
    bool DeleteTareProfile(string profileName);

    // ---- Raw tare readings ---------------------------------------------------
    // Written while the sweep runs, so a sweep that never converges still leaves its
    // measurements on disk. One file per sweep: a retry never overwrites an earlier attempt.

    /// <summary>Opens a raw file for one sweep and returns its name inside the assay folder.</summary>
    string BeginTareRawCapture(string testFolderName, DateTimeOffset startedUtc);

    /// <summary>Appends one accepted tare reading to the sweep opened by <see cref="BeginTareRawCapture"/>.</summary>
    void AppendTareRawSample(string testFolderName, string rawFileName, TareSample sample, int pointIndex);

    /// <summary>Absolute path of a sweep's raw file, whether or not it exists yet.</summary>
    string GetTareRawDataPath(string testFolderName, string rawFileName);

    /// <summary>Reads back a sweep's raw readings; empty when the file is absent.</summary>
    IReadOnlyList<TareSample> LoadTareRawData(string testFolderName, string rawFileName);

    // ---- Single-point checks -------------------------------------------------
    // The panel can run with no assay open, so these take a nullable folder and fall back to
    // a store-level folder rather than refusing to record.

    /// <summary>Opens a raw file for one single-point capture and returns its name.</summary>
    string BeginSinglePointCapture(string? testFolderName, SinglePointSession session);

    /// <summary>Appends one reading to the capture opened by <see cref="BeginSinglePointCapture"/>.</summary>
    void AppendSinglePointSample(string? testFolderName, string rawFileName, PowerDataPoint point);

    /// <summary>Writes the capture's manifest beside its CSV, sealing the sample count and end time.</summary>
    void CompleteSinglePointCapture(string? testFolderName, SinglePointSession session);

    /// <summary>Absolute path of a single-point capture's raw file, whether or not it exists yet.</summary>
    string GetSinglePointDataPath(string? testFolderName, string rawFileName);

    void SaveCalibration(string testFolderName, TorqueCalibration calibration);

    TorqueCalibration? LoadCalibration(string testFolderName);

    string InitializeRunFolder(string testFolderName, PowerRun run);

    void AppendRunRawDataPoint(string testFolderName, string runFolderName, PowerDataPoint point);

    string GetRunRawDataPath(string testFolderName, string runFolderName);

    IReadOnlyList<PowerDataPoint> LoadRunRawData(string testFolderName, string runFolderName);

    /// <summary>Atomically writes the scientific result for one run and seals its raw-data hash.</summary>
    void SaveRunResult(string testFolderName, PowerRun run);

    void AppendGlobalSeriesSample(string testFolderName, PowerGlobalSeriesSample sample);

    void AppendEventLog(string testFolderName, PowerTestEventLogEntry entry);

    /// <summary>Rebuilds the test-wide, condition-level export from accepted runs.</summary>
    void UpdateResultsSummary(string testFolderName, PowerTestDocument doc);

    void SaveFlooding(string testFolderName, FloodingAnalysisResult flooding);

    bool ValidateTestName(string name, out string? error);

    bool TestExists(string name);
}
