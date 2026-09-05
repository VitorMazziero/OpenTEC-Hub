using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Persistence;
using Xunit;

namespace OpenTECHub.Tests;

[Collection("AppPaths")]
public sealed class KlaTestStoreTests : IDisposable
{
    private readonly string _testRoot;
    private readonly IDisposable _overrideScope;
    private readonly KlaTestStore _store;

    public KlaTestStoreTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"opentechub-klateststore-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
        _overrideScope = AppPaths.OverrideForTests(_testRoot);
        _store = new KlaTestStore(AppPaths.KlaTestsDirectory);
    }

    public void Dispose()
    {
        _overrideScope.Dispose();
        if (Directory.Exists(_testRoot))
        {
            try
            {
                Directory.Delete(_testRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    [Theory]
    [InlineData("Teste Meio 1", true)]
    [InlineData("GassingOut_2026", true)]
    [InlineData("Ensaio-01", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("Teste.", false)]
    [InlineData("Teste ", false)]
    [InlineData("CON", false)]
    [InlineData("COM1", false)]
    [InlineData("Teste/Invalido", false)]
    [InlineData("Teste:Invalido", false)]
    public void ValidateTestName_Matches_Expected_Rules(string name, bool expectedValid)
    {
        var isValid = _store.ValidateTestName(name, out var error);
        Assert.Equal(expectedValid, isValid);
        if (!expectedValid)
        {
            Assert.NotNull(error);
        }
    }

    [Fact]
    public void CreateTest_Initializes_Expected_File_Structure()
    {
        var initialConditions = new List<KlaTestCondition>
        {
            new() { AgitationRpm = 300, AirflowLpm = 2.0, RequestedReplicates = 3 },
            new() { AgitationRpm = 500, AirflowLpm = 4.0, RequestedReplicates = 2 },
        };

        var doc = _store.CreateTest(
            "Campanha 1",
            new KlaTestSettings { DOMinPercent = 10.0, DOMaxPercent = 85.0 },
            NitrogenValve.Valve2,
            new KlaMapReference { MapId = Guid.NewGuid(), MapName = "Mapa A" },
            initialConditions);

        Assert.NotNull(doc);
        Assert.Equal("Campanha 1", doc.Name);
        Assert.Equal(2, doc.Conditions.Count);
        Assert.Equal(NitrogenValve.Valve2, doc.SelectedNitrogenValve);

        var testDir = Path.Combine(_store.RootDirectory, "Campanha 1");
        Assert.True(Directory.Exists(testDir));
        Assert.True(File.Exists(Path.Combine(testDir, KlaTestFileContracts.TestManifestFileName)));
        Assert.True(File.Exists(Path.Combine(testDir, KlaTestFileContracts.ConditionTableFileName)));
        Assert.True(File.Exists(Path.Combine(testDir, KlaTestFileContracts.GlobalSeriesFileName)));
        Assert.True(File.Exists(Path.Combine(testDir, KlaTestFileContracts.ResultsSummaryFileName)));
        Assert.True(Directory.Exists(Path.Combine(testDir, KlaTestFileContracts.RunsDirectoryName)));

        var loaded = _store.LoadTest("Campanha 1");
        Assert.NotNull(loaded);
        Assert.Equal(doc.TestId, loaded.TestId);
        Assert.Equal("Campanha 1", loaded.Name);
        Assert.Equal("Mapa A", loaded.LinkedMap?.MapName);
        Assert.Equal(2, loaded.Conditions.Count);
    }

    [Fact]
    public void RunFolder_RawData_And_Analysis_RoundTrip()
    {
        var doc = _store.CreateTest("Campanha 2", new KlaTestSettings(), NitrogenValve.Valve1);
        var cond = new KlaTestCondition { AgitationRpm = 400, AirflowLpm = 3.5, RequestedReplicates = 2 };
        doc.Conditions.Add(cond);
        _store.SaveConditionsTable(doc.FolderName, doc.Conditions);

        var run = new KlaTestRun
        {
            TestId = doc.TestId,
            ConditionId = cond.ConditionId,
            ReplicateNumber = 1,
            AgitationRpm = cond.AgitationRpm,
            AirflowLpm = cond.AirflowLpm,
            NitrogenValve = NitrogenValve.Valve1,
        };

        var runFolderName = _store.InitializeRunFolder(doc.FolderName, run);
        Assert.Equal("N0400_Q03p50_Rep01", runFolderName);

        var now = DateTimeOffset.UtcNow;
        var points = new List<KlaRawDataPoint>
        {
            new(now, 0.0, RunPhase.Deoxygenating, 85.0, 85.0, 0.0, 0.0, 300, true, false, true),
            new(now.AddSeconds(1), 1.0, RunPhase.Deoxygenating, 45.0, 45.0, 0.0, 0.0, 300, true, false, true),
            new(now.AddSeconds(2), 2.0, RunPhase.Reoxygenating, 5.0, 5.0, 3.5, 3.5, 400, false, false, false),
            new(now.AddSeconds(3), 3.0, RunPhase.Reoxygenating, 40.0, 40.0, 3.5, 3.5, 400, false, false, false),
        };

        _store.SaveRunRawData(doc.FolderName, runFolderName, points);
        var loadedPoints = _store.LoadRunRawData(doc.FolderName, runFolderName);

        Assert.Equal(4, loadedPoints.Count);
        Assert.Equal(85.0, loadedPoints[0].DORaw);
        Assert.Equal(40.0, loadedPoints[3].DORaw);
        Assert.True(loadedPoints[0].Valve1);
        Assert.False(loadedPoints[2].Valve1);

        var analysis = new KlaAnalysisRevision
        {
            RevisionNumber = 1,
            CeqPercent = 98.5,
            TStartSeconds = 2.0,
            TEndSeconds = 3.0,
            KlaPerHour = 45.8,
            AnalysisR2 = 0.998,
            Quality = DecisionQuality.Acceptable,
        };

        _store.SaveRunAnalysis(doc.FolderName, runFolderName, analysis);
        var loadedAnalysis = _store.LoadRunAnalysis(doc.FolderName, runFolderName);

        Assert.NotNull(loadedAnalysis);
        Assert.Equal(98.5, loadedAnalysis.CeqPercent);
        Assert.Equal(45.8, loadedAnalysis.KlaPerHour);
        Assert.Equal(DecisionQuality.Acceptable, loadedAnalysis.Quality);
    }

    [Fact]
    public void UpdateResultsSummary_Calculates_Mean_StdDev_Correctly()
    {
        var doc = _store.CreateTest("Campanha 3", new KlaTestSettings(), NitrogenValve.Valve1);
        var cond = new KlaTestCondition { AgitationRpm = 500, AirflowLpm = 5.0, RequestedReplicates = 3 };
        doc.Conditions.Add(cond);

        // Run 1: kLa = 50.0
        doc.Runs.Add(new KlaTestRunSummary
        {
            RunId = Guid.NewGuid(),
            ConditionId = cond.ConditionId,
            ReplicateNumber = 1,
            FolderName = "N0500_Q05p00_Rep01",
            AgitationRpm = 500,
            AirflowLpm = 5.0,
            Phase = RunPhase.Accepted,
            Decision = DecisionQuality.Acceptable,
            KlaPerHour = 50.0,
            AnalysisR2 = 0.99,
            StartedUtc = DateTimeOffset.UtcNow,
        });

        // Run 2: kLa = 54.0
        doc.Runs.Add(new KlaTestRunSummary
        {
            RunId = Guid.NewGuid(),
            ConditionId = cond.ConditionId,
            ReplicateNumber = 2,
            FolderName = "N0500_Q05p00_Rep02",
            AgitationRpm = 500,
            AirflowLpm = 5.0,
            Phase = RunPhase.Accepted,
            Decision = DecisionQuality.Acceptable,
            KlaPerHour = 54.0,
            AnalysisR2 = 0.98,
            StartedUtc = DateTimeOffset.UtcNow,
        });

        _store.UpdateResultsSummary(doc.FolderName, doc);

        var summaryPath = Path.Combine(_store.RootDirectory, doc.FolderName, KlaTestFileContracts.ResultsSummaryFileName);
        Assert.True(File.Exists(summaryPath));

        var lines = File.ReadAllLines(summaryPath);
        Assert.True(lines.Length >= 2);

        // Header: ConditionId,AgitationRpm,AirflowLpm,RequestedReplicates,CompletedReplicates,AcceptedReplicates,MeanKlaPerHour,StdDevKlaPerHour,MeanR2
        var row = lines[1].Split(',');
        Assert.Equal("500", row[1]);
        Assert.Equal("5.00", row[2]);
        Assert.Equal("3", row[3]);
        Assert.Equal("2", row[4]); // completed
        Assert.Equal("2", row[5]); // accepted
        Assert.Equal("52.00", row[6]); // mean = (50+54)/2 = 52.0
        Assert.Equal("2.83", row[7]); // sample std dev = sqrt(((50-52)^2 + (54-52)^2)/(2-1)) = sqrt(8) ~ 2.828 ~ 2.83
        Assert.Equal("0.9850", row[8]); // mean R2 = (0.99+0.98)/2 = 0.9850
    }

    [Fact]
    public void ImportTestFolder_CopiesCompleteRoutine_AndRebuildsMatrixFromRunFolders()
    {
        var sourceRoot = Path.Combine(_testRoot, "origem-externa");
        var sourceStore = new KlaTestStore(sourceRoot);
        var sourceDoc = sourceStore.CreateTest("Rotina Completa", new KlaTestSettings(), NitrogenValve.Valve2);
        var condition = new KlaTestCondition { AgitationRpm = 550, AirflowLpm = 4.25, RequestedReplicates = 1 };
        sourceDoc.Conditions.Add(condition);
        sourceStore.SaveConditionsTable(sourceDoc.FolderName, sourceDoc.Conditions);

        var run = new KlaTestRun
        {
            TestId = sourceDoc.TestId,
            ConditionId = condition.ConditionId,
            ReplicateNumber = 1,
            AgitationRpm = condition.AgitationRpm,
            AirflowLpm = condition.AirflowLpm,
        };
        var runFolder = sourceStore.InitializeRunFolder(sourceDoc.FolderName, run);
        sourceStore.SaveRunRawData(sourceDoc.FolderName, runFolder,
        [
            new(DateTimeOffset.UtcNow, 0, RunPhase.Reoxygenating, 10, 10, 4.25, 4.25, 550, false, false, false),
            new(DateTimeOffset.UtcNow.AddSeconds(2), 2, RunPhase.Reoxygenating, 20, 20, 4.25, 4.25, 550, false, false, false),
        ]);
        sourceStore.SaveRunAnalysis(sourceDoc.FolderName, runFolder, new KlaAnalysisRevision
        {
            KlaPerHour = 64.2,
            AnalysisR2 = 0.997,
            Quality = DecisionQuality.Acceptable,
        });

        var importedFolder = _store.ImportTestFolder(Path.Combine(sourceRoot, sourceDoc.FolderName));
        var imported = _store.LoadTest(importedFolder);

        Assert.NotNull(imported);
        Assert.Single(imported.Runs);
        Assert.Equal(runFolder, imported.Runs[0].FolderName);
        Assert.Equal(64.2, imported.Runs[0].KlaPerHour);
        Assert.Equal(1, imported.Conditions[0].AcceptedReplicates);
        Assert.Equal(ConditionStatus.Completed, imported.Conditions[0].Status);
        Assert.True(File.Exists(_store.GetRunRawDataPath(importedFolder, runFolder)));
    }
}
