using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TecnalHub.Services.Dialogs;
using TecnalHub.Services.KlaMapping;
using TecnalHub.Services.Platform;
using TecnalHub.Services.Telemetry;

namespace TecnalHub.ViewModels;

public sealed partial class KlaAnchorRowViewModel : ObservableObject
{
    public KlaAnchorRowViewModel(KlaAnchor? anchor = null)
    {
        if (anchor is not null)
        {
            Airflow = anchor.AirflowLpm.ToString("G12", CultureInfo.CurrentCulture);
            Agitation = anchor.AgitationRpm.ToString("G12", CultureInfo.CurrentCulture);
            Kla = anchor.KlaPerHour.ToString("G12", CultureInfo.CurrentCulture);
        }
    }

    public KlaAnchorRowViewModel(KlaAnchorDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        Airflow = draft.Airflow;
        Agitation = draft.Agitation;
        Kla = draft.Kla;
    }

    [ObservableProperty]
    public partial string Airflow { get; set; } = "";

    [ObservableProperty]
    public partial string Agitation { get; set; } = "";

    [ObservableProperty]
    public partial string Kla { get; set; } = "";

    public bool TryBuild(out KlaAnchor anchor)
    {
        var airflow = 0.0;
        var agitation = 0.0;
        var kla = 0.0;
        var valid = TryParse(Airflow, out airflow) &
                    TryParse(Agitation, out agitation) &
                    TryParse(Kla, out kla);
        anchor = new KlaAnchor(airflow, agitation, kla);
        return valid;
    }

    public KlaAnchorDraft ToDraft() => new(Airflow, Agitation, Kla);

    private static bool TryParse(string text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
           double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}

public sealed record KlaExperimentListItem(Guid Id, string Name, KlaWorkflowStage Stage)
{
    public string StageLabel => Stage switch
    {
        KlaWorkflowStage.SurfaceEstimated => "Superfície",
        KlaWorkflowStage.PathValid => "Trajetória",
        KlaWorkflowStage.Reviewed => "Revisada",
        KlaWorkflowStage.Published => "Publicada",
        _ => "Rascunho",
    };
}

public sealed record KlaPublishedListItem(
    string ReceiptFingerprint,
    string Name,
    int Version,
    DateTimeOffset PublishedAtUtc)
{
    public string Display => $"{Name} · v{Version}";

    public string FingerprintShort => KlaFingerprint.Short(ReceiptFingerprint);
}

/// <summary>
/// Operator workflow around the pure kLa engine. Long calculations capture an immutable
/// input and reject stale results if the editor changes while they are running.
/// </summary>
public sealed partial class KlaMappingViewModel : ObservableObject, IDisposable
{
    private readonly IKlaMappingEngine _engine;
    private readonly IKlaProfileStore _store;
    private readonly IFileInteractionService _files;
    private readonly IDialogService _dialogs;
    private readonly IEventJournal _journal;
    private readonly Dictionary<Guid, KlaExperimentDocument> _documents = [];

    private CancellationTokenSource? _workCancellation;
    private bool _loading;
    private bool _suppressSelectionLoad;
    private bool _initialized;
    private string? _resultInputFingerprint;

    public KlaMappingViewModel(
        IKlaMappingEngine engine,
        IKlaProfileStore store,
        IFileInteractionService files,
        IDialogService dialogs,
        IEventJournal journal)
    {
        _engine = engine;
        _store = store;
        _files = files;
        _dialogs = dialogs;
        _journal = journal;
    }

    public event Action? VisualizationChanged;

    public ObservableCollection<KlaExperimentListItem> Experiments { get; } = [];

    public ObservableCollection<KlaAnchorRowViewModel> Anchors { get; } = [];

    public ObservableCollection<KlaPublishedListItem> PublishedProfiles { get; } = [];

    public KlaSurface? Surface { get; private set; }

    public KlaPathResult? PathResult { get; private set; }

    [ObservableProperty]
    public partial KlaExperimentListItem? SelectedExperiment { get; set; }

    [ObservableProperty]
    public partial KlaPublishedListItem? SelectedPublishedProfile { get; set; }

    [ObservableProperty]
    public partial string NewExperimentName { get; set; } = "Novo experimento";

    [ObservableProperty]
    public partial string ExperimentName { get; set; } = "";

    [ObservableProperty]
    public partial string Broth { get; set; } = "";

    [ObservableProperty]
    public partial string RunCode { get; set; } = "";

    [ObservableProperty]
    public partial string Notes { get; set; } = "";

    [ObservableProperty]
    public partial double AirflowMinimum { get; set; } = 2;

    [ObservableProperty]
    public partial double AirflowMaximum { get; set; } = 12;

