using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ScottPlot;
using ScottPlot.WPF;
using TecnalHub.Services.KlaTesting;
using TecnalHub.ViewModels;

using MediaColor = System.Windows.Media.Color;
using MediaColors = System.Windows.Media.Colors;
using PlotColor = ScottPlot.Color;

namespace TecnalHub.Views;

public partial class KlaDeterminationView : UserControl
{
    private readonly WpfPlot _plotDo = new();
    private readonly WpfPlot _plotLogLinear = new();
    private readonly WpfPlot _plotInstantKla = new();
    private readonly DispatcherTimer _redrawTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private KlaDeterminationViewModel? ViewModel => DataContext as KlaDeterminationViewModel;

    public KlaDeterminationView()
    {
        InitializeComponent();

        ChartDoHost.Child = _plotDo;
        ChartLogLinearHost.Child = _plotLogLinear;
        ChartInstantKlaHost.Child = _plotInstantKla;

        _redrawTimer.Tick += (_, _) => RedrawPlots();

        Loaded += (_, _) =>
        {
            ApplyThemeToPlots();
            _redrawTimer.Start();
        };

        Unloaded += (_, _) =>
        {
            _redrawTimer.Stop();
        };
    }

    private void ApplyThemeToPlots()
    {
        StyleSinglePlot(_plotDo.Plot, "Oxigênio Dissolvido (OD)", "OD (%)", "Tempo (s)");
        StyleSinglePlot(_plotLogLinear.Plot, "Regressão Log-Linear: ln(Ceq - C)", "ln(Ceq - C)", "Tempo (s)");
        StyleSinglePlot(_plotInstantKla.Plot, "kLa Instantâneo Diagnóstico", "kLa (h⁻¹)", "Tempo (s)");

        _plotDo.Refresh();
        _plotLogLinear.Refresh();
        _plotInstantKla.Refresh();
    }

    private static void StyleSinglePlot(Plot plot, string title, string yLabel, string xLabel)
    {
        var surface = ToPlotColor(TryBrush("SurfaceCardBrush"), MediaColors.White);
        var text = ToPlotColor(TryBrush("TextSecondaryBrush"), MediaColors.Gray);
        var grid = ToPlotColor(TryBrush("StrokeDefaultBrush"), MediaColors.LightGray);

        plot.FigureBackground.Color = surface;
        plot.DataBackground.Color = surface;

        plot.Axes.Color(text);
        plot.Grid.MajorLineColor = grid.WithAlpha(0.45);

        plot.Axes.Bottom.Label.Text = xLabel;
        plot.Axes.Left.Label.Text = yLabel;
        plot.Axes.Title.Label.Text = title;
        plot.Axes.Title.Label.ForeColor = ToPlotColor(TryBrush("TextPrimaryBrush"), MediaColors.Black);
        plot.Axes.Title.Label.FontSize = 13;

        plot.Axes.Bottom.Label.FontSize = 11;
        plot.Axes.Left.Label.FontSize = 11;
    }

