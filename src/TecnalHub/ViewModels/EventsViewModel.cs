using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TecnalHub.Services.Persistence;
using TecnalHub.Services.Platform;
using TecnalHub.Services.Telemetry;

namespace TecnalHub.ViewModels;

public sealed record AuditSourceOption(AuditSource? Source, string Label)
{
    public override string ToString() => Label;
}

public sealed record AuditSeverityOption(AuditSeverity? Severity, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Presentation wrapper for an immutable journal entry.</summary>
public sealed record AuditEventRow(AuditEvent Entry)
{
    public string TimestampText => Entry.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.CurrentCulture);

    public string SourceLabel => SourceText(Entry.Source);

    public string SeverityLabel => Entry.Severity switch
    {
        AuditSeverity.Error => "Erro",
        AuditSeverity.Warning => "Aviso",
        _ => "Informação",
    };

    public string Message => Entry.Message;

    public string Detail => Entry.Detail;

    public static string SourceText(AuditSource source) => source switch
    {
        AuditSource.Equipment => "Equipamento",
        AuditSource.Command => "Comando",
        AuditSource.Connection => "Conexão",
        AuditSource.Setpoint => "Setpoint",
        AuditSource.Alarm => "Alarme",
        AuditSource.Recipe => "Receita",
        AuditSource.Calibration => "Calibração",
        _ => "Aplicação",
    };
}

/// <summary>Filterable audit trail and session-log controls for WP7.</summary>
public sealed partial class EventsViewModel : ObservableObject, IDisposable
{
    private readonly IEventJournal _journal;
    private readonly ISessionLogger _sessionLogger;
    private readonly ISettingsService _settings;
    private readonly IFileInteractionService _files;
    private long _clearedThroughSequence;
    private bool _filtersReady;

    public EventsViewModel(
        IEventJournal journal,
        ISessionLogger sessionLogger,
        ISettingsService settings,
        IFileInteractionService files)
    {
        _journal = journal;
        _sessionLogger = sessionLogger;
        _settings = settings;
        _files = files;

        SourceOptions =
        [
            new(null, "Todas as fontes"),
            .. Enum.GetValues<AuditSource>().Select(source => new AuditSourceOption(source, AuditEventRow.SourceText(source))),
        ];
        SeverityOptions =
        [
            new(null, "Todas as severidades"),
            new(AuditSeverity.Information, "Informação"),
            new(AuditSeverity.Warning, "Aviso"),
            new(AuditSeverity.Error, "Erro"),
        ];
        SelectedSource = SourceOptions[0];
        SelectedSeverity = SeverityOptions[0];
        _filtersReady = true;

        journal.EntryAdded += OnEntryAdded;
        sessionLogger.StatusChanged += OnSessionStatusChanged;
        RefreshVisible();
        RefreshSessionStatus();
    }

    public IReadOnlyList<AuditSourceOption> SourceOptions { get; }

    public IReadOnlyList<AuditSeverityOption> SeverityOptions { get; }

    public ObservableCollection<AuditEventRow> VisibleEvents { get; } = [];

    [ObservableProperty]
    public partial AuditSourceOption SelectedSource { get; set; }

