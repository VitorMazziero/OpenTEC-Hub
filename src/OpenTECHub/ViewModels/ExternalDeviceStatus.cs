using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenTECHub.ViewModels;

/// <summary>
/// The link state of one external Wi-Fi node, as the operator needs to read it.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from the flowmeter row, which was the only surface in the application telling
/// the truth about the device on the other end. Every other external device showed the
/// operator's own checkbox back at them. This type is that row's vocabulary, made shared.
/// </para>
/// <para>
/// It keeps <b>three states apart</b> that are easy to collapse into one and wrong to:
/// </para>
/// <list type="bullet">
/// <item><b>requested</b> — what the operator's switch says;</item>
/// <item><b>routed</b> (<see cref="CommEnabledOnHub"/>) — whether the Hub is forwarding to
/// the node, which it persists across its own reboots and can silently disagree about;</item>
/// <item><b>present</b> (<see cref="IsOnline"/>) — whether the node is actually answering.</item>
/// </list>
/// <para>
/// <see cref="HasTelemetry"/> guards all of it. Before the Hub has said anything about a
/// device, the honest reading is <i>awaiting telemetry</i>, never <i>offline</i>: an
/// operator must not be told a device failed when nothing has been claimed about it.
/// </para>
/// </remarks>
public sealed partial class ExternalDeviceStatus : ObservableObject
{
    /// <summary>
    /// How long a dispatch stays "pending" when the Hub offers no acknowledgement channel.
    /// </summary>
    /// <remarks>
    /// The flowmeter is acknowledged for real: the Hub retains its command until the v05
    /// echoes the matching <c>cmd_id</c>. The other nodes poll a consume-on-read mailbox
    /// every 2 s and never acknowledge, so this window is the only thing standing between
    /// a dispatch and the next one. Two and a half poll periods: long enough that a slow
    /// node is not raced, short enough that the operator is not left staring at a chip.
    /// </remarks>
    public static readonly TimeSpan AckFallbackWindow = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time;
    private DateTimeOffset? _dispatchedAt;

    /// <param name="displayName">Sentence-case device name, e.g. <c>Sensor de biomassa</c>.</param>
    /// <param name="genitiveName">
    /// The same name in the genitive, e.g. <c>do sensor de biomassa</c>, so the pt-BR status
    /// sentences read naturally without the caller assembling them.
    /// </param>
    /// <param name="timeProvider">Clock for the acknowledgement fallback; injectable for tests.</param>
    public ExternalDeviceStatus(string displayName, string genitiveName, TimeProvider? timeProvider = null)
    {
        DisplayName = displayName;
        GenitiveName = genitiveName;
        _time = timeProvider ?? TimeProvider.System;
    }

    public string DisplayName { get; }

    public string GenitiveName { get; }

