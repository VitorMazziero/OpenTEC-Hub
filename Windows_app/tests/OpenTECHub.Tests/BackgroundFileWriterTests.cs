using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.PowerTesting;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// D-048: assay I/O leaves the UI thread through one ordered queue. What must hold: order between
/// appends and atomic rewrites, byte parity with the synchronous writes it replaces, a flush that
/// really drains, a hash sealed on the caller that equals the file's, and a failure that is
/// reported without stopping the consumer.
/// </summary>
public sealed class BackgroundFileWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"opentechub-bgwriter-{Guid.NewGuid():N}");

    public BackgroundFileWriterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Appends_are_byte_identical_to_File_AppendAllText_with_Encoding_UTF8()
    {
        var queued = Path.Combine(_root, "queued.csv");
        var reference = Path.Combine(_root, "reference.csv");
        var lines = new[] { "a,b,ç", "1,2,3", "4,5,6" };

        using (var writer = new BackgroundFileWriter())
        {
            writer.AppendLine(queued, lines[1], headerIfEmpty: lines[0]);
            writer.AppendLine(queued, lines[2], headerIfEmpty: lines[0]);
            await writer.FlushAsync();
        }
        File.AppendAllText(reference, lines[0] + Environment.NewLine, Encoding.UTF8);
        File.AppendAllText(reference, lines[1] + Environment.NewLine, Encoding.UTF8);
        File.AppendAllText(reference, lines[2] + Environment.NewLine, Encoding.UTF8);

        Assert.Equal(File.ReadAllBytes(reference), File.ReadAllBytes(queued));
        Assert.Equal(Encoding.UTF8.GetPreamble(), File.ReadAllBytes(queued).Take(3));
    }

    [Fact]
    public async Task Header_is_written_once_and_not_on_a_file_that_already_has_content()
    {
        var path = Path.Combine(_root, "h.csv");
        File.WriteAllText(path, "Header" + Environment.NewLine, Encoding.UTF8);

        using var writer = new BackgroundFileWriter();
        writer.AppendLine(path, "row1", headerIfEmpty: "Header");
        writer.AppendLine(path, "row2", headerIfEmpty: "Header");
        await writer.FlushAsync();

        Assert.Equal(["Header", "row1", "row2"], File.ReadAllLines(path));
    }

    [Fact]
    public async Task Order_between_appends_atomic_rewrites_and_work_items_is_the_enqueue_order()
    {
        var log = Path.Combine(_root, "log.txt");
        var doc = Path.Combine(_root, "doc.json");
        var seen = new List<string>();

        using var writer = new BackgroundFileWriter();
        writer.AppendLine(log, "1");
        writer.WriteAllTextAtomic(doc, "v1");
        writer.AppendLine(log, "2");
        writer.Run(log, () => seen.Add(File.ReadAllText(doc) + "|" + File.ReadAllLines(log).Length));
        writer.WriteAllTextAtomic(doc, "v2");
        writer.AppendLine(log, "3");
        writer.Run(doc, () => seen.Add(File.ReadAllText(doc) + "|" + File.ReadAllLines(log).Length));
        await writer.FlushAsync();

        Assert.Equal(["v1|2", "v2|3"], seen);
        Assert.Equal(["1", "2", "3"], File.ReadAllLines(log));
        Assert.Equal("v2", File.ReadAllText(doc));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    [Fact]
    public async Task A_failing_item_is_reported_and_the_consumer_keeps_going()
    {
        var path = Path.Combine(_root, "ok.txt");
        var failures = new List<string>();
        using var writer = new BackgroundFileWriter();
        writer.WriteFailed += (p, _) => failures.Add(p);

        writer.AppendLine(path, "before");
        writer.Run("boom", () => throw new IOException("disco cheio"));
        writer.AppendLine(path, "after");
        await writer.FlushAsync();

        Assert.Equal(["boom"], failures);
        Assert.Equal(["before", "after"], File.ReadAllLines(path));
    }

    [Fact]
    public void Synchronous_mode_writes_inline_and_keeps_no_file_open()
    {
        var path = Path.Combine(_root, "sync.csv");
        using var writer = new BackgroundFileWriter(synchronous: true);
        writer.AppendLine(path, "row", headerIfEmpty: "H");

        Assert.Equal(["H", "row"], File.ReadAllLines(path));
        File.Delete(path); // would throw if the writer still held it
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Power_store_seals_the_raw_hash_on_the_caller_and_it_equals_the_file()
    {
        using var writer = new BackgroundFileWriter();
        var store = new PowerTestStore(Path.Combine(_root, "Testes-Potencia"), writer);
        var impeller = PowerImpellerCatalog.Create(ImpellerType.RushtonFlatBlade);
        impeller.DiameterM = 0.065;
        var doc = store.CreateTest("Fila", new FluidProperties(),
            new PowerGeometry { VesselDiameterM = 0.19, LiquidVolumeM3 = 0.01, Impellers = [impeller] },
            new PowerTestSettings(), [new PowerCondition { AgitationRpm = 300 }]);
        var run = new PowerRun { TestId = doc.TestId, ConditionId = doc.Conditions[0].ConditionId, ReplicateNumber = 1, AgitationRpm = 300, StartedUtc = DateTimeOffset.UtcNow, SampleCount = 3, NetPowerW = 0.4 };
        var folder = store.InitializeRunFolder(doc.FolderName, run);
        for (var i = 0; i < 50; i++)
        {
            store.AppendRunRawDataPoint(doc.FolderName, folder,
                new PowerDataPoint(DateTimeOffset.UtcNow, i, PowerRunPhase.AccumulatingToTarget, 300.0 + i * 0.01, 1.6, 0.02, 0.63, null, true));
        }

        store.SaveRunResult(doc.FolderName, run); // no flush before: the seal must not depend on the queue
        var sealedHash = run.RawDataSha256;
        await store.FlushAsync();

        var rawPath = store.GetRunRawDataPath(doc.FolderName, folder);
        Assert.Equal(PowerTestFileContracts.ComputeFileSha256(rawPath), sealedHash);
        Assert.Equal(51, File.ReadAllLines(rawPath).Length);
        var result = File.ReadAllText(Path.Combine(store.RootDirectory, doc.FolderName, PowerTestFileContracts.RunsDirectoryName, folder, PowerTestFileContracts.RunResultFileName));
        Assert.Contains(sealedHash!, result, StringComparison.Ordinal);
    }

    [Fact]
    public void Power_store_reads_see_the_queued_writes()
    {
        using var writer = new BackgroundFileWriter();
        var store = new PowerTestStore(Path.Combine(_root, "Testes-Potencia"), writer);
        var impeller = PowerImpellerCatalog.Create(ImpellerType.RushtonFlatBlade);
        var doc = store.CreateTest("Leitura", new FluidProperties(),
            new PowerGeometry { VesselDiameterM = 0.19, LiquidVolumeM3 = 0.01, Impellers = [impeller] },
            new PowerTestSettings(), [new PowerCondition { AgitationRpm = 300 }]);
        var revisionBefore = doc.SettingsRevision;
        for (var i = 0; i < 20; i++)
        {
            doc.SettingsRevision++;
            store.SaveTestManifest(doc);
        }
        store.AppendEventLog(doc.FolderName, new PowerTestEventLogEntry(DateTimeOffset.UtcNow, "X", "y"));

        var loaded = store.LoadTest(doc.FolderName);

        Assert.Equal(revisionBefore + 20, loaded!.SettingsRevision);
        Assert.Contains(doc.Name, store.ListTests().Select(t => t.Name));
        Assert.Contains("\"X\"", File.ReadAllText(Path.Combine(store.RootDirectory, doc.FolderName, PowerTestFileContracts.EventLogFileName)), StringComparison.Ordinal);
    }

    [Fact]
    public void An_old_manifest_with_embedded_tare_samples_is_migrated_to_the_sidecar_once()
    {
        var store = new PowerTestStore(Path.Combine(_root, "Testes-Potencia"));
        var impeller = PowerImpellerCatalog.Create(ImpellerType.RushtonFlatBlade);
        var doc = store.CreateTest("Migracao", new FluidProperties(),
            new PowerGeometry { VesselDiameterM = 0.19, LiquidVolumeM3 = 0.01, Impellers = [impeller] },
            new PowerTestSettings(), [new PowerCondition { AgitationRpm = 300 }]);
        var folder = Path.Combine(store.RootDirectory, doc.FolderName);
        var samples = Enumerable.Range(0, 200)
            .Select(i => new TareSample(DateTimeOffset.UnixEpoch.AddSeconds(i), i, 300, 299.8, 1.5 + i * 0.001, TareCapturePhase.Accumulating, true, 1))
            .ToList();
        var tare = new TareCurve { Points = [new TarePoint(300, 0.5, 0.6)], Samples = samples, MeasuredUtc = DateTimeOffset.UnixEpoch };
        doc.Tare = tare;
        // Hand-write the pre-D-048 shape: samples embedded in both files, no sidecar.
        File.WriteAllText(Path.Combine(folder, PowerTestFileContracts.TestManifestFileName), PowerTestFileContracts.SerializeTestDocument(doc), Encoding.UTF8);
        File.WriteAllText(Path.Combine(folder, PowerTestFileContracts.TareFileName), PowerTestFileContracts.SerializeTare(tare), Encoding.UTF8);
        var manifestSizeBefore = new FileInfo(Path.Combine(folder, PowerTestFileContracts.TestManifestFileName)).Length;

        var loaded = store.LoadTest(doc.FolderName);

        Assert.NotNull(loaded?.Tare);
        Assert.Empty(loaded.Tare!.Samples);
        Assert.NotEmpty(loaded.Tare.RawSamplesFileName);
        Assert.Equal(200, store.LoadTareRawData(doc.FolderName, loaded.Tare.RawSamplesFileName).Count);
        var manifestSizeAfter = new FileInfo(Path.Combine(folder, PowerTestFileContracts.TestManifestFileName)).Length;
        Assert.True(manifestSizeAfter < manifestSizeBefore / 4, $"{manifestSizeBefore} -> {manifestSizeAfter}");
        Assert.DoesNotContain("\"targetRpm\"", File.ReadAllText(Path.Combine(folder, PowerTestFileContracts.TareFileName)), StringComparison.Ordinal);

        // Stable: a second load neither writes another sidecar nor changes the manifest.
        var sidecars = Directory.GetFiles(Path.Combine(folder, PowerTestFileContracts.TareRawDirectoryName)).Length;
        var manifestText = File.ReadAllText(Path.Combine(folder, PowerTestFileContracts.TestManifestFileName));
        var again = store.LoadTest(doc.FolderName);
        Assert.Equal(loaded.Tare.RawSamplesFileName, again!.Tare!.RawSamplesFileName);
        Assert.Equal(sidecars, Directory.GetFiles(Path.Combine(folder, PowerTestFileContracts.TareRawDirectoryName)).Length);
        Assert.Equal(manifestText, File.ReadAllText(Path.Combine(folder, PowerTestFileContracts.TestManifestFileName)));
    }

    [Fact]
    public async Task Kla_store_returns_the_hash_the_raw_file_will_have()
    {
        using var writer = new BackgroundFileWriter();
        var store = new KlaTestStore(Path.Combine(_root, "Testes-kLa"), writer);
        var doc = store.CreateTest("Fila kLa", new KlaTestSettings(), NitrogenValve.Valve1,
            initialConditions: [new KlaTestCondition { AgitationRpm = 300, AirflowLpm = 2 }]);
        var run = new KlaTestRun { ConditionId = doc.Conditions[0].ConditionId, ReplicateNumber = 1, AgitationRpm = 300, AirflowLpm = 2 };
        var folder = store.InitializeRunFolder(doc.FolderName, run);
        var points = Enumerable.Range(0, 30).Select(i => new KlaRawDataPoint(DateTimeOffset.UtcNow.AddSeconds(i), i, RunPhase.Reoxygenating, 10 + i, 10 + i, 2.0, 2.0, 300, true, false, false)).ToList();

        var sealedHash = store.SaveRunRawData(doc.FolderName, folder, points);
        await store.FlushAsync();

        Assert.Equal(KlaTestFileContracts.ComputeFileSha256(store.GetRunRawDataPath(doc.FolderName, folder)), sealedHash);
    }
}
