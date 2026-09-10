using System.IO;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// Pins the custom window chrome so nobody silently reverts to the OS caption, drops a
/// caption button, or lets the drag region swallow the header controls.
/// </summary>
/// <remarks>
/// These read the shell files from disk, like <see cref="ReactorAssetTests"/> and
/// <c>ResourceKeyTests</c>: the chrome is a XAML/code-behind contract that a headless test
/// cannot exercise by rendering, but can guard against regression by asserting it is wired.
/// </remarks>
public sealed class ShellChromeTests
{
    /// <summary>Every window that draws its own caption, so the chrome stays one thing.</summary>
    private static readonly string[] ChromedWindows =
    [
        "MainWindow.xaml",
        Path.Combine("Views", "Dialogs", "CaptureSettingsDialog.xaml"),
        Path.Combine("Views", "Dialogs", "ImpellerCatalogDialog.xaml"),
        Path.Combine("Views", "Dialogs", "InputDialog.xaml"),
        Path.Combine("Views", "Dialogs", "OxygenConfigDialog.xaml"),
    ];

    private static string ReadShell(string relative)
        => File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "src", "OpenTECHub", relative));

    [Fact]
    public void Window_uses_custom_chrome_with_its_own_caption_buttons()
    {
        var xaml = ReadShell("MainWindow.xaml");

        Assert.Contains("shell:WindowChrome", xaml, StringComparison.Ordinal);
        Assert.Contains("UseAeroCaptionButtons=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CaptionMinimizeButtonStyle", xaml, StringComparison.Ordinal);
        Assert.Contains("CaptionMaximizeButtonStyle", xaml, StringComparison.Ordinal);
        Assert.Contains("CaptionCloseButtonStyle", xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// The caption templates live in Themes/Controls.xaml and nowhere else. They used to be
    /// copy-pasted into every window, which is how the close glyph came to be drawn one way
    /// in the shell and another in a dialog.
    /// </summary>
    [Fact]
    public void Caption_button_templates_are_defined_once_for_every_window()
    {
        var shared = ReadShell(Path.Combine("Themes", "Controls.xaml"));

        foreach (var key in new[]
                 {
                     "CaptionButtonBase",
                     "CaptionMinimizeButtonStyle",
                     "CaptionMaximizeButtonStyle",
                     "CaptionCloseButtonStyle",
                 })
        {
            Assert.Contains($"x:Key=\"{key}\"", shared, StringComparison.Ordinal);
        }

        // The close-hover red is a token, not a colour literal.
        Assert.Contains("CaptionCloseHoverBrush", shared, StringComparison.Ordinal);

        foreach (var window in ChromedWindows)
        {
            var xaml = ReadShell(window);
            Assert.DoesNotContain(
                "x:Key=\"CaptionButtonBase\"",
                xaml,
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The X used to be drawn from 0,0 to 10,10, so half of its 1 DIP stroke hung outside
    /// the glyph box and was clipped against the frame of a maximised window.
    /// </summary>
    [Fact]
    public void Close_glyph_is_drawn_inside_its_box()
    {
        var shared = ReadShell(Path.Combine("Themes", "Controls.xaml"));

        Assert.Contains("M 0.5,0.5 L 9.5,9.5 M 0.5,9.5 L 9.5,0.5", shared, StringComparison.Ordinal);
        Assert.DoesNotContain("M0,0 L10,10 M0,10 L10,0", shared, StringComparison.Ordinal);
    }

    [Fact]
    public void Interactive_caption_controls_stay_clickable_in_the_drag_region()
    {
        var xaml = ReadShell("MainWindow.xaml");

        // Without IsHitTestVisibleInChrome the connection chip and the window buttons would be
        // swallowed by the caption drag region.
        Assert.Contains("WindowChrome.IsHitTestVisibleInChrome", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Maximize_is_constrained_to_the_work_area_and_the_buttons_are_wired()
    {
        var code = ReadShell("MainWindow.xaml.cs");

        Assert.Contains("WindowChromeMaximizeFix.Enable", code, StringComparison.Ordinal);
        Assert.Contains("OnMinimizeWindow", code, StringComparison.Ordinal);
        Assert.Contains("OnMaximizeRestoreWindow", code, StringComparison.Ordinal);
        Assert.Contains("OnCloseWindow", code, StringComparison.Ordinal);
    }
}
