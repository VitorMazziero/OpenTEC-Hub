namespace TecnalHub.Protocol;

/// <summary>Which physical link a transport uses.</summary>
public enum TransportMedium
{
    Usb,
    WiFi,
}

/// <summary>
/// The physical link contract: connect, disconnect, read, write, probe.
/// </summary>
/// <remarks>
/// <para>
/// A transport is responsible <b>only</b> for moving bytes. No state management, no
/// retry policy, no reconnection - that is <c>ConnectionManager</c>'s job.
/// </para>
/// <para>
/// <b>Ownership:</b> exactly one component may call these members, and it is
/// <c>ConnectionManager</c>. v.6 had two threads reaching for the link and relied on
/// a lock to stay correct while its own docstring described a different contract
/// (see <c>docs/MIGRATION.md</c> section 3, item 9). Here there is one owner.
/// </para>
/// </remarks>
public interface ITransport : IAsyncDisposable
{
    /// <summary>Which medium this transport drives.</summary>
    TransportMedium Medium { get; }

    /// <summary>Human-readable endpoint, e.g. <c>COM7</c> or <c>192.168.4.1</c>.</summary>
    string Endpoint { get; }

    /// <summary>True between a successful <see cref="ConnectAsync"/> and disconnection.</summary>
    bool IsConnected { get; }

    /// <summary>
    /// Opens the link and completes the handshake.
    /// </summary>
    /// <returns>True when the device answered <c>OK</c>.</returns>
    Task<bool> ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Releases everything unconditionally. Must not throw.</summary>
    Task DisconnectAsync();

    /// <summary>
    /// Returns the newest line available, or null when there is nothing new.
    /// </summary>
    /// <remarks>
    /// Null is <b>not</b> an error: on Wi-Fi it is the normal answer to a 304, and on
    /// USB it means the buffer was empty. A genuine fault throws
    /// <see cref="TransportFaultException"/> so the state machine can react, rather
    /// than being flattened into the same null v.6 returned for both cases.
    /// </remarks>
    Task<string?> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Sends one JSON payload. Framing is the transport's business.</summary>
    /// <returns>True when the device accepted it.</returns>
    Task<bool> WriteAsync(string payload, CancellationToken cancellationToken = default);

    /// <summary>Cheap liveness probe, called about once a second by the heartbeat.</summary>
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A real I/O fault, as distinct from "nothing to read".
/// </summary>
/// <remarks>
/// Exists specifically to fix v.6's Wi-Fi <c>read()</c>, which returned null for both
/// "no new data" and "the network threw", making link loss invisible until a separate
/// heartbeat happened to notice. See <c>docs/MIGRATION.md</c> section 3, item 7.
/// </remarks>
public sealed class TransportFaultException(string message, Exception? innerException = null)
    : Exception(message, innerException);
