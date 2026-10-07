using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ScottPlot;
using ScottPlot.WPF;
using OpenTECHub.Services.Telemetry;

using MediaColor = System.Windows.Media.Color;
using PlotColor = ScottPlot.Color;

namespace OpenTECHub.Controls;

/// <summary>
/// The detail pane's inline trend: one variable, recent history, no chrome.
/// </summary>
/// <remarks>
/// <para>
/// Answers "how has it behaved?" at a glance, between the live reading above it and the
/// controls below. Anything more than that - comparing two variables, changing the
/// window, reading a value off the cursor - is Históricos' job, and clicking this chart
/// goes there.
/// </para>
/// <para>
/// Uses the <b>role</b> chart palette, not the identity palette: every series here
/// describes the same variable, so PV is solid blue and the setpoint is a dashed green
/// that cannot be mistaken for a healthy-state dot. See <c>docs/UI_DESIGN.md</c> 3.5.
/// </para>
/// <para>
/// Redraws at 1 Hz on a timer, and only while loaded. The device emits every 2 s;
/// repainting faster is wasted work on the UI thread.
/// </para>
/// </remarks>
public partial class TrendSpark : UserControl
{
    /// <summary>A pane is a few hundred pixels wide, so more cannot be resolved.</summary>
    private const int MaxPoints = 400;

    public static readonly DependencyProperty ChannelProperty =
        DependencyProperty.Register(
            nameof(Channel), typeof(TelemetryChannel?), typeof(TrendSpark),
            new PropertyMetadata(null, OnInputsChanged));

    public static readonly DependencyProperty HistoryProperty =
        DependencyProperty.Register(
            nameof(History), typeof(ITelemetryHistory), typeof(TrendSpark),
            new PropertyMetadata(null, OnInputsChanged));

    public static readonly DependencyProperty SetpointProperty =
        DependencyProperty.Register(
            nameof(Setpoint), typeof(double?), typeof(TrendSpark),
            new PropertyMetadata(null, OnInputsChanged));

    public static readonly DependencyProperty WindowMinutesProperty =
        DependencyProperty.Register(
            nameof(WindowMinutes), typeof(double), typeof(TrendSpark),
            new PropertyMetadata(30.0, OnInputsChanged));

    public static readonly DependencyProperty PlotHeightProperty =
        DependencyProperty.Register(
            nameof(PlotHeight), typeof(double), typeof(TrendSpark),
            new PropertyMetadata(120.0));

    private readonly WpfPlot _plot = new();
    private readonly DispatcherTimer _redraw = new() { Interval = TimeSpan.FromSeconds(1) };


    public TrendSpark()
    {
        InitializeComponent();

        PlotHost.Child = _plot;
        StylePlot();

        _redraw.Tick += (_, _) => Redraw();

        Loaded += (_, _) =>
        {
            SubscribeToThemeChanges();
            StylePlot();
            Redraw();
            _redraw.Start();
        };

        // Stopping on unload matters: the detail pane is re-created as the selection
        // changes, and a timer per abandoned instance is a slow leak of UI-thread work.
        Unloaded += (_, _) =>
        {
            _redraw.Stop();
            UnsubscribeFromThemeChanges();
        };
    }

    /// <summary>Series to draw. Null draws nothing.</summary>
    public TelemetryChannel? Channel
    {
        get => (TelemetryChannel?)GetValue(ChannelProperty);
        set => SetValue(ChannelProperty, value);
    }

    public ITelemetryHistory? History
    {
        get => (ITelemetryHistory?)GetValue(HistoryProperty);
        set => SetValue(HistoryProperty, value);
    }

    /// <summary>Drawn as a dashed reference line when set.</summary>
    public double? Setpoint
    {
        get => (double?)GetValue(SetpointProperty);
        set => SetValue(SetpointProperty, value);
    }

    public double WindowMinutes
    {
        get => (double)GetValue(WindowMinutesProperty);
        set => SetValue(WindowMinutesProperty, value);
    }

    public double PlotHeight
    {
        get => (double)GetValue(PlotHeightProperty);
        set => SetValue(PlotHeightProperty, value);
    }

