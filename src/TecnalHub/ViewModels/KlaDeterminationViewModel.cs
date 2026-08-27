using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Dialogs;
using TecnalHub.Services.KlaMapping;
using TecnalHub.Services.KlaTesting;
using TecnalHub.Services.Platform;

namespace TecnalHub.ViewModels;

public sealed partial class KlaConditionRowViewModel : ObservableObject
{
    public KlaTestCondition Model { get; }

    public KlaConditionRowViewModel(KlaTestCondition model)
    {
        Model = model;
    }

    public Guid ConditionId => Model.ConditionId;
    public int OrderIndex => Model.OrderIndex + 1;
    public double AgitationRpm => Model.AgitationRpm;
    public double AirflowLpm => Model.AirflowLpm;
    public int RequestedReplicates
    {
        get => Model.RequestedReplicates;
        set
        {
            var normalized = Math.Clamp(value, 1, 99);
            if (Model.RequestedReplicates == normalized)
            {
                return;
            }

            Model.RequestedReplicates = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayReplicates));
        }
    }
    public int CompletedReplicates => Model.CompletedReplicates;
    public int AcceptedReplicates => Model.AcceptedReplicates;
    public int RejectedReplicates => Model.RejectedReplicates;
    public ConditionStatus Status => Model.Status;
    public ConditionOrigin Origin => Model.Origin;

    public string DisplayStatus => Status switch
    {
        ConditionStatus.Pending => "Pendente",
        ConditionStatus.InProgress => "Em Execução",
        ConditionStatus.Completed => "Concluído",
        ConditionStatus.Skipped => "Ignorado",
        _ => Status.ToString()
    };

    public string DisplayCondition => $"{AgitationRpm:F0} rpm · {AirflowLpm:F2} L/min";
    public string DisplayReplicates => $"{AcceptedReplicates}/{RequestedReplicates}";
    public bool CanExecute => Status != ConditionStatus.Completed;

    public void NotifyChanged()
    {
        OnPropertyChanged(nameof(CompletedReplicates));
        OnPropertyChanged(nameof(AcceptedReplicates));
        OnPropertyChanged(nameof(RejectedReplicates));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(DisplayStatus));
        OnPropertyChanged(nameof(DisplayReplicates));
        OnPropertyChanged(nameof(CanExecute));
    }
}

public sealed partial class KlaMatrixRowViewModel : ObservableObject
{
    public KlaTestCondition Condition { get; }
    public Guid ConditionId => Condition.ConditionId;
    public int OrderIndex { get; set; }
    public int ReplicateIndex { get; set; } = 1;
    public double AgitationRpm => Condition.AgitationRpm;
    public double AirflowLpm => Condition.AirflowLpm;

    public string ReplicateLabel => $"R{ReplicateIndex}";

    private ConditionStatus _status = ConditionStatus.Pending;
    public ConditionStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(DisplayStatus));
            }
        }
    }

    public string DisplayStatus => Status switch
    {
        ConditionStatus.Pending => "Pendente",
        ConditionStatus.InProgress => "Em Execução",
        ConditionStatus.Completed => "Concluído",
        ConditionStatus.Skipped => "Ignorado",
        _ => Status.ToString()
    };

    private double? _klaPerHour;
    public double? KlaPerHour
    {
        get => _klaPerHour;
        set
        {
            if (SetProperty(ref _klaPerHour, value))
            {
                OnPropertyChanged(nameof(DisplayKla));
            }
        }
    }

    private double? _analysisR2;
    public double? AnalysisR2
    {
        get => _analysisR2;
        set
        {
            if (SetProperty(ref _analysisR2, value))
            {
                OnPropertyChanged(nameof(DisplayR2));
            }
        }
    }

    public string DisplayKla => KlaPerHour.HasValue ? KlaPerHour.Value.ToString("F1", CultureInfo.InvariantCulture) : "—";
    public string DisplayR2 => AnalysisR2.HasValue ? AnalysisR2.Value.ToString("F4", CultureInfo.InvariantCulture) : "—";

    public string? RunFolderName { get; set; }
    public bool HasRunData => !string.IsNullOrEmpty(RunFolderName);

    public KlaMatrixRowViewModel(KlaTestCondition condition, int replicateIndex = 1)
    {
        Condition = condition;
        ReplicateIndex = replicateIndex;
        OrderIndex = condition.OrderIndex + 1;
        Status = condition.Status;
    }

    public void NotifyChanged()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(DisplayStatus));
        OnPropertyChanged(nameof(KlaPerHour));
        OnPropertyChanged(nameof(AnalysisR2));
        OnPropertyChanged(nameof(DisplayKla));
        OnPropertyChanged(nameof(DisplayR2));
        OnPropertyChanged(nameof(HasRunData));
    }
}

public sealed partial class KlaDeterminationViewModel : ObservableObject, IDisposable
{
    private readonly IKlaTestRunner _runner;
    private readonly IKlaTestStore _store;
    private readonly IKlaAnalysisEngine _analysisEngine;
    private readonly IKlaProfileStore _mappingStore;
    private readonly IDialogService _dialogs;
    private readonly IFileInteractionService _files;

    private bool _disposed;

    public KlaDeterminationViewModel(
        IKlaTestRunner runner,
        IKlaTestStore store,
        IKlaAnalysisEngine analysisEngine,
        IKlaProfileStore mappingStore,
        IDialogService dialogs,
        IFileInteractionService files,
        KlaPlaybackOptions? playbackOptions = null)
    {
        _runner = runner;
        _store = store;
        _analysisEngine = analysisEngine;
        _mappingStore = mappingStore;
        _dialogs = dialogs;
        _files = files;
        IsSimulationMode = playbackOptions is not null;
        SimulationDescription = playbackOptions is null
            ? ""
            : $"SIMULAÇÃO · {playbackOptions.DisplayName} · {playbackOptions.Speed:G}× · nenhum comando é enviado ao hardware";

        _runner.StateChanged += OnRunnerStateChanged;
        _runner.DataPointAdded += OnDataPointAdded;
        _runner.Logged += OnRunnerLogged;

        RefreshTestsList();
        RefreshAvailableMaps();
    }

    // ── Observable Properties ──────────────────────────────────────────────────

