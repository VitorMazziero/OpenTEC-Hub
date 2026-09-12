using OpenTECHub.Protocol;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// Decoding who is on the other end of each external link (Hub 10.1 node identity).
/// </summary>
/// <remarks>
/// The keys are additive and conditional: a Hub 10.0.1 sends the five <c>*IP</c> only, a
/// Hub 10.1 adds <c>*NodeVer</c>/<c>*NodeMac</c> once a node has registered, and a Hub 9
/// sends none of them. All three shapes must parse without an exception and without
/// pretending to know something the Hub did not say.
/// </remarks>
public class ExternalNodeIdentityTests
{
    /// <summary>Hub 10.1 with three registered nodes; the other two never seen.</summary>
    private const string RegisteredFrame =
        """
        {"HubFirmwareVersion":"10.1.0-dev","HubProtocolVersion":10,
         "DistanceIP":"192.168.4.2","DistanceNodeVer":"v10","DistanceNodeMac":"AA:BB:CC:DD:EE:02",
         "AgitatorIP":"0.0.0.0",
         "PumpIP":"192.168.4.3","PumpNodeVer":"3.8","PumpNodeMac":"AA:BB:CC:DD:EE:03",
         "FlowmeterIP":"192.168.4.4","FlowmeterNodeVer":"v10","FlowmeterNodeMac":"AA:BB:CC:DD:EE:04",
         "BiomassIP":"0.0.0.0","SensorCommOK":true}
        """;

    /// <summary>Hub 10.0.1: IPs only, no identity keys, one node seen through its data push.</summary>
    private const string IpOnlyFrame =
        """{"HubFirmwareVersion":"10.0.1-dev","DistanceIP":"0.0.0.0","AgitatorIP":"0.0.0.0","PumpIP":"192.168.4.3","FlowmeterIP":"0.0.0.0","BiomassIP":"0.0.0.0","SensorCommOK":true}""";

    /// <summary>A Hub that predates every one of these keys.</summary>
    private const string LegacyFrame = """{"Tempval":25.0,"SensorCommOK":true}""";

    [Fact]
    public void A_registered_node_lands_ip_version_and_mac()
    {
        var parser = new TelemetryParser();

        Assert.Equal(ParseOutcome.Updated, parser.Parse(RegisteredFrame));

        var pump = parser.Readings.PumpNode;
        Assert.Equal("192.168.4.3", pump.Ip);
        Assert.Equal("3.8", pump.FirmwareVersion);
        Assert.Equal("AA:BB:CC:DD:EE:03", pump.Mac);
        Assert.True(pump.IsKnown);
        Assert.True(pump.IsReachable);
        Assert.Equal("v10", parser.Readings.FlowmeterNode.FirmwareVersion);
        Assert.Equal("v10", parser.Readings.DistanceNode.FirmwareVersion);
    }

    [Fact]
    public void The_unassigned_address_is_unknown_not_a_string()
    {
        var parser = new TelemetryParser();
        parser.Parse(RegisteredFrame);

        Assert.Equal(ExternalNodeIdentity.Empty, parser.Readings.AgitatorNode);
        Assert.False(parser.Readings.BiomassNode.IsKnown);
    }

    [Fact]
    public void A_hub_10_0_frame_gives_ip_only_and_a_legacy_frame_gives_nothing()
    {
        var parser = new TelemetryParser();

        Assert.Equal(ParseOutcome.Updated, parser.Parse(IpOnlyFrame));
        Assert.Equal(new ExternalNodeIdentity("192.168.4.3", null, null), parser.Readings.PumpNode);

        var legacy = new TelemetryParser();
        Assert.Equal(ParseOutcome.Updated, legacy.Parse(LegacyFrame));
        Assert.All(
            new[] { legacy.Readings.DistanceNode, legacy.Readings.AgitatorNode, legacy.Readings.PumpNode, legacy.Readings.FlowmeterNode, legacy.Readings.BiomassNode },
            n => Assert.Equal(ExternalNodeIdentity.Empty, n));
    }

    [Fact]
    public void Version_and_mac_are_sticky_but_a_reset_hub_clears_the_ip()
    {
        var parser = new TelemetryParser();
        parser.Parse(RegisteredFrame);

        // The next frame carries no identity keys (e.g. a Hub that rebooted and lost its
        // registry, sending 0.0.0.0 again). The address is gone; what the node once said
        // about itself is not.
        parser.Parse(IpOnlyFrame.Replace("\"PumpIP\":\"192.168.4.3\"", "\"PumpIP\":\"0.0.0.0\""));

        var pump = parser.Readings.PumpNode;
        Assert.Null(pump.Ip);
        Assert.Equal("3.8", pump.FirmwareVersion);
        Assert.Equal("AA:BB:CC:DD:EE:03", pump.Mac);
        Assert.False(pump.IsReachable);
        Assert.True(pump.IsKnown);
    }

