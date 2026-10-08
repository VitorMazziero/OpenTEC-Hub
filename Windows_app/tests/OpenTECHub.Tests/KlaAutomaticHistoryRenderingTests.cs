using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Tests.Rendering;
using OpenTECHub.Views;
using Xunit;

namespace OpenTECHub.Tests;

public sealed partial class KlaDeterminationViewModelTests
{
    [Theory]
    [InlineData(KlaAssayProtocol.Abiotic, false)]
    [InlineData(KlaAssayProtocol.Biotic, false)]
    [InlineData(KlaAssayProtocol.Abiotic, true)]
    [InlineData(KlaAssayProtocol.Biotic, true)]
    public void AutomaticHistoryRendersAllAttemptsAndKeepsProtocolReadOnly(KlaAssayProtocol protocol, bool dark)
        => WpfRenderingHost.Run(() =>
        {
            WpfRenderingHost.SetTheme(dark);
            try
            {
                var request = RecipeExecutionContractTests.Request(protocol);
                var document = _store.CreateTest("Histórico automático simulado", request.Definition);
                document.RecipeRequest = request;
                document.Runs.Add(KlaAutomaticResultsTests.Run(request, 1, KlaAutomaticDecision.Retry));
                document.Runs.Add(KlaAutomaticResultsTests.Run(request, 2, KlaAutomaticDecision.Selected));
                _store.SaveTestManifest(document);
                _vm.LoadTest(document.FolderName);
                _vm.RefreshConditionsList();
                var view = new KlaDeterminationView { DataContext = _vm };
                WpfRenderingHost.RenderElement(view, 1280, 800, 120);
                var expander = Assert.Single(HistoryControls<Expander>(view)
                    .Where(item => Equals(item.Header, "Todas as tentativas · inclui recusadas")));
                expander.IsExpanded = true;
                var bitmap = WpfRenderingHost.RenderElement(view, 1280, 800, 120);
                Assert.True(VisualValidationHelper.ValidateBitmap(bitmap).IsNonTrivial);
                var attempts = HistoryControls<Button>(view).Where(button => Equals(button.Content, "Abrir tentativa")).ToArray();
                Assert.Equal(2, attempts.Length);
                Assert.All(attempts, button => Assert.Same(_vm.LoadRecordedAutomaticAttemptCommand, button.Command));
                Assert.All(HistoryControls<RadioButton>(view).Where(button => Equals(button.GroupName, "AssayProtocol")),
                    button => Assert.False(button.IsEnabled));
                Assert.Contains(HistoryControls<TextBlock>(view), block => block.Text.Contains("nova tentativa autorizada"));
                Assert.Contains(HistoryControls<TextBlock>(view), block => block.Text.Contains("Selecionada automaticamente"));
                Assert.Equal(protocol == KlaAssayProtocol.Biotic, _vm.IsBiotic);
                Assert.False(_vm.CanDecideRun);
                Assert.All(document.Runs, run => Assert.Equal(KlaOperatorDecision.Pending, run.EffectiveOutcome.OperatorDecision));
                WpfRenderingHost.SavePng(bitmap, Path.Combine(TestPaths.RepositoryRoot, "docs", "plans", "receitas-r61",
                    "evidence", "ui-results", $"history-{protocol}-{dark}.png"));
            }
            finally { WpfRenderingHost.SetTheme(false); }
        });

    private static IEnumerable<T> HistoryControls<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var nested in HistoryControls<T>(child)) yield return nested;
        }
    }
}
