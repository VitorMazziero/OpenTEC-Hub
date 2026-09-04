using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.KlaMapping;
using OpenTECHub.Services.PowerTesting;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// The operator's path from a finished kLa map to a power condition table (§18.3 step 3.1).
/// The service existed and was tested, but nothing on the acquisition page could reach it.
/// </summary>
public sealed class PowerKlaImportUiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PowerKlaImportUi_" + Guid.NewGuid().ToString("N"));
    private readonly KlaProfileStore _klaStore;
    private readonly PowerTestStore _testStore;
    private readonly RecordingDeviceService _device = new();
    private readonly CommandArbiter _arbiter;

    public PowerKlaImportUiTests()
    {
        Directory.CreateDirectory(_root);
        _klaStore = new KlaProfileStore(Path.Combine(_root, "Mapas"));
        _testStore = new PowerTestStore(Path.Combine(_root, "Testes-Potencia"));
        _arbiter = new CommandArbiter(_device, TimeProvider.System);
    }

    public void Dispose()
    {
        _arbiter.Dispose();
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    private PowerTestViewModel BuildViewModel()
        => new(_testStore, _arbiter, _arbiter, null, null, null, _klaStore);

    /// <summary>
    /// Opens an empty assay the way the page does after "Novo": the store creates it, the picker
    /// selects it, and the plan becomes editable. (CreateTest itself needs the dialog service to
    /// prompt for the folder name, which a headless test has no use for.)
    /// </summary>
    private static void OpenEmptyAssay(PowerTestViewModel viewModel, PowerTestStore store, string name)
    {
        store.CreateTest(
            name,
            new FluidProperties(),
            new PowerGeometry
            {
                Impellers = [new Impeller { StageIndex = 0, DiameterM = 0.060 }],
                VesselDiameterM = 0.190,
                LiquidVolumeM3 = 0.010,
            },
            new PowerTestSettings(),
            []);

        viewModel.RefreshTestsCommand.Execute(null);
        viewModel.SelectedTest = viewModel.Tests.First(t => t.Name == name);
        viewModel.LoadSelectedTestCommand.Execute(null);
    }

    private async Task<KlaExperimentDocument> PersistMapAsync(string name)
    {
        var document = new KlaExperimentDocument
        {
            Snapshot = new KlaExperimentSnapshot
            {
                Id = Guid.NewGuid(),
                Name = name,
                Domain = new KlaDomain(0, 15, 15, 1000),
                Anchors =
                [
                    new KlaAnchor(5.0, 500.0, 48.0),
                    new KlaAnchor(2.0, 300.0, 18.0),
                    new KlaAnchor(5.0, 300.0, 26.0),
                    new KlaAnchor(2.0, 500.0, 34.0),
                ],
            },
        };

        await _klaStore.SaveExperimentAsync(document);
        return document;
    }

    [Fact]
    public async Task Maps_are_listed_and_import_is_blocked_until_one_is_chosen()
    {
        await PersistMapAsync("Mapa A");
        var viewModel = BuildViewModel();

        await viewModel.RefreshKlaMapsCommand.ExecuteAsync(null);

        var option = Assert.Single(viewModel.AvailableKlaMaps);
        Assert.Equal("Mapa A", option.Name);
        Assert.Equal(4, option.AnchorCount);
        Assert.Contains("4 âncoras", option.DisplayText, StringComparison.Ordinal);

        // No assay open yet, so the plan cannot be edited and the import stays unavailable.
        Assert.False(viewModel.CanImportFromKlaMap);

        viewModel.SelectedKlaMapForImport = option;
        Assert.False(viewModel.CanImportFromKlaMap);

        OpenEmptyAssay(viewModel, _testStore, "ensaio");
        Assert.True(viewModel.CanEditPlan);
        Assert.True(viewModel.CanImportFromKlaMap);

        viewModel.Dispose();
    }

    [Fact]
    public async Task Import_lands_on_the_map_coordinates_and_adds_the_p0_references()
    {
        await PersistMapAsync("Mapa 2x2");
        var viewModel = BuildViewModel();

        await viewModel.RefreshKlaMapsCommand.ExecuteAsync(null);
        OpenEmptyAssay(viewModel, _testStore, "ensaio");
        viewModel.SelectedKlaMapForImport = viewModel.AvailableKlaMaps[0];
        viewModel.ImportUngassedReferences = true;

        await viewModel.ImportConditionsFromKlaMapCommand.ExecuteAsync(null);

        // Two ungassed references (300 and 500 rpm) plus the four gassed map points.
        Assert.Equal(6, viewModel.Conditions.Count);

        var ungassed = viewModel.Conditions.Where(c => c.GasMode == PowerGasMode.Ungassed).ToList();
        Assert.Equal(2, ungassed.Count);
        Assert.Equal([300.0, 500.0], ungassed.Select(c => c.AgitationRpm).OrderBy(r => r));

        var gassed = viewModel.Conditions.Where(c => c.GasMode == PowerGasMode.Gassed).ToList();
        Assert.Equal(4, gassed.Count);

        // Canonical order and provenance are what let the two datasets be paired later.
        Assert.All(viewModel.Conditions, c => Assert.Equal(PowerConditionOrigin.Map, c.Origin));
        Assert.All(viewModel.Conditions, c => Assert.Equal("Mapa 2x2", c.SourceMapName));
        Assert.Equal(
            Enumerable.Range(0, viewModel.Conditions.Count),
            viewModel.Conditions.Select(c => c.OrderIndex));

        Assert.Contains("referência", viewModel.ValidationMessage ?? "", StringComparison.OrdinalIgnoreCase);

        viewModel.Dispose();
    }

    [Fact]
    public async Task Import_without_references_keeps_only_the_measured_points()
    {
        await PersistMapAsync("Mapa sem P0");
        var viewModel = BuildViewModel();

        await viewModel.RefreshKlaMapsCommand.ExecuteAsync(null);
        OpenEmptyAssay(viewModel, _testStore, "ensaio");
        viewModel.SelectedKlaMapForImport = viewModel.AvailableKlaMaps[0];
        viewModel.ImportUngassedReferences = false;
        viewModel.ImportReplicates = 3;

        await viewModel.ImportConditionsFromKlaMapCommand.ExecuteAsync(null);

        Assert.Equal(4, viewModel.Conditions.Count);
        Assert.All(viewModel.Conditions, c => Assert.Equal(PowerGasMode.Gassed, c.GasMode));
        Assert.All(viewModel.Conditions, c => Assert.Equal(3, c.RequestedReplicates));

        viewModel.Dispose();
    }

    [Fact]
    public async Task Import_is_refused_while_the_plan_is_locked_and_when_nothing_is_selected()
    {
        await PersistMapAsync("Mapa");
        var viewModel = BuildViewModel();
        await viewModel.RefreshKlaMapsCommand.ExecuteAsync(null);

        // No assay open: the plan is not editable.
        await viewModel.ImportConditionsFromKlaMapCommand.ExecuteAsync(null);
        Assert.Empty(viewModel.Conditions);
        Assert.Contains("parado", viewModel.ValidationMessage ?? "", StringComparison.OrdinalIgnoreCase);

        // Assay open but no map picked.
        OpenEmptyAssay(viewModel, _testStore, "ensaio");
        viewModel.SelectedKlaMapForImport = null;
        await viewModel.ImportConditionsFromKlaMapCommand.ExecuteAsync(null);
        Assert.Empty(viewModel.Conditions);
        Assert.Contains("Selecione um mapa", viewModel.ValidationMessage ?? "", StringComparison.Ordinal);

        viewModel.Dispose();
    }

    [Fact]
    public async Task An_empty_workspace_says_so_instead_of_offering_nothing_silently()
    {
        var viewModel = BuildViewModel();

        await viewModel.RefreshKlaMapsCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.AvailableKlaMaps);
        Assert.Contains("Nenhum mapa de kLa", viewModel.ValidationMessage ?? "", StringComparison.Ordinal);

        viewModel.Dispose();
    }

    [Fact]
    public void Without_a_kla_store_the_page_explains_itself_rather_than_failing()
    {
        var viewModel = new PowerTestViewModel(_testStore, _arbiter, _arbiter);

        viewModel.RefreshKlaMapsCommand.ExecuteAsync(null).GetAwaiter().GetResult();

        Assert.Empty(viewModel.AvailableKlaMaps);
        Assert.Contains("indisponível", viewModel.ValidationMessage ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.CanImportFromKlaMap);

        viewModel.Dispose();
    }
}
