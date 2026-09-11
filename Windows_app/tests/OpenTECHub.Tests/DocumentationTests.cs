using System;
using System.IO;
using System.Linq;
using OpenTECHub.Services.Dialogs;
using OpenTECHub.Services.Documentation;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Theme;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// The in-app manual and the pages that now defer to it.
/// </summary>
/// <remarks>
/// Two things are guarded here, and they only mean something together. The catalog has to hold
/// real content for every page that claims to be documented, and the pages have to have actually
/// given the explanation up — a card that kept its three paragraphs *and* gained a help button
/// is the failure this file exists to catch.
/// </remarks>
public sealed class DocumentationTests
{
    private static SettingsViewModel CreateSettings() => new(
        new MemorySettingsService(new AppSettings()),
        new NullThemeService(),
        new RecordingDeviceService(),
        new NullDialogService());

    /// <summary>Theme service that records nothing: these tests never switch themes.</summary>
    private sealed class NullThemeService : IThemeService
    {
        public bool IsDark => false;

        public event Action<bool>? ThemeChanged;

        public void Apply(ThemePreference preference) => ThemeChanged?.Invoke(IsDark);
    }

    /// <summary>Dialog service that always declines: nothing here may open a window.</summary>
    private sealed class NullDialogService : IDialogService
    {
        public bool ConfirmDestructive(string title, string consequence, string exactCommand) => false;

        public bool Confirm(string title, string message, string confirmText = "Confirmar", string cancelText = "Cancelar", bool isDanger = false) => false;

        public bool PromptInput(string title, string message, out string response, string initialValue = "")
        {
            response = "";
            return false;
        }

        public RecipeStartOption PromptRecipeStart(string recipeName) => RecipeStartOption.StartPreserving;
    }

