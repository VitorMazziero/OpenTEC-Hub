namespace OpenTECHub.Services.Alarms;

/// <summary>
/// Which latched alarms the shell's banner shows on a given page (§H of the 2026-09-11 plan).
/// </summary>
/// <remarks>
/// <para>
/// Everywhere but the Events page, the banner is an annunciator: each row has its own
/// <c>Reconhecer</c>, an acknowledged row disappears, and the banner closes when nothing is
/// left to acknowledge. On Events — the audit page — the banner stays while any alarm is
/// latched, acknowledged or not, so the operator sees the whole picture.
/// </para>
/// <para>
/// Presentation only: <see cref="IAlarmService"/>, its latch and its deadband are untouched, so
/// the journal and the safety logic are exactly what they were. Before this the banner showed
/// while anything was latched and an acknowledged row stayed — with its button greyed — until
/// the condition cleared by deadband; acknowledging hid nothing on any page.
/// </para>
/// </remarks>
public static class AlarmBannerPresenter
{
    /// <summary>Navigation id of the page that shows every latched alarm.</summary>
    public const string EventsPageId = "events";

    /// <summary>The rows the banner lists on this page, headline first, in the service's own order.</summary>
    public static IReadOnlyList<AlarmSnapshot> Displayed(IReadOnlyList<AlarmSnapshot> latched, string? pageId)
        => IsEventsPage(pageId) ? latched : latched.Where(a => a.IsAnnunciating).ToArray();

    /// <summary>Whether the banner is shown at all on this page.</summary>
    public static bool IsVisible(IReadOnlyList<AlarmSnapshot> latched, string? pageId)
        => Displayed(latched, pageId).Count > 0;

    /// <summary>
    /// The row shown on the banner's first line: the service's headline when it is among the
    /// displayed rows (annunciating alarms always come first there), else the first displayed row.
    /// </summary>
    public static AlarmSnapshot? Headline(IReadOnlyList<AlarmSnapshot> latched, AlarmSnapshot? serviceHeadline, string? pageId)
    {
        var displayed = Displayed(latched, pageId);
        if (displayed.Count == 0)
        {
            return null;
        }

        return serviceHeadline is { } head && displayed.Any(a => a.Id == head.Id)
            ? displayed.First(a => a.Id == head.Id)
            : displayed[0];
    }

    /// <summary>The displayed rows other than the headline — the expandable part of the banner.</summary>
    public static IReadOnlyList<AlarmSnapshot> Others(IReadOnlyList<AlarmSnapshot> latched, AlarmSnapshot? serviceHeadline, string? pageId)
    {
        var headline = Headline(latched, serviceHeadline, pageId);
        return Displayed(latched, pageId).Where(a => headline is null || a.Id != headline.Id).ToArray();
    }

    /// <summary>"+N" for the rows beyond the headline on this page, or empty.</summary>
    public static string MoreText(IReadOnlyList<AlarmSnapshot> latched, string? pageId)
    {
        var others = Displayed(latched, pageId).Count - 1;
        return others > 0 ? $"+{others}" : "";
    }

    /// <summary>
    /// The dot on the Eventos icon (D-069): null while no alarm is latched; otherwise
    /// <see cref="AlarmSeverity.Critical"/> (red) while any latched alarm is a live critical fault,
    /// else <see cref="AlarmSeverity.Warning"/> (amber: warnings, or faults that already returned to normal).
    /// It stays for as long as the alarm does; silencing the sound does not touch it.
    /// </summary>
    public static AlarmSeverity? IndicatorSeverity(IReadOnlyList<AlarmSnapshot> latched)
    {
        var pending = latched.Where(a => a.Latched).ToArray();
        if (pending.Length == 0)
        {
            return null;
        }

        return pending.Any(a => a.ConditionActive && a.Severity != AlarmSeverity.Warning)
            ? AlarmSeverity.Critical : AlarmSeverity.Warning;
    }

    public static bool IsEventsPage(string? pageId) => string.Equals(pageId, EventsPageId, StringComparison.Ordinal);
}
