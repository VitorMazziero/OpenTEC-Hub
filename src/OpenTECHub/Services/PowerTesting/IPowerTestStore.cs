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

    void AppendGlobalSeriesSample(string testFolderName, PowerGlobalSeriesSample sample);

    void AppendEventLog(string testFolderName, PowerTestEventLogEntry entry);

    bool ValidateTestName(string name, out string? error);

    bool TestExists(string name);
}
