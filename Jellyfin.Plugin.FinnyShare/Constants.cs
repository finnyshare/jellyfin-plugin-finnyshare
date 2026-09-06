namespace Jellyfin.Plugin.FinnyShare;

/// <summary>
/// Every tunable in one place: endpoints, paths, sizes and timings.
///
/// Nothing else in the plugin should hard-code a URL or a duration. Changing where the
/// service lives should be a change to this file alone.
/// </summary>
public static class Constants
{
    /// <summary>Plugin identity. The GUID is also referenced by the settings page.</summary>
    public const string PluginName = "FinnyShare";

    public const string PluginGuid = "8b7a2f1e-3c4d-4a5b-9e6f-1d2c3b4a5e6f";

    // ---- endpoints -----------------------------------------------------------------

    /// <summary>Relay the plugin dials. Must be wss:// - see RelayEndpoint.</summary>
    public const string DefaultRelayUrl = "wss://finnyshare.space/tunnel";

    /// <summary>Control plane and console. One domain for the whole service.</summary>
    public const string DefaultControlUrl = "https://finnyshare.space";

    /// <summary>The Jellyfin this plugin fronts. Local only, always.</summary>
    public const string DefaultLocalUrl = "http://127.0.0.1:8096";

    // ---- control-plane paths -------------------------------------------------------

    /// <summary>Self-registration: the whole onboarding, with no human step.</summary>
    public const string RegisterPath = "/v1/register";

    /// <summary>Assigns a fresh random address.</summary>
    public const string RandomLabelPath = "/v1/label/random";

    /// <summary>Turns sharing on or off without uninstalling the plugin.</summary>
    public const string DisabledPath = "/v1/disabled";

    /// <summary>Console page for choosing a custom address.</summary>
    public const string RenamePath = "/app/rename";

    // ---- environment variables -----------------------------------------------------

    public const string EnvRelay = "FINNYSHARE_RELAY";
    public const string EnvControl = "FINNYSHARE_CONTROL";
    public const string EnvControlPublic = "FINNYSHARE_CONTROL_PUBLIC";
    public const string EnvLocal = "FINNYSHARE_LOCAL";
    public const string EnvToken = "FINNYSHARE_TOKEN";
    public const string EnvAllowInsecure = "FINNYSHARE_ALLOW_INSECURE";

    /// <summary>Named HttpClient used for proxying; configured to not follow redirects.</summary>
    public const string HttpClientName = "FinnyShare";

    /// <summary>Handshake header carrying this Jellyfin's display name.</summary>
    public const string ServerNameHeader = "X-FinnyShare-Server-Name";

    // ---- transport -----------------------------------------------------------------

    /// <summary>Streaming chunk size. Media is never buffered whole.</summary>
    public const int ChunkSize = 64 * 1024;

    /// <summary>
    /// Largest tunnel message accepted. The peer controls WebSocket fragmentation, so
    /// without a ceiling it can grow our assembly buffer until the host runs out of memory.
    /// </summary>
    public const int MaxMessageBytes = (2 * ChunkSize) + 1024;

    /// <summary>Request-body chunks queued per stream before the stream is reset.</summary>
    public const int BodyQueueDepth = 32;

    /// <summary>WebSocket frames queued per stream before the stream is reset.</summary>
    public const int WsQueueDepth = 64;

    /// <summary>
    /// Concurrent streams one tunnel may carry. Each costs a task, a cancellation source and
    /// two queues, and every stream opens a connection to the local Jellyfin - so an
    /// unbounded count turns this plugin into a denial-of-service tool against its own server.
    /// </summary>
    public const int MaxConcurrentStreams = 256;

    /// <summary>Gap between tunnel reconnection attempts.</summary>
    public static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Consecutive relay rejections tolerated before re-registering. A freshly issued
    /// credential races the relay's registry reload, so the first rejection is expected and
    /// must not throw away a valid token.
    /// </summary>
    public const int RejectionsBeforeRepair = 5;
}
