using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using OpenTECHub.Services.PowerMapping;
using OpenTECHub.ViewModels;
using ScottPlot;
using ScottPlot.WPF;

using MediaColor = System.Windows.Media.Color;
using MediaColors = System.Windows.Media.Colors;
using PlotColor = ScottPlot.Color;

namespace OpenTECHub.Views;

/// <summary>
/// Theme-aware scientific plots for the phase 3 power synthesis map: the layered 2D surface over
/// (N, Qg) with its flooding frontier, and the kLa &lt;-&gt; P/V validation pair.
/// </summary>
public partial class PowerMapView : UserControl
{
    private const int IsolineCount = 9;

    private readonly WpfPlot _surfacePlot = new();
    private readonly WpfPlot _parityPlot = new();
    private readonly WpfPlot _klaPvPlot = new();
    private ScottPlot.Panels.ColorBar? _surfaceColorBar;
    private PowerMapViewModel? _subscribed;

    public static readonly DependencyProperty ShowHeaderProperty =
        DependencyProperty.Register(
            nameof(ShowHeader),
            typeof(bool),
            typeof(PowerMapView),
            new PropertyMetadata(true));

    public bool ShowHeader
    {
        get => (bool)GetValue(ShowHeaderProperty);
        set => SetValue(ShowHeaderProperty, value);
    }

    public PowerMapView()
    {
        InitializeComponent();
        SurfacePlotHost.Child = _surfacePlot;
        ParityPlotHost.Child = _parityPlot;
        KlaPvPlotHost.Child = _klaPvPlot;

        _surfacePlot.MouseMove += OnSurfaceMouseMove;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
        DataContextChanged += (_, _) => Attach();
    }

    private PowerMapViewModel? ViewModel => DataContext as PowerMapViewModel;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SubscribeToThemeChanges();
        Attach();
        if (ViewModel is { } viewModel)
        {
            // Re-reads the workspace on every visit; the first call initializes.
            await viewModel.RefreshOnEnterCommand.ExecuteAsync(null);
        }

