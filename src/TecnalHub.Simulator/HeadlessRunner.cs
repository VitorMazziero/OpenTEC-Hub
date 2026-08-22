using System.Globalization;
using System.Text;

namespace TecnalHub.Simulator;

/// <summary>
/// Headless runner for accelerated batch simulation, controller tuning comparisons, and CSV export.
/// </summary>
public static class HeadlessRunner
{
    public static int Run(
        DeviceModel model,
        double durationSeconds,
        double stepSeconds,
        string? outputPath,
        Action<string>? logger = null)
    {
        logger ??= Console.WriteLine;

        logger($"[Headless Simulation]");
        logger($"  Duration   : {durationSeconds:F1} s ({(durationSeconds / 3600.0):F2} h)");
        logger($"  Time Step  : {stepSeconds:F3} s");
        logger($"  kLa Source : {model.KlaSource.Description}");
        logger($"  Profile    : {model.Profile.Name} ({model.Profile.Phases.Count} phases)");
        logger($"  Dead Time  : {model.OxygenProbeDeadTime.TotalSeconds:F1} s");
        logger($"  Quant.     : {(model.OxygenQuantisation > 0 ? $"{model.OxygenQuantisation:F2}%" : "none")}");
        logger($"  Output     : {outputPath ?? "stdout"}");
        logger("");

        var steps = (int)Math.Ceiling(durationSeconds / stepSeconds);
        var sb = new StringBuilder();

        // Write CSV Header
        sb.AppendLine("time_s,temp_c,flow_lpm,oxygen_true_pct,oxygen_reported_pct,ph,biomass_au,pressure_kpa,motor_rpm,flow_sp,kla_per_s,our_per_s,phase");

        var sw = System.Diagnostics.Stopwatch.StartNew();

        using var fileWriter = outputPath != null ? new StreamWriter(outputPath, append: false, Encoding.UTF8) : null;

        if (fileWriter != null)
        {
            fileWriter.WriteLine("time_s,temp_c,flow_lpm,oxygen_true_pct,oxygen_reported_pct,ph,biomass_au,pressure_kpa,motor_rpm,flow_sp,kla_per_s,our_per_s,phase");
        }

        for (var i = 0; i <= steps; i++)
        {
            var t = model.ElapsedSimulationSeconds;
            var line = string.Format(
                CultureInfo.InvariantCulture,
                "{0:F2},{1:F2},{2:F3},{3:F2},{4:F2},{5:F2},{6:F4},{7:F2},{8},{9:F1},{10:F5},{11:F5},\"{12}\"",
                t,
                model.ReadTemperature(),
                model.ReadFlow(),
                model.TrueOxygen,
                model.ReadOxygenPercent(),
                model.ReadPH(),
                model.ReadBiomass(),
                model.ReadPressure(),
                model.MotorRpm,
                model.FlowSetpoint,
                model.CurrentKLa,
                model.CurrentOur,
                model.CurrentPhase?.Name ?? "Normal");

            if (fileWriter != null)
            {
                fileWriter.WriteLine(line);
            }
            else
            {
                Console.WriteLine(line);
            }

            model.Tick(stepSeconds);
        }

        fileWriter?.Flush();
        sw.Stop();

        if (outputPath != null)
        {
            logger($"Completed {steps + 1} steps in {sw.ElapsedMilliseconds} ms ({((steps + 1) / Math.Max(1, sw.ElapsedMilliseconds / 1000.0)):F0} steps/sec).");
            logger($"Results saved to: {outputPath}");
        }

        return 0;
    }
}
