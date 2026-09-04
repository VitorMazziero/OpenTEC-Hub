using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace OpenTECHub.Controls;

/// <summary>
/// Hosts one shell page and builds it only when it is actually needed.
/// </summary>
/// <remarks>
/// <para>
/// The shell used to declare all eleven pages as siblings and toggle <c>Visibility</c>. A collapsed
/// element is still constructed, still laid out and still raises <c>Loaded</c>, so every launch paid
/// for every page: the XAML of all of them was parsed, their element trees built, and each one's
/// <c>Loaded</c> handler ran - including the ones that read the workspace from disk and start
/// redraw timers. The destination page was known before the first frame and none of that knowledge
/// was used.
/// </para>
/// <para>
/// This host realises the selected page synchronously, so the first frame carries exactly the page
/// the operator asked for, and queues every other page at <see cref="DispatcherPriority.ApplicationIdle"/>
/// - after the window is up and interactive. Navigating to a page that has not been built yet
/// realises it on the spot, so the deferral is never visible as a missing page.
/// </para>
/// <para>
/// Deferring is safe because it defers <em>views only</em>: every page ViewModel is a DI singleton
/// taken by <c>ShellViewModel</c>'s constructor, so telemetry subscriptions and accumulated state
/// exist from start-up regardless of whether anyone has looked at the page.
/// </para>
/// </remarks>
public sealed class DeferredPageHost : ContentControl
{
    static DeferredPageHost()
    {
        // A page fills its cell; the ContentControl default of top-left would letterbox it.
        HorizontalContentAlignmentProperty.OverrideMetadata(
            typeof(DeferredPageHost), new FrameworkPropertyMetadata(HorizontalAlignment.Stretch));
        VerticalContentAlignmentProperty.OverrideMetadata(
            typeof(DeferredPageHost), new FrameworkPropertyMetadata(VerticalAlignment.Stretch));
    }

    public DeferredPageHost()
    {
        Loaded += OnLoaded;
    }

    /// <summary>Navigation id this host answers to, e.g. <c>power-map</c>.</summary>
    public static readonly DependencyProperty PageIdProperty = DependencyProperty.Register(
        nameof(PageId),
        typeof(string),
        typeof(DeferredPageHost),
        new PropertyMetadata(null, OnRoutingChanged));

    /// <summary>The shell's current navigation id.</summary>
    public static readonly DependencyProperty SelectedPageIdProperty = DependencyProperty.Register(
        nameof(SelectedPageId),
        typeof(string),
        typeof(DeferredPageHost),
        new PropertyMetadata(null, OnRoutingChanged));

    /// <summary>The page itself, held as a template so it is not built until asked for.</summary>
    public static readonly DependencyProperty PageTemplateProperty = DependencyProperty.Register(
        nameof(PageTemplate),
        typeof(DataTemplate),
        typeof(DeferredPageHost),
        new PropertyMetadata(null));

    public string? PageId
    {
        get => (string?)GetValue(PageIdProperty);
        set => SetValue(PageIdProperty, value);
    }

    public string? SelectedPageId
    {
        get => (string?)GetValue(SelectedPageIdProperty);
        set => SetValue(SelectedPageIdProperty, value);
    }

    public DataTemplate? PageTemplate
    {
        get => (DataTemplate?)GetValue(PageTemplateProperty);
        set => SetValue(PageTemplateProperty, value);
    }

    /// <summary>True once the page's element tree has been built.</summary>
    public bool IsRealized { get; private set; }

    private bool _deferralQueued;

    private bool IsSelected =>
        PageId is { Length: > 0 } id &&
        string.Equals(id, SelectedPageId, StringComparison.Ordinal);

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        UpdateRouting();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => QueueDeferredRealization();

    private static void OnRoutingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((DeferredPageHost)d).UpdateRouting();

    private void UpdateRouting()
    {
        var selected = IsSelected;
        Visibility = selected ? Visibility.Visible : Visibility.Collapsed;

        if (selected)
        {
            // Navigated to: the page has to exist now, whether or not its idle turn came up.
            Realize();
        }
    }

    /// <summary>
    /// Builds the page after the window is up, so an unvisited page costs nothing before the
    /// first frame but is still ready by the time the operator navigates to it.
    /// </summary>
    private void QueueDeferredRealization()
    {
        if (IsRealized || _deferralQueued || PageTemplate is null)
        {
            return;
        }

        _deferralQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(Realize));
    }

    private void Realize()
    {
        if (IsRealized || PageTemplate is not { } template)
        {
            return;
        }

        IsRealized = true;
        Content = template.LoadContent();
    }
}
