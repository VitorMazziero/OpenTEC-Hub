using OpenTECHub.Protocol;
using OpenTECHub.Simulator;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class BathSimulatorTests
{
    [Fact]
    public void External_bath_runs_two_mass_model_and_completes_mailbox_command()
    {
        var model = new DeviceModel(clock: new AcceleratedClock())
        {
            BathCascadePeriodMs = 1_000,
        };
        Assert.True(WireCodec.ApplyCommand(model, "{\"tempControlMode\":1,\"bathComm\":1}", out _));
        // Hub 10.6: the setpoint of the route-change frame is discarded.
        Assert.False(model.TemperatureCommanded);
        Assert.True(WireCodec.ApplyCommand(model, "{\"tempSetpoint\":32.0}", out _));
        Assert.True(model.TemperatureCommanded);
        Assert.True(model.BathOwned);

        model.Tick(1.1);
        Assert.True(model.BathCommandPending || model.BathCommandCompletionPending);
        Assert.True(model.BathCommandId > 0);

        model.Tick(2.1);
        Assert.Equal(model.BathCommandLastSentId, model.BathCommandAck);
        Assert.Equal(model.BathCommandLastSentId, model.BathCommandLastDoneId);

        var parser = new TelemetryParser();
        Assert.Equal(ParseOutcome.Updated, parser.Parse(WireCodec.BuildTelemetry(model)));
        Assert.True(parser.Readings.HasBathTelemetry);
        Assert.True(parser.Readings.TempControlViaBath);
        Assert.Equal(model.TemperatureSetpoint, parser.Readings.TempSetpoint);
    }

    [Theory]
    [InlineData(Scenario.BathOffline, "node_offline")]
    [InlineData(Scenario.BathError, "bath_error:sp_mismatch")]
    [InlineData(Scenario.BathGuardSuspended, "guard_suspended")]
    public void External_bath_faults_are_visible_without_fabricating_a_pv(Scenario scenario, string reason)
    {
        var model = new DeviceModel(clock: new AcceleratedClock())
        {
            Scenario = scenario,
            TempControlViaBath = true,
            BathCommEnabled = true,
            TemperatureSetpoint = 30.0,
            TemperatureCommanded = true,
        };
        model.Tick(1.0);

        var parser = new TelemetryParser();
        parser.Parse(WireCodec.BuildTelemetry(model));
        Assert.True(parser.Readings.HasBathTelemetry);
        Assert.Equal(reason, parser.Readings.BathCascadePausedReason);
        if (scenario == Scenario.BathOffline)
        {
            Assert.False(parser.Readings.BathOnline);
            Assert.Null(parser.Readings.BathPv);
        }
        else
        {
            Assert.Equal("fault", parser.Readings.BathCascadeState);
            Assert.Equal(reason, parser.Readings.BathCascadeFaultReason);
        }
    }

    [Fact]
    public void Bath_stop_keeps_route_and_comm_and_leaves_node_manual()
    {
        var model = new DeviceModel(clock: new AcceleratedClock());
        WireCodec.ApplyCommand(model, "{\"tempControlMode\":1,\"bathComm\":1}", out _);
        WireCodec.ApplyCommand(model, "{\"tempSetpoint\":31.0}", out _);
        model.Tick(1.0);
        Assert.True(model.BathOwned);

        Assert.True(WireCodec.ApplyCommand(model, "{\"bathAbort\":1}", out _));
        model.Tick(1.0);

        Assert.True(model.TempControlViaBath);
        Assert.True(model.BathCommEnabled);
        Assert.False(model.BathModeAuto);
        Assert.False(model.BathOwned);
        Assert.Equal("off", model.BathCascadeState);

        // A new reactor reference resumes the cascade and re-arms the node guard.
        WireCodec.ApplyCommand(model, "{\"tempSetpoint\":31.0}", out _);
        Assert.True(model.BathModeAuto);
        Assert.True(model.BathOwned);
    }

    [Fact]
    public void Latched_fault_clears_only_with_reset()
    {
        var model = new DeviceModel(clock: new AcceleratedClock())
        {
            Scenario = Scenario.BathError,
        };
        WireCodec.ApplyCommand(model, "{\"tempControlMode\":1,\"bathComm\":1}", out _);
        WireCodec.ApplyCommand(model, "{\"tempSetpoint\":31.0}", out _);
        model.Tick(1.0);
        model.Scenario = Scenario.Normal;
        model.Tick(1.0);
        Assert.Equal("fault", model.BathCascadeState);

        WireCodec.ApplyCommand(model, "{\"bathCascadeReset\":1}", out _);
        model.Tick(1.0);
        Assert.NotEqual("fault", model.BathCascadeState);
    }

    [Fact]
    public void Invalid_tuning_is_rejected_atomically_and_echoed()
    {
        var model = new DeviceModel(clock: new AcceleratedClock());
        WireCodec.ApplyCommand(model, "{\"bathCascadeKp\":2.0,\"bathCascadeOutputMinC\":50,\"bathCascadeOutputMaxC\":40}", out _);
        Assert.Equal(0.5, model.BathCascadeKp);
        Assert.Equal("out_of_range", model.BathCascadeConfigError);

        WireCodec.ApplyCommand(model, "{\"bathCascadeKp\":2.0}", out _);
        var parser = new TelemetryParser();
        parser.Parse(WireCodec.BuildTelemetry(model));
        Assert.Equal(2.0, parser.Readings.BathCascadeConfig!.Kp);
        Assert.Equal("", parser.Readings.BathCascadeConfigError);
    }
}
