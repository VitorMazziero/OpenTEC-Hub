using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TecnalHub.Services.Dialogs;
using TecnalHub.Services.KlaMapping;
using TecnalHub.Services.Persistence;
using TecnalHub.Services.Recipes;

namespace TecnalHub.ViewModels;

/// <summary>A draggable block in the library, grouped by category.</summary>
public sealed record BlockLibraryItem(NodeType Type, string Title, string CategoryColor);

/// <summary>A collapsible category group in the block library.</summary>
public sealed record BlockLibraryGroup(string Label, string Color, IReadOnlyList<BlockLibraryItem> Items);

/// <summary>
/// The Receitas page: open recipes as tabs, the Minhas Receitas library, the block palette, and the
/// execution controls that drive the engine.
/// </summary>
/// <remarks>
/// Starting switches the whole application to <c>Modo Receita</c> — the engine claims every actuator,
/// so the manual control surfaces go inert and only the recipe writes to the wire (§5.3.3).
/// </remarks>
public sealed partial class ReceitasViewModel : ObservableObject, IDisposable
{
    private readonly IRecipeEngine _engine;
    private readonly IRecipeStore _store;
    private readonly ISettingsService? _settings;
    private readonly IDialogService? _dialogs;
    private readonly IKlaProfileStore? _klaStore;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _elapsedTimer;
    private RecipeTabViewModel? _runningTab;

    public ReceitasViewModel(
        IRecipeEngine engine,
        IRecipeStore store,
        ISettingsService? settings = null,
        IDialogService? dialogs = null,
        IKlaProfileStore? klaStore = null)
    {
        _engine = engine;
        _store = store;
        _settings = settings;
        _dialogs = dialogs;
        _klaStore = klaStore;
        _dispatcher = Dispatcher.CurrentDispatcher;

        Library = BuildLibrary();

        _engine.NodeStateChanged += OnNodeStateChanged;
        _engine.StateChanged += OnEngineStateChanged;
        _engine.Logged += OnEngineLogged;
        _engine.WaitingChanged += OnEngineWaitingChanged;

        _elapsedTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => ElapsedText = _engine.Elapsed.ToString(@"hh\:mm\:ss");

        RefreshLibrary();
        NewRecipe();
    }

    /// <summary>Open recipes.</summary>
    public ObservableCollection<RecipeTabViewModel> Tabs { get; } = [];

    /// <summary>Saved recipes on disk, for the Minhas Receitas list.</summary>
    public ObservableCollection<RecipeSummary> LibraryItems { get; } = [];

    /// <summary>Engine log lines, newest last.</summary>
    public ObservableCollection<string> Log { get; } = [];

    /// <summary>The block library, grouped by category.</summary>
    public IReadOnlyList<BlockLibraryGroup> Library { get; }

    /// <summary>Raised when a block should be scrolled into view (from a validation finding).</summary>
    public event Action<RecipeNodeViewModel>? CenterOnNodeRequested;

    /// <summary>Queries the view for the current viewport center coordinates in canvas space.</summary>
    public event Func<(double X, double Y)>? RequestViewportCenter;

