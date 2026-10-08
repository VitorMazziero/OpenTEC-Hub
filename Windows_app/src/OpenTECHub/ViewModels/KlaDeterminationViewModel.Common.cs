using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTECHub.Services.KlaTesting;

namespace OpenTECHub.ViewModels;

public sealed record KlaExecutionStep(int Number, string Title, string State);
public sealed record KlaPhaseRange(string Name, string Interval, string Origin);

public sealed partial class KlaDeterminationViewModel
{
    [ObservableProperty] private bool _isBiotic;
    [ObservableProperty] private bool _isSingleCapture;
    [ObservableProperty] private bool _showAllPlots;
    [ObservableProperty] private double _minimumRpm = 50;
    [ObservableProperty] private double _maximumRpm = 1000;
    [ObservableProperty] private double _minimumFlow = 0.5;
    [ObservableProperty] private double _maximumFlow = 16;
    [ObservableProperty] private double _minimumOperatingDo = 30;
    [ObservableProperty] private double _maximumOperatingDo = 100;
    [ObservableProperty] private double _minimumRemovalTarget = 5;
    [ObservableProperty] private double _maximumRemovalTarget = 20;
    [ObservableProperty] private double _oxygenTimeout = 10;
    [ObservableProperty] private double _commandTimeout = 10;
    [ObservableProperty] private double _initialStability = 10;
    [ObservableProperty] private double _recoveryStability = 10;
    [ObservableProperty] private double _returnRpmTolerance = 25;
    [ObservableProperty] private double _returnFlowTolerance = 0.3;
    [ObservableProperty] private double? _returnAgitation;
    [ObservableProperty] private double? _maximumDoDrop;
    [ObservableProperty] private double? _minimumReturnDo;
    [ObservableProperty] private string _probeTechnology = "Polarográfica";
    [ObservableProperty] private string _probeModel = "";
    [ObservableProperty] private double? _probeResponseSeconds;
    [ObservableProperty] private bool _nitrogenIsolationConfirmed;
    [ObservableProperty] private bool _reviewManualWindow;
    [ObservableProperty] private bool _reviewManualOurWindow;
    [ObservableProperty] private double _reviewOurStart;
    [ObservableProperty] private double _reviewOurEnd;
    [ObservableProperty] private bool _reviewManualPhases;
    [ObservableProperty] private double _reviewConsumptionPhaseStart;
    [ObservableProperty] private double _reviewRecoveryPhaseStart;
    [ObservableProperty] private double _reviewSteadyPhaseStart;
    [ObservableProperty] private bool _residualTransferVerified;
    [ObservableProperty] private bool _consumptionRepresentative;
    [ObservableProperty] private bool _probeResponseVerified;
    [ObservableProperty] private double? _physicalSaturation;
    [ObservableProperty] private string _physicalSaturationSource = "";
    [ObservableProperty] private double? _referenceConcentration;
    [ObservableProperty] private string _referenceConcentrationSource = "";
    [ObservableProperty] private string _reviewMessage = "";

    partial void OnReviewManualWindowChanged(bool value) => AutoRecompute();
    partial void OnReviewManualOurWindowChanged(bool value) => AutoRecompute();
    partial void OnReviewManualPhasesChanged(bool value) => AutoRecompute();
    partial void OnReviewOurStartChanged(double value) => AutoRecompute();
    partial void OnReviewOurEndChanged(double value) => AutoRecompute();
    partial void OnResidualTransferVerifiedChanged(bool value) => AutoRecompute();
    partial void OnConsumptionRepresentativeChanged(bool value) => AutoRecompute();
    partial void OnProbeResponseVerifiedChanged(bool value) => AutoRecompute();
    partial void OnPhysicalSaturationChanged(double? value) => AutoRecompute();
    partial void OnPhysicalSaturationSourceChanged(string value) => AutoRecompute();
    partial void OnReferenceConcentrationChanged(double? value) => AutoRecompute();
    partial void OnReferenceConcentrationSourceChanged(string value) => AutoRecompute();
    partial void OnReviewConsumptionPhaseStartChanged(double value) => AutoRecompute();
    partial void OnReviewRecoveryPhaseStartChanged(double value) => AutoRecompute();
    partial void OnReviewSteadyPhaseStartChanged(double value) => AutoRecompute();

