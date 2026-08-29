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
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ScottPlot;
using ScottPlot.WPF;
using TecnalHub.Services.Telemetry;
using TecnalHub.ViewModels;

using MediaColor = System.Windows.Media.Color;
using MediaColors = System.Windows.Media.Colors;
using PlotColor = ScottPlot.Color;
using HAlign = System.Windows.HorizontalAlignment;
using VAlign = System.Windows.VerticalAlignment;

namespace TecnalHub.Views;

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
    private readonly DispatcherTimer _redraw = new() { Interval = TimeSpan.FromSeconds(1) };

    private ChartsViewModel? _subscribed;
    private Border? _activeSubmenuTarget;

    public SynopticView()
    {
        InitializeComponent();

        LeftHost.Child = _leftPlot;
        RightHost.Child = _rightPlot;
        BottomLeftHost.Child = _bottomLeftPlot;
        BottomRightHost.Child = _bottomRightPlot;

        _redraw.Tick += (_, _) => Redraw();
        _leftPlot.MouseMove += (_, args) => UpdateCursor(_leftPlot, args.GetPosition(_leftPlot));
        _rightPlot.MouseMove += (_, args) => UpdateCursor(_rightPlot, args.GetPosition(_rightPlot));
        _bottomLeftPlot.MouseMove += (_, args) => UpdateCursor(_bottomLeftPlot, args.GetPosition(_bottomLeftPlot));
        _bottomRightPlot.MouseMove += (_, args) => UpdateCursor(_bottomRightPlot, args.GetPosition(_bottomRightPlot));

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

    private ChartsViewModel? ViewModel => (DataContext as ShellViewModel)?.Charts;

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
        var text = ToPlotColor(TryBrush("TextSecondaryBrush"), MediaColors.Gray);
        var textPrimary = ToPlotColor(TryBrush("TextPrimaryBrush"), MediaColors.Black);
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
        plot.Axes.Left.TickGenerator = new TecnalHub.Controls.ConsistentNumericTickGenerator();
        plot.Axes.Bottom.TickGenerator = new TecnalHub.Controls.ConsistentNumericTickGenerator();

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
        if (ViewModel is not { } viewModel || viewModel.IsPaused)
        {
            return;
        }

        viewModel.UpdateSampleCount();

        DrawPanel(_leftPlot, viewModel.LeftChannel, viewModel);

        if (viewModel.PanelCount >= 2 && viewModel.RightChannel is { } right)
        {
            DrawPanel(_rightPlot, right, viewModel);
        }

        if (viewModel.PanelCount >= 4)
        {
            if (viewModel.BottomLeftChannel is { } bl)
            {
                DrawPanel(_bottomLeftPlot, bl, viewModel);
            }
            if (viewModel.BottomRightChannel is { } br)
            {
                DrawPanel(_bottomRightPlot, br, viewModel);
            }
        }
    }

    private static void DrawPanel(WpfPlot host, ChartChannelOption spec, ChartsViewModel viewModel)
    {
        var series = viewModel.GetSeries(spec, MaxPointsPerPanel);

        host.Plot.Clear();

        if (series.Count >= 2)
        {
            var scatter = host.Plot.Add.Scatter(series.Minutes, series.Values);
            scatter.MarkerSize = 0;              // continuous line
            scatter.LineWidth = 2.0f;
            scatter.Color = ToPlotColor(TryBrush(spec.SeriesBrushKey), MediaColors.SteelBlue);

            host.Plot.Axes.AutoScale();
            var limits = host.Plot.Axes.GetLimits();
            var xMin = Math.Max(0, limits.Left);
            var xSpan = Math.Max(0.5, limits.Right - xMin);
            var xMax = limits.Right + (xSpan * 0.05); // slight padding on right so data doesn't clip edge
            host.Plot.Axes.SetLimitsX(xMin, xMax);
        }
        else
        {
            // Default window starting strictly at zero (no negative time)
            host.Plot.Axes.SetLimits(0, 5, 0, 10);
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
        }
    }

    private void Plot_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.StringFormat))
        {
            e.Effects = DragDropEffects.Copy;
            if (_activeSubmenuTarget == null)
            {
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
            Text = "Bomba Dosadora Externa",
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

        card.Child = stack;
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
        if (tag.Contains("Nutrientes", StringComparison.OrdinalIgnoreCase))
        {
            return viewModel.Channels.FirstOrDefault(c => c.Channel == TelemetryChannel.Antifoam);
        }
        if (tag.Contains("Antiespumante", StringComparison.OrdinalIgnoreCase))
        {
            return viewModel.Channels.FirstOrDefault(c => c.Channel == TelemetryChannel.Antifoam);
        }
        if (tag.Contains("Distância", StringComparison.OrdinalIgnoreCase))
        {
            return viewModel.Channels.FirstOrDefault(c => c.Channel == TelemetryChannel.Distance);
        }
        if (tag.Contains("Biomassa", StringComparison.OrdinalIgnoreCase))
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

