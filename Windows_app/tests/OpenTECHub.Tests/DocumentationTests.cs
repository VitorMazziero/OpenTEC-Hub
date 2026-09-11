using System;
using System.IO;
using System.Linq;
using OpenTECHub.Services.Dialogs;
using OpenTECHub.Services.Documentation;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;
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
    /// <summary>Every word of a topic — section titles included — as one searchable string.</summary>
    private static string Flatten(DocumentationTopic topic) => string.Join(
        Environment.NewLine,
        topic.Sections.SelectMany(section =>
            new[] { section.Title }.Concat(section.Blocks.Select(b => b.Label + " " + b.Text))));

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
    public void Recipes_documentation_covers_concept_authoring_execution_and_json()
    {
        var topic = DocumentationCatalog.Find(DocumentationCatalog.RecipesTopicId)!;
        var titles = topic.Sections.Select(s => s.Title).ToList();
        var text = Flatten(topic);

        // The page is the least obvious in the program, so the manual opens with what it is for
        // before it opens with how to use it.
        Assert.Contains("Salvar", text, StringComparison.Ordinal);
        Assert.Contains("JSON", text, StringComparison.Ordinal);
        Assert.Contains("Iniciar", text, StringComparison.Ordinal);
        Assert.Contains("Parar", text, StringComparison.Ordinal);

        // Running a recipe taking the actuators away from manual control is the single fact an
        // operator must not discover by surprise.
        Assert.Contains("desativa o controle manual", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(titles, t => t.Contains("famílias de blocos", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Every_block_in_the_catalog_is_documented()
    {
        var blocks = DocumentationCatalog.Find(DocumentationCatalog.RecipeBlocksTopicId)!;
        var cascade = DocumentationCatalog.Find(DocumentationCatalog.RecipeCascadeTopicId)!;
        var documented = blocks.Sections
            .SelectMany(s => s.Blocks)
            .Where(b => b.Kind == DocumentationBlockKind.Field)
            .Select(b => b.Label)
            .Concat([cascade.Title])
            .ToList();

        // Straight from the catalog the page itself builds its library from: a block added there
        // and left undocumented fails here rather than reaching an operator unexplained.
        foreach (var definition in RecipeNodeCatalog.All)
        {
            Assert.Contains(
                documented,
                label => label.Contains(definition.Title, StringComparison.Ordinal)
                         || definition.Title.Contains(label, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_cascade_topic_goes_deeper_than_the_block_list()
    {
        var cascade = DocumentationCatalog.Find(DocumentationCatalog.RecipeCascadeTopicId)!;
        var text = Flatten(cascade);

        foreach (var term in new[] { "Kp", "Ki", "Kd", "K_DOT", "t_pred", "Anti-windup", "I_min", "I_max" })
        {
            Assert.Contains(term, text, StringComparison.OrdinalIgnoreCase);
        }

        // All four actuation modes, named as the picker names them.
        foreach (var mode in new[] { "Agitação", "Aeração", "Cascata (percentuais)", "Mapa (trajetória kLa)" })
        {
            Assert.Contains(mode, text, StringComparison.Ordinal);
        }

        // And the link back to the same loop seen from the Controle page.
        Assert.Contains("Controle", text, StringComparison.Ordinal);
        Assert.True(cascade.Sections.Count >= 6, "A cascata precisa de mais fôlego que um item de lista.");
    }

    [Fact]
    public void Context_dependent_block_behaviour_is_written_down()
    {
        var blocks = DocumentationCatalog.Find(DocumentationCatalog.RecipeBlocksTopicId)!;
        var text = Flatten(blocks);

        // The two blocks that change meaning inside a cascade loop, and the fields that appear
        // only for one choice of another field.
        Assert.Contains("no laço da cascata", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Manter Rodando", text, StringComparison.Ordinal);
        Assert.Contains("Histerese", text, StringComparison.Ordinal);
        Assert.Contains("enviar perfil", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_cascade_card_keeps_its_note_clear_of_the_port_labels()
    {
        var node = new RecipeNodeViewModel(RecipeNode.Create(NodeType.CascadeControl));

        var withCondition = node.Height;
        var portsWithCondition = node.Ports.Select(p => p.OffsetY).ToList();

        // With nothing wired to Condição de Saída the card shows the settling chip, and the card
        // has to grow by it: leaving the ports put is exactly how the old paragraph ended up
        // printed over "Entrada" and "Saída".
        node.IsCascadeWithoutExitCondition = true;

        Assert.True(node.Height > withCondition);
        Assert.All(
            node.Ports.Select(p => p.OffsetY).Zip(portsWithCondition),
            pair => Assert.True(pair.First > pair.Second));

        // Every port still sits inside the card.
        Assert.All(node.Ports, port => Assert.True(port.OffsetY < node.Height));

        // And the note itself is a chip now, with the sentence in the tooltip.
        Assert.Equal("SAI AO ESTABILIZAR", node.CascadeNoConditionBadge);
        Assert.Contains("±2 %", node.CascadeNoConditionHint, StringComparison.Ordinal);
    }

    [Fact]
    public void A_block_help_button_points_at_the_right_topic()
    {
        var cascade = new RecipeNodeViewModel(RecipeNode.Create(NodeType.CascadeControl));
        var timer = new RecipeNodeViewModel(RecipeNode.Create(NodeType.Timer));

        Assert.Equal(DocumentationCatalog.RecipeCascadeTopicId, cascade.DocumentationTopicId);
        Assert.Equal(DocumentationCatalog.RecipeBlocksTopicId, timer.DocumentationTopicId);
        Assert.NotNull(DocumentationCatalog.Find(cascade.DocumentationTopicId));
        Assert.NotNull(DocumentationCatalog.Find(timer.DocumentationTopicId));
    }

    [Fact]
    public void The_recipes_page_points_at_the_manual()
    {
        var xaml = ReadProjectFile(Path.Combine("Views", "ReceitasView.xaml"));

        Assert.Contains("CommandParameter=\"receitas\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedNode.DocumentationTopicId", xaml, StringComparison.Ordinal);

        // The paragraph that ran over the ports is gone from the card.
        Assert.DoesNotContain("Text=\"{Binding CascadeNoConditionHint}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CascadeNoConditionBadge", xaml, StringComparison.Ordinal);
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
