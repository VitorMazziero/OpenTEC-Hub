using System.IO;
using System.Globalization;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.KlaMapping;
using OpenTECHub.Services.PowerMapping;
using OpenTECHub.Services.PowerTesting;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class PowerNavigationContractTests
{
    private static string ReadProjectFile(string relativePath)
        => File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "src", "OpenTECHub", relativePath));

    private static string ReadTabContent(string xaml, string header)
    {
        var startToken = $"<TabItem Header=\"{header}\"";
        var start = xaml.IndexOf(startToken, StringComparison.Ordinal);
        Assert.True(start >= 0, $"A aba '{header}' não foi encontrada.");

        var end = xaml.IndexOf("</TabItem>", start, StringComparison.Ordinal);
        Assert.True(end > start, $"A aba '{header}' não possui fechamento.");
        return xaml[start..end];
    }

    [Fact]
    public void Power_destination_follows_kla_mapping_in_the_automation_group()
    {
        var shell = ReadProjectFile(Path.Combine("ViewModels", "ShellViewModel.cs"));
        var kla = shell.IndexOf("new NavigationItem(\"kla-mapping\"", StringComparison.Ordinal);
        var power = shell.IndexOf("new NavigationItem(\"power\"", StringComparison.Ordinal);
        var history = shell.IndexOf("new NavigationItem(\"history\"", StringComparison.Ordinal);

        Assert.True(kla >= 0 && kla < power && power < history);
        Assert.Contains("\"Potência\", \"Impeller\", \"Automação\", \"#64B5F6\"", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("new NavigationItem(\"power-map\"", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void Shell_routes_power_view_and_embeds_map_view()
    {
        var xaml = ReadProjectFile("MainWindow.xaml");
        var powerXaml = ReadProjectFile(Path.Combine("Views", "PowerView.xaml"));

        Assert.Contains("<views:PowerView DataContext=\"{Binding PowerTest}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PageId=\"power\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("PageId=\"power-map\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedPageId=\"{Binding SelectedNavigationId}\"", xaml, StringComparison.Ordinal);

        // PowerView embeds PowerMapView bound to MapViewModel
        Assert.Contains("<views:PowerMapView DataContext=\"{Binding MapViewModel}\"", powerXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Mapeamento de Potência\"", powerXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Modelos e Ajustes\"", powerXaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// A navigation id with no host renders a blank page, and nothing else would catch it:
    /// the build is happy and the smoke run only opens one destination.
    /// </summary>
    [Fact]
    public void Every_shell_destination_is_hosted_by_a_deferred_page()
    {
        var xaml = ReadProjectFile("MainWindow.xaml");
        var shell = ReadProjectFile(Path.Combine("ViewModels", "ShellViewModel.cs"));

        var ids = System.Text.RegularExpressions.Regex
            .Matches(shell, "new NavigationItem\\(\"([a-z0-9-]+)\"")
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .ToList();

        Assert.NotEmpty(ids);

        var missing = ids
            .Where(id => !xaml.Contains($"PageId=\"{id}\"", StringComparison.Ordinal))
            .ToList();

        Assert.True(missing.Count == 0, $"ids de navegação sem página hospedada: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Composition_root_registers_both_power_view_models()
    {
        var app = ReadProjectFile("App.xaml.cs");
        var interlock = ReadProjectFile(Path.Combine("Services", "PowerTesting", "PowerTestInterlock.cs"));

        Assert.Contains("services.AddSingleton<PowerTestViewModel>();", app, StringComparison.Ordinal);
        Assert.Contains("services.AddSingleton<PowerMapViewModel>();", app, StringComparison.Ordinal);
        Assert.Contains("services.AddSingleton<IPowerTestInterlock, PowerTestInterlock>();", app, StringComparison.Ordinal);
        Assert.Contains("services.AddSingleton<IPowerTestRunner, PowerTestRunner>();", app, StringComparison.Ordinal);
        Assert.DoesNotContain("ISessionLogger", interlock, StringComparison.Ordinal);
    }

    [Fact]
    public void New_routes_receive_ctrl_6_and_ctrl_7_and_last_page_stays_generic()
    {
        var shell = ReadProjectFile(Path.Combine("ViewModels", "ShellViewModel.cs"));
        var windowCode = ReadProjectFile("MainWindow.xaml.cs");

        Assert.Contains("Key.D6 or Key.NumPad6 => 5", windowCode, StringComparison.Ordinal);
        Assert.Contains("Key.D7 or Key.NumPad7 => 6", windowCode, StringComparison.Ordinal);
        Assert.Contains("Ui = settings.Ui with { LastPage = value }", shell, StringComparison.Ordinal);
        Assert.Contains("NavigationItems.Any(item => item.Id == settings.Current.Ui.LastPage)", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void Power_map_page_delivers_the_synthesis_surface_instead_of_a_placeholder()
    {
        var xaml = ReadProjectFile(Path.Combine("Views", "PowerMapView.xaml"));
        var codeBehind = ReadProjectFile(Path.Combine("Views", "PowerMapView.xaml.cs"));
        var viewModel = ReadProjectFile(Path.Combine("ViewModels", "PowerMapViewModel.cs"));

        Assert.DoesNotContain("FASE 3", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PhaseLabel", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("public string PhaseLabel", viewModel, StringComparison.Ordinal);

        // The page is the real synthesis surface, not a stand-in.
        Assert.DoesNotContain("placeholder", xaml, StringComparison.OrdinalIgnoreCase);

        // Sidebar follows the operator's synthesis sequence and each tab owns related cards.
        var mapTab = ReadTabContent(xaml, "Mapa");
        Assert.Contains("Mapa de síntese", mapTab, StringComparison.Ordinal);
        Assert.Contains("Ensaios de origem", mapTab, StringComparison.Ordinal);

        var modelTab = ReadTabContent(xaml, "Modelo");
        Assert.Contains("Malha de interpolação", modelTab, StringComparison.Ordinal);
        Assert.Contains("Correlação kLa", modelTab, StringComparison.Ordinal);

        var displayTab = ReadTabContent(xaml, "Exibição");
        Assert.Contains("Visualização", displayTab, StringComparison.Ordinal);
        Assert.Contains("Inspeção sob o cursor", displayTab, StringComparison.Ordinal);

        // Plot hosts for the surface and both validation panels (§18.3 steps 5.2 and 5.3).
        Assert.Contains("SurfacePlotHost", xaml, StringComparison.Ordinal);
        Assert.Contains("ParityPlotHost", xaml, StringComparison.Ordinal);
        Assert.Contains("KlaPvPlotHost", xaml, StringComparison.Ordinal);

        // Visual settings apply on change - the workspace contract forbids an "Aplicar" step.
        Assert.DoesNotContain("Aplicar", xaml, StringComparison.OrdinalIgnoreCase);

        // Orientation and colour-bar handling are what make the surface readable in both themes.
        Assert.Contains("FlipVertically = true", codeBehind, StringComparison.Ordinal);
        Assert.Contains("StyleColorBar", codeBehind, StringComparison.Ordinal);
        Assert.Contains("UpdateCursorInspection", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Acquisition_shell_observes_servo_and_unsubscribes_on_dispose()
    {
        var root = Path.Combine(Path.GetTempPath(), "PowerNavigationTests_" + Guid.NewGuid().ToString("N"));
        try
        {
            var inner = new RecordingDeviceService();
            using var arbiter = new CommandArbiter(inner, TimeProvider.System);
            var store = new PowerTestStore(root);
            var viewModel = new PowerTestViewModel(store, arbiter, arbiter);

            inner.PushTelemetry(new SensorSnapshot
            {
                HasServoSample = true,
                ServoRpm = 450.2,
                ServoTorqueNm = 0.1234,
                ServoPowerW = 5.81,
            });

            Assert.True(viewModel.HasServoSample);
            Assert.Equal(450.2, viewModel.CurrentRpm);
            Assert.Contains("rpm", viewModel.LiveSummary, StringComparison.Ordinal);

            arbiter.Claim(CommandOwner.KlaAssay, [ActuatorId.Agitation], "test");
            Assert.Equal("Ensaio kLa", viewModel.AgitationOwnerLabel);

            viewModel.Dispose();
            inner.PushTelemetry(new SensorSnapshot { HasServoSample = false });
            arbiter.Release(CommandOwner.KlaAssay, "test complete");

            Assert.True(viewModel.HasServoSample);
            Assert.Equal("Ensaio kLa", viewModel.AgitationOwnerLabel);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Acquisition_view_exposes_setup_live_results_and_no_apply_or_png_path()
    {
        var xaml = ReadProjectFile(Path.Combine("Views", "PowerView.xaml"));
        var codeBehind = ReadProjectFile(Path.Combine("Views", "PowerView.xaml.cs"));

        var assemblyTab = ReadTabContent(xaml, "Montagem");
        Assert.Contains("Ensaio", assemblyTab, StringComparison.Ordinal);
        Assert.Contains("Fluido e vaso", assemblyTab, StringComparison.Ordinal);
        Assert.Contains("Impelidores", assemblyTab, StringComparison.Ordinal);

        var acquisitionTab = ReadTabContent(xaml, "Aquisição");
        Assert.Contains("Captura automática", acquisitionTab, StringComparison.Ordinal);
        Assert.Contains("Condições do ensaio", acquisitionTab, StringComparison.Ordinal);
        Assert.Contains("GenerateConditionsPlanCommand", acquisitionTab, StringComparison.Ordinal);
        Assert.Contains("ClearAllConditionsCommand", acquisitionTab, StringComparison.Ordinal);
        Assert.Contains("MinFlowLpm", acquisitionTab, StringComparison.Ordinal);
        Assert.Contains("MaxFlowLpm", acquisitionTab, StringComparison.Ordinal);

        var mapXaml = ReadProjectFile(Path.Combine("Views", "PowerMapView.xaml"));
        Assert.True(
            mapXaml.IndexOf("NewMapName", StringComparison.Ordinal) < mapXaml.IndexOf("CreateMapCommand", StringComparison.Ordinal),
            "O nome do novo mapa deve aparecer antes do botão Novo mapa.");

        Assert.DoesNotContain("CurrentRpmText, StringFormat={}{0} rpm", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentPowerWText, StringFormat={}{0} W", xaml, StringComparison.Ordinal);
        Assert.Contains("<TextBlock Text=\"rpm\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<TextBlock Text=\"W\"", xaml, StringComparison.Ordinal);

        var validationTab = ReadTabContent(xaml, "Validação e Eficiência de kLa");
        Assert.Contains("Rastreabilidade", validationTab, StringComparison.Ordinal);
        Assert.Contains("Calibração estática", validationTab, StringComparison.Ordinal);
        Assert.Contains("Ensaio de tara no ar", validationTab, StringComparison.Ordinal);
        Assert.Contains("CurrentTarePoints", validationTab, StringComparison.Ordinal);
        Assert.Contains("PVoidCi95W", validationTab, StringComparison.Ordinal);
        Assert.Contains("CancelTareSweepCommand", validationTab, StringComparison.Ordinal);
        Assert.Contains("Mapa de kLa e Eficiência", validationTab, StringComparison.Ordinal);
        Assert.Contains("ImportConditionsFromKlaMapCommand", validationTab, StringComparison.Ordinal);
        Assert.Contains("KlaEfficiencyItems", validationTab, StringComparison.Ordinal);

        Assert.Contains("LiveChartHost", xaml, StringComparison.Ordinal);
        Assert.Contains("NpChartHost", xaml, StringComparison.Ordinal);
        Assert.Contains("Exportar CSV", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Aplicar", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PNG", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PNG", codeBehind, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ui_numeric_parser_accepts_invariant_and_pt_br_decimal_without_thousands_ambiguity()
    {
        var prior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
            Assert.True(PowerTestViewModel.TryParseUiDouble("1.25", out var invariant));
            Assert.True(PowerTestViewModel.TryParseUiDouble("1,25", out var local));
            Assert.Equal(1.25, invariant, 12);
            Assert.Equal(1.25, local, 12);
            Assert.False(PowerTestViewModel.TryParseUiDouble("NaN", out _));
        }
        finally
        {
            CultureInfo.CurrentCulture = prior;
        }
    }

    [Fact]
    public void Missing_result_values_render_as_dash_and_net_torque_comes_from_net_power()
    {
        var missing = PowerResultRow.From(new PowerRunSummary
        {
            RunId = Guid.NewGuid(),
            MeanRpmMeasured = 0,
            StartedUtc = DateTimeOffset.UtcNow,
        });
        Assert.Equal("—", missing.Np);
        Assert.Equal("—", missing.Re);
        Assert.Equal("—", missing.NetTorque);

        var net = PowerResultRow.From(new PowerRunSummary
        {
            RunId = Guid.NewGuid(),
            MeanRpmMeasured = 60,
            MeanTorqueNm = 9,
            NetPowerW = 2 * Math.PI,
            StartedUtc = DateTimeOffset.UtcNow,
        });
        Assert.Equal(1.0, double.Parse(net.NetTorque, CultureInfo.CurrentCulture), 5);
    }

    [Fact]
    public void Power_view_model_switches_main_tabs_and_exposes_map_view_model()
    {
        var root = Path.Combine(Path.GetTempPath(), "PowerNavTabs_" + Guid.NewGuid().ToString("N"));
        try
        {
            var inner = new RecordingDeviceService();
            using var arbiter = new CommandArbiter(inner, TimeProvider.System);
            var store = new PowerTestStore(root);
            var mapStore = new PowerMapStore(root);
            var engine = new PowerMapEngine();
            var klaStore = new KlaProfileStore(root);
            using var mapVm = new PowerMapViewModel(store, mapStore, engine, klaStore);
            using var vm = new PowerTestViewModel(store, inner, arbiter, null, null, null, klaStore, mapVm);

            Assert.Same(mapVm, vm.MapViewModel);
            Assert.Equal(0, vm.SelectedMainTabIndex);
            Assert.True(vm.IsMappingTabSelected);
            Assert.False(vm.IsModelsTabSelected);

            vm.IsModelsTabSelected = true;
            Assert.Equal(1, vm.SelectedMainTabIndex);
            Assert.False(vm.IsMappingTabSelected);
            Assert.True(vm.IsModelsTabSelected);

            vm.IsMappingTabSelected = true;
            Assert.Equal(0, vm.SelectedMainTabIndex);
            Assert.True(vm.IsMappingTabSelected);
            Assert.False(vm.IsModelsTabSelected);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
