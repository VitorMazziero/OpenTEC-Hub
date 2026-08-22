using System.Text.Json.Nodes;

namespace TecnalHub.Services.Recipes;

/// <summary>
/// One node instance in a recipe graph.
/// </summary>
/// <remarks>
/// The node is a thin instance over its catalog <see cref="RecipeNodeDefinition"/>: its
/// identity, canvas position and the parameter <b>values</b>, held in a <see cref="JsonObject"/>
/// keyed by the definition's parameter keys. Keeping values in a JSON object (rather than a
/// hand-written class per type) is what lets one declaration in <see cref="RecipeNodeCatalog"/>
/// drive editing, validation and the JSON panel alike. Execution state is <b>not</b> stored here
/// — it belongs to the engine and the view models, so the document stays a pure serialisable model.
/// </remarks>
public sealed class RecipeNode
{
    /// <summary>Stable unique id, referenced by connections.</summary>
    public required string Id { get; set; }

    /// <summary>The block type; its definition lives in <see cref="RecipeNodeCatalog"/>.</summary>
    public required NodeType Type { get; init; }

    /// <summary>Canvas X position.</summary>
    public double X { get; set; }

    /// <summary>Canvas Y position.</summary>
    public double Y { get; set; }

    /// <summary>Parameter values, keyed by the definition's parameter keys.</summary>
    public JsonObject Parameters { get; set; } = new();

    /// <summary>Free-text notes, shown as a tooltip on the block.</summary>
    public string? Notes { get; set; }

    /// <summary>Optional "executar apenas se…" guard expression.</summary>
    public string? Condition { get; set; }

    /// <summary>The catalog definition for this node.</summary>
    public RecipeNodeDefinition Definition => RecipeNodeCatalog.Definition(Type);

    /// <summary>
    /// Creates a node initialised with its definition's default parameter values.
    /// </summary>
    public static RecipeNode Create(NodeType type, double x = 0, double y = 0, string? id = null)
    {
        var node = new RecipeNode
        {
            Id = id ?? Guid.NewGuid().ToString("n"),
            Type = type,
            X = x,
            Y = y,
        };

        foreach (var parameter in RecipeNodeCatalog.Definition(type).Parameters)
        {
            node.Parameters[parameter.Key] = DefaultValue(parameter);
        }

        return node;
    }

    /// <summary>The default JSON value for a parameter, from its schema.</summary>
    internal static JsonNode DefaultValue(RecipeParameter parameter) => parameter.Kind switch
    {
        ParameterKind.Number or ParameterKind.Integer => JsonValue.Create(Convert.ToDouble(parameter.Default ?? 0.0)),
        ParameterKind.Bool => JsonValue.Create(parameter.Default is true),
        ParameterKind.List => new JsonArray(),
        _ => JsonValue.Create(parameter.Default?.ToString() ?? ""),
    };

    // ── Typed value access ─────────────────────────────────────────────────────

    /// <summary>Reads a numeric parameter, falling back to its schema default.</summary>
    public double Number(string key)
    {
        if (Parameters[key] is JsonValue value && TryReadNumber(value, out var number))
        {
            return number;
        }

        return Convert.ToDouble(Definition.Parameter(key)?.Default ?? 0.0);
    }

    /// <summary>
    /// Reads a number from a <see cref="JsonValue"/> regardless of its backing type.
    /// </summary>
    /// <remarks>
    /// A value parsed from JSON is <see cref="System.Text.Json.JsonElement"/>-backed and converts
    /// between numeric types freely, but one created from a CLR <c>int</c> (e.g. via the editor's
    /// implicit <c>JsonNode</c> conversion) is a <c>JsonValue&lt;int&gt;</c> whose
    /// <c>TryGetValue&lt;double&gt;</c> fails — so we probe each numeric backing in turn.
    /// </remarks>
    internal static bool TryReadNumber(JsonValue value, out double number)
    {
        if (value.TryGetValue(out double d)) { number = d; return true; }
        if (value.TryGetValue(out int i)) { number = i; return true; }
        if (value.TryGetValue(out long l)) { number = l; return true; }
        if (value.TryGetValue(out float f)) { number = f; return true; }
        if (value.TryGetValue(out decimal m)) { number = (double)m; return true; }
        number = 0;
        return false;
    }

