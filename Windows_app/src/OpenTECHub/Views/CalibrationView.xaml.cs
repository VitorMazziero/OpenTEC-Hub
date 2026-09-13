using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ScottPlot;
using ScottPlot.WPF;
using OpenTECHub.Services.Calibration;
using OpenTECHub.ViewModels;

using MediaColor = System.Windows.Media.Color;
using MediaColors = System.Windows.Media.Colors;
using PlotColor = ScottPlot.Color;

namespace OpenTECHub.Views;

/// <summary>Calibration page and its ScottPlot flow-curve rendering surface.</summary>
public partial class CalibrationView : UserControl
{
    /// <summary>Roughly the flowmeter's full-scale output — 14 L/min sits at about 1,29 V.</summary>
    private const double FullScaleVoltage = 1.4;

    /// <summary>Where the low segment starts when no point below the split has been measured.</summary>
    private const double FirstMeasuredVoltage = 0.01;

    private readonly WpfPlot _flowPlot = new();
    private readonly WpfPlot _pumpPlot = new();
    private FlowCalibrationViewModel? _flowSubscribed;
    private PumpCalibrationViewModel? _pumpSubscribed;


    public CalibrationView()
    {
        InitializeComponent();
        // Keep the external-pump calibration immediately to the right of airflow while
        // preserving the existing XAML blocks and their design-time readability.
        CalibrationTabs.Items.Remove(PumpTab);
        CalibrationTabs.Items.Insert(3, PumpTab);
        FlowPlotHost.Child = _flowPlot;
        PumpPlotHost.Child = _pumpPlot;
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
                RedrawFlowCurve();
                RedrawPumpCurve();
            }
        });

    private void Attach()
    {
        Detach();
        var calibration = DataContext as CalibrationViewModel;
        _flowSubscribed = calibration?.Flow;
        _pumpSubscribed = calibration?.Pump;
        if (_flowSubscribed is not null)
        {
            _flowSubscribed.CurveChanged += RedrawFlowCurve;
        }
        if (_pumpSubscribed is not null)
        {
            _pumpSubscribed.CurveChanged += RedrawPumpCurve;
        }
        RedrawFlowCurve();
        RedrawPumpCurve();
    }

    private void Detach()
    {
        if (_flowSubscribed is not null)
        {
            _flowSubscribed.CurveChanged -= RedrawFlowCurve;
            _flowSubscribed = null;
        }
        if (_pumpSubscribed is not null)
        {
            _pumpSubscribed.CurveChanged -= RedrawPumpCurve;
            _pumpSubscribed = null;
        }
    }

    private void RedrawFlowCurve()
    {
        var plot = _flowPlot.Plot;
        plot.Clear();
        var hasCalibrationData = false;

        var surface = ToPlotColor(TryBrush("SurfaceCardBrush"), MediaColors.White);
        // Axis text is content, not chrome; the secondary token reads as dim on a dark card.
        var text = ToPlotColor(TryBrush("TextPrimaryBrush"), MediaColors.Black);
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

        if (_flowSubscribed is { } viewModel)
        {
            var points = viewModel.GetValidPoints();
            hasCalibrationData = points.Count > 0;
            var vt = viewModel.TransitionVoltage;

            // Each segment is drawn only across the voltages it was actually fitted for.
            // Extrapolating the low quartic down to 0 V used to plot an unphysical negative
            // flow, and stubbing the high curve just past the split hid its whole range.
            var lowMinimum = points.Where(point => point.Voltage <= vt)
                                   .Select(point => point.Voltage)
                                   .DefaultIfEmpty(FirstMeasuredVoltage)
                                   .Min();
            var highMaximum = points.Where(point => point.Voltage > vt)
                                    .Select(point => point.Voltage)
                                    .DefaultIfEmpty(FullScaleVoltage)
                                    .Max();

            // The two fitted segments are the generated curves; the scatter is the measured
            // data. Naming all three puts the legend to work distinguishing them.
            DrawSegment(plot, viewModel.Curve.LowVoltage,
                lowMinimum, vt,
                ToPlotColor(TryBrush("StateAlarmBrush"), MediaColors.IndianRed),
                $"Curva inferior (V ≤ {vt:F4})");

            DrawSegment(plot, viewModel.Curve.HighVoltage,
                vt,
                Math.Max(highMaximum, vt + 0.01),
                ToPlotColor(TryBrush("StateOkBrush"), MediaColors.SeaGreen),
                $"Curva superior (V > {vt:F4})");

            if (hasCalibrationData)
            {
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

            var voltageLimit = Math.Max(highMaximum, FullScaleVoltage * 0.1);
            var flowLimit = viewModel.Curve.Evaluate(voltageLimit) ?? 10.0;
            // Flow is positive by construction, so the axis starts at zero rather than
            // reserving room for a polynomial artefact below the first measured point.
            plot.Axes.SetLimits(0, voltageLimit * 1.04, 0, Math.Max(flowLimit * 1.08, 1.0));
        }

        var splitVt = _flowSubscribed?.TransitionVoltage ?? FlowCalibrationCurve.DefaultTransitionVoltage;
        var split = plot.Add.VerticalLine(splitVt);
        split.Color = grid;
        split.LineWidth = 1;
        _flowPlot.Refresh();
    }

    private void RedrawPumpCurve()
    {
        var plot = _pumpPlot.Plot;
        plot.Clear();

        var surface = ToPlotColor(TryBrush("SurfaceCardBrush"), MediaColors.White);
        var text = ToPlotColor(TryBrush("TextPrimaryBrush"), MediaColors.Black);
        var grid = ToPlotColor(TryBrush("StrokeDefaultBrush"), MediaColors.LightGray);
        var accent = ToPlotColor(TryBrush("AccentBrush"), MediaColors.SteelBlue);
        var fitColor = ToPlotColor(TryBrush("StateOkBrush"), MediaColors.SeaGreen);

        plot.FigureBackground.Color = surface;
        plot.DataBackground.Color = surface;
        plot.Axes.Color(text);
        plot.Grid.MajorLineColor = grid.WithAlpha(0.45);
        plot.Axes.Bottom.Label.Text = "Velocidade S";
        plot.Axes.Left.Label.Text = "Vazão (mL/min)";
        plot.Axes.Title.Label.Text = "Curva volumétrica da bomba";
        plot.Axes.Title.Label.ForeColor = text;

        var maximumFlow = 1.0;
        if (_pumpSubscribed is { } viewModel)
        {
            var runs = viewModel.Runs.ToArray();
            if (runs.Length > 0)
            {
                var scatter = plot.Add.Scatter(
                    runs.Select(run => run.SpeedUnits).ToArray(),
                    runs.Select(run => run.FlowMlPerMin).ToArray());
                scatter.LineWidth = 0;
                scatter.MarkerSize = 8;
                scatter.Color = accent;
                scatter.LegendText = "Volumes medidos";
                maximumFlow = Math.Max(maximumFlow, runs.Max(run => run.FlowMlPerMin));
            }

            if (viewModel.TryGetDisplayedCurve(out var slope, out var intercept))
            {
                var speeds = Enumerable.Range(0, 101).Select(index => index * 10.0).ToArray();
                var flows = speeds.Select(speed => Math.Max(0.0, (slope * speed) + intercept)).ToArray();
                var line = plot.Add.Scatter(speeds, flows);
                line.MarkerSize = 0;
                line.LineWidth = 2;
                line.Color = fitColor;
                line.LegendText = viewModel.HasFit ? "Ajuste dos pontos" : "Curva informada";
                maximumFlow = Math.Max(maximumFlow, flows.Max());
            }
        }

        plot.Legend.IsVisible = true;
        plot.Legend.Alignment = Alignment.UpperLeft;
        plot.Legend.FontSize = 10;
        plot.Legend.BackgroundColor = surface;
        plot.Legend.FontColor = text;
        plot.Legend.OutlineColor = grid;
        plot.Axes.SetLimits(0, 1020, 0, maximumFlow * 1.1);
        _pumpPlot.Refresh();
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
