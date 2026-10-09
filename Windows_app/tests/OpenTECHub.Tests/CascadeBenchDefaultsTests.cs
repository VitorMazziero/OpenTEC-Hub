using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Control;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>D-068/D-071: the author's tuning is the default of the O₂ cascade, in the settings, the page and the recipe block.</summary>
public sealed class CascadeBenchDefaultsTests
{
    [Fact]
    public void Settings_defaults_are_the_bench_tuning_limits_and_windows()
    {
        var cascade = new CascadeSettings();
        var pid = cascade.CascadePid;

        Assert.Equal((0.075, 60.0, 0.035, 0.0010, 3.5, 30.0), (pid.KDot, pid.TPred, pid.Kp, pid.Ki, pid.Kd, pid.TauD));
        Assert.Equal((-2.5, 2.5, 2400, 20, 15, 3.0), (pid.IMin, pid.IMax, pid.MWindow, pid.JAvg, pid.NPred, pid.IntervalSeconds));
        Assert.False(pid.HabilitarGainScheduling);
        Assert.Equal(new ModePidSettings(), pid); // a blank mode starts from the same tuning
        Assert.Equal((50.0, 800.0, 0.5, 12.0), (cascade.AgitationMinRpm, cascade.AgitationMaxRpm, cascade.AerationMinLpm, cascade.AerationMaxLpm));
        Assert.Equal((0.0, 90.0, 10.0, 100.0), (cascade.AgitationEffortStart, cascade.AgitationEffortEnd, cascade.AerationEffortStart, cascade.AerationEffortEnd));
        Assert.Equal(0.2, cascade.AerationStepLpm);
        // The first-run preset seeds the same values.
        var preset = new AppSettings().Cascade;
        Assert.Equal(cascade.CascadePid, preset.CascadePid);
        Assert.Equal(cascade.AerationMaxLpm, preset.AerationMaxLpm);
    }

    [Fact]
    public void The_recipe_oxygen_block_is_born_with_the_same_values()
    {
        var node = RecipeNode.Create(NodeType.CascadeControl);

        Assert.Equal((0.075, 0.035, 0.001, 3.5), (node.Number("kDot"), node.Number("kp"), node.Number("ki"), node.Number("kd")));
        Assert.Equal((-2.5, 2.5, 2400.0), (node.Number("iMin"), node.Number("iMax"), node.Number("janelaIntegradorS")));
        Assert.Equal((60.0, 15.0, 30.0, 20.0), (node.Number("horizonteTPredS"), node.Number("janelaPreditorAmostras"),
            node.Number("tauDFiltroS"), node.Number("janelaMediaAmostras")));
        Assert.Equal((50.0, 800.0, 0.5, 12.0), (node.Number("nMinRpm"), node.Number("nMaxRpm"), node.Number("qMinVvm"), node.Number("qMaxVvm")));
        Assert.Equal((0.0, 90.0, 10.0, 100.0), (node.Number("agitacaoOutMin"), node.Number("agitacaoOutMax"),
            node.Number("aeracaoOutMin"), node.Number("aeracaoOutMax")));
        Assert.Equal(0.2, node.Number("passoAeracaoLpm"));
    }

    [Fact]
    public void The_oxygen_page_starts_from_the_same_values_when_nothing_was_saved()
    {
        var settings = new MemorySettingsService();
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var arbiter = new CommandArbiter(new RecordingDeviceService(), clock);
        using var service = new CascadeService(arbiter, arbiter, settings, new FakeKlaProfileStore(), clock);
        using var vm = new OxygenConfigViewModel(service, settings, new FakeKlaProfileStore());

        Assert.Equal(("0.075", "0.035", "3.500", "60.0", "30.0"), (vm.KDotText, vm.KpText, vm.KdText, vm.TPredText, vm.TauDText));
        Assert.Equal(("-2.5", "2.5", "2400", "20", "15"), (vm.IMinText, vm.IMaxText, vm.MWindowText, vm.JAvgText, vm.NPredText));
        Assert.Equal(("50", "800", "0.50", "12.00"), (vm.AgitationMinRpmText, vm.AgitationMaxRpmText, vm.AerationMinLpmText, vm.AerationMaxLpmText));
        Assert.Equal(("0", "90", "10", "100"), (vm.AgitationEffortStartText, vm.AgitationEffortEndText, vm.AerationEffortStartText, vm.AerationEffortEndText));
        Assert.Equal("0.2", vm.AerationStepLpmText);
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
