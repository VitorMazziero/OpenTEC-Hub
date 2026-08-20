using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TecnalHub.Services.Persistence;
using TecnalHub.Services.Telemetry;

namespace TecnalHub.ViewModels;

/// <summary>How much history the charts show.</summary>
public sealed record ChartWindow(string Label, TimeSpan? Window)
{
    public override string ToString() => Label;
}

/// <summary>A channel offered in either panel of the dedicated graph page.</summary>
public sealed partial class ChartChannelOption(
    TelemetryChannel channel,
    string title,
    string unit,
    string seriesBrushKey) : ObservableObject
{
    public TelemetryChannel Channel { get; } = channel;

    public string Title { get; } = title;

    [ObservableProperty]
    public partial string Unit { get; set; } = unit;

    public string SeriesBrushKey { get; } = seriesBrushKey;

    public override string ToString() => Title;
}

/// <summary>
/// Backs the dedicated dual-graph page. Live telemetry and a loaded session use the
/// same two panels; browsing files remains a separate Históricos destination.
/// </summary>
public sealed partial class ChartsViewModel : ObservableObject, IDisposable
{
    private const int ExportPointLimit = 100_000;

    private readonly ISettingsService _settings;
    private UnitSettings _units;
    private SessionFileData? _loadedSession;

    public ChartsViewModel(ITelemetryHistory history, ISettingsService settings)
    {
        History = history;
        _settings = settings;
        _units = settings.Current.Units;

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
            new(TelemetryChannel.Temperature, "Temperatura", "°C", "Series1Brush"),
            new(TelemetryChannel.Oxygen, "Oxigênio dissolvido", "%", "Series2Brush"),
            new(TelemetryChannel.PH, "pH", "", "Series3Brush"),
            new(TelemetryChannel.Flow, "Vazão de ar", "L/min", "Series4Brush"),
            new(TelemetryChannel.Pressure, "Pressão", "kPa", "Series5Brush"),
            new(TelemetryChannel.MotorRpm, "Agitação (comandada)", "rpm", "Series6Brush"),
            new(TelemetryChannel.Antifoam, "Antiespumante", "", "Series1Brush"),
            new(TelemetryChannel.Distance, "Distância", "mm", "Series2Brush"),
            new(TelemetryChannel.Biomass, "Biomassa", "Abs", "Series3Brush"),
            new(TelemetryChannel.PumpFlow, "Bomba — vazão", "mL/min", "Series4Brush"),
            new(TelemetryChannel.PumpVolume, "Bomba — volume", "mL", "Series5Brush"),
        ];

