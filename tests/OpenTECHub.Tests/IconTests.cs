using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// The icon geometries parse, are drawable, and cover every name the app asks for.
/// </summary>
/// <remarks>
/// <para>
/// <b>A bad icon is silent.</b> <c>Icon.Key</c> resolves a name against application
/// resources and leaves the glyph blank when the lookup misses, because a typo in a
/// glyph name should not take down a window. That is the right runtime behaviour and
/// exactly why it needs a test: a missing icon is an empty space nobody notices in a
/// screenshot review.
/// </para>
/// <para>
/// Path data is also parsed here rather than trusted. Malformed path mini-language
/// throws at <i>load</i> time in the real app - a single stray token in a geometry would
/// take out the whole merged dictionary, and with it every icon in the application.
/// </para>
/// </remarks>
public sealed class IconTests
{
    private static string IconsFile =>
        Path.Combine(TestPaths.RepositoryRoot, "src", "OpenTECHub", "Resources", "Icons", "Icons.xaml");

    private static ResourceDictionary LoadIcons()
    {
        using var stream = File.OpenRead(IconsFile);
        return (ResourceDictionary)XamlReader.Load(stream);
    }

    [Fact]
    public void Every_icon_parses_into_a_drawable_geometry()
    {
        var icons = LoadIcons();
        Assert.NotEmpty(icons.Keys);

        foreach (var key in icons.Keys)
        {
            var geometry = Assert.IsAssignableFrom<Geometry>(icons[key]);

            // An empty geometry parses but draws nothing, which looks identical to a
            // missing icon.
            Assert.False(geometry.IsEmpty(), $"{key} parsed but is empty");

            var bounds = geometry.Bounds;
            Assert.True(bounds.Width > 0 && bounds.Height > 0, $"{key} has no extent");
        }
    }

    [Fact]
    public void Every_icon_fits_the_24_unit_design_space()
    {
        // Icon scales by Size/24 and does not stretch, so a geometry drawn outside the
        // 24 x 24 box is clipped by its layout slot rather than shrunk to fit. Half a
        // unit of tolerance for stroke overhang on the outermost points.
        const double Design = 24.0;
        const double Tolerance = 0.5;

        var icons = LoadIcons();
        var offenders = new List<string>();

        foreach (var key in icons.Keys)
        {
            var bounds = ((Geometry)icons[key]).Bounds;

            if (bounds.Left < -Tolerance || bounds.Top < -Tolerance ||
                bounds.Right > Design + Tolerance || bounds.Bottom > Design + Tolerance)
            {
                offenders.Add($"{key} {bounds}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Geometries outside the 24x24 design space: " + string.Join("; ", offenders));
    }

    [Fact]
    public void Icons_are_keyed_by_the_convention_Icon_resolves()
    {
        // Icon.Key looks up "Icon{Key}Geometry". A resource named any other way is
        // unreachable through the string form, which is the form the nav rail uses.
        var icons = LoadIcons();

        foreach (var key in icons.Keys.Cast<object>().Select(k => k.ToString()!))
        {
            Assert.StartsWith("Icon", key, StringComparison.Ordinal);
            Assert.EndsWith("Geometry", key, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_navigation_glyph_resolves_to_an_icon()
    {
        var icons = LoadIcons().Keys.Cast<object>()
            .Select(k => k.ToString()!)
            .ToHashSet(StringComparer.Ordinal);

        var shell = File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot, "src", "OpenTECHub", "ViewModels", "ShellViewModel.cs"));

        var glyphs = Regex.Matches(shell, @"new NavigationItem\(""[^""]*"",\s*""[^""]*"",\s*""([^""]*)""(?:,\s*""[^""]*"")*\)")
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.NotEmpty(glyphs);

        foreach (var glyph in glyphs)
        {
            Assert.False(string.IsNullOrEmpty(glyph), "A navigation item has no glyph.");

            // Guards against the previous state of this field, which held Segoe MDL2
            // codepoints in the private use area with no icon font to render them.
            Assert.True(
                glyph.All(c => c < 0xE000),
                $"Glyph '{Convert.ToHexString(System.Text.Encoding.Unicode.GetBytes(glyph))}' " +
                "looks like an icon-font codepoint, not an icon name.");

            Assert.True(
                icons.Contains($"Icon{glyph}Geometry"),
                $"Navigation glyph '{glyph}' has no Icon{glyph}Geometry in Icons.xaml.");
        }
    }

    [Theory]
    // The inventory promised by UI_DESIGN section 3.9. Phases 2-3 bind these as their
    // subsystems arrive; the geometry is drawn once, here, so those phases do not each
    // invent their own thermometer.
    [InlineData("Vessel")]
    [InlineData("Sliders")]
    [InlineData("NodeGraph")]
    [InlineData("Bell")]
    [InlineData("Trend")]
    [InlineData("EventLog")]
    [InlineData("Target")]
    [InlineData("Gear")]
    [InlineData("Temperature")]
    [InlineData("Ph")]
    [InlineData("Oxygen")]
    [InlineData("Impeller")]
    [InlineData("Airflow")]
    [InlineData("Pressure")]
    [InlineData("Level")]
    [InlineData("Foam")]
    [InlineData("Nutrient")]
    [InlineData("Pump")]
    [InlineData("Biomass")]
    [InlineData("Valve")]
    [InlineData("Cascade")]
    [InlineData("Export")]
    [InlineData("Fullscreen")]
    [InlineData("Refresh")]
    public void Documented_icon_exists(string name)
    {
        var icons = LoadIcons();
        Assert.True(icons.Contains($"Icon{name}Geometry"), $"Icon{name}Geometry is missing.");
    }
}
