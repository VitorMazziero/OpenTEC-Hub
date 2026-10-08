using Microsoft.Extensions.DependencyInjection;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using OpenTECHub.Controls;
using ScottPlot;
using ScottPlot.WPF;
using OpenTECHub.Services.Telemetry;
using OpenTECHub.ViewModels;

using MediaColor = System.Windows.Media.Color;
using MediaColors = System.Windows.Media.Colors;
using PlotColor = ScottPlot.Color;
using HAlign = System.Windows.HorizontalAlignment;
using VAlign = System.Windows.VerticalAlignment;

namespace OpenTECHub.Views;

/// <summary>
/// Operational synoptic view of the bioreactor with horizontal parameter card rail and live process charts (1, 2, or 4 panels).
/// </summary>
public partial class SynopticView : UserControl
{
    private const int MaxPointsPerPanel = 2000;

    private readonly WpfPlot _leftPlot = new();
    private readonly WpfPlot _rightPlot = new();
    private readonly WpfPlot _bottomLeftPlot = new();
    private readonly WpfPlot _bottomRightPlot = new();
    /// <summary>
    /// Redraws only while the page is on screen (§C): the shell collapses a hidden page and never
    /// unloads it, so a Loaded-started timer kept rendering four unseen plots per second.
    /// </summary>
    private readonly VisibleRedrawTimer _redraw;

    private ChartsViewModel? _subscribed;
    private Border? _activeSubmenuTarget;

    public SynopticView()
    {
        InitializeComponent();

        LeftHost.Child = _leftPlot;
        RightHost.Child = _rightPlot;
        BottomLeftHost.Child = _bottomLeftPlot;
        BottomRightHost.Child = _bottomRightPlot;

        _redraw = new VisibleRedrawTimer(this, TimeSpan.FromSeconds(1), Redraw);
        foreach (var plot in new[] { _leftPlot, _rightPlot, _bottomLeftPlot, _bottomRightPlot })
        {
            plot.MouseMove += (_, args) =>
            {
                UpdateCursor(plot, args.GetPosition(plot));
                UpdateHover(plot, args.GetPosition(plot));
            };
            plot.MouseLeave += (_, _) => ClearHover(plot);
        }

        Loaded += (_, _) =>
        {
            SubscribeToThemeChanges();
            Attach();
            _redraw.Invalidate();
        };

        Unloaded += (_, _) =>
        {
            UnsubscribeFromThemeChanges();
            Detach();
        };

        DataContextChanged += (_, _) => Attach();
    }

