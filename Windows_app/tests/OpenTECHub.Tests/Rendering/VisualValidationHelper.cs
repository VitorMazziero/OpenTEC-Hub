using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenTECHub.Tests.Rendering;

/// <summary>
/// Metric results for rendered in-memory bitmaps.
/// </summary>
public sealed record BitmapMetrics(
    int PixelWidth,
    int PixelHeight,
    double NonZeroPixelRatio,
    int SampledUniqueColors,
    bool IsNonTrivial);

/// <summary>
/// Automated layout and visual integrity validator for rendered views.
/// Detects text truncation, clipping, and invalid empty renders.
/// </summary>
public static class VisualValidationHelper
{
    /// <summary>
    /// Scans the visual tree starting at <paramref name="root"/> for TextBlocks
    /// whose text extends past their layout bounds without explicit trimming.
    /// </summary>
    public static List<string> CheckTextTruncation(DependencyObject root)
    {
        var issues = new List<string>();
        Traverse(root, node =>
        {
            if (node is TextBlock tb && tb.IsVisible && tb.ActualWidth > 0 && tb.ActualHeight > 0)
            {
                // Only flag if no trimming is specified and desired exceeds actual width by > 1.5 DIP
                if (tb.TextTrimming == TextTrimming.None && tb.DesiredSize.Width > tb.ActualWidth + 1.5)
                {
                    issues.Add(
                        $"TextBlock '{tb.Text}' truncated: desired {tb.DesiredSize.Width:F1}px > actual {tb.ActualWidth:F1}px");
                }
            }
        });

        return issues;
    }

    /// <summary>
    /// Validates that the bitmap contains rich graphical output rather than
    /// a flat blank/white/black error rectangle.
    /// </summary>
    public static BitmapMetrics ValidateBitmap(RenderTargetBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;

        if (width <= 0 || height <= 0)
        {
            return new BitmapMetrics(width, height, 0, 0, false);
        }

        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        var nonZeroPixels = 0;
        var uniqueColors = new HashSet<int>();
        var sampleStep = Math.Max(1, (width * height) / 2000); // sample ~2000 pixels

        for (var i = 0; i < pixels.Length; i += 4 * sampleStep)
        {
            var b = pixels[i];
            var g = pixels[i + 1];
            var r = pixels[i + 2];
            var a = pixels[i + 3];

            if (a > 0 && (r > 0 || g > 0 || b > 0))
            {
                nonZeroPixels++;
            }

            var argb = (a << 24) | (r << 16) | (g << 8) | b;
            uniqueColors.Add(argb);
        }

        var sampledTotal = Math.Max(1, pixels.Length / (4 * sampleStep));
        var nonZeroRatio = (double)nonZeroPixels / sampledTotal;
        var isNonTrivial = width >= 64 && height >= 64 && uniqueColors.Count >= 4 && nonZeroRatio > 0.05;

        return new BitmapMetrics(width, height, nonZeroRatio, uniqueColors.Count, isNonTrivial);
    }

    private static void Traverse(DependencyObject current, Action<DependencyObject> visitor)
    {
        visitor(current);
        var count = VisualTreeHelper.GetChildrenCount(current);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(current, i);
            Traverse(child, visitor);
        }
    }
}
