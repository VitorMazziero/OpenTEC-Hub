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
