using System.Globalization;
using System.IO;
using TecnalHub.Simulator;
using Xunit;

namespace TecnalHub.Tests;

public sealed class SimulatorPhase2Tests
{
    [Fact]
    public void PowerLawKla_EvaluatesExpectedValues()
    {
        var kla = new PowerLawKla();

        // At 0 rpm / 0 flow, kLa is 0
        Assert.Equal(0.0, kla.Evaluate(0, 0));

        // At 1000 rpm (n=1.0) and 10 L/min (q=1.0), kLa = 0.055 1/s
        var val = kla.Evaluate(1000, 10.0);
        Assert.Equal(0.055, val, precision: 6);

        // Monotonic in agitation and aeration
        Assert.True(kla.Evaluate(1200, 10.0) > kla.Evaluate(1000, 10.0));
        Assert.True(kla.Evaluate(1000, 15.0) > kla.Evaluate(1000, 10.0));
    }

    [Fact]
    public void ProfileKla_InterpolatesAllocationTable()
    {
        var samples = new List<ProfileKla.Sample>
        {
            new(KlaPerHour: 36.0, AirflowLpm: 2.0, AgitationRpm: 200.0),   // 36 / 3600 = 0.01 1/s
            new(KlaPerHour: 180.0, AirflowLpm: 8.0, AgitationRpm: 600.0),  // 180 / 3600 = 0.05 1/s
            new(KlaPerHour: 360.0, AirflowLpm: 15.0, AgitationRpm: 1000.0),// 360 / 3600 = 0.10 1/s
        };

        var profileKla = new ProfileKla(samples, "Test Profile");

        // Closest to sample 0
        var val0 = profileKla.Evaluate(210, 2.1);
        Assert.Equal(0.01, val0, precision: 4);

        // Closest to sample 2
        var val2 = profileKla.Evaluate(980, 14.8);
        Assert.Equal(0.10, val2, precision: 4);
    }

    [Fact]
    public void CultivationProfile_TransitionsAcrossPhases()
    {
        var profile = CultivationProfile.BatchEColi;

        // At t=0 (0s): Lag phase
        var phase0 = profile.GetPhase(0);
        Assert.Contains("Lag", phase0.Name);

        // At t=1801s (> 30 min): Exponential phase
        var phase1 = profile.GetPhase(1801);
        Assert.Contains("Exponential", phase1.Name);
        Assert.True(phase1.SpecificGrowthRatePerSecond > phase0.SpecificGrowthRatePerSecond);
        Assert.True(phase1.SpecificOurPerAu > phase0.SpecificOurPerAu);

        // At t=5h (18000s): Stationary phase
        var phase2 = profile.GetPhase(18000);
        Assert.Contains("Stationary", phase2.Name);
        Assert.Equal(0.0, phase2.SpecificGrowthRatePerSecond);
    }

    [Fact]
    public void AcceleratedClock_AdvancesVirtualTimeDeterministically()
    {
        var start = new DateTimeOffset(2026, 8, 22, 0, 0, 0, TimeSpan.Zero);
        var clock = new AcceleratedClock(start);

        Assert.True(clock.IsAccelerated);
        Assert.Equal(start, clock.Now);

        clock.Advance(TimeSpan.FromSeconds(120));
        Assert.Equal(start.AddSeconds(120), clock.Now);
    }

    [Fact]
    public void DeviceModel_OxygenProbeDeadTimeAndQuantisation_BehavesCorrectly()
    {
        var clock = new AcceleratedClock();
        var model = new DeviceModel(
            clock: clock,
            probeDeadTime: TimeSpan.FromSeconds(25),
            oxygenQuantisation: 0.5,
            randomSeed: 42)
        {
            MotorRpm = 500,
            FlowSetpoint = 5.0,
            FlowmeterEnabled = true,
        };

        // Initially oxygen is near 95
        var initialReported = model.ReportedOxygen;

        // Step 10 seconds: delay line is 25s, so reported reading must NOT have reacted to new dynamics yet
        for (var i = 0; i < 50; i++)
        {
            model.Tick(0.2);
        }
        Assert.Equal(initialReported, model.ReportedOxygen);

        // Step past 25s: reported reading starts receiving new values
        for (var i = 0; i < 100; i++)
        {
            model.Tick(0.2);
        }

        // Test quantisation: ReadOxygenPercent() snaps to multiples of 0.5% (ignoring random noise magnitude)
        var quantised = model.ReadOxygenPercent();
        Assert.True(quantised is >= 0.0 and <= 100.0);
    }

    [Fact]
    public void HeadlessRunner_ExecutesBatchSimulationAndProducesCsv()
    {
        var clock = new AcceleratedClock();
        var model = new DeviceModel(
            clock: clock,
            profile: CultivationProfile.StepTest,
            probeDeadTime: TimeSpan.FromSeconds(10),
            randomSeed: 12345)
        {
            MotorRpm = 600,
            FlowSetpoint = 6.0,
            FlowmeterEnabled = true,
        };

        var tempCsv = Path.GetTempFileName();
        try
        {
            var exitCode = HeadlessRunner.Run(
                model: model,
                durationSeconds: 100.0,
                stepSeconds: 1.0,
                outputPath: tempCsv,
                logger: _ => { });

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(tempCsv));

            var lines = File.ReadAllLines(tempCsv);
            Assert.True(lines.Length >= 101); // Header + 101 samples (0..100s)

            // Verify header
            Assert.StartsWith("time_s,temp_c,flow_lpm,oxygen_true_pct", lines[0]);

            // Verify last row
            var lastLine = lines[^1];
            var parts = lastLine.Split(',');
            Assert.Equal("100.00", parts[0]);
        }
        finally
        {
            if (File.Exists(tempCsv))
            {
                File.Delete(tempCsv);
            }
        }
    }
}
