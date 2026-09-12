namespace OpenTECHub.Protocol;

/// <summary>
/// Who is on the other end of one external Wi-Fi link, as the Hub's node registry knows it.
/// </summary>
/// <remarks>
/// <para>
/// The Hub extracts <see cref="Ip"/> from the node's TCP connection, so it is the address
/// that actually answers - never one the node declared. <see cref="FirmwareVersion"/> and
/// <see cref="Mac"/> are what the node sent in its <c>/nodeHello</c>. See
/// <c>docs/PROTOCOL.md</c> section 2.0.2.
/// </para>
/// <para>
/// Every member is nullable and null means <i>unknown</i>: a Hub older than 10.1, a node
/// that has not registered yet, or the Hub's <c>0.0.0.0</c> "never seen" sentinel. Unknown
/// is not a fault; nothing here says whether the node is present.
/// </para>
/// </remarks>
public sealed record ExternalNodeIdentity(string? Ip, string? Mac, string? FirmwareVersion)
{
    /// <summary>The Hub's "never seen" address.</summary>
    public const string UnassignedIp = "0.0.0.0";

    public static readonly ExternalNodeIdentity Empty = new(null, null, null);

    /// <summary>At least one of the three is known.</summary>
    public bool IsKnown => Ip is not null || Mac is not null || FirmwareVersion is not null;

    /// <summary>The Hub has an address for the node, so it can be reached from the Hub's network.</summary>
    public bool IsReachable => Ip is not null;

    /// <summary>
    /// Normalises one wire value: blank, whitespace and the unassigned address all become null.
    /// </summary>
    public static string? Normalize(string? wire)
    {
        if (string.IsNullOrWhiteSpace(wire))
        {
            return null;
        }

        var trimmed = wire.Trim();
        return trimmed == UnassignedIp ? null : trimmed;
    }
}
