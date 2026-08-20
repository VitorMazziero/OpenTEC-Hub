using TecnalHub.Protocol;
using TecnalHub.Services.Communication;
using TecnalHub.ViewModels;
using Xunit;

namespace TecnalHub.Tests;

/// <summary>Captures what the UI would send, without a device.</summary>
internal sealed class RecordingDeviceService : IDeviceService
{
    public List<string> Sent { get; } = [];

    public int ConnectCalls { get; private set; }

    public int DisconnectCalls { get; private set; }

    public List<string> UsbConnections { get; } = [];

    public List<string> WiFiConnections { get; } = [];

    public ConnectionState State => ConnectionState.Connected;

    public TransportMedium? Medium => TransportMedium.Usb;

    public string Endpoint => "FAKE";

    public SensorSnapshot? Latest => null;

    public LinkDiagnostics Diagnostics => new();

    public event Action<ConnectionStateChange>? StateChanged;

    public event Action<SensorSnapshot>? TelemetryReceived;

    public event Action<string>? DeviceLogReceived;

    public event Action<string>? CommandSent;

    public void Send(TecnalCommand command)
    {
        var json = command.ToJson();
        Sent.Add(json);
        CommandSent?.Invoke(json);
    }

    public void Connect()
    {
        ConnectCalls++;
    }

    public void ConnectUsb(string portName)
    {
        UsbConnections.Add(portName);
    }

    public void ConnectWiFi(string ipAddress)
    {
        WiFiConnections.Add(ipAddress);
    }

    public void Disconnect()
    {
        DisconnectCalls++;
    }

    public Task<string?> DiscoverUsbPortAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    /// <summary>Silences the unused-event warnings; nothing here raises them.</summary>
    internal void Unused()
    {
        StateChanged?.Invoke(default);
        TelemetryReceived?.Invoke(new SensorSnapshot());
        DeviceLogReceived?.Invoke("");
    }
}

/// <summary>
/// Setpoint validation and the commands it produces.
/// </summary>
/// <remarks>
/// This is the layer that stands between a typo and a reactor. v.6 wrapped every
/// setpoint parse in a bare <c>except</c> that substituted a plausible default, so a
/// mistyped pH silently sent setpoint 7 and said nothing. These tests exist so that
/// cannot come back.
/// </remarks>
public class SetpointValidationTests
{
    private static (SubsystemViewModel Vm, RecordingDeviceService Device) Temperature(
        double initial = 30.0)
    {
        var device = new RecordingDeviceService();
        var variable = new ProcessVariableViewModel("temperature", "Temperatura", "°C", decimals: 1);

        var vm = new SubsystemViewModel(
            variable,
            new SubsystemSpec(15, 60, IsInteger: false,
                value => TecnalCommand.Create().Set(CommandKeys.TempSetpoint, value),
                () => TecnalCommand.Create().Set(CommandKeys.TempSetpoint, 0.0)),
            device,
            initial);

        return (vm, device);
    }

    [Fact]
    public void A_valid_entry_is_accepted()
    {
        var (vm, _) = Temperature();

        vm.SetpointText = "37.5";

        Assert.True(vm.IsValid);
        Assert.Null(vm.ValidationError);
    }

