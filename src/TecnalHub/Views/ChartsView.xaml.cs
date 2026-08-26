using Microsoft.Extensions.DependencyInjection;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ScottPlot;
using ScottPlot.WPF;
using TecnalHub.ViewModels;

// ScottPlot and WPF both define Color and Colors. Aliasing both sides keeps every use
// site unambiguous without the reader having to work out which library is meant.
using MediaColor = System.Windows.Media.Color;
using MediaColors = System.Windows.Media.Colors;
using PlotColor = ScottPlot.Color;

namespace TecnalHub.Views;

/// <summary>
/// Live charts for the selected process variables.
/// </summary>
/// <remarks>
/// <para>
/// ScottPlot is a rendering surface rather than something to express through bindings,
/// so the plots are built and driven here. The ViewModel holds no plot objects; it only
/// says which channels to draw and over what window.
/// </para>
/// <para>
/// Redraw is on a timer at 1 Hz, not per telemetry frame. The device emits every 2 s and
/// a chart repainted more often than the eye resolves is wasted work on the UI thread -
/// which the roadmap's "UI thread never blocks" target does not allow.
/// </para>
/// </remarks>
public partial class ChartsView : UserControl
{
    /// <summary>
    /// Points handed to a plot per panel.
    /// </summary>
    /// <remarks>
    /// A panel is at most a screen wide, so more than this cannot be seen. The history
    /// buffer downsamples to this before the data ever reaches ScottPlot.
    /// </remarks>
    private const int MaxPointsPerPanel = 2000;

    private readonly WpfPlot _leftPlot = new();
    private readonly WpfPlot _rightPlot = new();
    private readonly DispatcherTimer _redraw = new() { Interval = TimeSpan.FromSeconds(1) };


    private ChartsViewModel? _subscribed;

    public ChartsView()
    {
        InitializeComponent();

        LeftHost.Child = _leftPlot;
        RightHost.Child = _rightPlot;

        _redraw.Tick += (_, _) => Redraw();
        _leftPlot.MouseMove += (_, args) => UpdateCursor(_leftPlot, args.GetPosition(_leftPlot));
        _rightPlot.MouseMove += (_, args) => UpdateCursor(_rightPlot, args.GetPosition(_rightPlot));

        Loaded += (_, _) =>
        {
            SubscribeToThemeChanges();
            Attach();
            _redraw.Start();
        };

        Unloaded += (_, _) =>
        {
            _redraw.Stop();
            UnsubscribeFromThemeChanges();
            Detach();
        };

        DataContextChanged += (_, _) => Attach();
    }

