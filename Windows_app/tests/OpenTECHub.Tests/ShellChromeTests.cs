using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
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
[Collection(Rendering.WpfRenderingCollection.Name)]
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

    [Fact]
    public void ShellRoot_has_maximized_margin_trigger_in_xaml()
    {
        var xaml = ReadShell("MainWindow.xaml");

        Assert.Contains("<DockPanel x:Name=\"ShellRoot\">", xaml, StringComparison.Ordinal);
        Assert.Contains("Value=\"Maximized\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Property=\"Margin\" Value=\"8\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Maximized_window_insets_shell_root_and_keeps_close_button_on_screen()
    {
        Rendering.WpfRenderingHost.Run(() =>
        {
            var win = new MainWindow();
            win.WindowState = WindowState.Normal;
            win.Show();
            win.UpdateLayout();

            var shellRoot = (System.Windows.Controls.DockPanel)win.FindName("ShellRoot");
            var topBorder = (System.Windows.Controls.Border)shellRoot.Children[0];
            var headerDock = (System.Windows.Controls.DockPanel)topBorder.Child;
            var captionStack = (System.Windows.Controls.StackPanel)headerDock.Children[0];
            var closeBtn = (System.Windows.Controls.Button)captionStack.Children[2];

            // Normal state: Margin must be 0 so windowed geometry is unpadded.
            Assert.Equal(new Thickness(0), shellRoot.Margin);

            // Maximize window
            win.WindowState = WindowState.Maximized;
            win.UpdateLayout();

            // Maximized state: ShellRoot must have an 8 DIP margin compensating for
            // Windows User32's thickframe overscan.
            Assert.Equal(new Thickness(8), shellRoot.Margin);

            var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
            var monitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(monitor, ref info);

            var closeScreenPt = closeBtn.PointToScreen(new Point(0, 0));
            var closeRightEdge = closeScreenPt.X + closeBtn.ActualWidth;

            // Close button right edge must be on-screen (within monitor work area).
            Assert.True(closeRightEdge <= info.rcWork.Right + 0.5,
                $"Close button right edge ({closeRightEdge}) exceeded work area right ({info.rcWork.Right})");
            Assert.True(closeScreenPt.Y >= info.rcWork.Top - 0.5,
                $"Close button top ({closeScreenPt.Y}) was clipped above work area top ({info.rcWork.Top})");

            // Restore to normal: Margin must revert to 0.
            win.WindowState = WindowState.Normal;
            win.UpdateLayout();
            Assert.Equal(new Thickness(0), shellRoot.Margin);

            win.Close();
        });
    }

    [Fact]
    public void NavDrawer_in_compact_mode_overlays_from_left_edge_covering_rail()
    {
        Rendering.WpfRenderingHost.Run(() =>
        {
            var settings = Rendering.WpfRenderingHost.Services.GetRequiredService<Services.Persistence.ISettingsService>();
            var theme = Rendering.WpfRenderingHost.Services.GetRequiredService<Services.Theme.IThemeService>();
            var shell = Rendering.WpfRenderingHost.Services.GetRequiredService<ViewModels.ShellViewModel>();

            // The shared settings service may contain the operator's saved maximized state.
            // This test exercises compact normal-window geometry, so make that precondition explicit.
            var win = new MainWindow(settings, theme)
            {
                DataContext = shell,
                WindowState = WindowState.Normal,
            };
            win.Width = 1280;
            win.Height = 800;
            win.Show();
            win.UpdateLayout();

            Assert.True(shell.IsNavigationCompact);

            var navRail = (FrameworkElement)win.FindName("NavigationRail");
            var navDrawer = (FrameworkElement)win.FindName("NavDrawer");

            // Rail is 56 DIP wide and sits at Left = 0.
            var railPt = navRail.TransformToAncestor(win).Transform(new Point(0, 0));
            Assert.Equal(0, railPt.X);
            Assert.Equal(56, navRail.ActualWidth);

            // Initially drawer is closed.
            Assert.Equal(Visibility.Collapsed, navDrawer.Visibility);

            // Open drawer
            shell.IsNavDrawerOpen = true;
            win.UpdateLayout();

            Assert.Equal(Visibility.Visible, navDrawer.Visibility);
            var drawerPt = navDrawer.TransformToAncestor(win).Transform(new Point(0, 0));

            // Crucial: Drawer must start at x=0 (covering the rail), NOT at x=56 (beside the rail).
            Assert.Equal(0, drawerPt.X);
            Assert.True(navDrawer.ActualWidth >= navRail.ActualWidth, "Drawer width must cover the rail width");

            win.Close();
        });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }
}
