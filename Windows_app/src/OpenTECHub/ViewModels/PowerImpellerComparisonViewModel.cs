using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTECHub.Services.PowerMapping;
using OpenTECHub.Services.PowerTesting;

namespace OpenTECHub.ViewModels;

/// <summary>One benchmarking row, formatted for the comparison table.</summary>
public sealed record ImpellerComparisonRow(ImpellerComparisonItem Item)
{
    public string TestName => Item.TestName;

    public string ImpellerTypeLabel => Item.ImpellerType.ToString();

    public string DiameterMm => (Item.ImpellerDiameterM * 1000).ToString("F1", CultureInfo.CurrentCulture);

    public string DiameterRatio => Item.DiameterRatioDt.ToString("F3", CultureInfo.CurrentCulture);

    public string PlateauNp => Item.TurbulentNpMean > 0
        ? $"{Item.TurbulentNpMean.ToString("F3", CultureInfo.CurrentCulture)} ± {Item.TurbulentNpCi95.ToString("F3", CultureInfo.CurrentCulture)}"
        : "—";

    public string ExperimentalFlG => Format(Item.ExperimentalFloodingFlG, "F4");

    public string NienowFlG => Format(Item.NienowFloodingFlG, "F4");

    public string ParasiticPower => Format(Item.ParasiticPowerZeroSpeedW, "F3");

    public string SpecificPower => Format(Item.AverageSpecificPowerWm3, "F0");

    public string DispersionEfficiency => Format(Item.GasDispersionEfficiencyRatio, "F3");

    public string TestDate => Item.TestDateUtc.ToLocalTime().ToString("dd/MM/yyyy", CultureInfo.CurrentCulture);

    private static string Format(double? value, string format)
        => value.HasValue && double.IsFinite(value.Value)
            ? value.Value.ToString(format, CultureInfo.CurrentCulture)
            : "—";
}

/// <summary>
/// Multi-assay impeller benchmarking (§18.3 step 6): Np×Re and P_G/P₀×Fl_G overlays, the specific
/// power demand, and the comparison table with its unified CSV export.
/// </summary>
public sealed partial class PowerImpellerComparisonViewModel : ObservableObject
{
    private readonly IPowerTestStore _testStore;
    private readonly IPowerMapStore _mapStore;
    private readonly IPowerAnalysisEngine _analysisEngine;

    public PowerImpellerComparisonViewModel(
        IPowerTestStore testStore,
        IPowerMapStore mapStore,
        IPowerAnalysisEngine analysisEngine)
    {
        _testStore = testStore ?? throw new ArgumentNullException(nameof(testStore));
        _mapStore = mapStore ?? throw new ArgumentNullException(nameof(mapStore));
        _analysisEngine = analysisEngine ?? throw new ArgumentNullException(nameof(analysisEngine));
    }

    public event Action? VisualizationChanged;

    public ObservableCollection<PowerTestSourceItemViewModel> AvailableTests { get; } = [];

    public ObservableCollection<ImpellerComparisonRow> Rows { get; } = [];

    public ObservableCollection<string> CompatibilityNotes { get; } = [];

