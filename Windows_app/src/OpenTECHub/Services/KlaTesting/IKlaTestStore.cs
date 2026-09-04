using System.Collections.Generic;

namespace OpenTECHub.Services.KlaTesting;

public interface IKlaTestStore
{
    string RootDirectory { get; }

    IReadOnlyList<KlaTestSummary> ListTests();

    KlaTestDocument? LoadTest(string folderName);

    string ImportTestFolder(string sourceFolder);

    KlaTestDocument CreateTest(
        string name,
        KlaTestSettings settings,
        NitrogenValve n2Valve,
        KlaMapReference? linkedMap = null,
        IReadOnlyList<KlaTestCondition>? initialConditions = null,
        NitrogenValve ventValve = NitrogenValve.Valve2);

    void SaveTestManifest(KlaTestDocument doc);

    IReadOnlyList<KlaTestCondition> LoadConditionsTable(string testFolderName);

    void SaveConditionsTable(string testFolderName, IReadOnlyList<KlaTestCondition> conditions);

    string InitializeRunFolder(string testFolderName, KlaTestRun run);

    void SaveRunRawData(string testFolderName, string runFolderName, IEnumerable<KlaRawDataPoint> points);

    void AppendRunRawDataPoint(string testFolderName, string runFolderName, KlaRawDataPoint point);

    string GetRunRawDataPath(string testFolderName, string runFolderName);

    IReadOnlyList<KlaRawDataPoint> LoadRunRawData(string testFolderName, string runFolderName);

    void SaveRunAnalysis(string testFolderName, string runFolderName, KlaAnalysisRevision analysis);

    KlaAnalysisRevision? LoadRunAnalysis(string testFolderName, string runFolderName);

    void SaveRunResult(string testFolderName, string runFolderName, KlaTestRun run, KlaAnalysisRevision analysis);

    void UpdateResultsSummary(string testFolderName, KlaTestDocument doc);

    void AppendGlobalSeriesSample(string testFolderName, KlaGlobalSeriesSample sample);

    void AppendEventLog(string testFolderName, KlaTestEventLogEntry entry);

    bool ValidateTestName(string name, out string? error);

    bool TestExists(string name);
}