    public ObservableCollection<KlaExecutionStep> ExecutionSteps { get; } = [];
    public ObservableCollection<KlaPhaseRange> ReviewPhases { get; } = [];
    public bool IsAbiotic { get => !IsBiotic; set { if (value) IsBiotic = false; } }
    public bool IsMultipleCapture { get => !IsSingleCapture; set { if (value) IsSingleCapture = false; } }
    public bool CanEditPreparation => !IsRunning && !IsReviewOpen && !IsAutomaticSession;
    public bool ShowPreparation => !IsReviewOpen;
    public bool CanEditProtocol => !IsRunning && !IsAutomaticSession;
    public bool CanRunSingle => CanStartSequence && IsSingleCapture && CurrentTest?.Runs.Count == 0;
    public bool CanDecideRun => IsReviewOpen && !IsAutomaticSession && (CurrentTest?.EffectiveProtocol != KlaAssayProtocol.Biotic ||
        (_currentlyEditingRun?.EffectiveOutcome.Restoration ?? CurrentRun?.Outcome?.Restoration) == KlaRestorationState.Confirmed);
    public bool CanAcceptAnalysis => CanDecideRun && CurrentAnalysis is { Quality: not DecisionQuality.Inconclusive };
    public bool CanRepeatLiveRun => CanDecideRun && _runner.IsInReview && _currentlyEditingRun is null;
    public bool IsDeterministicReview => CurrentAnalysis?.DeterministicResult is not null;
    public bool IsLegacyReview => IsReviewOpen && !IsDeterministicReview;
    public bool ShowOur => IsReviewOpen && (CurrentAnalysis?.DeterministicResult?.Input?.Protocol ?? CurrentTest?.EffectiveProtocol) == KlaAssayProtocol.Biotic;
    public string GasConfirmationLabel => IsBiotic ? "Confirmo N₂ fechado e isolado na fonte" : "Confirmo N₂ aberto na fonte";
    public string ProtocolSummary => IsBiotic ? "Consumo respiratório · ar desviado ao escape · fluxômetro ligado" : "Remoção por N₂ · pré-estabilização do ar · recuperação";
    public string PrimaryActionLabel => !HasActiveTest ? "Preparar teste" : IsReviewOpen ? "Salvar revisão" : IsSingleCapture ? "Iniciar teste único" : "Executar matriz";
    public string SavedPath => CurrentTest is null ? "" : Path.Combine(_store.RootDirectory, CurrentTest.FolderName);
    public string RestorationLabel => (CurrentRun?.Outcome ?? _currentlyEditingRun?.EffectiveOutcome)?.Restoration switch
    {
        KlaRestorationState.Pending => "Retomada pendente — aguardando confirmação de ar, agitação e OD",
        KlaRestorationState.Confirmed => "Cultivo retomado — ar, agitação e controle confirmados",
        KlaRestorationState.Failed => "Retomada não confirmada — nova corrida bloqueada",
        KlaRestorationState.NotRequired => "Fechamento da corrida confirmado",
        _ => CurrentTest?.EffectiveProtocol == KlaAssayProtocol.Biotic ? "Retomada ainda não registrada" : "Finalização da corrida abiótica",
    };
    public string NextEvent => Phase switch
    {
        RunPhase.DivertingAir => "Aguardar confirmação do ar no escape",
        RunPhase.MeasuringConsumption => $"Comutar ar para o reator ao atingir alvo de OD ({SettingDOMin:G}%), queda ou prazo configurado",
        RunPhase.Deoxygenating => $"Aguardar OD ≤ {SettingDOMin:G}% e estabilidade",
        RunPhase.PrestagingAir => "Aguardar vazão estável no escape e comutação para A",
        RunPhase.SwitchingToReactor => "Aguardar confirmação de ar entrando no reator",
        RunPhase.Reoxygenating => CurrentTest?.EffectiveProtocol == KlaAssayProtocol.Biotic ? "Aguardar equilíbrio respiratório na faixa de retomada" : $"Aguardar OD final ({SettingDOMax:G}%) ou prazo",
        RunPhase.RestoringCultivation => "Aguardar ar, agitação medida, OD estável e retomada do controlador",
        RunPhase.Reviewing => "Revisar janela, qualidade e decidir sobre a corrida",
        RunPhase.Faulted => _runner.StatusMessage,
        _ => "Preparar condição, parâmetros e confirmação dos gases",
    };
    public string DisplayOur => CurrentAnalysis?.DeterministicResult?.Our.PercentPointsPerHour is { } our ? $"{our:F1} pp/h" : "—";
    public string DisplayOurConcentration => CurrentAnalysis?.DeterministicResult?.Our.MmolPerLPerHour is { } our ? $"{our:F3} mmol/L/h" : "Concentração indisponível sem Cref e origem";
    public string DisplayOurQuality => QualityText(CurrentAnalysis?.DeterministicResult?.Our.Quality ?? KlaScientificQuality.NotEvaluated);
    public string DisplayEquilibrium => CurrentAnalysis?.DeterministicResult is { } r
        ? r.Equilibrium.Percent is { } c ? $"Ceq: {c:F2}%" : "Ceq indisponível"
        : CurrentAnalysis is null ? "Ceq: —" : $"Ceq legado: {ReviewCeq:F2}%";
    public string ReviewDiagnostics => CurrentAnalysis?.DeterministicResult is { } r
        ? string.Join(" · ", r.Reasons.Concat(r.Our.Reasons).Distinct().Select(ExplainReason))
        : "Análise histórica. Recalcular só usa o novo núcleo quando há eventos de gás confirmados.";

    private static string QualityText(KlaScientificQuality quality) => quality switch
    {
        KlaScientificQuality.Valid => "Validado",
        KlaScientificQuality.Conditional => "Condicionado às hipóteses",
        KlaScientificQuality.NotApplicable => "Não aplicável",
        KlaScientificQuality.NotEvaluated => "Não avaliado",
        _ => "Inconclusivo",
    };

