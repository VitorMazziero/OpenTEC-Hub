using OpenTECHub.Services.Telemetry;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class ChartOutlierFilterTests
{
    [Fact]
    public void A_single_frame_dropping_to_zero_rpm_is_drawn_at_the_local_median()
    {
        var values = Enumerable.Range(0, 40).Select(i => 300 + (i % 3) - 1.0).ToArray();
        values[20] = 0;

        var filtered = ChartOutlierFilter.Apply(values);

        Assert.InRange(filtered[20], 298, 302);
        Assert.Equal(values.Where((_, i) => i != 20), filtered.Where((_, i) => i != 20));
    }

    [Fact]
    public void A_real_step_and_ordinary_noise_are_preserved()
    {
        var random = new Random(7);
        var values = Enumerable.Range(0, 60).Select(i => (i < 30 ? 300.0 : 500.0) + random.NextDouble() * 6 - 3).ToArray();

        Assert.Equal(values, ChartOutlierFilter.Apply(values));
    }

    [Fact]
    public void Gaps_stay_gaps_and_a_two_sample_spike_is_removed()
    {
        var values = Enumerable.Repeat(2.0, 30).ToArray();
        values[5] = double.NaN;
        values[15] = values[16] = 9;

        var filtered = ChartOutlierFilter.Apply(values);

        Assert.True(double.IsNaN(filtered[5]));
        Assert.Equal(2, filtered[15]);
        Assert.Equal(2, filtered[16]);
    }

    [Fact]
    public void Measured_channels_are_filtered_but_commanded_and_cumulative_ones_are_not()
    {
        Assert.True(ChartsViewModel.FiltersOutliers(TelemetryChannel.ServoRpm));
        Assert.True(ChartsViewModel.FiltersOutliers(TelemetryChannel.Flow));
        Assert.False(ChartsViewModel.FiltersOutliers(TelemetryChannel.MotorRpm));
        Assert.False(ChartsViewModel.FiltersOutliers(TelemetryChannel.ServoEnergyWh));
        Assert.False(ChartsViewModel.FiltersOutliers(TelemetryChannel.Oxygen));
    }
}
