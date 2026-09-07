using OpenTECHub.Services.Control;
using OpenTECHub.Services.Recipes;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// The Receitas page view-model: tabs, the Minhas Receitas library, authoring (add / connect /
/// delete / undo), the generated property fields and repeating-list rows, and live validation —
/// all synchronous and engine-independent.
/// </summary>
public sealed class ReceitasViewModelTests
{
    private sealed class FakeRecipeEngine : IRecipeEngine
    {
        public RecipeRunState State => RecipeRunState.Idle;
        public string? StatusReason => null;
        public RecipeDeviceWait? Waiting => null;
        public RecipeDocument? Current => null;
        public TimeSpan Elapsed => TimeSpan.Zero;
        public Task Completion => Task.CompletedTask;
        public bool CanStart(RecipeDocument recipe, out string? reason) { reason = null; return true; }
        public Task StartAsync(RecipeDocument recipe, bool resetLoopsBeforeStart = false, CancellationToken ct = default) => Task.CompletedTask;
        public void Pause() { }
        public void Resume() { }
        public Task StopAsync(string reason) => Task.CompletedTask;
        public void SkipWait() { }
        public bool ApplyLiveTuning(RecipeNode node) => false;
        public NodeState NodeStateOf(string nodeId) => NodeState.Waiting;
        public bool WasTraversed(RecipeConnection connection) => false;
        public CascadeTerms? CascadeTermsFor(string nodeId) => null;
        public event Action<string>? NodeStateChanged { add { } remove { } }
        public event Action? StateChanged { add { } remove { } }
        public event Action? WaitingChanged { add { } remove { } }
        public event Action<RecipeLogEntry>? Logged { add { } remove { } }
        public void Dispose() { }
    }

    /// <summary>In-memory recipe store, deep-copying on save/load so tabs do not share documents.</summary>
    private sealed class FakeRecipeStore : IRecipeStore
    {
        private readonly Dictionary<string, string> _files = new();
        public string Directory => "memory";
        public IReadOnlyList<RecipeSummary> List()
            => [.. _files.Select(kv =>
            {
                var doc = RecipeSerializer.Deserialize(kv.Value);
                return new RecipeSummary(kv.Key, doc.Name, doc.Description, doc.Nodes.Count, DateTimeOffset.UtcNow);
            })];
        public RecipeDocument Load(string fileName) => RecipeSerializer.Deserialize(_files[fileName]);
        public string Save(RecipeDocument recipe, string? fileName = null)
        {
            fileName ??= recipe.Name + ".recipe.json";
            _files[fileName] = RecipeSerializer.Serialize(recipe);
            return fileName;
        }
        public void Delete(string fileName) => _files.Remove(fileName);
    }

    private static ReceitasViewModel Build() => new(new FakeRecipeEngine(), new FakeRecipeStore());

    private static RecipeTabViewModel Tab(ReceitasViewModel vm) => vm.SelectedTab!;

    [Fact]
    public void Opens_a_valid_start_end_tab()
    {
        var vm = Build();

        Assert.Single(vm.Tabs);
        Assert.NotNull(vm.SelectedTab);
        Assert.Equal(2, Tab(vm).Nodes.Count);
        Assert.Single(Tab(vm).Connections);
        Assert.True(Tab(vm).IsValid);
        Assert.Contains("\"schemaVersion\": 1", Tab(vm).JsonText);
    }

    [Fact]
    public void Nova_receita_opens_a_new_tab_rather_than_replacing()
    {
        var vm = Build();

        vm.NewRecipeCommand.Execute(null);

        Assert.Equal(2, vm.Tabs.Count);
        Assert.Same(vm.Tabs[1], vm.SelectedTab);
    }

    [Fact]
    public void Adding_a_block_appends_a_node()
    {
        var vm = Build();

        vm.AddBlockCommand.Execute(NodeType.Timer);

        Assert.Equal(3, Tab(vm).Nodes.Count);
        Assert.Contains(Tab(vm).Nodes, n => n.Type == NodeType.Timer);
    }

