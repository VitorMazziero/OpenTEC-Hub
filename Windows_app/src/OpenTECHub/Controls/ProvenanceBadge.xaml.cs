using System.Windows;
using System.Windows.Controls;

namespace OpenTECHub.Controls;

/// <summary>
/// Marks a displayed figure that is <b>not a measurement</b>.
/// </summary>
/// <remarks>
/// <para>
/// Named for what it does rather than for one of its values: <c>comandado</c> is the
/// common case, but <c>calculado</c> (derived volume) and <c>estimado</c> (the Phase 2
/// OUR soft sensor) are the same idea and must look the same.
/// </para>
/// <para>
/// <b>Existing as one component is the point.</b> Agitation, nutrient and the flask
/// agitator have no feedback path on the wire at all - the device reports no RPM key,
/// and there is no nutrient telemetry key whatsoever. Every surface that shows those
/// figures has to say so: tile, rail row, synoptic label, detail pane, chart legend.
/// Spread across five hand-written call sites, one of them eventually forgets, and an
/// operator infers a measurement nobody took. See <c>docs/UI_DESIGN.md</c> section 2.
/// </para>
/// </remarks>
public partial class ProvenanceBadge : UserControl
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(ProvenanceBadge),
            new PropertyMetadata("comandado"));

    public static readonly DependencyProperty ExplanationProperty =
        DependencyProperty.Register(
            nameof(Explanation), typeof(string), typeof(ProvenanceBadge),
            new PropertyMetadata(
                "Valor comandado — o equipamento não informa leitura desta variável."));

    public ProvenanceBadge() => InitializeComponent();

    /// <summary>The badge word: <c>comandado</c>, <c>calculado</c>, <c>estimado</c>.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Tooltip saying why the figure is not a reading.</summary>
    public string Explanation
    {
        get => (string)GetValue(ExplanationProperty);
        set => SetValue(ExplanationProperty, value);
    }
}
