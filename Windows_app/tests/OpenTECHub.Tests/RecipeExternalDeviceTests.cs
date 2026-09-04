using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// The three external-device recipe blocks: the reworked external pump, the biomass sensor and the
/// flask agitator.
/// </summary>
/// <remarks>
/// What makes these different from the dosing-pump blocks is that their devices can be absent on
/// their own, so each block dispatches and then holds until the device confirms. Two of them also
/// have to honour a firmware ordering constraint the Hub imposes on disabling.
/// </remarks>
public sealed class RecipeExternalDeviceTests
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

    private static Task CappedDelay(TimeSpan requested, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(requested.TotalMilliseconds, 0, 5)), ct);

    /// <summary>
    /// A frame from a Hub that says nothing about any external device.
    /// </summary>
    /// <remarks>
    /// Pushed before the run in the tests that are only checking what goes on the wire. The engine
    /// holds while it has no telemetry at all — with nothing to evaluate, "confirmed" is not a
    /// question it can answer — so a block would otherwise stall for reasons that have nothing to
    /// do with what is being asserted.
    /// </remarks>
    private static SensorSnapshot SilentHub() => new() { SensorCommOk = true };

    /// <summary>Start → the block under test → End.</summary>
    private static RecipeDocument OneBlock(NodeType type, params (string Key, object Value)[] parameters)
    {
        var recipe = new RecipeDocument { Name = "Dispositivo externo" };
        var start = RecipeNode.Create(NodeType.Start, id: "start");
        var block = RecipeNode.Create(type, id: "dev");
        foreach (var (key, value) in parameters)
        {
            switch (value)
            {
                case string s: block.Set(key, s); break;
                case bool b: block.Set(key, b); break;
                default: block.Set(key, Convert.ToDouble(value)); break;
            }
        }

        var end = RecipeNode.Create(NodeType.End, id: "end");
        recipe.Nodes.AddRange([start, block, end]);
        recipe.Connections.Add(new RecipeConnection("start", ConnectorNames.Out, "dev", ConnectorNames.In));
        recipe.Connections.Add(new RecipeConnection("dev", ConnectorNames.Out, "end", ConnectorNames.In));
        return recipe;
    }

    // ── External pump ────────────────────────────────────────────────────────

    /// <summary>
    /// The reason this block exists at all: it used to log an intent and send nothing, because the
    /// external pump's actuation was still outstanding when the recipe engine was written.
    /// </summary>
    [Fact]
    public async Task The_external_pump_block_sends_the_same_profile_frame_as_the_manual_card()
    {
        var (engine, device, _, _) = Build();
        device.PushTelemetry(SilentHub());

        await engine.StartAsync(OneBlock(
            NodeType.PumpControl,
            ("acao", nameof(ExternalPumpAction.SendProfile)),
            ("modo", nameof(PumpProfileMode.Linear)),
            ("inicioMin", 0.0),
            ("fimMin", 30.0),
            ("lambda", 2.0),
            ("phi", 0.25)));
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(
            """{"mode":2,"init_t":0.0,"final_t":30.0,"lambda_linear":2.0,"phi_linear":0.25}""",
            device.Sent);
    }

    /// <summary>
    /// The Hub parses <c>pumpComm</c> before it reaches the pump block, so a combined
    /// <c>{"pumpComm":0,"mode":0}</c> clears routing and then discards its own <c>mode:0</c> — the
    /// node keeps dosing and only its telemetry goes quiet.
    /// </summary>
    [Fact]
    public async Task Stopping_the_external_pump_sends_the_profile_stop_before_it_clears_routing()
    {
        var (engine, device, _, _) = Build();
        device.PushTelemetry(SilentHub());

        await engine.StartAsync(OneBlock(
            NodeType.PumpControl, ("acao", nameof(ExternalPumpAction.Stop))));
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        var stop = device.Sent.IndexOf("""{"mode":0,"speed":0}""");
        var routing = device.Sent.IndexOf("""{"pumpComm":0}""");

        Assert.True(stop >= 0, "o quadro de parada do perfil não foi enviado");
        Assert.True(routing >= 0, "o quadro de roteamento não foi enviado");
        Assert.True(stop < routing, "o roteamento foi desligado antes da parada do perfil");
    }

    // ── Biomass sensor ───────────────────────────────────────────────────────

    [Fact]
    public async Task The_biomass_block_sends_the_momentary_and_threshold_frames()
    {
        var (engine, device, _, _) = Build();
        device.PushTelemetry(SilentHub());

        await engine.StartAsync(OneBlock(
            NodeType.BiomassSensor,
            ("acao", nameof(BiomassAction.Thresholds)),
            ("limiarBaixo", 12000.0),
            ("limiarAlto", 44000.0),
            ("limiarOtimo", 26000.0)));
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("""{"low":12000,"high":44000,"opt":26000}""", device.Sent);
    }

    /// <summary>Same ordering constraint as the pump: the Hub drops a stop that rides along.</summary>
    [Fact]
    public async Task Disabling_biomass_stops_acquisition_before_it_clears_routing()
    {
        var (engine, device, _, _) = Build();
        device.PushTelemetry(SilentHub());

        await engine.StartAsync(OneBlock(
            NodeType.BiomassSensor, ("acao", nameof(BiomassAction.Disable))));
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        var stop = device.Sent.IndexOf("""{"stop":1}""");
        var routing = device.Sent.IndexOf("""{"biomassComm":0}""");

        Assert.True(stop >= 0 && routing >= 0);
        Assert.True(stop < routing, "o roteamento foi desligado antes da parada da aquisição");
    }

    // ── Flask agitator ───────────────────────────────────────────────────────

    [Fact]
    public async Task The_agitator_block_splits_the_direction_off_the_magnitude()
    {
        var (engine, device, _, _) = Build();
        device.PushTelemetry(SilentHub());

        await engine.StartAsync(OneBlock(
            NodeType.FlaskAgitator,
            ("acao", nameof(FlaskAgitatorAction.Run)),
            ("intensidade", 70.0),
            ("sentido", nameof(AgitatorDirection.CounterClockwise))));
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(
            """{"agitatorOn":1,"agitatorAuto":0,"agitatorPercent":70.0,"agitatorDir":0}""",
            device.Sent);
    }

    /// <summary>
    /// A recipe stop has to be deterministic. Leaving the potentiometer enabled means the node
    /// re-reads the bench knob on its next loop, so the motor restarts — and the block would then
    /// hold forever waiting for a zero that never arrives.
    /// </summary>
    [Fact]
    public async Task Stopping_the_agitator_from_a_recipe_locks_the_potentiometer_out()
    {
        var (engine, device, _, _) = Build();
        device.PushTelemetry(SilentHub());

        await engine.StartAsync(OneBlock(
            NodeType.FlaskAgitator,
            ("acao", nameof(FlaskAgitatorAction.Stop)),
            ("intensidade", 70.0)));
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(device.Sent, s => s.Contains("""agitatorOn":0""", StringComparison.Ordinal)
                                          && s.Contains("""agitatorReEnablePot":0""", StringComparison.Ordinal));
    }

    // ── Holding for the device ───────────────────────────────────────────────

    /// <summary>
    /// A Hub built before the presence keys omits them entirely. Holding on evidence that firmware
    /// cannot produce would stall every one of these blocks and force the operator to skip each in
    /// turn, so silence proceeds.
    /// </summary>
    [Fact]
    public async Task A_hub_that_reports_nothing_about_a_device_does_not_stall_the_recipe()
    {
        var (engine, device, _, _) = Build();
        device.PushTelemetry(new SensorSnapshot { SensorCommOk = true });

        await engine.StartAsync(OneBlock(
            NodeType.PumpControl,
            ("acao", nameof(ExternalPumpAction.SendProfile)),
            ("modo", nameof(PumpProfileMode.Constant)),
            ("inicioMin", 0.0),
            ("fimMin", 10.0),
            ("lambda", 1.5)));
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RecipeRunState.Completed, engine.State);
        Assert.Null(engine.Waiting);
    }

    /// <summary>
    /// With a Hub that does report presence, a device that is not answering holds the block and
    /// the engine publishes the wait so the operator can skip it or stop the recipe.
    /// </summary>
    [Fact]
    public async Task An_absent_device_holds_the_block_and_the_operator_can_skip_it()
    {
        var (engine, device, _, _) = Build();

        var run = engine.StartAsync(OneBlock(
            NodeType.PumpControl,
            ("acao", nameof(ExternalPumpAction.SendProfile)),
            ("modo", nameof(PumpProfileMode.Constant)),
            ("inicioMin", 0.0),
            ("fimMin", 10.0),
            ("lambda", 1.5)));
        await run;

        // The Hub knows the pump and says it is absent, so the echo the block waits for cannot come.
        device.PushTelemetry(new SensorSnapshot
        {
            SensorCommOk = true,
            HasPumpTelemetry = true,
            PumpOnline = false,
        });

        var stalled = await Task.WhenAny(
            engine.Completion, Task.Delay(TimeSpan.FromMilliseconds(300)));
        Assert.NotSame(engine.Completion, stalled);

        engine.SkipWait();
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Completed, engine.State);
    }

    /// <summary>
    /// <c>PumpMode</c> is the node's own report of what it loaded, not an echo of the frame, so it
    /// is real evidence the profile arrived and parsed — what a recipe needs before it starts
    /// timing a feed.
    /// </summary>
    [Fact]
    public async Task The_block_completes_once_the_node_reports_the_mode_it_was_given()
    {
        var (engine, device, _, _) = Build();

        await engine.StartAsync(OneBlock(
            NodeType.PumpControl,
            ("acao", nameof(ExternalPumpAction.SendProfile)),
            ("modo", nameof(PumpProfileMode.Exponential)),
            ("inicioMin", 0.0),
            ("fimMin", 10.0),
            ("lambda", 1.0),
            ("phi", 0.1)));

        device.PushTelemetry(new SensorSnapshot
        {
            SensorCommOk = true,
            HasPumpTelemetry = true,
            PumpOnline = true,
            PumpMode = (int)PumpProfileMode.Exponential,
        });

        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecipeRunState.Completed, engine.State);
    }

    // ── Validation ───────────────────────────────────────────────────────────

    [Fact]
    public void A_piecewise_profile_with_non_increasing_times_is_rejected()
    {
        var recipe = OneBlock(
            NodeType.PumpControl,
            ("acao", nameof(ExternalPumpAction.SendProfile)),
            ("modo", nameof(PumpProfileMode.Piecewise)),
            ("inicioMin", 0.0),
            ("fimMin", 60.0),
            ("tempos", "0, 30, 30"),
            ("vazoes", "1, 2, 3"));

        var result = RecipeValidator.Validate(recipe);

        Assert.False(result.IsValid);
        Assert.Contains(result.Findings, f => f.Message.Contains("crescentes", StringComparison.Ordinal));
    }

    [Fact]
    public void A_polynomial_beyond_p20_is_rejected()
    {
        var recipe = OneBlock(
            NodeType.PumpControl,
            ("acao", nameof(ExternalPumpAction.SendProfile)),
            ("modo", nameof(PumpProfileMode.Polynomial)),
            ("inicioMin", 0.0),
            ("fimMin", 60.0),
            ("coeficientes", string.Join(", ", Enumerable.Repeat("1", 22))));

        var result = RecipeValidator.Validate(recipe);

        Assert.False(result.IsValid);
        Assert.Contains(result.Findings, f => f.Message.Contains("p0..p20", StringComparison.Ordinal));
    }

    [Fact]
    public void Biomass_thresholds_out_of_order_are_rejected()
    {
        var recipe = OneBlock(
            NodeType.BiomassSensor,
            ("acao", nameof(BiomassAction.Thresholds)),
            ("limiarBaixo", 40000.0),
            ("limiarAlto", 10000.0),
            ("limiarOtimo", 25000.0));

        var result = RecipeValidator.Validate(recipe);

        Assert.False(result.IsValid);
        Assert.Contains(result.Findings, f => f.Message.Contains("menor que o alto", StringComparison.Ordinal));
    }

    /// <summary>
    /// Only the selected action's fields are checked, so an operator can leave the other modes'
    /// values staged in the block without failing validation.
    /// </summary>
    [Fact]
    public void An_unused_mode_with_nonsense_values_does_not_invalidate_the_block()
    {
        var recipe = OneBlock(
            NodeType.PumpControl,
            ("acao", nameof(ExternalPumpAction.Enable)),
            ("modo", nameof(PumpProfileMode.Piecewise)),
            ("tempos", "not a number"),
            ("vazoes", ""));

        Assert.True(RecipeValidator.Validate(recipe).IsValid);
    }
}
