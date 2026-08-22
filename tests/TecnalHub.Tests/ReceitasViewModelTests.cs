using TecnalHub.Services.Control;
using TecnalHub.Services.Recipes;
using TecnalHub.ViewModels;
using Xunit;

namespace TecnalHub.Tests;

/// <summary>
/// The Receitas page view-model: authoring the graph (new, add, click-to-connect), the generated
/// property fields, live validation, and the block library — all synchronous, engine-independent.
/// </summary>
public sealed class ReceitasViewModelTests
{
    /// <summary>A no-op engine: the authoring surface never needs a live run.</summary>
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

    private static ReceitasViewModel Build() => new(new FakeRecipeEngine());

    [Fact]
    public void New_recipe_seeds_a_valid_start_end_graph()
    {
        var vm = Build();

        Assert.Equal(2, vm.Nodes.Count);
        Assert.Single(vm.Connections);
        Assert.True(vm.IsValid);
        Assert.Contains("válida", vm.ValidationSummary);
    }

    [Fact]
    public void Json_panel_reflects_the_current_recipe()
    {
        var vm = Build();
        Assert.Contains("\"schemaVersion\": 1", vm.JsonText);
        Assert.Contains("Start", vm.JsonText);
    }

    [Fact]
    public void Adding_a_block_appends_a_node()
    {
        var vm = Build();

        vm.AddBlockCommand.Execute(NodeType.Timer);

        Assert.Equal(3, vm.Nodes.Count);
        Assert.Contains(vm.Nodes, n => n.Type == NodeType.Timer);
        Assert.NotNull(vm.SelectedNode);
    }

    [Fact]
    public void Click_to_connect_makes_a_connection_between_two_ports()
    {
        var vm = Build();
        vm.AddBlockCommand.Execute(NodeType.Timer);
        var timer = vm.Nodes.First(n => n.Type == NodeType.Timer);
        var end = vm.Nodes.First(n => n.Type == NodeType.End);

        var before = vm.Connections.Count;
        vm.PortClicked(timer, timer.Ports.First(p => !p.IsInput));  // output first
        vm.PortClicked(end, end.Ports.First(p => p.IsInput));       // then an input

        Assert.Equal(before + 1, vm.Connections.Count);
    }

    [Fact]
    public void Clicking_two_outputs_does_not_connect()
    {
        var vm = Build();
        vm.AddBlockCommand.Execute(NodeType.Timer);
        var start = vm.Nodes.First(n => n.Type == NodeType.Start);
        var timer = vm.Nodes.First(n => n.Type == NodeType.Timer);

        var before = vm.Connections.Count;
        vm.PortClicked(start, start.Ports.First(p => !p.IsInput));
        vm.PortClicked(timer, timer.Ports.First(p => !p.IsInput)); // another output — no connection

        Assert.Equal(before, vm.Connections.Count);
    }

    [Fact]
    public void An_out_of_range_setpoint_invalidates_the_recipe()
    {
        var vm = Build();
        vm.AddBlockCommand.Execute(NodeType.SetSetpoint);
        var setpoint = vm.Nodes.First(n => n.Type == NodeType.SetSetpoint);

        var valueField = setpoint.Fields.First(f => f.Key == "valor");
        valueField.NumberValue = 500; // temperature tops out at 60 °C

        Assert.False(vm.IsValid);
        Assert.Contains(vm.Findings, f => f.Severity == RecipeFindingSeverity.Error);
    }

    [Fact]
    public void Deleting_a_block_removes_it_and_its_connections()
    {
        var vm = Build();
        vm.AddBlockCommand.Execute(NodeType.Timer);
        var timer = vm.Nodes.First(n => n.Type == NodeType.Timer);
        vm.PortClicked(timer, timer.Ports.First(p => !p.IsInput));
        vm.PortClicked(vm.Nodes.First(n => n.Type == NodeType.End), vm.Nodes.First(n => n.Type == NodeType.End).Ports.First(p => p.IsInput));

        vm.SelectNode(timer);
        vm.DeleteSelectedCommand.Execute(null);

        Assert.DoesNotContain(vm.Nodes, n => n.Type == NodeType.Timer);
        Assert.DoesNotContain(vm.Connections, c => c.Source.Type == NodeType.Timer || c.Target.Type == NodeType.Timer);
    }

    [Fact]
    public void The_start_block_cannot_be_deleted()
    {
        var vm = Build();
        var start = vm.Nodes.First(n => n.Type == NodeType.Start);

        vm.SelectNode(start);
        vm.DeleteSelectedCommand.Execute(null);

        Assert.Contains(vm.Nodes, n => n.Type == NodeType.Start);
    }

    [Fact]
    public void Library_covers_all_nineteen_blocks_in_six_categories()
    {
        var vm = Build();

        Assert.Equal(6, vm.Library.Count);
        Assert.Equal(19, vm.Library.Sum(g => g.Items.Count));
    }

    [Fact]
    public void Generated_fields_expose_the_cascade_parameter_schema()
    {
        var vm = Build();
        vm.AddBlockCommand.Execute(NodeType.CascadeControl);
        var cascade = vm.Nodes.First(n => n.Type == NodeType.CascadeControl);

        Assert.Contains(cascade.Fields, f => f.Key == "spO2" && f.IsNumber);
        Assert.Contains(cascade.Fields, f => f.Key == "atuadorMisturador" && f.IsBool);
        // The gas-mixer ships disabled — enrichment is deferred.
        Assert.False(cascade.Fields.First(f => f.Key == "atuadorMisturador").BoolValue);
    }
}