    [Fact]
    public void Undo_reverts_the_last_add()
    {
        var vm = Build();
        vm.AddBlockCommand.Execute(NodeType.Timer);
        Assert.Equal(3, Tab(vm).Nodes.Count);

        vm.UndoCommand.Execute(null);

        Assert.Equal(2, Tab(vm).Nodes.Count);
    }

    [Fact]
    public void Click_to_connect_makes_a_connection()
    {
        var vm = Build();
        vm.AddBlockCommand.Execute(NodeType.Timer);
        var timer = Tab(vm).Nodes.First(n => n.Type == NodeType.Timer);
        var end = Tab(vm).Nodes.First(n => n.Type == NodeType.End);

        var before = Tab(vm).Connections.Count;
        vm.PortClicked(timer, timer.Ports.First(p => !p.IsInput));
        vm.PortClicked(end, end.Ports.First(p => p.IsInput));

        Assert.Equal(before + 1, Tab(vm).Connections.Count);
    }

    [Fact]
    public void Deleting_a_selected_connection_removes_it()
    {
        var vm = Build();
        var connection = Tab(vm).Connections.First();

        Tab(vm).SelectConnection(connection);
        vm.DeleteSelectedCommand.Execute(null);

        Assert.Empty(Tab(vm).Connections);
    }

    [Fact]
    public void The_start_block_cannot_be_deleted()
    {
        var vm = Build();
        var start = Tab(vm).Nodes.First(n => n.Type == NodeType.Start);

        Tab(vm).SelectNode(start);
        vm.DeleteSelectedCommand.Execute(null);

        Assert.Contains(Tab(vm).Nodes, n => n.Type == NodeType.Start);
    }

    [Fact]
    public void The_end_block_cannot_be_deleted()
    {
        var vm = Build();
        var end = Tab(vm).Nodes.First(n => n.Type == NodeType.End);

        Tab(vm).SelectNode(end);
        vm.DeleteSelectedCommand.Execute(null);

        Assert.Contains(Tab(vm).Nodes, n => n.Type == NodeType.End);
    }

    /// <summary>
    /// Start and End are placed by the canvas when a recipe is created, so dragging a second one
    /// in would produce a document the validator rejects. Everything else is authorable —
    /// including the external pump, which was excluded only while it was a placeholder.
    /// </summary>
    [Fact]
    public void Library_does_not_contain_start_or_end_blocks()
    {
        var vm = Build();
        var allLibraryTypes = vm.Library.SelectMany(g => g.Items).Select(i => i.Type).ToList();

        Assert.DoesNotContain(NodeType.Start, allLibraryTypes);
        Assert.DoesNotContain(NodeType.End, allLibraryTypes);
        Assert.Contains(NodeType.PumpControl, allLibraryTypes);
    }

    [Fact]
    public void An_out_of_range_setpoint_invalidates_the_recipe()
    {
        var vm = Build();
        vm.AddBlockCommand.Execute(NodeType.SetSetpoint);
        var setpoint = Tab(vm).Nodes.First(n => n.Type == NodeType.SetSetpoint);

        setpoint.Fields.First(f => f.Key == "valor").NumberValue = 500; // temperature tops out at 60 °C

        Assert.False(Tab(vm).IsValid);
        Assert.Contains(Tab(vm).Findings, f => f.Severity == RecipeFindingSeverity.Error);
    }

    [Fact]
    public void Library_covers_every_authorable_block_grouped_by_category()
    {
        var vm = Build();

        // Six groups: Flow drops out because Start and End are placed by the canvas rather than
        // dragged, so it has no authorable member. Dispositivos Externos is the sixth.
        Assert.Equal(6, vm.Library.Count);
        Assert.Equal(Enum.GetValues<NodeType>().Length - 2, vm.Library.Sum(g => g.Items.Count));

        var external = vm.Library.Single(g => g.Label == "Dispositivos Externos");
        Assert.Equal(
            [NodeType.PumpControl, NodeType.BiomassSensor, NodeType.FlaskAgitator],
            external.Items.Select(i => i.Type));
    }

