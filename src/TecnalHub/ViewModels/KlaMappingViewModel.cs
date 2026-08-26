using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TecnalHub.Services.Dialogs;
using TecnalHub.Services.KlaMapping;
using TecnalHub.Services.KlaTesting;
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
    [NotifyPropertyChangedFor(nameof(AirflowValue))]
    public partial string Airflow { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgitationValue))]
    public partial string Agitation { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KlaValue))]
    public partial string Kla { get; set; } = "";

    public double? AirflowValue => TryParse(Airflow, out var val) ? val : null;

    public double? AgitationValue => TryParse(Agitation, out var val) ? val : null;

    public double? KlaValue => TryParse(Kla, out var val) ? val : null;

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

public sealed partial class KlaTestImportCandidateViewModel : ObservableObject
{
    public required KlaTestRunSummary Run { get; init; }
    public required KlaAnalysisRevision Analysis { get; init; }
    public bool AlreadyImported { get; init; }
    [ObservableProperty] public partial bool IsSelected { get; set; }
    public string Condition => $"{Run.AgitationRpm:F0} rpm · {Run.AirflowLpm:F2} L/min";
    public string Result => $"Rep {Run.ReplicateNumber} · kLa {Analysis.KlaPerHour:F2} h⁻¹ · R² {Analysis.AnalysisR2:F4} · rev {Analysis.RevisionNumber}";
    public string ImportStatus => AlreadyImported ? "Já importada" : Analysis.Quality == DecisionQuality.AcceptableWithWarning ? "Aceita com aviso" : "Aceita";
}

public sealed record KlaExperimentListItem(Guid Id, string Name, KlaWorkflowStage Stage, bool IsAvailableForControl = false)
{
    public string StageLabel => IsAvailableForControl || Stage == KlaWorkflowStage.Published
        ? "Publicada"
        : Stage switch
        {
            KlaWorkflowStage.SurfaceEstimated => "Superfície",
            KlaWorkflowStage.PathValid => "Trajetória",
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
    private readonly IKlaTestStore? _testStore;
    private readonly IFileInteractionService _files;
    private readonly IDialogService _dialogs;
    private readonly IEventJournal _journal;
    private readonly Dictionary<Guid, KlaExperimentDocument> _documents = [];
    private readonly List<KlaImportedMeasurement> _importedMeasurements = [];

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
        IEventJournal journal,
        IKlaTestStore? testStore = null)
    {
        _engine = engine;
        _store = store;
        _files = files;
        _dialogs = dialogs;
        _journal = journal;
        _testStore = testStore;
    }

    public event Action? VisualizationChanged;

    public ObservableCollection<KlaExperimentListItem> Experiments { get; } = [];

    public ObservableCollection<KlaAnchorRowViewModel> Anchors { get; } = [];

    public ObservableCollection<KlaPublishedListItem> PublishedProfiles { get; } = [];

    public ObservableCollection<KlaTestSummary> AvailableKlaTests { get; } = [];
    public ObservableCollection<KlaTestImportCandidateViewModel> KlaTestImportCandidates { get; } = [];

    public KlaSurface? Surface { get; private set; }

    public KlaPathResult? PathResult { get; private set; }

    [ObservableProperty]
    public partial KlaTestSummary? SelectedKlaTestForImport { get; set; }