    [ObservableProperty]
    public partial bool ShowJsonPanel { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCanvas))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveRecipeCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddBlockCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    [NotifyCanExecuteChangedFor(nameof(RedoCommand))]
    public partial RecipeTabViewModel? SelectedTab { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCanvas))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddBlockCommand))]
    public partial bool IsLibrarySelected { get; set; }

    /// <summary>True when a recipe canvas is showing (a tab is active and not the library).</summary>
    public bool ShowCanvas => !IsLibrarySelected && SelectedTab is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsStopped))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(NewRecipeCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveRecipeCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddBlockCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    public partial RecipeRunState RunState { get; set; } = RecipeRunState.Idle;

    public bool IsRunning => RunState is RecipeRunState.Running or RecipeRunState.Paused;

    public bool IsStopped => !IsRunning;

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Parado";

    [ObservableProperty]
    public partial string ElapsedText { get; set; } = "00:00:00";

    /// <summary>
    /// True while a block is holding for an external device that has not confirmed its command.
    /// </summary>
    /// <remarks>
    /// The recipe holds rather than aborting — the operator decides whether to skip the block or
    /// stop the run, and the banner is where that decision is offered.
    /// </remarks>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SkipWaitCommand))]
    public partial bool IsWaitingOnDevice { get; set; }

    [ObservableProperty]
    public partial string WaitingText { get; set; } = "";

    [ObservableProperty]
    public partial double CanvasWidth { get; set; } = 3200;

    [ObservableProperty]
    public partial double CanvasHeight { get; set; } = 2000;

    // ── Tabs & library ───────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(IsStopped))]
    private void NewRecipe()
    {
        var document = new RecipeDocument { Name = $"Nova Receita {Tabs.Count + 1}" };
        document.Nodes.Add(RecipeNode.Create(NodeType.Start, x: 100, y: 150));
        document.Nodes.Add(RecipeNode.Create(NodeType.End, x: 500, y: 150));
        document.Connections.Add(new RecipeConnection(
            document.Nodes[0].Id, ConnectorNames.Out, document.Nodes[1].Id, ConnectorNames.In));

        var tab = CreateTab(document, fileName: null);
        Tabs.Add(tab);
        SelectTab(tab);
    }

    [RelayCommand]
    private void SelectTab(RecipeTabViewModel tab)
    {
        SelectedTab = tab;
        IsLibrarySelected = false;
        WatchSelectedTab();
    }

    [RelayCommand]
    private void CloseTab(RecipeTabViewModel tab)
    {
        if (IsRunning && ReferenceEquals(tab, _runningTab))
        {
            return; // cannot close the running recipe
        }

        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);

        if (ReferenceEquals(SelectedTab, tab))
        {
            if (Tabs.Count == 0)
            {
                OpenLibrary();
            }
            else
            {
                SelectTab(Tabs[Math.Min(index, Tabs.Count - 1)]);
            }
        }
    }

    [RelayCommand]
    private void OpenLibrary()
    {
        RefreshLibrary();
        IsLibrarySelected = true;
    }

    [RelayCommand(CanExecute = nameof(CanEditRecipe))]
    private void SaveRecipe()
    {
        if (SelectedTab is not { } tab)
        {
            return;
        }

        tab.FileName = _store.Save(tab.Document, tab.FileName);
        tab.IsDirty = false;
        RefreshLibrary();
    }

    [RelayCommand(CanExecute = nameof(IsStopped))]
    private void OpenRecipe(RecipeSummary summary)
    {
        if (Tabs.FirstOrDefault(t => t.FileName == summary.FileName) is { } already)
        {
            SelectTab(already);
            return;
        }

        try
        {
            var tab = CreateTab(_store.Load(summary.FileName), summary.FileName);
            Tabs.Add(tab);
            SelectTab(tab);
        }
        catch (Exception ex) when (ex is RecipeFormatException or System.IO.IOException)
        {
            StatusText = $"Falha ao abrir: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(IsStopped))]
    private void DeleteRecipe(RecipeSummary summary)
    {
        _store.Delete(summary.FileName);
        if (Tabs.FirstOrDefault(t => t.FileName == summary.FileName) is { } open)
        {
            open.FileName = null; // it no longer maps to a file
        }

        RefreshLibrary();
    }

    [RelayCommand(CanExecute = nameof(IsStopped))]
    private void DuplicateRecipe(RecipeSummary summary)
    {
        var document = _store.Load(summary.FileName);
        document.Name = "Cópia de " + document.Name;
        _store.Save(document, fileName: null);
        RefreshLibrary();
    }

    private void RefreshLibrary()
    {
        LibraryItems.Clear();
        foreach (var summary in _store.List())
        {
            LibraryItems.Add(summary);
        }
    }

    private RecipeTabViewModel CreateTab(RecipeDocument document, string? fileName)
    {
        var tab = new RecipeTabViewModel(document, fileName, _settings, _dialogs, _klaStore);
        tab.CenterRequested += node => OnUi(() => CenterOnNodeRequested?.Invoke(node));
        return tab;
    }

    private void WatchSelectedTab()
    {
        foreach (var tab in Tabs)
        {
            tab.PropertyChanged -= OnSelectedTabChanged;
            tab.CanvasBoundsChanged -= OnCanvasBoundsChanged;
        }

        if (SelectedTab is { } selected)
        {
            selected.PropertyChanged += OnSelectedTabChanged;
            selected.CanvasBoundsChanged += OnCanvasBoundsChanged;
            RefreshCanvasSize(selected);
        }
    }

    private void OnSelectedTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RecipeTabViewModel.IsValid))
        {
            StartCommand.NotifyCanExecuteChanged();
        }
        else if (e.PropertyName is nameof(RecipeTabViewModel.CanDeleteSelected)
                 or nameof(RecipeTabViewModel.SelectedNode)
                 or nameof(RecipeTabViewModel.SelectedConnection))
        {
            DeleteSelectedCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnCanvasBoundsChanged()
    {
        if (SelectedTab is { } tab)
        {
            RefreshCanvasSize(tab);
        }
    }

    private void RefreshCanvasSize(RecipeTabViewModel tab)
    {
        var (w, h) = tab.ComputeCanvasBounds();
        CanvasWidth = w;
        CanvasHeight = h;
    }

    // ── Authoring (delegated to the selected tab) ────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanEditRecipe))]
    private void AddBlock(NodeType type)
    {
        if (SelectedTab is null) return;
        if (RequestViewportCenter?.Invoke() is { } center)
        {
            var offset = (SelectedTab.Nodes.Count % 5) * 24;
            SelectedTab.AddBlock(type, center.X + offset, center.Y + offset);
        }
        else
        {
            SelectedTab.AddBlock(type);
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private void DeleteSelected() => SelectedTab?.DeleteSelected();

    [RelayCommand(CanExecute = nameof(CanEditRecipe))]
    private void Undo() => SelectedTab?.Undo();

    [RelayCommand(CanExecute = nameof(CanEditRecipe))]
    private void Redo() => SelectedTab?.Redo();

    [RelayCommand]
    private void RevealFinding(RecipeFinding finding) => SelectedTab?.RevealFinding(finding);

    public void PortClicked(RecipeNodeViewModel node, RecipePortViewModel port)
    {
        if (IsStopped)
        {
            SelectedTab?.PortClicked(node, port);
        }
    }

    private bool CanEditRecipe() => IsStopped && ShowCanvas;

    private bool CanDeleteSelected() => CanEditRecipe() && (SelectedTab?.CanDeleteSelected ?? false);

    // ── Execution ────────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanStartRecipe))]
    private async Task Start()
    {
        if (SelectedTab is not { } tab)
        {
            return;
        }

        Log.Clear();
        tab.ResetExecutionState();
        _runningTab = tab;
        try
        {
            await _engine.StartAsync(tab.Document);
            _elapsedTimer.Start();
        }
        catch (InvalidOperationException ex)
        {
            StatusText = ex.Message;
            _runningTab = null;
        }
    }

    private bool CanStartRecipe() => IsStopped && ShowCanvas && SelectedTab is { IsValid: true };

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Pause()
    {
        if (RunState == RecipeRunState.Paused)
        {
            _engine.Resume();
        }
        else
        {
            _engine.Pause();
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private async Task Stop() => await _engine.StopAsync("parada pelo operador");

    [RelayCommand(CanExecute = nameof(IsWaitingOnDevice))]
    private void SkipWait() => _engine.SkipWait();

    // ── Engine events (marshalled to the UI thread) ──────────────────────────────

    private void OnNodeStateChanged(string nodeId) => OnUi(() =>
    {
        _runningTab?.ApplyNodeState(nodeId, _engine.NodeStateOf(nodeId));
        _runningTab?.ApplyExecutedPath(_engine);
    });

    private void OnEngineStateChanged() => OnUi(() =>
    {
        RunState = _engine.State;
        StatusText = _engine.State switch
        {
            RecipeRunState.Running => "Executando",
            RecipeRunState.Paused => "Pausado",
            RecipeRunState.Completed => "Concluído",
            RecipeRunState.Failed => $"Falhou: {_engine.StatusReason}",
            RecipeRunState.Stopped => $"Parado: {_engine.StatusReason}",
            _ => "Parado",
        };

        if (_engine.State is RecipeRunState.Completed or RecipeRunState.Stopped or RecipeRunState.Failed)
        {
            _elapsedTimer.Stop();
        }
    });

    private void OnEngineWaitingChanged() => OnUi(() =>
    {
        var wait = _engine.Waiting;
        IsWaitingOnDevice = wait is not null;
        WaitingText = wait is null ? "" : $"Aguardando {wait.Device}: {wait.Detail}";
    });

    private void OnEngineLogged(RecipeLogEntry entry) => OnUi(() =>
    {
        Log.Add($"{DateTime.Now:HH:mm:ss}  {entry.Message}");
        while (Log.Count > 200)
        {
            Log.RemoveAt(0);
        }
    });

    private void OnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }

    private static IReadOnlyList<BlockLibraryGroup> BuildLibrary()
        => [.. Enum.GetValues<BlockCategory>()
            .Select(category => new BlockLibraryGroup(
                RecipeNodeCatalog.Categories[category].Label,
                RecipeNodeCatalog.Categories[category].HeaderColor,
                [.. RecipeNodeCatalog.All
                    .Where(d => d.Category == category && d.Type is not (NodeType.Start or NodeType.End or NodeType.PumpControl))
                    .Select(d => new BlockLibraryItem(d.Type, d.Title, RecipeNodeCatalog.HeaderColor(d.Type)))]))
            .Where(g => g.Items.Count > 0)];

    public void Dispose()
    {
        _engine.NodeStateChanged -= OnNodeStateChanged;
        _engine.StateChanged -= OnEngineStateChanged;
        _engine.Logged -= OnEngineLogged;
        _engine.WaitingChanged -= OnEngineWaitingChanged;
        _elapsedTimer.Stop();
        foreach (var tab in Tabs)
        {
            tab.CanvasBoundsChanged -= OnCanvasBoundsChanged;
        }
    }
}
