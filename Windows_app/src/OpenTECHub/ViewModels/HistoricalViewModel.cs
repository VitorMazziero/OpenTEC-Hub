using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Platform;
using OpenTECHub.Services.Telemetry;

namespace OpenTECHub.ViewModels;

/// <summary>Session browser kept separate from the dedicated dual-graph workspace.</summary>
public sealed partial class HistoricalViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly ISessionFileService _sessions;
    private readonly IFileInteractionService _files;
    private readonly ChartsViewModel _charts;
    private IReadOnlyList<SessionFileSummary> _allSessions = [];

    public HistoricalViewModel(
        ISettingsService settings,
        ISessionFileService sessions,
        IFileInteractionService files,
        ChartsViewModel charts)
    {
        _settings = settings;
        _sessions = sessions;
        _files = files;
        _charts = charts;
        Refresh();
    }

    public ObservableCollection<SessionFileSummary> Sessions { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadInGraphsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCsvCommand))]
    public partial SessionFileSummary? SelectedSession { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    public event Action? OpenGraphsRequested;

    [RelayCommand]
    private void Refresh()
    {
        try
        {
            _allSessions = _sessions.Discover(_settings.Current);
            ApplySearch();
            StatusText = _allSessions.Count == 0
                ? "Nenhum arquivo de sessão encontrado."
                : $"{_allSessions.Count} arquivo(s) inspecionado(s).";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _allSessions = [];
            Sessions.Clear();
            StatusText = $"Não foi possível ler as sessões: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var path = SelectedSession is { } selected
            ? Path.GetDirectoryName(selected.Path) ?? AppPaths.SessionsDirectory
            : AppPaths.SessionsDirectory;
        _files.OpenFolder(path);
    }

    private bool CanUseSelected() => SelectedSession is { HeaderValid: true };

    [RelayCommand(CanExecute = nameof(CanUseSelected))]
    private void LoadInGraphs()
    {
        if (SelectedSession is not { } selected)
        {
            return;
        }

        try
        {
            _charts.LoadSession(_sessions.Load(selected));
            StatusText = $"{selected.Name} carregado no Gráficos; nenhuma comunicação foi iniciada.";
            OpenGraphsRequested?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            StatusText = $"Não foi possível carregar a sessão: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseSelected))]
    private void ExportCsv()
    {
        if (SelectedSession is not { } selected)
        {
            return;
        }

        var destination = _files.ChooseSavePath(
            "Exportar sessão como CSV",
            Path.GetFileNameWithoutExtension(selected.Name) + ".csv",
            "CSV (*.csv)|*.csv",
            ".csv");
        if (destination is null)
        {
            return;
        }

        try
        {
            _sessions.ExportCsv(selected, destination);
            StatusText = $"CSV exportado para {destination}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Falha ao exportar: {ex.Message}";
        }
    }

    partial void OnSearchTextChanged(string value) => ApplySearch();

    private void ApplySearch()
    {
        var search = SearchText.Trim();
        var selectedPath = SelectedSession?.Path;
        Sessions.Clear();
        foreach (var session in _allSessions.Where(session =>
                     search.Length == 0 ||
                     session.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                     session.ConnectionMedia.Contains(search, StringComparison.CurrentCultureIgnoreCase)))
        {
            Sessions.Add(session);
        }

        SelectedSession = Sessions.FirstOrDefault(session => session.Path == selectedPath) ??
                          Sessions.FirstOrDefault();
    }
}
