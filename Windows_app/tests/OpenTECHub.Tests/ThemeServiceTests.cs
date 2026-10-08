using System.Collections;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Theme;
using Xunit;

namespace OpenTECHub.Tests;

[Collection(Rendering.WpfRenderingCollection.Name)]
public sealed class ThemeServiceTests
{
    // Skipped: ThemeService.Apply loads the token dictionaries through pack:// URIs
    // (ResourceDictionary.Source), which only resolve inside a hosted WPF Application — not in the
    // headless test host, where they throw "Cannot locate resource 'themes/tokens.light.xaml'".
    // This is the same reason TokenParityTests reads the token files from disk with XamlReader.
    // Theme switching is covered headlessly by TokenParityTests (every brush has a matching colour)
    // and was verified live by pixel-sampling in Phase 1b WP2.
    [Fact]
    public void ThemeService_repaints_all_brushes_on_light_dark_light_cycle()
    {
        Rendering.WpfRenderingHost.Run(() =>
        {
            var app = Application.Current;
            var themeService = Rendering.WpfRenderingHost.Services.GetRequiredService<IThemeService>();
            var changeEvents = new List<bool>();
            Action<bool> handler = dark => changeEvents.Add(dark);
            themeService.ThemeChanged += handler;

            try
            {
                // Step 1: Start Light
                themeService.Apply(ThemePreference.Light);
                Assert.False(themeService.IsDark);
                Assert.Contains(false, changeEvents);
                var cardBrush1 = (SolidColorBrush)app.Resources["SurfaceCardBrush"];
                var baseBrush1 = (SolidColorBrush)app.Resources["SurfaceBaseBrush"];
                var textBrush1 = (SolidColorBrush)app.Resources["TextPrimaryBrush"];
                Assert.Equal(Color.FromRgb(0xFF, 0xFF, 0xFF), cardBrush1.Color);
                Assert.Equal(Color.FromRgb(0xF7, 0xF9, 0xFC), baseBrush1.Color);
                Assert.Equal(Color.FromRgb(0x17, 0x20, 0x33), textBrush1.Color);

                // Step 2: Switch to Dark
                changeEvents.Clear();
                themeService.Apply(ThemePreference.Dark);
                Assert.True(themeService.IsDark);
                Assert.Contains(true, changeEvents);
                var cardBrush2 = (SolidColorBrush)app.Resources["SurfaceCardBrush"];
                var baseBrush2 = (SolidColorBrush)app.Resources["SurfaceBaseBrush"];
                var textBrush2 = (SolidColorBrush)app.Resources["TextPrimaryBrush"];
                Assert.Equal(Color.FromRgb(0x18, 0x1E, 0x26), cardBrush2.Color);
                Assert.Equal(Color.FromRgb(0x11, 0x16, 0x1D), baseBrush2.Color);
                Assert.Equal(Color.FromRgb(0xE7, 0xEC, 0xF2), textBrush2.Color);

                // Step 3: Switch back to Light
                changeEvents.Clear();
                themeService.Apply(ThemePreference.Light);
                Assert.False(themeService.IsDark);
                Assert.Contains(false, changeEvents);
                var cardBrush3 = (SolidColorBrush)app.Resources["SurfaceCardBrush"];
                var baseBrush3 = (SolidColorBrush)app.Resources["SurfaceBaseBrush"];
                var textBrush3 = (SolidColorBrush)app.Resources["TextPrimaryBrush"];
                Assert.Equal(Color.FromRgb(0xFF, 0xFF, 0xFF), cardBrush3.Color);
                Assert.Equal(Color.FromRgb(0xF7, 0xF9, 0xFC), baseBrush3.Color);
                Assert.Equal(Color.FromRgb(0x17, 0x20, 0x33), textBrush3.Color);
            }
            finally
            {
                themeService.ThemeChanged -= handler;
            }
        });
    }
}
