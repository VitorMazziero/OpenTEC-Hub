using System.IO;
using System.Text;
using System.Text.Json;
using TecnalHub.Services.KlaMapping;
using Xunit;

namespace TecnalHub.Tests;

public sealed class KlaMappingPersistenceTests
{
    private static readonly KlaAnchor[] Anchors =
    [
        new(2, 800, 121.84), new(7, 800, 179.91), new(12, 800, 195.83),
        new(2, 500, 58.21), new(7, 500, 96.98), new(12, 500, 140.51),
        new(2, 200, 8.28), new(7, 200, 15.17), new(12, 200, 21.20),
    ];

    [Fact]
    public async Task Experiment_store_saves_and_loads_individual_experiment_files()
    {
        await WithStoreAsync(async store =>
        {
            var snapshot = ReferenceInput();
            var rows = new[]
            {
                new KlaAnchorDraft("2", "800", ""),
                new KlaAnchorDraft("7,0", "800", "em medição"),
            };
            var doc = new KlaExperimentDocument
            {
                Snapshot = snapshot with { Anchors = [] },
                DraftRows = rows,
            };
            await store.SaveExperimentAsync(doc);

            var filePath = store.GetExperimentFilePath(snapshot.Id);
            Assert.True(File.Exists(filePath));

            var loaded = Assert.Single(await store.LoadExperimentsAsync());
            Assert.Equal(snapshot.Id, loaded.Snapshot.Id);
            Assert.Equal(rows.Length, loaded.DraftRows.Length);
            Assert.Equal(rows[0].Airflow, loaded.DraftRows[0].Airflow);
            Assert.Empty(loaded.Snapshot.Anchors);
        });
    }

    [Fact]
    public async Task Publication_makes_profile_available_for_control_and_exportable()
    {
        await WithStoreAsync(async store =>
        {
            var input = ReferenceInput();
            var surface = new KlaMappingEngine().Reconstruct(input);
            var path = BuildFixturePath(surface);

            var doc = new KlaExperimentDocument
            {
                Snapshot = input,
                DraftRows = input.Anchors.Select(a => new KlaAnchorDraft(
                    a.AirflowLpm.ToString(), a.AgitationRpm.ToString(), a.KlaPerHour.ToString())).ToArray(),
                SurfaceData = new KlaSurfaceData(surface.Diagnostics, surface.Fingerprint),
                PathData = new KlaPathData(
                    path.Path.ToArray(),
                    path.Allocation.ToArray(),
                    path.HeadroomScores.ToArray(),
                    path.HeadroomResolution,
                    path.Diagnostics,
                    path.SourceSurfaceFingerprint,
                    path.Fingerprint),
            };

            var publishedProfile = await store.PublishAsync(doc);
            Assert.NotNull(publishedProfile);
            Assert.Equal(input.Name, publishedProfile.Name);

            var publishedList = await store.LoadPublishedAsync();
            var reopened = Assert.Single(publishedList);
            Assert.Equal(publishedProfile.Name, reopened.Name);
            Assert.Equal(path.Allocation.Count, reopened.Payload.Allocation.Length);

            var exportPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.kla.json");
            try
            {
                await store.ExportExperimentAsync(input.Id, exportPath);
                Assert.True(File.Exists(exportPath));

                var imported = await store.ImportExperimentAsync(exportPath);
                Assert.NotEqual(input.Id, imported.Snapshot.Id);
                Assert.Contains("importado", imported.Snapshot.Name);
            }
            finally
            {
                if (File.Exists(exportPath))
                {
                    File.Delete(exportPath);
                }
            }
        });
    }

    [Fact]
    public async Task Clean_store_has_no_bundled_profiles_and_rejects_publishing_without_trajectory()
    {
        await WithStoreAsync(async store =>
        {
            Assert.Empty(await store.LoadExperimentsAsync());
            Assert.Empty(await store.LoadPublishedAsync());

            var input = ReferenceInput();
            var doc = new KlaExperimentDocument
            {
                Snapshot = input,
                DraftRows = [],
                Stage = KlaWorkflowStage.Draft,
            };

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.PublishAsync(doc));
            Assert.Contains("trajetória", error.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Legacy_experiments_and_receipts_are_migrated_automatically()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tecnalhub-kla-mig-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            var legacySnap = ReferenceInput();
            var legacyDocs = new[]
            {
                new KlaExperimentDocument
                {
                    Snapshot = legacySnap,
                    DraftRows = [],
                    Stage = KlaWorkflowStage.Draft,
                }
            };
            var legacyJson = JsonSerializer.Serialize(legacyDocs, KlaFingerprint.JsonOptions);
            await File.WriteAllTextAsync(Path.Combine(directory, "experiments.json"), legacyJson);

            var store = new KlaProfileStore(directory);
            var loaded = await store.LoadExperimentsAsync();
            var doc = Assert.Single(loaded);
            Assert.Equal(legacySnap.Id, doc.Snapshot.Id);
            Assert.True(File.Exists(store.GetExperimentFilePath(legacySnap.Id)));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static KlaPathResult BuildFixturePath(KlaSurface surface)
    {
        var path = new KlaMappingEngine().GeneratePath(surface, 0.59, 0.66).ToArray();
        var allocation = path
            .Aggregate(new List<KlaAllocationSample>(), (items, point) =>
            {
                if (items.Count == 0 || point.KlaPerHour > items[^1].KlaPerHour + 1e-9)
                {
                    items.Add(new KlaAllocationSample(
                        point.KlaPerHour,
                        point.AirflowLpm,
                        point.AgitationRpm));
                }

                return items;
            })
            .ToArray();
        var diagnostics = new KlaPathDiagnostics(
            0.59,
            0.66,
            surface.Input.Domain.DenormalizeAirflow(0.59),
            surface.Input.Domain.DenormalizeAgitation(0.66),
            path.Average(point => point.Headroom),
            path.Average(point => point.Headroom),
            1,
            allocation[0].KlaPerHour,
            allocation[^1].KlaPerHour,
            1,
            allocation.Length,
            []);
        return new KlaPathResult(
            path,
            allocation,
            [diagnostics.MeanHeadroom],
            1,
            diagnostics,
            surface.Fingerprint,
            "path-fixture-fingerprint");
    }

    private static KlaExperimentSnapshot ReferenceInput() => new()
    {
        Name = "Ensaio de referência",
        Broth = "Serratia marcescens",
        RunCode = "fixture",
        Domain = new KlaDomain(2, 12, 200, 800),
        Anchors = Anchors,
    };

    private static async Task WithStoreAsync(Func<KlaProfileStore, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tecnalhub-kla-{Guid.NewGuid():N}");
        try
        {
            await action(new KlaProfileStore(directory));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}

