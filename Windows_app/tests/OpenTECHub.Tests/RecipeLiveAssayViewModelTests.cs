using System.IO;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Tests.Rendering;
using OpenTECHub.ViewModels;
using OpenTECHub.Views;
using Xunit;

namespace OpenTECHub.Tests;

[Collection(Rendering.WpfRenderingCollection.Name)]
public sealed class RecipeLiveAssayViewModelTests
{
    internal static KlaRecipeProgress Progress(KlaAssayProtocol protocol = KlaAssayProtocol.Biotic)
    {
        var request = RecipeExecutionContractTests.Request(protocol);
        var condition = request.Definition.Conditions[0];
        return new(request.Context, protocol, null, request.Quality.ProfileId, request.Quality.Version, "saved-session",
            KlaRecipeProgressStage.Recovering, RunPhase.RestoringCultivation, new(condition.ConditionId, 1, 2), condition,
            1, 0, 40, 560, new(4, 120, 15, "Aguarde recuperação"), DateTimeOffset.UnixEpoch, null);
    }

    [Theory]
    [InlineData(KlaAssayProtocol.Abiotic)] [InlineData(KlaAssayProtocol.Biotic)]
    public void Live_observation_keeps_recovery_unconfirmed_and_uses_explicit_shared_budget_and_saved_counts(KlaAssayProtocol protocol)
    {
        var view = new RecipeLiveAssayViewModel(Progress(protocol));
        Assert.Contains("ainda em verificação", view.Stage);
        Assert.Contains(protocol == KlaAssayProtocol.Abiotic ? "não aplicável" : "avaliado ao final", view.Protocol);
        Assert.Contains("1 tentativa(s) com decisão gravada", view.Counts);
        Assert.Contains("0 selecionada(s)", view.Counts);
        Assert.Contains("4 tentativa(s) restantes", view.Budget);
        Assert.Contains("120", view.Budget);
        Assert.Contains("Aguarde recuperação", view.BudgetDetail);
        Assert.Contains("tentativa 2", view.Attempt);
        Assert.Contains("rpm", view.Condition); Assert.Contains("L/min", view.Condition);
        Assert.Contains("560", view.Time);
        var unobserved = new RecipeLiveAssayViewModel(view.Progress with { CultivationBudget = null,
            BudgetObservedUtc = null, BudgetObservationError = "Leitura indisponível" });
        Assert.Contains("ainda não observado", unobserved.Budget);
        Assert.Contains("Leitura indisponível", unobserved.BudgetDetail);
        Assert.Equal("", unobserved.BudgetObservation);
    }

    [Fact]
    public void Live_progress_renders_with_terminal_navigation_separate_from_active_acquisition()
    {
        WpfRenderingHost.Run(() =>
        {
            var vm = WpfRenderingHost.Services.GetRequiredService<ReceitasViewModel>();
            vm.LiveAssays.Add(new(Progress()));
            try
            {
                var view = new ReceitasView { DataContext = vm };
                ((Expander)view.FindName("KlaObservationsExpander")).IsExpanded = true;
                var bitmap = WpfRenderingHost.RenderElement(view, 1280, 800, 120);
                Assert.True(VisualValidationHelper.ValidateBitmap(bitmap).IsNonTrivial);
                Assert.InRange(((Border)view.FindName("RecipeTabsBar")).ActualHeight, 20, 70);
                WpfRenderingHost.SavePng(bitmap, Path.Combine(TestPaths.EvidenceRoot, "docs", "plans", "receitas-r42",
                    "evidence", "live-progress-125dpi.png"));
            }
            finally { vm.RefreshAutomaticSessions(); }
        });
    }
}
