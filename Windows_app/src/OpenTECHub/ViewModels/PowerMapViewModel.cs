using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTECHub.Services.Dialogs;
using OpenTECHub.Services.KlaMapping;
using OpenTECHub.Services.Platform;
using OpenTECHub.Services.PowerMapping;
using OpenTECHub.Services.PowerTesting;
using OpenTECHub.Services.Telemetry;

namespace OpenTECHub.ViewModels;

public enum PowerMapLayer
{
    VolumetricPower,
    NetPower,
    PowerRatio,
    Efficiency,
}

public enum PowerMapColormap
{
    Viridis,
    Magma,
    Turbo,
}

public sealed partial class PowerTestSourceItemViewModel : ObservableObject
{
    public PowerTestSummary Summary { get; }
    public string DisplayText => $"{Summary.Name} ({Summary.AcceptedRunCount} ensaios aceitos)";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public PowerTestSourceItemViewModel(PowerTestSummary summary, bool isSelected = false)
    {
        Summary = summary;
        IsSelected = isSelected;
    }
}

public sealed record KlaMapOptionViewModel(Guid Id, string Name, int AnchorCount)
{
    public string DisplayText => $"{Name} ({AnchorCount} âncoras)";
}

/// <summary>One selectable surface layer, with the unit the colour bar has to announce.</summary>
public sealed record PowerMapLayerOption(PowerMapLayer Layer, string DisplayText, string Unit, string ColorBarLabel);

public sealed record PowerMapColormapOption(PowerMapColormap Colormap, string DisplayText);

/// <summary>
/// Routed Phase 3 ViewModel managing 2D power surface synthesis, layer selection,
/// continuous flooding boundaries, cursor inspection, and kLa/van 't Riet coupling.
/// </summary>
public sealed partial class PowerMapViewModel : ObservableObject, IDisposable
{
    private readonly IPowerTestStore _testStore;
    private readonly IPowerMapStore _mapStore;
    private readonly IPowerMapEngine _engine;
    private readonly IKlaProfileStore _klaStore;
    private readonly IKlaMappingEngine _klaMappingEngine;
    private readonly IKlaPowerIntegrationService? _integrationService;
    private readonly IDialogService? _dialogs;
    private readonly IEventJournal? _journal;

    private CancellationTokenSource? _reconstructionCts;
    private bool _initialized;
    private double? _lastInspectedRpm;
    private double? _lastInspectedFlow;
    private int _reconstructionGeneration;
    private bool _suppressStale;
    private readonly IPowerAnalysisEngine _analysisEngine;
    private PowerTestViewModel? _powerTestViewModel;

    public PowerMapViewModel(
        IPowerTestStore testStore,
        IPowerMapStore mapStore,
        IPowerMapEngine engine,
        IKlaProfileStore klaStore,
        IKlaPowerIntegrationService? integrationService = null,
        IDialogService? dialogs = null,
        IEventJournal? journal = null,
        IPowerAnalysisEngine? analysisEngine = null,
        IKlaMappingEngine? klaMappingEngine = null)
    {
        _testStore = testStore ?? throw new ArgumentNullException(nameof(testStore));
        _mapStore = mapStore ?? throw new ArgumentNullException(nameof(mapStore));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _klaStore = klaStore ?? throw new ArgumentNullException(nameof(klaStore));
        _klaMappingEngine = klaMappingEngine ?? new KlaMappingEngine();
        _integrationService = integrationService;
        _dialogs = dialogs;
        _journal = journal;

        TestRootDirectory = _testStore.RootDirectory;
        MapRootDirectory = _mapStore.RootDirectory;

        // The impeller benchmarking lives on this page (§10 keeps the shell at two power
        // destinations), so the map owns it rather than the shell routing a third one.
        _analysisEngine = analysisEngine ?? new PowerAnalysisEngine();
        Comparison = new PowerImpellerComparisonViewModel(_testStore, _mapStore, _analysisEngine);
    }

    /// <summary>Multi-assay impeller benchmarking shown alongside the surface (§18.3 step 6).</summary>
    public PowerImpellerComparisonViewModel Comparison { get; }

    /// <summary>Resumo da comparação kLa/PV exibido junto às fontes do mapa.</summary>
    public bool HasLinkedKlaMap => _powerTestViewModel?.HasLinkedKlaMap ?? false;
    public string LinkedKlaMapName => _powerTestViewModel?.LinkedKlaMapName ?? "";
    public string ControlRegionSummary => _powerTestViewModel?.ControlRegionSummary ?? "Nenhum mapa de kLa vinculado.";
    public double? AverageKlaEfficiency => _powerTestViewModel?.AverageKlaEfficiency;
    public ObservableCollection<KlaEfficiencyComparisonItem> KlaEfficiencyItems =>
        _powerTestViewModel?.KlaEfficiencyItems ?? _emptyKlaEfficiencyItems;
    private static readonly ObservableCollection<KlaEfficiencyComparisonItem> _emptyKlaEfficiencyItems = [];

    public void AttachPowerTestViewModel(PowerTestViewModel viewModel)
    {
        if (ReferenceEquals(_powerTestViewModel, viewModel))
        {
            return;
        }

        if (_powerTestViewModel is not null)
        {
            _powerTestViewModel.PropertyChanged -= OnPowerTestPropertyChanged;
        }

        _powerTestViewModel = viewModel;
        _powerTestViewModel.PropertyChanged += OnPowerTestPropertyChanged;
        OnPropertyChanged(nameof(HasLinkedKlaMap));
        OnPropertyChanged(nameof(LinkedKlaMapName));
        OnPropertyChanged(nameof(ControlRegionSummary));
        OnPropertyChanged(nameof(AverageKlaEfficiency));
        OnPropertyChanged(nameof(KlaEfficiencyItems));
    }

