using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Dialogs;
using OpenTECHub.Services.KlaMapping;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Platform;
using OpenTECHub.Services.Telemetry;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class KlaDeterminationViewModelTests : IDisposable
{
    private readonly string _testRoot;
    private readonly KlaTestStore _store;
    private readonly KlaAnalysisEngine _analysisEngine;
    private readonly FakeTestRunner _runner;
    private readonly FakeKlaProfileStore _profileStore;
    private readonly FakeDialogService _dialogs;
    private readonly FakeEventJournal _journal;
    private readonly FakeSettingsService _settings;
    private readonly KlaDeterminationViewModel _vm;

    public KlaDeterminationViewModelTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "KlaVmTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);

        _store = new KlaTestStore(_testRoot);
        _analysisEngine = new KlaAnalysisEngine();
        _runner = new FakeTestRunner();
        _profileStore = new FakeKlaProfileStore();
        _dialogs = new FakeDialogService();
        _journal = new FakeEventJournal();
        _settings = new FakeSettingsService();

        _vm = new KlaDeterminationViewModel(
            _runner,
            _store,
            _analysisEngine,
            _profileStore,
            _dialogs,
            new FakeFileInteractionService(),
            _settings);
    }

    public void Dispose()
    {
        _vm.Dispose();
        if (Directory.Exists(_testRoot))
        {
            try
            {
                Directory.Delete(_testRoot, true);
            }
            catch
            {
                // Best effort
            }
        }
    }

    [Fact]
    public void CreateNewTest_Adds_Test_And_Initializes_Conditions()
    {
        _vm.NewTestName = "Ensaio Aeracao 2026";
        _vm.SettingDOMin = 15.0;
        _vm.SettingDOMax = 80.0;
        _vm.SelectedN2Valve = NitrogenValve.Valve1;

        _vm.CreateNewTest();

        Assert.NotNull(_vm.CurrentTest);
        Assert.Equal("Ensaio Aeracao 2026", _vm.CurrentTest.Name);
        Assert.True(_vm.HasActiveTest);
        Assert.Equal(15.0, _vm.SettingDOMin);
        Assert.Equal(80.0, _vm.SettingDOMax);

        // Add a condition
        _vm.NewConditionRpm = 450;
        _vm.NewConditionFlow = 3.5;
        _vm.NewConditionReplicates = 3;
        _vm.AddManualCondition();

        Assert.Single(_vm.Conditions);
        Assert.Equal(450, _vm.Conditions[0].AgitationRpm);
        Assert.Equal(3.5, _vm.Conditions[0].AirflowLpm);
        Assert.Equal("0/3", _vm.Conditions[0].DisplayReplicates);
    }

    /// <summary>
    /// The vent line is bench wiring, so it travels with the test manifest rather than with the
    /// application settings, and a shared output is refused before it can be saved.
    /// </summary>
    [Fact]
    public void Vent_Configuration_Persists_With_The_Test_And_Refuses_A_Shared_Valve()
    {
        _vm.NewTestName = "Ensaio Alivio VM";
        _vm.SelectedN2Valve = NitrogenValve.Valve1;
        _vm.SelectedVentValve = NitrogenValve.Valve2;
        _vm.UseVentStabilization = true;
        _vm.SettingVentFlowTolerance = 0.25;
        _vm.SettingVentFlowStableSamples = 4;
        _vm.SettingMaxVentStabilizationSeconds = 90;

        _vm.CreateNewTest();

        Assert.NotNull(_vm.CurrentTest);
        Assert.False(_vm.HasVentValveConflict);

        var reloaded = _store.LoadTest(_vm.CurrentTest.FolderName);
        Assert.NotNull(reloaded);
        Assert.Equal(NitrogenValve.Valve2, reloaded.SelectedVentValve);
        Assert.True(reloaded.Settings.VentStabilizationEnabled);
        Assert.Equal(0.25, reloaded.Settings.VentFlowToleranceLpm);
        Assert.Equal(4, reloaded.Settings.VentFlowStableSamples);
        Assert.Equal(90, reloaded.Settings.MaxVentStabilizationSeconds);

        // Pointing the vent at the nitrogen output is reported and blocks the save.
        _vm.SelectedVentValve = NitrogenValve.Valve1;
        Assert.True(_vm.HasVentValveConflict);

        _vm.OpenAdvancedSettingsDialog();
        _vm.SaveAdvancedSettings();
        Assert.Contains("alívio", _vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(_vm.IsAdvancedSettingsDialogOpen);
    }

    /// <summary>
    /// A test created without the vent line keeps the detour off, so the standard rig is
    /// unaffected by the option existing.
    /// </summary>
    [Fact]
    public void Vent_Stabilization_Is_Off_By_Default()
    {
        _vm.NewTestName = "Ensaio Sem Alivio";
        _vm.CreateNewTest();

        Assert.NotNull(_vm.CurrentTest);
        Assert.False(_vm.UseVentStabilization);
        Assert.False(_vm.CurrentTest.Settings.VentStabilizationEnabled);
    }

    [Fact]
    public void RecomputeReviewAnalysis_Calculates_Kla_And_Quality_Metrics()
    {
        _vm.NewTestName = "Ensaio Analise";
        _vm.CreateNewTest();

        // Simulate synthetic reoxygenation data in LivePoints (kla = 36 /h = 0.01 /s)
        var trueKlaSec = 0.01;
        var ceq = 100.0;
        _vm.LivePoints.Clear();

        for (var t = 0.0; t <= 120.0; t += 2.0)
        {
            var doVal = ceq * (1.0 - Math.Exp(-trueKlaSec * t));
            _vm.LivePoints.Add(new KlaRawDataPoint(
                TimestampUtc: DateTimeOffset.UtcNow.AddSeconds(t),
                RelativeSeconds: t,
                Phase: RunPhase.Reoxygenating,
                DORaw: doVal,
                DOFiltered: doVal,
                FlowMeasured: 4.0,
                FlowSetpoint: 4.0,
                AgitationSetpoint: 500,
                Valve1: false,
                Valve2: false,
                VFlow: true));
        }

        _vm.ReviewCeq = 100.0;
        _vm.ReviewCeqIsManual = true;
        _vm.ReviewTStart = 10.0;
        _vm.ReviewTEnd = 100.0;

        _vm.RecomputeReviewAnalysis();

        Assert.NotNull(_vm.CurrentAnalysis);
        Assert.InRange(_vm.ReviewKla, 35.0, 37.0); // kla ~ 36 /h
        Assert.True(_vm.ReviewR2 > 0.999);
        Assert.Equal(DecisionQuality.Acceptable, _vm.ReviewQuality);
        Assert.NotEmpty(_vm.LogLinearSeries);
        Assert.NotEmpty(_vm.InstantaneousKlaSeries);
    }

    [Fact]
    public void KlaMappingViewModel_Imports_Accepted_Replicates_From_Test()
    {
        // 1. Create test and add an accepted run
        var doc = _store.CreateTest("Ensaio Integracao", new KlaTestSettings(), NitrogenValve.Valve1);
        var cond = new KlaTestCondition
        {
            ConditionId = Guid.NewGuid(),
            AgitationRpm = 500,
            AirflowLpm = 4.0,
            RequestedReplicates = 2,
        };
        doc.Conditions.Add(cond);
        var run1 = new KlaTestRun { RunId = Guid.NewGuid(), TestId = doc.TestId, ConditionId = cond.ConditionId, ReplicateNumber = 1, AgitationRpm = 500, AirflowLpm = 4.0 };
        run1.FolderName = _store.InitializeRunFolder(doc.FolderName, run1);
        _store.SaveRunAnalysis(doc.FolderName, run1.FolderName, new KlaAnalysisRevision { RevisionNumber = 1, KlaPerHour = 48, AnalysisR2 = 0.995, Quality = DecisionQuality.Acceptable });
        doc.Runs.Add(new KlaTestRunSummary
        {
            RunId = run1.RunId,
            ConditionId = cond.ConditionId,
            ReplicateNumber = 1,
            AgitationRpm = 500,
            AirflowLpm = 4.0,
            Phase = RunPhase.Accepted,
            Decision = DecisionQuality.Acceptable,
            KlaPerHour = 48.0,
            AnalysisR2 = 0.995,
            FolderName = run1.FolderName,
            StartedUtc = DateTimeOffset.UtcNow,
        });
        var run2 = new KlaTestRun { RunId = Guid.NewGuid(), TestId = doc.TestId, ConditionId = cond.ConditionId, ReplicateNumber = 2, AgitationRpm = 500, AirflowLpm = 4.0 };
        run2.FolderName = _store.InitializeRunFolder(doc.FolderName, run2);
        _store.SaveRunAnalysis(doc.FolderName, run2.FolderName, new KlaAnalysisRevision { RevisionNumber = 1, KlaPerHour = 52, AnalysisR2 = 0.996, Quality = DecisionQuality.Acceptable });
        doc.Runs.Add(new KlaTestRunSummary
        {
            RunId = run2.RunId,
            ConditionId = cond.ConditionId,
            ReplicateNumber = 2,
            AgitationRpm = 500,
            AirflowLpm = 4.0,
            Phase = RunPhase.Accepted,
            Decision = DecisionQuality.Acceptable,
            KlaPerHour = 52.0,
            AnalysisR2 = 0.996,
            FolderName = run2.FolderName,
            StartedUtc = DateTimeOffset.UtcNow,
        });
        _store.SaveTestManifest(doc);

        // 2. Open mapping VM and import
        var mappingEngine = new KlaMappingEngine();
        var mappingVm = new KlaMappingViewModel(
            mappingEngine,
            _profileStore,
            new FakeFileInteractionService(),
            _dialogs,
            _journal,
            _store);

        mappingVm.OpenImportFromTestDialog();
        Assert.Single(mappingVm.AvailableKlaTests);
        mappingVm.SelectedKlaTestForImport = mappingVm.AvailableKlaTests[0];

        mappingVm.ImportSelectedKlaTest();

        Assert.Single(mappingVm.Anchors);
        Assert.Equal(500, mappingVm.Anchors[0].AgitationValue);
        Assert.Equal(4.0, mappingVm.Anchors[0].AirflowValue);
        Assert.Equal(50.0, mappingVm.Anchors[0].KlaValue); // Mean of 48 and 52
    }

    [Fact]
    public void RemoveCondition_RemovesCondition_AndReindexes()
    {
        _vm.NewTestName = "Ensaio Remocao";
        _vm.CreateNewTest();

        _vm.NewConditionRpm = 300;
        _vm.NewConditionFlow = 2.0;
        _vm.AddManualCondition();

        _vm.NewConditionRpm = 600;
        _vm.NewConditionFlow = 4.0;
        _vm.AddManualCondition();

        Assert.Equal(2, _vm.Conditions.Count);
        Assert.Equal(1, _vm.Conditions[0].OrderIndex);
        Assert.Equal(2, _vm.Conditions[1].OrderIndex);

        // Remove the first condition
        _vm.RemoveCondition(_vm.Conditions[0]);

        Assert.Single(_vm.Conditions);
        Assert.Equal(600, _vm.Conditions[0].AgitationRpm);
        Assert.Equal(1, _vm.Conditions[0].OrderIndex);
    }

    [Fact]
    public async Task StartSequence_ExecutesFirstPendingCondition()
    {
        _vm.NewTestName = "Ensaio Sequencia";
        _vm.CreateNewTest();

        _vm.NewConditionRpm = 300;
        _vm.NewConditionFlow = 2.0;
        _vm.AddManualCondition();

        _vm.NewConditionRpm = 600;
        _vm.NewConditionFlow = 4.0;
        _vm.AddManualCondition();

        Assert.True(_vm.HasActiveTest);
        Assert.False(_vm.IsRunning);

        await _vm.StartSequenceAsync();

        Assert.NotNull(_runner.CurrentCondition);
        Assert.Equal(300, _runner.CurrentCondition.AgitationRpm);
        Assert.Equal(2.0, _runner.CurrentCondition.AirflowLpm);
    }

    [Fact]
    public async Task AcceptCurrentRun_AutomaticallyStartsNextConditionInSequence()
    {
        _vm.NewTestName = "Ensaio Avanco Automatico";
        _vm.CreateNewTest();

        _vm.NewConditionRpm = 300;
        _vm.NewConditionFlow = 2.0;
        _vm.NewConditionReplicates = 1;
        _vm.AddManualCondition();

        _vm.NewConditionRpm = 600;
        _vm.NewConditionFlow = 4.0;
        _vm.NewConditionReplicates = 1;
        _vm.AddManualCondition();

        await _vm.StartSequenceAsync();

        Assert.Equal(300, _runner.CurrentCondition!.AgitationRpm);

        // Prepare valid review analysis for current run
        var trueKlaSec = 0.01;
        var ceq = 100.0;
        _vm.LivePoints.Clear();
        for (var t = 0.0; t <= 120.0; t += 2.0)
        {
            var doVal = ceq * (1.0 - Math.Exp(-trueKlaSec * t));
            _vm.LivePoints.Add(new KlaRawDataPoint(
                TimestampUtc: DateTimeOffset.UtcNow.AddSeconds(t),
                RelativeSeconds: t,
                Phase: RunPhase.Reoxygenating,
                DORaw: doVal,
                DOFiltered: doVal,
                FlowMeasured: 2.0,
                FlowSetpoint: 2.0,
                AgitationSetpoint: 300,
                Valve1: false,
                Valve2: false,
                VFlow: true));
        }
        _vm.ReviewCeq = 100.0;
        _vm.ReviewCeqIsManual = true;
        _vm.ReviewTStart = 10.0;
        _vm.ReviewTEnd = 100.0;
        _vm.RecomputeReviewAnalysis();

        // Operator accepts run
        await _vm.AcceptCurrentRunAsync();

        // Runner should now have automatically advanced to condition 2 (600 rpm, 4.0 L/min)
        Assert.NotNull(_runner.CurrentCondition);
        Assert.Equal(600, _runner.CurrentCondition.AgitationRpm);
        Assert.Equal(4.0, _runner.CurrentCondition.AirflowLpm);
    }

    [Fact]
    public void DisplayStatus_ReturnsPortugueseText()
    {
        var cond1 = new KlaTestCondition { Status = ConditionStatus.Pending };
        var cond2 = new KlaTestCondition { Status = ConditionStatus.InProgress };
        var cond3 = new KlaTestCondition { Status = ConditionStatus.Completed };
        var cond4 = new KlaTestCondition { Status = ConditionStatus.Skipped };

        Assert.Equal("Pendente", new KlaConditionRowViewModel(cond1).DisplayStatus);
        Assert.Equal("Em Execução", new KlaConditionRowViewModel(cond2).DisplayStatus);
        Assert.Equal("Concluído", new KlaConditionRowViewModel(cond3).DisplayStatus);
        Assert.Equal("Ignorado", new KlaConditionRowViewModel(cond4).DisplayStatus);
    }

    [Fact]
    public async Task CompleteTest_ResolvesInProgressConditionToCompletedOrPending()
    {
        _vm.NewTestName = "Ensaio Conclusao Status";
        _vm.CreateNewTest();

        _vm.NewConditionRpm = 400;
        _vm.NewConditionFlow = 2.0;
        _vm.NewConditionReplicates = 2;
        _vm.AddManualCondition();

        // Mark condition as InProgress
        _vm.Conditions[0].Model.Status = ConditionStatus.InProgress;
        _vm.Conditions[0].Model.AcceptedReplicates = 1;

        await _vm.CompleteTestAsync();

        Assert.Equal(ConditionStatus.Completed, _vm.Conditions[0].Status);
        Assert.Equal("Concluído", _vm.Conditions[0].DisplayStatus);
    }

    [Fact]
    public async Task CanStartSequence_DisabledWhileRunning()
    {
        _vm.NewTestName = "Ensaio Sequence Enablement";
        _vm.CreateNewTest();

        _vm.NewConditionRpm = 400;
        _vm.NewConditionFlow = 2.0;
        _vm.AddManualCondition();

        Assert.True(_vm.CanStartSequence);

        await _vm.StartSequenceAsync();

        Assert.False(_vm.CanStartSequence);
    }

    [Fact]
    public void AdvancedSettingsDialog_OpenCloseAndSave_UpdatesRunner()
    {
        _vm.NewTestName = "Ensaio Adv Settings";
        _vm.CreateNewTest();

        Assert.False(_vm.IsAdvancedSettingsDialogOpen);

        _vm.OpenAdvancedSettingsDialog();
        Assert.True(_vm.IsAdvancedSettingsDialogOpen);

        _vm.SettingSmoothingWindow = 9;
        _vm.SettingMaxDegassingMinutes = 45;
        _vm.SettingMaxReoxygenationMinutes = 75;
        _vm.SettingPostNitrogenMinimumDelaySeconds = 8;
        _vm.SettingStabilityDerivativeSpanSeconds = 10;
        _vm.SettingStabilityDerivativeThreshold = 0.03;
        _vm.SettingStabilityRequiredSamples = 7;
        _vm.SettingMaxPostNitrogenStabilizationSeconds = 180;
        _vm.SettingDefaultCeq = 102;

        _vm.SaveAdvancedSettings();
        Assert.False(_vm.IsAdvancedSettingsDialogOpen);

        Assert.NotNull(_vm.CurrentTest);
        Assert.Equal(9, _vm.CurrentTest.Settings.SmoothingWindowSize);
        Assert.Equal(45, _vm.CurrentTest.Settings.MaxDegassingTimeMinutes);
        Assert.Equal(75, _vm.CurrentTest.Settings.MaxReoxygenationTimeMinutes);
        Assert.Equal(8, _vm.CurrentTest.Settings.PostNitrogenMinimumDelaySeconds);
        Assert.Equal(10, _vm.CurrentTest.Settings.StabilityDerivativeSpanSeconds);
        Assert.Equal(0.03, _vm.CurrentTest.Settings.StabilityDerivativeThresholdPercentPerSecond);
        Assert.Equal(7, _vm.CurrentTest.Settings.StabilityRequiredSamples);
        Assert.Equal(180, _vm.CurrentTest.Settings.MaxPostNitrogenStabilizationSeconds);
        Assert.Equal(102, _vm.CurrentTest.Settings.DefaultCeqPercent);
    }

    [Fact]
    public void AdvancedSettingsDialog_CanConfigureBeforeCreatingTest()
    {
        // No active test yet
        Assert.False(_vm.HasActiveTest);

        _vm.OpenAdvancedSettingsDialog();
        Assert.True(_vm.IsAdvancedSettingsDialogOpen);

        _vm.SettingDOMin = 18.0;
        _vm.SettingDOMax = 82.0;
        _vm.SettingSmoothingWindow = 7;
        _vm.SaveAdvancedSettings();
        Assert.False(_vm.IsAdvancedSettingsDialogOpen);

        // Now create a test
        _vm.NewTestName = "Ensaio Preconfig";
        _vm.CreateNewTest();

        Assert.NotNull(_vm.CurrentTest);
        Assert.Equal(18.0, _vm.CurrentTest.Settings.DOMinPercent);
        Assert.Equal(82.0, _vm.CurrentTest.Settings.DOMaxPercent);
        Assert.Equal(7, _vm.CurrentTest.Settings.SmoothingWindowSize);
    }

    [Fact]
    public async Task AutoAcceptRuns_AutomaticallyCalculatesAndAdvancesRun()
    {
        _vm.NewTestName = "Ensaio Auto Accept";
        _vm.AutoAcceptRuns = true;
        _vm.SettingAutoLinearStartPercent = 45.0;
        _vm.SettingAutoLinearEndPercent = 70.0;
        _vm.CreateNewTest();

        _vm.NewConditionRpm = 400;
        _vm.NewConditionFlow = 2.0;
        _vm.NewConditionReplicates = 1;
        _vm.AddManualCondition();

        _vm.NewConditionRpm = 600;
        _vm.NewConditionFlow = 4.0;
        _vm.NewConditionReplicates = 1;
        _vm.AddManualCondition();

        await _vm.StartSequenceAsync();

        // Simulate reoxygenation data curve
        _vm.LivePoints.Clear();
        for (var t = 0.0; t <= 120.0; t += 2.0)
        {
            var doVal = 100.0 * (1.0 - Math.Exp(-0.02 * t));
            _vm.LivePoints.Add(new KlaRawDataPoint(
                TimestampUtc: DateTimeOffset.UtcNow.AddSeconds(t),
                RelativeSeconds: t,
                Phase: RunPhase.Reoxygenating,
                DORaw: doVal,
                DOFiltered: doVal,
                FlowMeasured: 2.0,
                FlowSetpoint: 2.0,
                AgitationSetpoint: 400,
                Valve1: false,
                Valve2: false,
                VFlow: true));
        }

        // Trigger review phase with AutoAccept active
        _runner.Phase = RunPhase.Reviewing;
        _runner.RaiseStateChanged();

        // Wait adaptively for async auto-accept delay and runner advancement
        for (var i = 0; i < 40 && _runner.CurrentCondition?.AgitationRpm != 600; i++)
        {
            await Task.Delay(50);
        }

        // Runner should have auto-accepted condition 1 and advanced to condition 2
        Assert.NotNull(_runner.CurrentCondition);
        Assert.Equal(600, _runner.CurrentCondition.AgitationRpm);
        Assert.Equal(4.0, _runner.CurrentCondition.AirflowLpm);
    }

    /// <summary>§E: the diagnostic series are recomputed every few reoxygenation points, not on every frame.</summary>
    [Fact]
    public void Live_derived_series_are_recomputed_every_few_points_not_every_frame() => OnUiThread(() =>
    {
        _vm.NewTestName = "Ensaio Derivadas";
        _vm.CreateNewTest();
        var resets = 0;
        _vm.InstantaneousKlaSeries.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                resets++;
            }
        };

        for (var i = 0; i < 3; i++)
        {
            _runner.RaiseDataPoint(new KlaRawDataPoint(DateTimeOffset.UtcNow, i, RunPhase.Deoxygenating, 5, 5, 0, 0, 300, false, true, false));
        }
        for (var i = 0; i < 20; i++)
        {
            var t = 10 + i * 5.0;
            var value = 100 * (1 - Math.Exp(-0.01 * t));
            _runner.RaiseDataPoint(new KlaRawDataPoint(DateTimeOffset.UtcNow, t, RunPhase.Reoxygenating, value, value, 4, 4, 300, false, false, true));
        }

        Assert.Equal(23, _vm.LivePoints.Count);
        Assert.NotEmpty(_vm.InstantaneousKlaSeries);
        Assert.NotEmpty(_vm.LogLinearSeries);
        // 20 reoxygenation points: recomputed at 5, 10, 15 and 20 — not 16 times.
        Assert.Equal(4, resets);

        // A cleared chart starts over: the next run's first points do not see the old slice.
        _vm.LivePoints.Clear();
        resets = 0;
        for (var i = 0; i < 4; i++)
        {
            _runner.RaiseDataPoint(new KlaRawDataPoint(DateTimeOffset.UtcNow, i, RunPhase.Reoxygenating, 10 + i, 10 + i, 4, 4, 300, false, false, true));
        }
        Assert.Equal(0, resets);
        _runner.RaiseDataPoint(new KlaRawDataPoint(DateTimeOffset.UtcNow, 5, RunPhase.Reoxygenating, 15, 15, 4, 4, 300, false, false, true));
        Assert.Equal(1, resets);
    });

    /// <summary>
    /// The ViewModel marshals runner events through <c>Application.Current.Dispatcher</c> when one
    /// exists — which it does once another test has started the WPF host. Running the body on that
    /// dispatcher keeps the delivery synchronous either way.
    /// </summary>
    private static void OnUiThread(Action body)
    {
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(body);
        }
        else
        {
            body();
        }
    }

    /// <summary>§E: refreshing the matrix keeps the row objects (no Reset) and does not re-read analyses it already has.</summary>
    [Fact]
    public void RefreshConditionsList_updates_rows_in_place()
    {
        _vm.NewTestName = "Ensaio Matriz In Place";
        _vm.CreateNewTest();
        _vm.NewConditionRpm = 350;
        _vm.NewConditionFlow = 1.5;
        _vm.NewConditionReplicates = 2;
        _vm.AddManualCondition();
        _vm.NewConditionRpm = 500;
        _vm.NewConditionFlow = 3.0;
        _vm.NewConditionReplicates = 1;
        _vm.AddManualCondition();
        var rows = _vm.MatrixRows.ToArray();
        var events = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        _vm.MatrixRows.CollectionChanged += (_, e) => events.Add(e.Action);

        _vm.RefreshConditionsList();
        _vm.RefreshConditionsList();

        Assert.Empty(events);
        Assert.Equal(rows, _vm.MatrixRows.ToArray());
        Assert.Equal([1, 2, 3], _vm.MatrixRows.Select(r => r.OrderIndex));
    }

    [Fact]
    public void MatrixRows_DisposesReplicatesAsIndividualRows()
    {
        _vm.NewTestName = "Ensaio Matrix Rows";
        _vm.CreateNewTest();

        _vm.NewConditionRpm = 350;
        _vm.NewConditionFlow = 1.5;
        _vm.NewConditionReplicates = 2;
        _vm.AddManualCondition();

        _vm.NewConditionRpm = 500;
        _vm.NewConditionFlow = 3.0;
        _vm.NewConditionReplicates = 1;
        _vm.AddManualCondition();

        Assert.Equal(3, _vm.MatrixRows.Count);
        Assert.Equal("R1", _vm.MatrixRows[0].ReplicateLabel);
        Assert.Equal(350, _vm.MatrixRows[0].AgitationRpm);
        Assert.Equal("R2", _vm.MatrixRows[1].ReplicateLabel);
        Assert.Equal(350, _vm.MatrixRows[1].AgitationRpm);
        Assert.Equal("R1", _vm.MatrixRows[2].ReplicateLabel);
        Assert.Equal(500, _vm.MatrixRows[2].AgitationRpm);

        _vm.RemoveMatrixRow(_vm.MatrixRows[0]);
        Assert.Single(_vm.MatrixRows);
        Assert.Equal(500, _vm.MatrixRows[0].AgitationRpm);
    }

    [Fact]
    public async Task LoadMatrixRow_AndSaveRevision_UpdatesTestAndMatrix()
    {
        _vm.NewTestName = "Ensaio Edit Run";
        _vm.CreateNewTest();

        _vm.NewConditionRpm = 450;
        _vm.NewConditionFlow = 2.5;
        _vm.NewConditionReplicates = 1;
        _vm.AddManualCondition();

        var cond = _vm.Conditions[0].Model;
        Assert.NotNull(_vm.CurrentTest);
        var run = new KlaTestRunSummary
        {
            RunId = Guid.NewGuid(),
            ConditionId = cond.ConditionId,
            ReplicateNumber = 1,
            AgitationRpm = 450,
            AirflowLpm = 2.5,
            FolderName = "Run_001_N450_Q2.50_R01",
            Phase = RunPhase.Accepted,
            KlaPerHour = 108.0,
            AnalysisR2 = 0.998,
            StartedUtc = DateTimeOffset.UtcNow,
        };
        _vm.CurrentTest.Runs.Add(run);

        // Store raw points
        var points = new List<KlaRawDataPoint>();
        for (var t = 0.0; t <= 60.0; t += 2.0)
        {
            var doVal = 10.0 + 80.0 * (1.0 - Math.Exp(-0.03 * t));
            points.Add(new KlaRawDataPoint(
                TimestampUtc: DateTimeOffset.UtcNow.AddSeconds(t),
                RelativeSeconds: t,
                Phase: RunPhase.Reoxygenating,
                DORaw: doVal,
                DOFiltered: doVal,
                FlowMeasured: 2.5,
                FlowSetpoint: 2.5,
                AgitationSetpoint: 450,
                Valve1: false,
                Valve2: false,
                VFlow: true));
        }
        _store.SaveRunRawData(_vm.CurrentTest.FolderName, run.FolderName, points);

        var initialAnalysis = new KlaAnalysisRevision
        {
            RevisionNumber = 1,
            KlaPerHour = 108.0,
            AnalysisR2 = 0.998,
            TStartSeconds = 10.0,
            TEndSeconds = 40.0,
            CeqPercent = 90.0,
            Quality = DecisionQuality.Acceptable,
        };
        _store.SaveRunAnalysis(_vm.CurrentTest.FolderName, run.FolderName, initialAnalysis);

        _vm.RefreshConditionsList();

        Assert.Single(_vm.MatrixRows);
        var row = _vm.MatrixRows[0];
        Assert.Equal("108.0", row.DisplayKla);
        Assert.Equal("0.9980", row.DisplayR2);

        // Load row for review
        _vm.LoadMatrixRow(row);
        Assert.True(_vm.IsReviewOpen);
        Assert.NotEmpty(_vm.LivePoints);
        Assert.Equal(108.0, _vm.ReviewKla);

        // Recompute with modified region
        _vm.ReviewTStart = 15.0;
        _vm.ReviewTEnd = 35.0;
        _vm.RecomputeReviewAnalysis();

        // Save modification
        await _vm.AcceptCurrentRunAsync();

        var loadedAnalysis = _store.LoadRunAnalysis(_vm.CurrentTest.FolderName, run.FolderName);
        Assert.NotNull(loadedAnalysis);
        Assert.Equal(2, loadedAnalysis.RevisionNumber);
        Assert.Equal(15.0, loadedAnalysis.TStartSeconds);
        Assert.Equal(35.0, loadedAnalysis.TEndSeconds);
    }

    [Fact]
    public async Task StartSequenceDialog_ModesPreviewAndConfirm_ExecutesSelectedScope()
    {
        _vm.NewTestName = "Ensaio Sequence Modes";
        _vm.CreateNewTest();

        _vm.NewConditionRpm = 300;
        _vm.NewConditionFlow = 2.0;
        _vm.AddManualCondition();

        _vm.NewConditionRpm = 450;
        _vm.NewConditionFlow = 3.0;
        _vm.AddManualCondition();

        _vm.NewConditionRpm = 600;
        _vm.NewConditionFlow = 4.0;
        _vm.AddManualCondition();

        // Mark condition 1 as already completed
        _vm.Conditions[0].Model.AcceptedReplicates = 1;
        _vm.Conditions[0].Model.Status = ConditionStatus.Completed;

        // Open Dialog
        _vm.OpenStartSequenceDialog();
        Assert.True(_vm.IsStartSequenceDialogOpen);
        Assert.True(_vm.IsSequenceModePending);
        Assert.Equal(2, _vm.SequencePreviewQueue.Count);

        // Select row #2 and switch to FromSelected
        _vm.SelectedMatrixRow = _vm.MatrixRows[1];
        _vm.IsSequenceModeFromSelected = true;
        Assert.Equal(2, _vm.SequencePreviewQueue.Count);
        Assert.Equal(450, _vm.SequencePreviewQueue[0].AgitationRpm);

        // Switch to All
        _vm.IsSequenceModeAll = true;
        Assert.Equal(3, _vm.SequencePreviewQueue.Count);
        Assert.Equal(300, _vm.SequencePreviewQueue[0].AgitationRpm);

        // Confirm
        await _vm.ConfirmStartSequenceAsync();
        Assert.False(_vm.IsStartSequenceDialogOpen);
        Assert.NotNull(_runner.CurrentCondition);
        Assert.Equal(300, _runner.CurrentCondition.AgitationRpm);
    }

    private sealed class FakeTestRunner : IKlaTestRunner
    {
        public KlaTestDocument? CurrentTest { get; private set; }
        public KlaTestCondition? CurrentCondition { get; private set; }
        public KlaTestRun? CurrentRun { get; private set; }
        public RunPhase Phase { get; set; } = RunPhase.Idle;
        public bool IsRunning => Phase is RunPhase.Deoxygenating or RunPhase.Reoxygenating or RunPhase.Preflight;
        public bool IsInReview => Phase == RunPhase.Reviewing;
        public double PhaseElapsedSeconds { get; set; }
        public double TotalElapsedSeconds { get; set; }
        public double CurrentDO { get; set; }
        public double CurrentDORaw { get; set; }
        public double CurrentFlowMeasured { get; set; }
        public double? CurrentDODerivative { get; set; }
        public int StabilityConfirmationCount { get; set; }
        public int VentFlowStableCount { get; set; }
        public double? VentFlowDeviation { get; set; }
        public string StatusMessage { get; set; } = "Pronto";

        public IReadOnlyList<KlaRawDataPoint> CurrentRunPoints => [];
        public IReadOnlyList<KlaGlobalSeriesSample> GlobalSeriesSamples => [];

#pragma warning disable CS0067
        public event Action? StateChanged;
        public event Action<KlaRawDataPoint>? DataPointAdded;
        public event Action<string>? Logged;
#pragma warning restore CS0067

        public void RaiseStateChanged() => StateChanged?.Invoke();
        public void RaiseDataPoint(KlaRawDataPoint point) => DataPointAdded?.Invoke(point);

        public void PrepareTest(KlaTestDocument test)
        {
            CurrentTest = test;
            CurrentCondition = null;
            CurrentRun = null;
            Phase = RunPhase.Idle;
            StateChanged?.Invoke();
        }

        public Task StartTestAsync(KlaTestDocument test, CancellationToken cancellationToken = default)
        {
            CurrentTest = test;
            Phase = RunPhase.Idle;
            StateChanged?.Invoke();
            return Task.CompletedTask;
        }

        public Task StartRunAsync(KlaTestCondition condition, int replicateNumber, CancellationToken cancellationToken = default)
        {
            CurrentCondition = condition;
            Phase = RunPhase.Deoxygenating;
            StateChanged?.Invoke();
            return Task.CompletedTask;
        }

        public Task StopRunAndReviewAsync(string reason = "Parada pelo operador")
        {
            Phase = RunPhase.Reviewing;
            StateChanged?.Invoke();
            return Task.CompletedTask;
        }

        public Task AcceptRunAsync(KlaAnalysisRevision revision)
        {
            Phase = RunPhase.Accepted;
            if (CurrentCondition is not null)
            {
                CurrentCondition.CompletedReplicates++;
                CurrentCondition.AcceptedReplicates++;
                if (CurrentCondition.AcceptedReplicates >= CurrentCondition.RequestedReplicates)
                {
                    CurrentCondition.Status = ConditionStatus.Completed;
                }
            }
            StateChanged?.Invoke();
            return Task.CompletedTask;
        }

        public Task RejectRunAsync(string reason)
        {
            Phase = RunPhase.Rejected;
            StateChanged?.Invoke();
            return Task.CompletedTask;
        }

        public Task RepeatRunAsync()
        {
            Phase = RunPhase.Deoxygenating;
            StateChanged?.Invoke();
            return Task.CompletedTask;
        }

        public Task AbortTestAsync(string reason)
        {
            Phase = RunPhase.Aborting;
            StateChanged?.Invoke();
            return Task.CompletedTask;
        }

        public Task CompleteTestAsync()
        {
            Phase = RunPhase.Completed;
            StateChanged?.Invoke();
            return Task.CompletedTask;
        }

        public void UpdateLiveSettings(KlaTestSettings newSettings)
        {
        }

        public void SetDegassingAgitation(double rpm)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeDialogService : IDialogService
    {
        public bool ConfirmDestructive(string title, string consequence, string exactCommand) => true;
        public bool Confirm(string title, string message, string confirmText = "Confirmar", string cancelText = "Cancelar", bool isDanger = false) => true;
        public bool PromptInput(string title, string message, out string response, string initialValue = "")
        {
            response = initialValue;
            return true;
        }
        public RecipeStartOption PromptRecipeStart(string recipeName) => RecipeStartOption.StartPreserving;
    }

    private sealed class FakeEventJournal : IEventJournal
    {
#pragma warning disable CS0067
        public event Action<AuditEvent>? EntryAdded;
#pragma warning restore CS0067
        public IReadOnlyList<AuditEvent> Snapshot() => [];
        public void Add(AuditSource source, AuditSeverity severity, string message, string? detail = null) { }
        public void Dispose() { }
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Current { get; private set; } = new();
        public event Action<AppSettings>? Changed;
        public void Update(Func<AppSettings, AppSettings> mutate)
        {
            Current = mutate(Current);
            Changed?.Invoke(Current);
        }
        public void Reload() { }
        public Task SaveNowAsync() => Task.CompletedTask;
    }

    private sealed class FakeFileInteractionService : IFileInteractionService
    {
        public string? ChooseSavePath(string title, string suggestedName, string filter, string extension) => null;
        public string? ChooseOpenPath(string title, string filter, string extension) => null;
        public string? ChooseFolder(string title, string? initialDirectory = null) => null;
        public void OpenFolder(string path) { }
        public void CopyText(string text) { }
    }
}
