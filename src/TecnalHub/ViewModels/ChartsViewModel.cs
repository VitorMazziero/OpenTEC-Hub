using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TecnalHub.Services.Telemetry;

namespace TecnalHub.ViewModels;

/// <summary>How much history the charts show.</summary>
/// <param name="Label">pt-BR label for the selector.</param>
/// <param name="Window">Null means the whole run.</param>
public sealed record ChartWindow(string Label, TimeSpan? Window)
{
    /// <summary>
    /// The label, so a plain ComboBox shows something readable.
    /// </summary>
    /// <remarks>
    /// A record's generated ToString prints the type name and every member, which is
    /// what appeared in the selector before this override.
    /// </remarks>
    public override string ToString() => Label;
}

/// <summary>A channel offered in the chart selectors.</summary>
/// <param name="Channel">Which series to read from history.</param>
/// <param name="Title">pt-BR heading.</param>
/// <param name="Unit">Y-axis label; empty for dimensionless quantities.</param>
/// <param name="SeriesBrushKey">Token key for the line colour.</param>
public sealed record ChartChannelOption(
    TelemetryChannel Channel,
    string Title,
    string Unit,
    string SeriesBrushKey)
{
    public override string ToString() => Title;
}

/// <summary>
/// Backs the charts page: which two channels are drawn, over what window.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two panels at most, side by side.</b> Five small charts fitted on screen but
/// none of them answered a question - and a bioreactor question is almost always one
/// variable against one other, not five at once. Side by side rather than stacked
/// because a trend is read along the time axis, and on a wide screen that is where
/// the pixels are.
/// </para>
/// <para>
/// The selectable set mirrors what v.6's graphs page offered, restricted to channels
/// this app can actually produce.
/// </para>
/// <para>
/// Holds no plot objects. The View owns ScottPlot entirely and pulls series from
/// <see cref="ITelemetryHistory"/> on a timer, so the ViewModel stays testable and
/// free of a plotting library.
/// </para>
/// </remarks>
public sealed partial class ChartsViewModel : ObservableObject
{
    public ChartsViewModel(ITelemetryHistory history)
    {
        History = history;

        Windows =
        [
            new ChartWindow("5 min", TimeSpan.FromMinutes(5)),
            new ChartWindow("30 min", TimeSpan.FromMinutes(30)),
            new ChartWindow("2 h", TimeSpan.FromHours(2)),
            new ChartWindow("12 h", TimeSpan.FromHours(12)),
            new ChartWindow("Tudo", null),
        ];
        SelectedWindow = Windows[1];

        Channels =
        [
            new ChartChannelOption(TelemetryChannel.Temperature, "Temperatura", "°C", "Series1Brush"),
            new ChartChannelOption(TelemetryChannel.Oxygen, "Oxigênio dissolvido", "%", "Series2Brush"),
            new ChartChannelOption(TelemetryChannel.PH, "pH", "", "Series3Brush"),
            new ChartChannelOption(TelemetryChannel.Flow, "Vazão de ar", "L/min", "Series4Brush"),
            new ChartChannelOption(TelemetryChannel.Pressure, "Pressão", "kPa", "Series5Brush"),
            new ChartChannelOption(TelemetryChannel.MotorRpm, "Agitação (comandada)", "rpm", "Series6Brush"),
            new ChartChannelOption(TelemetryChannel.Antifoam, "Antiespumante", "", "Series1Brush"),
            new ChartChannelOption(TelemetryChannel.Distance, "Distância", "mm", "Series2Brush"),
            new ChartChannelOption(TelemetryChannel.Biomass, "Biomassa", "Abs", "Series3Brush"),
            new ChartChannelOption(TelemetryChannel.PumpFlow, "Bomba — vazão", "mL/min", "Series4Brush"),
            new ChartChannelOption(TelemetryChannel.PumpVolume, "Bomba — volume", "mL", "Series5Brush"),
        ];

        // Temperature and dissolved oxygen: the pair an operator watches most.
        LeftChannel = Channels[0];
        RightChannel = Channels[1];
    }

    public ITelemetryHistory History { get; }

    public IReadOnlyList<ChartWindow> Windows { get; }

    /// <summary>Everything that can be plotted, for both selectors.</summary>
    public IReadOnlyList<ChartChannelOption> Channels { get; }

    [ObservableProperty]
    public partial ChartWindow SelectedWindow { get; set; }

    /// <summary>Channel drawn in the left panel.</summary>
    [ObservableProperty]
    public partial ChartChannelOption LeftChannel { get; set; }

    /// <summary>
    /// Channel drawn in the right panel. Null shows a single full-width chart.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRightPanel))]
    public partial ChartChannelOption? RightChannel { get; set; }

    public bool ShowRightPanel => RightChannel is not null;

    /// <summary>Pauses redraws so a chart can be read without it moving underfoot.</summary>
    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    [ObservableProperty]
    public partial string SampleCountText { get; set; } = "—";

    /// <summary>Raised when the View must rebuild its plots.</summary>
    public event Action? LayoutChanged;

    /// <summary>Refreshes the sample counter shown beside the window selector.</summary>
    public void UpdateSampleCount()
        => SampleCountText = string.Create(CultureInfo.CurrentCulture,
            $"{History.Count} amostras · {History.LatestMinutes:F1} min");

    [RelayCommand]
    private void TogglePause() => IsPaused = !IsPaused;

    /// <summary>Collapses to a single chart, or restores the second panel.</summary>
    [RelayCommand]
    private void ToggleSecondPanel()
        => RightChannel = RightChannel is null
            ? Channels.FirstOrDefault(c => c != LeftChannel) ?? Channels[1]
            : null;

    partial void OnLeftChannelChanged(ChartChannelOption value) => LayoutChanged?.Invoke();

    partial void OnRightChannelChanged(ChartChannelOption? value) => LayoutChanged?.Invoke();
}
