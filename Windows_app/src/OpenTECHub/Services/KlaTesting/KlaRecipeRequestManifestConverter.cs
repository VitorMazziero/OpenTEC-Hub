using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTECHub.Services.Recipes;

namespace OpenTECHub.Services.KlaTesting;

/// <summary>The legacy manifest omits nulls; frozen execution contracts must preserve explicit nulls.</summary>
public sealed class KlaRecipeRequestManifestConverter : JsonConverter<KlaRecipeRequest>
{
    public override KlaRecipeRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return RecipeContractSerializer.ReadKlaRequest("{\"schemaVersion\":1,\"kind\":\"klaRecipeRequest\",\"payload\":" +
            document.RootElement.GetRawText() + "}");
    }

    public override void Write(Utf8JsonWriter writer, KlaRecipeRequest value, JsonSerializerOptions options)
    {
        using var document = JsonDocument.Parse(RecipeContractSerializer.Serialize(value));
        document.RootElement.GetProperty("payload").WriteTo(writer);
    }
}
