using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using OpenTECHub.Services.KlaTesting;

if (args.Length == 2 && args[0] == "--noise")
{
    NoiseAudit.Run(args[1]);
    return;
}
if (args.Length != 2) throw new ArgumentException("Usage: KlaCorpusAudit input-bundle.json output-report.json OR --noise output-report.json");
var curves = JsonSerializer.Deserialize<Curve[]>(File.ReadAllText(args[0])) ?? throw new InvalidDataException("Empty corpus.");
if (curves.Length != 92 || curves.Select(c => c.CurveId).Distinct().Count() != 92)
    throw new InvalidDataException("Expected 92 distinct frozen curves.");
var results = new List<object>();
var refused = 0;
foreach (var curve in curves)
{
    var biotic = curve.Biology == "biotic";
    var request = new KlaDeterministicRequest
    {
        Protocol = biotic ? KlaAssayProtocol.Biotic : KlaAssayProtocol.Abiotic,
        RemovalMode = biotic ? KlaGasRemovalMode.Respiration : KlaGasRemovalMode.NitrogenStripping,
        Samples = curve.Samples.Select(p => new KlaObservation(p.Seconds ?? double.NaN,
            p.CalibratedDoPercent ?? double.NaN, IsValid: p.Seconds.HasValue && p.CalibratedDoPercent.HasValue)).ToImmutableArray(),
        Probe = new() { Technology = curve.ProbeTechnology, ResponseTimeSeconds = curve.ProbeResponseSeconds },
        RawDataSha256 = curve.Sha256,
        // The curated files provide OD and descriptive timing, not hardware-confirmed gas events.
        // No application/legacy kLa value, trough-derived t0, or invented C*=100 is supplied.
    };
    var result = KlaDeterministicAnalysis.Analyze(request);
    if (result.KlaQuality == KlaScientificQuality.Inconclusive) refused++;
    results.Add(new { curve.CurveId, curve.Biology, curve.Group, curve.Role, curve.Sha256,
        curve.EventProvenance, Points = request.Samples.Length,
        Quality = result.KlaQuality.ToString(), result.KlaPerHour, Reasons = result.Reasons,
        OurQuality = result.Our.Quality.ToString(), OurReasons = result.Our.Reasons });
}
var report = new
{
    Algorithm = KlaDeterministicAnalysis.Version, Config = new KlaDeterministicConfig(),
    Scope = "Strict metadata audit of all 92 curves; no confirmed gas events present. This is not estimator accuracy or independent biotic validation.",
    SourceCleaning = "Curated source may already remove duplicate/nonfinite observations; no additional cleaning in this audit.",
    Total = curves.Length, Inconclusive = refused, ValidatedKla = 0,
    Biotic = curves.Count(c => c.Biology == "biotic"), Results = results,
};
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
File.WriteAllText(args[1], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Audited {curves.Length} curves: {refused} inconclusive; no physical timing inferred.");

internal sealed record Sample(double? Seconds, double? CalibratedDoPercent);
internal sealed record Curve(string CurveId, string Biology, string Group, string Role, string Sha256,
    string ProbeTechnology, double? ProbeResponseSeconds, string EventProvenance, Sample[] Samples);
