using TecnalHub.Protocol;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Control;
using TecnalHub.Services.Persistence;
using TecnalHub.ViewModels;
using Xunit;

namespace TecnalHub.Tests;

/// <summary>The Cascata e sintonia workspace: staging, validation, apply/revert, save/load.</summary>
public class CascadeTuningViewModelTests
{
    private sealed class Fixture : IDisposable
    {
        public Fixture(AppSettings? settings = null)
        {
            Device = new RecordingDeviceService();
            Settings = new MemorySettingsService(settings ?? new AppSettings());
            Clock = new TestClock(DateTimeOffset.UnixEpoch);
            var arbiter = new CommandArbiter(Device, Clock);
            Service = new CascadeService(arbiter, arbiter, Settings, new FakeKlaProfileStore(), Clock);
            ViewModel = new CascadeTuningViewModel(Service, Settings);
        }

        public RecordingDeviceService Device { get; }
        public MemorySettingsService Settings { get; }
        public TestClock Clock { get; }
        public CascadeService Service { get; }
        public CascadeTuningViewModel ViewModel { get; }

        public void PushOxygen(double oxygen, int frames)
        {
            for (var i = 0; i < frames; i++)
            {
                Clock.Advance(TimeSpan.FromSeconds(2));
                Device.PushTelemetry(new SensorSnapshot { OxygenCalibrated = oxygen });
            }
        }

        public void Dispose()
        {
            ViewModel.Dispose();
            Service.Dispose();
        }
    }

    [Fact]
    public void Defaults_load_from_settings_and_are_valid()
    {
        using var fixture = new Fixture();

        Assert.Equal(0.25, fixture.ViewModel.Kp);
        Assert.Null(fixture.ViewModel.ValidationError);
        Assert.True(fixture.ViewModel.CanApply);
        Assert.False(fixture.ViewModel.HasPendingChange);
    }

    [Fact]
    public void An_interval_outside_the_firmware_band_blocks_apply()
    {
        using var fixture = new Fixture();

        fixture.ViewModel.IntervalSeconds = 0.02;

        Assert.NotNull(fixture.ViewModel.ValidationError);
        Assert.False(fixture.ViewModel.CanApply);
        Assert.True(fixture.ViewModel.HasPendingChange);
    }

    [Fact]
    public void Editing_a_gain_marks_the_workspace_dirty()
    {
        using var fixture = new Fixture();

        fixture.ViewModel.Kp = 0.5;

        Assert.True(fixture.ViewModel.HasPendingChange);
    }

    [Fact]
    public void Apply_configures_the_service_persists_and_never_sends()
    {
        using var fixture = new Fixture();
        fixture.ViewModel.Kp = 0.4;
        fixture.ViewModel.OxygenSetpoint = 35;

        fixture.ViewModel.ApplyCommand.Execute(null);

        Assert.Equal(0.4, fixture.Service.Tuning.Kp);
        Assert.Equal(35, fixture.Service.OxygenSetpoint);
        Assert.Equal(0.4, fixture.Settings.Current.Cascade.Kp);
        Assert.False(fixture.ViewModel.HasPendingChange);
        Assert.Empty(fixture.Device.Sent);
    }

    [Fact]
    public void Revert_restores_the_applied_values()
    {
        using var fixture = new Fixture();
        fixture.ViewModel.Kp = 0.9;

        fixture.ViewModel.RevertCommand.Execute(null);

        Assert.Equal(0.25, fixture.ViewModel.Kp);
        Assert.False(fixture.ViewModel.HasPendingChange);
    }

    [Fact]
    public void Saving_a_tuning_persists_it_and_loading_only_stages()
    {
        using var fixture = new Fixture();
        fixture.ViewModel.Kp = 0.6;
        fixture.ViewModel.PresetName = "Ensaio rápido";

        fixture.ViewModel.SaveTuningCommand.Execute(null);

        var saved = Assert.Single(fixture.Settings.Current.CascadeTuningPresets);
        Assert.Equal("Ensaio rápido", saved.Name);
        Assert.Equal(0.6, saved.Settings.Kp);

        // Move away, then load: the fields stage but nothing applies or sends.
        fixture.ViewModel.Kp = 0.1;
        fixture.ViewModel.ApplyCommand.Execute(null);
        fixture.ViewModel.LoadTuningCommand.Execute(null);

        Assert.Equal(0.6, fixture.ViewModel.Kp);
        Assert.True(fixture.ViewModel.HasPendingChange);
        Assert.Equal(0.1, fixture.Service.Tuning.Kp); // still the applied value, not the loaded one
        Assert.Empty(fixture.Device.Sent);
    }

    [Fact]
    public void Toggling_arm_arms_and_disarms_the_service()
    {
        using var fixture = new Fixture();

        fixture.ViewModel.ToggleArmCommand.Execute(null);
        Assert.True(fixture.Service.IsArmed);
        Assert.True(fixture.ViewModel.IsArmed);

        fixture.ViewModel.ToggleArmCommand.Execute(null);
        Assert.False(fixture.Service.IsArmed);
        Assert.False(fixture.ViewModel.IsArmed);
    }

    [Fact]
    public void Live_terms_populate_once_armed_and_telemetry_flows()
    {
        using var fixture = new Fixture();
        fixture.ViewModel.ToggleArmCommand.Execute(null);

        fixture.PushOxygen(oxygen: 12, frames: 10);

        Assert.Equal("12,0 %".Replace(',', '.'), fixture.ViewModel.LiveOxygen.Replace(',', '.'));
        Assert.NotEqual("—", fixture.ViewModel.LiveOutput);
        Assert.NotEqual("—", fixture.ViewModel.LiveAgitation);
    }
}