    private static void OnInputsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TrendSpark spark && spark.IsLoaded)
        {
            spark.Redraw();
        }
    }

    /// <summary>Resolves a theme colour for ScottPlot, which knows nothing of our tokens.</summary>
    private static PlotColor Token(string key, PlotColor fallback)
    {
        if (Application.Current?.TryFindResource(key) is System.Windows.Media.SolidColorBrush brush)
        {
            MediaColor c = brush.Color;
            return new PlotColor(c.R, c.G, c.B);
        }

        return fallback;
    }

    /// <summary>
    /// ScottPlot is not in WPF's resource tree, so repaint it when the shared surface
    /// brush changes. ThemeService updates that brush in place on every live switch.
    /// </summary>
    private void SubscribeToThemeChanges()
    {
        var theme = (Application.Current as App)?.Services?.GetService<OpenTECHub.Services.Theme.IThemeService>();
        if (theme != null)
        {
            theme.ThemeChanged -= OnThemeChanged;
            theme.ThemeChanged += OnThemeChanged;
        }
    }

    private void UnsubscribeFromThemeChanges()
    {
        var theme = (Application.Current as App)?.Services?.GetService<OpenTECHub.Services.Theme.IThemeService>();
        if (theme != null)
        {
            theme.ThemeChanged -= OnThemeChanged;
        }
    }

    private void OnThemeChanged(bool isDark)
        => Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            if (IsLoaded)
            {
                StylePlot();
                Redraw();
            }
        });

    private void StylePlot()
    {
        var plot = _plot.Plot;
        var surface = Token("SurfaceCardBrush", new PlotColor(255, 255, 255));
        var grid = Token("StrokeSubtleBrush", new PlotColor(231, 235, 240));
        var text = Token("TextMutedBrush", new PlotColor(124, 135, 149));

        plot.FigureBackground.Color = surface;
        plot.DataBackground.Color = surface;
        plot.Grid.MajorLineColor = grid;

        // No title, no axis labels, no legend: the pane already says which variable this
        // is and in what unit, three times over.
        plot.Axes.Left.TickLabelStyle.ForeColor = text;
        plot.Axes.Bottom.TickLabelStyle.ForeColor = text;
        plot.Axes.Left.TickLabelStyle.FontSize = 10;
        plot.Axes.Bottom.TickLabelStyle.FontSize = 10;
        plot.Axes.Left.FrameLineStyle.Color = grid;
        plot.Axes.Bottom.FrameLineStyle.Color = grid;
        plot.Axes.Right.FrameLineStyle.Width = 0;
        plot.Axes.Top.FrameLineStyle.Width = 0;

        _plot.UserInputProcessor.Disable();
    }

    private void Redraw()
    {
        if (Channel is not { } channel || History is not { } history)
        {
            ShowEmpty(true);
            return;
        }

        var series = history.GetSeries(channel, TimeSpan.FromMinutes(WindowMinutes), MaxPoints);

        if (series.Count < 2)
        {
            ShowEmpty(true);
            return;
        }

        ShowEmpty(false);

        var plot = _plot.Plot;
        plot.Clear();

        var pv = plot.Add.ScatterLine(series.Minutes, series.Values);
        pv.LineWidth = 2;
        pv.Color = Token("ChartPvBrush", new PlotColor(37, 99, 217));
        pv.MarkerStyle.IsVisible = false;

        if (Setpoint is { } setpoint)
        {
            // Dashed, always: a solid green line here would read as the state green of a
            // healthy dot, and the two vocabularies must not blur.
            var line = plot.Add.HorizontalLine(setpoint);
            line.Color = Token("ChartSetpointBrush", new PlotColor(34, 164, 71));
            line.LineWidth = 1.5f;
            line.LinePattern = LinePattern.Dashed;
        }

        if (channel == TelemetryChannel.PH)
        {
            foreach (var limitChannel in new[] { TelemetryChannel.PHLowerLimit, TelemetryChannel.PHUpperLimit })
            {
                var limit = history.GetSetpointSeries(limitChannel, TimeSpan.FromMinutes(WindowMinutes), MaxPoints);
                if (!limit.Values.Any(double.IsFinite)) continue;
                var line = plot.Add.ScatterLine(limit.Minutes, limit.Values);
                line.MarkerStyle.IsVisible = false;
                line.LineWidth = 1.2f;
                line.LinePattern = LinePattern.Dotted;
                line.Color = Token("ChartSetpointBrush", new PlotColor(34, 164, 71)).WithAlpha(0.45f);
            }
        }

        plot.Axes.AutoScale();
        _plot.Refresh();
    }

    private void ShowEmpty(bool empty)
    {
        EmptyNotice.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        PlotHost.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }
}
