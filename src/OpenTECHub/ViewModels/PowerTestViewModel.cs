using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
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
    private long _lastPreflightTick;
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
        PowerCondition.LiquidVolumeLProvider = () => LiquidVolumeL;
        PowerCondition.OnVvmValidationFailed = msg => ValidationMessage = msg;
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
        Impellers.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
            {
                foreach (Impeller imp in e.NewItems) imp.PropertyChanged += OnImpellerPropertyChanged;
            }
            if (e.OldItems is not null)
            {
                foreach (Impeller imp in e.OldItems) imp.PropertyChanged -= OnImpellerPropertyChanged;
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

    private void OnImpellerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
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
    [ObservableProperty] public partial double SweepStartRpm { get; set; } = 50.0;
    [ObservableProperty] public partial double SweepEndRpm { get; set; } = 1000.0;
    [ObservableProperty] public partial double SweepStepRpm { get; set; } = 50.0;
    [ObservableProperty] public partial double SweepConstantRpm { get; set; } = 300.0;
    [ObservableProperty] public partial double SweepStartQgLpm { get; set; } = 2.0;
    [ObservableProperty] public partial double SweepEndQgLpm { get; set; } = 20.0;
    [ObservableProperty] public partial double SweepStepQgLpm { get; set; } = 2.0;
    [ObservableProperty] public partial double SweepConstantQgLpm { get; set; } = 0.0;
    [ObservableProperty] public partial PowerGasMode SweepGasMode { get; set; } = PowerGasMode.Gassed;
    [ObservableProperty] public partial FlowInputUnit SweepFlowUnit { get; set; } = FlowInputUnit.Lpm;

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

    partial void OnSelectedSweepTypeChanged(PowerSweepType value)
    {
        OnPropertyChanged(nameof(IsSweepTypeNVariable));
        OnPropertyChanged(nameof(IsSweepTypeQgVariable));
        OnPropertyChanged(nameof(IsSweepTypeNConstant));
        OnPropertyChanged(nameof(IsSweepTypeQgConstant));
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
    public bool CanEditPlan => CurrentTest is not null && !IsRunning && CurrentTest.Status != PowerTestStatus.Completed;
    public bool CanStartOrContinue => CurrentTest is not null && !IsRunning && !IsInReview && CurrentTest.Status != PowerTestStatus.Completed;

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
        PowerTestStatus.Interrupted => "Interrompido",
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
            return string.Equals(CurrentTest.Tare.ImpellerSetHash, currentHash, StringComparison.OrdinalIgnoreCase)
                ? "Tara compatível"
                : "Tara de outro conjunto";
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
        if (IsRunning || _dialogs is null || !_dialogs.PromptInput("Novo ensaio de potência", "Nome da nova pasta de ensaio:", out var name))
        {
            return;
        }

        if (!_store.ValidateTestName(name, out var error)) { ShowError(error ?? "Nome inválido."); return; }
        if (_store.TestExists(name)) { ShowError($"Já existe um ensaio chamado '{name.Trim()}'."); return; }

        var impeller = PowerImpellerCatalog.Create(ImpellerType.RushtonFlatBlade);
        impeller.DiameterM = 0.065;
        impeller.ClearanceM = 0.065;
        impeller.StageIndex = 0;
        var doc = _store.CreateTest(
            name.Trim(),
            new FluidProperties { DensityKgM3 = 997.0, ViscosityPaS = 0.00089, TemperatureC = 25, PresetName = "Água 25 °C" },
            new PowerGeometry { VesselDiameterM = 0.190, LiquidVolumeM3 = 0.010, Baffled = true, Impellers = [impeller] },
            new PowerTestSettings(),
            [new PowerCondition { AgitationRpm = 300, OrderIndex = 0 }]);
        LoadDocument(doc);
        RefreshTests();
        SelectedTest = Tests.FirstOrDefault(t => t.FolderName == doc.FolderName);
    }

    [RelayCommand]
    private void LoadSelectedTest()
    {
        if (SelectedTest is null || IsRunning)
        {
            return;
        }

        var doc = _store.LoadTest(SelectedTest.FolderName);
        if (doc is null) { ShowError("O ensaio selecionado não pôde ser aberto."); return; }
        LoadDocument(doc);
        RefreshTests();
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
        }
        finally
        {
            _isLoadingTest = false;
        }
    }

    [RelayCommand]
    private void ApplyWaterPreset(string? preset)
    {
        if (preset == "20") { DensityKgM3 = 998.2; ViscosityPaS = 0.001002; TemperatureC = 20; }
        else { DensityKgM3 = 997.0; ViscosityPaS = 0.00089; TemperatureC = 25; }
        ValidationMessage = "Propriedades de água preenchidas; confirme a temperatura medida.";
    }

    [RelayCommand]
    private void SaveSetup()
    {
        if (CurrentTest is null || IsRunning)
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
        NotifyDocumentState();
    }

    [RelayCommand] private void MoveConditionUp() => MoveItem(Conditions, SelectedCondition, -1, NormalizeConditionOrder);
    [RelayCommand] private void MoveConditionDown() => MoveItem(Conditions, SelectedCondition, 1, NormalizeConditionOrder);

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
                : null;
        }
        catch (Exception ex)
        {
            ValidationMessage = $"Falha ao ler os mapas de kLa: {ex.Message}";
        }

        OnPropertyChanged(nameof(CanImportFromKlaMap));
    }

    /// <summary>
    /// Fills the condition table with the operating points a kLa map was measured at, so the power
    /// assay lands on exactly the same (N, Qg) coordinates and the two datasets can be paired later.
    /// </summary>
    [RelayCommand]
    private async Task ImportConditionsFromKlaMapAsync()
    {
        if (!CanEditPlan)
        {
            ValidationMessage = "A tabela de condições só pode ser editada com o ensaio parado.";
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

            var replicates = Math.Clamp(ImportReplicates, 1, 10);
            var (_, mapName, imported) = PowerMapImportHelper.ImportConditionsFromKlaMap(klaDocument, replicates);

            if (imported.Count == 0)
            {
                ValidationMessage = $"O mapa '{mapName}' não tem âncoras para importar.";
                return;
            }

            if (Conditions.Count > 0 && _dialogs?.Confirm(
                    "Substituir tabela",
                    $"A importação de '{mapName}' substituirá as {Conditions.Count} condição(ões) atuais. Continuar?",
                    "Substituir",
                    "Cancelar") == false)
            {
                return;
            }

            Conditions.Clear();

            var order = 0;
            if (ImportUngassedReferences)
            {
                foreach (var rpm in imported
                             .Where(c => (c.GasFlowLpm ?? 0) > 0)
                             .Select(c => c.AgitationRpm)
                             .Distinct()
                             .OrderBy(rpm => rpm))
                {
                    Conditions.Add(new PowerCondition
                    {
                        OrderIndex = order++,
                        AgitationRpm = rpm,
                        GasMode = PowerGasMode.Ungassed,
                        RequestedReplicates = replicates,
                        Origin = PowerConditionOrigin.Map,
                        SourceMapId = SelectedKlaMapForImport.Id,
                        SourceMapName = mapName,
                        Status = PowerConditionStatus.Pending,
                    });
                }
            }

            foreach (var condition in imported)
            {
                condition.OrderIndex = order++;
                Conditions.Add(condition);
            }

            NormalizeConditionOrder();
            SelectedCondition = Conditions.FirstOrDefault();

            var referenceCount = Conditions.Count - imported.Count;
            ValidationMessage = referenceCount > 0
                ? $"Importadas {imported.Count} condição(ões) de '{mapName}', mais {referenceCount} referência(s) " +
                  "não gaseificada(s) para o cálculo de P_G/P₀. Salve o setup para gravar o plano."
                : $"Importadas {imported.Count} condição(ões) de '{mapName}'. Salve o setup para gravar o plano.";
        }
        catch (Exception ex)
        {
            ValidationMessage = $"Falha ao importar do mapa de kLa: {ex.Message}";
        }
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
                if (!double.IsFinite(SweepStartRpm) || !double.IsFinite(SweepEndRpm) || !double.IsFinite(SweepStepRpm) ||
                    SweepStartRpm < 15 || SweepEndRpm > 1000 || SweepEndRpm < SweepStartRpm || SweepStepRpm < 5)
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
                var mode = SweepConstantQgLpm > 0 ? SweepGasMode : PowerGasMode.Ungassed;
                if (mode == PowerGasMode.Ungassed && SweepConstantQgLpm > 0)
                {
                    mode = PowerGasMode.Gassed;
                }

                for (var rpm = SweepStartRpm; rpm <= SweepEndRpm + 1e-9; rpm += SweepStepRpm)
                {
                    var cond = new PowerCondition
                    {
                        AgitationRpm = rpm,
                        GasFlowLpm = SweepConstantQgLpm > 0 ? SweepConstantQgLpm : null,
                        GasFlowVvm = SweepConstantQgLpm > 0 && LiquidVolumeL > 0 ? Math.Round(SweepConstantQgLpm / LiquidVolumeL, 4) : null,
                        GasMode = mode,
                        FlowUnit = FlowInputUnit.Lpm,
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
                for (var qg = SweepStartQgLpm; qg <= SweepEndQgLpm + 1e-9; qg += SweepStepQgLpm)
                {
                    var cond = new PowerCondition
                    {
                        AgitationRpm = SweepConstantRpm,
                        GasFlowLpm = qg > 0 ? qg : null,
                        GasFlowVvm = qg > 0 && LiquidVolumeL > 0 ? Math.Round(qg / LiquidVolumeL, 4) : null,
                        GasMode = qg > 0 ? SweepGasMode : PowerGasMode.Ungassed,
                        FlowUnit = FlowInputUnit.Lpm,
                        OrderIndex = index++,
                        Origin = PowerConditionOrigin.Manual,
                    };
                    Conditions.Add(cond);
                }
                break;
            }

            case PowerSweepType.MatrixNByQg:
            {
                if (!double.IsFinite(SweepStartRpm) || !double.IsFinite(SweepEndRpm) || !double.IsFinite(SweepStepRpm) ||
                    SweepStartRpm < 15 || SweepEndRpm > 1000 || SweepEndRpm < SweepStartRpm || SweepStepRpm < 5)
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
                for (var rpm = SweepStartRpm; rpm <= SweepEndRpm + 1e-9; rpm += SweepStepRpm)
                {
                    for (var qg = SweepStartQgLpm; qg <= SweepEndQgLpm + 1e-9; qg += SweepStepQgLpm)
                    {
                        var cond = new PowerCondition
                        {
                            AgitationRpm = rpm,
                            GasFlowLpm = qg > 0 ? qg : null,
                            GasFlowVvm = qg > 0 && LiquidVolumeL > 0 ? Math.Round(qg / LiquidVolumeL, 4) : null,
                            GasMode = qg > 0 ? SweepGasMode : PowerGasMode.Ungassed,
                            FlowUnit = FlowInputUnit.Lpm,
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
        ValidationMessage = $"{Conditions.Count} condições geradas.";
        NotifyDocumentState();
    }

    [RelayCommand]
    private void ToggleSkipCondition()
    {
        if (CurrentTest is null || SelectedCondition is null || IsRunning)
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
        if (_runner is null || CurrentTest is null)
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

    [RelayCommand] private async Task StopAndReviewAsync() { if (_runner is not null)
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

    [RelayCommand] private async Task AcceptRunAsync() { if (_runner is not null)
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

    [RelayCommand] private async Task RepeatRunAsync() { if (_runner is not null)
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

    [RelayCommand] private async Task CompleteTestAsync() { if (_runner is not null)
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
        if (CurrentTest is null) return;
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
        if (CurrentTest is null || SelectedResultRow is null) return;
        var run = CurrentTest.Runs.FirstOrDefault(r => r.RunId == SelectedResultRow.RunId);
        if (run is null) return;

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
        if (_isLoadingTest || CurrentTest is null) return;
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
        if (CurrentTest is null) return;

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
            if (run.GasMode == PowerGasMode.Ungassed) continue;

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
        if (TareStartRpm < 15 || TareEndRpm < TareStartRpm || TareStepRpm <= 0)
        {
            ShowError("Defina uma faixa de rotação válida (mínimo 15 rpm).");
            return;
        }

        IsTareRunning = true;
        CurrentTarePoints.Clear();
        var points = new List<TarePoint>();
        var tNom = CurrentTest.Calibration?.MotorRatedTorqueNm ?? CurrentTest.MotorRatedTorqueNm;

        try
        {
            _arbiter.Claim(CommandOwner.PowerAssay, [ActuatorId.Agitation], "Varredura de tara no ar");

            for (var rpm = TareStartRpm; rpm <= TareEndRpm; rpm += TareStepRpm)
            {
                TareProgressMessage = $"Medindo patamar N = {rpm:F0} rpm no ar...";
                _arbiter.Dispatch(CommandOwner.PowerAssay, CommandBuilders.MotorSetpoint((int)rpm));

                var samples = new List<double>();
                for (var s = 0; s < 5; s++)
                {
                    await Task.Delay(100);
                    var torquePct = CurrentTorquePercent ?? 0.8;
                    samples.Add(torquePct);
                }

                var meanPct = samples.Average();
                var sigmaPct = samples.Count > 1
                    ? Math.Max(0.05, Math.Sqrt(samples.Select(x => (x - meanPct) * (x - meanPct)).Sum() / (samples.Count - 1)))
                    : 0.05;

                var rawTorqueNm = (meanPct / 100.0) * tNom;
                var pVoidW = rawTorqueNm * PowerCalc.AngularVelocity(rpm);
                var pt = new TarePoint(rpm, Math.Max(0.0, pVoidW), Math.Round(sigmaPct, 4));
                points.Add(pt);
                CurrentTarePoints.Add(pt);
            }

            SafeParkAndReleaseAgitation("Tara concluída");

            var tare = new TareCurve
            {
                Points = points,
                ImpellerSetHash = PowerTestFileContracts.ComputeImpellerSetHash(BuildGeometry()),
                MeasuredUtc = DateTimeOffset.UtcNow,
            };

            CurrentTest.Tare = tare;
            _store.SaveTare(CurrentTest.FolderName, tare);
            _store.SaveTestManifest(CurrentTest);

            TareProgressMessage = $"Tara gravada com {points.Count} patamares em {PowerTestFileContracts.TareFileName}.";
            ValidationMessage = TareProgressMessage;
            OnPropertyChanged(nameof(TareStatus));
            OnPropertyChanged(nameof(ResultModeLabel));
        }
        catch (Exception ex)
        {
            SafeParkAndReleaseAgitation("Falha na varredura de tara");
            ShowError($"Erro na varredura de tara: {ex.Message}");
        }
        finally
        {
            IsTareRunning = false;
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

    private void SafeParkAndReleaseAgitation(string reason)
    {
        try
        {
            if (_arbiter.OwnerOf(ActuatorId.Agitation) == CommandOwner.PowerAssay)
            {
                _arbiter.Dispatch(CommandOwner.PowerAssay, CommandBuilders.MotorSetpoint(15));
                _arbiter.Dispatch(CommandOwner.PowerAssay, CommandBuilders.MotorSetpoint(0));
                _arbiter.Release(CommandOwner.PowerAssay, reason);
            }
        }
        catch
        {
            // best-effort
        }
    }

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
        CurrentTest.Settings = CurrentTest.Settings with
        {
            MinRpm = MinRpm, MaxRpm = MaxRpm, DefaultStepRpm = StepRpm,
            RelativeCiFraction = RelativeCiPercent / 100.0, CiFloorSigmaMultiple = CiFloorSigmaMultiple,
            MinSamples = MinimumSamples, MaxCaptureSeconds = MaxCaptureSeconds, MaxTries = MaxTries,
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
        CurrentFlowLpm = ValidOptional(snapshot.FlowRate);
        RecalculateLiveMetrics();
        RefreshPreflight();
    });

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

        RefreshConditionRows();
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
        CommandOwner.Manual => "Operador", CommandOwner.Automatic => "Cascata", CommandOwner.Recipe => "Receita",
        CommandOwner.KlaAssay => "Ensaio kLa", CommandOwner.PowerAssay => "Ensaio de potência", var owner => owner.ToString(),
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
        OnPropertyChanged(nameof(HasActiveTest)); OnPropertyChanged(nameof(CanEditPlan)); OnPropertyChanged(nameof(CanStartOrContinue));
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

    private void NormalizeImpellerOrder() { for (var i = 0; i < Impellers.Count; i++)
        {
            Impellers[i].StageIndex = i;
        }
    }
    private void NormalizeConditionOrder() { for (var i = 0; i < Conditions.Count; i++)
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
        PowerRunPhase.Idle => "Pronto", PowerRunPhase.Preflight => "Pré-voo", PowerRunPhase.PreparingCondition => "Preparando",
        PowerRunPhase.SettingSpeed => "Ajustando rotação", PowerRunPhase.SettlingTorque => "Porta 1 · estacionariedade",
        PowerRunPhase.AccumulatingToTarget => "Porta 2 · IC95", PowerRunPhase.PausedByOperator => "Pausado pelo operador",
        PowerRunPhase.PausedForMeasurement => "Pausado · sem medida", PowerRunPhase.HoldingForManualEnergy => "Aguardando wattímetro",
        PowerRunPhase.Reviewing => "Revisão", PowerRunPhase.Accepted => "Aceito", PowerRunPhase.Rejected => "Rejeitado",
        PowerRunPhase.Completed => "Concluído", PowerRunPhase.Faulted => "Interrompido", _ => phase.ToString(),
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
