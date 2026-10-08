using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Control;

namespace OpenTECHub.Services.Recipes;

// Cascade: the oxygen kLa cascade block — the scientific core. It drives the ported CascadeController
// under CommandOwner.Recipe (one owner, one queue) and computes/actuates each PID interval. Its
// Saída Loop wires to the loop's EXIT CONDITION — a Monitor (automatic) or an Intervenção Manual
// (a Continuar/Pular switch), read each iteration — mirroring ReceitasOpenTEC's RecipeEngine.Cascade.
// The gas mixer (enrichment) is deferred, so only the agitation and aeration windows are configured.
public sealed partial class RecipeEngine
{
    /// <summary>How close to the O₂ setpoint counts as settled, when no exit condition is wired.</summary>
    private const double CascadeSettleTolerancePercent = 2.0;

    /// <summary>Consecutive settled frames that end a loop with no exit condition.</summary>
    private const int CascadeSettleFrames = 3;

    /// <summary>Live controllers, keyed by cascade block id, for the live-terms readout and tuning.</summary>
    private readonly Dictionary<string, CascadeController> _liveCascades = [];
    private readonly Dictionary<string, RecipeCascadeSuspensionGate> _cascadeGates = [];

    private sealed class CascadeResourceProducer(string nodeId, RecipeCascadeSuspensionGate gate,
        Func<ControllerReturnSnapshot> capture) : IRecipeResourceProducer
    {
        public string NodeId => nodeId;
        public IReadOnlyList<ActuatorId> Resources { get; } = [ActuatorId.Agitation, ActuatorId.Aeration, ActuatorId.Oxygen];
        public async Task<IRecipeResourceSuspension> SuspendAsync(CancellationToken ct)
            => new CascadeReturnSuspension(await gate.PauseAsync(ct).ConfigureAwait(false), capture);
    }

    private sealed class CascadeReturnSuspension(RecipeCascadeSuspensionGate.PauseReceipt pause,
        Func<ControllerReturnSnapshot> capture) : IRecipeResourceSuspension
    {
        private ControllerReturnSnapshot? _captured;
        public bool CanResume => pause.CanResume && (_captured is null || capture() == _captured);
        public ControllerReturnSnapshot CaptureControllerState()
        {
            if (!CanResume) throw new InvalidOperationException("Cascata encerrada ou estado alterado durante cessão.");
            return _captured ??= capture();
        }
        public void Resume() => pause.Resume();
        public void Stop() => pause.Stop();
    }

    private async Task ExecuteCascadeAsync(RecipeNode node, CancellationToken ct)
    {
        CascadeController? controller = null;
        using var suspension = new RecipeCascadeSuspensionGate(() => Volatile.Read(ref _frameVersion));
        RecipeCascadePeriodicGroup? periodic = null;
        using var groupStopped = new CancellationTokenSource();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, suspension.StopToken, groupStopped.Token);
        Resources?.Register(new CascadeResourceProducer(node.Id, suspension, () =>
        {
            lock (_lock)
                return new ControllerReturnSnapshot { ControllerId = node.Id, WasActive = controller is not null,
                    StateJson = controller?.CaptureStateJson() ?? "{}", StateVersion = "recipe-cascade-v1" };
        }));
        lock (_lock) _cascadeGates.Add(node.Id, suspension);
        long resumeVersion = 0;

        // The Condição de Saída port wires to the rule that ends the loop: a Monitor (exits when the
        // comparison becomes true), a Temporizador (exits once it elapses) or an Intervenção Manual
        // (the operator's Manter Rodando / Sair do Loop switch). Every one of them is *read* once per
        // iteration and never executed as a flow block. The internal self-loop is the
        // explicit continuous mode; with no loop edge the finite mode settles below.
        var condition = LoopConditionNode(node);
        var infinite = HasCascadeSelfLoop(node);
        var mode = node.Enum<CascadeMode>("modo");
        Log(RecipeLogSeverity.Info,
            $"Controle de O₂ [{ModeLabel(mode)}] iniciado (SP {node.Number("spO2"):0.#} %{DescribeCondition(condition, infinite)}).", node.Id);

        long? lastStep = null;
        var settled = 0;
        var loopStarted = _time.GetUtcNow();

