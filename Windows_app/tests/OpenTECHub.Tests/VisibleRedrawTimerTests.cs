using System;
using System.Windows;
using System.Windows.Controls;
using OpenTECHub.Controls;
using OpenTECHub.Tests.Rendering;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// §C: a page's chart timer runs only while the page is on screen. The shell hides a page by
/// collapsing its host — <c>Unloaded</c> never fires — so a timer keyed to <c>Loaded</c> kept
/// redrawing hidden plots for the whole session. And a tick with nothing dirty draws nothing.
/// </summary>
public sealed class VisibleRedrawTimerTests
{
    [Fact]
    public void Timer_runs_only_while_the_host_is_loaded_and_visible_and_redraws_only_when_dirty()
    {
        WpfRenderingHost.Run(() =>
        {
            var redraws = 0;
            var page = new UserControl { Width = 100, Height = 100 };
            var host = new ContentControl { Content = page }; // stands in for DeferredPageHost
            var timer = new VisibleRedrawTimer(page, TimeSpan.FromMilliseconds(10), () => redraws++);
            var window = new Window
            {
                Width = 200, Height = 200, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, Content = host,
            };

            try
            {
                Assert.False(timer.IsRunning);

                window.Show();
                WpfRenderingHost.PumpDispatcher();
                Assert.True(page.IsVisible);
                Assert.True(timer.IsRunning);
                // Becoming visible redraws at once (the timer starts dirty).
                Assert.Equal(1, redraws);
                Assert.False(timer.IsDirty);

                // Clean ticks are no-ops.
                Pump(50);
                Assert.Equal(1, redraws);

                timer.MarkDirty();
                Pump(50);
                Assert.Equal(2, redraws);

                // Hidden the way the shell hides a page: collapse the host, not the page.
                host.Visibility = Visibility.Collapsed;
                WpfRenderingHost.PumpDispatcher();
                Assert.False(page.IsVisible);
                Assert.False(timer.IsRunning);
                timer.MarkDirty();
                Pump(50);
                Assert.Equal(2, redraws);

                // Shown again: the pending change is drawn immediately.
                host.Visibility = Visibility.Visible;
                WpfRenderingHost.PumpDispatcher();
                Assert.True(timer.IsRunning);
                Assert.Equal(3, redraws);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void Pump(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            WpfRenderingHost.PumpDispatcher();
            System.Threading.Thread.Sleep(5);
        }
    }
}
