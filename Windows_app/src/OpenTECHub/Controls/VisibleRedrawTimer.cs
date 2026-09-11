using System.Windows;
using System.Windows.Threading;

namespace OpenTECHub.Controls;

/// <summary>
/// Periodic chart redraw that runs only while its host is on screen and only when something has
/// changed since the last draw.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DeferredPageHost"/> hides a page by collapsing it, and <c>Unloaded</c> never fires
/// for a collapsed element — so a page's <c>Loaded</c>-started timer kept redrawing its charts
/// forever, unseen. A session that had visited Sinóptico, Potência and kLa was preparing up to
/// nine plots per second on the UI thread (bench of 2026-09-11). The host's <c>IsVisible</c>
/// follows the ancestor chain, so it is the right gate: the timer runs iff the host is loaded
/// and visible.
/// </para>
/// <para>
/// <see cref="MarkDirty"/> is what data sources call (a collection changed, a property the chart
/// reads moved); a tick with nothing dirty is a no-op. Becoming visible redraws at once when
/// dirty, so a page never shows stale charts for up to one interval after navigation.
/// </para>
/// <para>
/// The timer is a <see cref="DispatcherTimer"/> at its default <see cref="DispatcherPriority.Background"/>,
/// which sits below input and render: a redraw never delays a click.
/// </para>
/// </remarks>
public sealed class VisibleRedrawTimer
{
    private readonly FrameworkElement _host;
    private readonly Action _redraw;
    private readonly DispatcherTimer _timer;
    private bool _dirty = true;

    public VisibleRedrawTimer(FrameworkElement host, TimeSpan interval, Action redraw)
    {
        _host = host;
        _redraw = redraw;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
        _timer.Tick += (_, _) => RedrawIfDirty();
        host.Loaded += (_, _) => UpdateRunning();
        host.Unloaded += (_, _) => UpdateRunning();
        host.IsVisibleChanged += (_, _) => UpdateRunning();
    }

    /// <summary>True while the host is loaded and on screen — the only time ticks fire.</summary>
    public bool IsRunning => _timer.IsEnabled;

    public bool IsDirty => _dirty;

    /// <summary>Something the chart shows changed; the next tick (or the next show) redraws.</summary>
    public void MarkDirty() => _dirty = true;

    /// <summary>Redraws now if anything is dirty and the host is visible; otherwise waits.</summary>
    public void RedrawIfDirty()
    {
        if (!_dirty || !_host.IsLoaded || !_host.IsVisible)
        {
            return;
        }

        _dirty = false;
        _redraw();
    }

    /// <summary>Forces a redraw on the next opportunity regardless of the dirty flag (theme change).</summary>
    public void Invalidate()
    {
        _dirty = true;
        RedrawIfDirty();
    }

    private void UpdateRunning()
    {
        var shouldRun = _host.IsLoaded && _host.IsVisible;
        if (shouldRun && !_timer.IsEnabled)
        {
            _timer.Start();
            RedrawIfDirty();
        }
        else if (!shouldRun && _timer.IsEnabled)
        {
            _timer.Stop();
        }
    }
}