    [ObservableProperty]
    private KlaTestSummary? _selectedTestSummary;

    [ObservableProperty]
    private KlaTestDocument? _currentTest;

    [ObservableProperty]
    private KlaTestRun? _currentRun;

    [ObservableProperty]
    private KlaTestCondition? _currentCondition;

    [ObservableProperty]
    private RunPhase _phase = RunPhase.Idle;

    [ObservableProperty]
    private string _statusMessage = "Pronto para iniciar novo teste ou abrir existente.";

    [ObservableProperty]
    private double _currentDO;

    [ObservableProperty]
    private double _currentFlow;

    [ObservableProperty]
    private double _phaseElapsedSeconds;

    [ObservableProperty]
    private double _totalElapsedSeconds;

    // Test Creation Form
    [ObservableProperty]
    private string _newTestName = "";

    [ObservableProperty]
    private NitrogenValve _selectedN2Valve = NitrogenValve.Valve1;

    [ObservableProperty]
    private double _settingDOMin = 5.0;

    [ObservableProperty]
    private double _settingDOMax = 85.0;

    [ObservableProperty]
    private double _settingDegassingAgitation = 700.0;

    [ObservableProperty]
    private int _settingSmoothingWindow = 5;

    [ObservableProperty]
    private double _settingMaxDegassingMinutes = 30;

    [ObservableProperty]
    private double _settingMaxReoxygenationMinutes = 60;

    [ObservableProperty]
    private bool _autoAcceptRuns;

    [ObservableProperty]
    private double _settingAutoLinearStartPercent = 45.0;

    [ObservableProperty]
    private double _settingAutoLinearEndPercent = 70.0;

    [ObservableProperty]
    private KlaExperimentDocument? _selectedMapForImport;

    [ObservableProperty]
    private bool _isCreateDialogOpen;

    [ObservableProperty]
    private bool _isLoadTestDialogOpen;

    [ObservableProperty]
    private KlaMatrixRowViewModel? _selectedMatrixRow;

    private KlaTestRunSummary? _currentlyEditingRun;
    private KlaMatrixRowViewModel? _currentlyEditingRow;

    [ObservableProperty]
    private bool _isAdvancedSettingsDialogOpen;

    // Review Drawer Properties
    [ObservableProperty]
    private bool _isReviewOpen;

    [ObservableProperty]
    private double _reviewMinTime = 0.0;

    [ObservableProperty]
    private double _reviewMaxTime = 100.0;

    [ObservableProperty]
    private double _reviewCeq = 100.0;

    [ObservableProperty]
    private bool _reviewCeqIsManual;

    [ObservableProperty]
    private double _reviewTStart;

    [ObservableProperty]
    private double _reviewTEnd = 100.0;

    [ObservableProperty]
    private double _reviewCeqTStart;

    [ObservableProperty]
    private double _reviewCeqTEnd = 100.0;

    [ObservableProperty]
    private double _reviewKla;

    [ObservableProperty]
    private double _reviewR2;

    [ObservableProperty]
    private double _reviewRmse;

    [ObservableProperty]
    private double _reviewCi95Low;

    [ObservableProperty]
    private double _reviewCi95High;

    [ObservableProperty]
    private double _reviewSensLow;

    [ObservableProperty]
    private double _reviewSensHigh;

    [ObservableProperty]
    private DecisionQuality _reviewQuality = DecisionQuality.Inconclusive;

    [ObservableProperty]
    private string? _reviewWarning;

    [ObservableProperty]
    private string? _reviewRejectionReason;

    [ObservableProperty]
    private KlaAnalysisRevision? _currentAnalysis;

    public string DisplayReviewKla => IsReviewOpen && CurrentAnalysis != null ? $"{ReviewKla:F1} h⁻¹" : "—";
    public string DisplayReviewR2 => IsReviewOpen && CurrentAnalysis != null ? $"R²: {ReviewR2:F4}" : "R²: —";
    public string DisplayReviewRmse => IsReviewOpen && CurrentAnalysis != null ? $"RMSE: {ReviewRmse:F4}" : "RMSE: —";
    public string DisplayReviewCi95 => IsReviewOpen && CurrentAnalysis != null ? $"IC 95%: [{ReviewCi95Low:F1}; {ReviewCi95High:F1}]" : "IC 95%: [—; —]";
    public string DisplayReviewSens => IsReviewOpen && CurrentAnalysis != null ? $"[{ReviewSensLow:F1}; {ReviewSensHigh:F1}] h⁻¹" : "[—; —] h⁻¹";
    public string DisplayReviewQuality => !IsReviewOpen || CurrentAnalysis == null
        ? "Inativo"
        : ReviewQuality switch
        {
            DecisionQuality.Acceptable => "Aceitável",
            DecisionQuality.AcceptableWithWarning => "Atenção / Ruído",
            DecisionQuality.Inconclusive => "Inconclusivo",
            _ => ReviewQuality.ToString()
        };

    private bool _isRecomputing;

    partial void OnIsReviewOpenChanged(bool value) => NotifyDisplayReviewChanged();
    partial void OnReviewKlaChanged(double value) => OnPropertyChanged(nameof(DisplayReviewKla));
    partial void OnReviewR2Changed(double value) => OnPropertyChanged(nameof(DisplayReviewR2));
    partial void OnReviewRmseChanged(double value) => OnPropertyChanged(nameof(DisplayReviewRmse));
    partial void OnReviewCi95LowChanged(double value) => OnPropertyChanged(nameof(DisplayReviewCi95));
    partial void OnReviewCi95HighChanged(double value) => OnPropertyChanged(nameof(DisplayReviewCi95));
    partial void OnReviewSensLowChanged(double value) => OnPropertyChanged(nameof(DisplayReviewSens));
    partial void OnReviewSensHighChanged(double value) => OnPropertyChanged(nameof(DisplayReviewSens));
    partial void OnReviewQualityChanged(DecisionQuality value) => OnPropertyChanged(nameof(DisplayReviewQuality));
    partial void OnCurrentAnalysisChanged(KlaAnalysisRevision? value) => NotifyDisplayReviewChanged();

