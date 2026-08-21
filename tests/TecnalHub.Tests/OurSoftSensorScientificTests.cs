using System.IO;
using System.Text.Json;
using TecnalHub.Services.Control;
using Xunit;

namespace TecnalHub.Tests;

/// <summary>
/// Cross-language parity for the conditional-OUR soft sensor (WP8): the C# inversion and the
/// quasi-steady acceptance rule must reproduce the manuscript's own computed values.
/// </summary>
/// <remarks>
/// The oracle in <c>tests/fixtures/our-reference.json</c> is extracted from the paper's
/// <c>analysis/2_our_soft_sensor</c> output by <c>tools/our-reference/generate_fixture.py</c>.
/// The centred Savitzky-Golay smoothing and derivative are the paper's offline preprocessing and
/// are taken as given inputs (the paper's <c>DOT_smooth</c> and <c>dDOT/dt</c> columns); the live
/// sensor recomputes the rate causally, which is what <see cref="OurSoftSensorTests"/> exercises.
/// This test isolates the two scientific claims: the OUR inversion and the acceptance predicate.
/// </remarks>
public sealed class OurSoftSensorScientificTests
{
    private static readonly Fixture Reference = Load();

    [Fact]
    public void The_config_defaults_match_the_manuscript()
    {
        var config = new OurSensorConfig();

        Assert.Equal(Reference.Config.OxygenSaturationMmolPerL, config.OxygenSaturationMmolPerL, precision: 12);
        Assert.Equal(Reference.Config.StableTolerancePp, config.SetpointTolerancePercentPoints, precision: 12);
        Assert.Equal(Reference.Config.StableRateLimitPpH, config.RateLimitPointsPerHour, precision: 12);
        Assert.Equal(Reference.Config.GateTolerancePp, config.GateTolerancePercentPoints, precision: 12);
    }

    [Fact]
    public void The_our_inversion_reproduces_every_paper_row()
    {
        var cStar = Reference.Config.OxygenSaturationMmolPerL;

        foreach (var row in Reference.Rows)
        {
            var inferred = OurSoftSensor.InferOur(row.KlaPerHour, row.DotSmoothPercent, cStar);
            Assert.Equal(row.OurMmolPerLPerHour, inferred, precision: 9);
        }
    }

    [Fact]
    public void The_acceptance_predicate_reproduces_every_paper_mask_row()
    {
        var config = new OurSensorConfig
        {
            SetpointTolerancePercentPoints = Reference.Config.StableTolerancePp,
            RateLimitPointsPerHour = Reference.Config.StableRateLimitPpH,
        };
        var setpoint = Reference.Config.DotSetpointPercent;

        foreach (var row in Reference.Rows)
        {
            // full_post_gate_stable = post_gate AND (on-band AND quasi-steady).
            var quasiSteady = OurSoftSensor.IsQuasiSteady(row.RawDotPercent, setpoint, row.DotRatePpH, config);
            var expected = row.PostGate && quasiSteady;

            Assert.Equal(row.FullPostGateStable, expected);
        }
    }

    [Fact]
    public void The_documented_conditional_mean_is_positive_and_bounded_by_the_max()
    {
        // A cheap guard on the recorded oracle: the paper's mean/max/cumulative are self-consistent.
        Assert.True(Reference.Summary.MeanMmolPerLPerHour > 0);
        Assert.True(Reference.Summary.MeanMmolPerLPerHour <= Reference.Summary.MaxMmolPerLPerHour);
        Assert.True(Reference.Summary.CumulativeMmolPerL > 0);
        Assert.True(Reference.Summary.AcceptedDurationHours > 0);
    }

    private static Fixture Load()
    {
        var path = Path.Combine(TestPaths.RepositoryRoot, "tests", "fixtures", "our-reference.json");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        var configElement = root.GetProperty("config");
        var config = new FixtureConfig(
            configElement.GetProperty("oxygen_saturation_mmol_l").GetDouble(),
            configElement.GetProperty("dot_setpoint_percent").GetDouble(),
            configElement.GetProperty("stable_tolerance_pp").GetDouble(),
            configElement.GetProperty("stable_rate_limit_pp_h").GetDouble(),
            configElement.GetProperty("gate_tolerance_pp").GetDouble());

        var summaryElement = root.GetProperty("documented_summary");
        var summary = new FixtureSummary(
            summaryElement.GetProperty("mean_conditional_our_mmol_l_h").GetDouble(),
            summaryElement.GetProperty("max_conditional_our_mmol_l_h").GetDouble(),
            summaryElement.GetProperty("cumulative_inferred_oxygen_uptake_accepted_mmol_l").GetDouble(),
            summaryElement.GetProperty("accepted_duration_h").GetDouble());

        var rows = new List<FixtureRow>();
        foreach (var row in root.GetProperty("rows").EnumerateArray())
        {
            rows.Add(new FixtureRow(
                row.GetProperty("raw_dot_percent").GetDouble(),
                row.GetProperty("dot_smooth_percent").GetDouble(),
                row.GetProperty("dot_rate_pp_h").GetDouble(),
                row.GetProperty("kla_h_inv").GetDouble(),
                row.GetProperty("our_mmol_l_h").GetDouble(),
                row.GetProperty("post_gate").GetBoolean(),
                row.GetProperty("full_post_gate_stable").GetBoolean()));
        }

        Assert.NotEmpty(rows);
        return new Fixture(config, summary, rows);
    }

    private sealed record Fixture(FixtureConfig Config, FixtureSummary Summary, IReadOnlyList<FixtureRow> Rows);

    private sealed record FixtureConfig(
        double OxygenSaturationMmolPerL,
        double DotSetpointPercent,
        double StableTolerancePp,
        double StableRateLimitPpH,
        double GateTolerancePp);

    private sealed record FixtureSummary(
        double MeanMmolPerLPerHour,
        double MaxMmolPerLPerHour,
        double CumulativeMmolPerL,
        double AcceptedDurationHours);

    private sealed record FixtureRow(
        double RawDotPercent,
        double DotSmoothPercent,
        double DotRatePpH,
        double KlaPerHour,
        double OurMmolPerLPerHour,
        bool PostGate,
        bool FullPostGateStable);
}