    private void OnPowerTestPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PowerTestViewModel.HasLinkedKlaMap): OnPropertyChanged(nameof(HasLinkedKlaMap)); break;
            case nameof(PowerTestViewModel.LinkedKlaMapName): OnPropertyChanged(nameof(LinkedKlaMapName)); break;
            case nameof(PowerTestViewModel.ControlRegionSummary): OnPropertyChanged(nameof(ControlRegionSummary)); break;
            case nameof(PowerTestViewModel.AverageKlaEfficiency): OnPropertyChanged(nameof(AverageKlaEfficiency)); break;
            case nameof(PowerTestViewModel.KlaEfficiencyItems): OnPropertyChanged(nameof(KlaEfficiencyItems)); break;
        }
    }

    public string TestRootDirectory { get; }

    public string MapRootDirectory { get; }

    public event Action? VisualizationChanged;

    // Collections
    public ObservableCollection<PowerMapSummary> AvailableMaps { get; } = [];

    public ObservableCollection<PowerTestSourceItemViewModel> AvailablePowerTests { get; } = [];

    public ObservableCollection<KlaMapOptionViewModel> AvailableKlaMaps { get; } = [];

    public ObservableCollection<KlaPowerPair> MatchedPairs { get; } = [];

    public IReadOnlyList<PowerMapLayerOption> AvailableLayers { get; } =
    [
        new(PowerMapLayer.VolumetricPower, "Potência específica (P/V)", "W/m³", "P/V (W/m³)"),
        new(PowerMapLayer.NetPower, "Potência de eixo (P_líq)", "W", "P_líq (W)"),
        new(PowerMapLayer.PowerRatio, "Razão de aeração (P_G/P₀)", "–", "P_G/P₀ (–)"),
        new(PowerMapLayer.Efficiency, "Eficiência kLa/(P/V)", "h⁻¹/(W/m³)", "Eficiência kLa/(P/V)"),
    ];

    public IReadOnlyList<PowerMapColormapOption> AvailableColormaps { get; } =
    [
        new(PowerMapColormap.Viridis, "Viridis"),
        new(PowerMapColormap.Magma, "Magma"),
        new(PowerMapColormap.Turbo, "Turbo"),
    ];

    // Current document & selection
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMap))]
    public partial PowerMapDocument? CurrentDocument { get; set; }

    [ObservableProperty]
    public partial PowerMapSummary? SelectedMapSummary { get; set; }

    [ObservableProperty]
    public partial KlaMapOptionViewModel? SelectedKlaMapOption { get; set; }

    [ObservableProperty]
    public partial string MapName { get; set; } = "";

    [ObservableProperty]
    public partial string NewMapName { get; set; } = "Novo Mapa de Potência";

    [ObservableProperty]
    public partial string Notes { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSurface))]
    public partial PowerMapSurfaceData? CurrentSurfaceData { get; set; }

    [ObservableProperty]
    public partial PowerMapFloodingBoundary? CurrentFloodingBoundary { get; set; }


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCorrelation))]
    public partial KlaCorrelationResult? CurrentCorrelation { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSurfaceIntersection))]
    [NotifyPropertyChangedFor(nameof(CanFitSurfaceIntersection))]
    public partial SurfaceIntersectionResult? CurrentSurfaceIntersection { get; set; }

    // Layer and visual controls
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColorBarLabel))]
    [NotifyPropertyChangedFor(nameof(SelectedLayerUnit))]
    public partial PowerMapLayer SelectedLayer { get; set; } = PowerMapLayer.VolumetricPower;

    [ObservableProperty]
    public partial PowerMapColormap SelectedColormap { get; set; } = PowerMapColormap.Viridis;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GridResolutionLabel))]
    public partial int GridResolution { get; set; } = 150;

    /// <summary>Clough-Tocher gradient tolerance, exposed so a noisy anchor set can be loosened (§18.3 step 5.1).</summary>
    [ObservableProperty]
    public partial double GradientTolerance { get; set; } = 1e-6;

    [ObservableProperty]
    public partial int GradientIterations { get; set; } = 400;

    [ObservableProperty]
    public partial bool AutoScale { get; set; } = true;

    [ObservableProperty]
    public partial double? ManualScaleMin { get; set; }

    [ObservableProperty]
    public partial double? ManualScaleMax { get; set; }

    [ObservableProperty]
    public partial double ContrastPercent { get; set; } = 100.0;

    [ObservableProperty]
    public partial bool ShowAnchors { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowIsolines { get; set; } = true;

    // Inspection coordinates & values
    [ObservableProperty]
    public partial double? InspectedAgitationRpm { get; set; }

    [ObservableProperty]
    public partial double? InspectedGasFlowLpm { get; set; }

    [ObservableProperty]
    public partial double? InspectedGasFlowVvm { get; set; }

    [ObservableProperty]
    public partial double? InspectedSuperficialVelocityMs { get; set; }

    [ObservableProperty]
    public partial double? InspectedLayerValue { get; set; }

    [ObservableProperty]
    public partial string InspectedLayerValueFormatted { get; set; } = "—";

    [ObservableProperty]
    public partial string InspectedFlowRegime { get; set; } = "—";

    [ObservableProperty]
    public partial bool IsInspectedFlooded { get; set; }

    // van 't Riet display parameters
    [ObservableProperty]
    public partial string VanTRietKText { get; set; } = "—";

    [ObservableProperty]
    public partial string VanTRietAlphaText { get; set; } = "—";

    [ObservableProperty]
    public partial string VanTRietBetaText { get; set; } = "—";

    [ObservableProperty]
    public partial string VanTRietR2Text { get; set; } = "—";

    [ObservableProperty]
    public partial string VanTRietFormulaText { get; set; } = "kLa = K · (P/V)^α · (v_s)^β";

    [ObservableProperty]
    public partial int MatchedPairsCount { get; set; }

    [ObservableProperty]
    public partial string CommonDomainText { get; set; } = "—";

    [ObservableProperty]
    public partial string IntersectionCoverageText { get; set; } = "—";

    [ObservableProperty]
    public partial string EfficiencyMinimumText { get; set; } = "—";

    [ObservableProperty]
    public partial string EfficiencyMeanText { get; set; } = "—";

    [ObservableProperty]
    public partial string EfficiencyMaximumText { get; set; } = "—";

    [ObservableProperty]
    public partial string EfficiencyMaximumPointText { get; set; } = "—";

    [ObservableProperty]
    public partial string IntersectionStatusText { get; set; } = "Nenhuma intersecção calculada.";

    // Status and progress
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; set; }

    public bool IsIdle => !IsBusy;

    public bool HasMap => CurrentDocument is not null;

    public bool HasSurface => CurrentSurfaceData is not null;

    public bool HasCorrelation => CurrentCorrelation?.HasFit == true;

    public bool HasMatchedPairs => MatchedPairs.Count > 0;

    public bool HasSurfaceIntersection => CurrentSurfaceIntersection?.ValidPointCount > 0;

    public bool CanFitSurfaceIntersection => CurrentSurfaceIntersection?.ValidPointCount >= 4;

    /// <summary>Colour-bar caption for the layer on screen; the plot must never show a bare number.</summary>
    public string ColorBarLabel =>
        AvailableLayers.FirstOrDefault(l => l.Layer == SelectedLayer)?.ColorBarLabel ?? "";

    public string SelectedLayerUnit =>
        AvailableLayers.FirstOrDefault(l => l.Layer == SelectedLayer)?.Unit ?? "";

    public string GridResolutionLabel => $"{GridResolution}×{GridResolution}";

    /// <summary>
    /// True when the grid on screen no longer reflects the current selection or resolution.
    /// The surface is expensive, so it is not recomputed on every keystroke - the operator is told
    /// instead, and asks for it.
    /// </summary>
    [ObservableProperty]
    public partial bool IsSurfaceStale { get; set; }

    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    [ObservableProperty]
    public partial string ProgressText { get; set; } = "";

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Selecione ou crie um mapa de potência para começar.";

    [ObservableProperty]
    public partial string? ValidationMessage { get; set; }

    partial void OnSelectedLayerChanged(PowerMapLayer value)
    {
        RefreshInspectionForCurrentLayer();
        VisualizationChanged?.Invoke();
    }

    partial void OnSelectedKlaMapOptionChanged(KlaMapOptionViewModel? value)
    {
        InvalidateSurfaceIntersection();
    }

    partial void OnCurrentSurfaceDataChanged(PowerMapSurfaceData? value)
    {
        if (value is not null)
        {
            InvalidateSurfaceIntersection();
        }
    }

    partial void OnSelectedColormapChanged(PowerMapColormap value) => VisualizationChanged?.Invoke();
    partial void OnShowAnchorsChanged(bool value) => VisualizationChanged?.Invoke();
    partial void OnShowIsolinesChanged(bool value) => VisualizationChanged?.Invoke();
    partial void OnAutoScaleChanged(bool value) => VisualizationChanged?.Invoke();
    partial void OnManualScaleMinChanged(double? value) => VisualizationChanged?.Invoke();
    partial void OnManualScaleMaxChanged(double? value) => VisualizationChanged?.Invoke();
    partial void OnContrastPercentChanged(double value) => VisualizationChanged?.Invoke();

    partial void OnGradientToleranceChanged(double value) => MarkSurfaceStale();

    partial void OnGradientIterationsChanged(int value) => MarkSurfaceStale();

    partial void OnGridResolutionChanged(int value)
    {
        if (CurrentSurfaceData is { } surface && surface.ResolutionN != Math.Clamp(value, 50, 300))
        {
            MarkSurfaceStale();
        }
    }

    /// <summary>
    /// Abandons a grid computation in flight. Bumping the generation is what stops a late result
    /// from being applied to a map the operator has already left or deleted.
    /// </summary>
    private void CancelReconstruction()
    {
        _reconstructionGeneration++;
        _reconstructionCts?.Cancel();
        IsBusy = false;
    }

    /// <summary>Flags the drawn grid as out of date and says why, without recomputing behind the operator.</summary>
    public void MarkSurfaceStale()
    {
        if (CurrentSurfaceData is null)
        {
            return;
        }

        IsSurfaceStale = true;
        InvalidateSurfaceIntersection();
    }

    private void InvalidateSurfaceIntersection()
    {
        if (CurrentSurfaceIntersection is null && CurrentCorrelation is null && MatchedPairs.Count == 0)
        {
            return;
        }

        CurrentSurfaceIntersection = null;
        CurrentCorrelation = null;
        MatchedPairs.Clear();
        MatchedPairsCount = 0;
        OnPropertyChanged(nameof(HasMatchedPairs));
        ResetCorrelationDisplay();
        CommonDomainText = "—";
        IntersectionCoverageText = "—";
        EfficiencyMinimumText = "—";
        EfficiencyMeanText = "—";
        EfficiencyMaximumText = "—";
        EfficiencyMaximumPointText = "—";
        IntersectionStatusText = "Intersecção desatualizada; recalcule após alterar a superfície ou o mapa kLa.";
        VisualizationChanged?.Invoke();
    }

    private void ResetCorrelationDisplay()
    {
        VanTRietKText = "—";
        VanTRietAlphaText = "—";
        VanTRietBetaText = "—";
        VanTRietR2Text = "—";
        VanTRietFormulaText = "kLa = K · (P/V)^α · (v_s)^β";
    }

    partial void OnSelectedMapSummaryChanged(PowerMapSummary? value)
    {
        if (value is not null)
        {
            LoadMap(value.FolderName);
        }
    }

    [RelayCommand]
    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        ReloadMaps();
        ReloadPowerTests();
        Comparison.ReloadTests();
        await ReloadKlaMapsAsync();

        if (AvailableMaps.Count > 0)
        {
            SelectedMapSummary = AvailableMaps[0];
        }
        else
        {
            StatusMessage = "Nenhum mapa salvo. Crie um mapa para começar a síntese 2D.";
        }
    }

    /// <summary>
    /// Re-reads the workspace on every visit to the page, keeping the open map and the ticked
    /// assays. Initialization runs once, so without this an assay finished after the first visit
    /// would never appear in the picker.
    /// </summary>
    [RelayCommand]
    public async Task RefreshOnEnterAsync()
    {
        if (!_initialized)
        {
            await InitializeAsync();
            return;
        }

        var openMapId = CurrentDocument?.MapId;
        var selectedTestIds = AvailablePowerTests
            .Where(t => t.IsSelected)
            .Select(t => t.Summary.TestId)
            .ToHashSet();
        var linkedKlaId = SelectedKlaMapOption?.Id;
        var wasStale = IsSurfaceStale;

        ReloadMaps();

        _suppressStale = true;
        try
        {
            ReloadPowerTests();
            foreach (var item in AvailablePowerTests)
            {
                item.IsSelected = selectedTestIds.Contains(item.Summary.TestId);
            }
        }
        finally
        {
            _suppressStale = false;
        }

        IsSurfaceStale = wasStale;
        Comparison.ReloadTests();
        await ReloadKlaMapsAsync();

        if (openMapId is { } mapId)
        {
            var summary = AvailableMaps.FirstOrDefault(m => m.MapId == mapId);
            if (summary is not null && !ReferenceEquals(SelectedMapSummary, summary))
            {
                // Re-selecting reloads the document from disk, which is what we want if another
                // part of the app touched it; the surface and correlation come back with it.
                SelectedMapSummary = summary;
            }
        }

        if (linkedKlaId is { } klaId)
        {
            SelectedKlaMapOption = AvailableKlaMaps.FirstOrDefault(k => k.Id == klaId) ?? SelectedKlaMapOption;
        }
    }

    public void ReloadMaps()
    {
        AvailableMaps.Clear();
        var maps = _mapStore.ListMaps();
        foreach (var m in maps)
        {
            AvailableMaps.Add(m);
        }
    }

    [RelayCommand]
    public void ReloadPowerTests()
    {
        foreach (var existing in AvailablePowerTests)
        {
            existing.PropertyChanged -= OnPowerTestSelectionChanged;
        }

        AvailablePowerTests.Clear();
        var tests = _testStore.ListTests();
        foreach (var t in tests)
        {
            var isSelected = CurrentDocument?.SourceTestIds.Contains(t.TestId) ?? false;
            var item = new PowerTestSourceItemViewModel(t, isSelected);
            item.PropertyChanged += OnPowerTestSelectionChanged;
            AvailablePowerTests.Add(item);
        }
    }

    private void OnPowerTestSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressStale || e.PropertyName != nameof(PowerTestSourceItemViewModel.IsSelected))
        {
            return;
        }

        MarkSurfaceStale();
    }

    public async Task ReloadKlaMapsAsync()
    {
        AvailableKlaMaps.Clear();
        var klaExps = await _klaStore.LoadExperimentsAsync();
        foreach (var exp in klaExps)
        {
            AvailableKlaMaps.Add(new KlaMapOptionViewModel(exp.Snapshot.Id, exp.Snapshot.Name, exp.Snapshot.Anchors.Length));
        }

        if (CurrentDocument?.LinkedKlaMapId is { } linkedId)
        {
            SelectedKlaMapOption = AvailableKlaMaps.FirstOrDefault(k => k.Id == linkedId);
        }
    }

    [RelayCommand]
    public void CreateMap()
    {
        var name = string.IsNullOrWhiteSpace(NewMapName) ? "Novo Mapa de Potência" : NewMapName.Trim();
        var selectedTestIds = AvailablePowerTests.Where(t => t.IsSelected).Select(t => t.Summary.TestId).ToList();
        var selectedTestNames = AvailablePowerTests.Where(t => t.IsSelected).Select(t => t.Summary.Name).ToList();

        PowerGeometry? geom = null;
        FluidProperties? fluid = null;
        if (AvailablePowerTests.FirstOrDefault(t => t.IsSelected) is { } firstTest)
        {
            var firstDoc = _testStore.LoadTest(firstTest.Summary.FolderName);
            if (firstDoc != null)
            {
                geom = firstDoc.Geometry;
                fluid = firstDoc.Fluid;
            }
        }

        var doc = _mapStore.CreateMap(name, selectedTestIds, selectedTestNames, fluid, geom);
        ReloadMaps();
        SelectedMapSummary = AvailableMaps.FirstOrDefault(m => m.MapId == doc.MapId);
        NewMapName = "Novo Mapa de Potência";
        StatusMessage = $"Mapa '{doc.Name}' criado com sucesso.";
    }

    [RelayCommand]
    public void SaveMap()
    {
        if (CurrentDocument == null)
        {
            return;
        }

        var selectedTestIds = AvailablePowerTests.Where(t => t.IsSelected).Select(t => t.Summary.TestId).ToList();
        var selectedTestNames = AvailablePowerTests.Where(t => t.IsSelected).Select(t => t.Summary.Name).ToList();

        var updated = CurrentDocument with
        {
            Name = string.IsNullOrWhiteSpace(MapName) ? CurrentDocument.Name : MapName.Trim(),
            Notes = Notes,
            SourceTestIds = selectedTestIds,
            SourceTestNames = selectedTestNames,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        _mapStore.SaveMap(updated);
        CurrentDocument = updated;
        ReloadMaps();
        // Rebuilding the list clears the ComboBox selection; restore it or the operator's map
        // silently drops out of the picker right after a save.
        SelectedMapSummary = AvailableMaps.FirstOrDefault(m => m.MapId == updated.MapId);
        StatusMessage = $"Mapa '{updated.Name}' salvo com sucesso.";
    }

    [RelayCommand]
    public void DeleteMap()
    {
        if (CurrentDocument == null)
        {
            return;
        }

        if (_dialogs != null && !_dialogs.Confirm(
            "Excluir Mapa de Potência",
            $"Deseja realmente remover o mapa '{CurrentDocument.Name}'?",
            confirmText: "Excluir",
            cancelText: "Cancelar",
            isDanger: true))
        {
            return;
        }

        CancelReconstruction();
        _mapStore.DeleteMap(CurrentDocument.FolderName);
        CurrentDocument = null;
        CurrentSurfaceData = null;
        CurrentFloodingBoundary = null;
        CurrentCorrelation = null;
        CurrentSurfaceIntersection = null;
        ResetCorrelationDisplay();
        ReloadMaps();
        SelectedMapSummary = AvailableMaps.FirstOrDefault();
        StatusMessage = "Mapa excluído.";
        VisualizationChanged?.Invoke();
    }

    public void LoadMap(string folderName)
    {
        var doc = _mapStore.LoadMap(folderName);
        if (doc == null)
        {
            return;
        }

        // A grid still being built belongs to the map we are leaving; it must not land on this one.
        CancelReconstruction();

        CurrentDocument = doc;
        MapName = doc.Name;
        Notes = doc.Notes;
        CurrentSurfaceData = doc.SurfaceData;
        CurrentFloodingBoundary = doc.FloodingBoundary;

        _suppressStale = true;
        try
        {
            foreach (var t in AvailablePowerTests)
            {
                t.IsSelected = doc.SourceTestIds.Contains(t.Summary.TestId);
            }
        }
        finally
        {
            _suppressStale = false;
        }

        IsSurfaceStale = false;

        if (doc.LinkedKlaMapId.HasValue)
        {
            SelectedKlaMapOption = AvailableKlaMaps.FirstOrDefault(k => k.Id == doc.LinkedKlaMapId.Value);
        }

        CurrentSurfaceIntersection = doc.SurfaceIntersection;
        CurrentCorrelation = doc.KlaCorrelation;
        ApplyIntersectionDisplay(doc.SurfaceIntersection);

        MatchedPairs.Clear();
        foreach (var p in doc.KlaPairs)
        {
            MatchedPairs.Add(p);
        }
        MatchedPairsCount = doc.KlaPairs.Count;
        OnPropertyChanged(nameof(HasMatchedPairs));

        if (doc.KlaCorrelation is { HasFit: true } corr)
        {
            VanTRietKText = $"{corr.K:G4} ± {corr.StdErrorK:G3}";
            VanTRietAlphaText = $"{corr.Alpha:F3} ± {corr.StdErrorAlpha:F3}";
            VanTRietBetaText = $"{corr.Beta:F3} ± {corr.StdErrorBeta:F3}";
            VanTRietR2Text = $"{corr.R2:F4}";
            VanTRietFormulaText = $"kLa = {corr.K:F4} · (P/V)^{corr.Alpha:F3} · (v_s)^{corr.Beta:F3}  [R² = {corr.R2:F4}]";
        }
        else if (doc.KlaCorrelation is { } refused)
        {
            VanTRietKText = "—";
            VanTRietAlphaText = "—";
            VanTRietBetaText = "—";
            VanTRietR2Text = "—";
            VanTRietFormulaText = $"Ajuste recusado: {refused.FailureReason ?? "dados insuficientes ou matriz singular"}";
        }
        else
        {
            VanTRietKText = "—";
            VanTRietAlphaText = "—";
            VanTRietBetaText = "—";
            VanTRietR2Text = "—";
            VanTRietFormulaText = "kLa = K · (P/V)^α · (v_s)^β";
        }

        StatusMessage = $"Mapa '{doc.Name}' carregado.";
        VisualizationChanged?.Invoke();
    }

    private void ApplyIntersectionDisplay(SurfaceIntersectionResult? intersection)
    {
        if (intersection is null)
        {
            CommonDomainText = "—";
            IntersectionCoverageText = "—";
            EfficiencyMinimumText = "—";
            EfficiencyMeanText = "—";
            EfficiencyMaximumText = "—";
            EfficiencyMaximumPointText = "—";
            IntersectionStatusText = "Nenhuma intersecção calculada.";
            return;
        }

        CommonDomainText = intersection.CandidatePointCount > 0
            ? $"N: {intersection.MinRpm:F0}–{intersection.MaxRpm:F0} rpm · Qg: {intersection.MinFlowLpm:F2}–{intersection.MaxFlowLpm:F2} L/min"
            : "Sem domínio comum";
        IntersectionCoverageText = intersection.CandidatePointCount > 0
            ? $"{intersection.ValidPointCount:N0} / {intersection.CandidatePointCount:N0} ({intersection.CoveragePercent:F1}%)"
            : "—";
        EfficiencyMinimumText = intersection.EfficiencyMinimum is { } min ? $"{min:G5}" : "—";
        EfficiencyMeanText = intersection.EfficiencyMean is { } mean ? $"{mean:G5}" : "—";
        EfficiencyMaximumText = intersection.EfficiencyMaximum is { } max ? $"{max:G5}" : "—";
        EfficiencyMaximumPointText = intersection.MaximumEfficiencyRpm is { } rpm && intersection.MaximumEfficiencyFlowLpm is { } flow
            ? $"N = {rpm:F0} rpm · Qg = {flow:F2} L/min"
            : "—";
        IntersectionStatusText = intersection.ValidPointCount > 0
            ? $"Eficiência calculada a partir da intersecção dos mapas. {intersection.Warnings.FirstOrDefault() ?? ""}".Trim()
            : intersection.Warnings.FirstOrDefault() ?? "Nenhuma célula válida na região comum.";
    }

    [RelayCommand]
    public async Task ReconstructSurfaceAsync()
    {
        // A run already in flight is abandoned rather than blocking the new one: the operator may
        // have changed the resolution or the selected assays while the previous grid was building,
        // and §18.3 step 4.2 requires the obsolete result to be discarded.
        _reconstructionCts?.Cancel();
        _reconstructionCts?.Dispose();
        _reconstructionCts = new CancellationTokenSource();
        var token = _reconstructionCts.Token;
        var generation = ++_reconstructionGeneration;
        var targetMapId = CurrentDocument?.MapId;

        IsBusy = true;
        ProgressPercent = 10;
        ProgressText = "Coletando pontos operacionais dos ensaios...";

        try
        {
            var selectedTestSummaries = AvailablePowerTests.Where(t => t.IsSelected).Select(t => t.Summary).ToList();
            if (selectedTestSummaries.Count == 0)
            {
                StatusMessage = "Selecione ao menos um ensaio de potência para sintetizar a superfície.";
                return;
            }

            var anchors = new List<PowerMapAnchorPoint>();
            var sourceTestIds = new List<Guid>();
            var sourceTestNames = new List<string>();
            PowerGeometry? refGeometry = null;
            FluidProperties? refFluid = null;

            foreach (var testSummary in selectedTestSummaries)
            {
                var doc = _testStore.LoadTest(testSummary.FolderName);
                if (doc == null)
                {
                    continue;
                }

                sourceTestIds.Add(doc.TestId);
                sourceTestNames.Add(doc.Name);
                refGeometry ??= doc.Geometry;
                refFluid ??= doc.Fluid;

                var vesselD = doc.Geometry.VesselDiameterM > 0 ? doc.Geometry.VesselDiameterM : 0.190;
                var liquidV = doc.Geometry.LiquidVolumeM3 > 0 ? doc.Geometry.LiquidVolumeM3 : 0.010;

                foreach (var run in doc.Runs.Where(r => r.Phase == PowerRunPhase.Accepted && r.NetPowerW.HasValue))
                {
                    if (run.NetPowerW is not { } netPowerW)
                    {
                        continue;
                    }

                    var flowLpm = run.GasFlowLpm ?? 0.0;
                    var vs = PowerCalc.GasSuperficialVelocity(flowLpm, vesselD);
                    var pv = PowerCalc.VolumetricPower(netPowerW, liquidV);
                    anchors.Add(new PowerMapAnchorPoint
                    {
                        RunId = run.RunId,
                        SourceTestId = doc.TestId,
                        SourceTestName = doc.Name,
                        AgitationRpm = run.AgitationRpm,
                        GasFlowLpm = flowLpm,
                        GasSuperficialVelocityMs = vs,
                        NetPowerW = run.NetPowerW.Value,
                        VolumetricPowerWm3 = pv,
                        PowerRatio = run.PowerRatio,
                        GasFlowNumber = run.GasFlowNumber,
                        FroudeNumber = run.FroudeNumber,
                        ReynoldsNumber = run.Analysis?.AssemblyReynoldsNumber,
                        IsFlooded = false,
                        MeasuredAtUtc = run.CompletedUtc ?? run.StartedUtc,
                    });
                }
            }

            token.ThrowIfCancellationRequested();

            if (anchors.Count < 3)
            {
                StatusMessage = $"São necessários ao menos 3 pontos operacionais aceitos (encontrados {anchors.Count}).";
                return;
            }

            refGeometry ??= CurrentDocument?.Geometry ?? new PowerGeometry();
            refFluid ??= CurrentDocument?.Fluid ?? new FluidProperties();

            ProgressPercent = 35;
            ProgressText = "Reconstruindo malha 2D contínua via Clough-Tocher C¹...";

            var resolution = Math.Clamp(GridResolution, 50, 300);
            var settings = new PowerMapAlgorithmSettings
            {
                ResolutionN = resolution,
                ResolutionQg = resolution,
                GradientTolerance = GradientTolerance,
                GradientIterations = GradientIterations,
            };

            var surfaceData = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var surface = _engine.ReconstructSurface(anchors, refGeometry, refFluid, settings);
                token.ThrowIfCancellationRequested();
                return surface;
            }, token);

            token.ThrowIfCancellationRequested();
            if (generation != _reconstructionGeneration || CurrentDocument?.MapId != targetMapId)
            {
                return;
            }

            ProgressPercent = 85;
            ProgressText = "Persistindo superfície reconstruída...";

            if (CurrentDocument != null)
            {
                CurrentDocument = CurrentDocument with
                {
                    SourceTestIds = sourceTestIds,
                    SourceTestNames = sourceTestNames,
                    Geometry = refGeometry,
                    Fluid = refFluid,
                    SurfaceData = surfaceData,
                    // Legacy flooding fields are retained in the document schema for
                    // compatibility, but are no longer recomputed or presented here.
                    FloodingBoundary = CurrentDocument.FloodingBoundary,
                    SurfaceIntersection = null,
                    KlaPairs = [],
                    KlaCorrelation = null,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                };
                _mapStore.SaveMap(CurrentDocument);
            }

            CurrentSurfaceData = surfaceData;
            CurrentFloodingBoundary = CurrentDocument?.FloodingBoundary;
            CurrentSurfaceIntersection = null;
            CurrentCorrelation = null;
            MatchedPairs.Clear();
            MatchedPairsCount = 0;
            OnPropertyChanged(nameof(HasMatchedPairs));
            ResetCorrelationDisplay();
            ApplyIntersectionDisplay(null);
            IsSurfaceStale = false;

            ProgressPercent = 100;
            ProgressText = "Concluído";

            var covered = surfaceData.PNetSurface.Count(v => v.HasValue);
            var total = Math.Max(1, surfaceData.PNetSurface.Length);
            StatusMessage =
                $"Superfície sintetizada: {anchors.Count} âncoras, malha {resolution}×{resolution}, " +
                $"{covered * 100.0 / total:F0}% do domínio dentro do fecho convexo " +
                $"(N {surfaceData.MinRpm:F0}–{surfaceData.MaxRpm:F0} rpm, Qg {surfaceData.MinFlowLpm:F1}–{surfaceData.MaxFlowLpm:F1} L/min).";

            if (covered == 0)
            {
                StatusMessage = "Nenhuma célula interpolada: as âncoras são colineares no plano (N, Qg). " +
                                "Um ensaio só sem gás, ou só numa vazão, não define uma superfície 2D — " +
                                "inclua condições com ao menos duas vazões de gás distintas.";
            }

            VisualizationChanged?.Invoke();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cálculo da malha descartado.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro ao sintetizar superfície: {ex.Message}";
        }
        finally
        {
            if (generation == _reconstructionGeneration)
            {
                IsBusy = false;
            }
        }
    }

    [RelayCommand]
    public async Task CalculateSurfaceIntersectionAsync()
    {
        if (CurrentDocument is null || CurrentSurfaceData is null)
        {
            StatusMessage = "Reconstrua primeiro a superfície do mapa de potência.";
            return;
        }

        if (SelectedKlaMapOption is null)
        {
            StatusMessage = "Selecione um mapa kLa para calcular a intersecção.";
            return;
        }

        var allKla = await _klaStore.LoadExperimentsAsync();
        var klaDoc = allKla.FirstOrDefault(k => k.Snapshot.Id == SelectedKlaMapOption.Id);
        if (klaDoc is null)
        {
            StatusMessage = "Mapa kLa não encontrado no repositório.";
            return;
        }

        try
        {
            IsBusy = true;
            ProgressText = "Reconstruindo a superfície kLa e avaliando o domínio comum...";
            ProgressPercent = 30;
            var klaSurface = await Task.Run(() => _klaMappingEngine.Reconstruct(klaDoc.Snapshot));
            var intersection = await Task.Run(() => new SurfaceIntersectionService().Intersect(CurrentDocument, klaSurface));

            CurrentSurfaceIntersection = intersection;
            ApplyIntersectionDisplay(intersection);
            CurrentCorrelation = null;
            MatchedPairs.Clear();
            MatchedPairsCount = 0;
            OnPropertyChanged(nameof(HasMatchedPairs));
            ResetCorrelationDisplay();

            CurrentDocument = CurrentDocument with
            {
                LinkedKlaMapId = klaDoc.Snapshot.Id,
                LinkedKlaMapName = klaDoc.Snapshot.Name,
                SurfaceIntersection = intersection,
                KlaPairs = [],
                KlaCorrelation = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            _mapStore.SaveMap(CurrentDocument);

            ProgressPercent = 100;
            ProgressText = "Concluído";
            StatusMessage = intersection.ValidPointCount > 0
                ? $"Intersecção calculada: {intersection.ValidPointCount:N0} de {intersection.CandidatePointCount:N0} pontos válidos ({intersection.CoveragePercent:F1}%)."
                : intersection.Warnings.FirstOrDefault() ?? "Nenhum ponto válido na região comum.";
            VisualizationChanged?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Falha ao calcular a intersecção das superfícies: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task FitSurfaceIntersectionAsync()
    {
        var intersection = CurrentSurfaceIntersection;
        if (intersection is null || intersection.ValidPointCount == 0)
        {
            StatusMessage = "Calcule primeiro a intersecção para disponibilizar o ajuste van 't Riet.";
            return;
        }

        if (intersection.ValidPointCount < 4)
        {
            StatusMessage = $"Ajuste van 't Riet indisponível: {intersection.ValidPointCount} pontos válidos (mínimo 4). A eficiência continua disponível.";
            return;
        }

        try
        {
            IsBusy = true;
            var cells = intersection.EnumerateValidCells().ToArray();
            var pairs = cells.Select(c => new KlaPowerPair
            {
                SourceKlaTestId = intersection.KlaMapId,
                AgitationRpm = c.AgitationRpm,
                GasFlowLpm = c.GasFlowLpm,
                SuperficialVelocityMs = c.SuperficialVelocityMs,
                VolumetricPowerWm3 = c.VolumetricPowerWm3,
                KlaPerHour = c.KlaPerHour,
                ConfidenceInterval95 = 0.0,
            }).ToList();

            var fit = await Task.Run(() =>
            {
                var result = _engine.FitVanTRietModel(pairs, out var fittedPairs);
                return (Result: result, Pairs: fittedPairs);
            });
            var correlation = fit.Result with
            {
                DataBasis = "Intersecção de superfícies (mapa kLa × mapa de potência)",
            };
            var updatedPairs = fit.Pairs;

            CurrentCorrelation = correlation;
            MatchedPairs.Clear();
            foreach (var p in updatedPairs)
            {
                MatchedPairs.Add(p);
            }

            MatchedPairsCount = updatedPairs.Count;
            OnPropertyChanged(nameof(HasMatchedPairs));

            if (!correlation.HasFit)
            {
                var reason = correlation.FailureReason ?? correlation.ExcludedPointsNotes.FirstOrDefault() ?? "variação insuficiente ou matriz colinear";
                VanTRietFormulaText = $"Ajuste recusado: {reason}";
                StatusMessage = $"Ajuste van 't Riet indisponível: {reason}. A eficiência continua disponível.";
            }
            else
            {
                VanTRietKText = $"{correlation.K:G4} ± {correlation.StdErrorK:G3}";
                VanTRietAlphaText = $"{correlation.Alpha:F3} ± {correlation.StdErrorAlpha:F3}";
                VanTRietBetaText = $"{correlation.Beta:F3} ± {correlation.StdErrorBeta:F3}";
                VanTRietR2Text = $"{correlation.R2:F4}";
                VanTRietFormulaText = $"kLa = {correlation.K:F4} · (P/V)^{correlation.Alpha:F3} · (v_s)^{correlation.Beta:F3}  [R² = {correlation.R2:F4}]";
                StatusMessage = $"Ajuste van 't Riet concluído sobre a intersecção: R² = {correlation.R2:F4} ({updatedPairs.Count:N0} pontos de mapa).";
            }

            if (CurrentDocument is not null)
            {
                CurrentDocument = CurrentDocument with
                {
                    KlaPairs = updatedPairs,
                    KlaCorrelation = correlation,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                };
                _mapStore.SaveMap(CurrentDocument);
            }

            VisualizationChanged?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Falha no ajuste van 't Riet: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Compatibility entry point for older callers: it now performs map × map first.</summary>
    public async Task LinkKlaMapAndFitAsync()
    {
        await CalculateSurfaceIntersectionAsync();
        if (HasSurfaceIntersection)
        {
            await FitSurfaceIntersectionAsync();
        }
    }

    [RelayCommand]
    public void ExportSurfaceIntersectionCsv()
    {
        if (CurrentSurfaceIntersection is null || CurrentDocument is null)
        {
            StatusMessage = "Calcule primeiro a intersecção das superfícies.";
            return;
        }

        _mapStore.SaveMap(CurrentDocument);
        StatusMessage = $"CSV da eficiência exportado em '{PowerMapFileContracts.SurfaceIntersectionCsvFileName}'.";
    }

    [RelayCommand]
    public async Task ExportEnrichedKlaMapAsync()
    {
        if (_integrationService == null)
        {
            StatusMessage = "Serviço de integração não disponível.";
            return;
        }

        if (SelectedKlaMapOption == null)
        {
            StatusMessage = "Selecione um mapa kLa de destino.";
            return;
        }

        var firstSelectedPowerTest = AvailablePowerTests.FirstOrDefault(t => t.IsSelected);
        if (firstSelectedPowerTest == null)
        {
            StatusMessage = "Selecione ao menos um ensaio de potência para exportação.";
            return;
        }

        var result = await _integrationService.ExportPowerResultsToKlaMapAsync(firstSelectedPowerTest.Summary.TestId, SelectedKlaMapOption.Id);
        if (result.Success)
        {
            StatusMessage = $"Mapa enriquecido exportado com sucesso: '{result.EnrichedMapName}' ({result.MatchedPairsCount} âncoras vinculadas).";
            await ReloadKlaMapsAsync();
        }
        else
        {
            StatusMessage = $"Falha na exportação: {result.Message}";
        }
    }

    /// <summary>
    /// Builds the field the heatmap draws for the selected layer, already oriented as
    /// [row = N index, column = Qg index] and carrying the finite range found in it.
    /// Cells outside the convex hull come back as NaN so the view can leave them unpainted.
    /// </summary>
    public bool TryBuildLayerField(out double[,] field, out double minValue, out double maxValue)
    {
        field = new double[1, 1];
        minValue = 0;
        maxValue = 1;

        var surface = CurrentSurfaceData;
        if (surface is null || surface.ResolutionN < 2 || surface.ResolutionQg < 2)
        {
            return false;
        }

        var rows = surface.ResolutionN;
        var cols = surface.ResolutionQg;
        var values = new double[rows, cols];

        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        var anyFinite = false;

        {
            var source = SelectedLayer switch
            {
                PowerMapLayer.NetPower => surface.PNetSurface,
                PowerMapLayer.PowerRatio => surface.PowerRatioSurface,
                PowerMapLayer.Efficiency => CurrentSurfaceIntersection is { } intersection &&
                                             intersection.ResolutionN == rows &&
                                             intersection.ResolutionQg == cols
                    ? intersection.EfficiencySurface
                    : [],
                _ => surface.PVolumetricSurface,
            };

            if (source.Length < rows * cols)
            {
                return false;
            }

            for (var i = 0; i < rows; i++)
            {
                for (var j = 0; j < cols; j++)
                {
                    var cell = source[surface.GetIndex(i, j)];
                    if (cell.HasValue && double.IsFinite(cell.Value))
                    {
                        values[i, j] = cell.Value;
                        anyFinite = true;
                        if (cell.Value < min)
                        {
                            min = cell.Value;
                        }

                        if (cell.Value > max)
                        {
                            max = cell.Value;
                        }
                    }
                    else
                    {
                        values[i, j] = double.NaN;
                    }
                }
            }
        }

        if (!anyFinite)
        {
            return false;
        }

        if (max - min < 1e-12)
        {
            max = min + 1e-9;
        }

        field = values;
        minValue = min;
        maxValue = max;
        return true;
    }

    /// <summary>
    /// Why the selected layer has nothing to draw. "No cells" has more than one cause, and telling
    /// the operator the wrong one sends them to fix the wrong thing.
    /// </summary>
    public string DescribeUndrawableLayer()
    {
        var surface = CurrentSurfaceData;
        if (surface is null)
        {
            return "Selecione os ensaios de origem e reconstrua a superfície";
        }

        if (SelectedLayer == PowerMapLayer.PowerRatio &&
            surface.AnchorPoints.All(a => a.PowerRatio is null))
        {
            return "Nenhum ponto gaseificado com razão P_G/P₀ nos ensaios selecionados." + Environment.NewLine +
                   "Inclua condições com gás — e a referência sem gás na mesma rotação — para esta camada.";
        }

        if (SelectedLayer == PowerMapLayer.VolumetricPower &&
            (CurrentDocument?.Geometry.LiquidVolumeM3 ?? 0) <= 0 &&
            surface.AnchorPoints.All(a => a.VolumetricPowerWm3 <= 0))
        {
            return "Sem volume útil declarado, não há P/V." + Environment.NewLine +
                   "Informe o volume de trabalho no ensaio de origem.";
        }

        if (SelectedLayer == PowerMapLayer.Efficiency && !HasSurfaceIntersection)
        {
            return "Calcule a intersecção dos mapas para gerar a camada de eficiência kLa/(P/V).";
        }

        var distinctFlows = surface.AnchorPoints.Select(a => Math.Round(a.GasFlowLpm, 3)).Distinct().Count();
        var distinctRpms = surface.AnchorPoints.Select(a => Math.Round(a.AgitationRpm, 1)).Distinct().Count();

        if (distinctFlows < 2 || distinctRpms < 2)
        {
            return "As âncoras são colineares no plano (N, Qg): uma superfície 2D precisa" + Environment.NewLine +
                   "de ao menos duas rotações e duas vazões de gás distintas.";
        }

        return "A malha não produziu células definidas para esta camada.";
    }

    /// <summary>
    /// Colour range actually used by the heatmap: the automatic range is the data range narrowed
    /// by the contrast control; the manual range wins when the operator sets one.
    /// </summary>
    public (double Min, double Max) ResolveDisplayRange(double dataMin, double dataMax)
    {
        if (!AutoScale && ManualScaleMin is { } manualMin && ManualScaleMax is { } manualMax && manualMax > manualMin)
        {
            return (manualMin, manualMax);
        }

        var contrast = Math.Clamp(ContrastPercent, 10.0, 100.0) / 100.0;
        if (contrast >= 0.999)
        {
            return (dataMin, dataMax);
        }

        // Squeezing the range around its midpoint saturates the extremes and pulls detail out of
        // the middle of the distribution, which is where the operating points sit.
        var mid = (dataMin + dataMax) / 2.0;
        var half = (dataMax - dataMin) / 2.0 * contrast;
        return (mid - half, mid + half);
    }

    /// <summary>Re-reads the value under the last cursor position after the layer changed.</summary>
    private void RefreshInspectionForCurrentLayer()
    {
        if (_lastInspectedRpm is { } rpm && _lastInspectedFlow is { } flow)
        {
            UpdateCursorInspection(rpm, flow);
        }
    }

    public void UpdateCursorInspection(double agitationRpm, double gasFlowLpm)
    {
        _lastInspectedRpm = agitationRpm;
        _lastInspectedFlow = gasFlowLpm;
        InspectedAgitationRpm = agitationRpm;
        InspectedGasFlowLpm = gasFlowLpm;

        var geom = CurrentDocument?.Geometry ?? new PowerGeometry();
        var vesselDiameter = geom.VesselDiameterM > 0 ? geom.VesselDiameterM : 0.190;
        var liquidVol = geom.LiquidVolumeM3 > 0 ? geom.LiquidVolumeM3 : 0.010;

        InspectedGasFlowVvm = liquidVol > 0 ? PowerCalc.LpmToVvm(gasFlowLpm, liquidVol) : 0.0;
        InspectedSuperficialVelocityMs = PowerCalc.GasSuperficialVelocity(gasFlowLpm, vesselDiameter);

        IsInspectedFlooded = false;
        InspectedFlowRegime = "—";

        var surface = CurrentSurfaceData;
        if (surface != null &&
            agitationRpm >= surface.MinRpm && agitationRpm <= surface.MaxRpm &&
            gasFlowLpm >= surface.MinFlowLpm && gasFlowLpm <= surface.MaxFlowLpm &&
            surface.ResolutionN > 1 && surface.ResolutionQg > 1)
        {
            var dRpm = (surface.MaxRpm - surface.MinRpm) / (surface.ResolutionN - 1);
            var dQg = (surface.MaxFlowLpm - surface.MinFlowLpm) / (surface.ResolutionQg - 1);

            var i = Math.Clamp((int)((agitationRpm - surface.MinRpm) / dRpm), 0, surface.ResolutionN - 2);
            var j = Math.Clamp((int)((gasFlowLpm - surface.MinFlowLpm) / dQg), 0, surface.ResolutionQg - 2);

            var u = dRpm > 1e-12 ? (agitationRpm - surface.RpmGrid[i]) / dRpm : 0.0;
            var v = dQg > 1e-12 ? (gasFlowLpm - surface.FlowGrid[j]) / dQg : 0.0;

            double?[] layerData = SelectedLayer switch
            {
                PowerMapLayer.NetPower => surface.PNetSurface,
                PowerMapLayer.PowerRatio => surface.PowerRatioSurface,
                PowerMapLayer.Efficiency => CurrentSurfaceIntersection is { } intersection &&
                                             intersection.ResolutionN == surface.ResolutionN &&
                                             intersection.ResolutionQg == surface.ResolutionQg
                    ? intersection.EfficiencySurface
                    : [],
                _ => surface.PVolumetricSurface,
            };

            if (layerData.Length < surface.ResolutionN * surface.ResolutionQg)
            {
                InspectedLayerValue = null;
                InspectedLayerValueFormatted = "Fora da camada calculada";
                return;
            }

            var c00 = layerData[surface.GetIndex(i, j)];
            var c01 = layerData[surface.GetIndex(i, j + 1)];
            var c10 = layerData[surface.GetIndex(i + 1, j)];
            var c11 = layerData[surface.GetIndex(i + 1, j + 1)];

            if (c00.HasValue && c01.HasValue && c10.HasValue && c11.HasValue)
            {
                InspectedLayerValue = (1 - u) * (1 - v) * c00.Value +
                                      (1 - u) * v * c01.Value +
                                      u * (1 - v) * c10.Value +
                                      u * v * c11.Value;
            }
            else
            {
                InspectedLayerValue = c00 ?? c01 ?? c10 ?? c11;
            }
        }
        else
        {
            InspectedLayerValue = null;
        }

        InspectedLayerValueFormatted = InspectedLayerValue.HasValue
            ? SelectedLayer switch
            {
                PowerMapLayer.NetPower => $"{InspectedLayerValue.Value:F2} W",
                PowerMapLayer.PowerRatio => $"{InspectedLayerValue.Value:F3}",
                PowerMapLayer.Efficiency => $"{InspectedLayerValue.Value:G5} h⁻¹/(W/m³)",
                _ => $"{InspectedLayerValue.Value:F1} W/m³"
            }
            : "Fora do domínio interpolado";
    }

    public void Dispose()
    {
        foreach (var item in AvailablePowerTests)
        {
            item.PropertyChanged -= OnPowerTestSelectionChanged;
        }

        _reconstructionCts?.Cancel();
        _reconstructionCts?.Dispose();
        _reconstructionCts = null;
    }
}
