using System;
using System.Collections.Generic;
using System.Linq;
using OpenTECHub.Services.Alarms;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// §H (2026-09-11): acknowledging a row removes it from the banner, and the banner closes when
/// nothing is left to acknowledge — except on Eventos, the audit page, where every latched alarm
/// stays listed, acknowledged or not. Presentation only: the service's latch/deadband is untouched.
/// </summary>
public sealed class AlarmBannerPresenterTests
{
    private static AlarmSnapshot Alarm(AlarmId id, bool acknowledged = false, bool conditionActive = true, AlarmSeverity severity = AlarmSeverity.Critical)
        => new(id, id.ToString(), severity, conditionActive, Latched: true, acknowledged,
            DateTimeOffset.UnixEpoch, acknowledged ? DateTimeOffset.UnixEpoch.AddMinutes(1) : null, "detalhe");

    /// <summary>The service's own headline rule: annunciating first, then severity, then newest.</summary>
    private static AlarmSnapshot? ServiceHeadline(IReadOnlyList<AlarmSnapshot> latched)
        => latched.OrderByDescending(a => a.IsAnnunciating).ThenByDescending(a => a.Severity).FirstOrDefault();

    [Fact]
    public void The_icon_dot_stays_for_as_long_as_an_alarm_is_latched_acknowledged_or_not()
    {
        Assert.Null(AlarmBannerPresenter.IndicatorSeverity([]));
        Assert.Equal(AlarmSeverity.Critical, AlarmBannerPresenter.IndicatorSeverity([Alarm(AlarmId.LinkLost, acknowledged: true)]));

        Assert.Equal(AlarmSeverity.Critical, AlarmBannerPresenter.IndicatorSeverity([Alarm(AlarmId.LinkLost)]));
        Assert.Equal(AlarmSeverity.Warning, AlarmBannerPresenter.IndicatorSeverity(
            [Alarm(AlarmId.FlowmeterOffline, severity: AlarmSeverity.Warning)]));
        // A critical fault that already returned to normal is about to clear itself: amber, not red.
        Assert.Equal(AlarmSeverity.Warning, AlarmBannerPresenter.IndicatorSeverity([Alarm(AlarmId.LinkLost, conditionActive: false)]));
        // Red wins over amber; an acknowledged red is still red.
        Assert.Equal(AlarmSeverity.Critical, AlarmBannerPresenter.IndicatorSeverity(
            [Alarm(AlarmId.FlowmeterOffline, severity: AlarmSeverity.Warning), Alarm(AlarmId.LinkLost)]));
        Assert.Equal(AlarmSeverity.Critical, AlarmBannerPresenter.IndicatorSeverity(
            [Alarm(AlarmId.FlowmeterOffline, severity: AlarmSeverity.Warning), Alarm(AlarmId.LinkLost, acknowledged: true)]));
    }

    [Fact]
    public void Acknowledging_the_only_alarm_hides_the_banner_on_a_normal_page()
    {
        var before = new[] { Alarm(AlarmId.LinkLost) };
        var after = new[] { Alarm(AlarmId.LinkLost, acknowledged: true) };

        Assert.True(AlarmBannerPresenter.IsVisible(before, "dashboard"));
        Assert.False(AlarmBannerPresenter.IsVisible(after, "dashboard"));
        Assert.Null(AlarmBannerPresenter.Headline(after, ServiceHeadline(after), "dashboard"));
    }

    [Fact]
    public void The_same_acknowledged_alarm_stays_visible_on_the_events_page()
    {
        var latched = new[] { Alarm(AlarmId.LinkLost, acknowledged: true) };

        Assert.True(AlarmBannerPresenter.IsVisible(latched, AlarmBannerPresenter.EventsPageId));
        var headline = AlarmBannerPresenter.Headline(latched, ServiceHeadline(latched), AlarmBannerPresenter.EventsPageId);
        Assert.NotNull(headline);
        Assert.Equal(AlarmId.LinkLost, headline!.Id);
        Assert.False(headline.IsAnnunciating); // the row's Reconhecer is disabled there
    }

    [Fact]
    public void Switching_pages_with_an_acknowledged_latched_alarm_toggles_the_visibility()
    {
        var latched = new[] { Alarm(AlarmId.ModuleOffline, acknowledged: true) };

        Assert.False(AlarmBannerPresenter.IsVisible(latched, "dashboard"));
        Assert.True(AlarmBannerPresenter.IsVisible(latched, "events"));
        Assert.False(AlarmBannerPresenter.IsVisible(latched, "power"));
    }

    [Fact]
    public void An_alarm_cleared_by_the_deadband_is_gone_from_both_lists()
    {
        // The service drops an alarm from Snapshot() once the deadband clears it: nothing to show anywhere.
        var latched = Array.Empty<AlarmSnapshot>();

        Assert.False(AlarmBannerPresenter.IsVisible(latched, "dashboard"));
        Assert.False(AlarmBannerPresenter.IsVisible(latched, "events"));
        Assert.Empty(AlarmBannerPresenter.Displayed(latched, "events"));
    }

    [Fact]
    public void More_text_and_others_count_only_the_rows_displayed_on_this_page()
    {
        var latched = new[]
        {
            Alarm(AlarmId.LinkLost),
            Alarm(AlarmId.ModuleOffline, acknowledged: true),
            Alarm(AlarmId.FlowmeterOffline, acknowledged: true),
        };
        var head = ServiceHeadline(latched);

        // Normal page: one unacknowledged row, nothing beyond the headline.
        Assert.Equal("", AlarmBannerPresenter.MoreText(latched, "dashboard"));
        Assert.Empty(AlarmBannerPresenter.Others(latched, head, "dashboard"));
        Assert.Equal(AlarmId.LinkLost, AlarmBannerPresenter.Headline(latched, head, "dashboard")!.Id);

        // Events: all three, "+2" beyond the headline.
        Assert.Equal("+2", AlarmBannerPresenter.MoreText(latched, "events"));
        Assert.Equal(2, AlarmBannerPresenter.Others(latched, head, "events").Count);
        Assert.Equal(AlarmId.LinkLost, AlarmBannerPresenter.Headline(latched, head, "events")!.Id);
    }

    [Fact]
    public void Acknowledging_the_headline_promotes_the_next_unacknowledged_alarm()
    {
        var latched = new List<AlarmSnapshot> { Alarm(AlarmId.LinkLost), Alarm(AlarmId.ModuleOffline) };
        Assert.Equal(AlarmId.LinkLost, AlarmBannerPresenter.Headline(latched, ServiceHeadline(latched), "dashboard")!.Id);

        latched[0] = Alarm(AlarmId.LinkLost, acknowledged: true);

        var headline = AlarmBannerPresenter.Headline(latched, ServiceHeadline(latched), "dashboard");
        Assert.Equal(AlarmId.ModuleOffline, headline!.Id);
        Assert.Equal("", AlarmBannerPresenter.MoreText(latched, "dashboard"));
        Assert.True(AlarmBannerPresenter.IsVisible(latched, "dashboard"));

        latched[1] = Alarm(AlarmId.ModuleOffline, acknowledged: true);
        Assert.False(AlarmBannerPresenter.IsVisible(latched, "dashboard"));
    }
}
