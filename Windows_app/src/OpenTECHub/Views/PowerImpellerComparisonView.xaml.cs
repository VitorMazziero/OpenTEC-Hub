using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
/// Side-by-side impeller benchmarking (§18.3 step 6.2): the Np×Re plateau overlay with its
/// uncertainty bands, the gas-dispersion drop P_G/P₀×Fl_G, and the specific power demand.
/// </summary>
public partial class PowerImpellerComparisonView : UserControl
{
    private readonly WpfPlot _npRePlot = new();
    private readonly WpfPlot _powerRatioPlot = new();
    private readonly WpfPlot _specificPowerPlot = new();
    private PowerImpellerComparisonViewModel? _subscribed;

    public PowerImpellerComparisonView()
    {
        InitializeComponent();
        NpRePlotHost.Child = _npRePlot;
        PowerRatioPlotHost.Child = _powerRatioPlot;
        SpecificPowerPlotHost.Child = _specificPowerPlot;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += (_, _) => Attach();
    }

    private PowerImpellerComparisonViewModel? ViewModel => DataContext as PowerImpellerComparisonViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SubscribeToThemeChanges();
        Attach();
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

    private void Redraw()
    {
        DrawNpReynolds();
        DrawPowerRatio();
        DrawSpecificPower();
    }

    private void DrawNpReynolds()
    {
        var plot = _npRePlot.Plot;
        plot.Clear();
        StylePlot(plot, "Re (–)", "Np (–)");

        var items = ViewModel?.Items ?? [];
        var withCurve = items
            .Where(i => i.PowerNumberReynoldsCurve.Any(p => p.Reynolds > 0 && p.PowerNumber > 0))
            .ToArray();

        if (withCurve.Length == 0)
        {
            AddCentredNote(plot, "Selecione ensaios e clique em Comparar");
            _npRePlot.Refresh();
            return;
        }

        var palette = new ScottPlot.Palettes.Category10();

        for (var index = 0; index < withCurve.Length; index++)
        {
            var item = withCurve[index];
            var color = palette.GetColor(index);

            // Re spans decades, so the X axis is log10 (§4.3). ScottPlot 5 has no log scale of its
            // own: the data is transformed here and the tick labels are formatted back.
            var points = item.PowerNumberReynoldsCurve
                .Where(p => p.Reynolds > 0 && double.IsFinite(p.PowerNumber))
                .OrderBy(p => p.Reynolds)
                .ToArray();

            if (points.Length == 0)
            {
                continue;
            }

            var series = plot.Add.Scatter(
                points.Select(p => Math.Log10(p.Reynolds)).ToArray(),
                points.Select(p => p.PowerNumber).ToArray());
            series.MarkerSize = 6;
            series.LineWidth = 1.4f;
            series.Color = color;

            // IC95 as a vertical whisker per point: the band is the measurement, not decoration.
            foreach (var point in points.Where(p => p.Uncertainty95 > 0))
            {
                var logRe = Math.Log10(point.Reynolds);
                var whisker = plot.Add.Line(
                    logRe, point.PowerNumber - point.Uncertainty95,
                    logRe, point.PowerNumber + point.Uncertainty95);
                whisker.LineColor = color.WithAlpha(0.55);
                whisker.LineWidth = 1.2f;
                whisker.MarkerStyle.IsVisible = false;
            }

            // The fitted turbulent plateau, drawn only over the Reynolds range it was fitted on.
            if (item.TurbulentNpMean > 0)
            {
                var turbulent = points
                    .Where(p => p.Reynolds >= ImpellerComparisonBuilder.TurbulentReynoldsCutoff)
                    .ToArray();

                if (turbulent.Length > 0)
                {
                    var plateau = plot.Add.Line(
                        Math.Log10(turbulent.Min(p => p.Reynolds)), item.TurbulentNpMean,
                        Math.Log10(turbulent.Max(p => p.Reynolds)), item.TurbulentNpMean);
                    plateau.LineColor = color;
                    plateau.LineWidth = 2.2f;
                    plateau.LinePattern = LinePattern.Dashed;
                    plateau.MarkerStyle.IsVisible = false;
                }
            }
        }

        var logTicks = new ScottPlot.TickGenerators.NumericAutomatic
        {
            MinorTickGenerator = new ScottPlot.TickGenerators.LogMinorTickGenerator(),
            LabelFormatter = value => FormatPowerOfTen(value),
        };
        plot.Axes.Bottom.TickGenerator = logTicks;
        plot.Axes.AutoScale();
        _npRePlot.Refresh();
    }

