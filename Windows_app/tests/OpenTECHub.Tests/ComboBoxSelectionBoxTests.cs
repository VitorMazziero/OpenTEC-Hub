using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// What a ComboBox shows in its <em>closed</em> box when the items are objects.
/// </summary>
/// <remarks>
/// The shell's ComboBox template renders the closed box itself, with
/// <c>Content="{TemplateBinding SelectionBoxItem}"</c> and
/// <c>ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"</c>. The open list honoured
/// <c>DisplayMemberPath</c> and the closed box did not, so the operator saw
/// <c>PowerTestSummary { FolderName = ... }</c> where the assay name belonged. This pins down
/// which of the two properties actually carries <c>DisplayMemberPath</c>, so the fix is chosen
/// from behaviour rather than from memory of WPF internals.
/// </remarks>
[Collection(Rendering.WpfRenderingCollection.Name)]
public sealed class ComboBoxSelectionBoxTests
{
    private sealed record Item(string Name, int Value);

    [Fact]
    public void DisplayMemberPath_alone_does_not_produce_a_selection_box_template()
    {
        var (template, boxItem) = OnStaThread(() =>
        {
            var combo = new ComboBox
            {
                ItemsSource = new List<Item> { new("Rushton", 1), new("Smith", 2) },
                DisplayMemberPath = "Name",
            };

            combo.SelectedIndex = 0;
            Realize(combo);

            return (combo.SelectionBoxItemTemplate, combo.SelectionBoxItem);
        });

        // The item is handed over raw, so a ContentPresenter with no template falls back to
        // ToString() - which for a record is its whole shape.
        Assert.NotNull(boxItem);
        Assert.Null(template);
        Assert.Contains("Name = Rushton", boxItem!.ToString() ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void An_explicit_item_template_does_reach_the_selection_box()
    {
        var template = OnStaThread(() =>
        {
            var combo = new ComboBox
            {
                ItemsSource = new List<Item> { new("Rushton", 1) },
                ItemTemplate = BuildNameTemplate(),
            };

            combo.SelectedIndex = 0;
            Realize(combo);

            return combo.SelectionBoxItemTemplate;
        });

        // ItemTemplate is the property the closed box actually reads, so that is what the
        // shell's pickers must set when the items are not strings.
        Assert.NotNull(template);
    }

    private static DataTemplate BuildNameTemplate()
    {
        var factory = new FrameworkElementFactory(typeof(TextBlock));
        factory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Name"));
        return new DataTemplate { VisualTree = factory };
    }

    /// <summary>Puts the combo in a window and runs a layout pass, so the selection box updates.</summary>
    private static void Realize(ComboBox combo)
    {
        var window = new Window { Content = combo, Width = 200, Height = 60 };
        window.Show();
        combo.Measure(new Size(200, 60));
        combo.Arrange(new Rect(0, 0, 200, 60));
        window.Close();
    }

    private static T OnStaThread<T>(Func<T> action)
    {
        return Rendering.WpfRenderingHost.Run(action);
    }
}
