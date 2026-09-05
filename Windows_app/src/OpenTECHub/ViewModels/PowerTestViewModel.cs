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
    private readonly ICommandArbiter _arbiter;
    private readonly IPowerTestRunner? _runner;
    private readonly IDialogService? _dialogs;
    private readonly IPowerAnalysisEngine _analysis;
    private readonly IKlaProfileStore? _klaStore;
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
    private long _lastPreflightTick;
    private bool _suppressConditionPersistence;
    private bool _disposed;

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
        PowerMapViewModel? mapViewModel)
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
        MapViewModel = mapViewModel;
        TestRootDirectory = store.RootDirectory;

        _device.TelemetryReceived += OnTelemetryReceived;
        _arbiter.OwnershipChanged += OnOwnershipChanged;
        if (_runner is not null)
        {
            _runner.StateChanged += OnRunnerStateChanged;
            _runner.DataPointAdded += OnDataPointAdded;
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
    public IReadOnlyList<EnumChoice<PowerVentValve>> VentValves { get; } =
    [
        new(PowerVentValve.Valve2, "Válvula 2 (Alívio)"),
        new(PowerVentValve.Valve1, "Válvula 1 (Alívio)"),
    ];
    public IReadOnlyList<EnumChoice<PowerSweepType>> SweepTypes { get; } =
    [
        new(PowerSweepType.VariableNConstantQg, "N variável (Qg constante)"),
        new(PowerSweepType.VariableQgConstantN, "Qg variável (N constante — Flooding)"),
        new(PowerSweepType.MatrixNByQg, "Matriz 2D (N × Qg)"),
    ];

    public ObservableCollection<PowerTestSummary> Tests { get; } = [];
    public ObservableCollection<Impeller> Impellers { get; } = [];
    public ObservableCollection<PowerCondition> Conditions { get; } = [];
    public ObservableCollection<PowerDataPoint> LivePoints { get; } = [];
    public ObservableCollection<PowerResultRow> Results { get; } = [];

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
    [ObservableProperty] public partial double? CurrentFlG { get; private set; }
    [ObservableProperty] public partial double? CurrentFr { get; private set; }
    [ObservableProperty] public partial double? CurrentFlowVvm { get; private set; }
    [ObservableProperty] public partial double? CurrentPowerRatio { get; private set; }
    [ObservableProperty] public partial string GasLoopStatusBadge { get; private set; } = "Fechado";
    [ObservableProperty] public partial string AgitationOwnerLabel { get; private set; } = "Manual";
    [ObservableProperty] public partial string StatusMessage { get; private set; } = "Crie ou abra um ensaio de potência.";
    [ObservableProperty] public partial string ValidationMessage { get; private set; } = "";
    [ObservableProperty] public partial string PhaseLabel { get; private set; } = "Inativo";
    [ObservableProperty] public partial bool IsRunning { get; private set; }
    [ObservableProperty] public partial bool IsInReview { get; private set; }
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
    [ObservableProperty] public partial bool VentStabilizationEnabled { get; set; }
    [ObservableProperty] public partial PowerVentValve SelectedVentValve { get; set; } = PowerVentValve.Valve2;
    [ObservableProperty] public partial double VentFlowToleranceLpm { get; set; } = 0.2;
    [ObservableProperty] public partial int VentFlowStableSamples { get; set; } = 5;
    [ObservableProperty] public partial double VentAgitationRpm { get; set; } = 15.0;
    [ObservableProperty] public partial double MaxVentStabilizationSeconds { get; set; } = 120.0;
    [ObservableProperty] public partial bool ManualEnergyCaptureEnabled { get; set; }

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

    [ObservableProperty] public partial bool ShowFloodingChart { get; set; }
    [ObservableProperty] public partial FloodingAnalysisResult? FloodingResult { get; private set; }
    [ObservableProperty] public partial bool HasFloodingPoint { get; private set; }
    [ObservableProperty] public partial string FloodingSummary { get; private set; } = "";
    [ObservableProperty] public partial string FloodingCoordinates { get; private set; } = "";
    [ObservableProperty] public partial string FloodingDeviationText { get; private set; } = "";

    [ObservableProperty] public partial PowerResultRow? SelectedResultRow { get; set; }
    public bool CanSetManualFlooding => SelectedResultRow is not null && SelectedResultRow.IsGassed;
    public bool CanToggleRowAcceptance => SelectedResultRow is not null;

    partial void OnSelectedResultRowChanged(PowerResultRow? value)
    {
        OnPropertyChanged(nameof(CanSetManualFlooding));
        OnPropertyChanged(nameof(CanToggleRowAcceptance));
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
    // 8.1 Torque Calibration (1-point static)
    [ObservableProperty] public partial bool IsCalibrationAssistantOpen { get; set; }
    [ObservableProperty] public partial double CalibrationMassKg { get; set; } = 0.100;
    [ObservableProperty] public partial double CalibrationLeverArmM { get; set; } = 0.050;
    [ObservableProperty] public partial double CalibrationRatedTorqueNm { get; set; } = 1.27;
    [ObservableProperty] public partial double CalibrationMeasuredTorquePercent { get; set; } = 3.86;
    public double CalibrationReferenceNm => CalibrationMassKg * PowerCalc.GravityMetersPerSecondSquared * CalibrationLeverArmM;
    public double CalibrationCalculatedScale => CalibrationMeasuredTorquePercent > 0 ? CalibrationReferenceNm / ((CalibrationMeasuredTorquePercent / 100.0) * CalibrationRatedTorqueNm) : 1.0;

    // 8.2 Tare Curve Assistant (in-air sweep)
    [ObservableProperty] public partial bool IsTareAssistantOpen { get; set; }
    [ObservableProperty] public partial bool IsTareRunning { get; set; }
    [ObservableProperty] public partial double TareStartRpm { get; set; } = 100.0;
    [ObservableProperty] public partial double TareEndRpm { get; set; } = 1000.0;
    [ObservableProperty] public partial double TareStepRpm { get; set; } = 100.0;
    [ObservableProperty] public partial string TareProgressMessage { get; set; } = "";
    public ObservableCollection<TarePoint> CurrentTarePoints { get; } = [];

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
    public bool CanStartOrContinue => CurrentTest is not null && !IsRunning && !IsTareRunning && !IsInReview && CurrentTest.Status != PowerTestStatus.Completed;
    public bool CanManageTest => CurrentTest is not null && !IsRunning && !IsTareRunning;

    partial void OnIsTareRunningChanged(bool value) => NotifyDocumentState();

    /// <summary>True when the runner's preflight passes right now (§12, §14).</summary>
    [ObservableProperty]
    public partial bool IsReadyToStart { get; set; }

    /// <summary>What the preflight says, so the operator reads it before pressing start.</summary>
    [ObservableProperty]
    public partial string PreflightMessage { get; set; } = "Abra ou crie um ensaio para começar.";

    public bool CanPause => IsRunning && _runner?.Phase is PowerRunPhase.SettingSpeed or PowerRunPhase.SettlingTorque or PowerRunPhase.AccumulatingToTarget or PowerRunPhase.PausedByOperator or PowerRunPhase.PausedForMeasurement;
    public bool CanStop => IsRunning;
    public bool CanSkipCurrent => IsRunning && _runner?.CurrentCondition is not null;
    public string PauseButtonLabel => _runner?.IsPausedByOperator == true || _runner?.IsPausedForMeasurement == true ? "▶ Retomar" : "⏸ Pausar";
    public string TestStatusLabel => CurrentTest?.Status switch
    {
        PowerTestStatus.Draft => "Rascunho",
        PowerTestStatus.Running => "Em execução",
        PowerTestStatus.Interrupted => "Interrompido · edição liberada",
        PowerTestStatus.Completed => "Concluído",
        _ => "Nenhum ensaio",
    };
    public string CalibrationStatus => CurrentTest?.Calibration is null ? "Calibração ausente" : "Calibração registrada";
    public string TareStatus
    {
        get
        {
            if (CurrentTest?.Tare is null)
            {
                return "Tara ausente";
            }

            var currentHash = PowerTestFileContracts.ComputeImpellerSetHash(BuildGeometry());
            if (!string.Equals(CurrentTest.Tare.ImpellerSetHash, currentHash, StringComparison.OrdinalIgnoreCase))
            {
                return "Tara de outro conjunto";
            }

            var calibrationHash = PowerTestFileContracts.ComputeTorqueCalibrationHash(
                CurrentTest.Calibration,
                CurrentTest.MotorRatedTorqueNm);
            return string.IsNullOrEmpty(CurrentTest.Tare.CalibrationHash) ||
                   string.Equals(CurrentTest.Tare.CalibrationHash, calibrationHash, StringComparison.OrdinalIgnoreCase)
                ? "Tara compatível"
                : "Tara anterior à calibração atual";
        }
    }
    public string ResultModeLabel => RelativeMode || CurrentTest?.Calibration is null || CurrentTest?.Tare is null ? "RELATIVO" : "ABSOLUTO";
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
    public double? ReferenceLiteratureNp => Impellers
        .OrderByDescending(i => i.DiameterM)
        .Select(i => i.LiteratureNp)
        .FirstOrDefault(value => value.HasValue);
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
    public string CurrentFlGText => Format(CurrentFlG, "G4");
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
            MinRpm = doc.Settings.MinRpm;
            MaxRpm = doc.Settings.MaxRpm;
            StepRpm = doc.Settings.DefaultStepRpm;
            MinFlowLpm = doc.Settings.MinFlowLpm;
            MaxFlowLpm = doc.Settings.MaxFlowLpm;
            StepFlowLpm = doc.Settings.DefaultStepFlowLpm;
            RelativeCiPercent = doc.Settings.RelativeCiFraction * 100.0;
            CiFloorSigmaMultiple = doc.Settings.CiFloorSigmaMultiple;
            MinimumSamples = doc.Settings.MinSamples;
            MaxCaptureSeconds = doc.Settings.MaxCaptureSeconds;
            MaxTries = doc.Settings.MaxTries;
            StationarityWindowSeconds = doc.Settings.StationarityWindowSeconds;
            StationaritySlopeTolerance = doc.Settings.StationaritySlopeTolerancePercentPerSecond;
            StationarityRequiredSamples = doc.Settings.StationarityRequiredSamples;
            VentStabilizationEnabled = doc.Settings.VentStabilizationEnabled;
            SelectedVentValve = doc.Settings.SelectedVentValve;
            VentFlowToleranceLpm = doc.Settings.VentFlowToleranceLpm;
            VentFlowStableSamples = doc.Settings.VentFlowStableSamples;
            VentAgitationRpm = doc.Settings.VentAgitationRpm;
            MaxVentStabilizationSeconds = doc.Settings.MaxVentStabilizationSeconds;
            ManualEnergyCaptureEnabled = doc.Settings.ManualEnergyCaptureEnabled;

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
            RebuildResults();
            _runner?.PrepareTest(doc);
            ValidationMessage = "";
            StatusMessage = $"Ensaio '{doc.Name}' carregado.";
            RefreshManualEnergyReadings();
            NotifyDocumentState();
            RecalculateLiveMetrics();
            _linkedKlaDocument = null;
            _linkedKlaSurface = null;
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

    [RelayCommand]
    private void AddCondition()
    {
        if (!CanEditPlan)
        {
            return;
        }

        var rpm = Conditions.Count == 0 ? Math.Max(MinRpm, 300) : Math.Min(MaxRpm, Conditions.Max(c => c.AgitationRpm) + StepRpm);
        var condition = new PowerCondition { AgitationRpm = rpm, OrderIndex = Conditions.Count };
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
                    GasFlowLpm = isGassed ? Math.Round(qg, 2) : null,
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

        if (_linkedKlaDocument is null && _klaStore is not null)
        {
            try
            {
                var experiments = _klaStore.LoadExperimentsAsync().GetAwaiter().GetResult();
                _linkedKlaDocument = experiments.FirstOrDefault(e => e.Snapshot.Id == CurrentTest.LinkedMap.MapId);
                if (_linkedKlaDocument is not null)
                {
                    try
                    {
                        var engine = new KlaMappingEngine();
                        _linkedKlaSurface = engine.Reconstruct(_linkedKlaDocument.Snapshot);
                    }
                    catch
                    {
                        _linkedKlaSurface = null;
                    }
                }
            }
            catch
            {
                // Best effort
            }
        }

        if (_linkedKlaDocument is null)
        {
            ControlRegionSummary = $"Mapa '{CurrentTest.LinkedMap.MapName}' vinculado, mas arquivo não encontrado.";
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
                            GasFlowLpm = hasGas ? SweepConstantQgLpm : null,
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
                            GasFlowLpm = hasGas ? qg : null,
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
                                GasFlowLpm = hasGas ? qg : null,
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
                GasFlowLpm = hasGas ? qg : null,
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
            if (CurrentTest.Status == PowerTestStatus.Running)
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

    // =========================================================================
    // Step 7: Revisão Científica e Ajuste Interativo de Flooding (§16)
    // =========================================================================

    [RelayCommand]
    private void SetSelectedAsFloodingPoint()
    {
        if (CurrentTest is null || SelectedResultRow is null)
        {
            return;
        }

        var run = CurrentTest.Runs.FirstOrDefault(r => r.RunId == SelectedResultRow.RunId);
        if (run is null || run.GasMode == PowerGasMode.Ungassed)
        {
            ShowError("Selecione um ponto experimental gaseificado na tabela para definir como transição de flooding.");
            return;
        }

        var refImpeller = CurrentTest.Geometry.Impellers.OrderByDescending(i => i.DiameterM).FirstOrDefault()
                          ?? new Impeller { Type = ImpellerType.RushtonFlatBlade, DiameterM = 0.06 };
        var rpm = run.MeanRpmMeasured > 0 ? run.MeanRpmMeasured : run.AgitationRpm;
        var fr = run.FroudeNumber ?? PowerCalc.FroudeNumber(rpm, refImpeller.DiameterM);
        var nienowFlG = PowerCalc.NienowFloodingAerationNumber(refImpeller.DiameterM, CurrentTest.Geometry.VesselDiameterM, fr);
        var expFlG = run.GasFlowNumber ?? (run.GasFlowLpm.HasValue && rpm > 0 ? PowerCalc.AerationNumber(run.GasFlowLpm.Value, rpm, refImpeller.DiameterM) : 0.0);
        var relDev = nienowFlG > 0 ? (expFlG - nienowFlG) / nienowFlG * 100.0 : 0.0;

        var adjusted = new FloodingAnalysisResult
        {
            ExperimentalFlG = expFlG,
            ExperimentalRpm = rpm,
            ExperimentalFlowLpm = run.GasFlowLpm ?? 0.0,
            TheoreticalFlGNienow = nienowFlG,
            RelativeDeviationPercent = relDev,
            ReferenceStageIndex = refImpeller.StageIndex,
            ReferenceImpellerType = refImpeller.Type,
            Method = FloodingDetectionMethod.ManualAdjusted,
            DeterminedUtc = DateTimeOffset.UtcNow,
            Notes = $"Transição ajustada manualmente pelo operador no ponto PG/P0 = {run.PowerRatio:F3} ({rpm:F0} rpm, {run.GasFlowLpm:F2} L/min)",
        };

        CurrentTest.Flooding = adjusted;
        _store.SaveFlooding(CurrentTest.FolderName, adjusted);
        _store.SaveTestManifest(CurrentTest);
        RebuildResults();
        StatusMessage = $"Ponto de flooding ajustado manualmente para Fl_G = {adjusted.ExperimentalFlG:G4}.";
    }

    [RelayCommand]
    private void ResetAutomaticFlooding()
    {
        if (CurrentTest is null)
        {
            return;
        }

        var auto = _analysis.DetectFlooding(CurrentTest.Runs, CurrentTest.Geometry);
        CurrentTest.Flooding = auto;
        if (auto is not null)
        {
            _store.SaveFlooding(CurrentTest.FolderName, auto);
        }
        _store.SaveTestManifest(CurrentTest);
        RebuildResults();
        StatusMessage = auto is not null
            ? $"Flooding automático detectado em Fl_G = {auto.ExperimentalFlG:G4}."
            : "Flooding automático restaurado (insuficientes pontos para detecção automática).";
    }

    [RelayCommand]
    private void ToggleAcceptSelectedRow()
    {
        if (CurrentTest is null || SelectedResultRow is null)
        {
            return;
        }

        var run = CurrentTest.Runs.FirstOrDefault(r => r.RunId == SelectedResultRow.RunId);
        if (run is null)
        {
            return;
        }

        var newPhase = run.Phase == PowerRunPhase.Accepted ? PowerRunPhase.Rejected : PowerRunPhase.Accepted;
        var updatedRun = run with { Phase = newPhase };
        var idx = CurrentTest.Runs.IndexOf(run);
        CurrentTest.Runs[idx] = updatedRun;

        var cond = CurrentTest.Conditions.FirstOrDefault(c => c.ConditionId == run.ConditionId);
        if (cond is not null)
        {
            cond.AcceptedReplicates = CurrentTest.Runs.Count(r => r.ConditionId == cond.ConditionId && r.Phase == PowerRunPhase.Accepted);
            cond.RejectedReplicates = CurrentTest.Runs.Count(r => r.ConditionId == cond.ConditionId && r.Phase == PowerRunPhase.Rejected);
            _store.SaveConditionsTable(CurrentTest.FolderName, CurrentTest.Conditions);
        }

        if (CurrentTest.Flooding?.Method != FloodingDetectionMethod.ManualAdjusted)
        {
            CurrentTest.Flooding = _analysis.DetectFlooding(CurrentTest.Runs, CurrentTest.Geometry);
            if (CurrentTest.Flooding is not null)
            {
                _store.SaveFlooding(CurrentTest.FolderName, CurrentTest.Flooding);
            }
        }

        _store.UpdateResultsSummary(CurrentTest.FolderName, CurrentTest);
        _store.SaveTestManifest(CurrentTest);
        RebuildResults();
        StatusMessage = $"Ponto {(newPhase == PowerRunPhase.Accepted ? "aceito" : "rejeitado")}: {run.AgitationRpm:F0} rpm (Qg = {run.GasFlowLpm:F2} L/min).";
    }

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
    /// Reprocesses all dimensionless groups (Re, Np, Fl_G, Fr, P_G/P0) and Nienow correlation (§16)
    /// without modifying raw data.
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

            double? flg = null;
            double? fr = null;
            double? vvm = null;
            if (run.GasMode != PowerGasMode.Ungassed && run.GasFlowLpm is { } flowLpm)
            {
                if (rpm > 0 && refImpeller.DiameterM > 0)
                {
                    flg = PowerCalc.AerationNumber(flowLpm, rpm, refImpeller.DiameterM);
                    fr = PowerCalc.FroudeNumber(rpm, refImpeller.DiameterM);
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
                GasFlowNumber = flg ?? run.GasFlowNumber,
                FroudeNumber = fr ?? run.FroudeNumber,
                Analysis = newAnalysis,
            };
        }

        for (var i = 0; i < CurrentTest.Runs.Count; i++)
        {
            var run = CurrentTest.Runs[i];
            if (run.GasMode == PowerGasMode.Ungassed)
            {
                continue;
            }

            var rpm = run.MeanRpmMeasured > 0 ? run.MeanRpmMeasured : run.AgitationRpm;
            var (p0, p0Ci, provenance) = _analysis.ResolveReferenceP0(rpm, CurrentTest);

            double? ratio = null;
            double? ratioCi = null;
            if (p0 is { } p0Val && p0Val > 0 && run.NetPowerW is { } pgVal)
            {
                var (r, ci) = PowerCalc.PropagatePowerRatioUncertainty(pgVal, run.Ci95PowerW ?? 0.0, p0Val, p0Ci ?? 0.0);
                ratio = r;
                ratioCi = ci;
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

        if (CurrentTest.Flooding is { } currentFlood)
        {
            var nienowFr = PowerCalc.FroudeNumber(currentFlood.ExperimentalRpm, refImpeller.DiameterM);
            var theoNienow = PowerCalc.NienowFloodingAerationNumber(refImpeller.DiameterM, geometry.VesselDiameterM, nienowFr);
            var expFlg = refImpeller.DiameterM > 0 && currentFlood.ExperimentalRpm > 0
                ? PowerCalc.AerationNumber(currentFlood.ExperimentalFlowLpm, currentFlood.ExperimentalRpm, refImpeller.DiameterM)
                : currentFlood.ExperimentalFlG;
            var relDev = theoNienow > 0 ? (expFlg - theoNienow) / theoNienow * 100.0 : 0.0;

            CurrentTest.Flooding = currentFlood with
            {
                ExperimentalFlG = expFlg,
                TheoreticalFlGNienow = theoNienow,
                RelativeDeviationPercent = relDev,
            };
            _store.SaveFlooding(CurrentTest.FolderName, CurrentTest.Flooding);
        }
        else if (CurrentTest.Runs.Count >= 3)
        {
            var autoFlood = _analysis.DetectFlooding(CurrentTest.Runs, geometry);
            if (autoFlood is not null)
            {
                CurrentTest.Flooding = autoFlood;
                _store.SaveFlooding(CurrentTest.FolderName, autoFlood);
            }
        }

        _store.UpdateResultsSummary(CurrentTest.FolderName, CurrentTest);
        _store.SaveTestManifest(CurrentTest);
        RebuildResults();
    }

    // =========================================================================
    // 8.1 Calibração de torque (1 ponto)
    // =========================================================================

    [RelayCommand]
    private void CalibrationInfo()
    {
        IsCalibrationAssistantOpen = !IsCalibrationAssistantOpen;
        if (IsCalibrationAssistantOpen)
        {
            IsTareAssistantOpen = false;
            IsSinglePointPanelOpen = false;
            IsEnergyCorrelationOpen = false;
            if (CurrentTorquePercent is { } livePct && Math.Abs(livePct) > 0.05)
            {
                CalibrationMeasuredTorquePercent = Math.Round(Math.Abs(livePct), 2);
            }
        }
    }

    [RelayCommand]
    private void CloseCalibrationAssistant() => IsCalibrationAssistantOpen = false;

    [RelayCommand]
    private void ApplyCalibration()
    {
        if (CurrentTest is null)
        {
            ShowError("Crie ou abra um ensaio para registrar a calibração.");
            return;
        }
        if (CalibrationMassKg <= 0 || CalibrationLeverArmM <= 0 || CalibrationMeasuredTorquePercent <= 0)
        {
            ShowError("Massa, braço e torque medido devem ser valores positivos.");
            return;
        }

        try
        {
            var calib = PowerCalc.ComputeStaticTorqueCalibration(
                CalibrationMassKg,
                CalibrationLeverArmM,
                CalibrationMeasuredTorquePercent,
                CalibrationRatedTorqueNm);

            CurrentTest.Calibration = calib;
            _store.SaveCalibration(CurrentTest.FolderName, calib);
            _store.SaveTestManifest(CurrentTest);

            IsCalibrationAssistantOpen = false;
            ValidationMessage = $"Calibração gravada: Escala = {calib.Scale:F4} (τ_ref = {calib.ReferenceNm:F4} N·m).";
            OnPropertyChanged(nameof(CalibrationStatus));
            OnPropertyChanged(nameof(ResultModeLabel));
        }
        catch (Exception ex)
        {
            ShowError($"Erro ao calibrar torque: {ex.Message}");
        }
    }

    // =========================================================================
    // 8.2 Tara P_vazio(N) + σ_τ (varredura no ar)
    // =========================================================================

    [RelayCommand]
    private void TareMeasurementInfo()
    {
        IsTareAssistantOpen = !IsTareAssistantOpen;
        if (IsTareAssistantOpen)
        {
            IsCalibrationAssistantOpen = false;
            IsSinglePointPanelOpen = false;
            IsEnergyCorrelationOpen = false;
            TareProgressMessage = CurrentTest?.Tare is null
                ? "Monte os impelidores no eixo e opere com o vaso no ar (seco)."
                : $"Tara atual possui {CurrentTest.Tare.Points.Count} patamares ({TareStatus}).";
        }
    }

    [RelayCommand]
    private void CloseTareAssistant() => IsTareAssistantOpen = false;

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

        if (!double.IsFinite(TareStartRpm) || !double.IsFinite(TareEndRpm) || !double.IsFinite(TareStepRpm) ||
            TareStartRpm < settings.MinRpm || TareEndRpm > settings.MaxRpm ||
            TareEndRpm < TareStartRpm || TareStepRpm < settings.MinStepRpm)
        {
            ShowError($"Defina a tara entre {settings.MinRpm:F0} e {settings.MaxRpm:F0} rpm, com passo mínimo de {settings.MinStepRpm:F0} rpm.");
            return;
        }

        var targets = BuildTareTargets(TareStartRpm, TareEndRpm, TareStepRpm);
        if (targets.Count == 0)
        {
            ShowError("A faixa informada não produziu nenhum patamar de tara.");
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

        try
        {
            _arbiter.Claim(CommandOwner.PowerAssay, [ActuatorId.Agitation], "Varredura de tara no ar");
            if (_arbiter.OwnerOf(ActuatorId.Agitation) != CommandOwner.PowerAssay)
            {
                throw new InvalidOperationException("Não foi possível obter o controle da agitação para medir a tara.");
            }

            EnsureTareDispatch(
                CommandBuilders.ServoPollInterval(settings.CaptureServoPollMs),
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
                _tareCapture = new PowerTareCaptureController(settings, rpm);
                UpdateTareProgressMessage();

                await WaitForTarePointAsync(_tareCapture, settings, cancellation.Token);
                if (_tareCapture.State != TareCaptureState.Converged)
                {
                    throw new InvalidOperationException(
                        _tareCapture.State == TareCaptureState.SpeedTimedOut
                            ? $"A rotação não estabilizou em {rpm:F0} rpm."
                            : $"O patamar de {rpm:F0} rpm não atingiu o IC95 após {settings.MaxTries} tentativa(s).");
                }

                var point = _tareCapture.CreatePoint(CurrentTest);
                points.Add(point);
                rawSamples.AddRange(_tareCapture.Samples);
                CurrentTarePoints.Add(point);
            }

            var tare = new TareCurve
            {
                SchemaVersion = 2,
                Points = points,
                Samples = rawSamples,
                AcquisitionSettings = settings,
                ImpellerSetHash = PowerTestFileContracts.ComputeImpellerSetHash(BuildGeometry()),
                CalibrationHash = PowerTestFileContracts.ComputeTorqueCalibrationHash(
                    CurrentTest.Calibration,
                    CurrentTest.MotorRatedTorqueNm),
                MeasuredUtc = DateTimeOffset.UtcNow,
            };

            CurrentTest.Tare = tare;
            _store.SaveTare(CurrentTest.FolderName, tare);
            _store.SaveTestManifest(CurrentTest);

            TareProgressMessage =
                $"Tara concluída e gravada: {points.Count} patamares, {rawSamples.Count} leituras válidas em {PowerTestFileContracts.TareFileName}.";
            ValidationMessage = TareProgressMessage;
            OnPropertyChanged(nameof(TareStatus));
            OnPropertyChanged(nameof(ResultModeLabel));
        }
        catch (OperationCanceledException)
        {
            RefreshCurrentTarePoints();
            TareProgressMessage = _tareFailureMessage ?? "Tara cancelada. A curva válida anterior foi mantida.";
            ValidationMessage = TareProgressMessage;
        }
        catch (Exception ex)
        {
            RefreshCurrentTarePoints();
            TareProgressMessage = $"Tara não gravada: {ex.Message} A curva válida anterior foi mantida.";
            ShowError(TareProgressMessage);
        }
        finally
        {
            _tareCapture = null;
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
    // 8.3 Ponto único (conferência rápida)
    // =========================================================================

    [RelayCommand]
    private void ToggleSinglePointPanel()
    {
        IsSinglePointPanelOpen = !IsSinglePointPanelOpen;
        if (IsSinglePointPanelOpen)
        {
            IsCalibrationAssistantOpen = false;
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
            _arbiter.Dispatch(CommandOwner.PowerAssay, CommandBuilders.MotorSetpoint((int)SinglePointRpm));
            if (SinglePointFlowLpm > 0)
            {
                _arbiter.Dispatch(CommandOwner.PowerAssay, CommandBuilders.FlowSetpoint(SinglePointFlowLpm, 10.0));
            }
            IsSinglePointActive = true;
            LivePoints.Clear();
            _tareSweepStartedTimestamp = Stopwatch.GetTimestamp();
            ValidationMessage = $"Ponto único em curso: {SinglePointRpm:F0} rpm.";
        }
        catch (Exception ex)
        {
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
            ValidationMessage = "Ponto único encerrado; eixo desocupado.";
        }
        catch (Exception ex)
        {
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
            GasFlowLpm = SinglePointFlowLpm > 0 ? SinglePointFlowLpm : null,
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
            IsCalibrationAssistantOpen = false;
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
        VentStabilizationEnabled = VentStabilizationEnabled,
        SelectedVentValve = SelectedVentValve,
        VentFlowToleranceLpm = VentFlowToleranceLpm,
        VentFlowStableSamples = VentFlowStableSamples,
        VentAgitationRpm = VentAgitationRpm,
        MaxVentStabilizationSeconds = MaxVentStabilizationSeconds,
        ManualEnergyCaptureEnabled = ManualEnergyCaptureEnabled,
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
        CurrentTest.RelativeMode = RelativeMode;
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

        if (!FinitePositive(MinRpm) || !FinitePositive(MaxRpm) || MinRpm < 15 || MaxRpm > 1000 || MinRpm > MaxRpm || StepRpm < 5)
        {
            return "Faixa de rotação inválida: 15–1000 rpm e passo mínimo de 5 rpm.";
        }

        if (RelativeCiPercent < 0 || !double.IsFinite(RelativeCiPercent) || CiFloorSigmaMultiple < 0 || !double.IsFinite(CiFloorSigmaMultiple) || MinimumSamples < 2 || !FinitePositive(MaxCaptureSeconds) || MaxTries < 1 || !FinitePositive(StationarityWindowSeconds) || StationaritySlopeTolerance < 0 || !double.IsFinite(StationaritySlopeTolerance) || StationarityRequiredSamples < 1)
        {
            return "Revise os limites de estacionariedade e parada adaptativa.";
        }

        if (VentStabilizationEnabled)
        {
            if (VentFlowToleranceLpm <= 0 || !double.IsFinite(VentFlowToleranceLpm))
            {
                return "Tolerância de vazão no alívio deve ser positiva.";
            }
            if (VentFlowStableSamples < 1)
            {
                return "Amostras estáveis no alívio deve ser ao menos 1.";
            }
            if (VentAgitationRpm < 0 || VentAgitationRpm > MaxRpm || !double.IsFinite(VentAgitationRpm))
            {
                return $"Rotação no alívio deve estar entre 0 e {MaxRpm:F0} rpm.";
            }
            if (MaxVentStabilizationSeconds <= 0 || !double.IsFinite(MaxVentStabilizationSeconds))
            {
                return "Tempo limite de alívio deve ser positivo.";
            }
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

        if (!RelativeMode && (CurrentTest.Calibration is null || CurrentTest.Tare is null))
        {
            return "O modo absoluto exige calibração de torque e tara compatível.";
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
                UpdateTareProgressMessage();

                var sweepElapsed = TareSweepElapsedSeconds();
                var torqueNm = snapshot.ServoTorquePct / 100.0 * (CurrentTest?.MotorRatedTorqueNm ?? 1.27);
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
            var torqueNm = snapshot.ServoTorquePct / 100.0 * (CurrentTest?.MotorRatedTorqueNm ?? 1.27);
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
            while (LivePoints.Count > 6000)
            {
                LivePoints.RemoveAt(0);
            }
        }

        CurrentFlowLpm = ValidOptional(snapshot.FlowRate);
        RecalculateLiveMetrics();
        RefreshPreflight();
    });

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
            CurrentTorqueNm = CurrentPowerW = CurrentNp = CurrentRe = CurrentFlG = CurrentFr = CurrentFlowVvm = CurrentPowerRatio = null;
            UpdateGasLoopStatus();
            NotifyLiveText();
            return;
        }
        var doc = CurrentTest;
        var tNom = doc?.Calibration?.MotorRatedTorqueNm ?? doc?.MotorRatedTorqueNm ?? 1.27;
        var torqueNm = doc?.Calibration is { } cal ? cal.Scale * (torquePct / 100.0 * cal.MotorRatedTorqueNm) + cal.Offset : torquePct / 100.0 * tNom;
        CurrentTorqueNm = torqueNm;
        CurrentPowerW = PowerCalc.ShaftPower(torqueNm, rpm);
        CurrentNp = CurrentRe = CurrentFlG = CurrentFr = CurrentFlowVvm = CurrentPowerRatio = null;
        var reference = Impellers.OrderByDescending(i => i.DiameterM).FirstOrDefault();
        if (doc is not null && reference is not null && reference.DiameterM > 0 && rpm > 0 && doc.Fluid.DensityKgM3 > 0 && doc.Fluid.ViscosityPaS > 0)
        {
            var netPower = CurrentPowerW.Value - (doc.Tare is null ? 0 : TareInterpolator.InterpolatePowerW(doc.Tare, rpm));
            CurrentNp = PowerCalc.PowerNumber(netPower, doc.Fluid.DensityKgM3, rpm, reference.DiameterM);
            CurrentRe = PowerCalc.ReynoldsNumber(doc.Fluid.DensityKgM3, rpm, reference.DiameterM, doc.Fluid.ViscosityPaS);
            CurrentFr = PowerCalc.FroudeNumber(rpm, reference.DiameterM);
            if (CurrentFlowLpm is { } flow)
            {
                CurrentFlG = PowerCalc.AerationNumber(flow, rpm, reference.DiameterM);
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

    private void UpdateGasLoopStatus()
    {
        if (_runner?.Phase == PowerRunPhase.VentStabilizing)
        {
            GasLoopStatusBadge = "Alívio Estabilizando";
        }
        else if (_runner is not null && _runner.IsRunning &&
                 (_runner.Phase is PowerRunPhase.PreparingCondition or PowerRunPhase.SettingSpeed or PowerRunPhase.SettlingTorque or PowerRunPhase.AccumulatingToTarget or PowerRunPhase.HoldingForManualEnergy) &&
                 _runner.CurrentRun?.GasMode == PowerGasMode.Gassed)
        {
            GasLoopStatusBadge = "Reator Aberto";
        }
        else if (_latestSnapshot is { } s && (s.FlowValve1 == 1 || s.FlowValve2 == 1))
        {
            GasLoopStatusBadge = "Reator Aberto";
        }
        else if (_latestSnapshot is { } s2 && s2.FlowValveMain == 1)
        {
            GasLoopStatusBadge = "Alívio Estabilizando";
        }
        else
        {
            GasLoopStatusBadge = "Fechado";
        }
        OnPropertyChanged(nameof(GasLoopStatusBadge));
    }

    private void OnRunnerStateChanged() => RunOnUi(UpdateRunnerState);

    private void UpdateRunnerState()
    {
        if (_runner is null)
        {
            return;
        }

        if (_runner.CurrentTest is { } runnerDoc)
        {
            CurrentTest = runnerDoc;
        }

        IsRunning = _runner.IsRunning;
        IsInReview = _runner.IsInReview;
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
        UpdateSequenceProgress();
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
        UpdateGasLoopStatus();
        NotifyDocumentState();
    }

    private void OnDataPointAdded(PowerDataPoint point) => RunOnUi(() =>
    {
        LivePoints.Add(point);
        while (LivePoints.Count > 6000)
        {
            LivePoints.RemoveAt(0);
        }

        UpdateRunnerState();
    });

    private void RebuildResults()
    {
        Results.Clear();
        if (CurrentTest is null)
        {
            FloodingResult = null;
            HasFloodingPoint = false;
            FloodingCoordinates = "";
            FloodingDeviationText = "";
            FloodingSummary = "Nenhum ensaio carregado.";
            return;
        }

        foreach (var run in CurrentTest.Runs.OrderBy(r => r.StartedUtc))
        {
            Results.Add(PowerResultRow.From(run));
        }

        var flooding = CurrentTest.Flooding;
        if (flooding is null && CurrentTest.Runs.Count >= 3)
        {
            flooding = _analysis.DetectFlooding(CurrentTest.Runs, CurrentTest.Geometry);
            if (flooding is not null)
            {
                CurrentTest.Flooding = flooding;
                _store.SaveFlooding(CurrentTest.FolderName, flooding);
                _store.SaveTestManifest(CurrentTest);
            }
        }

        FloodingResult = flooding;
        HasFloodingPoint = flooding is not null;
        if (flooding is not null)
        {
            var methodLabel = flooding.Method == FloodingDetectionMethod.ManualAdjusted ? "Manual" : "Automático";
            FloodingCoordinates = $"Fl_G,F = {flooding.ExperimentalFlG:G4} · (PG/P0)_F @ {flooding.ExperimentalRpm:F0} rpm ({flooding.ExperimentalFlowLpm:F2} L/min)";
            var devSign = flooding.RelativeDeviationPercent >= 0 ? "+" : "";
            FloodingDeviationText = $"Nienow teórico: Fl_G = {flooding.TheoreticalFlGNienow:G4} ({devSign}{flooding.RelativeDeviationPercent:F1}%) · Método: {methodLabel}";
            FloodingSummary = $"{FloodingCoordinates}\n{FloodingDeviationText}";
        }
        else
        {
            FloodingCoordinates = "";
            FloodingDeviationText = "";
            FloodingSummary = "Flooding não identificado (necessário varredura com ≥ 3 patamares de gás).";
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

    private void RefreshConditionRows()
    {
        if (CurrentTest is null)
        {
            return;
        }

        var selectedId = SelectedCondition?.ConditionId;
        Conditions.Clear();
        foreach (var condition in CurrentTest.Conditions.OrderBy(c => c.OrderIndex))
        {
            Conditions.Add(condition.Clone());
        }

        SelectedCondition = Conditions.FirstOrDefault(c => c.ConditionId == selectedId) ?? Conditions.FirstOrDefault();
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
        OnPropertyChanged(nameof(CurrentNpText)); OnPropertyChanged(nameof(CurrentReText)); OnPropertyChanged(nameof(CurrentFlGText)); OnPropertyChanged(nameof(CurrentFrText));
        OnPropertyChanged(nameof(CurrentPowerRatioText)); OnPropertyChanged(nameof(GasLoopStatusBadge));
    }

    private void NotifyGeometryState()
    {
        OnPropertyChanged(nameof(ImpellerSetHash)); OnPropertyChanged(nameof(TareStatus)); OnPropertyChanged(nameof(VortexWarning));
    }

    private void NotifyDocumentState()
    {
        OnPropertyChanged(nameof(HasActiveTest)); OnPropertyChanged(nameof(CanEditPlan)); OnPropertyChanged(nameof(CanStartOrContinue)); OnPropertyChanged(nameof(CanManageTest));
        OnPropertyChanged(nameof(CanPause)); OnPropertyChanged(nameof(CanStop)); OnPropertyChanged(nameof(CanSkipCurrent));
        OnPropertyChanged(nameof(PauseButtonLabel)); OnPropertyChanged(nameof(TestStatusLabel)); OnPropertyChanged(nameof(CalibrationStatus));
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
        _ => phase.ToString(),
    };

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
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
        if (_runner is not null) { _runner.StateChanged -= OnRunnerStateChanged; _runner.DataPointAdded -= OnDataPointAdded; }
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

    // Gas & Flooding columns (§4.5, §11, §16)
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

        return new PowerResultRow
        {
            RunId = run.RunId,
            Rpm = F(run.MeanRpmMeasured, "F1"),
            NetTorque = F(run.NetPowerW is { } netPower && run.MeanRpmMeasured != 0
                ? netPower / PowerCalc.AngularVelocity(run.MeanRpmMeasured)
                : null, "F5"),
            Power = F(run.NetPowerW, "F4"),
            Np = F(analysis?.AssemblyPowerNumber, "G5"),
            Re = F(analysis?.AssemblyReynoldsNumber, "G5"),
            Ci = F(analysis?.AssemblyPowerNumberCi95, "G4"),
            StopReason = run.StopReason.ToString(),
            Attempts = run.Tries.ToString(CultureInfo.CurrentCulture),
            Timestamp = (run.CompletedUtc ?? run.StartedUtc).ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.CurrentCulture),
            Status = run.Phase == PowerRunPhase.Accepted ? "Aceito" : run.Phase == PowerRunPhase.Rejected ? "Rejeitado" : run.Phase.ToString(),

            GasFlowLpm = F(run.GasFlowLpm, "F2"),
            FlG = F(run.GasFlowNumber, "G4"),
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
        };
    }
}

public sealed record EnumChoice<T>(T Value, string Label) where T : struct, Enum;

public enum PowerSweepType
{
    VariableNConstantQg,
    VariableQgConstantN,
    MatrixNByQg,
}
