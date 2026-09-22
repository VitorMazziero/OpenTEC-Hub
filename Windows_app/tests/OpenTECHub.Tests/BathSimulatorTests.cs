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
        Assert.True(WireCodec.ApplyCommand(model, "{\"tempControlMode\":1,\"bathComm\":1,\"tempSetpoint\":32.0}", out _));

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
    [InlineData(Scenario.BathOffline, "bath_offline")]
    [InlineData(Scenario.BathError, "bath_fault")]
    [InlineData(Scenario.BathGuardSuspended, "bath_guard")]
    public void External_bath_faults_are_visible_without_fabricating_a_pv(Scenario scenario, string reason)
    {
        var model = new DeviceModel(clock: new AcceleratedClock())
        {
            Scenario = scenario,
            TempControlViaBath = true,
            BathCommEnabled = true,
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
    }
}
