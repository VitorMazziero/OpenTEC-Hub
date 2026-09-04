using System.IO;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTECHub.Protocol;
using OpenTECHub.Services.KlaTesting;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class KlaPlaybackDeviceServiceTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"kla-playback-{Guid.NewGuid():N}.txt");

    public KlaPlaybackDeviceServiceTests()
    {
        File.WriteAllText(_file,
            "Time (min)\tTemperature (°C)\tMotor (rpm)\tOxygen\n" +
            "1.00\t25.0\t700\t9.0\n" +
            "1.03\t25.0\t700\t8.0\n" +
            "1.06\t25.0\t700\t6.0\n" +
            "1.09\t25.0\t50\t4.5\n" +
            "1.12\t25.0\t50\t5.0\n" +
            "1.15\t25.0\t50\t8.0\n",
            Encoding.UTF8);
    }

    [Fact]
    public void Connect_And_FlowCommand_Expose_ExperimentalDo_And_SyntheticPendingAckState()
    {
        using var service = new KlaPlaybackDeviceService(
            new KlaPlaybackOptions(_file, 10),
            NullLogger<KlaPlaybackDeviceService>.Instance);

        string? sent = null;
        service.CommandSent += json => sent = json;
        service.Connect();

        Assert.Equal(ConnectionState.Connected, service.State);
        Assert.Equal(TransportMedium.Simulation, service.Medium);
        Assert.Equal(9.0, service.Latest?.OxygenCalibrated);
        Assert.True(service.Latest?.FlowmeterOnline == true);

        service.Send(CommandBuilders.FlowSetpoint(0, 12, valve1: true, valve2: false));

        Assert.NotNull(sent);
        Assert.Equal(1, service.Latest?.FlowCommandId);
        Assert.True(service.Latest?.FlowCommandPending == true);
        Assert.Equal(1, service.Latest?.FlowValve1);
        Assert.Equal(0, service.Latest?.FlowValve2);
        Assert.Equal(0, service.Latest?.FlowSetpoint);
    }

    public void Dispose()
    {
        if (File.Exists(_file))
        {
            File.Delete(_file);
        }
    }
}
