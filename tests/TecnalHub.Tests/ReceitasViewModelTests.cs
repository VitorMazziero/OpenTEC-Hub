using TecnalHub.Services.Control;
using TecnalHub.Services.Recipes;
using TecnalHub.ViewModels;
using Xunit;

namespace TecnalHub.Tests;

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
        public RecipeDocument? Current => null;
        public TimeSpan Elapsed => TimeSpan.Zero;
        public Task Completion => Task.CompletedTask;
        public bool CanStart(RecipeDocument recipe, out string? reason) { reason = null; return true; }
        public Task StartAsync(RecipeDocument recipe, CancellationToken ct = default) => Task.CompletedTask;
        public void Pause() { }
        public void Resume() { }
        public Task StopAsync(string reason) => Task.CompletedTask;
        public bool ApplyLiveTuning(RecipeNode node) => false;
        public NodeState NodeStateOf(string nodeId) => NodeState.Waiting;
        public bool WasTraversed(RecipeConnection connection) => false;
        public CascadeTerms? CascadeTermsFor(string nodeId) => null;
        public event Action<string>? NodeStateChanged { add { } remove { } }
        public event Action? StateChanged { add { } remove { } }
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
    public void Library_covers_all_nineteen_blocks_in_six_categories()
    {
        var vm = Build();

        Assert.Equal(6, vm.Library.Count);
        Assert.Equal(19, vm.Library.Sum(g => g.Items.Count));
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
        Assert.Contains(operation.Options, o => o.Label == "Continuar Cascata");
        Assert.Contains(operation.Options, o => o.Label == "Pular Cascata");
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
        Assert.False(cascade.Fields.First(f => f.Key == "atuadorMisturador").BoolValue); // enrichment deferred
    }
}
