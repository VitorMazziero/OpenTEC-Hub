using System.IO;
using Microsoft.Extensions.DependencyInjection;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Tests.Rendering;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>D-069: the speaker button next to Buscar mutes every sound of the application, indefinitely.</summary>
[Collection(Rendering.WpfRenderingCollection.Name)]
public sealed class SoundMuteButtonTests
{
    [Fact]
    public void The_sound_is_on_by_default_and_the_choice_is_a_persisted_setting()
    {
        Assert.False(new AppSettings().Ui.SoundMuted);
    }

    [Fact]
    public void The_speaker_button_sits_right_after_Buscar_and_before_the_theme_button()
    {
        var window = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "src", "OpenTECHub", "MainWindow.xaml"));
        var search = window.IndexOf("Content=\"Buscar\"", StringComparison.Ordinal);
        var speaker = window.IndexOf("ToggleSoundCommand", StringComparison.Ordinal);
        var theme = window.IndexOf("ToggleThemeCommand", StringComparison.Ordinal);

        Assert.True(search > 0 && search < speaker && speaker < theme);
        Assert.Contains("Key=\"{Binding SoundIconKey}\"", window, StringComparison.Ordinal);
    }

    [Fact]
    public void Toggling_flips_the_icon_and_the_setting_and_back()
    {
        var settings = WpfRenderingHost.Services.GetRequiredService<ISettingsService>();
        var shell = WpfRenderingHost.Services.GetRequiredService<ShellViewModel>();
        WpfRenderingHost.Run(() =>
        {
            var before = settings.Current.Ui.SoundMuted;
            try
            {
                shell.ToggleSoundCommand.Execute(null);
                Assert.Equal(!before, settings.Current.Ui.SoundMuted);
                Assert.Equal(before, shell.IsSoundOn);
                Assert.Equal(before ? "SoundOn" : "SoundOff", shell.SoundIconKey);
            }
            finally
            {
                settings.Update(s => s with { Ui = s.Ui with { SoundMuted = before } });
            }

            Assert.Equal(!before, shell.IsSoundOn);
        });
    }
}
