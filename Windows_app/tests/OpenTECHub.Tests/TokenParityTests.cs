using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using OpenTECHub.Services.Persistence;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// The light and dark token dictionaries must define exactly the same key set.
/// </summary>
/// <remarks>
/// <para>
/// <b>A key present in one file and missing in the other is a runtime crash on theme
/// switch</b> - the shared dictionary resolves every brush through
/// <c>DynamicResource</c> against whichever token file is in slot 0, so a missing key
/// only fails once the operator flips the theme, which is exactly when nobody is
/// watching a debugger.
/// </para>
/// <para>
/// Both dictionaries claimed in comments to be "asserted against each other in
/// OpenTECHub.Tests" from the beginning. They were not: this file was written during the
/// Phase 1b token refit, which added roughly twenty-five keys to each and made the gap
/// worth closing.
/// </para>
/// </remarks>
public sealed class TokenParityTests
{
    /// <summary>
    /// Parses a token dictionary from its source file.
    /// </summary>
    /// <remarks>
    /// Read from disk rather than through a <c>pack://</c> URI on purpose. Resolving a
    /// pack URI needs the WebRequest factory that <see cref="Application"/> registers
    /// during its static initialisation, and no test here starts an Application - the
    /// ViewModels take interfaces and run headless, which is the property that keeps
    /// this suite fast. The token files are pure <c>Color</c> dictionaries with no
    /// code-behind and no custom types, so <see cref="XamlReader"/> parses them on any
    /// thread.
    /// </remarks>
    private static ResourceDictionary Load(string file)
    {
        var path = Path.Combine(ThemesDirectory.Value, file);
        using var stream = File.OpenRead(path);

        return (ResourceDictionary)XamlReader.Load(stream);
    }

    /// <summary>
    /// Locates <c>src/OpenTECHub/Themes</c> by walking up from the test binaries to the
    /// solution file. Fails loudly if the layout moves, which is the correct outcome -
    /// a token test that silently stops finding the tokens proves nothing.
    /// </summary>
    private static readonly Lazy<string> ThemesDirectory = new(() =>
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OpenTECHub.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "Could not find OpenTECHub.slnx above the test binaries.");

        var themes = Path.Combine(directory!.FullName, "src", "OpenTECHub", "Themes");
        Assert.True(Directory.Exists(themes), $"Themes directory not found at {themes}");

