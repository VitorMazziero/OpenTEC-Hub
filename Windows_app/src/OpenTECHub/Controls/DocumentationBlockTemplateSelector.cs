using System.Windows;
using System.Windows.Controls;
using OpenTECHub.Services.Documentation;

namespace OpenTECHub.Controls;

/// <summary>
/// Picks the template for one documentation block from its <see cref="DocumentationBlockKind"/>.
/// </summary>
/// <remarks>
/// The catalog decides what is prose, what is a list item, what is a named control and what is a
/// caution; the view only knows how to draw those four. Keeping the choice here — rather than in
/// four parallel <c>ItemsControl</c>s filtered by kind — is what lets a page be documented by
/// writing records, with the reading order preserved exactly as written.
/// </remarks>
public sealed class DocumentationBlockTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ParagraphTemplate { get; set; }

    public DataTemplate? BulletTemplate { get; set; }

    public DataTemplate? FieldTemplate { get; set; }

    public DataTemplate? NoteTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container) =>
        item is DocumentationBlock block
            ? block.Kind switch
            {
                DocumentationBlockKind.Bullet => BulletTemplate,
                DocumentationBlockKind.Field => FieldTemplate,
                DocumentationBlockKind.Note => NoteTemplate,
                _ => ParagraphTemplate,
            }
            : base.SelectTemplate(item, container);
}
