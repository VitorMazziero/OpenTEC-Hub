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
                var points = Enumerable.Range(0, 40).Select(index => new KlaRawDataPoint(DateTimeOffset.UnixEpoch.AddSeconds(index),
                    index, RunPhase.Reoxygenating, 80 - 60 * Math.Exp(-index / 10d), 80 - 60 * Math.Exp(-index / 10d),
                    2, 2, 300, false, false, true)).ToArray();
                foreach (var run in document.Runs)
                {
                    _store.SaveRunRawData(document.FolderName, run.FolderName, points);
                    _store.SaveRunAnalysis(document.FolderName, run.FolderName, new KlaAnalysisRevision
                        { RevisionNumber = 1, KlaPerHour = 77, Quality = DecisionQuality.Acceptable });
                }
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
                attempts[0].Command!.Execute(attempts[0].CommandParameter);
                Assert.True(_vm.IsReviewOpen);
                Assert.Equal(points.Length, _vm.LivePoints.Count);
                Assert.False(_vm.CanDecideRun);
                Assert.Equal(77, _vm.CurrentAnalysis!.KlaPerHour);
                Assert.Null(_vm.RecordedAutomaticAttempts[0].Run.AutomaticDecision!.KlaPerHour);
                var review = WpfRenderingHost.RenderElement(view, 1280, 800, 120);
                var chart = Assert.IsType<ScottPlot.WPF.WpfPlot>(((Border)view.FindName("ChartDoHost")).Child);
                var series = Assert.Single(chart.Plot.GetPlottables<ScottPlot.Plottables.DataLogger>().Where(series => series.IsVisible));
                var limits = series.GetAxisLimits();
                Assert.Equal(0, limits.Left);
                Assert.Equal(39, limits.Right);
                Assert.Equal(20, limits.Bottom, 6);
                Assert.Equal(_vm.LivePoints[^1].DOFiltered, limits.Top, 6);
                var curvePath = Path.Combine(TestPaths.RepositoryRoot, "docs", "plans", "receitas-r61",
                    "evidence", "ui-results", $"curve-{protocol}-{dark}.png");
                chart.Plot.SavePng(curvePath, 1000, 650);
                WpfRenderingHost.SavePng(review, Path.Combine(TestPaths.RepositoryRoot, "docs", "plans", "receitas-r61",
                    "evidence", "ui-results", $"review-{protocol}-{dark}.png"));
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
