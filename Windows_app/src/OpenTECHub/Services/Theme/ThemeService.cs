using System.Collections;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.Services.Theme;

/// <summary>Applies the light or dark token dictionary at runtime.</summary>
public interface IThemeService
{
    /// <summary>True when dark tokens are currently applied.</summary>
    bool IsDark { get; }

    /// <summary>Fired whenever the active theme changes.</summary>
    event Action<bool>? ThemeChanged;

    /// <summary>Applies the preference, resolving <see cref="ThemePreference.System"/>.</summary>
    void Apply(ThemePreference preference);
}

/// <summary>
/// Refills the active token dictionary in <see cref="Application.Resources"/>.
/// </summary>
/// <remarks>
/// <para>
/// Slot 0 of the merged dictionaries holds the light/dark colours. Every brush in
/// <c>Tokens.Shared.xaml</c> resolves its colour from there through
/// <c>DynamicResource</c>, so changing those entries repaints the whole tree without
/// rebuilding it.
/// </para>
/// <para>
/// <b>The entries are replaced, not the dictionary.</b> This originally assigned
/// <c>MergedDictionaries[0]</c> a new dictionary, which does not invalidate
/// <c>DynamicResource</c> references already resolved in a live visual tree. The effect
/// was that the theme worked at startup - applied before the window was shown - and the
/// in-app toggle silently did nothing at all. See <see cref="_live"/>.
/// </para>
/// <para>
/// Under <see cref="ThemePreference.System"/> the Windows app theme is tracked live,
/// so switching Windows to dark repaints the app immediately.
/// </para>
/// </remarks>
public sealed class ThemeService : IThemeService, IDisposable
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    private static readonly Uri LightTokens = new("Themes/Tokens.Light.xaml", UriKind.Relative);
    private static readonly Uri DarkTokens = new("Themes/Tokens.Dark.xaml", UriKind.Relative);

    private readonly ILogger<ThemeService> _log;
    private ThemePreference _preference = ThemePreference.System;
    private bool _watchingSystem;

    /// <summary>
    /// The theme dictionary in slot 0, owned by this service and never replaced.
    /// </summary>
    /// <remarks>
    /// <b>Replacing a merged dictionary does not repaint a live visual tree.</b> The
    /// brushes in Tokens.Shared.xaml resolve their Color through DynamicResource against
    /// slot 0, and assigning <c>MergedDictionaries[0]</c> does not invalidate those
    /// references - the switch was silently a no-op after the first frame, so the theme
    /// only ever appeared to work because it was applied before the window was shown.
    /// Assigning entries INTO a live dictionary does invalidate, so slot 0 is created
    /// once and only its contents change.
    /// </remarks>
    private ResourceDictionary? _live;

    public ThemeService(ILogger<ThemeService> log) => _log = log;

    public bool IsDark { get; private set; }

    public event Action<bool>? ThemeChanged;

    public void Apply(ThemePreference preference)
    {
        _preference = preference;

        var dark = preference switch
        {
            ThemePreference.Light => false,
            ThemePreference.Dark => true,
            _ => IsSystemDark(),
        };

        SetWatchingSystem(preference == ThemePreference.System);
        ApplyTokens(dark);
    }

    public void Dispose() => SetWatchingSystem(false);

    private void ApplyTokens(bool dark)
    {
        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        IsDark = dark;

        var source = new ResourceDictionary { Source = dark ? DarkTokens : LightTokens };
        var merged = application.Resources.MergedDictionaries;

        // Slot 0 is the theme by convention; see App.xaml. On the first call we swap
        // App.xaml's boot dictionary for one this service owns, then never replace it
        // again - only its entries change.
        if (_live is null)
        {
            _live = [];

            if (merged.Count == 0)
            {
                merged.Add(_live);
            }
            else
            {
                merged[0] = _live;
            }
        }

        foreach (DictionaryEntry entry in source)
        {
            _live[entry.Key] = entry.Value;
        }

        var repainted = RepaintBrushes(merged);

        foreach (Window window in application.Windows)
        {
            window.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "SurfaceBaseBrush");
            window.InvalidateVisual();
        }

        _log.LogInformation(
            "Theme applied: {Theme} ({Count} brushes repainted)",
            dark ? "dark" : "light", repainted);

        ThemeChanged?.Invoke(dark);
    }

    /// <summary>
    /// Pushes the new colours into the live brush objects.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what actually repaints the application, and it cannot be done in
    /// XAML.</b> The brushes live in <c>Tokens.Shared.xaml</c> and the colours in the
    /// theme dictionary beside it - <i>sibling</i> merged dictionaries. A
    /// <c>DynamicResource</c> written inside one resource dictionary that points at a
    /// key in a sibling resolves correctly the first time it is read and is then never
    /// re-evaluated: WPF invalidates the visual tree when a dictionary changes, but a
    /// brush sitting in another dictionary is not in the visual tree.
    /// </para>
    /// <para>
    /// The result was a theme system that looked right whenever it was applied before
    /// the window appeared, and did nothing at all afterwards - the in-app toggle was
    /// silently inert. Assigning <see cref="SolidColorBrush.Color"/> on the existing
    /// brush instance repaints reliably, because every element in the tree already holds
    /// that instance.
    /// </para>
    /// <para>
    /// Pairing is by convention: <c>FooBrush</c> takes its colour from <c>FooColor</c>.
    /// A brush whose colour key is missing keeps its current value rather than throwing,
    /// but that is a bug in the token files and <c>TokenParityTests</c> is what catches
    /// it.
    /// </para>
    /// </remarks>
    private int RepaintBrushes(IEnumerable<ResourceDictionary> dictionaries)
    {
        const string BrushSuffix = "Brush";
        var repainted = 0;

        foreach (var dictionary in dictionaries)
        {
            repainted += RepaintDictionary(dictionary, BrushSuffix);
        }

        return repainted;
    }

    private int RepaintDictionary(ResourceDictionary dictionary, string brushSuffix)
    {
        if (ReferenceEquals(dictionary, _live))
        {
            return 0;
        }

        var repainted = 0;
        var entries = new System.Collections.Generic.List<System.Collections.DictionaryEntry>();
        foreach (System.Collections.DictionaryEntry entry in dictionary)
        {
            entries.Add(entry);
        }

        foreach (var entry in entries)
        {
            if (entry.Key is not string key ||
                !key.EndsWith(brushSuffix, StringComparison.Ordinal) ||
                entry.Value is not SolidColorBrush brush)
            {
                continue;
            }

            var colourKey = string.Concat(key.AsSpan(0, key.Length - brushSuffix.Length), "Color");

            if (_live?[colourKey] is Color colour)
            {
                if (brush.Color != colour)
                {
                    _log.LogInformation("Replacing {Key} to color {New}", key, colour);
                    var newBrush = new SolidColorBrush(colour);
                    newBrush.Freeze();
                    dictionary[key] = newBrush;
                    repainted++;
                }
            }
        }

        foreach (var merged in dictionary.MergedDictionaries)
        {
            repainted += RepaintDictionary(merged, brushSuffix);
        }

        return repainted;
    }

    /// <remarks>
    /// The value is <c>AppsUseLightTheme</c>: 0 means dark. A missing key means an
    /// older Windows that only had light, so light is the right fallback.
    /// </remarks>
    private bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            if (key?.GetValue(AppsUseLightThemeValue) is int useLight)
            {
                return useLight == 0;
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not read the Windows theme preference; assuming light");
        }

        return false;
    }

    private void SetWatchingSystem(bool watch)
    {
        if (watch == _watchingSystem)
        {
            return;
        }

        _watchingSystem = watch;

        if (watch)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
        else
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color))
        {
            return;
        }

        if (_preference != ThemePreference.System)
        {
            return;
        }

        // SystemEvents fires on its own thread; resource dictionaries are UI-affine.
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var dark = IsSystemDark();
            if (dark != IsDark)
            {
                ApplyTokens(dark);
            }
        });
    }
}
