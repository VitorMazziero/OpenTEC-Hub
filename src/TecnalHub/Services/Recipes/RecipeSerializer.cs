using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TecnalHub.Services.Recipes;

/// <summary>Raised when recipe JSON cannot be read as a recipe.</summary>
public sealed class RecipeFormatException(string message) : Exception(message);

/// <summary>
/// Reads and writes recipe JSON, versioned from v1 with a migration hook.
/// </summary>
/// <remarks>
/// <para>
/// The canonical wire is <c>schemaVersion 1</c>. Older documents pass through
/// <see cref="Migrate"/> before they are read, so a future v2 format can upgrade v1 at load time
/// the way ReceitasTECNAL eventually had to — except here the hook exists from the first release
/// rather than being retrofitted.
/// </para>
/// <para>
/// Two tolerances keep hand-written and migrated recipes loading: node <b>type</b> names accept
/// ReceitasTECNAL's spellings (<see cref="TypeAliases"/>), and <b>connector</b> names accept the
/// accented/joined loop spellings (<see cref="ConnectorNames"/>). Both are normalised to their
/// canonical form in memory, so what is written back is always canonical.
/// </para>
/// </remarks>
public static class RecipeSerializer
{
    /// <summary>The recipe schema version this build writes.</summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>ReceitasTECNAL type names tolerated on load, mapped to TECNAL-Hub canonical types.</summary>
    private static readonly IReadOnlyDictionary<string, NodeType> TypeAliases = new Dictionary<string, NodeType>(StringComparer.OrdinalIgnoreCase)
    {
        ["WriteSetpoint"] = NodeType.SetSetpoint,
        ["MultiWriteSetpoint"] = NodeType.MultiSetpoint,
        ["ToggleControlWord"] = NodeType.SetLoop,
        ["MultiToggleControlWord"] = NodeType.MultiLoop,
        ["SetPumpMode"] = NodeType.PumpControl,
        ["ConfigurePHPump"] = NodeType.PhPump,
        ["ConfigureFoamPump"] = NodeType.AntifoamPump,
        ["ConfigureNutrientPump"] = NodeType.NutrientPump,
        ["ResetAccumulator"] = NodeType.ResetVariables,
    };

    /// <summary>Serialises a recipe to indented JSON.</summary>
    public static string Serialize(RecipeDocument recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        var nodes = new JsonArray();
        foreach (var node in recipe.Nodes)
        {
            var obj = new JsonObject
            {
                ["id"] = node.Id,
                ["type"] = node.Type.ToString(),
                ["x"] = node.X,
                ["y"] = node.Y,
                ["parameters"] = node.Parameters.DeepClone(),
            };

            if (!string.IsNullOrEmpty(node.Notes))
            {
                obj["notes"] = node.Notes;
            }

            if (!string.IsNullOrEmpty(node.Condition))
            {
                obj["condition"] = node.Condition;
            }

            nodes.Add(obj);
        }

        var connections = new JsonArray();
        foreach (var c in recipe.Connections)
        {
            connections.Add(new JsonObject
            {
                ["source"] = c.SourceNodeId,
                ["sourcePort"] = ConnectorNames.Canonical(c.SourceConnector),
                ["target"] = c.TargetNodeId,
                ["targetPort"] = ConnectorNames.Canonical(c.TargetConnector),
            });
        }

        var root = new JsonObject
        {
            ["schemaVersion"] = CurrentVersion,
            ["name"] = recipe.Name,
            ["description"] = recipe.Description,
            ["author"] = recipe.Author,
            ["createdUtc"] = recipe.CreatedUtc.ToString("O", CultureInfo.InvariantCulture),
            ["modifiedUtc"] = recipe.ModifiedUtc.ToString("O", CultureInfo.InvariantCulture),
            ["nodes"] = nodes,
            ["connections"] = connections,
        };

        return root.ToJsonString(WriteOptions);
    }

