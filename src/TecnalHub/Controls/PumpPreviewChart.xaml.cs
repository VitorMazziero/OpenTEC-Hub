using Microsoft.Extensions.DependencyInjection;
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
/// The external-pump profile preview: simulated flow Q(t) on the left axis and accumulated
/// volume V(t) on its own right axis, redrawn whenever the staged profile changes.
/// </summary>
/// <remarks>
/// It shows the same two curves v.6's pump dialog drew, but inline in the card rather than in a
/// modal. Unlike the live charts it has no timer — the preview is a function of the staged
/// parameters, so it redraws only when <see cref="Preview"/> changes (or the theme does).
/// </remarks>
public partial class PumpPreviewChart : UserControl
{
    public static readonly DependencyProperty PreviewProperty =
        DependencyProperty.Register(
            nameof(Preview), typeof(PumpPreview), typeof(PumpPreviewChart),
            new PropertyMetadata(null, OnInputsChanged));

    public static readonly DependencyProperty PlotHeightProperty =
        DependencyProperty.Register(
            nameof(PlotHeight), typeof(double), typeof(PumpPreviewChart),
            new PropertyMetadata(200.0));

    private readonly WpfPlot _plot = new();

    public PumpPreviewChart()
    {
        InitializeComponent();

        PlotHost.Child = _plot;
        StylePlot();

        Loaded += (_, _) =>
        {
            SubscribeToThemeChanges();
            StylePlot();
            Redraw();
        };

        Unloaded += (_, _) => UnsubscribeFromThemeChanges();
    }

    /// <summary>The sampled flow/volume preview. Null draws the empty notice.</summary>
    public PumpPreview? Preview
    {
        get => (PumpPreview?)GetValue(PreviewProperty);
        set => SetValue(PreviewProperty, value);
    }

    public double PlotHeight
    {
        get => (double)GetValue(PlotHeightProperty);
        set => SetValue(PlotHeightProperty, value);
    }

    private static void OnInputsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PumpPreviewChart chart && chart.IsLoaded)
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
        var theme = ((App)Application.Current).Services?.GetService<TecnalHub.Services.Theme.IThemeService>();
        if (theme != null)
        {
            theme.ThemeChanged -= OnThemeChanged;
            theme.ThemeChanged += OnThemeChanged;
        }
    }

    private void UnsubscribeFromThemeChanges()
    {
        var theme = ((App)Application.Current).Services?.GetService<TecnalHub.Services.Theme.IThemeService>();
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

        StyleAxis(plot.Axes.Left, text, grid);
        StyleAxis(plot.Axes.Bottom, text, grid);
        StyleAxis(plot.Axes.Right, text, grid);
        plot.Axes.Top.FrameLineStyle.Width = 0;

        plot.Legend.IsVisible = true;
        plot.Legend.Alignment = Alignment.UpperLeft;
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
        if (Preview is not { } preview || preview.Minutes.Count < 2)
        {
            ShowEmpty(true);
            return;
        }

        ShowEmpty(false);

        var plot = _plot.Plot;
        plot.Clear();

        var minutes = preview.Minutes.ToArray();

        var flow = plot.Add.ScatterLine(minutes, preview.FlowMlPerMin.ToArray());
        flow.LineWidth = 2;
        flow.Color = Token("ChartPvBrush", new PlotColor(37, 99, 217));
        flow.MarkerStyle.IsVisible = false;
        flow.LegendText = "Vazão (mL/min)";

        // Reuse ScottPlot's built-in right axis. AddRightAxis() on every staged mode
        // change leaked another Y axis into the plot and progressively crushed the graph.
        var rightAxis = plot.Axes.Right;
        rightAxis.TickLabelStyle.ForeColor = Token("TextMutedBrush", new PlotColor(124, 135, 149));
        rightAxis.TickLabelStyle.FontSize = 10;

        var volume = plot.Add.ScatterLine(minutes, preview.VolumeMl.ToArray());
        volume.LineWidth = 1.5f;
        volume.Color = Token("ChartOutputBrush", new PlotColor(198, 127, 0));
        volume.MarkerStyle.IsVisible = false;
        volume.LegendText = "Volume (mL)";
        volume.Axes.YAxis = rightAxis;

        plot.Axes.AutoScale();
        _plot.Refresh();
    }

    private void ShowEmpty(bool empty)
    {
        EmptyNotice.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        PlotHost.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }
}
