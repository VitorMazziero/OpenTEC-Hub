using System.IO;
using Xunit;

namespace TecnalHub.Tests;

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
    private static string ReadShell(string relative)
        => File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "src", "TecnalHub", relative));

    [Fact]
    public void Window_uses_custom_chrome_with_its_own_caption_buttons()
    {
        var xaml = ReadShell("MainWindow.xaml");

        Assert.Contains("shell:WindowChrome", xaml, StringComparison.Ordinal);
        Assert.Contains("UseAeroCaptionButtons=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CaptionMinimizeButtonStyle", xaml, StringComparison.Ordinal);
        Assert.Contains("CaptionMaximizeButtonStyle", xaml, StringComparison.Ordinal);
        Assert.Contains("CaptionCloseButtonStyle", xaml, StringComparison.Ordinal);

        // The close-hover red is a token, not a colour literal (Views carry no literals).
        Assert.Contains("CaptionCloseHoverBrush", xaml, StringComparison.Ordinal);
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
