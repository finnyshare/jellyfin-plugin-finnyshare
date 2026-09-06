namespace Jellyfin.Plugin.FinnyShare;

/// <summary>
/// Flow-control negotiation, settled in the tunnel handshake.
///
/// The plugin asks by sending the header; the relay confirms by echoing it. Both halves are
/// required because either side may be older than the other: a relay that never grants
/// credit would hang a waiting plugin on the first byte of every response, and a plugin that
/// ignores credit would have its streams cut off by a relay that enforces the window.
/// </summary>
public static class TunnelFlow
{
    /// <summary>Handshake header carrying the flow-control scheme.</summary>
    public const string Header = "X-FinnyShare-Flow";

    /// <summary>The only scheme there is: per-stream credit, as in HTTP/2.</summary>
    public const string Credit = "credit";

    /// <summary>
    /// Reports whether the relay confirmed credit flow control. Absent, unrecognised or
    /// uncollected headers all mean no, because sending freely degrades to today's
    /// behaviour whereas waiting for a grant that never arrives does not degrade at all.
    /// </summary>
    public static bool IsConfirmed(IReadOnlyDictionary<string, IEnumerable<string>>? headers)
    {
        if (headers is null)
        {
            return false;
        }

        // Header names are case-insensitive, and nothing promises which casing the runtime
        // hands back; a plain lookup would silently disable flow control against a relay
        // that supports it.
        foreach (var (name, values) in headers)
        {
            if (!string.Equals(name, Header, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var v in values)
            {
                if (string.Equals(v?.Trim(), Credit, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
