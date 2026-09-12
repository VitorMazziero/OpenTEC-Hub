using System.Collections.Generic;

namespace OpenTECHub.Services.KlaTesting;

public interface IKlaTestStore
{
    string RootDirectory { get; }

    /// <summary>
    /// Completes when every write issued so far has reached the file system. Saves are queued to
    /// a background writer (D-048); call this before handing the files to something outside the
    /// store, or when shutting down.
    /// </summary>
    Task FlushAsync() => Task.CompletedTask;

    /// <summary>A queued write failed: the path and the exception. The store keeps going; the runner decides what to tell the operator.</summary>
    event Action<string, Exception>? WriteFailed { add { } remove { } }

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

    /// <summary>
    /// Rewrites the run's raw CSV in full and returns the SHA-256 the file has once written — the
    /// same value <see cref="KlaTestFileContracts.ComputeFileSha256"/> gives for it — computed
    /// from the bytes handed to the writer, so the seal does not wait for the queue to drain.
    /// </summary>
    string SaveRunRawData(string testFolderName, string runFolderName, IEnumerable<KlaRawDataPoint> points);

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
