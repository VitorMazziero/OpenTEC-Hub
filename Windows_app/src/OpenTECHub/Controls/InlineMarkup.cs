using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace OpenTECHub.Controls;

/// <summary>
/// Renders <c>**bold**</c> spans inside a <see cref="TextBlock"/>.
/// </summary>
/// <remarks>
/// <para>
/// The documentation is written as plain strings in a catalog, and prose that runs for a screen
/// needs a way to put weight on the two or three phrases that carry the consequence — "running a
/// recipe <b>deactivates manual control</b>". Binding that string to <c>Text</c> prints the
/// asterisks, which is worse than no emphasis at all.
/// </para>
/// <para>
/// Deliberately only this one marker. A full Markdown renderer would invite tables, links and
/// headings into text whose layout is already decided by the block kind, and every one of those
/// would be a second way to express something the catalog already expresses structurally.
/// </para>
/// </remarks>
public static class InlineMarkup
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text",
        typeof(string),
        typeof(InlineMarkup),
        new PropertyMetadata(null, OnTextChanged));

    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
        {
            return;
        }

        block.Inlines.Clear();
        var text = e.NewValue as string;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // Split on the marker: even segments are plain, odd segments are bold. An unclosed
        // marker leaves its tail plain rather than swallowing the rest of the paragraph.
        var segments = text.Split("**");
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length == 0)
            {
                continue;
            }

            // An odd segment is bold only when a closing marker followed it; a trailing odd
            // segment means the marker was never closed, and unclosed emphasis emphasises nothing.
            var isBold = i % 2 == 1 && i < segments.Length - 1;
            block.Inlines.Add(new Run(segments[i])
            {
                FontWeight = isBold ? FontWeights.SemiBold : FontWeights.Normal,
            });
        }
    }
}
