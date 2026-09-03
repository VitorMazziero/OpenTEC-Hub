using System.IO;
using OpenTECHub.Services.Persistence;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>Operator-facing structure that must not regress back into detached cards.</summary>
public sealed class ControlWorkspaceContractTests
{
    private static readonly string ViewPath = Path.Combine(
        TestPaths.RepositoryRoot, "src", "OpenTECHub", "Views", "ControlView.xaml");

    [Fact]
    public void Process_rows_and_external_devices_follow_the_requested_order()
    {
        var xaml = File.ReadAllText(ViewPath);
        var markers = new[]
        {
            "<!-- 1. Agitação (com o servo drive na gaveta) -->",
            "<!-- 2. Temperatura -->",
            "<!-- 3. pH (com dropdown) -->",
            "<!-- 4. Pressão -->",
            "<!-- 5. Oxigênio -->",
            "<!-- 6. Nutrientes (com dropdown) -->",
            "<!-- 7. Antiespumante (com dropdown) -->",
            "Text=\"Dispositivos Externos\"",
            "<!-- 8. Vazão de Ar (com dropdown) -->",
            "<!-- 9. Distância (Controle de espuma) -->",
            "<!-- 10. Bomba Externa (roadmap) -->",
            "<!-- 11. Absorbância -->",
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
        Assert.Equal(11, Count(xaml, "Tag=\"ExpanderToggle\""));
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
        var start = xaml.IndexOf("<!-- 10. Bomba Externa", StringComparison.Ordinal);
        var end = xaml.IndexOf("<!-- 11. Absorbância", start, StringComparison.Ordinal);
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
        var start = xaml.IndexOf("<!-- 11. Absorbância", StringComparison.Ordinal);
        var end = xaml.IndexOf("<!-- 12. Frasco Agitador", start, StringComparison.Ordinal);
        var section = xaml[start..end];

        Assert.Contains("Text=\"{Binding OptimalThresholdText, UpdateSourceTrigger=PropertyChanged}\"", section, StringComparison.Ordinal);
        Assert.Contains("maior que o baixo e menor que o alto", section, StringComparison.Ordinal);
    }

    [Fact]
    public void Pump_preview_reuses_the_single_right_axis()
    {
        var chartCode = File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot, "src", "OpenTECHub", "Controls", "PumpPreviewChart.xaml.cs"));

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
        // The seven internal rows report their effective loop state. The five external rows
        // use ExternalDeviceState below so known device absence can outrank operator intent.
        Assert.DoesNotContain("Converter=\"{StaticResource ActiveVariableState}\"", xaml, StringComparison.Ordinal);
        Assert.Equal(7, Count(xaml, "Converter={StaticResource ActiveToState}"));
    }

    /// <summary>
    /// The four external-device rows bound their dot straight to the operator's own enable
    /// checkbox, so the page reported their intent back at them and called it hardware
    /// state. The dot now takes the Hub's presence as well, and a reported absence outranks
    /// the switch.
    /// </summary>
    [Fact]
    public void External_device_dots_report_the_device_and_not_the_operator_checkbox()
    {
        var xaml = File.ReadAllText(ViewPath);

        // Five external rows: level/foam, pump, biomass, agitator and the flowmeter. The
        // servo drive is not among them - it lives in the Agitação drawer, because it is
        // internal to the module and nobody switches it on. Its chips are there, which is
        // why the chip count is one higher than the dot count.
        Assert.Equal(5, Count(xaml, "StaticResource ExternalDeviceState"));
        Assert.Equal(6, Count(xaml, "<ctl:ExternalDeviceChips"));

        // The dot takes the same three signals the chip does, so the two can never disagree.
        Assert.Equal(5, Count(xaml, "Status.ShowRoutingChipOnly"));
    }

    /// <summary>
    /// The bench potentiometer outranks the app whenever the Hub has it enabled, so the
    /// staged percent on the agitator row is not what the motor is holding. That cannot
    /// live in a tooltip.
    /// </summary>
    [Fact]
    public void The_agitator_row_shows_when_the_potentiometer_holds_the_motor()
    {
        var xaml = File.ReadAllText(ViewPath);

        Assert.Contains("Text=\"potenciômetro\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsPotentiometerInControl", xaml, StringComparison.Ordinal);
        Assert.Contains("Comandado por: {0}", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void External_devices_are_only_operable_while_the_link_is_up()
    {
        var xaml = File.ReadAllText(ViewPath);
        var start = xaml.IndexOf("Text=\"Dispositivos Externos\"", StringComparison.Ordinal);
        var section = xaml[start..];

        Assert.Contains("Status.IsOnline", xaml, StringComparison.Ordinal);

        // One switch per external row. Pump and biomass no longer duplicate their switch
        // inside the drawer; four non-flow devices use the stricter node-online style.
        Assert.Equal(5, Count(section, "ExternalDeviceToggleStyle"));
        Assert.Equal(4, Count(section, "ConnectedExternalDeviceToggleStyle"));

        // Distance, biomass and flask editors require actual node presence, not merely the
        // app-to-Hub link. The flowmeter retains its acknowledgement-aware inline guard.
        Assert.Equal(10, Count(section, "ConnectedExternalDeviceEntryStyle"));
    }

    [Fact]
    public void External_drawers_share_the_theme_and_do_not_duplicate_primary_switches()
    {
        var xaml = File.ReadAllText(ViewPath);
        var markers = new[]
        {
            "<!-- 8. Vazão de Ar",
            "<!-- 9. Distância",
            "<!-- 10. Bomba Externa",
            "<!-- 11. Absorbância",
            "<!-- 12. Frasco Agitador",
        };

        for (var index = 0; index < markers.Length; index++)
        {
            var start = xaml.IndexOf(markers[index], StringComparison.Ordinal);
            var end = index + 1 < markers.Length
                ? xaml.IndexOf(markers[index + 1], start, StringComparison.Ordinal)
                : xaml.IndexOf("<!-- Bottom Action & Safety Bar -->", start, StringComparison.Ordinal);
            var device = xaml[start..end];

            Assert.Contains("Background=\"{DynamicResource SurfaceHoverBrush}\"", device, StringComparison.Ordinal);
            Assert.Contains("CornerRadius=\"{DynamicResource RadiusSmall}\"", device, StringComparison.Ordinal);
            Assert.Contains("Margin=\"28,4,8,8\"", device, StringComparison.Ordinal);
            Assert.Equal(1, Count(device, "ExternalDeviceToggleStyle"));
        }

        Assert.DoesNotContain("Modo automático pelo potenciômetro", xaml, StringComparison.Ordinal);
        Assert.Contains("Automação por espuma", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource AppSliderStyle}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Contagens brutas de integração", xaml, StringComparison.Ordinal);
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
