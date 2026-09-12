using Xunit;
using OpenTECHub.Protocol;

namespace OpenTECHub.Tests;

/// <summary>
/// The USB reader drains to the newest telemetry frame (v.6 semantics) but must not
/// swallow the lines that are not telemetry: the five <c>NodeDiag</c> answers to one
/// <c>{"nodeDiag":"all"}</c>, the <c>OK</c> acks and the device log lines.
/// </summary>
public class SerialLineCoalescerTests
{
    private const string Frame1 = """{"Time":1.0,"HubFirmwareVersion":"10.2.0-dev"}""";
    private const string Frame2 = """{"Time":3.0,"HubFirmwareVersion":"10.2.0-dev"}""";

    [Fact]
    public void Telemetry_frames_collapse_to_the_newest()
    {
        var c = new SerialLineCoalescer();
        c.Push(Frame1);
        c.Push(Frame2);

        Assert.Equal(Frame2, c.Take());
        Assert.Null(c.Take());
    }

    [Fact]
    public void A_nodeDiag_burst_is_delivered_line_by_line_in_order()
    {
        var c = new SerialLineCoalescer();
        var burst = new[] { "distance", "agitator", "pump", "flowmeter", "biomass" }
            .Select(d => "{\"NodeDiag\":{\"dev\":\"" + d + "\",\"code\":200,\"age_ms\":1200,\"diag\":{\"rssi\":-58}}}")
            .ToArray();

        c.Push(Frame1);
        foreach (var line in burst)
        {
            c.Push(line);
        }
        c.Push(Frame2);

        // The newest frame first (the reader wants the present), then every answer.
        Assert.Equal(Frame2, c.Take());
        foreach (var line in burst)
        {
            Assert.Equal(line, c.Take());
        }
        Assert.Null(c.Take());
    }

    [Fact]
    public void Acks_and_device_logs_are_never_dropped_by_a_later_frame()
    {
        var c = new SerialLineCoalescer();
        c.Push("OK");
        c.Push("[ESP32_AVISO]: Falha de leitura UART");
        c.Push(Frame1);
        c.Push("OK");

        Assert.Equal(Frame1, c.Take());
        Assert.Equal("OK", c.Take());
        Assert.Equal("[ESP32_AVISO]: Falha de leitura UART", c.Take());
        Assert.Equal("OK", c.Take());
        Assert.Null(c.Take());
    }

    [Fact]
    public void Side_lines_are_bounded_and_the_oldest_goes_first()
    {
        var c = new SerialLineCoalescer();
        for (var i = 0; i < 70; i++)
        {
            c.Push($"[ESP32_AVISO]: {i}");
        }

        Assert.Equal(64, c.Pending);
        Assert.Equal("[ESP32_AVISO]: 6", c.Take());
    }

    [Theory]
    [InlineData("""{"Time":1}""", true)]
    [InlineData("""{"NodeDiag":{"dev":"pump"}}""", false)]
    [InlineData("OK", false)]
    [InlineData("[ESP32_AVISO]: x", false)]
    public void Classification_follows_the_wire_shape(string line, bool isFrame)
        => Assert.Equal(isFrame, SerialLineCoalescer.IsTelemetryFrame(line));

    [Fact]
    public void Every_line_of_a_nodeDiag_burst_parses_as_NodeDiag_not_telemetry()
    {
        // The parser side of the same contract: none of the five may count as a frame
        // or as a parse failure, or the USB link-loss streak would trip on a health poll.
        var parser = new TelemetryParser();
        var c = new SerialLineCoalescer();
        foreach (var d in new[] { "distance", "agitator", "pump", "flowmeter", "biomass" })
        {
            c.Push("{\"NodeDiag\":{\"dev\":\"" + d + "\",\"code\":200,\"age_ms\":1200,\"diag\":{\"rssi\":-58,\"free_heap\":210000}}}");
        }

        var outcomes = new List<ParseOutcome>();
        while (c.Take() is { } line)
        {
            outcomes.Add(parser.Parse(line));
        }

        Assert.Equal(5, outcomes.Count);
        Assert.All(outcomes, o => Assert.Equal(ParseOutcome.NodeDiag, o));
    }
}
