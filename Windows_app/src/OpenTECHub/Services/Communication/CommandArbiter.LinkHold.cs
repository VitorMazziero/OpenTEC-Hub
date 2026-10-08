using OpenTECHub.Protocol;

namespace OpenTECHub.Services.Communication;

/// <summary>
/// Link hold (D-065): a lost PC–Hub link no longer hands the oxygen controls and the recipe back to
/// Manual. The Hub keeps applying the last commands, so the cascade, the recipe and its reserved
/// assay or ramp keep their ownership, stop sending while the link is down and resume when it returns.
/// Only an explicit disconnect (state <see cref="ConnectionState.Disconnected"/>), a safety stop or the
/// operator taking the wire back returns them to Manual.
/// </summary>
public sealed partial class CommandArbiter
{
    private bool _linkHeld;
    private long _linkEpoch;

    /// <summary>True while held owners wait for the link to return; their dispatches are refused meanwhile.</summary>
    public bool IsLinkHeld { get { lock (_gate) return _linkHeld; } }

    /// <summary>Incremented each time a held link returns, so controllers can rebase across the gap.</summary>
    public long LinkEpoch { get { lock (_gate) return _linkEpoch; } }

    /// <summary>Raised with true when a hold starts and false when the link returns.</summary>
    public event Action<bool>? LinkHoldChanged;

    /// <summary>
    /// Owners that survive a link loss: the oxygen cascade and the recipe, plus anything working under a
    /// recipe reservation (autonomous kLa, ramps). Power and manual kLa assays keep their own safe abort.
    /// </summary>
    private bool HoldsThroughLinkLossUnderLock(ActuatorId actuator)
        => _ownership.GetValueOrDefault(actuator, CommandOwner.Manual) is CommandOwner.Automatic or CommandOwner.Recipe ||
            _reservedActuators.ContainsKey(actuator);

    private bool RefusesForLinkHoldUnderLock(CommandOwner requester)
        => _linkHeld && requester != CommandOwner.Manual && _inner.State != ConnectionState.Connected;

    private static bool IsLinkLoss(ConnectionStateChange change)
        => change.State is ConnectionState.Reconnecting or ConnectionState.Faulted or ConnectionState.Connecting;
}
