using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ScottPlot;
using ScottPlot.WPF;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Theme;
using OpenTECHub.ViewModels;

using MediaColor = System.Windows.Media.Color;
using MediaColors = System.Windows.Media.Colors;
using PlotColor = ScottPlot.Color;

namespace OpenTECHub.Views;

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
            SubscribeToThemeChanges();
            ApplyThemeToPlots();
            _redrawTimer.Start();
        };

        Unloaded += (_, _) =>
        {
            _redrawTimer.Stop();
            UnsubscribeFromThemeChanges();
        };
    }

    private void SubscribeToThemeChanges()
    {
        var theme = ((App)Application.Current).Services?.GetService<IThemeService>();
        if (theme != null)
        {
            theme.ThemeChanged -= OnThemeChanged;
            theme.ThemeChanged += OnThemeChanged;
        }
    }

    private void UnsubscribeFromThemeChanges()
    {
        var theme = ((App)Application.Current).Services?.GetService<IThemeService>();
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
                ApplyThemeToPlots();
                RedrawPlots();
            }
        });

    private void ApplyThemeToPlots()
    {
        StyleSinglePlot(_plotDo.Plot, "OD (%)", string.Empty);
        StyleSinglePlot(_plotLogLinear.Plot, "ln(Ceq - C)", string.Empty);
        StyleSinglePlot(_plotInstantKla.Plot, "kLa (h⁻¹)", "Tempo (s)");

        _plotDo.Refresh();
        _plotLogLinear.Refresh();
        _plotInstantKla.Refresh();
    }

    private static void StyleSinglePlot(Plot plot, string yLabel, string xLabel)
    {
        var surface = ToPlotColor(TryBrush("SurfaceCardBrush"), MediaColors.White);
        var text = ToPlotColor(TryBrush("TextSecondaryBrush"), MediaColors.Gray);
        var grid = ToPlotColor(TryBrush("StrokeDefaultBrush"), MediaColors.LightGray);

        plot.FigureBackground.Color = surface;
        plot.DataBackground.Color = surface;

        plot.Axes.Color(text);
        plot.Grid.MajorLineColor = grid.WithAlpha(0.45);

        // Fixed margin for pixel-perfect alignment with RangeSlider (45px left, 15px right)
        plot.Axes.Left.MinimumSize = 45;
        plot.Axes.Left.MaximumSize = 45;
        plot.Axes.Right.MinimumSize = 15;
        plot.Axes.Right.MaximumSize = 15;

        // Minimal vertical padding: remove top axis frame and title space
        plot.Axes.Title.Label.Text = string.Empty;
        plot.Axes.Title.Label.IsVisible = false;
        plot.Axes.Top.FrameLineStyle.Width = 0;
        plot.Axes.Top.TickLabelStyle.IsVisible = false;

        if (string.IsNullOrEmpty(xLabel))
        {
            plot.Axes.Bottom.Label.Text = string.Empty;
            plot.Axes.Bottom.Label.IsVisible = false;
            plot.Axes.Bottom.MinimumSize = 26;
            plot.Axes.Bottom.MaximumSize = 26;
        }
        else
        {
            plot.Axes.Bottom.Label.Text = xLabel;
            plot.Axes.Bottom.Label.IsVisible = true;
            plot.Axes.Bottom.Label.FontSize = 10.5f;
            plot.Axes.Bottom.MinimumSize = 44;
            plot.Axes.Bottom.MaximumSize = 44;
        }

        plot.Axes.Left.Label.Text = yLabel;
        plot.Axes.Left.Label.FontSize = 10;

        // Legends are rendered cleanly outside the plot canvas in WPF header bars
        plot.Legend.IsVisible = false;
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
            scatterRaw.LineWidth = 1;
            scatterRaw.MarkerSize = 0;

            var scatterFilt = plot.Add.Scatter(xs, ysFilt);
            scatterFilt.Color = PlotColor.FromHex("#2563EB");
            scatterFilt.LineWidth = 2;
            scatterFilt.MarkerSize = 0;
        }

        // Horizontal threshold lines
        var lineMin = plot.Add.HorizontalLine(vm.SettingDOMin);
        lineMin.Color = PlotColor.FromHex("#EF4444");
        lineMin.LinePattern = LinePattern.Dashed;

        var lineMax = plot.Add.HorizontalLine(vm.SettingDOMax);
        lineMax.Color = PlotColor.FromHex("#10B981");
        lineMax.LinePattern = LinePattern.Dashed;

        if (vm.IsReviewOpen)
        {
            // Linear region vertical lines (Amber)
            if (vm.ReviewTStart < vm.ReviewTEnd)
            {
                var vStart = plot.Add.VerticalLine(vm.ReviewTStart);
                vStart.Color = PlotColor.FromHex("#F59E0B");
                vStart.LinePattern = LinePattern.Dashed;

                var vEnd = plot.Add.VerticalLine(vm.ReviewTEnd);
                vEnd.Color = PlotColor.FromHex("#F59E0B");
                vEnd.LinePattern = LinePattern.Dashed;
            }

            // C* region vertical lines (Cyan)
            if (vm.ReviewCeqTStart < vm.ReviewCeqTEnd)
            {
                var vCeqStart = plot.Add.VerticalLine(vm.ReviewCeqTStart);
                vCeqStart.Color = PlotColor.FromHex("#06B6D4");
                vCeqStart.LinePattern = LinePattern.Dotted;

                var vCeqEnd = plot.Add.VerticalLine(vm.ReviewCeqTEnd);
                vCeqEnd.Color = PlotColor.FromHex("#06B6D4");
                vCeqEnd.LinePattern = LinePattern.Dotted;
            }

            // C* (Ceq) horizontal asymptote line
            if (vm.ReviewCeq > 0)
            {
                var hCeq = plot.Add.HorizontalLine(vm.ReviewCeq);
                hCeq.Color = PlotColor.FromHex("#10B981").WithAlpha(0.6);
                hCeq.LinePattern = LinePattern.Dashed;
            }

            // Pink dashed fitted exponential series: C(t) = Ceq - exp(beta0 + beta1 * t)
            if (vm.CurrentAnalysis is { } analysis && analysis.SlopeBeta1 < 0 && vm.ReviewTStart < vm.ReviewTEnd)
            {
                var expStart = vm.ReviewTStart;
                var maxTime = points.Count > 0 ? points.Last().RelativeSeconds : vm.ReviewTEnd;
                var expEnd = Math.Max(maxTime, vm.ReviewTEnd);
                const int steps = 80;
                var dt = (expEnd - expStart) / steps;
                if (dt > 0)
                {
                    var expXs = new double[steps + 1];
                    var expYs = new double[steps + 1];
                    for (int i = 0; i <= steps; i++)
                    {
                        var t = expStart + (i * dt);
                        var lnVal = analysis.InterceptBeta0 + (analysis.SlopeBeta1 * t);
                        var expDrivingForce = Math.Exp(lnVal);
                        var cVal = analysis.CeqPercent - expDrivingForce;
                        expXs[i] = t;
                        expYs[i] = Math.Min(cVal, analysis.CeqPercent);
                    }

                    var expScatter = plot.Add.Scatter(expXs, expYs);
                    expScatter.Color = PlotColor.FromHex("#EC4899");
                    expScatter.LineWidth = 2.2f;
                    expScatter.LinePattern = LinePattern.Dashed;
                    expScatter.MarkerSize = 0;
                }
            }
        }

        if (vm.IsReviewOpen && vm.ReviewMaxTime > vm.ReviewMinTime)
        {
            plot.Axes.SetLimitsX(vm.ReviewMinTime, vm.ReviewMaxTime);
            plot.Axes.AutoScaleY();
        }
        else
        {
            plot.Axes.AutoScale();
        }
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

            var regionPoints = series.Where(p => p.IsInAnalysisRegion).ToList();
            if (regionPoints.Count >= 2)
            {
                var xReg = new[] { regionPoints.First().RelativeSeconds, regionPoints.Last().RelativeSeconds };
                var yReg = new[] { regionPoints.First().FittedLnDrivingForce, regionPoints.Last().FittedLnDrivingForce };

                var lineFit = plot.Add.Scatter(xReg, yReg);
                lineFit.Color = PlotColor.FromHex("#DC2626");
                lineFit.LineWidth = 2.5f;
                lineFit.MarkerSize = 0;
            }
        }

        AddAnalysisRegionLines(plot, vm);

        if (vm.IsReviewOpen && vm.ReviewMaxTime > vm.ReviewMinTime)
        {
            plot.Axes.SetLimitsX(vm.ReviewMinTime, vm.ReviewMaxTime);
            plot.Axes.AutoScaleY();
        }
        else
        {
            plot.Axes.AutoScale();
        }
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
            }
        }

        if (vm.ReviewKla > 0)
        {
            var hLine = plot.Add.HorizontalLine(vm.ReviewKla);
            hLine.Color = PlotColor.FromHex("#DC2626");
            hLine.LinePattern = LinePattern.Dashed;
        }

        AddAnalysisRegionLines(plot, vm);

        if (vm.IsReviewOpen && vm.ReviewMaxTime > vm.ReviewMinTime)
        {
            plot.Axes.SetLimitsX(vm.ReviewMinTime, vm.ReviewMaxTime);
            plot.Axes.AutoScaleY();
        }
        else
        {
            plot.Axes.AutoScale();
        }
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
        start.LinePattern = LinePattern.Dashed;

        var end = plot.Add.VerticalLine(vm.ReviewTEnd);
        end.Color = PlotColor.FromHex("#F59E0B");
        end.LinePattern = LinePattern.Dashed;
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