    internal static string ExplainReason(string reason) => reason switch
    {
        "qualified_result" => "Resultado atende aos critérios do perfil",
        "recipe_pause" => "Tentativa interrompida pela pausa da receita",
        "acquisition_cancelled" => "Aquisição cancelada",
        "acquisition_failed" => "Falha durante a aquisição",
        "acquisition_faulted" => "Falha durante a aquisição",
        "unknown_probe_response_rate_is_conditional" => "Resposta da sonda não verificada: taxa condicionada",
        "respiratory_transient_probe_response_not_independently_verified" => "Transiente respiratório condicionado à resposta da sonda",
        "constant_process_conditions_not_independently_verified" => "Condições de processo não verificadas",
        "no_qualified_contiguous_window" => "Nenhuma janela contínua passou nos critérios",
        "insufficient_confirmed_recovery" => "Recuperação confirmada insuficiente",
        "apparent_consumption_residual_transfer_not_verified" => "Transferência residual não verificada: consumo aparente",
        "constant_representative_our_not_independently_verified" => "Consumo constante/representativo não verificado",
        "nitrogen_stripping_not_respiration" => "N₂ não mede consumo respiratório",
        "transfer_and_probe_response_not_separately_identifiable" => "Taxa não separável da resposta da sonda",
        "independent_oxygen_balance_inconsistent" => "OUR e balanço de oxigênio incompatíveis",
        "concentration_conversion_unavailable_without_reference" => "Conversão indisponível sem Cref e origem",
        "ols_interval_is_conditional_on_equilibrium_and_correlated_errors" => "Intervalo condicionado a Ceq e correlação dos erros",
        "invalid_or_repeated_oxygen_observation" => "OD inválido ou amostra repetida",
        "oxygen_sample_gap_exceeds_configured_span" => "Lacuna de OD excede o intervalo permitido",
        "time_not_strictly_monotonic" => "Tempos repetidos ou fora de ordem",
        "invalid_or_unconfirmed_gas_event" => "Comutação de gás inválida ou sem confirmação",
        "invalid_protocol_or_insufficient_samples" => "Protocolo inválido ou amostras insuficientes",
        "protocol_removal_mode_mismatch" => "Remoção de oxigênio incompatível com o protocolo",
        "invalid_manual_phase_provenance" => "Fase manual sem origem válida",
        "manual_phase_conflicts_with_confirmed_gas_route" => "Fase manual diverge da rota confirmada",
        "noncontinuous_recovery_episode" => "Reoxigenação interrompida por outra fase",
        "one_recovery_episode_required" => "Selecione um único episódio de reoxigenação",
        "recovery_event_outside_episode" => "Comutação fora do episódio selecionado",
        "our_window_outside_respiration_phase" => "Janela de OUR fora da fase de consumo",
        "physical_saturation_unavailable_balance_not_checked" => "C* físico ausente: balanço independente não verificado",
        "independent_our_and_physical_balance_disagree" => "OUR e balanço independente divergem",
        "invalid_probe_response_time" => "Tempo de resposta da sonda inválido",
        "invalid_physical_saturation" => "Saturação física inválida",
        "invalid_concentration_reference" => "Referência de concentração inválida",
        "invalid_manual_equilibrium" => "Ceq informado incompatível com a curva",
        "operator_supplied_equilibrium" => "Ceq informado pelo operador",
        "insufficient_equilibrium_points" => "Poucos pontos para estimar Ceq",
        "invalid_equilibrium_observation" => "Leitura inválida na estimativa de Ceq",
        "equilibrium_signal_not_identifiable" => "Curva não permite identificar Ceq",
        "equilibrium_fit_low_information" => "Informação insuficiente no ajuste de Ceq",
        "equilibrium_covariance_singular" => "Incerteza de Ceq não identificável",
        "equilibrium_fit_at_configured_boundary" => "Ceq atingiu um limite do ajuste",
        "no_interior_exponential_solution" => "Não foi encontrado ajuste exponencial identificável",
        "insufficient_respiratory_window" => "Poucos pontos na janela de consumo",
        "invalid_or_disturbed_respiratory_window" => "Janela de consumo contém leitura inválida ou perturbação",
        "short_or_noncontinuous_respiratory_window" => "Janela de consumo curta ou descontínua",
        "nonpositive_or_undefined_our" => "Consumo não positivo ou indefinido",
        "our_not_applicable_to_nitrogen_stripping" => "Remoção por N₂ não permite medir OUR",
        "our_slope_uncertain" => "Inclinação do consumo com incerteza excessiva",
        "respiration_curved_or_oxygen_limited" => "Consumo varia ou apresenta limitação por OD",
        "respiratory_fit_r2_below_threshold" => "Ajuste do consumo abaixo do critério de qualidade",
        "respiratory_signal_too_small" => "Variação de OD insuficiente para medir consumo",
        "correlated_respiratory_residuals" => "Erros do ajuste respiratório correlacionados",
        "invalid_or_short_window" => "Janela inválida ou curta",
        "invalid_or_disturbed_observation_in_window" => "Janela contém leitura inválida ou perturbação",
        "log_fit_r2_below_threshold" => "Ajuste linear abaixo do critério de qualidade",
        "insufficient_signal_to_noise" => "Variação insuficiente em relação ao ruído",
        "slope_uncertain" => "Inclinação com incerteza excessiva",
        "correlated_log_residuals" => "Erros do ajuste linear correlacionados",
        "nonconstant_rate_in_subwindows" => "Taxa varia entre trechos da janela",
        "ceq_sensitive_or_invalid_deficit" => "Resultado sensível a Ceq ou força motriz inválida",
        "endpoint_sensitive" => "Resultado sensível às extremidades da janela",
        "operator_phase_timing_invalid" => "Tempos das fases informados inválidos",
        _ => $"Diagnóstico adicional: {reason}",
    };