        return themes;
    });

    private static ResourceDictionary Light() => Load("Tokens.Light.xaml");

    private static ResourceDictionary Dark() => Load("Tokens.Dark.xaml");

    private static HashSet<string> KeysOf(ResourceDictionary dictionary)
        => [.. dictionary.Keys.Cast<object>().Select(k => k.ToString()!)];

    [Fact]
    public void Light_and_dark_define_the_same_keys()
    {
        var light = KeysOf(Light());
        var dark = KeysOf(Dark());

        var onlyInLight = light.Except(dark).Order().ToList();
        var onlyInDark = dark.Except(light).Order().ToList();

        Assert.True(
            onlyInLight.Count == 0 && onlyInDark.Count == 0,
            $"Token dictionaries disagree.{Environment.NewLine}" +
            $"Only in Light: {string.Join(", ", onlyInLight)}{Environment.NewLine}" +
            $"Only in Dark:  {string.Join(", ", onlyInDark)}");
    }

    [Fact]
    public void Every_token_is_a_colour()
    {
        // The token files carry colours only. Brushes, typography and geometry live in
        // Tokens.Shared.xaml, which is not swapped at runtime - putting a brush here
        // would freeze it at whichever theme was loaded first.
        foreach (var dictionary in new[] { Light(), Dark() })
        {
            foreach (var key in dictionary.Keys)
            {
                Assert.IsType<Color>(dictionary[key]);
            }
        }
    }

    [Theory]
    [InlineData("StateOk")]
    [InlineData("StateActuating")]
    [InlineData("StateWarning")]
    [InlineData("StateAlarm")]
    [InlineData("StateIdle")]
    [InlineData("StateDisabled")]
    public void Every_state_has_a_fill_and_a_text_variant(string state)
    {
        foreach (var dictionary in new[] { Light(), Dark() })
        {
            Assert.True(dictionary.Contains($"{state}Color"), $"{state}Color missing");
            Assert.True(dictionary.Contains($"{state}TextColor"), $"{state}TextColor missing");
        }
    }

    [Theory]
    [InlineData("StateOkTextColor")]
    [InlineData("StateWarningTextColor")]
    [InlineData("StateAlarmTextColor")]
    [InlineData("StateActuatingTextColor")]
    [InlineData("StateIdleTextColor")]
    public void State_text_variants_are_readable_on_the_light_card_surface(string key)
    {
        // 4.5:1 is the WCAG AA threshold for normal-size text. The vivid FILL variants
        // deliberately do not clear it - they are for 8 px dots. This asserts that the
        // text variants, which do get rendered as words, are readable.
        var light = Light();
        var surface = (Color)light["SurfaceCardColor"];
        var foreground = (Color)light[key];

        var ratio = ContrastRatio(foreground, surface);

        Assert.True(ratio >= 4.5, $"{key} is {ratio:F2}:1 on SurfaceCard; needs 4.5:1");
    }

    [Fact]
    public void State_fill_variants_are_visible_as_non_text_marks()
    {
        // 3:1 is the WCAG threshold for non-text UI components. A state dot that does
        // not clear it is a dot nobody can see.
        var light = Light();
        var surface = (Color)light["SurfaceCardColor"];

        foreach (var key in new[]
                 {
                     "StateOkColor", "StateActuatingColor", "StateWarningColor",
                     "StateAlarmColor", "StateIdleColor",
                 })
        {
            var ratio = ContrastRatio((Color)light[key], surface);
            Assert.True(ratio >= 3.0, $"{key} is {ratio:F2}:1 on SurfaceCard; needs 3:1");
        }
    }

    [Fact]
    public void Dark_state_variants_are_readable_on_the_dark_card_surface()
    {
        // In dark the fill and text variants converge, so the fills themselves must
        // clear the text threshold. This is what licenses that convergence.
        var dark = Dark();
        var surface = (Color)dark["SurfaceCardColor"];

        foreach (var key in new[]
                 {
                     "StateOkColor", "StateActuatingColor", "StateWarningColor",
                     "StateAlarmColor", "StateIdleColor",
                 })
        {
            var ratio = ContrastRatio((Color)dark[key], surface);
            Assert.True(ratio >= 4.5, $"{key} is {ratio:F2}:1 on dark SurfaceCard; needs 4.5:1");
        }
    }

    [Fact]
    public void Primary_text_is_readable_on_every_surface_in_both_themes()
    {
        foreach (var (dictionary, theme) in new[] { (Light(), "light"), (Dark(), "dark") })
        {
            var text = (Color)dictionary["TextPrimaryColor"];

            foreach (var surfaceKey in new[]
                     {
                         "SurfaceBaseColor", "SurfaceCardColor",
                         "SurfaceElevatedColor", "SurfaceSunkenColor",
                     })
            {
                var ratio = ContrastRatio(text, (Color)dictionary[surfaceKey]);
                Assert.True(
                    ratio >= 4.5,
                    $"{theme}: TextPrimary on {surfaceKey} is {ratio:F2}:1; needs 4.5:1");
            }
        }
    }

    [Fact]
    public void Text_on_accent_is_readable_against_the_accent_fill()
    {
        // Dark's accent is a light blue, so its on-accent text is near-black rather
        // than white. Getting this backwards produces an unreadable primary button.
        foreach (var (dictionary, theme) in new[] { (Light(), "light"), (Dark(), "dark") })
        {
            var ratio = ContrastRatio(
                (Color)dictionary["TextOnAccentColor"],
                (Color)dictionary["AccentColor"]);

            Assert.True(ratio >= 4.5, $"{theme}: TextOnAccent is {ratio:F2}:1; needs 4.5:1");
        }
    }

    [Fact]
    public void Every_token_brush_has_a_matching_colour()
    {
        // ThemeService repaints the app by walking the shared brushes and assigning each
        // one the colour named by stripping "Brush" and appending "Color". A brush whose
        // colour key does not exist is SKIPPED IN SILENCE: it keeps whatever colour it
        // first resolved to and simply stops following the theme, which looks like one
        // stubbornly light element on a dark page and is very hard to trace back.
        //
        // The theme switch was inert for a long time for a related reason - see
        // ThemeService.RepaintBrushes - and nothing failed. This is the guard.
        var shared = Load("Tokens.Shared.xaml");
        var light = KeysOf(Light());

        var orphans = shared.Keys.Cast<object>()
            .Select(k => k.ToString()!)
            .Where(k => k.EndsWith("Brush", StringComparison.Ordinal))
            .Where(k => shared[k] is SolidColorBrush)
            .Select(k => k[..^"Brush".Length] + "Color")
            .Where(colourKey => !light.Contains(colourKey))
            .Order()
            .ToList();

        Assert.True(
            orphans.Count == 0,
            "Token brushes whose colour key does not exist, so they cannot follow a " +
            "theme switch: " + string.Join(", ", orphans));
    }

    /// <summary>WCAG 2.1 relative-luminance contrast ratio between two opaque colours.</summary>
    private static double ContrastRatio(Color a, Color b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        var (lighter, darker) = la >= lb ? (la, lb) : (lb, la);

        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color c)
        => (0.2126 * Linearise(c.R)) + (0.7152 * Linearise(c.G)) + (0.0722 * Linearise(c.B));

    private static double Linearise(byte channel)
    {
        var v = channel / 255.0;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }
}

/// <summary>The theme default is a product decision, so it is pinned by a test.</summary>
public sealed class ThemeDefaultTests
{
    [Fact]
    public void Light_is_the_default_even_when_windows_is_dark()
    {
        // Light is the primary designed theme; dark is chosen deliberately rather than
        // inherited from the desktop. See docs/UI_DESIGN.md section 1.3.
        Assert.Equal(ThemePreference.Light, new AppSettings().Theme);
    }
}
