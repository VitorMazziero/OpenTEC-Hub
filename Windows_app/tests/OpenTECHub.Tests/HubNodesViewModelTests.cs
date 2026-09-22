using System.IO;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// The "Nós na rede do Hub" panel: six fixed rows fed by the frame, enriched by
/// <c>/nodes</c> only on Wi-Fi, honest about a Hub that predates the keys.
/// </summary>
public class HubNodesViewModelTests
{
    private static readonly SensorSnapshot TwoRegistered = new()
    {
        HubFirmwareVersion = "10.1.0-dev",
        FlowmeterOnline = true,
        HasPumpTelemetry = true,
        PumpOnline = true,
        HasDistanceTelemetry = true,
        DistanceOnline = false,
        PumpNode = new ExternalNodeIdentity("192.168.4.3", "AA:BB:CC:DD:EE:03", "3.9"),
        FlowmeterNode = new ExternalNodeIdentity("192.168.4.4", "AA:BB:CC:DD:EE:04", "v11"),
    };

    private static HubNodeDirectory Directory(long hubTime = 50_000) => new(hubTime,
    [
        new HubNodeEntry("pump", new ExternalNodeIdentity("192.168.4.3", "AA:BB:CC:DD:EE:03", "3.9"), true, true, 40_000, 49_600, 400),
        new HubNodeEntry("distance", ExternalNodeIdentity.Empty, false, false, 0, 0, 999999),
        new HubNodeEntry("biomass", new ExternalNodeIdentity(null, "AA:BB:CC:DD:EE:06", "v11"), false, true, 10_000, 0, 40_000),
    ]);

    private static HubNodeDiagDirectory Diagnostics(int failStreak = 0) => new(50_000,
    [
        new HubNodeDiag("pump", 200, TimeSpan.FromMilliseconds(400), -61, 208000, 42, failStreak, false,
            new Dictionary<string, string> { ["flow"] = "1.25", ["vol"] = "2.5", ["mode"] = "1" }),
        new HubNodeDiag("distance", 0, null, null, null, null, null, null,
            new Dictionary<string, string>()),
    ]);

    [Fact]
    public void Five_rows_always_and_the_frame_fills_them()
    {
        var device = new RecordingDeviceService();
        using var vm = new HubNodesViewModel(device, (_, _) => Task.FromResult<HubNodeDirectory?>(null));
        device.PushTelemetry(TwoRegistered);

        Assert.Equal(6, vm.Nodes.Count);
        Assert.Equal(["distance", "agitator", "pump", "flowmeter", "biomass", "bath"], vm.Nodes.Select(n => n.Device));

        var pump = vm.Nodes.Single(n => n.Device == "pump");
        Assert.Equal(DeviceNames.ExternalPump, pump.DisplayName);
        Assert.Equal("192.168.4.3", pump.IpText);
        Assert.Equal("3.9", pump.FirmwareText);
        Assert.Equal("Online", pump.StateText);
        Assert.Null(pump.FirmwareAdvisoryText);

        var distance = vm.Nodes.Single(n => n.Device == "distance");
        Assert.Equal("—", distance.IpText);
        Assert.Equal("Offline", distance.StateText);

        var agitator = vm.Nodes.Single(n => n.Device == "agitator");
        Assert.Equal("Aguardando telemetria", agitator.StateText);

        Assert.Equal(2, vm.RegisteredCount);
        Assert.Equal("2/6 nós com endereço", vm.RegisteredCountText);
        Assert.False(vm.ShowLegacyHubNotice);
    }

    [Fact]
    public void A_hub_before_10_1_gets_one_notice_not_five_empty_mysteries()
    {
        var device = new RecordingDeviceService();
        using var vm = new HubNodesViewModel(device, (_, _) => Task.FromResult<HubNodeDirectory?>(null));
        device.PushTelemetry(new SensorSnapshot { HubFirmwareVersion = "10.0.1-dev", FlowmeterOnline = true });

        Assert.True(vm.ShowLegacyHubNotice);
        Assert.Contains("10.0.1-dev", vm.LegacyHubNoticeText);
        Assert.Contains("10.1", vm.LegacyHubNoticeText);
        Assert.All(vm.Nodes, n => Assert.Equal("—", n.IpText));
    }

