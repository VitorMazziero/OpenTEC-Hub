using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ScottPlot;
using ScottPlot.WPF;
using OpenTECHub.Services.Control;

using MediaColor = System.Windows.Media.Color;
using PlotColor = ScottPlot.Color;

namespace OpenTECHub.Controls;

/// <summary>
/// The external-pump plan as two stacked charts with a shared time axis: flow Q(t) on top and
/// accumulated volume V(t) below, each on its own y axis (plan 2026-10-11 §5.2).
/// </summary>
/// <remarks>
/// <para>
/// The single chart it replaces put flow and volume on two y axes; the curves crossed at a
/// point with no meaning and the scales competed. Here each quantity has its own frame.
/// </para>
/// <para>
/// <see cref="LiveMinutes"/>, <see cref="LiveFlow"/> and <see cref="LiveVolume"/> are optional:
/// when set (Phase 7) they draw what the pump actually did over the plan, with a "now" marker
/// at the last live sample.
/// </para>
/// </remarks>
public partial class PumpPlanChart : UserControl
{
    public static readonly DependencyProperty PreviewProperty = DependencyProperty.Register(
        nameof(Preview), typeof(PumpPreview), typeof(PumpPlanChart), new PropertyMetadata(null, OnInputsChanged));

    public static readonly DependencyProperty LiveMinutesProperty = DependencyProperty.Register(
        nameof(LiveMinutes), typeof(IReadOnlyList<double>), typeof(PumpPlanChart), new PropertyMetadata(null, OnInputsChanged));

    public static readonly DependencyProperty LiveFlowProperty = DependencyProperty.Register(
        nameof(LiveFlow), typeof(IReadOnlyList<double>), typeof(PumpPlanChart), new PropertyMetadata(null, OnInputsChanged));

    public static readonly DependencyProperty LiveVolumeProperty = DependencyProperty.Register(
        nameof(LiveVolume), typeof(IReadOnlyList<double>), typeof(PumpPlanChart), new PropertyMetadata(null, OnInputsChanged));

    public static readonly DependencyProperty DosePreviewProperty = DependencyProperty.Register(
        nameof(DosePreview), typeof(PumpDosePreview), typeof(PumpPlanChart), new PropertyMetadata(null, OnInputsChanged));

    public static readonly DependencyProperty MeanFlowProperty = DependencyProperty.Register(
        nameof(MeanFlow), typeof(double), typeof(PumpPlanChart), new PropertyMetadata(0.0, OnInputsChanged));

    private readonly WpfPlot _flow = new();
    private readonly WpfPlot _volume = new();

