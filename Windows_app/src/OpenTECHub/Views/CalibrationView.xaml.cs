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
    private readonly WpfPlot _doseRepPlot = new();
    private readonly WpfPlot _dosePulsePlot = new();
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
        DoseRepPlotHost.Child = _doseRepPlot;
        DosePulsePlotHost.Child = _dosePulsePlot;
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
                RedrawDosePlots();
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
            _pumpSubscribed.DosePoint.Points.CollectionChanged += OnDosePointsChanged;
            _pumpSubscribed.DosePoint.Repetitions.CollectionChanged += OnDosePointsChanged;
            _pumpSubscribed.DosePoint.PulseChecks.CollectionChanged += OnDosePointsChanged;
        }
        RedrawFlowCurve();
        RedrawPumpCurve();
        RedrawDosePlots();
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
            _pumpSubscribed.DosePoint.Points.CollectionChanged -= OnDosePointsChanged;
            _pumpSubscribed.DosePoint.Repetitions.CollectionChanged -= OnDosePointsChanged;
            _pumpSubscribed.DosePoint.PulseChecks.CollectionChanged -= OnDosePointsChanged;
            _pumpSubscribed = null;
        }
    }

    private void OnDosePointsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        RedrawPumpCurve();
        RedrawDosePlots();
    }

    /// <summary>
    /// Dose-point section: flow of each repetition with the mean and ±CV band, and the pulse
    /// checks as volume per pulse against pulse length, with the line v = (Q/60000)(t − t₀).
    /// </summary>
    private void RedrawDosePlots()
    {
        var surface = ToPlotColor(TryBrush("SurfaceCardBrush"), MediaColors.White);
        var text = ToPlotColor(TryBrush("TextPrimaryBrush"), MediaColors.Black);
        var grid = ToPlotColor(TryBrush("StrokeDefaultBrush"), MediaColors.LightGray);
        var accent = ToPlotColor(TryBrush("AccentBrush"), MediaColors.SteelBlue);
        var output = ToPlotColor(TryBrush("ChartOutputBrush"), MediaColors.DarkOrange);

        void Style(ScottPlot.Plot plot, string title, string x, string y)
        {
            plot.Clear();
            plot.FigureBackground.Color = surface;
            plot.DataBackground.Color = surface;
            plot.Axes.Color(text);
            plot.Grid.MajorLineColor = grid.WithAlpha(0.45);
            plot.Axes.Title.Label.Text = title;
            plot.Axes.Title.Label.ForeColor = text;
            plot.Axes.Bottom.Label.Text = x;
            plot.Axes.Left.Label.Text = y;
            plot.Legend.IsVisible = true;
            plot.Legend.Alignment = Alignment.LowerRight;
            plot.Legend.FontSize = 10;
            plot.Legend.BackgroundColor = surface;
            plot.Legend.FontColor = text;
            plot.Legend.OutlineColor = grid;
        }

        var dose = _pumpSubscribed?.DosePoint;
        var reps = dose?.Repetitions.ToArray() ?? [];
        DoseRepEmpty.Visibility = reps.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        DoseRepPlotHost.Visibility = reps.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (reps.Length > 0 && dose is not null)
        {
            var plot = _doseRepPlot.Plot;
            Style(plot, "Repetições do ponto de dosagem", "Repetição", "Vazão (mL/min)");
            var xs = Enumerable.Range(1, reps.Length).Select(i => (double)i).ToArray();
            var mean = dose.MeanRateMlMin;
            var halfBand = double.IsFinite(dose.CvPercent) ? mean * dose.CvPercent / 100.0 : 0.0;
            if (halfBand > 0)
            {
                var band = plot.Add.HorizontalSpan(mean - halfBand, mean + halfBand);
                band.FillStyle.Color = accent.WithAlpha(0.12);
                band.LineStyle.Width = 0;
                band.LegendText = $"± CV ({dose.CvPercent:F1} %)";
            }

            var meanLine = plot.Add.HorizontalLine(mean);
            meanLine.Color = accent;
            meanLine.LineWidth = 1.5f;
            meanLine.LinePattern = LinePattern.Dashed;
            meanLine.LegendText = $"Média {mean:F3} mL/min";
            var scatter = plot.Add.Scatter(xs, reps.Select(r => r.RateMlMin).ToArray());
            scatter.LineWidth = 0;
            scatter.MarkerSize = 10;
            scatter.MarkerShape = MarkerShape.FilledDiamond;
            scatter.Color = output;
            scatter.LegendText = "Vazão por ensaio";
            var lo = reps.Min(r => r.RateMlMin);
            var hi = reps.Max(r => r.RateMlMin);
            var pad = Math.Max(Math.Max(halfBand * 2, (hi - lo) * 0.5), mean * 0.03);
            plot.Axes.SetLimits(0.5, reps.Length + 0.5, Math.Max(0, lo - pad), hi + pad);
            plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericFixedInterval(1);
            _doseRepPlot.Refresh();
        }

        var checks = dose?.PulseChecks.ToArray() ?? [];
        DosePulseEmpty.Visibility = checks.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        DosePulsePlotHost.Visibility = checks.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (checks.Length > 0 && dose is not null)
        {
            var plot = _dosePulsePlot.Plot;
            Style(plot, "Verificação em pulsos", "Duração do pulso (ms)", "Volume por pulso (mL)");
            var rate = dose.MeanRateMlMin;
            var t0 = OpenTECHub.Services.Calibration.PumpDosePointMath.DeadTimeMs(checks, rate) ?? 0.0;
            var tMax = Math.Max(checks.Max(c => c.OnMs) * 1.2, 500.0);
            var line = plot.Add.Line(t0, 0.0, tMax, rate * (tMax - t0) / 60000.0);
            line.Color = accent;
            line.LineWidth = 1.5f;
            line.LegendText = $"v = (Q/60000)·(t − {t0:F0} ms)";
            var ideal = plot.Add.Line(0.0, 0.0, tMax, rate * tMax / 60000.0);
            ideal.Color = grid;
            ideal.LinePattern = LinePattern.Dashed;
            ideal.LegendText = "Sem atraso de partida";
            var scatter = plot.Add.Scatter(checks.Select(c => c.OnMs).ToArray(), checks.Select(c => c.PerPulseMl).ToArray());
            scatter.LineWidth = 0;
            scatter.MarkerSize = 9;
            scatter.Color = output;
            scatter.LegendText = "Medido (média por pulso)";
            plot.Legend.Alignment = Alignment.UpperLeft;
            plot.Axes.SetLimits(0, tMax, 0, rate * tMax / 60000.0 * 1.1);
            _dosePulsePlot.Refresh();
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
        var lowColor = ToPlotColor(TryBrush("StateAlarmBrush"), MediaColors.IndianRed);
        var highColor = ToPlotColor(TryBrush("StateOkBrush"), MediaColors.SeaGreen);

        plot.FigureBackground.Color = surface;
        plot.DataBackground.Color = surface;
        plot.Axes.Color(text);
        plot.Grid.MajorLineColor = grid.WithAlpha(0.45);
        plot.Axes.Bottom.Label.Text = "Velocidade S";
        plot.Axes.Left.Label.Text = "Vazão (mL/min)";
        plot.Axes.Title.Label.Text = "Curva de calibração da bomba";
        plot.Axes.Title.Label.ForeColor = text;

        var maximumFlow = 1.0;
        var hasMeasuredPoints = false;
        if (_pumpSubscribed is { } viewModel)
        {
            var runs = viewModel.Runs.Where(run => double.IsFinite(run.SpeedUnits) && double.IsFinite(run.FlowMlPerMin)).ToArray();
            if (runs.Length > 0)
            {
                hasMeasuredPoints = true;
                var scatter = plot.Add.Scatter(
                    runs.Select(run => run.SpeedUnits).ToArray(),
                    runs.Select(run => run.FlowMlPerMin).ToArray());
                scatter.LineWidth = 0;
                scatter.MarkerSize = 8;
                scatter.Color = accent;
                scatter.LegendText = "Pontos medidos";
                maximumFlow = Math.Max(maximumFlow, runs.Max(run => run.FlowMlPerMin));
            }

            if (viewModel.TryGetPreviewCurve(out var curve, out var curveIsValid))
            {
                var st = Math.Clamp(curve.TransitionSpeed, 1.0, 999.0);

                var lowMinimum = runs.Where(run => run.SpeedUnits <= st)
                                     .Select(run => run.SpeedUnits)
                                     .DefaultIfEmpty(st)
                                     .Min();
                var highMaximum = runs.Where(run => run.SpeedUnits > st)
                                      .Select(run => run.SpeedUnits)
                                      .DefaultIfEmpty(st)
                                      .Max();

                // Assim como no fluxômetro, cada equação aparece apenas na faixa medida.
                const int steps = 50;
                var lowSpeeds = new double[steps + 1];
                var lowFlows = new double[steps + 1];
                for (var i = 0; i <= steps; i++)
                {
                    var s = lowMinimum + ((st - lowMinimum) * i / steps);
                    lowSpeeds[i] = s;
                    var flow = curve.FlowFromSpeed(s);
                    lowFlows[i] = curveIsValid ? Math.Max(0.0, flow) : flow;
                }
                var lowLine = plot.Add.Scatter(lowSpeeds, lowFlows);
                lowLine.MarkerSize = 0;
                lowLine.LineWidth = 2;
                lowLine.Color = curveIsValid ? lowColor : ToPlotColor(TryBrush("StateWarningBrush"), MediaColors.DarkOrange);
                lowLine.LinePattern = curveIsValid ? LinePattern.Solid : LinePattern.Dashed;
                lowLine.LegendText = curveIsValid ? $"Curva inferior (S ≤ {st:F1})" : "Candidata inválida — não enviar";

                var highSpeeds = new double[steps + 1];
                var highFlows = new double[steps + 1];
                for (var i = 0; i <= steps; i++)
                {
                    var s = st + ((highMaximum - st) * i / steps);
                    highSpeeds[i] = s;
                    var flow = curve.FlowFromSpeed(s);
                    highFlows[i] = curveIsValid ? Math.Max(0.0, flow) : flow;
                }
                var highLine = plot.Add.Scatter(highSpeeds, highFlows);
                highLine.MarkerSize = 0;
                highLine.LineWidth = 2;
                highLine.Color = curveIsValid ? highColor : ToPlotColor(TryBrush("StateWarningBrush"), MediaColors.DarkOrange);
                highLine.LinePattern = curveIsValid ? LinePattern.Solid : LinePattern.Dashed;
                highLine.LegendText = curveIsValid ? $"Curva superior (S > {st:F1})" : "Candidata inválida — não enviar";

                var vLine = plot.Add.VerticalLine(st);
                vLine.Color = grid;
                vLine.LineWidth = 1;

                var allCurveFlows = lowFlows.Concat(highFlows).Where(double.IsFinite).ToArray();
                var maxFlowCurve = allCurveFlows.DefaultIfEmpty(0.0).Max();
                maximumFlow = Math.Max(maximumFlow, maxFlowCurve);

                var speedMaximum = Math.Max(highMaximum, st);
                var minimumCurveFlow = allCurveFlows.DefaultIfEmpty(0.0).Min();
                plot.Axes.SetLimits(0, speedMaximum * 1.04,
                    curveIsValid ? 0.0 : Math.Min(0.0, minimumCurveFlow * 1.1), maximumFlow * 1.1);
            }
        }

        // Dose points: their own marker, to see whether they agree with the curve.
        var dosePoints = _pumpSubscribed?.DosePoint.PointsForSave()
            .Where(p => double.IsFinite(p.SpeedUnits) && double.IsFinite(p.RateMlMin)).ToArray() ?? [];
        if (dosePoints.Length > 0)
        {
            var dose = plot.Add.Scatter(dosePoints.Select(p => p.SpeedUnits).ToArray(), dosePoints.Select(p => p.RateMlMin).ToArray());
            dose.LineWidth = 0;
            dose.MarkerSize = 11;
            dose.MarkerShape = MarkerShape.FilledDiamond;
            dose.Color = ToPlotColor(TryBrush("ChartOutputBrush"), MediaColors.DarkOrange);
            dose.LegendText = "Ponto de dosagem";
            if (!hasMeasuredPoints && (_pumpSubscribed is null || !_pumpSubscribed.TryGetPreviewCurve(out _, out _)))
            {
                hasMeasuredPoints = false;
                var maxRate = dosePoints.Max(p => p.RateMlMin);
                plot.Axes.SetLimits(0, Math.Max(200.0, dosePoints.Max(p => p.SpeedUnits) * 1.3), 0, Math.Max(1.0, maxRate * 1.4));
                plot.Legend.IsVisible = true;
                plot.Legend.Alignment = Alignment.UpperLeft;
                plot.Legend.FontSize = 10;
                plot.Legend.BackgroundColor = surface;
                plot.Legend.FontColor = text;
                plot.Legend.OutlineColor = grid;
                _pumpPlot.Refresh();
                return;
            }
        }

        plot.Legend.IsVisible = true;
        plot.Legend.Alignment = Alignment.UpperLeft;
        plot.Legend.FontSize = 10;
        plot.Legend.BackgroundColor = surface;
        plot.Legend.FontColor = text;
        plot.Legend.OutlineColor = grid;
        if (hasMeasuredPoints && (_pumpSubscribed is null || !_pumpSubscribed.TryGetPreviewCurve(out _, out _)))
        {
            var runs = _pumpSubscribed!.Runs.Where(run => double.IsFinite(run.SpeedUnits) && double.IsFinite(run.FlowMlPerMin)).ToArray();
            var minSpeed = runs.Min(run => run.SpeedUnits);
            var maxSpeed = runs.Max(run => run.SpeedUnits);
            var minFlow = runs.Min(run => run.FlowMlPerMin);
            var maxFlow = runs.Max(run => run.FlowMlPerMin);
            var speedPadding = Math.Max(5.0, (maxSpeed - minSpeed) * 0.08);
            var flowPadding = Math.Max(1.0, (maxFlow - minFlow) * 0.10);
            plot.Axes.SetLimits(Math.Max(0.0, minSpeed - speedPadding), maxSpeed + speedPadding,
                Math.Max(0.0, minFlow - flowPadding), maxFlow + flowPadding);
        }
        else if (!hasMeasuredPoints && (_pumpSubscribed is null || !_pumpSubscribed.TryGetPreviewCurve(out _, out _)))
        {
            plot.Axes.SetLimits(0, 1000, 0, 1);
        }
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
