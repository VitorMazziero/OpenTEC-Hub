using System.Text.Json.Nodes;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// The pure recipe domain (Phase 3 WP4 part 1): the declared-once block catalog, versioned
/// serialisation with its tolerances, and every validator rule from <c>docs/UI_DESIGN.md</c> §5.3.
/// </summary>
public sealed class RecipeDomainTests
{
    // ── Catalog ────────────────────────────────────────────────────────────────

    [Fact]
    public void Catalog_declares_every_block_type_exactly_once()
    {
        // Twenty-one: the original nineteen plus the biomass sensor and the flask agitator, which
        // gained recipe blocks once their devices could confirm what they were told.
        Assert.Equal(21, RecipeNodeCatalog.All.Count);
        Assert.Equal(Enum.GetValues<NodeType>().Length, RecipeNodeCatalog.All.Count);

        foreach (var type in Enum.GetValues<NodeType>())
        {
            Assert.True(RecipeNodeCatalog.Has(type), $"catalog is missing {type}");
        }
    }

    [Fact]
    public void Start_and_end_have_the_expected_single_ports()
    {
        var start = RecipeNodeCatalog.Definition(NodeType.Start);
        Assert.Single(start.Ports);
        Assert.Equal(PortDirection.Out, start.Ports[0].Direction);

        var end = RecipeNodeCatalog.Definition(NodeType.End);
        Assert.Single(end.Ports);
        Assert.Equal(PortDirection.In, end.Ports[0].Direction);
    }

    [Fact]
    public void End_keeps_the_green_header_as_the_single_exception()
    {
        Assert.Equal(RecipeNodeCatalog.EndHeaderColor, RecipeNodeCatalog.HeaderColor(NodeType.End));
        Assert.NotEqual(RecipeNodeCatalog.EndHeaderColor, RecipeNodeCatalog.HeaderColor(NodeType.Start));
    }

    [Fact]
    public void Cascade_has_the_four_ports_including_the_loop_pair()
    {
        var cascade = RecipeNodeCatalog.Definition(NodeType.CascadeControl);
        Assert.Contains(cascade.Ports, p => p.Name == ConnectorNames.In);
        Assert.Contains(cascade.Ports, p => p.Name == ConnectorNames.Out);
        Assert.Contains(cascade.Ports, p => p.Name == ConnectorNames.LoopOut);
        Assert.Contains(cascade.Ports, p => p.Name == ConnectorNames.LoopIn);
    }

    [Fact]
    public void Cascade_mode_defaults_to_dual_cascade()
    {
        var node = RecipeNode.Create(NodeType.CascadeControl);
        Assert.Equal("DualCascade", node.Text("modo"));
        Assert.DoesNotContain(node.Definition.Parameters, p => p.Key is "atuadorAgitacao" or "atuadorAeracao");
    }

    [Fact]
    public void New_node_initialises_from_schema_defaults()
    {
        var timer = RecipeNode.Create(NodeType.Timer);
        Assert.Equal(10, timer.Number("duracao"));
        Assert.Equal(nameof(TimeUnit.Seconds), timer.Text("unidade"));
    }

    // ── Serialization ──────────────────────────────────────────────────────────

    [Fact]
    public void Round_trip_preserves_nodes_connections_and_metadata()
    {
        var recipe = SampleRecipe();
        recipe.Author = "Vitor";

        var json = RecipeSerializer.Serialize(recipe);
        var back = RecipeSerializer.Deserialize(json);

        Assert.Equal(recipe.Name, back.Name);
        Assert.Equal("Vitor", back.Author);
        Assert.Equal(recipe.Nodes.Count, back.Nodes.Count);
        Assert.Equal(recipe.Connections.Count, back.Connections.Count);

        var timer = back.Nodes.First(n => n.Type == NodeType.Timer);
        Assert.Equal(30, timer.Number("duracao"));
        Assert.Equal(nameof(TimeUnit.Minutes), timer.Text("unidade"));
    }

