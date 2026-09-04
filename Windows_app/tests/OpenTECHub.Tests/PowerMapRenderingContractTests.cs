using System;
using System.IO;
using OpenTECHub.Services.PowerMapping;
using ScottPlot;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// Rendering contracts the power surface depends on, answered by pixels rather than by opinion
/// (§18.3 step 8.3). The map leaves everything outside the convex hull of the anchors undefined,
/// so both the heatmap and the contour overlay have to cope with NaN without inventing geometry.
/// </summary>
public sealed class PowerMapRenderingContractTests
{
    [Fact]
    public void Heatmap_leaves_nan_cells_unpainted()
    {
        // Left half finite, right half undefined. If NaN were painted the right half would carry
        // colour and the two sides would look alike.
        var values = new double[2, 2]
        {
            { 1.0, double.NaN },
            { 1.0, double.NaN },
        };

        var plot = new Plot();
        var heatmap = plot.Add.Heatmap(values);
        heatmap.Colormap = new ScottPlot.Colormaps.Viridis();
        heatmap.Rectangle = new CoordinateRect(0, 1, 0, 1);
        plot.Axes.SetLimits(0, 1, 0, 1);
        plot.Axes.Frameless();

        var (left, right) = SampleHalves(plot);

        Assert.True(
            Math.Abs(left - right) > 8.0,
            $"NaN cells must not be painted like finite ones; got left={left:0.0}, right={right:0.0}");
    }

    [Fact]
    public void Isolines_are_never_extracted_over_undefined_cells()
    {
        // A ramp in N on the left half of the plane, undefined on the right. Every segment has to
        // stay on the measured half - a contour crossing into the undefined half would be geometry
        // the assay never produced (§19).
        const int n = 21;
        var field = new double[n, n];
        var xGrid = new double[n];
        var yGrid = new double[n];

        for (var i = 0; i < n; i++)
        {
            xGrid[i] = (double)i / (n - 1);
            yGrid[i] = (double)i / (n - 1);
        }

        for (var row = 0; row < n; row++)
        {
            for (var column = 0; column < n; column++)
            {
                field[row, column] = column < n / 2 ? yGrid[row] : double.NaN;
            }
        }

        var levels = PowerMapContours.Extract(field, xGrid, yGrid, levelCount: 6);

        Assert.NotEmpty(levels);

        // The last fully finite cell ends at column (n/2 - 1), so nothing may be drawn beyond it.
        var lastFiniteX = xGrid[(n / 2) - 1];
        foreach (var level in levels)
        {
            Assert.NotEmpty(level.Segments);
            foreach (var segment in level.Segments)
            {
                Assert.True(
                    segment.X1 <= lastFiniteX + 1e-9 && segment.X2 <= lastFiniteX + 1e-9,
                    $"segment ({segment.X1:0.000},{segment.Y1:0.000})-({segment.X2:0.000},{segment.Y2:0.000}) " +
                    $"crossed into the undefined half beyond x={lastFiniteX:0.000}");
            }
        }
    }

    [Fact]
    public void Isolines_follow_the_level_they_are_named_after()
    {
        // f(N, Qg) = N, so the isoline at value v must be the horizontal line y = v.
        const int n = 11;
        var field = new double[n, n];
        var grid = new double[n];
        for (var i = 0; i < n; i++)
        {
            grid[i] = i;
        }

        for (var row = 0; row < n; row++)
        {
            for (var column = 0; column < n; column++)
            {
                field[row, column] = grid[row];
            }
        }

        var levels = PowerMapContours.Extract(field, grid, grid, levelCount: 4);

        Assert.Equal(4, levels.Count);
        foreach (var level in levels)
        {
            foreach (var segment in level.Segments)
            {
                Assert.Equal(level.Value, segment.Y1, 6);
                Assert.Equal(level.Value, segment.Y2, 6);
            }
        }
    }

    [Fact]
    public void Isolines_are_empty_for_a_flat_or_fully_undefined_field()
    {
        const int n = 6;
        var grid = new double[n];
        for (var i = 0; i < n; i++)
        {
            grid[i] = i;
        }

        var flat = new double[n, n];
        var undefinedField = new double[n, n];
        for (var row = 0; row < n; row++)
        {
            for (var column = 0; column < n; column++)
            {
                flat[row, column] = 7.0;
                undefinedField[row, column] = double.NaN;
            }
        }

        Assert.Empty(PowerMapContours.Extract(flat, grid, grid));
        Assert.Empty(PowerMapContours.Extract(undefinedField, grid, grid));
    }

    [Fact]
    public void Saddle_uses_bilinear_asymptotic_decider_instead_of_cell_average()
    {
        // Case 5 at level zero. The arithmetic centre is negative, but the bilinear
        // determinant is positive: the high BL/TR regions are connected through the cell.
        var field = new[,]
        {
            { 2.0, -0.1 },
            { -30.0, 2.0 },
        };
        var grid = new[] { 0.0, 1.0 };

        var contour = Assert.Single(PowerMapContours.Extract(
            field, grid, grid, levelCount: 1, minValue: -1, maxValue: 1));

        Assert.Equal(2, contour.Segments.Count);
        Assert.Contains(contour.Segments, segment =>
            Math.Abs(segment.X1) < 1e-12 && Math.Abs(segment.Y2 - 1.0) < 1e-12);
        Assert.Contains(contour.Segments, segment =>
            Math.Abs(segment.Y1) < 1e-12 && Math.Abs(segment.X2 - 1.0) < 1e-12);
    }