    partial void OnIsBioticChanged(bool value)
    {
        OnPropertyChanged(nameof(IsAbiotic));
        OnPropertyChanged(nameof(GasConfirmationLabel));
        OnPropertyChanged(nameof(ProtocolSummary));
        if (!_isLoadingSettings && CurrentTest is not null && (CurrentTest.EffectiveProtocol == KlaAssayProtocol.Biotic) != value)
            OpenCreateDialog();
        RefreshCommonState();
    }
    partial void OnIsSingleCaptureChanged(bool value)
    {
        OnPropertyChanged(nameof(IsMultipleCapture));
        if (!_isLoadingSettings && CurrentTest is not null && (CurrentTest.EffectiveCaptureMode == KlaCaptureMode.Single) != value)
            OpenCreateDialog();
        RefreshCommonState();
    }

    private bool TryBuildDefinition(KlaTestSettings settings, IReadOnlyList<KlaTestCondition>? conditions,
        out KlaAssayDefinition definition, out string error)
    {
        var planned = IsSingleCapture
            ? new[] { new KlaTestCondition { AgitationRpm = NewConditionRpm, AirflowLpm = NewConditionFlow, RequestedReplicates = 1 } }
            : conditions?.ToArray() ?? [];
        definition = new()
        {
            Protocol = IsBiotic ? KlaAssayProtocol.Biotic : KlaAssayProtocol.Abiotic,
            CaptureMode = IsSingleCapture ? KlaCaptureMode.Single : KlaCaptureMode.Multiple,
            Settings = settings with { AutoAcceptRuns = false },
            ProtocolSettings = BuildProtocolSettings(),
            Context = BuildMeasurementContext(), SequenceLimits = BuildSequenceLimits(),
            Conditions = planned.Select(KlaAssayCondition.From).ToImmutableArray(),
        };
        try { definition.Validate(requireConditions: false); error = ""; return true; }
        catch (ArgumentException ex) { error = ex.Message; return false; }
    }

    private KlaProtocolSettings BuildProtocolSettings() => new()
    {
        OperatingRange = new(MinimumRpm, MaximumRpm, MinimumFlow, MaximumFlow, MinimumOperatingDo, MaximumOperatingDo),
        OxygenRemovalAgitationRpm = SettingDegassingAgitation,
        MinimumRemovalTargetDoPercent = MinimumRemovalTarget, MaximumRemovalTargetDoPercent = MaximumRemovalTarget,
        RemovalTargetDoPercent = SettingDOMin,
        Probe = new() { Technology = ProbeTechnology, Model = ProbeModel, ResponseTimeSeconds = ProbeResponseSeconds },
        AerationReturn = new() { MinimumDoPercent = MinimumReturnDo, MaximumDoDropPoints = MaximumDoDrop,
            MaximumGasOffSeconds = SettingMaxDegassingMinutes * 60, MaximumRecoverySeconds = SettingMaxReoxygenationMinutes * 60,
            MinimumInterAssaySeconds = MinimumInterAssaySeconds },
        OxygenSampleTimeoutSeconds = OxygenTimeout, CommandConfirmationTimeoutSeconds = CommandTimeout,
        InitialStabilitySeconds = InitialStability, RecoveryStabilitySeconds = RecoveryStability,
        ReturnAgitationRpm = ReturnAgitation, ReturnAgitationToleranceRpm = ReturnRpmTolerance, ReturnFlowToleranceLpm = ReturnFlowTolerance,
    };

    private void LoadCommonSettings(KlaTestDocument doc)
    {
        var previous = _isLoadingSettings; _isLoadingSettings = true;
        try
        {
            LoadSequenceSettings(doc);
            IsBiotic = doc.EffectiveProtocol == KlaAssayProtocol.Biotic;
            IsSingleCapture = doc.EffectiveCaptureMode == KlaCaptureMode.Single;
            NitrogenSourceConfirmed = false; NitrogenIsolationConfirmed = false;
            var p = doc.ProtocolSettings ?? new() { OxygenRemovalAgitationRpm = doc.Settings.DegassingAgitationRpm };
            var range = p.OperatingRange ?? KlaOperatingRange.CurrentCultivation;
            MinimumRpm = range.MinimumAgitationRpm; MaximumRpm = range.MaximumAgitationRpm;
            MinimumFlow = range.MinimumAirflowLpm; MaximumFlow = range.MaximumAirflowLpm;
            MinimumOperatingDo = range.MinimumOperatingDoPercent; MaximumOperatingDo = range.MaximumOperatingDoPercent;
            MinimumRemovalTarget = p.MinimumRemovalTargetDoPercent; MaximumRemovalTarget = p.MaximumRemovalTargetDoPercent;
            OxygenTimeout = p.OxygenSampleTimeoutSeconds; CommandTimeout = p.CommandConfirmationTimeoutSeconds;
            InitialStability = p.InitialStabilitySeconds; RecoveryStability = p.RecoveryStabilitySeconds;
            ReturnAgitation = p.ReturnAgitationRpm; ReturnRpmTolerance = p.ReturnAgitationToleranceRpm; ReturnFlowTolerance = p.ReturnFlowToleranceLpm;
            MaximumDoDrop = p.AerationReturn.MaximumDoDropPoints; MinimumReturnDo = p.AerationReturn.MinimumDoPercent;
            ProbeTechnology = p.Probe.Technology ?? ""; ProbeModel = p.Probe.Model ?? ""; ProbeResponseSeconds = p.Probe.ResponseTimeSeconds;
            if (doc.Conditions.FirstOrDefault() is { } c) { NewConditionRpm = c.AgitationRpm; NewConditionFlow = c.AirflowLpm; }
        }
        finally { _isLoadingSettings = previous; }
        RefreshCommonState();
    }

