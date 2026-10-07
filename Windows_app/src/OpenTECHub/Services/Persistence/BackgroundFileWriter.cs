using System.IO;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace OpenTECHub.Services.Persistence;

/// <summary>
/// Ordered, single-consumer file writer that takes assay I/O off the UI thread (D-048).
/// </summary>
/// <remarks>
/// <para>
/// Every operation is queued and executed by one long-running consumer in the order it was
/// enqueued, so the ordering that a per-store <c>lock</c> gave the synchronous writes is kept
/// exactly: an append enqueued before an atomic rewrite lands before it, and a hash computed by
/// a queued <see cref="Run"/> sees every append queued before it.
/// </para>
/// <para>
/// Appends share one <see cref="StreamWriter"/> per file across telemetry frames. Keeping the
/// handle open removes the open/write/close cycle that previously occurred every time the queue
/// drained. Readers that need a consistent snapshot call <see cref="Flush"/>, which closes the
/// append handles before reading.
/// </para>
/// <para>
/// A writer built with <c>synchronous: true</c> executes each operation inline on the caller and
/// keeps no file open: that is the behaviour the stores had before, and what tests that read the
/// files back immediately rely on. <see cref="Flush"/> is then a no-op.
/// </para>
/// <para>
/// A failed operation is logged and reported through <see cref="WriteFailed"/>; the consumer
/// moves on to the next item rather than stopping, because the next frame's sample is data too.
/// </para>
/// </remarks>
public sealed class BackgroundFileWriter : IDisposable
{
    /// <summary>Maximum normal interval between test-data stream flushes.</summary>
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);
    private readonly bool _synchronous;
    private readonly ILogger? _log;
    private readonly Channel<WorkItem>? _queue;
    private readonly Task? _consumer;
    private readonly object _syncGate = new();
    private readonly Dictionary<string, OpenWriter> _writers = new(StringComparer.OrdinalIgnoreCase);
    private long _lastFlushTimestamp;
    private bool _disposed;
    private readonly Dictionary<string, Exception> _writeFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _writtenFiles = new(StringComparer.OrdinalIgnoreCase);

    public BackgroundFileWriter(bool synchronous = false, ILogger? logger = null)
    {
        _synchronous = synchronous;
        _log = logger;
        if (!synchronous)
        {
            _queue = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
            _consumer = Task.Factory.StartNew(ConsumeAsync, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            _lastFlushTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

    public bool IsSynchronous => _synchronous;

    /// <summary>Raised on the consumer thread when an operation throws. The path is the file involved.</summary>
    public event Action<string, Exception>? WriteFailed;

    /// <summary>
    /// Appends <paramref name="line"/> plus a newline. When <paramref name="headerIfEmpty"/> is
    /// given and the file is missing or empty, the header line is written first — the same
    /// contract the stores implemented with <c>File.Exists</c> + <c>FileInfo.Length</c>.
    /// Encoding is UTF-8 with the BOM emitted only at the start of a new file, byte for byte what
    /// <c>File.AppendAllText(path, text, Encoding.UTF8)</c> produced.
    /// </summary>
    public void AppendLine(string path, string line, string? headerIfEmpty = null)
        => Enqueue(new WorkItem(WorkKind.Append, path, line, headerIfEmpty, null, null));

    /// <summary>Writes the whole file through a temp file and a move, retrying the move briefly.</summary>
    public void WriteAllTextAtomic(string path, string contents)
        => Enqueue(new WorkItem(WorkKind.WriteAtomic, path, contents, null, null, null));

    /// <summary>Deletes the file if present (closing an append writer on it first).</summary>
    public void Delete(string path)
        => Enqueue(new WorkItem(WorkKind.Delete, path, null, null, null, null));

    /// <summary>Runs arbitrary work in queue order, after everything enqueued before it has landed and been closed.</summary>
    public void Run(string pathForDiagnostics, Action work)
        => Enqueue(new WorkItem(WorkKind.Run, pathForDiagnostics, null, null, work, null));

    /// <summary>Closes every open append writer whose path starts with <paramref name="pathPrefix"/> (queue-ordered).</summary>
    public void CloseWriters(string pathPrefix)
        => Enqueue(new WorkItem(WorkKind.CloseWriters, pathPrefix, null, null, null, null));

    /// <summary>
    /// Blocks until everything enqueued so far has been executed and the open writers flushed.
    /// A read that must see the latest write calls this first. No-op when synchronous.
    /// </summary>
    public void Flush()
    {
        if (_synchronous || _disposed)
        {
            return;
        }

        FlushAsync().GetAwaiter().GetResult();
    }

    public Task FlushAsync()
    {
        if (_synchronous || _disposed)
        {
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(new WorkItem(WorkKind.Flush, "", null, null, null, completion));
        return completion.Task;
    }

    /// <summary>Queue-ordered durable barrier for one directory. Earlier failures remain fatal for that scope.</summary>
    public Task FlushDurableAsync(string directory)
    {
        var scope = Path.GetFullPath(directory);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(new WorkItem(WorkKind.Durable, scope, null, null, null, completion));
        return completion.Task;
    }

    private static bool InDirectory(string path, string directory)
        => string.Equals(Path.GetFullPath(path), directory, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFullPath(path).StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private void FlushDurable(string directory)
    {
        var failures = _writeFailures.Where(pair => InDirectory(pair.Key, directory)).Select(pair => pair.Value).ToArray();
        if (failures.Length > 0) throw new AggregateException("Gravação anterior falhou nesta sessão; durabilidade não confirmada.", failures);
        foreach (var path in _writers.Keys.Where(path => InDirectory(path, directory)).ToArray())
        {
            var open = _writers[path];
            open.Writer.Flush();
            ((FileStream)open.Writer.BaseStream).Flush(flushToDisk: true);
            CloseWriter(path);
        }
        foreach (var path in _writtenFiles.Where(path => InDirectory(path, directory)))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            stream.Flush(flushToDisk: true);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_queue is not null)
        {
            try
            {
                FlushAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // The consumer reports its own failures.
            }
            _queue.Writer.TryComplete();
            try
            {
                _consumer?.Wait(TimeSpan.FromSeconds(10));
            }
            catch
            {
                // Best effort at shutdown.
            }
        }

        lock (_syncGate)
        {
            CloseAllWriters();
        }

        _disposed = true;
    }

    // ------------------------------------------------------------------ queue

    private void Enqueue(WorkItem item)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_synchronous)
        {
            lock (_syncGate)
            {
                Execute(item);
                // Synchronous mode keeps nothing open: what the caller wrote is on disk when it returns.
                CloseAllWriters();
            }
            return;
        }

        if (!_queue!.Writer.TryWrite(item))
        {
            throw new InvalidOperationException("O escritor de arquivos já foi encerrado.");
        }
    }

    private async Task ConsumeAsync()
    {
        var reader = _queue!.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var item))
            {
                Execute(item);
                // Keep append handles open across telemetry frames.  The old drain-time close
                // caused an open/write/close cycle for every frame (normally every 2 seconds).
                // FlushDue below provides bounded durability without repeating that metadata work.
            }
        }

        CloseAllWriters();
    }

    private void Execute(WorkItem item)
    {
        try
        {
            switch (item.Kind)
            {
                case WorkKind.Append:
                    Append(item.Path, item.Text!, item.Header);
                    break;
                case WorkKind.WriteAtomic:
                    CloseWriter(item.Path);
                    WriteAtomic(item.Path, item.Text!);
                    break;
                case WorkKind.Delete:
                    CloseWriter(item.Path);
                    if (File.Exists(item.Path))
                    {
                        File.Delete(item.Path);
                    }
                    break;
                case WorkKind.Run:
                    // Arbitrary work may read what was appended: hand it closed files.
                    CloseAllWriters();
                    item.Work!();
                    break;
                case WorkKind.CloseWriters:
                    CloseWritersUnder(item.Path);
                    break;
                case WorkKind.Flush:
                    CloseAllWriters();
                    item.Completion!.TrySetResult();
                    break;
                case WorkKind.Durable:
                    FlushDurable(item.Path);
                    item.Completion!.TrySetResult();
                    break;
            }
            if (item.Kind is WorkKind.Append or WorkKind.WriteAtomic) _writtenFiles.Add(Path.GetFullPath(item.Path));
            else if (item.Kind == WorkKind.Delete) _writtenFiles.Remove(Path.GetFullPath(item.Path));
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrEmpty(item.Path))
                _writeFailures[Path.GetFullPath(item.Path)] = ex;
            item.Completion?.TrySetException(ex);
            _log?.LogError(ex, "Falha ao gravar {Path}", item.Path);
            try
            {
                WriteFailed?.Invoke(item.Path, ex);
            }
            catch
            {
                // A failing subscriber must not take the consumer down.
            }
        }
    }

    // ------------------------------------------------------------------ files

    private void Append(string path, string line, string? headerIfEmpty)
    {
        if (!_writers.TryGetValue(path, out var open))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096);
            var writer = new StreamWriter(stream, Encoding.UTF8);
            open = new OpenWriter(writer, stream.Position == 0);
            _writers[path] = open;
        }

        if (open.IsEmpty && headerIfEmpty is not null)
        {
            open.Writer.Write(headerIfEmpty);
            open.Writer.Write(Environment.NewLine);
        }

        open.Writer.Write(line);
        open.Writer.Write(Environment.NewLine);
        open.IsEmpty = false;
        FlushDue();
    }

    private void FlushDue()
    {
        if (System.Diagnostics.Stopwatch.GetElapsedTime(_lastFlushTimestamp) < FlushInterval)
        {
            return;
        }

        foreach (var open in _writers.Values)
        {
            open.Writer.Flush();
        }

        _lastFlushTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    private static void WriteAtomic(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tempPath, contents, Encoding.UTF8);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(tempPath, path, overwrite: true);
                    return;
                }
                catch (Exception) when (attempt < 5)
                {
                    // Off the UI thread now, so a short back-off for a reader holding the file is harmless.
                    Thread.Sleep(20);
                }
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    private void CloseWriter(string path)
    {
        if (_writers.Remove(path, out var open))
        {
            open.Writer.Dispose();
        }
    }

    private void CloseWritersUnder(string pathPrefix)
    {
        foreach (var path in _writers.Keys.Where(p => p.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            CloseWriter(path);
        }
    }

    private void CloseAllWriters()
    {
        foreach (var entry in _writers)
        {
            try { entry.Value.Writer.Dispose(); }
            catch (Exception ex)
            {
                _writeFailures[Path.GetFullPath(entry.Key)] = ex;
                _log?.LogError(ex, "Falha ao fechar {Path}", entry.Key);
                try { WriteFailed?.Invoke(entry.Key, ex); } catch { }
            }
        }
        _writers.Clear();
        _lastFlushTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    private enum WorkKind { Append, WriteAtomic, Delete, Run, CloseWriters, Flush, Durable }

    private sealed record WorkItem(WorkKind Kind, string Path, string? Text, string? Header, Action? Work, TaskCompletionSource? Completion);

    private sealed class OpenWriter(StreamWriter writer, bool isEmpty)
    {
        public StreamWriter Writer { get; } = writer;
        public bool IsEmpty { get; set; } = isEmpty;
    }
}
