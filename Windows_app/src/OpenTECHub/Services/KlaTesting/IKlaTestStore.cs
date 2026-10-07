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
        KlaMapReference? linkedMap = null,
        IReadOnlyList<KlaTestCondition>? initialConditions = null);

    KlaTestDocument CreateTest(string name, KlaAssayDefinition definition, KlaMapReference? linkedMap = null)
    {
        definition.Validate(requireConditions: false);
        var doc = CreateTest(name, definition.Settings, linkedMap,
            definition.Conditions.Select(c => c.ToSessionCondition()).ToArray());
        doc.Protocol = definition.Protocol;
        doc.CaptureMode = definition.CaptureMode;
        doc.ProtocolSettings = definition.ProtocolSettings;
        doc.Context = definition.Context;
        doc.SequenceLimits = definition.SequenceLimits;
        doc.Nature = definition.Protocol == KlaAssayProtocol.Biotic ? "Biotico" : "Abiotico";
        SaveTestManifest(doc);
        return doc;
    }

    void SaveTestManifest(KlaTestDocument doc);

    IReadOnlyList<KlaTestCondition> LoadConditionsTable(string testFolderName);

    void SaveConditionsTable(string testFolderName, IReadOnlyList<KlaTestCondition> conditions);

    string InitializeRunFolder(string testFolderName, KlaTestRun run);

    KlaRunDefinition? LoadRunDefinition(string testFolderName, string runFolderName) => null;
    void SaveRunAcquisition(string testFolderName, string runFolderName, KlaAcquisitionMetadata metadata) { }
    void SaveRunPhysicalOutcome(string testFolderName, string runFolderName, KlaRunOutcome outcome) { }
    void SaveRunGasEvents(string testFolderName, string runFolderName, IReadOnlyList<KlaGasEvent> events) { }
    IReadOnlyList<KlaGasEvent> LoadRunGasEvents(string testFolderName, string runFolderName) => [];

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
