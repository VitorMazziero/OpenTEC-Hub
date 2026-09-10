using System.Windows;

namespace OpenTECHub.Services.Platform;

/// <summary>
/// Keeps a modal dialog inside the space that is actually on screen.
/// </summary>
/// <remarks>
/// Dialogs declare a comfortable size for a desk monitor - the capture-criteria window asks
/// for 520 x 580 and refuses to go under 460 x 420. On a laptop that can be taller than the
/// space left for it, and a dialog whose footer is off screen is a dialog whose Confirmar
/// button cannot be reached. Clamping happens before the dialog is shown, against the owner
/// window (itself already held inside the monitor work area) or, with no owner, the work
/// area directly.
/// </remarks>
public static class DialogBounds
{
    /// <summary>Breathing room left around a dialog, per edge, in DIP.</summary>
    public const double Margin = 16;

    /// <summary>
    /// Resolves the size a dialog should actually open at.
    /// </summary>
    /// <param name="requested">The dialog's declared size.</param>
    /// <param name="minimum">The dialog's declared minimum size.</param>
    /// <param name="available">The space the dialog has to fit inside.</param>
    /// <param name="margin">Room to leave per edge.</param>
    /// <remarks>
    /// The minimum wins over the margin, and the available space wins over the minimum: a
    /// dialog that cannot honour its own minimum is still better shown at the size that fits
    /// than opened with its footer past the bottom of the screen.
    /// </remarks>
    public static Size Clamp(Size requested, Size minimum, Size available, double margin = Margin)
    {
        return new Size(
            ClampAxis(requested.Width, minimum.Width, available.Width, margin),
            ClampAxis(requested.Height, minimum.Height, available.Height, margin));
    }

    private static double ClampAxis(double requested, double minimum, double available, double margin)
    {
        if (available <= 0 || double.IsNaN(available) || double.IsInfinity(available))
        {
            return requested;
        }

        var usable = Math.Max(available - (2 * margin), Math.Min(minimum, available));
        var wanted = double.IsNaN(requested) ? usable : requested;
        return Math.Min(wanted, usable);
    }

    /// <summary>
    /// Clamps <paramref name="dialog"/> to its owner, or to the work area when it has none.
    /// Call before <c>ShowDialog</c>, while the size is still only a declared value.
    /// </summary>
    public static void ConstrainToOwner(Window dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        var available = ResolveAvailable(dialog.Owner);
        if (available.Width <= 0 || available.Height <= 0)
        {
            return;
        }

        var clamped = Clamp(
            new Size(dialog.Width, dialog.Height),
            new Size(dialog.MinWidth, dialog.MinHeight),
            available);

        // MinWidth/MinHeight come down first: left above the clamped size they would win the
        // measure and put the window back outside the screen.
        dialog.MinWidth = Math.Min(dialog.MinWidth, clamped.Width);
        dialog.MinHeight = Math.Min(dialog.MinHeight, clamped.Height);
        dialog.MaxWidth = Math.Max(clamped.Width, dialog.MinWidth);
        dialog.MaxHeight = Math.Max(clamped.Height, dialog.MinHeight);

        if (!double.IsNaN(dialog.Width))
        {
            dialog.Width = clamped.Width;
        }

        // SizeToContent="Height" dialogs leave Height as NaN on purpose; MaxHeight above is
        // what holds them in, so the declared height is only overwritten when there is one.
        if (!double.IsNaN(dialog.Height))
        {
            dialog.Height = clamped.Height;
        }
    }

    private static Size ResolveAvailable(Window? owner)
    {
        if (owner is not null)
        {
            var width = owner.ActualWidth > 0 ? owner.ActualWidth : owner.Width;
            var height = owner.ActualHeight > 0 ? owner.ActualHeight : owner.Height;
            if (width > 0 && height > 0 && !double.IsNaN(width) && !double.IsNaN(height))
            {
                return new Size(width, height);
            }
        }

        var work = SystemParameters.WorkArea;
        return new Size(work.Width, work.Height);
    }
}