    /// <summary>
    /// The external-device blocks select an action and then show only that action's fields. Listing
    /// every field on the card would not merely be noisy — it would read as though all of them were
    /// in effect, which for a pump block means five profiles at once.
    /// </summary>
    [Fact]
    public void An_external_device_block_summarises_only_the_fields_its_action_uses()
    {
        var vm = Build();
        vm.AddBlockCommand.Execute(NodeType.PumpControl);
        var node = Tab(vm).Nodes.First(n => n.Type == NodeType.PumpControl);

        // Default action is Enable, which uses no profile fields at all.
        Assert.Contains("Ativar roteamento", node.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("λ", node.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Segmentos", node.Summary, StringComparison.Ordinal);

        node.Fields.First(f => f.Key == "acao").TextValue = nameof(ExternalPumpAction.SendProfile);

        Assert.Contains("λ", node.Summary, StringComparison.Ordinal);
        Assert.Contains("Perfil", node.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Multiplos_pontos_add_and_remove_rows()
    {
        var vm = Build();
        vm.AddBlockCommand.Execute(NodeType.MultiSetpoint);
        var node = Tab(vm).Nodes.First(n => n.Type == NodeType.MultiSetpoint);
        var pontos = node.Fields.First(f => f.Key == "pontos");

        pontos.AddRowCommand.Execute(null);
        pontos.AddRowCommand.Execute(null);
        Assert.Equal(2, pontos.Rows.Count);
        Assert.Contains(pontos.Rows[0].Fields, f => f.Key == "variavel");
        Assert.Contains("Temperatura → 25", node.Summary);

        pontos.Rows[0].RemoveCommand.Execute(null);
        Assert.Single(pontos.Rows);
    }

    [Fact]
    public void Intervencao_manual_wired_to_a_cascade_saida_loop_shows_the_cascade_switch_labels()
    {
        var vm = Build();
        vm.AddBlockCommand.Execute(NodeType.CascadeControl);
        vm.AddBlockCommand.Execute(NodeType.ManualIntervention);
        var cascade = Tab(vm).Nodes.First(n => n.Type == NodeType.CascadeControl);
        var gate = Tab(vm).Nodes.First(n => n.Type == NodeType.ManualIntervention);

        // Wire the cascade's Saída Loop to the gate's input.
        vm.PortClicked(cascade, cascade.Ports.First(p => p.Name == ConnectorNames.LoopOut));
        vm.PortClicked(gate, gate.Ports.First(p => p.Name == ConnectorNames.In));

        Assert.True(gate.IsCascadeLoopCondition);
        var operation = gate.Fields.First(f => f.Key == "operacao");
        Assert.Contains(operation.Options, o => o.Label == "Manter Rodando");
        Assert.Contains(operation.Options, o => o.Label == "Sair do Loop");
    }

    [Theory]
    [InlineData(NodeType.MonitorVariable)]
    [InlineData(NodeType.Timer)]
    public void A_block_wired_to_the_exit_condition_says_it_leaves_the_loop(NodeType type)
    {
        var vm = Build();
        vm.AddBlockCommand.Execute(NodeType.CascadeControl);
        vm.AddBlockCommand.Execute(type);
        var cascade = Tab(vm).Nodes.First(n => n.Type == NodeType.CascadeControl);
        var condition = Tab(vm).Nodes.First(n => n.Type == type);

        // Off the loop it reads as a normal step; the summary must not promise an exit.
        Assert.DoesNotContain("Sai do loop", condition.Summary);

        vm.PortClicked(cascade, cascade.Ports.First(p => p.Name == ConnectorNames.LoopOut));
        vm.PortClicked(condition, condition.Ports.First(p => p.Name == ConnectorNames.In));

        Assert.True(condition.IsCascadeLoopCondition);
        Assert.Contains("Sai do loop", condition.Summary);
    }

    [Fact]
    public void Monitor_hides_its_polling_interval_while_it_is_an_exit_condition()
    {
        var vm = Build();
        vm.AddBlockCommand.Execute(NodeType.CascadeControl);
        vm.AddBlockCommand.Execute(NodeType.MonitorVariable);
        var cascade = Tab(vm).Nodes.First(n => n.Type == NodeType.CascadeControl);
        var monitor = Tab(vm).Nodes.First(n => n.Type == NodeType.MonitorVariable);

        Assert.Contains(monitor.VisibleFields, f => f.Key == "intervaloPollingMs");

        // In the loop role the cascade's intervaloPidS sets the cadence, so the field decides nothing.
        vm.PortClicked(cascade, cascade.Ports.First(p => p.Name == ConnectorNames.LoopOut));
        vm.PortClicked(monitor, monitor.Ports.First(p => p.Name == ConnectorNames.In));

        Assert.DoesNotContain(monitor.VisibleFields, f => f.Key == "intervaloPollingMs");
        // The debounce and the timeout still apply, so they stay editable.
        Assert.Contains(monitor.VisibleFields, f => f.Key == "confirmacoes");
        Assert.Contains(monitor.VisibleFields, f => f.Key == "tempoLimiteMs");
    }

    [Fact]
    public void A_cascade_without_an_exit_condition_is_flagged_until_one_is_wired()
    {
        var vm = Build();
        vm.AddBlockCommand.Execute(NodeType.CascadeControl);
        vm.AddBlockCommand.Execute(NodeType.ManualIntervention);
        var cascade = Tab(vm).Nodes.First(n => n.Type == NodeType.CascadeControl);
        var gate = Tab(vm).Nodes.First(n => n.Type == NodeType.ManualIntervention);

        Assert.True(cascade.IsCascadeWithoutExitCondition);

        vm.PortClicked(cascade, cascade.Ports.First(p => p.Name == ConnectorNames.LoopOut));
        vm.PortClicked(gate, gate.Ports.First(p => p.Name == ConnectorNames.In));

        Assert.False(cascade.IsCascadeWithoutExitCondition);
    }

    [Fact]
    public void A_self_loop_is_refused_once_the_saida_loop_feeds_an_exit_condition()
    {
        var vm = Build();
        vm.AddBlockCommand.Execute(NodeType.CascadeControl);
        vm.AddBlockCommand.Execute(NodeType.ManualIntervention);
        var cascade = Tab(vm).Nodes.First(n => n.Type == NodeType.CascadeControl);
        var gate = Tab(vm).Nodes.First(n => n.Type == NodeType.ManualIntervention);

        vm.PortClicked(cascade, cascade.Ports.First(p => p.Name == ConnectorNames.LoopOut));
        vm.PortClicked(gate, gate.Ports.First(p => p.Name == ConnectorNames.In));
        var before = Tab(vm).Connections.Count;

        // Saída Loop -> Entrada Loop on the cascade itself: it would decide nothing, so it is refused.
        vm.PortClicked(cascade, cascade.Ports.First(p => p.Name == ConnectorNames.LoopOut));
        vm.PortClicked(cascade, cascade.Ports.First(p => p.Name == ConnectorNames.LoopIn));

        Assert.Equal(before, Tab(vm).Connections.Count);
    }

    [Fact]
    public void Saving_lists_the_recipe_in_the_library_and_it_reopens()
    {
        var vm = Build();
        vm.AddBlockCommand.Execute(NodeType.Timer);
        vm.SaveRecipeCommand.Execute(null);

        vm.OpenLibraryCommand.Execute(null);
        var summary = Assert.Single(vm.LibraryItems);
        Assert.Equal(3, summary.BlockCount);

        vm.OpenRecipeCommand.Execute(summary);
        Assert.Contains(Tab(vm).Nodes, n => n.Type == NodeType.Timer);
    }

    [Fact]
    public void Generated_fields_expose_the_cascade_parameter_schema()
    {
        var vm = Build();
        vm.AddBlockCommand.Execute(NodeType.CascadeControl);
        var cascade = Tab(vm).Nodes.First(n => n.Type == NodeType.CascadeControl);

        Assert.Contains(cascade.Fields, f => f.Key == "spO2" && f.IsNumber);
        Assert.Equal("DualCascade", cascade.Fields.First(f => f.Key == "modo" && f.IsEnum).TextValue);
        Assert.DoesNotContain(cascade.Fields, f => f.Key == "atuadorAgitacao");
        Assert.DoesNotContain(cascade.Fields, f => f.Key == "atuadorMisturador");
        Assert.DoesNotContain(cascade.Fields, f => f.Key == "loopInfinito");
    }
}