    /// <summary>Reads a boolean parameter.</summary>
    public bool Flag(string key)
        => Parameters[key] is JsonValue value && value.TryGetValue(out bool b) ? b : Definition.Parameter(key)?.Default is true;

    /// <summary>Reads a text or enum-value parameter.</summary>
    public string Text(string key)
    {
        if (Parameters[key] is JsonValue value && value.TryGetValue(out string? s) && s is not null)
        {
            return s;
        }

        return Definition.Parameter(key)?.Default?.ToString() ?? "";
    }

    /// <summary>Reads an enum parameter, parsing it to <typeparamref name="TEnum"/>.</summary>
    public TEnum Enum<TEnum>(string key) where TEnum : struct, Enum
        => System.Enum.TryParse<TEnum>(Text(key), out var parsed) ? parsed : default;

    /// <summary>The rows of a list parameter, or an empty array.</summary>
    public JsonArray Rows(string key) => Parameters[key] as JsonArray ?? [];

    /// <summary>Sets a value, replacing any existing one.</summary>
    public void Set(string key, JsonNode? value) => Parameters[key] = value;
}

/// <summary>
/// A directed connection between two node ports.
/// </summary>
/// <param name="SourceNodeId">Origin node id.</param>
/// <param name="SourceConnector">Origin port name (canonical spelling once written).</param>
/// <param name="TargetNodeId">Destination node id.</param>
/// <param name="TargetConnector">Destination port name.</param>
public sealed record RecipeConnection(
    string SourceNodeId,
    string SourceConnector,
    string TargetNodeId,
    string TargetConnector);

/// <summary>
/// A complete recipe: metadata, nodes and connections.
/// </summary>
/// <remarks>
/// The <see cref="SchemaVersion"/> is stamped from v1 with a migration hook in
/// <see cref="RecipeSerializer"/> — ReceitasTECNAL learned versioning late and now converts at
/// load time with warnings; TECNAL-Hub versions from the first release.
/// </remarks>
public sealed class RecipeDocument
{
    /// <summary>Recipe name, shown on the tab and in the library.</summary>
    public string Name { get; set; } = "Nova Receita";

    /// <summary>Free-text description.</summary>
    public string Description { get; set; } = "";

    /// <summary>Recipe author.</summary>
    public string Author { get; set; } = "";

    /// <summary>Creation timestamp (UTC).</summary>
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Last-modified timestamp (UTC).</summary>
    public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The nodes, in no particular order.</summary>
    public List<RecipeNode> Nodes { get; init; } = [];

    /// <summary>The connections between node ports.</summary>
    public List<RecipeConnection> Connections { get; init; } = [];

    /// <summary>The node with the given id, or null.</summary>
    public RecipeNode? Node(string id) => Nodes.FirstOrDefault(n => n.Id == id);

    /// <summary>The single <see cref="NodeType.Start"/> node, or null.</summary>
    public RecipeNode? Start => Nodes.FirstOrDefault(n => n.Type == NodeType.Start);

    /// <summary>
    /// Outgoing connections from a node's normal flow (loop-out edges excluded by default).
    /// </summary>
    public IEnumerable<RecipeConnection> OutgoingFrom(string nodeId, bool includeLoop = false)
        => Connections.Where(c => c.SourceNodeId == nodeId
            && (includeLoop || !ConnectorNames.IsLoopOut(c.SourceConnector)));

    /// <summary>Connections into a node.</summary>
    public IEnumerable<RecipeConnection> IncomingTo(string nodeId)
        => Connections.Where(c => c.TargetNodeId == nodeId);
}
