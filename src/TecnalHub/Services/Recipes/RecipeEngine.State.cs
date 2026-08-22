namespace TecnalHub.Services.Recipes;

// State tracking: run state, per-block state, the executed-path set, and the outward events.
public sealed partial class RecipeEngine
{
    public RecipeRunState State { get; private set; } = RecipeRunState.Idle;

    public string? StatusReason { get; private set; }

    public event Action<string>? NodeStateChanged;

    public event Action? StateChanged;

    public event Action<RecipeLogEntry>? Logged;

    public bool ApplyLiveTuning(RecipeNode node)
    {
        return false;
    }

    public Control.CascadeTerms? CascadeTermsFor(string nodeId)
    {
        return null;
    }

    private void SetState(RecipeRunState state, string? reason = null)
    {
        if (reason is not null)
        {
            StatusReason = reason;
        }

        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke();
    }

    public NodeState NodeStateOf(string nodeId)
    {
        lock (_lock)
        {
            return _nodeStates.GetValueOrDefault(nodeId, NodeState.Waiting);
        }
    }

    private void ResetNodeStates(RecipeDocument recipe)
    {
        lock (_lock)
        {
            _nodeStates.Clear();
            foreach (var node in recipe.Nodes)
            {
                _nodeStates[node.Id] = NodeState.Waiting;
            }
        }

        foreach (var node in recipe.Nodes)
        {
            NodeStateChanged?.Invoke(node.Id);
        }
    }

    private void SetNodeState(string nodeId, NodeState state)
    {
        lock (_lock)
        {
            if (_nodeStates.GetValueOrDefault(nodeId) == state)
            {
                return;
            }

            _nodeStates[nodeId] = state;
        }

        NodeStateChanged?.Invoke(nodeId);
    }

    /// <summary>True when a block reached <see cref="NodeState.Completed"/> in this run.</summary>
    private bool IsCompleted(string nodeId) => NodeStateOf(nodeId) == NodeState.Completed;

    public bool WasTraversed(RecipeConnection connection)
    {
        lock (_lock)
        {
            return _executedConnections.Contains(ConnectionKey(connection));
        }
    }

    private void MarkTraversed(RecipeConnection connection)
    {
        lock (_lock)
        {
            _executedConnections.Add(ConnectionKey(connection));
        }
    }

    /// <summary>A stable key for a connection, used for the executed-path set and join idempotency.</summary>
    private static string ConnectionKey(RecipeConnection c)
        => $"{c.SourceNodeId}|{c.SourceConnector}->{c.TargetNodeId}|{c.TargetConnector}";

    private void Log(RecipeLogSeverity severity, string message, string? nodeId = null)
    {
        Logged?.Invoke(new RecipeLogEntry(severity, message, nodeId));

        _journal?.Add(
            Telemetry.AuditSource.Recipe,
            severity switch
            {
                RecipeLogSeverity.Error => Telemetry.AuditSeverity.Error,
                RecipeLogSeverity.Warning => Telemetry.AuditSeverity.Warning,
                _ => Telemetry.AuditSeverity.Information,
            },
            message);
    }
}
