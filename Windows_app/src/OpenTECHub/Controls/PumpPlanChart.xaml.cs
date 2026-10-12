using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
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
/// The external-pump plan as two charts, one y axis each: flow Q(t) on a log scale and
/// accumulated volume V(t) (plan 2026-10-11 §5.2). Stacked by default, side by side with
/// <see cref="SideBySide"/>.
/// </summary>
/// <remarks>
/// <para>
/// Flow is drawn on a log scale so a pulse train (S 120 → 5 mL/min for seconds) and its mean
/// (0,07 mL/min) both read on the same axis; zero flow sits on the bottom decade. The plots carry
/// no legend: one caption line under them names the series, so the frame is all data.
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

    public static readonly DependencyProperty DosePreviewProperty = DependencyProperty.Register(
        nameof(DosePreview), typeof(PumpDosePreview), typeof(PumpPlanChart), new PropertyMetadata(null, OnInputsChanged));

    public static readonly DependencyProperty MeanFlowProperty = DependencyProperty.Register(
        nameof(MeanFlow), typeof(double), typeof(PumpPlanChart), new PropertyMetadata(0.0, OnInputsChanged));

    public static readonly DependencyProperty LiveMinutesProperty = DependencyProperty.Register(
        nameof(LiveMinutes), typeof(IReadOnlyList<double>), typeof(PumpPlanChart), new PropertyMetadata(null, OnInputsChanged));

    public static readonly DependencyProperty LiveFlowProperty = DependencyProperty.Register(
        nameof(LiveFlow), typeof(IReadOnlyList<double>), typeof(PumpPlanChart), new PropertyMetadata(null, OnInputsChanged));

    public static readonly DependencyProperty LiveVolumeProperty = DependencyProperty.Register(
        nameof(LiveVolume), typeof(IReadOnlyList<double>), typeof(PumpPlanChart), new PropertyMetadata(null, OnInputsChanged));

    public static readonly DependencyProperty SideBySideProperty = DependencyProperty.Register(
        nameof(SideBySide), typeof(bool), typeof(PumpPlanChart), new PropertyMetadata(false, OnLayoutChanged));

    private readonly WpfPlot _flow = new();
    private readonly WpfPlot _volume = new();

    public PumpPlanChart()
    {
        InitializeComponent();
        FlowHost.Child = _flow;
        VolumeHost.Child = _volume;
        _flow.UserInputProcessor.Disable();
        _volume.UserInputProcessor.Disable();
        ApplyLayout();

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
    /// Pulse mode: the pulse train over a detail window and the delivered-volume staircase over
    /// the whole run. When set, it is drawn instead of <see cref="Preview"/>.
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

    /// <summary>Flow and volume side by side instead of stacked.</summary>
    public bool SideBySide
    {
        get => (bool)GetValue(SideBySideProperty);
        set => SetValue(SideBySideProperty, value);
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

    private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PumpPlanChart chart)
        {
            chart.ApplyLayout();
            chart.Redraw();
        }
    }

    private void ApplyLayout()
    {
        var star = new GridLength(1, GridUnitType.Star);
        var none = new GridLength(0);
        FlowRow.Height = star;
        FlowColumn.Width = star;
        if (SideBySide)
        {
            GapRow.Height = none;
            VolumeRow.Height = none;
            GapColumn.Width = new GridLength(12);
            VolumeColumn.Width = star;
            Grid.SetRow(VolumeHost, 0);
            Grid.SetColumn(VolumeHost, 2);
        }
        else
        {
            GapRow.Height = new GridLength(8);
            VolumeRow.Height = star;
            GapColumn.Width = none;
            VolumeColumn.Width = none;
            Grid.SetRow(VolumeHost, 2);
            Grid.SetColumn(VolumeHost, 0);
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

    private static void Style(Plot plot, string yLabel, string xLabel)
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
        plot.Axes.Bottom.Label.Text = xLabel;
        plot.Legend.IsVisible = false;
    }

    /// <summary>
    /// The bottom of the log axis: one decade below the smallest positive value (and never more
    /// than four decades under the largest), so zero flow has a place to sit.
    /// </summary>
    internal static double LogFloor(IEnumerable<double> values)
    {
        var positive = values.Where(v => double.IsFinite(v) && v > 0).ToArray();
        if (positive.Length == 0)
        {
            return 0.01;
        }

        var lo = positive.Min();
        var hi = positive.Max();
        var floor = Math.Pow(10, Math.Floor(Math.Log10(lo)));
        if (lo / floor < 2.0)
        {
            floor /= 10.0;
        }

        return Math.Max(floor, hi / 1e4);
    }

    private static double[] ToLog(IEnumerable<double> values, double floor)
        => [.. values.Select(v => Math.Log10(Math.Max(double.IsFinite(v) ? v : 0.0, floor)))];

    /// <summary>Log y axis: ticks at each decade with minor ticks between, labelled in mL/min.</summary>
    private static void UseLogFlowAxis(Plot plot, double floor, double top)
    {
        plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic
        {
            MinorTickGenerator = new ScottPlot.TickGenerators.LogMinorTickGenerator(),
            IntegerTicksOnly = true,
            LabelFormatter = y => Math.Pow(10, y).ToString("G3", CultureInfo.CurrentCulture),
        };
        // Minor lines only across the flow axis: on the time axis they read as noise.
        plot.Grid.XAxisStyle.MinorLineStyle.Width = 0;
        plot.Grid.YAxisStyle.MinorLineStyle.Width = 1;
        plot.Grid.YAxisStyle.MinorLineStyle.Color = Token("StrokeSubtleBrush", new PlotColor(231, 235, 240)).WithAlpha(0.6);
        // A little below the bottom decade, so its label is drawn.
        plot.Axes.SetLimitsY(Math.Log10(floor) - 0.08, Math.Log10(Math.Max(top, floor * 10)) + 0.15);
    }

    private void ShowPlots(bool visible)
    {
        EmptyNotice.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        FlowHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        VolumeHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        KeyText.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
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
            ShowPlots(false);
            return;
        }

        ShowPlots(true);
        var minutes = preview.Minutes.ToArray();
        var xMax = minutes[^1];
        var planFlowColor = Token("ChartPvBrush", new PlotColor(37, 99, 217));
        var planVolumeColor = Token("ChartOutputBrush", new PlotColor(198, 127, 0));
        var liveColor = Token("TextPrimaryBrush", new PlotColor(30, 30, 30));
        var nowColor = Token("StrokeStrongBrush", new PlotColor(150, 150, 150));

        var live = LiveMinutes is { Count: > 0 } lm ? lm.ToArray() : null;
        double? now = live?[^1];
        var hasLiveFlow = live is not null && LiveFlow is { } lf0 && lf0.Count == live.Length;
        var hasLiveVolume = live is not null && LiveVolume is { } lv0 && lv0.Count == live.Length;

        var flowValues = preview.FlowMlPerMin.Concat(hasLiveFlow ? LiveFlow! : []).ToArray();
        var floor = LogFloor(flowValues);

        var flowPlot = _flow.Plot;
        flowPlot.Clear();
        Style(flowPlot, "Vazão (mL/min, log)", "Tempo (min)");
        var flow = flowPlot.Add.ScatterLine(minutes, ToLog(preview.FlowMlPerMin, floor));
        flow.LineWidth = 2;
        flow.Color = planFlowColor;
        if (hasLiveFlow)
        {
            var lflow = flowPlot.Add.ScatterLine(live!, ToLog(LiveFlow!, floor));
            lflow.LineWidth = 1.5f;
            lflow.Color = liveColor;
        }

        var volumePlot = _volume.Plot;
        volumePlot.Clear();
        Style(volumePlot, "Volume (mL)", "Tempo (min)");
        var volume = volumePlot.Add.ScatterLine(minutes, preview.VolumeMl.ToArray());
        volume.LineWidth = 2;
        volume.Color = planVolumeColor;
        if (hasLiveVolume)
        {
            var lvol = volumePlot.Add.ScatterLine(live!, LiveVolume!.ToArray());
            lvol.LineWidth = 1.5f;
            lvol.Color = liveColor;
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
        flowPlot.Axes.SetLimitsX(0, xMax);
        volumePlot.Axes.SetLimitsX(0, xMax);
        UseLogFlowAxis(flowPlot, floor, flowValues.Where(double.IsFinite).DefaultIfEmpty(floor).Max());
        volumePlot.Axes.SetLimitsY(0, Math.Max(preview.TotalVolumeMl, 1e-6) * 1.08);
        KeyText.Text = live is null
            ? "Azul: vazão planejada (escala log) · laranja: volume planejado"
            : "Azul / laranja: planejado · preto: medido · tracejado: agora";
        _flow.Refresh();
        _volume.Refresh();
    }

    /// <summary>
    /// Pulse mode. The two charts do not share x here: the train needs a detail window (a pulse
    /// lasts seconds), the staircase the whole run. The flow chart says so in its label.
    /// </summary>
    private void RedrawPulses(PumpDosePreview dose)
    {
        ShowPlots(true);
        var planFlowColor = Token("ChartPvBrush", new PlotColor(37, 99, 217));
        var planVolumeColor = Token("ChartOutputBrush", new PlotColor(198, 127, 0));
        var liveColor = Token("TextPrimaryBrush", new PlotColor(30, 30, 30));
        var nowColor = Token("StrokeStrongBrush", new PlotColor(150, 150, 150));

        var trainT = dose.TrainMinutes.ToArray();
        var trainQ = dose.TrainFlowMlMin.ToArray();
        var window = trainT[^1];
        var floor = LogFloor(trainQ.Append(MeanFlow));

        var flowPlot = _flow.Plot;
        flowPlot.Clear();
        Style(flowPlot, "Vazão (mL/min, log)", $"Tempo (min) — primeiros {window:0} min");
        var train = flowPlot.Add.ScatterLine(trainT, ToLog(trainQ, floor));
        train.LineWidth = 1.5f;
        train.Color = planFlowColor;
        if (MeanFlow > 0)
        {
            var mean = flowPlot.Add.HorizontalLine(Math.Log10(MeanFlow));
            mean.Color = planVolumeColor;
            mean.LinePattern = LinePattern.Dashed;
            mean.LineWidth = 1.5f;
        }

        flowPlot.Axes.SetLimitsX(0, window);
        UseLogFlowAxis(flowPlot, floor, trainQ.DefaultIfEmpty(floor).Max());

        var volT = dose.VolumeMinutes.ToArray();
        var volV = dose.VolumeMl.ToArray();
        var volumePlot = _volume.Plot;
        volumePlot.Clear();
        Style(volumePlot, "Volume (mL)", "Tempo (min)");
        var stair = volumePlot.Add.ScatterLine(volT, volV);
        stair.LineWidth = 2;
        stair.Color = planVolumeColor;

        var xMax = Math.Max(volT[^1], 1e-6);
        var yMax = Math.Max(volV.Max(), 1e-6);
        var hasLive = LiveMinutes is { Count: > 0 } lm && LiveVolume is { } lv && lv.Count == lm.Count;
        if (hasLive)
        {
            var lm2 = LiveMinutes!;
            var lv2 = LiveVolume!;
            var live = volumePlot.Add.ScatterLine(lm2.ToArray(), lv2.ToArray());
            live.LineWidth = 1.5f;
            live.Color = liveColor;
            var marker = volumePlot.Add.VerticalLine(lm2[^1]);
            marker.Color = nowColor;
            marker.LinePattern = LinePattern.Dashed;
            marker.LineWidth = 1;
            xMax = Math.Max(xMax, lm2[^1]);
            yMax = Math.Max(yMax, lv2.Max());
        }

        volumePlot.Axes.SetLimitsX(0, xMax);
        volumePlot.Axes.SetLimitsY(0, yMax * 1.08);
        KeyText.Text = $"Azul: pulsos (escala log) · laranja tracejado: média {MeanFlow.ToString("0.####", CultureInfo.CurrentCulture)} mL/min · laranja: volume planejado (escada)" +
                       (hasLive ? " · preto: entregue" : "");
        _flow.Refresh();
        _volume.Refresh();
    }
}
