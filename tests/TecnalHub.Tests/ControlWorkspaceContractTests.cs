using System.IO;
using TecnalHub.Services.Persistence;
using Xunit;

namespace TecnalHub.Tests;

/// <summary>Operator-facing structure that must not regress back into detached cards.</summary>
public sealed class ControlWorkspaceContractTests
{
    private static readonly string ViewPath = Path.Combine(
        TestPaths.RepositoryRoot, "src", "TecnalHub", "Views", "ControlView.xaml");

    [Fact]
    public void Process_rows_and_external_devices_follow_the_requested_order()
    {
        var xaml = File.ReadAllText(ViewPath);
        var markers = new[]
        {
            "<!-- 1. Agitação -->",
            "<!-- 2. Temperatura -->",
            "<!-- 3. pH (com dropdown) -->",
            "<!-- 4. Pressão -->",
            "<!-- 5. Oxigênio -->",
            "<!-- 6. Dosagem de Nutrientes (com dropdown) -->",
            "<!-- 7. Dosagem de Antiespumante (com dropdown) -->",
            "Text=\"Dispositivos Externos\"",
            "<!-- 8. Vazão de Ar (com dropdown) -->",
            "<!-- 9. Sensor de Distância (Controle de espuma) -->",
            "<!-- 10. Bomba Dosadora Externa (roadmap) -->",
            "<!-- 11. Sensor de Biomassa -->",
            "<!-- 12. Frasco Agitador -->",
        };

        var previous = -1;
        foreach (var marker in markers)
        {
            var current = xaml.IndexOf(marker, previous + 1, StringComparison.Ordinal);
            Assert.True(current > previous, $"Linha ausente ou fora de ordem: {marker}");
            previous = current;
        }
    }

    [Fact]
    public void Unified_table_keeps_headers_drawers_and_automatic_apply_contract()
    {
        var xaml = File.ReadAllText(ViewPath);
        var codeBehind = File.ReadAllText(Path.ChangeExtension(ViewPath, ".xaml.cs"));

        Assert.Contains("Text=\"Valor Lido\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Setpoint\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Novo Setpoint\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Confira e aplique toda a configuração", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Parâmetros do processo\"", xaml, StringComparison.Ordinal);
        Assert.Equal(8, Count(xaml, "Tag=\"ExpanderToggle\""));
        Assert.DoesNotContain("ProvenanceBadge Text=\"comandado\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Aplicar", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("(0 = desligado)", xaml, StringComparison.Ordinal);
        Assert.Contains("DataContext.IsExpandedFlow, RelativeSource={RelativeSource AncestorType=UserControl}", xaml, StringComparison.Ordinal);
        Assert.Contains("Thumb.DragCompletedEvent", codeBehind, StringComparison.Ordinal);
        Assert.Contains("dataContext is BiomassControlViewModel", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Variable_labels_are_left_aligned_and_setpoint_editors_stay_compact()
    {
        var xaml = File.ReadAllText(ViewPath);

        Assert.Equal(5, Count(xaml, "HorizontalAlignment=\"Left\" HorizontalContentAlignment=\"Left\""));
        Assert.Contains("<Setter Property=\"Width\" Value=\"18\" />", xaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Margin\" Value=\"0,0,8,0\" />", xaml, StringComparison.Ordinal);
        Assert.Equal(14, Count(xaml, "<ColumnDefinition Width=\"160\" />"));
        Assert.Contains("Width=\"90\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void External_pump_drawer_exposes_the_implemented_profile_controls()
    {
        var xaml = File.ReadAllText(ViewPath);
        var start = xaml.IndexOf("<!-- 10. Bomba Dosadora Externa", StringComparison.Ordinal);
        var end = xaml.IndexOf("<!-- 11. Sensor de Biomassa", start, StringComparison.Ordinal);
        var section = xaml[start..end];

        Assert.Contains("IsExpandedExternalPump", section, StringComparison.Ordinal);
        Assert.Contains("DataContext=\"{Binding PumpControl}\"", section, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding IsEnabled, Mode=TwoWay}\"", section, StringComparison.Ordinal);
        Assert.Contains("<ctl:PumpPreviewChart", section, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding ModeOptions}\"", section, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedModeOption, Mode=TwoWay}\"", section, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ApplyProfileCommand}\"", section, StringComparison.Ordinal);
        Assert.DoesNotContain("o controle ainda não está implementado", section, StringComparison.Ordinal);
    }

    [Fact]
    public void Biomass_row_exposes_a_validated_optimal_threshold_entry()
    {
        var xaml = File.ReadAllText(ViewPath);
        var start = xaml.IndexOf("<!-- 11. Sensor de Biomassa", StringComparison.Ordinal);
        var end = xaml.IndexOf("<!-- 12. Frasco Agitador", start, StringComparison.Ordinal);
        var section = xaml[start..end];

        Assert.Contains("Text=\"{Binding OptimalThresholdText, UpdateSourceTrigger=PropertyChanged}\"", section, StringComparison.Ordinal);
        Assert.Contains("maior que o baixo e menor que o alto", section, StringComparison.Ordinal);
    }

    [Fact]
    public void Pump_preview_reuses_the_single_right_axis()
    {
        var chartCode = File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot, "src", "TecnalHub", "Controls", "PumpPreviewChart.xaml.cs"));

