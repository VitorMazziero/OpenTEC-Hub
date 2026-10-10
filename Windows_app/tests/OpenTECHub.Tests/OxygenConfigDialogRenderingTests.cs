using System.IO;
using System.Windows;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Control;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Tests.Rendering;
using OpenTECHub.ViewModels;
using OpenTECHub.Views.Dialogs;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>The oxygen settings dialog loads and draws its restart-effort card (D-074).</summary>
[Collection(WpfRenderingCollection.Name)]
public sealed class OxygenConfigDialogRenderingTests
{
    [Fact]
    public void Dialog_renders_with_the_restart_effort_card()
    {
        WpfRenderingHost.Run(() =>
        {
            var settings = new MemorySettingsService(new AppSettings
            {
                Cascade = new CascadeSettings { UseRestartEffort = true, RestartEffortPercent = 35, LastEffortPercent = 42.5, LastEffortAt = DateTimeOffset.Now },
            });
            var clock = new TestClock(DateTimeOffset.UnixEpoch);
            var arbiter = new CommandArbiter(new RecordingDeviceService(), clock);
            using var service = new CascadeService(arbiter, arbiter, settings, new FakeKlaProfileStore(), clock);
            using var vm = new OxygenConfigViewModel(service, settings, new FakeKlaProfileStore());
            var dialog = new OxygenConfigDialog(vm);
            var content = (FrameworkElement)dialog.Content;
            dialog.Content = null;
            content.DataContext = vm;

            var bitmap = WpfRenderingHost.RenderElement(content, 760, 1900);
            Assert.True(VisualValidationHelper.ValidateBitmap(bitmap).IsNonTrivial);
            WpfRenderingHost.SavePng(bitmap, Path.Combine(Path.GetTempPath(), "opentec-oxygen-config-dialog.png"));
            dialog.Close();
        });
    }
}
