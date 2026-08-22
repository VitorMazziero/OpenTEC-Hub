using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TecnalHub.Services.Recipes;

namespace TecnalHub.ViewModels;

/// <summary>One draggable connector between two node ports, with live endpoint geometry.</summary>
public sealed partial class RecipeConnectionViewModel : ObservableObject
{
    private readonly RecipeConnection _model;

    public RecipeConnectionViewModel(RecipeConnection model, RecipeNodeViewModel source, RecipeNodeViewModel target)
    {
        _model = model;
        Source = source;
        Target = target;
        source.PropertyChanged += OnEndpointMoved;
        target.PropertyChanged += OnEndpointMoved;
    }

    public RecipeConnection Model => _model;

    public RecipeNodeViewModel Source { get; }

    public RecipeNodeViewModel Target { get; }

    [ObservableProperty]
    public partial bool IsExecuted { get; set; }

    public double X1 => Anchor(Source, _model.SourceConnector).X;

    public double Y1 => Anchor(Source, _model.SourceConnector).Y;

    public double X2 => Anchor(Target, _model.TargetConnector).X;

    public double Y2 => Anchor(Target, _model.TargetConnector).Y;

    private void OnEndpointMoved(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RecipeNodeViewModel.X) or nameof(RecipeNodeViewModel.Y))
        {
            OnPropertyChanged(nameof(X1));
            OnPropertyChanged(nameof(Y1));
            OnPropertyChanged(nameof(X2));
            OnPropertyChanged(nameof(Y2));
        }
    }

    private static (double X, double Y) Anchor(RecipeNodeViewModel node, string connector)
    {
        var port = node.Ports.FirstOrDefault(p => p.Name == connector)
                   ?? node.Ports.FirstOrDefault(p =>
                       (ConnectorNames.IsLoopIn(connector) && ConnectorNames.IsLoopIn(p.Name)) ||
                       (ConnectorNames.IsLoopOut(connector) && ConnectorNames.IsLoopOut(p.Name)));

        return port is null
            ? (node.X + RecipeNodeViewModel.Width / 2, node.Y + RecipeNodeViewModel.HeaderHeight / 2)
            : (node.X + port.OffsetX, node.Y + port.OffsetY);
    }

    public void Detach()
    {
        Source.PropertyChanged -= OnEndpointMoved;
        Target.PropertyChanged -= OnEndpointMoved;
    }
}

/// <summary>A draggable block in the library, grouped by category.</summary>
public sealed record BlockLibraryItem(NodeType Type, string Title, string CategoryColor);

/// <summary>A collapsible category group in the block library.</summary>
public sealed record BlockLibraryGroup(string Label, string Color, IReadOnlyList<BlockLibraryItem> Items);

/// <summary>
/// The Receitas page: authoring the node graph, validating it, and running it through the engine.
/// </summary>
/// <remarks>
/// Starting switches the whole application to <c>Modo Receita</c> — the engine claims every actuator,
/// so the manual control surfaces go inert and only the recipe writes to the wire (§5.3.3).
/// </remarks>
public sealed partial class ReceitasViewModel : ObservableObject, IDisposable
{
    private readonly IRecipeEngine _engine;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _elapsedTimer;
    private RecipeDocument _document = new();
    private (string NodeId, string Port)? _pendingConnection;

    public ReceitasViewModel(IRecipeEngine engine)
    {
        _engine = engine;
        _dispatcher = Dispatcher.CurrentDispatcher;

        Library = BuildLibrary();

        _engine.NodeStateChanged += OnNodeStateChanged;
        _engine.StateChanged += OnEngineStateChanged;
        _engine.Logged += OnEngineLogged;

        _elapsedTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => ElapsedText = _engine.Elapsed.ToString(@"hh\:mm\:ss");

        NewRecipe();
    }

    /// <summary>The nodes on the canvas.</summary>
    public ObservableCollection<RecipeNodeViewModel> Nodes { get; } = [];

    /// <summary>The connectors between nodes.</summary>
    public ObservableCollection<RecipeConnectionViewModel> Connections { get; } = [];

    /// <summary>Engine log lines, newest last.</summary>
    public ObservableCollection<string> Log { get; } = [];

    /// <summary>The block library, grouped by category.</summary>
    public IReadOnlyList<BlockLibraryGroup> Library { get; }

    [ObservableProperty]
    public partial string RecipeName { get; set; } = "Nova Receita";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial RecipeNodeViewModel? SelectedNode { get; set; }

    public bool HasSelection => SelectedNode is not null;

    [ObservableProperty]
    public partial string JsonText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsValid { get; set; }

    [ObservableProperty]
    public partial string ValidationSummary { get; set; } = "";

