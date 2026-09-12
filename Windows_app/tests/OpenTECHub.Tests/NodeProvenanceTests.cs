using System.IO;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.PowerTesting;
using OpenTECHub.Services.Telemetry;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>Node identity changes reach the Eventos page, once each, with a time.</summary>
public class NodeIdentityJournalTests
{
    private static SensorSnapshot Pump(string? ip, string? mac = "AA:BB:CC:DD:EE:03", string? fw = "3.8")
        => new() { PumpNode = new ExternalNodeIdentity(ip, mac, fw) };

    [Fact]
    public void Registration_renumbering_firmware_and_board_swap_are_journalled_once_each()
    {
        var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, new TestClock(DateTimeOffset.UnixEpoch));
        using var journal = new EventJournal(device, arbiter, new MemorySettingsService());

        device.PushTelemetry(Pump("192.168.4.3"));
        device.PushTelemetry(Pump("192.168.4.3"));
        device.PushTelemetry(Pump("192.168.4.3"));
        device.PushTelemetry(Pump("192.168.4.5"));
        device.PushTelemetry(Pump("192.168.4.5", fw: "3.9"));
        device.PushTelemetry(Pump("192.168.4.5", mac: "AA:BB:CC:DD:EE:99", fw: "3.9"));

        var connection = journal.Snapshot().Where(e => e.Source == AuditSource.Connection).ToList();

        Assert.Equal(4, connection.Count);
        Assert.Contains("registrado em 192.168.4.3 (firmware 3.8)", connection[0].Message);
        Assert.Equal(AuditSeverity.Information, connection[0].Severity);
        Assert.Contains("mudou de 192.168.4.3 para 192.168.4.5", connection[1].Message);
        Assert.Contains("Firmware", connection[2].Message);
        Assert.Equal(AuditSeverity.Warning, connection[2].Severity);
        Assert.Contains("outro MAC", connection[3].Message);
        Assert.Equal(AuditSeverity.Warning, connection[3].Severity);
        Assert.Contains("dev=pump", connection[3].Detail);
        Assert.Contains(DeviceNamesForTest.Pump, connection[0].Message);
    }

    [Fact]
    public void A_new_link_makes_the_same_address_a_registration_again()
    {
        var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, new TestClock(DateTimeOffset.UnixEpoch));
        using var journal = new EventJournal(device, arbiter, new MemorySettingsService());

        device.PushTelemetry(Pump("192.168.4.3"));
        device.PushState(ConnectionState.Reconnecting);
        device.PushState(ConnectionState.Connected);
        device.PushTelemetry(Pump("192.168.4.3"));

        var registrations = journal.Snapshot().Count(e => e.Message.Contains("registrado em 192.168.4.3", StringComparison.Ordinal));
        Assert.Equal(2, registrations);
    }

    private static class DeviceNamesForTest
    {
        public const string Pump = ViewModels.DeviceNames.ExternalPump;
    }
}

/// <summary>Node firmware next to the Hub firmware in every provenance header.</summary>
public class NodeProvenanceTests : IDisposable
{
    private readonly string _root;
    private readonly IDisposable _scope;

