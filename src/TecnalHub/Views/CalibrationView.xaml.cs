using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ScottPlot;
using ScottPlot.WPF;
using TecnalHub.Services.Calibration;
using TecnalHub.ViewModels;

using MediaColor = System.Windows.Media.Color;
using MediaColors = System.Windows.Media.Colors;
using PlotColor = ScottPlot.Color;

namespace TecnalHub.Views;

/// <summary>Calibration page and its ScottPlot flow-curve rendering surface.</summary>
public partial class CalibrationView : UserControl
{
    private readonly WpfPlot _flowPlot = new();
    private FlowCalibrationViewModel? _subscribed;


    public CalibrationView()
    {
        InitializeComponent();
        FlowPlotHost.Child = _flowPlot;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += (_, _) => Attach();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SubscribeToThemeChanges();
        Attach();
        RedrawFlowCurve();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UnsubscribeFromThemeChanges();
        Detach();
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
                RedrawFlowCurve();
            }
        });

    private void Attach()
    {
        Detach();
        _subscribed = (DataContext as CalibrationViewModel)?.Flow;
        if (_subscribed is not null)
        {
            _subscribed.CurveChanged += RedrawFlowCurve;
        }
        RedrawFlowCurve();
    }

    private void Detach()
    {
        if (_subscribed is not null)
        {
            _subscribed.CurveChanged -= RedrawFlowCurve;
            _subscribed = null;
        }
    }

    private void RedrawFlowCurve()
    {
        var plot = _flowPlot.Plot;
        plot.Clear();
        var hasCalibrationData = false;

        var surface = ToPlotColor(TryBrush("SurfaceCardBrush"), MediaColors.White);
        var text = ToPlotColor(TryBrush("TextSecondaryBrush"), MediaColors.Gray);
        var grid = ToPlotColor(TryBrush("StrokeDefaultBrush"), MediaColors.LightGray);
        var accent = ToPlotColor(TryBrush("AccentBrush"), MediaColors.SteelBlue);

        plot.FigureBackground.Color = surface;
        plot.DataBackground.Color = surface;
        plot.Axes.Color(text);
        plot.Grid.MajorLineColor = grid.WithAlpha(0.45);
        plot.Axes.Bottom.Label.Text = "Tensão (V)";
        plot.Axes.Left.Label.Text = "Vazão real (L/min)";
        plot.Axes.Title.Label.Text = "Curva de calibração da vazão";
        plot.Axes.Title.Label.ForeColor = ToPlotColor(TryBrush("TextPrimaryBrush"), MediaColors.Black);

        if (_subscribed is { } viewModel)
        {
            var points = viewModel.GetValidPoints();

            // The two fitted segments are the generated curves; the scatter is the measured
            // data. Naming all three puts the legend to work distinguishing them.
            DrawSegment(plot, viewModel.Curve.LowVoltage,
                points.Where(point => point.Voltage <= FlowCalibrationCurve.SplitVoltage)
                      .Select(point => point.Voltage).DefaultIfEmpty(0).Min(),
                FlowCalibrationCurve.SplitVoltage,
                ToPlotColor(TryBrush("StateAlarmBrush"), MediaColors.IndianRed),
                "Curva inferior (V ≤ 0,0545)");

            var highMaximum = points.Where(point => point.Voltage > FlowCalibrationCurve.SplitVoltage)
                                    .Select(point => point.Voltage)
                                    .DefaultIfEmpty(FlowCalibrationCurve.SplitVoltage + 0.1)
                                    .Max();
            DrawSegment(plot, viewModel.Curve.HighVoltage,
                FlowCalibrationCurve.SplitVoltage,
                Math.Max(highMaximum * 1.05, FlowCalibrationCurve.SplitVoltage + 0.01),
                ToPlotColor(TryBrush("StateOkBrush"), MediaColors.SeaGreen),
                "Curva superior (V > 0,0545)");

            if (points.Count > 0)
            {
                hasCalibrationData = true;
                var scatter = plot.Add.Scatter(
                    points.Select(point => point.Voltage).ToArray(),
                    points.Select(point => point.Flow).ToArray());
                scatter.LineWidth = 0;
                scatter.MarkerSize = 8;
                scatter.Color = accent;
                scatter.LegendText = "Pontos medidos";
            }

            plot.Legend.IsVisible = true;
            plot.Legend.Alignment = Alignment.UpperLeft;
            plot.Legend.FontSize = 10;
            plot.Legend.BackgroundColor = surface;
            plot.Legend.FontColor = text;
            plot.Legend.OutlineColor = grid;
        }

        var split = plot.Add.VerticalLine(FlowCalibrationCurve.SplitVoltage);
        split.Color = grid;
        split.LineWidth = 1;
        if (hasCalibrationData)
        {
            plot.Axes.AutoScale();
        }
        else
        {
            // The flowmeter works in a narrow positive voltage domain. ScottPlot's
            // empty default (-1..1) made the dedicated calibration page look like a
            // generic mathematical plot and visually buried the 0.0545 V split.
            plot.Axes.SetLimits(0, 0.12, 0, 10);
        }
        _flowPlot.Refresh();
    }

    private static void DrawSegment(
        Plot plot,
        PolynomialCalibration? polynomial,
        double minimum,
        double maximum,
        PlotColor color,
        string legendText)
    {
        if (polynomial is not { } curve || maximum <= minimum)
        {
            return;
        }

        const int count = 80;
        var x = new double[count];
        var y = new double[count];
        for (var index = 0; index < count; index++)
        {
            x[index] = minimum + ((maximum - minimum) * index / (count - 1));
            y[index] = curve.Evaluate(x[index]);
        }

        var line = plot.Add.Scatter(x, y);
        line.MarkerSize = 0;
        line.LineWidth = 2;
        line.Color = color;
        line.LegendText = legendText;
    }

    private static SolidColorBrush? TryBrush(string key)
        => Application.Current?.TryFindResource(key) as SolidColorBrush;

    private static PlotColor ToPlotColor(SolidColorBrush? brush, MediaColor fallback)
    {
        var color = brush?.Color ?? fallback;
        return new PlotColor(color.R, color.G, color.B, color.A);
    }
}