    private void RefreshCommonState()
    {
        NotifyQueue();
        foreach (var name in new[] { nameof(IsAbiotic), nameof(IsMultipleCapture), nameof(CanEditPreparation), nameof(ShowPreparation), nameof(CanEditProtocol),
            nameof(CanRunSingle), nameof(CanDecideRun), nameof(CanAcceptAnalysis), nameof(CanRepeatLiveRun), nameof(PrimaryActionLabel),
            nameof(SavedPath), nameof(RestorationLabel), nameof(NextEvent), nameof(IsDeterministicReview), nameof(IsLegacyReview), nameof(ShowOur),
            nameof(DisplayOur), nameof(DisplayOurConcentration), nameof(DisplayOurQuality), nameof(DisplayEquilibrium), nameof(ReviewDiagnostics) })
            OnPropertyChanged(name);
        string[] titles;
        int step;
        if (IsBiotic)
        {
            titles = ["Preparar", "Medir consumo", "Reoxigenar", "Restaurar cultivo", "Revisar"];
            step = Phase switch
            {
                RunPhase.DivertingAir or RunPhase.MeasuringConsumption => 2,
                RunPhase.SwitchingToReactor or RunPhase.Reoxygenating => 3,
                RunPhase.RestoringCultivation or RunPhase.StoppingRun or RunPhase.Aborting => 4,
                RunPhase.Reviewing or RunPhase.Accepted or RunPhase.Rejected or RunPhase.Completed => 5,
                _ => 1,
            };
        }
        else
        {
            titles = ["Preparar", "Remover O₂", "Retomar ar", "Reoxigenar", "Finalizar corrida", "Revisar"];
            step = Phase switch
            {
                RunPhase.ClosingAllGas or RunPhase.OpeningNitrogen or RunPhase.Deoxygenating => 2,
                RunPhase.PrestagingAir or RunPhase.SwitchingToReactor => 3,
                RunPhase.Reoxygenating => 4,
                RunPhase.StoppingRun or RunPhase.Aborting => 5,
                RunPhase.Reviewing or RunPhase.Accepted or RunPhase.Rejected or RunPhase.Completed => 6,
                _ => 1,
            };
        }
        // Stable collection unless the phase/protocol actually changes; no redraw on each telemetry tick.
        var desired = titles.Select((title, i) => new KlaExecutionStep(i + 1, title, i + 1 < step ? "Concluído" : i + 1 == step ? "Atual" : "A seguir")).ToArray();
        if (!ExecutionSteps.SequenceEqual(desired)) { ExecutionSteps.Clear(); foreach (var s in desired) ExecutionSteps.Add(s); }
    }

    private async Task StartRunSafelyAsync(KlaTestCondition condition, int replicate)
    {
        IsReviewOpen = false;
        try { await _runner.StartRunAsync(condition, replicate); }
        catch (InvalidOperationException ex) { StatusMessage = ex.Message; _activeSequenceQueue.Clear(); }
        catch (ArgumentException ex) { StatusMessage = ex.Message; _activeSequenceQueue.Clear(); }
        UpdateUiState();
    }

    [RelayCommand]
    public async Task StartSingleAsync()
    {
        if (!CanRunSingle || CurrentTest?.Conditions.SingleOrDefault() is not { } condition) return;
        _activeSequenceQueue.Clear();
        await StartConditionRunAsync(new(condition));
    }

    [RelayCommand]
    public void CloseReview() { if (!IsRunning) { IsReviewOpen = false; UpdateUiState(); } }

    [RelayCommand]
    public void CloseDialogs()
    {
        if (IsCreateDialogOpen) CloseCreateDialog();
        IsAdvancedSettingsDialogOpen = false; IsLoadTestDialogOpen = false; IsStartSequenceDialogOpen = false;
    }

    [RelayCommand]
    public async Task PrimaryActionAsync()
    {
        if (!HasActiveTest) OpenCreateDialog();
        else if (IsReviewOpen) await SaveReviewRevisionAsync();
        else if (IsSingleCapture) await StartSingleAsync();
        else OpenStartSequenceDialog();
    }

    [RelayCommand]
    public void OpenSavedFolder() { if (!string.IsNullOrEmpty(SavedPath)) _files.OpenFolder(SavedPath); }

