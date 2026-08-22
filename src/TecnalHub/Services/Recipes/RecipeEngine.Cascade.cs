using TecnalHub.Protocol;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Control;

namespace TecnalHub.Services.Recipes;

// Cascade: the oxygen kLa cascade block — the scientific core. It drives the ported CascadeController
// under CommandOwner.Recipe (one owner, one queue) and computes/actuates each PID interval. Its
// Saída Loop wires to the loop's EXIT CONDITION — a Monitor (automatic) or an Intervenção Manual
// (a Continuar/Pular switch), read each iteration — mirroring ReceitasTECNAL's RecipeEngine.Cascade.
// The gas mixer (enrichment) is deferred, so only the agitation and aeration windows are configured.
public sealed partial class RecipeEngine
{
    /// <summary>How close to the O₂ setpoint counts as settled, when no exit condition is wired.</summary>
    private const double CascadeSettleTolerancePercent = 2.0;

    /// <summary>Consecutive settled frames that end a loop with no exit condition.</summary>
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

        // The Saída Loop wires to the loop's exit condition: a Monitor (automatic) or an Intervenção
        // Manual (a manual Continuar/Pular switch). It is read here, never executed as a flow block.
        var condition = LoopConditionNode(node);
        Log(RecipeLogSeverity.Info,
            $"Cascata O₂ iniciada (SP {node.Number("spO2"):0.#} %{DescribeCondition(condition)}).", node.Id);

        DateTimeOffset? lastStep = null;
        var settled = 0;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                _pauseGate.Wait(ct);

                // Manual exit: the operator flipped the gate to Pular Cascata (Passar). Checked before
                // the frame wait so a skip takes effect promptly.
                if (condition is { Type: NodeType.ManualIntervention } gate &&
                    gate.Enum<ManualGateOperation>("operacao") == ManualGateOperation.Pass)
                {
                    Log(RecipeLogSeverity.Info, "Cascata encerrada pelo operador (Pular Cascata).", node.Id);
                    break;
                }

                await WaitNextFrameAsync(ct).ConfigureAwait(false);
                if (_latest is not { } snapshot || snapshot.OxygenCalibrated <= SensorReadings.NotReceived)
                {
                    continue; // flying blind without a usable O₂ reading; wait for the next frame
                }

                // Automatic exit: the monitored variable met the condition.
                if (condition is { Type: NodeType.MonitorVariable } monitor && MonitorConditionMet(monitor, snapshot))
                {
                    Log(RecipeLogSeverity.Info, "Cascata: condição de saída atingida.", node.Id);
                    break;
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
                NodeStateChanged?.Invoke(node.Id); // refresh the live P/I/D/Saída terms

                if (!_arbiter.Dispatch(CommandOwner.Recipe, CascadeController.BuildCommand(result)).Accepted)
                {
                    Log(RecipeLogSeverity.Warning, "Cascata: posse dos atuadores perdida; encerrando.", node.Id);
                    break;
                }

                // With no exit condition wired, the cascade settles once the O2 stabilizes at the setpoint.
                if (condition is null)
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

    /// <summary>The node wired to the cascade's Saída Loop — its exit condition, or null.</summary>
    private RecipeNode? LoopConditionNode(RecipeNode cascade)
    {
        var loopEdge = Current!.Connections.FirstOrDefault(c =>
            c.SourceNodeId == cascade.Id && ConnectorNames.IsLoopOut(c.SourceConnector));
        return loopEdge is null ? null : Current.Node(loopEdge.TargetNodeId);
    }

    private static string DescribeCondition(RecipeNode? condition) => condition?.Type switch
    {
        NodeType.MonitorVariable => ", saída por condição",
        NodeType.ManualIntervention => ", saída manual",
        _ => "",
    };

    private static bool MonitorConditionMet(RecipeNode monitor, SensorSnapshot snapshot)
    {
        var value = MeasuredValue(snapshot, monitor.Enum<MeasuredVariable>("variavel"));
        return value is { } reading
            && Satisfies(reading, monitor.Enum<ComparisonOperator>("condicao"), monitor.Number("valorAlvo"));
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
}
