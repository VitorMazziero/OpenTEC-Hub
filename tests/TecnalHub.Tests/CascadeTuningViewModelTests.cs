using TecnalHub.Protocol;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Control;
using TecnalHub.Services.Persistence;
using TecnalHub.Services.Recipes;
using TecnalHub.ViewModels;
using Xunit;

namespace TecnalHub.Tests;

/// <summary>The Cascata e sintonia workspace: staging, validation, apply/revert, save/load.</summary>
public class CascadeTuningViewModelTests
{
    private sealed class Fixture : IDisposable
    {
        public Fixture(AppSettings? settings = null)
        {
            Device = new RecordingDeviceService();
            Settings = new MemorySettingsService(settings ?? new AppSettings());
            Clock = new TestClock(DateTimeOffset.UnixEpoch);
            var arbiter = new CommandArbiter(Device, Clock);
            Service = new CascadeService(arbiter, arbiter, Settings, new FakeKlaProfileStore(), Clock);
            Service.SelectMode(CascadeMode.AgitationOnly);
            ViewModel = new CascadeTuningViewModel(Service, Settings);
        }

        public RecordingDeviceService Device { get; }
        public MemorySettingsService Settings { get; }
        public TestClock Clock { get; }
        public CascadeService Service { get; }
        public CascadeTuningViewModel ViewModel { get; }

        public void PushOxygen(double oxygen, int frames)
        {
            for (var i = 0; i < frames; i++)
            {
                Clock.Advance(TimeSpan.FromSeconds(2));
                Device.PushTelemetry(new SensorSnapshot { OxygenCalibrated = oxygen });
            }
        }

        public void Dispose()
        {
            ViewModel.Dispose();
            Service.Dispose();
        }
    }

    [Fact]
    public void Defaults_load_from_settings_and_are_valid()
    {
        using var fixture = new Fixture();

        Assert.Equal(0.25, fixture.ViewModel.Kp);
        Assert.Null(fixture.ViewModel.ValidationError);
        Assert.True(fixture.ViewModel.CanApply);
        Assert.False(fixture.ViewModel.HasPendingChange);
    }

    [Fact]
    public void An_interval_outside_the_firmware_band_blocks_apply()
    {
        using var fixture = new Fixture();

        fixture.ViewModel.IntervalSeconds = 0.02;

        Assert.NotNull(fixture.ViewModel.ValidationError);
        Assert.False(fixture.ViewModel.CanApply);
        Assert.True(fixture.ViewModel.HasPendingChange);
    }

    [Fact]
    public void Editing_a_gain_marks_the_workspace_dirty()
    {
        using var fixture = new Fixture();

        fixture.ViewModel.Kp = 0.5;

        Assert.True(fixture.ViewModel.HasPendingChange);
    }

    [Fact]
    public void Apply_configures_the_service_persists_and_never_sends()
    {
        using var fixture = new Fixture();
        fixture.ViewModel.Kp = 0.4;
        fixture.ViewModel.OxygenSetpoint = 35;

        fixture.ViewModel.ApplyCommand.Execute(null);

        Assert.Equal(0.4, fixture.Service.Tuning.Kp);
        Assert.Equal(35, fixture.Service.OxygenSetpoint);
        Assert.Equal(0.4, fixture.Settings.Current.Cascade.Kp);
        Assert.False(fixture.ViewModel.HasPendingChange);
        Assert.Empty(fixture.Device.Sent);
    }

    [Fact]
    public void Revert_restores_the_applied_values()
    {
        using var fixture = new Fixture();
        fixture.ViewModel.Kp = 0.9;

        fixture.ViewModel.RevertCommand.Execute(null);

        Assert.Equal(0.25, fixture.ViewModel.Kp);
        Assert.False(fixture.ViewModel.HasPendingChange);
    }

    [Fact]
    public void Saving_a_tuning_persists_it_and_loading_only_stages()
    {
        using var fixture = new Fixture();
        fixture.ViewModel.Kp = 0.6;
        fixture.ViewModel.PresetName = "Ensaio rápido";

        fixture.ViewModel.SaveTuningCommand.Execute(null);

        var saved = Assert.Single(fixture.Settings.Current.CascadeTuningPresets);
        Assert.Equal("Ensaio rápido", saved.Name);
        Assert.Equal(0.6, saved.Settings.Kp);

        // Move away, then load: the fields stage but nothing applies or sends.
        fixture.ViewModel.Kp = 0.1;
        fixture.ViewModel.ApplyCommand.Execute(null);
        fixture.ViewModel.LoadTuningCommand.Execute(null);

        Assert.Equal(0.6, fixture.ViewModel.Kp);
        Assert.True(fixture.ViewModel.HasPendingChange);
        Assert.Equal(0.1, fixture.Service.Tuning.Kp); // still the applied value, not the loaded one
        Assert.Empty(fixture.Device.Sent);
    }

