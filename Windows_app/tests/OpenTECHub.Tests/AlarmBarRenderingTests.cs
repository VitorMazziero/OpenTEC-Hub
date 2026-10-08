using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Extensions.DependencyInjection;
using OpenTECHub.Services.Alarms;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Theme;
using OpenTECHub.Tests.Rendering;
using OpenTECHub.ViewModels;
using OpenTECHub.Views;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>D-068 rendered: the bar sits on the Eventos page beside the rail, and the nav icon carries the dot.</summary>
[Collection(Rendering.WpfRenderingCollection.Name)]
public sealed class AlarmBarRenderingTests
{
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static List<Ellipse> VisibleDots(Window window)
        => Descendants<Ellipse>(window).Where(e => e is { Width: 9, Height: 9 } && e.IsVisible).ToList();

    [Fact]
    public void The_bar_is_on_the_events_page_only_and_the_dot_marks_the_events_icon()
    {
        var device = WpfRenderingHost.Services.GetRequiredService<RecordingDeviceService>();
        var alarms = WpfRenderingHost.Services.GetRequiredService<IAlarmService>();
        WpfRenderingHost.Run(() => device.PushState(OpenTECHub.Protocol.ConnectionState.Faulted));
        Thread.Sleep(1400); // the link-lost alarm latches after its one-second on-delay
        try
        {
            WpfRenderingHost.Run(() =>
            {
                alarms.Poll();
                Assert.True(alarms.HasActiveAlarms);
                var settings = WpfRenderingHost.Services.GetRequiredService<ISettingsService>();
                var theme = WpfRenderingHost.Services.GetRequiredService<IThemeService>();
                var shell = WpfRenderingHost.Services.GetRequiredService<ShellViewModel>();
                WpfRenderingHost.SetTheme(isDark: false);

                foreach (var page in new[] { "dashboard", "events" })
                {
                    shell.SelectedNavigationId = page;
                    var window = new MainWindow(settings, theme) { DataContext = shell, WindowState = WindowState.Normal };
                    window.Width = 1280; window.Height = 800; window.WindowStyle = WindowStyle.None;
                    window.ShowInTaskbar = false; window.ShowActivated = false;
                    window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -32000; window.Top = -32000;
                    window.Show();
                    shell.SelectedNavigationId = page;
                    WpfRenderingHost.PumpDispatcher();
                    window.UpdateLayout();
                    WpfRenderingHost.PumpDispatcher();
                    try
                    {
                        var banners = Descendants<AlarmBanner>(window).ToList();
                        var dots = VisibleDots(window);
                        var all = Descendants<Ellipse>(window).Where(e => e.Width == 9).ToList();
                        Assert.True(dots.Count == 1, $"{page}: nine-pixel ellipses={all.Count}, visible={dots.Count}, indicator={shell.HasAlarmIndicator}, state={shell.AlarmIndicatorState}, items={Descendants<ListBoxItem>(window).Count()}, hasAlarms={alarms.HasActiveAlarms}, unack={alarms.AnnunciatingCount}");
                        Assert.Equal(((SolidColorBrush)dots[0].Fill).Color,
                            ((SolidColorBrush)Application.Current.FindResource("StateAlarmBrush")).Color);

                        var rail = (FrameworkElement)window.FindName("NavigationRail");
                        if (page == "events")
                        {
                            var bar = Assert.Single(banners);
                            Assert.True(bar.IsVisible && bar.ActualHeight > 20);
                            var left = bar.TranslatePoint(new Point(0, 0), window).X;
                            Assert.True(left >= rail.ActualWidth, $"bar at {left}px must not sit over the {rail.ActualWidth}px rail");
                            Assert.True(bar.ActualWidth < window.ActualWidth - rail.ActualWidth + 1);
                        }
                        else
                        {
                            Assert.DoesNotContain(banners, b => b.IsVisible && b.ActualHeight > 0);
                        }

                        var rtb = new RenderTargetBitmap(1280, 800, 96, 96, PixelFormats.Pbgra32);
                        rtb.Render(window);
                        WpfRenderingHost.SavePng(rtb, System.IO.Path.Combine(TestPaths.EvidenceRoot, "docs", "evidence", "ui-alarm-bar", $"{page}.png"));
                    }
                    finally { window.Close(); }
                }
            });
        }
        finally
        {
            WpfRenderingHost.Run(() => device.PushState(OpenTECHub.Protocol.ConnectionState.Connected));
            Thread.Sleep(1200);
            WpfRenderingHost.Run(() => { alarms.Poll(); alarms.AcknowledgeAll(); alarms.Poll(); });
        }
    }
}
