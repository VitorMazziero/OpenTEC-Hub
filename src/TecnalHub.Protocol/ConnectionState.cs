namespace TecnalHub.Protocol;

/// <summary>
/// Link lifecycle.
/// </summary>
/// <remarks>
/// <code>
/// Disconnected --(connect)------------------> Connecting
/// Connecting   --(handshake ok)-------------> Connected
/// Connecting   --(failed, backup on)--------> Reconnecting
/// Connecting   --(failed, backup off)-------> Faulted
/// Connected    --(link lost, backup on)-----> Reconnecting
/// Connected    --(link lost, backup off)----> Faulted
/// Connected    --(user disconnect)----------> Disconnected
/// Reconnecting --(handshake ok)-------------> Connected
/// Reconnecting --(user disconnect)----------> Disconnected
/// Faulted      --(user connect)-------------> Connecting
/// any          --(shutdown)-----------------> Disconnected
/// </code>
/// </remarks>
public enum ConnectionState
{
    /// <summary>No transport, no retries scheduled.</summary>
    Disconnected,

    /// <summary>Handshake in progress.</summary>
    Connecting,

    /// <summary>Link up, telemetry flowing.</summary>
    Connected,

    /// <summary>Link down, automatic retry loop running.</summary>
    Reconnecting,

    /// <summary>Link down and no retry scheduled, because backup is disabled.</summary>
    Faulted,
}

/// <summary>A state transition, published to observers.</summary>
/// <param name="State">The state just entered.</param>
/// <param name="Medium">Which link it applies to.</param>
/// <param name="Endpoint">Port name or IP address.</param>
/// <param name="Reason">Human-readable cause; empty for routine transitions.</param>
public readonly record struct ConnectionStateChange(
    ConnectionState State,
    TransportMedium? Medium,
    string Endpoint,
    string Reason = "");

/// <summary>Counters for the connection popover and for diagnosing field problems.</summary>
public sealed record LinkDiagnostics
{
    public int FramesReceived { get; init; }
    public int CommandsSent { get; init; }
    public int ParseFailures { get; init; }
    public int DeviceLogLines { get; init; }

    /// <summary>Bare <c>OK</c> lines seen; on USB these share the telemetry stream.</summary>
    public int CommandAcks { get; init; }
    public int ConnectAttemptsUsb { get; init; }
    public int ConnectAttemptsWiFi { get; init; }
    public double? LastRoundTripMs { get; init; }
    public string LastError { get; init; } = "";
    public DateTimeOffset? LastFrameAt { get; init; }
}
