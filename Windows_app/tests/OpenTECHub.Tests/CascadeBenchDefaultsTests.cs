using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Control;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>D-068: the author's bench values are the defaults of the O₂ cascade, in the settings, the page and the recipe block.</summary>
public sealed class CascadeBenchDefaultsTests
{
    [Fact]
    public void Settings_defaults_are_the_bench_tuning_limits_and_windows()
    {
        var cascade = new CascadeSettings();
        var pid = cascade.CascadePid;

        Assert.Equal((0.075, 90.0, 0.035, 0.0010, 1.5, 25.0), (pid.KDot, pid.TPred, pid.Kp, pid.Ki, pid.Kd, pid.TauD));
        Assert.Equal((-1.0, 1.0, 2400, 8, 20, 3.0), (pid.IMin, pid.IMax, pid.MWindow, pid.JAvg, pid.NPred, pid.IntervalSeconds));
        Assert.Equal((50.0, 800.0, 0.5, 12.0), (cascade.AgitationMinRpm, cascade.AgitationMaxRpm, cascade.AerationMinLpm, cascade.AerationMaxLpm));
        Assert.Equal((0.0, 90.0, 10.0, 100.0), (cascade.AgitationEffortStart, cascade.AgitationEffortEnd, cascade.AerationEffortStart, cascade.AerationEffortEnd));
        // The first-run preset seeds the same values.
        var preset = new AppSettings().Cascade;
        Assert.Equal(cascade.CascadePid, preset.CascadePid);
        Assert.Equal(cascade.AerationMaxLpm, preset.AerationMaxLpm);
    }

    [Fact]
    public void The_recipe_oxygen_block_is_born_with_the_same_values()
    {
        var node = RecipeNode.Create(NodeType.CascadeControl);

        Assert.Equal((0.075, 0.035, 0.001, 1.5), (node.Number("kDot"), node.Number("kp"), node.Number("ki"), node.Number("kd")));
        Assert.Equal((-1.0, 1.0, 2400.0), (node.Number("iMin"), node.Number("iMax"), node.Number("janelaIntegradorS")));
        Assert.Equal((90.0, 20.0, 25.0, 8.0), (node.Number("horizonteTPredS"), node.Number("janelaPreditorAmostras"),
            node.Number("tauDFiltroS"), node.Number("janelaMediaAmostras")));
        Assert.Equal((50.0, 800.0, 0.5, 12.0), (node.Number("nMinRpm"), node.Number("nMaxRpm"), node.Number("qMinVvm"), node.Number("qMaxVvm")));
        Assert.Equal((0.0, 90.0, 10.0, 100.0), (node.Number("agitacaoOutMin"), node.Number("agitacaoOutMax"),
            node.Number("aeracaoOutMin"), node.Number("aeracaoOutMax")));
    }

    [Fact]
    public void The_oxygen_page_starts_from_the_same_values_when_nothing_was_saved()
    {
        var settings = new MemorySettingsService();
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var arbiter = new CommandArbiter(new RecordingDeviceService(), clock);
        using var service = new CascadeService(arbiter, arbiter, settings, new FakeKlaProfileStore(), clock);
        using var vm = new OxygenConfigViewModel(service, settings, new FakeKlaProfileStore());

        Assert.Equal(("0.075", "0.035", "1.500", "90.0", "25.0"), (vm.KDotText, vm.KpText, vm.KdText, vm.TPredText, vm.TauDText));
        Assert.Equal(("-1.0", "1.0", "2400", "8", "20"), (vm.IMinText, vm.IMaxText, vm.MWindowText, vm.JAvgText, vm.NPredText));
        Assert.Equal(("50", "800", "0.50", "12.00"), (vm.AgitationMinRpmText, vm.AgitationMaxRpmText, vm.AerationMinLpmText, vm.AerationMaxLpmText));
        Assert.Equal(("0", "90", "10", "100"), (vm.AgitationEffortStartText, vm.AgitationEffortEndText, vm.AerationEffortStartText, vm.AerationEffortEndText));
    }

    [Fact]
    public void The_recipe_name_identifies_the_assays_and_zero_attempts_means_no_limit()
    {
        var recipe = new RecipeDocument { Name = "  Cultivo: lote 7/2026?  " };
        Assert.Equal("Cultivo- lote 7-2026-", KlaRecipeAutonomousWorkSource.CultivationFor(recipe));
        Assert.Equal("receita", KlaRecipeAutonomousWorkSource.CultivationFor(new RecipeDocument { Name = "   " }));
        Assert.Equal(60, KlaRecipeAutonomousWorkSource.CultivationFor(new RecipeDocument { Name = new string('x', 90) }).Length);

        var node = RecipeNode.Create(NodeType.KlaAssay);
        Assert.Equal(KlaRecipeOperatorProfile.NotApplicableAttempts, RecipeAutonomousBlockConfiguration.ReadKla(node).Retry.MaximumAttemptsPerCultivation);
        node.Set("maximumAttemptsPerCultivation", 12);
        Assert.Equal(12, RecipeAutonomousBlockConfiguration.ReadKla(node).Retry.MaximumAttemptsPerCultivation);
    }
}
