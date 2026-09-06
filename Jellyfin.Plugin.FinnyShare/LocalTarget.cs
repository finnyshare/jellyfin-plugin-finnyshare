using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.FinnyShare;

/// <summary>
/// Builds the local Jellyfin URL for a tunnelled request.
///
/// This is a trust boundary. The relay is remote and replaceable; the plugin runs inside
/// someone's home network. If a relay-supplied path could change the target host, the
/// plugin becomes an SSRF gateway and FinnyShare becomes a generic open proxy - which
/// plan §21 forbids outright.
///
/// Two concrete tricks this defeats, both verified by tests:
///   "@evil.com/x"  - concatenation makes "127.0.0.1:8096" userinfo and evil.com the host
///   "//evil.com/x" - protocol-relative, adopted as a new authority during URI resolution
/// </summary>
public static class LocalTarget
{
    /// <summary>
    /// Resolves a tunnelled WebSocket upgrade against the local base.
    ///
    /// Shares the HTTP validation deliberately. This path previously concatenated the
    /// relay-supplied path directly, which made it the one route out of the local endpoint -
    /// a WebSocket handshake is a real HTTP request, so it reached any host on the network
    /// and, against anything speaking WebSocket, opened a bidirectional pipe.
    /// </summary>
    public static bool TryResolveWebSocket(string localBase, string path, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;

        if (!TryResolve(localBase, path, out var http) || http is null)
        {
            return false;
        }

        // Scheme swapped on the parsed, already-validated Uri - never by string replacement,
        // which would also rewrite an occurrence anywhere else in the URL.
        uri = new UriBuilder(http) { Scheme = http.Scheme == Uri.UriSchemeHttps ? "wss" : "ws" }.Uri;
        return true;
    }

    /// <summary>
    /// Resolves a tunnelled path against the local base, or returns false if the result
    /// would address anything other than that exact endpoint.
    /// </summary>
    public static bool TryResolve(string localBase, string path, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;

        // Jellyfin is only ever asked for absolute paths. Anything else is not a request
        // we would generate, so there is no reason to be permissive about it.
        if (string.IsNullOrEmpty(path) || path[0] != '/')
        {
            return false;
        }

        // Protocol-relative: "//host" adopts a whole new authority.
        if (path.Length > 1 && (path[1] == '/' || path[1] == '\\'))
        {
            return false;
        }

        if (!Uri.TryCreate(localBase, UriKind.Absolute, out var base_))
        {
            return false;
        }

        if (!Uri.TryCreate(localBase.TrimEnd('/') + path, UriKind.Absolute, out var candidate))
        {
            return false;
        }

        // The decisive check: whatever the parser made of that string, it must still point
        // at exactly our endpoint. Credentials in the authority are never legitimate here.
        if (candidate.Scheme != base_.Scheme ||
            !string.Equals(candidate.Host, base_.Host, StringComparison.OrdinalIgnoreCase) ||
            candidate.Port != base_.Port ||
            !string.IsNullOrEmpty(candidate.UserInfo))
        {
            return false;
        }

        uri = candidate;
        return true;
    }
}
