namespace TecnalHub.Services.Recipes;

/// <summary>The editing control a parameter needs.</summary>
public enum ParameterKind
{
    /// <summary>Real-valued numeric entry.</summary>
    Number,

    /// <summary>Integer numeric entry.</summary>
    Integer,

    /// <summary>Free text.</summary>
    Text,

    /// <summary>One choice from <see cref="RecipeParameter.Options"/>.</summary>
    Enum,

    /// <summary>Boolean toggle.</summary>
    Bool,

    /// <summary>A repeating list of rows, each shaped by <see cref="RecipeParameter.ItemSchema"/>.</summary>
    List,
}

/// <summary>One selectable option for an <see cref="ParameterKind.Enum"/> parameter.</summary>
/// <param name="Value">Stable value written to JSON.</param>
/// <param name="Label">pt-BR text shown to the operator.</param>
/// <remarks>
/// <see cref="ToString"/> returns the <see cref="Label"/> so a <c>ComboBox</c> that falls back to
/// the item's string form (rather than honouring <c>DisplayMemberPath</c>) still shows the pt-BR
/// text, never the record's default <c>"RecipeOption { … }"</c> rendering.
/// </remarks>
public sealed record RecipeOption(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// One parameter of a block, declared once in <see cref="RecipeNodeCatalog"/>.
/// </summary>
/// <remarks>
/// This is the "declared once and generated" schema that replaces ReceitasTECNAL's
/// hand-written model + viewmodel + view triple per node type. The validator reads ranges
/// from here, the editor generates its fields from here, and node instances initialise their
/// values from <see cref="Default"/> here — one declaration, three consumers.
/// </remarks>
public sealed record RecipeParameter
{
    /// <summary>The JSON key the value is stored under on the node.</summary>
    public required string Key { get; init; }

    /// <summary>pt-BR label shown above the field.</summary>
    public required string Label { get; init; }

    /// <summary>The editing control.</summary>
    public required ParameterKind Kind { get; init; }

    /// <summary>Optional group heading, for the grouped cascade pane (§5.3.7).</summary>
    public string? Group { get; init; }

    /// <summary>Engineering unit shown beside the field, when any.</summary>
    public string? Unit { get; init; }

    /// <summary>Inclusive lower bound for numeric kinds; <see cref="double.NegativeInfinity"/> if none.</summary>
    public double Min { get; init; } = double.NegativeInfinity;

    /// <summary>Inclusive upper bound for numeric kinds; <see cref="double.PositiveInfinity"/> if none.</summary>
    public double Max { get; init; } = double.PositiveInfinity;

    /// <summary>Default value: a <see cref="double"/>, <see cref="bool"/>, or option value string.</summary>
    public object? Default { get; init; }

    /// <summary>The options for an <see cref="ParameterKind.Enum"/> parameter.</summary>
    public IReadOnlyList<RecipeOption> Options { get; init; } = [];

    /// <summary>The per-row schema for a <see cref="ParameterKind.List"/> parameter.</summary>
    public IReadOnlyList<RecipeParameter> ItemSchema { get; init; } = [];

    /// <summary>
    /// Optional visibility guard, <c>"key=value"</c>: the field shows only when the sibling
    /// parameter <c>key</c> holds option <c>value</c> (e.g. histerese only for pH).
    /// </summary>
    public string? VisibleWhen { get; init; }
}

/// <summary>
/// The complete, immutable declaration of one block type: its identity, category, ports and
/// parameter schema.
/// </summary>
public sealed record RecipeNodeDefinition
{
    /// <summary>The block type.</summary>
    public required NodeType Type { get; init; }

    /// <summary>pt-BR block title, shown in the library and on the block header.</summary>
    public required string Title { get; init; }

    /// <summary>The category, which fixes the header colour.</summary>
    public required BlockCategory Category { get; init; }

    /// <summary>The block's ports, in display order.</summary>
    public required IReadOnlyList<RecipePort> Ports { get; init; }

    /// <summary>The block's parameters, in display order. Empty for structural blocks.</summary>
    public IReadOnlyList<RecipeParameter> Parameters { get; init; } = [];

    /// <summary>The inbound flow port, if the block has one.</summary>
    public RecipePort? InputPort => Ports.FirstOrDefault(p => p is { Direction: PortDirection.In, Name: ConnectorNames.In });

    /// <summary>The block's parameter with the given key, or null.</summary>
    public RecipeParameter? Parameter(string key) => Parameters.FirstOrDefault(p => p.Key == key);
}
