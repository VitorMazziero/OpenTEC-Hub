using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ScottPlot;
using ScottPlot.WPF;
using TecnalHub.Services.Control;

using MediaColor = System.Windows.Media.Color;
using PlotColor = ScottPlot.Color;

namespace TecnalHub.Controls;

/// <summary>
/// The cascade tuning chart: PV, setpoint and effort on the left percent axis, and the kLa
/// demand on its own right axis, over the recent run.
/// </summary>
/// <remarks>
/// It reads <see cref="CascadeTrend"/> — the ring the cascade service fills on every armed
/// step — and redraws at 1 Hz while loaded, exactly as <see cref="TrendSpark"/> does. It never
/// touches the controller; it only shows it. The kLa series appears only while engaged on the
/// path, so it is drawn against a second axis rather than crammed onto the percent scale.
/// </remarks>
public partial class CascadeChart : UserControl
{
    public static readonly DependencyProperty TrendProperty =
        DependencyProperty.Register(
            nameof(Trend), typeof(CascadeTrend), typeof(CascadeChart),
            new PropertyMetadata(null, OnInputsChanged));

    public static readonly DependencyProperty PlotHeightProperty =
        DependencyProperty.Register(
            nameof(PlotHeight), typeof(double), typeof(CascadeChart),
            new PropertyMetadata(220.0));

    private readonly WpfPlot _plot = new();
    private readonly DispatcherTimer _redraw = new() { Interval = TimeSpan.FromSeconds(1) };
    private System.Windows.Media.SolidColorBrush? _themeSentinel;

    public CascadeChart()
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

        Unloaded += (_, _) =>
        {
            _redraw.Stop();
            UnsubscribeFromThemeChanges();
        };
    }

    /// <summary>The live sample ring. Null draws the empty notice.</summary>
    public CascadeTrend? Trend
    {
        get => (CascadeTrend?)GetValue(TrendProperty);
        set => SetValue(TrendProperty, value);
    }

    public double PlotHeight
    {
        get => (double)GetValue(PlotHeightProperty);
        set => SetValue(PlotHeightProperty, value);
    }

    private static void OnInputsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CascadeChart chart && chart.IsLoaded)
        {
            chart.Redraw();
        }
    }

    private static PlotColor Token(string key, PlotColor fallback)
    {
        if (Application.Current?.TryFindResource(key) is System.Windows.Media.SolidColorBrush brush)
        {
            MediaColor c = brush.Color;
            return new PlotColor(c.R, c.G, c.B);
        }

        return fallback;
    }

    private void SubscribeToThemeChanges()
    {
        var sentinel = Application.Current?.TryFindResource("SurfaceCardBrush")
            as System.Windows.Media.SolidColorBrush;

        if (ReferenceEquals(_themeSentinel, sentinel))
        {
            return;
        }

        UnsubscribeFromThemeChanges();
        _themeSentinel = sentinel;

        if (_themeSentinel is not null)
        {
            _themeSentinel.Changed += OnThemeSentinelChanged;
        }
    }

    private void UnsubscribeFromThemeChanges()
    {
        if (_themeSentinel is not null)
        {
            _themeSentinel.Changed -= OnThemeSentinelChanged;
            _themeSentinel = null;
        }
    }

    private void OnThemeSentinelChanged(object? sender, EventArgs e)
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

        StyleAxis(plot.Axes.Left, text, grid);
        StyleAxis(plot.Axes.Bottom, text, grid);
        StyleAxis(plot.Axes.Right, text, grid);
        plot.Axes.Top.FrameLineStyle.Width = 0;

        plot.Legend.IsVisible = true;
        plot.Legend.Alignment = Alignment.UpperRight;
        plot.Legend.FontSize = 10;
        plot.Legend.BackgroundColor = surface;
        plot.Legend.FontColor = text;
        plot.Legend.OutlineColor = grid;

        _plot.UserInputProcessor.Disable();
    }

    private static void StyleAxis(ScottPlot.IAxis axis, PlotColor text, PlotColor grid)
    {
        axis.TickLabelStyle.ForeColor = text;
        axis.TickLabelStyle.FontSize = 10;
        axis.FrameLineStyle.Color = grid;
    }

    private void Redraw()
    {
        if (Trend is not { } trend)
        {
            ShowEmpty(true);
            return;
        }

        var snapshot = trend.Snapshot();
        if (snapshot.Count < 2)
        {
            ShowEmpty(true);
            return;
        }

        ShowEmpty(false);

        var plot = _plot.Plot;
        plot.Clear();

        // No axis labels: the legend already names each series and its unit, the same
        // restraint TrendSpark keeps.
        var pv = plot.Add.ScatterLine(snapshot.Minutes, snapshot.Pv);
        pv.LineWidth = 2;
        pv.Color = Token("ChartPvBrush", new PlotColor(37, 99, 217));
        pv.MarkerStyle.IsVisible = false;
        pv.LegendText = "PV O₂ (%)";

        var sp = plot.Add.ScatterLine(snapshot.Minutes, snapshot.Setpoint);
        sp.LineWidth = 1.5f;
        sp.Color = Token("ChartSetpointBrush", new PlotColor(34, 164, 71));
        sp.LinePattern = LinePattern.Dashed;
        sp.MarkerStyle.IsVisible = false;
        sp.LegendText = "SP (%)";

        var output = plot.Add.ScatterLine(snapshot.Minutes, snapshot.Output);
        output.LineWidth = 1.5f;
        output.Color = Token("ChartOutputBrush", new PlotColor(198, 127, 0));
        output.MarkerStyle.IsVisible = false;
        output.LegendText = "Saída (%)";

        // kLa lives on its own right axis: it is in units per hour, not percent, and only
        // exists while engaged on the path.
        if (snapshot.KlaCount >= 2)
        {
            var rightAxis = plot.Axes.AddRightAxis();
            rightAxis.TickLabelStyle.ForeColor = Token("TextMutedBrush", new PlotColor(124, 135, 149));
            rightAxis.TickLabelStyle.FontSize = 10;

            var kla = plot.Add.ScatterLine(snapshot.KlaMinutes, snapshot.Kla);
            kla.LineWidth = 1.5f;
            kla.Color = Token("ChartLimitBrush", new PlotColor(139, 92, 246));
            kla.MarkerStyle.IsVisible = false;
            kla.LegendText = "kLa (/h)";
            kla.Axes.YAxis = rightAxis;
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