    private void DrawPowerRatio()
    {
        var plot = _powerRatioPlot.Plot;
        plot.Clear();
        StylePlot(plot, "Fl_G (–)", "P_G/P₀ (–)");

        var items = ViewModel?.Items ?? [];
        var withCurve = items.Where(i => i.PowerRatioCurve.Count > 0).ToArray();

        if (withCurve.Length == 0)
        {
            AddCentredNote(plot, "Nenhum ponto gaseificado nos ensaios selecionados");
            _powerRatioPlot.Refresh();
            return;
        }

        var palette = new ScottPlot.Palettes.Category10();

        for (var index = 0; index < withCurve.Length; index++)
        {
            var item = withCurve[index];
            var color = palette.GetColor(index);
            var points = item.PowerRatioCurve.OrderBy(p => p.GasFlowNumber).ToArray();

            var series = plot.Add.Scatter(
                points.Select(p => p.GasFlowNumber).ToArray(),
                points.Select(p => p.PowerRatio).ToArray());
            series.MarkerSize = 6;
            series.LineWidth = 1.4f;
            series.Color = color;
        }

        plot.Axes.AutoScale();
        _powerRatioPlot.Refresh();
    }

    private void DrawSpecificPower()
    {
        var plot = _specificPowerPlot.Plot;
        plot.Clear();
        StylePlot(plot, "Qg (L/min)", "P/V (W/m³)");

        var items = ViewModel?.Items ?? [];
        var usable = items
            .Where(i => i.LiquidVolumeM3 > 0 && i.PowerRatioCurve.Count > 0)
            .ToArray();

        if (usable.Length == 0)
        {
            AddCentredNote(plot, "Sem volume útil declarado ou sem pontos gaseificados");
            _specificPowerPlot.Refresh();
            return;
        }

        var palette = new ScottPlot.Palettes.Category10();

        for (var index = 0; index < usable.Length; index++)
        {
            var item = usable[index];
            var byFlow = item.PowerRatioCurve
                .GroupBy(p => Math.Round(p.GasFlowLpm, 2))
                .OrderBy(g => g.Key)
                .ToArray();

            if (byFlow.Length == 0)
            {
                continue;
            }

            // P/V at each flow, averaged across the rotations measured at that flow.
            var flows = byFlow.Select(g => g.Key).ToArray();
            var specific = byFlow
                .Select(g => (item.AverageSpecificPowerWm3 ?? 0.0) * g.Average(p => p.PowerRatio))
                .ToArray();

            var series = plot.Add.Scatter(flows, specific);
            series.MarkerSize = 6;
            series.LineWidth = 1.4f;
            series.Color = palette.GetColor(index);
        }

        plot.Axes.AutoScale();
        _specificPowerPlot.Refresh();
    }

    private void ExportComparisonCsv_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel || !viewModel.HasComparison)
        {
            MessageBox.Show(
                "Monte a comparação antes de exportar.",
                "Exportar comparação",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Exportar comparação de impelidores",
            Filter = "CSV (*.csv)|*.csv",
            FileName = viewModel.SuggestedCsvFileName,
            RestoreDirectory = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, viewModel.BuildCsvContent(), System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Não foi possível exportar a comparação.\n\n{ex.Message}",
                "Exportar comparação",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>Turns a log10 axis position back into the Reynolds number it stands for.</summary>
    private static string FormatPowerOfTen(double logValue)
    {
        var value = Math.Pow(10, logValue);
        return value >= 1000 ? $"{value:0.#e+0}" : $"{value:0.##}";
    }

    private static void AddCentredNote(Plot plot, string text)
    {
        var note = plot.Add.Text(text, 0.5, 0.5);
        note.Alignment = Alignment.MiddleCenter;
        note.LabelFontColor = ToPlotColor(TryBrush("TextMutedBrush"), MediaColors.Gray);
        plot.Axes.SetLimits(0, 1, 0, 1);
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
