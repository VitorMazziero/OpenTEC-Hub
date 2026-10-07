using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using OpenTECHub.Services.KlaTesting;

/// <summary>Fixed synthetic truth, no tuning or filtering after seeing the estimates.</summary>
internal static class NoiseAudit
{
    private const double TrueKla = 72;
    private const double TrueOur = 1800;

    public static void Run(string output)
    {
        var reports = new List<object>();
        foreach (var biotic in new[] { false, true })
        foreach (var noise in new[] { "white-0.1", "white-0.5", "correlated-0.2", "outliers-0.2" })
        {
            var trials = new List<Trial>();
            for (var index = 0; index < 20; index++)
            {
                var seed = 20261007 + index;
                var request = Request(biotic, noise, seed);
                var result = KlaDeterministicAnalysis.Analyze(request);
                var trial = new Trial(seed, result.KlaQuality.ToString(), result.KlaPerHour,
                    result.ConditionalCi95Low, result.ConditionalCi95High,
                    result.Equilibrium.Percent, result.Our.Quality.ToString(), result.Our.PercentPointsPerHour,
                    result.Our.ConditionalCi95Low, result.Our.ConditionalCi95High,
                    result.Reasons.ToArray(), result.Our.Reasons.ToArray(), result.SelectedWindow?.Window);
                trials.Add(trial);
            }
            var accepted = trials.Where(t => t.KlaPerHour.HasValue).ToArray();
            var our = trials.Where(t => t.OurPercentPointsPerHour.HasValue).ToArray();
            var covered = accepted.Where(t => t.CiLow.HasValue && t.CiHigh.HasValue).ToArray();
            var ourCovered = our.Where(t => t.OurCiLow.HasValue && t.OurCiHigh.HasValue).ToArray();
            reports.Add(new
            {
                Protocol = biotic ? "biotic" : "abiotic", Noise = noise, Trials = trials.Count,
                EstimatedKla = accepted.Length, RefusedKla = trials.Count - accepted.Length,
                KlaQualityCounts = trials.GroupBy(t => t.Quality).ToDictionary(g => g.Key, g => g.Count()),
                KlaBiasPerHour = Mean(accepted.Select(t => t.KlaPerHour!.Value - TrueKla)),
                KlaDispersionPerHour = Sd(accepted.Select(t => t.KlaPerHour!.Value)),
                ConditionalCiCoverageAmongEstimated = covered.Length == 0 ? (double?)null :
                    covered.Count(t => t.CiLow <= TrueKla && t.CiHigh >= TrueKla) / (double)covered.Length,
                CiAvailable = covered.Length,
                EstimatedOur = our.Length,
                RefusedOur = biotic ? (int?)(trials.Count - our.Length) : null,
                OurQualityCounts = trials.GroupBy(t => t.OurQuality).ToDictionary(g => g.Key, g => g.Count()),
                OurBiasPercentPointsPerHour = biotic ? Mean(our.Select(t => t.OurPercentPointsPerHour!.Value - TrueOur)) : null,
                OurDispersionPercentPointsPerHour = Sd(our.Select(t => t.OurPercentPointsPerHour!.Value)),
                OurConditionalCiCoverageAmongEstimated = ourCovered.Length == 0 ? (double?)null :
                    ourCovered.Count(t => t.OurCiLow <= TrueOur && t.OurCiHigh >= TrueOur) / (double)ourCovered.Length,
                OurCiAvailable = ourCovered.Length,
                RefusalReasons = trials.Where(t => !t.KlaPerHour.HasValue).SelectMany(t => t.Reasons)
                    .GroupBy(r => r).ToDictionary(g => g.Key, g => g.Count()),
                Runs = trials,
            });
            Console.WriteLine($"{(biotic ? "biotic" : "abiotic")} {noise}: kLa estimated {accepted.Length}/20, OUR {our.Length}/20.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            Algorithm = KlaDeterministicAnalysis.Version, Runtime = Environment.Version.ToString(),
            AnalysisAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(KlaDeterministicAnalysis).Assembly.Location))).ToLowerInvariant(),
            TotalSyntheticTrials = reports.Count * 20,
            Config = new KlaDeterministicConfig(), TrueKlaPerHour = TrueKla, TrueOurPercentPointsPerHour = TrueOur,
            Scope = "160 seeded synthetic trajectories; ideal independently negligible probe; constant process/OUR and negligible residual transfer. Biotic starts at Ceq=75%, falls to 15% over 120 s, then recovers. No hardware, adaptive cleanup, criterion tuning or experimental truth borrowed. Estimated does not imply operator acceptance.",
            IntervalScope = "Coverage uses only estimates with an available OLS conditional interval; all refusals and missing intervals are reported. Twenty repetitions per scenario are exploratory, not proof of nominal 95% coverage or full uncertainty.",
            NoiseScope = "Gaussian sigma in percentage points. Correlated: stationary AR(1), rho=0.8. Outliers: white sigma=0.2 plus alternating +/-3 pp every 37 observations. Identical seed pairing across protocols/noise scenarios is deliberate; scenarios are not independent experiments.",
            Scenarios = reports,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static KlaDeterministicRequest Request(bool biotic, string noise, int seed)
    {
        var random = new Random(seed);
        double Gaussian() => Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
        var sigma = noise switch { "white-0.1" => 0.1, "white-0.5" => 0.5, _ => 0.2 };
        var samples = ImmutableArray.CreateBuilder<KlaObservation>();
        var previous = sigma * Gaussian();
        var lastTime = biotic ? 420 : 300;
        for (var seconds = 0; seconds <= lastTime; seconds += 2)
        {
            var trueOxygen = biotic ? seconds < 120 ? 75 - 0.5 * seconds
                : 75 - 60 * Math.Exp(-0.02 * (seconds - 120)) : 100 - 90 * Math.Exp(-0.02 * seconds);
            var error = sigma * Gaussian();
            if (noise == "correlated-0.2") { error = 0.8 * previous + 0.6 * error; previous = error; }
            if (noise == "outliers-0.2" && samples.Count % 37 == 0) error += samples.Count % 74 == 0 ? 3 : -3;
            samples.Add(new(seconds, trueOxygen + error));
        }
        return new()
        {
            Protocol = biotic ? KlaAssayProtocol.Biotic : KlaAssayProtocol.Abiotic,
            RemovalMode = biotic ? KlaGasRemovalMode.Respiration : KlaGasRemovalMode.NitrogenStripping,
            Samples = samples.ToImmutable(), Events = biotic
                ? [new(0, KlaGasEventKind.GasOffConfirmed, "synthetic_true_gas_off"), new(120, KlaGasEventKind.GasOnConfirmed, "synthetic_true_gas_on")]
                : [new(0, KlaGasEventKind.GasOnConfirmed, "synthetic_true_gas_on")],
            ProbeResponseNegligibleIndependentlyVerified = true, ProcessConditionsIndependentlyVerified = true,
            ResidualTransferNegligible = true, ConsumptionRepresentativeOfRecovery = true,
            PhysicalSaturationPercent = 100, PhysicalSaturationSource = "synthetic_ground_truth",
        };
    }

    private static double? Mean(IEnumerable<double> source)
    {
        var values = source.ToArray();
        return values.Length == 0 ? null : values.Average();
    }
    private static double? Sd(IEnumerable<double> source)
    {
        var values = source.ToArray();
        if (values.Length < 2) return null;
        var mean = values.Average();
        return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Length - 1));
    }

    private sealed record Trial(int Seed, string Quality, double? KlaPerHour, double? CiLow, double? CiHigh,
        double? CeqPercent, string OurQuality, double? OurPercentPointsPerHour, double? OurCiLow, double? OurCiHigh,
        string[] Reasons, string[] OurReasons, KlaIndexWindow? Window);
}
