using System.IO;
using OpenTECHub.Protocol;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// Pins where the logged agitation figure comes from, ahead of RPM feedback arriving.
/// </summary>
/// <remarks>
/// <para>
/// Column 2 of the session log and <c>TelemetryChannel.MotorRpm</c> are a frozen contract
/// meaning <b>what was asked for</b>. Until the ASDA-B2 servo node existed there was no
/// RPM feedback on the wire at all, so <c>ShellViewModel</c> could read that figure off
/// the agitation tile's <c>Value</c> - for a commanded-only variable the value and the
/// setpoint are the same number by construction.
/// </para>
/// <para>
/// That equivalence is about to end. Once the servo node's measured RPM is pushed into
/// the tile, a <c>Value</c>-sourced log column would silently start carrying the
/// measurement under the command's name, and every analysis script reading those files
/// would keep working while answering a different question. These tests fix the
/// provenance now, so the change in <c>docs/PLANO_SERVO_POTENCIA_APP.md</c> section 2.3
/// cannot reintroduce it.
/// </para>
/// </remarks>
public class CommandedRpmProvenanceTests
{
    /// <summary>The agitation subsystem's real shape: 15-1000 rpm, integer, 0 disables.</summary>
    private static (SubsystemViewModel Vm, ProcessVariableViewModel Variable) Motor(
        double initial = 300.0)
    {
        // isCommandedOnly is deliberately false here: this is the shape the tile takes
        // once measured RPM arrives, and the invariant has to hold in that shape.
        var variable = new ProcessVariableViewModel(
            "motor", "Agitação", "rpm", decimals: 0,
            channel: OpenTECHub.Services.Telemetry.TelemetryChannel.MotorRpm);

        var vm = new SubsystemViewModel(
            variable,
            new SubsystemSpec(15, 1000, IsInteger: true,
                value => CommandBuilders.MotorSetpoint((int)value),
                () => CommandBuilders.MotorSetpoint(0),
                HasHealth: false),
            new RecordingDeviceService(),
            initial);

        return (vm, variable);
    }

    [Fact]
    public void The_setpoint_carries_the_command_even_when_a_measurement_disagrees()
    {
        var (vm, variable) = Motor();

        vm.IsEnabled = true;
        vm.SetpointText = "100";
        vm.ApplyCommand.Execute(null);

        // What the bench measured on 2026-09-02: 100 rpm commanded through the CN1
        // analogue chain produced 96.6 rpm at the shaft. The tile shows the measurement.
        variable.Push(96.6);

        Assert.Equal(96.6, variable.Value);
        Assert.Equal(100.0, variable.Setpoint);
        Assert.Equal(100.0, vm.AppliedSetpoint);
    }

    [Fact]
    public void Disabling_drives_the_setpoint_to_zero_regardless_of_the_measurement()
    {
        var (vm, variable) = Motor();

        vm.IsEnabled = true;
        vm.SetpointText = "600";
        vm.ApplyCommand.Execute(null);

        variable.Push(598.9);

        vm.IsEnabled = false;
        vm.ApplyCommand.Execute(null);

        // The shaft is still turning down the ramp; the command is already zero. The
        // logged figure has to follow the command, not the coasting measurement.
        Assert.Equal(0.0, variable.Setpoint);
        Assert.Equal(598.9, variable.Value);
    }

    /// <summary>
    /// The provenance itself, asserted against the source.
    /// </summary>
    /// <remarks>
    /// <c>ShellViewModel</c> takes the whole application graph, so the frame handler
    /// cannot be exercised in isolation. This repository already pins untestable wiring
    /// this way - see <c>DetailPaneContractTests</c> - and the line is worth pinning:
    /// it is one token away from being wrong, and wrong silently.
    /// </remarks>
    [Fact]
    public void The_frame_handler_logs_the_setpoint_and_never_the_tile_value()
    {
        var source = File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot, "src", "OpenTECHub", "ViewModels", "ShellViewModel.cs"));

        Assert.Contains("var commandedRpm = Motor.Setpoint ?? 0;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var commandedRpm = Motor.Value", source, StringComparison.Ordinal);

        // And that it is this figure - not the snapshot - that reaches both sinks.
        Assert.Contains("_history.Add(snapshot, commandedRpm);", source, StringComparison.Ordinal);
        Assert.Contains(
            "_sessionLogger.Write(snapshot, commandedRpm, DescribeConnection());",
            source,
            StringComparison.Ordinal);
    }
}
