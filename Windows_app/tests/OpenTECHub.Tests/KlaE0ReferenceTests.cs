using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using OpenTECHub.Services.KlaTesting;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>Frozen E0 references. No test opens an operator's acquisition folder.</summary>
public sealed class KlaE0ReferenceTests
{
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures", "KlaE0");

    [Fact]
    public void FrozenReferences_MatchTheirRecordedHashes()
    {
        using var baseline = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "baseline.json")));
        foreach (var file in baseline.RootElement.GetProperty("fixtureHashes").EnumerateObject())
        {
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(Fixtures, file.Name))));
            Assert.Equal(file.Value.GetString(), hash.ToLowerInvariant());
        }
    }

    [Fact]
    public void ExperimentalSplit_DoesNotSeparateSiblingsOrClaimIndependentBioticHoldout()
    {
        using var split = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "split-manifest.json")));
        var rows = split.RootElement.EnumerateArray().ToArray();
        Assert.Equal(92, rows.Length);
        foreach (var group in rows.GroupBy(r => r.GetProperty("group").GetString()))
        {
            Assert.Single(group.Select(r => r.GetProperty("role").GetString()).Distinct());
        }
        var biotic = rows.Where(r => r.GetProperty("biology").GetString() == "biotic").ToArray();
        Assert.Equal(17, biotic.Length);
        Assert.All(biotic, r => Assert.Equal("development", r.GetProperty("role").GetString()));
        Assert.Contains(rows, r => r.GetProperty("role").GetString() == "reserved-not-evaluated");
    }

    [Fact]
    public void OperatingEnvelope_DoesNotApproveUnknownBiologicalLimits()
    {
        using var envelope = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "operating-envelope.json")));
        Assert.False(envelope.RootElement.GetProperty("bioticExecutionApproved").GetBoolean());
        Assert.All(envelope.RootElement.GetProperty("approved").EnumerateObject(),
            p => Assert.Equal(JsonValueKind.Null, p.Value.ValueKind));
    }

    [Fact]
    public void ActualLegacyV1_RemainsReadableWithoutInventingTemperatureOrSpeed()
    {
        WithCopy("legacy-v1", (store, name) =>
        {
            var manifest = store.LoadTest(name);
            Assert.NotNull(manifest);
            Assert.Equal(1, manifest.SchemaVersion);
            Assert.True(manifest.IsLegacyRig);
            var points = store.LoadRunRawData(name, "N0450_Q03p00_Rep01");
            Assert.NotEmpty(points);
            Assert.Contains(points, p => p.Phase == RunPhase.LegacyOpeningVent);
            Assert.All(points, p => { Assert.Null(p.TemperatureC); Assert.Null(p.RpmMeasured); });
        });
    }

    [Theory]
    [InlineData("abiotic")]
    [InlineData("biotic-constant-our")]
    [InlineData("shifted-time")]
    public void V2CalibratedSamples_RecoverAnalyticalTruth(string caseId)
    {
        using var cases = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "analytical-cases.json")));
        var reference = cases.RootElement.EnumerateArray().Single(c => c.GetProperty("id").GetString() == caseId);
        WithCopy(reference.GetProperty("path").GetString()!, (store, name) =>
        {
            var manifest = store.LoadTest(name);
            Assert.NotNull(manifest);
            Assert.Equal(2, manifest.SchemaVersion);
            var points = store.LoadRunRawData(name, "N0400_Q03p00_Rep01");
            Assert.Equal(reference.GetProperty("points").GetInt32(), points.Count);
            Assert.Null(points[0].TemperatureC);
            Assert.Null(points[0].RpmMeasured);
            Assert.Equal(30.0, points[1].TemperatureC);
            Assert.Equal(400.0, points[1].RpmMeasured);
            Assert.True(points[0].DORaw > 10000); // Distinct ADC prevents a channel mix-up.

            var engine = new KlaAnalysisEngine();
            var result = engine.PerformLogLinearAnalysis(
                points.Select(p => p.RelativeSeconds).ToArray(),
                points.Select(p => p.DOFiltered).ToArray(),
                reference.GetProperty("ceqPercent").GetDouble(), true,
                reference.GetProperty("windowStartSeconds").GetDouble(),
                reference.GetProperty("windowEndSeconds").GetDouble());
            var expected = reference.GetProperty("klaPerHour").GetDouble();
            var tolerance = reference.GetProperty("toleranceKlaPerHour").GetDouble();
            Assert.InRange(result.KlaPerHour, expected - tolerance, expected + tolerance);
            Assert.Equal(reference.GetProperty("expectedUsedPoints").GetInt32(), result.UsedPoints);
            Assert.Equal(DecisionQuality.Acceptable, result.Quality);
            Assert.True(result.AnalysisR2 > 0.999999);
        });
    }

    private static void WithCopy(string relativePath, Action<KlaTestStore, string> assert)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"opentechub-kla-e0-{Guid.NewGuid():N}");
        const string name = "Reference";
        var source = Path.Combine(Fixtures, relativePath);
        try
        {
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(temp, name, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            assert(new KlaTestStore(temp), name);
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
        }
    }
}