    private ChartsViewModel? ViewModel => (DataContext as ShellViewModel)?.Charts;

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
            if (IsLoaded && ViewModel is { } viewModel)
            {
                ApplyAllPlotStyles(viewModel);
                Redraw();
            }
        });

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

        ApplyAllPlotStyles(viewModel);

        var count = viewModel.PanelCount;

        // Configure Grid rows & columns
        if (count == 1)
        {
            TopRow.Height = new GridLength(1, GridUnitType.Star);
            BottomRow.Height = new GridLength(0);
            LeftColumn.Width = new GridLength(1, GridUnitType.Star);
            RightColumn.Width = new GridLength(0);

            LeftDropTarget.Visibility = Visibility.Visible;
            RightDropTarget.Visibility = Visibility.Collapsed;
            BottomLeftDropTarget.Visibility = Visibility.Collapsed;
            BottomRightDropTarget.Visibility = Visibility.Collapsed;
        }
        else if (count == 2)
        {
            TopRow.Height = new GridLength(1, GridUnitType.Star);
            BottomRow.Height = new GridLength(0);
            LeftColumn.Width = new GridLength(1, GridUnitType.Star);
            RightColumn.Width = new GridLength(1, GridUnitType.Star);

            LeftDropTarget.Visibility = Visibility.Visible;
            RightDropTarget.Visibility = Visibility.Visible;
            BottomLeftDropTarget.Visibility = Visibility.Collapsed;
            BottomRightDropTarget.Visibility = Visibility.Collapsed;
        }
        else // count == 4
        {
            TopRow.Height = new GridLength(1, GridUnitType.Star);
            BottomRow.Height = new GridLength(1, GridUnitType.Star);
            LeftColumn.Width = new GridLength(1, GridUnitType.Star);
            RightColumn.Width = new GridLength(1, GridUnitType.Star);

            LeftDropTarget.Visibility = Visibility.Visible;
            RightDropTarget.Visibility = Visibility.Visible;
            BottomLeftDropTarget.Visibility = Visibility.Visible;
            BottomRightDropTarget.Visibility = Visibility.Visible;
        }

        Redraw();
    }

    private void ApplyAllPlotStyles(ChartsViewModel viewModel)
    {
        StylePlot(_leftPlot.Plot, viewModel.LeftChannel, "Gráfico 1");

        if (viewModel.RightChannel is { } right)
        {
            StylePlot(_rightPlot.Plot, right, "Gráfico 2");
        }

        if (viewModel.BottomLeftChannel is { } bl)
        {
            StylePlot(_bottomLeftPlot.Plot, bl, "Gráfico 3");
        }

        if (viewModel.BottomRightChannel is { } br)
        {
            StylePlot(_bottomRightPlot.Plot, br, "Gráfico 4");
        }
    }

    /// <summary>Applies the app's theme tokens to a plot with refined axis borders and horizontal grid lines.</summary>
    private static void StylePlot(Plot plot, ChartChannelOption spec, string defaultTitle)
    {
        var surface = ToPlotColor(TryBrush("SurfaceCardBrush"), MediaColors.White);
        var sunken = ToPlotColor(TryBrush("SurfaceSunkenBrush"), new MediaColor { R = 243, G = 246, B = 250, A = 255 });
        // Axis text is content, not chrome; the secondary token reads as dim on a dark card.
        var textPrimary = ToPlotColor(TryBrush("TextPrimaryBrush"), MediaColors.Black);
        var text = textPrimary;
        var stroke = ToPlotColor(TryBrush("StrokeDefaultBrush"), MediaColors.SlateGray);
        var grid = ToPlotColor(TryBrush("StrokeSubtleBrush"), MediaColors.LightGray);

        plot.FigureBackground.Color = surface;
        plot.DataBackground.Color = sunken;

        // Open graph: only left and bottom axis lines visible
        plot.Axes.Top.FrameLineStyle.Width = 0;
        plot.Axes.Right.FrameLineStyle.Width = 0;
        plot.Axes.Left.FrameLineStyle.Width = 1.2f;
        plot.Axes.Bottom.FrameLineStyle.Width = 1.2f;
        plot.Axes.Left.FrameLineStyle.Color = stroke;
        plot.Axes.Bottom.FrameLineStyle.Color = stroke;

        // Major ticks and removal of secondary (minor) ticks
        plot.Axes.Left.MajorTickStyle.Color = stroke;
        plot.Axes.Bottom.MajorTickStyle.Color = stroke;
        plot.Axes.Left.MajorTickStyle.Length = 4;
        plot.Axes.Bottom.MajorTickStyle.Length = 4;
        plot.Axes.Left.MinorTickStyle.Length = 0;
        plot.Axes.Bottom.MinorTickStyle.Length = 0;
        plot.Axes.Top.MajorTickStyle.Length = 0;
        plot.Axes.Top.MinorTickStyle.Length = 0;
        plot.Axes.Right.MajorTickStyle.Length = 0;
        plot.Axes.Right.MinorTickStyle.Length = 0;

        // Consistent decimal labels (e.g. 0, 0.5, 1.0, 1.5...)
        plot.Axes.Left.TickGenerator = new OpenTECHub.Controls.ConsistentNumericTickGenerator();
        plot.Axes.Bottom.TickGenerator = new OpenTECHub.Controls.ConsistentNumericTickGenerator();

        plot.Axes.Left.TickLabelStyle.ForeColor = text;
        plot.Axes.Bottom.TickLabelStyle.ForeColor = text;
        plot.Axes.Left.TickLabelStyle.FontSize = 11;
        plot.Axes.Bottom.TickLabelStyle.FontSize = 11;

        // Horizontal grid only
        plot.Grid.XAxisStyle.IsVisible = false;
        plot.Grid.YAxisStyle.IsVisible = true;
        plot.Grid.MajorLineColor = grid.WithAlpha(0.35f);

        plot.Axes.Bottom.Label.Text = "Tempo (min)";
        plot.Axes.Bottom.Label.ForeColor = text;
        plot.Axes.Bottom.Label.FontSize = 12;

        plot.Axes.Left.Label.Text = string.IsNullOrWhiteSpace(spec.Unit) ? "Valor" : spec.Unit;
        plot.Axes.Left.Label.ForeColor = text;
        plot.Axes.Left.Label.FontSize = 12;

        plot.Axes.Title.Label.Text = string.IsNullOrWhiteSpace(spec.Title) ? defaultTitle : spec.Title;
        plot.Axes.Title.Label.ForeColor = textPrimary;
        plot.Axes.Title.Label.FontSize = 14;
        plot.Axes.Title.Label.Bold = true;

        // Auto-scale margins with room top/bottom and slight right padding
        plot.Axes.Margins(bottom: 0.12, top: 0.12, left: 0.0, right: 0.06);
    }

    private void Redraw()
    {
        // Live data moves every frame, so the chart is always dirty while telemetry arrives;
        // the gate that matters here is visibility. Paused charts are left as they are.
        _redraw.MarkDirty();
        if (ViewModel is not { } viewModel || viewModel.IsPaused)
        {
            return;
        }

        viewModel.UpdateSampleCount();

        DrawPanel(_leftPlot, viewModel.LeftChannel, viewModel, HoverOf(_leftPlot));

        if (viewModel.PanelCount >= 2 && viewModel.RightChannel is { } right)
        {
            DrawPanel(_rightPlot, right, viewModel, HoverOf(_rightPlot));
        }

        if (viewModel.PanelCount >= 4)
        {
            if (viewModel.BottomLeftChannel is { } bl)
            {
                DrawPanel(_bottomLeftPlot, bl, viewModel, HoverOf(_bottomLeftPlot));
            }
            if (viewModel.BottomRightChannel is { } br)
            {
                DrawPanel(_bottomRightPlot, br, viewModel, HoverOf(_bottomRightPlot));
            }
        }
    }

    private static void DrawPanel(WpfPlot host, ChartChannelOption spec, ChartsViewModel viewModel,
        (double Minutes, double Value)? hover = null)
    {
        var series = viewModel.GetSeries(spec, MaxPointsPerPanel);
        var setpoint = viewModel.GetSetpointSeries(spec, MaxPointsPerPanel);
        var phBand = viewModel.GetPHBandSeries(spec, MaxPointsPerPanel);

        host.Plot.Clear();

        var drewData = false;

        if (series.Count >= 2)
        {
            var scatter = host.Plot.Add.Scatter(series.Minutes, series.Values);
            scatter.MarkerSize = 0;              // continuous line
            scatter.LineWidth = 2.0f;
            scatter.Color = ToPlotColor(TryBrush(spec.SeriesBrushKey), MediaColors.SteelBlue);
            drewData = true;
        }

        // The commanded setpoint, dashed and slightly muted so the measured line stays the
        // primary read. Drawn only where the loop published one; a gap (NaN) elsewhere.
        if (HasFinite(setpoint))
        {
            var line = host.Plot.Add.Scatter(setpoint.Minutes, setpoint.Values);
            line.MarkerSize = 0;
            line.LineWidth = 1.6f;
            line.LinePattern = LinePattern.Dashed;
            line.Color = ToPlotColor(TryBrush(spec.SeriesBrushKey), MediaColors.SteelBlue).WithAlpha(0.55f);
            drewData = true;
        }

        foreach (var (limit, label) in new[] { (phBand.Lower, "Histerese inferior"), (phBand.Upper, "Histerese superior") })
        {
            if (!HasFinite(limit)) continue;
            var line = host.Plot.Add.Scatter(limit.Minutes, limit.Values);
            line.MarkerSize = 0;
            line.LineWidth = 1.2f;
            line.LinePattern = LinePattern.Dotted;
            line.LegendText = label;
            line.Color = ToPlotColor(TryBrush(spec.SeriesBrushKey), MediaColors.SteelBlue).WithAlpha(0.45f);
            drewData = true;
        }

        var latestMinutes = viewModel.History.LatestMinutes;
        if (series.Count > 0 && series.Minutes[^1] > latestMinutes)
        {
            latestMinutes = series.Minutes[^1];
        }

        double xMin, xMax;
        if (viewModel.SelectedWindow?.Window is { } span)
        {
            var spanMinutes = span.TotalMinutes;
            if (latestMinutes < spanMinutes)
            {
                xMin = 0.0;
                xMax = spanMinutes;
            }
            else
            {
                xMin = Math.Max(0.0, latestMinutes - spanMinutes);
                xMax = latestMinutes + (spanMinutes * 0.02);
            }
        }
        else
        {
            xMin = 0.0;
            xMax = Math.Max(5.0, latestMinutes * 1.05);
        }

        if (drewData)
        {
            host.Plot.Axes.AutoScale();
            host.Plot.Axes.SetLimitsX(xMin, xMax);
        }
        else
        {
            host.Plot.Axes.SetLimits(xMin, xMax, 0, 10);
        }

        if (viewModel.IsCursorEnabled && viewModel.CursorMinutes is { } cursor)
        {
            var line = host.Plot.Add.VerticalLine(cursor);
            line.Color = ToPlotColor(TryBrush("AccentBrush"), MediaColors.SteelBlue);
            line.LineWidth = 1.2f;

            if (viewModel.GetValueAt(spec, cursor) is { } pt)
            {
                var marker = host.Plot.Add.Marker(pt.Minutes, pt.Value);
                marker.Color = ToPlotColor(TryBrush(spec.SeriesBrushKey), MediaColors.SteelBlue);
                marker.Size = 7;
                marker.Shape = MarkerShape.FilledCircle;

                var unitStr = string.IsNullOrWhiteSpace(spec.Unit) ? "" : $" {spec.Unit}";
                var anno = host.Plot.Add.Annotation($"t: {pt.Minutes:F1} min\n{pt.Value:F2}{unitStr}", Alignment.UpperRight);
                anno.LabelFontSize = 11;
                anno.LabelBold = true;
                anno.LabelFontColor = ToPlotColor(TryBrush("TextPrimaryBrush"), MediaColors.Black);
                anno.LabelBackgroundColor = ToPlotColor(TryBrush("SurfaceCardBrush"), MediaColors.White).WithAlpha(0.92f);
                anno.LabelBorderColor = ToPlotColor(TryBrush("StrokeDefaultBrush"), MediaColors.SlateGray);
                anno.LabelBorderWidth = 1;
                anno.LabelPadding = 4;
            }
        }

        if (hover is { } point)
        {
            // Hover readout: value and time in hours, right next to the point under the mouse.
            var color = ToPlotColor(TryBrush(spec.SeriesBrushKey), MediaColors.SteelBlue);
            var marker = host.Plot.Add.Marker(point.Minutes, point.Value);
            marker.Color = color;
            marker.Size = 6;
            marker.Shape = MarkerShape.FilledCircle;
            var unit = string.IsNullOrWhiteSpace(spec.Unit) ? "" : $" {spec.Unit}";
            var label = host.Plot.Add.Text($"{FormatHoverValue(point.Value)}{unit} · {point.Minutes / 60:0.00} h",
                point.Minutes, point.Value);
            label.LabelFontSize = 11;
            label.LabelFontColor = ToPlotColor(TryBrush("TextPrimaryBrush"), MediaColors.Black);
            label.LabelBackgroundColor = ToPlotColor(TryBrush("SurfaceCardBrush"), MediaColors.White).WithAlpha(0.9f);
            label.LabelBorderColor = color.WithAlpha(0.6f);
            label.LabelBorderWidth = 1;
            label.LabelPadding = 3;
            label.OffsetX = 8;
            label.OffsetY = -8;
            label.LabelAlignment = Alignment.LowerLeft;
        }

        host.Refresh();
    }

    private readonly Dictionary<WpfPlot, (double Minutes, double Value)> _hover = [];

    private (double Minutes, double Value)? HoverOf(WpfPlot plot)
        => _hover.TryGetValue(plot, out var point) ? point : null;

    private ChartChannelOption? SpecFor(WpfPlot plot, ChartsViewModel viewModel)
        => ReferenceEquals(plot, _leftPlot) ? viewModel.LeftChannel
            : ReferenceEquals(plot, _rightPlot) ? viewModel.RightChannel
            : ReferenceEquals(plot, _bottomLeftPlot) ? viewModel.BottomLeftChannel
            : ReferenceEquals(plot, _bottomRightPlot) ? viewModel.BottomRightChannel : null;

    private static string FormatHoverValue(double value)
        => Math.Abs(value) >= 100 ? value.ToString("0", System.Globalization.CultureInfo.CurrentCulture)
            : Math.Abs(value) >= 10 ? value.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture)
            : value.ToString("0.00", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>Shows the readout only when the mouse is near the line, not anywhere in the panel.</summary>
    private void UpdateHover(WpfPlot plot, Point point)
    {
        if (ViewModel is not { } viewModel || SpecFor(plot, viewModel) is not { } spec)
        {
            return;
        }

        var coordinates = plot.Plot.GetCoordinates((float)point.X, (float)point.Y);
        (double Minutes, double Value)? hit = null;
        if (viewModel.GetValueAt(spec, coordinates.X) is { } nearest)
        {
            var pixel = plot.Plot.GetPixel(new Coordinates(nearest.Minutes, nearest.Value));
            if (Math.Abs(pixel.X - point.X) <= HoverRadiusPixels && Math.Abs(pixel.Y - point.Y) <= HoverRadiusPixels)
            {
                hit = nearest;
            }
        }

        var had = HoverOf(plot);
        if (hit == had) return;
        if (hit is { } value) _hover[plot] = value; else _hover.Remove(plot);
        DrawPanel(plot, spec, viewModel, hit);
    }

    private void ClearHover(WpfPlot plot)
    {
        if (!_hover.Remove(plot) || ViewModel is not { } viewModel || SpecFor(plot, viewModel) is not { } spec) return;
        DrawPanel(plot, spec, viewModel);
    }

    private const double HoverRadiusPixels = 24;

    private void UpdateCursor(WpfPlot plot, Point point)
    {
        if (ViewModel is not { IsCursorEnabled: true } viewModel)
        {
            return;
        }

        var coordinates = plot.Plot.GetCoordinates((float)point.X, (float)point.Y);
        viewModel.UpdateCursor(coordinates.X);
        Redraw();
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
            RestoreDirectory = true,
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

    /// <summary>True when a series has at least one real point to draw (not all gaps).</summary>
    private static bool HasFinite(ChannelSeries series)
    {
        for (var i = 0; i < series.Count; i++)
        {
            if (!double.IsNaN(series.Values[i]))
            {
                return true;
            }
        }

        return false;
    }

    // ── Mouse Wheel Scroll for Top Cards ─────────────────────────────────────

    private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer scv)
        {
            scv.ScrollToHorizontalOffset(scv.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
    }

    // ── Drag & Drop Interactions ─────────────────────────────────────────────

    private void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && sender is FrameworkElement card && card.Tag is string tag)
        {
            DragDrop.DoDragDrop(card, tag, DragDropEffects.Copy);

            // DoDragDrop returns only once the drag has ended (dropped or cancelled). A cue
            // still lit on a panel the pointer merely crossed on the way to the drop is now
            // stale — WPF can swallow that panel's DragLeave — so clear every default cue.
            // A panel that opened a sub-menu on drop keeps its overlay.
            ClearDefaultDropCues();
        }
    }

    private void Plot_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.StringFormat))
        {
            e.Effects = DragDropEffects.Copy;
            if (_activeSubmenuTarget == null)
            {
                // Only the panel under the pointer shows the cue; drop any left lit on a
                // panel the drag has since left, so two are never shown at once.
                ClearDefaultDropCues(sender as Border);
                ShowDefaultDropCue(sender as Border, true);
            }
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private void Plot_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.StringFormat))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private void Plot_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            var pos = e.GetPosition(fe);
            if (pos.X >= 0 && pos.Y >= 0 && pos.X <= fe.ActualWidth && pos.Y <= fe.ActualHeight)
            {
                return;
            }
        }

        if (_activeSubmenuTarget == null)
        {
            ShowDefaultDropCue(sender as Border, false);
        }
    }

    private void Plot_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Border dropTarget || !e.Data.GetDataPresent(DataFormats.StringFormat) || ViewModel is not { } viewModel)
        {
            return;
        }

        var tag = (string)e.Data.GetData(DataFormats.StringFormat);
        var panelIndex = GetPanelIndex(dropTarget);

        if (tag.Contains("Bomba", StringComparison.OrdinalIgnoreCase))
        {
            ShowPumpSubMenu(dropTarget, panelIndex);
            return;
        }

        if (tag.Contains("Agitação", StringComparison.OrdinalIgnoreCase))
        {
            ShowAgitationSubMenu(dropTarget, panelIndex);
            return;
        }

        if (tag.Contains("Oxigênio", StringComparison.OrdinalIgnoreCase))
        {
            var isOxygenControlActive = (DataContext as ShellViewModel)?.IsOxygenControlActive ?? false;
            if (isOxygenControlActive)
            {
                ShowOxygenSubMenu(dropTarget, panelIndex);
                return;
            }

            // Direct assignment if cascade control is not active
            ShowDefaultDropCue(dropTarget, false);
            SetPanelChannel(panelIndex, TelemetryChannel.Oxygen);
            return;
        }

        // Direct channel resolution for other cards
        ShowDefaultDropCue(dropTarget, false);
        var channelOption = ResolveChannel(tag, viewModel);
        if (channelOption != null)
        {
            SetPanelChannel(panelIndex, channelOption);
        }
    }

    private int GetPanelIndex(Border dropTarget)
    {
        if (dropTarget == LeftDropTarget)
        {
            return 1;
        }
        if (dropTarget == RightDropTarget)
        {
            return 2;
        }
        if (dropTarget == BottomLeftDropTarget)
        {
            return 3;
        }
        if (dropTarget == BottomRightDropTarget)
        {
            return 4;
        }
        return 1;
    }

    private void SetPanelChannel(int panelIndex, ChartChannelOption option)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        switch (panelIndex)
        {
            case 1:
                viewModel.LeftChannel = option;
                break;
            case 2:
                viewModel.RightChannel = option;
                break;
            case 3:
                viewModel.BottomLeftChannel = option;
                break;
            case 4:
                viewModel.BottomRightChannel = option;
                break;
        }

        OnLayoutChanged();
    }

    private void SetPanelChannel(int panelIndex, TelemetryChannel channel)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var match = viewModel.Channels.FirstOrDefault(c => c.Channel == channel);
        if (match != null)
        {
            SetPanelChannel(panelIndex, match);
        }
    }

    /// <summary>
    /// Hides the "Solte para visualizar" cue on every panel except <paramref name="except"/>.
    /// </summary>
    /// <remarks>
    /// The cue is lit per panel on <see cref="Plot_DragEnter"/> and is meant to clear on the
    /// matching leave, but a drag that crosses one panel on the way to another can leave that
    /// crossing's DragLeave unbalanced (WPF reports the pointer still inside the panel as it
    /// moves onto the child plot), so the cue stays lit after the drop. Clearing the others
    /// whenever a panel lights its cue, and once more when the drag ends, keeps at most one
    /// cue shown and leaves none stuck. A panel showing a drop sub-menu owns its overlay and
    /// is skipped.
    /// </remarks>
    private void ClearDefaultDropCues(Border? except = null)
    {
        foreach (var target in new[] { LeftDropTarget, RightDropTarget, BottomLeftDropTarget, BottomRightDropTarget })
        {
            if (target == except || target == _activeSubmenuTarget)
            {
                continue;
            }

            ShowDefaultDropCue(target, false);
        }
    }

    private void ShowDefaultDropCue(Border? dropTarget, bool visible)
    {
        if (dropTarget == null)
        {
            return;
        }

        var (overlay, presenter) = GetOverlayElements(dropTarget);
        if (overlay == null || presenter == null)
        {
            return;
        }

        if (!visible)
        {
            overlay.IsHitTestVisible = false;
            overlay.Visibility = Visibility.Collapsed;
            presenter.Content = null;
            return;
        }

        var panelName = dropTarget == LeftDropTarget ? "Gráfico 1" :
                        dropTarget == RightDropTarget ? "Gráfico 2" :
                        dropTarget == BottomLeftDropTarget ? "Gráfico 3" : "Gráfico 4";

        var panel = new StackPanel
        {
            HorizontalAlignment = HAlign.Center,
            VerticalAlignment = VAlign.Center,
            IsHitTestVisible = false
        };

        panel.Children.Add(new TextBlock
        {
            Text = $"Solte para visualizar no {panelName}",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = TryBrush("TextPrimaryBrush") ?? Brushes.Black,
            HorizontalAlignment = HAlign.Center
        });

        panel.Children.Add(new TextBlock
        {
            Text = "Arraste um parâmetro da barra superior",
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = TryBrush("TextSecondaryBrush") ?? Brushes.Gray,
            HorizontalAlignment = HAlign.Center
        });

        presenter.Content = panel;
        overlay.IsHitTestVisible = false;
        overlay.Visibility = Visibility.Visible;
    }

    private void ShowPumpSubMenu(Border dropTarget, int panelIndex)
    {
        var (overlay, presenter) = GetOverlayElements(dropTarget);
        if (overlay == null || presenter == null)
        {
            return;
        }

        _activeSubmenuTarget = dropTarget;

        var card = new Border
        {
            Padding = new Thickness(16, 12, 16, 12),
            Background = TryBrush("SurfaceSunkenBrush") ?? Brushes.WhiteSmoke,
            BorderBrush = TryBrush("StrokeDefaultBrush") ?? Brushes.LightGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            MaxWidth = 260
        };

        var stack = new StackPanel { HorizontalAlignment = HAlign.Stretch };

        stack.Children.Add(new TextBlock
        {
            Text = DeviceNames.ExternalPump,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = TryBrush("TextPrimaryBrush") ?? Brushes.Black,
            HorizontalAlignment = HAlign.Center
        });

        stack.Children.Add(new TextBlock
        {
            Text = "Selecione a série para este gráfico:",
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 10),
            Foreground = TryBrush("TextSecondaryBrush") ?? Brushes.Gray,
            HorizontalAlignment = HAlign.Center
        });

        var btnFlow = new Button
        {
            Content = "Vazão da Bomba (mL/min)",
            Margin = new Thickness(0, 0, 0, 6),
            Padding = new Thickness(12, 6, 12, 6),
            Cursor = Cursors.Hand
        };
        btnFlow.Click += (_, _) =>
        {
            DismissSubMenu(dropTarget);
            SetPanelChannel(panelIndex, TelemetryChannel.PumpFlow);
        };
        stack.Children.Add(btnFlow);

        var btnVolume = new Button
        {
            Content = "Volume Acumulado (mL)",
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(12, 6, 12, 6),
            Cursor = Cursors.Hand
        };
        btnVolume.Click += (_, _) =>
        {
            DismissSubMenu(dropTarget);
            SetPanelChannel(panelIndex, TelemetryChannel.PumpVolume);
        };
        stack.Children.Add(btnVolume);

        var btnCancel = new Button
        {
            Content = "Cancelar",
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = TryBrush("TextMutedBrush") ?? Brushes.Gray,
            Cursor = Cursors.Hand,
            HorizontalAlignment = HAlign.Center
        };
        btnCancel.Click += (_, _) => DismissSubMenu(dropTarget);
        stack.Children.Add(btnCancel);

        card.Child = stack;
        presenter.Content = card;
        overlay.IsHitTestVisible = true;
        overlay.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// The series behind the agitation card, offered when it is dropped on a plot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Agitation is the one card backed by more than one instrument. The commanded speed
    /// comes from this application; everything else is measured on the shaft by the ASDA-B2
    /// node, and each answers a different question - is it turning at the right speed, is it
    /// working hard to get there, how much energy has that cost.
    /// </para>
    /// <para>
    /// Offered rather than guessed at, exactly like the pump's two. Dropping the card and
    /// silently picking one of seven would be picking wrong six times out of seven.
    /// </para>
    /// </remarks>
    private void ShowAgitationSubMenu(Border dropTarget, int panelIndex)
    {
        var (overlay, presenter) = GetOverlayElements(dropTarget);
        if (overlay == null || presenter == null)
        {
            return;
        }

        _activeSubmenuTarget = dropTarget;

        var card = new Border
        {
            Padding = new Thickness(16, 12, 16, 12),
            Background = TryBrush("SurfaceSunkenBrush") ?? Brushes.WhiteSmoke,
            BorderBrush = TryBrush("StrokeDefaultBrush") ?? Brushes.LightGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            MaxWidth = 280
        };

        var stack = new StackPanel { HorizontalAlignment = HAlign.Stretch };

        stack.Children.Add(new TextBlock
        {
            Text = "Agitação",
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = TryBrush("TextPrimaryBrush") ?? Brushes.Black,
            HorizontalAlignment = HAlign.Center
        });

        stack.Children.Add(new TextBlock
        {
            Text = "Selecione a série para este gráfico:",
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 10),
            Foreground = TryBrush("TextSecondaryBrush") ?? Brushes.Gray,
            HorizontalAlignment = HAlign.Center
        });

        // Measured first: it is what the shaft is doing, and it is the reading the card
        // itself shows. The commanded figure is second because it is what was asked for -
        // useful beside the measurement, misleading in place of it.
        AddSeriesButton(stack, dropTarget, panelIndex, "Rotação medida (rpm)", TelemetryChannel.ServoRpm);
        AddSeriesButton(stack, dropTarget, panelIndex, "Rotação comandada (rpm)", TelemetryChannel.MotorRpm);
        AddSeriesButton(stack, dropTarget, panelIndex, "Torque (%)", TelemetryChannel.ServoTorquePct);
        AddSeriesButton(stack, dropTarget, panelIndex, "Torque (N·m)", TelemetryChannel.ServoTorqueNm);
        AddSeriesButton(stack, dropTarget, panelIndex, "Carga média (%)", TelemetryChannel.ServoLoadPct);
        AddSeriesButton(stack, dropTarget, panelIndex, "Potência mecânica estimada (W)", TelemetryChannel.ServoPowerW);
        AddSeriesButton(stack, dropTarget, panelIndex, "Energia acumulada (Wh)", TelemetryChannel.ServoEnergyWh);

        var btnCancel = new Button
        {
            Content = "Cancelar",
            Margin = new Thickness(0, 4, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = TryBrush("TextMutedBrush") ?? Brushes.Gray,
            Cursor = Cursors.Hand,
            HorizontalAlignment = HAlign.Center
        };
        btnCancel.Click += (_, _) => DismissSubMenu(dropTarget);
        stack.Children.Add(btnCancel);

        SetSubMenuContent(card, dropTarget, stack);
        presenter.Content = card;
        overlay.IsHitTestVisible = true;
        overlay.Visibility = Visibility.Visible;
    }

    /// <summary>One series button in a drop sub-menu.</summary>
    private void AddSeriesButton(
        StackPanel stack, Border dropTarget, int panelIndex, string label, TelemetryChannel channel)
    {
        var button = new Button
        {
            Content = label,
            Margin = new Thickness(0, 0, 0, 5),
            Padding = new Thickness(12, 5, 12, 5),
            HorizontalContentAlignment = HAlign.Left,
            Cursor = Cursors.Hand
        };

        button.Click += (_, _) =>
        {
            DismissSubMenu(dropTarget);
            SetPanelChannel(panelIndex, channel);
        };

        stack.Children.Add(button);
    }

    private void ShowOxygenSubMenu(Border dropTarget, int panelIndex)
    {
        var (overlay, presenter) = GetOverlayElements(dropTarget);
        if (overlay == null || presenter == null)
        {
            return;
        }

        _activeSubmenuTarget = dropTarget;

        var card = new Border
        {
            Padding = new Thickness(16, 12, 16, 12),
            Background = TryBrush("SurfaceSunkenBrush") ?? Brushes.WhiteSmoke,
            BorderBrush = TryBrush("StrokeDefaultBrush") ?? Brushes.LightGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            MaxWidth = 290
        };

        var stack = new StackPanel { HorizontalAlignment = HAlign.Stretch };

        stack.Children.Add(new TextBlock
        {
            Text = "Oxigênio & Controle de O₂",
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = TryBrush("TextPrimaryBrush") ?? Brushes.Black,
            HorizontalAlignment = HAlign.Center
        });

        stack.Children.Add(new TextBlock
        {
            Text = "Selecione a série para este gráfico:",
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 10),
            Foreground = TryBrush("TextSecondaryBrush") ?? Brushes.Gray,
            HorizontalAlignment = HAlign.Center
        });

        // Dissolved Oxygen main button
        var btnDo = new Button
        {
            Content = "Oxigênio Dissolvido (%)",
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(12, 6, 12, 6),
            Background = TryBrush("AccentBrush") ?? Brushes.SteelBlue,
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand
        };
        btnDo.Click += (_, _) =>
        {
            DismissSubMenu(dropTarget);
            SetPanelChannel(panelIndex, TelemetryChannel.Oxygen);
        };
        stack.Children.Add(btnDo);

        stack.Children.Add(new TextBlock
        {
            Text = "Variáveis de Controle de O₂:",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 2, 0, 4),
            Foreground = TryBrush("TextSecondaryBrush") ?? Brushes.Gray
        });

        var cascadeGrid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, 0, 8) };

        AddCascadeButton(cascadeGrid, "Saída PID (%)", TelemetryChannel.CascadeEffort, dropTarget, panelIndex);
        AddCascadeButton(cascadeGrid, "O₂ Predito (%)", TelemetryChannel.CascadePredictedO2, dropTarget, panelIndex);
        AddCascadeButton(cascadeGrid, "SP Taxa (%/s)", TelemetryChannel.CascadeRateSetpoint, dropTarget, panelIndex);
        AddCascadeButton(cascadeGrid, "Taxa Medida (%/s)", TelemetryChannel.CascadeRateMeasured, dropTarget, panelIndex);
        AddCascadeButton(cascadeGrid, "Demanda kLa (h⁻¹)", TelemetryChannel.CascadeKlaDemand, dropTarget, panelIndex);

        stack.Children.Add(cascadeGrid);

        var btnCancel = new Button
        {
            Content = "Cancelar",
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = TryBrush("TextMutedBrush") ?? Brushes.Gray,
            Cursor = Cursors.Hand,
            HorizontalAlignment = HAlign.Center
        };
        btnCancel.Click += (_, _) => DismissSubMenu(dropTarget);
        stack.Children.Add(btnCancel);

        SetSubMenuContent(card, dropTarget, stack);
        presenter.Content = card;
        overlay.IsHitTestVisible = true;
        overlay.Visibility = Visibility.Visible;
    }

    private void AddCascadeButton(UniformGrid grid, string title, TelemetryChannel channel, Border dropTarget, int panelIndex)
    {
        var btn = new Button
        {
            Content = title,
            Margin = new Thickness(2),
            Padding = new Thickness(6, 4, 6, 4),
            FontSize = 11,
            Cursor = Cursors.Hand
        };
        btn.Click += (_, _) =>
        {
            DismissSubMenu(dropTarget);
            SetPanelChannel(panelIndex, channel);
        };
        grid.Children.Add(btn);
    }

    private void DismissSubMenu(Border dropTarget)
    {
        _activeSubmenuTarget = null;
        var (overlay, presenter) = GetOverlayElements(dropTarget);
        if (overlay != null && presenter != null)
        {
            overlay.IsHitTestVisible = false;
            overlay.Visibility = Visibility.Collapsed;
            presenter.Content = null;
        }
    }

    /// <summary>
    /// Hosts a drop sub-menu's content so a panel too short to show every option scrolls
    /// to the rest instead of clipping them.
    /// </summary>
    /// <remarks>
    /// The four-panel layout leaves each plot short enough that the agitation menu's seven
    /// series ran off the bottom, taking Cancelar with them. Capping the card to the panel
    /// height gives the ScrollViewer a bound to scroll within; a comfortable floor keeps it
    /// usable even on a very short panel.
    /// </remarks>
    private void SetSubMenuContent(Border card, Border dropTarget, UIElement content)
    {
        card.Child = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = content,
        };

        card.MaxHeight = Math.Max(160, dropTarget.ActualHeight - 8);
    }

    private (Border? Overlay, ContentPresenter? Presenter) GetOverlayElements(Border dropTarget)
    {
        if (dropTarget == LeftDropTarget)
        {
            return (LeftOverlay, LeftOverlayContent);
        }
        if (dropTarget == RightDropTarget)
        {
            return (RightOverlay, RightOverlayContent);
        }
        if (dropTarget == BottomLeftDropTarget)
        {
            return (BottomLeftOverlay, BottomLeftOverlayContent);
        }
        if (dropTarget == BottomRightDropTarget)
        {
            return (BottomRightOverlay, BottomRightOverlayContent);
        }
        return (null, null);
    }

    private static ChartChannelOption? ResolveChannel(string tag, ChartsViewModel viewModel)
    {
        var match = viewModel.Channels.FirstOrDefault(c => string.Equals(c.Title, tag, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            return match;
        }

        if (tag.Contains("Agitação", StringComparison.OrdinalIgnoreCase))
        {
            return viewModel.Channels.FirstOrDefault(c => c.Channel == TelemetryChannel.MotorRpm);
        }
        if (tag.Contains("Temperatura", StringComparison.OrdinalIgnoreCase))
        {
            return viewModel.Channels.FirstOrDefault(c => c.Channel == TelemetryChannel.Temperature);
        }
        if (tag.Contains("pH", StringComparison.OrdinalIgnoreCase))
        {
            return viewModel.Channels.FirstOrDefault(c => c.Channel == TelemetryChannel.PH);
        }
        if (tag.Contains("Oxigênio", StringComparison.OrdinalIgnoreCase))
        {
            return viewModel.Channels.FirstOrDefault(c => c.Channel == TelemetryChannel.Oxygen);
        }
        if (tag.Contains("Pressão", StringComparison.OrdinalIgnoreCase))
        {
            return viewModel.Channels.FirstOrDefault(c => c.Channel == TelemetryChannel.Pressure);
        }
        if (tag.Contains("Vazão", StringComparison.OrdinalIgnoreCase))
        {
            return viewModel.Channels.FirstOrDefault(c => c.Channel == TelemetryChannel.Flow);
        }
        // Nutriente is commanded-only — the device reports nothing back — so its series is
        // the duty cycle the app asked for, recorded per frame (a flat zero while off). It
        // used to return null here, so dragging Nutrientes onto a plot drew no series at all.
        if (tag.Contains("Nutrientes", StringComparison.OrdinalIgnoreCase))
        {
            return viewModel.Channels.FirstOrDefault(c => c.Channel == TelemetryChannel.Nutrient);
        }
        if (tag.Contains("Antiespumante", StringComparison.OrdinalIgnoreCase))
        {
            return viewModel.Channels.FirstOrDefault(c => c.Channel == TelemetryChannel.Antifoam);
        }
        if (tag.Contains("Distância", StringComparison.OrdinalIgnoreCase))
        {
            return viewModel.Channels.FirstOrDefault(c => c.Channel == TelemetryChannel.Distance);
        }
        if (tag.Contains("Absorbância", StringComparison.OrdinalIgnoreCase) ||
            tag.Contains("Biomassa", StringComparison.OrdinalIgnoreCase))
        {
            return viewModel.Channels.FirstOrDefault(c => c.Channel == TelemetryChannel.Biomass);
        }
        if (tag.Contains("Bomba", StringComparison.OrdinalIgnoreCase))
        {
            return viewModel.Channels.FirstOrDefault(c => c.Channel == TelemetryChannel.PumpFlow);
        }

        return viewModel.Channels.FirstOrDefault(c => c.Title.Contains(tag, StringComparison.OrdinalIgnoreCase) ||
                                                      tag.Contains(c.Title, StringComparison.OrdinalIgnoreCase));
    }
}

