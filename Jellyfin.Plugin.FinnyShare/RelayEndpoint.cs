namespace Jellyfin.Plugin.FinnyShare;

/// <summary>
/// Resolves the relay endpoint the plugin dials.
///
/// This is a trust boundary. The installation credential travels as an Authorization header
/// on the tunnel handshake, so a plaintext ws:// endpoint puts that credential on the wire in
/// clear. Plaintext therefore has to be opted into explicitly for local development; a
/// misconfiguration must fail closed rather than quietly downgrade.
/// </summary>
public static class RelayEndpoint
{
    /// <summary>The endpoint used when nothing is configured. Always encrypted.</summary>
    public const string Default = Constants.DefaultRelayUrl;

    /// <summary>
    /// Parses and validates a control-plane URL. The installation token is *received* over
    /// this connection and the installation credential is sent over it, so it needs the same fail-closed
    /// treatment as the relay: plaintext must be a deliberate choice, not a typo.
    /// </summary>
    public static bool TryResolveControl(string url, bool allowInsecure, out Uri? uri)
    {
        uri = null;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var candidate))
        {
            return false;
        }

        if (candidate.Scheme == Uri.UriSchemeHttps)
        {
            uri = candidate;
            return true;
        }

        if (candidate.Scheme == Uri.UriSchemeHttp && allowInsecure)
        {
            uri = candidate;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses and validates a relay URL. Returns false for anything that is not a WebSocket
    /// endpoint, and for plaintext ws:// unless insecure transport was explicitly allowed.
    /// </summary>
    public static bool TryResolve(string url, bool allowInsecure, out Uri? uri)
    {
        uri = null;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var candidate))
        {
            return false;
        }

        var secure = candidate.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase);
        var insecure = candidate.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase);

        if (!secure && !insecure)
        {
            return false;
        }

        if (insecure && !allowInsecure)
        {
            return false;
        }

        uri = candidate;
        return true;
    }
}
