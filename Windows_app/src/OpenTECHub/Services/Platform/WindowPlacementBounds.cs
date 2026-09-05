using System.Windows;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.Services.Platform;

/// <summary>Resolves persisted window bounds into a visible current-screen rectangle.</summary>
internal static class WindowPlacementBounds
{
    private const double MinimumVisibleWidth = 96;
    private const double MinimumVisibleHeight = 32;

    public static Rect Resolve(
        WindowPlacementSettings saved,
        Rect available,
        Size fallback,
        Size minimum)
    {
        ArgumentNullException.ThrowIfNull(saved);
        if (available.IsEmpty || available.Width <= 0 || available.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(available));
        }

        var width = Math.Clamp(
            saved.HasBounds ? saved.Width!.Value : fallback.Width,
            Math.Min(minimum.Width, available.Width),
            available.Width);
        var height = Math.Clamp(
            saved.HasBounds ? saved.Height!.Value : fallback.Height,
            Math.Min(minimum.Height, available.Height),
            available.Height);

        if (!saved.HasBounds)
        {
            return Center(available, width, height);
        }

        var candidate = new Rect(saved.Left!.Value, saved.Top!.Value, width, height);
        var visible = Rect.Intersect(candidate, available);
        if (visible.IsEmpty ||
            visible.Width < MinimumVisibleWidth ||
            visible.Height < MinimumVisibleHeight)
        {
            return Center(available, width, height);
        }

        var left = Math.Clamp(
            candidate.Left,
            available.Left - width + MinimumVisibleWidth,
            available.Right - MinimumVisibleWidth);
        var top = Math.Clamp(
            candidate.Top,
            available.Top,
            available.Bottom - MinimumVisibleHeight);
        return new Rect(left, top, width, height);
    }

    private static Rect Center(Rect available, double width, double height)
        => new(
            available.Left + ((available.Width - width) / 2.0),
            available.Top + ((available.Height - height) / 2.0),
            width,
            height);
}
