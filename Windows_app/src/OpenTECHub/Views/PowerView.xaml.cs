using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using OpenTECHub.Controls;
using OpenTECHub.Services.PowerTesting;
using OpenTECHub.Services.Theme;
using OpenTECHub.ViewModels;
using ScottPlot;
using ScottPlot.Plottables;
using ScottPlot.WPF;

using MediaColor = System.Windows.Media.Color;
using MediaColors = System.Windows.Media.Colors;
using PlotColor = ScottPlot.Color;

namespace OpenTECHub.Views;

public partial class PowerView : UserControl
{
    private readonly WpfPlot _livePlot = new();
    private readonly WpfPlot _npPlot = new();
    private readonly WpfPlot _powerRatioPlot = new();
    private ScottPlot.Plottables.Text? _npTooltip;
    private (double X, double Y, double N, double Q)[] _npPoints = [];
    private ScottPlot.Plottables.Text? _powerRatioTooltip;
    private (double X, double Y, double N, double Q)[] _powerRatioPoints = [];

    /// <summary>
    /// Charts redraw only while the page is on screen and only when their data moved (§C). The
    /// live chart is incremental: two <see cref="DataLogger"/>s receive each new point as the
    /// ViewModel adds it, instead of the plot being cleared and rebuilt from 6000 points a second.
    /// </summary>
    private readonly VisibleRedrawTimer _redraw;
    private DataLogger? _liveTorque;
    private DataLogger? _liveRpm;
    private bool _liveNeedsRebuild = true;
    private bool _resultsDirty = true;
    private bool _liveDirty = true;

    private PowerTestViewModel? ViewModel => DataContext as PowerTestViewModel;

    /// <summary>The plan grid follows the condition in progress unless the operator scrolled it within this window.</summary>
    private static readonly TimeSpan OperatorScrollHold = TimeSpan.FromSeconds(5);
    private DateTime _lastOperatorScrollUtc = DateTime.MinValue;
    private bool _autoScrolling;
    private PowerTestViewModel? _observedViewModel;

    public PowerView()
    {
        InitializeComponent();
        LiveChartHost.Child = _livePlot;
        NpChartHost.Child = _npPlot;
        PowerRatioChartHost.Child = _powerRatioPlot;
        _npPlot.MouseMove += OnNpPlotMouseMove;
        _npPlot.MouseLeave += OnNpPlotMouseLeave;
        _powerRatioPlot.MouseMove += OnPowerRatioPlotMouseMove;
        _powerRatioPlot.MouseLeave += OnPowerRatioPlotMouseLeave;
        _redraw = new VisibleRedrawTimer(this, TimeSpan.FromSeconds(1), RedrawPlots);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_observedViewModel is not null)
        {
            _observedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _observedViewModel.LivePoints.CollectionChanged -= OnLivePointsChanged;
            _observedViewModel.Results.CollectionChanged -= OnResultsChanged;
        }

        _observedViewModel = ViewModel;
        if (_observedViewModel is not null)
        {
            _observedViewModel.PropertyChanged += OnViewModelPropertyChanged;
            _observedViewModel.LivePoints.CollectionChanged += OnLivePointsChanged;
            _observedViewModel.Results.CollectionChanged += OnResultsChanged;
        }