    [Theory]
    [InlineData("10.1.0-dev", true)]
    [InlineData("10.1", true)]
    [InlineData("10.2.0", true)]
    [InlineData("11.0.0", true)]
    [InlineData("10.0.1-dev", false)]
    [InlineData("9.1.0-dev", false)]
    [InlineData("10", false)]
    [InlineData("garbage", false)]
    public void Identity_support_is_read_from_the_leading_major_minor(string firmware, bool publishes)
        => Assert.Equal(publishes, HubNodesViewModel.PublishesIdentity(firmware));

    [Fact]
    public async Task Over_usb_refresh_requests_cached_diagnostics_without_consulting_http()
    {
        var calls = 0;
        var device = new RecordingDeviceService { Medium = TransportMedium.Usb };
        using var vm = new HubNodesViewModel(device, (_, _) => { calls++; return Task.FromResult<HubNodeDirectory?>(Directory()); });
        device.PushState(ConnectionState.Connected);
        device.PushTelemetry(TwoRegistered);

        Assert.False(vm.IsHubOnWiFi);
        Assert.True(vm.RefreshCommand.CanExecute(null));
        await vm.RefreshAsync();

        Assert.Equal(0, calls);
        Assert.Contains("Por USB", vm.DirectoryAvailabilityText);
        Assert.Contains("{\"nodeDiag\":\"all\"}", Assert.Single(device.Sent));
        Assert.Null(vm.Nodes.Single(n => n.Device == "pump").Registered);
    }

    [Fact]
    public async Task On_wifi_the_directory_enriches_registration_and_freshness_without_overriding_the_frame()
    {
        var device = new RecordingDeviceService { Medium = TransportMedium.WiFi };
        using var vm = new HubNodesViewModel(
            device,
            (hub, _) => Task.FromResult<HubNodeDirectory?>(hub == "FAKE" ? Directory() : null),
            (hub, _) => Task.FromResult<HubNodeDiagDirectory?>(hub == "FAKE" ? Diagnostics() : null));
        device.PushState(ConnectionState.Connected);
        device.PushTelemetry(TwoRegistered);

        Assert.True(vm.IsHubOnWiFi);
        Assert.True(vm.RefreshCommand.CanExecute(null));
        await vm.RefreshAsync();

        var pump = vm.Nodes.Single(n => n.Device == "pump");
        Assert.True(pump.Registered);
        Assert.Equal(TimeSpan.FromMilliseconds(400), pump.HubReportedAge);
        Assert.Equal("0 s", pump.SeenText);

        var distance = vm.Nodes.Single(n => n.Device == "distance");
        Assert.False(distance.Registered);
        Assert.Equal("Nunca se registrou", distance.StateText);

        // The directory knows the biomass MAC/firmware the frame did not carry (never online).
        var biomass = vm.Nodes.Single(n => n.Device == "biomass");
        Assert.Equal("AA:BB:CC:DD:EE:06", biomass.MacText);
        Assert.Equal("—", biomass.IpText);
        Assert.Contains("Diretório consultado", vm.LastDirectoryText);
        Assert.Equal("-61 dBm", pump.RssiText);
        Assert.Equal("203 KiB", pump.HeapText);
        Assert.Equal("42 s", pump.UptimeText);
        Assert.Equal("Livre", pump.OtaText);
        Assert.Contains("vazão", pump.DiagnosticText);
        Assert.Contains("Diagnóstico via Hub há", vm.LastDiagnosticsText);
    }

