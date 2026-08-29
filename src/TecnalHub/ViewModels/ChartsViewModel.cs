using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Dialogs;
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
    private readonly IDeviceService? _device;
    private readonly ISessionLogger? _sessionLogger;
    private readonly IEventJournal? _journal;
    private readonly IDialogService? _dialogs;
    private UnitSettings _units;
    private SessionFileData? _loadedSession;

    public ChartsViewModel(
        ITelemetryHistory history,
        ISettingsService settings,
        IDeviceService? device = null,
        ISessionLogger? sessionLogger = null,
        IEventJournal? journal = null,
        IDialogService? dialogs = null)
    {
        History = history;
        _settings = settings;
        _device = device;
        _sessionLogger = sessionLogger;
        _journal = journal;
        _dialogs = dialogs;
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
            new(TelemetryChannel.CascadeEffort, "Controle O₂ — Saída PID", "%", "Series1Brush"),
            new(TelemetryChannel.CascadePredictedO2, "Controle O₂ — O₂ Predito", "%", "Series2Brush"),
            new(TelemetryChannel.CascadeRateSetpoint, "Controle O₂ — SP de Taxa", "%/s", "Series3Brush"),
            new(TelemetryChannel.CascadeRateMeasured, "Controle O₂ — Taxa Medida", "%/s", "Series4Brush"),
            new(TelemetryChannel.CascadeKlaDemand, "Controle O₂ — Demanda kLa", "h⁻¹", "Series5Brush"),
        ];

        LeftChannel = Channels[0];
        RightChannel = Channels[1];
        BottomLeftChannel = Channels[2]; // pH
        BottomRightChannel = Channels[3]; // Vazão
        ApplyUnits(_units);
        settings.Changed += OnSettingsChanged;
    }

    public ITelemetryHistory History { get; }

    public IReadOnlyList<ChartWindow> Windows { get; }

    public IReadOnlyList<ChartChannelOption> Channels { get; }

    [ObservableProperty]
    public partial ChartWindow SelectedWindow { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRightPanel))]
    [NotifyPropertyChangedFor(nameof(ShowBottomPanels))]
    [NotifyPropertyChangedFor(nameof(PanelModeLabel))]
    public partial int PanelCount { get; set; } = 2;

    public bool ShowRightPanel => PanelCount >= 2;

    public bool ShowBottomPanels => PanelCount == 4;

    public string PanelModeLabel => PanelCount switch
    {
        1 => "1 Gráfico",
        2 => "2 Gráficos",
        4 => "4 Gráficos",
        _ => "1 / 2 / 4",
    };

    [ObservableProperty]
    public partial ChartChannelOption LeftChannel { get; set; }

    [ObservableProperty]
    public partial ChartChannelOption? RightChannel { get; set; }

    [ObservableProperty]
    public partial ChartChannelOption? BottomLeftChannel { get; set; }

    [ObservableProperty]
    public partial ChartChannelOption? BottomRightChannel { get; set; }

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
        if (PanelCount >= 2 && RightChannel is { } right)
        {
            AddCursorValue(fragments, right, minutes);
        }
        if (PanelCount >= 4)
        {
            if (BottomLeftChannel is { } bl)
            {
                AddCursorValue(fragments, bl, minutes);
            }
            if (BottomRightChannel is { } br)
            {
                AddCursorValue(fragments, br, minutes);
            }
        }

        CursorText = string.Join(" · ", fragments);
        LayoutChanged?.Invoke();
    }

    public string BuildCsv()
    {
        var channelsToExport = new List<ChartChannelOption> { LeftChannel };
        if (PanelCount >= 2 && RightChannel is not null)
        {
            channelsToExport.Add(RightChannel);
        }
        if (PanelCount >= 4)
        {
            if (BottomLeftChannel is not null)
            {
                channelsToExport.Add(BottomLeftChannel);
            }
            if (BottomRightChannel is not null)
            {
                channelsToExport.Add(BottomRightChannel);
            }
        }

        var seriesList = channelsToExport.Select(c => GetSeries(c, ExportPointLimit)).ToList();
        var maxLen = seriesList.Count > 0 ? seriesList.Max(s => s.Count) : 0;
        var timeSeries = seriesList.Count > 0 ? seriesList.First(s => s.Count == maxLen) : ChannelSeries.Empty;

        var text = new StringBuilder(Math.Max(256, maxLen * 32));
        text.Append("Time (min)");
        foreach (var c in channelsToExport)
        {
            text.Append(",\"").Append(c.Title).Append(" (").Append(c.Unit).Append(")\"");
        }
        text.AppendLine();

        for (var i = 0; i < maxLen; i++)
        {
            var t = i < timeSeries.Count ? timeSeries.Minutes[i].ToString("R", CultureInfo.InvariantCulture) : "";
            text.Append(t);
            for (var col = 0; col < seriesList.Count; col++)
            {
                var s = seriesList[col];
                text.Append(',');
                if (i < s.Count)
                {
                    text.Append(FormatCsvValue(s.Values[i]));
                }
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
    {
        PanelCount = PanelCount switch
        {
            1 => 2,
            2 => 4,
            _ => 1,
        };
        LayoutChanged?.Invoke();
    }

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

    partial void OnBottomLeftChannelChanged(ChartChannelOption? value) => LayoutChanged?.Invoke();

    partial void OnBottomRightChannelChanged(ChartChannelOption? value) => LayoutChanged?.Invoke();

    partial void OnPanelCountChanged(int value) => LayoutChanged?.Invoke();

    partial void OnSelectedWindowChanged(ChartWindow value) => LayoutChanged?.Invoke();

    [RelayCommand]
    private void NewSession()
    {
        if (_dialogs is null || _sessionLogger is null)
        {
            return;
        }

        var defaultPrefix = "Ensaio";
        if (!_dialogs.PromptInput(
            "Nova Corrida / Etapa de Processo",
            "Digite o nome ou rótulo do ensaio / etapa:",
            out var response,
            defaultPrefix))
        {
            return;
        }

        var fileName = AppPaths.FormatSessionFileName(response);
        var path = Path.Combine(AppPaths.SessionsDirectory, fileName);
        var cleanName = Path.GetFileNameWithoutExtension(path);

        _sessionLogger.Stop();
        _settings.Update(settings => settings with
        {
            Logging = settings.Logging with { SessionLogPath = path },
        });
        _sessionLogger.Start(path);

        if (_device?.State is Protocol.ConnectionState.Connected)
        {
            _device.ZeroSessionTime();
        }

        _journal?.Add(
            AuditSource.Application,
            AuditSeverity.Information,
            $"Nova corrida/etapa iniciada: {cleanName}",
            path);
    }

    [RelayCommand]
    private void AnnotateEvent()
    {
        if (_dialogs is null)
        {
            return;
        }

        if (_dialogs.PromptInput(
            "Marcar Evento / Anotação",
            "Digite a descrição ou anotação do evento de processo:",
            out var note) && !string.IsNullOrWhiteSpace(note))
        {
            _journal?.Add(
                AuditSource.Application,
                AuditSeverity.Information,
                $"[Anotação de Processo] {note.Trim()}");
        }
    }

    private static string FormatCsvValue(double value)
        => double.IsNaN(value) ? "" : value.ToString("R", CultureInfo.InvariantCulture);

    public void Dispose() => _settings.Changed -= OnSettingsChanged;
}
