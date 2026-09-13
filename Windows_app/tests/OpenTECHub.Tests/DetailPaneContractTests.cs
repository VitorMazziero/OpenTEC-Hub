using System.IO;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class DetailPaneContractTests
{
    private static string ReadView(string name) => File.ReadAllText(Path.Combine(
        TestPaths.RepositoryRoot, "src", "OpenTECHub", "Views", name));

    [Fact]
    public void Scalar_detail_uses_automatic_apply_and_has_no_advanced_placeholder()
    {
        var xaml = ReadView("DetailPaneView.xaml");
        var code = ReadView("DetailPaneView.xaml.cs");

        Assert.DoesNotContain("Content=\"Aplicar\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Reverter\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Configurações avançadas", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Contagem bruta, estado do filtro", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Confirmado pelo equipamento", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Subsistema ativo", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnToggleClicked\"", xaml, StringComparison.Ordinal);
        Assert.Contains("StackPanel Margin=\"0,0,16,0\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TextBox.LostKeyboardFocusEvent", code, StringComparison.Ordinal);
        Assert.Contains("e.Key == Key.Enter", code, StringComparison.Ordinal);
        Assert.Contains("OpenCalibrationCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("SensorHealthView", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Ph_detail_matches_the_common_panel_and_opens_the_ph_calibration_tab()
    {
        var xaml = ReadView("PHControlView.xaml");
        var code = ReadView("PHControlView.xaml.cs");

        Assert.Contains("Text=\"PV\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"SP\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Δ\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<ctl:TrendSpark", xaml, StringComparison.Ordinal);
        Assert.Contains("<Expander Header=\"Controle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<Expander Header=\"Calibração\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CommandParameter=\"ph\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<Expander Header=\"Saúde do sensor\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Dosagem e calibração são independentes", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Configurações avançadas", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Aplicar", xaml, StringComparison.Ordinal);
        Assert.Contains("TextBox.LostKeyboardFocusEvent", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Calibration_destination_selects_the_requested_tab()
    {
        var xaml = ReadView("CalibrationView.xaml");
        var vm = File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot, "src", "OpenTECHub", "ViewModels", "CalibrationViewModel.cs"));

        Assert.Contains("SelectedIndex=\"{Binding SelectedTabIndex, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("\"ph\" => 0", vm, StringComparison.Ordinal);
        Assert.Contains("\"oxygen\" => 1", vm, StringComparison.Ordinal);
        Assert.Contains("\"flow\" => 2", vm, StringComparison.Ordinal);
        Assert.Contains("\"pump\" => 3", vm, StringComparison.Ordinal);
        Assert.Contains("\"biomass\" => 4", vm, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PumpPlotHost\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PumpRightScroll\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource CalibrationSidePanelStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Controle manual · preencher mangueira", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Leitura e ecos do nó", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Pré-visualização da vazão calculada", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Headless_startup_can_bypass_the_windows_workspace_picker()
    {
        var app = File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot, "src", "OpenTECHub", "App.xaml.cs"));

        Assert.Contains("\"--workspace\"", app, StringComparison.Ordinal);
        Assert.Contains("\"--no-workspace-prompt\"", app, StringComparison.Ordinal);
        Assert.Contains("Path.GetFullPath(explicitWorkspace)", app, StringComparison.Ordinal);
    }
}
