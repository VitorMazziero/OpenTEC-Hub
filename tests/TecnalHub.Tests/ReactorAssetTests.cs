using System.Buffers.Binary;
using System.IO;
using Xunit;

namespace TecnalHub.Tests;

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
    public void Synoptic_uses_only_the_approved_reactor_figure()
    {
        var xaml = File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot, "src", "TecnalHub", "Views", "SynopticView.xaml"));

        Assert.Contains("Imagem_biorreator_side.png", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("reactor-neutral.png", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("reactor-impellers.png", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("VectorFallback", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Polyline", xaml, StringComparison.Ordinal);
        Assert.Contains("CommandParameter=\"ph\"", xaml, StringComparison.Ordinal);

        // WP7 dosing tags share the synoptic; the flask agitator deliberately does not,
        // because it is a separate bench device.
        Assert.Contains("CommandParameter=\"nutrient\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CommandParameter=\"antifoam\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CommandParameter=\"level\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CommandParameter=\"agitator\"", xaml, StringComparison.Ordinal);

        // Phase 3 sensors on the drawing; the flask agitator stays off it.
        Assert.Contains("CommandParameter=\"biomass\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CommandParameter=\"pump\"", xaml, StringComparison.Ordinal);
        Assert.Contains("bomba externa em mL/min", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Synoptic_groups_compact_cards_with_control_workspace_names()
    {
        var xaml = File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot, "src", "TecnalHub", "Views", "SynopticView.xaml"));

        var internalStart = xaml.IndexOf("Text=\"PARÂMETROS INTERNOS\"", StringComparison.Ordinal);
        var externalStart = xaml.IndexOf("Text=\"DISPOSITIVOS EXTERNOS\"", StringComparison.Ordinal);

        Assert.True(internalStart >= 0, "Cabeçalho de parâmetros internos ausente.");
        Assert.True(externalStart > internalStart, "Cabeçalho de dispositivos externos ausente ou fora de ordem.");

        // Grouped by how each device is wired, not by what it measures. The two cards
        // used to be the other way round: the biomass and distance sensors are separate
        // ESP32s on the Hub's SoftAP, while the nutrient and antifoam pumps live inside the
        // TECNAL module on its internal UART. An operator diagnosing a dropout has to know
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

        // Presence is shown for every external node, not only the flowmeter — which was
        // the one device on this page whose absence an operator could actually see.
        var chipCount = externalSection.Split("<ctl:ExternalDeviceChips").Length - 1;
        Assert.Equal(3, chipCount);
        Assert.Contains("FlowControl.IsFlowmeterOffline", externalSection, StringComparison.Ordinal);

        Assert.Contains("<UniformGrid Columns=\"2\">", xaml, StringComparison.Ordinal);
        Assert.Contains("<ScrollViewer Grid.Column=\"0\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<Viewbox Grid.Column=\"2\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Viewbox Grid.Row=\"1\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"Wrap\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"COMANDADO\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CommandParameter=\"agitator\"", xaml, StringComparison.Ordinal);
    }

}