    /// <summary>The items behind <see cref="Rows"/>, in the same order; the plots read these.</summary>
    public IReadOnlyList<ImpellerComparisonItem> Items => Rows.Select(r => r.Item).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasComparison))]
    public partial int ComparedCount { get; set; }

    public bool HasComparison => ComparedCount > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCompatibilityWarnings))]
    public partial bool IsCompatible { get; set; } = true;

    public bool HasCompatibilityWarnings => !IsCompatible;

    [ObservableProperty]
    public partial string CompatibilitySummary { get; set; } = "";

    [ObservableProperty]
    public partial string StatusMessage { get; set; } =
        "Selecione dois ou mais ensaios concluídos para comparar impelidores.";

    [ObservableProperty]
    public partial string ComparisonName { get; set; } = "Comparação de impelidores";

    /// <summary>Reloads the assay picker, keeping whatever was already ticked.</summary>
    [RelayCommand]
    public void ReloadTests()
    {
        var previouslySelected = AvailableTests
            .Where(t => t.IsSelected)
            .Select(t => t.Summary.TestId)
            .ToHashSet();

        AvailableTests.Clear();

        // Only assays with accepted points can be benchmarked; a draft has nothing to plot.
        foreach (var summary in _testStore.ListTests().Where(t => t.AcceptedRunCount > 0))
        {
            AvailableTests.Add(new PowerTestSourceItemViewModel(summary, previouslySelected.Contains(summary.TestId)));
        }

        if (AvailableTests.Count == 0)
        {
            StatusMessage = "Nenhum ensaio com pontos aceitos disponível para comparação.";
        }
    }

    [RelayCommand]
    public void BuildComparison()
    {
        var selected = AvailableTests.Where(t => t.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "Selecione ao menos um ensaio concluído.";
            return;
        }

        var documents = new List<PowerTestDocument>();
        foreach (var entry in selected)
        {
            var document = _testStore.LoadTest(entry.Summary.FolderName);
            if (document != null)
            {
                documents.Add(document);
            }
        }

        if (documents.Count == 0)
        {
            StatusMessage = "Não foi possível carregar os ensaios selecionados.";
            return;
        }

        var items = documents.Select(d => ImpellerComparisonBuilder.BuildItem(d, _analysisEngine)).ToList();
        var (compatible, notes) = ImpellerComparisonBuilder.CheckCompatibility(items, documents);

        Rows.Clear();
        foreach (var item in items)
        {
            Rows.Add(new ImpellerComparisonRow(item));
        }

        CompatibilityNotes.Clear();
        foreach (var note in notes)
        {
            CompatibilityNotes.Add(note);
        }

        IsCompatible = compatible;
        ComparedCount = items.Count;
        OnPropertyChanged(nameof(Items));

        CompatibilitySummary = compatible
            ? "Montagens equivalentes: as curvas podem ser lidas lado a lado."
            : $"Montagens NÃO equivalentes ({notes.Count} diferença(s)). As séries continuam sobrepostas para " +
              "inspeção, mas não devem ser agregadas como se viessem da mesma configuração.";

        var withoutPlateau = items.Count(i => i.TurbulentNpMean <= 0);
        StatusMessage = withoutPlateau > 0
            ? $"{items.Count} ensaio(s) comparado(s); {withoutPlateau} sem platô turbulento ajustado " +
              $"(nenhum ponto aceito acima de Re = {ImpellerComparisonBuilder.TurbulentReynoldsCutoff:N0})."
            : $"{items.Count} ensaio(s) comparado(s).";

        VisualizationChanged?.Invoke();
    }

    /// <summary>Persists the current comparison under <c>Mapas-Potencia/</c> for later reuse.</summary>
    [RelayCommand]
    public void SaveComparison()
    {
        if (Rows.Count == 0)
        {
            StatusMessage = "Nada a salvar: monte a comparação primeiro.";
            return;
        }

        var items = Items;
        var name = string.IsNullOrWhiteSpace(ComparisonName) ? "Comparação de impelidores" : ComparisonName.Trim();

        try
        {
            var document = _mapStore.CreateComparison(
                name,
                items.Select(i => i.SourceTestId).ToList(),
                items,
                notes: CompatibilitySummary);

            var saved = document with
            {
                IsCompatibleGeometry = IsCompatible,
                CompatibilityNotes = CompatibilityNotes.ToList(),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };

            _mapStore.SaveComparison(saved);
            StatusMessage = $"Comparação '{saved.Name}' salva.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Falha ao salvar a comparação: {ex.Message}";
        }
    }

    /// <summary>Unified CSV of the benchmarking table plus the raw series behind it (§18.3 step 6.3).</summary>
    public string BuildCsvContent()
        => ImpellerComparisonBuilder.BuildCsv(Items, CompatibilityNotes.ToList());

    public string SuggestedCsvFileName
    {
        get
        {
            var stem = string.IsNullOrWhiteSpace(ComparisonName) ? "comparacao-impelidores" : ComparisonName.Trim();
            foreach (var invalid in System.IO.Path.GetInvalidFileNameChars())
            {
                stem = stem.Replace(invalid, '-');
            }

            return $"{stem}-{DateTime.Now:yyyyMMdd-HHmm}.csv";
        }
    }
}