    [ObservableProperty]
    public partial bool IsImportFromTestDialogOpen { get; set; }

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
        KlaWorkflowStage.PathValid => "3 · Trajetória calculada",
        KlaWorkflowStage.Published => "4 · Publicada para controle",
        _ => "1 · Rascunho experimental",
    };

    public string ExperimentFilePath => SelectedExperiment is not null
        ? _store.GetExperimentFilePath(SelectedExperiment.Id)
        : "";

    public bool IsPublishedForControl => _documents.TryGetValue(SelectedExperiment?.Id ?? Guid.Empty, out var doc) &&
        (doc.IsAvailableForControl || doc.Stage == KlaWorkflowStage.Published);

    public string PublishedStatusText => IsPublishedForControl
        ? "Disponível para controle"
        : "Não publicado para controle";

    public string LastPublishedDateText => _documents.TryGetValue(SelectedExperiment?.Id ?? Guid.Empty, out var doc) && doc.LastPublishedAtUtc is { } dt
        ? dt.ToLocalTime().ToString("dd/MM/yyyy HH:mm")
        : "—";

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
                warnings.Add("Parâmetros personalizados: restaure a referência antes da publicação como método do artigo.");
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

    public bool CanPublishForControl => HasExperiment && !IsBusy && PathResult is not null && Surface is not null && ResultIsCurrent();

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
                _documents[document.Snapshot.Id] = document;
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
            !_dialogs.Confirm(
                "Excluir experimento kLa",
                $"O experimento “{selected.Name}” será removido do disco.",
                confirmText: "Excluir",
                cancelText: "Cancelar",
                isDanger: true))
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
            SurfaceData = Surface is not null ? new KlaSurfaceData(Surface.Diagnostics, Surface.Fingerprint) : null,
            PathData = PathResult is not null ? new KlaPathData(
                PathResult.Path.ToArray(),
                PathResult.Allocation.ToArray(),
                PathResult.HeadroomScores.ToArray(),
                PathResult.HeadroomResolution,
                PathResult.Diagnostics,
                PathResult.SourceSurfaceFingerprint,
                PathResult.Fingerprint) : null,
        };
        _documents[copy.Id] = document;
        await _store.SaveExperimentAsync(document);
        RebuildExperimentList();
        SelectedExperiment = Experiments.Single(item => item.Id == copy.Id);
    }

    [RelayCommand]
    private async Task ImportExperimentAsync()
    {
        var path = _files.ChooseOpenPath(
            "Importar experimento kLa",
            "Experimento kLa (*.kla.json)|*.kla.json|JSON (*.json)|*.json",
            ".kla.json");
        if (path is null)
        {
            return;
        }

        try
        {
            var document = await _store.ImportExperimentAsync(path);
            _documents[document.Snapshot.Id] = document;
            RebuildExperimentList(document.Snapshot.Id);
            SelectedExperiment = Experiments.FirstOrDefault(e => e.Id == document.Snapshot.Id);
            StatusMessage = $"Experimento “{document.Snapshot.Name}” importado com sucesso.";
        }
        catch (Exception exception)
        {
            ValidationMessage = "Importação recusada: " + exception.Message;
        }
    }

    [RelayCommand]
    private async Task ExportExperimentAsync()
    {
        if (SelectedExperiment is not { } selected)
        {
            return;
        }

        var destination = _files.ChooseSavePath(
            "Exportar experimento kLa",
            $"{SafeFileName(selected.Name)}.kla.json",
            "Experimento kLa (*.kla.json)|*.kla.json",
            ".kla.json");
        if (destination is not null)
        {
            await _store.ExportExperimentAsync(selected.Id, destination);
            StatusMessage = $"Experimento exportado para {Path.GetFileName(destination)}.";
        }
    }

    [RelayCommand]
    private void CopyFilePath()
    {
        var path = ExperimentFilePath;
        if (!string.IsNullOrWhiteSpace(path))
        {
            _files.CopyText(path);
            StatusMessage = "Caminho do arquivo copiado para a área de transferência.";
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var path = ExperimentFilePath;
        if (!string.IsNullOrWhiteSpace(path))
        {
            _files.OpenFolder(path);
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
        var agitation = new[] { AgitationMinimum, (AgitationMinimum + AgitationMaximum) / 2, AgitationMaximum };
        foreach (var n in agitation)
        {
            foreach (var q in airflow)
            {
                AddRow(new KlaAnchorRowViewModel
                {
                    Agitation = n.ToString("G12", CultureInfo.CurrentCulture),
                    Airflow = q.ToString("G12", CultureInfo.CurrentCulture),
                    Kla = "",
                });
            }
        }

        InvalidateScientificResult();
        StatusMessage = "Desenho 3² criado com kLa em branco; nenhum valor do artigo foi carregado.";
    }

    [RelayCommand]
    public void SortAnchors()
    {
        var sorted = Anchors
            .OrderBy(a => a.AgitationValue is null ? 1 : 0)
            .ThenBy(a => a.AgitationValue ?? double.MaxValue)
            .ThenBy(a => a.AirflowValue is null ? 1 : 0)
            .ThenBy(a => a.AirflowValue ?? double.MaxValue)
            .ThenBy(a => a.KlaValue is null ? 1 : 0)
            .ThenBy(a => a.KlaValue ?? double.MaxValue)
            .ToList();

        for (var i = 0; i < sorted.Count; i++)
        {
            var oldIndex = Anchors.IndexOf(sorted[i]);
            if (oldIndex != i)
            {
                Anchors.Move(oldIndex, i);
            }
        }
    }

    [RelayCommand]
    public void OpenImportFromTestDialog()
    {
        AvailableKlaTests.Clear();
        if (_testStore is not null)
        {
            foreach (var t in _testStore.ListTests())
            {
                AvailableKlaTests.Add(t);
            }
        }

        SelectedKlaTestForImport = AvailableKlaTests.FirstOrDefault();
        IsImportFromTestDialogOpen = true;
    }

    [RelayCommand]
    public void CloseImportFromTestDialog()
    {
        IsImportFromTestDialogOpen = false;
    }

    partial void OnSelectedKlaTestForImportChanged(KlaTestSummary? value)
    {
        KlaTestImportCandidates.Clear();
        if (value is null || _testStore?.LoadTest(value.FolderName) is not { } doc) return;
        foreach (var run in doc.Runs.Where(r => r.Phase == RunPhase.Accepted))
        {
            var analysis = _testStore.LoadRunAnalysis(doc.FolderName, run.FolderName);
            if (analysis is null || analysis.Quality == DecisionQuality.Inconclusive || analysis.KlaPerHour <= 0) continue;
            var imported = _importedMeasurements.Any(m => m.SourceTestId == doc.TestId && m.SourceRunId == run.RunId && m.SourceAnalysisRevision == analysis.RevisionNumber);
            KlaTestImportCandidates.Add(new KlaTestImportCandidateViewModel { Run = run, Analysis = analysis, AlreadyImported = imported, IsSelected = !imported });
        }
    }

    [RelayCommand]
    public void ImportSelectedKlaTest()
    {
        if (SelectedKlaTestForImport is null || _testStore is null)
        {
            return;
        }

        var doc = _testStore.LoadTest(SelectedKlaTestForImport.FolderName);
        if (doc is null)
        {
            _dialogs.Confirm("Erro", "Não foi possível carregar o teste selecionado.", "OK", "");
            return;
        }

        var acceptedRuns = KlaTestImportCandidates.Where(c => c.IsSelected && !c.AlreadyImported).ToList();

        if (acceptedRuns.Count == 0)
        {
            _dialogs.Confirm("Aviso", $"Nenhuma corrida aceita encontrada no teste '{doc.Name}'.", "OK", "");
            return;
        }

        var importedCount = 0;
        foreach (var candidate in acceptedRuns)
        {
            var run = candidate.Run;
            var analysis = candidate.Analysis;
            if (_importedMeasurements.Any(m => m.SourceTestId == doc.TestId && m.SourceRunId == run.RunId &&
                                                m.SourceAnalysisRevision == analysis.RevisionNumber))
            {
                continue;
            }

            _importedMeasurements.Add(new KlaImportedMeasurement
            {
                SourceTestId = doc.TestId,
                SourceRunId = run.RunId,
                SourceAnalysisRevision = analysis.RevisionNumber,
                AirflowLpm = run.AirflowLpm,
                AgitationRpm = run.AgitationRpm,
                KlaPerHour = analysis.KlaPerHour,
                SlopeStandardError = analysis.SlopeStandardError,
                ConfidenceInterval95Low = analysis.ConfidenceInterval95Low,
                ConfidenceInterval95High = analysis.ConfidenceInterval95High,
                AnalysisR2 = analysis.AnalysisR2,
                RawRelativePath = Path.Combine("Testes-kLa", doc.FolderName, KlaTestFileContracts.RunsDirectoryName, run.FolderName, KlaTestFileContracts.RunRawDataFileName),
                AnalysisRelativePath = Path.Combine("Testes-kLa", doc.FolderName, KlaTestFileContracts.RunsDirectoryName, run.FolderName, $"analise-rev-{analysis.RevisionNumber:D3}.json"),
                RawSha256 = analysis.RawDataSha256,
            });
            importedCount++;
        }

        foreach (var group in _importedMeasurements.Where(m => m.Included)
                     .GroupBy(m => (N: Math.Round(m.AgitationRpm, 1), Q: Math.Round(m.AirflowLpm, 2))))
        {
            var existing = Anchors.FirstOrDefault(a => a.AgitationValue.HasValue && a.AirflowValue.HasValue &&
                Math.Abs(a.AgitationValue.Value - group.Key.N) < 0.1 && Math.Abs(a.AirflowValue.Value - group.Key.Q) < 0.05);
            var mean = group.Average(m => m.KlaPerHour).ToString("G12", CultureInfo.CurrentCulture);
            if (existing is null)
            {
                AddRow(new KlaAnchorRowViewModel { Agitation = group.Key.N.ToString("G12", CultureInfo.CurrentCulture), Airflow = group.Key.Q.ToString("G12", CultureInfo.CurrentCulture), Kla = mean });
            }
            else
            {
                existing.Kla = mean;
            }
        }

        InvalidateScientificResult();
        IsImportFromTestDialogOpen = false;
        StatusMessage = $"{importedCount} replicatas aceitas importadas; pontos do mapa agregados por N e Q.";
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
        StatusMessage = "Prévia rápida selecionada. Restaure os parâmetros do artigo antes de publicar.";
    }

    [RelayCommand]
    private async Task SaveExperimentAsync()
    {
        if (!TryBuildSnapshot(out var snapshot, out var issues, validateScientific: false))
        {
            ValidationMessage = issues[0];
            return;
        }

        SortAnchors();
        await PersistCurrentAsync(snapshot);
        StatusMessage = "Experimento salvo em disco.";
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

        SortAnchors();

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
                StatusMessage = "Trajetória calculada e válida, orientada de baixo para alto kLa.";
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

    [RelayCommand(CanExecute = nameof(CanPublishForControl))]
    private async Task PublishForControlAsync()
    {
        if (Surface is not { } surface || PathResult is not { } path)
        {
            ValidationMessage = "Calcule uma superfície e trajetória válidas antes de publicar para controle.";
            return;
        }

        if (!TryBuildSnapshot(out var snapshot, out var issues))
        {
            ValidationMessage = issues.FirstOrDefault() ?? "O experimento atual é inválido.";
            return;
        }

        try
        {
            var document = new KlaExperimentDocument
            {
                Snapshot = snapshot,
                DraftRows = Anchors.Select(row => row.ToDraft()).ToArray(),
                Stage = KlaWorkflowStage.Published,
                IsAvailableForControl = true,
                LastPublishedAtUtc = DateTimeOffset.UtcNow,
                SurfaceData = new KlaSurfaceData(surface.Diagnostics, surface.Fingerprint),
                PathData = new KlaPathData(
                    path.Path.ToArray(),
                    path.Allocation.ToArray(),
                    path.HeadroomScores.ToArray(),
                    path.HeadroomResolution,
                    path.Diagnostics,
                    path.SourceSurfaceFingerprint,
                    path.Fingerprint),
            };

            var profile = await _store.PublishAsync(document);
            _documents[snapshot.Id] = document;
            Stage = KlaWorkflowStage.Published;
            HasUnsavedChanges = false;
            RebuildExperimentList(snapshot.Id);
            _journal.Add(
                AuditSource.Application,
                AuditSeverity.Information,
                $"Perfil kLa publicado para controle: {profile.Payload.Name}.",
                $"Superfície {surface.Fingerprint}; trajetória {path.Fingerprint}.");
            StatusMessage = $"Experimento publicado para controle com sucesso em {DateTime.Now:HH:mm:ss}.";
            RefreshComputedProperties();
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
            MeasurementFingerprint = _importedMeasurements.Count == 0 ? "" : KlaFingerprint.ForObject(_importedMeasurements
                .OrderBy(m => m.SourceTestId).ThenBy(m => m.SourceRunId).ThenBy(m => m.SourceAnalysisRevision)
                .Select(m => new { m.SourceTestId, m.SourceRunId, m.SourceAnalysisRevision, m.KlaPerHour, m.Included, m.ExclusionReason })
                .ToArray()),
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
        _importedMeasurements.Clear();
        _importedMeasurements.AddRange(document.ImportedMeasurements);
        ExperimentName = snapshot.Name;
        Broth = snapshot.Broth;
        RunCode = snapshot.RunCode;
        Notes = snapshot.Notes;
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

        SortAnchors();

        Surface = null;
        PathResult = null;
        _resultInputFingerprint = null;

        if (snapshot.Anchors.Length >= 6 && _engine.Validate(snapshot).Count == 0)
        {
            try
            {
                Surface = _engine.Reconstruct(snapshot);
                _resultInputFingerprint = snapshot.ScientificFingerprint();
            }
            catch
            {
                Surface = null;
            }
        }

        if (document.PathData is { } pd && pd.Allocation.Length > 0)
        {
            PathResult = new KlaPathResult(
                pd.Path,
                pd.Allocation,
                pd.HeadroomScores,
                pd.HeadroomResolution,
                pd.Diagnostics,
                pd.SourceSurfaceFingerprint,
                pd.Fingerprint);
        }

        Stage = document.Stage;
        HasUnsavedChanges = false;
        ValidationMessage = null;
        StatusMessage = document.IsAvailableForControl || document.Stage == KlaWorkflowStage.Published
            ? "Experimento publicado para controle carregado."
            : "Experimento carregado.";
        _loading = false;
        RefreshComputedProperties();
        VisualizationChanged?.Invoke();
    }

    private void ClearEditor()
    {
        _loading = true;
        ExperimentName = Broth = RunCode = Notes = "";
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
        if (Stage == KlaWorkflowStage.Published)
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
        StatusMessage = "Dados científicos alterados: superfície e trajetória foram invalidadas.";
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
        KlaSurfaceData? surfaceData = Surface is not null
            ? new KlaSurfaceData(Surface.Diagnostics, Surface.Fingerprint)
            : _documents.TryGetValue(snapshot.Id, out var existingDoc) ? existingDoc.SurfaceData : null;

        KlaPathData? pathData = PathResult is not null
            ? new KlaPathData(
                PathResult.Path.ToArray(),
                PathResult.Allocation.ToArray(),
                PathResult.HeadroomScores.ToArray(),
                PathResult.HeadroomResolution,
                PathResult.Diagnostics,
                PathResult.SourceSurfaceFingerprint,
                PathResult.Fingerprint)
            : _documents.TryGetValue(snapshot.Id, out var existingDoc2) ? existingDoc2.PathData : null;

        var isPublished = _documents.TryGetValue(snapshot.Id, out var prev) && prev.IsAvailableForControl;
        var publishedAt = _documents.TryGetValue(snapshot.Id, out var prev2) ? prev2.LastPublishedAtUtc : null;

        var document = new KlaExperimentDocument
        {
            Snapshot = snapshot,
            DraftRows = Anchors.Select(row => row.ToDraft()).ToArray(),
            Stage = Stage,
            IsAvailableForControl = isPublished || Stage == KlaWorkflowStage.Published,
            LastPublishedAtUtc = publishedAt,
            SurfaceData = surfaceData,
            PathData = pathData,
            ImportedMeasurements = _importedMeasurements.ToArray(),
        };
        _documents[snapshot.Id] = document;
        await _store.SaveExperimentAsync(document);
        HasUnsavedChanges = false;
        RebuildExperimentList(snapshot.Id);
        RefreshComputedProperties();
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
                document.Stage,
                document.IsAvailableForControl));
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
        OnPropertyChanged(nameof(ExperimentFilePath));
        OnPropertyChanged(nameof(IsPublishedForControl));
        OnPropertyChanged(nameof(PublishedStatusText));
        OnPropertyChanged(nameof(LastPublishedDateText));
        UpdateCommands();
    }

    private void UpdateCommands()
    {
        OnPropertyChanged(nameof(CanEstimate));
        OnPropertyChanged(nameof(CanCalculatePath));
        OnPropertyChanged(nameof(CanPublishForControl));
        EstimateSurfaceCommand.NotifyCanExecuteChanged();
        CalculatePathCommand.NotifyCanExecuteChanged();
        PublishForControlCommand.NotifyCanExecuteChanged();
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
