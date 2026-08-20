using System.IO;
using System.Text;
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
    public async Task Draft_store_preserves_blank_and_invalid_editor_rows_exactly()
    {
        await WithStoreAsync(async store =>
        {
            var snapshot = ReferenceInput();
            var rows = new[]
            {
                new KlaAnchorDraft("2", "800", ""),
                new KlaAnchorDraft("7,0", "800", "em medição"),
            };
            await store.SaveExperimentAsync(new KlaExperimentDocument
            {
                Snapshot = snapshot with { Anchors = [] },
                DraftRows = rows,
                ReviewNote = "aguardando bancada",
            });

            var loaded = Assert.Single(await store.LoadExperimentsAsync());
            Assert.Equal(rows, loaded.DraftRows);
            Assert.Equal("aguardando bancada", loaded.ReviewNote);
            Assert.Empty(loaded.Snapshot.Anchors);
        });
    }

    [Fact]
    public async Task Publication_is_versioned_integrity_checked_and_reopens_byte_for_byte()
    {
        await WithStoreAsync(async store =>
        {
            var input = ReferenceInput();
            var surface = new KlaMappingEngine().Reconstruct(input);
            var path = BuildFixturePath(surface);

            var missingReview = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.PublishAsync(input, surface, path, "   "));
            Assert.Contains("nota de revisão", missingReview.Message, StringComparison.Ordinal);

            var first = await store.PublishAsync(input, surface, path, "revisão A");
            var firstBytes = await store.ReadReceiptBytesAsync(first.ReceiptFingerprint);
            var reopened = Assert.Single(await store.LoadPublishedAsync());
            Assert.Equal(first.ReceiptFingerprint, reopened.ReceiptFingerprint);
            Assert.Equal(1, reopened.Payload.Version);

            var export = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.kla.json");
            try
            {
                await store.ExportReceiptAsync(first.ReceiptFingerprint, export);
                Assert.Equal(firstBytes, await File.ReadAllBytesAsync(export));

                var imported = await store.ImportReceiptAsDraftAsync(export);
                Assert.NotEqual(input.Id, imported.Id);
                Assert.Equal(input.Anchors, imported.Anchors);
                Assert.Contains("Revisão local obrigatória", imported.Notes, StringComparison.Ordinal);

                var original = Encoding.UTF8.GetString(firstBytes);
                var altered = original.Replace("\"version\": 1", "\"version\": 9", StringComparison.Ordinal);
                Assert.NotEqual(original, altered);
                await File.WriteAllTextAsync(export, altered, new UTF8Encoding(false));
                await Assert.ThrowsAsync<InvalidDataException>(
                    () => store.ImportReceiptAsDraftAsync(export));
            }
            finally
            {
                if (File.Exists(export))
                {
                    File.Delete(export);
                }
            }

            var second = await store.PublishAsync(input, surface, path, "revisão B");
            Assert.Equal(2, second.Payload.Version);
            Assert.Equal(first.Payload.ProfileId, second.Payload.ProfileId);
            Assert.NotEqual(first.ReceiptFingerprint, second.ReceiptFingerprint);
            Assert.Equal(2, (await store.LoadPublishedAsync()).Count);
        });
    }

    [Fact]
    public async Task Clean_store_has_no_bundled_profile_and_custom_method_cannot_publish()
    {
        await WithStoreAsync(async store =>
        {
            Assert.Empty(await store.LoadExperimentsAsync());
            Assert.Empty(await store.LoadPublishedAsync());

            var input = ReferenceInput() with
            {
                Algorithm = new KlaAlgorithmSettings { SurfaceGridResolution = 100 },
            };
            var surface = new KlaMappingEngine().Reconstruct(input);
            var path = BuildFixturePath(surface);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.PublishAsync(input, surface, path, "preview"));
            Assert.Contains("referência", error.Message, StringComparison.Ordinal);
        });
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
