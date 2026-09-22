using System.Net.Http;
using OpenTECHub.Protocol;
using OpenTECHub.Simulator;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// The simulator reproduces the Hub 10.1 node registry closely enough that the app's
/// identity path can be exercised without a board: the same conditional keys, the same
/// <c>0.0.0.0</c> sentinel, the same <c>/nodes</c> shape, plus the renumbering fault.
/// </summary>
public class SimulatorNodeIdentityTests
{
    private static DeviceModel Model(AcceleratedClock? clock = null) => new(clock: clock ?? new AcceleratedClock(), randomSeed: 1)
    {
        PumpEnabled = true,
        DistanceSensorEnabled = true,
        BiomassEnabled = false,
        FlowmeterEnabled = true,
    };

    [Fact]
    public void Registered_nodes_carry_ip_version_and_mac_and_unregistered_ones_only_the_sentinel()
    {
        var parser = new TelemetryParser();
        Assert.Equal(ParseOutcome.Updated, parser.Parse(WireCodec.BuildTelemetry(Model())));

        var pump = parser.Readings.PumpNode;
        Assert.Equal("192.168.4.4", pump.Ip);
        Assert.Equal("3.9", pump.FirmwareVersion);
        Assert.Equal("AA:BB:CC:DD:EE:04", pump.Mac);
        Assert.Equal("v11", parser.Readings.DistanceNode.FirmwareVersion);
        Assert.Equal("v11", parser.Readings.FlowmeterNode.FirmwareVersion);
        Assert.Equal("v10", parser.Readings.AgitatorNode.FirmwareVersion);

        // Biomass is switched off: the Hub never got its hello.
        Assert.Equal(ExternalNodeIdentity.Empty, parser.Readings.BiomassNode);
        Assert.Contains("\"BiomassIP\":\"0.0.0.0\"", WireCodec.BuildTelemetry(Model()));
        Assert.DoesNotContain("BiomassNodeVer", WireCodec.BuildTelemetry(Model()));
    }

    [Fact]
    public void Node_dropout_takes_every_identity_back_to_the_sentinel()
    {
        var model = Model();
        model.Scenario = Scenario.NodeDropout;

        var frame = WireCodec.BuildTelemetry(model);

        foreach (var (_, prefix) in DeviceModel.RegistryNodes)
        {
            Assert.Contains($"\"{prefix}IP\":\"0.0.0.0\"", frame);
            Assert.DoesNotContain(prefix + "NodeVer", frame);
        }
    }

    [Fact]
    public void A_legacy_hub_emits_no_identity_key_at_all()
    {
        var model = Model();
        model.Scenario = Scenario.LegacyHub;

        var frame = WireCodec.BuildTelemetry(model);

        Assert.DoesNotContain("IP\":", frame);
        Assert.DoesNotContain("NodeVer", frame);
        var parser = new TelemetryParser();
        parser.Parse(frame);
        Assert.Equal(ExternalNodeIdentity.Empty, parser.Readings.PumpNode);
    }

    [Fact]
    public void Renumbering_moves_every_address_after_twenty_seconds()
    {
        var clock = new AcceleratedClock();
        var model = Model(clock);
        model.Scenario = Scenario.NodeRenumber;

        var before = model.NodeIp("pump");
        clock.Advance(TimeSpan.FromSeconds(21));
        var after = model.NodeIp("pump");

        Assert.Equal("192.168.4.4", before);
        Assert.Equal("192.168.4.14", after);
        Assert.Equal(DeviceModel.NodeMac("pump"), DeviceModel.NodeMac("pump")); // the board did not change
    }

    [Fact]
    public void Scenario_name_with_dashes_parses_like_the_cli_does()
        => Assert.Equal(Scenario.NodeRenumber, Enum.Parse<Scenario>("node-renumber".Replace("-", ""), ignoreCase: true));

    [Fact]
    public async Task The_nodes_route_answers_in_the_10_1_shape()
    {
        var model = Model();
        var port = FreePort();
        using var endpoint = new HttpEndpoint(model, port, _ => { });
        endpoint.Start(CancellationToken.None);
        using var client = new HubNodeDirectoryClient();

        var directory = await client.FetchAsync($"127.0.0.1:{port}");

        Assert.NotNull(directory);
        Assert.NotNull(directory!.HubTimeMs);
        Assert.Equal(6, directory.Nodes.Count);
        var pump = directory.Find("pump")!;
        Assert.Equal("192.168.4.4", pump.Identity.Ip);
        Assert.Equal("3.9", pump.Identity.FirmwareVersion);
        Assert.True(pump.Registered);
        Assert.True(pump.Online);
        var biomass = directory.Find("biomass")!;
        Assert.False(biomass.Registered);
        Assert.Equal(ExternalNodeIdentity.Empty, biomass.Identity);
        Assert.Null(biomass.SinceLastSeen(directory.HubTimeMs));

        using var http = new HttpClient();
        var one = await http.GetStringAsync($"http://127.0.0.1:{port}/nodes?dev=pump");
        Assert.Single(HubNodeDirectoryClient.Parse(one)!.Nodes);
    }

    [Fact]
    public async Task The_node_diag_route_matches_hub_10_2_and_filters_by_device()
    {
        var model = Model();
        var port = FreePort();
        using var endpoint = new HttpEndpoint(model, port, _ => { });
        endpoint.Start(CancellationToken.None);
        using var client = new HubNodeDiagClient();

        var diagnostics = await client.FetchAsync($"127.0.0.1:{port}");

        Assert.NotNull(diagnostics);
        Assert.Equal(6, diagnostics!.Nodes.Count);
        var pump = diagnostics.Find("pump")!;
        Assert.Equal(200, pump.Code);
        Assert.Equal(-58, pump.Rssi);
        Assert.Equal(210000, pump.FreeHeap);
        Assert.Contains("flow", pump.Extra.Keys);
        Assert.Equal(0, diagnostics.Find("biomass")!.Code);

        using var http = new HttpClient();
        var one = await http.GetStringAsync($"http://127.0.0.1:{port}/nodeDiag?dev=pump");
        Assert.Single(HubNodeDiagClient.Parse(one)!.Nodes);
    }

    [Fact]
    public async Task A_legacy_hub_has_no_nodes_route()
    {
        var model = Model();
        model.Scenario = Scenario.LegacyHub;
        var port = FreePort();
        using var endpoint = new HttpEndpoint(model, port, _ => { });
        endpoint.Start(CancellationToken.None);
        using var client = new HubNodeDirectoryClient();

        Assert.Null(await client.FetchAsync($"127.0.0.1:{port}"));

        using var diagClient = new HubNodeDiagClient();
        Assert.Null(await diagClient.FetchAsync($"127.0.0.1:{port}"));
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}
