using TecnalHub.Protocol;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Persistence;
using TecnalHub.Services.Recipes;
using Xunit;

namespace TecnalHub.Tests;

/// <summary>
/// The recipe execution engine (Phase 3 WP4 part 2): it deactivates manual control by claiming every
/// actuator on start, drives the shared arbiter under <see cref="CommandOwner.Recipe"/>, reacts to
/// live telemetry, and safe-aborts on link loss. Driven against an injected clock and a recording
/// device, with capped delays so the async run finishes fast without wall-clock waits.
/// </summary>
public sealed class RecipeEngineTests
{
    private static (RecipeEngine Engine, RecordingDeviceService Device, CommandArbiter Arbiter, TestClock Clock) Build()
    {
        var device = new RecordingDeviceService();
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var arbiter = new CommandArbiter(device, clock);
        var settings = new MemorySettingsService(new AppSettings());
        var engine = new RecipeEngine(arbiter, arbiter, settings, clock, journal: null, delay: CappedDelay);
        return (engine, device, arbiter, clock);
    }

    /// <summary>Real but tiny delays: fast enough for a test, cooperative enough to observe cancellation.</summary>
    private static Task CappedDelay(TimeSpan requested, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(requested.TotalMilliseconds, 0, 5)), ct);

    [Fact]
    public async Task Starting_claims_every_actuator_so_manual_control_is_deactivated()
    {
        var (engine, _, arbiter, _) = Build();

        await engine.StartAsync(HoldingRecipe());

        // The claim runs synchronously in StartAsync, before the run task launches.
        Assert.All(CommandActuators.All, a => Assert.Equal(CommandOwner.Recipe, arbiter.OwnerOf(a)));

        // A manual write is now refused — the manual surfaces are inert while the recipe runs.
        var manual = arbiter.Dispatch(CommandOwner.Manual, CommandBuilders.MotorSetpoint(500));
        Assert.False(manual.Accepted);

        await engine.StopAsync("fim do teste");
    }

    [Fact]
    public async Task Stopping_returns_every_actuator_to_manual()
    {
        var (engine, _, arbiter, _) = Build();
        await engine.StartAsync(HoldingRecipe());

        await engine.StopAsync("operador parou");

        Assert.All(CommandActuators.All, a => Assert.Equal(CommandOwner.Manual, arbiter.OwnerOf(a)));
        Assert.Equal(RecipeRunState.Stopped, engine.State);
    }

    [Fact]
    public void Cannot_start_when_disconnected()
    {
        var (engine, device, _, _) = Build();
        device.PushState(ConnectionState.Disconnected);

        Assert.False(engine.CanStart(SetpointRecipe(SetpointVariable.Temperature, 37), out var reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public async Task Cannot_start_an_invalid_recipe()
    {
        var (engine, _, _, _) = Build();
        var invalid = new RecipeDocument(); // no blocks

        Assert.False(engine.CanStart(invalid, out _));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartAsync(invalid));
    }

    [Fact]
    public async Task Setpoint_block_dispatches_the_wire_frame_under_recipe_ownership()
    {
        var (engine, device, _, _) = Build();

        await engine.StartAsync(SetpointRecipe(SetpointVariable.Temperature, 37));
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(device.Sent, s => s.Contains("tempSetpoint") && s.Contains("37"));
        Assert.Equal(RecipeRunState.Completed, engine.State);
    }

    [Fact]
    public async Task Monitor_block_completes_when_telemetry_satisfies_the_condition()
    {
        var (engine, device, _, _) = Build();

        await engine.StartAsync(MonitorRecipe(MeasuredVariable.Temperature, ComparisonOperator.GreaterOrEqual, 40));
        device.PushTelemetry(new SensorSnapshot { Temperature = 45 });

        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Completed, engine.State);
    }

    [Fact]
    public async Task Link_loss_safe_aborts_the_running_recipe()
    {
        var (engine, device, _, _) = Build();
        await engine.StartAsync(MonitorRecipe(MeasuredVariable.Temperature, ComparisonOperator.GreaterOrEqual, 1000));

        // The arbiter revokes the recipe's ownership on link loss; the engine aborts.
        device.PushState(ConnectionState.Disconnected);

        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Stopped, engine.State);
        Assert.Contains("aborto seguro", engine.StatusReason ?? "");
    }

    [Fact]
    public async Task Cascade_block_dispatches_a_combined_frame_and_settles()
    {
        var (engine, device, _, clock) = Build();

        await engine.StartAsync(CascadeRecipe());

        // Drive dissolved oxygen at the setpoint so the finite loop settles and the recipe finishes.
        for (var i = 0; i < 8; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(3));
            device.PushTelemetry(new SensorSnapshot { OxygenCalibrated = 30 });
            await Task.Delay(10);
        }

        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        // The combined cascade frame carries flow, oxygen and motor together.
        Assert.Contains(device.Sent, s => s.Contains("oxygenMonitor") && s.Contains("motorSetpoint") && s.Contains("flowSetpoint"));
        Assert.Equal(RecipeRunState.Completed, engine.State);
    }

    [Fact]
    public async Task Cascade_loop_exits_when_its_saida_loop_manual_gate_passes()
    {
        var (engine, device, _, clock) = Build();
        var recipe = CascadeWithGateRecipe(out var gate);

        await engine.StartAsync(recipe);
        // A couple of frames while the gate holds Continuar Cascata (Bloquear) — the loop runs.
        for (var i = 0; i < 2; i++) { PushFrame(device, clock, oxygen: 25); await Task.Delay(10); }
        Assert.Equal(RecipeRunState.Running, engine.State);

        // Operator flips the gate to Pular Cascata (Passar): the loop must end and the recipe finish.
        gate.Set("operacao", nameof(ManualGateOperation.Pass));
        PushFrame(device, clock, oxygen: 25);

        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Completed, engine.State);
    }

    [Fact]
    public async Task Cascade_loop_exits_when_its_saida_loop_monitor_condition_is_met()
    {
        var (engine, device, _, clock) = Build();
        var recipe = CascadeWithMonitorRecipe(MeasuredVariable.Temperature, ComparisonOperator.GreaterOrEqual, 40);

        await engine.StartAsync(recipe);
        // Oxygen valid so the cascade runs; temperature below 40 keeps the loop going.
        for (var i = 0; i < 2; i++) { PushFrame(device, clock, oxygen: 25, temperature: 30); await Task.Delay(10); }
        // Temperature reaches the exit condition.
        PushFrame(device, clock, oxygen: 25, temperature: 45);

        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Completed, engine.State);
    }

    [Fact]
    public async Task Cascade_executes_in_agitation_only_mode()
    {
        var (engine, device, _, clock) = Build();
        var recipe = CascadeWithMonitorRecipe(MeasuredVariable.Temperature, ComparisonOperator.GreaterOrEqual, 40);
        var cascadeNode = recipe.Nodes.First(n => n.Type == NodeType.CascadeControl);
        cascadeNode.Set("modo", nameof(TecnalHub.Services.Control.CascadeMode.AgitationOnly));

        await engine.StartAsync(recipe);
        await DriveCascadeUntilCompletion(engine, device, clock);

        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Completed, engine.State);
    }

    [Fact]
    public async Task Cascade_executes_in_aeration_only_mode()
    {
        var (engine, device, _, clock) = Build();
        var recipe = CascadeWithMonitorRecipe(MeasuredVariable.Temperature, ComparisonOperator.GreaterOrEqual, 40);
        var cascadeNode = recipe.Nodes.First(n => n.Type == NodeType.CascadeControl);
        cascadeNode.Set("modo", nameof(TecnalHub.Services.Control.CascadeMode.AerationOnly));

        await engine.StartAsync(recipe);
        await DriveCascadeUntilCompletion(engine, device, clock);

        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Completed, engine.State);
    }

    private static async Task DriveCascadeUntilCompletion(
        RecipeEngine engine,
        RecordingDeviceService device,
        TestClock clock)
    {
        // Keep publishing the satisfied monitor state until the async cascade loop has
        // consumed it. A one-shot frame can arrive before the loop subscribes on a busy CI host.
        for (var i = 0; i < 50 && !engine.Completion.IsCompleted; i++)
        {
            PushFrame(device, clock, oxygen: 25, temperature: i < 2 ? 30 : 45);
            await Task.Delay(10);
        }
    }

    private static void PushFrame(RecordingDeviceService device, TestClock clock, double oxygen, double temperature = 0)
    {
        clock.Advance(TimeSpan.FromSeconds(3));
        device.PushTelemetry(new SensorSnapshot { OxygenCalibrated = oxygen, Temperature = temperature });
    }

    // ── External devices: the flowmeter (air flow) ─────────────────────────────

    [Fact]
    public async Task Flow_setpoint_holds_the_recipe_until_the_flowmeter_echoes_it_back()
    {
        var (engine, device, _, _) = Build();

        await engine.StartAsync(SetpointRecipe(SetpointVariable.Flow, 2.5));
        Assert.True(await Eventually(() => device.Sent.Any(s => s.Contains("flowSetpoint") && s.Contains("2.5"))));

        // An echo from a flowmeter that is not online proves nothing: the block keeps holding.
        device.PushTelemetry(new SensorSnapshot { FlowmeterOnline = false, FlowSetpoint = 2.5 });
        await Task.Delay(30);
        Assert.Equal(RecipeRunState.Running, engine.State);

        device.PushTelemetry(new SensorSnapshot { FlowmeterOnline = true, FlowSetpoint = 2.5 });
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Completed, engine.State);
    }

    [Fact]
    public async Task A_silent_flowmeter_is_reported_as_a_wait_and_the_operator_can_skip_the_block()
    {
        var (engine, device, _, clock) = Build();
        var log = new List<RecipeLogEntry>();
        engine.Logged += entry => { lock (log) { log.Add(entry); } };

        await engine.StartAsync(SetpointRecipe(SetpointVariable.Flow, 2.5));
        Assert.True(await Eventually(() => device.Sent.Any(s => s.Contains("flowSetpoint"))));

        // Past the grace with no usable frame: the engine declares the hold rather than moving on.
        clock.Advance(TimeSpan.FromSeconds(30));
        device.PushTelemetry(new SensorSnapshot { FlowmeterOnline = false });
        Assert.True(await Eventually(() => engine.Waiting is not null));
        Assert.Equal("Fluxômetro", engine.Waiting!.Device);
        Assert.Equal("sp", engine.Waiting.NodeId);
        Assert.Equal(RecipeRunState.Running, engine.State);

        engine.SkipWait();
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RecipeRunState.Completed, engine.State);
        Assert.Null(engine.Waiting);
        lock (log)
        {
            Assert.Contains(log, e => e.Severity == RecipeLogSeverity.Warning && e.Message.Contains("pulado"));
        }
    }

    [Fact]
    public async Task The_operator_can_stop_the_recipe_while_it_holds_for_the_flowmeter()
    {
        var (engine, device, arbiter, clock) = Build();

        await engine.StartAsync(SetpointRecipe(SetpointVariable.Flow, 2.5));
        Assert.True(await Eventually(() => device.Sent.Any(s => s.Contains("flowSetpoint"))));
        clock.Advance(TimeSpan.FromSeconds(30));
        device.PushTelemetry(new SensorSnapshot { FlowmeterOnline = false });
        Assert.True(await Eventually(() => engine.Waiting is not null));

        await engine.StopAsync("parada pelo operador");

        Assert.Equal(RecipeRunState.Stopped, engine.State);
        Assert.Null(engine.Waiting);
        Assert.All(CommandActuators.All, a => Assert.Equal(CommandOwner.Manual, arbiter.OwnerOf(a)));
    }

    [Fact]
    public async Task Zeroing_the_flow_never_holds_because_a_stop_must_not_wait_on_the_device()
    {
        var (engine, _, _, _) = Build();

        await engine.StartAsync(SetpointRecipe(SetpointVariable.Flow, 0));
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RecipeRunState.Completed, engine.State);
        Assert.Null(engine.Waiting);
    }

    [Fact]
    public async Task Enabling_the_aeration_loop_never_holds_because_it_puts_no_command_on_the_wire()
    {
        var (engine, _, _, _) = Build();

        await engine.StartAsync(LoopRecipe(ControlLoop.Aeration, LoopOperation.Enable));
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RecipeRunState.Completed, engine.State);
        Assert.Null(engine.Waiting);
    }

    /// <summary>Polls a condition the running engine reaches on its own loop, with a hard cap.</summary>
    private static async Task<bool> Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return false;
    }

    // ── Recipe fixtures ────────────────────────────────────────────────────────

    /// <summary>Start → Monitor (never satisfied) → Fim: holds until stopped or aborted.</summary>
    private static RecipeDocument HoldingRecipe()
        => MonitorRecipe(MeasuredVariable.Temperature, ComparisonOperator.GreaterOrEqual, 100000);

    private static RecipeDocument MonitorRecipe(MeasuredVariable variable, ComparisonOperator op, double target)
    {
        var recipe = new RecipeDocument { Name = "Monitor" };
        var start = RecipeNode.Create(NodeType.Start, id: "start");
        var monitor = RecipeNode.Create(NodeType.MonitorVariable, id: "mon");
        monitor.Set("variavel", variable.ToString());
        monitor.Set("condicao", op.ToString());
        monitor.Set("valorAlvo", target);
        monitor.Set("confirmacoes", 1);
        var end = RecipeNode.Create(NodeType.End, id: "end");

        recipe.Nodes.AddRange([start, monitor, end]);
        recipe.Connections.Add(new RecipeConnection("start", ConnectorNames.Out, "mon", ConnectorNames.In));
        recipe.Connections.Add(new RecipeConnection("mon", ConnectorNames.Out, "end", ConnectorNames.In));
        return recipe;
    }

    private static RecipeDocument LoopRecipe(ControlLoop loop, LoopOperation operation)
    {
        var recipe = new RecipeDocument { Name = "Malha" };
        var start = RecipeNode.Create(NodeType.Start, id: "start");
        var setLoop = RecipeNode.Create(NodeType.SetLoop, id: "loop");
        setLoop.Set("malha", loop.ToString());
        setLoop.Set("operacao", operation.ToString());
        var end = RecipeNode.Create(NodeType.End, id: "end");

        recipe.Nodes.AddRange([start, setLoop, end]);
        recipe.Connections.Add(new RecipeConnection("start", ConnectorNames.Out, "loop", ConnectorNames.In));
        recipe.Connections.Add(new RecipeConnection("loop", ConnectorNames.Out, "end", ConnectorNames.In));
        return recipe;
    }

    private static RecipeDocument SetpointRecipe(SetpointVariable variable, double value)
    {
        var recipe = new RecipeDocument { Name = "Setpoint" };
        var start = RecipeNode.Create(NodeType.Start, id: "start");
        var setpoint = RecipeNode.Create(NodeType.SetSetpoint, id: "sp");
        setpoint.Set("variavel", variable.ToString());
        setpoint.Set("valor", value);
        var end = RecipeNode.Create(NodeType.End, id: "end");

        recipe.Nodes.AddRange([start, setpoint, end]);
        recipe.Connections.Add(new RecipeConnection("start", ConnectorNames.Out, "sp", ConnectorNames.In));
        recipe.Connections.Add(new RecipeConnection("sp", ConnectorNames.Out, "end", ConnectorNames.In));
        return recipe;
    }

    private static RecipeDocument CascadeRecipe()
    {
        var recipe = new RecipeDocument { Name = "Cascata" };
        var start = RecipeNode.Create(NodeType.Start, id: "start");
        var cascade = RecipeNode.Create(NodeType.CascadeControl, id: "casc");
        cascade.Set("spO2", 30.0);
        var end = RecipeNode.Create(NodeType.End, id: "end");

        recipe.Nodes.AddRange([start, cascade, end]);
        recipe.Connections.Add(new RecipeConnection("start", ConnectorNames.Out, "casc", ConnectorNames.In));
        recipe.Connections.Add(new RecipeConnection("casc", ConnectorNames.Out, "end", ConnectorNames.In));
        return recipe;
    }

    /// <summary>Start → Cascade → End, with the cascade's Saída Loop wired to a manual gate and back.</summary>
    private static RecipeDocument CascadeWithGateRecipe(out RecipeNode gate)
    {
        var recipe = new RecipeDocument { Name = "Cascata com portão" };
        var start = RecipeNode.Create(NodeType.Start, id: "start");
        var cascade = RecipeNode.Create(NodeType.CascadeControl, id: "casc");
        gate = RecipeNode.Create(NodeType.ManualIntervention, id: "gate"); // default Hold = Continuar Cascata
        var end = RecipeNode.Create(NodeType.End, id: "end");

        recipe.Nodes.AddRange([start, cascade, gate, end]);
        recipe.Connections.Add(new RecipeConnection("start", ConnectorNames.Out, "casc", ConnectorNames.In));
        recipe.Connections.Add(new RecipeConnection("casc", ConnectorNames.LoopOut, "gate", ConnectorNames.In));
        recipe.Connections.Add(new RecipeConnection("gate", ConnectorNames.Out, "casc", ConnectorNames.LoopIn));
        recipe.Connections.Add(new RecipeConnection("casc", ConnectorNames.Out, "end", ConnectorNames.In));
        return recipe;
    }

    /// <summary>Start → Cascade → End, with the cascade's Saída Loop wired to a monitor exit condition.</summary>
    private static RecipeDocument CascadeWithMonitorRecipe(MeasuredVariable variable, ComparisonOperator op, double target)
    {
        var recipe = new RecipeDocument { Name = "Cascata com condição" };
        var start = RecipeNode.Create(NodeType.Start, id: "start");
        var cascade = RecipeNode.Create(NodeType.CascadeControl, id: "casc");
        var monitor = RecipeNode.Create(NodeType.MonitorVariable, id: "mon");
        monitor.Set("variavel", variable.ToString());
        monitor.Set("condicao", op.ToString());
        monitor.Set("valorAlvo", target);
        var end = RecipeNode.Create(NodeType.End, id: "end");

        recipe.Nodes.AddRange([start, cascade, monitor, end]);
        recipe.Connections.Add(new RecipeConnection("start", ConnectorNames.Out, "casc", ConnectorNames.In));
        recipe.Connections.Add(new RecipeConnection("casc", ConnectorNames.LoopOut, "mon", ConnectorNames.In));
        recipe.Connections.Add(new RecipeConnection("mon", ConnectorNames.Out, "casc", ConnectorNames.LoopIn));
        recipe.Connections.Add(new RecipeConnection("casc", ConnectorNames.Out, "end", ConnectorNames.In));
        return recipe;
    }
}