    [Fact]
    public void Serial_diagnostics_enrich_the_row_and_fail_streak_is_only_a_warning()
    {
        var device = new RecordingDeviceService { Medium = TransportMedium.Usb };
        using var vm = new HubNodesViewModel(device, (_, _) => Task.FromResult<HubNodeDirectory?>(null));
        device.PushState(ConnectionState.Connected);
        device.PushNodeDiag(
            """{"NodeDiag":{"dev":"pump","code":200,"age_ms":400,"diag":{"uptime_s":42,"free_heap":208000,"rssi":-61,"hub_fail_streak":8,"ota":false,"flow":1.25,"vol":2.5,"mode":1}}}""");

        var pump = vm.Nodes.Single(n => n.Device == "pump");
        Assert.Equal("8", pump.HubFailuresText);
        Assert.True(pump.HasHubFailureWarning);
        Assert.Equal("-61 dBm", pump.RssiText);
        Assert.DoesNotContain("alarme", pump.DiagnosticText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Truncated_diag_is_reported_as_a_hub_limit_not_as_missing_metrics()
    {
        // PONTOS §8.3: HTTP 200 with diag null because the body outgrew the Hub's 511 B
        // cache must not read as "Sem métricas específicas" - the fix is on the Hub side.
        var device = new RecordingDeviceService { Medium = TransportMedium.Usb };
        using var vm = new HubNodesViewModel(device, (_, _) => Task.FromResult<HubNodeDirectory?>(null));
        device.PushState(ConnectionState.Connected);
        device.PushNodeDiag(
            """{"NodeDiag":{"dev":"biomass","code":200,"age_ms":400,"truncated":true,"body_bytes":907,"diag":null}}""");

        var biomass = vm.Nodes.Single(n => n.Device == "biomass");
        Assert.True(biomass.Diagnostic!.Truncated);
        Assert.Equal(907, biomass.Diagnostic.BodyBytes);
        Assert.Contains("907 B", biomass.DiagnosticText);
        Assert.Contains("atualize o Hub", biomass.DiagnosticText);

        // A Hub that predates the field still parses; the flag simply stays false.
        device.PushNodeDiag(
            """{"NodeDiag":{"dev":"pump","code":200,"age_ms":400,"diag":null}}""");
        var pump = vm.Nodes.Single(n => n.Device == "pump");
        Assert.False(pump.Diagnostic!.Truncated);
        Assert.Null(pump.Diagnostic.BodyBytes);
        Assert.Equal("Sem métricas específicas", pump.DiagnosticText);
    }

    [Fact]
    public async Task A_silent_hub_changes_nothing_but_the_footer()
    {
        var device = new RecordingDeviceService { Medium = TransportMedium.WiFi };
        using var vm = new HubNodesViewModel(device, (_, _) => Task.FromResult<HubNodeDirectory?>(null));
        device.PushState(ConnectionState.Connected);
        device.PushTelemetry(TwoRegistered);

        await vm.RefreshAsync();

        Assert.Equal("192.168.4.3", vm.Nodes.Single(n => n.Device == "pump").IpText);
        Assert.Contains("não respondeu", vm.LastDirectoryText);
    }

    [Fact]
    public void Losing_the_link_clears_presence_but_keeps_what_the_nodes_said_about_themselves()
    {
        var device = new RecordingDeviceService { Medium = TransportMedium.WiFi };
        using var vm = new HubNodesViewModel(device, (_, _) => Task.FromResult<HubNodeDirectory?>(null));
        device.PushState(ConnectionState.Connected);
        device.PushTelemetry(TwoRegistered);

        device.PushState(ConnectionState.Reconnecting);

        Assert.False(vm.IsHubOnWiFi);
        Assert.Equal(0, vm.RegisteredCount);
        var pump = vm.Nodes.Single(n => n.Device == "pump");
        Assert.Null(pump.Online);
        Assert.Equal("Aguardando telemetria", pump.StateText);
        Assert.Equal("3.9", pump.FirmwareText);
    }

    [Theory]
    [InlineData(5, "5 s")]
    [InlineData(59, "59 s")]
    [InlineData(61, "1 min")]
    [InlineData(3_700, "1,0 h")]
    public void Seen_age_reads_in_the_operator_units(int seconds, string expected)
    {
        var text = HubNodeRowViewModel.Format(TimeSpan.FromSeconds(seconds));
        Assert.Equal(expected.Replace(",", System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator), text);
    }

    [Fact]
    public void Settings_activates_the_panel_only_on_the_connection_section()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService(new Services.Persistence.AppSettings());
        var hubNodes = new HubNodesViewModel(device, (_, _) => Task.FromResult<HubNodeDirectory?>(null));
        using var vm = new SettingsViewModel(settings, new FakeThemeService(), device, new RecordingDialogServiceForSettings(), hubNodes: hubNodes);

        Assert.True(hubNodes.IsActive);
        vm.SelectedSection = vm.Sections.Single(s => s.Id == "calibration");
        Assert.False(hubNodes.IsActive);
        vm.SelectedSection = vm.Sections.Single(s => s.Id == "connection");
        Assert.True(hubNodes.IsActive);
    }

    [Fact]
    public void The_settings_view_and_popover_carry_the_panel_and_the_count()
    {
        var settings = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "src", "OpenTECHub", "Views", "SettingsView.xaml"));
        var window = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "src", "OpenTECHub", "MainWindow.xaml"));

        Assert.Contains("Text=\"Nós na rede do Hub\"", settings, StringComparison.Ordinal);
        foreach (var header in new[] { "Dispositivo", "IP", "MAC", "Firmware", "Estado", "Visto há", "RSSI", "Heap", "Uptime", "Falhas c/ Hub", "OTA", "Métricas do nó" })
        {
            Assert.Contains($"Header=\"{header}\"", settings, StringComparison.Ordinal);
        }

        Assert.Contains("Command=\"{Binding RefreshCommand}\"", settings, StringComparison.Ordinal);
        Assert.Contains("{Binding ShowLegacyHubNotice, Converter={StaticResource BoolToVis}}", settings, StringComparison.Ordinal);
        Assert.Contains("Text=\"Nós do Hub\"", window, StringComparison.Ordinal);
        Assert.Contains("{Binding NodesText}", window, StringComparison.Ordinal);
    }

    private sealed class FakeThemeService : Services.Theme.IThemeService
    {
        public bool IsDark => false;
        public event Action<bool>? ThemeChanged;
        public void Apply(Services.Persistence.ThemePreference preference) => ThemeChanged?.Invoke(IsDark);
    }

    private sealed class RecordingDialogServiceForSettings : Services.Dialogs.IDialogService
    {
        public bool ConfirmDestructive(string title, string consequence, string exactCommand) => false;
        public bool Confirm(string title, string message, string confirmText = "Confirmar", string cancelText = "Cancelar", bool isDanger = false) => false;
        public bool PromptInput(string title, string message, out string response, string initialValue = "") { response = ""; return false; }
        public Services.Dialogs.RecipeStartOption PromptRecipeStart(string recipeName) => Services.Dialogs.RecipeStartOption.StartPreserving;
    }
}

/// <summary>The popover's node count follows the frame and never claims a network nobody described.</summary>
public class ConnectionNodesCountTests
{
    [Fact]
    public void Count_is_dash_before_identity_and_addresses_over_five_after()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService(new Services.Persistence.AppSettings());
        using var vm = new ConnectionViewModel(device, settings);

        device.PushTelemetry(new SensorSnapshot { FlowmeterOnline = true });
        Assert.Equal("—", vm.NodesText);

        device.PushTelemetry(new SensorSnapshot
        {
            PumpNode = new ExternalNodeIdentity("192.168.4.3", null, "3.9"),
            BiomassNode = new ExternalNodeIdentity(null, "AA", "v11"),
        });
        Assert.Equal("1/6", vm.NodesText);

        device.PushState(ConnectionState.Reconnecting);
        Assert.Equal("—", vm.NodesText);
    }
}
