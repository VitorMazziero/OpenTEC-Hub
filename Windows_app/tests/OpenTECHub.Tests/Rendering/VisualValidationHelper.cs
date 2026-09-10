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
                // DesiredSize carries the margin, ActualWidth does not, so the margin has to
                // come off before the two are comparable - otherwise every labelled field
                // with a 6 DIP gap reads as truncated.
                var desiredText = tb.DesiredSize.Width - tb.Margin.Left - tb.Margin.Right;

                // Only flag if no trimming is specified and desired exceeds actual width by > 1.5 DIP
                if (tb.TextTrimming == TextTrimming.None && desiredText > tb.ActualWidth + 1.5)
                {
                    issues.Add(
                        $"TextBlock '{tb.Text}' truncated: desired {desiredText:F1}px > actual {tb.ActualWidth:F1}px");
                }
            }
        });

        return issues;
    }

    /// <summary>
    /// Scans for visible content that is arranged past the right edge of
    /// <paramref name="root"/>, which is how a fixed-width layout loses a column on a
    /// narrow window. Vertical overflow is not an error - the pages scroll - but
    /// horizontal overflow is: nothing in this shell scrolls sideways by design.
    /// </summary>
    /// <param name="root">The arranged root whose width defines the usable area.</param>
    /// <param name="tolerance">Slack, in DIP, for rounding and shadow bleed.</param>
    public static List<string> CheckHorizontalOverflow(FrameworkElement root, double tolerance = 1.0)
    {
        ArgumentNullException.ThrowIfNull(root);

        var issues = new List<string>();
        var limit = root.ActualWidth + tolerance;
        if (limit <= tolerance)
        {
            return issues;
        }

        Traverse(root, node =>
        {
            // Leaves only. Reporting a panel as well as each of its children turns one
            // layout fault into a page of noise pointing at the same edge.
            if (node is not FrameworkElement element
                || ReferenceEquals(element, root)
                || !element.IsVisible
                || element.ActualWidth <= 0
                || VisualTreeHelper.GetChildrenCount(element) > 0)
            {
                return;
            }

            Point origin;
            try
            {
                origin = element.TransformToAncestor(root).Transform(new Point(0, 0));
            }
            catch (InvalidOperationException)
            {
                // Not parented to this root any more (a template swapped mid-walk).
                return;
            }

            var right = origin.X + element.ActualWidth;
            if (right > limit && !IsInsideHorizontallyScrollableRegion(element, root))
            {
                issues.Add(
                    $"{Describe(element)} reaches {right:F0} DIP, past the {root.ActualWidth:F0} DIP edge.");
            }
        });

        return issues;
    }

    /// <summary>
    /// A wide table the operator can scroll sideways inside its own card is a deliberate
    /// choice, not lost content - the plan allows local horizontal scrolling for tables
    /// while ruling it out for the page. Only overflow with no way to reach it counts.
    /// </summary>
    private static bool IsInsideHorizontallyScrollableRegion(DependencyObject element, DependencyObject root)
    {
        var current = VisualTreeHelper.GetParent(element);
        while (current != null && !ReferenceEquals(current, root))
        {
            var scrolls = current switch
            {
                ScrollViewer sv => sv.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled,
                DataGrid grid => grid.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled,
                _ => false,
            };

            if (scrolls)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static string Describe(FrameworkElement element)
    {
        var name = string.IsNullOrEmpty(element.Name) ? element.GetType().Name : $"{element.GetType().Name} '{element.Name}'";
        return element switch
        {
            TextBlock { Text.Length: > 0 } tb => $"{name} \"{Shorten(tb.Text)}\"",
            _ => name,
        };
    }

    private static string Shorten(string text)
        => text.Length <= 40 ? text : text[..40] + "...";

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
