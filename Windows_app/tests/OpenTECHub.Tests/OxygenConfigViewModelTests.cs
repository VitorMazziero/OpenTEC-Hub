using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Control;
using OpenTECHub.Services.KlaMapping;
using OpenTECHub.Services.Persistence;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

public class OxygenConfigViewModelTests
{
    private sealed class Fixture : IDisposable
    {
        public Fixture(AppSettings? settings = null)
        {
            Device = new RecordingDeviceService();
            Settings = new MemorySettingsService(settings ?? new AppSettings());
            Clock = new TestClock(DateTimeOffset.UnixEpoch);
            Store = new FakeKlaProfileStore();
            var arbiter = new CommandArbiter(Device, Clock);
            Service = new CascadeService(arbiter, arbiter, Settings, Store, Clock);
            Service.SelectMode(CascadeMode.DualCascade);
            ViewModel = new OxygenConfigViewModel(Service, Settings, Store);
        }

        public RecordingDeviceService Device { get; }
        public MemorySettingsService Settings { get; }
        public TestClock Clock { get; }
        public FakeKlaProfileStore Store { get; }
        public CascadeService Service { get; }
        public OxygenConfigViewModel ViewModel { get; }

        public void Dispose()
        {
            Service.Dispose();
        }
    }

    [Fact]
    public void Loads_current_settings_for_selected_mode()
    {
        using var fixture = new Fixture();
        var vm = fixture.ViewModel;

        Assert.Equal(CascadeMode.DualCascade, vm.SelectedMode.Mode);
        Assert.True(vm.ShowAgitationLimits);
        Assert.True(vm.ShowAerationLimits);
        Assert.True(vm.ShowEffortWindows);
        Assert.True(vm.ShowAdvancedGains);
        Assert.False(vm.ShowKlaPathSelector);

        Assert.Equal("0.070", vm.KDotText);
        Assert.Equal("0.065", vm.KpText);
        Assert.Equal("0.0010", vm.KiText);
        Assert.Equal("0.500", vm.KdText);
        Assert.Equal("60.0", vm.TPredText);
    }

    [Fact]
    public void Loads_published_kla_paths_for_the_map_selector()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        var store = new FakeKlaProfileStore();
        var published = KlaTestProfiles.Linear("Trajetória publicada");
        store.Published.Add(published);
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var arbiter = new CommandArbiter(device, clock);
        using var service = new CascadeService(arbiter, arbiter, settings, store, clock);
        using var vm = new OxygenConfigViewModel(service, settings, store);

