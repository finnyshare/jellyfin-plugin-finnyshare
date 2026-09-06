namespace Jellyfin.Plugin.FinnyShare;

/// <summary>
/// Hop-by-hop headers describe a single connection, not the message, so they must not be
/// forwarded across the tunnel (RFC 9110 §7.6.1).
///
/// Getting this wrong is not cosmetic: forwarding Transfer-Encoding alongside a re-chunked
/// body invites request smuggling, and forwarding Connection/Upgrade confuses the local
/// WebSocket handshake.
/// </summary>
public static class HopByHop
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection",
        "keep-alive",
        "transfer-encoding",
        "upgrade",
        "proxy-authenticate",
        "proxy-authorization",
        "te",
        "trailer",

        // Not hop-by-hop per the RFC, but the tunnel rewrites it: the plugin addresses
        // Jellyfin locally, so a forwarded Host would point at the public name.
        "host",
    };

    /// <summary>Reports whether a header must be dropped rather than forwarded.</summary>
    public static bool Contains(string name) => Names.Contains(name);
}