        // A Monitorar Variável keeps the debounce and timeout it obeys in the normal flow, so the
        // same block does not mean two different things depending on where it is wired.
        var monitorConfirmations = 0;
        // Begin with the current observation, then preserve subsequent frames for debounce.
        long observedFrame;
        lock (_lock) observedFrame = _frameVersion - 1;
        var monitorDeadline = condition is { Type: NodeType.MonitorVariable } cond
                              && cond.Number("tempoLimiteMs") > 0
            ? loopStarted + TimeSpan.FromMilliseconds(cond.Number("tempoLimiteMs"))
            : (DateTimeOffset?)null;
        var exitDeadline = condition is { Type: NodeType.Timer } timed
            ? loopStarted + TimeSpan.FromSeconds(DurationSeconds(timed.Number("duracao"), timed.Enum<TimeUnit>("unidade")))
            : monitorDeadline;

        try
        {
            periodic = StartPeriodicGroup(node.Id, ct);
            using var groupStopRegistration = periodic?.StopToken.Register(() => groupStopped.Cancel());
            while (true)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                _pauseGate.Wait(lifetime.Token);

                // Manual exit: the operator flipped the switch to Sair do Loop (Passar). Checked before
                // the frame wait so leaving the loop takes effect promptly.
                if (condition is { Type: NodeType.ManualIntervention } gate &&
                    gate.Enum<ManualGateOperation>("operacao") == ManualGateOperation.Pass)
                {
                    Log(RecipeLogSeverity.Info, "Controle de O₂ encerrado pelo operador (Sair do Loop).", node.Id);
                    break;
                }

                // Timed exit: the loop runs for the block's duration, then leaves. Also checked before
                // the frame wait, so the loop ends on time even if telemetry has gone quiet.
                if (condition is { Type: NodeType.Timer } timer &&
                    (_time.GetUtcNow() - loopStarted).TotalSeconds
                        >= DurationSeconds(timer.Number("duracao"), timer.Enum<TimeUnit>("unidade")))
                {
                    Log(RecipeLogSeverity.Info,
                        $"Controle de O₂ encerrado por tempo ({timer.Number("duracao"):0.##} "
                        + $"{UnitLabel(timer.Enum<TimeUnit>("unidade"))}).", node.Id);
                    break;
                }

                var frame = await WaitCascadeFrameAsync(observedFrame, exitDeadline, lifetime.Token).ConfigureAwait(false);
                if (frame.Snapshot is null)
                {
                    Log(RecipeLogSeverity.Info, "Controle de O₂ encerrado pelo prazo da condição de saída.", node.Id);
                    break;
                }
                if (observedFrame >= 0 && frame.Version > observedFrame + 1) monitorConfirmations = 0;
                observedFrame = frame.Version;

                // Automatic exit, evaluated BEFORE the cascade's own O₂ guard: a condition on
                // temperature or pH must not be held hostage by a silent O₂ probe, and the timeout
                // has to be able to fire even when no reading is arriving at all.
                if (condition is { Type: NodeType.MonitorVariable } monitor
                    && MonitorExitReached(monitor, frame.Snapshot, ref monitorConfirmations, monitorDeadline, node.Id))
                {
                    break;
                }

                // Conditions consume every buffered observation, but actuators use only the newest.
                if (frame.Version < Volatile.Read(ref _frameVersion)) continue;

                var snapshot = frame.Snapshot;
                if (snapshot.OxygenCalibrated <= SensorReadings.NotReceived)
                {
                    continue; // flying blind without a usable O₂ reading; wait for the next frame
                }

                // The lease covers computation and enqueue. Exit rules above keep observing during suspension.
                using var step = suspension.TryEnterStep();
                if (step is null || frame.Version <= step.IgnoreFramesThrough) continue;

                if (controller is null)
                {
                    try
                    {
                        controller = BuildCascadeController(node, snapshot);
                    }
                    catch (Exception ex)
                    {
                        Log(RecipeLogSeverity.Error, $"Erro ao inicializar controle de O₂: {ex.Message}", node.Id);
                        throw;
                    }

                    lock (_lock)
                    {
                        _liveCascades[node.Id] = controller;
                    }
                }

                var now = _time.GetTimestamp();
                if (step.ResumeVersion != resumeVersion)
                {
                    lock (_lock) controller.ResumeFromSuspension(snapshot.OxygenCalibrated);
                    lastStep = now;
                    resumeVersion = step.ResumeVersion;
                    settled = 0;
                    NodeStateChanged?.Invoke(node.Id);
                    continue; // Hold restored effort on the rebase frame; integrate only on a subsequent active sample.
                }
                var dt = lastStep is { } last ? _time.GetElapsedTime(last, now).TotalSeconds : node.Number("intervaloPidS");
                if (dt <= 0)
                {
                    dt = node.Number("intervaloPidS");
                }

                lastStep = now;

                // The one place the recipe cascade meets the wire, under Recipe ownership.
                CascadeActuationResult result;
                lock (_lock) result = controller.Update(snapshot.OxygenCalibrated, dt);
                NodeStateChanged?.Invoke(node.Id); // refresh the live P/I/D/Saída terms

                if (!_arbiter.Dispatch(CommandOwner.Recipe, CascadeController.BuildCommand(result, _settings.Current.GasRig.ToConfiguration())).Accepted)
                {
                    Log(RecipeLogSeverity.Warning, "Controle de O₂: posse dos atuadores perdida; encerrando.", node.Id);
                    break;
                }

                // A self-loop means continuous operation. Only the explicit no-loop
                // state uses the existing ±2% / 3-reading settle rule.
                if (condition is null && !infinite)
                {
                    settled = Math.Abs(snapshot.OxygenCalibrated - node.Number("spO2")) <= CascadeSettleTolerancePercent
                        ? settled + 1
                        : 0;

                    if (settled >= CascadeSettleFrames)
                    {
                        Log(RecipeLogSeverity.Info, "Controle de O₂: O₂ estabilizado no setpoint.", node.Id);
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && suspension.StopToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Controle de O₂ encerrado por falha da cessão de atuadores.");
        }
        finally
        {
            try
            {
                if (periodic is not null) await periodic.StopAndWaitAsync().ConfigureAwait(false);
            }
            finally
            {
                await CloseCascadeRampsAsync(node.Id).ConfigureAwait(false);
                lock (_lock) _periodicGroups.Remove(node.Id);
                if (!_graphPeriodicBindings.Values.Any(b => b.CascadeNodeId == node.Id)) periodic?.Dispose();
                suspension.Stop();
                Resources?.Unregister(node.Id);
                lock (_lock)
                {
                    _liveCascades.Remove(node.Id);
                    _cascadeGates.Remove(node.Id);
                }
            }
        }
    }

    private static string ModeLabel(CascadeMode mode) => mode switch
    {
        CascadeMode.AgitationOnly => "Agitação",
        CascadeMode.AerationOnly => "Aeração",
        CascadeMode.DualCascade => "Cascata (percentuais)",
        CascadeMode.KlaPath => "Mapa (trajetória kLa)",
        _ => mode.ToString(),
    };

    /// <summary>The node wired to the cascade's Saída Loop — its exit condition, or null.</summary>
    /// <remarks>
    /// More than one edge can leave the Saída Loop — a hand-drawn self-loop back into Entrada Loop
    /// alongside a real gate, say. The condition is therefore chosen by kind, not by position: the
    /// self-loop carries no condition and is skipped, and a Monitor / Intervenção Manual wins over
    /// anything else. Taking the first edge would make the loop's exit depend on the order the
    /// operator happened to draw the connections.
    /// </remarks>
    private RecipeNode? LoopConditionNode(RecipeNode cascade)
    {
        RecipeNode? fallback = null;

        foreach (var edge in Current!.Connections.Where(c =>
                     c.SourceNodeId == cascade.Id && ConnectorNames.IsLoopOut(c.SourceConnector)))
        {
            if (edge.TargetNodeId == cascade.Id)
            {
                continue; // a self-loop is the loop turning on itself, not an exit condition
            }

            var target = Current.Node(edge.TargetNodeId);
            if (target?.Type is NodeType.MonitorVariable or NodeType.ManualIntervention or NodeType.Timer)
            {
                return target;
            }

            fallback ??= target;
        }

        return fallback;
    }

    private bool HasCascadeSelfLoop(RecipeNode cascade)
        => Current!.Connections.Any(c => c.SourceNodeId == cascade.Id && ConnectorNames.IsCascadeSelfLoop(c));

    private static string DescribeCondition(RecipeNode? condition, bool infinite) => condition?.Type switch
    {
        NodeType.MonitorVariable => ", saída por condição",
        NodeType.ManualIntervention => ", saída manual",
        NodeType.Timer => ", saída por tempo",
        _ when infinite => ", execução contínua",
        _ => ", saída por estabilização",
    };

    /// <summary>
    /// Evaluates a Monitorar Variável acting as the loop's exit condition, honouring the same
    /// <c>confirmacoes</c> debounce and <c>tempoLimiteMs</c> timeout it obeys as a normal flow block.
    /// </summary>
    /// <remarks>
    /// <c>intervaloPollingMs</c> has no meaning here — the cascade's own <c>intervaloPidS</c> sets
    /// the cadence — so the editor hides that field while the block is wired as a condition.
    /// Parameters are read fresh on every pass, so a live edit takes effect mid-run.
    /// </remarks>
    private bool MonitorExitReached(
        RecipeNode monitor,
        SensorSnapshot? snapshot,
        ref int consecutive,
        DateTimeOffset? deadline,
        string cascadeId)
    {
        var variable = monitor.Enum<MeasuredVariable>("variavel");

        if (snapshot is not null && MeasuredValue(snapshot, variable) is { } value)
        {
            consecutive = Satisfies(value, monitor.Enum<ComparisonOperator>("condicao"), monitor.Number("valorAlvo"))
                ? consecutive + 1
                : 0; // a single frame that misses resets the run, exactly as in the normal flow

            if (consecutive >= Math.Max(1, (int)monitor.Number("confirmacoes")))
            {
                Log(RecipeLogSeverity.Info,
                    $"Controle de O₂: condição de saída atingida ({variable} = {value:0.##}).", cascadeId);
                return true;
            }
        }

        if (deadline is { } d && _time.GetUtcNow() >= d)
        {
            Log(RecipeLogSeverity.Warning,
                "Tempo limite da condição de saída atingido; encerrando o controle de O₂.", cascadeId);
            return true;
        }

        return false;
    }

    /// <summary>Builds a controller from the block's parameters (§5.3.7 defaults on a fresh block).</summary>
    private CascadeController BuildCascadeController(RecipeNode node, SensorSnapshot? snapshot = null)
    {
        var mode = node.Enum<CascadeMode>("modo");

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

        var controller = new CascadeController(BuildTuning(node), agitation, aeration, node.Number("spO2"));

        var currentAgitation = node.Number("nMinRpm");
        var currentAeration = snapshot?.FlowRate is { } fr && fr >= 0 ? fr : node.Number("qMinVvm");

        CascadeAllocation allocation = mode switch
        {
            CascadeMode.AgitationOnly => SingleActuatorAllocation.Agitation(
                node.Number("nMinRpm"), node.Number("nMaxRpm"), currentAeration),
            CascadeMode.AerationOnly => SingleActuatorAllocation.Aeration(
                node.Number("qMinVvm"), node.Number("qMaxVvm"), currentAgitation),
            CascadeMode.DualCascade => new WindowAllocation(agitation, aeration),
            CascadeMode.KlaPath => BuildKlaPathAllocation(node),
            _ => new WindowAllocation(agitation, aeration),
        };

        controller.SetAllocation(allocation);
        return controller;
    }

    private CascadeAllocation BuildKlaPathAllocation(RecipeNode node)
    {
        var mapId = node.Text("klaMapId");
        if (_klaStore != null && !string.IsNullOrWhiteSpace(mapId))
        {
            try
            {
                var profiles = _klaStore.LoadPublishedAsync().GetAwaiter().GetResult();
                var profile = profiles.FirstOrDefault(p =>
                    string.Equals(p.ReceiptFingerprint, mapId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p.Name, mapId, StringComparison.OrdinalIgnoreCase));
                if (profile != null)
                {
                    return new KlaPathAllocation(profile.Payload.Allocation);
                }
            }
            catch (Exception ex)
            {
                Log(RecipeLogSeverity.Error, $"Falha ao carregar mapa kLa '{mapId}': {ex.Message}", node.Id);
                throw;
            }
        }

        throw new InvalidOperationException($"Mapeamento kLa '{mapId}' não encontrado para o bloco de O₂.");
    }

    /// <summary>Projects the block's gain/anti-windup/prediction parameters onto the controller tuning.</summary>
    private static CascadeTuning BuildTuning(RecipeNode node)
    {
        var mode = node.Enum<CascadeMode>("modo");
        return new CascadeTuning
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
            FatorGanhoAeracao = node.Number("aeracaoGanho"),
            HabilitarGainScheduling = mode == CascadeMode.DualCascade,
        };
    }
}
