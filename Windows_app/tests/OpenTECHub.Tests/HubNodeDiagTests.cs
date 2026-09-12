using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class HubNodeDiagTests
{
    private const string HttpDocument = """
        {"hub_time_ms":50000,"nodes":[
          {"dev":"pump","code":200,"age_ms":400,"diag":{"device":"peristaltic-pump","version":"3.9","uptime_s":42,"free_heap":208000,"rssi":-61,"hub_fail_streak":8,"ota":false,"flow":1.25,"vol":2.5,"mode":1}},
          {"dev":"distance","code":0,"age_ms":999999,"diag":null}
        ]}
        """;

    [Fact]
    public void Http_document_extracts_common_health_and_preserves_device_specific_metrics()
    {
        var parsed = HubNodeDiagClient.Parse(HttpDocument);

        Assert.NotNull(parsed);
        Assert.Equal(50000, parsed!.HubTimeMs);
        var pump = parsed.Find("pump")!;
        Assert.Equal(200, pump.Code);
        Assert.Equal(TimeSpan.FromMilliseconds(400), pump.Age);
        Assert.Equal(-61, pump.Rssi);
        Assert.Equal(208000, pump.FreeHeap);
        Assert.Equal(42, pump.UptimeS);
        Assert.Equal(8, pump.HubFailStreak);
        Assert.False(pump.Ota);
        Assert.Equal("1.25", pump.Extra["flow"]);
        Assert.Equal("2.5", pump.Extra["vol"]);

        var distance = parsed.Find("distance")!;
        Assert.Equal(0, distance.Code);
        Assert.Null(distance.Age);
        Assert.Null(distance.Rssi);
    }

    [Fact]
    public void Serial_envelope_parses_as_one_entry()
    {
        var parsed = HubNodeDiagClient.Parse(
            """{"NodeDiag":{"dev":"biomass","code":200,"age_ms":250,"diag":{"uptime_s":90,"free_heap":190000,"rssi":-54,"hub_fail_streak":0,"ota":true,"absorbance":0.421,"raw":24500}}}""");

        var biomass = Assert.Single(parsed!.Nodes);
        Assert.Equal("biomass", biomass.Device);
        Assert.True(biomass.Ota);
        Assert.Equal("0.421", biomass.Extra["absorbance"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("{\"nodes\":{}}")]
    public void Invalid_or_unrelated_body_returns_null(string body)
        => Assert.Null(HubNodeDiagClient.Parse(body));

    [Fact]
    public void Catalog_describes_each_nodes_specific_values()
    {
        var pump = HubNodeDiagClient.Parse(HttpDocument)!.Find("pump")!;
        var text = NodeFirmwareCatalog.DescribeDiag(NodeFirmwareCatalog.Pump, pump.Extra);

        Assert.Contains("vazão", text);
        Assert.Contains("volume", text);
        Assert.Contains("modo", text);
    }

    [Fact]
    public void Command_builder_accepts_only_the_five_nodes_or_all()
    {
        Assert.Equal("""{"nodeDiag":"all"}""", CommandBuilders.NodeDiag("all").ToJson());
        Assert.Equal("""{"nodeDiag":"pump"}""", CommandBuilders.NodeDiag("pump").ToJson());
        Assert.Throws<ArgumentOutOfRangeException>(() => CommandBuilders.NodeDiag("servo"));
    }
}
