using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenTECHub.Services.Dialogs;
using OpenTECHub.Services.KlaMapping;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;

namespace OpenTECHub.ViewModels;

/// <summary>
/// One open recipe on the Receitas page: its graph, the current selection (a block or a connection),
/// live validation, the JSON view, and an undo/redo history. The page (<see cref="ReceitasViewModel"/>)
/// owns the commands and drives these tabs.
/// </summary>
public sealed partial class RecipeTabViewModel : ObservableObject
{
    private readonly ISettingsService? _settings;
    private readonly IDialogService? _dialogs;
    private readonly IKlaProfileStore? _klaStore;
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private (string NodeId, string Port)? _pendingConnection;
    private bool _restoring;

    public RecipeTabViewModel(
        RecipeDocument document,
        string? fileName,
        ISettingsService? settings = null,
        IDialogService? dialogs = null,
        IKlaProfileStore? klaStore = null)
    {
        _settings = settings;
        _dialogs = dialogs;
        _klaStore = klaStore;
        Document = document;
        FileName = fileName;
        Name = document.Name;
        Rebuild();
        IsDirty = false;
    }

    /// <summary>The underlying recipe.</summary>
    public RecipeDocument Document { get; private set; }

    /// <summary>The file this recipe is saved under, or null if never saved.</summary>
    public string? FileName { get; set; }

    public ObservableCollection<RecipeNodeViewModel> Nodes { get; } = [];

    public ObservableCollection<RecipeConnectionViewModel> Connections { get; } = [];

    /// <summary>Transient canvas guides displayed while the operator aligns a block.</summary>
    public ObservableCollection<RecipeAlignmentGuide> AlignmentGuides { get; } = [];

