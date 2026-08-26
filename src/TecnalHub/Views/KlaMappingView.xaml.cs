using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ScottPlot;
using ScottPlot.WPF;
using TecnalHub.ViewModels;

using MediaColor = System.Windows.Media.Color;
using MediaColors = System.Windows.Media.Colors;
using PlotColor = ScottPlot.Color;

namespace TecnalHub.Views;

/// <summary>Theme-aware scientific plots for the D-008 mapping workflow.</summary>
public partial class KlaMappingView : UserControl
{
    private const int DisplayResolution = 81;

    private readonly WpfPlot _surfacePlot = new();
    private readonly WpfPlot _headroomPlot = new();
    private ScottPlot.Panels.ColorBar? _surfaceColorBar;
    private ScottPlot.Panels.ColorBar? _headroomColorBar;
    private KlaMappingViewModel? _subscribed;
    

    public KlaMappingView()
    {
        InitializeComponent();
        SurfacePlotHost.Child = _surfacePlot;
        HeadroomPlotHost.Child = _headroomPlot;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += (_, _) => Attach();
    }

    private KlaMappingViewModel? ViewModel => DataContext as KlaMappingViewModel;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SubscribeToThemeChanges();
        Attach();
        if (ViewModel is { } viewModel)
        {
            await viewModel.InitializeCommand.ExecuteAsync(null);
        }

        Redraw();
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

