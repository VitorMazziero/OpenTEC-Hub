using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using TecnalHub.Services.Persistence;

namespace TecnalHub.Services.Theme;

/// <summary>Applies the light or dark token dictionary at runtime.</summary>
public interface IThemeService
{
    /// <summary>True when dark tokens are currently applied.</summary>
    bool IsDark { get; }

    /// <summary>Applies the preference, resolving <see cref="ThemePreference.System"/>.</summary>
    void Apply(ThemePreference preference);
}

/// <summary>
/// Swaps the active token dictionary in <see cref="Application.Resources"/>.
/// </summary>
/// <remarks>
/// <para>
/// Only slot 0 of the merged dictionaries is replaced - the light/dark tokens. Every
/// brush in <c>Tokens.Shared.xaml</c> resolves its colour through
/// <c>DynamicResource</c>, so replacing that one dictionary repaints the whole tree
/// without rebuilding it.
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

    public ThemeService(ILogger<ThemeService> log) => _log = log;

    public bool IsDark { get; private set; }

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

        var dictionary = new ResourceDictionary { Source = dark ? DarkTokens : LightTokens };
        var merged = application.Resources.MergedDictionaries;

        if (merged.Count == 0)
        {
            merged.Add(dictionary);
        }
        else
        {
            // Slot 0 is the theme by convention; see App.xaml.
            merged[0] = dictionary;
        }

        _log.LogInformation("Theme applied: {Theme}", dark ? "dark" : "light");
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