    private void NotifyDisplayReviewChanged()
    {
        OnPropertyChanged(nameof(DisplayReviewKla));
        OnPropertyChanged(nameof(DisplayReviewR2));
        OnPropertyChanged(nameof(DisplayReviewRmse));
        OnPropertyChanged(nameof(DisplayReviewCi95));
        OnPropertyChanged(nameof(DisplayReviewSens));
        OnPropertyChanged(nameof(DisplayReviewQuality));
    }

    partial void OnReviewTStartChanged(double value)
    {
        var rounded = Math.Round(value, 1);
        if (Math.Abs(value - rounded) > 0.001)
        {
            ReviewTStart = rounded;
            return;
        }
        AutoRecompute();
    }

    partial void OnReviewTEndChanged(double value)
    {
        var rounded = Math.Round(value, 1);
        if (Math.Abs(value - rounded) > 0.001)
        {
            ReviewTEnd = rounded;
            return;
        }
        AutoRecompute();
    }

    partial void OnReviewCeqTStartChanged(double value)
    {
        var rounded = Math.Round(value, 1);
        if (Math.Abs(value - rounded) > 0.001)
        {
            ReviewCeqTStart = rounded;
            return;
        }
        AutoRecompute();
    }

    partial void OnReviewCeqTEndChanged(double value)
    {
        var rounded = Math.Round(value, 1);
        if (Math.Abs(value - rounded) > 0.001)
        {
            ReviewCeqTEnd = rounded;
            return;
        }
        AutoRecompute();
    }

    partial void OnReviewCeqChanged(double value)
    {
        if (ReviewCeqIsManual)
        {
            AutoRecompute();
        }
    }
    partial void OnReviewCeqIsManualChanged(bool value) => AutoRecompute();

    private void AutoRecompute()
    {
        if (!_isRecomputing && IsReviewOpen)
        {
            RecomputeReviewAnalysis();
        }
    }

    // New condition manual entry
    [ObservableProperty]
    private double _newConditionRpm = 300;

    [ObservableProperty]
    private double _newConditionFlow = 2.0;

    [ObservableProperty]
    private int _newConditionReplicates = 1;

    // Collections
    public ObservableCollection<KlaTestSummary> Tests { get; } = [];
    public ObservableCollection<KlaExperimentDocument> AvailableMaps { get; } = [];
    public ObservableCollection<KlaConditionRowViewModel> Conditions { get; } = [];
    public ObservableCollection<KlaMatrixRowViewModel> MatrixRows { get; } = [];
    public ObservableCollection<KlaRawDataPoint> LivePoints { get; } = [];
    public ObservableCollection<InstantaneousKlaPoint> InstantaneousKlaSeries { get; } = [];
    public ObservableCollection<LogLinearPoint> LogLinearSeries { get; } = [];
    public ObservableCollection<string> RunLogs { get; } = [];

    public bool IsSimulationMode { get; }
    public string SimulationDescription { get; }

    // Computed / Display helpers
    public bool HasActiveTest => CurrentTest is not null;
    public bool IsRunning => _runner.IsRunning;
    public bool IsIdle => !IsRunning && !IsReviewOpen;
    public bool CanStartSequence => HasActiveTest && IsIdle;
    public string DisplayPhase => Phase switch
    {
        RunPhase.Idle => "Inativo",
        RunPhase.Preflight => "Pré-voo",
        RunPhase.ClosingAllGas => "Fechando todas as válvulas",
        RunPhase.OpeningNitrogen => "Abrindo N₂",
        RunPhase.Deoxygenating => "Desoxigenando (N₂)",
        RunPhase.ClosingNitrogen => "Fechando N₂",
        RunPhase.OpeningAir => "Abrindo Ar",
        RunPhase.Reoxygenating => "Reoxigenando (Ar)",
        RunPhase.StoppingRun => "Fechando válvulas da corrida",
        RunPhase.Reviewing => "Em Revisão",
        RunPhase.Accepted => "Corrida Aceita",
        RunPhase.Rejected => "Corrida Rejeitada",
        RunPhase.Completed => "Teste Concluído",
        RunPhase.Faulted => "Falha / Interrompido",
        _ => Phase.ToString(),
    };

    public string FormattedTotalTime => TimeSpan.FromSeconds(TotalElapsedSeconds).ToString(@"hh\:mm\:ss");
    public string FormattedPhaseTime => TimeSpan.FromSeconds(PhaseElapsedSeconds).ToString(@"mm\:ss");

    // ── Commands ───────────────────────────────────────────────────────────────

    [RelayCommand]
    public void OpenCreateDialog()
    {
        NewTestName = $"Ensaio_kLa_{DateTime.Now:yyyy-MM-dd_HHmm}";
        RefreshAvailableMaps();
        IsCreateDialogOpen = true;
    }

    [RelayCommand]
    public void CloseCreateDialog()
    {
        IsCreateDialogOpen = false;
    }

    [RelayCommand]
    public void OpenLoadTestDialog()
    {
        RefreshTestsList();
        IsLoadTestDialogOpen = true;
    }

    [RelayCommand]
    public void CloseLoadTestDialog()
    {
        IsLoadTestDialogOpen = false;
    }

    [RelayCommand]
    public void ConfirmLoadTest()
    {
        if (SelectedTestSummary is null)
        {
            return;
        }
        LoadTest(SelectedTestSummary.FolderName);
        IsLoadTestDialogOpen = false;
    }

    [RelayCommand]
    public void CreateNewTest()
    {
        if (!_store.ValidateTestName(NewTestName, out var error))
        {
            _dialogs.Confirm("Nome Inválido", error ?? "Nome de teste inválido.", "OK", "");
            return;
        }

        if (_store.TestExists(NewTestName))
        {
            _dialogs.Confirm("Nome Duplicado", $"Já existe um teste com o nome '{NewTestName}'. Escolha outro nome.", "OK", "");
            return;
        }

        var settings = new KlaTestSettings
        {
            DOMinPercent = SettingDOMin,
            DOMaxPercent = SettingDOMax,
            DegassingAgitationRpm = SettingDegassingAgitation,
            SmoothingWindowSize = SettingSmoothingWindow,
            MaxDegassingTimeMinutes = SettingMaxDegassingMinutes,
            MaxReoxygenationTimeMinutes = SettingMaxReoxygenationMinutes,
            AutoAcceptRuns = AutoAcceptRuns,
            AutoLinearStartPercent = SettingAutoLinearStartPercent,
            AutoLinearEndPercent = SettingAutoLinearEndPercent,
        };

        KlaMapReference? mapRef = null;
        IReadOnlyList<KlaTestCondition>? initialConditions = null;

        if (SelectedMapForImport is not null)
        {
            (mapRef, initialConditions) = KlaMapImportHelper.ImportConditionsFromMap(SelectedMapForImport, 1);
        }

        var doc = _store.CreateTest(NewTestName, settings, SelectedN2Valve, mapRef, initialConditions);
        IsCreateDialogOpen = false;

        LoadTest(doc.FolderName);
        RefreshTestsList();
    }

