using System.Security.Cryptography;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTECHub.Services.KlaTesting;

namespace OpenTECHub.Services.Recipes;

/// <summary>Separate strict execution contract; legacy recipe documents remain on their v1 schema.</summary>
public static class RecipeContractSerializer
{
    public const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    private sealed record Envelope<T>(int SchemaVersion, string Kind, T Payload);

    public static string Serialize(KlaRecipeRequest value) => Write(value, "klaRecipeRequest", value.Validate);
    public static string Serialize(KlaRecipeResult value) => Write(value, "klaRecipeResult", value.Validate);
    public static string Serialize(LinearSetpointRampDefinition value) => Write(value, "linearSetpointRamp", value.Validate);
    public static KlaRecipeRequest ReadKlaRequest(string json)
        => Read<KlaRecipeRequest>(json, "klaRecipeRequest", v => v.Validate());
    public static KlaRecipeResult ReadKlaResult(string json)
        => Read<KlaRecipeResult>(json, "klaRecipeResult", v => v.Validate());
    public static LinearSetpointRampDefinition ReadRamp(string json)
        => Read<LinearSetpointRampDefinition>(json, "linearSetpointRamp", v => v.Validate());

    /// <summary>Detaches legacy nested settings from the editor before enqueueing an immutable request.</summary>
    public static KlaRecipeRequest Snapshot(KlaRecipeRequest request) => ReadKlaRequest(Serialize(request));

    public static string Fingerprint(KlaReturnSnapshot snapshot)
    {
        snapshot.Validate();
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot, Options))));
    }

    /// <summary>Payload fingerprint for conflict detection, independent of JSON property order.</summary>
    public static string Fingerprint(KlaRecipeRequest request)
    {
        using var document = JsonDocument.Parse(Serialize(request));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, document.RootElement);
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static string Write<T>(T value, string kind, Action validate)
    {
        ArgumentNullException.ThrowIfNull(value);
        validate();
        return JsonSerializer.Serialize(new Envelope<T>(CurrentVersion, kind, value), Options);
    }

    private static T Read<T>(string json, string kind, Action<T> validate)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            RejectDuplicateProperties(document.RootElement);
            var envelope = JsonSerializer.Deserialize<Envelope<T>>(json, Options)
                ?? throw new RecipeFormatException("Contrato vazio.");
            if (envelope.SchemaVersion != CurrentVersion || envelope.Kind != kind || envelope.Payload is null)
            {
                throw new RecipeFormatException("Versão, tipo ou payload de contrato desconhecido.");
            }

            validate(envelope.Payload);
            return envelope.Payload;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new RecipeFormatException($"Contrato inválido: {ex.Message}");
        }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in element.EnumerateObject())
            {
                if (!seen.Add(p.Name))
                {
                    throw new RecipeFormatException("Propriedade JSON duplicada.");
                }

                RejectDuplicateProperties(p.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var p in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(p.Name); WriteCanonical(writer, p.Value);
            }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray())
            {
                WriteCanonical(writer, item);
            }

            writer.WriteEndArray();
        }
        else
        {
            element.WriteTo(writer);
        }
    }
}
