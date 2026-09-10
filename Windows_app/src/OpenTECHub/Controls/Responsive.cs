using System.Windows;

namespace OpenTECHub.Controls;

/// <summary>
/// Publishes breakpoint flags for a layout container so a page can adapt from XAML
/// triggers instead of a per-page code-behind carrying its own copy of the numbers.
/// </summary>
/// <remarks>
/// Put the threshold on the container that actually owns the space:
/// <code>
/// &lt;Grid x:Name="Body" ctl:Responsive.NarrowBelow="1200" ctl:Responsive.ShortBelow="720"&gt;
/// </code>
/// then read it back wherever the layout has to change:
/// <code>
/// &lt;DataTrigger Binding="{Binding (ctl:Responsive.IsNarrow), ElementName=Body}" Value="True"&gt;
/// </code>
/// The flags describe the <em>container</em>, never the monitor: the same page is narrow
/// in a 1024 DIP window and wide when maximised, and the operator may cross the
/// threshold by dragging. Adaptation therefore has to be a trigger, not a startup
/// decision, and it must never touch anything but layout - no commands, no selection,
/// no acquisition state.
/// </remarks>
public static class Responsive
{
    /// <summary>Width, in DIP, below which the container counts as narrow. 0 disables.</summary>
    public static readonly DependencyProperty NarrowBelowProperty =
        DependencyProperty.RegisterAttached(
            "NarrowBelow",
            typeof(double),
            typeof(Responsive),
            new PropertyMetadata(0.0, OnThresholdChanged));

    /// <summary>Height, in DIP, below which the container counts as short. 0 disables.</summary>
    public static readonly DependencyProperty ShortBelowProperty =
        DependencyProperty.RegisterAttached(
            "ShortBelow",
            typeof(double),
            typeof(Responsive),
            new PropertyMetadata(0.0, OnThresholdChanged));

    /// <summary>True while the container is narrower than <see cref="NarrowBelowProperty"/>.</summary>
    public static readonly DependencyProperty IsNarrowProperty =
        DependencyProperty.RegisterAttached(
            "IsNarrow",
            typeof(bool),
            typeof(Responsive),
            new PropertyMetadata(false));

    /// <summary>True while the container is shorter than <see cref="ShortBelowProperty"/>.</summary>
    public static readonly DependencyProperty IsShortProperty =
        DependencyProperty.RegisterAttached(
            "IsShort",
            typeof(bool),
            typeof(Responsive),
            new PropertyMetadata(false));

    public static void SetNarrowBelow(DependencyObject element, double value)
        => element.SetValue(NarrowBelowProperty, value);

    public static double GetNarrowBelow(DependencyObject element)
        => (double)element.GetValue(NarrowBelowProperty);

    public static void SetShortBelow(DependencyObject element, double value)
        => element.SetValue(ShortBelowProperty, value);

    public static double GetShortBelow(DependencyObject element)
        => (double)element.GetValue(ShortBelowProperty);

    public static bool GetIsNarrow(DependencyObject element)
        => (bool)element.GetValue(IsNarrowProperty);

    public static bool GetIsShort(DependencyObject element)
        => (bool)element.GetValue(IsShortProperty);

    private static void OnThresholdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        // Re-subscription is harmless and keeps the handler attached exactly once even when
        // both thresholds are set, because the delegate compares equal on removal.
        element.SizeChanged -= OnSizeChanged;
        element.Loaded -= OnLoaded;
        element.SizeChanged += OnSizeChanged;
        element.Loaded += OnLoaded;

        Evaluate(element);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            Evaluate(element);
        }
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            Evaluate(element);
        }
    }

    /// <summary>
    /// Recomputes the flags from the element's current size. Exposed for tests, which
    /// can set the size directly instead of waiting for a real layout pass.
    /// </summary>
    internal static void Evaluate(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        var narrowBelow = GetNarrowBelow(element);
        var shortBelow = GetShortBelow(element);

        // An unmeasured element reports 0. Treating that as "narrow" would make every
        // page flash through its compact layout on the way to the first frame, so the
        // wide layout stays until a real measurement arrives.
        var width = element.ActualWidth;
        var height = element.ActualHeight;

        element.SetValue(IsNarrowProperty, narrowBelow > 0 && width > 0 && width < narrowBelow);
        element.SetValue(IsShortProperty, shortBelow > 0 && height > 0 && height < shortBelow);
    }
}
