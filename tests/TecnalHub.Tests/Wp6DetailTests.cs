using TecnalHub.Protocol;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Control;
using TecnalHub.Services.Persistence;
using TecnalHub.ViewModels;
using Xunit;

namespace TecnalHub.Tests;

/// <summary>
/// WP6 part 2: the cascade trend ring behind the tuning chart, and the read-only view behind
/// the oxygen detail pane's Cascata/PID/Saída tabs.
/// </summary>
public sealed class Wp6DetailTests
{
    // ── CascadeTrend (pure ring buffer) ──────────────────────────────────────

    [Fact]
    public void An_empty_trend_snapshots_to_nothing()
    {
        var trend = new CascadeTrend();
        Assert.Equal(0, trend.Snapshot().Count);
    }

    [Fact]
    public void The_trend_times_samples_relative_to_the_first_and_keeps_pv_and_output()
    {
        var trend = new CascadeTrend();
        trend.Add(pv: 20, setpoint: 30, kla: null, output: 40, nowMinutes: 100.0);
        trend.Add(pv: 22, setpoint: 30, kla: null, output: 42, nowMinutes: 100.5);

        var s = trend.Snapshot();
        Assert.Equal(2, s.Count);
        Assert.Equal([0.0, 0.5], s.Minutes);
        Assert.Equal([20.0, 22.0], s.Pv);
        Assert.Equal([40.0, 42.0], s.Output);
        Assert.Equal(0, s.KlaCount); // no kLa samples were supplied
    }

    [Fact]
    public void The_kla_series_is_sparse_and_carries_its_own_x_coordinates()
    {
        var trend = new CascadeTrend();
        trend.Add(20, 30, kla: null, output: 40, nowMinutes: 0);   // advisory, no kLa
        trend.Add(22, 30, kla: 70, output: 42, nowMinutes: 0.5);   // engaged on path
        trend.Add(24, 30, kla: 75, output: 44, nowMinutes: 1.0);

        var s = trend.Snapshot();
        Assert.Equal(3, s.Count);
        Assert.Equal(2, s.KlaCount);
        Assert.Equal([0.5, 1.0], s.KlaMinutes);
        Assert.Equal([70.0, 75.0], s.Kla);
    }

    [Fact]
    public void Clearing_the_trend_drops_samples_and_rebases_the_clock()
    {
        var trend = new CascadeTrend();
        trend.Add(20, 30, null, 40, nowMinutes: 100);
        trend.Clear();
        trend.Add(20, 30, null, 40, nowMinutes: 250);

        var s = trend.Snapshot();
        Assert.Equal(1, s.Count);
        Assert.Equal(0.0, s.Minutes[0]); // re-based, not 150 minutes in
    }

    // ── Service records the trend while armed ────────────────────────────────

    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            Device = new RecordingDeviceService();
            Clock = new TestClock(DateTimeOffset.UnixEpoch);
            Arbiter = new CommandArbiter(Device, Clock);
            Settings = new MemorySettingsService(new AppSettings
            {
                Cascade = new CascadeSettings { OxygenSetpointPercent = 30 },
            });
            Service = new CascadeService(Arbiter, Arbiter, Settings, new FakeKlaProfileStore(), Clock);
        }

        public RecordingDeviceService Device { get; }
        public TestClock Clock { get; }
        public CommandArbiter Arbiter { get; }
        public MemorySettingsService Settings { get; }
        public CascadeService Service { get; }

        public void PushOxygen(double o, int frames = 1)
        {
            for (var i = 0; i < frames; i++)
            {
                Clock.Advance(TimeSpan.FromSeconds(2));
                Device.PushTelemetry(new SensorSnapshot { OxygenCalibrated = o });
            }
        }

        public void Dispose()
        {
            Service.Dispose();
            Arbiter.Dispose();
        }
    }

    [Fact]
    public void Arming_records_a_trend_sample_per_frame_and_disarming_clears_it()
    {
        using var h = new Harness();
        h.Service.Arm();

        h.PushOxygen(25, frames: 3);
        Assert.Equal(3, h.Service.Trend.Snapshot().Count);

        h.Service.Disarm();
        Assert.Equal(0, h.Service.Trend.Snapshot().Count);
    }

    [Fact]
    public void Engaging_on_the_path_fills_the_trend_kla_series()
    {
        using var h = new Harness();
        h.Service.SelectPath(KlaTestProfiles.Linear());
        h.Service.Engage(440, 5.0);

        h.PushOxygen(30, frames: 3);

        Assert.True(h.Service.Trend.Snapshot().KlaCount >= 3);
    }

    // ── CascadeDetailViewModel (oxygen tabs) ─────────────────────────────────

    [Fact]
    public void The_detail_view_reports_the_three_cascade_states()
    {
        using var h = new Harness();
        using var vm = new CascadeDetailViewModel(h.Service);

        Assert.False(vm.IsRunning);
        Assert.Contains("parada", vm.StateText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("—", vm.OutputText);

        h.Service.Arm();
        h.PushOxygen(25, frames: 2);
        Assert.True(vm.IsRunning);
        Assert.Contains("Consultiva", vm.StateText, StringComparison.Ordinal);
        Assert.NotEqual("—", vm.OutputText);

        h.Service.SelectPath(KlaTestProfiles.Linear());
        h.Service.Engage(440, 5.0);
        h.PushOxygen(30);
        Assert.True(vm.IsEngaged);
        Assert.Contains("Automático ativo", vm.StateText, StringComparison.Ordinal);
    }

    [Fact]
    public void The_detail_view_reflects_the_selected_mode()
    {
        using var h = new Harness();
        using var vm = new CascadeDetailViewModel(h.Service);

        h.Service.SelectMode(CascadeMode.AerationOnly);

        Assert.Equal("Somente aeração", vm.ModeText);
    }

    [Fact]
    public void The_detail_view_raises_change_notifications_on_each_frame()
    {
        using var h = new Harness();
        using var vm = new CascadeDetailViewModel(h.Service);
        var changes = 0;
        vm.PropertyChanged += (_, _) => changes++;

        h.Service.Arm();
        h.PushOxygen(25, frames: 2);

        Assert.True(changes > 0);
    }
}
