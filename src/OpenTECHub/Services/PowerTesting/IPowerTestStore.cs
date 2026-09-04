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
