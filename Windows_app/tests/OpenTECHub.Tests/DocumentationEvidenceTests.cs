using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using OpenTECHub.Services.Documentation;
using OpenTECHub.Tests.Rendering;
using OpenTECHub.ViewModels;
using OpenTECHub.Views;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// Renders the screens this change touched into <c>docs/evidence/ui-documentation/</c>.
/// </summary>
/// <remarks>
/// The evidence folder is produced, not collected by hand: a screenshot pasted into a chat
/// cannot be re-made when the layout changes again, and a stale picture of a fixed screen is
/// worse than none. Running the suite refreshes every file here from the real visual tree.
/// </remarks>
public sealed class DocumentationEvidenceTests
{
    private static readonly string EvidenceRoot = Path.Combine(
        TestPaths.RepositoryRoot, "docs", "evidence", "ui-documentation");

    [Fact]
    public void Capture_power_assembly_tab_with_the_reorganised_assay_card()
    {
        WpfRenderingHost.Run(() =>
        {
            var shell = WpfRenderingHost.Services.GetRequiredService<ShellViewModel>();
            var view = new PowerView { DataContext = shell.PowerTest };
            var bitmap = WpfRenderingHost.RenderElement(view, 1280, 800);

            Assert.True(VisualValidationHelper.ValidateBitmap(bitmap).IsNonTrivial);
            Save(bitmap, "potencia-montagem.png");
        });
    }

    [Fact]
    public void Capture_power_validation_tab_without_its_paragraphs()
    {
        WpfRenderingHost.Run(() =>
        {
            var shell = WpfRenderingHost.Services.GetRequiredService<ShellViewModel>();
            var view = new PowerView { DataContext = shell.PowerTest };

            // The tab is selected on the built tree, not through a view-model flag: which tab
            // is open is view state, and the capture has to show what the operator sees.
            view.Loaded += (_, _) =>
            {
                var tabs = FindDescendant<TabControl>(view);
                if (tabs is not null)
                {
                    tabs.SelectedIndex = 2;
                    tabs.UpdateLayout();
                }
            };

            var bitmap = WpfRenderingHost.RenderElement(view, 1280, 800);
            Assert.True(VisualValidationHelper.ValidateBitmap(bitmap).IsNonTrivial);
            Save(bitmap, "potencia-validacao.png");
        });
    }

    [Theory]
    [InlineData(DocumentationCatalog.DashboardTopicId, "documentacao-painel.png")]
    [InlineData(DocumentationCatalog.ControlTopicId, "documentacao-controle.png")]
    [InlineData(DocumentationCatalog.PowerTareTopicId, "documentacao-potencia-tara.png")]
    public void Capture_the_documentation_section_for_a_topic(string topicId, string fileName)
    {
        WpfRenderingHost.Run(() =>
        {
            var shell = WpfRenderingHost.Services.GetRequiredService<ShellViewModel>();
            shell.Settings.SelectDocumentation(topicId);

            var view = new SettingsView { DataContext = shell.Settings };
            var bitmap = WpfRenderingHost.RenderElement(view, 1280, 900);

            Assert.True(VisualValidationHelper.ValidateBitmap(bitmap).IsNonTrivial);
            Save(bitmap, fileName);
        });
    }

    private static void Save(System.Windows.Media.Imaging.RenderTargetBitmap bitmap, string fileName)
    {
        var path = Path.Combine(EvidenceRoot, fileName);
        WpfRenderingHost.SavePng(bitmap, path);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 1024);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