                Redraw();
            }
        });

    private void Attach()
    {
        Detach();
        _subscribed = ViewModel;
        if (_subscribed is not null)
        {
            _subscribed.VisualizationChanged += Redraw;
        }

        Redraw();
    }

    private void Detach()
    {
        if (_subscribed is not null)
        {
            _subscribed.VisualizationChanged -= Redraw;
            _subscribed = null;
        }
    }

    private void Redraw()
    {
        DrawSurface();
        DrawHeadroom();
    }

    private void DrawSurface()
    {
        var plot = _surfacePlot.Plot;
        if (_surfaceColorBar is not null)
        {
            plot.Remove(_surfaceColorBar);
            _surfaceColorBar = null;
        }

        plot.Clear();
        StylePlot(plot, "Vazão de ar Qg (L/min)", "Agitação N (rpm)");
        if (ViewModel is not { } viewModel)
        {
            _surfacePlot.Refresh();
            return;
        }

        var qMinimum = viewModel.AirflowMinimum;
        var qMaximum = viewModel.AirflowMaximum;
        var nMinimum = viewModel.AgitationMinimum;
        var nMaximum = viewModel.AgitationMaximum;

        if (viewModel.Surface is { } surface)
        {
            var heatmapValues = new double[DisplayResolution, DisplayResolution];
            var contourValues = new Coordinates3d[DisplayResolution, DisplayResolution];
            for (var row = 0; row < DisplayResolution; row++)
            {
                var normalizedN = (double)row / (DisplayResolution - 1);
                var physicalN = surface.Input.Domain.DenormalizeAgitation(normalizedN);
                for (var column = 0; column < DisplayResolution; column++)
                {
                    var normalizedQ = (double)column / (DisplayResolution - 1);
                    var physicalQ = surface.Input.Domain.DenormalizeAirflow(normalizedQ);
                    var value = surface.EvaluateNormalized(normalizedQ, normalizedN).Value;
                    heatmapValues[row, column] = value;
                    contourValues[row, column] = new Coordinates3d(physicalQ, physicalN, value);
                }
            }

            var heatmap = plot.Add.Heatmap(heatmapValues);
            heatmap.Rectangle = new CoordinateRect(qMinimum, qMaximum, nMinimum, nMaximum);
            heatmap.Colormap = new ScottPlot.Colormaps.Viridis();
            heatmap.FlipVertically = true;
            _surfaceColorBar = plot.Add.ColorBar(heatmap);
            _surfaceColorBar.Label = "kLa (h⁻¹)";

            var contours = plot.Add.ContourLines(contourValues, count: 12);
            contours.LineColor = ToPlotColor(TryBrush("TextPrimaryBrush"), MediaColors.Black).WithAlpha(0.35);
            contours.LineWidth = 0.8f;
            contours.LabelStyle.IsVisible = false;

            DrawGradient(plot, surface);
        }

        var anchors = viewModel.Anchors
            .Select(row => row.TryBuild(out var anchor) ? anchor : null)
            .Where(anchor => anchor is not null)
            .ToArray();
        if (anchors.Length > 0)
        {
            var points = plot.Add.Scatter(
                anchors.Select(anchor => anchor!.AirflowLpm).ToArray(),
                anchors.Select(anchor => anchor!.AgitationRpm).ToArray());
            points.LineWidth = 0;
            points.MarkerSize = 8;
            points.Color = ToPlotColor(TryBrush("TextPrimaryBrush"), MediaColors.Black);
        }

        if (viewModel.PathResult is { } path)
        {
            var trajectory = plot.Add.Scatter(
                path.Path.Select(point => point.AirflowLpm).ToArray(),
                path.Path.Select(point => point.AgitationRpm).ToArray());
            trajectory.MarkerSize = 0;
            trajectory.LineWidth = 3;
            trajectory.Color = ToPlotColor(TryBrush("AccentBrush"), MediaColors.DodgerBlue);

            var start = plot.Add.Scatter(
                new double[] { path.Diagnostics.SelectedStartAirflowLpm },
                new double[] { path.Diagnostics.SelectedStartAgitationRpm });
            start.LineWidth = 0;
            start.MarkerSize = 13;
            start.Color = ToPlotColor(TryBrush("StateWarningBrush"), MediaColors.Goldenrod);
        }

        if (qMaximum > qMinimum && nMaximum > nMinimum)
        {
            plot.Axes.SetLimits(qMinimum, qMaximum, nMinimum, nMaximum);
        }

        _surfacePlot.Refresh();
    }

    private static void DrawGradient(Plot plot, Services.KlaMapping.KlaSurface surface)
    {
        var color = ToPlotColor(TryBrush("TextSecondaryBrush"), MediaColors.Gray).WithAlpha(0.72);
        var domain = surface.Input.Domain;
        const int arrowAxis = 8;
        for (var row = 1; row < arrowAxis; row++)
        {
            var n = (double)row / arrowAxis;
            for (var column = 1; column < arrowAxis; column++)
            {
                var q = (double)column / arrowAxis;
                var value = surface.EvaluateNormalized(q, n);
                if (value.GradientMagnitude < surface.Input.Algorithm.GradientTermination)
                {
                    continue;
                }

                const double normalizedLength = 0.035;
                var deltaQ = normalizedLength * value.Dq / value.GradientMagnitude;
                var deltaN = normalizedLength * value.Dn / value.GradientMagnitude;
                var start = new Coordinates(
                    domain.DenormalizeAirflow(q - (deltaQ / 2)),
                    domain.DenormalizeAgitation(n - (deltaN / 2)));
                var end = new Coordinates(
                    domain.DenormalizeAirflow(q + (deltaQ / 2)),
                    domain.DenormalizeAgitation(n + (deltaN / 2)));
                var arrow = plot.Add.Arrow(new CoordinateLine(start, end));
                arrow.ArrowLineColor = color;
                arrow.ArrowFillColor = color;
                arrow.ArrowLineWidth = 0.8f;
                arrow.ArrowWidth = 2.5f;
                arrow.ArrowheadLength = 5;
                arrow.ArrowheadWidth = 4;
            }
        }
    }

    private void DrawHeadroom()
    {
        try
        {
            var plot = _headroomPlot.Plot;
            if (_headroomColorBar is not null)
            {
                plot.Remove(_headroomColorBar);
                _headroomColorBar = null;
            }

            plot.Clear();
            StylePlot(plot, "Qg inicial (L/min)", "N inicial (rpm)");
            if (ViewModel is not { } viewModel || viewModel.PathResult is not { } path)
            {
                var note = plot.Add.Text("A classificação aparece após Calcular trajetória", 0.5, 0.5);
                note.Alignment = Alignment.MiddleCenter;
                note.LabelFontColor = ToPlotColor(TryBrush("TextMutedBrush"), MediaColors.Gray);
                _headroomPlot.Refresh();
                return;
            }

            var resolution = path.HeadroomResolution;
            var scores = new double[resolution, resolution];
            var minScore = double.PositiveInfinity;
            var maxScore = double.NegativeInfinity;
            var hasFinite = false;
            for (var row = 0; row < resolution; row++)
            {
                for (var column = 0; column < resolution; column++)
                {
                    var score = path.HeadroomScores[(row * resolution) + column];
                    scores[row, column] = score;
                    if (double.IsFinite(score))
                    {
                        hasFinite = true;
                        if (score < minScore)
                        {
                            minScore = score;
                        }

                        if (score > maxScore)
                        {
                            maxScore = score;
                        }
                    }
                }
            }

            if (!hasFinite)
            {
                scores[0, 0] = 0;
            }

            var domain = viewModel.Surface!.Input.Domain;
            var algorithm = viewModel.Surface.Input.Algorithm;
            var qMinimum = domain.DenormalizeAirflow(algorithm.CandidateMinimum);
            var qMaximum = domain.DenormalizeAirflow(algorithm.CandidateMaximum);
            var nMinimum = domain.DenormalizeAgitation(algorithm.CandidateMinimum);
            var nMaximum = domain.DenormalizeAgitation(algorithm.CandidateMaximum);
            var heatmap = plot.Add.Heatmap(scores);
            heatmap.Rectangle = new CoordinateRect(qMinimum, qMaximum, nMinimum, nMaximum);
            heatmap.Colormap = new ScottPlot.Colormaps.Magma();
            if (double.IsFinite(minScore) && double.IsFinite(maxScore) && (maxScore - minScore) > 1e-6)
            {
                heatmap.ManualRange = new ScottPlot.Range(minScore, maxScore);
            }
            else if (double.IsFinite(maxScore) && maxScore > 0)
            {
                heatmap.ManualRange = new ScottPlot.Range(0, maxScore);
            }
            else
            {
                heatmap.ManualRange = new ScottPlot.Range(0, 0.5);
            }
            heatmap.FlipVertically = true;
            _headroomColorBar = plot.Add.ColorBar(heatmap);
            _headroomColorBar.Label = "H médio";

            var best = plot.Add.Scatter(
                new double[] { path.Diagnostics.SelectedStartAirflowLpm },
                new double[] { path.Diagnostics.SelectedStartAgitationRpm });
            best.LineWidth = 0;
            best.MarkerSize = 12;
            best.Color = ToPlotColor(TryBrush("AccentBrush"), MediaColors.DodgerBlue);
            
            if (qMaximum > qMinimum && nMaximum > nMinimum)
            {
                plot.Axes.SetLimits(qMinimum, qMaximum, nMinimum, nMaximum);
            }
            
            _headroomPlot.Refresh();
        }
        catch (Exception ex)
        {
            var plot = _headroomPlot.Plot;
            plot.Clear();
            var note = plot.Add.Text($"Erro ao renderizar folga:\n{ex.Message}", 0.5, 0.5);
            note.Alignment = Alignment.MiddleCenter;
            note.LabelFontColor = ToPlotColor(TryBrush("StateAlarmTextBrush"), MediaColors.Red);
            _headroomPlot.Refresh();
        }
    }

    private static void StylePlot(Plot plot, string xLabel, string yLabel)
    {
        var surface = ToPlotColor(TryBrush("SurfaceCardBrush"), MediaColors.White);
        var text = ToPlotColor(TryBrush("TextSecondaryBrush"), MediaColors.Gray);
        var grid = ToPlotColor(TryBrush("StrokeDefaultBrush"), MediaColors.LightGray);
        plot.FigureBackground.Color = surface;
        plot.DataBackground.Color = surface;
        plot.Axes.Color(text);
        plot.Grid.MajorLineColor = grid.WithAlpha(0.35);
        plot.Axes.Bottom.Label.Text = xLabel;
        plot.Axes.Left.Label.Text = yLabel;
        plot.Axes.Bottom.Label.FontSize = 11;
        plot.Axes.Left.Label.FontSize = 11;
    }

    private static SolidColorBrush? TryBrush(string key)
        => Application.Current?.TryFindResource(key) as SolidColorBrush;

    private static PlotColor ToPlotColor(SolidColorBrush? brush, MediaColor fallback)
    {
        var color = brush?.Color ?? fallback;
        return new PlotColor(color.R, color.G, color.B, color.A);
    }
}
