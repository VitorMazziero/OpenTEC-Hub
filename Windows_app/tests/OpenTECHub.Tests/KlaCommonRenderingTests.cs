using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenTECHub.Tests.Rendering;
using OpenTECHub.Views;
using Xunit;

namespace OpenTECHub.Tests;

public sealed partial class KlaDeterminationViewModelTests
{
    [Theory]
    [InlineData(false, false, 96)] [InlineData(false, true, 96)]
    [InlineData(true, false, 96)] [InlineData(true, true, 96)]
    [InlineData(false, false, 120)] [InlineData(false, true, 120)]
    [InlineData(true, false, 120)] [InlineData(true, true, 120)]
    [InlineData(false, false, 144)] [InlineData(false, true, 144)]
    [InlineData(true, false, 144)] [InlineData(true, true, 144)]
    public void E4_CommonLayoutAcrossModesAndDpi(bool biotic, bool single, double dpi)
    {
        WpfRenderingHost.Run(() =>
        {
            CreateCommonSession(biotic, single);
            var view = new KlaDeterminationView { DataContext = _vm };
            foreach (var size in new[] { new Size(936, 534), new Size(1680, 980) })
            {
                var issues = CompactLayoutTests.ArrangeAndInspect(view, size.Width, size.Height);
                Assert.True(issues.Count == 0, string.Join("\n", issues.Take(20)));
            }
            var bitmap = WpfRenderingHost.RenderElement(view, 1280, 800, dpi);
            Assert.True(VisualValidationHelper.ValidateBitmap(bitmap).IsNonTrivial);
            var output = Path.Combine(TestPaths.RepositoryRoot, "docs", "evidence", "ui-kla-e4",
                $"{(biotic ? "biotico" : "abiotico")}-{(single ? "unico" : "multiplos")}-{dpi / 96 * 100:F0}dpi.png");
            WpfRenderingHost.SavePng(bitmap, output);
        });
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task E4_ReviewAndModalsFitCompactWindow(bool biotic)
    {
        WpfRenderingHost.EnsureInitialized();
        await SimulateCommonRecovery(biotic);
        WpfRenderingHost.Run(() =>
        {
            var view = new KlaDeterminationView { DataContext = _vm };
            var issues = CompactLayoutTests.ArrangeAndInspect(view, 936, 534);
            Assert.True(issues.Count == 0, string.Join("\n", issues.Take(20)));
            WpfRenderingHost.SavePng(WpfRenderingHost.RenderElement(view, 1280, 800),
                Path.Combine(TestPaths.RepositoryRoot, "docs", "evidence", "ui-kla-e4", $"revisao-{biotic}.png"));
            var chartHost = (Border)view.FindName("ChartDoHost");
            var chart = (ScottPlot.WPF.WpfPlot)chartHost.Child;
            Assert.True(chart.Plot.Axes.GetLimits().Top < 150,
                "Phase intervals belong to the time axis and must not stretch the calibrated OD axis.");
            Assert.True(chartHost.ActualHeight > 600, "The primary chart should receive the available page height.");
            Assert.True(chartHost.TranslatePoint(new Point(), view).Y < 160,
                "Preparation and step cards must not push the plot halfway down the page.");
            _vm.CloseReview();
            _vm.OpenCreateDialog();
            issues = CompactLayoutTests.ArrangeAndInspect(view, 936, 534);
            Assert.True(issues.Count == 0, string.Join("\n", issues.Take(20)));
            Assert.Equal(KeyboardNavigationMode.Cycle,
                KeyboardNavigation.GetTabNavigation((DependencyObject)view.FindName("CreateModal")));
            var escape = Assert.Single(view.InputBindings.OfType<KeyBinding>(), b => b.Key == Key.Escape);
            Assert.NotNull(escape.Command);
            Assert.True(escape.Command.CanExecute(null));
            escape.Command.Execute(null);
            Assert.False(_vm.IsCreateDialogOpen);
            _vm.OpenAdvancedSettingsDialog();
            issues = CompactLayoutTests.ArrangeAndInspect(view, 936, 534);
            Assert.True(issues.Count == 0, string.Join("\n", issues.Take(20)));
            _vm.CloseDialogs();
            Assert.False(_vm.IsAdvancedSettingsDialogOpen);
            Assert.False(_vm.IsCreateDialogOpen);
        });
    }
}
