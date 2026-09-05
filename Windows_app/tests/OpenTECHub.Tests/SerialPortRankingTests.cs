using System.IO;
using OpenTECHub.Protocol;
using Xunit;

namespace OpenTECHub.Tests;

public class SerialPortRankingTests
{
    [Fact]
    public void RankCandidatePorts_orders_by_tier_and_natural_number()
    {
        var descriptors = new Dictionary<string, SerialPortDescriptor>(StringComparer.OrdinalIgnoreCase)
        {
            ["COM10"] = new("COM10", "USB-Enhanced-SERIAL CH343 (COM10)", "wch.cn", @"USB\VID_1A86&PID_55D4\12345"),
            ["COM3"] = new("COM3", "USB-Enhanced-SERIAL CH343 (COM3)", "wch.cn", @"USB\VID_1A86&PID_55D4\67890"),
            ["COM4"] = new("COM4", "Silicon Labs CP210x USB to UART Bridge (COM4)", "Silicon Labs", @"USB\VID_10C4&PID_EA60\0001"),
            ["COM20"] = new("COM20", "CH340 USB-to-Serial (COM20)", "wch.cn", @"USB\VID_1A86&PID_7523\111"),
            ["COM1"] = new("COM1", "Communications Port (COM1)", "(Standard port types)", @"ACPI\PNP0501\1"),
            ["COM9"] = new("COM9", "Standard Serial over Bluetooth (COM9)", "Microsoft", @"BTHENUM\0000"),
        };

        var input = new[] { "COM9", "COM1", "COM20", "COM4", "COM10", "COM3" };
        var ranked = SerialTransport.RankCandidatePorts(input, descriptors);

        // Tier 1 (CH343): COM3, COM10 (natural order COM3 before COM10)
        // Tier 2 (CP210 / CH340): COM4, COM20
        // Tier 3 (Generic): COM1, COM9
        var expected = new[] { "COM3", "COM10", "COM4", "COM20", "COM1", "COM9" };
        Assert.Equal(expected, ranked);
    }

    [Fact]
    public void RankCandidatePorts_natural_sorts_port_numbers_without_descriptors()
    {
        var input = new[] { "COM10", "COM2", "COM1", "COM21", "COM3" };
        var ranked = SerialTransport.RankCandidatePorts(input, descriptors: null);

        var expected = new[] { "COM1", "COM2", "COM3", "COM10", "COM21" };
        Assert.Equal(expected, ranked);
    }

    [Fact]
    public void RankCandidatePorts_deduplicates_case_insensitively()
    {
        var input = new[] { "com3", "COM3", "Com3" };
        var ranked = SerialTransport.RankCandidatePorts(input);

        Assert.Single(ranked);
        Assert.Equal("com3", ranked[0], ignoreCase: true);
    }

    [Fact]
    public void IsPortBusyException_identifies_unauthorized_and_sharing_violations()
    {
        var unauthorized = new UnauthorizedAccessException("Access to the port 'COM3' is denied.");
        var sharingViolation = new IOException("The process cannot access the file because it is being used by another process.", unchecked((int)0x80070020));
        var accessDeniedIo = new IOException("Access denied", unchecked((int)0x80070005));
        var unrelatedIo = new IOException("General IO failure", unchecked((int)0x80004005));
        var argumentEx = new ArgumentException("Invalid port name");

        Assert.True(SerialTransport.IsPortBusyException(unauthorized));
        Assert.True(SerialTransport.IsPortBusyException(sharingViolation));
        Assert.True(SerialTransport.IsPortBusyException(accessDeniedIo));
        Assert.False(SerialTransport.IsPortBusyException(unrelatedIo));
        Assert.False(SerialTransport.IsPortBusyException(argumentEx));
        Assert.False(SerialTransport.IsPortBusyException(null));
    }

    [Fact]
    public void PortBusyException_retains_port_name_and_friendly_message()
    {
        var inner = new UnauthorizedAccessException("Access denied");
        var ex = new PortBusyException("COM5", inner);

        Assert.Equal("COM5", ex.PortName);
        Assert.Same(inner, ex.InnerException);
        Assert.Contains("COM5", ex.Message);
        Assert.Contains("ocupada por outra aplicação", ex.Message);
    }
}
