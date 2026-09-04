using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private readonly WpfPlot _pgPlot = new();
    private readonly DispatcherTimer _redrawTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private PowerTestViewModel? ViewModel => DataContext as PowerTestViewModel;

    public PowerView()
    {
        InitializeComponent();
        LiveChartHost.Child = _livePlot;
        NpChartHost.Child = _npPlot;
        PgChartHost.Child = _pgPlot;
        _pgPlot.MouseDown += OnPgPlotMouseDown;
        _redrawTimer.Tick += (_, _) => RedrawPlots();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SubscribeToThemeChanges();
        ApplyThemeToPlots();
        RedrawPlots();
        _redrawTimer.Start();

        // The kLa maps live in a sibling workspace root that can change between visits, so the
        // picker is filled on entry rather than once at construction.
        if (ViewModel is { } viewModel)
        {
            await viewModel.RefreshKlaMapsCommand.ExecuteAsync(null);
        }
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
        StylePlot(_pgPlot.Plot, "Número de aeração (Fl_G)", "Razão de potência (PG / P₀)");
        _pgPlot.Plot.Axes.Right.Label.Text = "Número de Froude (Fr)";
        _pgPlot.Plot.Axes.Right.Label.FontSize = 10;
        _pgPlot.Plot.Axes.Right.TickLabelStyle.IsVisible = true;
        _pgPlot.Plot.Axes.Right.FrameLineStyle.Width = 1;
        _livePlot.Refresh();
        _npPlot.Refresh();
        _pgPlot.Refresh();
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
        if (!vm.ShowFloodingChart)
        {
            RedrawNp(vm);
        }
        else
        {
            RedrawPg(vm);
        }
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

    private void RedrawPg(PowerTestViewModel vm)
    {
        var plot = _pgPlot.Plot;
        plot.Clear();

        var gassedRows = vm.Results
            .Where(r => r.IsGassed && r.AerationNumber > 0 && r.Ratio > 0 &&
                        double.IsFinite(r.AerationNumber) && double.IsFinite(r.Ratio))
            .OrderBy(r => r.AerationNumber)
            .ToArray();

        if (gassedRows.Length > 0)
        {
            var xs = gassedRows.Select(r => r.AerationNumber).ToArray();
            var ys = gassedRows.Select(r => r.Ratio).ToArray();
            var frs = gassedRows.Select(r => r.FroudeNumber).ToArray();

            // 1. Curva experimental PG/P0
            var scatter = plot.Add.Scatter(xs, ys);
            scatter.Color = PlotColor.FromHex("#3B82F6"); // Azul
            scatter.LineWidth = 1.6f;
            scatter.MarkerSize = 7;

            // Barras de incerteza IC95 de PG/P0
            for (var i = 0; i < gassedRows.Length; i++)
            {
                var ci = gassedRows[i].RatioCi95;
                if (double.IsFinite(ci) && ci > 0)
                {
                    var err = plot.Add.Line(xs[i], Math.Max(0.0, ys[i] - ci), xs[i], ys[i] + ci);
                    err.Color = PlotColor.FromHex("#3B82F6").WithAlpha(0.75);
                    err.LineWidth = 1.3f;
                }
            }

            // 2. Eixo secundário: Número de Froude Fr
            if (frs.Any(f => double.IsFinite(f) && f > 0))
            {
                var frScatter = plot.Add.Scatter(xs, frs);
                frScatter.Color = PlotColor.FromHex("#10B981").WithAlpha(0.7f); // Verde esmeralda
                frScatter.LineWidth = 1.2f;
                frScatter.LinePattern = LinePattern.Dotted;
                frScatter.MarkerSize = 4;
                frScatter.Axes.YAxis = plot.Axes.Right;
            }
        }

        // 3. Overlay da correlação teórica de Nienow e destaque do ponto de Flooding
        if (vm.FloodingResult is { } flooding)
        {
            if (flooding.TheoreticalFlGNienow > 0 && double.IsFinite(flooding.TheoreticalFlGNienow))
            {
                var nienowLine = plot.Add.VerticalLine(flooding.TheoreticalFlGNienow);
                nienowLine.Color = PlotColor.FromHex("#F59E0B"); // Âmbar tracejado
                nienowLine.LinePattern = LinePattern.Dashed;
                nienowLine.LineWidth = 1.5f;
            }

            var floodingRow = gassedRows.FirstOrDefault(r => Math.Abs(r.AerationNumber - flooding.ExperimentalFlG) < 1e-4)
                              ?? gassedRows.FirstOrDefault();
            var floodRatio = floodingRow?.Ratio ?? 0.7;

            var markerScatter = plot.Add.Scatter(new double[] { flooding.ExperimentalFlG }, new double[] { floodRatio });
            markerScatter.Color = PlotColor.FromHex("#EF4444"); // Vermelho
            markerScatter.LineWidth = 0;
            markerScatter.MarkerSize = 13;
            markerScatter.MarkerShape = MarkerShape.FilledDiamond;

            var note = plot.Add.Text($"Flooding ((Fl_G)_F={flooding.ExperimentalFlG:G4})", flooding.ExperimentalFlG, floodRatio);
            note.LabelFontSize = 10;
            note.LabelFontColor = PlotColor.FromHex("#EF4444");
        }

        ApplyThemeToPgAfterClear(plot);
        plot.Axes.AutoScale();
        _pgPlot.Refresh();
    }

    private static void ApplyThemeToPgAfterClear(Plot plot)
    {
        plot.Axes.Right.Label.Text = "Número de Froude (Fr)";
        plot.Axes.Right.Label.FontSize = 10;
        plot.Axes.Right.TickLabelStyle.IsVisible = true;
        plot.Axes.Right.FrameLineStyle.Width = 1;
    }

    private void OnPgPlotMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { } vm || !vm.ShowFloodingChart || vm.Results.Count == 0)
        {
            return;
        }

        var pos = e.GetPosition(_pgPlot);
        var coords = _pgPlot.Plot.GetCoordinates((float)pos.X, (float)pos.Y);

        PowerResultRow? closestRow = null;
        var minDistanceSq = double.PositiveInfinity;

        foreach (var row in vm.Results)
        {
            if (!row.IsGassed || !double.IsFinite(row.AerationNumber) || !double.IsFinite(row.Ratio))
            {
                continue;
            }

            var dx = coords.X - row.AerationNumber;
            var dy = coords.Y - row.Ratio;
            var distSq = dx * dx + dy * dy;

            if (distSq < minDistanceSq)
            {
                minDistanceSq = distSq;
                closestRow = row;
            }
        }

        if (closestRow is not null && minDistanceSq < 0.25)
        {
            vm.SelectedResultRow = closestRow;
        }
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