    /// <summary>Reads a recipe from JSON, migrating and normalising as needed.</summary>
    /// <exception cref="RecipeFormatException">The JSON is not a readable recipe.</exception>
    public static RecipeDocument Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new RecipeFormatException("Receita vazia.");
        }

        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject
                ?? throw new RecipeFormatException("A raiz da receita não é um objeto JSON.");
        }
        catch (JsonException ex)
        {
            throw new RecipeFormatException($"JSON inválido: {ex.Message}");
        }

        var version = root["schemaVersion"]?.GetValue<int>() ?? 1;
        if (version > CurrentVersion)
        {
            throw new RecipeFormatException(
                $"A receita foi salva por uma versão mais recente (v{version}); esta versão lê até v{CurrentVersion}.");
        }

        Migrate(root, version);

        var recipe = new RecipeDocument
        {
            Name = root["name"]?.GetValue<string>() ?? "Receita",
            Description = root["description"]?.GetValue<string>() ?? "",
            Author = root["author"]?.GetValue<string>() ?? "",
            CreatedUtc = ParseTime(root["createdUtc"]),
            ModifiedUtc = ParseTime(root["modifiedUtc"]),
        };

        if (root["nodes"] is JsonArray nodes)
        {
            foreach (var element in nodes.OfType<JsonObject>())
            {
                if (ReadNode(element) is { } node)
                {
                    recipe.Nodes.Add(node);
                }
            }
        }

        if (root["connections"] is JsonArray connections)
        {
            foreach (var element in connections.OfType<JsonObject>())
            {
                var source = element["source"]?.GetValue<string>();
                var target = element["target"]?.GetValue<string>();
                if (source is null || target is null)
                {
                    continue;
                }

                recipe.Connections.Add(new RecipeConnection(
                    source,
                    ConnectorNames.Canonical(element["sourcePort"]?.GetValue<string>() ?? ConnectorNames.Out),
                    target,
                    ConnectorNames.Canonical(element["targetPort"]?.GetValue<string>() ?? ConnectorNames.In)));
            }
        }

        return recipe;
    }

    /// <summary>
    /// The migration hook. Upgrades an older document's JSON in place to the current schema before
    /// it is read. v1 is current, so today this only guards the version field; a future v2 adds its
    /// upgrade branch here.
    /// </summary>
    private static void Migrate(JsonObject root, int fromVersion)
    {
        // No structural migrations exist yet. When schema v2 lands, transform v1 → v2 here,
        // keyed on fromVersion, then fall through. Kept as the single, discoverable seam.
        _ = root;
        _ = fromVersion;
    }

    private static RecipeNode? ReadNode(JsonObject element)
    {
        var typeName = element["type"]?.GetValue<string>();
        if (typeName is null || !TryParseType(typeName, out var type))
        {
            // An unknown block type from a newer or foreign document is dropped rather than
            // failing the whole load; the validator then reports the missing connections.
            return null;
        }

        var node = new RecipeNode
        {
            Id = element["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("n"),
            Type = type,
            X = element["x"]?.GetValue<double>() ?? 0,
            Y = element["y"]?.GetValue<double>() ?? 0,
            Notes = element["notes"]?.GetValue<string>(),
            Condition = element["condition"]?.GetValue<string>(),
        };

        // Start from schema defaults so a hand-written recipe that omits a field still runs,
        // then overlay whatever the document supplied.
        node.Parameters = new JsonObject();
        foreach (var parameter in node.Definition.Parameters)
        {
            node.Parameters[parameter.Key] = RecipeNode.DefaultValue(parameter);
        }

        if (element["parameters"] is JsonObject supplied)
        {
            foreach (var pair in supplied)
            {
                node.Parameters[pair.Key] = pair.Value?.DeepClone();
            }
        }

        return node;
    }

    private static bool TryParseType(string name, out NodeType type)
    {
        if (Enum.TryParse(name, out type) && RecipeNodeCatalog.Has(type))
        {
            return true;
        }

        return TypeAliases.TryGetValue(name, out type);
    }

    private static DateTimeOffset ParseTime(JsonNode? node)
        => node is not null
           && DateTimeOffset.TryParse(node.GetValue<string>(), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;
}
