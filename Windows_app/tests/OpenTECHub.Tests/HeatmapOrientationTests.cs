using System;
using System.IO;
using System.Windows.Media.Imaging;
using ScottPlot;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// The orientation contract the kLa surface and headroom maps are built on.
/// </summary>
/// <remarks>
/// <para>
/// Both maps fill <c>values[row, column]</c> with row 0 at the <b>minimum</b> agitation,
/// because that is the order the engine writes <c>HeadroomScores</c> in. ScottPlot draws
/// row 0 at the top by default - image order, not axis order - so both call sites set
/// <c>FlipVertically = true</c> to put the minimum back at the bottom of the Y axis.
/// </para>
/// <para>
/// That single boolean has now been flipped twice by eye. This test renders a heatmap
/// and looks at the pixels, so the next person to touch it gets an answer instead of an
/// opinion.
/// </para>
/// </remarks>
public sealed class HeatmapOrientationTests
{
    [Fact]
    public void FlipVertically_true_puts_row_zero_at_the_bottom_of_the_axis()
    {
        // Row 0 dark, row 1 bright - the same convention as an agitation axis whose
        // minimum lives in row 0.
        var (top, bottom) = RenderRowBrightness(flipVertically: true);

        Assert.True(
            top > bottom,
            $"row 1 (the maximum) must render at the top; got top={top:0.0}, bottom={bottom:0.0}");
    }

    [Fact]
    public void FlipVertically_false_puts_row_zero_at_the_top_of_the_axis()
    {
        var (top, bottom) = RenderRowBrightness(flipVertically: false);

        Assert.True(
            bottom > top,
            $"without the flip row 0 renders at the top; got top={top:0.0}, bottom={bottom:0.0}");
    }

    private static (double Top, double Bottom) RenderRowBrightness(bool flipVertically)
    {
        var plot = new Plot();
        var heatmap = plot.Add.Heatmap(new double[,] { { 0, 0 }, { 1, 1 } });
        heatmap.Colormap = new ScottPlot.Colormaps.Viridis();
        heatmap.Rectangle = new CoordinateRect(0, 1, 0, 1);
        heatmap.FlipVertically = flipVertically;
        plot.Axes.SetLimits(0, 1, 0, 1);
        plot.Axes.Frameless();

        const int size = 200;
        var file = Path.Combine(Path.GetTempPath(), $"heatmap-orientation-{Guid.NewGuid():N}.png");
        try
        {
            plot.SavePng(file, size, size);
            var bitmap = new FormatConvertedBitmap(
                BitmapFrame.Create(
                    new Uri(file),
                    BitmapCreateOptions.None,
                    BitmapCacheOption.OnLoad),
                System.Windows.Media.PixelFormats.Bgra32,
                null,
                0);

            return (Brightness(bitmap, size / 2, size / 4), Brightness(bitmap, size / 2, size * 3 / 4));
        }
        finally
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    /// <summary>Viridis runs dark at the low end and bright at the high end.</summary>
    private static double Brightness(BitmapSource bitmap, int x, int y)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return (0.114 * pixel[0]) + (0.587 * pixel[1]) + (0.299 * pixel[2]);
    }
}
