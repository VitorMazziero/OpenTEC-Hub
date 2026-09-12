using System.IO;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Platform;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// The two node actions on the Controle drawers, and the rule that gates them: the Hub
/// must have an address <i>and</i> the PC must be on the Hub's Wi-Fi.
/// </summary>
public class NodeNetworkActionsTests
{
    private sealed class RecordingFiles : IFileInteractionService
    {
        public List<Uri> Opened { get; } = [];
        public List<string> Copied { get; } = [];

        public string? ChooseSavePath(string title, string suggestedName, string filter, string extension) => null;
        public string? ChooseOpenPath(string title, string filter, string extension) => null;
        public string? ChooseFolder(string title, string? initialDirectory = null) => null;
        public void OpenFolder(string path) { }
        public void CopyText(string text) => Copied.Add(text);
        public void OpenUri(Uri uri) => Opened.Add(uri);
    }

    private static readonly SensorSnapshot PumpRegistered = new()
    {
        FlowmeterOnline = true,
        HasPumpTelemetry = true,
        PumpOnline = true,
        PumpNode = new ExternalNodeIdentity("192.168.4.3", "AA:BB:CC:DD:EE:03", "3.8"),
    };

    [Fact]
    public void Over_usb_the_app_knows_the_address_but_the_actions_stay_off()
    {
        var files = new RecordingFiles();
        var device = new RecordingDeviceService { Medium = TransportMedium.Usb };
        using var fx = new ControlViewModelTests.ControlFixture(device: device, files: files);
        device.PushState(ConnectionState.Connected);
        device.PushTelemetry(PumpRegistered);

        Assert.False(fx.Control.IsHubOnWiFi);
        Assert.Equal("192.168.4.3 · fw 3.8", fx.Pump.Status.NetworkSummaryText);

        fx.Control.OpenNodeDiagnosticsCommand.Execute(fx.Pump.Status);
        Assert.Empty(files.Opened);

        // Copying needs no network: the address is still useful for the OTA tool.
        fx.Control.CopyNodeIpCommand.Execute(fx.Pump.Status);
        Assert.Equal("192.168.4.3", Assert.Single(files.Copied));
    }

    [Fact]
    public void On_wifi_open_diagnostics_hands_the_node_diag_page_to_the_browser()
    {
        var files = new RecordingFiles();
        var device = new RecordingDeviceService { Medium = TransportMedium.WiFi };
        using var fx = new ControlViewModelTests.ControlFixture(device: device, files: files);
        device.PushState(ConnectionState.Connected);
        device.PushTelemetry(PumpRegistered);

        Assert.True(fx.Control.IsHubOnWiFi);
        fx.Control.OpenNodeDiagnosticsCommand.Execute(fx.Pump.Status);

        Assert.Equal(new Uri("http://192.168.4.3/diag"), Assert.Single(files.Opened));
        Assert.Contains("192.168.4.3", fx.Control.StatusText);
    }

    [Fact]
    public void Without_an_address_neither_action_does_anything()
    {
        var files = new RecordingFiles();
        var device = new RecordingDeviceService { Medium = TransportMedium.WiFi };
        using var fx = new ControlViewModelTests.ControlFixture(device: device, files: files);
        device.PushState(ConnectionState.Connected);
        device.PushTelemetry(new SensorSnapshot { FlowmeterOnline = true, HasPumpTelemetry = true, PumpOnline = true });

        fx.Control.OpenNodeDiagnosticsCommand.Execute(fx.Pump.Status);
        fx.Control.CopyNodeIpCommand.Execute(fx.Pump.Status);
        fx.Control.OpenNodeDiagnosticsCommand.Execute(null);

        Assert.Empty(files.Opened);
        Assert.Empty(files.Copied);
    }

    [Fact]
    public void Losing_the_link_turns_the_wifi_flag_off()
    {
        var device = new RecordingDeviceService { Medium = TransportMedium.WiFi };
        using var fx = new ControlViewModelTests.ControlFixture(device: device);
        device.PushState(ConnectionState.Connected);
        Assert.True(fx.Control.IsHubOnWiFi);

        device.PushState(ConnectionState.Reconnecting);
        Assert.False(fx.Control.IsHubOnWiFi);
    }

    [Fact]
    public void Every_external_drawer_carries_the_network_panel_wired_to_the_page_commands()
    {
        var xaml = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "src", "OpenTECHub", "Views", "ControlView.xaml"));

        foreach (var vm in new[] { "FlowControl", "FoamControl", "PumpControl", "BiomassControl", "FlaskAgitator" })
        {
            Assert.Contains($"<ctl:NodeNetworkPanel Status=\"{{Binding DataContext.{vm}.Status, RelativeSource={{RelativeSource AncestorType=UserControl}}}}\"", xaml, StringComparison.Ordinal);
        }

        Assert.Equal(5, Count(xaml, "CanReachNodes=\"{Binding DataContext.IsHubOnWiFi,"));
        Assert.Equal(5, Count(xaml, "OpenCommand=\"{Binding DataContext.OpenNodeDiagnosticsCommand,"));
        Assert.Equal(5, Count(xaml, "CopyCommand=\"{Binding DataContext.CopyNodeIpCommand,"));

        // The servo drive is not an external node in the Hub's registry: no panel in its drawer.
        Assert.DoesNotContain("DataContext.ServoDrive.Status, RelativeSource={RelativeSource AncestorType=UserControl}}}\"\n                          CanReachNodes", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_panel_explains_a_disabled_button_instead_of_hiding_it()
    {
        var xaml = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "src", "OpenTECHub", "Controls", "NodeNetworkPanel.xaml"));

        Assert.Contains("Content=\"Abrir diagnóstico\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Copiar IP\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<Condition Binding=\"{Binding Status.IsNodeReachable, ElementName=Root}\" Value=\"True\" />", xaml, StringComparison.Ordinal);
        Assert.Contains("<Condition Binding=\"{Binding CanReachNodes, ElementName=Root}\" Value=\"True\" />", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Visibility=\"{Binding CanReachNodes", xaml, StringComparison.Ordinal);
        Assert.Contains("rede Wi-Fi do Hub", Controls.NodeReachabilityToolTipConverter.OffWiFi, StringComparison.Ordinal);
    }

    private static int Count(string text, string token)
    {
        var n = 0;
        for (var i = text.IndexOf(token, StringComparison.Ordinal); i >= 0; i = text.IndexOf(token, i + 1, StringComparison.Ordinal))
        {
            n++;
        }

        return n;
    }
}
