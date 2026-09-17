using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Dialogs;
using OpenTECHub.Services.KlaMapping;
using OpenTECHub.Services.PowerMapping;
using OpenTECHub.Services.PowerTesting;

namespace OpenTECHub.ViewModels;

/// <summary>Master-detail acquisition surface for impeller-power assays.</summary>
public sealed partial class PowerTestViewModel : ObservableObject, IDisposable
{
    private readonly IPowerTestStore _store;
    private readonly IDeviceService _device;
    /// <summary>
    /// Nameplate torque of the ECMA-C20604ES, used when no assay is open to say otherwise.
    /// </summary>
    /// <remarks>
    /// The servo reports torque as a percentage of this figure, so it is what turns a reading
    /// into N·m and then into watts. It is recorded in a single-point manifest for that reason:
    /// the watts column cannot be rebuilt from the percentage without it.
    /// </remarks>
    private const double DefaultMotorRatedTorqueNm = 1.27;

    private readonly ICommandArbiter _arbiter;
    private readonly IPowerTestRunner? _runner;
    private readonly IDialogService? _dialogs;
    private readonly IPowerAnalysisEngine _analysis;
    private readonly IKlaProfileStore? _klaStore;

    /// <summary>The A/B/C wiring for the single-point check; the documented default when not injected.</summary>
    private readonly Func<GasRigConfiguration> _rig;
    private SensorSnapshot? _latestSnapshot;
    private readonly HashSet<PowerCondition> _flowConfiguredConditions = [];
    private CancellationTokenSource? _tareCancellation;
    private PowerTareCaptureController? _tareCapture;
    private long _tarePointStartedTimestamp;
    private long _tareSweepStartedTimestamp;
    private double _tareLastValidSeconds = double.NaN;
    private int _tarePointIndex;
    private int _tarePointTotal;
    private string? _tareFailureMessage;

    /// <summary>Raw file of the sweep in progress, written as the readings arrive.</summary>
    private string? _tareRawFileName;

    /// <summary>How many of the current rung's readings are already on disk.</summary>
    private int _tareRawWrittenCount;

    /// <summary>Manifest of the single-point capture in progress, null when the panel is idle.</summary>
    private SinglePointSession? _singlePointSession;
    private string? _singlePointFileName;
    private int _singlePointSampleCount;
    private long _lastPreflightTick;
    private bool _suppressConditionPersistence;
    private bool _disposed;
    private readonly PowerMotorRouteCoordinator _routeCoordinator;

    public PowerTestViewModel(IPowerTestStore store, IDeviceService device, ICommandArbiter arbiter)
        : this(store, device, arbiter, null, null, null)
    {
    }

    public PowerTestViewModel(
        IPowerTestStore store,
        IDeviceService device,
        ICommandArbiter arbiter,
        IPowerTestRunner? runner,
        IDialogService? dialogs)
        : this(store, device, arbiter, runner, dialogs, null)
    {
    }

    public PowerTestViewModel(
        IPowerTestStore store,
        IDeviceService device,
        ICommandArbiter arbiter,
        IPowerTestRunner? runner,
        IDialogService? dialogs,
        IPowerAnalysisEngine? analysis)
        : this(store, device, arbiter, runner, dialogs, analysis, null)
    {
    }

    public PowerTestViewModel(
        IPowerTestStore store,
        IDeviceService device,
        ICommandArbiter arbiter,
        IPowerTestRunner? runner,
        IDialogService? dialogs,
        IPowerAnalysisEngine? analysis,
        IKlaProfileStore? klaStore)
        : this(store, device, arbiter, runner, dialogs, analysis, klaStore, null)
    {
    }

    public PowerTestViewModel(
        IPowerTestStore store,
        IDeviceService device,
        ICommandArbiter arbiter,
        IPowerTestRunner? runner,
        IDialogService? dialogs,
        IPowerAnalysisEngine? analysis,
        IKlaProfileStore? klaStore,
        PowerMapViewModel? mapViewModel,
        Func<GasRigConfiguration>? gasRig = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(arbiter);
        _store = store;
        _device = device;
        _arbiter = arbiter;
        _runner = runner;
        _dialogs = dialogs;
        _analysis = analysis ?? new PowerAnalysisEngine();
        _klaStore = klaStore;
        _rig = gasRig ?? (static () => GasRigConfiguration.Default);
        MapViewModel = mapViewModel;
        TestRootDirectory = store.RootDirectory;
        _routeCoordinator = runner?.RouteCoordinator ?? new PowerMotorRouteCoordinator(arbiter, device, CommandOwner.PowerAssay);

        _device.TelemetryReceived += OnTelemetryReceived;
        _arbiter.OwnershipChanged += OnOwnershipChanged;
        if (_runner is not null)
        {
            _runner.StateChanged += OnRunnerStateChanged;
            _runner.DataPointAdded += OnDataPointAdded;
            _runner.RunStarted += OnRunStarted;
        }

        RefreshOwnership();
        RefreshTests();
        Conditions.CollectionChanged += OnConditionsCollectionChanged;
        Impellers.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
            {
                foreach (Impeller imp in e.NewItems)
                {
                    imp.PropertyChanged += OnImpellerPropertyChanged;
                }
            }
            if (e.OldItems is not null)
            {
                foreach (Impeller imp in e.OldItems)
                {
                    imp.PropertyChanged -= OnImpellerPropertyChanged;
                }
            }
            NotifyGeometryState();
            ReprocessIfActive();
        };

        if (_device.Latest is { } latest)
        {
            OnTelemetryReceived(latest);
        }