    [ObservableProperty]
    public partial double AgitationMinimum { get; set; } = 200;

    [ObservableProperty]
    public partial double AgitationMaximum { get; set; } = 800;

    [ObservableProperty]
    public partial int SurfaceGridResolution { get; set; } = 300;

    [ObservableProperty]
    public partial double GaussianSigma { get; set; } = 5;

    [ObservableProperty]
    public partial double CloughTocherGradientTolerance { get; set; } = 1e-6;

    [ObservableProperty]
    public partial int CloughTocherMaximumIterations { get; set; } = 400;

    [ObservableProperty]
    public partial int CandidateGridResolution { get; set; } = 150;

    [ObservableProperty]
    public partial double CandidateMinimum { get; set; } = 0.0001;

    [ObservableProperty]
    public partial double CandidateMaximum { get; set; } = 0.9999;

    [ObservableProperty]
    public partial double OdeMaximumStep { get; set; } = 0.05;

    [ObservableProperty]
    public partial double OdeRelativeTolerance { get; set; } = 1e-5;

    [ObservableProperty]
    public partial double OdeAbsoluteTolerance { get; set; } = 1e-7;

    [ObservableProperty]
    public partial double GradientTermination { get; set; } = 1e-4;

    [ObservableProperty]
    public partial double IntegrationHorizon { get; set; } = 10;