        LeftChannel = Channels[0];
        RightChannel = Channels[1];
        ApplyUnits(_units);
        settings.Changed += OnSettingsChanged;
    }

    public ITelemetryHistory History { get; }

    public IReadOnlyList<ChartWindow> Windows { get; }

    public IReadOnlyList<ChartChannelOption> Channels { get; }

    [ObservableProperty]
    public partial ChartWindow SelectedWindow { get; set; }

    [ObservableProperty]
    public partial ChartChannelOption LeftChannel { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRightPanel))]
    public partial ChartChannelOption? RightChannel { get; set; }

    public bool ShowRightPanel => RightChannel is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseLabel))]
    public partial bool IsPaused { get; set; }

    public string PauseLabel => IsPaused ? "Continuar" : "Pausar";

    [ObservableProperty]
    public partial string SampleCountText { get; set; } = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CursorLabel))]
    public partial bool IsCursorEnabled { get; set; }

    [ObservableProperty]
    public partial string CursorText { get; set; } = "Cursor desligado";

    public string CursorLabel => IsCursorEnabled ? "Cursor ligado" : "Cursor";

    public double? CursorMinutes { get; private set; }

    public bool IsSessionLoaded => _loadedSession is not null;

    public string DataSourceText => _loadedSession is { } session
        ? $"Sessão: {session.Summary.Name}"
        : "Tempo real";

    public event Action? LayoutChanged;

    public void UpdateSampleCount()
    {
        if (_loadedSession is { } session)
        {
            SampleCountText = $"{session.Summary.RowCount} linhas · {session.Summary.DurationText}";
            return;
        }

        SampleCountText = string.Create(CultureInfo.CurrentCulture,
            $"{History.Count} amostras · {History.LatestMinutes:F1} min");
    }

    public ChannelSeries GetSeries(ChartChannelOption option, int maxPoints)
    {
        var canonical = _loadedSession is { } session
            ? session.GetSeries(option.Channel, SelectedWindow.Window, maxPoints)
            : History.GetSeries(option.Channel, SelectedWindow.Window, maxPoints);

        if (canonical.Count == 0 ||
            option.Channel is not (TelemetryChannel.Temperature or TelemetryChannel.Pressure))
        {
            return canonical;
        }

        var values = new double[canonical.Count];
        for (var i = 0; i < values.Length; i++)
        {
            var value = canonical.Values[i];
            values[i] = double.IsNaN(value) ? value : option.Channel switch
            {
                TelemetryChannel.Temperature => UnitConversions.TemperatureToDisplay(value, _units.Temperature),
                TelemetryChannel.Pressure => UnitConversions.PressureToDisplay(value, _units.Pressure),
                _ => value,
            };
        }

        return new ChannelSeries(canonical.Minutes, values);
    }

    public void LoadSession(SessionFileData session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _loadedSession = session;
        SelectedWindow = Windows[^1];
        // A persisted session is already immutable; keeping the live-pause flag set
        // would suppress redraws, including cursor moves and a second file load.
        IsPaused = false;
        CursorMinutes = null;
        CursorText = "Cursor desligado";
        OnPropertyChanged(nameof(IsSessionLoaded));
        OnPropertyChanged(nameof(DataSourceText));
        LayoutChanged?.Invoke();
    }

    [RelayCommand]
    private void ReturnToLive()
    {
        _loadedSession = null;
        IsPaused = false;
        CursorMinutes = null;
        CursorText = "Cursor desligado";
        OnPropertyChanged(nameof(IsSessionLoaded));
        OnPropertyChanged(nameof(DataSourceText));
        LayoutChanged?.Invoke();
    }

    public void UpdateCursor(double minutes)
    {
        if (!IsCursorEnabled)
        {
            return;
        }

        CursorMinutes = minutes;
        var fragments = new List<string> { $"{minutes:F2} min" };
        AddCursorValue(fragments, LeftChannel, minutes);
        if (RightChannel is { } right)
        {
            AddCursorValue(fragments, right, minutes);
        }

        CursorText = string.Join(" · ", fragments);
        LayoutChanged?.Invoke();
    }

    public string BuildCsv()
    {
        var left = GetSeries(LeftChannel, ExportPointLimit);
        var rightSeries = RightChannel is { } rightOption
            ? GetSeries(rightOption, ExportPointLimit)
            : ChannelSeries.Empty;
        var length = left.Count;
        var text = new StringBuilder(Math.Max(256, length * 32));

        text.Append("Time (min),\"").Append(LeftChannel.Title).Append(" (")
            .Append(LeftChannel.Unit).Append(")\"");
        if (RightChannel is { } visibleRight)
        {
            text.Append(",\"").Append(visibleRight.Title).Append(" (").Append(visibleRight.Unit).Append(")\"");
        }

        text.AppendLine();
        for (var i = 0; i < length; i++)
        {
            text.Append(left.Minutes[i].ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(FormatCsvValue(left.Values[i]));
            if (RightChannel is not null)
            {
                text.Append(',').Append(i < rightSeries.Count ? FormatCsvValue(rightSeries.Values[i]) : "");
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    [RelayCommand]
    private void TogglePause() => IsPaused = !IsPaused;

    [RelayCommand]
    private void ToggleCursor()
    {
        IsCursorEnabled = !IsCursorEnabled;
        if (!IsCursorEnabled)
        {
            CursorMinutes = null;
            CursorText = "Cursor desligado";
        }

        LayoutChanged?.Invoke();
    }

    [RelayCommand]
    private void ToggleSecondPanel()
        => RightChannel = RightChannel is null
            ? Channels.FirstOrDefault(channel => channel != LeftChannel) ?? Channels[1]
            : null;

    private void AddCursorValue(List<string> fragments, ChartChannelOption option, double minutes)
    {
        var series = GetSeries(option, 2000);
        if (series.Count == 0)
        {
            fragments.Add($"{option.Title}: —");
            return;
        }

        var index = Array.BinarySearch(series.Minutes, minutes);
        if (index < 0)
        {
            index = ~index;
            if (index >= series.Count)
            {
                index = series.Count - 1;
            }
            else if (index > 0 &&
                     Math.Abs(series.Minutes[index - 1] - minutes) < Math.Abs(series.Minutes[index] - minutes))
            {
                index--;
            }
        }

        var value = series.Values[index];
        fragments.Add(double.IsNaN(value)
            ? $"{option.Title}: —"
            : $"{option.Title}: {value:F2} {option.Unit}".TrimEnd());
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        if (settings.Units == _units)
        {
            return;
        }

        _units = settings.Units;
        ApplyUnits(_units);
        LayoutChanged?.Invoke();
    }

    private void ApplyUnits(UnitSettings units)
    {
        Channels.First(channel => channel.Channel == TelemetryChannel.Temperature).Unit =
            UnitConversions.TemperatureLabel(units.Temperature);
        Channels.First(channel => channel.Channel == TelemetryChannel.Pressure).Unit =
            UnitConversions.PressureLabel(units.Pressure);
    }

    partial void OnLeftChannelChanged(ChartChannelOption value) => LayoutChanged?.Invoke();

    partial void OnRightChannelChanged(ChartChannelOption? value) => LayoutChanged?.Invoke();

    partial void OnSelectedWindowChanged(ChartWindow value) => LayoutChanged?.Invoke();

    private static string FormatCsvValue(double value)
        => double.IsNaN(value) ? "" : value.ToString("R", CultureInfo.InvariantCulture);

    public void Dispose() => _settings.Changed -= OnSettingsChanged;
}
