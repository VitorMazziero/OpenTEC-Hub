using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace OpenTECHub.Controls;

/// <summary>
/// Makes the closed box of a <see cref="ComboBox"/> honour <see cref="ItemsControl.DisplayMemberPath"/>.
/// </summary>
/// <remarks>
/// <para>
/// The shell draws its own ComboBox, so the closed box is a <see cref="ContentPresenter"/> bound to
/// <c>SelectionBoxItem</c> and <c>SelectionBoxItemTemplate</c>. WPF fills that template from
/// <c>ItemTemplate</c> but <b>not</b> from <c>DisplayMemberPath</c> - verified in
/// <c>ComboBoxSelectionBoxTests</c> - so a picker of objects opened with proper labels and then
/// closed showing <c>ToString()</c>: the operator saw
/// <c>PowerTestSummary { FolderName = Rushton, Na…</c> where the assay name belonged, and
/// <c>EnumChoice {…}</c> where the impeller type belonged.
/// </para>
/// <para>
/// Attached as the presenter's <c>ContentTemplateSelector</c>, which WPF consults only when
/// <c>ContentTemplate</c> is null - so an explicit <c>ItemTemplate</c> still wins, and this only
/// fills the gap <c>DisplayMemberPath</c> leaves.
/// </para>
/// </remarks>
public sealed class SelectionBoxTemplateSelector : DataTemplateSelector
{
    private static readonly Dictionary<string, DataTemplate> Cache = new(StringComparer.Ordinal);

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
    {
        if (item is null || FindComboBox(container) is not { } combo)
        {
            return null;
        }

        var path = combo.DisplayMemberPath;
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var cached))
            {
                return cached;
            }

            var factory = new FrameworkElementFactory(typeof(TextBlock));
            factory.SetBinding(TextBlock.TextProperty, new Binding(path));
            factory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            factory.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);

            var template = new DataTemplate { VisualTree = factory };
            template.Seal();

            Cache[path] = template;
            return template;
        }
    }

    private static ComboBox? FindComboBox(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is ComboBox combo)
            {
                return combo;
            }

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return null;
    }
}