        Assert.Contains("plot.Axes.Right", chartCode, StringComparison.Ordinal);
        Assert.DoesNotContain("plot.Axes.AddRightAxis()", chartCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Requested_equipment_defaults_are_consistent_with_the_default_preset()
    {
        var settings = new AppSettings();
        var preset = SetpointPreset.DefaultPreset;

        Assert.Equal(PressureUnitPreference.MmHg, settings.Units.Pressure);
        Assert.Equal(new PHControlSettings(), preset.PHControl);
        Assert.Equal(new NutrientControlSettings(), settings.NutrientControl);
        Assert.Equal(new AntifoamControlSettings(), settings.AntifoamControl);
    }

    [Fact]
    public void Row_dots_are_green_only_while_the_loop_is_active()
    {
        var xaml = File.ReadAllText(ViewPath);

        // A dot pinned to a state cannot report whether the loop is running.
        Assert.DoesNotContain("<ctl:StateDot Grid.Column=\"1\" State=\"Ok\"", xaml, StringComparison.Ordinal);
        Assert.Equal(5, Count(xaml, "Converter=\"{StaticResource ActiveVariableState}\""));
        Assert.Equal(7, Count(xaml, "Converter={StaticResource ActiveToState}"));
    }

    [Fact]
    public void External_devices_are_only_operable_while_the_link_is_up()
    {
        var xaml = File.ReadAllText(ViewPath);
        var start = xaml.IndexOf("Text=\"Dispositivos Externos\"", StringComparison.Ordinal);
        var section = xaml[start..];

        Assert.Contains("DataContext.CanActuate", xaml, StringComparison.Ordinal);

        // Five device rows plus the pump and biomass drawer twins.
        Assert.Equal(7, Count(section, "ExternalDeviceToggleStyle"));

        // Air flow, distance and flask agitator are the rows with a setpoint entry.
        Assert.Equal(3, Count(section, "ExternalDeviceEntryStyle"));
    }

    [Fact]
    public void Pressure_row_is_labelled_as_the_relief_valve()
    {
        var xaml = File.ReadAllText(ViewPath);
        var start = xaml.IndexOf("<!-- 4. Pressão -->", StringComparison.Ordinal);
        var end = xaml.IndexOf("<!-- 5. Oxigênio -->", start, StringComparison.Ordinal);
        var row = xaml[start..end];

        Assert.Contains("Text=\"Alívio de Pressão\"", row, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding Subsystem.DisplayName}\"", row, StringComparison.Ordinal);
    }

    private static int Count(string value, string term)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(term, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += term.Length;
        }

        return count;
    }
}