    private static string ReadProjectFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OpenTECHub.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, "src", "OpenTECHub", relativePath);
        Assert.True(File.Exists(path), $"Arquivo não encontrado: {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void Every_topic_has_an_id_a_title_and_real_content()
    {
        Assert.NotEmpty(DocumentationCatalog.Topics);

        foreach (var topic in DocumentationCatalog.Topics)
        {
            Assert.False(string.IsNullOrWhiteSpace(topic.Id));
            Assert.False(string.IsNullOrWhiteSpace(topic.Title));
            Assert.False(string.IsNullOrWhiteSpace(topic.Summary));
            Assert.NotEmpty(topic.Sections);

            foreach (var section in topic.Sections)
            {
                Assert.False(string.IsNullOrWhiteSpace(section.Title));
                Assert.NotEmpty(section.Blocks);
                foreach (var block in section.Blocks)
                {
                    Assert.False(string.IsNullOrWhiteSpace(block.Text));

                    // A field without its on-screen name documents nothing an operator can
                    // find; a non-field with a label would render a dangling dash.
                    if (block.Kind == DocumentationBlockKind.Field)
                    {
                        Assert.False(string.IsNullOrWhiteSpace(block.Label));
                    }
                    else
                    {
                        Assert.Equal("", block.Label);
                    }
                }
            }
        }
    }

    [Fact]
    public void Topic_ids_are_unique()
    {
        var ids = DocumentationCatalog.Topics.Select(t => t.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void The_dashboard_and_control_pages_are_documented_first()
    {
        // The implementation plan starts with these two: they are the pages every operator
        // uses, and the ones a newcomer meets before anything else.
        Assert.Equal(DocumentationCatalog.DashboardTopicId, DocumentationCatalog.Topics[0].Id);
        Assert.Equal(DocumentationCatalog.ControlTopicId, DocumentationCatalog.Topics[1].Id);
    }

    [Fact]
    public void Each_page_topic_explains_its_layout_before_its_controls()
    {
        foreach (var topicId in new[] { DocumentationCatalog.DashboardTopicId, DocumentationCatalog.ControlTopicId })
        {
            var topic = DocumentationCatalog.Find(topicId)!;
            Assert.Contains("organizada", topic.Sections[0].Title, StringComparison.OrdinalIgnoreCase);

            // And the controls themselves are named, not merely described in prose.
            Assert.Contains(
                topic.Sections.SelectMany(s => s.Blocks),
                b => b.Kind == DocumentationBlockKind.Field);
        }
    }

    [Fact]
    public void Control_documentation_covers_every_row_of_the_page()
    {
        var topic = DocumentationCatalog.Find(DocumentationCatalog.ControlTopicId)!;
        var labels = topic.Sections
            .SelectMany(s => s.Blocks)
            .Where(b => b.Kind == DocumentationBlockKind.Field)
            .Select(b => b.Label)
            .ToList();

        foreach (var row in new[]
                 {
                     "Agitação", "Temperatura", "pH", "Oxigênio", "Nutrientes", "Antiespumante",
                     "Alívio de Pressão", "Vazão de Ar", "Distância", "Bomba Externa",
                     "Absorbância", "Frasco Agitador",
                 })
        {
            Assert.Contains(labels, label => label.Contains(row, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Find_falls_back_to_null_for_an_unknown_topic()
    {
        Assert.Null(DocumentationCatalog.Find("pagina-que-nao-existe"));
        Assert.Null(DocumentationCatalog.Find(null));
        Assert.NotNull(DocumentationCatalog.Find(DocumentationCatalog.PowerTareTopicId));
    }

    [Fact]
    public void Settings_exposes_documentation_as_the_last_section_after_device_commands()
    {
        using var vm = CreateSettings();
        var sections = vm.Sections;

        var device = sections.Select((s, i) => (s.Id, i)).Single(x => x.Id == "device").i;
        var documentation = sections.Select((s, i) => (s.Id, i)).Single(x => x.Id == SettingsViewModel.DocumentationSectionId).i;

        Assert.Equal(device + 1, documentation);
        Assert.Equal("Documentação", sections[documentation].Label);
        Assert.Equal("Book", sections[documentation].Glyph);
    }

    [Fact]
    public void Selecting_a_topic_switches_to_the_documentation_section()
    {
        using var vm = CreateSettings();
        Assert.NotEqual(SettingsViewModel.DocumentationSectionId, vm.SelectedSection.Id);

        vm.SelectDocumentation(DocumentationCatalog.PowerSinglePointTopicId);

        Assert.Equal(SettingsViewModel.DocumentationSectionId, vm.SelectedSection.Id);
        Assert.Equal(DocumentationCatalog.PowerSinglePointTopicId, vm.SelectedDocumentationTopic.Id);
    }

    [Fact]
    public void An_unknown_topic_still_opens_the_manual()
    {
        // A help button whose topic was renamed must land the operator in the manual, not
        // leave them on the page wondering whether the click registered.
        using var vm = CreateSettings();

        vm.SelectDocumentation("topico-renomeado");

        Assert.Equal(SettingsViewModel.DocumentationSectionId, vm.SelectedSection.Id);
        Assert.Equal(DocumentationCatalog.Topics[0].Id, vm.SelectedDocumentationTopic.Id);
    }

    [Fact]
    public void The_book_icon_exists_for_the_documentation_section()
    {
        var icons = ReadProjectFile(Path.Combine("Resources", "Icons", "Icons.xaml"));
        Assert.Contains("IconBookGeometry", icons, StringComparison.Ordinal);
    }

    [Fact]
    public void The_settings_page_renders_the_catalog_rather_than_its_own_copy()
    {
        var xaml = ReadProjectFile(Path.Combine("Views", "SettingsView.xaml"));

        Assert.Contains("ConverterParameter=documentation", xaml, StringComparison.Ordinal);
        Assert.Contains("DocumentationTopics", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedDocumentationTopic", xaml, StringComparison.Ordinal);
        Assert.Contains("DocBlockSelector", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_power_page_points_at_the_manual_instead_of_printing_it()
    {
        var xaml = ReadProjectFile(Path.Combine("Views", "PowerView.xaml"));

        // The explanations that used to sit inside the cards are gone.
        Assert.DoesNotContain("Para que serve o Ponto Único", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("quantifica o atrito mecânico de selos", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("wattímetro externo de bancada", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("a tabela de condições é preservada", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Sem tara aplicada, o ensaio opera em modo relativo", xaml, StringComparison.Ordinal);

        // And each card that lost text carries the way back to it.
        Assert.Contains("OpenDocumentationCommand", xaml, StringComparison.Ordinal);
        foreach (var topicId in new[]
                 {
                     DocumentationCatalog.PowerTareTopicId,
                     DocumentationCatalog.PowerSinglePointTopicId,
                     DocumentationCatalog.PowerElectricalTopicId,
                     DocumentationCatalog.PowerKlaMapTopicId,
                 })
        {
            Assert.Contains($"CommandParameter=\"{topicId}\"", xaml, StringComparison.Ordinal);
            Assert.NotNull(DocumentationCatalog.Find(topicId));
        }
    }

    [Fact]
    public void The_assay_card_loads_an_assay_by_name_and_stacks_its_actions()
    {
        var xaml = ReadProjectFile(Path.Combine("Views", "PowerView.xaml"));

        Assert.Contains("Content=\"Carregar Ensaio\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Abrir\"", xaml, StringComparison.Ordinal);

        // The three actions no longer share one row with the status label: at 340 DIP that
        // row is what clipped "Nenhum ensaio" and would clip the longer button outright.
        Assert.Contains("PowerSubCard", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Tables_in_the_narrow_column_size_themselves_instead_of_clipping()
    {
        var xaml = ReadProjectFile(Path.Combine("Views", "PowerView.xaml"));

        // Fixed column widths summing past the 340 DIP column are what cropped the headers.
        Assert.DoesNotContain("Header=\"Pot. Mecânica (W)\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"Pot. Elétrica (W)\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"P mec (W)\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"P elét (W)\"", xaml, StringComparison.Ordinal);
    }
}