    [RelayCommand]
    public void LoadSelectedTest()
    {
        if (SelectedTestSummary is null)
        {
            return;
        }
        LoadTest(SelectedTestSummary.FolderName);
    }

    public void LoadTest(string folderName)
    {
        var doc = _store.LoadTest(folderName);
        if (doc is null)
        {
            _dialogs.Confirm("Erro", $"Não foi possível carregar o teste '{folderName}'.", "OK", "");
            return;
        }

        CurrentTest = doc;
        SettingDOMin = doc.Settings.DOMinPercent;
        SettingDOMax = doc.Settings.DOMaxPercent;
        SettingDegassingAgitation = doc.Settings.DegassingAgitationRpm;
        SettingSmoothingWindow = doc.Settings.SmoothingWindowSize;
        SettingMaxDegassingMinutes = doc.Settings.MaxDegassingTimeMinutes;
        SettingMaxReoxygenationMinutes = doc.Settings.MaxReoxygenationTimeMinutes;
        AutoAcceptRuns = doc.Settings.AutoAcceptRuns;
        SettingAutoLinearStartPercent = doc.Settings.AutoLinearStartPercent;
        SettingAutoLinearEndPercent = doc.Settings.AutoLinearEndPercent;
        SelectedN2Valve = doc.SelectedNitrogenValve;

        Conditions.Clear();
        foreach (var c in doc.Conditions)
        {
            Conditions.Add(new KlaConditionRowViewModel(c));
        }

        RefreshConditionsList();

        _runner.StartTestAsync(doc);
        UpdateUiState();

        // If doc has runs, load the first completed run into charts for immediate viewing
        var firstRun = doc.Runs.FirstOrDefault(r => r.KlaPerHour.HasValue) ?? doc.Runs.FirstOrDefault();
        if (firstRun is not null)
        {
            var matchingRow = MatrixRows.FirstOrDefault(r => r.ConditionId == firstRun.ConditionId && r.ReplicateIndex == firstRun.ReplicateNumber)
                           ?? MatrixRows.FirstOrDefault(r => r.ConditionId == firstRun.ConditionId);
            if (matchingRow is not null)
            {
                LoadMatrixRow(matchingRow);
            }
        }
    }

    [RelayCommand]
    public void LoadMatrixRow(KlaMatrixRowViewModel? row)
    {
        if (row is null || CurrentTest is null)
        {
            return;
        }

        SelectedMatrixRow = row;
        _currentlyEditingRow = row;

        // 1. Locate matching run in doc
        var run = CurrentTest.Runs.FirstOrDefault(r => r.ConditionId == row.ConditionId && r.ReplicateNumber == row.ReplicateIndex)
               ?? CurrentTest.Runs.FirstOrDefault(r => r.ConditionId == row.ConditionId);

        if (run is null && !string.IsNullOrEmpty(row.RunFolderName))
        {
            run = new KlaTestRunSummary
            {
                RunId = Guid.NewGuid(),
                ConditionId = row.ConditionId,
                ReplicateNumber = row.ReplicateIndex,
                FolderName = row.RunFolderName,
                AgitationRpm = row.AgitationRpm,
                AirflowLpm = row.AirflowLpm,
                Phase = RunPhase.Accepted,
                StartedUtc = DateTimeOffset.UtcNow,
            };
        }

        if (run is null)
        {
            // Scan folder on disk
            var pattern = KlaTestFileContracts.FormatRunFolderName(row.AgitationRpm, row.AirflowLpm, row.ReplicateIndex);
            var runsDir = System.IO.Path.Combine(_store.RootDirectory, CurrentTest.FolderName, KlaTestFileContracts.RunsDirectoryName);
            if (System.IO.Directory.Exists(runsDir))
            {
                var match = System.IO.Directory.GetDirectories(runsDir, $"*{pattern}*").FirstOrDefault();
                if (match != null)
                {
                    var folderName = System.IO.Path.GetFileName(match);
                    run = new KlaTestRunSummary
                    {
                        RunId = Guid.NewGuid(),
                        ConditionId = row.ConditionId,
                        ReplicateNumber = row.ReplicateIndex,
                        FolderName = folderName,
                        AgitationRpm = row.AgitationRpm,
                        AirflowLpm = row.AirflowLpm,
                        Phase = RunPhase.Accepted,
                        StartedUtc = DateTimeOffset.UtcNow,
                    };
                    CurrentTest.Runs.Add(run);
                }
            }
        }

        if (run is null)
        {
            _dialogs.Confirm("Aviso", $"Não há dados gravados para a réplica #{row.ReplicateIndex} ({row.AgitationRpm:F0} rpm, {row.AirflowLpm:F2} L/min).", "OK", "");
            return;
        }

        _currentlyEditingRun = run;

        // 2. Load Raw Data Points from CSV
        var rawPoints = _store.LoadRunRawData(CurrentTest.FolderName, run.FolderName);
        if (rawPoints.Count == 0)
        {
            _dialogs.Confirm("Aviso", $"O arquivo de dados brutos da corrida '{run.FolderName}' está vazio ou não foi encontrado.", "OK", "");
            return;
        }

        LivePoints.Clear();
        foreach (var p in rawPoints)
        {
            LivePoints.Add(p);
        }

        // 3. Load or initialize analysis
        var analysis = _store.LoadRunAnalysis(CurrentTest.FolderName, run.FolderName);
        if (analysis is not null)
        {
            ReviewTStart = analysis.TStartSeconds;
            ReviewTEnd = analysis.TEndSeconds;
            ReviewCeqTStart = analysis.CeqTStartSeconds > 0 ? analysis.CeqTStartSeconds : analysis.TStartSeconds;
            ReviewCeqTEnd = analysis.CeqTEndSeconds > 0 ? analysis.CeqTEndSeconds : analysis.TEndSeconds;
            ReviewCeq = analysis.CeqPercent;
            ReviewCeqIsManual = analysis.IsCeqManual;
            CurrentAnalysis = analysis;
            ReviewKla = analysis.KlaPerHour;
            ReviewR2 = analysis.AnalysisR2;
            ReviewRmse = analysis.AnalysisRmse;
            ReviewCi95Low = analysis.ConfidenceInterval95Low;
            ReviewCi95High = analysis.ConfidenceInterval95High;
            ReviewSensLow = analysis.KlaSensitivityLow;
            ReviewSensHigh = analysis.KlaSensitivityHigh;
            ReviewQuality = analysis.Quality;
            ReviewWarning = analysis.WarningJustification;
            ReviewRejectionReason = analysis.RejectionReason;

            var times = LivePoints.Select(p => p.RelativeSeconds).ToList();
            var dos = LivePoints.Select(p => p.DOFiltered > 0 ? p.DOFiltered : p.DORaw).ToList();

            LogLinearSeries.Clear();
            foreach (var lp in _analysisEngine.ComputeLogLinearPoints(times, dos, ReviewCeq, ReviewTStart, ReviewTEnd))
            {
                LogLinearSeries.Add(lp);
            }

            InstantaneousKlaSeries.Clear();
            foreach (var ip in _analysisEngine.CalculateInstantaneousKlaSeries(times, dos, ReviewCeq, SettingSmoothingWindow))
            {
                InstantaneousKlaSeries.Add(ip);
            }

            ReviewMinTime = Math.Floor(times.First());
            ReviewMaxTime = Math.Ceiling(times.Last());
            IsReviewOpen = true;
        }
        else
        {
            OpenReviewDrawer();
        }

        StatusMessage = $"Ensaio carregado: #{row.OrderIndex} - {row.AgitationRpm:F0} rpm · {row.AirflowLpm:F2} L/min (Réplica {row.ReplicateIndex})";
    }