    [Fact]
    public void Serialize_writes_the_current_schema_version()
    {
        var json = RecipeSerializer.Serialize(SampleRecipe());
        var root = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(RecipeSerializer.CurrentVersion, root["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public void Deserialize_tolerates_legacy_connector_spelling_and_canonicalises_it()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "name": "Legado",
          "nodes": [
            { "id": "a", "type": "CascadeControl", "parameters": {} },
            { "id": "b", "type": "LogEvent", "parameters": {} }
          ],
          "connections": [
            { "source": "a", "sourcePort": "Saída Loop", "target": "b", "targetPort": "Entrada" }
          ]
        }
        """;

        var recipe = RecipeSerializer.Deserialize(json);
        var connection = Assert.Single(recipe.Connections);
        Assert.Equal(ConnectorNames.LoopOut, connection.SourceConnector); // canonicalised on load
        Assert.True(ConnectorNames.IsLoopOut(connection.SourceConnector));
    }

    [Fact]
    public void Deserialize_maps_receitasopentec_type_aliases()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "name": "Migrada",
          "nodes": [
            { "id": "s", "type": "WriteSetpoint", "parameters": { "variavel": "Temperature", "valor": 30 } },
            { "id": "z", "type": "ResetAccumulator", "parameters": {} }
          ],
          "connections": []
        }
        """;

        var recipe = RecipeSerializer.Deserialize(json);
        Assert.Equal(NodeType.SetSetpoint, recipe.Node("s")!.Type);
        Assert.Equal(NodeType.ResetVariables, recipe.Node("z")!.Type);
    }

    [Fact]
    public void Deserialize_defaults_missing_schema_version_to_one()
    {
        const string json = """{ "name": "Sem versão", "nodes": [], "connections": [] }""";
        var recipe = RecipeSerializer.Deserialize(json);
        Assert.Equal("Sem versão", recipe.Name);
    }

    [Fact]
    public void Deserialize_refuses_a_newer_schema_version()
    {
        var json = $$"""{ "schemaVersion": {{RecipeSerializer.CurrentVersion + 1}}, "nodes": [] }""";
        Assert.Throws<RecipeFormatException>(() => RecipeSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_rejects_malformed_json()
    {
        Assert.Throws<RecipeFormatException>(() => RecipeSerializer.Deserialize("{ not json"));
        Assert.Throws<RecipeFormatException>(() => RecipeSerializer.Deserialize(""));
    }

    // ── Validation ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_minimal_start_timer_end_recipe_is_valid()
    {
        var result = RecipeValidator.Validate(SampleRecipe());
        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.Message)));
    }