        Assert.Equal(published, Assert.Single(vm.AvailablePaths));
        Assert.Equal(published, vm.SelectedPath);
    }

    [Fact]
    public void Switching_mode_updates_visibility_and_loads_mode_specific_pids()
    {
        using var fixture = new Fixture();
        var vm = fixture.ViewModel;

        // Switch to Agitação only
        vm.SelectedMode = vm.Modes.First(m => m.Mode == CascadeMode.AgitationOnly);
        Assert.True(vm.ShowAgitationLimits);
        Assert.False(vm.ShowAerationLimits);
        Assert.False(vm.ShowEffortWindows);
        Assert.False(vm.ShowAdvancedGains);
        Assert.False(vm.ShowKlaPathSelector);

        // Switch to Aeração only
        vm.SelectedMode = vm.Modes.First(m => m.Mode == CascadeMode.AerationOnly);
        Assert.False(vm.ShowAgitationLimits);
        Assert.True(vm.ShowAerationLimits);
        Assert.False(vm.ShowEffortWindows);
        Assert.False(vm.ShowAdvancedGains);
        Assert.False(vm.ShowKlaPathSelector);

        // Switch to Mapa
        vm.SelectedMode = vm.Modes.First(m => m.Mode == CascadeMode.KlaPath);
        Assert.False(vm.ShowAgitationLimits);
        Assert.False(vm.ShowAerationLimits);
        Assert.False(vm.ShowEffortWindows);
        Assert.False(vm.ShowAdvancedGains);
        Assert.True(vm.ShowKlaPathSelector);
    }

    [Fact]
    public void Intermediate_edits_are_preserved_across_mode_switches()
    {
        using var fixture = new Fixture();
        var vm = fixture.ViewModel;

        // Select Agitacao and customize Kp
        vm.SelectedMode = vm.Modes.First(m => m.Mode == CascadeMode.AgitationOnly);
        vm.KpText = "0.999";

        // Switch to Aeracao and change Ki
        vm.SelectedMode = vm.Modes.First(m => m.Mode == CascadeMode.AerationOnly);
        vm.KiText = "0.0888";

        // Switch back to Agitacao - Kp must be 0.999
        vm.SelectedMode = vm.Modes.First(m => m.Mode == CascadeMode.AgitationOnly);
        Assert.Equal("0.999", vm.KpText);

        // Switch back to Aeracao - Ki must be 0.0888
        vm.SelectedMode = vm.Modes.First(m => m.Mode == CascadeMode.AerationOnly);
        Assert.Equal("0.0888", vm.KiText);
    }

    [Fact]
    public void Apply_persists_settings_and_updates_service()
    {
        using var fixture = new Fixture();
        var vm = fixture.ViewModel;

        vm.SelectedMode = vm.Modes.First(m => m.Mode == CascadeMode.DualCascade);
        vm.KpText = "0.123";
        vm.AgitationMinRpmText = "200";
        vm.AgitationMaxRpmText = "600";
        vm.AerationMinLpmText = "1.0";
        vm.AerationMaxLpmText = "8.0";

        var closed = false;
        vm.CloseRequested = () => closed = true;

        vm.ApplyCommand.Execute(null);

        Assert.True(closed);
        Assert.True(vm.DialogResult);

        var saved = fixture.Settings.Current.Cascade;
        Assert.Equal(0.123, saved.CascadePid.Kp, precision: 4);
        Assert.Equal(200.0, saved.AgitationMinRpm);
        Assert.Equal(600.0, saved.AgitationMaxRpm);
        Assert.Equal(1.0, saved.AerationMinLpm);
        Assert.Equal(8.0, saved.AerationMaxLpm);

        Assert.Equal(CascadeMode.DualCascade, fixture.Service.Mode);
        Assert.Equal(0.123, fixture.Service.Tuning.Kp, precision: 4);
    }

    [Fact]
    public void Apply_persists_the_dissolved_oxygen_setpoint()
    {
        using var fixture = new Fixture();
        var vm = fixture.ViewModel;

        vm.OxygenSetpointText = "45";

        vm.ApplyCommand.Execute(null);

        Assert.Equal(45.0, fixture.Settings.Current.Cascade.OxygenSetpointPercent, precision: 3);
        Assert.Equal(45.0, fixture.Service.OxygenSetpoint, precision: 3);
    }

    [Fact]
    public void Apply_rejects_an_out_of_range_setpoint()
    {
        using var fixture = new Fixture();
        var vm = fixture.ViewModel;

        vm.OxygenSetpointText = "150";

        var closed = false;
        vm.CloseRequested = () => closed = true;

        vm.ApplyCommand.Execute(null);

        Assert.False(closed);
        Assert.True(vm.HasValidationError);
        Assert.Contains("oxigênio dissolvido", vm.ValidationError);
    }

    [Fact]
    public void Apply_re_engages_with_the_new_parameters_when_already_engaged()
    {
        using var fixture = new Fixture();
        var vm = fixture.ViewModel;

        fixture.Service.Engage(300, 3.0);
        Assert.True(fixture.Service.IsEngaged);

        vm.KpText = "0.321";
        vm.ApplyCommand.Execute(null);

        Assert.True(vm.DialogResult);
        Assert.True(fixture.Service.IsEngaged);
        Assert.Equal(0.321, fixture.Service.Tuning.Kp, precision: 3);
    }

    [Fact]
    public void Apply_changes_the_mode_live_when_already_engaged()
    {
        using var fixture = new Fixture();
        var vm = fixture.ViewModel;

        fixture.Service.Engage(300, 3.0);
        Assert.Equal(CascadeMode.DualCascade, fixture.Service.Mode);

        vm.SelectedMode = vm.Modes.First(m => m.Mode == CascadeMode.AgitationOnly);
        vm.ApplyCommand.Execute(null);

        Assert.True(vm.DialogResult);
        Assert.True(fixture.Service.IsEngaged);
        Assert.Equal(CascadeMode.AgitationOnly, fixture.Service.Mode);
    }

    [Fact]
    public void Cancel_does_not_persist_or_update_service()
    {
        using var fixture = new Fixture();
        var vm = fixture.ViewModel;

        var originalKp = fixture.Settings.Current.Cascade.CascadePid.Kp;
        vm.KpText = "999.0";

        var closed = false;
        vm.CloseRequested = () => closed = true;

        vm.CancelCommand.Execute(null);

        Assert.True(closed);
        Assert.False(vm.DialogResult);
        Assert.Equal(originalKp, fixture.Settings.Current.Cascade.CascadePid.Kp);
    }

    [Fact]
    public void Apply_rejects_invalid_actuator_limits()
    {
        using var fixture = new Fixture();
        var vm = fixture.ViewModel;

        vm.AgitationMinRpmText = "500";
        vm.AgitationMaxRpmText = "300"; // Max < Min

        var closed = false;
        vm.CloseRequested = () => closed = true;

        vm.ApplyCommand.Execute(null);

        Assert.False(closed);
        Assert.True(vm.HasValidationError);
        Assert.Contains("estritamente maior", vm.ValidationError);
    }

    [Fact]
    public void Apply_rejects_invalid_effort_windows()
    {
        using var fixture = new Fixture();
        var vm = fixture.ViewModel;

        vm.SelectedMode = vm.Modes.First(m => m.Mode == CascadeMode.DualCascade);
        vm.AgitationEffortStartText = "50";
        vm.AgitationEffortEndText = "30"; // End < Start

        var closed = false;
        vm.CloseRequested = () => closed = true;

        vm.ApplyCommand.Execute(null);

        Assert.False(closed);
        Assert.True(vm.HasValidationError);
        Assert.Contains("janela de esforço", vm.ValidationError);
    }

    [Fact]
    public void Apply_accepts_comma_decimal_inputs()
    {
        using var fixture = new Fixture();
        var vm = fixture.ViewModel;

        vm.KpText = "0,145";
        vm.AerationMinLpmText = "1,25";
        vm.AerationMaxLpmText = "6,50";

        var closed = false;
        vm.CloseRequested = () => closed = true;

        vm.ApplyCommand.Execute(null);

        Assert.True(closed);
        Assert.False(vm.HasValidationError);
        var saved = fixture.Settings.Current.Cascade;
        Assert.Equal(0.145, saved.CascadePid.Kp, precision: 4);
        Assert.Equal(1.25, saved.AerationMinLpm, precision: 2);
        Assert.Equal(6.50, saved.AerationMaxLpm, precision: 2);
    }
}