    /// <summary>The Hub has reported on this device at least once since the link came up.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOffline))]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyPropertyChangedFor(nameof(ShowPendingChip))]
    [NotifyPropertyChangedFor(nameof(ShowRoutingChipOnly))]
    [NotifyPropertyChangedFor(nameof(HasStatusAlert))]
    [NotifyPropertyChangedFor(nameof(PresenceText))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial bool HasTelemetry { get; set; }

    /// <summary>The Hub is receiving pushes from the node inside its window.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOffline))]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyPropertyChangedFor(nameof(ShowPendingChip))]
    [NotifyPropertyChangedFor(nameof(ShowRoutingChipOnly))]
    [NotifyPropertyChangedFor(nameof(HasStatusAlert))]
    [NotifyPropertyChangedFor(nameof(PresenceText))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial bool IsOnline { get; set; }

    /// <summary>A command has left for the node and has not been confirmed yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyPropertyChangedFor(nameof(ShowPendingChip))]
    [NotifyPropertyChangedFor(nameof(ShowRoutingChipOnly))]
    [NotifyPropertyChangedFor(nameof(HasStatusAlert))]
    [NotifyPropertyChangedFor(nameof(PendingStatusText))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial bool IsAwaitingAck { get; set; }

    /// <summary>
    /// The Hub's own routing flag for this device, echoed back. Null when the Hub does not
    /// publish it — an unflashed Hub, where the app genuinely cannot know.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCommMismatch))]
    [NotifyPropertyChangedFor(nameof(ShowRoutingChipOnly))]
    [NotifyPropertyChangedFor(nameof(CommMismatchText))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial bool? CommEnabledOnHub { get; set; }

    /// <summary>What the operator's own enable switch currently says.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCommMismatch))]
    [NotifyPropertyChangedFor(nameof(ShowRoutingChipOnly))]
    [NotifyPropertyChangedFor(nameof(CommMismatchText))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial bool IsCommRequested { get; set; }

    /// <summary>Known absent, as opposed to not yet reported on.</summary>
    public bool IsOffline => HasTelemetry && !IsOnline;

    /// <summary>The row may accept an order that can actually leave over the wire.</summary>
    /// <remarks>
    /// Blocked by a <i>known</i> absence and by an unconfirmed command, never by the mere
    /// lack of evidence. Blocking on <see cref="HasTelemetry"/> would deadlock every node
    /// that only reports while it is working: the biomass sensor pushes nothing at all when
    /// idle, so requiring telemetry before accepting a command would mean the operator can
    /// never press Start — the one command that would produce the telemetry.
    /// </remarks>
    public bool CanSend => !IsOffline && !IsAwaitingAck;

    public bool ShowPendingChip => !IsOffline && IsAwaitingAck;

    public bool HasStatusAlert => HasTelemetry && (IsOffline || IsAwaitingAck);

    /// <summary>
    /// The Hub and the operator's switch disagree about whether this device is routed.
    /// </summary>
    /// <remarks>
    /// The Hub persists its routing flags in NVS while the app persists the operator's
    /// switches on the PC. After a Hub reboot the two can differ, and every sub-command for
    /// a device the Hub is not routing is dropped without a word. This is what makes that
    /// visible.
    /// </remarks>
    public bool HasCommMismatch => CommEnabledOnHub is { } hub && hub != IsCommRequested;

    /// <summary>
    /// Show the routing chip only when nothing more severe is already showing.
    /// </summary>
    /// <remarks>
    /// A row shows <b>one</b> chip. Two of them overflow the device-name column and collide with
    /// the reading beside it — measured at 1280 px with a name as long as
    /// <i>Bomba Dosadora Externa</i>. The precedence is the same one <see cref="StatusText"/>
    /// uses, and the tooltip carries the full sentence, so nothing is lost: a device that is both
    /// absent and misrouted reads as absent, which is the condition to act on first.
    /// </remarks>
    public bool ShowRoutingChipOnly => HasCommMismatch && !IsOffline && !ShowPendingChip;

    public string PresenceText => !HasTelemetry
        ? "Aguardando telemetria do Hub"
        : IsOnline
            ? "Online"
            : $"{DisplayName} desconectado da Central";

    public string PendingStatusText => IsAwaitingAck
        ? $"Aguardando confirmação {GenitiveName}..."
        : "Nenhum comando pendente.";

    public string CommMismatchText => CommEnabledOnHub switch
    {
        true => $"O Hub está roteando {GenitiveName}, mas o comando local está desligado.",
        false => $"O Hub não está roteando {GenitiveName}; os comandos serão descartados.",
        null => "",
    };

    /// <summary>One sentence for the row, worst condition first.</summary>
    public string StatusText => IsOffline
        ? PresenceText
        : IsAwaitingAck
            ? PendingStatusText
            : HasCommMismatch
                ? CommMismatchText
                : !HasTelemetry
                    ? PresenceText
                    : $"{DisplayName} online; comandos liberados.";

    /// <summary>Locks the row before the asynchronous transport returns.</summary>
    public void MarkCommandDispatched()
    {
        _dispatchedAt = _time.GetUtcNow();
        IsAwaitingAck = true;
    }

    /// <summary>Clears the pending lock without waiting, after a refused dispatch.</summary>
    /// <remarks>
    /// A frame the arbiter refused never reached the wire, so there is nothing to confirm
    /// and the operator must get the controls straight back.
    /// </remarks>
    public void MarkCommandRefused()
    {
        _dispatchedAt = null;
        IsAwaitingAck = false;
    }

    /// <summary>
    /// Separates loss of the app-to-Hub link from an outage of this device.
    /// </summary>
    /// <remarks>
    /// When the Hub itself is unreachable, nothing is known about the node behind it. Saying
    /// "offline" would be a claim the app cannot support, so it drops back to no evidence.
    /// </remarks>
    public void MarkHubUnavailable()
    {
        HasTelemetry = false;
        IsOnline = false;
        IsAwaitingAck = false;
        CommEnabledOnHub = null;
        _dispatchedAt = null;
    }

    /// <summary>Folds one telemetry frame into the device's state.</summary>
    /// <param name="hasTelemetry">The Hub said something about this device.</param>
    /// <param name="online">The Hub considers the node present.</param>
    /// <param name="pending">
    /// The Hub's pending flag, or null when it has no acknowledgement channel for this
    /// device — in which case <see cref="AckFallbackWindow"/> releases the lock instead.
    /// </param>
    /// <param name="commEnabled">The Hub's routing flag, or null when it does not publish one.</param>
    public void Update(bool hasTelemetry, bool online, bool? pending, bool? commEnabled)
    {
        HasTelemetry = hasTelemetry;
        IsOnline = online;
        CommEnabledOnHub = commEnabled;

        if (pending is { } reported)
        {
            // The Hub tracks this device's mailbox; its answer outranks any local guess.
            IsAwaitingAck = reported;
            _dispatchedAt = reported ? _dispatchedAt ?? _time.GetUtcNow() : null;
            return;
        }

        if (IsAwaitingAck &&
            (_dispatchedAt is not { } at || _time.GetUtcNow() - at >= AckFallbackWindow))
        {
            IsAwaitingAck = false;
            _dispatchedAt = null;
        }
    }
}