    [Fact]
    public void Identical_frames_keep_the_same_identity_instance()
    {
        var parser = new TelemetryParser();
        parser.Parse(RegisteredFrame);
        var first = parser.Readings.PumpNode;
        parser.Parse(RegisteredFrame);

        Assert.Same(first, parser.Readings.PumpNode);
    }

    [Fact]
    public void Keys_match_case_insensitively()
    {
        var parser = new TelemetryParser();
        parser.Parse("""{"pumpip":"192.168.4.9","pumpnodever":"3.9","PUMPNODEMAC":"01:02:03:04:05:06"}""");

        Assert.Equal(new ExternalNodeIdentity("192.168.4.9", "01:02:03:04:05:06", "3.9"), parser.Readings.PumpNode);
    }

    [Fact]
    public void Snapshot_carries_the_five_identities()
    {
        var parser = new TelemetryParser();
        parser.Parse(RegisteredFrame);

        var s = parser.Readings.Snapshot();
        Assert.Equal("192.168.4.2", s.DistanceNode.Ip);
        Assert.Equal("192.168.4.3", s.PumpNode.Ip);
        Assert.Equal("192.168.4.4", s.FlowmeterNode.Ip);
        Assert.Equal(ExternalNodeIdentity.Empty, s.AgitatorNode);
        Assert.Equal(ExternalNodeIdentity.Empty, s.BiomassNode);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("0.0.0.0", null)]
    [InlineData(" 192.168.4.5 ", "192.168.4.5")]
    public void Normalize_turns_blank_and_unassigned_into_unknown(string? wire, string? expected)
        => Assert.Equal(expected, ExternalNodeIdentity.Normalize(wire));
}

/// <summary>Parsing <c>GET /nodes</c>, tolerant of the 10.0 shape and of garbage.</summary>
public class HubNodeDirectoryClientTests
{
    private const string Hub101 =
        """
        {"hub_time_ms":48210,"nodes":[
          {"dev":"distance","ip":"192.168.4.2","mac":"AA:BB:CC:DD:EE:02","version":"v10","online":true,"age_ms":300,"registered":true,"last_hello_ms":20000,"last_data_ms":47910},
          {"dev":"agitator","ip":"0.0.0.0","mac":"","version":"","online":false,"age_ms":999999,"registered":false,"last_hello_ms":0,"last_data_ms":0},
          {"dev":"pump","ip":"192.168.4.3","mac":"AA:BB:CC:DD:EE:03","version":"3.8","online":true,"age_ms":420,"registered":true,"last_hello_ms":41000,"last_data_ms":47790}
        ]}
        """;

    private const string Hub100 =
        """{"nodes":[{"dev":"pump","ip":"192.168.4.3","mac":"AA:BB:CC:DD:EE:03","version":"3.8","online":true,"age_ms":420}]}""";

    [Fact]
    public void A_10_1_directory_parses_with_registration_and_freshness()
    {
        var dir = HubNodeDirectoryClient.Parse(Hub101);

        Assert.NotNull(dir);
        Assert.Equal(48210, dir!.HubTimeMs);
        Assert.Equal(3, dir.Nodes.Count);

        var pump = dir.Find("pump")!;
        Assert.Equal("192.168.4.3", pump.Identity.Ip);
        Assert.Equal("3.8", pump.Identity.FirmwareVersion);
        Assert.True(pump.Online);
        Assert.True(pump.Registered);
        Assert.Equal(TimeSpan.FromMilliseconds(48210 - 47790), pump.SinceLastSeen(dir.HubTimeMs));

        var agitator = dir.Find("agitator")!;
        Assert.Equal(ExternalNodeIdentity.Empty, agitator.Identity);
        Assert.False(agitator.Registered);
        Assert.Null(agitator.SinceLastSeen(dir.HubTimeMs));
    }

    [Fact]
    public void A_10_0_directory_parses_with_unknown_registration_and_the_hub_age()
    {
        var dir = HubNodeDirectoryClient.Parse(Hub100);

        Assert.NotNull(dir);
        Assert.Null(dir!.HubTimeMs);
        var pump = Assert.Single(dir.Nodes);
        Assert.Null(pump.Registered);
        Assert.Null(pump.LastHelloMs);
        Assert.Equal(TimeSpan.FromMilliseconds(420), pump.SinceLastSeen(dir.HubTimeMs));
    }

    [Theory]
    [InlineData("")]
    [InlineData("OK")]
    [InlineData("{\"Tempval\":25.0}")]
    [InlineData("{\"nodes\":\"no\"}")]
    [InlineData("{\"nodes\":[")]
    public void Anything_that_is_not_the_directory_is_null_not_an_exception(string body)
        => Assert.Null(HubNodeDirectoryClient.Parse(body));

    [Fact]
    public void Rows_without_a_device_name_are_skipped()
    {
        var dir = HubNodeDirectoryClient.Parse("""{"nodes":[{"ip":"192.168.4.3"},{"dev":"pump","ip":"192.168.4.3"}]}""");

        Assert.Equal("pump", Assert.Single(dir!.Nodes).Device);
    }
}
