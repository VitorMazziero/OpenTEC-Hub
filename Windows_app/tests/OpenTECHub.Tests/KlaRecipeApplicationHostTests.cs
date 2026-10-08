using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;
using OpenTECHub.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using OpenTECHub.Tests.Rendering;
using OpenTECHub.Views;
using System.Windows.Controls;
using Xunit;

namespace OpenTECHub.Tests;

[Collection(Rendering.WpfRenderingCollection.Name)]
public sealed class KlaRecipeApplicationHostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kla-host-" + Guid.NewGuid().ToString("N"));
    private readonly BackgroundFileWriter _writer = new(synchronous: true);
    private readonly RecipeAssayRestorationTests.Fixture _fixture = new(virtualTimers: true);
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };
    private KlaRecipeApplicationHost Host(bool isolated = true, BackgroundFileWriter? writer = null)
    {
        writer ??= _writer;
        var settings = new MemorySettingsService();
        var store = new KlaTestStore(Path.Combine(_root, "sessions"), writer);
        var factory = new KlaRecipeAssayExecutionFactory(_fixture.Device, _fixture.Arbiter, store,
            new KlaAnalysisEngine(), settings, _fixture.Clock, isolated);
        return new(_root, isolated, factory, store, settings, _fixture.Clock, writer);
    }
    private string ImportFile(KlaRecipeOperationalProfile profile)
    {
        var path = Path.Combine(_root, "import.json");
        File.WriteAllText(path, JsonSerializer.Serialize(profile, Options));
        return path;
    }

    [Fact]
    public async Task Installation_and_explicit_cultivation_reopen_and_concurrent_context_cannot_overwrite()
    {
        using var first = Host();
        Assert.Null(first.Context!.CultivationId); Assert.Empty(_fixture.Device.Sent);
        var stale = new KlaRecipeApplicationContext(_root, _writer);
        await first.Context.SetCultivationAsync(" culture-A ");
        await Assert.ThrowsAsync<InvalidOperationException>(() => stale.SetCultivationAsync("culture-B"));
        first.Dispose();
        using var reopened = Host();
        Assert.Equal(first.Context.InstallationId, reopened.Context!.InstallationId);
        Assert.Equal("culture-A", reopened.Context.CultivationId);
        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.Context.SetCultivationAsync("bad\nidentifier"));
        Assert.Equal("culture-A", reopened.Context.CultivationId);
        Assert.Empty(_fixture.Device.Sent);
    }

    [Theory]
    [InlineData(KlaAssayProtocol.Abiotic)] [InlineData(KlaAssayProtocol.Biotic)]
    public async Task Import_requires_matching_installation_and_reopens_explicit_qualification(KlaAssayProtocol protocol)
    {
        using var host = Host();
        var profile = KlaRecipeOperationalProfileTests.Profile(_fixture.Clock, protocol);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ImportProfileAsync(ImportFile(profile)));
        // The operator profile is always offered for both protocols (D-062); imported records are counted apart.
        Assert.Equal(2, host.Profiles.AvailableProfiles.Count(p => p.Capabilities.ProfileId == KlaRecipeOperatorProfile.Id));
        Assert.Empty(Imported(host));
        profile = profile with { Capabilities = profile.Capabilities with { InstallationId = host.Context!.InstallationId } };
        await host.ImportProfileAsync(ImportFile(profile));
        Assert.Single(Imported(host));
        host.Dispose();
        using var reopened = Host();
        Assert.Single(Imported(reopened));
        Assert.Null(reopened.AvailabilityError);
        // Without a cultivation the run uses its own budget identity (D-062); only the invalid graph blocks it.
        Assert.False(reopened.CanExecute(new RecipeDocument(), out var reason));
        Assert.DoesNotContain("cultivo", reason);
        Assert.Contains("validação", reason);
        Assert.Empty(_fixture.Device.Sent);
    }

    [Fact]
    public async Task Physical_environment_cannot_import_isolated_capability()
    {
        using var host = Host(isolated: false);
        var profile = KlaRecipeOperationalProfileTests.Profile(_fixture.Clock);
        profile = profile with { Capabilities = profile.Capabilities with { InstallationId = host.Context!.InstallationId } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ImportProfileAsync(ImportFile(profile)));
        Assert.False(host.CanExecute(new RecipeDocument(), out var reason));
        Assert.Contains("física", reason);
        Assert.Empty(_fixture.Device.Sent);
    }

    [Fact]
    public async Task Configured_host_resolves_recipe_work_without_actuation_and_ui_locks_cultivation_during_run()
    {
        using var host = Host();
        var profile = KlaRecipeOperationalProfileTests.Profile(_fixture.Clock);
        profile = profile with { Capabilities = profile.Capabilities with { InstallationId = host.Context!.InstallationId } };
        await host.ImportProfileAsync(ImportFile(profile));
        var node = RecipeAutonomousBlockConfigurationTests.ConfiguredKla();
        node.Set("minimumIntervalSeconds", 30);
        var recipe = new RecipeDocument();
        recipe.Nodes.AddRange([RecipeNode.Create(NodeType.Start, id: "start"), node, RecipeNode.Create(NodeType.End, id: "end")]);
        recipe.Connections.AddRange([new("start", ConnectorNames.Out, "kla", ConnectorNames.In),
            new("kla", ConnectorNames.Out, "end", ConnectorNames.In)]);
        using var engine = new RecipeEngine(_fixture.Arbiter, _fixture.Device, new MemorySettingsService(), _fixture.Clock,
            autonomousWorkSource: host);
        using var vm = new ReceitasViewModel(engine, new RecipeStore(Path.Combine(_root, "recipes")),
            operationalProfiles: host.Profiles, klaHost: host);
        vm.KlaCultivationId = profile.Template.Context?.CultivationId ?? "culture-A";
        await vm.SaveKlaCultivationCommand.ExecuteAsync(null);
        Assert.Equal(vm.KlaCultivationId, host.Context.CultivationId);
        Assert.True(host.CanExecute(recipe, out var reason), reason);
        var work = host.CreateWork(recipe, Guid.NewGuid(), engine.Resources!);
        Assert.Single(work.Assays); Assert.Empty(work.Schedules); Assert.Empty(_fixture.Device.Sent);
        foreach (var state in new[] { RecipeRunState.Running, RecipeRunState.Paused })
        {
            vm.RunState = state;
            Assert.False(vm.CanConfigureKlaAutomation);
            Assert.False(vm.SaveKlaCultivationCommand.CanExecute(null));
            Assert.False(vm.ImportKlaOperationalProfileCommand.CanExecute(null));
            vm.KlaCultivationId = "another-culture";
            await vm.SaveKlaCultivationCommand.ExecuteAsync(null);
            Assert.NotEqual(vm.KlaCultivationId, host.Context.CultivationId);
        }
    }

    [Theory]
    [InlineData("context")] [InlineData("catalog")] [InlineData("journal")]
    public void Corrupt_persistence_keeps_automation_closed_without_commanding_or_repairing_data(string target)
    {
        using (var initial = Host()) { }
        var path = target switch { "context" => Path.Combine(_root, "context.json"),
            "catalog" => Path.Combine(_root, "profiles", "profile-corrupt.json"), _ => Path.Combine(_root, "assay-journal.json") };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "broken");
        using var host = Host();
        Assert.NotNull(host.AvailabilityError);
        Assert.False(host.CanExecute(new RecipeDocument(), out var reason));
        Assert.Equal(host.AvailabilityError, reason);
        Assert.Equal("broken", File.ReadAllText(path));
        Assert.Empty(_fixture.Device.Sent);
    }

    [Fact]
    public void Failed_durable_context_write_disables_automation_without_crashing_application_host()
    {
        Directory.CreateDirectory(Path.Combine(_root, "context.json"));
        using var host = Host();
        Assert.Null(host.Context);
        Assert.NotNull(host.AvailabilityError);
        Assert.False(host.CanExecute(new RecipeDocument(), out _));
        Assert.Empty(_fixture.Device.Sent);
    }

    [Fact]
    public async Task Pending_cultivation_write_blocks_recipe_start_and_automatic_preflight_until_durable_completion()
    {
        using var writer = new BackgroundFileWriter();
        using var host = Host(writer: writer);
        using var engine = new RecipeEngine(_fixture.Arbiter, _fixture.Device, new MemorySettingsService(), _fixture.Clock,
            autonomousWorkSource: host);
        using var vm = new ReceitasViewModel(engine, new RecipeStore(Path.Combine(_root, "recipes")), klaHost: host);
        using var release = new ManualResetEventSlim(false);
        writer.Run(Path.Combine(_root, "controlled-wait"), () => release.Wait(TimeSpan.FromSeconds(5)));
        vm.KlaCultivationId = "culture-pending";
        var save = vm.SaveKlaCultivationCommand.ExecuteAsync(null);
        try
        {
            Assert.True(host.Context!.IsChangingCultivation);
            Assert.True(vm.IsKlaConfigurationBusy);
            Assert.False(vm.StartCommand.CanExecute(null));
            Assert.False(host.CanExecute(new RecipeDocument(), out var reason));
            Assert.Contains("gravação do cultivo", reason);
        }
        finally { release.Set(); }
        await save.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(vm.IsKlaConfigurationBusy);
        Assert.False(host.Context!.IsChangingCultivation);
        Assert.Equal("culture-pending", host.Context.CultivationId);
    }

    [Fact]
    public void Application_registration_uses_one_host_for_engine_and_editor_and_renders_configuration()
    {
        WpfRenderingHost.Run(() =>
        {
            var services = WpfRenderingHost.Services;
            var host = services.GetRequiredService<KlaRecipeApplicationHost>();
            Assert.Same(host, services.GetRequiredService<IRecipeAutonomousWorkSource>());
            Assert.Same(host.Profiles, services.GetRequiredService<KlaRecipeOperationalProfileRegistry>());
            var vm = services.GetRequiredService<ReceitasViewModel>();
            Assert.True(vm.HasKlaApplicationHost);
            Assert.Equal(host.Context?.InstallationId ?? "Indisponível", vm.KlaInstallationId);
            var view = new ReceitasView { DataContext = vm };
            ((Expander)view.FindName("KlaPreparationExpander")).IsExpanded = true;
            var bitmap = WpfRenderingHost.RenderElement(view, 1280, 800, 120);
            Assert.InRange(((Border)view.FindName("RecipeTabsBar")).ActualHeight, 20, 70);
            Assert.True(((System.Windows.FrameworkElement)view.FindName("RecipeBody")).ActualHeight > 400,
                "Preparation must preserve the recipe workspace and must not stretch the tab controls.");
            Assert.True(VisualValidationHelper.ValidateBitmap(bitmap).IsNonTrivial);
            WpfRenderingHost.SavePng(bitmap, Path.Combine(TestPaths.EvidenceRoot, "docs", "plans", "receitas-r42",
                "evidence", "application-configuration-125dpi.png"));
        });
    }

    public void Dispose()
    {
        _fixture.Dispose(); _writer.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static IEnumerable<KlaRecipeOperationalProfile> Imported(KlaRecipeApplicationHost host)
        => host.Profiles.AvailableProfiles.Where(p => p.Capabilities.ProfileId != KlaRecipeOperatorProfile.Id);
}