    [RelayCommand]
    public void AddManualCondition()
    {
        if (CurrentTest is null)
        {
            return;
        }

        var cond = new KlaTestCondition
        {
            ConditionId = Guid.NewGuid(),
            OrderIndex = CurrentTest.Conditions.Count,
            AgitationRpm = NewConditionRpm,
            AirflowLpm = NewConditionFlow,
            RequestedReplicates = NewConditionReplicates,
            Origin = ConditionOrigin.Manual,
            Status = ConditionStatus.Pending,
        };

        CurrentTest.Conditions.Add(cond);
        _store.SaveConditionsTable(CurrentTest.FolderName, CurrentTest.Conditions);
        Conditions.Add(new KlaConditionRowViewModel(cond));
        RefreshConditionsList();
        UpdateUiState();
    }

    [RelayCommand]
    public void RemoveMatrixRow(KlaMatrixRowViewModel? row)
    {
        if (row is null || CurrentTest is null)
        {
            return;
        }

        if (IsRunning)
        {
            _dialogs.Confirm("Aviso", "Não é possível remover condições enquanto um ensaio está em execução.", "OK", "");
            return;
        }

        CurrentTest.Conditions.Remove(row.Condition);
        for (int i = 0; i < CurrentTest.Conditions.Count; i++)
        {
            CurrentTest.Conditions[i].OrderIndex = i;
        }
        _store.SaveConditionsTable(CurrentTest.FolderName, CurrentTest.Conditions);

        Conditions.Clear();
        foreach (var c in CurrentTest.Conditions)
        {
            Conditions.Add(new KlaConditionRowViewModel(c));
        }

        RefreshConditionsList();
        UpdateUiState();
    }

    [RelayCommand]
    public void RemoveCondition(KlaConditionRowViewModel? row)
    {
        if (row is null || CurrentTest is null)
        {
            return;
        }

        if (IsRunning)
        {
            _dialogs.Confirm("Aviso", "Não é possível remover condições enquanto um ensaio está em execução.", "OK", "");
            return;
        }

        CurrentTest.Conditions.Remove(row.Model);
        for (int i = 0; i < CurrentTest.Conditions.Count; i++)
        {
            CurrentTest.Conditions[i].OrderIndex = i;
        }
        _store.SaveConditionsTable(CurrentTest.FolderName, CurrentTest.Conditions);

        Conditions.Clear();
        foreach (var c in CurrentTest.Conditions)
        {
            Conditions.Add(new KlaConditionRowViewModel(c));
        }

        RefreshConditionsList();
        UpdateUiState();
    }

    [RelayCommand]
    public async Task StartSequenceAsync()
    {
        if (CurrentTest is null || IsRunning)
        {
            return;
        }

        var nextCondition = Conditions.FirstOrDefault(c => c.Model.AcceptedReplicates < c.Model.RequestedReplicates);
        if (nextCondition is null)
        {
            StatusMessage = "Todas as condições da matriz foram concluídas.";
            _dialogs.Confirm("Ensaio Concluído", "Todas as condições da matriz experimental foram concluídas.", "OK", "");
            return;
        }

        var cond = nextCondition.Model;
        _store.SaveConditionsTable(CurrentTest.FolderName, CurrentTest.Conditions);
        var nextRep = cond.CompletedReplicates + 1;

        LivePoints.Clear();
        InstantaneousKlaSeries.Clear();
        LogLinearSeries.Clear();

        await _runner.StartRunAsync(cond, nextRep);
    }

    [RelayCommand]
    public async Task StartConditionRunAsync(KlaConditionRowViewModel? row)
    {
        if (row is null || CurrentTest is null)
        {
            return;
        }

        var cond = row.Model;
        _store.SaveConditionsTable(CurrentTest.FolderName, CurrentTest.Conditions);
        var nextRep = cond.CompletedReplicates + 1;

        LivePoints.Clear();
        InstantaneousKlaSeries.Clear();
        LogLinearSeries.Clear();

        await _runner.StartRunAsync(cond, nextRep);
    }

    [RelayCommand]
    public async Task StopRunAsync()
    {
        await _runner.StopRunAndReviewAsync("Parada manual pelo operador");
    }

