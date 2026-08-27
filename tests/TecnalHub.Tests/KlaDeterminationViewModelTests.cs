using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Dialogs;
using TecnalHub.Services.KlaMapping;
using TecnalHub.Services.KlaTesting;
using TecnalHub.Services.Persistence;
using TecnalHub.Services.Platform;
using TecnalHub.Services.Telemetry;
using TecnalHub.ViewModels;
using Xunit;

namespace TecnalHub.Tests;

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
            new FakeFileInteractionService());
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
        public string StatusMessage { get; set; } = "Pronto";

        public IReadOnlyList<KlaRawDataPoint> CurrentRunPoints => [];
        public IReadOnlyList<KlaGlobalSeriesSample> GlobalSeriesSamples => [];

#pragma warning disable CS0067
        public event Action? StateChanged;
        public event Action<KlaRawDataPoint>? DataPointAdded;
        public event Action<string>? Logged;
#pragma warning restore CS0067

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