    /// <summary>
    /// Every power plot view subscribes to the theme service and to its view model's redraw signal.
    /// Both are longer-lived than the control, so both have to come back off on unload or the
    /// ScottPlot surfaces stay rooted for the life of the process (§18.3 step 8.3).
    /// </summary>
    [Theory]
    [InlineData("PowerMapView.xaml.cs")]
    [InlineData("PowerImpellerComparisonView.xaml.cs")]
    public void Plot_views_release_their_subscriptions_on_unload(string viewFileName)
    {
        var source = File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot, "src", "OpenTECHub", "Views", viewFileName));

        Assert.Contains("Unloaded += OnUnloaded", source, StringComparison.Ordinal);
        Assert.Contains("UnsubscribeFromThemeChanges", source, StringComparison.Ordinal);
        Assert.Contains("theme.ThemeChanged -= OnThemeChanged", source, StringComparison.Ordinal);
        Assert.Contains("VisualizationChanged -= Redraw", source, StringComparison.Ordinal);

        // Attach must detach first, or navigating back and forth stacks handlers on the same event.
        var attachIndex = source.IndexOf("private void Attach()", StringComparison.Ordinal);
        Assert.True(attachIndex > 0, "Attach() is the single place the redraw handler is wired");
        var detachInAttach = source.IndexOf("Detach();", attachIndex, StringComparison.Ordinal);
        var subscribeInAttach = source.IndexOf("VisualizationChanged += Redraw", attachIndex, StringComparison.Ordinal);
        Assert.True(
            detachInAttach > 0 && detachInAttach < subscribeInAttach,
            "Attach() must call Detach() before subscribing again");
    }

    /// <summary>The map view model owns per-item handlers, so it has to let them go on dispose.</summary>
    [Fact]
    public void Power_map_view_model_unhooks_its_per_assay_handlers_on_dispose()
    {
        var source = File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot, "src", "OpenTECHub", "ViewModels", "PowerMapViewModel.cs"));

        var disposeIndex = source.IndexOf("public void Dispose()", StringComparison.Ordinal);
        Assert.True(disposeIndex > 0);

        var disposeBody = source[disposeIndex..];
        Assert.Contains("PropertyChanged -= OnPowerTestSelectionChanged", disposeBody, StringComparison.Ordinal);
        Assert.Contains("_reconstructionCts?.Cancel()", disposeBody, StringComparison.Ordinal);

        // Rebuilding the assay list must also drop the handlers of the items it discards.
        var reloadIndex = source.IndexOf("public void ReloadPowerTests()", StringComparison.Ordinal);
        Assert.True(reloadIndex > 0);
        var reloadBody = source[reloadIndex..(reloadIndex + 900)];
        Assert.Contains("PropertyChanged -= OnPowerTestSelectionChanged", reloadBody, StringComparison.Ordinal);
    }

    /// <summary>Mean per-pixel deviation from the background colour, for the left and right halves.</summary>
    private static (double Left, double Right) SampleHalves(Plot plot)
    {
        const int size = 240;
        // Both backgrounds forced to the same white so "ink" means drawn content, not the
        // figure/data background seam that a frameless plot still leaves at the corners.
        plot.FigureBackground.Color = Colors.White;
        plot.DataBackground.Color = Colors.White;

        var file = Path.Combine(Path.GetTempPath(), $"power-map-render-{Guid.NewGuid():N}.png");
        try
        {
            plot.SavePng(file, size, size);
            var bytes = File.ReadAllBytes(file);
            using var stream = new MemoryStream(bytes);
            var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(
                stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);

            var width = converted.PixelWidth;
            var height = converted.PixelHeight;
            var stride = width * 4;
            var pixels = new byte[height * stride];
            converted.CopyPixels(pixels, stride, 0);

            // The background is whatever the corner pixel is; everything is measured against it.
            var bgB = pixels[0];
            var bgG = pixels[1];
            var bgR = pixels[2];

            double leftSum = 0, rightSum = 0;
            var half = width / 2;
            var leftCount = 0;
            var rightCount = 0;

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var i = (y * stride) + (x * 4);
                    var deviation =
                        Math.Abs(pixels[i] - bgB) +
                        Math.Abs(pixels[i + 1] - bgG) +
                        Math.Abs(pixels[i + 2] - bgR);

                    // Keep a margin away from the seam so antialiasing on the boundary is not counted.
                    if (x < half - 8)
                    {
                        leftSum += deviation;
                        leftCount++;
                    }
                    else if (x > half + 8)
                    {
                        rightSum += deviation;
                        rightCount++;
                    }
                }
            }

            return (leftSum / Math.Max(1, leftCount), rightSum / Math.Max(1, rightCount));
        }
        finally
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }
}
