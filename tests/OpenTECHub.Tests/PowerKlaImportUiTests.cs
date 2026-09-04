using System;
using System.Collections.Generic;
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
/// Verifies the kLa map linking pipeline (preserving conditions and computing the control region)
/// and the manual condition plan generation (N x Qg) without spontaneous regeneration.
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

    private static void OpenEmptyAssay(PowerTestViewModel viewModel, PowerTestStore store, string name, List<PowerCondition>? initialConditions = null)
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
            initialConditions ?? []);

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
                    new KlaAnchor(2.0, 300.0, 18.0),
                    new KlaAnchor(3.5, 300.0, 22.0),
                    new KlaAnchor(5.0, 300.0, 26.0),
                    new KlaAnchor(2.0, 500.0, 34.0),
                    new KlaAnchor(3.5, 500.0, 41.0),
                    new KlaAnchor(5.0, 500.0, 48.0),
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
        Assert.Equal(6, option.AnchorCount);
        Assert.Contains("6 âncoras", option.DisplayText, StringComparison.Ordinal);

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
    public async Task Linking_kla_map_preserves_existing_conditions_and_computes_control_region()
    {
        await PersistMapAsync("Mapa 2x2");
        var viewModel = BuildViewModel();

        await viewModel.RefreshKlaMapsCommand.ExecuteAsync(null);
        var existing = new List<PowerCondition>
        {
            new() { OrderIndex = 0, AgitationRpm = 400.0, GasFlowLpm = 3.0, GasMode = PowerGasMode.Gassed },
            new() { OrderIndex = 1, AgitationRpm = 600.0, GasFlowLpm = 8.0, GasMode = PowerGasMode.Gassed },
        };
        OpenEmptyAssay(viewModel, _testStore, "ensaio", existing);

        viewModel.SelectedKlaMapForImport = viewModel.AvailableKlaMaps[0];

        await viewModel.ImportConditionsFromKlaMapCommand.ExecuteAsync(null);

        // Condition table is NOT replaced or overwritten!
        Assert.Equal(2, viewModel.Conditions.Count);
        Assert.Equal(400.0, viewModel.Conditions[0].AgitationRpm);
        Assert.Equal(600.0, viewModel.Conditions[1].AgitationRpm);

        // LinkedMap is set
        Assert.True(viewModel.HasLinkedKlaMap);
        Assert.Equal("Mapa 2x2", viewModel.LinkedKlaMapName);

        // Control region intersection (kLa: 300-500 rpm, 2-5 L/min; Power: 400-600 rpm, 3-8 L/min)
        // Intersection should be N 400-500 rpm, Qg 3.0-5.0 L/min
        Assert.Contains("Intersecção", viewModel.ControlRegionSummary);
        Assert.Contains("400", viewModel.ControlRegionSummary);
        Assert.Contains("500", viewModel.ControlRegionSummary);

        // Comparison items generated
        Assert.Equal(2, viewModel.KlaEfficiencyItems.Count);
        var item1 = viewModel.KlaEfficiencyItems[0];
        Assert.True(item1.IsInControlRegion);
        Assert.NotNull(item1.KlaInterpolatedPerHour);

        var item2 = viewModel.KlaEfficiencyItems[1];
        Assert.False(item2.IsInControlRegion); // 600 rpm is outside [300, 500]

        // Persisted manifest verification
        var saved = _testStore.LoadTest("ensaio");
        Assert.NotNull(saved);
        Assert.Equal(2, saved.Conditions.Count);
        Assert.NotNull(saved.LinkedMap);
        Assert.Equal("Mapa 2x2", saved.LinkedMap!.MapName);

        viewModel.Dispose();
    }

    [Fact]
    public void Generate_conditions_plan_creates_grid_and_clear_removes_all()
    {
        var viewModel = BuildViewModel();
        OpenEmptyAssay(viewModel, _testStore, "ensaio");

        viewModel.MinRpm = 100;
        viewModel.MaxRpm = 200;
        viewModel.StepRpm = 100; // 100, 200 (2 levels)

        viewModel.MinFlowLpm = 0;
        viewModel.MaxFlowLpm = 1;
        viewModel.StepFlowLpm = 0.5; // 0, 0.5, 1.0 (3 levels)

        // Verifies typing did not generate conditions
        Assert.Empty(viewModel.Conditions);

        // Execute Generate
        viewModel.GenerateConditionsPlanCommand.Execute(null);

        // 2 x 3 = 6 points
        Assert.Equal(6, viewModel.Conditions.Count);
        Assert.Equal(2, viewModel.Conditions.Count(c => c.GasMode == PowerGasMode.Ungassed));
        Assert.Equal(4, viewModel.Conditions.Count(c => c.GasMode == PowerGasMode.Gassed));

        // Execute Clear
        viewModel.ClearAllConditionsCommand.Execute(null);
        Assert.Empty(viewModel.Conditions);

        var saved = _testStore.LoadTest("ensaio");
        Assert.NotNull(saved);
        Assert.Empty(saved.Conditions);

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
    public async Task Without_a_kla_store_the_page_explains_itself_rather_than_failing()
    {
        var viewModel = new PowerTestViewModel(_testStore, _arbiter, _arbiter);

        await viewModel.RefreshKlaMapsCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.AvailableKlaMaps);
        Assert.Contains("indisponível", viewModel.ValidationMessage ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.CanImportFromKlaMap);

        viewModel.Dispose();
    }
}
