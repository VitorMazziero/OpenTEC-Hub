namespace OpenTECHub.Services.Recipes;

// Flow: walking the graph from Início, fanning out parallel branches, and the idempotent AND/OR
// join gates. Ported from ReceitasOpenTEC's RecipeEngine.Flow.
public sealed partial class RecipeEngine
{
    private readonly Dictionary<string, HashSet<string>> _joinArrivals = [];
    private readonly HashSet<string> _joinFired = [];
    private readonly Dictionary<string, string> _orWinner = [];

    private void ResetFlowState()
    {
        lock (_lock)
        {
            _joinArrivals.Clear();
            _joinFired.Clear();
            _orWinner.Clear();
            _closingRampCascades.Clear();
        }
    }

    /// <summary>
    /// Walks a single flow strand from <paramref name="node"/>, executing each block and following
    /// its normal-flow output. A block with several outputs fans out into parallel branches; a join
    /// block gates propagation so a diamond does not double-fire the downstream.
    /// </summary>
    private async Task ExecuteFlowAsync(RecipeNode? node, RecipeConnection? entryConnection, CancellationToken ct)
    {
        var current = node;
        var arrival = entryConnection;

        while (current is not null)
        {
            ct.ThrowIfCancellationRequested();
            _pauseGate.Wait(ct);

            var isJoin = current.Type is NodeType.And or NodeType.Or;
            if (isJoin && !RegisterJoinArrivalAndShouldFire(current, arrival))
            {
                return; // this arrival does not fire the join; another branch will (or already did)
            }

            if (!isJoin && (IsCompleted(current.Id) || NodeStateOf(current.Id) == NodeState.Cancelled))
            {
                // A diamond without an explicit join re-reached this block; run it once only.
                return;
            }

            if (!(isJoin && IsCompleted(current.Id)))
            {
                if (!await ExecuteNodeWithStateAsync(current, ct).ConfigureAwait(false)) return;
            }

            if (current.Type == NodeType.Periodic) return; // Its owned target returns to the scheduler.

            var outputs = Current!.OutgoingFrom(current.Id).ToList();

            // A loop-return edge (target = Entrada Loop) ends this strand: the cascade block, not
            // the flow walk, drives the next iteration, so we must not re-enter the cascade here.
            foreach (var loopReturn in outputs.Where(o => ConnectorNames.IsLoopIn(o.TargetConnector)))
            {
                MarkTraversed(loopReturn);
            }

            outputs = outputs.Where(o => !ConnectorNames.IsLoopIn(o.TargetConnector)).ToList();
            if (outputs.Count == 0)
            {
                return; // end of this strand (a Fim block, a loop return, or a dangling output)
            }

            if (outputs.Count == 1)
            {
                MarkTraversed(outputs[0]);
                arrival = outputs[0];
                current = Current.Node(outputs[0].TargetNodeId);
                continue;
            }

            await FanOutAsync(outputs, ct).ConfigureAwait(false);
            return;
        }
    }

    private async Task FanOutAsync(IReadOnlyList<RecipeConnection> outputs, CancellationToken ct)
    {
        var branches = outputs.Select(edge => (Func<CancellationToken, Task>)(branchToken =>
        {
            MarkTraversed(edge);
            return ExecuteFlowAsync(Current!.Node(edge.TargetNodeId), edge, branchToken);
        })).ToArray();

        await RecipeParallelGroup.RunAsync(branches, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records an arrival at a join and decides whether <b>this</b> arrival fires it. Idempotent, so
    /// a re-walk never double-fires: AND fires when the set of distinct inbound edges is covered; OR
    /// fires on its first winning edge. A guard set caps the join at one fire per run.
    /// </summary>
    private bool RegisterJoinArrivalAndShouldFire(RecipeNode join, RecipeConnection? arrival)
    {
        var key = arrival is null ? $"__direct__->{join.Id}" : ConnectionKey(arrival);

        lock (_lock)
        {
            if (join.Type == NodeType.And)
            {
                var required = Current!.IncomingTo(join.Id).Count(c => !ConnectorNames.IsLoopIn(c.TargetConnector));
                if (!_joinArrivals.TryGetValue(join.Id, out var arrived))
                {
                    arrived = [];
                    _joinArrivals[join.Id] = arrived;
                }

                arrived.Add(key);
                if (arrived.Count < Math.Max(1, required))
                {
                    return false;
                }

                return _joinFired.Add(join.Id);
            }

            // OR: the first edge to arrive wins; the same edge re-walked is idempotent.
            if (!_orWinner.TryGetValue(join.Id, out var winner))
            {
                winner = key;
                _orWinner[join.Id] = key;
            }

            return winner == key && _joinFired.Add(join.Id);
        }
    }

    private async Task<bool> ExecuteNodeWithStateAsync(RecipeNode node, CancellationToken ct)
    {
        SetNodeState(node.Id, NodeState.Evaluating);
        try
        {
            if (node.Type == NodeType.LinearSetpointRamp)
            {
                if (!await ExecuteRampAsync(node, ct).ConfigureAwait(false))
                {
                    SetNodeState(node.Id, NodeState.Cancelled);
                    return false;
                }
            }
            else await ExecuteNodeAsync(node, ct).ConfigureAwait(false);
            SetNodeState(node.Id, NodeState.Completed);
            return true;
        }
        catch (OperationCanceledException)
        {
            SetNodeState(node.Id, NodeState.Error);
            throw;
        }
        catch (Exception ex)
        {
            SetNodeState(node.Id, NodeState.Error);
            Log(RecipeLogSeverity.Error, $"Erro no bloco '{node.Definition.Title}': {ex.Message}", node.Id);
            throw;
        }
    }
}