    private ChartsViewModel? ViewModel => DataContext as ChartsViewModel;

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
            if (IsLoaded && ViewModel is { } viewModel)
            {
                StylePlot(_leftPlot.Plot, viewModel.LeftChannel);
                if (viewModel.RightChannel is { } right)
                {
                    StylePlot(_rightPlot.Plot, right);
                }
                Redraw();
            }
        });

    /// <summary>Subscribes to layout changes so a channel swap re-styles its plot.</summary>
    private void Attach()
    {
        Detach();

        _subscribed = ViewModel;
        if (_subscribed is { } current)
        {
            current.LayoutChanged += OnLayoutChanged;
        }

        OnLayoutChanged();
    }

    private void Detach()
    {
        if (_subscribed is { } previous)
        {
            previous.LayoutChanged -= OnLayoutChanged;
            _subscribed = null;
        }
    }

    private void OnLayoutChanged()
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        StylePlot(_leftPlot.Plot, viewModel.LeftChannel);

        if (viewModel.RightChannel is { } right)
        {
            StylePlot(_rightPlot.Plot, right);
        }

        // Collapse the column, not just the Border: hiding the Border alone would
        // leave the left chart at half width with an empty gap beside it.
        RightColumn.Width = viewModel.ShowRightPanel
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);

        Redraw();
    }

    /// <summary>Applies the app's theme tokens to a plot.</summary>
    /// <remarks>
    /// ScottPlot renders its own chrome, so it does not inherit WPF resources. The
    /// colours are read from the same tokens as everything else, which is what keeps a
    /// chart from looking like a foreign object after a theme switch.
    /// </remarks>
    private static void StylePlot(Plot plot, ChartChannelOption spec)
    {
        var surface = ToPlotColor(TryBrush("SurfaceCardBrush"), MediaColors.White);
        var text = ToPlotColor(TryBrush("TextSecondaryBrush"), MediaColors.Gray);
        var grid = ToPlotColor(TryBrush("StrokeDefaultBrush"), MediaColors.LightGray);

        plot.FigureBackground.Color = surface;
        plot.DataBackground.Color = surface;

        plot.Axes.Color(text);
        plot.Grid.MajorLineColor = grid.WithAlpha(0.45);

        plot.Axes.Bottom.Label.Text = "Tempo (min)";
        plot.Axes.Left.Label.Text = spec.Unit;
        plot.Axes.Title.Label.Text = spec.Title;
        plot.Axes.Title.Label.ForeColor = ToPlotColor(TryBrush("TextPrimaryBrush"), MediaColors.Black);
        plot.Axes.Title.Label.FontSize = 14;

        plot.Axes.Bottom.Label.FontSize = 11;
        plot.Axes.Left.Label.FontSize = 11;
    }

    private void Redraw()
    {
        if (ViewModel is not { } viewModel || viewModel.IsPaused)
        {
            return;
        }

        viewModel.UpdateSampleCount();

        DrawPanel(_leftPlot, viewModel.LeftChannel, viewModel);

        if (viewModel.RightChannel is { } right)
        {
            DrawPanel(_rightPlot, right, viewModel);
        }
    }

    private static void DrawPanel(WpfPlot host, ChartChannelOption spec, ChartsViewModel viewModel)
    {
        var series = viewModel.GetSeries(spec, MaxPointsPerPanel);

        host.Plot.Clear();

        if (series.Count >= 2)
        {
            var scatter = host.Plot.Add.Scatter(series.Minutes, series.Values);
            scatter.MarkerSize = 0;              // a line, not a scatter of dots
            scatter.LineWidth = 1.8f;
            scatter.Color = ToPlotColor(TryBrush(spec.SeriesBrushKey), MediaColors.SteelBlue);

            host.Plot.Axes.AutoScale();
        }

        if (viewModel.IsCursorEnabled && viewModel.CursorMinutes is { } cursor)
        {
            var line = host.Plot.Add.VerticalLine(cursor);
            line.Color = ToPlotColor(TryBrush("AccentBrush"), MediaColors.SteelBlue);
            line.LineWidth = 1.2f;
        }

        host.Refresh();
    }

    private void UpdateCursor(WpfPlot plot, Point point)
    {
        if (ViewModel is not { IsCursorEnabled: true } viewModel)
        {
            return;
        }

        var coordinates = plot.Plot.GetCoordinates((float)point.X, (float)point.Y);
        viewModel.UpdateCursor(coordinates.X);
    }

    private void ExportPng_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Exportar gráficos como PNG",
            FileName = $"graficos_{DateTimeOffset.Now:yyyy-MM-dd_HH-mm-ss}.png",
            Filter = "Imagem PNG (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        ChartArea.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(ChartArea.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(ChartArea.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(ChartArea);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(dialog.FileName);
        encoder.Save(stream);
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Exportar dados visíveis como CSV",
            FileName = $"graficos_{DateTimeOffset.Now:yyyy-MM-dd_HH-mm-ss}.csv",
            Filter = "CSV (*.csv)|*.csv",
            DefaultExt = ".csv",
            AddExtension = true,
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            File.WriteAllText(dialog.FileName, viewModel.BuildCsv(), new UTF8Encoding(false));
        }
    }

    private static SolidColorBrush? TryBrush(string key)
        => Application.Current?.TryFindResource(key) as SolidColorBrush;

    private static PlotColor ToPlotColor(SolidColorBrush? brush, MediaColor fallback)
    {
        var c = brush?.Color ?? fallback;
        return new PlotColor(c.R, c.G, c.B, c.A);
    }
}