    [RelayCommand]
    public void ApplyLiveSettings()
    {
        if (CurrentTest is null)
        {
            return;
        }

        var newSettings = CurrentTest.Settings with
        {
            DOMinPercent = SettingDOMin,
            DOMaxPercent = SettingDOMax,
            DegassingAgitationRpm = SettingDegassingAgitation,
            SmoothingWindowSize = SettingSmoothingWindow,
            MaxDegassingTimeMinutes = SettingMaxDegassingMinutes,
            MaxReoxygenationTimeMinutes = SettingMaxReoxygenationMinutes,
            AutoAcceptRuns = AutoAcceptRuns,
            AutoLinearStartPercent = SettingAutoLinearStartPercent,
            AutoLinearEndPercent = SettingAutoLinearEndPercent,
        };

        CurrentTest.Settings = newSettings;
        _runner.UpdateLiveSettings(newSettings);
        _store.SaveTestManifest(CurrentTest);
    }

    partial void OnAutoAcceptRunsChanged(bool value)
    {
        if (CurrentTest is not null)
        {
            CurrentTest.Settings = CurrentTest.Settings with { AutoAcceptRuns = value };
            _runner.UpdateLiveSettings(CurrentTest.Settings);
            _store.SaveTestManifest(CurrentTest);
        }
    }

    [RelayCommand]
    public void OpenAdvancedSettingsDialog()
    {
        IsAdvancedSettingsDialogOpen = true;
    }

    [RelayCommand]
    public void CloseAdvancedSettingsDialog()
    {
        IsAdvancedSettingsDialogOpen = false;
    }

    [RelayCommand]
    public void SaveAdvancedSettings()
    {
        ApplyLiveSettings();
        IsAdvancedSettingsDialogOpen = false;
    }

    [RelayCommand]
    public void RecomputeReviewAnalysis()
    {
        if (LivePoints.Count < 5)
        {
            return;
        }

        var recovery = LivePoints.Where(p => p.Phase == RunPhase.Reoxygenating).ToList();
        if (recovery.Count < 5)
        {
            ReviewRejectionReason = "A corrida não contém ao menos 5 pontos de reoxigenação.";
            return;
        }

        _isRecomputing = true;
        try
        {
            var times = recovery.Select(p => p.RelativeSeconds).ToList();
            var dos = recovery.Select(p => p.DORaw).ToList();

            var ceqPoints = recovery.Where(p => p.RelativeSeconds >= ReviewCeqTStart && p.RelativeSeconds <= ReviewCeqTEnd).ToList();

            // 1. Ceq Fit / Override
            var ceqResult = _analysisEngine.EstimateCeq(
                ceqPoints.Select(p => p.RelativeSeconds).ToList(),
                ceqPoints.Select(p => p.DORaw).ToList(),
                ReviewCeqIsManual ? ReviewCeq : null);

            if (!ReviewCeqIsManual && ceqResult.Converged)
            {
                ReviewCeq = Math.Round(ceqResult.CeqPercent, 2);
            }

            // 2. Perform Log-Linear OLS
            var analysis = _analysisEngine.PerformLogLinearAnalysis(
                times,
                dos,
                ReviewCeq,
                ReviewCeqIsManual,
                ReviewTStart,
                ReviewTEnd,
                ceqResult);

            CurrentAnalysis = analysis;
            analysis.CeqTStartSeconds = ReviewCeqTStart;
            analysis.CeqTEndSeconds = ReviewCeqTEnd;
            ReviewKla = analysis.KlaPerHour;
            ReviewR2 = analysis.AnalysisR2;
            ReviewRmse = analysis.AnalysisRmse;
            ReviewCi95Low = analysis.ConfidenceInterval95Low;
            ReviewCi95High = analysis.ConfidenceInterval95High;
            ReviewSensLow = analysis.KlaSensitivityLow;
            ReviewSensHigh = analysis.KlaSensitivityHigh;
            ReviewQuality = analysis.Quality;
            ReviewWarning = analysis.WarningJustification;
            ReviewRejectionReason = analysis.RejectionReason;

            // 3. Update Chart Series
            var logPoints = _analysisEngine.ComputeLogLinearPoints(
                times,
                dos,
                ReviewCeq,
                ReviewTStart,
                ReviewTEnd);

            LogLinearSeries.Clear();
            foreach (var lp in logPoints)
            {
                LogLinearSeries.Add(lp);
            }

            var instPoints = _analysisEngine.CalculateInstantaneousKlaSeries(
                times,
                dos,
                ReviewCeq,
                SettingSmoothingWindow);

            InstantaneousKlaSeries.Clear();
            foreach (var ip in instPoints)
            {
                InstantaneousKlaSeries.Add(ip);
            }
        }
        finally
        {
            _isRecomputing = false;
        }
    }