    [ObservableProperty]
    public partial string ReviewNote { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StageLabel))]
    [NotifyPropertyChangedFor(nameof(HasExperiment))]
    public partial KlaWorkflowStage Stage { get; set; } = KlaWorkflowStage.Draft;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    [ObservableProperty]
    public partial string ProgressText { get; set; } = "";

    [ObservableProperty]
    public partial string? ValidationMessage { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Crie ou selecione um experimento para começar.";

    [ObservableProperty]
    public partial bool HasUnsavedChanges { get; set; }

    public bool HasExperiment => SelectedExperiment is not null;

    public bool IsIdle => !IsBusy;

    public string StageLabel => Stage switch
    {
        KlaWorkflowStage.SurfaceEstimated => "2 · Superfície estimada",
        KlaWorkflowStage.PathValid => "4 · Trajetória válida",
        KlaWorkflowStage.Reviewed => "5 · Revisada",
        KlaWorkflowStage.Published => "5 · Publicada",
        _ => "1 · Rascunho experimental",
    };

    public string AlgorithmIdentity => KlaMappingEngine.AlgorithmIdentity(BuildAlgorithm());

    public bool IsPaperReference => BuildAlgorithm().IsPaperReference;

    public string SurfaceFingerprintShort => Surface is null ? "—" : KlaFingerprint.Short(Surface.Fingerprint);

    public string PathFingerprintShort => PathResult is null ? "—" : KlaFingerprint.Short(PathResult.Fingerprint);

    public string SurfaceRangeText => Surface is null
        ? "—"
        : $"{Surface.Diagnostics.MinimumKlaPerHour:F2} — {Surface.Diagnostics.MaximumKlaPerHour:F2} h⁻¹";

    public string ResidualText => Surface is null
        ? "—"
        : $"RMSE {Surface.Diagnostics.AnchorResidualRmse:F3} · máx. |e| {Surface.Diagnostics.AnchorResidualMaximumAbsolute:F3} h⁻¹";

    public string CoverageText => Surface is null
        ? "—"
        : $"{Surface.Diagnostics.ConvexHullCoveragePercent:F1}% · {Surface.Diagnostics.NearestFilledNodes} nós por vizinho";

    public string SelectedStartText => PathResult is null
        ? "—"
        : $"q={PathResult.Diagnostics.SelectedStartAirflowNormalized:F4}, n={PathResult.Diagnostics.SelectedStartAgitationNormalized:F4}\n" +
          $"{PathResult.Diagnostics.SelectedStartAirflowLpm:F2} L/min · {PathResult.Diagnostics.SelectedStartAgitationRpm:F0} rpm";

    public string HeadroomText => PathResult is null
        ? "—"
        : $"selecionada {PathResult.Diagnostics.MeanHeadroom:F4} · máximo avaliado {PathResult.Diagnostics.EvaluatedMaximumHeadroom:F4}";

    public string PathSummaryText => PathResult is null
        ? "—"
        : $"{PathResult.Diagnostics.MinimumKlaPerHour:F2} — {PathResult.Diagnostics.MaximumKlaPerHour:F2} h⁻¹ · " +
          $"L={PathResult.Diagnostics.NormalizedPathLength:F3} · {PathResult.Diagnostics.AllocationSampleCount} pontos";

    public string NumericalParametersText
    {
        get
        {
            var settings = BuildAlgorithm();
            return $"Malha {settings.SurfaceGridResolution}×{settings.SurfaceGridResolution} · σ={settings.GaussianSigmaGridCells:G4} células\n" +
                   $"Clough–Tocher tol={settings.CloughTocherGradientTolerance:E1}, máx. {settings.CloughTocherMaximumIterations}\n" +
                   $"Busca {settings.CandidateGridResolution}×{settings.CandidateGridResolution} em [{settings.CandidateMinimum:G4},{settings.CandidateMaximum:G4}]\n" +
                   $"RK45 passo≤{settings.OdeMaximumStep:G4}, rtol={settings.OdeRelativeTolerance:E1}, atol={settings.OdeAbsoluteTolerance:E1}\n" +
                   $"Parada |∇kLa|<{settings.GradientTermination:E1}, τ≤{settings.IntegrationHorizon:G4}";
        }
    }

    public string WarningText
    {
        get
        {
            var warnings = new List<string>();
            if (!IsPaperReference)
            {
                warnings.Add("Parâmetros personalizados: restaure a referência antes da revisão/publicação como método do artigo.");
            }

            if (Surface is not null)
            {
                warnings.AddRange(Surface.Diagnostics.Warnings);
            }

            if (PathResult is not null)
            {
                warnings.AddRange(PathResult.Diagnostics.Warnings);
            }

            return warnings.Count == 0 ? "Nenhuma recusa numérica." : string.Join("\n", warnings);
        }
    }

    public bool CanEstimate => HasExperiment && !IsBusy && Anchors.Count >= 6;

    public bool CanCalculatePath => !IsBusy && Surface is not null && ResultIsCurrent();

    public bool CanReview => !IsBusy && Stage == KlaWorkflowStage.PathValid &&
                             PathResult is not null && IsPaperReference &&
                             !string.IsNullOrWhiteSpace(ReviewNote) &&
                             !PathResult.Diagnostics.Warnings.Any(warning =>
                                 warning.Contains("recusada", StringComparison.OrdinalIgnoreCase));

    public bool CanPublish => !IsBusy && Stage == KlaWorkflowStage.Reviewed &&
                              PathResult is not null && Surface is not null &&
                              ResultIsCurrent() && IsPaperReference &&
                              !string.IsNullOrWhiteSpace(ReviewNote);

    [RelayCommand]
    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        try
        {
            var documents = await _store.LoadExperimentsAsync();
            var published = await _store.LoadPublishedAsync();
            _documents.Clear();
            foreach (var document in documents)
            {
                // Computed arrays are intentionally not mutable-draft state. After a
                // restart they must be recomputed; only a published receipt is durable.
                _documents[document.Snapshot.Id] = document.Stage == KlaWorkflowStage.Published
                    ? document
                    : document with { Stage = KlaWorkflowStage.Draft };
            }

            RebuildExperimentList();
            PublishedProfiles.Clear();
            foreach (var profile in published)
            {
                PublishedProfiles.Add(new KlaPublishedListItem(
                    profile.ReceiptFingerprint,
                    profile.Payload.Name,
                    profile.Payload.Version,
                    profile.Payload.PublishedAtUtc));
            }

            if (Experiments.Count > 0)
            {
                SelectedExperiment = Experiments[0];
            }
            else
            {
                StatusMessage = "Nenhum mapa operacional foi instalado. Crie um experimento com dados medidos.";
            }
        }
        catch (Exception exception)
        {
            ValidationMessage = "Não foi possível abrir os experimentos kLa: " + exception.Message;
        }
    }

    [RelayCommand]
    private async Task CreateExperimentAsync()
    {
        var name = string.IsNullOrWhiteSpace(NewExperimentName)
            ? "Novo experimento"
            : NewExperimentName.Trim();
        var snapshot = new KlaExperimentSnapshot { Name = UniqueName(name) };
        var document = new KlaExperimentDocument { Snapshot = snapshot };
        _documents[snapshot.Id] = document;
        await _store.SaveExperimentAsync(document);
        RebuildExperimentList();
        SelectedExperiment = Experiments.Single(item => item.Id == snapshot.Id);
        NewExperimentName = "Novo experimento";
        StatusMessage = "Rascunho criado sem pontos pré-carregados.";
    }

    [RelayCommand]
    private async Task DeleteExperimentAsync()
    {
        if (SelectedExperiment is not { } selected ||
            !_dialogs.ConfirmDestructive(
                "Excluir experimento kLa",
                $"O rascunho “{selected.Name}” será removido. Recibos já publicados permanecem imutáveis.",
                "Nenhum comando será enviado ao equipamento."))
        {
            return;
        }

        await _store.DeleteExperimentAsync(selected.Id);
        _documents.Remove(selected.Id);
        RebuildExperimentList();
        SelectedExperiment = Experiments.FirstOrDefault();
        if (SelectedExperiment is null)
        {
            ClearEditor();
        }
    }

    [RelayCommand]
    private async Task DuplicateExperimentAsync()
    {
        if (!TryBuildSnapshot(out var current, out _, validateScientific: false))
        {
            return;
        }

        var copy = current with
        {
            Id = Guid.NewGuid(),
            Name = UniqueName(current.Name + " · cópia"),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        var document = new KlaExperimentDocument
        {
            Snapshot = copy,
            DraftRows = Anchors.Select(row => row.ToDraft()).ToArray(),
            ReviewNote = ReviewNote,
        };
        _documents[copy.Id] = document;
        await _store.SaveExperimentAsync(document);
        RebuildExperimentList();
        SelectedExperiment = Experiments.Single(item => item.Id == copy.Id);
    }

    [RelayCommand]
    private async Task ImportReceiptAsync()
    {
        var path = _files.ChooseOpenPath(
            "Importar recibo kLa como novo rascunho",
            "Recibo kLa (*.kla.json)|*.kla.json|JSON (*.json)|*.json",
            ".kla.json");
        if (path is null)
        {
            return;
        }

        try
        {
            var snapshot = await _store.ImportReceiptAsDraftAsync(path);
            snapshot = snapshot with { Name = UniqueName(snapshot.Name) };
            var document = new KlaExperimentDocument { Snapshot = snapshot };
            _documents[snapshot.Id] = document;
            await _store.SaveExperimentAsync(document);
            RebuildExperimentList();
            SelectedExperiment = Experiments.Single(item => item.Id == snapshot.Id);
            StatusMessage = "Recibo importado como rascunho; cálculo e revisão locais continuam obrigatórios.";
        }
        catch (Exception exception)
        {
            ValidationMessage = "Importação recusada: " + exception.Message;
        }
    }

    [RelayCommand]
    private async Task ExportReceiptAsync()
    {
        if (SelectedPublishedProfile is not { } profile)
        {
            return;
        }

        var destination = _files.ChooseSavePath(
            "Exportar recibo kLa",
            $"{SafeFileName(profile.Name)}_v{profile.Version}.kla.json",
            "Recibo kLa (*.kla.json)|*.kla.json",
            ".kla.json");
        if (destination is not null)
        {
            await _store.ExportReceiptAsync(profile.ReceiptFingerprint, destination);
            StatusMessage = "Recibo exportado byte por byte.";
        }
    }

    [RelayCommand]
    private void AddAnchor()
    {
        AddRow(new KlaAnchorRowViewModel());
        InvalidateScientificResult();
    }

    [RelayCommand]
    private void RemoveAnchor(KlaAnchorRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        row.PropertyChanged -= OnAnchorChanged;
        Anchors.Remove(row);
        InvalidateScientificResult();
    }

    [RelayCommand]
    private void CreateFactorialDesign()
    {
        foreach (var row in Anchors)
        {
            row.PropertyChanged -= OnAnchorChanged;
        }

        Anchors.Clear();
        var airflow = new[] { AirflowMinimum, (AirflowMinimum + AirflowMaximum) / 2, AirflowMaximum };
        var agitation = new[] { AgitationMaximum, (AgitationMinimum + AgitationMaximum) / 2, AgitationMinimum };
        foreach (var n in agitation)
        {
            foreach (var q in airflow)
            {
                AddRow(new KlaAnchorRowViewModel
                {
                    Airflow = q.ToString("G12", CultureInfo.CurrentCulture),
                    Agitation = n.ToString("G12", CultureInfo.CurrentCulture),
                    Kla = "",
                });
            }
        }

        InvalidateScientificResult();
        StatusMessage = "Desenho 3² criado com kLa em branco; nenhum valor do artigo foi carregado.";
    }

    [RelayCommand]
    private void RestorePaperParameters()
    {
        _loading = true;
        var settings = new KlaAlgorithmSettings();
        LoadAlgorithm(settings);
        _loading = false;
        InvalidateScientificResult();
    }

    [RelayCommand]
    private void UsePreviewParameters()
    {
        _loading = true;
        LoadAlgorithm(new KlaAlgorithmSettings
        {
            SurfaceGridResolution = 100,
            CandidateGridResolution = 25,
        });
        _loading = false;
        InvalidateScientificResult();
        StatusMessage = "Prévia rápida selecionada. Restaure os parâmetros do artigo antes de revisar.";
    }

    [RelayCommand]
    private async Task SaveDraftAsync()
    {
        if (!TryBuildSnapshot(out var snapshot, out var issues, validateScientific: false))
        {
            ValidationMessage = issues[0];
            return;
        }

        await PersistCurrentAsync(snapshot);
        StatusMessage = "Rascunho salvo. Salvar não calcula nem envia comandos.";
    }

    [RelayCommand(CanExecute = nameof(CanEstimate))]
    private async Task EstimateSurfaceAsync()
    {
        if (!TryBuildSnapshot(out var snapshot, out var issues))
        {
            ValidationMessage = issues[0];
            return;
        }

        var validation = _engine.Validate(snapshot);
        if (validation.Count > 0)
        {
            ValidationMessage = validation[0];
            return;
        }

        await RunWorkAsync(
            "Reconstruindo Clough–Tocher, filtro gaussiano e spline…",
            async cancellationToken =>
            {
                var fingerprint = snapshot.ScientificFingerprint();
                var result = await Task.Run(() => _engine.Reconstruct(snapshot, cancellationToken), cancellationToken);
                if (!CurrentFingerprintEquals(fingerprint))
                {
                    StatusMessage = "Resultado descartado: os dados mudaram durante o cálculo.";
                    return;
                }

                Surface = result;
                PathResult = null;
                _resultInputFingerprint = fingerprint;
                Stage = KlaWorkflowStage.SurfaceEstimated;
                StatusMessage = "Superfície estimada. Revise resíduos e cobertura antes de calcular a trajetória.";
                RefreshComputedProperties();
                VisualizationChanged?.Invoke();
                await PersistCurrentAsync(snapshot);
            });
    }

    [RelayCommand(CanExecute = nameof(CanCalculatePath))]
    private async Task CalculatePathAsync()
    {
        if (Surface is not { } surface)
        {
            return;
        }

        await RunWorkAsync(
            $"Avaliando {surface.Input.Algorithm.CandidateGridResolution}×{surface.Input.Algorithm.CandidateGridResolution} condições iniciais…",
            async cancellationToken =>
            {
                var fingerprint = surface.Input.ScientificFingerprint();
                var progress = new Progress<KlaSearchProgress>(update =>
                {
                    ProgressPercent = 100.0 * update.CompletedCandidates / update.TotalCandidates;
                    ProgressText = $"Linha {update.CompletedRows}/{update.TotalRows} · " +
                                   $"melhor folga {update.BestHeadroom:F4} em " +
                                   $"({update.BestAirflowNormalized:F3}, {update.BestAgitationNormalized:F3})";
                });
                var result = await Task.Run(
                    () => _engine.FindMaximumHeadroomPath(surface, progress, cancellationToken),
                    cancellationToken);
                if (!CurrentFingerprintEquals(fingerprint))
                {
                    StatusMessage = "Trajetória descartada: os dados mudaram durante a busca.";
                    return;
                }

                PathResult = result;
                Stage = KlaWorkflowStage.PathValid;
                ProgressPercent = 100;
                StatusMessage = "Trajetória válida, orientada de baixo para alto kLa. A revisão ainda é explícita.";
                RefreshComputedProperties();
                VisualizationChanged?.Invoke();
                if (TryBuildSnapshot(out var snapshot, out _))
                {
                    await PersistCurrentAsync(snapshot);
                }
            });
    }

    [RelayCommand]
    private void CancelWork() => _workCancellation?.Cancel();

    [RelayCommand(CanExecute = nameof(CanReview))]
    private async Task MarkReviewedAsync()
    {
        Stage = KlaWorkflowStage.Reviewed;
        if (TryBuildSnapshot(out var snapshot, out _))
        {
            await PersistCurrentAsync(snapshot);
        }

        StatusMessage = "Resultado marcado como revisado. Publicar criará um recibo imutável; não enviará comandos.";
        UpdateCommands();
    }

    [RelayCommand(CanExecute = nameof(CanPublish))]
    private async Task PublishAsync()
    {
        if (Surface is not { } surface || PathResult is not { } path)
        {
            ValidationMessage = "Não há resultado atual para publicar.";
            return;
        }

        if (!TryBuildSnapshot(out var snapshot, out var issues))
        {
            ValidationMessage = issues.FirstOrDefault() ?? "O experimento atual é inválido.";
            return;
        }

        try
        {
            var profile = await _store.PublishAsync(snapshot, surface, path, ReviewNote);
            Stage = KlaWorkflowStage.Published;
            HasUnsavedChanges = false;
            var document = new KlaExperimentDocument
            {
                Snapshot = snapshot,
                DraftRows = Anchors.Select(row => row.ToDraft()).ToArray(),
                Stage = Stage,
                LatestReceiptFingerprint = profile.ReceiptFingerprint,
                ReviewNote = ReviewNote,
            };
            _documents[snapshot.Id] = document;
            await _store.SaveExperimentAsync(document);
            PublishedProfiles.Insert(0, new KlaPublishedListItem(
                profile.ReceiptFingerprint,
                profile.Payload.Name,
                profile.Payload.Version,
                profile.Payload.PublishedAtUtc));
            SelectedPublishedProfile = PublishedProfiles[0];
            RebuildExperimentList(snapshot.Id);
            _journal.Add(
                AuditSource.Application,
                AuditSeverity.Information,
                $"Perfil kLa publicado: {profile.Payload.Name} v{profile.Payload.Version}.",
                $"Recibo {profile.ReceiptFingerprint}; superfície {surface.Fingerprint}; trajetória {path.Fingerprint}. " +
                "Nenhum comando foi enviado.");
            StatusMessage = $"Perfil publicado: v{profile.Payload.Version} · {KlaFingerprint.Short(profile.ReceiptFingerprint)}.";
            UpdateCommands();
        }
        catch (Exception exception)
        {
            ValidationMessage = "Publicação recusada: " + exception.Message;
        }
    }

    partial void OnSelectedExperimentChanged(KlaExperimentListItem? value)
    {
        if (!_suppressSelectionLoad && value is not null && _documents.TryGetValue(value.Id, out var document))
        {
            LoadDocument(document);
        }

        OnPropertyChanged(nameof(HasExperiment));
        UpdateCommands();
    }

    partial void OnExperimentNameChanged(string value) => MetadataChanged();
    partial void OnBrothChanged(string value) => MetadataChanged();
    partial void OnRunCodeChanged(string value) => MetadataChanged();
    partial void OnNotesChanged(string value) => MetadataChanged();
    partial void OnReviewNoteChanged(string value)
    {
        if (!_loading)
        {
            HasUnsavedChanges = true;
            UpdateCommands();
        }
    }

    partial void OnAirflowMinimumChanged(double value) => InvalidateScientificResult();
    partial void OnAirflowMaximumChanged(double value) => InvalidateScientificResult();
    partial void OnAgitationMinimumChanged(double value) => InvalidateScientificResult();
    partial void OnAgitationMaximumChanged(double value) => InvalidateScientificResult();
    partial void OnSurfaceGridResolutionChanged(int value) => InvalidateScientificResult();
    partial void OnGaussianSigmaChanged(double value) => InvalidateScientificResult();
    partial void OnCloughTocherGradientToleranceChanged(double value) => InvalidateScientificResult();
    partial void OnCloughTocherMaximumIterationsChanged(int value) => InvalidateScientificResult();
    partial void OnCandidateGridResolutionChanged(int value) => InvalidateScientificResult();
    partial void OnCandidateMinimumChanged(double value) => InvalidateScientificResult();
    partial void OnCandidateMaximumChanged(double value) => InvalidateScientificResult();
    partial void OnOdeMaximumStepChanged(double value) => InvalidateScientificResult();
    partial void OnOdeRelativeToleranceChanged(double value) => InvalidateScientificResult();
    partial void OnOdeAbsoluteToleranceChanged(double value) => InvalidateScientificResult();
    partial void OnGradientTerminationChanged(double value) => InvalidateScientificResult();
    partial void OnIntegrationHorizonChanged(double value) => InvalidateScientificResult();

    private async Task RunWorkAsync(string status, Func<CancellationToken, Task> work)
    {
        _workCancellation?.Cancel();
        _workCancellation?.Dispose();
        _workCancellation = new CancellationTokenSource();
        IsBusy = true;
        ProgressPercent = 0;
        ProgressText = status;
        ValidationMessage = null;
        UpdateCommands();
        try
        {
            await work(_workCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cálculo cancelado; o último resultado válido foi preservado.";
        }
        catch (Exception exception)
        {
            ValidationMessage = exception.Message;
            StatusMessage = "O estágio numérico foi recusado.";
        }
        finally
        {
            IsBusy = false;
            UpdateCommands();
        }
    }

    private bool TryBuildSnapshot(
        out KlaExperimentSnapshot snapshot,
        out IReadOnlyList<string> issues,
        bool validateScientific = true)
    {
        var found = new List<string>();
        var anchors = new List<KlaAnchor>();
        foreach (var row in Anchors)
        {
            if (!row.TryBuild(out var anchor))
            {
                if (validateScientific)
                {
                    found.Add("Preencha Qg, N e kLa em todas as linhas ou remova as linhas incompletas.");
                }

                continue;
            }

            anchors.Add(anchor);
        }

        if (string.IsNullOrWhiteSpace(ExperimentName))
        {
            found.Add("O experimento precisa de um nome.");
        }

        snapshot = new KlaExperimentSnapshot
        {
            Id = SelectedExperiment?.Id ?? Guid.NewGuid(),
            Name = ExperimentName.Trim(),
            Broth = Broth.Trim(),
            RunCode = RunCode.Trim(),
            Notes = Notes.Trim(),
            Domain = new KlaDomain(AirflowMinimum, AirflowMaximum, AgitationMinimum, AgitationMaximum),
            Anchors = anchors.ToArray(),
            Algorithm = BuildAlgorithm(),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        if (validateScientific)
        {
            found.AddRange(_engine.Validate(snapshot));
        }

        issues = found.Distinct(StringComparer.Ordinal).ToArray();
        return issues.Count == 0;
    }

    private KlaAlgorithmSettings BuildAlgorithm() => new()
    {
        SurfaceGridResolution = SurfaceGridResolution,
        GaussianSigmaGridCells = GaussianSigma,
        CloughTocherGradientTolerance = CloughTocherGradientTolerance,
        CloughTocherMaximumIterations = CloughTocherMaximumIterations,
        CandidateGridResolution = CandidateGridResolution,
        CandidateMinimum = CandidateMinimum,
        CandidateMaximum = CandidateMaximum,
        OdeMaximumStep = OdeMaximumStep,
        OdeRelativeTolerance = OdeRelativeTolerance,
        OdeAbsoluteTolerance = OdeAbsoluteTolerance,
        GradientTermination = GradientTermination,
        IntegrationHorizon = IntegrationHorizon,
    };

    private void LoadAlgorithm(KlaAlgorithmSettings settings)
    {
        SurfaceGridResolution = settings.SurfaceGridResolution;
        GaussianSigma = settings.GaussianSigmaGridCells;
        CloughTocherGradientTolerance = settings.CloughTocherGradientTolerance;
        CloughTocherMaximumIterations = settings.CloughTocherMaximumIterations;
        CandidateGridResolution = settings.CandidateGridResolution;
        CandidateMinimum = settings.CandidateMinimum;
        CandidateMaximum = settings.CandidateMaximum;
        OdeMaximumStep = settings.OdeMaximumStep;
        OdeRelativeTolerance = settings.OdeRelativeTolerance;
        OdeAbsoluteTolerance = settings.OdeAbsoluteTolerance;
        GradientTermination = settings.GradientTermination;
        IntegrationHorizon = settings.IntegrationHorizon;
    }

    private void LoadDocument(KlaExperimentDocument document)
    {
        _loading = true;
        var snapshot = document.Snapshot;
        ExperimentName = snapshot.Name;
        Broth = snapshot.Broth;
        RunCode = snapshot.RunCode;
        Notes = snapshot.Notes;
        ReviewNote = document.ReviewNote;
        AirflowMinimum = snapshot.Domain.AirflowMinimumLpm;
        AirflowMaximum = snapshot.Domain.AirflowMaximumLpm;
        AgitationMinimum = snapshot.Domain.AgitationMinimumRpm;
        AgitationMaximum = snapshot.Domain.AgitationMaximumRpm;
        LoadAlgorithm(snapshot.Algorithm);
        foreach (var row in Anchors)
        {
            row.PropertyChanged -= OnAnchorChanged;
        }

        Anchors.Clear();
        if (document.DraftRows.Length > 0)
        {
            foreach (var row in document.DraftRows)
            {
                AddRow(new KlaAnchorRowViewModel(row));
            }
        }
        else
        {
            foreach (var anchor in snapshot.Anchors)
            {
                AddRow(new KlaAnchorRowViewModel(anchor));
            }
        }

        Surface = null;
        PathResult = null;
        _resultInputFingerprint = null;
        Stage = document.Stage;
        HasUnsavedChanges = false;
        ValidationMessage = null;
        StatusMessage = document.Stage == KlaWorkflowStage.Published
            ? "Recibo publicado preservado. Reestime para iniciar uma nova versão."
            : "Experimento carregado; resultados não publicados são recalculados para evitar estado numérico obsoleto.";
        _loading = false;
        RefreshComputedProperties();
        VisualizationChanged?.Invoke();
    }

    private void ClearEditor()
    {
        _loading = true;
        ExperimentName = Broth = RunCode = Notes = ReviewNote = "";
        foreach (var row in Anchors)
        {
            row.PropertyChanged -= OnAnchorChanged;
        }

        Anchors.Clear();
        Surface = null;
        PathResult = null;
        Stage = KlaWorkflowStage.Draft;
        _loading = false;
        StatusMessage = "Nenhum mapa operacional foi instalado.";
        RefreshComputedProperties();
        VisualizationChanged?.Invoke();
    }

    private void AddRow(KlaAnchorRowViewModel row)
    {
        row.PropertyChanged += OnAnchorChanged;
        Anchors.Add(row);
    }

    private void OnAnchorChanged(object? sender, PropertyChangedEventArgs e)
        => InvalidateScientificResult();

    private void MetadataChanged()
    {
        if (_loading)
        {
            return;
        }

        HasUnsavedChanges = true;
        if (Stage == KlaWorkflowStage.Reviewed)
        {
            Stage = KlaWorkflowStage.PathValid;
        }
        else if (Stage == KlaWorkflowStage.Published)
        {
            Stage = PathResult is null ? KlaWorkflowStage.Draft : KlaWorkflowStage.PathValid;
        }

        UpdateCommands();
    }

    private void InvalidateScientificResult()
    {
        if (_loading)
        {
            return;
        }

        _workCancellation?.Cancel();
        Surface = null;
        PathResult = null;
        _resultInputFingerprint = null;
        Stage = KlaWorkflowStage.Draft;
        HasUnsavedChanges = true;
        StatusMessage = "Dados científicos alterados: superfície, trajetória e revisão foram invalidadas.";
        ValidationMessage = null;
        RefreshComputedProperties();
        VisualizationChanged?.Invoke();
    }

    private bool ResultIsCurrent()
        => _resultInputFingerprint is not null && CurrentFingerprintEquals(_resultInputFingerprint);

    private bool CurrentFingerprintEquals(string fingerprint)
        => TryBuildSnapshot(out var snapshot, out _) && snapshot.ScientificFingerprint() == fingerprint;

    private async Task PersistCurrentAsync(KlaExperimentSnapshot snapshot)
    {
        var document = new KlaExperimentDocument
        {
            Snapshot = snapshot,
            DraftRows = Anchors.Select(row => row.ToDraft()).ToArray(),
            Stage = Stage,
            LatestReceiptFingerprint = _documents.TryGetValue(snapshot.Id, out var prior)
                ? prior.LatestReceiptFingerprint
                : null,
            ReviewNote = ReviewNote,
        };
        _documents[snapshot.Id] = document;
        await _store.SaveExperimentAsync(document);
        HasUnsavedChanges = false;
        RebuildExperimentList(snapshot.Id);
    }

    private void RebuildExperimentList(Guid? keepSelected = null)
    {
        var selectedId = keepSelected ?? SelectedExperiment?.Id;
        Experiments.Clear();
        foreach (var document in _documents.Values
                     .OrderBy(item => item.Snapshot.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Experiments.Add(new KlaExperimentListItem(
                document.Snapshot.Id,
                document.Snapshot.Name,
                document.Stage));
        }

        if (selectedId is { } id)
        {
            _suppressSelectionLoad = true;
            SelectedExperiment = Experiments.FirstOrDefault(item => item.Id == id);
            _suppressSelectionLoad = false;
        }
    }

    private string UniqueName(string requested)
    {
        var names = _documents.Values.Select(item => item.Snapshot.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (!names.Contains(requested))
        {
            return requested;
        }

        var suffix = 2;
        while (names.Contains($"{requested} ({suffix})"))
        {
            suffix++;
        }

        return $"{requested} ({suffix})";
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    private void RefreshComputedProperties()
    {
        OnPropertyChanged(nameof(AlgorithmIdentity));
        OnPropertyChanged(nameof(IsPaperReference));
        OnPropertyChanged(nameof(SurfaceFingerprintShort));
        OnPropertyChanged(nameof(PathFingerprintShort));
        OnPropertyChanged(nameof(SurfaceRangeText));
        OnPropertyChanged(nameof(ResidualText));
        OnPropertyChanged(nameof(CoverageText));
        OnPropertyChanged(nameof(SelectedStartText));
        OnPropertyChanged(nameof(HeadroomText));
        OnPropertyChanged(nameof(PathSummaryText));
        OnPropertyChanged(nameof(NumericalParametersText));
        OnPropertyChanged(nameof(WarningText));
        UpdateCommands();
    }

    private void UpdateCommands()
    {
        OnPropertyChanged(nameof(CanEstimate));
        OnPropertyChanged(nameof(CanCalculatePath));
        OnPropertyChanged(nameof(CanReview));
        OnPropertyChanged(nameof(CanPublish));
        EstimateSurfaceCommand.NotifyCanExecuteChanged();
        CalculatePathCommand.NotifyCanExecuteChanged();
        MarkReviewedCommand.NotifyCanExecuteChanged();
        PublishCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        _workCancellation?.Cancel();
        _workCancellation?.Dispose();
        foreach (var row in Anchors)
        {
            row.PropertyChanged -= OnAnchorChanged;
        }
    }
}