        _liveNeedsRebuild = true;
        MarkLiveDirty();
        MarkResultsDirty();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PowerTestViewModel.CurrentConditionId):
                FollowCurrentCondition();
                break;
            case nameof(PowerTestViewModel.ReferenceLiteratureNp):
            case nameof(PowerTestViewModel.ShowPowerRatioChart):
                MarkResultsDirty();
                break;
            case nameof(PowerTestViewModel.TareStatus):
            case nameof(PowerTestViewModel.ResultModeLabel):
                MarkResultsDirty();
                _redraw.Invalidate();
                break;
            case nameof(PowerTestViewModel.CurrentTest):
                ApplyDefaultResultsSorting();
                MarkResultsDirty();
                break;
        }
    }

    /// <summary>
    /// New points go straight into the loggers; a reset (new run) clears them; a trim at the
    /// 6000-point cap — an edge the logger cannot express — schedules a full rebuild.
    /// </summary>
    private void OnLivePointsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null && !_liveNeedsRebuild && _liveTorque is not null && _liveRpm is not null)
        {
            foreach (PowerDataPoint point in e.NewItems)
            {
                _liveTorque.Add(point.RelativeSeconds, point.TorquePercent);
                _liveRpm.Add(point.RelativeSeconds, point.RpmMeasured);
            }
        }
        else if (e.Action == NotifyCollectionChangedAction.Reset && _liveTorque is not null && _liveRpm is not null)
        {
            _liveTorque.Clear();
            _liveRpm.Clear();
            _liveNeedsRebuild = false;
        }
        else
        {
            _liveNeedsRebuild = true;
        }

        MarkLiveDirty();
    }

    private void OnResultsChanged(object? sender, NotifyCollectionChangedEventArgs e) => MarkResultsDirty();

    private void MarkLiveDirty()
    {
        _liveDirty = true;
        _redraw.MarkDirty();
    }

    private void MarkResultsDirty()
    {
        _resultsDirty = true;
        _redraw.MarkDirty();
    }

    /// <summary>
    /// Scrolls the row in progress into view — without touching the selection, which is the
    /// operator's — and only if the operator has not scrolled the grid in the last few seconds.
    /// </summary>
    private void FollowCurrentCondition()
    {
        if (ViewModel is not { CurrentConditionId: { } id } vm || !IsLoaded)
        {
            return;
        }

        if (DateTime.UtcNow - _lastOperatorScrollUtc < OperatorScrollHold)
        {
            return;
        }

        var row = vm.Conditions.FirstOrDefault(c => c.ConditionId == id);
        if (row is null)
        {
            return;
        }

        _autoScrolling = true;
        try
        {
            ConditionsGrid.ScrollIntoView(row);
        }
        finally
        {
            _autoScrolling = false;
        }
    }

    private void OnConditionsGridScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Extent-only changes (rows added, layout) are not operator scrolls.
        if (_autoScrolling || (e.VerticalChange == 0 && e.HorizontalChange == 0))
        {
            return;
        }

        _lastOperatorScrollUtc = DateTime.UtcNow;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SubscribeToThemeChanges();
        ApplyThemeToPlots();
        ApplyDefaultResultsSorting();
        _redraw.Invalidate();

        // The kLa maps live in a sibling workspace root that can change between visits, so the
        // picker is filled on entry rather than once at construction.
        if (ViewModel is { } viewModel)
        {
            await viewModel.RefreshKlaMapsCommand.ExecuteAsync(null);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UnsubscribeFromThemeChanges();
    }

    private void SubscribeToThemeChanges()
    {
        var theme = (Application.Current as App)?.Services?.GetService<IThemeService>();
        if (theme is null)
        {
            return;
        }

        theme.ThemeChanged -= OnThemeChanged;
        theme.ThemeChanged += OnThemeChanged;
    }

    private void UnsubscribeFromThemeChanges()
    {
        var theme = (Application.Current as App)?.Services?.GetService<IThemeService>();
        if (theme is not null)
        {
            theme.ThemeChanged -= OnThemeChanged;
        }
    }

    private void OnThemeChanged(bool isDark) => Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
    {
        if (!IsLoaded)
        {
            return;
        }

        ApplyThemeToPlots();
        _liveNeedsRebuild = true;
        _liveDirty = true;
        _resultsDirty = true;
        _redraw.Invalidate();
    });

    private void NpChart_Checked(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { ShowPowerRatioChart: true } vm)
        {
            vm.ShowPowerRatioChart = false;
        }
    }

    private void PowerRatioChart_Checked(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { ShowPowerRatioChart: false } vm)
        {
            vm.ShowPowerRatioChart = true;
        }
    }

    private void ApplyThemeToPlots()
    {
        StylePlot(_livePlot.Plot, "Tempo (s)", "Torque (%)");
        _livePlot.Plot.Axes.Right.Label.Text = "Rotação (rpm)";
        _livePlot.Plot.Axes.Right.Label.FontSize = 10;
        _livePlot.Plot.Axes.Right.TickLabelStyle.IsVisible = true;
        _livePlot.Plot.Axes.Right.FrameLineStyle.Width = 1;
        _livePlot.Plot.Axes.Right.MinimumSize = 45;
        StylePlot(_npPlot.Plot, "log₁₀(Re)", "Np");
        StylePlot(_powerRatioPlot.Plot, "Fl_G (–)", "P_G/P₀ (–)");
        _powerRatioPlot.Plot.Axes.Right.Label.Text = "Fr (–)";
        _powerRatioPlot.Plot.Axes.Right.Label.FontSize = 10;
        _powerRatioPlot.Plot.Axes.Right.TickLabelStyle.IsVisible = true;
        _powerRatioPlot.Plot.Axes.Right.FrameLineStyle.Width = 1;
        _powerRatioPlot.Plot.Axes.Right.MinimumSize = 45;
        _livePlot.Refresh();
        _npPlot.Refresh();
        _powerRatioPlot.Refresh();
    }

    private static void StylePlot(Plot plot, string xLabel, string yLabel)
    {
        var surface = ToPlotColor(TryBrush("SurfaceCardBrush"), MediaColors.White);
        var text = ToPlotColor(TryBrush("TextPrimaryBrush"), MediaColors.Black);
        var grid = ToPlotColor(TryBrush("StrokeDefaultBrush"), MediaColors.LightGray);
        plot.FigureBackground.Color = surface;
        plot.DataBackground.Color = surface;
        plot.Axes.Color(text);
        plot.Grid.MajorLineColor = grid.WithAlpha(0.45);
        plot.Axes.Title.Label.IsVisible = false;
        plot.Axes.Top.FrameLineStyle.Width = 0;
        plot.Axes.Top.TickLabelStyle.IsVisible = false;
        plot.Axes.Bottom.Label.Text = xLabel;
        plot.Axes.Bottom.Label.FontSize = 10;
        plot.Axes.Bottom.MinimumSize = 44;
        plot.Axes.Left.Label.Text = yLabel;
        plot.Axes.Left.Label.FontSize = 10;
        plot.Axes.Left.MinimumSize = 45;
        plot.Legend.IsVisible = false;
    }

    private void RedrawPlots()
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        if (_liveDirty)
        {
            _liveDirty = false;
            RedrawLive(vm);
        }

        if (_resultsDirty)
        {
            _resultsDirty = false;
            if (vm.ShowPowerRatioChart)
            {
                RedrawPowerRatio(vm);
            }
            else
            {
                RedrawNp(vm);
            }
        }
    }

    private void RedrawLive(PowerTestViewModel vm)
    {
        var plot = _livePlot.Plot;
        if (_liveNeedsRebuild || _liveTorque is null || _liveRpm is null)
        {
            plot.Clear();
            _liveTorque = plot.Add.DataLogger();
            _liveTorque.Color = PlotColor.FromHex("#3B82F6");
            _liveTorque.LineWidth = 1.8f;
            _liveTorque.MarkerSize = 0;
            _liveTorque.ViewFull();
            _liveRpm = plot.Add.DataLogger();
            _liveRpm.Color = PlotColor.FromHex("#10B981");
            _liveRpm.LineWidth = 1.4f;
            _liveRpm.MarkerSize = 0;
            _liveRpm.Axes.YAxis = plot.Axes.Right;
            _liveRpm.ViewFull();
            foreach (var point in vm.LivePoints)
            {
                _liveTorque.Add(point.RelativeSeconds, point.TorquePercent);
                _liveRpm.Add(point.RelativeSeconds, point.RpmMeasured);
            }
            _liveNeedsRebuild = false;
            ApplyThemeToLiveAfterClear(plot);
        }

        _livePlot.Refresh();
    }

    private static void ApplyThemeToLiveAfterClear(Plot plot)
    {
        plot.Axes.Right.Label.Text = "Rotação (rpm)";
        plot.Axes.Right.Label.FontSize = 10;
        plot.Axes.Right.TickLabelStyle.IsVisible = true;
        plot.Axes.Right.FrameLineStyle.Width = 1;
        plot.Axes.Right.MinimumSize = 45;
    }

    private void RedrawNp(PowerTestViewModel vm)
    {
        var plot = _npPlot.Plot;
        plot.Clear();
        var points = vm.Results
            .Where(p => p.IsAccepted)
            .Where(p => p.ReynoldsNumber > 0 && p.PowerNumber > 0 &&
                        double.IsFinite(p.ReynoldsNumber) && double.IsFinite(p.PowerNumber))
            .ToArray();
        if (points.Length > 0)
        {
            var xs = points.Select(p => Math.Log10(p.ReynoldsNumber)).ToArray();
            var ys = points.Select(p => p.PowerNumber).ToArray();
            var scatter = plot.Add.Scatter(xs, ys);
            scatter.Color = PlotColor.FromHex("#3B82F6");
            scatter.LineWidth = 0;
            scatter.MarkerSize = 7;
            scatter.MarkerLineWidth = 1.3f;
            scatter.MarkerLineColor = PlotColor.FromHex("#1E3A8A");

            _npPoints = new (double X, double Y, double N, double Q)[points.Length];
            for (var i = 0; i < points.Length; i++)
            {
                _npPoints[i] = (xs[i], ys[i], points[i].MeanRpm, points[i].GasFlowLpmNumber);
            }

            for (var i = 0; i < points.Length; i++)
            {
                var ci = points[i].PowerNumberCi95;
                if (!double.IsFinite(ci) || ci <= 0 || points[i].PowerNumber - ci <= 0)
                {
                    continue;
                }

                var error = plot.Add.Line(xs[i], points[i].PowerNumber - ci, xs[i], points[i].PowerNumber + ci);
                error.Color = PlotColor.FromHex("#3B82F6").WithAlpha(0.75);
                error.LineWidth = 1.3f;
            }
        }
        else
        {
            _npPoints = [];
        }

        if (vm.ReferenceLiteratureNp is { } literature && literature > 0 && double.IsFinite(literature))
        {
            var overlay = plot.Add.HorizontalLine(literature);
            overlay.Color = PlotColor.FromHex("#F59E0B");
            overlay.LinePattern = LinePattern.Dashed;
            overlay.LineWidth = 1.5f;
        }

        _npTooltip = plot.Add.Text("", 0, 0);
        _npTooltip.IsVisible = false;
        _npTooltip.LabelFontColor = PlotColor.FromHex("#F1F5F9");
        _npTooltip.LabelBackgroundColor = PlotColor.FromHex("#1E293B").WithAlpha(0.9f);
        _npTooltip.LabelFontSize = 13;
        _npTooltip.LabelPadding = 4;
        _npTooltip.LabelBorderColor = PlotColor.FromHex("#475569");
        _npTooltip.LabelBorderWidth = 1;

        plot.Axes.AutoScale();
        _npPlot.Refresh();
    }

    private void OnNpPlotMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_npTooltip is not null && _npTooltip.IsVisible)
        {
            _npTooltip.IsVisible = false;
            _npPlot.Refresh();
        }
    }

    private void OnNpPlotMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_npTooltip is null || _npPoints.Length == 0)
        {
            return;
        }

        var position = e.GetPosition(_npPlot);
        var pixel = new Pixel(
            (float)(position.X * _npPlot.DisplayScale),
            (float)(position.Y * _npPlot.DisplayScale));

        double minDistance = double.MaxValue;
        int nearestIndex = -1;
        var coordinates = _npPlot.Plot.GetCoordinates(pixel);
        var limits = _npPlot.Plot.Axes.GetLimits();
        double xRange = limits.Right - limits.Left;
        double yRange = limits.Top - limits.Bottom;
        if (xRange <= 0)
        {
            xRange = 1;
        }
        if (yRange <= 0)
        {
            yRange = 1;
        }

        for (int i = 0; i < _npPoints.Length; i++)
        {
            var p = _npPoints[i];
            double dx = (p.X - coordinates.X) / xRange;
            double dy = (p.Y - coordinates.Y) / yRange;
            double distanceSquared = dx * dx + dy * dy;
            if (distanceSquared < minDistance)
            {
                minDistance = distanceSquared;
                nearestIndex = i;
            }
        }

        if (nearestIndex >= 0 && minDistance < 0.002) // Roughly 4% of the plot area radius
        {
            var p = _npPoints[nearestIndex];
            double qg = double.IsNaN(p.Q) ? 0.0 : p.Q;
            _npTooltip.LabelText = $"N = {p.N:F0} rpm\nQg = {qg:F1} L/min";
            _npTooltip.Location = new Coordinates(p.X, p.Y);
            if (!_npTooltip.IsVisible)
            {
                _npTooltip.IsVisible = true;
            }
            _npPlot.Refresh();
        }
        else if (_npTooltip.IsVisible)
        {
            _npTooltip.IsVisible = false;
            _npPlot.Refresh();
        }
    }

    private void OnPowerRatioPlotMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_powerRatioTooltip is not null && _powerRatioTooltip.IsVisible)
        {
            _powerRatioTooltip.IsVisible = false;
            _powerRatioPlot.Refresh();
        }
    }

    private void OnPowerRatioPlotMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_powerRatioTooltip is null || _powerRatioPoints.Length == 0)
        {
            return;
        }

        var position = e.GetPosition(_powerRatioPlot);
        var pixel = new Pixel(
            (float)(position.X * _powerRatioPlot.DisplayScale),
            (float)(position.Y * _powerRatioPlot.DisplayScale));

        var coordinates = _powerRatioPlot.Plot.GetCoordinates(pixel);
        var limits = _powerRatioPlot.Plot.Axes.GetLimits();
        double xRange = limits.Right - limits.Left;
        double yRange = limits.Top - limits.Bottom;
        if (xRange <= 0)
        {
            xRange = 1;
        }
        if (yRange <= 0)
        {
            yRange = 1;
        }

        double minDistance = double.MaxValue;
        int nearestIndex = -1;

        for (int i = 0; i < _powerRatioPoints.Length; i++)
        {
            var p = _powerRatioPoints[i];
            double dx = (p.X - coordinates.X) / xRange;
            double dy = (p.Y - coordinates.Y) / yRange;
            double distanceSquared = dx * dx + dy * dy;
            if (distanceSquared < minDistance)
            {
                minDistance = distanceSquared;
                nearestIndex = i;
            }
        }

        if (nearestIndex >= 0 && minDistance < 0.002) // Roughly 4% of the plot area radius
        {
            var p = _powerRatioPoints[nearestIndex];
            double qg = double.IsNaN(p.Q) ? 0.0 : p.Q;
            _powerRatioTooltip.LabelText = $"N = {p.N:F0} rpm\nQg = {qg:F1} L/min";
            _powerRatioTooltip.Location = new Coordinates(p.X, p.Y);
            if (!_powerRatioTooltip.IsVisible)
            {
                _powerRatioTooltip.IsVisible = true;
            }
            _powerRatioPlot.Refresh();
        }
        else if (_powerRatioTooltip.IsVisible)
        {
            _powerRatioTooltip.IsVisible = false;
            _powerRatioPlot.Refresh();
        }
    }

    private void RedrawPowerRatio(PowerTestViewModel vm)
    {
        var plot = _powerRatioPlot.Plot;
        plot.Clear();
        StylePlot(plot, "Fl_G (–)", "P_G/P₀ (–)");
        plot.Axes.Right.Label.Text = "Fr (–)";
        plot.Axes.Right.Label.FontSize = 10;
        plot.Axes.Right.TickLabelStyle.IsVisible = true;
        plot.Axes.Right.FrameLineStyle.Width = 1;

        var points = vm.Results
            .Where(p => p.IsAccepted && p.IsGassed &&
                        p.AerationNumber > 0 && p.Ratio > 0 &&
                        double.IsFinite(p.AerationNumber) && double.IsFinite(p.Ratio))
            .OrderBy(p => p.AerationNumber)
            .ToArray();

        if (points.Length > 0)
        {
            var xs = points.Select(p => p.AerationNumber).ToArray();
            var ys = points.Select(p => p.Ratio).ToArray();
            _powerRatioPoints = new (double X, double Y, double N, double Q)[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                _powerRatioPoints[i] = (xs[i], ys[i], points[i].MeanRpm, points[i].GasFlowLpmNumber);
            }

            var scatter = plot.Add.Scatter(xs, ys);
            scatter.Color = PlotColor.FromHex("#3B82F6");
            scatter.LineWidth = 1.6f;
            scatter.MarkerSize = 7;
            scatter.MarkerLineWidth = 1.3f;
            scatter.MarkerLineColor = PlotColor.FromHex("#1E3A8A");

            for (var i = 0; i < points.Length; i++)
            {
                var ci = points[i].RatioCi95;
                if (!double.IsFinite(ci) || ci <= 0)
                {
                    continue;
                }

                var error = plot.Add.Line(xs[i], Math.Max(0.0, ys[i] - ci), xs[i], ys[i] + ci);
                error.Color = PlotColor.FromHex("#3B82F6").WithAlpha(0.75);
                error.LineWidth = 1.3f;
            }

            var frPoints = points
                .Where(p => p.FroudeNumber > 0 && double.IsFinite(p.FroudeNumber))
                .ToArray();
            if (frPoints.Length > 0)
            {
                var fr = plot.Add.Scatter(
                    frPoints.Select(p => p.AerationNumber).ToArray(),
                    frPoints.Select(p => p.FroudeNumber).ToArray());
                fr.Color = PlotColor.FromHex("#10B981").WithAlpha(0.75);
                fr.LineWidth = 1.2f;
                fr.LinePattern = LinePattern.Dotted;
                fr.MarkerSize = 4;
                fr.MarkerLineWidth = 1.3f;
                fr.MarkerLineColor = PlotColor.FromHex("#064E3B");
                fr.Axes.YAxis = plot.Axes.Right;
            }
        }
        else
        {
            _powerRatioPoints = [];
        }

        _powerRatioTooltip = plot.Add.Text("", 0, 0);
        _powerRatioTooltip.IsVisible = false;
        _powerRatioTooltip.LabelFontColor = PlotColor.FromHex("#F1F5F9");
        _powerRatioTooltip.LabelBackgroundColor = PlotColor.FromHex("#1E293B").WithAlpha(0.9f);
        _powerRatioTooltip.LabelFontSize = 13;
        _powerRatioTooltip.LabelPadding = 4;
        _powerRatioTooltip.LabelBorderColor = PlotColor.FromHex("#475569");
        _powerRatioTooltip.LabelBorderWidth = 1;

        plot.Axes.AutoScale();
        _powerRatioPlot.Refresh();
    }

    private void ResultsDataGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        var dataGrid = (DataGrid)sender;
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(dataGrid.ItemsSource);
        if (view is null)
        {
            return;
        }

        var propertyName = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(propertyName) && e.Column is DataGridBoundColumn boundCol && boundCol.Binding is System.Windows.Data.Binding binding)
        {
            propertyName = binding.Path.Path;
        }

        if (string.IsNullOrEmpty(propertyName))
        {
            return;
        }

        var direction = System.ComponentModel.ListSortDirection.Ascending;
        if (e.Column.SortDirection == System.ComponentModel.ListSortDirection.Ascending)
        {
            direction = System.ComponentModel.ListSortDirection.Descending;
        }

        e.Column.SortDirection = direction;

        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new System.ComponentModel.SortDescription(propertyName, direction));

        if (propertyName is "SortPhasePriority" or "Status" or "StopReason")
        {
            view.SortDescriptions.Add(new System.ComponentModel.SortDescription("MeanRpm", System.ComponentModel.ListSortDirection.Ascending));
            view.SortDescriptions.Add(new System.ComponentModel.SortDescription("GasFlowLpmNumber", System.ComponentModel.ListSortDirection.Ascending));
        }
        else if (propertyName == "MeanRpm")
        {
            view.SortDescriptions.Add(new System.ComponentModel.SortDescription("GasFlowLpmNumber", System.ComponentModel.ListSortDirection.Ascending));
        }

        view.Refresh();
        e.Handled = true;
    }

    private void ResultsDataGrid_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyDefaultResultsSorting();
    }

    private void ApplyDefaultResultsSorting()
    {
        if (ResultsDataGrid?.ItemsSource is null)
        {
            return;
        }

        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(ResultsDataGrid.ItemsSource);
        if (view is null)
        {
            return;
        }

        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new System.ComponentModel.SortDescription("SortPhasePriority", System.ComponentModel.ListSortDirection.Ascending));
        view.SortDescriptions.Add(new System.ComponentModel.SortDescription("MeanRpm", System.ComponentModel.ListSortDirection.Ascending));
        view.SortDescriptions.Add(new System.ComponentModel.SortDescription("GasFlowLpmNumber", System.ComponentModel.ListSortDirection.Ascending));

        foreach (var column in ResultsDataGrid.Columns)
        {
            if (column.Header?.ToString() == "Status")
            {
                column.SortDirection = System.ComponentModel.ListSortDirection.Ascending;
            }
            else
            {
                column.SortDirection = null;
            }
        }

        view.Refresh();
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || string.IsNullOrWhiteSpace(vm.ResultsCsvPath) || !File.Exists(vm.ResultsCsvPath))
        {
            MessageBox.Show("Ainda não existe um resumo CSV para este ensaio.", "Exportar resultados", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "Exportar resultados do ensaio",
            Filter = "Arquivo CSV (*.csv)|*.csv",
            FileName = Path.GetFileName(vm.ResultsCsvPath),
            AddExtension = true,
            DefaultExt = ".csv",
            RestoreDirectory = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            File.Copy(vm.ResultsCsvPath, dialog.FileName, overwrite: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Não foi possível exportar o CSV.\n\n{ex.Message}", "Exportar resultados", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static System.Windows.Media.Brush? TryBrush(string key) => Application.Current?.TryFindResource(key) as System.Windows.Media.Brush;

    private static PlotColor ToPlotColor(System.Windows.Media.Brush? brush, MediaColor fallback)
        => brush is System.Windows.Media.SolidColorBrush scb
            ? new PlotColor(scb.Color.R, scb.Color.G, scb.Color.B, scb.Color.A)
            : new PlotColor(fallback.R, fallback.G, fallback.B, fallback.A);
}
