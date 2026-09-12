namespace OpenTECHub.Protocol;

/// <summary>
/// Orders what a serial drain produced: telemetry frames collapse to the newest one,
/// every other line is queued and handed out one per read, oldest first.
/// </summary>
/// <remarks>
/// <para>
/// A frame that arrived before a newer frame is worthless - the reader wants the
/// present, not the backlog (v.6 semantics, <c>docs/PROTOCOL.md</c> §1.2). A
/// <c>{"NodeDiag":…}</c> answer, an <c>OK</c> ack or an <c>[ESP32_</c> log line is
/// not superseded by anything: each one is a message in its own right, and the Hub
/// emits five <c>NodeDiag</c> lines in one burst for <c>{"nodeDiag":"all"}</c>.
/// </para>
/// <para>
/// The queue is bounded so a Hub stuck logging cannot grow it without limit; the
/// oldest side line is dropped first.
/// </para>
/// </remarks>
public sealed class SerialLineCoalescer
{
    private const int MaxQueuedSideLines = 64;
    private const string NodeDiagPrefix = "{\"NodeDiag\"";

    private readonly Queue<string> _side = new();
    private string? _newestFrame;

    /// <summary>Lines waiting to be taken.</summary>
    public int Pending => (_newestFrame is null ? 0 : 1) + _side.Count;

    /// <summary>A JSON object that is not a <c>NodeDiag</c> envelope is a telemetry frame.</summary>
    public static bool IsTelemetryFrame(string line)
        => line.StartsWith('{') && !line.StartsWith(NodeDiagPrefix, StringComparison.Ordinal);

    public void Push(string line)
    {
        if (IsTelemetryFrame(line))
        {
            _newestFrame = line;
            return;
        }

        if (_side.Count >= MaxQueuedSideLines)
        {
            _side.Dequeue();
        }
        _side.Enqueue(line);
    }

    /// <summary>The newest frame first, then the side lines in arrival order; null when empty.</summary>
    public string? Take()
    {
        if (_newestFrame is { } frame)
        {
            _newestFrame = null;
            return frame;
        }

        return _side.TryDequeue(out var side) ? side : null;
    }
}
