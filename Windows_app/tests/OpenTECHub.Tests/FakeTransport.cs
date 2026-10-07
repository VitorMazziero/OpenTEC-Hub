using System.Collections.Concurrent;
using OpenTECHub.Protocol;

namespace OpenTECHub.Tests;

/// <summary>
/// A scriptable <see cref="ITransport"/> for exercising the connection state machine.
/// </summary>
/// <remarks>
/// Reconnect cycling, silence detection and command requeue cannot be reproduced
/// reliably against real hardware - you cannot ask an ESP32 to stall its telemetry
/// task on cue. This can.
/// </remarks>
internal sealed class FakeTransport(TransportMedium medium = TransportMedium.Usb) : ITransport
{
    private readonly ConcurrentQueue<string> _inbox = new();

    public TransportMedium Medium { get; } = medium;

    public string Endpoint => "FAKE";

    public bool IsConnected { get; private set; }

    // ---- knobs -------------------------------------------------------

    /// <summary>When false, <see cref="ConnectAsync"/> reports failure.</summary>
    public bool ConnectSucceeds { get; set; } = true;

    /// <summary>When false, <see cref="WriteAsync"/> reports the device rejected it.</summary>
    public bool WriteSucceeds { get; set; } = true;
    public Func<string, CancellationToken, Task<bool>>? WriteBehavior { get; set; }

    /// <summary>Result of the liveness probe.</summary>
    public bool IsAlive { get; set; } = true;

    /// <summary>When set, <see cref="ConnectAsync"/> throws it.</summary>
    public Exception? ConnectThrows { get; set; }

    /// <summary>When set, <see cref="ReadAsync"/> throws it once.</summary>
    public Exception? NextReadThrows { get; set; }

    /// <summary>
    /// How long <see cref="ConnectAsync"/> stalls, honouring cancellation throughout —
    /// a real port open plus handshake is seconds, not the instant this fake usually is.
    /// </summary>
    public TimeSpan ConnectDuration { get; set; } = TimeSpan.Zero;

    /// <summary>Completes when a stalling <see cref="ConnectAsync"/> is entered.</summary>
    public Task StalledConnectEntered => _stalledConnect.Task;

    private readonly TaskCompletionSource _stalledConnect =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ---- observations ------------------------------------------------

    public List<string> Writes { get; } = [];

    public int ConnectCalls { get; private set; }

    public int LivenessProbeCalls { get; private set; }

    /// <summary>Queues a line for the next read, as if the device had sent it.</summary>
    public void Emit(string line) => _inbox.Enqueue(line);

    /// <summary>Queues a well-formed telemetry frame carrying the given device clock.</summary>
    public void EmitTelemetry(double timeSeconds)
        => Emit($$"""{"Time":{{timeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"Pressure":0}""");

    // ---- ITransport --------------------------------------------------

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ConnectCalls++;

        if (ConnectThrows is { } ex)
        {
            throw ex;
        }

        if (ConnectDuration > TimeSpan.Zero)
        {
            _stalledConnect.TrySetResult();
            await Task.Delay(ConnectDuration, cancellationToken).ConfigureAwait(false);
        }

        IsConnected = ConnectSucceeds;
        return ConnectSucceeds;
    }

    public Task DisconnectAsync()
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (NextReadThrows is { } ex)
        {
            NextReadThrows = null;
            throw ex;
        }

        // Null means "nothing new", exactly as the real transports report a 304 or
        // an empty serial buffer.
        return Task.FromResult(_inbox.TryDequeue(out var line) ? line : null);
    }

    public Task<bool> WriteAsync(string payload, CancellationToken cancellationToken = default)
    {
        lock (Writes)
        {
            Writes.Add(payload);
        }

        return WriteBehavior?.Invoke(payload, cancellationToken) ?? Task.FromResult(WriteSucceeds);
    }

    public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        LivenessProbeCalls++;
        return Task.FromResult(IsAlive);
    }

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }
}
