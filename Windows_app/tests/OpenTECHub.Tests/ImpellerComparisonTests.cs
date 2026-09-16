using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenTECHub.Services.PowerMapping;
using OpenTECHub.Services.PowerTesting;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// Multi-assay impeller benchmarking (§18.3 step 6): what a comparison row is made of, and the
/// rule that non-equivalent rigs are labelled rather than quietly averaged together.
/// </summary>
public sealed class ImpellerComparisonTests
{
    private readonly PowerAnalysisEngine _engine = new();

    [Fact]
    public void Comparison_item_carries_the_plateau_without_active_flooding_columns()
    {
        var document = BuildDocument("Rushton D=60", ImpellerType.RushtonFlatBlade, diameterM: 0.060);

        var item = ImpellerComparisonBuilder.BuildItem(document, _engine);

        Assert.Equal(document.TestId, item.SourceTestId);
        Assert.Equal(ImpellerType.RushtonFlatBlade, item.ImpellerType);
        Assert.Equal(0.060, item.ImpellerDiameterM, 6);
        Assert.Equal(0.060 / 0.190, item.DiameterRatioDt, 6);

        // A plateau exists because the synthetic points sit well above Re = 10 000.
        Assert.True(item.TurbulentNpMean > 0, "the turbulent plateau should have been fitted");
        Assert.NotEmpty(item.PowerNumberReynoldsCurve);
        Assert.NotEmpty(item.PowerRatioCurve);

        // Flooding/Nienow values are legacy-only and are not populated in new comparisons.
        Assert.Null(item.ExperimentalFloodingFlG);
        Assert.Null(item.NienowFloodingFlG);
        Assert.Null(item.GasDispersionEfficiencyRatio);

        // Parasitic drag is the tare the assay actually subtracted.
        Assert.Equal(0.5, item.ParasiticPowerZeroSpeedW!.Value, 6);
    }

    [Fact]
    public void Assays_on_the_same_rig_compare_as_equivalent()
    {
        var first = BuildDocument("Rushton", ImpellerType.RushtonFlatBlade, diameterM: 0.060);
        var second = BuildDocument("Smith côncavo", ImpellerType.SmithConcaveBlade, diameterM: 0.060);

        var items = new[]
        {
            ImpellerComparisonBuilder.BuildItem(first, _engine),
            ImpellerComparisonBuilder.BuildItem(second, _engine),
        };

        var (compatible, notes) = ImpellerComparisonBuilder.CheckCompatibility(items, [first, second]);

        Assert.True(compatible);
        Assert.Empty(notes);
    }