    [RelayCommand]
    public async Task AcceptCurrentRunAsync()
    {
        if (CurrentAnalysis is null)
        {
            RecomputeReviewAnalysis();
        }

        if (CurrentAnalysis is null)
        {
            return;
        }

        if (CurrentAnalysis.Quality == DecisionQuality.Inconclusive)
        {
            _dialogs.Confirm("Análise inconclusiva", CurrentAnalysis.RejectionReason ?? "Ajuste a análise ou rejeite a corrida.", "OK", "");
            return;
        }

        if (_runner.IsInReview || (_runner.CurrentCondition is not null && _currentlyEditingRun is null))
        {
            await _runner.AcceptRunAsync(CurrentAnalysis);
            IsReviewOpen = false;
            RefreshConditionsList();

            // Auto-advance to the next pending condition/replicate in sequence
            var nextCondition = Conditions.FirstOrDefault(c => c.Model.AcceptedReplicates < c.Model.RequestedReplicates);
            if (nextCondition is not null && CurrentTest is not null)
            {
                var cond = nextCondition.Model;
                var nextRep = cond.CompletedReplicates + 1;

                LivePoints.Clear();
                InstantaneousKlaSeries.Clear();
                LogLinearSeries.Clear();

                StatusMessage = $"Iniciando automaticamente condição #{cond.OrderIndex + 1} ({cond.AgitationRpm:F0} rpm, {cond.AirflowLpm:F2} L/min - réplica {nextRep}/{cond.RequestedReplicates})...";
                await _runner.StartRunAsync(cond, nextRep);
            }
            else
            {
                StatusMessage = "Todas as condições da matriz experimental foram concluídas com sucesso!";
                _dialogs.Confirm("Ensaio Concluído", "Todas as condições da matriz experimental foram concluídas com sucesso. O ensaio pode ser finalizado.", "OK", "");
            }

            UpdateUiState();
        }
        else if (_currentlyEditingRun is not null && CurrentTest is not null)
        {
            // Saving edited analysis on an existing / imported run
            CurrentAnalysis.RevisionNumber++;
            CurrentAnalysis.AnalyzedUtc = DateTimeOffset.UtcNow;
            _store.SaveRunAnalysis(CurrentTest.FolderName, _currentlyEditingRun.FolderName, CurrentAnalysis);

            var existingRunIndex = CurrentTest.Runs.FindIndex(r => r.RunId == _currentlyEditingRun.RunId || r.FolderName == _currentlyEditingRun.FolderName);
            var updatedRunSummary = _currentlyEditingRun with
            {
                Phase = RunPhase.Accepted,
                Decision = CurrentAnalysis.Quality,
                KlaPerHour = CurrentAnalysis.KlaPerHour,
                AnalysisR2 = CurrentAnalysis.AnalysisR2,
                CompletedUtc = DateTimeOffset.UtcNow,
            };
            if (existingRunIndex >= 0)
            {
                CurrentTest.Runs[existingRunIndex] = updatedRunSummary;
            }
            else
            {
                CurrentTest.Runs.Add(updatedRunSummary);
            }
            _currentlyEditingRun = updatedRunSummary;
            _store.SaveTestManifest(CurrentTest);

            if (_currentlyEditingRow is not null)
            {
                _currentlyEditingRow.KlaPerHour = CurrentAnalysis.KlaPerHour;
                _currentlyEditingRow.AnalysisR2 = CurrentAnalysis.AnalysisR2;
                _currentlyEditingRow.Status = ConditionStatus.Completed;
                _currentlyEditingRow.NotifyChanged();
            }

            RefreshConditionsList();
            StatusMessage = $"Revisão do ensaio '{_currentlyEditingRun.FolderName}' salva com sucesso (kLa = {CurrentAnalysis.KlaPerHour:F1} h⁻¹, R² = {CurrentAnalysis.AnalysisR2:F4}).";
        }
    }

    [RelayCommand]
    public async Task RejectCurrentRunAsync()
    {
        var reason = ReviewRejectionReason ?? "Rejeitado pelo operador na revisão.";
        await _runner.RejectRunAsync(reason);
        IsReviewOpen = false;
        RefreshConditionsList();
    }

    [RelayCommand]
    public async Task RepeatCurrentRunAsync()
    {
        IsReviewOpen = false;
        await _runner.RepeatRunAsync();
    }

    [RelayCommand]
    public async Task CompleteTestAsync()
    {
        await _runner.CompleteTestAsync();
        if (CurrentTest is not null)
        {
            foreach (var cond in CurrentTest.Conditions)
            {
                if (cond.Status == ConditionStatus.InProgress)
                {
                    cond.Status = cond.AcceptedReplicates >= cond.RequestedReplicates
                        ? ConditionStatus.Completed
                        : (cond.AcceptedReplicates > 0 ? ConditionStatus.Completed : ConditionStatus.Pending);
                }
            }
            _store.SaveConditionsTable(CurrentTest.FolderName, CurrentTest.Conditions);
        }
        RefreshConditionsList();
        RefreshTestsList();
        UpdateUiState();
    }

    [RelayCommand]
    public async Task AbortTestAsync()
    {
        await _runner.AbortTestAsync("Cancelado pelo operador");
        if (CurrentTest is not null)
        {
            foreach (var cond in CurrentTest.Conditions)
            {
                if (cond.Status == ConditionStatus.InProgress)
                {
                    cond.Status = cond.AcceptedReplicates >= cond.RequestedReplicates
                        ? ConditionStatus.Completed
                        : ConditionStatus.Pending;
                }
            }
            _store.SaveConditionsTable(CurrentTest.FolderName, CurrentTest.Conditions);
        }
        RefreshConditionsList();
        RefreshTestsList();
        UpdateUiState();
    }

    // ── Event Handlers ─────────────────────────────────────────────────────────

