using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Dialogs;
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
    private bool _disposed;

    public PowerTestViewModel(IPowerTestStore store, IDeviceService device, ICommandArbiter arbiter)
        : this(store, device, arbiter, null, null)
    {
    }

    public PowerTestViewModel(
        IPowerTestStore store,
        IDeviceService device,
        ICommandArbiter arbiter,
        IPowerTestRunner? runner,
        IDialogService? dialogs)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(arbiter);
        _store = store;
        _device = device;
        _arbiter = arbiter;
        _runner = runner;
        _dialogs = dialogs;
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
        if (_device.Latest is { } latest)
        {
            OnTelemetryReceived(latest);
        }

        UpdateRunnerState();
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
        new(PowerGasMode.Ungassed, "Sem gás (P0)"),
        new(PowerGasMode.Gassed, "Com gás (Fase 2)"),
        new(PowerGasMode.Both, "P0 + Pg"),
        new(PowerGasMode.SinglePoint, "Ponto único"),
    ];
    public IReadOnlyList<EnumChoice<FlowInputUnit>> FlowUnits { get; } =
    [
        new(FlowInputUnit.Lpm, "L/min"),
        new(FlowInputUnit.Vvm, "vvm"),
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
    [ObservableProperty] public partial bool ManualEnergyCaptureEnabled { get; set; }
    [ObservableProperty] public partial double SweepStartRpm { get; set; } = 50.0;
    [ObservableProperty] public partial double SweepEndRpm { get; set; } = 1000.0;
    [ObservableProperty] public partial double SweepStepRpm { get; set; } = 50.0;

    public bool HasActiveTest => CurrentTest is not null;
    public bool CanEditPlan => CurrentTest is not null && !IsRunning && CurrentTest.Status != PowerTestStatus.Completed;
    public bool CanStartOrContinue => CurrentTest is not null && !IsRunning && !IsInReview && CurrentTest.Status != PowerTestStatus.Completed;
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
        ManualEnergyCaptureEnabled = doc.Settings.ManualEnergyCaptureEnabled;

        Impellers.Clear();
        foreach (var impeller in doc.Geometry.Impellers.OrderBy(i => i.StageIndex))
        {
            Impellers.Add(impeller.Clone());
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
        NotifyDocumentState();
        RecalculateLiveMetrics();
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

    [RelayCommand]
    private void GenerateSweep()
    {
        if (!CanEditPlan)
        {
            return;
        }

        if (!double.IsFinite(SweepStartRpm) || !double.IsFinite(SweepEndRpm) || !double.IsFinite(SweepStepRpm) || SweepStartRpm < 15 || SweepEndRpm > 1000 || SweepEndRpm < SweepStartRpm || SweepStepRpm < 5)
        {
            ValidationMessage = "Varredura inválida: use 15–1000 rpm e passo mínimo de 5 rpm.";
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
            Conditions.Add(new PowerCondition { AgitationRpm = rpm, OrderIndex = index++, Origin = PowerConditionOrigin.Manual });
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

    [RelayCommand] private void TareMeasurementInfo() => ValidationMessage = "A aquisição guiada da tara será implementada na etapa 8; a compatibilidade do conjunto já é verificada aqui.";
    [RelayCommand] private void CalibrationInfo() => ValidationMessage = "A aquisição guiada da calibração de torque será implementada na etapa 8.";

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
        HasServoSample = snapshot.HasServoSample;
        CurrentRpm = snapshot.HasServoSample && double.IsFinite(snapshot.ServoRpm) ? snapshot.ServoRpm : null;
        CurrentTorquePercent = snapshot.HasServoSample && double.IsFinite(snapshot.ServoTorquePct) ? snapshot.ServoTorquePct : null;
        CurrentFlowLpm = ValidOptional(snapshot.FlowRate);
        RecalculateLiveMetrics();
    });

    private void RecalculateLiveMetrics()
    {
        if (!HasServoSample || CurrentRpm is not { } rpm || CurrentTorquePercent is not { } torquePct)
        {
            CurrentTorqueNm = CurrentPowerW = CurrentNp = CurrentRe = CurrentFlG = CurrentFr = null;
            NotifyLiveText();
            return;
        }
        var doc = CurrentTest;
        var tNom = doc?.Calibration?.MotorRatedTorqueNm ?? doc?.MotorRatedTorqueNm ?? 1.27;
        var torqueNm = doc?.Calibration is { } cal ? cal.Scale * (torquePct / 100.0 * cal.MotorRatedTorqueNm) + cal.Offset : torquePct / 100.0 * tNom;
        CurrentTorqueNm = torqueNm;
        CurrentPowerW = PowerCalc.ShaftPower(torqueNm, rpm);
        CurrentNp = CurrentRe = CurrentFlG = CurrentFr = null;
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
            }
        }
        NotifyLiveText();
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
            return;
        }

        foreach (var run in CurrentTest.Runs.OrderBy(r => r.StartedUtc))
        {
            Results.Add(PowerResultRow.From(run));
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

    private void OnOwnershipChanged(OwnershipTransfer _) => RunOnUi(RefreshOwnership);
    private void RefreshOwnership() => AgitationOwnerLabel = _arbiter.OwnerOf(ActuatorId.Agitation) switch
    {
        CommandOwner.Manual => "Operador", CommandOwner.Automatic => "Cascata", CommandOwner.Recipe => "Receita",
        CommandOwner.KlaAssay => "Ensaio kLa", CommandOwner.PowerAssay => "Ensaio de potência", var owner => owner.ToString(),
    };

    private void NotifyLiveText()
    {
        OnPropertyChanged(nameof(LiveSummary)); OnPropertyChanged(nameof(CurrentRpmText)); OnPropertyChanged(nameof(CurrentTorquePercentText));
        OnPropertyChanged(nameof(CurrentTorqueNmText)); OnPropertyChanged(nameof(CurrentPowerWText)); OnPropertyChanged(nameof(CurrentFlowText));
        OnPropertyChanged(nameof(CurrentNpText)); OnPropertyChanged(nameof(CurrentReText)); OnPropertyChanged(nameof(CurrentFlGText)); OnPropertyChanged(nameof(CurrentFrText));
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
    public double ReynoldsNumber { get; init; }
    public double PowerNumber { get; init; }
    public double PowerNumberCi95 { get; init; }

    public static PowerResultRow From(PowerRunSummary run)
    {
        static string F(double? value, string format) => value is { } v && double.IsFinite(v) ? v.ToString(format, CultureInfo.CurrentCulture) : "—";
        var analysis = run.Analysis;
        return new PowerResultRow
        {
            RunId = run.RunId,
            Rpm = F(run.MeanRpmMeasured, "F1"),
            NetTorque = F(run.NetPowerW is { } netPower && run.MeanRpmMeasured != 0
                ? netPower / PowerCalc.AngularVelocity(run.MeanRpmMeasured)
                : null, "F5"),
            Power = F(run.NetPowerW, "F4"),
            Np = F(analysis?.AssemblyPowerNumber, "G5"), Re = F(analysis?.AssemblyReynoldsNumber, "G5"), Ci = F(analysis?.AssemblyPowerNumberCi95, "G4"),
            StopReason = run.StopReason.ToString(), Attempts = run.Tries.ToString(CultureInfo.CurrentCulture),
            Timestamp = (run.CompletedUtc ?? run.StartedUtc).ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.CurrentCulture),
            Status = run.Phase == PowerRunPhase.Accepted ? "Aceito" : run.Phase == PowerRunPhase.Rejected ? "Rejeitado" : run.Phase.ToString(),
            ReynoldsNumber = analysis?.AssemblyReynoldsNumber ?? double.NaN,
            PowerNumber = analysis?.AssemblyPowerNumber ?? double.NaN,
            PowerNumberCi95 = analysis?.AssemblyPowerNumberCi95 ?? double.NaN,
        };
    }
}

public sealed record EnumChoice<T>(T Value, string Label) where T : struct, Enum;