    [Fact]
    public void Different_rigs_stay_comparable_but_every_difference_is_named()
    {
        var reference = BuildDocument("Rushton 10 L", ImpellerType.RushtonFlatBlade, diameterM: 0.060);

        var other = BuildDocument("Rushton 5 L sem chicanas", ImpellerType.RushtonFlatBlade, diameterM: 0.060);
        other.Geometry.LiquidVolumeM3 = 0.005;
        other.Geometry.VesselDiameterM = 0.150;
        other.Geometry.Baffled = false;
        other.Fluid = other.Fluid with { ViscosityPaS = 0.005 };
        other.RelativeMode = true;

        var items = new[]
        {
            ImpellerComparisonBuilder.BuildItem(reference, _engine),
            ImpellerComparisonBuilder.BuildItem(other, _engine),
        };

        var (compatible, notes) = ImpellerComparisonBuilder.CheckCompatibility(items, [reference, other]);

        Assert.False(compatible);

        // The comparison is still produced - it is labelled, not refused.
        Assert.Equal(2, items.Length);

        var joined = string.Join(" | ", notes);
        Assert.Contains("vaso", joined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("volume útil", joined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chicanas", joined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("viscosidade", joined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("relativo", joined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Csv_export_carries_the_benchmark_table_the_raw_series_and_the_warnings()
    {
        var document = BuildDocument("Rushton", ImpellerType.RushtonFlatBlade, diameterM: 0.060);
        var item = ImpellerComparisonBuilder.BuildItem(document, _engine);

        var csv = ImpellerComparisonBuilder.BuildCsv([item], ["montagens diferentes; atenção"]);

        Assert.Contains("benchmark;Rushton;RushtonFlatBlade", csv, StringComparison.Ordinal);
        Assert.Contains("np_re;Rushton;", csv, StringComparison.Ordinal);
        Assert.Contains("pg_p0;Rushton;", csv, StringComparison.Ordinal);
        Assert.Contains("ATENCAO", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("eficiencia_relativa_dispersao", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("FlG_F_nienow", csv, StringComparison.Ordinal);

        // The separator is ';', so any ';' inside a note or a name must not open a new column.
        var noteLine = csv.Split('\n').First(l => l.Contains("montagens diferentes", StringComparison.Ordinal));
        Assert.Equal(1, noteLine.Count(c => c == ';'));
        Assert.Contains("montagens diferentes, atenção", noteLine, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewModel_builds_rows_and_reports_incompatibility_without_dropping_series()
    {
        var root = Path.Combine(Path.GetTempPath(), "ImpellerComparison_" + Guid.NewGuid().ToString("N"));
        try
        {
            var testStore = new PowerTestStore(root);
            var mapStore = new PowerMapStore(Path.Combine(root, "Mapas-Potencia"));

            var first = PersistDocument(testStore, "Rushton", ImpellerType.RushtonFlatBlade, 0.060, volumeM3: 0.010);
            var second = PersistDocument(testStore, "Smith", ImpellerType.SmithConcaveBlade, 0.060, volumeM3: 0.004);

            var viewModel = new PowerImpellerComparisonViewModel(testStore, mapStore, _engine);
            viewModel.ReloadTests();

            Assert.Equal(2, viewModel.AvailableTests.Count);

            foreach (var entry in viewModel.AvailableTests)
            {
                entry.IsSelected = true;
            }

            viewModel.BuildComparison();

            Assert.Equal(2, viewModel.Rows.Count);
            Assert.Equal(2, viewModel.ComparedCount);
            Assert.True(viewModel.HasComparison);

            // Different working volumes: flagged, but both series survive.
            Assert.False(viewModel.IsCompatible);
            Assert.True(viewModel.HasCompatibilityWarnings);
            Assert.NotEmpty(viewModel.CompatibilityNotes);
            Assert.Contains("NÃO equivalentes", viewModel.CompatibilitySummary, StringComparison.Ordinal);

            Assert.Contains(viewModel.Rows, r => r.TestName == first.Name);
            Assert.Contains(viewModel.Rows, r => r.TestName == second.Name);

            viewModel.SaveComparison();
            Assert.Single(mapStore.ListComparisons());

            var csv = viewModel.BuildCsvContent();
            Assert.Contains("benchmark;Rushton;", csv, StringComparison.Ordinal);
            Assert.Contains("benchmark;Smith;", csv, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Assays_without_accepted_points_are_not_offered_for_comparison()
    {
        var root = Path.Combine(Path.GetTempPath(), "ImpellerComparison_" + Guid.NewGuid().ToString("N"));
        try
        {
            var testStore = new PowerTestStore(root);
            var mapStore = new PowerMapStore(Path.Combine(root, "Mapas-Potencia"));

            PersistDocument(testStore, "Com pontos", ImpellerType.RushtonFlatBlade, 0.060, volumeM3: 0.010);

            var empty = testStore.CreateTest(
                "Rascunho vazio",
                new FluidProperties(),
                new PowerGeometry(),
                new PowerTestSettings());
            testStore.SaveTestManifest(empty);

            var viewModel = new PowerImpellerComparisonViewModel(testStore, mapStore, _engine);
            viewModel.ReloadTests();

            Assert.Single(viewModel.AvailableTests);
            Assert.Equal("Com pontos", viewModel.AvailableTests[0].Summary.Name);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static PowerTestDocument PersistDocument(
        PowerTestStore store,
        string name,
        ImpellerType type,
        double diameterM,
        double volumeM3)
    {
        var template = BuildDocument(name, type, diameterM);
        template.Geometry.LiquidVolumeM3 = volumeM3;

        var created = store.CreateTest(name, template.Fluid, template.Geometry, new PowerTestSettings());
        created.Flooding = template.Flooding;
        created.Runs = template.Runs;
        created.Conditions = template.Conditions;
        store.SaveTestManifest(created);

        return created;
    }

    /// <summary>An assay with accepted ungassed points in the turbulent range plus gassed points.</summary>
    private static PowerTestDocument BuildDocument(string name, ImpellerType type, double diameterM)
    {
        var geometry = new PowerGeometry
        {
            Impellers = [new Impeller { Type = type, DiameterM = diameterM, StageIndex = 0 }],
            VesselDiameterM = 0.190,
            LiquidVolumeM3 = 0.010,
            Baffled = true,
        };

        var fluid = new FluidProperties { DensityKgM3 = 1000.0, ViscosityPaS = 0.001 };

        var runs = new List<PowerRunSummary>();

        // Ungassed sweep: Re well above the 10 000 turbulent cutoff, so a plateau can be fitted.
        foreach (var rpm in new[] { 300.0, 400.0, 500.0, 600.0 })
        {
            var reynolds = PowerCalc.ReynoldsNumber(fluid.DensityKgM3, rpm, diameterM, fluid.ViscosityPaS);
            runs.Add(new PowerRunSummary
            {
                RunId = Guid.NewGuid(),
                AgitationRpm = rpm,
                MeanRpmMeasured = rpm,
                GasMode = PowerGasMode.Ungassed,
                Phase = PowerRunPhase.Accepted,
                NetPowerW = 0.001 * Math.Pow(rpm, 2),
                StartedUtc = DateTimeOffset.UtcNow,
                Analysis = new PowerPointResult
                {
                    MeanRpm = rpm,
                    AssemblyPowerNumber = 5.0,
                    AssemblyPowerNumberCi95 = 0.2,
                    AssemblyReynoldsNumber = reynolds,
                    VoidPowerW = 0.5,
                    NetPowerW = 0.001 * Math.Pow(rpm, 2),
                },
            });
        }

        // Gassed points: the aeration curve the comparison overlays.
        foreach (var (rpm, flow, ratio) in new[] { (400.0, 2.0, 0.85), (400.0, 4.0, 0.72), (400.0, 6.0, 0.61) })
        {
            runs.Add(new PowerRunSummary
            {
                RunId = Guid.NewGuid(),
                AgitationRpm = rpm,
                MeanRpmMeasured = rpm,
                GasFlowLpm = flow,
                GasMode = PowerGasMode.Gassed,
                Phase = PowerRunPhase.Accepted,
                NetPowerW = 0.001 * Math.Pow(rpm, 2) * ratio,
                PowerRatio = ratio,
                GasFlowNumber = PowerCalc.AerationNumber(flow, rpm, diameterM),
                StartedUtc = DateTimeOffset.UtcNow,
                Analysis = new PowerPointResult
                {
                    MeanRpm = rpm,
                    VoidPowerW = 0.5,
                    NetPowerW = 0.001 * Math.Pow(rpm, 2) * ratio,
                    PowerRatio = ratio,
                },
            });
        }

        return new PowerTestDocument
        {
            TestId = Guid.NewGuid(),
            Name = name,
            Fluid = fluid,
            Geometry = geometry,
            Runs = runs,
            Flooding = new FloodingAnalysisResult
            {
                ExperimentalFlG = 0.045,
                TheoreticalFlGNienow = 0.030,
                ExperimentalRpm = 400,
                ExperimentalFlowLpm = 5.0,
                ReferenceImpellerType = type,
            },
        };
    }
}