    public PumpPlanChart()
    {
        InitializeComponent();
        FlowHost.Child = _flow;
        VolumeHost.Child = _volume;
        _flow.UserInputProcessor.Disable();
        _volume.UserInputProcessor.Disable();

        Loaded += (_, _) =>
        {
            SubscribeToThemeChanges();
            Redraw();
        };
        Unloaded += (_, _) => UnsubscribeFromThemeChanges();

        // A plot drawn before layout has no size; redraw once the frame is sized (and on resize).
        SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
            {
                Redraw();
            }
        };
    }

    /// <summary>The sampled plan. Null draws the empty notice.</summary>
    public PumpPreview? Preview
    {
        get => (PumpPreview?)GetValue(PreviewProperty);
        set => SetValue(PreviewProperty, value);
    }

    /// <summary>
    /// Pulse mode: the pulse train over a detail window on top and the delivered-volume
    /// staircase over the whole run below. When set, it is drawn instead of <see cref="Preview"/>.
    /// </summary>
    public PumpDosePreview? DosePreview
    {
        get => (PumpDosePreview?)GetValue(DosePreviewProperty);
        set => SetValue(DosePreviewProperty, value);
    }

    /// <summary>Mean flow Q̄ drawn as a dashed line over the pulse train, mL/min.</summary>
    public double MeanFlow
    {
        get => (double)GetValue(MeanFlowProperty);
        set => SetValue(MeanFlowProperty, value);
    }

    /// <summary>Live sample times on the plan's clock, minutes (Phase 7).</summary>
    public IReadOnlyList<double>? LiveMinutes
    {
        get => (IReadOnlyList<double>?)GetValue(LiveMinutesProperty);
        set => SetValue(LiveMinutesProperty, value);
    }

    public IReadOnlyList<double>? LiveFlow
    {
        get => (IReadOnlyList<double>?)GetValue(LiveFlowProperty);
        set => SetValue(LiveFlowProperty, value);
    }

    public IReadOnlyList<double>? LiveVolume
    {
        get => (IReadOnlyList<double>?)GetValue(LiveVolumeProperty);
        set => SetValue(LiveVolumeProperty, value);
    }

    private static void OnInputsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Drawn as soon as the data arrives, like the calibration charts: waiting for Loaded
        // left the frame empty in a capture taken right after layout.
        if (d is PumpPlanChart chart)
        {
            chart.Redraw();
        }
    }

    private static PlotColor Token(string key, PlotColor fallback)
    {
        if (Application.Current?.TryFindResource(key) is System.Windows.Media.SolidColorBrush brush)
        {
            MediaColor c = brush.Color;
            return new PlotColor(c.R, c.G, c.B);
        }

        return fallback;
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

    private static void Style(Plot plot, string yLabel, bool showX)
    {
        var surface = Token("SurfaceCardBrush", new PlotColor(255, 255, 255));
        var grid = Token("StrokeSubtleBrush", new PlotColor(231, 235, 240));
        var text = Token("TextMutedBrush", new PlotColor(124, 135, 149));

        plot.FigureBackground.Color = surface;
        plot.DataBackground.Color = surface;
        plot.Grid.MajorLineColor = grid;
        foreach (var axis in new IAxis[] { plot.Axes.Left, plot.Axes.Bottom })
        {
            axis.TickLabelStyle.ForeColor = text;
            axis.TickLabelStyle.FontSize = 10;
            axis.FrameLineStyle.Color = grid;
            axis.Label.ForeColor = text;
            axis.Label.FontSize = 11;
        }

        plot.Axes.Right.FrameLineStyle.Width = 0;
        plot.Axes.Top.FrameLineStyle.Width = 0;
        plot.Axes.Left.Label.Text = yLabel;
        plot.Axes.Bottom.Label.Text = showX ? "Tempo (min)" : "";
        plot.Legend.IsVisible = true;
        plot.Legend.Alignment = Alignment.UpperLeft;
        plot.Legend.FontSize = 10;
        plot.Legend.BackgroundColor = surface;
        plot.Legend.FontColor = text;
        plot.Legend.OutlineColor = grid;
    }

    private void Redraw()
    {
        if (DosePreview is { } dose && dose.TrainMinutes.Count >= 2)
        {
            RedrawPulses(dose);
            return;
        }

        if (Preview is not { } preview || preview.Minutes.Count < 2)
        {
            EmptyNotice.Visibility = Visibility.Visible;
            FlowHost.Visibility = Visibility.Collapsed;
            VolumeHost.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyNotice.Visibility = Visibility.Collapsed;
        FlowHost.Visibility = Visibility.Visible;
        VolumeHost.Visibility = Visibility.Visible;

        var minutes = preview.Minutes.ToArray();
        var xMax = minutes[^1];
        var planFlowColor = Token("ChartPvBrush", new PlotColor(37, 99, 217));
        var planVolumeColor = Token("ChartOutputBrush", new PlotColor(198, 127, 0));
        var liveColor = Token("TextPrimaryBrush", new PlotColor(30, 30, 30));
        var nowColor = Token("StrokeStrongBrush", new PlotColor(150, 150, 150));

        var live = LiveMinutes is { Count: > 0 } lm ? lm.ToArray() : null;
        double? now = live?[^1];

        var flowPlot = _flow.Plot;
        flowPlot.Clear();
        Style(flowPlot, "Vazão (mL/min)", showX: false);
        var flow = flowPlot.Add.ScatterLine(minutes, preview.FlowMlPerMin.ToArray());
        flow.LineWidth = 2;
        flow.Color = planFlowColor;
        flow.LegendText = "Planejada";
        if (live is not null && LiveFlow is { } lf && lf.Count == live.Length)
        {
            var lflow = flowPlot.Add.ScatterLine(live, lf.ToArray());
            lflow.LineWidth = 1.5f;
            lflow.Color = liveColor;
            lflow.LegendText = "Medida";
        }

        var volumePlot = _volume.Plot;
        volumePlot.Clear();
        Style(volumePlot, "Volume (mL)", showX: true);
        var volume = volumePlot.Add.ScatterLine(minutes, preview.VolumeMl.ToArray());
        volume.LineWidth = 2;
        volume.Color = planVolumeColor;
        volume.LegendText = "Planejado";
        if (live is not null && LiveVolume is { } lv && lv.Count == live.Length)
        {
            var lvol = volumePlot.Add.ScatterLine(live, lv.ToArray());
            lvol.LineWidth = 1.5f;
            lvol.Color = liveColor;
            lvol.LegendText = "Entregue";
        }

        if (now is { } t)
        {
            foreach (var p in new[] { flowPlot, volumePlot })
            {
                var marker = p.Add.VerticalLine(t);
                marker.Color = nowColor;
                marker.LinePattern = LinePattern.Dashed;
                marker.LineWidth = 1;
            }
        }

        // Shared x range, so the two charts read as one timeline.
        flowPlot.Axes.AutoScale();
        volumePlot.Axes.AutoScale();
        flowPlot.Axes.SetLimitsX(0, xMax);
        volumePlot.Axes.SetLimitsX(0, xMax);
        var flowTop = Math.Max(preview.PeakFlowMlPerMin, 1e-6) * 1.15;
        flowPlot.Axes.SetLimitsY(0, flowTop);
        volumePlot.Axes.SetLimitsY(0, Math.Max(preview.TotalVolumeMl, 1e-6) * 1.08);
        _flow.Refresh();
        _volume.Refresh();
    }

    /// <summary>
    /// Pulse mode. The two charts do not share x here: the train needs a detail window (a pulse
    /// lasts seconds), the staircase the whole run. The flow chart says so in its label.
    /// </summary>
    private void RedrawPulses(PumpDosePreview dose)
    {
        EmptyNotice.Visibility = Visibility.Collapsed;
        FlowHost.Visibility = Visibility.Visible;
        VolumeHost.Visibility = Visibility.Visible;

        var planFlowColor = Token("ChartPvBrush", new PlotColor(37, 99, 217));
        var planVolumeColor = Token("ChartOutputBrush", new PlotColor(198, 127, 0));
        var liveColor = Token("TextPrimaryBrush", new PlotColor(30, 30, 30));
        var nowColor = Token("StrokeStrongBrush", new PlotColor(150, 150, 150));

        var trainT = dose.TrainMinutes.ToArray();
        var trainQ = dose.TrainFlowMlMin.ToArray();
        var window = trainT[^1];

        var flowPlot = _flow.Plot;
        flowPlot.Clear();
        Style(flowPlot, "Vazão (mL/min)", showX: true);
        flowPlot.Axes.Bottom.Label.Text = $"Tempo (min) — primeiros {window:0} min";
        var train = flowPlot.Add.ScatterLine(trainT, trainQ);
        train.LineWidth = 1.5f;
        train.Color = planFlowColor;
        train.LegendText = "Pulsos";
        if (MeanFlow > 0)
        {
            var mean = flowPlot.Add.HorizontalLine(MeanFlow);
            mean.Color = planVolumeColor;
            mean.LinePattern = LinePattern.Dashed;
            mean.LineWidth = 1;
            mean.LegendText = $"Média {MeanFlow:0.####}";
        }

        flowPlot.Axes.SetLimitsX(0, window);
        flowPlot.Axes.SetLimitsY(0, Math.Max(trainQ.Max(), 1e-6) * 1.15);

        var volT = dose.VolumeMinutes.ToArray();
        var volV = dose.VolumeMl.ToArray();
        var volumePlot = _volume.Plot;
        volumePlot.Clear();
        Style(volumePlot, "Volume (mL)", showX: true);
        var stair = volumePlot.Add.ScatterLine(volT, volV);
        stair.LineWidth = 2;
        stair.Color = planVolumeColor;
        stair.LegendText = "Planejado";

        var xMax = Math.Max(volT[^1], 1e-6);
        var yMax = Math.Max(volV.Max(), 1e-6);
        if (LiveMinutes is { Count: > 0 } lm && LiveVolume is { } lv && lv.Count == lm.Count)
        {
            var live = volumePlot.Add.ScatterLine(lm.ToArray(), lv.ToArray());
            live.LineWidth = 1.5f;
            live.Color = liveColor;
            live.LegendText = "Entregue";
            var marker = volumePlot.Add.VerticalLine(lm[^1]);
            marker.Color = nowColor;
            marker.LinePattern = LinePattern.Dashed;
            marker.LineWidth = 1;
            xMax = Math.Max(xMax, lm[^1]);
            yMax = Math.Max(yMax, lv.Max());
        }

        volumePlot.Axes.SetLimitsX(0, xMax);
        volumePlot.Axes.SetLimitsY(0, yMax * 1.08);
        _flow.Refresh();
        _volume.Refresh();
    }
}