        UpdateRunnerState();
        LoadImpellerCatalog();
    }

    private void OnConditionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var condition in _flowConfiguredConditions)
            {
                condition.PropertyChanged -= OnConditionPropertyChanged;
                condition.ConfigureFlowConversion(null);
            }
            _flowConfiguredConditions.Clear();
        }

        if (e.OldItems is not null)
        {
            foreach (PowerCondition condition in e.OldItems)
            {
                condition.PropertyChanged -= OnConditionPropertyChanged;
                condition.ConfigureFlowConversion(null);
                _flowConfiguredConditions.Remove(condition);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (PowerCondition condition in e.NewItems)
            {
                condition.PropertyChanged += OnConditionPropertyChanged;
                condition.ConfigureFlowConversion(
                    () => LiquidVolumeL,
                    message => ValidationMessage = message);
                _flowConfiguredConditions.Add(condition);
            }
        }
    }

    private void OnConditionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isLoadingTest || _suppressConditionPersistence || !CanEditPlan)
        {
            return;
        }

        PersistConditionPlan("Tabela de condições atualizada automaticamente.", showMessage: false);
    }

    private bool _applyingImpellerDefaults;

    private void OnImpellerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Choosing a type has to bring that impeller's identity with it. Without this the row kept
        // the previous blade count and the previous literature Np, so the reference overlay on the
        // Np(Re) chart silently described a different impeller than the one selected (§15).
        // The measured geometry - diameter and clearance - is the operator's, and is left alone.
        if (!_applyingImpellerDefaults &&
            e.PropertyName == nameof(Impeller.Type) &&
            sender is Impeller impeller)
        {
            _applyingImpellerDefaults = true;
            try
            {
                var reference = PowerImpellerCatalog.Create(impeller.Type);
                impeller.Label = reference.Label;
                impeller.LiteratureNp = reference.LiteratureNp;
                if (reference.BladeCount > 0)
                {
                    impeller.BladeCount = reference.BladeCount;
                }
            }
            finally
            {
                _applyingImpellerDefaults = false;
            }
        }

        NotifyGeometryState();
        ReprocessIfActive();
    }

    public string TestRootDirectory { get; }
    public string PageStage => "Aquisição · setup e captura ao vivo";
    public IReadOnlyList<EnumChoice<ImpellerType>> ImpellerTypes { get; } =
    [
        new(ImpellerType.RushtonFlatBlade, "Rushton"),
        new(ImpellerType.MarinePropeller, "Hélice marinha"),
        new(ImpellerType.ElephantEar, "Orelha de elefante"),
        new(ImpellerType.SmithConcaveBlade, "Smith côncavo"),
        new(ImpellerType.Custom, "Personalizado"),
    ];
    public IReadOnlyList<EnumChoice<PowerGasMode>> GasModes { get; } =
    [
        new(PowerGasMode.Ungassed, "Não-gaseificada"),
        new(PowerGasMode.Gassed, "Gaseificada"),
        new(PowerGasMode.Both, "Ambas"),
    ];
    public IReadOnlyList<EnumChoice<FlowInputUnit>> FlowUnits { get; } =
    [
        new(FlowInputUnit.Lpm, "L/min"),
        new(FlowInputUnit.Vvm, "vvm"),
    ];
    public IReadOnlyList<EnumChoice<PowerSweepType>> SweepTypes { get; } =
    [
        new(PowerSweepType.VariableNConstantQg, "N variável (Qg constante)"),
        new(PowerSweepType.VariableQgConstantN, "Qg variável (N constante)"),
        new(PowerSweepType.MatrixNByQg, "Matriz 2D (N × Qg)"),
    ];

    public ObservableCollection<PowerTestSummary> Tests { get; } = [];
    public ObservableCollection<Impeller> Impellers { get; } = [];
    public ObservableCollection<Impeller> CatalogImpellers { get; } = [];
    public ObservableCollection<PowerCondition> Conditions { get; } = [];
    public ObservableCollection<PowerDataPoint> LivePoints { get; } = [];
    public ObservableCollection<PowerResultRow> Results { get; } = [];

    [ObservableProperty]
    private Impeller? _selectedCatalogImpeller;

    public PowerMapViewModel? MapViewModel { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMappingTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsModelsTabSelected))]
    private int _selectedMainTabIndex;

    public bool IsMappingTabSelected
    {
        get => SelectedMainTabIndex == 0;
        set
        {
            if (value && SelectedMainTabIndex != 0)
            {
                SelectedMainTabIndex = 0;
            }
        }
    }

    public bool IsModelsTabSelected
    {
        get => SelectedMainTabIndex == 1;
        set
        {
            if (value && SelectedMainTabIndex != 1)
            {
                SelectedMainTabIndex = 1;
            }
        }
    }

    partial void OnSelectedMainTabIndexChanged(int value)
    {
        if (value == 1 && MapViewModel is { } mapVm)
        {
            _ = mapVm.RefreshOnEnterCommand.ExecuteAsync(null);
        }
    }

    [ObservableProperty] public partial PowerTestSummary? SelectedTest { get; set; }
    [ObservableProperty] public partial PowerTestDocument? CurrentTest { get; private set; }
    [ObservableProperty] public partial Impeller? SelectedImpeller { get; set; }
    [ObservableProperty] public partial PowerCondition? SelectedCondition { get; set; }
    [ObservableProperty] public partial bool HasServoSample { get; private set; }
    [ObservableProperty] public partial double? CurrentRpm { get; private set; }
    [ObservableProperty] public partial double? CurrentTorquePercent { get; private set; }
    [ObservableProperty] public partial double? CurrentTorqueNm { get; private set; }
    [ObservableProperty] public partial double? CurrentPowerW { get; private set; }
    [ObservableProperty] public partial double? CurrentFlowLpm { get; private set; }
    [ObservableProperty] public partial double? CurrentNp { get; private set; }
    [ObservableProperty] public partial double? CurrentRe { get; private set; }
    [ObservableProperty] public partial double? CurrentFr { get; private set; }
    [ObservableProperty] public partial double? CurrentFlowVvm { get; private set; }
    [ObservableProperty] public partial double? CurrentPowerRatio { get; private set; }
    [ObservableProperty] public partial bool ShowPowerRatioChart { get; set; }
    [ObservableProperty] public partial string GasLoopStatusBadge { get; private set; } = "Fechado";
    [ObservableProperty] public partial string AgitationOwnerLabel { get; private set; } = "Manual";
    [ObservableProperty] public partial string StatusMessage { get; private set; } = "Crie ou abra um ensaio de potência.";
    [ObservableProperty] public partial string ValidationMessage { get; private set; } = "";
    [ObservableProperty] public partial string PhaseLabel { get; private set; } = "Inativo";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreflightWarning))]
    public partial bool IsRunning { get; private set; }
    [ObservableProperty] public partial bool IsInReview { get; private set; }

    /// <summary>The condition the runner is executing, for the plan grid to follow; null when idle.</summary>
    [ObservableProperty] public partial Guid? CurrentConditionId { get; private set; }

    /// <summary>
    /// True while the run under review captured samples. A run that stopped before capturing —
    /// vent, valve or speed time-out — is not a result to review: the strip offers only
    /// <c>Repetir</c> and <c>Rejeitar</c> (D-050).
    /// </summary>
    [ObservableProperty] public partial bool ReviewHasCapture { get; private set; }

    public bool CanAcceptRun => IsInReview && ReviewHasCapture;
    public bool IsReviewingUnperformedRun => IsInReview && !ReviewHasCapture;

    /// <summary>"Sem captura — {motivo}", for the strip shown in place of the accept button.</summary>
    [ObservableProperty] public partial string ReviewNoCaptureText { get; private set; } = "";
    [ObservableProperty] public partial bool IsPaused { get; private set; }
    [ObservableProperty] public partial bool IsWaitingManualEnergy { get; private set; }
    [ObservableProperty] public partial bool IsAccumulating { get; private set; }
    [ObservableProperty] public partial double CiProgressPercent { get; private set; }
    [ObservableProperty] public partial string CiLabel { get; private set; } = "IC95 —";
    [ObservableProperty] public partial double SequenceProgressPercent { get; private set; }
    [ObservableProperty] public partial string SequenceProgressLabel { get; private set; } = "0/0 pontos";
    [ObservableProperty] public partial string EtaLabel { get; private set; } = "ETA —";
    [ObservableProperty] public partial string PhaseElapsedLabel { get; private set; } = "00:00";
    [ObservableProperty] public partial string TotalElapsedLabel { get; private set; } = "00:00";

    [ObservableProperty] public partial double DensityKgM3 { get; set; } = 997.0;
    [ObservableProperty] public partial double ViscosityPaS { get; set; } = 0.00089;
    [ObservableProperty] public partial double TemperatureC { get; set; } = 25.0;
    [ObservableProperty] public partial double VesselDiameterMm { get; set; } = 190.0;
    [ObservableProperty] public partial double LiquidVolumeL { get; set; } = 10.0;
    [ObservableProperty] public partial bool IsBaffled { get; set; } = true;
    [ObservableProperty] public partial bool RelativeMode { get; set; } = true;
    [ObservableProperty] public partial double MinRpm { get; set; } = 15.0;
    [ObservableProperty] public partial double MaxRpm { get; set; } = 1000.0;
    [ObservableProperty] public partial double StepRpm { get; set; } = 50.0;
    [ObservableProperty] public partial double MinFlowLpm { get; set; } = 0.0;
    [ObservableProperty] public partial double MaxFlowLpm { get; set; } = 20.0;
    [ObservableProperty] public partial double StepFlowLpm { get; set; } = 0.5;
    [ObservableProperty] public partial double RelativeCiPercent { get; set; } = 2.0;
    [ObservableProperty] public partial double CiFloorSigmaMultiple { get; set; } = 1.0;
    [ObservableProperty] public partial int MinimumSamples { get; set; } = 60;
    [ObservableProperty] public partial double MaxCaptureSeconds { get; set; } = 300.0;
    [ObservableProperty] public partial int MaxTries { get; set; } = 3;
    [ObservableProperty] public partial double StationarityWindowSeconds { get; set; } = 20.0;
    [ObservableProperty] public partial double StationaritySlopeTolerance { get; set; } = 0.5;
    [ObservableProperty] public partial int StationarityRequiredSamples { get; set; } = 5;
    // Pre-staging on C: mandatory for every gassed condition on the A/B/C rig (plan §3.4).
    [ObservableProperty] public partial double PrestageFlowToleranceLpm { get; set; } = 0.2;
    [ObservableProperty] public partial int PrestageFlowStableSamples { get; set; } = 5;
    [ObservableProperty] public partial double PrestageAgitationRpm { get; set; } = 15.0;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPrestageTimeoutShort))]
    public partial double MaxPrestageSeconds { get; set; } = 500.0;

    /// <summary>
    /// Bench of 2026-09-11: the flow on C overshoots to ~2.3× the target and decays with τ ≈ 45 s,
    /// so it takes ~110–170 s to enter the tolerance band. A time-out under ~3τ expires before the
    /// flow has settled and sends the run to review with nothing captured.
    /// </summary>
    public const double PrestageTimeoutShortThresholdSeconds = 150.0;

    public bool IsPrestageTimeoutShort => MaxPrestageSeconds < PrestageTimeoutShortThresholdSeconds;

    /// <summary>Stability way out of the pre-stage (§I.2): spread of the last N readings and the allowed offset.</summary>
    [ObservableProperty] public partial double PrestageFlowStabilityStdDevLpm { get; set; } = 0.05;
    [ObservableProperty] public partial double PrestageFlowStabilityMaxErrorLpm { get; set; } = 0.3;

    /// <summary>The wiring in force, from Documentação › Gás e válvulas — shown, never edited here.</summary>
    public string GasRigDescription => $"Arranjo: {_rig().Describe()} (Documentação › Gás e válvulas)";

    /// <summary>An assay recorded before the A/B/C rig: reviewable, never continued (plan §3.5).</summary>
    public bool IsLegacyRigTest => CurrentTest?.IsLegacyRig == true;
    public string LegacyRigMessage => IsLegacyRigTest
        ? "Montagem anterior ao arranjo A/B/C — só leitura. Crie um ensaio novo para continuar no arranjo atual."
        : "";

    /// <summary>
    /// <see cref="UnattendedFailurePolicy.RetryThenSkip"/> as a switch: with auto-accept, a vent,
    /// valve or speed time-out rejects the run, retries the condition once and then skips it,
    /// instead of parking the assay for review (§I.1). Limits still stop.
    /// </summary>
    [ObservableProperty] public partial bool RetryThenSkipOnSequenceFailure { get; set; }
    [ObservableProperty] public partial bool ManualEnergyCaptureEnabled { get; set; }

    /// <summary>
    /// Accept each captured point without operator review and chain straight into the next
    /// condition. Off by default: auto-accepting a point skips the only place a bad capture
    /// gets rejected, so it is an explicit opt-in per assay.
    /// </summary>
    [ObservableProperty] public partial bool AutoAcceptRuns { get; set; }
    [ObservableProperty] public partial bool AutoResumeOnLinkRestore { get; set; } = true;
    [ObservableProperty] public partial double LinkRecoveryTimeoutSeconds { get; set; } = 30.0;

    [ObservableProperty] public partial PowerSweepType SelectedSweepType { get; set; } = PowerSweepType.VariableNConstantQg;
    [ObservableProperty] public partial double SweepConstantRpm { get; set; } = 300.0;
    [ObservableProperty] public partial double SweepStartQgLpm { get; set; } = 2.0;
    [ObservableProperty] public partial double SweepEndQgLpm { get; set; } = 20.0;
    [ObservableProperty] public partial double SweepStepQgLpm { get; set; } = 2.0;
    [ObservableProperty] public partial double SweepConstantQgLpm { get; set; } = 0.0;
    [ObservableProperty] public partial PowerGasMode SweepGasMode { get; set; } = PowerGasMode.Gassed;

    [ObservableProperty] public partial string ControlRegionSummary { get; set; } = "Nenhum mapa de kLa vinculado.";
    [ObservableProperty] public partial bool HasLinkedKlaMap { get; set; }
    [ObservableProperty] public partial string LinkedKlaMapName { get; set; } = "";
    [ObservableProperty] public partial double? AverageKlaEfficiency { get; set; }
    public ObservableCollection<KlaEfficiencyComparisonItem> KlaEfficiencyItems { get; } = [];

    private KlaExperimentDocument? _linkedKlaDocument;
    private KlaSurface? _linkedKlaSurface;
    private bool _isLoadingTest;

    [ObservableProperty] public partial PowerResultRow? SelectedResultRow { get; set; }
    public bool CanChangeResultStatus => CurrentTest is not null && !IsRunning && !IsTareRunning && CurrentTest.Status != PowerTestStatus.Completed;

    partial void OnSelectedResultRowChanged(PowerResultRow? value)
    {
        OnPropertyChanged(nameof(CanChangeResultStatus));
    }

    public bool IsSweepTypeNVariable => SelectedSweepType is PowerSweepType.VariableNConstantQg or PowerSweepType.MatrixNByQg;
    public bool IsSweepTypeQgVariable => SelectedSweepType is PowerSweepType.VariableQgConstantN or PowerSweepType.MatrixNByQg;
    public bool IsSweepTypeNConstant => SelectedSweepType == PowerSweepType.VariableQgConstantN;
    public bool IsSweepTypeQgConstant => SelectedSweepType == PowerSweepType.VariableNConstantQg;
    public string SweepExplanation => SelectedSweepType switch
    {
        PowerSweepType.VariableNConstantQg =>
            "Cria uma linha para cada rotação entre N mín. e N máx., usando o passo do plano e a mesma vazão Qg.",
        PowerSweepType.VariableQgConstantN =>
            "Cria uma linha para cada vazão entre Qg mín. e Qg máx., mantendo a rotação N fixa.",
        PowerSweepType.MatrixNByQg =>
            "Cria todas as combinações entre a faixa de rotação do plano e a faixa de vazão Qg.",
        _ => "Cria automaticamente as linhas da tabela de condições.",
    };

    partial void OnSelectedSweepTypeChanged(PowerSweepType value)
    {
        OnPropertyChanged(nameof(IsSweepTypeNVariable));
        OnPropertyChanged(nameof(IsSweepTypeQgVariable));
        OnPropertyChanged(nameof(IsSweepTypeNConstant));
        OnPropertyChanged(nameof(IsSweepTypeQgConstant));
        OnPropertyChanged(nameof(SweepExplanation));
    }

    partial void OnDensityKgM3Changed(double value)
    {
        RecalculateLiveMetrics();
        ReprocessIfActive();
    }

    partial void OnViscosityPaSChanged(double value)
    {
        RecalculateLiveMetrics();
        ReprocessIfActive();
    }

    partial void OnTemperatureCChanged(double value)
    {
        ReprocessIfActive();
    }

    partial void OnVesselDiameterMmChanged(double value)
    {
        ReprocessIfActive();
    }

    partial void OnLiquidVolumeLChanged(double value)
    {
        if (value > 0)
        {
            foreach (var cond in Conditions)
            {
                if (cond.FlowUnit == FlowInputUnit.Vvm && cond.GasFlowVvm.HasValue)
                {
                    cond.GasFlowLpm = Math.Round(cond.GasFlowVvm.Value * value, 3);
                }
                else if (cond.GasFlowLpm.HasValue)
                {
                    cond.GasFlowVvm = Math.Round(cond.GasFlowLpm.Value / value, 4);
                }
            }
        }
        RecalculateLiveMetrics();
        ReprocessIfActive();
    }

    // --- Step 8: Guided Procedures ---
    // 8.1 Tare Curve Assistant (in-air sweep)
    [ObservableProperty] public partial bool IsTareAssistantOpen { get; set; }
    [ObservableProperty] public partial bool IsTareRunning { get; set; }
    [ObservableProperty] public partial double TareStartRpm { get; set; } = 100.0;
    [ObservableProperty] public partial double TareEndRpm { get; set; } = 1000.0;
    [ObservableProperty] public partial double TareStepRpm { get; set; } = 100.0;
    [ObservableProperty] public partial string TareProgressMessage { get; set; } = "";
    public ObservableCollection<TarePoint> CurrentTarePoints { get; } = [];

    // 8.2.1 Tare profile library (one curve per shaft, shared across assays)
    [ObservableProperty] public partial string TareProfileName { get; set; } = "";
    [ObservableProperty] public partial TareProfileSummary? SelectedTareProfile { get; set; }
    public ObservableCollection<TareProfileSummary> TareProfiles { get; } = [];
    public ObservableCollection<EditableTarePoint> SelectedTareProfilePoints { get; } = [];

    /// <summary>Typing over the box detaches it from the list, so the two never disagree.</summary>
    partial void OnSelectedTareProfileChanged(TareProfileSummary? value)
    {
        SelectedTareProfilePoints.Clear();
        if (value is not null)
        {
            TareProfileName = value.Name;
            var curve = _store.LoadTareProfile(value.Name);
            if (curve != null)
            {
                foreach (var pt in curve.Points)
                {
                    var editable = new EditableTarePoint(pt);
                    editable.PropertyChanged += (s, e) => SaveModifiedTareProfile();
                    SelectedTareProfilePoints.Add(editable);
                }
            }
        }
    }

    private void SaveModifiedTareProfile()
    {
        if (SelectedTareProfile is { Name: var name })
        {
            var curve = _store.LoadTareProfile(name);
            if (curve != null)
            {
                var newPoints = SelectedTareProfilePoints.Select(ep => ep.ToRecord()).ToList();
                var newCurve = curve with { Points = newPoints };
                _store.SaveTareProfile(name, newCurve);

                if (CurrentTest is not null &&
                    string.Equals(CurrentTest.Tare?.ProfileName, name, StringComparison.OrdinalIgnoreCase))
                {
                    CurrentTest.Tare = newCurve;
                    _store.SaveTare(CurrentTest.FolderName, newCurve);
                    _store.SaveTestManifest(CurrentTest);
                    RefreshCurrentTarePoints();
                    ReprocessScientificData();
                }
            }
        }
    }

    // 8.3 Single Point Spot-Check
    [ObservableProperty] public partial bool IsSinglePointPanelOpen { get; set; }
    [ObservableProperty] public partial bool IsSinglePointActive { get; set; }
    [ObservableProperty] public partial double SinglePointRpm { get; set; } = 300.0;
    [ObservableProperty] public partial double SinglePointFlowLpm { get; set; } = 0.0;

    // 8.4 Manual Energy Capture & Correlation
    [ObservableProperty] public partial double ManualEnergyWattsInput { get; set; } = 0.0;
    [ObservableProperty] public partial string ManualEnergyInstrument { get; set; } = "Wattímetro";
    [ObservableProperty] public partial string ManualEnergyNote { get; set; } = "";
    [ObservableProperty] public partial bool IsEnergyCorrelationOpen { get; set; }
    [ObservableProperty] public partial string EnergyCorrelationSummary { get; private set; } = "Sem pontos com medição elétrica.";
    public ObservableCollection<ManualElecReading> ManualEnergyReadings { get; } = [];

    public bool HasActiveTest => CurrentTest is not null;
    public bool CanEditPlan => CurrentTest is not null && !IsRunning && !IsTareRunning && CurrentTest.Status != PowerTestStatus.Completed;
    public bool CanStartOrContinue => CurrentTest is not null && (!IsRunning || _runner?.IsPausedForLinkRecovery == true) && !IsTareRunning && !IsInReview && CurrentTest.Status != PowerTestStatus.Completed;
    public bool CanManageTest => CurrentTest is not null && !IsRunning && !IsTareRunning;
    public PowerMotorRouteCoordinator RouteCoordinator => _routeCoordinator;

    partial void OnIsTareRunningChanged(bool value) => NotifyDocumentState();

    /// <summary>
    /// Manual energy capture parks every point waiting for a wattmeter reading, which the
    /// runner checks before auto-accept ever runs. Leaving both on would silently defeat the
    /// unattended sequence, so enabling auto-accept clears it rather than losing to it.
    /// </summary>
    partial void OnAutoAcceptRunsChanged(bool value)
    {
        if (value)
        {
            ManualEnergyCaptureEnabled = false;
        }
    }

    /// <summary>True when the runner's preflight passes right now (§12, §14).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreflightWarning))]
    public partial bool IsReadyToStart { get; set; }

    /// <summary>What the preflight says, so the operator reads it before pressing start.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreflightWarning))]
    public partial string PreflightMessage { get; set; } = "Abra ou crie um ensaio para começar.";

    /// <summary>
    /// True when there is an active warning / blocker notice preventing start.
    /// Used by the 'Ensaio' card to display the advisory below the path string.
    /// </summary>
    public bool HasPreflightWarning => !IsReadyToStart && !IsRunning && !string.IsNullOrWhiteSpace(PreflightMessage);

    public bool CanPause => IsRunning && _runner?.Phase is PowerRunPhase.SettingSpeed or PowerRunPhase.SettlingTorque or PowerRunPhase.AccumulatingToTarget or PowerRunPhase.PausedByOperator or PowerRunPhase.PausedForMeasurement or PowerRunPhase.PausedForLinkRecovery;
    public bool CanStop => IsRunning;
    public bool CanSkipCurrent => IsRunning && _runner?.CurrentCondition is not null;
    public string PauseButtonLabel => _runner?.IsPausedByOperator == true || _runner?.IsPausedForMeasurement == true || _runner?.IsPausedForLinkRecovery == true ? "▶ Retomar" : "⏸ Pausar";
    public string TestStatusLabel => CurrentTest?.Status switch
    {
        PowerTestStatus.Draft => "Rascunho",
        PowerTestStatus.Running => "Em execução",
        PowerTestStatus.Interrupted => "Interrompido · edição liberada",
        PowerTestStatus.Completed => "Concluído — somente leitura. Use Novo, Duplicar ou Reabrir.",
        _ => "Nenhum ensaio",
    };

    /// <summary>A finished assay can only be left through Novo, Duplicar or Reabrir (§G).</summary>
    public bool CanReopenTest => CanManageTest && CurrentTest?.Status == PowerTestStatus.Completed;
    public string TareStatus
    {
        get
        {
            if (CurrentTest?.Tare is null)
            {
                return "Sem tara aplicada · modo relativo";
            }

            var currentHash = PowerTestFileContracts.ComputeImpellerSetHash(BuildGeometry());
            var matchesGeometry = string.Equals(CurrentTest.Tare.ImpellerSetHash, currentHash, StringComparison.OrdinalIgnoreCase);
            var profileName = CurrentTest.Tare.ProfileName;

            if (!string.IsNullOrWhiteSpace(profileName))
            {
                return matchesGeometry
                    ? $"Tara compatível · {profileName}"
                    : $"Tara no ar aplicada · {profileName}";
            }

            return matchesGeometry
                ? "Tara compatível · curva do ensaio"
                : "Tara no ar aplicada · conjunto diferente";
        }
    }
    public string ResultModeLabel => CurrentTest?.Tare is null ? "RELATIVO" : "CALIBRADO";
    public string PrecisionBadge => "IC estatístico · não é precisão do sensor";
    public string ImpellerSetHash => Impellers.Count == 0 ? "—" : PowerTestFileContracts.ComputeImpellerSetHash(BuildGeometry())[..12];
    public string VortexWarning
    {
        get
        {
            if (IsBaffled || Impellers.Count == 0 || Conditions.Count == 0)
            {
                return "";
            }

            var diameter = Impellers.Max(i => i.DiameterM);
            var rpm = Conditions.Max(c => c.AgitationRpm);
            return PowerCalc.FroudeNumber(rpm, diameter) >= 0.3
                ? "Atenção: tanque sem chicanas e Fr elevado; verifique vórtice antes da execução."
                : "";
        }
    }
    public string ResultsCsvPath => CurrentTest is null ? "" : Path.Combine(_store.RootDirectory, CurrentTest.FolderName, PowerTestFileContracts.ResultsSummaryFileName);
    public double? ReferenceLiteratureNp
    {
        get
        {
            var value = PowerCalc.AssemblyLiteraturePowerNumber(Impellers);
            return double.IsFinite(value) && value > 0 ? value : null;
        }
    }
    public string LiveSummary => HasServoSample
        ? $"{Format(CurrentRpm, "F1")} rpm · {Format(CurrentTorqueNm, "F4")} N·m · {Format(CurrentPowerW, "F2")} W"
        : "Aguardando telemetria válida do servo";
    public string CurrentRpmText => Format(CurrentRpm, "F1");
    public string CurrentTorquePercentText => Format(CurrentTorquePercent, "F3");
    public string CurrentTorqueNmText => Format(CurrentTorqueNm, "F4");
    public string CurrentPowerWText => Format(CurrentPowerW, "F3");
    public string CurrentFlowText => Format(CurrentFlowLpm, "F2");
    public string CurrentNpText => Format(CurrentNp, "G5");
    public string CurrentReText => Format(CurrentRe, "G5");
    public string CurrentFrText => Format(CurrentFr, "G4");
    public string CurrentFlowVvmText => Format(CurrentFlowVvm, "F2");
    public string CurrentPowerRatioText => Format(CurrentPowerRatio, "F3");

    [RelayCommand]
    private void RefreshTests()
    {
        var selectedFolder = SelectedTest?.FolderName;
        Tests.Clear();
        foreach (var test in _store.ListTests())
        {
            Tests.Add(test);
        }

        SelectedTest = Tests.FirstOrDefault(t => t.FolderName == selectedFolder) ?? Tests.FirstOrDefault();
    }

    [RelayCommand]
    private void CreateTest()
    {
        if (IsRunning || IsTareRunning || _dialogs is null || !_dialogs.PromptInput("Novo ensaio de potência", "Nome da nova pasta de ensaio:", out var name))
        {
            return;
        }

        if (!_store.ValidateTestName(name, out var error)) { ShowError(error ?? "Nome inválido."); return; }
        if (_store.TestExists(name)) { ShowError($"Já existe um ensaio chamado '{name.Trim()}'."); return; }

        var impeller = PowerImpellerCatalog.Create(ImpellerType.RushtonFlatBlade);
        impeller.DiameterM = 0.065;
        impeller.ClearanceM = 0.065;
        impeller.StageIndex = 0;
        var settings = new PowerTestSettings();
        var initialConditions = BuildRotationConditions(
            settings.MinRpm,
            settings.MaxRpm,
            settings.DefaultStepRpm,
            gasFlowLpm: null,
            gasMode: PowerGasMode.Ungassed);
        var doc = _store.CreateTest(
            name.Trim(),
            new FluidProperties { DensityKgM3 = 997.0, ViscosityPaS = 0.00089, TemperatureC = 25, PresetName = "Água 25 °C" },
            new PowerGeometry { VesselDiameterM = 0.190, LiquidVolumeM3 = 0.010, Baffled = true, Impellers = [impeller] },
            settings,
            initialConditions);
        LoadDocument(doc);
        RefreshTests();
        SelectedTest = Tests.FirstOrDefault(t => t.FolderName == doc.FolderName);
    }

    [RelayCommand]
    private void LoadSelectedTest()
    {
        if (SelectedTest is null || IsRunning || IsTareRunning)
        {
            return;
        }

        var doc = _store.LoadTest(SelectedTest.FolderName);
        if (doc is null) { ShowError("O ensaio selecionado não pôde ser aberto."); return; }
        LoadDocument(doc);
        RefreshTests();

        // Pressing Abrir on the assay already open used to change nothing on screen, which reads
        // as a dead button. Say what was loaded either way.
        var accepted = doc.Runs.Count(r => r.Phase == PowerRunPhase.Accepted);
        ValidationMessage =
            $"Ensaio '{doc.Name}' aberto: {doc.Conditions.Count} condição(ões), {accepted} ponto(s) aceito(s).";
        StatusMessage = ValidationMessage;
    }

    /// <summary>
    /// New assay with this one's fluid, geometry, calibration, tare, settings and plan — counters
    /// zeroed, no runs, no flooding — so a finished assay is not a dead end that forces the operator
    /// to redo the whole setup through Novo (bench of 2026-09-11). The manifest records the source.
    /// </summary>
    [RelayCommand]
    private void DuplicateTest()
    {
        if (!CanManageTest || CurrentTest is null || _dialogs is null ||
            !_dialogs.PromptInput("Duplicar ensaio de potência", "Nome do novo ensaio (mesmo setup, sem pontos):", out var name, CurrentTest.Name + " (2)"))
        {
            return;
        }

        if (!_store.ValidateTestName(name, out var error)) { ShowError(error ?? "Nome inválido."); return; }
        if (_store.TestExists(name)) { ShowError($"Já existe um ensaio chamado '{name.Trim()}'."); return; }

        var source = CurrentTest;
        try
        {
            var conditions = source.Conditions
                .OrderBy(c => c.OrderIndex)
                .Select(c =>
                {
                    var clone = c.Clone();
                    clone.ConditionId = Guid.NewGuid();
                    clone.CompletedReplicates = 0;
                    clone.AcceptedReplicates = 0;
                    clone.RejectedReplicates = 0;
                    clone.Status = PowerConditionStatus.Pending;
                    clone.HasReplicateDisagreement = false;
                    clone.ReproducibilityWarning = null;
                    return clone;
                })
                .ToList();

            var doc = _store.CreateTest(
                name.Trim(),
                source.Fluid,
                new PowerGeometry
                {
                    VesselDiameterM = source.Geometry.VesselDiameterM,
                    LiquidVolumeM3 = source.Geometry.LiquidVolumeM3,
                    Baffled = source.Geometry.Baffled,
                    Impellers = source.Geometry.Impellers.Select(i => i.Clone()).ToList(),
                },
                source.Settings,
                conditions,
                source.LinkedMap);
            doc.DuplicatedFrom = source.TestId;
            doc.MotorRatedTorqueNm = source.MotorRatedTorqueNm;
            if (source.Calibration is { } calibration)
            {
                doc.Calibration = calibration;
                _store.SaveCalibration(doc.FolderName, calibration);
            }
            if (source.Tare is { } tare)
            {
                doc.Tare = tare;
                _store.SaveTare(doc.FolderName, tare);
            }
            doc.RelativeMode = doc.Calibration is null || doc.Tare is null;
            _store.SaveTestManifest(doc);
            _store.AppendEventLog(doc.FolderName, new PowerTestEventLogEntry(
                DateTimeOffset.UtcNow, "TestDuplicated", $"Duplicado de '{source.Name}' ({source.TestId}).", null));

            LoadDocument(_store.LoadTest(doc.FolderName) ?? doc);
            RefreshTests();
            SelectedTest = Tests.FirstOrDefault(t => t.FolderName == doc.FolderName);
            ValidationMessage = $"Ensaio '{doc.Name}' criado a partir de '{source.Name}': {conditions.Count} condição(ões), tara e calibração copiadas, sem pontos.";
            StatusMessage = ValidationMessage;
        }
        catch (Exception ex)
        {
            ShowError($"Não foi possível duplicar o ensaio: {ex.Message}");
        }
    }

    /// <summary>
    /// Takes a finished assay back to <see cref="PowerTestStatus.Interrupted"/> so the plan can be
    /// edited and the sequence continued; accepted runs stay. Nothing else ever leaves
    /// <see cref="PowerTestStatus.Completed"/> — not the app, not a reload.
    /// </summary>
    [RelayCommand]
    private void ReopenTest()
    {
        if (!CanReopenTest || CurrentTest is null || _dialogs is null)
        {
            return;
        }

        var doc = CurrentTest;
        if (!_dialogs.Confirm(
                "Reabrir ensaio concluído",
                $"Reabrir '{doc.Name}' para editar a tabela e continuar a sequência? Os pontos aceitos são mantidos; o ensaio deixa de constar como concluído.",
                "Reabrir",
                "Cancelar"))
        {
            return;
        }

        try
        {
            doc.Status = PowerTestStatus.Interrupted;
            doc.CompletedUtc = null;
            doc.InterruptionReason = "Reaberto pelo operador";
            _store.SaveTestManifest(doc);
            _store.AppendEventLog(doc.FolderName, new PowerTestEventLogEntry(DateTimeOffset.UtcNow, "TestReopened", "Reaberto pelo operador.", null));
            _runner?.PrepareTest(doc);
            RefreshTests();
            NotifyDocumentState();
            OnPropertyChanged(nameof(CanReopenTest));
            ValidationMessage = $"Ensaio '{doc.Name}' reaberto: edição liberada, {doc.Runs.Count(r => r.Phase == PowerRunPhase.Accepted)} ponto(s) aceito(s) mantidos.";
            StatusMessage = ValidationMessage;
        }
        catch (Exception ex)
        {
            ShowError($"Não foi possível reabrir o ensaio: {ex.Message}");
        }
    }

    [RelayCommand]
    private void RenameTest()
    {
        if (!CanManageTest || CurrentTest is null || _dialogs is null ||
            !_dialogs.PromptInput(
                "Renomear ensaio de potência",
                "Novo nome do ensaio e de sua pasta:",
                out var newName,
                CurrentTest.Name))
        {
            return;
        }

        try
        {
            var renamed = _store.RenameTest(CurrentTest.FolderName, newName);
            LoadDocument(renamed);
            RefreshTests();
            SelectedTest = Tests.FirstOrDefault(test => test.FolderName == renamed.FolderName);
            ValidationMessage = $"Ensaio renomeado para '{renamed.Name}'.";
            StatusMessage = ValidationMessage;
        }
        catch (Exception ex)
        {
            ShowError($"Não foi possível renomear o ensaio: {ex.Message}");
        }
    }

    [RelayCommand]
    private void DeleteTest()
    {
        if (!CanManageTest || CurrentTest is null || _dialogs is null)
        {
            return;
        }

        var doc = CurrentTest;
        if (!_dialogs.Confirm(
                "Excluir ensaio de potência",
                $"Remover o ensaio '{doc.Name}' da lista? Os arquivos serão movidos para a lixeira interna e poderão ser recuperados manualmente.",
                "Mover para lixeira",
                "Cancelar",
                isDanger: true))
        {
            return;
        }

        try
        {
            if (!_store.DeleteTest(doc.FolderName))
            {
                ShowError("O ensaio selecionado não foi encontrado.");
                return;
            }

            CurrentTest = null;
            SelectedTest = null;
            _runner?.ClearTest();
            _suppressConditionPersistence = true;
            try
            {
                Conditions.Clear();
            }
            finally
            {
                _suppressConditionPersistence = false;
            }
            Impellers.Clear();
            CurrentTarePoints.Clear();
            LivePoints.Clear();
            Results.Clear();
            RefreshTests();
            ValidationMessage = $"Ensaio '{doc.Name}' movido para a lixeira interna.";
            StatusMessage = ValidationMessage;
            NotifyDocumentState();
        }
        catch (Exception ex)
        {
            ShowError($"Não foi possível excluir o ensaio: {ex.Message}");
        }
    }

    /// <summary>Mirrors a <see cref="PowerTestSettings"/> into the editable fields (the inverse of <see cref="BuildEditedSettings"/>).</summary>
    private void LoadSettingsFields(PowerTestSettings settings)
    {
        MinRpm = settings.MinRpm;
        MaxRpm = settings.MaxRpm;
        StepRpm = settings.DefaultStepRpm;
        MinFlowLpm = settings.MinFlowLpm;
        MaxFlowLpm = settings.MaxFlowLpm;
        StepFlowLpm = settings.DefaultStepFlowLpm;
        RelativeCiPercent = settings.RelativeCiFraction * 100.0;
        CiFloorSigmaMultiple = settings.CiFloorSigmaMultiple;
        MinimumSamples = settings.MinSamples;
        MaxCaptureSeconds = settings.MaxCaptureSeconds;
        MaxTries = settings.MaxTries;
        StationarityWindowSeconds = settings.StationarityWindowSeconds;
        StationaritySlopeTolerance = settings.StationaritySlopeTolerancePercentPerSecond;
        StationarityRequiredSamples = settings.StationarityRequiredSamples;
        PrestageFlowToleranceLpm = settings.PrestageFlowToleranceLpm;
        PrestageFlowStableSamples = settings.PrestageFlowStableSamples;
        PrestageAgitationRpm = settings.PrestageAgitationRpm;
        MaxPrestageSeconds = settings.MaxPrestageSeconds;
        PrestageFlowStabilityStdDevLpm = settings.PrestageFlowStabilityStdDevLpm;
        PrestageFlowStabilityMaxErrorLpm = settings.PrestageFlowStabilityMaxErrorLpm;
        ManualEnergyCaptureEnabled = settings.ManualEnergyCaptureEnabled;
        AutoAcceptRuns = settings.AutoAcceptRuns;
        AutoResumeOnLinkRestore = settings.AutoResumeOnLinkRestore;
        LinkRecoveryTimeoutSeconds = settings.LinkRecoveryTimeoutSeconds;
        RetryThenSkipOnSequenceFailure = settings.UnattendedFailurePolicy == UnattendedFailurePolicy.RetryThenSkip;
    }

    private void LoadDocument(PowerTestDocument doc)
    {
        _isLoadingTest = true;
        try
        {
            CurrentTest = doc;
            DensityKgM3 = doc.Fluid.DensityKgM3;
            ViscosityPaS = doc.Fluid.ViscosityPaS;
            TemperatureC = doc.Fluid.TemperatureC;
            VesselDiameterMm = doc.Geometry.VesselDiameterM * 1000.0;
            LiquidVolumeL = doc.Geometry.LiquidVolumeM3 * 1000.0;
            IsBaffled = doc.Geometry.Baffled;
            RelativeMode = doc.RelativeMode;
            RefreshTareProfiles();
            SelectedTareProfile = TareProfiles.FirstOrDefault(p => p.Name == doc.Tare?.ProfileName);
            LoadSettingsFields(doc.Settings);

            CurrentTarePoints.Clear();
            if (doc.Tare is { } tare)
            {
                foreach (var point in tare.Points.OrderBy(point => point.Rpm))
                {
                    CurrentTarePoints.Add(point);
                }
            }

            Impellers.Clear();
            foreach (var impeller in doc.Geometry.Impellers.OrderBy(i => i.StageIndex))
            {
                var cloned = impeller.Clone();
                cloned.PropertyChanged += OnImpellerPropertyChanged;
                Impellers.Add(cloned);
            }

            Conditions.Clear();
            foreach (var condition in doc.Conditions.OrderBy(c => c.OrderIndex))
            {
                Conditions.Add(condition.Clone());
            }

            SelectedImpeller = Impellers.FirstOrDefault();
            SelectedCondition = Conditions.FirstOrDefault();
            LivePoints.Clear();
            Results.Clear();
            _structureKey = null;

            if (doc.Runs.Count > 0)
            {
                ReprocessScientificData();
            }
            else
            {
                RebuildResults();
            }

            _runner?.PrepareTest(doc);
            OnPropertyChanged(nameof(IsLegacyRigTest));
            OnPropertyChanged(nameof(LegacyRigMessage));
            OnPropertyChanged(nameof(GasRigDescription));
            ValidationMessage = doc.IsLegacyRig ? LegacyRigMessage : "";
            StatusMessage = doc.IsLegacyRig ? LegacyRigMessage : $"Ensaio '{doc.Name}' carregado.";
            RefreshManualEnergyReadings();
            NotifyDocumentState();
            RecalculateLiveMetrics();
            _linkedKlaDocument = null;
            _linkedKlaSurface = null;
            _linkedKlaLoad = null;
            UpdateKlaEfficiencyComparison();
        }
        finally
        {
            _isLoadingTest = false;
        }
    }

    /// <summary>Working volume the bench runs at; a preset fills it, and the operator may change it.</summary>
    public const double DefaultLiquidVolumeL = 4.0;

    [RelayCommand]
    private void ApplyWaterPreset(string? preset)
    {
        // Água pura, valores tabelados (ρ em kg/m³, μ em Pa·s).
        switch (preset)
        {
            case "20":
                DensityKgM3 = 998.2;
                ViscosityPaS = 0.001002;
                TemperatureC = 20;
                break;

            case "30":
                DensityKgM3 = 995.65;
                ViscosityPaS = 0.000797;
                TemperatureC = 30;
                break;

            default:
                DensityKgM3 = 997.0;
                ViscosityPaS = 0.00089;
                TemperatureC = 25;
                break;
        }

        LiquidVolumeL = DefaultLiquidVolumeL;
        ValidationMessage =
            $"Água a {TemperatureC:F0} °C e volume de {DefaultLiquidVolumeL:F0} L preenchidos; " +
            "confirme a temperatura e o volume medidos.";
    }

    [RelayCommand]
    private void SaveSetup()
    {
        if (CurrentTest is null || IsRunning || IsTareRunning)
        {
            return;
        }

        if (!TryPersist(out var error)) { ValidationMessage = error; return; }
        ValidationMessage = "Setup e tabela salvos.";
        StatusMessage = ValidationMessage;
        RefreshTests();
        NotifyDocumentState();
    }

    [RelayCommand]
    private void AddImpeller()
    {
        if (!CanEditPlan)
        {
            return;
        }

        var item = PowerImpellerCatalog.Create(ImpellerType.RushtonFlatBlade);
        item.DiameterM = Impellers.LastOrDefault()?.DiameterM ?? 0.065;

        item.ClearanceM = Impellers.Count == 0 ? 0.065 : Impellers.Max(i => i.ClearanceM) + item.DiameterM;
        item.StageIndex = Impellers.Count;
        Impellers.Add(item);
        SelectedImpeller = item;
        NotifyGeometryState();
    }

    [RelayCommand]
    private void RemoveImpeller()
    {
        if (!CanEditPlan || SelectedImpeller is null)
        {
            return;
        }

        Impellers.Remove(SelectedImpeller);
        NormalizeImpellerOrder();
        SelectedImpeller = Impellers.FirstOrDefault();
        NotifyGeometryState();
    }

    [RelayCommand] private void MoveImpellerUp() => MoveItem(Impellers, SelectedImpeller, -1, NormalizeImpellerOrder);
    [RelayCommand] private void MoveImpellerDown() => MoveItem(Impellers, SelectedImpeller, 1, NormalizeImpellerOrder);

    public void LoadImpellerCatalog()
    {
        CatalogImpellers.Clear();
        var catalog = PowerImpellerCatalog.LoadCatalog();
        foreach (var item in catalog)
        {
            CatalogImpellers.Add(item);
        }
        SelectedCatalogImpeller = CatalogImpellers.FirstOrDefault();
    }

    [RelayCommand]
    private void AddCatalogImpeller()
    {
        var item = new Impeller
        {
            Type = ImpellerType.Custom,
            Label = $"Impelidor {CatalogImpellers.Count + 1}",
            DiameterM = 0.065,
            BladeCount = 6,
            ClearanceM = 0.065,
            LiteratureNp = 1.0,
        };
        CatalogImpellers.Add(item);
        SelectedCatalogImpeller = item;
        TrySaveImpellerCatalog($"Impelidor '{item.Label}' adicionado ao catálogo.");
    }

    [RelayCommand]
    private void RemoveCatalogImpeller()
    {
        if (SelectedCatalogImpeller is null)
        {
            return;
        }

        var removed = SelectedCatalogImpeller;
        CatalogImpellers.Remove(removed);
        SelectedCatalogImpeller = CatalogImpellers.FirstOrDefault();
        TrySaveImpellerCatalog($"Impelidor '{removed.Label}' removido do catálogo.");
    }

    [RelayCommand]
    private void SaveCatalog()
    {
        TrySaveImpellerCatalog("Catálogo de impelidores salvo com sucesso.");
    }

    private void TrySaveImpellerCatalog(string successMessage)
    {
        try
        {
            PowerImpellerCatalog.SaveCatalog(CatalogImpellers);
            StatusMessage = successMessage;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Não foi possível salvar o catálogo de impelidores: {ex.Message}";
            ValidationMessage = StatusMessage;
        }
    }

    [RelayCommand]
    private void DuplicateCatalogImpeller(Impeller? source = null)
    {
        var target = source ?? SelectedCatalogImpeller;
        if (target is null)
        {
            return;
        }

        var copy = target.Clone();
        copy.Label = GetUniqueCatalogName($"{target.Label} (Cópia)");
        CatalogImpellers.Add(copy);
        SelectedCatalogImpeller = copy;
        TrySaveImpellerCatalog($"Impelidor duplicado como '{copy.Label}'.");
    }

    private string GetUniqueCatalogName(string baseName)
    {
        var name = baseName;
        var counter = 1;
        while (CatalogImpellers.Any(i => string.Equals(i.Label, name, StringComparison.OrdinalIgnoreCase)))
        {
            counter++;
            name = $"{baseName} {counter}";
        }
        return name;
    }

    [RelayCommand]
    public void AddCatalogImpellerToAssembly(Impeller? catalogItem = null)
    {
        var source = catalogItem ?? SelectedCatalogImpeller;
        if (!CanEditPlan || source is null)
        {
            return;
        }

        var item = source.Clone();
        item.ClearanceM = Impellers.Count == 0 ? item.ClearanceM : Impellers.Max(i => i.ClearanceM) + (item.DiameterM > 0 ? item.DiameterM : 0.065);
        item.StageIndex = Impellers.Count;
        Impellers.Add(item);
        SelectedImpeller = item;
        NotifyGeometryState();
        StatusMessage = $"Impelidor '{item.Label}' adicionado ao eixo.";
    }

    [RelayCommand]
    private void OpenImpellerCatalog()
    {
        if (_dialogs is null)
        {
            return;
        }

        _dialogs.ShowImpellerCatalog(
            CatalogImpellers,
            targetStage: null,
            out _,
            saveCatalog: () => SaveCatalog(),
            onAddToAssembly: (item) => AddCatalogImpellerToAssembly(item));
    }

    [RelayCommand]
    private void OpenCaptureSettings()
    {
        if (_dialogs is null)
        {
            return;
        }

        if (_dialogs.ShowCaptureSettings(this))
        {
            PersistCaptureSettings();
        }
        else if (CurrentTest is { } doc)
        {
            LoadSettingsFields(doc.Settings);
        }
    }

    /// <summary>True when the criteria edited on the page differ from what the open assay holds.</summary>
    public bool HasUnsavedCaptureSettings => CurrentTest is { } doc && BuildEditedSettings() != doc.Settings;

    /// <summary>
    /// True when anything <c>Salvar Preferências</c> would write differs from the open assay — the
    /// criteria, the fluid or the geometry. The conditions table is not included because every
    /// edit to it already persists itself.
    /// </summary>
    public bool HasUnsavedSetup => CurrentTest is { } doc && !_isLoadingTest &&
        (BuildEditedSettings() != doc.Settings ||
         doc.Fluid.DensityKgM3 != DensityKgM3 || doc.Fluid.ViscosityPaS != ViscosityPaS || doc.Fluid.TemperatureC != TemperatureC ||
         PowerTestFileContracts.Fingerprint(BuildGeometry()) != PowerTestFileContracts.Fingerprint(doc.Geometry));

    /// <summary>
    /// Commits the criteria alone — not the conditions, fluid or geometry, which stay behind
    /// <see cref="CanEditPlan"/>. Works with the assay stopped <em>or running</em>: the runner reads
    /// <c>Settings</c> from this same document at every phase, so a longer vent time-out applies to
    /// the next <c>VentStabilizing</c>. (A capture already in progress keeps the statistical gates it
    /// started with.) The change is journalled field by field in <c>eventos.jsonl</c>.
    /// </summary>
    public bool PersistCaptureSettings()
    {
        if (CurrentTest is not { } doc)
        {
            return false;
        }

        var error = ValidateCaptureSettings();
        if (error.Length > 0)
        {
            ShowError(error);
            return false;
        }

        var settings = BuildEditedSettings();
        if (settings == doc.Settings)
        {
            return true;
        }

        try
        {
            var diff = PowerTestSettingsDiff.Describe(doc.Settings, settings);
            doc.SettingsRevision++;
            doc.Settings = settings;
            doc.LastModifiedUtc = DateTimeOffset.UtcNow;
            _store.SaveTestManifest(doc);
            _store.AppendEventLog(doc.FolderName, new PowerTestEventLogEntry(
                DateTimeOffset.UtcNow, "SettingsChanged",
                $"Critérios alterados pelo operador (revisão {doc.SettingsRevision}).", diff));
            ValidationMessage = $"Critérios salvos (revisão {doc.SettingsRevision}).";
            StatusMessage = ValidationMessage;
            _lastPreflightTick = 0;
            RefreshPreflight();
            NotifyDocumentState();
            return true;
        }
        catch (Exception ex)
        {
            ShowError($"Não foi possível salvar os critérios: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Saves what the operator left unsaved before the application exits: the whole setup when the
    /// assay is idle, only the criteria when it is running (the rest is locked while it runs).
    /// </summary>
    public bool TrySaveSetupForExit(out string error)
    {
        error = "";
        if (CurrentTest is null)
        {
            return true;
        }

        if (IsRunning || IsTareRunning || CurrentTest.Status == PowerTestStatus.Completed)
        {
            return PersistCaptureSettings();
        }

        if (!TryPersist(out error))
        {
            return false;
        }

        RefreshTests();
        NotifyDocumentState();
        return true;
    }

    [RelayCommand]
    private void SelectImpellerForStage(Impeller? stage)
    {
        if (!CanEditPlan)
        {
            return;
        }

        var target = stage ?? SelectedImpeller;
        if (target is null)
        {
            return;
        }

        if (_dialogs != null && _dialogs.ShowImpellerCatalog(
            CatalogImpellers,
            targetStage: target,
            out var chosen,
            saveCatalog: () => SaveCatalog(),
            onAddToAssembly: (item) => AddCatalogImpellerToAssembly(item)))
        {
            if (chosen != null)
            {
                ApplyCatalogImpellerToStage(target, chosen);
            }
        }
    }

    public void ApplyCatalogImpellerToStage(Impeller stage, Impeller catalogModel)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(catalogModel);

        _applyingImpellerDefaults = true;
        try
        {
            stage.Label = catalogModel.Label;
            stage.Type = catalogModel.Type;
            stage.DiameterM = catalogModel.DiameterM;
            stage.BladeCount = catalogModel.BladeCount;
            stage.LiteratureNp = catalogModel.LiteratureNp;
            if (stage.ClearanceM <= 0 && catalogModel.ClearanceM > 0)
            {
                stage.ClearanceM = catalogModel.ClearanceM;
            }
        }
        finally
        {
            _applyingImpellerDefaults = false;
        }

        NotifyGeometryState();
        ReprocessIfActive();
        StatusMessage = $"Estágio #{stage.StageIndex} atualizado para '{stage.Label}'.";
    }

    [RelayCommand]
    private void AddCondition()
    {
        if (!CanEditPlan)
        {
            return;
        }

        var rpm = Conditions.Count == 0 ? Math.Max(MinRpm, 300) : Math.Min(MaxRpm, Conditions.Max(c => c.AgitationRpm) + StepRpm);
        var condition = new PowerCondition { AgitationRpm = rpm, GasFlowLpm = 0.0, OrderIndex = Conditions.Count };
        Conditions.Add(condition);
        SelectedCondition = condition;
        PersistConditionPlan("Condição adicionada e salva.");
        NotifyDocumentState();
    }

    [RelayCommand]
    private void RemoveCondition()
    {
        if (!CanEditPlan || SelectedCondition is null)
        {
            return;
        }

        Conditions.Remove(SelectedCondition);
        NormalizeConditionOrder();
        SelectedCondition = Conditions.FirstOrDefault();
        PersistConditionPlan("Condição removida e tabela salva.");
        NotifyDocumentState();
    }

    [RelayCommand] private void MoveConditionUp() => MoveSelectedCondition(-1);
    [RelayCommand] private void MoveConditionDown() => MoveSelectedCondition(1);

    private void MoveSelectedCondition(int delta)
    {
        if (!CanEditPlan || SelectedCondition is null)
        {
            return;
        }

        _suppressConditionPersistence = true;
        try
        {
            MoveItem(Conditions, SelectedCondition, delta, NormalizeConditionOrder);
        }
        finally
        {
            _suppressConditionPersistence = false;
        }
        PersistConditionPlan("Ordem das condições atualizada e salva.");
    }

    /// <summary>
    /// kLa maps available to seed the condition table (§18.3 step 3.1). Empty when the workspace has
    /// no kLa experiment yet, or when the view model was built without a kLa store.
    /// </summary>
    public ObservableCollection<KlaMapOptionViewModel> AvailableKlaMaps { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImportFromKlaMap))]
    public partial KlaMapOptionViewModel? SelectedKlaMapForImport { get; set; }

    /// <summary>
    /// Adds one ungassed point per rotation ahead of the imported gassed ones. Without a P₀ at the
    /// same rotation there is nothing to divide by, and P_G/P₀ falls back or goes missing (§4.5).
    /// </summary>
    [ObservableProperty]
    public partial bool ImportUngassedReferences { get; set; } = true;

    [ObservableProperty]
    public partial int ImportReplicates { get; set; } = 1;

    public bool CanImportFromKlaMap => SelectedKlaMapForImport is not null && CanEditPlan;

    [RelayCommand]
    private async Task RefreshKlaMapsAsync()
    {
        AvailableKlaMaps.Clear();

        if (_klaStore is null)
        {
            ValidationMessage = "Repositório de mapas de kLa indisponível nesta sessão.";
            return;
        }

        try
        {
            var experiments = await _klaStore.LoadExperimentsAsync();
            foreach (var experiment in experiments.Where(e => e.Snapshot.Anchors.Length > 0))
            {
                AvailableKlaMaps.Add(new KlaMapOptionViewModel(
                    experiment.Snapshot.Id,
                    experiment.Snapshot.Name,
                    experiment.Snapshot.Anchors.Length));
            }

            ValidationMessage = AvailableKlaMaps.Count == 0
                ? "Nenhum mapa de kLa com âncoras foi encontrado no workspace."
                : "";
        }
        catch (Exception ex)
        {
            ValidationMessage = $"Falha ao ler os mapas de kLa: {ex.Message}";
        }

        OnPropertyChanged(nameof(CanImportFromKlaMap));
    }

    partial void OnSelectedKlaMapForImportChanged(KlaMapOptionViewModel? value)
    {
        OnPropertyChanged(nameof(CanImportFromKlaMap));
    }

    /// <summary>
    /// Gera a matriz N × Qg na tabela de condições a partir das faixas da Captura Automática.
    /// Qg = 0 gera condições sem aeração; Qg > 0 gera condições com aeração.
    /// </summary>
    [RelayCommand]
    public void GenerateConditionsPlan()
    {
        if (!CanEditPlan)
        {
            ValidationMessage = "A tabela de condições só pode ser editada com o ensaio aberto e parado.";
            return;
        }

        if (!double.IsFinite(MinRpm) || !double.IsFinite(MaxRpm) || !double.IsFinite(StepRpm) ||
            MinRpm < 15 || MaxRpm > 1000 || MaxRpm < MinRpm || (MinRpm < MaxRpm && StepRpm <= 0))
        {
            ValidationMessage = "Faixa de rotação inválida: use 15 a 1000 rpm e passo positivo.";
            return;
        }

        if (!double.IsFinite(MinFlowLpm) || !double.IsFinite(MaxFlowLpm) || !double.IsFinite(StepFlowLpm) ||
            MinFlowLpm < 0 || MaxFlowLpm < MinFlowLpm || (MinFlowLpm < MaxFlowLpm && StepFlowLpm <= 0))
        {
            ValidationMessage = "Faixa de vazão inválida: use Qg ≥ 0 L/min, Qg máx ≥ Qg mín e passo positivo.";
            return;
        }

        if (Conditions.Count > 0 && _dialogs?.Confirm(
                "Substituir tabela",
                $"A geração de condições substituirá as {Conditions.Count} linha(s) atuais da tabela. Deseja continuar?",
                "Substituir",
                "Cancelar") == false)
        {
            return;
        }

        Conditions.Clear();

        var rpmRange = BuildInclusiveRange(MinRpm, MaxRpm, Math.Max(1.0, StepRpm));
        var flowRange = BuildInclusiveRange(MinFlowLpm, MaxFlowLpm, Math.Max(0.01, StepFlowLpm));

        var index = 0;
        foreach (var rpm in rpmRange)
        {
            foreach (var qg in flowRange)
            {
                var isGassed = qg > 0.0001;
                var condition = new PowerCondition
                {
                    ConditionId = Guid.NewGuid(),
                    OrderIndex = index++,
                    AgitationRpm = Math.Round(rpm, 1),
                    GasFlowLpm = isGassed ? Math.Round(qg, 2) : 0.0,
                    GasMode = isGassed ? PowerGasMode.Gassed : PowerGasMode.Ungassed,
                    FlowUnit = FlowInputUnit.Lpm,
                    RequestedReplicates = 1,
                    Origin = PowerConditionOrigin.Manual,
                    Status = PowerConditionStatus.Pending,
                };
                Conditions.Add(condition);
            }
        }

        SelectedCondition = Conditions.FirstOrDefault();
        PersistConditionPlan($"{Conditions.Count} condição(ões) gerada(s) com sucesso.");
        UpdateKlaEfficiencyComparison();
    }

    /// <summary>
    /// Remove todas as linhas da tabela de condições.
    /// </summary>
    [RelayCommand]
    public void ClearAllConditions()
    {
        if (!CanEditPlan)
        {
            ValidationMessage = "A tabela de condições só pode ser editada com o ensaio aberto e parado.";
            return;
        }

        if (Conditions.Count == 0)
        {
            return;
        }

        if (_dialogs?.Confirm(
                "Remover todos os pontos",
                $"Deseja remover todas as {Conditions.Count} condição(ões) da tabela?",
                "Remover todos",
                "Cancelar",
                isDanger: true) == false)
        {
            return;
        }

        Conditions.Clear();
        SelectedCondition = null;
        PersistConditionPlan("Todas as condições foram removidas da tabela.");
        UpdateKlaEfficiencyComparison();
    }

    /// <summary>
    /// Importa e vincula um mapa de kLa ao ensaio de potência SEM substituir a tabela de condições.
    /// Reconstrói a superfície contínua e calcula a comparação/eficiência na região de controle.
    /// </summary>
    [RelayCommand]
    private async Task ImportConditionsFromKlaMapAsync()
    {
        if (!CanEditPlan)
        {
            ValidationMessage = "O ensaio precisa estar aberto e parado para vincular um mapa de kLa.";
            return;
        }

        if (_klaStore is null || SelectedKlaMapForImport is null)
        {
            ValidationMessage = "Selecione um mapa de kLa para importar.";
            return;
        }

        try
        {
            var experiments = await _klaStore.LoadExperimentsAsync();
            var klaDocument = experiments.FirstOrDefault(e => e.Snapshot.Id == SelectedKlaMapForImport.Id);
            if (klaDocument is null)
            {
                ValidationMessage = "O mapa de kLa selecionado não está mais no workspace.";
                await RefreshKlaMapsAsync();
                return;
            }

            var mapName = klaDocument.Snapshot.Name;
            var mapId = klaDocument.Snapshot.Id;

            CurrentTest!.LinkedMap = new PowerMapReference
            {
                MapId = mapId,
                MapName = mapName,
                MapFingerprint = klaDocument.Snapshot.ScientificFingerprint(),
                LinkedAtUtc = DateTimeOffset.UtcNow,
            };
            _store.SaveTestManifest(CurrentTest);

            _linkedKlaDocument = klaDocument;
            try
            {
                var engine = new KlaMappingEngine();
                _linkedKlaSurface = engine.Reconstruct(klaDocument.Snapshot);
            }
            catch
            {
                _linkedKlaSurface = null;
            }

            UpdateKlaEfficiencyComparison();

            ValidationMessage = $"Mapa '{mapName}' vinculado com sucesso. {ControlRegionSummary}";
            StatusMessage = ValidationMessage;
            OnPropertyChanged(nameof(CanImportFromKlaMap));
        }
        catch (Exception ex)
        {
            ValidationMessage = $"Falha ao importar mapa de kLa: {ex.Message}";
        }
    }

    private Task? _linkedKlaLoad;

    private async Task LoadLinkedKlaMapAsync(Guid mapId)
    {
        KlaExperimentDocument? document = null;
        KlaSurface? surface = null;
        try
        {
            var store = _klaStore!;
            var experiments = await Task.Run(() => store.LoadExperimentsAsync()).ConfigureAwait(true);
            document = experiments.FirstOrDefault(e => e.Snapshot.Id == mapId);
            if (document is not null)
            {
                try
                {
                    surface = await Task.Run(() => new KlaMappingEngine().Reconstruct(document.Snapshot)).ConfigureAwait(true);
                }
                catch
                {
                    surface = null;
                }
            }
        }
        catch
        {
            // Best effort: the summary says the file was not found.
        }

        if (_disposed)
        {
            return;
        }

        RunOnUi(() =>
        {
            if (CurrentTest?.LinkedMap?.MapId != mapId)
            {
                return;
            }
            _linkedKlaDocument = document;
            _linkedKlaSurface = surface;
            UpdateKlaEfficiencyComparison();
        });
    }

    public void UpdateKlaEfficiencyComparison()
    {
        if (CurrentTest?.LinkedMap is null)
        {
            HasLinkedKlaMap = false;
            LinkedKlaMapName = "";
            ControlRegionSummary = "Nenhum mapa de kLa vinculado.";
            AverageKlaEfficiency = null;
            KlaEfficiencyItems.Clear();
            return;
        }

        HasLinkedKlaMap = true;
        LinkedKlaMapName = CurrentTest.LinkedMap.MapName;

        if (_linkedKlaDocument is null && _klaStore is not null && _linkedKlaLoad is null)
        {
            // The map is read from disk and its surface reconstructed off the UI thread; the
            // comparison is recomputed when it lands. Used to block the first refresh after opening
            // an assay with a linked map.
            _linkedKlaLoad = LoadLinkedKlaMapAsync(CurrentTest.LinkedMap.MapId);
            ControlRegionSummary = $"Mapa '{CurrentTest.LinkedMap.MapName}' vinculado · carregando…";
            return;
        }

        if (_linkedKlaDocument is null)
        {
            ControlRegionSummary = _linkedKlaLoad is { IsCompleted: false }
                ? $"Mapa '{CurrentTest.LinkedMap.MapName}' vinculado · carregando…"
                : $"Mapa '{CurrentTest.LinkedMap.MapName}' vinculado, mas arquivo não encontrado.";
            return;
        }

        // kLa domain
        var anchors = _linkedKlaDocument.Snapshot.Anchors;
        var klaDomain = _linkedKlaDocument.Snapshot.Domain;

        double klaMinRpm = anchors.Length > 0 ? anchors.Min(a => a.AgitationRpm) : klaDomain.AgitationMinimumRpm;
        double klaMaxRpm = anchors.Length > 0 ? anchors.Max(a => a.AgitationRpm) : klaDomain.AgitationMaximumRpm;
        double klaMinQg = anchors.Length > 0 ? anchors.Min(a => a.AirflowLpm) : klaDomain.AirflowMinimumLpm;
        double klaMaxQg = anchors.Length > 0 ? anchors.Max(a => a.AirflowLpm) : klaDomain.AirflowMaximumLpm;

        // Power domain
        var conditionsWithGas = Conditions.ToList();
        var acceptedRuns = CurrentTest?.Runs.Where(r => r.Phase == PowerRunPhase.Accepted).ToList() ?? [];
        if (conditionsWithGas.Count == 0 && acceptedRuns.Count == 0)
        {
            ControlRegionSummary = $"Mapa '{LinkedKlaMapName}' vinculado. Adicione condições para avaliar a região de controle.";
            KlaEfficiencyItems.Clear();
            AverageKlaEfficiency = null;
            return;
        }

        double pwrMinRpm = conditionsWithGas.Count > 0 ? conditionsWithGas.Min(c => c.AgitationRpm) : acceptedRuns.Min(r => r.AgitationRpm);
        double pwrMaxRpm = conditionsWithGas.Count > 0 ? conditionsWithGas.Max(c => c.AgitationRpm) : acceptedRuns.Max(r => r.AgitationRpm);
        double pwrMinQg = conditionsWithGas.Count > 0 ? conditionsWithGas.Min(c => c.GasFlowLpm ?? 0.0) : acceptedRuns.Min(r => r.GasFlowLpm ?? 0.0);
        double pwrMaxQg = conditionsWithGas.Count > 0 ? conditionsWithGas.Max(c => c.GasFlowLpm ?? 0.0) : acceptedRuns.Max(r => r.GasFlowLpm ?? 0.0);

        // Control region: intersection of conditions
        double ctrlMinRpm = Math.Max(klaMinRpm, pwrMinRpm);
        double ctrlMaxRpm = Math.Min(klaMaxRpm, pwrMaxRpm);
        double ctrlMinQg = Math.Max(klaMinQg, pwrMinQg);
        double ctrlMaxQg = Math.Min(klaMaxQg, pwrMaxQg);

        bool hasIntersection = ctrlMinRpm <= ctrlMaxRpm && ctrlMinQg <= ctrlMaxQg;
        ControlRegionSummary = hasIntersection
            ? $"Intersecção: N {ctrlMinRpm:F0}–{ctrlMaxRpm:F0} rpm · Qg {ctrlMinQg:F1}–{ctrlMaxQg:F1} L/min"
            : "Sem intersecção entre as faixas operacionais dos dois testes.";

        KlaEfficiencyItems.Clear();

        // Evaluate distinct points from Conditions (or from accepted runs)
        var pointsToEvaluate = conditionsWithGas
            .Select(c => (N: c.AgitationRpm, Qg: c.GasFlowLpm ?? 0.0))
            .Distinct()
            .OrderBy(p => p.N)
            .ThenBy(p => p.Qg)
            .ToList();

        if (pointsToEvaluate.Count == 0)
        {
            pointsToEvaluate = acceptedRuns
                .Select(r => (N: r.AgitationRpm, Qg: r.GasFlowLpm ?? 0.0))
                .Distinct()
                .OrderBy(p => p.N)
                .ThenBy(p => p.Qg)
                .ToList();
        }

        var vesselVolM3 = LiquidVolumeL > 0 ? LiquidVolumeL / 1000.0 : 0.010;

        foreach (var pt in pointsToEvaluate)
        {
            bool inControl = hasIntersection &&
                pt.N >= ctrlMinRpm - 0.5 && pt.N <= ctrlMaxRpm + 0.5 &&
                pt.Qg >= ctrlMinQg - 0.02 && pt.Qg <= ctrlMaxQg + 0.02;

            double? klaInterp = null;
            if (_linkedKlaSurface is not null && klaDomain.AirflowMaximumLpm > klaDomain.AirflowMinimumLpm && klaDomain.AgitationMaximumRpm > klaDomain.AgitationMinimumRpm)
            {
                var normQ = klaDomain.NormalizeAirflow(pt.Qg);
                var normN = klaDomain.NormalizeAgitation(pt.N);
                var surfaceVal = _linkedKlaSurface.EvaluateNormalized(normQ, normN);
                if (double.IsFinite(surfaceVal.Value) && surfaceVal.Value >= 0)
                {
                    klaInterp = surfaceVal.Value;
                }
            }

            if (!klaInterp.HasValue && anchors.Length > 0)
            {
                var exactAnchor = anchors.FirstOrDefault(a =>
                    Math.Abs(a.AgitationRpm - pt.N) <= 1.0 &&
                    Math.Abs(a.AirflowLpm - pt.Qg) <= 0.05);
                if (exactAnchor is not null)
                {
                    klaInterp = exactAnchor.KlaPerHour;
                }
            }

            // Find matching accepted run in acceptedRuns
            var matchingRun = acceptedRuns.FirstOrDefault(r =>
                Math.Abs(r.AgitationRpm - pt.N) <= 1.0 &&
                Math.Abs((r.GasFlowLpm ?? 0.0) - pt.Qg) <= 0.05);

            double? netPowerW = matchingRun is not null
                ? (matchingRun.NetPowerW ?? matchingRun.GassedPowerW)
                : null;
            if (netPowerW.HasValue && !double.IsFinite(netPowerW.Value))
            {
                netPowerW = null;
            }

            double? pvWm3 = (netPowerW.HasValue && vesselVolM3 > 0) ? (netPowerW.Value / vesselVolM3) : null;
            double? efficiency = (klaInterp.HasValue && pvWm3.HasValue && pvWm3.Value > 0)
                ? (klaInterp.Value / (pvWm3.Value / 1000.0)) // h⁻¹ / (kW/m³)
                : null;

            KlaEfficiencyItems.Add(new KlaEfficiencyComparisonItem
            {
                AgitationRpm = pt.N,
                GasFlowLpm = pt.Qg,
                KlaInterpolatedPerHour = klaInterp.HasValue ? Math.Round(klaInterp.Value, 2) : null,
                NetPowerW = netPowerW.HasValue ? Math.Round(netPowerW.Value, 2) : null,
                VolumetricPowerWm3 = pvWm3.HasValue ? Math.Round(pvWm3.Value, 1) : null,
                SpecificEfficiency = efficiency.HasValue ? Math.Round(efficiency.Value, 2) : null,
                IsInControlRegion = inControl,
            });
        }

        var controlEfficiencies = KlaEfficiencyItems
            .Where(i => i.IsInControlRegion && i.SpecificEfficiency.HasValue)
            .Select(i => i.SpecificEfficiency!.Value)
            .ToList();

        AverageKlaEfficiency = controlEfficiencies.Count > 0 ? Math.Round(controlEfficiencies.Average(), 2) : null;
    }

    [RelayCommand]
    private void GenerateSweep()
    {
        if (!CanEditPlan)
        {
            return;
        }

        switch (SelectedSweepType)
        {
            case PowerSweepType.VariableNConstantQg:
                {
                    if (!double.IsFinite(MinRpm) || !double.IsFinite(MaxRpm) || !double.IsFinite(StepRpm) ||
                        MinRpm < 15 || MaxRpm > 1000 || MaxRpm < MinRpm || StepRpm < 5)
                    {
                        ValidationMessage = "Varredura N inválida: use 15–1000 rpm e passo mínimo de 5 rpm.";
                        return;
                    }
                    if (!double.IsFinite(SweepConstantQgLpm) || SweepConstantQgLpm < 0)
                    {
                        ValidationMessage = "Vazão Qg fixa inválida: deve ser maior ou igual a zero.";
                        return;
                    }

                    if (Conditions.Count > 0 && _dialogs?.Confirm("Substituir tabela", "A varredura substituirá as condições atuais. Continuar?", "Substituir", "Cancelar") == false)
                    {
                        return;
                    }

                    Conditions.Clear();
                    var index = 0;
                    var hasGas = SweepConstantQgLpm > 0 && SweepGasMode != PowerGasMode.Ungassed;
                    var mode = hasGas ? SweepGasMode : PowerGasMode.Ungassed;

                    foreach (var rpm in BuildInclusiveRange(MinRpm, MaxRpm, StepRpm))
                    {
                        var cond = new PowerCondition
                        {
                            FlowUnit = FlowInputUnit.Lpm,
                            AgitationRpm = rpm,
                            GasFlowLpm = hasGas ? SweepConstantQgLpm : 0.0,
                            GasMode = mode,
                            OrderIndex = index++,
                            Origin = PowerConditionOrigin.Manual,
                        };
                        Conditions.Add(cond);
                    }
                    break;
                }

            case PowerSweepType.VariableQgConstantN:
                {
                    if (!double.IsFinite(SweepConstantRpm) || SweepConstantRpm < 15 || SweepConstantRpm > 1000)
                    {
                        ValidationMessage = "Rotação N fixa inválida: use 15–1000 rpm.";
                        return;
                    }
                    if (!double.IsFinite(SweepStartQgLpm) || !double.IsFinite(SweepEndQgLpm) || !double.IsFinite(SweepStepQgLpm) ||
                        SweepStartQgLpm < 0 || SweepEndQgLpm < SweepStartQgLpm || SweepStepQgLpm <= 0)
                    {
                        ValidationMessage = "Varredura Qg inválida: use Qg ≥ 0 e passo positivo.";
                        return;
                    }

                    if (Conditions.Count > 0 && _dialogs?.Confirm("Substituir tabela", "A varredura substituirá as condições atuais. Continuar?", "Substituir", "Cancelar") == false)
                    {
                        return;
                    }

                    Conditions.Clear();
                    var index = 0;
                    foreach (var qg in BuildInclusiveRange(SweepStartQgLpm, SweepEndQgLpm, SweepStepQgLpm))
                    {
                        var hasGas = qg > 0 && SweepGasMode != PowerGasMode.Ungassed;
                        var cond = new PowerCondition
                        {
                            FlowUnit = FlowInputUnit.Lpm,
                            AgitationRpm = SweepConstantRpm,
                            GasFlowLpm = hasGas ? qg : 0.0,
                            GasMode = hasGas ? SweepGasMode : PowerGasMode.Ungassed,
                            OrderIndex = index++,
                            Origin = PowerConditionOrigin.Manual,
                        };
                        Conditions.Add(cond);
                    }
                    break;
                }

            case PowerSweepType.MatrixNByQg:
                {
                    if (!double.IsFinite(MinRpm) || !double.IsFinite(MaxRpm) || !double.IsFinite(StepRpm) ||
                        MinRpm < 15 || MaxRpm > 1000 || MaxRpm < MinRpm || StepRpm < 5)
                    {
                        ValidationMessage = "Varredura N inválida: use 15–1000 rpm e passo mínimo de 5 rpm.";
                        return;
                    }
                    if (!double.IsFinite(SweepStartQgLpm) || !double.IsFinite(SweepEndQgLpm) || !double.IsFinite(SweepStepQgLpm) ||
                        SweepStartQgLpm < 0 || SweepEndQgLpm < SweepStartQgLpm || SweepStepQgLpm <= 0)
                    {
                        ValidationMessage = "Varredura Qg inválida: use Qg ≥ 0 e passo positivo.";
                        return;
                    }

                    if (Conditions.Count > 0 && _dialogs?.Confirm("Substituir tabela", "A varredura substituirá as condições atuais. Continuar?", "Substituir", "Cancelar") == false)
                    {
                        return;
                    }

                    Conditions.Clear();
                    var index = 0;
                    foreach (var rpm in BuildInclusiveRange(MinRpm, MaxRpm, StepRpm))
                    {
                        foreach (var qg in BuildInclusiveRange(SweepStartQgLpm, SweepEndQgLpm, SweepStepQgLpm))
                        {
                            var hasGas = qg > 0 && SweepGasMode != PowerGasMode.Ungassed;
                            var cond = new PowerCondition
                            {
                                FlowUnit = FlowInputUnit.Lpm,
                                AgitationRpm = rpm,
                                GasFlowLpm = hasGas ? qg : 0.0,
                                GasMode = hasGas ? SweepGasMode : PowerGasMode.Ungassed,
                                OrderIndex = index++,
                                Origin = PowerConditionOrigin.Manual,
                            };
                            Conditions.Add(cond);
                        }
                    }
                    break;
                }
        }

        SelectedCondition = Conditions.FirstOrDefault();
        if (CurrentTest is not null)
        {
            CurrentTest.LinkedMap = null;
        }
        PersistConditionPlan($"{Conditions.Count} condição(ões) gerada(s) e exibida(s) na tabela acima.");
    }

    private void RegenerateAutomaticPlanFromControls()
    {
        if (_isLoadingTest || _suppressConditionPersistence || !CanEditPlan ||
            Conditions.Any(condition => condition.Origin == PowerConditionOrigin.Map) ||
            Conditions.Any(condition => condition.CompletedReplicates > 0 || condition.AcceptedReplicates > 0))
        {
            return;
        }

        List<PowerCondition> rows;
        switch (SelectedSweepType)
        {
            case PowerSweepType.VariableNConstantQg:
                if (!IsValidRotationRange(MinRpm, MaxRpm, StepRpm) ||
                    !double.IsFinite(SweepConstantQgLpm) || SweepConstantQgLpm < 0)
                {
                    ValidationMessage = "Plano automático incompleto: revise a faixa N e a vazão Qg fixa.";
                    return;
                }
                var hasConstantGas = SweepConstantQgLpm > 0 && SweepGasMode != PowerGasMode.Ungassed;
                rows = BuildRotationConditions(
                    MinRpm,
                    MaxRpm,
                    StepRpm,
                    hasConstantGas ? SweepConstantQgLpm : null,
                    hasConstantGas ? SweepGasMode : PowerGasMode.Ungassed);
                break;

            case PowerSweepType.VariableQgConstantN:
                if (!IsValidConstantRpm(SweepConstantRpm) || !IsValidFlowRange())
                {
                    ValidationMessage = "Plano automático incompleto: revise N fixa e a faixa Qg.";
                    return;
                }
                rows = BuildFlowConditions(SweepConstantRpm);
                break;

            case PowerSweepType.MatrixNByQg:
                if (!IsValidRotationRange(MinRpm, MaxRpm, StepRpm) || !IsValidFlowRange())
                {
                    ValidationMessage = "Plano automático incompleto: revise as faixas N e Qg.";
                    return;
                }
                rows = [];
                foreach (var rpm in BuildInclusiveRange(MinRpm, MaxRpm, StepRpm))
                {
                    rows.AddRange(BuildFlowConditions(rpm));
                }
                for (var index = 0; index < rows.Count; index++)
                {
                    rows[index].OrderIndex = index;
                }
                break;

            default:
                return;
        }

        ReplaceConditionRows(rows);
        if (CurrentTest is not null)
        {
            CurrentTest.LinkedMap = null;
        }
        PersistConditionPlan(
            $"Tabela atualizada automaticamente pelos parâmetros de captura: {rows.Count} condição(ões).",
            showMessage: true);
    }

    private static bool IsValidRotationRange(double minRpm, double maxRpm, double stepRpm) =>
        double.IsFinite(minRpm) && double.IsFinite(maxRpm) && double.IsFinite(stepRpm) &&
        minRpm >= 15 && maxRpm <= 1000 && maxRpm >= minRpm && stepRpm >= 5;

    private static List<PowerCondition> BuildRotationConditions(
        double minRpm,
        double maxRpm,
        double stepRpm,
        double? gasFlowLpm,
        PowerGasMode gasMode)
    {
        var rows = new List<PowerCondition>();
        foreach (var rpm in BuildInclusiveRange(minRpm, maxRpm, stepRpm))
        {
            rows.Add(new PowerCondition
            {
                AgitationRpm = rpm,
                GasFlowLpm = gasFlowLpm,
                GasMode = gasMode,
                FlowUnit = FlowInputUnit.Lpm,
                OrderIndex = rows.Count,
                Origin = PowerConditionOrigin.Manual,
            });
        }

        return rows;
    }

    private bool IsValidFlowRange() =>
        double.IsFinite(SweepStartQgLpm) && double.IsFinite(SweepEndQgLpm) &&
        double.IsFinite(SweepStepQgLpm) && SweepStartQgLpm >= 0 &&
        SweepEndQgLpm >= SweepStartQgLpm && SweepStepQgLpm > 0;

    private static bool IsValidConstantRpm(double rpm) =>
        double.IsFinite(rpm) && rpm is >= 15 and <= 1000;

    private List<PowerCondition> BuildFlowConditions(double rpm)
    {
        var rows = new List<PowerCondition>();
        foreach (var qg in BuildInclusiveRange(SweepStartQgLpm, SweepEndQgLpm, SweepStepQgLpm))
        {
            var hasGas = qg > 0 && SweepGasMode != PowerGasMode.Ungassed;
            rows.Add(new PowerCondition
            {
                AgitationRpm = rpm,
                GasFlowLpm = hasGas ? qg : 0.0,
                GasMode = hasGas ? SweepGasMode : PowerGasMode.Ungassed,
                FlowUnit = FlowInputUnit.Lpm,
                OrderIndex = rows.Count,
                Origin = PowerConditionOrigin.Manual,
            });
        }
        return rows;
    }

    private static IReadOnlyList<double> BuildInclusiveRange(double start, double end, double step)
    {
        var values = new List<double>();
        for (var value = start; value <= end + 1e-9; value += step)
        {
            values.Add(value);
        }

        if (values.Count == 0 || Math.Abs(values[^1] - end) > 1e-9)
        {
            values.Add(end);
        }
        return values;
    }

    private void ReplaceConditionRows(IEnumerable<PowerCondition> rows)
    {
        _suppressConditionPersistence = true;
        try
        {
            Conditions.Clear();
            foreach (var row in rows)
            {
                Conditions.Add(row);
            }
            NormalizeConditionOrder();
            SelectedCondition = Conditions.FirstOrDefault();
        }
        finally
        {
            _suppressConditionPersistence = false;
        }
    }

    private void PersistConditionPlan(string successMessage, bool showMessage = true)
    {
        if (CurrentTest is null)
        {
            return;
        }

        try
        {
            NormalizeConditionOrder();
            var settings = BuildEditedSettings();
            if (CurrentTest.Settings != settings)
            {
                CurrentTest.SettingsRevision++;
                CurrentTest.Settings = settings;
            }
            CurrentTest.Conditions = Conditions.Select(condition => condition.Clone()).ToList();
            CurrentTest.LastModifiedUtc = DateTimeOffset.UtcNow;
            _store.SaveConditionsTable(CurrentTest.FolderName, CurrentTest.Conditions);
            _store.SaveTestManifest(CurrentTest);
            if (showMessage)
            {
                ValidationMessage = successMessage;
                StatusMessage = successMessage;
            }
            _lastPreflightTick = 0;
            RefreshPreflight();
            UpdateKlaEfficiencyComparison();
            NotifyDocumentState();
        }
        catch (Exception ex)
        {
            ValidationMessage = $"Não foi possível salvar a tabela de condições: {ex.Message}";
            StatusMessage = ValidationMessage;
        }
    }

    [RelayCommand]
    private void ToggleSkipCondition()
    {
        if (CurrentTest is null || SelectedCondition is null || IsRunning || IsTareRunning)
        {
            return;
        }

        SelectedCondition.Status = SelectedCondition.Status == PowerConditionStatus.Skipped ? PowerConditionStatus.Pending : PowerConditionStatus.Skipped;
        TryPersist(out _);
        RefreshConditionRows();
    }

    [RelayCommand]
    private async Task StartOrContinueAsync()
    {
        if (_runner is null || CurrentTest is null || IsTareRunning)
        {
            return;
        }

        if (!TryPersist(out var error)) { ShowError(error); return; }
        try
        {
            if (_runner.IsPausedForLinkRecovery)
            {
                await _runner.ResumeAfterLinkRecoveryAsync();
            }
            else if (CurrentTest.Status == PowerTestStatus.Running)
            {
                var next = CurrentTest.Conditions.Where(c => c.Status != PowerConditionStatus.Skipped && c.AcceptedReplicates < c.RequestedReplicates).OrderBy(c => c.OrderIndex).FirstOrDefault();
                if (next is null)
                {
                    await _runner.CompleteTestAsync();
                }
                else
                {
                    await _runner.StartRunAsync(next, Math.Max(1, next.CompletedReplicates + 1));
                }
            }
            else
            {
                await _runner.StartTestAsync(CurrentTest);
            }
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    [RelayCommand]
    private async Task PauseResumeAsync()
    {
        if (_runner is null)
        {
            return;
        }

        try
        {
            if (_runner.IsPausedForMeasurement)
            {
                await _runner.ResumeAfterMeasurementAsync();
            }
            else if (_runner.IsPausedForLinkRecovery)
            {
                await _runner.ResumeAfterLinkRecoveryAsync();
            }
            else if (_runner.IsPausedByOperator)
            {
                await _runner.ResumeAsync();
            }
            else
            {
                await _runner.PauseAsync();
            }
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    [RelayCommand]
    private async Task StopAndReviewAsync()
    {
        if (_runner is not null)
        {
            await _runner.StopRunAndReviewAsync();
        }
    }

    [RelayCommand]
    private async Task SkipCurrentAsync()
    {
        if (_runner is null)
        {
            return;
        }

        if (_dialogs?.Confirm("Pular condição", "A captura corrente será encerrada e marcada como pulada.", "Pular", "Cancelar") == false)
        {
            return;
        }

        try { await _runner.SkipCurrentConditionAsync(); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    [RelayCommand]
    private async Task AcceptRunAsync()
    {
        if (_runner is not null)
        {
            try { await _runner.AcceptRunAsync(); } catch (Exception ex) { ShowError(ex.Message); }
        }
    }

    [RelayCommand]
    private async Task RejectRunAsync()
    {
        if (_runner is null)
        {
            return;
        }

        var reason = "Rejeitada pelo operador";
        if (_dialogs is not null && !_dialogs.PromptInput("Rejeitar corrida", "Motivo obrigatório:", out reason, reason))
        {
            return;
        }

        try { await _runner.RejectRunAsync(reason); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    [RelayCommand]
    private async Task RepeatRunAsync()
    {
        if (_runner is not null)
        {
            try { await _runner.RepeatRunAsync(); } catch (Exception ex) { ShowError(ex.Message); }
        }
    }

    [RelayCommand]
    private async Task SubmitManualEnergyAsync()
    {
        if (_runner is null || _dialogs is null || !_dialogs.PromptInput("Leitura elétrica", "Potência do wattímetro em W (separador decimal local ou ponto):", out var text))
        {
            return;
        }

        if (!TryParseUiDouble(text, out var watts) || watts < 0) { ShowError("Informe uma potência elétrica válida e não negativa."); return; }
        try { await _runner.SubmitManualEnergyAsync(watts); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    [RelayCommand]
    private async Task CompleteTestAsync()
    {
        if (_runner is not null)
        {
            try { await _runner.CompleteTestAsync(); } catch (Exception ex) { ShowError(ex.Message); }
        }
    }

    [RelayCommand]
    private async Task AbortTestAsync()
    {
        if (_runner is null || CurrentTest is null)
        {
            return;
        }

        if (_dialogs?.Confirm("Interromper ensaio", "O motor será estacionado em 15 rpm e o ensaio ficará Interrompido.", "Interromper", "Cancelar", true) == false)
        {
            return;
        }

        await _runner.AbortTestAsync("Interrompido pelo operador");
    }

    [RelayCommand]
    private void CycleResultStatus(PowerResultRow? row)
    {
        if (!CanChangeResultStatus || CurrentTest is null || row is null)
        {
            return;
        }

        var run = CurrentTest.Runs.FirstOrDefault(r => r.RunId == row.RunId);
        if (run is null)
        {
            return;
        }

        var nextPhase = NextManualResultPhase(run.Phase);
        var index = CurrentTest.Runs.IndexOf(run);
        CurrentTest.Runs[index] = run with { Phase = nextPhase };

        var condition = CurrentTest.Conditions.FirstOrDefault(c => c.ConditionId == run.ConditionId);
        if (condition is not null)
        {
            var conditionRuns = CurrentTest.Runs.Where(r => r.ConditionId == condition.ConditionId).ToArray();
            condition.AcceptedReplicates = conditionRuns.Count(r => r.Phase == PowerRunPhase.Accepted);
            condition.RejectedReplicates = conditionRuns.Count(r => r.Phase == PowerRunPhase.Rejected);
            condition.CompletedReplicates = condition.AcceptedReplicates + condition.RejectedReplicates;
            if (condition.Status != PowerConditionStatus.Skipped)
            {
                condition.Status = condition.AcceptedReplicates >= condition.RequestedReplicates
                    ? PowerConditionStatus.Completed
                    : PowerConditionStatus.Pending;
            }
        }

        _store.SaveConditionsTable(CurrentTest.FolderName, CurrentTest.Conditions);
        ReprocessScientificData();

        // A row action is also allowed to resolve a no-capture review. The persisted summary is
        // authoritative for this explicit operator override; clear only the runner's transient
        // review state so the warning strip does not remain stuck on screen.
        if (_runner?.IsInReview == true && _runner.CurrentRun?.RunId == run.RunId)
        {
            _runner.PrepareTest(CurrentTest);
        }

        StatusMessage = $"Status do ponto alterado para {PowerResultRow.StatusText(nextPhase)}.";
        NotifyDocumentState();
    }

    private static PowerRunPhase NextManualResultPhase(PowerRunPhase phase) => phase switch
    {
        PowerRunPhase.Accepted => PowerRunPhase.Rejected,
        PowerRunPhase.Rejected => PowerRunPhase.Accepted,
        PowerRunPhase.Captured or PowerRunPhase.Reviewing => PowerRunPhase.Rejected,
        _ => PowerRunPhase.Accepted,
    };

    private void ReprocessIfActive()
    {
        if (_isLoadingTest || CurrentTest is null)
        {
            return;
        }

        if (!FinitePositive(DensityKgM3) || !FinitePositive(ViscosityPaS) || !FinitePositive(VesselDiameterMm) || LiquidVolumeL <= 0)
        {
            return;
        }

        ReprocessScientificData();
    }

    /// <summary>
    /// Reprocesses the dimensionless groups still used by the power assay (Re, Np and Fr,
    /// together with P_G/P0) without modifying raw data.
    /// </summary>
    public void ReprocessScientificData()
    {
        if (CurrentTest is null)
        {
            return;
        }

        CurrentTest.Fluid = new FluidProperties
        {
            DensityKgM3 = DensityKgM3,
            ViscosityPaS = ViscosityPaS,
            TemperatureC = TemperatureC,
            PresetName = CurrentTest.Fluid?.PresetName ?? "Água / personalizado",
        };
        CurrentTest.Geometry = BuildGeometry();

        var geometry = CurrentTest.Geometry;
        var fluid = CurrentTest.Fluid;
        var refImpeller = geometry.Impellers.OrderByDescending(i => i.DiameterM).FirstOrDefault()
                          ?? new Impeller { Type = ImpellerType.RushtonFlatBlade, DiameterM = 0.06 };

        for (var i = 0; i < CurrentTest.Runs.Count; i++)
        {
            var run = CurrentTest.Runs[i];
            var rpm = run.MeanRpmMeasured > 0 ? run.MeanRpmMeasured : run.AgitationRpm;
            var pNet = run.NetPowerW ?? run.MeanShaftPowerW;

            double re = 0.0;
            double np = 0.0;
            double? npCi = null;
            if (rpm > 0 && refImpeller.DiameterM > 0 && fluid.DensityKgM3 > 0 && fluid.ViscosityPaS > 0)
            {
                re = PowerCalc.ReynoldsNumber(fluid.DensityKgM3, rpm, refImpeller.DiameterM, fluid.ViscosityPaS);
                np = PowerCalc.PowerNumber(pNet, fluid.DensityKgM3, rpm, refImpeller.DiameterM);
                if (run.Ci95PowerW is { } ciW && ciW > 0)
                {
                    npCi = ciW / (fluid.DensityKgM3 * Math.Pow(rpm / 60.0, 3) * Math.Pow(refImpeller.DiameterM, 5));
                }
            }

            double? fr = null;
            double? fl = null;
            double? vvm = null;
            if (run.GasMode != PowerGasMode.Ungassed && run.GasFlowLpm is { } flowLpm)
            {
                if (rpm > 0 && refImpeller.DiameterM > 0)
                {
                    fr = PowerCalc.FroudeNumber(rpm, refImpeller.DiameterM);
                    fl = PowerCalc.AerationNumber(flowLpm, rpm, refImpeller.DiameterM);
                }
                if (geometry.LiquidVolumeM3 > 0)
                {
                    vvm = flowLpm / (geometry.LiquidVolumeM3 * 1000.0);
                }
            }

            var newAnalysis = run.Analysis is not null
                ? run.Analysis with
                {
                    AssemblyReynoldsNumber = re,
                    AssemblyPowerNumber = np,
                    AssemblyPowerNumberCi95 = npCi ?? run.Analysis.AssemblyPowerNumberCi95,
                }
                : new PowerPointResult
                {
                    AssemblyReynoldsNumber = re,
                    AssemblyPowerNumber = np,
                    AssemblyPowerNumberCi95 = npCi ?? 0.0,
                };

            CurrentTest.Runs[i] = run with
            {
                GasFlowVvm = vvm ?? run.GasFlowVvm,
                GasFlowNumber = run.GasFlowNumber ?? fl,
                FroudeNumber = run.FroudeNumber ?? fr,
                GassedPowerW = run.GassedPowerW ?? (run.GasMode != PowerGasMode.Ungassed ? run.NetPowerW : null),
                Analysis = newAnalysis,
            };
        }

        for (var i = 0; i < CurrentTest.Runs.Count; i++)
        {
            var run = CurrentTest.Runs[i];
            if (run.GasMode == PowerGasMode.Ungassed)
            {
                if (run.ReferenceP0W != null || run.PowerRatio != null)
                {
                    CurrentTest.Runs[i] = run with
                    {
                        ReferenceP0W = null,
                        ReferenceP0Ci95W = null,
                        P0Provenance = P0Provenance.None,
                        PowerRatio = null,
                        PowerRatioCi95 = null,
                    };
                }
                continue;
            }

            var rpm = run.AgitationRpm > 0 ? run.AgitationRpm : run.MeanRpmMeasured;
            var (p0, p0Ci, provenance) = _analysis.ResolveReferenceP0(rpm, CurrentTest);

            double? ratio = null;
            double? ratioCi = null;
            if (p0 is { } p0Val && p0Val > 0 && run.NetPowerW is { } pgVal)
            {
                var (r, ci) = PowerCalc.PropagatePowerRatioUncertainty(pgVal, run.Ci95PowerW ?? 0.0, p0Val, p0Ci ?? 0.0);
                ratio = r;
                ratioCi = ci;
            }
            else if (run.ReferenceP0W is { } existingP0 && existingP0 > 0 && run.NetPowerW is { } pgVal2)
            {
                p0 = existingP0;
                p0Ci = run.ReferenceP0Ci95W;
                provenance = run.P0Provenance;
                ratio = run.PowerRatio;
                ratioCi = run.PowerRatioCi95;
            }

            CurrentTest.Runs[i] = run with
            {
                ReferenceP0W = p0,
                ReferenceP0Ci95W = p0Ci,
                P0Provenance = provenance,
                PowerRatio = ratio,
                PowerRatioCi95 = ratioCi,
            };
        }

        _store.UpdateResultsSummary(CurrentTest.FolderName, CurrentTest);
        _store.SaveTestManifest(CurrentTest);
        RebuildResults();
    }

    // =========================================================================
    // 8.1 Calibração de torque (1 ponto)
    // =========================================================================

    // =========================================================================
    // 8.1 Tara P_vazio(N) + σ_τ (varredura no ar)
    // =========================================================================

    [RelayCommand]
    private void TareMeasurementInfo()
    {
        if (IsTareAssistantOpen)
        {
            IsTareAssistantOpen = false;
            return;
        }

        // A nova curva recebe seu nome antes de qualquer comando ao eixo. Isso
        // evita medir uma tara sem destino e elimina a segunda ação ambígua de
        // "salvar nova tara" no cartão.
        string? targetProfileName = null;
        var exists = false;
        if (_dialogs is not null)
        {
            var initialName = CurrentTest?.Tare?.ProfileName ?? "";
            if (!_dialogs.PromptInput(
                    "Nome da nova tara",
                    "Informe o nome do perfil que será arquivado ao concluir a varredura:",
                    out var profileName,
                    initialName))
            {
                return;
            }

            profileName = profileName.Trim();
            if (!PowerTestFileContracts.ValidateTareProfileName(profileName, out var nameError))
            {
                ShowError(nameError ?? "Nome de tara inválido.");
                return;
            }

            exists = _store.ListTareProfiles().Any(p => string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
            if (exists &&
                !_dialogs.Confirm(
                    "Substituir perfil de tara",
                    $"Já existe um perfil chamado \"{profileName}\". Substituir a curva ao concluir a medição?",
                    "Substituir",
                    "Cancelar",
                    isDanger: true))
            {
                return;
            }

            targetProfileName = profileName;
        }

        IsTareAssistantOpen = true;
        if (IsTareAssistantOpen)
        {
            RefreshTareProfiles(targetProfileName);

            if (!string.IsNullOrWhiteSpace(targetProfileName))
            {
                SelectedTareProfile = TareProfiles.FirstOrDefault(
                    p => string.Equals(p.Name, targetProfileName, StringComparison.OrdinalIgnoreCase));
                TareProfileName = targetProfileName;
                CurrentTarePoints.Clear();
                TareProgressMessage = exists
                    ? $"Perfil \"{targetProfileName}\" será substituído ao concluir a varredura. Clique em 'Iniciar ensaio no ar'."
                    : $"Perfil \"{targetProfileName}\" preparado para varredura no ar (vaso seco). Clique em 'Iniciar ensaio no ar'.";
            }
            else
            {
                TareProgressMessage = CurrentTest?.Tare is null
                    ? "Monte os impelidores no eixo e opere com o vaso no ar (seco)."
                    : $"Tara atual possui {CurrentTest.Tare.Points.Count} patamares ({TareStatus}).";
            }
        }
    }

    [RelayCommand]
    private void CloseTareAssistant()
    {
        IsTareAssistantOpen = false;
        RefreshCurrentTarePoints();
        TareProfileName = CurrentTest?.Tare?.ProfileName ?? "";
        RefreshTareProfiles(CurrentTest?.Tare?.ProfileName);
    }

    [RelayCommand]
    private async Task StartTareSweepAsync()
    {
        if (CurrentTest is null)
        {
            ShowError("Crie ou abra um ensaio para registrar a tara.");
            return;
        }
        if (IsRunning || IsTareRunning)
        {
            ShowError("Aguarde a operação atual finalizar para rodar a tara.");
            return;
        }
        var settings = BuildEditedSettings();
        if (!double.IsFinite(settings.MinRpm) || !double.IsFinite(settings.MaxRpm) ||
            settings.MinRpm < 15 || settings.MaxRpm > 1000 || settings.MinRpm > settings.MaxRpm ||
            settings.MinSamples < 2 || !double.IsFinite(settings.MaxCaptureSeconds) || settings.MaxCaptureSeconds <= 0 ||
            settings.MaxTries < 1 || !double.IsFinite(settings.StationarityWindowSeconds) ||
            settings.StationarityWindowSeconds <= 0 ||
            !double.IsFinite(settings.StationaritySlopeTolerancePercentPerSecond) ||
            settings.StationaritySlopeTolerancePercentPerSecond < 0 ||
            settings.StationarityRequiredSamples < 1 ||
            !double.IsFinite(settings.RelativeCiFraction) || settings.RelativeCiFraction < 0 ||
            !double.IsFinite(settings.CiFloorSigmaMultiple) || settings.CiFloorSigmaMultiple < 0 ||
            settings.CaptureServoPollMs is < CommandBuilders.ServoPollMinimumMs or > CommandBuilders.ServoPollMaximumMs ||
            settings.RestoreServoPollMs is < CommandBuilders.ServoPollMinimumMs or > CommandBuilders.ServoPollMaximumMs)
        {
            ShowError("Revise os limites de estacionariedade, amostragem e IC95 antes de iniciar a tara.");
            return;
        }

        var maxTareAllowed = PowerMotorRouteCoordinator.ModbusMaxRpm;

        if (!double.IsFinite(TareStartRpm) || !double.IsFinite(TareEndRpm) || !double.IsFinite(TareStepRpm) ||
            TareStartRpm < PowerMotorRouteCoordinator.MinRpm || TareEndRpm > maxTareAllowed ||
            TareEndRpm < TareStartRpm || TareStepRpm < settings.MinStepRpm)
        {
            ShowError($"Defina a tara entre {PowerMotorRouteCoordinator.MinRpm:F0} e {maxTareAllowed:F0} rpm, com passo mínimo de {settings.MinStepRpm:F0} rpm.");
            return;
        }

        var targets = BuildTareTargets(TareStartRpm, TareEndRpm, TareStepRpm);
        if (targets.Count == 0)
        {
            ShowError("A faixa informada não produziu nenhum patamar de tara.");
            return;
        }

        var tareSettings = settings with
        {
            MinRpm = Math.Min(settings.MinRpm, PowerMotorRouteCoordinator.MinRpm),
            MaxRpm = Math.Max(settings.MaxRpm, maxTareAllowed),
        };

        // Opened before anything is claimed or commanded, so a store that cannot be written
        // fails here with nothing to undo. Every reading the sweep accepts is appended to this
        // file as it arrives, which is what leaves a cancelled or timed-out sweep with its
        // measurements on disk instead of discarding them along with the in-memory list.
        string tareRawFileName;
        try
        {
            tareRawFileName = _store.BeginTareRawCapture(CurrentTest.FolderName, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            ShowError($"Não foi possível abrir o arquivo de leituras brutas da tara: {ex.Message}");
            return;
        }

        _tareCancellation?.Dispose();
        _tareCancellation = new CancellationTokenSource();
        var cancellation = _tareCancellation;
        IsTareRunning = true;
        CurrentTarePoints.Clear();
        LivePoints.Clear();
        _tareSweepStartedTimestamp = Stopwatch.GetTimestamp();
        var points = new List<TarePoint>();
        var rawSamples = new List<TareSample>();
        _tarePointTotal = targets.Count;
        _tareFailureMessage = null;
        _tareRawWrittenCount = 0;
        _tareRawFileName = tareRawFileName;

        try
        {
            _arbiter.Claim(CommandOwner.PowerAssay, [ActuatorId.Agitation], "Varredura de tara no ar");
            if (_arbiter.OwnerOf(ActuatorId.Agitation) != CommandOwner.PowerAssay)
            {
                throw new InvalidOperationException("Não foi possível obter o controle da agitação para medir a tara.");
            }

            _routeCoordinator.EnsurePrimaryRoute(out var routeMsg);
            if (!_routeCoordinator.RouteRequestAccepted) { throw new InvalidOperationException(routeMsg); }

            if (_routeCoordinator.IsUartFallback)
            {
                if (TareStartRpm > PowerMotorRouteCoordinator.UartFallbackMaxRpm)
                {
                    throw new InvalidOperationException(
                        $"Em modo de fallback UART, a rotação máxima é de {PowerMotorRouteCoordinator.UartFallbackMaxRpm:F0} rpm. " +
                        $"A rotação inicial informada ({TareStartRpm:F0} rpm) requer comunicação Modbus.");
                }
                targets = _routeCoordinator.AdjustTareTargets(targets);
                _tarePointTotal = targets.Count;
                TareProgressMessage = $"Modo de fallback UART ativo: varredura limitada a {PowerMotorRouteCoordinator.UartFallbackMaxRpm:F0} rpm.";
            }

            EnsureTareDispatch(
                CommandBuilders.ServoPollInterval(tareSettings.CaptureServoPollMs),
                "configurar a aquisição rápida do servo");

            for (var index = 0; index < targets.Count; index++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var rpm = targets[index];
                _tarePointIndex = index + 1;
                EnsureTareDispatch(
                    CommandBuilders.MotorSetpoint((int)rpm),
                    $"comandar o patamar de {rpm:F0} rpm");

                _tarePointStartedTimestamp = Stopwatch.GetTimestamp();
                _tareLastValidSeconds = double.NaN;
                _tareRawWrittenCount = 0;
                _tareCapture = new PowerTareCaptureController(tareSettings, rpm);
                UpdateTareProgressMessage();

                await WaitForTarePointAsync(_tareCapture, tareSettings, cancellation.Token);
                if (_tareCapture.State != TareCaptureState.Converged)
                {
                    var msg = _tareCapture.State == TareCaptureState.SpeedTimedOut
                        ? (_routeCoordinator.IsUartFallback && rpm > PowerMotorRouteCoordinator.UartFallbackMaxRpm
                            ? $"A rotação não estabilizou em {rpm:F0} rpm: no modo de fallback UART a rotação física máxima é {PowerMotorRouteCoordinator.UartFallbackMaxRpm:F0} rpm. Restabeleça a comunicação Modbus para alcançar 1000 rpm."
                            : $"A rotação não estabilizou em {rpm:F0} rpm.")
                        : $"O patamar de {rpm:F0} rpm não atingiu o IC95 após {tareSettings.MaxTries} tentativa(s).";
                    throw new InvalidOperationException(msg);
                }

                var point = _tareCapture.CreatePoint(CurrentTest);
                points.Add(point);
                rawSamples.AddRange(_tareCapture.Samples);
                CurrentTarePoints.Add(point);
            }

            // A named sweep is filed in the shaft library as well as in the assay, so the
            // other bioreactor's assays can pick it up without measuring in the air again.
            var profileName = TareProfileName?.Trim() ?? "";
            var filed = profileName.Length > 0 &&
                        PowerTestFileContracts.ValidateTareProfileName(profileName, out _);

            var tare = new TareCurve
            {
                SchemaVersion = 2,
                Points = points,
                Samples = rawSamples,
                AcquisitionSettings = tareSettings,
                ImpellerSetHash = PowerTestFileContracts.ComputeImpellerSetHash(BuildGeometry()),
                CalibrationHash = null,
                MeasuredUtc = DateTimeOffset.UtcNow,
                ProfileName = filed ? profileName : "",
                RawSamplesFileName = _tareRawFileName ?? "",
            };

            CurrentTest.Tare = tare;
            _store.SaveTare(CurrentTest.FolderName, tare);
            _store.SaveTestManifest(CurrentTest);

            if (filed)
            {
                _store.SaveTareProfile(profileName, tare);
                RefreshTareProfiles();
            }

            var rawNote = $" Leituras brutas em {PowerTestFileContracts.TareRawDirectoryName}/{tareRawFileName}.";
            TareProgressMessage = filed
                ? $"Tara concluída e gravada: {points.Count} patamares, {rawSamples.Count} leituras válidas em {PowerTestFileContracts.TareFileName}, também arquivada no perfil \"{profileName}\".{rawNote}"
                : $"Tara concluída e gravada: {points.Count} patamares, {rawSamples.Count} leituras válidas em {PowerTestFileContracts.TareFileName}.{rawNote}";
            ValidationMessage = TareProgressMessage;
            OnPropertyChanged(nameof(TareStatus));
            OnPropertyChanged(nameof(ResultModeLabel));
            ReprocessScientificData();
        }
        catch (OperationCanceledException)
        {
            RefreshCurrentTarePoints();
            TareProgressMessage = (_tareFailureMessage ?? "Tara cancelada. A curva válida anterior foi mantida.") +
                DescribeTareRawFile();
            ValidationMessage = TareProgressMessage;
        }
        catch (Exception ex)
        {
            RefreshCurrentTarePoints();
            TareProgressMessage = $"Tara não gravada: {ex.Message} A curva válida anterior foi mantida." +
                DescribeTareRawFile();
            ShowError(TareProgressMessage);
        }
        finally
        {
            _tareCapture = null;
            _tareRawFileName = null;
            SafeParkAndReleaseAgitation("Tara finalizada", settings.RestoreServoPollMs);
            IsTareRunning = false;
            IsAccumulating = false;
            _tareSweepStartedTimestamp = 0;
            if (ReferenceEquals(_tareCancellation, cancellation))
            {
                _tareCancellation.Dispose();
                _tareCancellation = null;
            }
        }
    }

    [RelayCommand]
    private void CancelTareSweep()
    {
        if (!IsTareRunning)
        {
            return;
        }

        TareProgressMessage = "Cancelando a tara e parando o eixo com segurança...";
        _tareCancellation?.Cancel();
    }

    private async Task WaitForTarePointAsync(
        PowerTareCaptureController capture,
        PowerTestSettings settings,
        CancellationToken cancellationToken)
    {
        var monitorIntervalMs = Math.Clamp(settings.CaptureServoPollMs, 100, 1000);
        while (!capture.IsDone)
        {
            await Task.Delay(monitorIntervalMs, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (_arbiter.OwnerOf(ActuatorId.Agitation) != CommandOwner.PowerAssay)
            {
                throw new InvalidOperationException("O controle da agitação foi transferido durante a tara.");
            }

            var elapsed = TarePointElapsedSeconds();
            capture.AdvanceTime(elapsed);
            if ((double.IsNaN(_tareLastValidSeconds) && elapsed >= settings.MeasurementTimeoutSeconds) ||
                (!double.IsNaN(_tareLastValidSeconds) && elapsed - _tareLastValidSeconds >= settings.MeasurementTimeoutSeconds))
            {
                throw new InvalidOperationException(
                    $"A telemetria válida do servo ficou ausente por {settings.MeasurementTimeoutSeconds:F0} s.");
            }

            UpdateTareProgressMessage();
        }
    }

    private void UpdateTareProgressMessage()
    {
        if (_tareCapture is not { } capture)
        {
            return;
        }

        var prefix = $"Patamar {_tarePointIndex}/{_tarePointTotal} · {capture.TargetRpm:F0} rpm";
        TareProgressMessage = capture.State switch
        {
            TareCaptureState.StabilizingSpeed =>
                $"{prefix}: estabilizando a rotação medida...",
            TareCaptureState.StabilizingTorque =>
                $"{prefix}: torque em estabilização · tentativa {capture.Attempt}/{capture.MaxAttempts}.",
            TareCaptureState.Accumulating =>
                $"{prefix}: n={capture.SampleCount} · IC95 ±{capture.CurrentTorqueCi95Percent:F4}% " +
                $"(alvo ≤ {capture.CurrentTargetTorqueCi95Percent:F4}%) · tentativa {capture.Attempt}/{capture.MaxAttempts}.",
            TareCaptureState.Converged =>
                $"{prefix}: precisão atingida com {capture.SampleCount} amostras.",
            TareCaptureState.SpeedTimedOut =>
                $"{prefix}: a rotação não estabilizou no tempo limite.",
            _ => $"{prefix}: o IC95 não convergiu no tempo limite.",
        };
    }

    private static List<double> BuildTareTargets(double startRpm, double endRpm, double stepRpm)
    {
        var targets = new List<double>();
        for (var rpm = startRpm; rpm <= endRpm + 1e-9; rpm += stepRpm)
        {
            var commandRpm = Math.Round(rpm, MidpointRounding.AwayFromZero);
            if (targets.Count == 0 || !double.Equals(targets[^1], commandRpm))
            {
                targets.Add(commandRpm);
            }
        }
        return targets;
    }

    private void EnsureTareDispatch(OpenTECCommand command, string action)
    {
        var result = _arbiter.Dispatch(CommandOwner.PowerAssay, command);
        if (!result.Accepted)
        {
            throw new InvalidOperationException(
                $"Comando recusado ao tentar {action}: {string.Join(", ", result.Refused)}.");
        }
    }

    private double TarePointElapsedSeconds() => _tarePointStartedTimestamp == 0
        ? 0.0
        : Stopwatch.GetElapsedTime(_tarePointStartedTimestamp).TotalSeconds;

    private double TareSweepElapsedSeconds() => _tareSweepStartedTimestamp == 0
        ? 0.0
        : Stopwatch.GetElapsedTime(_tareSweepStartedTimestamp).TotalSeconds;

    private void RefreshCurrentTarePoints()
    {
        CurrentTarePoints.Clear();
        if (CurrentTest?.Tare is not { } tare)
        {
            return;
        }

        foreach (var point in tare.Points.OrderBy(point => point.Rpm))
        {
            CurrentTarePoints.Add(point);
        }
    }

    // =========================================================================
    // 8.2.1 Biblioteca de taras por eixo
    //
    // A tara pertence ao eixo, não ao ensaio: uma bancada com dois eixos (p. ex.
    // "eixo_furo_unico" e "eixo_furo_duplo") tem duas taras válidas ao mesmo tempo.
    // A curva continua gravada dentro do ensaio (tara.json) para rastreabilidade; a
    // biblioteca guarda uma cópia nomeada que qualquer ensaio pode reaproveitar.
    // =========================================================================

    private void RefreshTareProfiles(string? keepSelectedName = null)
    {
        var previous = keepSelectedName ?? SelectedTareProfile?.Name ?? TareProfileName;

        TareProfiles.Clear();
        foreach (var profile in _store.ListTareProfiles())
        {
            TareProfiles.Add(profile);
        }

        SelectedTareProfile = TareProfiles.FirstOrDefault(
            p => string.Equals(p.Name, previous, StringComparison.OrdinalIgnoreCase));
    }

    [RelayCommand]
    private void SaveTareProfile()
    {
        if (CurrentTest?.Tare is not { } tare)
        {
            ShowError("Meça a tara antes de salvá-la como perfil de eixo.");
            return;
        }

        var name = TareProfileName?.Trim() ?? "";
        if (!PowerTestFileContracts.ValidateTareProfileName(name, out var error))
        {
            ShowError(error ?? "Nome de perfil de tara inválido.");
            return;
        }

        try
        {
            _store.SaveTareProfile(name, tare);

            // The assay keeps its own copy, now stamped with the shaft it came from.
            CurrentTest.Tare = tare with { ProfileName = name };
            _store.SaveTare(CurrentTest.FolderName, CurrentTest.Tare);
            _store.SaveTestManifest(CurrentTest);

            RefreshTareProfiles();
            TareProgressMessage =
                $"Tara salva como perfil \"{name}\" ({tare.Points.Count} patamares). Outros ensaios podem reaproveitá-la.";
            ValidationMessage = TareProgressMessage;
            OnPropertyChanged(nameof(TareStatus));
        }
        catch (Exception ex)
        {
            ShowError($"Não foi possível salvar o perfil de tara: {ex.Message}");
        }
    }

    [RelayCommand]
    private void UseNoTare()
    {
        if (!CanEditPlan || CurrentTest is null) { return; }
        try
        {
            _store.ClearTare(CurrentTest.FolderName);
            CurrentTest.Tare = null;
            CurrentTest.RelativeMode = true;
            SelectedTareProfile = null;
            _store.SaveTestManifest(CurrentTest);
            RefreshCurrentTarePoints();
            NotifyDocumentState();
            ReprocessScientificData();
        }
        catch (Exception ex) { ShowError($"Não foi possível retirar a tara: {ex.Message}"); }
    }

    [RelayCommand]
    private void ApplyTareProfile()
    {
        if (CurrentTest is null)
        {
            ShowError("Abra um ensaio para aplicar um perfil de tara.");
            return;
        }
        if (IsRunning || IsTareRunning)
        {
            ShowError("Aguarde a operação atual finalizar para trocar a tara.");
            return;
        }
        if (SelectedTareProfile is not { } selected)
        {
            ShowError("Escolha um perfil de tara na lista.");
            return;
        }

        var curve = _store.LoadTareProfile(selected.Name);
        if (curve is null)
        {
            ShowError($"O perfil \"{selected.Name}\" não pôde ser lido. Atualize a lista.");
            RefreshTareProfiles();
            return;
        }

        try
        {
            CurrentTest.Tare = curve;
            _store.SaveTare(CurrentTest.FolderName, curve);
            _store.SaveTestManifest(CurrentTest);

            RefreshCurrentTarePoints();
            TareProfileName = selected.Name;

            // TareStatus does the real compatibility check against the mounted impeller set
            // and the current torque calibration; it is repeated here so the operator sees the
            // verdict at the moment of the swap, not only in the traceability card.
            TareProgressMessage =
                $"Perfil \"{selected.Name}\" aplicado: {curve.Points.Count} patamares ({TareStatus}).";
            ValidationMessage = TareProgressMessage;
            OnPropertyChanged(nameof(TareStatus));
            OnPropertyChanged(nameof(ResultModeLabel));
            ReprocessScientificData();
        }
        catch (Exception ex)
        {
            ShowError($"Não foi possível aplicar o perfil de tara: {ex.Message}");
        }
    }

    [RelayCommand]
    private void DeleteTareProfile()
    {
        if (SelectedTareProfile is not { } selected)
        {
            ShowError("Escolha um perfil de tara para excluir.");
            return;
        }

        if (_dialogs is not null &&
            !_dialogs.Confirm(
                "Excluir perfil de tara",
                $"Excluir o perfil \"{selected.Name}\"? Os ensaios que já o utilizam mantêm a própria cópia.",
                isDanger: true))
        {
            return;
        }

        try
        {
            if (!_store.DeleteTareProfile(selected.Name))
            {
                ShowError($"O perfil \"{selected.Name}\" já não existe.");
            }

            RefreshTareProfiles();
            TareProgressMessage = $"Perfil de tara \"{selected.Name}\" removido da biblioteca.";
            ValidationMessage = TareProgressMessage;
        }
        catch (Exception ex)
        {
            ShowError($"Não foi possível excluir o perfil de tara: {ex.Message}");
        }
    }

    [RelayCommand]
    private void RenameTareProfile()
    {
        if (SelectedTareProfile is not { } selected)
        {
            ShowError("Escolha um perfil de tara para renomear.");
            return;
        }

        if (_dialogs is null ||
            !_dialogs.PromptInput(
                "Renomear perfil de tara",
                "Novo nome do perfil de tara:",
                out var newName,
                selected.Name))
        {
            return;
        }

        var trimmedNew = newName?.Trim() ?? "";
        if (!PowerTestFileContracts.ValidateTareProfileName(trimmedNew, out var error))
        {
            ShowError(error ?? "Nome de perfil de tara inválido.");
            return;
        }

        try
        {
            _store.RenameTareProfile(selected.Name, trimmedNew);

            // Se o ensaio aberto estiver usando este perfil de tara, atualize também o nome da tara no ensaio
            if (CurrentTest?.Tare is not null &&
                string.Equals(CurrentTest.Tare.ProfileName, selected.Name, StringComparison.OrdinalIgnoreCase))
            {
                CurrentTest.Tare = CurrentTest.Tare with { ProfileName = trimmedNew };
                _store.SaveTare(CurrentTest.FolderName, CurrentTest.Tare);
                _store.SaveTestManifest(CurrentTest);
            }

            TareProfileName = trimmedNew;
            RefreshTareProfiles(trimmedNew);
            RefreshCurrentTarePoints();
            TareProgressMessage = $"Perfil de tara renomeado de \"{selected.Name}\" para \"{trimmedNew}\".";
            ValidationMessage = TareProgressMessage;
            OnPropertyChanged(nameof(TareStatus));
        }
        catch (Exception ex)
        {
            ShowError($"Não foi possível renomear o perfil de tara: {ex.Message}");
        }
    }

    // =========================================================================
    // 8.3 Ponto único (conferência rápida)
    // =========================================================================

    [RelayCommand]
    private void ToggleSinglePointPanel()
    {
        IsSinglePointPanelOpen = !IsSinglePointPanelOpen;
        if (IsSinglePointPanelOpen)
        {
            IsTareAssistantOpen = false;
            IsEnergyCorrelationOpen = false;
        }
    }

    [RelayCommand]
    private async Task StartSinglePointAsync()
    {
        if (IsRunning || IsTareRunning)
        {
            ShowError("Aguarde a operação atual finalizar para comandar o ponto único.");
            return;
        }
        if (SinglePointRpm < 15 || SinglePointRpm > 1000)
        {
            ShowError("A rotação deve estar entre 15 e 1000 rpm.");
            return;
        }

        try
        {
            var actuators = SinglePointFlowLpm > 0 ? new[] { ActuatorId.Agitation, ActuatorId.Aeration } : [ActuatorId.Agitation];
            _arbiter.Claim(CommandOwner.PowerAssay, actuators, "Ponto único de conferência");
            _routeCoordinator.EnsurePrimaryRoute(out var routeMsg);
            if (!_routeCoordinator.RouteRequestAccepted) { throw new InvalidOperationException(routeMsg); }
            if (!_routeCoordinator.ValidateRpm(SinglePointRpm, out var rpmError)) { throw new InvalidOperationException(rpmError); }
            _arbiter.Dispatch(CommandOwner.PowerAssay, CommandBuilders.MotorSetpoint((int)SinglePointRpm));
            if (SinglePointFlowLpm > 0)
            {
                _arbiter.Dispatch(CommandOwner.PowerAssay, CommandBuilders.FlowRoute(
                    SinglePointFlowLpm, 10.0, GasRoute.Reactor, _rig()));
            }
            IsSinglePointActive = true;
            LivePoints.Clear();
            _tareSweepStartedTimestamp = Stopwatch.GetTimestamp();
            BeginSinglePointRecording();
            ValidationMessage = _singlePointFileName is null
                ? $"Ponto único em curso: {SinglePointRpm:F0} rpm."
                : $"Ponto único em curso: {SinglePointRpm:F0} rpm · gravando em {_singlePointFileName}.";
        }
        catch (Exception ex)
        {
            SafeParkAndReleaseAgitation("Falha no ponto único");
            IsSinglePointActive = false;
            CompleteSinglePointRecording($"Interrompido por falha: {ex.Message}");
            ShowError($"Erro ao comandar ponto único: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task StopSinglePointAsync()
    {
        try
        {
            if (SinglePointFlowLpm > 0)
            {
                _arbiter.Dispatch(CommandOwner.PowerAssay, CommandBuilders.FlowSafeStop(10.0));
            }
            SafeParkAndReleaseAgitation("Ponto único finalizado");
            IsSinglePointActive = false;
            _tareSweepStartedTimestamp = 0;
            var (recordedFile, recordedCount) = CompleteSinglePointRecording("Encerrado pelo operador");
            ValidationMessage = recordedFile is null
                ? "Ponto único encerrado; eixo desocupado."
                : $"Ponto único encerrado; {recordedCount} leitura(s) gravadas em {recordedFile}.";
        }
        catch (Exception ex)
        {
            CompleteSinglePointRecording($"Interrompido por falha: {ex.Message}");
            ShowError($"Erro ao parar ponto único: {ex.Message}");
        }
    }

    [RelayCommand]
    private void AddSinglePointToTest()
    {
        if (CurrentTest is null)
        {
            ShowError("Abra ou crie um ensaio para registrar a condição.");
            return;
        }

        var cond = new PowerCondition
        {
            AgitationRpm = SinglePointRpm,
            GasFlowLpm = SinglePointFlowLpm > 0 ? SinglePointFlowLpm : 0.0,
            GasMode = SinglePointFlowLpm > 0 ? PowerGasMode.Gassed : PowerGasMode.Ungassed,
            FlowUnit = FlowInputUnit.Lpm,
            RequestedReplicates = 1,
            Origin = PowerConditionOrigin.Manual,
            OrderIndex = Conditions.Count + 1,
        };
        Conditions.Add(cond);
        CurrentTest.Conditions.Add(cond);
        _store.SaveConditionsTable(CurrentTest.FolderName, CurrentTest.Conditions);
        _store.SaveTestManifest(CurrentTest);
        ValidationMessage = $"Ponto avulso ({SinglePointRpm:F0} rpm) adicionado à tabela.";
    }

    // =========================================================================
    // 8.4 Captura manual de energia e correlação P_elétrica × P_mecânica
    // =========================================================================

    [RelayCommand]
    private async Task ConfirmManualEnergyAsync()
    {
        if (_runner is null)
        {
            return;
        }
        if (ManualEnergyWattsInput < 0 || !double.IsFinite(ManualEnergyWattsInput))
        {
            ShowError("Informe uma potência elétrica válida em Watts (≥ 0).");
            return;
        }

        try
        {
            await _runner.SubmitManualEnergyAsync(
                ManualEnergyWattsInput,
                string.IsNullOrWhiteSpace(ManualEnergyInstrument) ? "Wattímetro" : ManualEnergyInstrument.Trim(),
                string.IsNullOrWhiteSpace(ManualEnergyNote) ? null : ManualEnergyNote.Trim());
            RefreshManualEnergyReadings();
            ValidationMessage = $"Leitura de {ManualEnergyWattsInput:F1} W confirmada.";
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    [RelayCommand]
    private void ToggleEnergyCorrelation()
    {
        IsEnergyCorrelationOpen = !IsEnergyCorrelationOpen;
        if (IsEnergyCorrelationOpen)
        {
            IsTareAssistantOpen = false;
            IsSinglePointPanelOpen = false;
            RefreshManualEnergyReadings();
        }
    }

    public void RefreshManualEnergyReadings()
    {
        ManualEnergyReadings.Clear();
        if (CurrentTest?.Runs is null)
        {
            EnergyCorrelationSummary = "Nenhum ensaio carregado.";
            return;
        }

        var pairs = new List<(double Mech, double Elec)>();
        foreach (var run in CurrentTest.Runs)
        {
            if (run.ManualElec is { } reading)
            {
                ManualEnergyReadings.Add(reading);
                pairs.Add((reading.PowerMechanicalWAtReading, reading.PowerElectricalW));
            }
        }

        if (pairs.Count >= 2 && PowerCalc.FitElectricalCorrelation(pairs) is { } fit)
        {
            EnergyCorrelationSummary = $"P_el = {fit.Slope:F3} · P_mec {(fit.Intercept >= 0 ? "+" : "-")} {Math.Abs(fit.Intercept):F2} W  (R² = {fit.R2:F4}, n = {pairs.Count})";
        }
        else
        {
            EnergyCorrelationSummary = pairs.Count == 0
                ? "Nenhuma leitura manual gravada até o momento."
                : $"1 leitura manual gravada ({pairs[0].Elec:F1} W @ {pairs[0].Mech:F1} W mec). Mínimo 2 para correlação.";
        }
    }

    private void SafeParkAndReleaseAgitation(string reason, int? restoreServoPollMs = null)
    {
        try
        {
            if (_arbiter.OwnerOf(ActuatorId.Agitation) == CommandOwner.PowerAssay)
            {
                _arbiter.Dispatch(CommandOwner.PowerAssay, CommandBuilders.MotorSetpoint(15));
                _arbiter.Dispatch(CommandOwner.PowerAssay, CommandBuilders.MotorSetpoint(0));
                if (restoreServoPollMs is { } pollMs)
                {
                    _arbiter.Dispatch(CommandOwner.PowerAssay, CommandBuilders.ServoPollInterval(pollMs));
                }
                _arbiter.Release(CommandOwner.PowerAssay, reason);
            }
        }
        catch
        {
            // best-effort
        }
    }

    private PowerTestSettings BuildEditedSettings() => (CurrentTest?.Settings ?? new PowerTestSettings()) with
    {
        MinRpm = MinRpm,
        MaxRpm = MaxRpm,
        DefaultStepRpm = StepRpm,
        MinFlowLpm = MinFlowLpm,
        MaxFlowLpm = MaxFlowLpm,
        DefaultStepFlowLpm = StepFlowLpm,
        RelativeCiFraction = RelativeCiPercent / 100.0,
        CiFloorSigmaMultiple = CiFloorSigmaMultiple,
        MinSamples = MinimumSamples,
        MaxCaptureSeconds = MaxCaptureSeconds,
        MaxTries = MaxTries,
        StationarityWindowSeconds = StationarityWindowSeconds,
        StationaritySlopeTolerancePercentPerSecond = StationaritySlopeTolerance,
        StationarityRequiredSamples = StationarityRequiredSamples,
        PrestageFlowToleranceLpm = PrestageFlowToleranceLpm,
        PrestageFlowStableSamples = PrestageFlowStableSamples,
        PrestageAgitationRpm = PrestageAgitationRpm,
        MaxPrestageSeconds = MaxPrestageSeconds,
        PrestageFlowStabilityStdDevLpm = PrestageFlowStabilityStdDevLpm,
        PrestageFlowStabilityMaxErrorLpm = PrestageFlowStabilityMaxErrorLpm,
        ManualEnergyCaptureEnabled = ManualEnergyCaptureEnabled,
        AutoAcceptRuns = AutoAcceptRuns,
        AutoResumeOnLinkRestore = AutoResumeOnLinkRestore,
        LinkRecoveryTimeoutSeconds = LinkRecoveryTimeoutSeconds,
        UnattendedFailurePolicy = RetryThenSkipOnSequenceFailure ? UnattendedFailurePolicy.RetryThenSkip : UnattendedFailurePolicy.StopForReview,
    };

    private bool TryPersist(out string error)
    {
        error = ValidateSetup();
        if (CurrentTest is null || error.Length > 0)
        {
            return false;
        }

        NormalizeImpellerOrder();
        NormalizeConditionOrder();
        foreach (var condition in Conditions)
        {
            if (condition.FlowUnit == FlowInputUnit.Vvm && condition.GasFlowVvm is { } vvm)
            {
                condition.GasFlowLpm = vvm * LiquidVolumeL;
            }
            else if (condition.FlowUnit == FlowInputUnit.Lpm && condition.GasFlowLpm is { } lpm && LiquidVolumeL > 0)
            {
                condition.GasFlowVvm = lpm / LiquidVolumeL;
            }
        }

        CurrentTest.Fluid = new FluidProperties { DensityKgM3 = DensityKgM3, ViscosityPaS = ViscosityPaS, TemperatureC = TemperatureC, PresetName = "Água / personalizado" };
        CurrentTest.Geometry = BuildGeometry();
        CurrentTest.Settings = BuildEditedSettings();
        CurrentTest.RelativeMode = CurrentTest.Calibration is null || CurrentTest.Tare is null;
        CurrentTest.Conditions = Conditions.Select(c => c.Clone()).ToList();
        CurrentTest.SettingsRevision++;
        _store.SaveConditionsTable(CurrentTest.FolderName, CurrentTest.Conditions);
        _store.SaveTestManifest(CurrentTest);
        _runner?.PrepareTest(CurrentTest);
        RebuildResults();
        return true;
    }

    private string ValidateSetup()
    {
        if (CurrentTest is null)
        {
            return "Crie ou abra um ensaio.";
        }

        if (!FinitePositive(DensityKgM3) || !FinitePositive(ViscosityPaS) || !double.IsFinite(TemperatureC))
        {
            return "Densidade e viscosidade devem ser positivas; temperatura deve ser finita.";
        }

        if (!FinitePositive(VesselDiameterMm) || LiquidVolumeL < 0 || !double.IsFinite(LiquidVolumeL))
        {
            return "Diâmetro do vaso deve ser positivo e o volume não pode ser negativo.";
        }

        if (Impellers.Count == 0 || Impellers.Any(i => !FinitePositive(i.DiameterM) || i.BladeCount < 1 || !double.IsFinite(i.ClearanceM) || i.ClearanceM < 0))
        {
            return "Cadastre ao menos um impelidor com D positivo, pás e folga válidos.";
        }

        if (ValidateCaptureSettings() is { Length: > 0 } settingsError)
        {
            return settingsError;
        }

        if (Conditions.Count == 0)
        {
            return "Inclua ao menos uma condição.";
        }

        if (Conditions.Any(c => !double.IsFinite(c.AgitationRpm) || c.AgitationRpm < MinRpm || c.AgitationRpm > MaxRpm || c.RequestedReplicates < 1))
        {
            return $"Cada condição deve ficar entre {MinRpm:F0} e {MaxRpm:F0} rpm e ter ao menos uma réplica.";
        }

        if (Conditions.Any(c => c.FlowUnit == FlowInputUnit.Vvm && c.GasFlowVvm.HasValue) && LiquidVolumeL <= 0)
        {
            return "Informe o volume de trabalho para converter vvm em L/min.";
        }

        if (Conditions.Any(c => c.GasFlowLpm is < 0 || c.GasFlowVvm is < 0))
        {
            return "A vazão de gás não pode ser negativa.";
        }

        return "";
    }

    /// <summary>The checks that concern only <see cref="PowerTestSettings"/> — what the criteria dialog edits.</summary>
    private string ValidateCaptureSettings()
    {
        if (!FinitePositive(MinRpm) || !FinitePositive(MaxRpm) || MinRpm < 15 || MaxRpm > 1000 || MinRpm > MaxRpm || StepRpm < 5)
        {
            return "Faixa de rotação inválida: 15–1000 rpm e passo mínimo de 5 rpm.";
        }

        if (RelativeCiPercent < 0 || !double.IsFinite(RelativeCiPercent) || CiFloorSigmaMultiple < 0 || !double.IsFinite(CiFloorSigmaMultiple) || MinimumSamples < 2 || !FinitePositive(MaxCaptureSeconds) || MaxTries < 1 || !FinitePositive(StationarityWindowSeconds) || StationaritySlopeTolerance < 0 || !double.IsFinite(StationaritySlopeTolerance) || StationarityRequiredSamples < 1)
        {
            return "Revise os limites de estacionariedade e parada adaptativa.";
        }

        if (PrestageFlowToleranceLpm <= 0 || !double.IsFinite(PrestageFlowToleranceLpm))
        {
            return "Tolerância de vazão da pré-estabilização por C deve ser positiva.";
        }
        if (PrestageFlowStableSamples < 1)
        {
            return "Amostras estáveis da pré-estabilização por C deve ser ao menos 1.";
        }
        if (PrestageAgitationRpm < 0 || PrestageAgitationRpm > MaxRpm || !double.IsFinite(PrestageAgitationRpm))
        {
            return $"Rotação durante a pré-estabilização deve estar entre 0 e {MaxRpm:F0} rpm.";
        }
        if (MaxPrestageSeconds <= 0 || !double.IsFinite(MaxPrestageSeconds))
        {
            return "Tempo limite da pré-estabilização por C deve ser positivo.";
        }
        if (PrestageFlowStabilityStdDevLpm < 0 || !double.IsFinite(PrestageFlowStabilityStdDevLpm) ||
            PrestageFlowStabilityMaxErrorLpm < 0 || !double.IsFinite(PrestageFlowStabilityMaxErrorLpm))
        {
            return "Critério de estabilidade da pré-estabilização (σ e |erro|) não pode ser negativo.";
        }

        return "";
    }

    private PowerGeometry BuildGeometry() => new()
    {
        VesselDiameterM = VesselDiameterMm / 1000.0,
        LiquidVolumeM3 = LiquidVolumeL / 1000.0,
        Baffled = IsBaffled,
        Impellers = Impellers.Select(i => i.Clone()).ToList(),
    };

    private void OnTelemetryReceived(SensorSnapshot snapshot) => RunOnUi(() =>
    {
        _latestSnapshot = snapshot;
        HasServoSample = snapshot.HasServoSample;
        CurrentRpm = snapshot.HasServoSample && double.IsFinite(snapshot.ServoRpm) ? snapshot.ServoRpm : null;
        CurrentTorquePercent = snapshot.HasServoSample && double.IsFinite(snapshot.ServoTorquePct) ? snapshot.ServoTorquePct : null;

        if (IsTareRunning && _tareCapture is { } tareCapture && HasValidTareSample(snapshot))
        {
            var elapsed = TarePointElapsedSeconds();
            _tareLastValidSeconds = elapsed;
            if (Math.Abs(snapshot.ServoTorquePct) > tareCapture.MaxAllowedTorquePercent ||
                snapshot.ServoRpm > tareCapture.MaxAllowedRpm)
            {
                _tareFailureMessage = "Tara interrompida: limite de torque ou rotação excedido. A curva válida anterior foi mantida.";
                _tareCancellation?.Cancel();
            }
            else
            {
                tareCapture.Add(DateTimeOffset.UtcNow, elapsed, snapshot.ServoTorquePct, snapshot.ServoRpm);
                AppendPendingTareSamples(tareCapture);
                UpdateTareProgressMessage();

                var sweepElapsed = TareSweepElapsedSeconds();
                var torqueNm = snapshot.ServoTorquePct / 100.0 * (CurrentTest?.MotorRatedTorqueNm ?? DefaultMotorRatedTorqueNm);
                var shaftPowerW = 2.0 * Math.PI * (snapshot.ServoRpm / 60.0) * torqueNm;
                var point = new PowerDataPoint(
                    DateTimeOffset.UtcNow,
                    sweepElapsed,
                    _tareCapture.State == TareCaptureState.Accumulating ? PowerRunPhase.AccumulatingToTarget : PowerRunPhase.SettlingTorque,
                    snapshot.ServoRpm,
                    snapshot.ServoTorquePct,
                    torqueNm,
                    shaftPowerW,
                    ValidOptional(snapshot.FlowRate),
                    _tareCapture.State == TareCaptureState.Accumulating,
                    _tarePointIndex);
                LivePoints.Add(point);
                while (LivePoints.Count > 6000)
                {
                    LivePoints.RemoveAt(0);
                }
                IsAccumulating = _tareCapture.State == TareCaptureState.Accumulating;
            }
        }
        else if (IsSinglePointActive && HasValidTareSample(snapshot))
        {
            var sweepElapsed = TareSweepElapsedSeconds();
            var torqueNm = snapshot.ServoTorquePct / 100.0 * (CurrentTest?.MotorRatedTorqueNm ?? DefaultMotorRatedTorqueNm);
            var shaftPowerW = 2.0 * Math.PI * (snapshot.ServoRpm / 60.0) * torqueNm;
            var point = new PowerDataPoint(
                DateTimeOffset.UtcNow,
                sweepElapsed,
                PowerRunPhase.SettlingTorque,
                snapshot.ServoRpm,
                snapshot.ServoTorquePct,
                torqueNm,
                shaftPowerW,
                ValidOptional(snapshot.FlowRate),
                true);
            LivePoints.Add(point);
            AppendSinglePointSample(point);
            while (LivePoints.Count > 6000)
            {
                LivePoints.RemoveAt(0);
            }
        }

        CurrentFlowLpm = ValidOptional(snapshot.FlowRate);
        RecalculateLiveMetrics();
        RefreshPreflight();
    });

    /// <summary>Opens the raw file for a single-point capture; failure downgrades to no recording.</summary>
    /// <remarks>
    /// The check itself is a bench operation, not an assay, so a store that cannot be written
    /// must not stop the shaft from turning - the operator is told, and the capture proceeds
    /// unrecorded rather than being refused outright.
    /// </remarks>
    private void BeginSinglePointRecording()
    {
        _singlePointSampleCount = 0;
        var session = new SinglePointSession
        {
            StartedUtc = DateTimeOffset.UtcNow,
            TargetRpm = SinglePointRpm,
            GasFlowSetpointLpm = SinglePointFlowLpm > 0 ? SinglePointFlowLpm : null,
            MotorRatedTorqueNm = CurrentTest?.MotorRatedTorqueNm ?? DefaultMotorRatedTorqueNm,
            TestFolderName = CurrentTest?.FolderName ?? "",
        };

        try
        {
            _singlePointFileName = _store.BeginSinglePointCapture(CurrentTest?.FolderName, session);
            _singlePointSession = session with { RawDataFileName = _singlePointFileName };
        }
        catch (Exception ex)
        {
            _singlePointFileName = null;
            _singlePointSession = null;
            ShowError($"Ponto único não será gravado: {ex.Message}");
        }
    }

    /// <summary>Seals the single-point manifest and returns the file written, with its row count.</summary>
    private (string? FileName, int SampleCount) CompleteSinglePointRecording(string stopReason)
    {
        if (_singlePointSession is not { } session || _singlePointFileName is null)
        {
            _singlePointSession = null;
            _singlePointFileName = null;
            return (null, 0);
        }

        var fileName = _singlePointFileName;
        var count = _singlePointSampleCount;
        try
        {
            _store.CompleteSinglePointCapture(
                string.IsNullOrEmpty(session.TestFolderName) ? null : session.TestFolderName,
                session with
                {
                    CompletedUtc = DateTimeOffset.UtcNow,
                    SampleCount = count,
                    StopReason = stopReason,
                });
        }
        catch (Exception ex)
        {
            ShowError($"O ponto único foi gravado, mas seu manifesto não pôde ser fechado: {ex.Message}");
        }
        finally
        {
            _singlePointSession = null;
            _singlePointFileName = null;
            _singlePointSampleCount = 0;
        }

        return (fileName, count);
    }

    /// <summary>Appends one reading of a single-point capture; a write failure stops the recording, not the shaft.</summary>
    private void AppendSinglePointSample(PowerDataPoint point)
    {
        if (_singlePointSession is not { } session || _singlePointFileName is null)
        {
            return;
        }

        try
        {
            _store.AppendSinglePointSample(
                string.IsNullOrEmpty(session.TestFolderName) ? null : session.TestFolderName,
                _singlePointFileName,
                point);
            _singlePointSampleCount++;
        }
        catch (Exception ex)
        {
            _singlePointSession = null;
            _singlePointFileName = null;
            ShowError($"A gravação do ponto único foi interrompida: {ex.Message}");
        }
    }

    /// <summary>
    /// Appends whatever readings the rung's controller accepted since the last frame.
    /// </summary>
    /// <remarks>
    /// Driven off the controller's own list rather than off the incoming frame, so the file
    /// contains exactly the samples the statistics were computed from - a frame the controller
    /// rejected as out of order or non-finite is not silently added to the record.
    /// </remarks>
    private void AppendPendingTareSamples(PowerTareCaptureController capture)
    {
        if (_tareRawFileName is null || CurrentTest is null)
        {
            return;
        }

        try
        {
            while (_tareRawWrittenCount < capture.Samples.Count)
            {
                _store.AppendTareRawSample(
                    CurrentTest.FolderName,
                    _tareRawFileName,
                    capture.Samples[_tareRawWrittenCount],
                    _tarePointIndex);
                _tareRawWrittenCount++;
            }
        }
        catch (Exception ex)
        {
            var fileName = _tareRawFileName;
            _tareRawFileName = null;
            ShowError($"A gravação das leituras brutas da tara ({fileName}) foi interrompida: {ex.Message}");
        }
    }

    /// <summary>Names the raw file kept by a sweep that did not finish, or says nothing when there is none.</summary>
    private string DescribeTareRawFile() =>
        _tareRawFileName is null || _tareRawWrittenCount == 0
            ? ""
            : $" As leituras brutas ficaram em {PowerTestFileContracts.TareRawDirectoryName}/{_tareRawFileName}.";

    private static bool HasValidTareSample(SensorSnapshot snapshot) =>
        snapshot.HasServoTelemetry &&
        snapshot.HasServoSample &&
        snapshot.ServoOnline &&
        snapshot.ServoCommEnabled == true &&
        double.IsFinite(snapshot.ServoRpm) &&
        double.IsFinite(snapshot.ServoTorquePct);

    /// <summary>
    /// Live readiness read-out. The runner already answers "can this start, and if not why", but
    /// that answer only reached the operator as an error dialog after they pressed the button.
    /// Publishing it continuously lets them fix the rig before committing to a run.
    /// </summary>
    private void RefreshPreflight()
    {
        if (_runner is null || CurrentTest is null)
        {
            IsReadyToStart = false;
            PreflightMessage = CurrentTest is null
                ? "Abra ou crie um ensaio para começar."
                : "Runner indisponível nesta sessão.";
            return;
        }

        if (IsRunning || IsInReview)
        {
            IsReadyToStart = false;
            PreflightMessage = IsInReview ? "Aguardando decisão sobre o ponto capturado." : "Ensaio em andamento.";
            return;
        }

        // Throttled: the check walks the whole plan and hashes the impeller set, and telemetry
        // arrives far faster than an operator can act on it.
        var now = Environment.TickCount64;
        if (now - _lastPreflightTick < 500)
        {
            return;
        }

        _lastPreflightTick = now;

        try
        {
            IsReadyToStart = _runner.CanStart(CurrentTest, out var reason);
            PreflightMessage = IsReadyToStart ? "Pronto para iniciar." : reason ?? "Ensaio não está pronto.";
        }
        catch (Exception ex)
        {
            IsReadyToStart = false;
            PreflightMessage = ex.Message;
        }
    }

    private void RecalculateLiveMetrics()
    {
        if (!HasServoSample || CurrentRpm is not { } rpm || CurrentTorquePercent is not { } torquePct)
        {
            CurrentTorqueNm = CurrentPowerW = CurrentNp = CurrentRe = CurrentFr = CurrentFlowVvm = CurrentPowerRatio = null;
            UpdateGasLoopStatus();
            NotifyLiveText();
            return;
        }
        var doc = CurrentTest;
        var tNom = doc?.Calibration?.MotorRatedTorqueNm ?? doc?.MotorRatedTorqueNm ?? DefaultMotorRatedTorqueNm;
        var torqueNm = doc?.Calibration is { } cal ? cal.Scale * (torquePct / 100.0 * cal.MotorRatedTorqueNm) + cal.Offset : torquePct / 100.0 * tNom;
        CurrentTorqueNm = torqueNm;
        CurrentPowerW = PowerCalc.ShaftPower(torqueNm, rpm);
        CurrentNp = CurrentRe = CurrentFr = CurrentFlowVvm = CurrentPowerRatio = null;
        var reference = Impellers.OrderByDescending(i => i.DiameterM).FirstOrDefault();
        if (doc is not null && reference is not null && reference.DiameterM > 0 && rpm > 0 && doc.Fluid.DensityKgM3 > 0 && doc.Fluid.ViscosityPaS > 0)
        {
            var netPower = CurrentPowerW.Value - (doc.Tare is null ? 0 : TareInterpolator.InterpolatePowerW(doc.Tare, rpm));
            CurrentNp = PowerCalc.PowerNumber(netPower, doc.Fluid.DensityKgM3, rpm, reference.DiameterM);
            CurrentRe = PowerCalc.ReynoldsNumber(doc.Fluid.DensityKgM3, rpm, reference.DiameterM, doc.Fluid.ViscosityPaS);
            CurrentFr = PowerCalc.FroudeNumber(rpm, reference.DiameterM);
            if (CurrentFlowLpm is { } flow)
            {
                CurrentFlowVvm = LiquidVolumeL > 0 ? Math.Round(flow / LiquidVolumeL, 3) : null;
                if (flow > 0.05)
                {
                    var (p0, _, _) = _analysis.ResolveReferenceP0(rpm, doc);
                    if (p0 is { } p0W && p0W > 0)
                    {
                        CurrentPowerRatio = Math.Round(netPower / p0W, 4);
                    }
                }
            }
        }
        UpdateGasLoopStatus();
        NotifyLiveText();
    }

    /// <summary>
    /// The "Malha de gás" chip. "Alívio Estabilizando" is reserved for the runner's own phase:
    /// outside a run, <c>v_Flow = 1</c> is the active-high main shutoff that <c>FlowSafeStop</c>
    /// leaves closed on purpose — seen on 2026-09-11 at the end of Rushton-Smith, 63/63 accepted,
    /// both valves closed, and the chip stuck on "Alívio Estabilizando" (§F.4).
    /// </summary>
    private void UpdateGasLoopStatus()
    {
        GasLoopStatusBadge = GasLoopStatusFor(_runner, _latestSnapshot, _rig());
        OnPropertyChanged(nameof(GasLoopStatusBadge));
    }

    /// <summary>Chip text for the runner's pre-stage; the other texts are <see cref="GasRouting.Describe(ObservedGasRoute)"/>.</summary>
    internal const string PrestagingChipText = "Ar por C · estabilizando";

    /// <summary>
    /// The "Malha de gás" chip reads the wire the way the rig does: the observed pair and the
    /// echoed setpoint, through <see cref="GasRouting.Interpret"/>, so "Reator (A)", "Descarga +
    /// N₂ (B/C)", "Gás sem destino" and "A e B/C abertas" are the same words everywhere. The
    /// pre-stage text is reserved for the runner's own phase.
    /// </summary>
    internal static string GasLoopStatusFor(IPowerTestRunner? runner, SensorSnapshot? snapshot, GasRigConfiguration rig)
    {
        if (runner?.Phase == PowerRunPhase.PrestagingFlow)
        {
            return PrestagingChipText;
        }

        if (snapshot is null)
        {
            return GasRouting.Describe(ObservedGasRoute.Closed);
        }

        var setpoint = double.IsFinite(snapshot.FlowSetpoint) ? snapshot.FlowSetpoint : 0.0;
        return GasRouting.Describe(GasRouting.Interpret(snapshot.FlowValve1 == 1, snapshot.FlowValve2 == 1, setpoint, rig));
    }

    private void OnRunnerStateChanged() => RunOnUi(UpdateRunnerState);

    /// <summary>
    /// The live chart shows one run at a time. The runner announces every run it starts —
    /// including the gassed subphase of a <c>Both</c> condition, which is its own
    /// <see cref="PowerRun"/> with its own folder — so this is the single place that clears it.
    /// </summary>
    private void OnRunStarted(PowerRun run) => RunOnUi(LivePoints.Clear);

    /// <summary>
    /// What the last structural refresh saw. A telemetry frame only refreshes the read-outs; the
    /// grids, the document-state notifications and the preflight are refreshed when one of these
    /// changes — a phase, a run, a replicate accepted or rejected, a plan or settings revision.
    /// Bench of 2026-09-11: refreshing the grids on every frame rebuilt the plan table twice a
    /// second, so no row container, hover state or cell edit ever survived.
    /// </summary>
    private readonly record struct RunnerStructureKey(
        PowerRunPhase Phase,
        bool IsRunning,
        bool IsInReview,
        Guid? RunId,
        int RunCount,
        int AcceptedReplicates,
        int CompletedReplicates,
        int SkippedConditions,
        int ConditionCount,
        int SettingsRevision,
        PowerTestStatus Status);

    private RunnerStructureKey? _structureKey;

    private RunnerStructureKey StructureKeyOf(IPowerTestRunner runner, PowerTestDocument? doc) => new(
        runner.Phase,
        runner.IsRunning,
        runner.IsInReview,
        runner.CurrentRun?.RunId,
        doc?.Runs.Count ?? 0,
        doc?.Conditions.Sum(c => c.AcceptedReplicates) ?? 0,
        doc?.Conditions.Sum(c => c.CompletedReplicates) ?? 0,
        doc?.Conditions.Count(c => c.Status == PowerConditionStatus.Skipped) ?? 0,
        doc?.Conditions.Count ?? 0,
        doc?.SettingsRevision ?? 0,
        doc?.Status ?? PowerTestStatus.Draft);

    private void UpdateRunnerState()
    {
        if (_runner is null)
        {
            return;
        }

        var documentChanged = false;
        if (_runner.CurrentTest is { } runnerDoc && !ReferenceEquals(CurrentTest, runnerDoc))
        {
            CurrentTest = runnerDoc;
            documentChanged = true;
        }

        if (_runner.Phase == PowerRunPhase.PreparingNextRun && LivePoints.Count > 0)
        {
            LivePoints.Clear();
        }

        UpdateRunnerLiveState();

        var key = StructureKeyOf(_runner, _runner.CurrentTest);
        if (documentChanged || _structureKey != key)
        {
            var acceptedChanged = _structureKey?.AcceptedReplicates != key.AcceptedReplicates;
            _structureKey = key;
            UpdateRunnerStructure(acceptedChanged);
        }
    }

    /// <summary>Per-sample: flags, labels, the CI gauge, sequence progress and the gas-loop chip. No grids.</summary>
    private void UpdateRunnerLiveState()
    {
        if (_runner is null)
        {
            return;
        }

        IsRunning = _runner.IsRunning;
        IsInReview = _runner.IsInReview;
        ReviewHasCapture = _runner.CurrentRun is { } reviewed && PowerTestRunner.HasCapture(reviewed);
        ReviewNoCaptureText = IsInReview && !ReviewHasCapture ? $"Sem captura — {_runner.StatusMessage}" : "";
        OnPropertyChanged(nameof(CanAcceptRun));
        OnPropertyChanged(nameof(IsReviewingUnperformedRun));
        IsPaused = _runner.IsPausedByOperator || _runner.IsPausedForMeasurement;
        IsWaitingManualEnergy = _runner.Phase == PowerRunPhase.HoldingForManualEnergy;
        IsAccumulating = _runner.Phase == PowerRunPhase.AccumulatingToTarget;
        PhaseLabel = PhaseText(_runner.Phase);
        StatusMessage = _runner.StatusMessage;
        PhaseElapsedLabel = FormatDuration(_runner.PhaseElapsedSeconds);
        TotalElapsedLabel = FormatDuration(_runner.TotalElapsedSeconds);
        var current = _runner.CurrentTorqueCi95Percent;
        var target = _runner.CurrentTorqueCiTargetPercent;
        CiProgressPercent = current <= 0 ? 0 : Math.Clamp(target / current * 100.0, 0, 100);
        CiLabel = current > 0 ? $"IC95 ±{current:F4}% · alvo ≤ {target:F4}% · tentativa {_runner.CurrentAttempt}" : "IC95 — · aguardando acumulação";
        CurrentConditionId = _runner.IsRunning ? _runner.CurrentCondition?.ConditionId : null;
        UpdateSequenceProgress();
        UpdateGasLoopStatus();
    }

    /// <summary>Structural: the results and plan grids (updated in place), document state, preflight.</summary>
    private void UpdateRunnerStructure(bool acceptedChanged)
    {
        if (_runner is null)
        {
            return;
        }

        if (_runner.CurrentTest is not null)
        {
            RebuildResults();
        }

        // Idle telemetry updates the readouts, but must never replace cells being edited in the plan.
        // During a run the runner is authoritative for counters and condition status.
        if (_runner.Phase != PowerRunPhase.Idle)
        {
            RefreshConditionRows();
        }
        NotifyDocumentState();
    }

    /// <summary>
    /// The runner raises <c>DataPointAdded</c> and then <c>StateChanged</c> in the same frame, so
    /// the point only goes to the chart here; everything else is refreshed by the state change.
    /// </summary>
    private void OnDataPointAdded(PowerDataPoint point) => RunOnUi(() =>
    {
        LivePoints.Add(point);
        while (LivePoints.Count > 6000)
        {
            LivePoints.RemoveAt(0);
        }
    });

    /// <summary>
    /// <summary>
    /// Brings <see cref="Results"/> in line with the document's runs <em>in place</em>: rows are
    /// matched by <c>RunId</c>, replaced only when their content changed, moved when the order
    /// changed, and only then added or removed. A <c>Reset</c> would drop the grid's containers
    /// and the operator's selection on every call.
    /// </summary>
    private void RebuildResults()
    {
        if (CurrentTest is null)
        {
            Results.Clear();
            return;
        }

        var selectedRunId = SelectedResultRow?.RunId;
        var index = 0;
        foreach (var run in CurrentTest.Runs
            .OrderBy(r => PowerResultRow.GetPhasePriority(r.Phase))
            .ThenBy(r => r.AgitationRpm > 0 ? r.AgitationRpm : r.MeanRpmMeasured)
            .ThenBy(r => r.GasFlowLpm ?? 0.0))
        {
            var row = PowerResultRow.From(run);
            var existingIndex = -1;
            for (var i = index; i < Results.Count; i++)
            {
                if (Results[i].RunId == run.RunId)
                {
                    existingIndex = i;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                Results.Insert(index, row);
            }
            else
            {
                if (existingIndex != index)
                {
                    Results.Move(existingIndex, index);
                }
                if (!Results[index].Equals(row))
                {
                    Results[index] = row;
                }
            }
            index++;
        }

        while (Results.Count > index)
        {
            Results.RemoveAt(Results.Count - 1);
        }

        if (selectedRunId is { } id && SelectedResultRow?.RunId != id || selectedRunId is { } && !Results.Contains(SelectedResultRow!))
        {
            SelectedResultRow = Results.FirstOrDefault(r => r.RunId == selectedRunId);
        }

        UpdateKlaEfficiencyComparison();
        OnPropertyChanged(nameof(ResultsCsvPath));
    }

    private void UpdateSequenceProgress()
    {
        if (CurrentTest is null) { SequenceProgressPercent = 0; SequenceProgressLabel = "0/0 pontos"; EtaLabel = "ETA —"; return; }
        var total = CurrentTest.Conditions.Where(c => c.Status != PowerConditionStatus.Skipped).Sum(c => c.RequestedReplicates);
        var done = CurrentTest.Conditions.Sum(c => c.AcceptedReplicates);
        SequenceProgressPercent = total > 0 ? Math.Clamp(done * 100.0 / total, 0, 100) : 0;
        SequenceProgressLabel = $"{done}/{total} pontos aceitos";
        var elapsed = _runner?.TotalElapsedSeconds ?? 0;
        EtaLabel = done > 0 && total > done ? $"ETA {FormatDuration(elapsed / done * (total - done))}" : total > 0 && done >= total ? "Sequência concluída" : "ETA —";
    }

    /// <summary>
    /// Brings the plan grid in line with the document's conditions <em>in place</em>: rows are
    /// matched by <c>ConditionId</c> and receive the runner-owned fields (status, counters); rows
    /// are moved, added or removed only when the plan itself changed. The row objects — and with
    /// them the grid's containers, hover, scroll position and selection — survive every frame.
    /// Copying into a row raises its <c>PropertyChanged</c>, so persistence is suppressed: these
    /// values come from the document, they are not edits.
    /// </summary>
    private void RefreshConditionRows()
    {
        if (CurrentTest is null)
        {
            return;
        }

        var selectedId = SelectedCondition?.ConditionId;
        _suppressConditionPersistence = true;
        try
        {
            var index = 0;
            foreach (var condition in CurrentTest.Conditions.OrderBy(c => c.OrderIndex))
            {
                var existingIndex = -1;
                for (var i = index; i < Conditions.Count; i++)
                {
                    if (Conditions[i].ConditionId == condition.ConditionId)
                    {
                        existingIndex = i;
                        break;
                    }
                }

                if (existingIndex < 0)
                {
                    Conditions.Insert(index, condition.Clone());
                }
                else
                {
                    if (existingIndex != index)
                    {
                        Conditions.Move(existingIndex, index);
                    }
                    Conditions[index].CopyRuntimeStateFrom(condition);
                }
                index++;
            }

            while (Conditions.Count > index)
            {
                Conditions.RemoveAt(Conditions.Count - 1);
            }
        }
        finally
        {
            _suppressConditionPersistence = false;
        }

        if (SelectedCondition is null || !Conditions.Contains(SelectedCondition))
        {
            SelectedCondition = Conditions.FirstOrDefault(c => c.ConditionId == selectedId) ?? Conditions.FirstOrDefault();
        }
    }

    private void OnOwnershipChanged(OwnershipTransfer _) => RunOnUi(OnOwnershipTransferred);
    private void RefreshOwnership() => AgitationOwnerLabel = _arbiter.OwnerOf(ActuatorId.Agitation) switch
    {
        CommandOwner.Manual => "Operador",
        CommandOwner.Automatic => "Cascata",
        CommandOwner.Recipe => "Receita",
        CommandOwner.KlaAssay => "Ensaio kLa",
        CommandOwner.PowerAssay => "Ensaio de potência",
        var owner => owner.ToString(),
    };

    private void OnOwnershipTransferred()
    {
        RefreshOwnership();

        // Ownership is one of the preflight gates, so a hand-over must refresh the read-out at once
        // rather than waiting for the throttle window.
        _lastPreflightTick = 0;
        RefreshPreflight();
    }

    private void NotifyLiveText()
    {
        OnPropertyChanged(nameof(LiveSummary)); OnPropertyChanged(nameof(CurrentRpmText)); OnPropertyChanged(nameof(CurrentTorquePercentText));
        OnPropertyChanged(nameof(CurrentTorqueNmText)); OnPropertyChanged(nameof(CurrentPowerWText)); OnPropertyChanged(nameof(CurrentFlowText));
        OnPropertyChanged(nameof(CurrentFlowVvmText));
        OnPropertyChanged(nameof(CurrentNpText)); OnPropertyChanged(nameof(CurrentReText)); OnPropertyChanged(nameof(CurrentFrText));
        OnPropertyChanged(nameof(CurrentPowerRatioText)); OnPropertyChanged(nameof(GasLoopStatusBadge));
    }

    private void NotifyGeometryState()
    {
        OnPropertyChanged(nameof(ImpellerSetHash)); OnPropertyChanged(nameof(TareStatus)); OnPropertyChanged(nameof(VortexWarning));
    }

    private void NotifyDocumentState()
    {
        OnPropertyChanged(nameof(HasActiveTest)); OnPropertyChanged(nameof(CanEditPlan)); OnPropertyChanged(nameof(CanStartOrContinue)); OnPropertyChanged(nameof(CanManageTest)); OnPropertyChanged(nameof(CanReopenTest));
        OnPropertyChanged(nameof(CanPause)); OnPropertyChanged(nameof(CanStop)); OnPropertyChanged(nameof(CanSkipCurrent));
        OnPropertyChanged(nameof(CanChangeResultStatus));
        OnPropertyChanged(nameof(PauseButtonLabel)); OnPropertyChanged(nameof(TestStatusLabel));
        OnPropertyChanged(nameof(TareStatus)); OnPropertyChanged(nameof(ResultModeLabel)); OnPropertyChanged(nameof(ImpellerSetHash));
        OnPropertyChanged(nameof(VortexWarning)); OnPropertyChanged(nameof(ResultsCsvPath)); OnPropertyChanged(nameof(ReferenceLiteratureNp));
        OnPropertyChanged(nameof(CanImportFromKlaMap));

        // Loading, saving, starting and finishing all change what the preflight would answer, so
        // the read-out is refreshed here rather than waiting for the next telemetry sample.
        _lastPreflightTick = 0;
        RefreshPreflight();
    }

    private void NormalizeImpellerOrder()
    {
        for (var i = 0; i < Impellers.Count; i++)
        {
            Impellers[i].StageIndex = i;
        }
    }
    private void NormalizeConditionOrder()
    {
        for (var i = 0; i < Conditions.Count; i++)
        {
            Conditions[i].OrderIndex = i;
        }
    }

    private static void MoveItem<T>(ObservableCollection<T> items, T? selected, int delta, Action normalize) where T : class
    {
        if (selected is null)
        {
            return;
        }

        var oldIndex = items.IndexOf(selected);
        var newIndex = oldIndex + delta;
        if (oldIndex < 0 || newIndex < 0 || newIndex >= items.Count)
        {
            return;
        }

        items.Move(oldIndex, newIndex);
        normalize();
    }

    private void ShowError(string message)
    {
        ValidationMessage = message;
        StatusMessage = message;
        _dialogs?.Confirm("Ensaio de potência", message, "OK", "");
    }

    internal static bool TryParseUiDouble(string text, out double value)
    {
        // Scientific setup fields do not accept group separators. This avoids interpreting
        // pt-BR "1,25" as invariant "125", while accepting both operator comma and persisted dot.
        const NumberStyles styles = NumberStyles.Float;
        return (double.TryParse(text, styles, CultureInfo.InvariantCulture, out value) || double.TryParse(text, styles, CultureInfo.CurrentCulture, out value)) && double.IsFinite(value);
    }

    private static bool FinitePositive(double value) => double.IsFinite(value) && value > 0;
    private static double? ValidOptional(double value) => double.IsFinite(value) && value > SensorReadings.NotReceived ? value : null;
    private static string Format(double? value, string format) => value is { } finite && double.IsFinite(finite) ? finite.ToString(format, CultureInfo.CurrentCulture) : "—";
    private static string FormatDuration(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"hh\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);
    private static string PhaseText(PowerRunPhase phase) => phase switch
    {
        PowerRunPhase.Idle => "Pronto",
        PowerRunPhase.Preflight => "Pré-voo",
        PowerRunPhase.PreparingCondition => "Preparando",
        PowerRunPhase.SettingSpeed => "Ajustando rotação",
        PowerRunPhase.SettlingTorque => "Porta 1 · estacionariedade",
        PowerRunPhase.AccumulatingToTarget => "Porta 2 · IC95",
        PowerRunPhase.PausedByOperator => "Pausado pelo operador",
        PowerRunPhase.PausedForMeasurement => "Pausado · sem medida",
        PowerRunPhase.HoldingForManualEnergy => "Aguardando wattímetro",
        PowerRunPhase.Reviewing => "Revisão",
        PowerRunPhase.Accepted => "Aceito",
        PowerRunPhase.Rejected => "Rejeitado",
        PowerRunPhase.Completed => "Concluído",
        PowerRunPhase.Faulted => "Interrompido",
        _ => "Estado não mapeado",
    };

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.HasShutdownStarted && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(action);
        }
        else
        {
            action();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tareCancellation?.Cancel();
        _tareCancellation?.Dispose();
        _tareCancellation = null;
        Conditions.CollectionChanged -= OnConditionsCollectionChanged;
        foreach (var condition in _flowConfiguredConditions)
        {
            condition.PropertyChanged -= OnConditionPropertyChanged;
            condition.ConfigureFlowConversion(null);
        }
        _flowConfiguredConditions.Clear();
        _device.TelemetryReceived -= OnTelemetryReceived;
        _arbiter.OwnershipChanged -= OnOwnershipChanged;
        if (_runner is not null) { _runner.StateChanged -= OnRunnerStateChanged; _runner.DataPointAdded -= OnDataPointAdded; _runner.RunStarted -= OnRunStarted; }
    }
}

public sealed record PowerResultRow
{
    public required Guid RunId { get; init; }
    public required string Rpm { get; init; }
    public required string NetTorque { get; init; }
    public required string Power { get; init; }
    public required string Np { get; init; }
    public required string Re { get; init; }
    public required string Ci { get; init; }
    public required string StopReason { get; init; }
    public required string Attempts { get; init; }
    public required string Timestamp { get; init; }
    public required string Status { get; init; }
    public bool IsAccepted { get; init; }

    // Gassed-result columns.
    public required string GasFlowLpm { get; init; }
    public required string FlG { get; init; }
    public required string Fr { get; init; }
    public required string PgLiquid { get; init; }
    public required string P0Ref { get; init; }
    public required string PowerRatio { get; init; }

    public double ReynoldsNumber { get; init; }
    public double PowerNumber { get; init; }
    public double PowerNumberCi95 { get; init; }
    public double AerationNumber { get; init; }
    public double FroudeNumber { get; init; }
    public double Ratio { get; init; }
    public double RatioCi95 { get; init; }
    public PowerGasMode GasMode { get; init; }
    public bool IsGassed => GasMode is PowerGasMode.Gassed or PowerGasMode.Both;

    // Sorting and grouping backing fields
    public double MeanRpm { get; init; }
    public double NetTorqueNm { get; init; }
    public double NetPowerW { get; init; }
    public double GasFlowLpmNumber { get; init; }
    public double PgLiquidW { get; init; }
    public double P0RefW { get; init; }
    public int AttemptsCount { get; init; }
    public DateTimeOffset SortTimestamp { get; init; }
    public int SortPhasePriority { get; init; }

    public static string StatusText(PowerRunPhase phase) => phase switch
    {
        PowerRunPhase.Idle => "Pronto",
        PowerRunPhase.Preflight => "Pré-voo",
        PowerRunPhase.PreparingCondition => "Preparando",
        PowerRunPhase.SettingSpeed => "Ajustando rotação",
        PowerRunPhase.PrestagingFlow => "Preparando vazão",
        PowerRunPhase.OpeningGas => "Abrindo gás",
        PowerRunPhase.SettlingTorque => "Estabilizando torque",
        PowerRunPhase.AccumulatingToTarget => "Acumulando amostras",
        PowerRunPhase.PausedByOperator => "Pausado pelo operador",
        PowerRunPhase.PausedForMeasurement => "Pausado · sem medida",
        PowerRunPhase.HoldingForManualEnergy => "Aguardando wattímetro",
        PowerRunPhase.Accepted => "Aceito",
        PowerRunPhase.Captured => "Em revisão",
        PowerRunPhase.Reviewing => "Em revisão",
        PowerRunPhase.Rejected => "Rejeitado",
        PowerRunPhase.StoppingRun => "Parando",
        PowerRunPhase.PreparingNextRun => "Preparando próximo ponto",
        PowerRunPhase.Aborting => "Interrompendo",
        PowerRunPhase.Completed => "Concluído",
        PowerRunPhase.Faulted => "Interrompido",
        _ => "Estado não mapeado",
    };

    public static string StopReasonText(PowerStopReason reason) => reason switch
    {
        PowerStopReason.Target => "Alvo de precisão",
        PowerStopReason.Tmax => "Limite de torque",
        PowerStopReason.NotConverged => "Não convergiu",
        PowerStopReason.Aborted => "Interrompido pelo operador",
        _ => "Motivo não mapeado",
    };

    public static PowerResultRow From(PowerRunSummary run)
    {
        static string F(double? value, string format) => value is { } v && double.IsFinite(v) ? v.ToString(format, CultureInfo.CurrentCulture) : "—";
        var analysis = run.Analysis;

        var ratioText = "—";
        if (run.PowerRatio is { } pr && double.IsFinite(pr))
        {
            ratioText = run.PowerRatioCi95 is { } prCi && double.IsFinite(prCi) && prCi > 0
                ? $"{pr.ToString("F3", CultureInfo.CurrentCulture)} ± {prCi.ToString("F3", CultureInfo.CurrentCulture)}"
                : pr.ToString("F3", CultureInfo.CurrentCulture);
        }

        var npText = "—";
        if (analysis?.AssemblyPowerNumber is { } assemblyNp && double.IsFinite(assemblyNp))
        {
            npText = analysis.AssemblyPowerNumberCi95 is { } assemblyNpCi && double.IsFinite(assemblyNpCi) && assemblyNpCi > 0
                ? $"{assemblyNp.ToString("G5", CultureInfo.CurrentCulture)} ± {assemblyNpCi.ToString("G4", CultureInfo.CurrentCulture)}"
                : assemblyNp.ToString("G5", CultureInfo.CurrentCulture);
        }

        return new PowerResultRow
        {
            RunId = run.RunId,
            Rpm = F(run.AgitationRpm, "F1"),
            NetTorque = F(run.NetPowerW is { } netPower && run.MeanRpmMeasured != 0
                ? netPower / PowerCalc.AngularVelocity(run.MeanRpmMeasured)
                : null, "F5"),
            Power = F(run.NetPowerW, "F4"),
            Np = npText,
            Re = F(analysis?.AssemblyReynoldsNumber, "G5"),
            Ci = F(analysis?.AssemblyPowerNumberCi95, "G4"),
            StopReason = StopReasonText(run.StopReason),
            Attempts = run.Tries.ToString(CultureInfo.CurrentCulture),
            Timestamp = (run.CompletedUtc ?? run.StartedUtc).ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.CurrentCulture),
            Status = StatusText(run.Phase),
            IsAccepted = run.Phase == PowerRunPhase.Accepted,

            GasFlowLpm = F(run.GasFlowLpm, "F2"),
            FlG = F(run.GasFlowNumber, "G5"),
            Fr = F(run.FroudeNumber, "G4"),
            PgLiquid = F(run.GassedPowerW ?? (run.GasMode is PowerGasMode.Gassed or PowerGasMode.Both ? run.NetPowerW : null), "F3"),
            P0Ref = F(run.ReferenceP0W, "F3"),
            PowerRatio = ratioText,

            ReynoldsNumber = analysis?.AssemblyReynoldsNumber ?? double.NaN,
            PowerNumber = analysis?.AssemblyPowerNumber ?? double.NaN,
            PowerNumberCi95 = analysis?.AssemblyPowerNumberCi95 ?? double.NaN,
            AerationNumber = run.GasFlowNumber ?? double.NaN,
            FroudeNumber = run.FroudeNumber ?? double.NaN,
            Ratio = run.PowerRatio ?? double.NaN,
            RatioCi95 = run.PowerRatioCi95 ?? double.NaN,
            GasMode = run.GasMode,

            MeanRpm = run.AgitationRpm > 0 ? run.AgitationRpm : run.MeanRpmMeasured,
            NetTorqueNm = run.NetPowerW is { } np && run.MeanRpmMeasured != 0 ? np / PowerCalc.AngularVelocity(run.MeanRpmMeasured) : double.NaN,
            NetPowerW = run.NetPowerW ?? double.NaN,
            GasFlowLpmNumber = run.GasFlowLpm ?? 0.0,
            PgLiquidW = run.GassedPowerW ?? (run.GasMode is PowerGasMode.Gassed or PowerGasMode.Both ? run.NetPowerW : null) ?? double.NaN,
            P0RefW = run.ReferenceP0W ?? double.NaN,
            AttemptsCount = run.Tries,
            SortTimestamp = run.CompletedUtc ?? run.StartedUtc,
            SortPhasePriority = GetPhasePriority(run.Phase),
        };
    }

    public static int GetPhasePriority(PowerRunPhase phase) => phase switch
    {
        PowerRunPhase.Accepted => 1,
        PowerRunPhase.Captured or PowerRunPhase.Reviewing => 2,
        PowerRunPhase.Rejected => 3,
        PowerRunPhase.Faulted or PowerRunPhase.Aborting => 4,
        _ => 5,
    };
}

public sealed record EnumChoice<T>(T Value, string Label) where T : struct, Enum;

public enum PowerSweepType
{
    VariableNConstantQg,
    VariableQgConstantN,
    MatrixNByQg,
}

public partial class EditableTarePoint : ObservableObject
{
    private readonly TarePoint _original;

    [ObservableProperty] private double _rpm;
    [ObservableProperty] private double _pVoidW;
    [ObservableProperty] private double _sigmaTauPercent;

    public EditableTarePoint(TarePoint point)
    {
        _original = point;
        _rpm = point.Rpm;
        _pVoidW = point.PVoidW;
        _sigmaTauPercent = point.SigmaTauPercent;
    }

    public TarePoint ToRecord() => _original with
    {
        Rpm = Rpm,
        PVoidW = PVoidW,
        SigmaTauPercent = SigmaTauPercent
    };
}

