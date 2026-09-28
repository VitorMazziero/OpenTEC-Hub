using OpenTECHub.Protocol;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class BathProtocolTests
{
    [Fact]
    public void Bath_commands_use_the_frozen_wire_contract()
    {
        Assert.Equal("{\"tempControlMode\":1}", CommandBuilders.TemperatureRoute(true).ToJson());
        Assert.Equal("{\"bathComm\":1}", CommandBuilders.BathCommunication(true).ToJson());
        Assert.Equal("{\"bathMode\":\"auto\"}", CommandBuilders.BathMode(true).ToJson());
        Assert.Equal("{\"bathSync\":30.0}", CommandBuilders.BathSynchronize(30).ToJson());
        Assert.Equal("{\"bathAbort\":1}", CommandBuilders.BathAbort().ToJson());
        Assert.Equal("{\"bathCascadeReset\":1}", CommandBuilders.BathCascadeReset().ToJson());
    }

    [Fact]
    public void Bath_telemetry_preserves_nullable_values_and_real_reactor_pv()
    {
        var parser = new TelemetryParser();
        var result = parser.Parse("""{"BathOnline":true,"BathCommEnabled":true,"TempControlViaBath":true,"Tempval":29.4,"TempSetpoint":30.0,"BathSp":30.1,"BathPv":30.0,"BathCascadeState":"controlling","BathCascadeError":0.6,"BathCascadeSaturated":false,"BathIP":"192.168.4.22","BathNodeVer":"r3.1","BathNodeMac":"AA:BB"}""");

        Assert.Equal(ParseOutcome.Updated, result);
        Assert.True(parser.Readings.HasBathTelemetry);
        Assert.True(parser.Readings.BathOnline);
        Assert.True(parser.Readings.TempControlViaBath);
        Assert.Equal(29.4, parser.Readings.Temperature);
        Assert.Equal(30.0, parser.Readings.TempSetpoint);
        Assert.Equal(30.0, parser.Readings.BathPv);
        Assert.Equal("192.168.4.22", parser.Readings.BathNode.Ip);
        Assert.Equal("r3.1", parser.Readings.BathNode.FirmwareVersion);

        parser.Parse("""{"BathOnline":true,"BathPv":null,"BathCascadeError":null}""");
        Assert.Null(parser.Readings.BathPv);
        Assert.Null(parser.Readings.BathCascadeError);
    }

    [Fact]
    public void Bath_fine_gate_telemetry_is_parsed_and_absent_on_older_hubs()
    {
        var parser = new TelemetryParser();
        parser.Parse("""{"BathOnline":true,"BathCascadeState":"approaching","BathCascadeFine":false,"BathCascadeSlopeCMin":0.412}""");
        Assert.Equal("approaching", parser.Readings.BathCascadeState);
        Assert.False(parser.Readings.BathCascadeFine);
        Assert.Equal(0.412, parser.Readings.BathCascadeSlopeCMin);

        parser.Parse("""{"BathOnline":true,"BathCascadeFine":true,"BathCascadeSlopeCMin":null}""");
        Assert.True(parser.Readings.BathCascadeFine);
        Assert.Null(parser.Readings.BathCascadeSlopeCMin);

        var older = new TelemetryParser();
        older.Parse("""{"BathOnline":true,"BathCascadeState":"controlling"}""");
        Assert.Null(older.Readings.BathCascadeFine);
    }

    [Fact]
    public void Bath_keys_are_owned_by_the_temperature_actuator()
    {
        var command = CommandBuilders.BathCascadeTuning(kp: 0.5, periodMs: 10000);
        Assert.Contains(ActuatorId.Temperature, CommandActuators.ActuatorsIn(command));
        Assert.Equal(ActuatorId.Temperature, CommandActuators.ForKey(CommandKeys.BathComm));
    }
}
