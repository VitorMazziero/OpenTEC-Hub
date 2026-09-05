using System.IO;
using System.Text;

namespace OpenTECHub.Services.Recipes;

/// <summary>A saved recipe, summarised for the Minhas Receitas library list.</summary>
/// <param name="FileName">The file the recipe lives in, the handle for load/save/delete.</param>
/// <param name="Name">Recipe name.</param>
/// <param name="Description">Recipe description.</param>
/// <param name="BlockCount">Number of blocks in the graph.</param>
/// <param name="ModifiedUtc">Last write time.</param>
public sealed record RecipeSummary(string FileName, string Name, string Description, int BlockCount, DateTimeOffset ModifiedUtc);

/// <summary>Reads and writes recipes to a folder on disk — the Minhas Receitas library.</summary>
public interface IRecipeStore
{
    /// <summary>The folder recipes live in.</summary>
    string Directory { get; }

    /// <summary>The saved recipes, most-recently-modified first.</summary>
    IReadOnlyList<RecipeSummary> List();

    /// <summary>Loads a recipe by its file name.</summary>
    RecipeDocument Load(string fileName);

    /// <summary>
    /// Writes a recipe, overwriting <paramref name="fileName"/> when given, or creating a new file
    /// from the recipe name. Returns the file name written.
    /// </summary>
    string Save(RecipeDocument recipe, string? fileName = null);

    /// <summary>Deletes a saved recipe.</summary>
    void Delete(string fileName);
}

/// <inheritdoc cref="IRecipeStore"/>
public sealed class RecipeStore(string directory) : IRecipeStore
{
    private const string Extension = ".recipe.json";

    public string Directory { get; } = directory;

    public IReadOnlyList<RecipeSummary> List()
    {
        System.IO.Directory.CreateDirectory(Directory);

        var summaries = new List<RecipeSummary>();
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*" + Extension))
        {
            try
            {
                var recipe = RecipeSerializer.Deserialize(File.ReadAllText(path));
                summaries.Add(new RecipeSummary(
                    Path.GetFileName(path), recipe.Name, recipe.Description, recipe.Nodes.Count,
                    File.GetLastWriteTimeUtc(path)));
            }
            catch (Exception ex) when (ex is RecipeFormatException or IOException)
            {
                // A corrupt or unreadable file is skipped rather than failing the whole listing.
            }
        }

        return [.. summaries.OrderByDescending(s => s.ModifiedUtc)];
    }

    public RecipeDocument Load(string fileName)
    {
        var path = Path.Combine(Directory, SafeName(fileName));
        return RecipeSerializer.Deserialize(File.ReadAllText(path));
    }

    public string Save(RecipeDocument recipe, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        System.IO.Directory.CreateDirectory(Directory);

        recipe.ModifiedUtc = DateTimeOffset.UtcNow;
        fileName = string.IsNullOrWhiteSpace(fileName) ? UniqueFileNameFor(recipe.Name) : SafeName(fileName);
        File.WriteAllText(Path.Combine(Directory, fileName), RecipeSerializer.Serialize(recipe));
        return fileName;
    }

    public void Delete(string fileName)
    {
        var path = Path.Combine(Directory, SafeName(fileName));
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>A collision-free file name for a new recipe, from its name.</summary>
    private string UniqueFileNameFor(string recipeName)
    {
        var slug = Slug(recipeName);
        var candidate = slug + Extension;
        var counter = 2;
        while (File.Exists(Path.Combine(Directory, candidate)))
        {
            candidate = $"{slug}-{counter++}{Extension}";
        }

        return candidate;
    }

    /// <summary>A filesystem-safe slug from a recipe name.</summary>
    private static string Slug(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            builder.Append(char.IsLetterOrDigit(c) ? c : '-');
        }

        var slug = builder.ToString().Trim('-');
        while (slug.Contains("--"))
        {
            slug = slug.Replace("--", "-");
        }

        return slug.Length == 0 ? "receita" : slug;
    }

    /// <summary>Guards against a file name escaping the recipes folder.</summary>
    private static string SafeName(string fileName) => Path.GetFileName(fileName);
}
