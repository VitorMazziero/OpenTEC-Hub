using System.Windows;
using OpenTECHub.Services.Platform;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// A dialog whose footer falls off the bottom of the screen is a dialog whose confirm button
/// cannot be clicked. These pin the arithmetic that keeps every modal inside the window it
/// opens over, including on the smallest supported laptop.
/// </summary>
public sealed class DialogBoundsTests
{
    private static readonly Size CaptureSettingsRequested = new(520, 580);
    private static readonly Size CaptureSettingsMinimum = new(460, 420);

    [Fact]
    public void A_dialog_that_fits_keeps_its_declared_size()
    {
        var clamped = DialogBounds.Clamp(
            CaptureSettingsRequested,
            CaptureSettingsMinimum,
            available: new Size(1280, 800));

        Assert.Equal(520, clamped.Width);
        Assert.Equal(580, clamped.Height);
    }

    /// <summary>
    /// The capture-criteria dialog asks for 580 DIP of height. The smallest supported window
    /// is 640 tall, which leaves 608 once a 16 DIP margin is kept on each edge.
    /// </summary>
    [Fact]
    public void A_tall_dialog_is_trimmed_to_the_smallest_supported_window()
    {
        var clamped = DialogBounds.Clamp(
            new Size(520, 760),
            CaptureSettingsMinimum,
            available: new Size(1024, 640));

        Assert.Equal(520, clamped.Width);
        Assert.Equal(608, clamped.Height);
    }

    /// <summary>
    /// Where the declared minimum no longer fits, the minimum gives way: a dialog shown at
    /// 300 DIP with its own scrolling is usable, one shown at 420 with the footer off screen
    /// is not.
    /// </summary>
    [Fact]
    public void Available_space_wins_over_a_minimum_that_no_longer_fits()
    {
        var clamped = DialogBounds.Clamp(
            CaptureSettingsRequested,
            CaptureSettingsMinimum,
            available: new Size(1024, 300));

        Assert.Equal(300, clamped.Height);
    }

    /// <summary>
    /// Between the margin and the minimum, the minimum wins - shaving a dialog below what it
    /// says it needs just to keep a decorative gap is the wrong trade.
    /// </summary>
    [Fact]
    public void The_minimum_wins_over_the_margin()
    {
        var clamped = DialogBounds.Clamp(
            CaptureSettingsRequested,
            CaptureSettingsMinimum,
            available: new Size(1024, 440));

        Assert.Equal(420, clamped.Height);
    }

    /// <summary>
    /// SizeToContent dialogs leave a NaN on the axis they measure themselves; clamping must
    /// hand back the usable space rather than propagating the NaN into Window.Height.
    /// </summary>
    [Fact]
    public void An_unset_axis_resolves_to_the_usable_space()
    {
        var clamped = DialogBounds.Clamp(
            new Size(540, double.NaN),
            new Size(460, 200),
            available: new Size(1024, 640));

        Assert.Equal(540, clamped.Width);
        Assert.Equal(608, clamped.Height);
    }

    [Fact]
    public void An_unknown_available_size_leaves_the_request_alone()
    {
        var clamped = DialogBounds.Clamp(
            CaptureSettingsRequested,
            CaptureSettingsMinimum,
            available: new Size(0, 0));

        Assert.Equal(CaptureSettingsRequested, clamped);
    }
}