    /// <summary>The validation findings, for the findings list.</summary>
    public ObservableCollection<RecipeFinding> Findings { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsStopped))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    public partial RecipeRunState RunState { get; set; } = RecipeRunState.Idle;

    public bool IsRunning => RunState is RecipeRunState.Running or RecipeRunState.Paused;

    public bool IsStopped => !IsRunning;

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Parado";

    [ObservableProperty]
    public partial string ElapsedText { get; set; } = "00:00:00";

    // ── Authoring ──────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(IsStopped))]
    private void NewRecipe()
    {
        ClearConnections();
        Nodes.Clear();

        _document = new RecipeDocument { Name = "Nova Receita" };
        RecipeName = _document.Name;

        // Seed a runnable starter graph: Início → Fim, spaced for the canvas.
        var start = RecipeNode.Create(NodeType.Start, x: 80, y: 200);
        var end = RecipeNode.Create(NodeType.End, x: 520, y: 200);
        _document.Nodes.AddRange([start, end]);
        _document.Connections.Add(new RecipeConnection(start.Id, ConnectorNames.Out, end.Id, ConnectorNames.In));

        Rebuild();
    }

    [RelayCommand(CanExecute = nameof(IsStopped))]
    private void AddBlock(NodeType type)
    {
        // Drop near the canvas origin, nudged by the current count so blocks do not stack exactly.
        var offset = _document.Nodes.Count % 6 * 28;
        var node = RecipeNode.Create(type, x: 300 + offset, y: 320 + offset);
        _document.Nodes.Add(node);

        var vm = CreateNodeViewModel(node);
        Nodes.Add(vm);
        SelectedNode = vm;
        Revalidate();
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void DeleteSelected()
    {
        if (SelectedNode is not { } node || node.Type == NodeType.Start)
        {
            return; // the single Início is not deletable
        }

        _document.Nodes.RemoveAll(n => n.Id == node.Id);
        _document.Connections.RemoveAll(c => c.SourceNodeId == node.Id || c.TargetNodeId == node.Id);
        SelectedNode = null;
        Rebuild();
    }

    private bool CanDelete() => IsStopped && SelectedNode is not null && SelectedNode.Type != NodeType.Start;

    /// <summary>Click-to-connect: an output port then an input port makes a connection.</summary>
    public void PortClicked(RecipeNodeViewModel node, RecipePortViewModel port)
    {
        if (IsRunning)
        {
            return;
        }

        if (_pendingConnection is null)
        {
            if (!port.IsInput)
            {
                _pendingConnection = (node.Id, port.Name);
            }

            return;
        }

        var (sourceId, sourcePort) = _pendingConnection.Value;
        _pendingConnection = null;

        // Complete only onto an input port of a different node.
        if (!port.IsInput || node.Id == sourceId)
        {
            return;
        }

        var connection = new RecipeConnection(sourceId, sourcePort, node.Id, port.Name);
        if (_document.Connections.Any(c => c.SourceNodeId == sourceId && c.SourceConnector == sourcePort
            && c.TargetNodeId == node.Id && c.TargetConnector == port.Name))
        {
            return; // already connected
        }

        _document.Connections.Add(connection);
        AddConnectionViewModel(connection);
        Revalidate();
    }

    public void SelectNode(RecipeNodeViewModel? node)
    {
        SelectedNode = node;
        foreach (var candidate in Nodes)
        {
            candidate.IsSelected = ReferenceEquals(candidate, node);
        }
    }

    // ── Execution ──────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        Log.Clear();
        try
        {
            await _engine.StartAsync(_document);
            _elapsedTimer.Start();
        }
        catch (InvalidOperationException ex)
        {
            StatusText = ex.Message;
        }
    }

    private bool CanStart() => IsStopped && IsValid;

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

    // ── Rebuild / validation ────────────────────────────────────────────────────

    private void Rebuild()
    {
        ClearConnections();
        Nodes.Clear();

        foreach (var node in _document.Nodes)
        {
            Nodes.Add(CreateNodeViewModel(node));
        }

        foreach (var connection in _document.Connections)
        {
            AddConnectionViewModel(connection);
        }

        Revalidate();
    }

    private RecipeNodeViewModel CreateNodeViewModel(RecipeNode node)
    {
        var vm = new RecipeNodeViewModel(node);
        vm.Changed += Revalidate;
        return vm;
    }

    private void AddConnectionViewModel(RecipeConnection connection)
    {
        var source = Nodes.FirstOrDefault(n => n.Id == connection.SourceNodeId);
        var target = Nodes.FirstOrDefault(n => n.Id == connection.TargetNodeId);
        if (source is not null && target is not null)
        {
            Connections.Add(new RecipeConnectionViewModel(connection, source, target));
        }
    }

    private void ClearConnections()
    {
        foreach (var connection in Connections)
        {
            connection.Detach();
        }

        Connections.Clear();
    }

    private void Revalidate()
    {
        _document.Name = RecipeName;
        var result = RecipeValidator.Validate(_document);
        IsValid = result.IsValid;

        Findings.Clear();
        foreach (var finding in result.Findings)
        {
            Findings.Add(finding);
        }

        ValidationSummary = result.IsValid
            ? (result.Warnings.Count > 0 ? $"✓ Receita válida · {result.Warnings.Count} aviso(s)" : "✓ Receita válida")
            : $"⚠ {result.Errors.Count} problema(s)";

        JsonText = RecipeSerializer.Serialize(_document);
        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnRecipeNameChanged(string value) => Revalidate();

    // ── Engine events (marshalled to the UI thread) ──────────────────────────────

    private void OnNodeStateChanged(string nodeId) => OnUi(() =>
    {
        if (Nodes.FirstOrDefault(n => n.Id == nodeId) is { } node)
        {
            node.ExecutionState = _engine.NodeStateOf(nodeId);
        }

        foreach (var connection in Connections)
        {
            connection.IsExecuted = _engine.WasTraversed(connection.Model);
        }
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
        => [.. Enum.GetValues<BlockCategory>().Select(category => new BlockLibraryGroup(
            RecipeNodeCatalog.Categories[category].Label,
            RecipeNodeCatalog.Categories[category].HeaderColor,
            [.. RecipeNodeCatalog.All
                .Where(d => d.Category == category)
                .Select(d => new BlockLibraryItem(d.Type, d.Title, RecipeNodeCatalog.HeaderColor(d.Type)))]))];

    public void Dispose()
    {
        _engine.NodeStateChanged -= OnNodeStateChanged;
        _engine.StateChanged -= OnEngineStateChanged;
        _engine.Logged -= OnEngineLogged;
        _elapsedTimer.Stop();
        ClearConnections();
    }
}
