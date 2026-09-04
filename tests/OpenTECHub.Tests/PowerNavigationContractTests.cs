using System.IO;
using System.Globalization;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.PowerTesting;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class PowerNavigationContractTests
{
    private static string ReadProjectFile(string relativePath)
        => File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "src", "OpenTECHub", relativePath));

    [Fact]
    public void Both_power_destinations_follow_kla_mapping_in_the_automation_group()
    {
        var shell = ReadProjectFile(Path.Combine("ViewModels", "ShellViewModel.cs"));
        var kla = shell.IndexOf("new NavigationItem(\"kla-mapping\"", StringComparison.Ordinal);
        var power = shell.IndexOf("new NavigationItem(\"power\"", StringComparison.Ordinal);
        var map = shell.IndexOf("new NavigationItem(\"power-map\"", StringComparison.Ordinal);
        var history = shell.IndexOf("new NavigationItem(\"history\"", StringComparison.Ordinal);

        Assert.True(kla >= 0 && kla < power && power < map && map < history);
        Assert.Contains("\"Potência\", \"Impeller\", \"Automação\", \"#64B5F6\"", shell, StringComparison.Ordinal);
        Assert.Contains("\"Mapa de Potência\", \"Search\", \"Automação\", \"#64B5F6\"", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void Shell_routes_both_views_to_their_own_view_models()
    {
        var xaml = ReadProjectFile("MainWindow.xaml");

        Assert.Contains("<views:PowerView DataContext=\"{Binding PowerTest}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ConverterParameter=power}", xaml, StringComparison.Ordinal);
        Assert.Contains("<views:PowerMapView DataContext=\"{Binding PowerMap}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ConverterParameter=power-map}", xaml, StringComparison.Ordinal);
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
    public void Power_map_is_explicitly_a_phase_3_placeholder()
    {
        var xaml = ReadProjectFile(Path.Combine("Views", "PowerMapView.xaml"));
        var viewModel = ReadProjectFile(Path.Combine("ViewModels", "PowerMapViewModel.cs"));

        Assert.Contains("PhaseLabel", xaml, StringComparison.Ordinal);
        Assert.Contains("\"FASE 3\"", viewModel, StringComparison.Ordinal);
        Assert.Contains("placeholder", xaml, StringComparison.OrdinalIgnoreCase);
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

        Assert.Contains("Fluido e vaso", xaml, StringComparison.Ordinal);
        Assert.Contains("Impelidores", xaml, StringComparison.Ordinal);
        Assert.Contains("Condições", xaml, StringComparison.Ordinal);
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
}
