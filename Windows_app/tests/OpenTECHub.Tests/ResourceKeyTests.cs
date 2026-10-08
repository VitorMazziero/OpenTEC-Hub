using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// Every <c>{DynamicResource}</c> and <c>{StaticResource}</c> key referenced by the app
/// must actually be defined somewhere.
/// </summary>
/// <remarks>
/// <para>
/// <b>A missing resource key is silent.</b> The XAML compiler catches a bad
/// <c>TargetName</c> but not a misspelt <c>DynamicResource</c>: WPF resolves it to
/// nothing at runtime and the element renders with an unset brush - transparent text, an
/// invisible border, a control that is simply not there. Nobody sees it until the page
/// that uses it is opened, which for a template built ahead of its page can be weeks.
/// </para>
/// <para>
/// WP2 added five control templates for pages that do not exist yet (SegmentedControl,
/// Expander, ListView), so nothing exercises them on screen. This test is what stands in
/// for that until WP5 and WP6 arrive.
/// </para>
/// </remarks>
public sealed class ResourceKeyTests
{
    private static readonly Regex DefinedKey =
        new(@"x:Key=""([^""]+)""", RegexOptions.Compiled);

    // Deliberately does not match {StaticResource {x:Type TextBox}} - the nested brace
    // means that is a type key, resolved by the framework rather than by our tokens.
    private static readonly Regex ReferencedKey =
        new(@"\{(?:Dynamic|Static)Resource\s+([A-Za-z0-9_.]+)\s*\}", RegexOptions.Compiled);

    private static string Root => TestPaths.RepositoryRoot;

    private static IEnumerable<string> XamlFiles()
    {
        var app = Path.Combine(Root, "src", "OpenTECHub");
        return Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }

    [Fact]
    public void Every_referenced_resource_key_is_defined()
    {
        var files = XamlFiles().ToList();
        Assert.NotEmpty(files);

        var defined = new HashSet<string>(StringComparer.Ordinal);
        var contents = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            contents[file] = text;

            foreach (Match m in DefinedKey.Matches(text))
            {
                defined.Add(m.Groups[1].Value);
            }
        }

        var missing = new List<string>();

        foreach (var (file, text) in contents)
        {
            foreach (Match m in ReferencedKey.Matches(text))
            {
                var key = m.Groups[1].Value;
                if (!defined.Contains(key))
                {
                    missing.Add($"{Path.GetFileName(file)}: {{...Resource {key}}}");
                }
            }
        }

        Assert.True(
            missing.Count == 0,
            "Resource keys referenced but never defined:" + Environment.NewLine +
            string.Join(Environment.NewLine, missing.Distinct().Order()));
    }

    [Theory]
    // The templates WP2 added for pages that do not exist yet. If one of these keys is
    // renamed or dropped, the page that finally needs it fails at the worst moment.
    [InlineData("ToggleSwitchStyle")]
    [InlineData("SegmentedControlStyle")]
    [InlineData("SegmentedItemStyle")]
    [InlineData("ScrollThumbStyle")]
    [InlineData("ScrollPageButtonStyle")]
    [InlineData("VerticalScrollBarTemplate")]
    [InlineData("HorizontalScrollBarTemplate")]
    // Typography and geometry the whole app leans on.
    [InlineData("ReadoutTextStyle")]
    [InlineData("NumericTextStyle")]
    [InlineData("CaptionTextStyle")]
    [InlineData("AccentButtonStyle")]
    [InlineData("RadiusSmall")]
    [InlineData("RadiusMedium")]
    [InlineData("FontMono")]
    public void Named_style_survives(string key)
    {
        var found = XamlFiles()
            .Select(File.ReadAllText)
            .Any(t => t.Contains($"x:Key=\"{key}\"", StringComparison.Ordinal));

        Assert.True(found, $"{key} is no longer defined in any XAML under src/OpenTECHub.");
    }

    [Fact]
    public void Views_carry_no_colour_literals()
    {
        // Colour lives in the token dictionaries so a theme switch repaints everything.
        // A literal in a View is invisible in the light theme and wrong in the dark one.
        var literal = new Regex(@"(?:Background|Foreground|Fill|Stroke|BorderBrush|Color)\s*=\s*""#",
            RegexOptions.Compiled);

        var offenders = XamlFiles()
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Themes{Path.DirectorySeparatorChar}"))
            .Where(f => literal.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Colour literals found outside Themes/: " + string.Join(", ", offenders));
    }
}

/// <summary>Locates the repository root from the test binaries.</summary>
internal static class TestPaths
{
    public static string RepositoryRoot { get; } = Find();

    /// <summary>
    /// Where rendering tests write their images. Versioned evidence is refreshed only with
    /// <c>OPENTEC_UPDATE_EVIDENCE=1</c>; otherwise a temporary mirror keeps the working tree clean (A-07).
    /// </summary>
    public static string EvidenceRoot { get; } =
        Environment.GetEnvironmentVariable("OPENTEC_UPDATE_EVIDENCE") == "1"
            ? RepositoryRoot
            : Path.Combine(Path.GetTempPath(), "OpenTECHub-test-evidence");

    private static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "OpenTECHub.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException(
                "Could not find OpenTECHub.slnx above the test binaries.");
    }
}
