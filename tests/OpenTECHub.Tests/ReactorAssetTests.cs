using System.Buffers.Binary;
using System.IO;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class ReactorAssetTests
{
    private static readonly string ImagePath = Path.Combine(
        TestPaths.RepositoryRoot,
        "docs", "UI_design_guides", "Bioreactor references", "Imagem_biorreator_side.png");

    [Fact]
    public void Approved_reactor_side_view_is_high_resolution_rgba_png()
    {
        var bytes = File.ReadAllBytes(ImagePath);

        Assert.True(bytes.Length > 1_000_000, "The bundled reactor appears unexpectedly compressed or empty.");
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes[..8]);
        Assert.Equal(1920, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)));
        Assert.Equal(1920, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
        Assert.Equal(8, bytes[24]); // bit depth
        Assert.Equal(6, bytes[25]); // PNG colour type 6: truecolour with alpha
    }

    [Fact]
    public void Settings_uses_only_the_approved_reactor_figure()
    {
        var settingsXaml = File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot, "src", "OpenTECHub", "Views", "SettingsView.xaml"));

        Assert.Contains("Imagem_biorreator_side.png", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("reactor-neutral.png", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("reactor-impellers.png", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("VectorFallback", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Polyline", settingsXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Synoptic_groups_compact_cards_with_control_workspace_names()
    {
        var xaml = File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot, "src", "OpenTECHub", "Views", "SynopticView.xaml"));

        var internalStart = xaml.IndexOf("Text=\"PARÂMETROS INTERNOS\"", StringComparison.Ordinal);
        var externalStart = xaml.IndexOf("Text=\"DISPOSITIVOS EXTERNOS\"", StringComparison.Ordinal);

        Assert.True(internalStart >= 0, "Cabeçalho de parâmetros internos ausente.");
        Assert.True(externalStart > internalStart, "Cabeçalho de dispositivos externos ausente ou fora de ordem.");

        // Grouped by how each device is wired, not by what it measures. The two cards
        // used to be the other way round: the biomass and distance sensors are separate
        // ESP32s on the Hub's SoftAP, while the nutrient and antifoam pumps live inside the
        // OpenTEC module on its internal UART. An operator diagnosing a dropout has to know
        // which of the two links to go and look at.
        var internalSection = xaml[internalStart..externalStart];
        foreach (var label in new[]
                 {
                     "Agitação",
                     "Temperatura",
                     "pH",
                     "Oxigênio",
                     "Alívio de Pressão",
                     "Dosagem de Nutrientes",
                     "Dosagem de Antiespumante",
                 })
        {
            Assert.Contains($"Tag=\"{label}\"", internalSection, StringComparison.Ordinal);
        }

        var externalSection = xaml[externalStart..];
        foreach (var label in new[]
                 {
                     "Vazão de Ar",
                     "Sensor de Distância",
                     "Sensor de Biomassa",
                     "Bomba Dosadora Externa",
                 })
        {
            Assert.Contains($"Tag=\"{label}\"", externalSection, StringComparison.Ordinal);
        }

        Assert.Contains("Orientation=\"Horizontal\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalScrollBarVisibility=\"Auto\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"Wrap\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"COMANDADO\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"MinWidth\" Value=\"85\" />", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CommandParameter=\"agitator\"", xaml, StringComparison.Ordinal);
    }

}
