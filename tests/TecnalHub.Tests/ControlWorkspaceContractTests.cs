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
        Assert.Equal(8, Count(xaml, "Tag=\"ExpanderToggle\""));
        Assert.DoesNotContain("Content=\"Aplicar", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("(0 = desligado)", xaml, StringComparison.Ordinal);
        Assert.Contains("ImageFailed", File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot, "src", "TecnalHub", "Views", "SynopticView.xaml")),
            StringComparison.Ordinal);
        Assert.Contains("Thumb.DragCompletedEvent", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void External_pump_placeholder_can_open_but_cannot_actuate()
    {
        var xaml = File.ReadAllText(ViewPath);
        var start = xaml.IndexOf("<!-- 10. Bomba Dosadora Externa", StringComparison.Ordinal);
        var end = xaml.IndexOf("<!-- 11. Sensor de Biomassa", start, StringComparison.Ordinal);
        var section = xaml[start..end];

        Assert.DoesNotContain(
            "<Grid Margin=\"0,4\" VerticalAlignment=\"Center\" IsEnabled=\"False\">",
            section,
            StringComparison.Ordinal);
        Assert.Contains("IsExpandedExternalPump", section, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"False\" IsEnabled=\"False\"", section, StringComparison.Ordinal);
        Assert.Contains("o controle ainda não está implementado", section, StringComparison.Ordinal);
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