    [RelayCommand]
    public async Task ExportReviewAsync()
    {
        if (CurrentAnalysis is null || CurrentTest is null) return;
        var path = _files.ChooseSavePath("Exportar resultado e auditoria", $"{CurrentTest.Name}-resultado.json", "Resultado JSON|*.json", ".json");
        if (path is null) return;
        await _store.FlushAsync();
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(CurrentAnalysis,
            new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
        ReviewMessage = $"Resultado exportado: {path}";
    }

    [RelayCommand]
    public async Task SaveReviewRevisionAsync()
    {
        if (CurrentAnalysis is null || CurrentTest is null || !CanDecideRun) return;
        var folder = _currentlyEditingRun?.FolderName ?? CurrentRun?.FolderName;
        if (string.IsNullOrEmpty(folder)) return;
        var latest = _store.LoadRunAnalysis(CurrentTest.FolderName, folder)?.RevisionNumber ?? 0;
        CurrentAnalysis.RevisionNumber = latest + 1;
        CurrentAnalysis.AnalyzedUtc = DateTimeOffset.UtcNow;
        var physical = _currentlyEditingRun?.EffectiveOutcome ?? CurrentRun?.Outcome ?? new();
        CurrentAnalysis.Outcome = (CurrentAnalysis.Outcome ?? new()) with
        {
            Restoration = physical.Restoration, RestorationReason = physical.RestorationReason,
            OperatorDecision = KlaOperatorDecision.Pending,
        };
        _store.SaveRunAnalysis(CurrentTest.FolderName, folder, CurrentAnalysis);
        await _store.FlushAsync();
        ReviewMessage = $"Revisão {CurrentAnalysis.RevisionNumber} salva. Decisão sobre a corrida permanece pendente.";
    }

    private void LoadReviewAnalysis(KlaAnalysisRevision analysis)
    {
        _isRecomputing = true;
        try
        {
            ResetReviewInputs();
            CurrentAnalysis = analysis;
            ReviewTStart = analysis.TStartSeconds; ReviewTEnd = analysis.TEndSeconds;
            ReviewCeqTStart = analysis.CeqTStartSeconds > 0 ? analysis.CeqTStartSeconds : analysis.TStartSeconds;
            ReviewCeqTEnd = analysis.CeqTEndSeconds > 0 ? analysis.CeqTEndSeconds : analysis.TEndSeconds;
            ReviewCeq = analysis.CeqPercent; ReviewCeqIsManual = analysis.IsCeqManual;
            ReviewKla = analysis.KlaPerHour; ReviewR2 = analysis.AnalysisR2; ReviewRmse = analysis.AnalysisRmse;
            ReviewCi95Low = analysis.ConfidenceInterval95Low; ReviewCi95High = analysis.ConfidenceInterval95High;
            ReviewQuality = analysis.Quality; ReviewWarning = analysis.WarningJustification; ReviewRejectionReason = analysis.RejectionReason;
            ReviewMinTime = Math.Floor(LivePoints.First().RelativeSeconds); ReviewMaxTime = Math.Ceiling(LivePoints.Last().RelativeSeconds);
            LogLinearSeries.Clear(); InstantaneousKlaSeries.Clear(); ReviewPhases.Clear();
            if (analysis.DeterministicResult?.Input is { } input)
            {
                ReviewManualWindow = input.RecoveryWindow is not null; ReviewManualOurWindow = input.OurWindow is not null;
                ReviewManualPhases = !input.PhaseOverride.IsEmpty;
                ResidualTransferVerified = input.ResidualTransferNegligible; ConsumptionRepresentative = input.ConsumptionRepresentativeOfRecovery;
                ProbeResponseVerified = input.ProbeResponseNegligibleIndependentlyVerified;
                PhysicalSaturation = input.PhysicalSaturationPercent; PhysicalSaturationSource = input.PhysicalSaturationSource ?? "";
                ReferenceConcentration = input.ReferenceConcentrationMmolPerL; ReferenceConcentrationSource = input.ReferenceConcentrationSource ?? "";
                var result = analysis.DeterministicResult;
                if (result.Our.Window is { Start: >= 0 } our && our.End < input.Samples.Length)
                { ReviewOurStart = input.Samples[our.Start].Seconds; ReviewOurEnd = input.Samples[our.End].Seconds; }
                foreach (var d in result.RateDiagnostics.Where(d => d.EquilibriumRatioPerHour.HasValue))
                    InstantaneousKlaSeries.Add(new(input.Samples[d.Index].Seconds, input.Samples[d.Index].CalibratedDoPercent,
                        input.Samples[d.Index].CalibratedDoPercent, d.EquilibriumRatioPerHour, d.EquilibriumRatioPerHour));
                LoadPhaseDisplay(result);
            }
            else
            {
                foreach (var p in _analysisEngine.CalculateInstantaneousKlaSeries(LivePoints.Select(p => p.RelativeSeconds).ToArray(), LivePoints.Select(p => p.DOFiltered).ToArray(), ReviewCeq, SettingSmoothingWindow))
                    InstantaneousKlaSeries.Add(p);
            }
            if (analysis.DeterministicResult is null || analysis.DeterministicResult.SelectedWindow is not null)
                foreach (var p in _analysisEngine.ComputeLogLinearPoints(LivePoints.Select(p => p.RelativeSeconds).ToArray(), LivePoints.Select(p => p.DOFiltered).ToArray(), ReviewCeq, ReviewTStart, ReviewTEnd)) LogLinearSeries.Add(p);
            IsReviewOpen = true;
            ReviewMessage = $"Revisão {analysis.RevisionNumber} carregada; histórico preservado.";
        }
        finally { _isRecomputing = false; }
        RefreshCommonState();
    }

    private void ResetReviewInputs()
    {
        ReviewManualWindow = false; ReviewManualOurWindow = false; ReviewManualPhases = false;
        ResidualTransferVerified = false; ConsumptionRepresentative = false; ProbeResponseVerified = false;
        PhysicalSaturation = null; PhysicalSaturationSource = ""; ReferenceConcentration = null; ReferenceConcentrationSource = "";
        ReviewMessage = "";
    }

    private void LoadPhaseDisplay(KlaDeterministicResult result)
    {
        if (result.Input is not { } input) return;
        ReviewPhases.Clear();
        foreach (var group in result.Phases.GroupAdjacentByPhase())
            ReviewPhases.Add(new(PhaseText(group[0].Phase), $"{input.Samples[group[0].Index].Seconds:F1}–{input.Samples[group[^1].Index].Seconds:F1} s", group[0].Origin));
        double Start(KlaScientificPhase phase) => result.Phases.FirstOrDefault(p => p.Phase == phase) is { } p ? input.Samples[p.Index].Seconds : input.Samples.LastOrDefault()?.Seconds ?? 0;
        ReviewConsumptionPhaseStart = Start(KlaScientificPhase.GasOffConsumption);
        ReviewRecoveryPhaseStart = Start(KlaScientificPhase.GasOnRecovery);
        ReviewSteadyPhaseStart = result.Phases.LastOrDefault(p => p.Phase == KlaScientificPhase.GasOnRecovery) is { } last
            ? input.Samples[Math.Min(last.Index + 1, input.Samples.Length - 1)].Seconds : input.Samples.LastOrDefault()?.Seconds ?? 0;
    }

    private bool TryRecomputeDeterministic()
    {
        if (CurrentTest is null || _analysisEngine is not IKlaDeterministicAnalysisEngine engine) return false;
        var folder = _currentlyEditingRun?.FolderName ?? CurrentRun?.FolderName;
        var events = _currentlyEditingRun is null ? CurrentRun?.GasEvents.ToArray() ?? []
            : _store.LoadRunGasEvents(CurrentTest.FolderName, folder!).ToArray();
        if (events.Length == 0) return false; // Never manufacture physical evidence for old files.
        var definition = _currentlyEditingRun?.Definition ?? (_currentlyEditingRun is null ? CurrentRun?.Definition : null)
            ?? (folder is null ? null : _store.LoadRunDefinition(CurrentTest.FolderName, folder));
        if (definition is null) return false;
        var request = KlaDeterministicRequestFactory.FromRun(definition, LivePoints.ToArray(), events,
            CurrentAnalysis?.DeterministicResult?.Config,
            _currentlyEditingRun is null ? CurrentRun?.RawDataSha256 ?? "" : KlaTestFileContracts.ComputeFileSha256(_store.GetRunRawDataPath(CurrentTest.FolderName, folder!)));
        KlaIndexWindow? Window(double start, double end, string origin)
        {
            var indices = request.Samples.Select((p, i) => (p, i)).Where(x => x.p.Seconds >= start && x.p.Seconds <= end).Select(x => x.i).ToArray();
            // Empty/reversed selections remain invalid instead of falling back to an automatic window.
            return indices.Length == 0 || end < start ? new(-1, -1, origin) : new(indices[0], indices[^1], origin);
        }
        request = request with
        {
            RecoveryWindow = ReviewManualWindow ? Window(ReviewTStart, ReviewTEnd, "operator_window_v1") : null,
            OurWindow = ReviewManualOurWindow ? Window(ReviewOurStart, ReviewOurEnd, "operator_our_window_v1") : null,
            ManualEquilibriumPercent = ReviewCeqIsManual ? ReviewCeq : null,
            ResidualTransferNegligible = ResidualTransferVerified, ConsumptionRepresentativeOfRecovery = ConsumptionRepresentative,
            ProbeResponseNegligibleIndependentlyVerified = ProbeResponseVerified,
            PhysicalSaturationPercent = PhysicalSaturation, PhysicalSaturationSource = PhysicalSaturationSource,
            ReferenceConcentrationMmolPerL = ReferenceConcentration, ReferenceConcentrationSource = ReferenceConcentrationSource,
        };
        if (ReviewManualPhases)
        {
            var off = events.FirstOrDefault(e => e.Kind == KlaGasEventKind.GasOffConfirmed)?.Seconds ?? double.NegativeInfinity;
            var on = events.FirstOrDefault(e => e.Kind == KlaGasEventKind.GasOnConfirmed)?.Seconds;
            if (on is null || !double.IsFinite(ReviewConsumptionPhaseStart) || !double.IsFinite(ReviewRecoveryPhaseStart) ||
                !double.IsFinite(ReviewSteadyPhaseStart) || ReviewConsumptionPhaseStart < off || ReviewConsumptionPhaseStart > on ||
                ReviewRecoveryPhaseStart < on || ReviewSteadyPhaseStart < ReviewRecoveryPhaseStart)
            {
                CurrentAnalysis = new KlaDeterministicResult { Input = request, Reasons = ["operator_phase_timing_invalid"] }.ToRevision(1, DateTimeOffset.UtcNow);
                LogLinearSeries.Clear(); InstantaneousKlaSeries.Clear();
                ReviewQuality = DecisionQuality.Inconclusive; ReviewMessage = "Tempos de fase incompatíveis com as comutações confirmadas.";
                return true;
            }
            request = request with { PhaseOverride = request.Samples.Select((p, i) => new KlaPhasePoint(i,
                p.Seconds < off ? KlaScientificPhase.Steady : p.Seconds < on.Value
                    ? p.Seconds < ReviewConsumptionPhaseStart ? KlaScientificPhase.GasOffTransient : KlaScientificPhase.GasOffConsumption
                    : p.Seconds < ReviewRecoveryPhaseStart ? KlaScientificPhase.GasOnTransient
                    : p.Seconds < ReviewSteadyPhaseStart ? KlaScientificPhase.GasOnRecovery : KlaScientificPhase.Steady,
                "operator_phase_timing_v1", 1)).ToImmutableArray() };
        }
        var result = engine.AnalyzeDeterministic(request);
        CurrentAnalysis = result.ToRevision((CurrentAnalysis?.RevisionNumber ?? 0) + 1, DateTimeOffset.UtcNow);
        if (_currentlyEditingRun?.EffectiveOutcome is not null || CurrentRun?.Outcome is not null)
        {
            var outcome = _currentlyEditingRun?.EffectiveOutcome ?? CurrentRun!.Outcome!;
            CurrentAnalysis.Outcome = CurrentAnalysis.Outcome! with { Restoration = outcome.Restoration, RestorationReason = outcome.RestorationReason };
        }
        ReviewKla = CurrentAnalysis.KlaPerHour; ReviewR2 = CurrentAnalysis.AnalysisR2; ReviewRmse = CurrentAnalysis.AnalysisRmse;
        ReviewCi95Low = CurrentAnalysis.ConfidenceInterval95Low; ReviewCi95High = CurrentAnalysis.ConfidenceInterval95High;
        ReviewQuality = CurrentAnalysis.Quality; ReviewWarning = CurrentAnalysis.WarningJustification; ReviewRejectionReason = CurrentAnalysis.RejectionReason;
        if (result.Equilibrium.Percent is { } ceq && !ReviewCeqIsManual) ReviewCeq = ceq;
        if (result.SelectedWindow is { } selected && !ReviewManualWindow) { ReviewTStart = request.Samples[selected.Window.Start].Seconds; ReviewTEnd = request.Samples[selected.Window.End].Seconds; }
        if (result.Equilibrium.Window is { } eq) { ReviewCeqTStart = request.Samples[eq.Start].Seconds; ReviewCeqTEnd = request.Samples[eq.End].Seconds; }
        if (result.Our.Window is { Start: >= 0 } our && our.End < request.Samples.Length && !ReviewManualOurWindow)
        { ReviewOurStart = request.Samples[our.Start].Seconds; ReviewOurEnd = request.Samples[our.End].Seconds; }
        LogLinearSeries.Clear(); InstantaneousKlaSeries.Clear(); ReviewPhases.Clear();
        if (result.Equilibrium.Percent is { } equilibrium && result.SelectedWindow is not null)
        {
            foreach (var p in _analysisEngine.ComputeLogLinearPoints(request.Samples.Select(s => s.Seconds).ToArray(), request.Samples.Select(s => s.CalibratedDoPercent).ToArray(), equilibrium, ReviewTStart, ReviewTEnd)) LogLinearSeries.Add(p);
        }
        foreach (var d in result.RateDiagnostics.Where(d => d.EquilibriumRatioPerHour.HasValue))
            InstantaneousKlaSeries.Add(new(request.Samples[d.Index].Seconds, request.Samples[d.Index].CalibratedDoPercent,
                request.Samples[d.Index].CalibratedDoPercent, d.EquilibriumRatioPerHour, d.EquilibriumRatioPerHour));
        if (!ReviewManualPhases) LoadPhaseDisplay(result);
        else foreach (var group in result.Phases.GroupAdjacentByPhase())
            ReviewPhases.Add(new(PhaseText(group[0].Phase), $"{request.Samples[group[0].Index].Seconds:F1}–{request.Samples[group[^1].Index].Seconds:F1} s", group[0].Origin));
        ReviewMessage = "Análise recalculada; salve uma revisão para registrar as alterações.";
        RefreshCommonState(); NotifyDisplayReviewChanged();
        return true;
    }

    private static string PhaseText(KlaScientificPhase phase) => phase switch
    {
        KlaScientificPhase.GasOffTransient => "Transiente sem gás",
        KlaScientificPhase.GasOffConsumption => "Consumo / remoção",
        KlaScientificPhase.GasOnTransient => "Transiente com ar",
        KlaScientificPhase.GasOnRecovery => "Recuperação",
        _ => "Patamar",
    };
}

internal static class KlaPhaseGrouping
{
    internal static IEnumerable<KlaPhasePoint[]> GroupAdjacentByPhase(this ImmutableArray<KlaPhasePoint> phases)
    {
        var start = 0;
        while (start < phases.Length)
        {
            var end = start + 1;
            while (end < phases.Length && phases[end].Phase == phases[start].Phase) end++;
            yield return phases.Skip(start).Take(end - start).ToArray(); start = end;
        }
    }
}