    [Fact]
    public void Empty_recipe_is_rejected()
    {
        var result = RecipeValidator.Validate(new RecipeDocument());
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Exactly_one_start_is_required()
    {
        var noStart = SampleRecipe();
        noStart.Nodes.RemoveAll(n => n.Type == NodeType.Start);
        Assert.False(RecipeValidator.Validate(noStart).IsValid);

        var twoStarts = SampleRecipe();
        twoStarts.Nodes.Add(RecipeNode.Create(NodeType.Start));
        Assert.False(RecipeValidator.Validate(twoStarts).IsValid);
    }

    [Fact]
    public void At_least_one_end_is_required()
    {
        var recipe = SampleRecipe();
        recipe.Nodes.RemoveAll(n => n.Type == NodeType.End);
        recipe.Connections.RemoveAll(c => c.TargetNodeId == "end");
        Assert.False(RecipeValidator.Validate(recipe).IsValid);
    }

    [Fact]
    public void An_unreachable_block_is_a_warning_not_an_error()
    {
        var recipe = SampleRecipe();
        recipe.Nodes.Add(RecipeNode.Create(NodeType.LogEvent, id: "orphan"));

        var result = RecipeValidator.Validate(recipe);
        Assert.True(result.IsValid); // still runnable
        Assert.Contains(result.Warnings, w => w.NodeId == "orphan");
    }

    [Fact]
    public void No_path_to_end_is_an_error()
    {
        var recipe = SampleRecipe();
        recipe.Connections.RemoveAll(c => c.TargetNodeId == "end");
        Assert.False(RecipeValidator.Validate(recipe).IsValid);
    }

    [Fact]
    public void A_cycle_outside_a_loop_is_an_error()
    {
        var recipe = SampleRecipe();
        // timer -> end already; add end -> timer to close a loop with no cascade construct.
        recipe.Connections.Add(new RecipeConnection("end", ConnectorNames.Out, "timer", ConnectorNames.In));
        Assert.False(RecipeValidator.Validate(recipe).IsValid);
    }

    [Fact]
    public void A_cascade_loop_construct_is_not_a_cycle()
    {
        var recipe = CascadeLoopRecipe();
        var result = RecipeValidator.Validate(recipe);
        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.Message)));
    }

    [Fact]
    public void Timer_with_negative_duration_is_rejected()
    {
        var recipe = SampleRecipe();
        recipe.Node("timer")!.Set("duracao", -5);
        Assert.False(RecipeValidator.Validate(recipe).IsValid);
    }

    [Fact]
    public void Setpoint_outside_the_device_range_is_rejected()
    {
        var recipe = SampleRecipe();
        var setpoint = RecipeNode.Create(NodeType.SetSetpoint, id: "sp");
        setpoint.Set("variavel", nameof(SetpointVariable.Temperature));
        setpoint.Set("valor", 500); // temperature tops out at 60 °C
        recipe.Nodes.Add(setpoint);
        recipe.Connections.Add(new RecipeConnection("timer", ConnectorNames.Out, "sp", ConnectorNames.In));

        Assert.False(RecipeValidator.Validate(recipe).IsValid);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(14, false)]
    [InlineData(15, true)]
    [InlineData(1000, true)]
    [InlineData(1001, false)]
    public void Agitation_range_matches_the_15_to_1000_rpm_wire_contract(double rpm, bool accepted)
        => Assert.Equal(accepted, DeviceRanges.Accepts(SetpointVariable.Agitation, rpm));

    [Fact]
    public void Monitor_refuses_an_actuation_variable()
    {
        var recipe = SampleRecipe();
        var monitor = RecipeNode.Create(NodeType.MonitorVariable, id: "mon");
        monitor.Set("variavel", "Agitation"); // not a MeasuredVariable — no feedback on the wire
        recipe.Nodes.Add(monitor);
        recipe.Connections.Add(new RecipeConnection("timer", ConnectorNames.Out, "mon", ConnectorNames.In));

        var result = RecipeValidator.Validate(recipe);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.NodeId == "mon");
    }

    [Fact]
    public void Monitor_rejects_a_non_positive_polling_interval()
    {
        var recipe = SampleRecipe();
        var monitor = RecipeNode.Create(NodeType.MonitorVariable, id: "mon");
        monitor.Set("intervaloPollingMs", 0);
        recipe.Nodes.Add(monitor);
        recipe.Connections.Add(new RecipeConnection("timer", ConnectorNames.Out, "mon", ConnectorNames.In));

        Assert.False(RecipeValidator.Validate(recipe).IsValid);
    }

    [Fact]
    public void Pump_intensity_over_one_hundred_percent_and_time_over_an_hour_are_rejected()
    {
        var recipe = SampleRecipe();
        var pump = RecipeNode.Create(NodeType.PhPump, id: "pump");
        pump.Set("intensidade", 150);
        pump.Set("tempoLigadaS", 5000);
        recipe.Nodes.Add(pump);
        recipe.Connections.Add(new RecipeConnection("timer", ConnectorNames.Out, "pump", ConnectorNames.In));

        var result = RecipeValidator.Validate(recipe);
        Assert.False(result.IsValid);
        Assert.Equal(2, result.Errors.Count(e => e.NodeId == "pump"));
    }

    [Fact]
    public void Cascade_pid_interval_out_of_range_is_rejected()
    {
        var recipe = SampleRecipe();
        var cascade = RecipeNode.Create(NodeType.CascadeControl, id: "casc");
        cascade.Set("intervaloPidS", 120); // above the 0.1–60 s band
        recipe.Nodes.Add(cascade);
        recipe.Connections.Add(new RecipeConnection("timer", ConnectorNames.Out, "casc", ConnectorNames.In));

        Assert.False(RecipeValidator.Validate(recipe).IsValid);
    }

    [Fact]
    public void Cascade_with_inverted_agitation_range_is_rejected()
    {
        var recipe = SampleRecipe();
        var cascade = RecipeNode.Create(NodeType.CascadeControl, id: "casc");
        cascade.Set("modo", "AgitationOnly");
        cascade.Set("nMinRpm", 400);
        cascade.Set("nMaxRpm", 200);
        recipe.Nodes.Add(cascade);
        recipe.Connections.Add(new RecipeConnection("timer", ConnectorNames.Out, "casc", ConnectorNames.In));

        Assert.False(RecipeValidator.Validate(recipe).IsValid);
    }

    [Fact]
    public void Cascade_actuation_window_out_of_bounds_is_rejected()
    {
        var recipe = SampleRecipe();
        var cascade = RecipeNode.Create(NodeType.CascadeControl, id: "casc");
        cascade.Set("aeracaoOutMin", 80);
        cascade.Set("aeracaoOutMax", 70); // min !< max
        recipe.Nodes.Add(cascade);
        recipe.Connections.Add(new RecipeConnection("timer", ConnectorNames.Out, "casc", ConnectorNames.In));

        Assert.False(RecipeValidator.Validate(recipe).IsValid);
    }

    [Fact]
    public void Cascade_rejects_an_unknown_mode()
    {
        var recipe = SampleRecipe();
        var cascade = RecipeNode.Create(NodeType.CascadeControl, id: "casc");
        cascade.Set("modo", "Unknown");
        recipe.Nodes.Add(cascade);
        recipe.Connections.Add(new RecipeConnection("timer", ConnectorNames.Out, "casc", ConnectorNames.In));

        var result = RecipeValidator.Validate(recipe);

        Assert.Contains(result.Errors, e => e.NodeId == "casc" && e.Message.Contains("inválido"));
    }

    [Fact]
    public void Dual_cascade_requires_overlapping_actuation_windows()
    {
        var recipe = SampleRecipe();
        var cascade = RecipeNode.Create(NodeType.CascadeControl, id: "casc");
        cascade.Set("modo", "DualCascade");
        cascade.Set("agitacaoOutMin", 0);
        cascade.Set("agitacaoOutMax", 40);
        cascade.Set("aeracaoOutMin", 60);
        cascade.Set("aeracaoOutMax", 100);
        recipe.Nodes.Add(cascade);
        recipe.Connections.Add(new RecipeConnection("timer", ConnectorNames.Out, "casc", ConnectorNames.In));

        var result = RecipeValidator.Validate(recipe);

        Assert.Contains(result.Errors, e => e.NodeId == "casc" && e.Message.Contains("sobrepor"));
    }

    // ── Fixtures ───────────────────────────────────────────────────────────────

    private static RecipeDocument SampleRecipe()
    {
        var recipe = new RecipeDocument { Name = "Receita de Exemplo" };

        var start = RecipeNode.Create(NodeType.Start, id: "start");
        var timer = RecipeNode.Create(NodeType.Timer, id: "timer");
        timer.Set("duracao", 30);
        timer.Set("unidade", nameof(TimeUnit.Minutes));
        var end = RecipeNode.Create(NodeType.End, id: "end");

        recipe.Nodes.AddRange([start, timer, end]);
        recipe.Connections.Add(new RecipeConnection("start", ConnectorNames.Out, "timer", ConnectorNames.In));
        recipe.Connections.Add(new RecipeConnection("timer", ConnectorNames.Out, "end", ConnectorNames.In));
        return recipe;
    }

    private static RecipeDocument CascadeLoopRecipe()
    {
        var recipe = new RecipeDocument { Name = "Cascata com laço" };

        var start = RecipeNode.Create(NodeType.Start, id: "start");
        var cascade = RecipeNode.Create(NodeType.CascadeControl, id: "casc");
        var body = RecipeNode.Create(NodeType.LogEvent, id: "body");
        var end = RecipeNode.Create(NodeType.End, id: "end");

        recipe.Nodes.AddRange([start, cascade, body, end]);
        recipe.Connections.Add(new RecipeConnection("start", ConnectorNames.Out, "casc", ConnectorNames.In));
        // Loop body: cascade fires Saída Loop -> body -> returns to cascade's Entrada Loop.
        recipe.Connections.Add(new RecipeConnection("casc", ConnectorNames.LoopOut, "body", ConnectorNames.In));
        recipe.Connections.Add(new RecipeConnection("body", ConnectorNames.Out, "casc", ConnectorNames.LoopIn));
        // Normal exit when the loop terminates.
        recipe.Connections.Add(new RecipeConnection("casc", ConnectorNames.Out, "end", ConnectorNames.In));
        return recipe;
    }
}
