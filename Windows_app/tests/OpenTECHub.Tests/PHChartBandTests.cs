using OpenTECHub.Protocol;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Telemetry;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class PHChartBandTests
{
    [Fact]
    public void Band_limits_keep_each_samples_applied_settings_and_leave_gaps_when_disabled()
    {
        var history = new TelemetryHistory(capacity: 4);
        history.Add(new SensorSnapshot { TimeMinutes = 1, PHCalibrated = 6.2 }, 50);
        history.RecordSetpoint(TelemetryChannel.PH, 6.25);
        history.RecordSetpoint(TelemetryChannel.PHLowerLimit, 6.1);
        history.RecordSetpoint(TelemetryChannel.PHUpperLimit, 6.4);
        history.Add(new SensorSnapshot { TimeMinutes = 2, PHCalibrated = 6.2 }, 330);
        history.RecordSetpoint(TelemetryChannel.PHLowerLimit, 6.05);
        history.RecordSetpoint(TelemetryChannel.PHUpperLimit, 6.45);
        history.Add(new SensorSnapshot { TimeMinutes = 3, PHCalibrated = 6.2 }, 350);

        var charts = new ChartsViewModel(history, new MemorySettingsService());
        var ph = Assert.Single(charts.Channels, c => c.Channel == TelemetryChannel.PH);
        var band = charts.GetPHBandSeries(ph, 100);
        Assert.Equal(new double[] { 1, 2, 3 }, band.Lower.Minutes);
        Assert.Equal(6.1, band.Lower.Values[0]);
        Assert.Equal(6.05, band.Lower.Values[1]);
        Assert.Equal(6.4, band.Upper.Values[0]);
        Assert.Equal(6.45, band.Upper.Values[1]);
        Assert.True(double.IsNaN(band.Lower.Values[2]));
        Assert.True(double.IsNaN(band.Upper.Values[2]));
        var rpm = Assert.Single(charts.Channels, c => c.Channel == TelemetryChannel.ServoRpm);
        Assert.Equal(0, charts.GetPHBandSeries(rpm, 100).Lower.Count);
        Assert.Equal(new double[] { 50, 330, 350 }, charts.GetSetpointSeries(rpm, 100).Values);
    }

    [Fact]
    public void Editing_the_pH_band_does_not_change_applied_limits_until_apply()
    {
        var device = new RecordingDeviceService();
        using var ph = new PHControlViewModel(device, new MemorySettingsService());
        var applied = ph.AppliedInactiveBand;
        ph.InactiveBandText = "0.30";
        Assert.Equal(applied, ph.AppliedInactiveBand);
        ph.IsEnabled = true;
        ph.ApplyCommand.Execute(null);
        Assert.Equal(0.30, ph.AppliedInactiveBand);
    }
}