    public ObservableCollection<RecipeFinding> Findings { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabTitle))]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabTitle))]
    public partial bool IsDirty { get; set; }

    /// <summary>Tab caption: the name, with a dirty marker.</summary>
    public string TabTitle => IsDirty ? Name + " *" : Name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNodeSelected))]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelected))]
    public partial RecipeNodeViewModel? SelectedNode { get; set; }

    partial void OnSelectedNodeChanged(RecipeNodeViewModel? value)
    {
        if (value is { Type: NodeType.CascadeControl } cascade)
        {
            cascade.RefreshPresets();
            cascade.LoadAvailableKlaPaths();
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConnectionSelected))]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelected))]
    public partial RecipeConnectionViewModel? SelectedConnection { get; set; }

    public bool HasNodeSelected => SelectedNode is not null;

    public bool HasConnectionSelected => SelectedConnection is not null;

    public bool CanDeleteSelected => SelectedConnection is not null
                                  || (SelectedNode is { } node && node.Type is not (NodeType.Start or NodeType.End));

    [ObservableProperty]
    public partial string JsonText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsValid { get; set; }

    [ObservableProperty]
    public partial bool IsRenaming { get; set; }

    [ObservableProperty]
    public partial string ValidationSummary { get; set; } = "";

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Raised when a block should be scrolled into view (from a validation finding).</summary>
    public event Action<RecipeNodeViewModel>? CenterRequested;

    /// <summary>Raised when the furthest-out node changes, so the canvas can grow.</summary>
    public event Action? CanvasBoundsChanged;

    private const double MinCanvasWidth = 3200;
    private const double MinCanvasHeight = 2000;
    private const double CanvasMargin = 400;
    private const double MaxCanvasSize = 10000;

    /// <summary>Computes the required canvas size to fit all nodes, with margin, capped at <see cref="MaxCanvasSize"/>.</summary>
    public (double Width, double Height) ComputeCanvasBounds()
    {
        double maxX = 0, maxY = 0;
        foreach (var node in Nodes)
        {
            var right = node.X + RecipeNodeViewModel.Width + CanvasMargin;
            var bottom = node.Y + node.Height + CanvasMargin;
            if (right > maxX)
            {
                maxX = right;
            }
            if (bottom > maxY)
            {
                maxY = bottom;
            }
        }

        return (Math.Min(MaxCanvasSize, Math.Max(MinCanvasWidth, maxX)),
                Math.Min(MaxCanvasSize, Math.Max(MinCanvasHeight, maxY)));
    }

    // ── Editing ────────────────────────────────────────────────────────────────

    public void AddBlock(NodeType type, double? x = null, double? y = null)
    {
        PushHistory();
        var posX = x ?? (Nodes.Count > 0 ? Nodes.Max(n => n.X) + 80 : 320);
        var posY = y ?? (Nodes.Count > 0 ? Nodes.Average(n => n.Y) : 200);
        var node = RecipeNode.Create(type, x: posX, y: posY);
        Document.Nodes.Add(node);

        var vm = CreateNodeViewModel(node);
        Nodes.Add(vm);

        // A new Controle de O₂ is continuous by default. The node and its internal
        // loop are part of the same history snapshot, so one Undo removes both.
        if (type == NodeType.CascadeControl)
        {
            var selfLoop = new RecipeConnection(node.Id, ConnectorNames.LoopOut, node.Id, ConnectorNames.LoopIn);
            Document.Connections.Add(selfLoop);
            AddConnectionViewModel(selfLoop);
        }

        SelectNode(vm);
        RefreshGateRoles();
        MarkDirty();
        Revalidate();
    }

    public void DeleteSelected()
    {
        if (SelectedConnection is { } connection)
        {
            PushHistory();
            Document.Connections.RemoveAll(c => Same(c, connection.Model));
            connection.Detach();
            Connections.Remove(connection);
            SelectedConnection = null;
            RefreshGateRoles();
            MarkDirty();
            Revalidate();
            return;
        }

        if (SelectedNode is { } node && node.Type is not (NodeType.Start or NodeType.End))
        {
            PushHistory();
            Document.Nodes.RemoveAll(n => n.Id == node.Id);
            Document.Connections.RemoveAll(c => c.SourceNodeId == node.Id || c.TargetNodeId == node.Id);
            SelectedNode = null;
            Rebuild();
            MarkDirty();
        }
    }

    /// <summary>Click-to-connect: an output port then an input port makes a connection.</summary>
    public void PortClicked(RecipeNodeViewModel node, RecipePortViewModel port)
    {
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

        var isLoopSelfConnection = node.Id == sourceId &&
                                   ConnectorNames.IsLoopOut(sourcePort) &&
                                   ConnectorNames.IsLoopIn(port.Name);

        if (!port.IsInput || (node.Id == sourceId && !isLoopSelfConnection))
        {
            return;
        }

        // A self-loop next to a real exit condition only muddies the graph: the engine skips it,
        // so the canvas would show a loop wire that decides nothing.
        if (isLoopSelfConnection && HasLoopExitCondition(node.Id))
        {
            return;
        }

        var connection = new RecipeConnection(sourceId, sourcePort, node.Id, port.Name);
        if (Document.Connections.Any(c => Same(c, connection)))
        {
            return;
        }

        PushHistory();

        // Wiring a real external condition replaces the default infinite self-loop in
        // the same mutation. Undo therefore restores the complete previous topology.
        if (ConnectorNames.IsLoopOut(sourcePort)
            && node.Id != sourceId
            && Document.Node(sourceId)?.Type == NodeType.CascadeControl
            && IsExternalConditionTarget(node.Type))
        {
            RemoveCascadeSelfLoop(sourceId);
        }

        Document.Connections.Add(connection);
        AddConnectionViewModel(connection);
        RefreshGateRoles();
        MarkDirty();
        Revalidate();
    }

    public void SelectNode(RecipeNodeViewModel? node)
    {
        SelectedConnection = null;
        SelectedNode = node;
        foreach (var candidate in Nodes)
        {
            candidate.IsSelected = ReferenceEquals(candidate, node);
        }

        foreach (var connection in Connections)
        {
            connection.IsSelected = false;
        }
    }

    public void SelectConnection(RecipeConnectionViewModel? connection)
    {
        SelectNode(null);
        SelectedConnection = connection;
        foreach (var candidate in Connections)
        {
            candidate.IsSelected = ReferenceEquals(candidate, connection);
        }
    }

    /// <summary>Selects and asks the view to centre the block behind a validation finding.</summary>
    public void RevealFinding(RecipeFinding finding)
    {
        if (finding.NodeId is { } id && Nodes.FirstOrDefault(n => n.Id == id) is { } node)
        {
            SelectNode(node);
            CenterRequested?.Invoke(node);
        }
    }

    /// <summary>Snapshots the graph before a mutation, for undo.</summary>
    public void PushHistory()
    {
        if (_restoring)
        {
            return;
        }

        _undo.Push(RecipeSerializer.Serialize(Document));
        _redo.Clear();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    public void Undo()
    {
        if (_undo.Count == 0)
        {
            return;
        }

        _redo.Push(RecipeSerializer.Serialize(Document));
        Restore(_undo.Pop());
    }

    public void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        _undo.Push(RecipeSerializer.Serialize(Document));
        Restore(_redo.Pop());
    }

    // ── Execution feedback (from the engine) ─────────────────────────────────────

    public void ApplyNodeState(string nodeId, NodeState state)
    {
        if (Nodes.FirstOrDefault(n => n.Id == nodeId) is { } node)
        {
            node.ExecutionState = state;
        }
    }

    public void ResetExecutionState()
    {
        foreach (var node in Nodes)
        {
            node.ExecutionState = NodeState.Waiting;
        }

        foreach (var connection in Connections)
        {
            connection.IsExecuted = false;
        }
    }

    public void ApplyExecutedPath(IRecipeEngine engine)
    {
        foreach (var connection in Connections)
        {
            connection.IsExecuted = engine.WasTraversed(connection.Model);
        }
    }

    // ── Rebuild / validation ─────────────────────────────────────────────────────

    private void Rebuild()
    {
        foreach (var connection in Connections)
        {
            connection.Detach();
        }

        Connections.Clear();
        Nodes.Clear();

        foreach (var node in Document.Nodes)
        {
            Nodes.Add(CreateNodeViewModel(node));
        }

        foreach (var connection in Document.Connections)
        {
            AddConnectionViewModel(connection);
        }

        RefreshGateRoles();
        Revalidate();
    }

    private RecipeNodeViewModel CreateNodeViewModel(RecipeNode node)
    {
        var vm = new RecipeNodeViewModel(node, _settings, _dialogs, _klaStore);
        vm.Changed += OnNodeChanged;
        return vm;
    }

    private void AddConnectionViewModel(RecipeConnection connection)
    {
        var source = Nodes.FirstOrDefault(n => n.Id == connection.SourceNodeId);
        var target = Nodes.FirstOrDefault(n => n.Id == connection.TargetNodeId);
        if (source is not null && target is not null)
        {
            Connections.Add(new RecipeConnectionViewModel(connection, source, target, () => Nodes));
        }
    }

    private void OnNodeChanged()
    {
        RecomputeConnections();
        MarkDirty();
        RefreshGateRoles();
        Revalidate();
        CanvasBoundsChanged?.Invoke();
    }

    /// <summary>
    /// Marks each Intervenção Manual / Monitorar node that is the target of a cascade's Saída Loop,
    /// so its editor shows the loop-role wording (Continuar/Pular Cascata).
    /// </summary>
    private void RefreshGateRoles()
    {
        var gateTargets = Document.Connections
            .Where(c => ConnectorNames.IsLoopOut(c.SourceConnector)
                        && c.TargetNodeId != c.SourceNodeId
                        && Document.Node(c.SourceNodeId)?.Type == NodeType.CascadeControl)
            .Select(c => c.TargetNodeId)
            .ToHashSet();

        var cascadesWithExternalCondition = Document.Connections
            .Where(c => ConnectorNames.IsLoopOut(c.SourceConnector)
                        && c.TargetNodeId != c.SourceNodeId
                        && IsExternalConditionTarget(Document.Node(c.TargetNodeId)?.Type))
            .Select(c => c.SourceNodeId)
            .ToHashSet();

        var cascadesWithInfiniteLoop = Document.Connections
            .Where(ConnectorNames.IsCascadeSelfLoop)
            .Select(c => c.SourceNodeId)
            .ToHashSet();

        foreach (var node in Nodes)
        {
            node.IsCascadeLoopCondition = gateTargets.Contains(node.Id);
            node.IsCascadeInfinite = node.Type == NodeType.CascadeControl
                                     && cascadesWithInfiniteLoop.Contains(node.Id)
                                     && !cascadesWithExternalCondition.Contains(node.Id);
            node.IsCascadeWithoutExitCondition =
                node.Type == NodeType.CascadeControl
                && !cascadesWithExternalCondition.Contains(node.Id)
                && !cascadesWithInfiniteLoop.Contains(node.Id);
        }
    }

    public void SetAlignmentGuides(IEnumerable<RecipeAlignmentGuide> guides)
    {
        AlignmentGuides.Clear();
        foreach (var guide in guides)
        {
            AlignmentGuides.Add(guide);
        }
    }

    public void ClearAlignmentGuides() => AlignmentGuides.Clear();

    private void RecomputeConnections()
    {
        foreach (var connection in Connections)
        {
            connection.Recompute(Nodes);
        }
    }

    private void RemoveCascadeSelfLoop(string nodeId)
    {
        var selfLoops = Document.Connections
            .Where(c => c.SourceNodeId == nodeId && ConnectorNames.IsCascadeSelfLoop(c))
            .ToArray();

        foreach (var selfLoop in selfLoops)
        {
            Document.Connections.Remove(selfLoop);
            if (Connections.FirstOrDefault(c => Same(c.Model, selfLoop)) is { } vm)
            {
                vm.Detach();
                Connections.Remove(vm);
            }
        }
    }

    private static bool IsExternalConditionTarget(NodeType? type)
        => type is NodeType.Timer or NodeType.MonitorVariable or NodeType.ManualIntervention;

    private void Revalidate()
    {
        Document.Name = Name;
        var result = RecipeValidator.Validate(Document);
        IsValid = result.IsValid;

        Findings.Clear();
        foreach (var finding in result.Findings)
        {
            Findings.Add(finding);
        }

        ValidationSummary = result.IsValid
            ? result.Warnings.Count > 0 ? $"✓ Receita válida · {result.Warnings.Count} aviso(s)" : "✓ Receita válida"
            : $"⚠ {result.Errors.Count} problema(s)";

        JsonText = RecipeSerializer.Serialize(Document);
    }

    private void Restore(string json)
    {
        _restoring = true;
        try
        {
            Document = RecipeSerializer.Deserialize(json);
            Name = Document.Name;
            Rebuild();
            MarkDirty();
        }
        finally
        {
            _restoring = false;
        }

        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    private void MarkDirty() => IsDirty = true;

    partial void OnNameChanged(string value)
    {
        Document.Name = value;
        if (!_restoring)
        {
            MarkDirty();
            Revalidate();
        }
    }

    /// <summary>True when the block's Saída Loop already feeds another block — its exit condition.</summary>
    private bool HasLoopExitCondition(string nodeId)
        => Document.Connections.Any(c => c.SourceNodeId == nodeId
                                         && ConnectorNames.IsLoopOut(c.SourceConnector)
                                         && c.TargetNodeId != nodeId);

    private static bool Same(RecipeConnection a, RecipeConnection b)
        => a.SourceNodeId == b.SourceNodeId && a.SourceConnector == b.SourceConnector
           && a.TargetNodeId == b.TargetNodeId && a.TargetConnector == b.TargetConnector;
}