    /// <summary>The exact failure mode this whole design exists to prevent.</summary>
    [Theory]
    [InlineData("abc")]
    [InlineData("3o")]      // letter o for zero
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("--5")]
    public void Garbage_input_blocks_the_send_rather_than_substituting_a_default(string text)
    {
        var (vm, device) = Temperature();

        vm.SetpointText = text;
        vm.IsEnabled = true;
        vm.ApplyCommand.Execute(null);

        Assert.False(vm.IsValid);
        Assert.NotNull(vm.ValidationError);
        Assert.Empty(device.Sent);
    }

    [Theory]
    [InlineData("14.9")]
    [InlineData("60.1")]
    [InlineData("999")]
    public void Out_of_range_values_block_the_send(string text)
    {
        var (vm, device) = Temperature();

        vm.SetpointText = text;
        vm.IsEnabled = true;
        vm.ApplyCommand.Execute(null);

        Assert.False(vm.IsValid);
        Assert.Empty(device.Sent);
    }

    /// <summary>Zero is the device's "off" encoding, so it is in range despite the floor.</summary>
    [Fact]
    public void Zero_is_valid_even_below_the_operating_minimum()
    {
        var (vm, _) = Temperature();

        vm.SetpointText = "0";

        Assert.True(vm.IsValid);
    }

    /// <summary>
    /// A pt-BR keyboard produces a comma. It must be accepted in the UI and become a
    /// point on the wire - the two halves of the culture story meeting.
    /// </summary>
    [Fact]
    public void Decimal_comma_is_accepted_and_leaves_as_a_point()
    {
        var (vm, device) = Temperature();

        vm.SetpointText = "37,5";
        vm.IsEnabled = true;
        vm.ApplyCommand.Execute(null);

        Assert.True(vm.IsValid);
        Assert.Equal("""{"tempSetpoint":37.5}""", Assert.Single(device.Sent));
    }

    [Fact]
    public void Fahrenheit_entry_is_converted_back_to_celsius_on_the_wire()
    {
        var (vm, device) = Temperature();
        vm.SetPresentation("°F", decimals: 1, scale: 9.0 / 5.0, offset: 32.0);

        vm.SetpointText = "98.6";
        vm.IsEnabled = true;
        vm.ApplyCommand.Execute(null);

        Assert.True(vm.IsValid);
        Assert.Equal("""{"tempSetpoint":37.0}""", Assert.Single(device.Sent));
        Assert.Equal(37.0, Assert.IsType<double>(vm.AppliedSetpoint), precision: 8);
        Assert.Equal("°F", vm.Unit);
        Assert.Equal(98.6, double.Parse(
            vm.FormattedAppliedSetpoint.Replace(',', '.'),
            System.Globalization.CultureInfo.InvariantCulture), precision: 8);
    }

    [Fact]
    public void Applying_sends_the_value_and_clears_the_pending_marker()
    {
        var (vm, device) = Temperature();

        vm.SetpointText = "42";
        Assert.True(vm.HasPendingChange);

        vm.IsEnabled = true;
        vm.ApplyCommand.Execute(null);

        Assert.Equal("""{"tempSetpoint":42.0}""", Assert.Single(device.Sent));
        Assert.False(vm.HasPendingChange);
        Assert.Equal(42.0, vm.AppliedSetpoint);
    }

    /// <summary>
    /// Restoring a persisted setpoint into the field is not an operator edit. Flagging
    /// it would light "não aplicado" on every subsystem at launch, and a warning that
    /// is always on is one nobody reads.
    /// </summary>
    [Fact]
    public void Seeding_the_field_at_construction_does_not_flag_a_pending_change()
    {
        var (vm, _) = Temperature(initial: 30.0);

        Assert.False(vm.HasPendingChange);
        Assert.True(vm.IsValid);
    }

    /// <summary>
    /// Turning a subsystem off must not depend on its value field parsing - an
    /// operator stopping something should never be blocked by a typo.
    /// </summary>
    [Fact]
    public void Disabling_works_even_with_invalid_text_in_the_field()
    {
        var (vm, device) = Temperature();

        vm.SetpointText = "nonsense";
        vm.IsEnabled = false;
        vm.ApplyCommand.Execute(null);

        Assert.Equal("""{"tempSetpoint":0.0}""", Assert.Single(device.Sent));
        Assert.Equal(0, vm.AppliedSetpoint);
    }

    [Fact]
    public void Reverting_restores_the_last_applied_value()
    {
        var (vm, _) = Temperature();

        vm.SetpointText = "40";
        vm.IsEnabled = true;
        vm.ApplyCommand.Execute(null);

        vm.SetpointText = "55";
        Assert.True(vm.HasPendingChange);

        vm.RevertCommand.Execute(null);

        Assert.False(vm.HasPendingChange);
        Assert.Equal(40.0, double.Parse(vm.SetpointText.Replace(',', '.'),
            System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Agitation_rejects_a_fractional_rpm()
    {
        var device = new RecordingDeviceService();
        var variable = new ProcessVariableViewModel(
            "motor", "Agitação", "rpm", decimals: 0, isCommandedOnly: true);

        var vm = new SubsystemViewModel(
            variable,
            new SubsystemSpec(50, 1000, IsInteger: true,
                value => CommandBuilders.MotorSetpoint((int)value),
                () => CommandBuilders.MotorSetpoint(0)),
            device,
            300);

        vm.SetpointText = "300.5";
        vm.IsEnabled = true;
        vm.ApplyCommand.Execute(null);

        Assert.False(vm.IsValid);
        Assert.Empty(device.Sent);
    }

    /// <summary>
    /// Agitation has no feedback path, so applying a setpoint is the only thing that
    /// can ever give it a displayed value.
    /// </summary>
    [Fact]
    public void Applying_agitation_updates_the_commanded_reading()
    {
        var device = new RecordingDeviceService();
        var variable = new ProcessVariableViewModel(
            "motor", "Agitação", "rpm", decimals: 0, isCommandedOnly: true);

        var vm = new SubsystemViewModel(
            variable,
            new SubsystemSpec(50, 1000, IsInteger: true,
                value => CommandBuilders.MotorSetpoint((int)value),
                () => CommandBuilders.MotorSetpoint(0)),
            device,
            300);

        vm.IsEnabled = true;
        vm.SetpointText = "450";
        vm.ApplyCommand.Execute(null);

        Assert.Equal("""{"motorSetpoint":450}""", Assert.Single(device.Sent));
        Assert.Equal(450.0, variable.Value);
    }
}