    private void OnRunnerStateChanged()
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(OnRunnerStateChanged);
            return;
        }
        Phase = _runner.Phase;
        StatusMessage = _runner.StatusMessage;
        CurrentDO = _runner.CurrentDO;
        CurrentFlow = _runner.CurrentFlowMeasured;
        PhaseElapsedSeconds = _runner.PhaseElapsedSeconds;
        TotalElapsedSeconds = _runner.TotalElapsedSeconds;
        CurrentRun = _runner.CurrentRun;
        CurrentCondition = _runner.CurrentCondition;

        if (_runner.IsInReview && !IsReviewOpen)
        {
            OpenReviewDrawer();
        }

        UpdateUiState();
    }

    private void OnDataPointAdded(KlaRawDataPoint point)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnDataPointAdded(point));
            return;
        }
        LivePoints.Add(point);
        if (point.Phase == RunPhase.Reoxygenating)
        {
            var recovery = LivePoints.Where(p => p.Phase == RunPhase.Reoxygenating).ToList();
            if (recovery.Count >= 5)
            {
                var times = recovery.Select(p => p.RelativeSeconds).ToList();
                var values = recovery.Select(p => p.DORaw).ToList();
                var ceq = CurrentTest?.Settings.DefaultCeqPercent ?? 100;
                InstantaneousKlaSeries.Clear();
                foreach (var item in _analysisEngine.CalculateInstantaneousKlaSeries(times, values, ceq, SettingSmoothingWindow))
                {
                    InstantaneousKlaSeries.Add(item);
                }

                LogLinearSeries.Clear();
                foreach (var item in _analysisEngine.ComputeLogLinearPoints(times, values, ceq, times[0], times[^1]))
                {
                    LogLinearSeries.Add(item);
                }
            }
        }
    }

    private void OnRunnerLogged(string message)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnRunnerLogged(message));
            return;
        }
        RunLogs.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    private async void OpenReviewDrawer()
    {
        if (LivePoints.Count > 0)
        {
            var times = LivePoints.Select(p => p.RelativeSeconds).ToList();
            var reoxPoints = LivePoints.Where(p => p.Phase == RunPhase.Reoxygenating).ToList();
            if (reoxPoints.Count >= 5)
            {
                var minT = reoxPoints.First().RelativeSeconds;
                var maxT = reoxPoints.Last().RelativeSeconds;
                var tSpan = Math.Max(1.0, maxT - minT);

                ReviewMinTime = Math.Floor(times.First());
                ReviewMaxTime = Math.Ceiling(times.Last());

                var startFraction = Math.Clamp(SettingAutoLinearStartPercent / 100.0, 0.01, 0.95);
                var endFraction = Math.Clamp(SettingAutoLinearEndPercent / 100.0, startFraction + 0.05, 0.99);

                var minDO = reoxPoints.Min(p => p.DOFiltered > 0 ? p.DOFiltered : p.DORaw);
                var maxDO = reoxPoints.Max(p => p.DOFiltered > 0 ? p.DOFiltered : p.DORaw);
                var doSpan = maxDO - minDO;
                if (doSpan > 5.0)
                {
                    var targetStartDO = minDO + (doSpan * startFraction);
                    var targetEndDO = minDO + (doSpan * endFraction);
                    var ptStart = reoxPoints.FirstOrDefault(p => (p.DOFiltered > 0 ? p.DOFiltered : p.DORaw) >= targetStartDO);
                    var ptEnd = reoxPoints.FirstOrDefault(p => (p.DOFiltered > 0 ? p.DOFiltered : p.DORaw) >= targetEndDO);
                    ReviewTStart = ptStart != null ? Math.Round(ptStart.RelativeSeconds, 1) : Math.Round(minT + (startFraction * tSpan), 1);
                    ReviewTEnd = ptEnd != null ? Math.Round(ptEnd.RelativeSeconds, 1) : Math.Round(minT + (endFraction * tSpan), 1);
                }
                else
                {
                    ReviewTStart = Math.Round(minT + (startFraction * tSpan), 1);
                    ReviewTEnd = Math.Round(minT + (endFraction * tSpan), 1);
                }

                ReviewCeqTStart = Math.Round(minT + (0.85 * tSpan), 1);
                ReviewCeqTEnd = Math.Round(maxT, 1);
            }
            else
            {
                ReviewMinTime = Math.Floor(times.First());
                ReviewMaxTime = Math.Ceiling(times.Last());
                ReviewTStart = Math.Round(times.First(), 1);
                ReviewTEnd = Math.Round(times.Last(), 1);
                ReviewCeqTStart = ReviewTStart;
                ReviewCeqTEnd = ReviewTEnd;
            }

            ReviewCeqIsManual = false;
            IsReviewOpen = true;
            RecomputeReviewAnalysis();

            if (AutoAcceptRuns)
            {
                await Task.Delay(100);
                if (IsReviewOpen)
                {
                    await AcceptCurrentRunAsync();
                }
            }
        }
        else
        {
            IsReviewOpen = true;
        }
    }

    public void RefreshTestsList()
    {
        Tests.Clear();
        foreach (var t in _store.ListTests())
        {
            Tests.Add(t);
        }
    }

    public async void RefreshAvailableMaps()
    {
        try
        {
            var maps = await _mappingStore.LoadExperimentsAsync();
            AvailableMaps.Clear();
            foreach (var m in maps)
            {
                AvailableMaps.Add(m);
            }
        }
        catch
        {
            // Best effort
        }
    }

    public void RefreshConditionsList()
    {
        foreach (var c in Conditions)
        {
            c.NotifyChanged();
        }

        MatrixRows.Clear();
        if (CurrentTest is null)
        {
            return;
        }

        var rowIndex = 1;
        foreach (var cond in CurrentTest.Conditions)
        {
            var requestedReps = Math.Max(1, cond.RequestedReplicates);
            for (var rep = 1; rep <= requestedReps; rep++)
            {
                var row = new KlaMatrixRowViewModel(cond, rep)
                {
                    OrderIndex = rowIndex++,
                };

                // Find matching run in CurrentTest.Runs
                var run = CurrentTest.Runs.FirstOrDefault(r => r.ConditionId == cond.ConditionId && r.ReplicateNumber == rep)
                       ?? (requestedReps == 1 ? CurrentTest.Runs.FirstOrDefault(r => r.ConditionId == cond.ConditionId) : null);

                if (run is not null)
                {
                    row.RunFolderName = run.FolderName;
                    var analysis = _store.LoadRunAnalysis(CurrentTest.FolderName, run.FolderName);
                    if (analysis is not null)
                    {
                        row.KlaPerHour = analysis.KlaPerHour;
                        row.AnalysisR2 = analysis.AnalysisR2;
                        row.Status = ConditionStatus.Completed;
                    }
                    else if (run.KlaPerHour.HasValue)
                    {
                        row.KlaPerHour = run.KlaPerHour;
                        row.AnalysisR2 = run.AnalysisR2;
                        row.Status = ConditionStatus.Completed;
                    }
                    else if (run.Phase == RunPhase.Accepted)
                    {
                        row.Status = ConditionStatus.Completed;
                    }
                }
                else if (cond.Status == ConditionStatus.Completed)
                {
                    row.Status = ConditionStatus.Completed;
                }
                else if (cond.Status == ConditionStatus.InProgress)
                {
                    row.Status = rep <= cond.CompletedReplicates ? ConditionStatus.Completed : ConditionStatus.InProgress;
                }
                else
                {
                    row.Status = ConditionStatus.Pending;
                }

                MatrixRows.Add(row);
            }
        }
    }

    private void UpdateUiState()
    {
        OnPropertyChanged(nameof(HasActiveTest));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(CanStartSequence));
        OnPropertyChanged(nameof(DisplayPhase));
        OnPropertyChanged(nameof(FormattedTotalTime));
        OnPropertyChanged(nameof(FormattedPhaseTime));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _runner.StateChanged -= OnRunnerStateChanged;
        _runner.DataPointAdded -= OnDataPointAdded;
        _runner.Logged -= OnRunnerLogged;
    }
}