    [Fact]
    public void Toggling_engage_engages_and_disengages_the_service()
    {
        using var fixture = new Fixture();

        fixture.ViewModel.ToggleEngageCommand.Execute(null);
        Assert.True(fixture.Service.IsEngaged);
        Assert.True(fixture.ViewModel.IsEngaged);

        fixture.ViewModel.ToggleEngageCommand.Execute(null);
        Assert.False(fixture.Service.IsEngaged);
        Assert.False(fixture.ViewModel.IsEngaged);
    }

    [Fact]
    public void Toggling_engage_uses_the_mode_selected_in_the_workspace()
    {
        using var fixture = new Fixture();
        fixture.ViewModel.SelectedModeOption = fixture.ViewModel.Modes.Single(m => m.Mode == CascadeMode.DualCascade);

        fixture.ViewModel.ToggleEngageCommand.Execute(null);

        Assert.True(fixture.Service.IsEngaged);
        Assert.Equal(CascadeMode.DualCascade, fixture.Service.Mode);
    }

    [Fact]
    public void Live_terms_populate_once_engaged_and_telemetry_flows()
    {
        using var fixture = new Fixture();
        fixture.ViewModel.ToggleEngageCommand.Execute(null);

        fixture.PushOxygen(oxygen: 12, frames: 10);

        Assert.Equal("12,0 %".Replace(',', '.'), fixture.ViewModel.LiveOxygen.Replace(',', '.'));
        Assert.NotEqual("—", fixture.ViewModel.LiveOutput);
        Assert.NotEqual("—", fixture.ViewModel.LiveAgitation);
    }

    private sealed class FakeEngine : IRecipeEngine
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

    private sealed class FakeStore : IRecipeStore
    {
        public string Directory => "memory";
        public IReadOnlyList<RecipeSummary> List() => [];
        public RecipeDocument Load(string fileName) => new();
        public string Save(RecipeDocument recipe, string? fileName = null) => fileName ?? "recipe.json";
        public void Delete(string fileName) { }
    }

    [Fact]
    public void Recipe_node_is_loaded_and_updated_on_apply()
    {
        var node = RecipeNode.Create(NodeType.CascadeControl);
        node.Parameters["spO2"] = 42.0;
        node.Parameters["kp"] = 0.88;
        node.Parameters["modo"] = "AgitationOnly";

        var doc = new RecipeDocument { Nodes = [node] };
        var tab = new RecipeTabViewModel(doc, "Receita Teste");
        var receitas = new ReceitasViewModel(new FakeEngine(), new FakeStore());
        receitas.Tabs.Clear();
        receitas.Tabs.Add(tab);
        receitas.SelectedTab = tab;

        using var fixture = new Fixture();
        using var vm = new CascadeTuningViewModel(fixture.Service, fixture.Settings, receitas);

        Assert.Equal(42.0, vm.OxygenSetpoint);
        Assert.Equal(0.88, vm.Kp);
        Assert.Equal(CascadeMode.AgitationOnly, vm.SelectedModeOption?.Mode);

        vm.Kp = 1.25;
        Assert.True(vm.HasPendingChange);

        vm.ApplyCommand.Execute(null);

        Assert.False(vm.HasPendingChange);
        Assert.Equal(1.25, node.Number("kp"));
        Assert.Equal(1.25, fixture.Service.Tuning.Kp);
    }

    [Fact]
    public void Recipe_canvas_edit_refreshes_the_oxygen_workspace()
    {
        var node = RecipeNode.Create(NodeType.CascadeControl);
        var tab = new RecipeTabViewModel(new RecipeDocument { Nodes = [node] }, "Receita Teste");
        var receitas = new ReceitasViewModel(new FakeEngine(), new FakeStore());
        receitas.Tabs.Clear();
        receitas.Tabs.Add(tab);
        receitas.SelectedTab = tab;

        using var fixture = new Fixture();
        using var vm = new CascadeTuningViewModel(fixture.Service, fixture.Settings, receitas);

        tab.Nodes.Single().SetParam("kp", 0.73);

        Assert.Equal(0.73, vm.Kp);
        Assert.False(vm.HasPendingChange);
    }

    [Fact]
    public void Revert_restores_from_active_recipe_node()
    {
        var node = RecipeNode.Create(NodeType.CascadeControl);
        node.Parameters["kp"] = 0.55;

        var doc = new RecipeDocument { Nodes = [node] };
        var tab = new RecipeTabViewModel(doc, "Receita Teste");
        var receitas = new ReceitasViewModel(new FakeEngine(), new FakeStore());
        receitas.Tabs.Clear();
        receitas.Tabs.Add(tab);
        receitas.SelectedTab = tab;

        using var fixture = new Fixture();
        using var vm = new CascadeTuningViewModel(fixture.Service, fixture.Settings, receitas);

        vm.Kp = 0.99;
        Assert.True(vm.HasPendingChange);

        vm.RevertCommand.Execute(null);

        Assert.False(vm.HasPendingChange);
        Assert.Equal(0.55, vm.Kp);
    }
}