    [ObservableProperty]
    public partial AuditSeverityOption SelectedSeverity { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    [ObservableProperty]
    public partial bool FollowNewEntries { get; set; } = true;

    [ObservableProperty]
    public partial AuditEventRow? SelectedEvent { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsLogging { get; set; }

    [ObservableProperty]
    public partial string LoggingStateText { get; set; } = "Parado";

    [ObservableProperty]
    public partial string SessionPathText { get; set; } = "—";

    [ObservableProperty]
    public partial string SessionRowsText { get; set; } = "0";

    [ObservableProperty]
    public partial string SessionSizeText { get; set; } = "—";

    [RelayCommand]
    private void CopySelected()
    {
        if (SelectedEvent is not { } selected)
        {
            StatusText = "Selecione um evento para copiar.";
            return;
        }

        _files.CopyText(
            $"{selected.TimestampText}\t{selected.SourceLabel}\t{selected.SeverityLabel}\t" +
            $"{selected.Message}{Environment.NewLine}{selected.Detail}");
        StatusText = "Evento copiado.";
    }

    [RelayCommand]
    private void ExportVisible()
    {
        var path = _files.ChooseSavePath(
            "Exportar eventos",
            $"eventos_{DateTimeOffset.Now:yyyy-MM-dd_HH-mm-ss}.csv",
            "CSV (*.csv)|*.csv",
            ".csv");
        if (path is null)
        {
            return;
        }

        try
        {
            using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
            writer.WriteLine("Timestamp,Source,Severity,Message,Detail");
            foreach (var row in VisibleEvents)
            {
                writer.WriteLine(string.Join(',', new[]
                {
                    EscapeCsv(row.Entry.Timestamp.ToString("O", CultureInfo.InvariantCulture)),
                    EscapeCsv(row.SourceLabel),
                    EscapeCsv(row.SeverityLabel),
                    EscapeCsv(row.Message),
                    EscapeCsv(row.Detail),
                }));
            }

            StatusText = $"Eventos exportados para {path}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Falha ao exportar eventos: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ClearView()
    {
        _clearedThroughSequence = _journal.Snapshot().LastOrDefault()?.Sequence ?? _clearedThroughSequence;
        VisibleEvents.Clear();
        SelectedEvent = null;
        StatusText = "Visualização limpa; o arquivo de aplicação não foi alterado.";
    }

    [RelayCommand]
    private void ToggleLogging()
    {
        if (_sessionLogger.IsLogging)
        {
            _sessionLogger.Stop();
            _journal.Add(AuditSource.Application, AuditSeverity.Information, "Registro de sessão interrompido.");
            return;
        }

        var path = _settings.Current.Logging.SessionLogPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = NewSessionPath();
            _settings.Update(settings => settings with
            {
                Logging = settings.Logging with { SessionLogPath = path },
            });
        }

        _sessionLogger.Start(path);
        _journal.Add(
            AuditSource.Application,
            AuditSeverity.Information,
            "Registro de sessão iniciado.",
            path);
    }

    [RelayCommand]
    private void NewSessionFile()
    {
        _sessionLogger.Stop();
        var path = NewSessionPath();
        _settings.Update(settings => settings with
        {
            Logging = settings.Logging with { SessionLogPath = path },
        });
        _sessionLogger.Start(path);
        _journal.Add(AuditSource.Application, AuditSeverity.Information, "Novo arquivo de sessão aberto.", path);
    }

    [RelayCommand]
    private void OpenSessionFolder()
        => _files.OpenFolder(_sessionLogger.CurrentPath ?? AppPaths.SessionsDirectory);

    private string NewSessionPath()
    {
        var configured = _settings.Current.Logging.SessionLogPath;
        var directory = !string.IsNullOrWhiteSpace(configured)
            ? Path.GetDirectoryName(configured)
            : null;
        directory = string.IsNullOrWhiteSpace(directory)
            ? AppPaths.SessionsDirectory
            : directory;

        return Path.Combine(directory, $"session_{DateTimeOffset.Now:yyyy-MM-dd_HH-mm-ss}.txt");
    }

    private void OnEntryAdded(AuditEvent entry)
    {
        void AddOnUi()
        {
            if (!IsPaused && entry.Sequence > _clearedThroughSequence && Matches(entry))
            {
                VisibleEvents.Add(new AuditEventRow(entry));
            }
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(AddOnUi);
        }
        else
        {
            AddOnUi();
        }
    }

    private void OnSessionStatusChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(RefreshSessionStatus);
        }
        else
        {
            RefreshSessionStatus();
        }
    }

    private void RefreshSessionStatus()
    {
        IsLogging = _sessionLogger.IsLogging;
        LoggingStateText = IsLogging ? "Gravando" : "Parado";
        SessionPathText = _sessionLogger.CurrentPath ?? "—";
        SessionRowsText = _sessionLogger.RowsWritten.ToString(CultureInfo.CurrentCulture);
        SessionSizeText = _sessionLogger.CurrentPath is { } path && File.Exists(path)
            ? FormatSize(new FileInfo(path).Length)
            : "—";
    }

    private bool Matches(AuditEvent entry)
    {
        if (SelectedSource is { Source: { } source } && entry.Source != source)
        {
            return false;
        }

        if (SelectedSeverity is { Severity: { } severity } && entry.Severity != severity)
        {
            return false;
        }

        var search = SearchText.Trim();
        return search.Length == 0 ||
               entry.Message.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
               entry.Detail.Contains(search, StringComparison.CurrentCultureIgnoreCase);
    }

    private void RefreshVisible()
    {
        VisibleEvents.Clear();
        foreach (var entry in _journal.Snapshot().Where(entry =>
                     entry.Sequence > _clearedThroughSequence && Matches(entry)))
        {
            VisibleEvents.Add(new AuditEventRow(entry));
        }
    }

    partial void OnSelectedSourceChanged(AuditSourceOption value)
    {
        if (_filtersReady)
        {
            RefreshVisible();
        }
    }

    partial void OnSelectedSeverityChanged(AuditSeverityOption value)
    {
        if (_filtersReady)
        {
            RefreshVisible();
        }
    }

    partial void OnSearchTextChanged(string value) => RefreshVisible();

    partial void OnIsPausedChanged(bool value)
    {
        if (!value)
        {
            RefreshVisible();
        }
    }

    private static string EscapeCsv(string text)
    {
        var escaped = text.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024d / 1024d:F1} MB",
        >= 1024 => $"{bytes / 1024d:F1} kB",
        _ => $"{bytes} B",
    };

    public void Dispose()
    {
        _journal.EntryAdded -= OnEntryAdded;
        _sessionLogger.StatusChanged -= OnSessionStatusChanged;
    }
}