    private void RedrawPlots()
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        RedrawDoPlot(vm);
        RedrawLogLinearPlot(vm);
        RedrawInstantKlaPlot(vm);
    }

    private void RedrawDoPlot(KlaDeterminationViewModel vm)
    {
        var plot = _plotDo.Plot;
        plot.Clear();

        var points = vm.LivePoints.ToList();
        if (points.Count > 0)
        {
            var xs = points.Select(p => p.RelativeSeconds).ToArray();
            var ysRaw = points.Select(p => p.DORaw).ToArray();
            var ysFilt = points.Select(p => p.DOFiltered).ToArray();

            var scatterRaw = plot.Add.Scatter(xs, ysRaw);
            scatterRaw.Color = PlotColor.FromHex("#3B82F6").WithAlpha(0.4);
            scatterRaw.LegendText = "OD Bruto";
            scatterRaw.LineWidth = 1;
            scatterRaw.MarkerSize = 0;

            var scatterFilt = plot.Add.Scatter(xs, ysFilt);
            scatterFilt.Color = PlotColor.FromHex("#2563EB");
            scatterFilt.LegendText = "OD Filtrado";
            scatterFilt.LineWidth = 2;
            scatterFilt.MarkerSize = 0;
        }

        // Horizontal threshold lines
        var lineMin = plot.Add.HorizontalLine(vm.SettingDOMin);
        lineMin.Color = PlotColor.FromHex("#EF4444");
        lineMin.LinePattern = LinePattern.Dashed;
        lineMin.LegendText = "DO Mín";

        var lineMax = plot.Add.HorizontalLine(vm.SettingDOMax);
        lineMax.Color = PlotColor.FromHex("#10B981");
        lineMax.LinePattern = LinePattern.Dashed;
        lineMax.LegendText = "DO Máx";

        if (vm.IsReviewOpen && vm.ReviewTStart < vm.ReviewTEnd)
        {
            var vStart = plot.Add.VerticalLine(vm.ReviewTStart);
            vStart.Color = PlotColor.FromHex("#F59E0B");
            vStart.LinePattern = LinePattern.Dotted;

            var vEnd = plot.Add.VerticalLine(vm.ReviewTEnd);
            vEnd.Color = PlotColor.FromHex("#F59E0B");
            vEnd.LinePattern = LinePattern.Dotted;
        }

        plot.Axes.AutoScale();
        _plotDo.Refresh();
    }

    private void RedrawLogLinearPlot(KlaDeterminationViewModel vm)
    {
        var plot = _plotLogLinear.Plot;
        plot.Clear();

        var series = vm.LogLinearSeries.ToList();
        if (series.Count > 0)
        {
            var xs = series.Select(p => p.RelativeSeconds).ToArray();
            var ys = series.Select(p => p.LnDrivingForce).ToArray();

            var scatter = plot.Add.Scatter(xs, ys);
            scatter.Color = PlotColor.FromHex("#6366F1");
            scatter.LineWidth = 0;
            scatter.MarkerSize = 4;
            scatter.LegendText = "ln(Ceq - C)";

            var regionPoints = series.Where(p => p.IsInAnalysisRegion).ToList();
            if (regionPoints.Count >= 2)
            {
                var xReg = new[] { regionPoints.First().RelativeSeconds, regionPoints.Last().RelativeSeconds };
                var yReg = new[] { regionPoints.First().FittedLnDrivingForce, regionPoints.Last().FittedLnDrivingForce };

                var lineFit = plot.Add.Scatter(xReg, yReg);
                lineFit.Color = PlotColor.FromHex("#DC2626");
                lineFit.LineWidth = 2.5f;
                lineFit.MarkerSize = 0;
                lineFit.LegendText = $"Ajuste OLS (kLa={vm.ReviewKla:F1} h⁻¹)";
            }
        }

        AddAnalysisRegionLines(plot, vm);

        plot.Axes.AutoScale();
        _plotLogLinear.Refresh();
    }

    private void RedrawInstantKlaPlot(KlaDeterminationViewModel vm)
    {
        var plot = _plotInstantKla.Plot;
        plot.Clear();

        var series = vm.InstantaneousKlaSeries.ToList();
        if (series.Count > 0)
        {
            var validPoints = series.Where(p => p.KlaFiltered.HasValue).ToList();
            if (validPoints.Count > 0)
            {
                var xs = validPoints.Select(p => p.RelativeSeconds).ToArray();
                var ys = validPoints.Select(p => p.KlaFiltered!.Value).ToArray();

                var scatter = plot.Add.Scatter(xs, ys);
                scatter.Color = PlotColor.FromHex("#059669");
                scatter.LineWidth = 2;
                scatter.MarkerSize = 0;
                scatter.LegendText = "kLa Inst";
            }
        }

        if (vm.ReviewKla > 0)
        {
            var hLine = plot.Add.HorizontalLine(vm.ReviewKla);
            hLine.Color = PlotColor.FromHex("#DC2626");
            hLine.LinePattern = LinePattern.Dashed;
            hLine.LegendText = "kLa Final";
        }

        AddAnalysisRegionLines(plot, vm);

        plot.Axes.AutoScale();
        _plotInstantKla.Refresh();
    }

    private static void AddAnalysisRegionLines(Plot plot, KlaDeterminationViewModel vm)
    {
        if (!vm.IsReviewOpen || vm.ReviewTStart >= vm.ReviewTEnd)
        {
            return;
        }

        var start = plot.Add.VerticalLine(vm.ReviewTStart);
        start.Color = PlotColor.FromHex("#F59E0B");
        start.LinePattern = LinePattern.Dotted;
        var end = plot.Add.VerticalLine(vm.ReviewTEnd);
        end.Color = PlotColor.FromHex("#F59E0B");
        end.LinePattern = LinePattern.Dotted;
    }

    private static System.Windows.Media.Brush? TryBrush(string key) =>
        Application.Current?.TryFindResource(key) as System.Windows.Media.Brush;

    private static PlotColor ToPlotColor(System.Windows.Media.Brush? brush, MediaColor fallback)
    {
        if (brush is System.Windows.Media.SolidColorBrush scb)
        {
            return new PlotColor(scb.Color.R, scb.Color.G, scb.Color.B, scb.Color.A);
        }

        return new PlotColor(fallback.R, fallback.G, fallback.B, fallback.A);
    }
}
