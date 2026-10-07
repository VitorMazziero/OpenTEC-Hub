using System.IO;
using OpenTECHub.Services.Persistence;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class DurableWriterTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Durable_barrier_propagates_prior_failure_without_poisoning_an_independent_session(bool synchronous)
    {
        var root = Path.Combine(Path.GetTempPath(), "opentechub-durable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var writer = new BackgroundFileWriter(synchronous);
            var failed = Path.Combine(root, "failed"); var healthy = Path.Combine(root, "healthy");
            Directory.CreateDirectory(failed);
            File.WriteAllText(Path.Combine(failed, "obstacle"), "file instead of directory");
            writer.WriteAllTextAtomic(Path.Combine(failed, "obstacle", "result.json"), "{}");
            await writer.FlushAsync(); // Legacy flush intentionally does not claim durability.
            await Assert.ThrowsAsync<AggregateException>(() => writer.FlushDurableAsync(failed));
            writer.AppendLine(Path.Combine(healthy, "raw.csv"), "1,2", "time,value");
            writer.WriteAllTextAtomic(Path.Combine(healthy, "result.json"), "{}");
            await writer.FlushDurableAsync(healthy);
            Assert.Contains("1,2", File.ReadAllText(Path.Combine(healthy, "raw.csv")));
            await Assert.ThrowsAsync<AggregateException>(() => writer.FlushDurableAsync(failed));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Durable_barrier_uses_directory_boundaries_and_closes_append_handles()
    {
        var root = Path.Combine(Path.GetTempPath(), "opentechub-durable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var writer = new BackgroundFileWriter();
            var session = Path.Combine(root, "session");
            writer.Run(Path.Combine(root, "session-other", "failure"), () => throw new IOException("controlled"));
            var raw = Path.Combine(session, "raw.csv");
            writer.AppendLine(raw, "1,2");
            await writer.FlushDurableAsync(session);
            using var exclusive = new FileStream(raw, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.True(exclusive.Length > 0);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
