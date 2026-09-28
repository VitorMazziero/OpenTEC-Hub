using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Telemetry;
using OpenTECHub.Simulator;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class BathIntegrationTests
{
    [Fact]
    public void History_exposes_the_bath_pv_filtered_pv_and_calculated_output()
    {
        var history = new TelemetryHistory(capacity: 8);
        history.Add(new SensorSnapshot
        {
            TimeMinutes = 1,
            HasBathTelemetry = true,
            BathPv = 41.2,
            BathCascadePvFiltered = 40.8,
            BathCommandSetpoint = 63.0,
        }, commandedRpm: 0);

        Assert.Equal(41.2, history.GetSeries(TelemetryChannel.BathPv, null, 8).Values[0]);
        Assert.Equal(40.8, history.GetSeries(TelemetryChannel.BathCascadePvFiltered, null, 8).Values[0]);
        Assert.Equal(63.0, history.GetSeries(TelemetryChannel.BathCommandSetpoint, null, 8).Values[0]);
    }

    [Fact]
    public void Bath_sidecar_row_keeps_tempval_and_route_distinct()
    {
        var row = SessionLogger.BuildBathRow(new SensorSnapshot
        {
            TimeMinutes = 2.5,
            HasBathTelemetry = true,
            Temperature = 36.7,
            BathCascadePvFiltered = 36.4,
            BathCascadeError = 0.3,
            BathCascadeP = 0.2,
            BathCascadeI = 0.1,
            BathCommandSetpoint = 55,
            BathCommandConfirmed = 54.9,
            BathPv = 38.1,
            BathSp = 38.0,
            BathTarget = 38.0,
            BathMode = 1,
            BathGuard = "ok",
            BathCascadeSaturated = false,
            BathCascadeState = "controlling",
            BathCascadePausedReason = "",
            TempControlViaBath = true,
            BathCascadeFine = true,
            BathCascadeSlopeCMin = 0.042,
        });

        var fields = row.Split('\t');
        Assert.Equal(19, fields.Length);
        Assert.Equal(BathSessionLogFormat.Header.Split('\t').Length, fields.Length);
        Assert.Equal("36.70", fields[1]);
        Assert.Equal("external", fields[16]);
        Assert.Equal("1", fields[17]);
        Assert.Equal("0.042", fields[18]);
    }

    [Fact]
    public void Bath_is_a_first_class_catalog_and_simulator_node()
    {
        Assert.Contains(NodeFirmwareCatalog.Bath, NodeFirmwareCatalog.Devices);
        Assert.Contains(DeviceModel.RegistryNodes, node => node.Device == NodeFirmwareCatalog.Bath);
        Assert.Equal("r3.2", DeviceModel.NodeVersion(NodeFirmwareCatalog.Bath));
    }

    [Fact]
    public void Bath_sidecar_is_created_only_after_real_bath_telemetry()
    {
        var directory = Path.Combine(Path.GetTempPath(), "opentec-bath-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var session = Path.Combine(directory, "session.txt");
        var sidecar = SessionLogger.BathSidecarPath(session);
        try
        {
            var logger = new SessionLogger(NullLogger<SessionLogger>.Instance);
            logger.Start(session);
            logger.Write(new SensorSnapshot { TimeMinutes = 1, Temperature = 30 }, 0, "USB");
            logger.Stop();
            Assert.False(File.Exists(sidecar));

            logger.Start(session);
            logger.Write(new SensorSnapshot
            {
                TimeMinutes = 2,
                Temperature = 30,
                HasBathTelemetry = true,
                BathOnline = true,
            }, 0, "USB");
            logger.Stop();

            Assert.True(File.Exists(sidecar));
            Assert.True(SessionFileService.Inspect(session).HasBathSidecar);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Bath_row_does_not_leak_sticky_values_without_bath_telemetry()
    {
        var fields = SessionLogger.BuildBathRow(new SensorSnapshot
        {
            TimeMinutes = 3,
            Temperature = 31,
            BathPv = 40,
            BathCascadeState = "controlling",
            BathGuard = "ok",
            TempControlViaBath = true,
        }).Split('\t');

        Assert.Equal("3.00", fields[0]);
        Assert.All(fields.Skip(1), Assert.Empty);
    }
}
