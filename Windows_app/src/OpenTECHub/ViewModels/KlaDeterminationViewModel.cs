using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Dialogs;
using OpenTECHub.Services.KlaMapping;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Platform;

namespace OpenTECHub.ViewModels;

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

    private RunPhase? _loadedRunPhase;
    public string? AutomaticDecisionLabel { get; set; }
    public RunPhase? LoadedRunPhase
    {
        get => _loadedRunPhase;
        set
        {
            if (SetProperty(ref _loadedRunPhase, value))
            {
                OnPropertyChanged(nameof(DisplayStatus));
            }
        }
    }

    public string DisplayStatus => AutomaticDecisionLabel ?? (LoadedRunPhase switch
    {
        RunPhase.Accepted => "Concluído",
        RunPhase.Rejected => "Rejeitado",
        RunPhase.Reviewing => "Revisão",
        _ => Status switch
        {
            ConditionStatus.Pending => "Pendente",
            ConditionStatus.InProgress => "Em Execução",
            ConditionStatus.Completed => "Concluído",
            ConditionStatus.Skipped => "Ignorado",
            _ => Status.ToString()
        }
    });

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
        OnPropertyChanged(nameof(LoadedRunPhase));
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
    private readonly ISettingsService _settings;

    private bool _disposed;
    private bool _isLoadingSettings;

    public KlaDeterminationViewModel(
        IKlaTestRunner runner,
        IKlaTestStore store,
        IKlaAnalysisEngine analysisEngine,
        IKlaProfileStore mappingStore,
        IDialogService dialogs,
        IFileInteractionService files,
        ISettingsService settings,
        KlaPlaybackOptions? playbackOptions = null)
    {
        _runner = runner;
        _store = store;
        _analysisEngine = analysisEngine;
        _mappingStore = mappingStore;
        _dialogs = dialogs;
        _files = files;
        _settings = settings;

        _isLoadingSettings = true;
        try
        {
            var klaSettings = settings.Current.KlaTest;
            SettingDOMin = klaSettings.DOMinPercent;
            SettingDOMax = klaSettings.DOMaxPercent;
            SettingDegassingAgitation = klaSettings.DegassingAgitationRpm == new KlaTestSettings().DegassingAgitationRpm
                ? 100 : klaSettings.DegassingAgitationRpm;
            SettingSmoothingWindow = klaSettings.SmoothingWindowSize;
            SettingMaxDegassingMinutes = klaSettings.MaxDegassingTimeMinutes;
            SettingMaxReoxygenationMinutes = klaSettings.MaxReoxygenationTimeMinutes;
            SettingStabilityDerivativeSpanSeconds = klaSettings.StabilityDerivativeSpanSeconds;
            SettingStabilityDerivativeThreshold = klaSettings.StabilityDerivativeThresholdPercentPerSecond;
            SettingStabilityRequiredSamples = klaSettings.StabilityRequiredSamples;
            SettingAirPrestageLeadPercent = klaSettings.AirPrestageLeadPercent;
            SettingPrestageFlowTolerance = klaSettings.PrestageFlowToleranceLpm;
            SettingPrestageFlowStableSamples = klaSettings.PrestageFlowStableSamples;
            SettingMaxPrestageSeconds = klaSettings.MaxPrestageSeconds;
            SettingDefaultCeq = klaSettings.DefaultCeqPercent;
            AutoAcceptRuns = klaSettings.AutoAcceptRuns;
            SettingAutoLinearStartPercent = klaSettings.AutoLinearStartPercent;
            SettingAutoLinearEndPercent = klaSettings.AutoLinearEndPercent;
        }
        finally
        {
            _isLoadingSettings = false;
        }

        IsSimulationMode = playbackOptions is not null;
        SimulationDescription = playbackOptions is null
            ? ""
            : $"SIMULAÇÃO · {playbackOptions.DisplayName} · {playbackOptions.Speed:G}× · nenhum comando é enviado ao hardware";
        if (playbackOptions is not null)
        {
            // O arquivo experimental padrão inicia em ~9% e atinge ~4,54%.
            // Estes limiares permitem percorrer N₂, espera estável e reoxigenação.
            SettingDOMin = 5.0;
            SettingDOMax = 95.0;
        }

        _runner.StateChanged += OnRunnerStateChanged;
        _runner.DataPointAdded += OnDataPointAdded;
        LivePoints.CollectionChanged += OnLivePointsCollectionChanged;
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
    private double _settingDOMin = 15.0;

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
    private double _settingStabilityDerivativeSpanSeconds = 6;

    [ObservableProperty]
    private double _settingStabilityDerivativeThreshold = 0.05;

    [ObservableProperty]
    private int _settingStabilityRequiredSamples = 5;

    /// <summary>DO points above DOMin at which the air is pre-staged through C (0 = at the floor).</summary>
    [ObservableProperty]
    private double _settingAirPrestageLeadPercent;

    [ObservableProperty]
    private double _settingPrestageFlowTolerance = 0.2;

    [ObservableProperty]
    private int _settingPrestageFlowStableSamples = 5;

    [ObservableProperty]
    private double _settingMaxPrestageSeconds = 180;

    /// <summary>Preflight: the operator's word that the N₂ is open at the source. Reset each time the dialog opens.</summary>
    [ObservableProperty]
    private bool _nitrogenSourceConfirmed;

    [ObservableProperty]
    private double _settingDefaultCeq = 100;

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

    partial void OnSelectedMatrixRowChanged(KlaMatrixRowViewModel? value)
    {
        if (value is not null && value.HasRunData && !IsRunning)
        {
            LoadMatrixRow(value);
        }
    }

    private KlaTestRunSummary? _currentlyEditingRun;
    private KlaMatrixRowViewModel? _currentlyEditingRow;

    [ObservableProperty]
    private bool _isAdvancedSettingsDialogOpen;

    [ObservableProperty]
    private bool _isStartSequenceDialogOpen;

    [ObservableProperty]
    private bool _isSequenceModePending = true;

    [ObservableProperty]
    private bool _isSequenceModeFromSelected;

    [ObservableProperty]
    private bool _isSequenceModeAll;

    [ObservableProperty]
    private string _sequenceSelectedRowDescription = "Nenhuma linha selecionada";

    [ObservableProperty]
    private bool _hasSelectedRowForSequence;

    [ObservableProperty]
    private string _sequenceQueueSummaryText = "";

    [ObservableProperty]
    private ObservableCollection<KlaConditionRowViewModel> _sequencePreviewQueue = new();

    private List<KlaTestCondition> _activeSequenceQueue = new();

    partial void OnIsSequenceModePendingChanged(bool value)
    {
        if (value) { UpdateSequencePreview(0); }
    }

    partial void OnIsSequenceModeFromSelectedChanged(bool value)
    {
        if (value) { UpdateSequencePreview(1); }
    }

    partial void OnIsSequenceModeAllChanged(bool value)
    {
        if (value) { UpdateSequencePreview(2); }
    }

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

    public string DisplayReviewKla => IsReviewOpen && CurrentAnalysis != null &&
        (CurrentAnalysis.DeterministicResult is null || CurrentAnalysis.DeterministicResult.KlaPerHour.HasValue) ? $"{ReviewKla:F1} h⁻¹" : "—";
    public string DisplayReviewR2 => IsReviewOpen && CurrentAnalysis != null ? $"R²: {ReviewR2:F4}" : "R²: —";
    public string DisplayReviewRmse => IsReviewOpen && CurrentAnalysis != null ? $"RMSE: {ReviewRmse:F4}" : "RMSE: —";
    public string DisplayReviewCi95 => IsReviewOpen && CurrentAnalysis != null &&
        (CurrentAnalysis.DeterministicResult is null ||
         (CurrentAnalysis.DeterministicResult.KlaPerHour.HasValue && CurrentAnalysis.DeterministicResult.ConditionalCi95Low.HasValue))
        ? $"IC 95% cond.: [{ReviewCi95Low:F1}; {ReviewCi95High:F1}]" : "IC 95%: [—; —]";
    public string DisplayReviewSens => IsReviewOpen && CurrentAnalysis != null ? $"[{ReviewSensLow:F1}; {ReviewSensHigh:F1}] h⁻¹" : "[—; —] h⁻¹";
    public string DisplayReviewQuality => !IsReviewOpen || CurrentAnalysis == null
        ? "Inativo"
        : ReviewQuality switch
        {
            DecisionQuality.Acceptable => "Aceitável",
            DecisionQuality.AcceptableWithWarning => "Condicionado às hipóteses",
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
        RefreshCommonState();
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
    public bool CanStartSequence => HasActiveTest && IsIdle && !IsLegacyRigTest && !IsAutomaticSession &&
        CurrentTest!.Status != KlaTestStatus.Completed &&
        !CurrentTest.Runs.Any(r => r.EffectiveOutcome.Restoration is KlaRestorationState.Pending or KlaRestorationState.Failed);

    /// <summary>An assay recorded before the A/B/C rig: reviewable, never continued (plan §3.5).</summary>
    public bool IsLegacyRigTest => CurrentTest?.IsLegacyRig == true;
    public string LegacyRigMessage => IsLegacyRigTest
        ? "Montagem anterior ao arranjo A/B/C — só leitura. Crie um ensaio novo para continuar no arranjo atual."
        : "";

    /// <summary>The wiring in force, from Documentação › Gás e válvulas — shown, never edited here.</summary>
    public string GasRigDescription =>
        $"Arranjo: {_settings.Current.GasRig.ToConfiguration().Describe()} (Documentação › Gás e válvulas)";

    public string DisplayDODerivative => _runner.CurrentDODerivative.HasValue
        ? $"{_runner.CurrentDODerivative.Value:+0.000;-0.000;0.000} %/s"
        : "—";
    public string DisplayStabilityProgress => CurrentTest is null
        ? "—"
        : $"{_runner.StabilityConfirmationCount}/{CurrentTest.Settings.StabilityRequiredSamples}";
    public string DisplayPrestageFlowDeviation => _runner.PrestageFlowDeviation.HasValue
        ? $"{_runner.PrestageFlowDeviation.Value:+0.00;-0.00;0.00} L/min"
        : "—";
    public string DisplayPrestageFlowProgress => CurrentTest is null
        ? "—"
        : $"{_runner.PrestageFlowStableCount}/{CurrentTest.Settings.PrestageFlowStableSamples}";
    public string DisplayPhase => Phase switch
    {
        RunPhase.Idle => "Inativo",
        RunPhase.Preflight => "Pré-voo",
        RunPhase.ClosingAllGas => "Fechando todas as válvulas",
        RunPhase.OpeningNitrogen => "Abrindo N₂ (B/C)",
        RunPhase.Deoxygenating => "Desoxigenando",
        RunPhase.PrestagingAir => "Ar por C · estabilizando",
        RunPhase.SwitchingToReactor => "Comutando para o reator (A)",
        RunPhase.Reoxygenating => "Reoxigenando",
        RunPhase.StoppingRun => "Fechando válvulas da corrida",
        RunPhase.Reviewing => "Em Revisão",
        RunPhase.Accepted => "Corrida Aceita",
        RunPhase.Rejected => "Corrida Rejeitada",
        RunPhase.Completed => "Teste Concluído",
        RunPhase.Faulted => "Falha / Interrompido",
        RunPhase.DivertingAir => "Desviando ar ao escape",
        RunPhase.MeasuringConsumption => "Medindo consumo respiratório",
        RunPhase.RestoringCultivation => "Confirmando retomada do cultivo",
        RunPhase.Aborting => "Cancelando com finalização",
        _ => Phase.ToString(),
    };

    public string FormattedTotalTime => TimeSpan.FromSeconds(TotalElapsedSeconds).ToString(@"hh\:mm\:ss");
    public string FormattedPhaseTime => TimeSpan.FromSeconds(PhaseElapsedSeconds).ToString(@"mm\:ss");

    // ── Commands ───────────────────────────────────────────────────────────────

    [RelayCommand]
    public void OpenCreateDialog()
    {
        if (IsRunning) return;
        NewTestName = $"Ensaio_kLa_{DateTime.Now:yyyy-MM-dd_HHmm}";
        RefreshAvailableMaps();
        IsCreateDialogOpen = true;
    }

    [RelayCommand]
    public void CloseCreateDialog()
    {
        IsCreateDialogOpen = false;
        if (CurrentTest is not null)
        {
            _isLoadingSettings = true;
            try
            {
                LoadCommonSettings(CurrentTest);
                SettingDOMin = CurrentTest.Settings.DOMinPercent; SettingDOMax = CurrentTest.Settings.DOMaxPercent;
                SettingDegassingAgitation = CurrentTest.Settings.DegassingAgitationRpm;
                SettingMaxDegassingMinutes = CurrentTest.Settings.MaxDegassingTimeMinutes;
                SettingMaxReoxygenationMinutes = CurrentTest.Settings.MaxReoxygenationTimeMinutes;
            }
            finally { _isLoadingSettings = false; }
        }
    }

    [RelayCommand]
    public void OpenLoadTestDialog()
    {
        if (IsRunning)
        {
            _dialogs.Confirm("Ensaio em execução", "Pare ou conclua a corrida antes de carregar outro ensaio.", "OK", "");
            return;
        }
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
    public void ImportTestFolder()
    {
        if (IsRunning)
        {
            _dialogs.Confirm("Ensaio em execução", "Pare ou conclua a corrida antes de importar outro ensaio.", "OK", "");
            return;
        }

        var selectedFolder = _files.ChooseFolder("Selecione a pasta completa do ensaio de kLa", _store.RootDirectory);
        if (string.IsNullOrWhiteSpace(selectedFolder))
        {
            return;
        }

        try
        {
            var importedFolder = _store.ImportTestFolder(selectedFolder);
            RefreshTestsList();
            LoadTest(importedFolder);
            IsLoadTestDialogOpen = false;
            StatusMessage = $"Ensaio completo importado para Testes-kLa/{importedFolder}. Selecione uma linha com curva para revisar.";
        }
        catch (Exception ex)
        {
            _dialogs.Confirm("Falha ao importar ensaio", ex.Message, "OK", "");
        }
    }

    [RelayCommand]
    public void CreateNewTest()
    {
        if (IsRunning)
        {
            _dialogs.Confirm("Ensaio em execução", "Pare ou conclua a corrida antes de criar outro ensaio.", "OK", "");
            return;
        }
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

        if (!TryBuildSettings(out var settings, out var settingsError))
        {
            _dialogs.Confirm("Parâmetros inválidos", settingsError, "OK", "");
            return;
        }

        KlaMapReference? mapRef = null;
        IReadOnlyList<KlaTestCondition>? initialConditions = null;

        if (SelectedMapForImport is not null)
        {
            (mapRef, initialConditions) = KlaMapImportHelper.ImportConditionsFromMap(SelectedMapForImport, 1);
        }

        if (!TryBuildDefinition(settings, initialConditions, out var definition, out var definitionError))
        {
            StatusMessage = definitionError;
            return;
        }
        var doc = _store.CreateTest(NewTestName, definition, IsSingleCapture ? null : mapRef);
        _settings.Update(s => s with { KlaTest = settings });
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
        if (IsRunning)
        {
            _dialogs.Confirm("Ensaio em execução", "Pare ou conclua a corrida antes de carregar outro ensaio.", "OK", "");
            return;
        }
        var doc = _store.LoadTest(folderName);
        if (doc is null)
        {
            _dialogs.Confirm("Erro", $"Não foi possível carregar o teste '{folderName}'.", "OK", "");
            return;
        }

        CurrentTest = doc;
        LoadCommonSettings(doc);
        SelectedMatrixRow = null;
        _currentlyEditingRun = null;
        _currentlyEditingRow = null;
        CurrentAnalysis = null;
        IsReviewOpen = false;
        LivePoints.Clear();
        InstantaneousKlaSeries.Clear();
        LogLinearSeries.Clear();
        _isLoadingSettings = true;
        try
        {
            SettingDOMin = doc.Settings.DOMinPercent;
            SettingDOMax = doc.Settings.DOMaxPercent;
            SettingDegassingAgitation = doc.Settings.DegassingAgitationRpm;
            SettingSmoothingWindow = doc.Settings.SmoothingWindowSize;
            SettingMaxDegassingMinutes = doc.Settings.MaxDegassingTimeMinutes;
            SettingMaxReoxygenationMinutes = doc.Settings.MaxReoxygenationTimeMinutes;
            SettingStabilityDerivativeSpanSeconds = doc.Settings.StabilityDerivativeSpanSeconds;
            SettingStabilityDerivativeThreshold = doc.Settings.StabilityDerivativeThresholdPercentPerSecond;
            SettingStabilityRequiredSamples = doc.Settings.StabilityRequiredSamples;
            SettingAirPrestageLeadPercent = doc.Settings.AirPrestageLeadPercent;
            SettingPrestageFlowTolerance = doc.Settings.PrestageFlowToleranceLpm;
            SettingPrestageFlowStableSamples = doc.Settings.PrestageFlowStableSamples;
            SettingMaxPrestageSeconds = doc.Settings.MaxPrestageSeconds;
            SettingDefaultCeq = doc.Settings.DefaultCeqPercent;
            AutoAcceptRuns = doc.Settings.AutoAcceptRuns;
            SettingAutoLinearStartPercent = doc.Settings.AutoLinearStartPercent;
            SettingAutoLinearEndPercent = doc.Settings.AutoLinearEndPercent;
        }
        finally
        {
            _isLoadingSettings = false;
        }
        OnPropertyChanged(nameof(IsLegacyRigTest));
        OnPropertyChanged(nameof(LegacyRigMessage));
        OnPropertyChanged(nameof(GasRigDescription));
        if (doc.IsLegacyRig)
        {
            StatusMessage = LegacyRigMessage;
        }

        Conditions.Clear();
        foreach (var c in doc.Conditions)
        {
            Conditions.Add(new KlaConditionRowViewModel(c));
        }

        RefreshConditionsList();

        _runner.PrepareTest(doc);
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
        if (row is null || CurrentTest is null || IsRunning)
        {
            return;
        }

        if (!row.HasRunData)
        {
            StatusMessage = $"Condição #{row.OrderIndex} ({row.AgitationRpm:F0} rpm, {row.AirflowLpm:F2} L/min) ainda não possui dados gravados.";
            return;
        }

        SelectedMatrixRow = row;
        _currentlyEditingRow = row;

        // 1. Locate matching run in doc
        var run = FindBestRun(CurrentTest.Runs, row.ConditionId, row.ReplicateIndex)
               ?? FindBestRun(CurrentTest.Runs, row.ConditionId, null);

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
            StatusMessage = $"Não há dados gravados para a réplica #{row.ReplicateIndex} ({row.AgitationRpm:F0} rpm, {row.AirflowLpm:F2} L/min).";
            return;
        }

        LoadStoredRun(run, row);
    }

    private void LoadStoredRun(KlaTestRunSummary run, KlaMatrixRowViewModel? row)
    {
        if (CurrentTest is null || IsRunning) return;
        _currentlyEditingRow = row;
        _currentlyEditingRun = run;

        // 2. Load Raw Data Points from CSV
        var rawPoints = _store.LoadRunRawData(CurrentTest.FolderName, run.FolderName);
        if (rawPoints.Count == 0)
        {
            StatusMessage = $"O arquivo de dados brutos da corrida '{run.FolderName}' está vazio ou não foi encontrado.";
            return;
        }

        LivePoints.Clear();
        foreach (var p in rawPoints)
        {
            LivePoints.Add(p);
        }

        // Loading a historical revision is read-only; it never recalculates or saves it.
        var analysis = _store.LoadRunAnalysis(CurrentTest.FolderName, run.FolderName);
        if (analysis is not null) LoadReviewAnalysis(analysis);
        else OpenReviewDrawer();
        StatusMessage = $"Corrida carregada: {run.AgitationRpm:F0} rpm · {run.AirflowLpm:F2} L/min · réplica {run.ReplicateNumber} · tentativa {run.AttemptNumber}";
    }

    [RelayCommand]
    public void AddManualCondition()
    {
        if (CurrentTest is null || IsRunning || IsAutomaticSession || CurrentTest.EffectiveCaptureMode == KlaCaptureMode.Single)
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

        try
        {
            var definition = KlaAssayDefinition.FromDocument(CurrentTest) with
            { Conditions = CurrentTest.Conditions.Append(cond).Select(KlaAssayCondition.From).ToImmutableArray() };
            definition.Validate();
        }
        catch (ArgumentException ex) { StatusMessage = ex.Message; return; }

        CurrentTest.Conditions.Add(cond);
        _store.SaveConditionsTable(CurrentTest.FolderName, CurrentTest.Conditions);
        Conditions.Add(new KlaConditionRowViewModel(cond));
        RefreshConditionsList();
        UpdateUiState();
    }

    [RelayCommand]
    public void RemoveMatrixRow(KlaMatrixRowViewModel? row)
    {
        if (row is null || CurrentTest is null || IsAutomaticSession)
        {
            return;
        }

        if (IsRunning)
        {
            _dialogs.Confirm("Aviso", "Não é possível remover condições enquanto um ensaio está em execução.", "OK", "", isDanger: false);
            return;
        }

        if (!_dialogs.Confirm("Excluir Condição", $"Deseja realmente remover a condição #{row.OrderIndex} ({row.AgitationRpm:F0} rpm, {row.AirflowLpm:F2} L/min) da matriz?", "Excluir", "Cancelar", isDanger: true))
        {
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
        StatusMessage = $"Condição #{row.OrderIndex} removida da matriz.";
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
            _dialogs.Confirm("Aviso", "Não é possível remover condições enquanto um ensaio está em execução.", "OK", "", isDanger: false);
            return;
        }

        if (!_dialogs.Confirm("Excluir Condição", $"Deseja realmente remover a condição #{row.OrderIndex} ({row.AgitationRpm:F0} rpm, {row.AirflowLpm:F2} L/min)?", "Excluir", "Cancelar", isDanger: true))
        {
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
        StatusMessage = $"Condição #{row.OrderIndex} removida da matriz.";
    }

    [RelayCommand]
    public void OpenStartSequenceDialog()
    {
        if (CurrentTest is null || Conditions.Count == 0)
        {
            StatusMessage = "Crie ou importe um ensaio com condições experimentais antes de iniciar a sequência.";
            return;
        }

        if (IsRunning)
        {
            _dialogs.Confirm("Aviso", "Já existe um ensaio em andamento.", "OK", "", isDanger: false);
            return;
        }

        HasSelectedRowForSequence = SelectedMatrixRow is not null;
        SequenceSelectedRowDescription = SelectedMatrixRow is not null
            ? $"Condição #{SelectedMatrixRow.OrderIndex} ({SelectedMatrixRow.AgitationRpm:F0} rpm, {SelectedMatrixRow.AirflowLpm:F2} L/min)"
            : "Nenhuma linha selecionada na matriz";

        var pendingCount = Conditions.Count(c => c.Model.AcceptedReplicates < c.Model.RequestedReplicates);
        if (pendingCount > 0)
        {
            IsSequenceModePending = true;
            IsSequenceModeFromSelected = false;
            IsSequenceModeAll = false;
            UpdateSequencePreview(0);
        }
        else if (HasSelectedRowForSequence)
        {
            IsSequenceModePending = false;
            IsSequenceModeFromSelected = true;
            IsSequenceModeAll = false;
            UpdateSequencePreview(1);
        }
        else
        {
            IsSequenceModePending = false;
            IsSequenceModeFromSelected = false;
            IsSequenceModeAll = true;
            UpdateSequencePreview(2);
        }

        NitrogenSourceConfirmed = CurrentTest.NitrogenSourceConfirmedUtc is not null;
        OnPropertyChanged(nameof(GasRigDescription));
        IsStartSequenceDialogOpen = true;
    }

    [RelayCommand]
    public void CloseStartSequenceDialog()
    {
        IsStartSequenceDialogOpen = false;
    }

    public void UpdateSequencePreview(int mode)
    {
        SequencePreviewQueue.Clear();

        IEnumerable<KlaConditionRowViewModel> targetList;
        if (mode == 0)
        {
            // Pending only
            targetList = Conditions.Where(c => c.Model.AcceptedReplicates < c.Model.RequestedReplicates);
        }
        else if (mode == 1)
        {
            // From selected
            var startIdx = SelectedMatrixRow is not null ? SelectedMatrixRow.OrderIndex - 1 : 0;
            targetList = Conditions.Where(c => c.Model.OrderIndex >= startIdx);
        }
        else
        {
            // All
            targetList = Conditions;
        }

        foreach (var c in targetList)
        {
            SequencePreviewQueue.Add(c);
        }

        SequenceQueueSummaryText = SequencePreviewQueue.Count == 1
            ? "1 condição na fila de execução."
            : $"{SequencePreviewQueue.Count} condições na fila de execução.";
    }

    [RelayCommand]
    public async Task ConfirmStartSequenceAsync()
    {
        if (CurrentTest is null || SequencePreviewQueue.Count == 0)
        {
            StatusMessage = "Nenhuma condição selecionada para execução.";
            return;
        }

        if (CurrentTest.EffectiveProtocol == KlaAssayProtocol.Abiotic && !NitrogenSourceConfirmed)
        {
            StatusMessage = "Confirme que o N₂ está aberto na fonte antes de iniciar.";
            return;
        }
        if (!EnsureNitrogenSourceConfirmed())
        {
            return;
        }

        _activeSequenceQueue = SequencePreviewQueue.Select(c => c.Model).ToList();
        _queueOwnerTestId = CurrentTest.TestId;
        IsStartSequenceDialogOpen = false;
        if (CurrentTest.SequenceLimits is not null)
        {
            await ContinueQueueAsync();
            return;
        }

        var firstCondition = _activeSequenceQueue.FirstOrDefault();
        if (firstCondition is not null)
        {
            LivePoints.Clear();
            InstantaneousKlaSeries.Clear();
            LogLinearSeries.Clear();

            var nextRep = firstCondition.CompletedReplicates + 1;
            StatusMessage = $"Iniciando sequência: condição #{firstCondition.OrderIndex + 1} ({firstCondition.AgitationRpm:F0} rpm, {firstCondition.AirflowLpm:F2} L/min - réplica {nextRep}/{firstCondition.RequestedReplicates})...";
            await StartRunSafelyAsync(firstCondition, nextRep);
        }
    }

    [RelayCommand]
    public async Task StartSequenceAsync()
    {
        if (CurrentTest is null || IsRunning)
        {
            return;
        }

        if (!EnsureNitrogenSourceConfirmed())
        {
            return;
        }
        UpdateSequencePreview(IsSequenceModePending ? 0 : (IsSequenceModeFromSelected ? 1 : 2));
        await ConfirmStartSequenceAsync();
    }

    [RelayCommand]
    public async Task StartConditionRunAsync(KlaConditionRowViewModel? row)
    {
        if (row is null || CurrentTest is null)
        {
            return;
        }

        var cond = row.Model;
        if (!EnsureNitrogenSourceConfirmed())
        {
            return;
        }

        _store.SaveConditionsTable(CurrentTest.FolderName, CurrentTest.Conditions);
        var nextRep = CurrentTest.SequenceLimits is null ? cond.CompletedReplicates + 1
            : KlaSequence.Pending(CurrentTest, new[] { cond }).FirstOrDefault()?.ReplicateNumber ?? 1;

        LivePoints.Clear();
        InstantaneousKlaSeries.Clear();
        LogLinearSeries.Clear();

        await StartRunSafelyAsync(cond, nextRep);
    }

    /// <summary>
    /// Preflight outside the sequence dialog (a row's ▶, the direct start): asks once per test
    /// and records the answer. The dialog path has its own checkbox and never nests a prompt.
    /// </summary>
    private bool EnsureNitrogenSourceConfirmed()
    {
        if (CurrentTest is null)
        {
            return false;
        }
        if (CurrentTest.EffectiveProtocol == KlaAssayProtocol.Biotic)
        {
            if (!NitrogenIsolationConfirmed)
            {
                StatusMessage = "Confirme que o N₂ está fechado e fisicamente isolado na fonte.";
                return false;
            }
            CurrentTest.NitrogenIsolationConfirmedUtc = DateTimeOffset.UtcNow;
            _store.SaveTestManifest(CurrentTest);
            return true;
        }
        if (CurrentTest.NitrogenSourceConfirmedUtc is not null || NitrogenSourceConfirmed)
        {
            RecordNitrogenSourceConfirmation();
            return true;
        }
        if (!_dialogs.Confirm(
                "Pré-voo: N₂ na fonte",
                "A desoxigenação entra pela válvula B (saída B/C), mas a fonte de N₂ é manual e o app não a enxerga. Confirme que o N₂ está aberto na fonte.",
                "Confirmo", "Cancelar"))
        {
            return false;
        }
        NitrogenSourceConfirmed = true;
        RecordNitrogenSourceConfirmation();
        return true;
    }

    /// <summary>Writes the preflight confirmation to the manifest once; the runner logs it with each run.</summary>
    private void RecordNitrogenSourceConfirmation()
    {
        if (CurrentTest is null || CurrentTest.NitrogenSourceConfirmedUtc is not null)
        {
            return;
        }

        CurrentTest.NitrogenSourceConfirmedUtc = DateTimeOffset.UtcNow;
        _store.SaveTestManifest(CurrentTest);
    }

    [RelayCommand]
    public async Task StopRunAsync()
    {
        await _runner.StopRunAndReviewAsync("Parada manual pelo operador");
    }

    [RelayCommand]
    public void ApplyLiveSettings()
    {
        if (CurrentTest is null || IsAutomaticSession)
        {
            return;
        }

        if (!TryBuildSettings(out var newSettings, out var error))
        {
            StatusMessage = error;
            return;
        }

        if (CurrentTest.ProtocolSettings is not null && !IsRunning && !IsCreateDialogOpen)
        {
            var candidate = KlaAssayDefinition.FromDocument(CurrentTest) with { Settings = newSettings, ProtocolSettings = BuildProtocolSettings(),
                Context = BuildMeasurementContext(), SequenceLimits = BuildSequenceLimits() };
            try { candidate.Validate(requireConditions: false); }
            catch (ArgumentException ex) { StatusMessage = ex.Message; return; }
            CurrentTest.ProtocolSettings = candidate.ProtocolSettings;
            CurrentTest.Context = candidate.Context;
            CurrentTest.SequenceLimits = candidate.SequenceLimits;
        }
        CurrentTest.Settings = newSettings;
        _runner.UpdateLiveSettings(newSettings);
        _store.SaveTestManifest(CurrentTest);
        _settings.Update(s => s with { KlaTest = newSettings });
        OnPropertyChanged(nameof(DisplayStabilityProgress));
        OnPropertyChanged(nameof(DisplayPrestageFlowProgress));
    }

    partial void OnSettingDOMinChanged(double value) => AutoApplyLiveSettings();
    partial void OnSettingDOMaxChanged(double value) => AutoApplyLiveSettings();
    partial void OnSettingDegassingAgitationChanged(double value) => AutoApplyLiveSettings();
    partial void OnSettingSmoothingWindowChanged(int value) => AutoApplyLiveSettings();
    partial void OnSettingMaxDegassingMinutesChanged(double value) => AutoApplyLiveSettings();
    partial void OnSettingMaxReoxygenationMinutesChanged(double value) => AutoApplyLiveSettings();
    partial void OnSettingStabilityDerivativeSpanSecondsChanged(double value) => AutoApplyLiveSettings();
    partial void OnSettingStabilityDerivativeThresholdChanged(double value) => AutoApplyLiveSettings();
    partial void OnSettingStabilityRequiredSamplesChanged(int value) => AutoApplyLiveSettings();
    partial void OnSettingAirPrestageLeadPercentChanged(double value) => AutoApplyLiveSettings();
    partial void OnSettingPrestageFlowToleranceChanged(double value) => AutoApplyLiveSettings();
    partial void OnSettingPrestageFlowStableSamplesChanged(int value) => AutoApplyLiveSettings();
    partial void OnSettingMaxPrestageSecondsChanged(double value) => AutoApplyLiveSettings();
    partial void OnSettingDefaultCeqChanged(double value) => AutoApplyLiveSettings();
    partial void OnSettingAutoLinearStartPercentChanged(double value) => AutoApplyLiveSettings();
    partial void OnSettingAutoLinearEndPercentChanged(double value) => AutoApplyLiveSettings();

    private void AutoApplyLiveSettings()
    {
        if (_isLoadingSettings || IsCreateDialogOpen || IsRunning)
        {
            return;
        }

        if (!TryBuildSettings(out var newSettings, out _))
        {
            return;
        }

        _settings.Update(s => s with { KlaTest = newSettings });

        if (CurrentTest is not null)
        {
            ApplyLiveSettings();
        }
    }

    partial void OnAutoAcceptRunsChanged(bool value)
    {
        if (CurrentTest is not null && !IsAutomaticSession && !_isLoadingSettings && !IsCreateDialogOpen && CurrentTest.ProtocolSettings is null)
        {
            CurrentTest.Settings = CurrentTest.Settings with { AutoAcceptRuns = value };
            _runner.UpdateLiveSettings(CurrentTest.Settings);
            _store.SaveTestManifest(CurrentTest);
        }
    }

    private bool TryBuildSettings(out KlaTestSettings settings, out string error)
    {
        settings = CurrentTest?.Settings ?? new KlaTestSettings();
        error = "";
        if (!double.IsFinite(SettingDOMin) || !double.IsFinite(SettingDOMax) ||
            SettingDOMin < 0 || SettingDOMin >= SettingDOMax)
        {
            error = "DO de desligamento do N₂ deve ser menor que DO final, dentro de 0–110%.";
            return false;
        }
        if (!double.IsFinite(SettingDegassingAgitation) || SettingDegassingAgitation <= 0 ||
            SettingSmoothingWindow is < 1 or > 101 || SettingSmoothingWindow % 2 == 0 ||
            !double.IsFinite(SettingMaxDegassingMinutes) || SettingMaxDegassingMinutes <= 0 ||
            !double.IsFinite(SettingMaxReoxygenationMinutes) || SettingMaxReoxygenationMinutes <= 0)
        {
            error = "Rotação, tempos máximos e janela ímpar de suavização (1–101) devem ser válidos.";
            return false;
        }
        if (!double.IsFinite(SettingStabilityDerivativeSpanSeconds) || SettingStabilityDerivativeSpanSeconds <= 0 ||
            !double.IsFinite(SettingStabilityDerivativeThreshold) || SettingStabilityDerivativeThreshold <= 0 ||
            SettingStabilityRequiredSamples is < 1 or > 100)
        {
            error = "Revise janela, limiar e confirmações da estabilidade da sonda no piso.";
            return false;
        }
        if (!double.IsFinite(SettingAirPrestageLeadPercent) || SettingAirPrestageLeadPercent < 0 ||
            SettingDOMin + SettingAirPrestageLeadPercent >= SettingDOMax)
        {
            error = "A antecipação da pré-estabilização deve ser ≥ 0 e, somada ao DO mínimo, ficar abaixo do DO final.";
            return false;
        }
        if (!double.IsFinite(SettingPrestageFlowTolerance) || SettingPrestageFlowTolerance <= 0 || SettingPrestageFlowTolerance > 10 ||
            SettingPrestageFlowStableSamples is < 1 or > 100 ||
            !double.IsFinite(SettingMaxPrestageSeconds) || SettingMaxPrestageSeconds <= 0)
        {
            error = "Revise tolerância de vazão, confirmações e tempo máximo da pré-estabilização do ar por C.";
            return false;
        }
        if (!double.IsFinite(SettingDefaultCeq) || SettingDefaultCeq <= 0 ||
            !double.IsFinite(SettingAutoLinearStartPercent) || !double.IsFinite(SettingAutoLinearEndPercent) ||
            SettingAutoLinearStartPercent < 0 || SettingAutoLinearEndPercent > 100 ||
            SettingAutoLinearStartPercent >= SettingAutoLinearEndPercent)
        {
            error = "Referência legada e faixa automática devem conter valores válidos.";
            return false;
        }

        settings = settings with
        {
            DOMinPercent = SettingDOMin,
            DOMaxPercent = SettingDOMax,
            DegassingAgitationRpm = SettingDegassingAgitation,
            SmoothingWindowSize = SettingSmoothingWindow,
            MaxDegassingTimeMinutes = SettingMaxDegassingMinutes,
            MaxReoxygenationTimeMinutes = SettingMaxReoxygenationMinutes,
            StabilityDerivativeSpanSeconds = SettingStabilityDerivativeSpanSeconds,
            StabilityDerivativeThresholdPercentPerSecond = SettingStabilityDerivativeThreshold,
            StabilityRequiredSamples = SettingStabilityRequiredSamples,
            AirPrestageLeadPercent = SettingAirPrestageLeadPercent,
            PrestageFlowToleranceLpm = SettingPrestageFlowTolerance,
            PrestageFlowStableSamples = SettingPrestageFlowStableSamples,
            MaxPrestageSeconds = SettingMaxPrestageSeconds,
            DefaultCeqPercent = SettingDefaultCeq,
            AutoAcceptRuns = AutoAcceptRuns,
            AutoLinearStartPercent = SettingAutoLinearStartPercent,
            AutoLinearEndPercent = SettingAutoLinearEndPercent,
        };
        return true;
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
        if (IsAutomaticSession) return;
        if (!TryBuildSettings(out var settings, out var error))
        {
            StatusMessage = error;
            return;
        }
        if (CurrentTest is not null && CurrentTest.ProtocolSettings is not null && !IsRunning)
        {
            var candidate = KlaAssayDefinition.FromDocument(CurrentTest) with { Settings = settings, ProtocolSettings = BuildProtocolSettings(),
                Context = BuildMeasurementContext(), SequenceLimits = BuildSequenceLimits() };
            try { candidate.Validate(requireConditions: false); }
            catch (ArgumentException ex) { StatusMessage = ex.Message; return; }
            CurrentTest.ProtocolSettings = candidate.ProtocolSettings;
            CurrentTest.Context = candidate.Context; CurrentTest.SequenceLimits = candidate.SequenceLimits;
        }
        _settings.Update(s => s with { KlaTest = settings });
        ApplyLiveSettings();
        IsAdvancedSettingsDialogOpen = false;
    }

    [RelayCommand]
    public void RecomputeReviewAnalysis()
    {
        if (IsAutomaticSession) { ReviewMessage = "A sessão automática preserva a análise e a decisão gravadas."; return; }
        if (_isRecomputing) return;
        _isRecomputing = true;
        try { if (TryRecomputeDeterministic()) return; }
        finally { _isRecomputing = false; }
        if (CurrentTest?.EffectiveProtocol == KlaAssayProtocol.Biotic || CurrentTest?.ProtocolSettings is not null)
        {
            CurrentAnalysis = null;
            ReviewMessage = "Não há definição/eventos confirmados suficientes para analisar pelo novo núcleo.";
            return;
        }
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
            var dos = recovery.Select(p => p.DOFiltered).ToList();

            var ceqPoints = recovery.Where(p => p.RelativeSeconds >= ReviewCeqTStart && p.RelativeSeconds <= ReviewCeqTEnd).ToList();

            // 1. Ceq Fit / Override
            var ceqResult = _analysisEngine.EstimateCeq(
                ceqPoints.Select(p => p.RelativeSeconds).ToList(),
                ceqPoints.Select(p => p.DOFiltered).ToList(),
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
        if (!CanDecideRun) { ReviewMessage = "A decisão exige finalização física confirmada."; return; }
        if (CurrentAnalysis is null)
        {
            RecomputeReviewAnalysis();
        }

        if (CurrentAnalysis is null)
        {
            return;
        }

        if (CurrentAnalysis.Quality == DecisionQuality.Inconclusive ||
            CurrentAnalysis.Outcome?.KlaQuality is KlaScientificQuality.Inconclusive or
                KlaScientificQuality.NotEvaluated or KlaScientificQuality.NotApplicable)
        {
            _dialogs.Confirm("Análise inconclusiva", CurrentAnalysis.RejectionReason ?? "Ajuste a análise ou rejeite a corrida.", "OK", "");
            return;
        }

        if (_runner.IsInReview || (_runner.CurrentCondition is not null && _currentlyEditingRun is null))
        {
            await _runner.AcceptRunAsync(CurrentAnalysis);
            IsReviewOpen = false;
            RefreshConditionsList();

            if (CurrentTest?.SequenceLimits is not null)
            {
                NotifyQueue();
                if (AutoAdvanceQueue && HasQueuedRuns) await ContinueQueueAsync();
                else StatusMessage = QueueStatus;
                UpdateUiState();
                return;
            }
            // Auto-advance to the next pending condition/replicate in sequence
            _activeSequenceQueue.RemoveAll(c => c.ConditionId == _runner.CurrentCondition?.ConditionId && c.AcceptedReplicates >= c.RequestedReplicates);
            var nextCondition = _activeSequenceQueue.FirstOrDefault()
                ?? (AutoAcceptRuns ? Conditions.FirstOrDefault(c => c.Model.AcceptedReplicates < c.Model.RequestedReplicates)?.Model : null);

            if (nextCondition is not null && CurrentTest?.EffectiveProtocol == KlaAssayProtocol.Biotic)
            {
                StatusMessage = "Cultivo retomado. Confirme o próximo teste na matriz quando desejar continuar.";
                _activeSequenceQueue.Clear();
            }
            else if (nextCondition is not null && CurrentTest is not null)
            {
                var cond = nextCondition;
                var nextRep = cond.CompletedReplicates + 1;

                LivePoints.Clear();
                InstantaneousKlaSeries.Clear();
                LogLinearSeries.Clear();

                StatusMessage = $"Iniciando automaticamente condição #{cond.OrderIndex + 1} ({cond.AgitationRpm:F0} rpm, {cond.AirflowLpm:F2} L/min - réplica {nextRep}/{cond.RequestedReplicates})...";
                await StartRunSafelyAsync(cond, nextRep);
            }
            else
            {
                _activeSequenceQueue.Clear();
                StatusMessage = "Todas as condições da sequência foram concluídas com sucesso!";
                _dialogs.Confirm("Sequência Concluída", "Todas as condições da sequência experimental foram concluídas com sucesso. O ensaio pode ser finalizado.", "OK", "", isDanger: false);
            }

            UpdateUiState();
        }
        else if (_currentlyEditingRun is not null && CurrentTest is not null)
        {
            // Saving edited analysis on an existing / imported run
            var latestRevision = _store.LoadRunAnalysis(CurrentTest.FolderName, _currentlyEditingRun.FolderName)?.RevisionNumber ?? 0;
            CurrentAnalysis.RevisionNumber = Math.Max(CurrentAnalysis.RevisionNumber, latestRevision) + 1;
            CurrentAnalysis.AnalyzedUtc = DateTimeOffset.UtcNow;
            CurrentAnalysis.Outcome = (CurrentAnalysis.Outcome ?? KlaRunOutcome.FromLegacy(CurrentAnalysis.Quality)) with
            {
                OperatorDecision = KlaOperatorDecision.Accepted,
                Restoration = _currentlyEditingRun.EffectiveOutcome.Restoration,
                RestorationReason = _currentlyEditingRun.EffectiveOutcome.RestorationReason,
            };
            _store.SaveRunAnalysis(CurrentTest.FolderName, _currentlyEditingRun.FolderName, CurrentAnalysis);

            var existingRunIndex = CurrentTest.Runs.FindIndex(r => r.RunId == _currentlyEditingRun.RunId || r.FolderName == _currentlyEditingRun.FolderName);
            var updatedRunSummary = _currentlyEditingRun with
            {
                Outcome = CurrentAnalysis.Outcome,
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
            foreach (var condition in CurrentTest.Conditions) KlaSequence.RefreshCounters(CurrentTest, condition);
            _store.SaveConditionsTable(CurrentTest.FolderName, CurrentTest.Conditions);
            _store.SaveTestManifest(CurrentTest);

            if (_currentlyEditingRow is not null)
            {
                _currentlyEditingRow.KlaPerHour = CurrentAnalysis.KlaPerHour;
                _currentlyEditingRow.AnalysisR2 = CurrentAnalysis.AnalysisR2;
                _currentlyEditingRow.Status = ConditionStatus.Completed;
                _currentlyEditingRow.LoadedRunPhase = RunPhase.Accepted;
                _currentlyEditingRow.NotifyChanged();
            }

            RefreshConditionsList();
            StatusMessage = $"Revisão do ensaio '{_currentlyEditingRun.FolderName}' salva com sucesso (kLa = {CurrentAnalysis.KlaPerHour:F1} h⁻¹, R² = {CurrentAnalysis.AnalysisR2:F4}).";
        }
    }

    [RelayCommand]
    public async Task RejectCurrentRunAsync()
    {
        if (!CanDecideRun) return;
        var reason = ReviewRejectionReason ?? "Rejeitado pelo operador na revisão.";
        if (_currentlyEditingRun is not null && CurrentTest is not null)
        {
            var folder = _currentlyEditingRun.FolderName;
            var revision = CurrentAnalysis ?? new KlaAnalysisRevision { Quality = DecisionQuality.Inconclusive };
            revision.RevisionNumber = (_store.LoadRunAnalysis(CurrentTest.FolderName, folder)?.RevisionNumber ?? 0) + 1;
            revision.AnalyzedUtc = DateTimeOffset.UtcNow;
            revision.RejectionReason = reason;
            revision.Outcome = (revision.Outcome ?? new()) with
            {
                OperatorDecision = KlaOperatorDecision.Rejected,
                Restoration = _currentlyEditingRun.EffectiveOutcome.Restoration,
                RestorationReason = _currentlyEditingRun.EffectiveOutcome.RestorationReason,
            };
            _store.SaveRunAnalysis(CurrentTest.FolderName, folder, revision);
            var index = CurrentTest.Runs.FindIndex(r => r.FolderName == folder);
            if (index >= 0) CurrentTest.Runs[index] = _currentlyEditingRun with { Phase = RunPhase.Rejected, Outcome = revision.Outcome };
            var condition = CurrentTest.Conditions.FirstOrDefault(c => c.ConditionId == _currentlyEditingRun.ConditionId);
            if (condition is not null)
            {
                KlaSequence.RefreshCounters(CurrentTest, condition);
                _store.SaveConditionsTable(CurrentTest.FolderName, CurrentTest.Conditions);
            }
            _store.SaveTestManifest(CurrentTest);
            await _store.FlushAsync();
            IsReviewOpen = false; RefreshConditionsList(); UpdateUiState();
            return;
        }
        await _runner.RejectRunAsync(reason);
        IsReviewOpen = false;
        RefreshConditionsList();
    }

    [RelayCommand]
    public async Task RepeatCurrentRunAsync()
    {
        if (!CanRepeatLiveRun) return;
        IsReviewOpen = false;
        try { await _runner.RepeatRunAsync(); }
        catch (InvalidOperationException ex) { StatusMessage = ex.Message; }
        NotifyQueue();
    }

    [RelayCommand]
    public async Task CompleteTestAsync()
    {
        StopQueue();
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
        StopQueue();
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
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.HasShutdownStarted && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(OnRunnerStateChanged);
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
        OnPropertyChanged(nameof(DisplayDODerivative));
        OnPropertyChanged(nameof(DisplayStabilityProgress));
        OnPropertyChanged(nameof(DisplayPrestageFlowDeviation));
        OnPropertyChanged(nameof(DisplayPrestageFlowProgress));

        // A new run id while running means a new curve: the live series must not carry the
        // previous run's points under it, whichever path started it (sequence, a row's ▶,
        // "repetir" from the review, the automatic advance).
        if (_runner.IsRunning && _runner.CurrentRun is { } run && run.RunId != _liveRunId)
        {
            _currentlyEditingRun = null;
            _currentlyEditingRow = null;
            CurrentAnalysis = null;
            _liveRunId = run.RunId;
            ResetLiveSeries();
        }

        if (_runner.IsInReview && !IsReviewOpen)
        {
            OpenReviewDrawer();
        }

        UpdateUiState();
    }

    /// <summary>
    /// Index of the first reoxygenation point in <see cref="LivePoints"/>, or -1. Kept so the live
    /// diagnostic series are built from a slice rather than a <c>Where</c> over every point on every
    /// frame (§E). Reset whenever the chart is cleared.
    /// </summary>
    private int _reoxygenationStart = -1;
    private Guid? _liveRunId;

    /// <summary>Empties the live chart and its derived series before the next run draws.</summary>
    private void ResetLiveSeries()
    {
        if (LivePoints.Count > 0)
        {
            LivePoints.Clear();
        }
        if (InstantaneousKlaSeries.Count > 0)
        {
            InstantaneousKlaSeries.Clear();
        }
        if (LogLinearSeries.Count > 0)
        {
            LogLinearSeries.Clear();
        }
    }
    private int _reoxygenationCount;
    private int _derivedSeriesComputedAt;

    /// <summary>
    /// The instantaneous-kLa and log-linear series are diagnostics: recomputing them (an O(n·window)
    /// smoothing and an OLS) on every frame was work repeated ~1 Hz for a chart drawn once a
    /// second. They are refreshed every this many reoxygenation points during the run; the review
    /// recomputes them exactly.
    /// </summary>
    private const int DerivedSeriesRecomputeEvery = 5;

    private void OnDataPointAdded(KlaRawDataPoint point)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnDataPointAdded(point));
            return;
        }
        LivePoints.Add(point);
        if (point.Phase != RunPhase.Reoxygenating)
        {
            return;
        }

        if (_reoxygenationStart < 0)
        {
            _reoxygenationStart = LivePoints.Count - 1;
        }
        _reoxygenationCount++;

        if (_reoxygenationCount >= 5 && _reoxygenationCount - _derivedSeriesComputedAt >= DerivedSeriesRecomputeEvery)
        {
            _derivedSeriesComputedAt = _reoxygenationCount;
            RecomputeLiveDerivedSeries();
        }
    }

    private void RecomputeLiveDerivedSeries()
    {
        if (CurrentTest?.ProtocolSettings is not null)
        {
            InstantaneousKlaSeries.Clear(); LogLinearSeries.Clear();
            if (CurrentRun?.Definition is { } definition && CurrentRun.GasEvents.Any(e => e.Kind == KlaGasEventKind.GasOnConfirmed) &&
                _analysisEngine is IKlaDeterministicAnalysisEngine deterministic)
            {
                var request = KlaDeterministicRequestFactory.FromRun(definition, LivePoints.ToArray(), CurrentRun.GasEvents);
                var provisional = deterministic.AnalyzeDeterministic(request);
                foreach (var d in provisional.RateDiagnostics.Where(d => d.EquilibriumRatioPerHour.HasValue))
                    InstantaneousKlaSeries.Add(new(request.Samples[d.Index].Seconds, request.Samples[d.Index].CalibratedDoPercent,
                        request.Samples[d.Index].CalibratedDoPercent, d.EquilibriumRatioPerHour, d.EquilibriumRatioPerHour));
                if (provisional.Equilibrium.Percent is { } ceqDeterministic && provisional.SelectedWindow is { } selected)
                    foreach (var p in _analysisEngine.ComputeLogLinearPoints(request.Samples.Select(p => p.Seconds).ToArray(), request.Samples.Select(p => p.CalibratedDoPercent).ToArray(),
                        ceqDeterministic, request.Samples[selected.Window.Start].Seconds, request.Samples[selected.Window.End].Seconds)) LogLinearSeries.Add(p);
            }
            return;
        }
        if (_reoxygenationStart < 0)
        {
            return;
        }

        var times = new List<double>(LivePoints.Count - _reoxygenationStart);
        var values = new List<double>(times.Capacity);
        for (var i = _reoxygenationStart; i < LivePoints.Count; i++)
        {
            var p = LivePoints[i];
            if (p.Phase != RunPhase.Reoxygenating)
            {
                continue;
            }
            times.Add(p.RelativeSeconds);
            values.Add(p.DOFiltered);
        }

        if (times.Count < 5)
        {
            return;
        }

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

    private void OnLivePointsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        {
            _reoxygenationStart = -1;
            _reoxygenationCount = 0;
            _derivedSeriesComputedAt = 0;
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
        _isRecomputing = true;
        ResetReviewInputs();
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

                var minDO = reoxPoints.Min(p => p.DOFiltered);
                var maxDO = reoxPoints.Max(p => p.DOFiltered);
                var doSpan = maxDO - minDO;
                if (doSpan > 5.0)
                {
                    var targetStartDO = minDO + (doSpan * startFraction);
                    var targetEndDO = minDO + (doSpan * endFraction);
                    var ptStart = reoxPoints.FirstOrDefault(p => (p.DOFiltered) >= targetStartDO);
                    var ptEnd = reoxPoints.FirstOrDefault(p => (p.DOFiltered) >= targetEndDO);
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
            _isRecomputing = false;
            RecomputeReviewAnalysis();

            if (AutoAcceptRuns && CurrentTest?.ProtocolSettings is null)
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
        _isRecomputing = false;
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

    /// <summary>
    /// <c>analise.json</c> of each run, keyed by run id and valid only for the summary instance it
    /// was read for: the runner replaces a run's summary when its outcome changes, and a reload
    /// from disk builds new instances, so a stale entry can never be served. Before this cache
    /// every refresh re-read every run's analysis from disk — with 20–40 runs, hundreds of ms on
    /// the UI thread at each accept (§E).
    /// </summary>
    private readonly Dictionary<Guid, (KlaTestRunSummary Source, KlaAnalysisRevision? Analysis)> _analysisCache = new();

    private KlaAnalysisRevision? LoadRunAnalysisCached(string testFolderName, KlaTestRunSummary run)
    {
        if (_analysisCache.TryGetValue(run.RunId, out var cached) && ReferenceEquals(cached.Source, run))
        {
            return cached.Analysis;
        }

        var analysis = _store.LoadRunAnalysis(testFolderName, run.FolderName);
        _analysisCache[run.RunId] = (run, analysis);
        return analysis;
    }

    /// <summary>
    /// Brings <see cref="MatrixRows"/> in line with the plan <em>in place</em>: rows are matched by
    /// condition and replicate and updated; they are inserted, moved or removed only when the plan
    /// itself changed, so the grid keeps its containers and selection across refreshes.
    /// </summary>
    public void RefreshConditionsList()
    {
        foreach (var c in Conditions)
        {
            c.NotifyChanged();
        }

        if (CurrentTest is null)
        {
            MatrixRows.Clear();
            _analysisCache.Clear();
            return;
        }

        var index = 0;
        var rowIndex = 1;
        foreach (var cond in CurrentTest.Conditions)
        {
            var requestedReps = Math.Max(1, cond.RequestedReplicates);
            for (var rep = 1; rep <= requestedReps; rep++)
            {
                var existingIndex = -1;
                for (var i = index; i < MatrixRows.Count; i++)
                {
                    if (MatrixRows[i].ConditionId == cond.ConditionId && MatrixRows[i].ReplicateIndex == rep && ReferenceEquals(MatrixRows[i].Condition, cond))
                    {
                        existingIndex = i;
                        break;
                    }
                }

                KlaMatrixRowViewModel row;
                if (existingIndex < 0)
                {
                    row = new KlaMatrixRowViewModel(cond, rep);
                    MatrixRows.Insert(index, row);
                }
                else
                {
                    if (existingIndex != index)
                    {
                        MatrixRows.Move(existingIndex, index);
                    }
                    row = MatrixRows[index];
                }
                row.OrderIndex = rowIndex++;
                index++;

                // Find matching run in CurrentTest.Runs
                var run = FindBestRun(CurrentTest.Runs, cond.ConditionId, rep)
                       ?? (requestedReps == 1 ? FindBestRun(CurrentTest.Runs, cond.ConditionId, null) : null);

                row.RunFolderName = run?.FolderName;
                row.LoadedRunPhase = run?.Phase;
                row.AutomaticDecisionLabel = run?.AutomaticDecision is not null
                    ? new KlaRecordedAttemptViewModel(run, CurrentTest.EffectiveProtocol).Decision : null;
                if (run is not null)
                {
                    if (run.AutomaticDecision is { } automatic)
                    {
                        row.KlaPerHour = automatic.KlaPerHour;
                        row.AnalysisR2 = run.AnalysisR2;
                        row.Status = KlaSequence.IsAccepted(run) ? ConditionStatus.Completed : ConditionStatus.Pending;
                        row.NotifyChanged();
                        continue;
                    }
                    var analysis = LoadRunAnalysisCached(CurrentTest.FolderName, run);
                    if (analysis is not null && analysis.Quality != DecisionQuality.Inconclusive)
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
                    else
                    {
                        row.KlaPerHour = null;
                        row.AnalysisR2 = null;
                        row.Status = run.Phase == RunPhase.Accepted ? ConditionStatus.Completed : ConditionStatus.Pending;
                    }
                }
                else
                {
                    row.KlaPerHour = null;
                    row.AnalysisR2 = null;
                    row.Status = cond.Status switch
                    {
                        ConditionStatus.Completed => ConditionStatus.Completed,
                        ConditionStatus.InProgress => rep <= cond.CompletedReplicates ? ConditionStatus.Completed : ConditionStatus.InProgress,
                        _ => ConditionStatus.Pending,
                    };
                }
            }
        }

        while (MatrixRows.Count > index)
        {
            MatrixRows.RemoveAt(MatrixRows.Count - 1);
        }
    }

    private static KlaTestRunSummary? FindBestRun(
        IEnumerable<KlaTestRunSummary> runs,
        Guid conditionId,
        int? replicateNumber)
    {
        return runs
            .Where(r => r.ConditionId == conditionId && (!replicateNumber.HasValue || r.ReplicateNumber == replicateNumber.Value))
            .OrderByDescending(r => r.KlaPerHour.HasValue &&
                (r.AutomaticDecision is not null ? KlaSequence.IsAccepted(r) : r.Phase == RunPhase.Accepted))
            .ThenByDescending(r => r.CompletedUtc ?? r.StartedUtc)
            .FirstOrDefault();
    }

    private void UpdateUiState()
    {
        RefreshCommonState();
        OnPropertyChanged(nameof(HasActiveTest));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(CanStartSequence));
        OnPropertyChanged(nameof(IsLegacyRigTest));
        OnPropertyChanged(nameof(LegacyRigMessage));
        OnPropertyChanged(nameof(DisplayPhase));
        OnPropertyChanged(nameof(FormattedTotalTime));
        OnPropertyChanged(nameof(FormattedPhaseTime));
    }

    public void Dispose()
    {
        _queueAdvanceCancellation?.Cancel();
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
