using TecnalHub.Protocol;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Control;

namespace TecnalHub.Services.Recipes;

// Cascade: the oxygen kLa cascade block — the scientific core. It drives the ported CascadeController
// under CommandOwner.Recipe (one owner, one queue), fires the loop body each iteration, and holds or
// terminates per the block's Laço setting. The gas mixer (enrichment) is deferred, so only the
// agitation and aeration windows are configured.
public sealed partial class RecipeEngine
{
    /// <summary>How close to the O₂ setpoint counts as settled, for a finite (non-infinite) loop.</summary>
    private const double CascadeSettleTolerancePercent = 2.0;

    /// <summary>Consecutive settled frames that end a finite loop.</summary>
    private const int CascadeSettleFrames = 3;

    /// <summary>Live controllers, keyed by cascade block id, for the live-terms readout and tuning.</summary>
    private readonly Dictionary<string, CascadeController> _liveCascades = [];

    private async Task ExecuteCascadeAsync(RecipeNode node, CancellationToken ct)
    {
        var controller = BuildCascadeController(node);
        lock (_lock)
        {
            _liveCascades[node.Id] = controller;
        }

        Log(RecipeLogSeverity.Info, $"Cascata O₂ iniciada (SP {node.Number("spO2"):0.#} %).", node.Id);

        var loopInfinite = node.Flag("loopInfinito");
        var loopEdges = Current!.Connections
            .Where(c => c.SourceNodeId == node.Id && ConnectorNames.IsLoopOut(c.SourceConnector))
            .ToList();
        var bodyNodes = CollectLoopBody(node, loopEdges);

        DateTimeOffset? lastStep = null;
        var settled = 0;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                _pauseGate.Wait(ct);

                await WaitNextFrameAsync(ct).ConfigureAwait(false);
                if (_latest is not { } snapshot || snapshot.OxygenCalibrated <= SensorReadings.NotReceived)
                {
                    continue; // flying blind without a usable O₂ reading; wait for the next frame
                }

                var now = _time.GetUtcNow();
                var dt = lastStep is { } last ? (now - last).TotalSeconds : node.Number("intervaloPidS");
                if (dt <= 0)
                {
                    dt = node.Number("intervaloPidS");
                }

                lastStep = now;

                // The one place the recipe cascade meets the wire, under Recipe ownership.
                var result = controller.Update(snapshot.OxygenCalibrated, dt);
                NodeStateChanged?.Invoke(node.Id); // refresh the live P/I/D/Saída terms pane

                if (!_arbiter.Dispatch(CommandOwner.Recipe, CascadeController.BuildCommand(result)).Accepted)
                {
                    Log(RecipeLogSeverity.Warning, "Cascata: posse dos atuadores perdida; encerrando.", node.Id);
                    break;
                }

                // Fire the loop body once per iteration, resetting it so it re-runs each pass.
                if (loopEdges.Count > 0)
                {
                    ResetLoopBody(bodyNodes);
                    foreach (var edge in loopEdges)
                    {
                        MarkTraversed(edge);
                        await ExecuteFlowAsync(Current.Node(edge.TargetNodeId), edge, ct).ConfigureAwait(false);
                    }

                    if (LoopBodyRequestsExit(bodyNodes))
                    {
                        Log(RecipeLogSeverity.Info, "Cascata encerrada pelo operador (Pular Cascata).", node.Id);
                        break;
                    }
                }

                // A finite loop ends once O₂ has held at the setpoint; an infinite loop holds until
                // stopped, or until a loop-body Intervenção Manual passes (handled above).
                if (!loopInfinite)
                {
                    settled = Math.Abs(snapshot.OxygenCalibrated - node.Number("spO2")) <= CascadeSettleTolerancePercent
                        ? settled + 1
                        : 0;

                    if (settled >= CascadeSettleFrames)
                    {
                        Log(RecipeLogSeverity.Info, "Cascata: O₂ estabilizado no setpoint.", node.Id);
                        break;
                    }
                }
            }
        }
        finally
        {
            lock (_lock)
            {
                _liveCascades.Remove(node.Id);
            }
        }
    }

    /// <summary>Builds a controller from the block's parameters (§5.3.7 defaults on a fresh block).</summary>
    private static CascadeController BuildCascadeController(RecipeNode node)
    {
        var agitation = new ActuatorWindow(
            CascadeController.AgitationActuator,
            node.Number("nMinRpm"), node.Number("nMaxRpm"),
            node.Number("agitacaoOutMin"), node.Number("agitacaoOutMax"));

        // The aeration window is applied directly in flow units; the vvm→L/min coupling (× reactor
        // volume) arrives with the proportional-gas work (Phase 3 WP2).
        var aeration = new ActuatorWindow(
            CascadeController.AerationActuator,
            node.Number("qMinVvm"), node.Number("qMaxVvm"),
            node.Number("aeracaoOutMin"), node.Number("aeracaoOutMax"));

        return new CascadeController(BuildTuning(node), agitation, aeration, node.Number("spO2"));
    }

    /// <summary>Projects the block's gain/anti-windup/prediction parameters onto the controller tuning.</summary>
    private static CascadeTuning BuildTuning(RecipeNode node) => new()
    {
        Kp = node.Number("kp"),
        Ki = node.Number("ki"),
        Kd = node.Number("kd"),
        IntegralMin = node.Number("iMin"),
        IntegralMax = node.Number("iMax"),
        OutputMin = 0,
        OutputMax = 100,
        PredictionHorizonSeconds = node.Number("horizonteTPredS"),
        // The rate-estimation window in seconds ≈ the sample count × the PID interval.
        RateWindowSeconds = Math.Max(1.0, node.Number("janelaMediaAmostras") * node.Number("intervaloPidS")),
        IntervalSeconds = Math.Clamp(node.Number("intervaloPidS"), 0.1, 60),
    };

    /// <summary>The block ids inside the cascade's loop, reachable from its Saída Loop port.</summary>
    private HashSet<string> CollectLoopBody(RecipeNode cascade, IReadOnlyList<RecipeConnection> loopEdges)
    {
        var body = new HashSet<string>();
        var stack = new Stack<string>(loopEdges.Select(e => e.TargetNodeId));
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (id == cascade.Id || !body.Add(id))
            {
                continue;
            }

            foreach (var edge in Current!.Connections.Where(c =>
                         c.SourceNodeId == id && !ConnectorNames.IsLoopIn(c.TargetConnector) && c.TargetNodeId != cascade.Id))
            {
                stack.Push(edge.TargetNodeId);
            }
        }

        return body;
    }

    /// <summary>Resets the loop body's block and join state so it re-runs cleanly next iteration.</summary>
    private void ResetLoopBody(HashSet<string> bodyNodes)
    {
        lock (_lock)
        {
            foreach (var id in bodyNodes)
            {
                _joinFired.Remove(id);
                _joinArrivals.Remove(id);
                _orWinner.Remove(id);
            }
        }

        foreach (var id in bodyNodes)
        {
            SetNodeState(id, NodeState.Waiting);
        }
    }

    /// <summary>True when a loop-body Intervenção Manual is set to Passar ("Pular Cascata").</summary>
    private bool LoopBodyRequestsExit(HashSet<string> bodyNodes)
        => bodyNodes
            .Select(id => Current!.Node(id))
            .Any(n => n is { Type: NodeType.ManualIntervention }
                      && n.Enum<ManualGateOperation>("operacao") == ManualGateOperation.Pass);
}
