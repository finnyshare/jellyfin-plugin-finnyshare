using System.Text;

namespace Jellyfin.Plugin.FinnyShare;

/// <summary>
/// Cleans the Jellyfin server's display name for transport.
///
/// The name is set by the server's owner and is sent to the relay as a handshake header, so it
/// crosses a trust boundary twice: a CR or LF would let it forge additional headers, and an
/// unbounded value would let any installation push arbitrary bulk into the control plane.
/// </summary>
public static class ServerName
{
    /// <summary>Longest name transported. Comfortably past any reasonable server name.</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// Returns a header-safe name, or an empty string when there is nothing usable - in which
    /// case the dashboard falls back to the hostname it assigned.
    /// </summary>
    public static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var clean = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            // Printable characters only: this drops CR, LF, NUL and tab together rather than
            // blocklisting them one at a time and missing one.
            if (!char.IsControl(c))
            {
                clean.Append(c);
            }

            if (clean.Length >= MaxLength)
            {
                break;
            }
        }

        return clean.ToString().Trim();
    }
}
