using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace TecnalHub.Tests;

public sealed class ReactorAssetTests
{
    private static readonly string ImagePath = Path.Combine(
        TestPaths.RepositoryRoot,
        "src", "TecnalHub", "Resources", "Images", "reactor-neutral.png");

    private static readonly string AnchorPath = Path.Combine(
        TestPaths.RepositoryRoot,
        "src", "TecnalHub", "Resources", "Images", "reactor-anchors.json");

    [Fact]
    public void Reactor_master_is_high_resolution_rgba_png()
    {
        var bytes = File.ReadAllBytes(ImagePath);

        Assert.True(bytes.Length > 1_000_000, "The bundled reactor appears unexpectedly compressed or empty.");
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes[..8]);
        Assert.Equal(1024, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)));
        Assert.Equal(1536, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
        Assert.Equal(8, bytes[24]); // bit depth
        Assert.Equal(6, bytes[25]); // PNG colour type 6: truecolour with alpha
    }

    [Fact]
    public void Reactor_master_has_real_transparent_canvas_not_baked_checkerboard()
    {
        using var stream = File.OpenRead(ImagePath);
        var frame = new PngBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).Frames[0];
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);

        foreach (var point in new[]
                 {
                     new Point(0, 0),
                     new Point(frame.PixelWidth - 1, 0),
                     new Point(0, frame.PixelHeight - 1),
                     new Point(frame.PixelWidth - 1, frame.PixelHeight - 1),
                 })
        {
            Assert.Equal(0, AlphaAt(bgra, (int)point.X, (int)point.Y));
        }

        Assert.True(
            AlphaAt(bgra, frame.PixelWidth / 2, frame.PixelHeight / 2) > 240,
            "The equipment centre should be substantially opaque.");
    }

    [Fact]
    public void Every_live_callout_has_a_normalized_asset_anchor()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(AnchorPath));
        var anchors = document.RootElement.GetProperty("anchors");

        Assert.Equal(
            ["flow", "motor", "oxygen", "ph", "pressure", "temperature"],
            anchors.EnumerateObject().Select(p => p.Name).Order().ToArray());

        foreach (var anchor in anchors.EnumerateObject())
        {
            var x = anchor.Value.GetProperty("x").GetDouble();
            var y = anchor.Value.GetProperty("y").GetDouble();
            var role = anchor.Value.GetProperty("role").GetString();

            Assert.InRange(x, 0.0, 1.0);
            Assert.InRange(y, 0.0, 1.0);
            Assert.False(string.IsNullOrWhiteSpace(role));
        }
    }

    [Fact]
    public void Synoptic_keeps_live_state_native_and_has_a_decode_fallback()
    {
        var xaml = File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot, "src", "TecnalHub", "Views", "SynopticView.xaml"));

        Assert.Contains("reactor-neutral.png", xaml, StringComparison.Ordinal);
        Assert.Contains("OnReactorRenderFailed", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"VectorFallback\"", xaml, StringComparison.Ordinal);
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

    private static byte AlphaAt(BitmapSource source, int x, int y)
    {
        var pixel = new byte[4];
        source.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel[3];
    }
}
