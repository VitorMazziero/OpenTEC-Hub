using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ScottPlot;
using ScottPlot.Plottables;
using ScottPlot.WPF;
using OpenTECHub.Controls;
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

    /// <summary>
    /// Charts redraw only while the page is on screen and only when their data moved (§C). The DO
    /// chart is incremental: raw and filtered DO live in two <see cref="DataLogger"/>s fed as the
    /// ViewModel adds points; only the review overlays (thresholds, windows, C*, fitted curve) are
    /// rebuilt on a redraw.
    /// </summary>
    private readonly VisibleRedrawTimer _redraw;
    private DataLogger? _doRaw;
    private DataLogger? _doFiltered;
    private readonly List<IPlottable> _doOverlays = [];
    private bool _doNeedsRebuild = true;
    private bool _doDirty = true;
    private bool _derivedDirty = true;
    private KlaDeterminationViewModel? _observed;

    private KlaDeterminationViewModel? ViewModel => DataContext as KlaDeterminationViewModel;

    public KlaDeterminationView()
    {
        InitializeComponent();

        ChartDoHost.Child = _plotDo;
        ChartLogLinearHost.Child = _plotLogLinear;
        ChartInstantKlaHost.Child = _plotInstantKla;

        _redraw = new VisibleRedrawTimer(this, TimeSpan.FromSeconds(1), RedrawPlots);
        DataContextChanged += OnDataContextChanged;

        Loaded += (_, _) =>
        {
            SubscribeToThemeChanges();
            ApplyThemeToPlots();
            _redraw.Invalidate();
        };

        Unloaded += (_, _) =>
        {
            UnsubscribeFromThemeChanges();
        };
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_observed is not null)
        {
            _observed.PropertyChanged -= OnViewModelPropertyChanged;
            _observed.LivePoints.CollectionChanged -= OnLivePointsChanged;
            _observed.LogLinearSeries.CollectionChanged -= OnDerivedSeriesChanged;
            _observed.InstantaneousKlaSeries.CollectionChanged -= OnDerivedSeriesChanged;
        }

        _observed = ViewModel;
        if (_observed is not null)
        {
            _observed.PropertyChanged += OnViewModelPropertyChanged;
            _observed.LivePoints.CollectionChanged += OnLivePointsChanged;
            _observed.LogLinearSeries.CollectionChanged += OnDerivedSeriesChanged;
            _observed.InstantaneousKlaSeries.CollectionChanged += OnDerivedSeriesChanged;
        }

        _doNeedsRebuild = true;
        _doDirty = true;
        _derivedDirty = true;
        _redraw.MarkDirty();
    }

    /// <summary>
    /// The three charts read the review window, the thresholds and the current analysis; any of
    /// those moving (operator dragging the window, a recompute) means a redraw. Naming them by
    /// prefix is deliberate: a new <c>Review*</c>/<c>Setting*</c> property is covered without
    /// remembering to list it here.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var name = e.PropertyName ?? "";
        if (name.StartsWith("Review", StringComparison.Ordinal) ||
            name.StartsWith("Setting", StringComparison.Ordinal) ||
            name is nameof(KlaDeterminationViewModel.IsReviewOpen) or nameof(KlaDeterminationViewModel.CurrentAnalysis))
        {
            _doDirty = true;
            _derivedDirty = true;
            _redraw.MarkDirty();
        }
    }

    private void OnLivePointsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null && !_doNeedsRebuild && _doRaw is not null && _doFiltered is not null)
        {
            foreach (KlaRawDataPoint point in e.NewItems)
            {
                _doRaw.Add(point.RelativeSeconds, point.DORaw);
                _doFiltered.Add(point.RelativeSeconds, point.DOFiltered);
            }
        }
        else if (e.Action == NotifyCollectionChangedAction.Reset && _doRaw is not null && _doFiltered is not null)
        {
            _doRaw.Clear();
            _doFiltered.Clear();
            _doNeedsRebuild = false;
        }
        else
        {
            _doNeedsRebuild = true;
        }

        _doDirty = true;
        _redraw.MarkDirty();
    }

    private void OnDerivedSeriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _derivedDirty = true;
        _redraw.MarkDirty();
    }

    private void SubscribeToThemeChanges()
    {
        var theme = (Application.Current as App)?.Services?.GetService<IThemeService>();
        if (theme != null)
        {
            theme.ThemeChanged -= OnThemeChanged;
            theme.ThemeChanged += OnThemeChanged;
        }
    }

    private void UnsubscribeFromThemeChanges()
    {
        var theme = (Application.Current as App)?.Services?.GetService<IThemeService>();
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
                _doNeedsRebuild = true;
                _doDirty = true;
                _derivedDirty = true;
                _redraw.Invalidate();
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
        // Axis text is content, not chrome; the secondary token reads as dim on a dark card.
        var text = ToPlotColor(TryBrush("TextPrimaryBrush"), MediaColors.Black);
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

        if (_doDirty)
        {
            _doDirty = false;
            RedrawDoPlot(vm);
        }

        if (_derivedDirty)
        {
            _derivedDirty = false;
            RedrawLogLinearPlot(vm);
            RedrawInstantKlaPlot(vm);
        }
    }

    private void RedrawDoPlot(KlaDeterminationViewModel vm)
    {
        var plot = _plotDo.Plot;

        if (_doNeedsRebuild || _doRaw is null || _doFiltered is null)
        {
            plot.Clear();
            _doOverlays.Clear();
            _doRaw = plot.Add.DataLogger();
            _doRaw.Color = PlotColor.FromHex("#3B82F6").WithAlpha(0.4);
            _doRaw.LineWidth = 1;
            _doRaw.MarkerSize = 0;
            _doRaw.ManageAxisLimits = false;
            _doFiltered = plot.Add.DataLogger();
            _doFiltered.Color = PlotColor.FromHex("#2563EB");
            _doFiltered.LineWidth = 2;
            _doFiltered.MarkerSize = 0;
            _doFiltered.ManageAxisLimits = false;
            foreach (var point in vm.LivePoints)
            {
                _doRaw.Add(point.RelativeSeconds, point.DORaw);
                _doFiltered.Add(point.RelativeSeconds, point.DOFiltered);
            }
            _doNeedsRebuild = false;
        }

        // The loggers stay; only the overlays are rebuilt.
        foreach (var overlay in _doOverlays)
        {
            plot.Remove(overlay);
        }
        _doOverlays.Clear();
        var points = vm.LivePoints;

        // Horizontal threshold lines
        var lineMin = plot.Add.HorizontalLine(vm.SettingDOMin);
        lineMin.Color = PlotColor.FromHex("#EF4444");
        lineMin.LinePattern = LinePattern.Dashed;
        _doOverlays.Add(lineMin);

        var lineMax = plot.Add.HorizontalLine(vm.SettingDOMax);
        lineMax.Color = PlotColor.FromHex("#10B981");
        lineMax.LinePattern = LinePattern.Dashed;
        _doOverlays.Add(lineMax);

        if (vm.IsReviewOpen)
        {
            // Linear region vertical lines (Amber)
            if (vm.ReviewTStart < vm.ReviewTEnd)
            {
                var vStart = plot.Add.VerticalLine(vm.ReviewTStart);
                vStart.Color = PlotColor.FromHex("#F59E0B");
                vStart.LinePattern = LinePattern.Dashed;
                _doOverlays.Add(vStart);

                var vEnd = plot.Add.VerticalLine(vm.ReviewTEnd);
                vEnd.Color = PlotColor.FromHex("#F59E0B");
                vEnd.LinePattern = LinePattern.Dashed;
                _doOverlays.Add(vEnd);
            }

            // C* region vertical lines (Cyan)
            if (vm.ReviewCeqTStart < vm.ReviewCeqTEnd)
            {
                var vCeqStart = plot.Add.VerticalLine(vm.ReviewCeqTStart);
                vCeqStart.Color = PlotColor.FromHex("#06B6D4");
                vCeqStart.LinePattern = LinePattern.Dotted;
                _doOverlays.Add(vCeqStart);

                var vCeqEnd = plot.Add.VerticalLine(vm.ReviewCeqTEnd);
                vCeqEnd.Color = PlotColor.FromHex("#06B6D4");
                vCeqEnd.LinePattern = LinePattern.Dotted;
                _doOverlays.Add(vCeqEnd);
            }

            // C* (Ceq) horizontal asymptote line
            if (vm.ReviewCeq > 0)
            {
                var hCeq = plot.Add.HorizontalLine(vm.ReviewCeq);
                hCeq.Color = PlotColor.FromHex("#10B981").WithAlpha(0.6);
                hCeq.LinePattern = LinePattern.Dashed;
                _doOverlays.Add(hCeq);
            }

            // Pink dashed fitted exponential series: C(t) = Ceq - exp(beta0 + beta1 * t)
            if (vm.CurrentAnalysis is { } analysis && analysis.SlopeBeta1 < 0 && vm.ReviewTStart < vm.ReviewTEnd)
            {
                var expStart = vm.ReviewTStart;
                var maxTime = points.Count > 0 ? points[^1].RelativeSeconds : vm.ReviewTEnd;
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
                    _doOverlays.Add(expScatter);
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
