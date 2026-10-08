using System.IO;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>D-068: the alarm bar is part of the Eventos page, not a window-wide strip; the nav icon carries a dot.</summary>
public sealed class AlarmBarPlacementTests
{
    private static string Read(string relative)
        => File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "src", "OpenTECHub", relative));

    [Fact]
    public void The_window_no_longer_hosts_a_full_width_alarm_bar()
    {
        var window = Read("MainWindow.xaml");

        Assert.DoesNotContain("IsAlarmBannerVisible", window, StringComparison.Ordinal);
        Assert.DoesNotContain("SilenceAlarmsCommand", window, StringComparison.Ordinal);
    }

    [Fact]
    public void The_events_page_hosts_the_bar_above_its_own_content()
    {
        var window = Read("MainWindow.xaml");
        var events = window.IndexOf("PageId=\"events\"", StringComparison.Ordinal);
        Assert.True(events > 0);
        var page = window[events..window.IndexOf("</ctl:DeferredPageHost>", events, StringComparison.Ordinal)];

        Assert.Contains("<views:AlarmBanner", page, StringComparison.Ordinal);
        Assert.True(page.IndexOf("<views:AlarmBanner", StringComparison.Ordinal) <
                    page.IndexOf("<views:EventsView", StringComparison.Ordinal));

        var banner = Read(Path.Combine("Views", "AlarmBanner.xaml"));
        Assert.Contains("IsAlarmBannerVisible", banner, StringComparison.Ordinal);
        Assert.Contains("SilenceAlarmsCommand", banner, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding IsAlarmAudible}\"", banner, StringComparison.Ordinal);
        Assert.Contains("AcknowledgeHeadlineCommand", banner, StringComparison.Ordinal);
    }

    [Fact]
    public void The_events_icon_carries_a_dot_in_its_own_cell_coloured_by_the_alarm_state()
    {
        var window = Read("MainWindow.xaml");

        Assert.Contains("DataContext.HasAlarmIndicator", window, StringComparison.Ordinal);
        Assert.Contains("DataContext.AlarmIndicatorState", window, StringComparison.Ordinal);
        // Drawn over the icon in the same Grid, so the icon is not displaced.
        var dot = window.IndexOf("<Ellipse Width=\"9\"", StringComparison.Ordinal);
        Assert.True(dot > window.LastIndexOf("<ctl:Icon Key=\"{Binding Glyph}\"", dot, StringComparison.Ordinal));
    }
}
