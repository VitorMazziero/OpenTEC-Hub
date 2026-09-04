using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using OpenTECHub.Services.Theme;
using OpenTECHub.ViewModels;
using ScottPlot;
using ScottPlot.WPF;

using MediaColor = System.Windows.Media.Color;
using MediaColors = System.Windows.Media.Colors;
using PlotColor = ScottPlot.Color;

namespace OpenTECHub.Views;

public partial class PowerView : UserControl
{
    private readonly WpfPlot _livePlot = new();
    private readonly WpfPlot _npPlot = new();
    private readonly DispatcherTimer _redrawTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private PowerTestViewModel? ViewModel => DataContext as PowerTestViewModel;

    public PowerView()
    {
        InitializeComponent();
        LiveChartHost.Child = _livePlot;
        NpChartHost.Child = _npPlot;
        _redrawTimer.Tick += (_, _) => RedrawPlots();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SubscribeToThemeChanges();
        ApplyThemeToPlots();
        RedrawPlots();
        _redrawTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _redrawTimer.Stop();
        UnsubscribeFromThemeChanges();
    }

    private void SubscribeToThemeChanges()
    {
        var theme = ((App)Application.Current).Services?.GetService<IThemeService>();
        if (theme is null)
        {
            return;
        }

        theme.ThemeChanged -= OnThemeChanged;
        theme.ThemeChanged += OnThemeChanged;
    }

    private void UnsubscribeFromThemeChanges()
    {
        var theme = ((App)Application.Current).Services?.GetService<IThemeService>();
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
        RedrawPlots();
    });

    private void ApplyThemeToPlots()
    {
        StylePlot(_livePlot.Plot, "Tempo (s)", "Torque (%)");
        _livePlot.Plot.Axes.Right.Label.Text = "Rotação (rpm)";
        _livePlot.Plot.Axes.Right.Label.FontSize = 10;
        _livePlot.Plot.Axes.Right.TickLabelStyle.IsVisible = true;
        _livePlot.Plot.Axes.Right.FrameLineStyle.Width = 1;
        StylePlot(_npPlot.Plot, "log₁₀(Re)", "log₁₀(Np)");
        _livePlot.Refresh();
        _npPlot.Refresh();
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
        plot.Axes.Left.Label.Text = yLabel;
        plot.Axes.Left.Label.FontSize = 10;
        plot.Legend.IsVisible = false;
    }

    private void RedrawPlots()
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        RedrawLive(vm);
        RedrawNp(vm);
    }

    private void RedrawLive(PowerTestViewModel vm)
    {
        var plot = _livePlot.Plot;
        plot.Clear();
        var points = vm.LivePoints.ToArray();
        if (points.Length > 0)
        {
            var xs = points.Select(p => p.RelativeSeconds).ToArray();
            var torque = points.Select(p => p.TorquePercent).ToArray();
            var rpm = points.Select(p => p.RpmMeasured).ToArray();
            var torqueLine = plot.Add.Scatter(xs, torque);
            torqueLine.Color = PlotColor.FromHex("#3B82F6");
            torqueLine.LineWidth = 1.8f;
            torqueLine.MarkerSize = 0;
            var rpmLine = plot.Add.Scatter(xs, rpm);
            rpmLine.Color = PlotColor.FromHex("#10B981");
            rpmLine.LineWidth = 1.4f;
            rpmLine.MarkerSize = 0;
            rpmLine.Axes.YAxis = plot.Axes.Right;
            plot.Axes.AutoScale();
        }
        ApplyThemeToLiveAfterClear(plot);
        _livePlot.Refresh();
    }

    private static void ApplyThemeToLiveAfterClear(Plot plot)
    {
        plot.Axes.Right.Label.Text = "Rotação (rpm)";
        plot.Axes.Right.Label.FontSize = 10;
        plot.Axes.Right.TickLabelStyle.IsVisible = true;
        plot.Axes.Right.FrameLineStyle.Width = 1;
    }

    private void RedrawNp(PowerTestViewModel vm)
    {
        var plot = _npPlot.Plot;
        plot.Clear();
        var points = vm.Results
            .Where(p => p.ReynoldsNumber > 0 && p.PowerNumber > 0 &&
                        double.IsFinite(p.ReynoldsNumber) && double.IsFinite(p.PowerNumber))
            .ToArray();
        if (points.Length > 0)
        {
            var xs = points.Select(p => Math.Log10(p.ReynoldsNumber)).ToArray();
            var ys = points.Select(p => Math.Log10(p.PowerNumber)).ToArray();
            var scatter = plot.Add.Scatter(xs, ys);
            scatter.Color = PlotColor.FromHex("#3B82F6");
            scatter.LineWidth = 0;
            scatter.MarkerSize = 7;

            for (var i = 0; i < points.Length; i++)
            {
                var ci = points[i].PowerNumberCi95;
                if (!double.IsFinite(ci) || ci <= 0 || points[i].PowerNumber - ci <= 0)
                {
                    continue;
                }

                var error = plot.Add.Line(xs[i], Math.Log10(points[i].PowerNumber - ci), xs[i], Math.Log10(points[i].PowerNumber + ci));
                error.Color = PlotColor.FromHex("#3B82F6").WithAlpha(0.75);
                error.LineWidth = 1.3f;
            }
        }

        if (vm.ReferenceLiteratureNp is { } literature && literature > 0 && double.IsFinite(literature))
        {
            var overlay = plot.Add.HorizontalLine(Math.Log10(literature));
            overlay.Color = PlotColor.FromHex("#F59E0B");
            overlay.LinePattern = LinePattern.Dashed;
            overlay.LineWidth = 1.5f;
        }
        plot.Axes.AutoScale();
        _npPlot.Refresh();
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