    public NodeProvenanceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"opentechub-nodeprov-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _scope = AppPaths.OverrideForTests(_root);
    }

    public void Dispose()
    {
        _scope.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static readonly SensorSnapshot Registered = new()
    {
        HubFirmwareVersion = "10.1.0-dev",
        HubProtocolVersion = 10,
        PumpNode = new ExternalNodeIdentity("192.168.4.3", "AA:BB:CC:DD:EE:03", "3.8"),
        FlowmeterNode = new ExternalNodeIdentity("192.168.4.4", "AA:BB:CC:DD:EE:04", "v10"),
        BiomassNode = new ExternalNodeIdentity(null, null, "v10"),
    };

    [Fact]
    public void From_keeps_only_nodes_the_hub_described_and_describe_reads_in_registry_order()
    {
        var nodes = ExternalNodeProvenance.From(Registered);

        Assert.Equal(["pump", "flowmeter", "biomass"], nodes.Keys.OrderBy(k => Array.IndexOf([.. NodeFirmwareCatalog.Devices], k)));
        Assert.Equal("3.8", nodes["pump"].FirmwareVersion);
        Assert.Equal("pump=3.8@192.168.4.3 flowmeter=v10@192.168.4.4 biomass=v10@?", ExternalNodeProvenance.Describe(nodes));
        Assert.Equal("desconhecidos", ExternalNodeProvenance.Describe(null));
        Assert.Empty(ExternalNodeProvenance.From(new SensorSnapshot()));
        Assert.Empty(ExternalNodeProvenance.From(null));
    }

    [Fact]
    public void The_sidecar_preamble_names_the_nodes_and_stays_one_line_longer_never_more()
    {
        var with = ServoSessionLogFormat.BuildPreamble("10.1.0-dev", 10, "app", ExternalNodeProvenance.From(Registered));
        var without = ServoSessionLogFormat.BuildPreamble(null, -1, "app");

        Assert.Contains("# nodes: pump=3.8@192.168.4.3 flowmeter=v10@192.168.4.4 biomass=v10@?", with);
        Assert.Contains("# nodes: desconhecidos", without);
        Assert.Equal(with.Split('\n').Length, without.Split('\n').Length);
    }

    [Fact]
    public void The_power_manifest_round_trips_the_nodes_and_an_old_manifest_loads_empty()
    {
        var store = new PowerTestStore(AppPaths.PowerTestsDirectory);
        var geometry = new PowerGeometry
        {
            VesselDiameterM = 0.2,
            LiquidVolumeM3 = 0.005,
            Impellers = { new Impeller { Type = ImpellerType.RushtonFlatBlade, Label = "R", DiameterM = 0.06, BladeCount = 6 } },
        };
        var doc = store.CreateTest("Proveniencia", new FluidProperties(), geometry, new PowerTestSettings(),
            [new PowerCondition { AgitationRpm = 300, GasMode = PowerGasMode.Ungassed, RequestedReplicates = 1 }]);
        doc.ExternalNodes = ExternalNodeProvenance.From(Registered);
        store.SaveTestManifest(doc);

        var loaded = store.LoadTest(doc.FolderName)!;
        Assert.Equal("3.8", loaded.ExternalNodes["pump"].FirmwareVersion);
        Assert.Equal("192.168.4.4", loaded.ExternalNodes["flowmeter"].Ip);

        // Strip the field the way a manifest written before this build would lack it.
        var manifest = Path.Combine(store.RootDirectory, doc.FolderName, PowerTestFileContracts.TestManifestFileName);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifest))!.AsObject();
        Assert.True(json.Remove("externalNodes"), "manifest is camelCase");
        File.WriteAllText(manifest, json.ToJsonString());

        var old = store.LoadTest(doc.FolderName)!;
        Assert.Empty(old.ExternalNodes);
    }

    [Fact]
    public void The_kla_manifest_round_trips_hub_and_nodes()
    {
        var store = new KlaTestStore(AppPaths.KlaTestsDirectory);
        var doc = store.CreateTest("Proveniencia kLa", new KlaTestSettings(), NitrogenValve.Valve2,
            initialConditions: [new KlaTestCondition { AgitationRpm = 300, AirflowLpm = 2.0, RequestedReplicates = 1 }]);
        doc.HubFirmwareVersion = "10.1.0-dev";
        doc.HubProtocolVersion = 10;
        doc.ExternalNodes = ExternalNodeProvenance.From(Registered);
        store.SaveTestManifest(doc);

        var loaded = store.LoadTest(doc.FolderName)!;
        Assert.Equal("10.1.0-dev", loaded.HubFirmwareVersion);
        Assert.Equal(10, loaded.HubProtocolVersion);
        Assert.Equal("v10", loaded.ExternalNodes["flowmeter"].FirmwareVersion);
    }

    [Fact]
    public void FlowTuning_is_captured_in_provenance_when_echoes_are_present()
    {
        var snapshotWithTuning = new SensorSnapshot
        {
            FlowmeterNode = new ExternalNodeIdentity("192.168.4.4", "AA:BB:CC:DD:EE:04", "v11"),
            FlowKp = 0.8,
            FlowKi = 0.15,
            FlowFfGain = 0.106,
            FlowFfOffset = 0.01033,
            FlowRampRate = 2.0,
        };

        var nodes = ExternalNodeProvenance.From(snapshotWithTuning);
        Assert.NotNull(nodes["flowmeter"].FlowTuning);
        Assert.Equal(0.8, nodes["flowmeter"].FlowTuning!.Kp);
        Assert.Equal(0.15, nodes["flowmeter"].FlowTuning!.Ki);
        Assert.Equal(0.106, nodes["flowmeter"].FlowTuning!.FfGain);
        Assert.Equal(0.01033, nodes["flowmeter"].FlowTuning!.FfOffset);
        Assert.Equal(2.0, nodes["flowmeter"].FlowTuning!.RampRate);

        var nodesWithoutTuning = ExternalNodeProvenance.From(Registered);
        Assert.Null(nodesWithoutTuning["flowmeter"].FlowTuning);
    }
}