        Redraw();
    }

    private async void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible && ViewModel is { } viewModel)
        {
            await viewModel.RefreshOnEnterCommand.ExecuteAsync(null);
            Redraw();
        }
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

    /// <summary>Live coordinate read-out under the cursor (§18.3 step 4.4).</summary>
    private void OnSurfaceMouseMove(object sender, MouseEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var position = e.GetPosition(_surfacePlot);
        var pixel = new Pixel(
            (float)(position.X * _surfacePlot.DisplayScale),
            (float)(position.Y * _surfacePlot.DisplayScale));
        var coordinates = _surfacePlot.Plot.GetCoordinates(pixel);

        if (!double.IsFinite(coordinates.X) || !double.IsFinite(coordinates.Y))
        {
            return;
        }

        // X is the gas flow and Y the agitation, matching the kLa map's convention.
        viewModel.UpdateCursorInspection(coordinates.Y, coordinates.X);
    }

    private void Redraw()
    {
        DrawSurface();
        DrawParity();
        DrawKlaVersusSpecificPower();
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
        StylePlot(plot, "Vazão de gás Qg (L/min)", "Agitação N (rpm)");

        if (ViewModel is not { } viewModel)
        {
            _surfacePlot.Refresh();
            return;
        }

        var surface = viewModel.CurrentSurfaceData;
        if (surface is null)
        {
            AddCentredNote(plot, "Selecione os ensaios de origem e reconstrua a superfície");
            _surfacePlot.Refresh();
            return;
        }

        var qMinimum = surface.MinFlowLpm;
        var qMaximum = surface.MaxFlowLpm;
        var nMinimum = surface.MinRpm;
        var nMaximum = surface.MaxRpm;

        if (viewModel.TryBuildLayerField(out var field, out var dataMin, out var dataMax))
        {
            var (displayMin, displayMax) = viewModel.ResolveDisplayRange(dataMin, dataMax);

            var heatmap = plot.Add.Heatmap(field);
            heatmap.Rectangle = new CoordinateRect(qMinimum, qMaximum, nMinimum, nMaximum);
            heatmap.Colormap = ResolveColormap(viewModel.SelectedColormap);
            // Row 0 is the minimum agitation and belongs at the bottom of the axis; ScottPlot
            // draws row 0 at the top by default (see HeatmapOrientationTests).
            heatmap.FlipVertically = true;
            if (displayMax > displayMin)
            {
                heatmap.ManualRange = new ScottPlot.Range(displayMin, displayMax);
            }

            _surfaceColorBar = plot.Add.ColorBar(heatmap);
            _surfaceColorBar.Label = viewModel.ColorBarLabel;
            StyleColorBar(_surfaceColorBar);

            if (viewModel.ShowIsolines)
            {
                DrawIsolines(plot, field, surface, displayMin, displayMax);
            }
        }
        else
        {
            AddCentredNote(plot, viewModel.DescribeUndrawableLayer());
        }

        if (viewModel.ShowAnchors && surface.AnchorPoints.Count > 0)
        {
            DrawAnchors(plot, surface.AnchorPoints);
        }

        DrawFloodingBoundaries(plot, viewModel, nMinimum, nMaximum, qMinimum, qMaximum);

        if (qMaximum > qMinimum && nMaximum > nMinimum)
        {
            plot.Axes.SetLimits(qMinimum, qMaximum, nMinimum, nMaximum);
        }

        _surfacePlot.Refresh();
    }

    private static void DrawIsolines(
        Plot plot,
        double[,] field,
        PowerMapSurfaceData surface,
        double displayMin,
        double displayMax)
    {
        var levels = PowerMapContours.Extract(
            field,
            surface.FlowGrid,
            surface.RpmGrid,
            IsolineCount,
            displayMin,
            displayMax);

        if (levels.Count == 0)
        {
            return;
        }

        var color = ToPlotColor(TryBrush("TextPrimaryBrush"), MediaColors.Black).WithAlpha(0.38);
        foreach (var level in levels)
        {
            foreach (var segment in level.Segments)
            {
                var line = plot.Add.Line(segment.X1, segment.Y1, segment.X2, segment.Y2);
                line.LineColor = color;
                line.LineWidth = 0.9f;
                line.MarkerStyle.IsVisible = false;
            }
        }
    }

    /// <summary>
    /// Experimental points, split by regime so the operator can see at a glance which conditions
    /// were already flooded when they were measured.
    /// </summary>
    private static void DrawAnchors(Plot plot, IReadOnlyList<PowerMapAnchorPoint> anchors)
    {
        var dispersed = anchors.Where(a => !a.IsFlooded).ToArray();
        var flooded = anchors.Where(a => a.IsFlooded).ToArray();

        if (dispersed.Length > 0)
        {
            var points = plot.Add.ScatterPoints(
                dispersed.Select(a => a.GasFlowLpm).ToArray(),
                dispersed.Select(a => a.AgitationRpm).ToArray());
            points.MarkerSize = 8;
            points.MarkerShape = MarkerShape.FilledCircle;
            points.Color = ToPlotColor(TryBrush("TextPrimaryBrush"), MediaColors.Black);
            points.LegendText = "Condições medidas";
        }

        if (flooded.Length > 0)
        {
            var points = plot.Add.ScatterPoints(
                flooded.Select(a => a.GasFlowLpm).ToArray(),
                flooded.Select(a => a.AgitationRpm).ToArray());
            points.MarkerSize = 10;
            points.MarkerShape = MarkerShape.OpenTriangleUp;
            points.Color = ToPlotColor(TryBrush("StateAlarmTextBrush"), MediaColors.Red);
            points.LegendText = "Medidas em afogamento";
        }
    }

    private static void DrawFloodingBoundaries(
        Plot plot,
        PowerMapViewModel viewModel,
        double nMinimum,
        double nMaximum,
        double qMinimum,
        double qMaximum)
    {
        if (viewModel.CurrentFloodingBoundary is not { } boundary)
        {
            return;
        }

        if (viewModel.ShowNienowBoundary && boundary.NienowTheoreticalPoints.Count > 1)
        {
            // Only the stretch that actually crosses the plotted window is worth drawing.
            var visible = boundary.NienowTheoreticalPoints
                .Where(p => p.AgitationRpm >= nMinimum && p.AgitationRpm <= nMaximum &&
                            p.GasFlowLpm >= qMinimum && p.GasFlowLpm <= qMaximum)
                .OrderBy(p => p.AgitationRpm)
                .ToArray();

            if (visible.Length > 1)
            {
                var line = plot.Add.Scatter(
                    visible.Select(p => p.GasFlowLpm).ToArray(),
                    visible.Select(p => p.AgitationRpm).ToArray());
                line.MarkerSize = 0;
                line.LineWidth = 2.6f;
                line.LinePattern = LinePattern.Dashed;
                line.Color = ToPlotColor(TryBrush("StateWarningTextBrush"), MediaColors.Goldenrod);
                line.LegendText = "Flooding — Nienow (teórica)";
            }
        }

        if (viewModel.ShowExperimentalFlooding && boundary.ExperimentalPoints.Count > 1)
        {
            var experimental = boundary.ExperimentalPoints.OrderBy(p => p.AgitationRpm).ToArray();
            var line = plot.Add.Scatter(
                experimental.Select(p => p.GasFlowLpm).ToArray(),
                experimental.Select(p => p.AgitationRpm).ToArray());
            line.MarkerSize = 7;
            line.LineWidth = 2.6f;
            line.Color = ToPlotColor(TryBrush("StateAlarmTextBrush"), MediaColors.Red);
            line.LegendText = "Flooding — experimental";
        }

        plot.ShowLegend(Alignment.UpperLeft);
        StyleLegend(plot);
    }

    /// <summary>Parity of measured against predicted kLa, with the ±15% acceptance band (§18.3 step 5.3).</summary>
    private void DrawParity()
    {
        var plot = _parityPlot.Plot;
        plot.Clear();
        StylePlot(plot, "kLa medido (h⁻¹)", "kLa previsto (h⁻¹)");

        var pairs = ViewModel?.MatchedPairs
            .Where(p => p.KlaPerHour > 0 && p.PredictedKlaPerHour is > 0)
            .ToArray() ?? [];

        if (pairs.Length == 0)
        {
            AddCentredNote(plot, "Calcule a intersecção e ajuste o modelo");
            _parityPlot.Refresh();
            return;
        }

        var measured = pairs.Select(p => p.KlaPerHour).ToArray();
        var predicted = pairs.Select(p => p.PredictedKlaPerHour!.Value).ToArray();

        var low = Math.Min(measured.Min(), predicted.Min());
        var high = Math.Max(measured.Max(), predicted.Max());
        var margin = Math.Max(1e-6, (high - low) * 0.08);
        low = Math.Max(0.0, low - margin);
        high += margin;

        var identityColor = ToPlotColor(TryBrush("TextSecondaryBrush"), MediaColors.Gray);
        var identity = plot.Add.Line(low, low, high, high);
        identity.LineWidth = 1.6f;
        identity.LineColor = identityColor;
        identity.MarkerStyle.IsVisible = false;

        foreach (var factor in new[] { 1.15, 0.85 })
        {
            var band = plot.Add.Line(low, low * factor, high, high * factor);
            band.LineWidth = 1.1f;
            band.LinePattern = LinePattern.Dashed;
            band.LineColor = identityColor.WithAlpha(0.55);
            band.MarkerStyle.IsVisible = false;
        }

        var inside = plot.Add.ScatterPoints(
            measured.Where((_, i) => WithinBand(measured[i], predicted[i])).ToArray(),
            predicted.Where((_, i) => WithinBand(measured[i], predicted[i])).ToArray());
        inside.MarkerSize = 8;
        inside.Color = ToPlotColor(TryBrush("AccentBrush"), MediaColors.DodgerBlue);

        var outside = plot.Add.ScatterPoints(
            measured.Where((_, i) => !WithinBand(measured[i], predicted[i])).ToArray(),
            predicted.Where((_, i) => !WithinBand(measured[i], predicted[i])).ToArray());
        outside.MarkerSize = 9;
        outside.MarkerShape = MarkerShape.OpenCircle;
        outside.Color = ToPlotColor(TryBrush("StateAlarmTextBrush"), MediaColors.Red);

        plot.Axes.SetLimits(low, high, low, high);
        _parityPlot.Refresh();
    }

    private static bool WithinBand(double measured, double predicted)
        => measured > 0 && Math.Abs(predicted - measured) / measured <= 0.15;

    /// <summary>kLa against specific power, one series per gas flow (§18.3 step 5.3).</summary>
    private void DrawKlaVersusSpecificPower()
    {
        var plot = _klaPvPlot.Plot;
        plot.Clear();
        StylePlot(plot, "P/V (W/m³)", "kLa (h⁻¹)");

        var pairs = ViewModel?.MatchedPairs
            .Where(p => p.KlaPerHour > 0 && p.VolumetricPowerWm3 > 0)
            .ToArray() ?? [];

        var intersectionCells = ViewModel?.CurrentSurfaceIntersection?.EnumerateValidCells()
            .Where(c => c.KlaPerHour > 0 && c.VolumetricPowerWm3 > 0)
            .ToArray() ?? [];

        if (pairs.Length == 0 && intersectionCells.Length == 0)
        {
            AddCentredNote(plot, "Sem pontos válidos na intersecção das superfícies");
            _klaPvPlot.Refresh();
            return;
        }

        var groups = pairs.Length > 0
            ? pairs
                .GroupBy(p => Math.Round(p.GasFlowLpm, 2))
                .Select(g => new
                {
                    Key = g.Key,
                    Points = g.Select(p => (p.VolumetricPowerWm3, p.KlaPerHour, p.SuperficialVelocityMs)).ToArray(),
                })
                .OrderBy(g => g.Key)
                .ToArray()
            : intersectionCells
                .GroupBy(c => Math.Round(c.GasFlowLpm, 2))
                .Select(g => new
                {
                    Key = g.Key,
                    Points = g.Select(c => (c.VolumetricPowerWm3, c.KlaPerHour, c.SuperficialVelocityMs)).ToArray(),
                })
                .OrderBy(g => g.Key)
                .ToArray();

        var palette = new ScottPlot.Palettes.Category10();
        for (var index = 0; index < groups.Length; index++)
        {
            var group = groups[index].Points.OrderBy(p => p.VolumetricPowerWm3).ToArray();
            var series = plot.Add.Scatter(
                group.Select(p => p.VolumetricPowerWm3).ToArray(),
                group.Select(p => p.KlaPerHour).ToArray());
            series.MarkerSize = 7;
            series.LineWidth = group.Length > 1 ? 1.4f : 0f;
            series.Color = palette.GetColor(index);
            series.LegendText = $"Qg = {groups[index].Key:0.##} L/min (v_s {group[0].SuperficialVelocityMs:0.0000} m/s)";
        }

        plot.ShowLegend(Alignment.LowerRight);
        StyleLegend(plot);
        plot.Axes.AutoScale();
        _klaPvPlot.Refresh();
    }

    private static IColormap ResolveColormap(PowerMapColormap colormap) => colormap switch
    {
        PowerMapColormap.Magma => new ScottPlot.Colormaps.Magma(),
        PowerMapColormap.Turbo => new ScottPlot.Colormaps.Turbo(),
        _ => new ScottPlot.Colormaps.Viridis(),
    };

    private static void AddCentredNote(Plot plot, string text)
    {
        var note = plot.Add.Text(text, 0.5, 0.5);
        note.Alignment = Alignment.MiddleCenter;
        note.LabelFontColor = ToPlotColor(TryBrush("TextMutedBrush"), MediaColors.Gray);
        plot.Axes.SetLimits(0, 1, 0, 1);
    }

    /// <summary>
    /// Paints the colour bar's label and ticks with the theme's text colour.
    /// </summary>
    /// <remarks>
    /// A colour bar is a panel, not an axis, so <c>Plot.Axes.Color</c> never reaches it.
    /// </remarks>
    private static void StyleColorBar(ScottPlot.Panels.ColorBar bar)
    {
        var text = ToPlotColor(TryBrush("TextPrimaryBrush"), MediaColors.Black);
        bar.LabelStyle.ForeColor = text;
        bar.Axis.TickLabelStyle.ForeColor = text;
        bar.Axis.MajorTickStyle.Color = text;
        bar.Axis.MinorTickStyle.Color = text;
        bar.Axis.FrameLineStyle.Color = text;
    }

    private static void StyleLegend(Plot plot)
    {
        var text = ToPlotColor(TryBrush("TextPrimaryBrush"), MediaColors.Black);
        var surface = ToPlotColor(TryBrush("SurfaceCardBrush"), MediaColors.White);
        var stroke = ToPlotColor(TryBrush("StrokeDefaultBrush"), MediaColors.LightGray);

        plot.Legend.BackgroundColor = surface.WithAlpha(0.88);
        plot.Legend.FontColor = text;
        plot.Legend.OutlineColor = stroke;
        plot.Legend.FontSize = 10;
    }

    private static void StylePlot(Plot plot, string xLabel, string yLabel)
    {
        var surface = ToPlotColor(TryBrush("SurfaceCardBrush"), MediaColors.White);
        var text = ToPlotColor(TryBrush("TextPrimaryBrush"), MediaColors.Black);
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
