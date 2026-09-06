using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MediaBrowser.Controller;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinnyShare;

public sealed class TunnelService : BackgroundService
{
    private const int ChunkSize = Constants.ChunkSize;
    private const int BodyQueueDepth = Constants.BodyQueueDepth;
    private const int WsQueueDepth = Constants.WsQueueDepth;
    private const int MaxConcurrentStreams = Constants.MaxConcurrentStreams;
    private const int MaxMessageBytes = Constants.MaxMessageBytes;

    private sealed class StreamState(CancellationToken parent) : IDisposable
    {
        // Bounded, and written non-blocking. A Pipe written from the shared receive loop
        // deadlocked the entire tunnel: its writer pauses at 64 KiB and GET/HEAD/WebSocket
        // streams never drain it, so one stream could park every other stream forever.
        public Channel<byte[]> Body { get; } =
            Channel.CreateBounded<byte[]>(new BoundedChannelOptions(BodyQueueDepth)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

        // Bounded for the same reason: this is fed by the relay, which applies no
        // backpressure of its own, so an unbounded queue is a memory-exhaustion primitive.
        public Channel<(byte Type, byte[] Data)> Ws { get; } =
            Channel.CreateBounded<(byte, byte[])>(new BoundedChannelOptions(WsQueueDepth)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

        // Per-stream cancellation. Without this a viewer who seeks or closes the player leaves
        // us pumping an entire media file into a stream nobody is reading.
        public CancellationTokenSource Cts { get; } = CancellationTokenSource.CreateLinkedTokenSource(parent);

        public void Dispose()
        {
            Body.Writer.TryComplete();
            Ws.Writer.TryComplete();
            Cts.Dispose();
        }
    }

    /// <summary>Reads a stream's request body out of its bounded channel.</summary>
    private sealed class ChannelBodyStream(ChannelReader<byte[]> reader) : Stream
    {
        private ReadOnlyMemory<byte> _current;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken ct = default)
        {
            while (_current.IsEmpty)
            {
                if (!await reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    return 0;
                }

                if (reader.TryRead(out var chunk))
                {
                    _current = chunk;
                }
            }

            var n = Math.Min(buffer.Length, _current.Length);
            _current[..n].CopyTo(buffer);
            _current = _current[n..];
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private readonly ILogger<TunnelService> _log;
    private readonly IHttpClientFactory _http;
    private readonly IServerApplicationHost _host;

    // Plaintext must be opted into: without this an unset variable would put the
    // installation credential on the wire in clear. See RelayEndpoint.
    private static readonly bool AllowInsecure =
        Environment.GetEnvironmentVariable(Constants.EnvAllowInsecure) == "1";

    private static readonly Uri? RelayUri = ResolveRelay();

    private static Uri? ResolveRelay()
    {
        var configured = Environment.GetEnvironmentVariable(Constants.EnvRelay);
        return RelayEndpoint.TryResolve(
            string.IsNullOrWhiteSpace(configured) ? RelayEndpoint.Default : configured,
            AllowInsecure,
            out var uri)
            ? uri
            : null;
    }

    // Where this plugin proxies to. Asked of Jellyfin rather than assumed, because the port is
    // the administrator's choice: hard-coding 8096 silently breaks every server that moved it,
    // and the failure looks like "FinnyShare is broken" rather than "wrong port".
    //
    // The host is always loopback. Only the port is variable, so this cannot be pointed
    // anywhere else - LocalTarget still rejects any request that would resolve off it.
    private readonly string _localBase;

    private string ResolveLocalBase()
    {
        var configured = Environment.GetEnvironmentVariable(Constants.EnvLocal);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.TrimEnd('/');
        }

        // HttpPort is 0 before Jellyfin finishes binding; fall back rather than build a
        // nonsense URL that would fail every request until a restart.
        var port = _host.HttpPort;
        return port > 0
            ? $"http://127.0.0.1:{port}"
            : Constants.DefaultLocalUrl;
    }

    // Env var wins for dev; normally the token arrives from self-registration and lives in plugin config.
    private static readonly string? TokenOverride = Environment.GetEnvironmentVariable(Constants.EnvToken);

    // What the plugin calls, server to server. The installation token is RECEIVED over this
    // connection and the installation credential is sent over it, so it gets the same fail-closed
    // treatment as the relay rather than being trusted because it is "ours".
    private static readonly Uri? ControlUri = RelayEndpoint.TryResolveControl(
        Environment.GetEnvironmentVariable(Constants.EnvControl) ?? Constants.DefaultControlUrl,
        AllowInsecure,
        out var control)
        ? control
        : null;

    internal static readonly string ControlBase = ControlUri?.ToString().TrimEnd('/') ?? string.Empty;

    // What the settings page tells the browser to open. Usually identical, but not in a
    // container rig where the plugin reaches the control plane by service name and the
    // user's browser cannot.
    internal static readonly string ControlPublicBase =
        (Environment.GetEnvironmentVariable(Constants.EnvControlPublic) ?? ControlBase).TrimEnd('/');

    private readonly ConcurrentDictionary<uint, StreamState> _streams = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _ws;

    public TunnelService(ILogger<TunnelService> log, IHttpClientFactory http, IServerApplicationHost host)
    {
        _log = log;
        _http = http;
        _host = host;
        _localBase = ResolveLocalBase();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (ControlUri is null)
        {
            _log.LogError(
                "FinnyShare disabled: {Var} must be an https:// URL " +
                "(set {Insecure}=1 only for local development)",
                Constants.EnvControl,
                Constants.EnvAllowInsecure);
            return;
        }

        if (RelayUri is null)
        {
            // Fail closed and say why, rather than connecting somewhere unsafe.
            _log.LogError(
                "FinnyShare disabled: FINNYSHARE_RELAY must be a wss:// endpoint " +
                "(set FINNYSHARE_ALLOW_INSECURE=1 only for local development)");
            return;
        }

        // These are derived from configuration, not from registration state, so refresh them
        // on every start. Writing them only at registration would leave every already-registered
        // installation without a working console link after an upgrade.
        PublishLinks();

        while (!ct.IsCancellationRequested)
        {
            if (Plugin.Instance?.Configuration.Disabled == true)
            {
                // Switched off from the settings page. Stay resident and re-check, so
                // turning it back on does not need a Jellyfin restart.
                await DelayBeforeRetryAsync(ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                var token = TokenOverride;
                if (string.IsNullOrEmpty(token))
                {
                    token = await EnsureRegisteredAsync(ct).ConfigureAwait(false);
                }

                using var ws = new ClientWebSocket();
                ws.Options.SetRequestHeader("Authorization", "Bearer " + token);

                // So the dashboard can label this server the way its owner named it, rather
                // than echoing back the hostname we generated. Sanitised: it is owner-supplied
                // text going onto a header line.
                var serverName = ServerName.Sanitize(_host.FriendlyName);
                if (serverName.Length > 0)
                {
                    ws.Options.SetRequestHeader(Constants.ServerNameHeader, serverName);
                }

                try
                {
                    await ws.ConnectAsync(RelayUri, ct).ConfigureAwait(false);
                }
                catch (WebSocketException ex) when (IsRejected(ex))
                {
                    // A 401 is not proof the credential is dead. The relay reloads its
                    // registry on a poll, so the very first connection after registering races
                    // that reload and is rejected legitimately. Only a credential rejected
                    // repeatedly, over a real span of time, has actually been revoked -
                    // otherwise we would discard a brand new token and loop forever.
                    _rejections++;
                    if (_rejections < RejectionsBeforeRepair)
                    {
                        _log.LogInformation(
                            "FinnyShare credential not accepted yet ({Attempt}/{Max}); retrying",
                            _rejections,
                            RejectionsBeforeRepair);
                    }
                    else
                    {
                        _log.LogWarning("FinnyShare credential rejected repeatedly; registering again");
                        ForgetCredential();
                        _rejections = 0;
                    }

                    await DelayBeforeRetryAsync(ct).ConfigureAwait(false);
                    continue;
                }

                _rejections = 0;
                _ws = ws;
                // Naming the local endpoint makes the commonest support question answerable
                // from the log alone: a server on a non-default port shows it here.
                _log.LogInformation(
                    "FinnyShare tunnel connected to {Relay}, proxying to {Local}",
                    RelayUri.GetLeftPart(UriPartial.Path), _localBase);
                await ReceiveLoopAsync(ws, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning("FinnyShare tunnel dropped: {Message}", ex.Message);
            }

            _ws = null;
            foreach (var id in _streams.Keys)
            {
                if (_streams.TryRemove(id, out var s))
                {
                    s.Cts.Cancel();
                    s.Dispose();
                }
            }

            try
            {
                await Task.Delay(Constants.ReconnectDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Returns this installation's credential, registering first if we have none.
    ///
    /// Registration is the entire onboarding: no account, no code, no human step. The
    /// credential is issued automatically and never shown to anyone - it exists only so the
    /// relay knows which tunnel may claim which address.
    /// </summary>
    private async Task<string> EnsureRegisteredAsync(CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        if (!string.IsNullOrEmpty(config?.Token))
        {
            return config.Token;
        }

        var client = _http.CreateClient(Constants.HttpClientName);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var resp = await client
                    .PostAsync($"{ControlBase}{Constants.RegisterPath}", null, ct)
                    .ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                using var doc = JsonDocument.Parse(
                    await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
                var token = doc.RootElement.GetProperty("token").GetString()!;
                var hostname = doc.RootElement.GetProperty("hostname").GetString()!;

                Save(token, hostname);
                _log.LogInformation("FinnyShare registered. Address: https://{Hostname}", hostname);
                return token;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning("FinnyShare could not register: {Message}", ex.Message);
                await DelayBeforeRetryAsync(ct).ConfigureAwait(false);
            }
        }

        throw new OperationCanceledException(ct);
    }

    /// <summary>Persists the credential and the address the settings page renders.</summary>
    private static void Save(string token, string hostname)
    {
        if (Plugin.Instance is not { } plugin)
        {
            return;
        }

        plugin.Configuration.Token = token;
        plugin.Configuration.Hostname = hostname;
        plugin.Configuration.RenameUrl =
            $"{ControlPublicBase}{Constants.RenamePath}?t={Uri.EscapeDataString(token)}";
        plugin.SaveConfiguration();
    }

    /// <summary>Asks the control plane for a fresh random address.</summary>
    public async Task<string?> NewAddressAsync(CancellationToken ct)
    {
        var token = Plugin.Instance?.Configuration.Token;
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var client = _http.CreateClient(Constants.HttpClientName);
        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"{ControlBase}{Constants.RandomLabelPath}");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

        var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(
            await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
        var hostname = doc.RootElement.GetProperty("hostname").GetString()!;

        Save(token, hostname);
        return hostname;
    }

    /// <summary>Turns sharing off or on. The relay drops the installation from its registry.</summary>
    public async Task SetDisabledAsync(bool disabled, CancellationToken ct)
    {
        var token = Plugin.Instance?.Configuration.Token;
        if (string.IsNullOrEmpty(token))
        {
            return;
        }

        var client = _http.CreateClient(Constants.HttpClientName);
        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"{ControlBase}{Constants.DisabledPath}")
        {
            Content = new FormUrlEncodedContent(
                new[] { new KeyValuePair<string, string>("disabled", disabled ? "1" : "0") }),
        };
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

        (await client.SendAsync(req, ct).ConfigureAwait(false)).EnsureSuccessStatusCode();

        if (Plugin.Instance is { } plugin)
        {
            plugin.Configuration.Disabled = disabled;
            plugin.SaveConfiguration();
        }
    }

    // A newly issued credential needs time to reach the relay, so tolerate a few rejections
    // before concluding the installation was actually removed.
    private const int RejectionsBeforeRepair = Constants.RejectionsBeforeRepair;

    private int _rejections;

    private async Task DelayBeforeRetryAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Constants.ReconnectDelay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    /// <summary>A 401/403 handshake failure means the relay refused us, not that the network failed.</summary>
    private static bool IsRejected(WebSocketException ex) =>
        ex.Message.Contains("401", StringComparison.Ordinal) ||
        ex.Message.Contains("403", StringComparison.Ordinal);

    private void ForgetCredential()
    {
        if (Plugin.Instance is { } plugin)
        {
            plugin.Configuration.Token = null;
            plugin.Configuration.Hostname = null;
            plugin.Configuration.RenameUrl = null;
            plugin.SaveConfiguration();
        }
    }

    /// <summary>Keeps the settings page's console link current without waiting for a re-register.</summary>
    private static void PublishLinks()
    {
        if (Plugin.Instance is not { } plugin)
        {
            return;
        }

        if (string.IsNullOrEmpty(plugin.Configuration.Token))
        {
            return;
        }

        var rename = $"{ControlPublicBase}{Constants.RenamePath}" +
            $"?t={Uri.EscapeDataString(plugin.Configuration.Token)}";
        if (plugin.Configuration.RenameUrl == rename)
        {
            return;
        }

        plugin.Configuration.RenameUrl = rename;
        plugin.SaveConfiguration();
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[ChunkSize + TunnelFrame.HeaderLength];
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var msg = new MemoryStream();
            ValueWebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buf.AsMemory(), ct).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                // Capped: the peer controls fragmentation and could otherwise stream
                // continuation frames forever, growing this buffer until the Jellyfin
                // process is killed by the OOM reaper.
                if (msg.Length + r.Count > MaxMessageBytes)
                {
                    _log.LogWarning("FinnyShare oversized tunnel message; dropping connection");
                    return;
                }

                msg.Write(buf, 0, r.Count);
            }
            while (!r.EndOfMessage);

            // One codec for both directions; a malformed frame is dropped, not fatal.
            if (!TunnelFrame.TryDecode(msg.GetBuffer().AsMemory(0, (int)msg.Length), out var decoded))
            {
                continue;
            }

            var type = decoded.Type;
            var streamId = decoded.StreamId;
            var payload = decoded.Payload;

            if (type == TunnelFrame.Request)
            {
                if (_streams.Count >= MaxConcurrentStreams)
                {
                    // Refuse rather than allocate: every stream costs a task, a CTS and two
                    // queues, so an unbounded count is a DoS against our own Jellyfin.
                    await RefuseAsync(streamId, 503, ct).ConfigureAwait(false);
                    continue;
                }

                var state = new StreamState(ct);

                // TryAdd, never an indexer assignment: overwriting would orphan the previous
                // state - still running, no longer cancellable - and let its cleanup delete
                // the replacement's entry, silently stalling an unrelated viewer.
                if (!_streams.TryAdd(streamId, state))
                {
                    state.Dispose();
                    await RefuseAsync(streamId, 409, ct).ConfigureAwait(false);
                    continue;
                }

                var meta = payload.ToArray();
                _ = Task.Run(() => DispatchAsync(streamId, meta, state), CancellationToken.None);
                continue;
            }

            if (!_streams.TryGetValue(streamId, out var s))
            {
                continue;
            }

            // One stream's fault must never take down the tunnel carrying everyone else's.
            // A DATA frame arriving after END used to throw straight out of this loop and
            // kill every viewer's stream at once.
            try
            {
                DispatchFrame(streamId, s, type, payload);
            }
            catch (Exception ex)
            {
                _log.LogWarning("FinnyShare stream {Id} frame rejected: {Message}", streamId, ex.Message);
                await ResetStreamAsync(streamId, s).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Routes one frame. Never blocks: every queue write is non-blocking, so a peer that
    /// outruns its consumer loses its own stream rather than parking the shared loop.
    /// </summary>
    private static void DispatchFrame(uint streamId, StreamState s, byte type, ReadOnlyMemory<byte> payload)
    {
        switch (type)
        {
            case TunnelFrame.Data:
                if (!s.Body.Writer.TryWrite(payload.ToArray()))
                {
                    throw new InvalidOperationException("body queue full or closed");
                }

                break;

            case TunnelFrame.WsText:
            case TunnelFrame.WsBinary:
                if (!s.Ws.Writer.TryWrite((type, payload.ToArray())))
                {
                    throw new InvalidOperationException("websocket queue full or closed");
                }

                break;

            case TunnelFrame.End:
                // End of the REQUEST BODY, not the stream - the relay sends this right after
                // every GET. Do NOT remove the stream here, or a later RESET has nothing to
                // cancel and we keep pumping to a viewer who already left.
                s.Body.Writer.TryComplete();
                break;

            case TunnelFrame.Reset:
                // Viewer is gone. Cancelling makes the read loop throw, so we stop pulling
                // from Jellyfin instead of pushing the rest of the file into the void.
                s.Cts.Cancel();
                break;
        }
    }

    /// <summary>Tears down one stream without disturbing any other.</summary>
    private async Task ResetStreamAsync(uint streamId, StreamState s)
    {
        // Keyed removal: a stream may only ever remove its own entry, never a successor's.
        _streams.TryRemove(new KeyValuePair<uint, StreamState>(streamId, s));
        await s.Cts.CancelAsync().ConfigureAwait(false);

        try
        {
            await SendAsync(TunnelFrame.Reset, streamId, ReadOnlyMemory<byte>.Empty, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // tunnel already gone
        }
    }

    /// <summary>Declines a stream we will not serve, without allocating state for it.</summary>
    private async Task RefuseAsync(uint streamId, int status, CancellationToken ct)
    {
        try
        {
            await SendAsync(TunnelFrame.Response, streamId, Meta(status), ct).ConfigureAwait(false);
            await SendAsync(TunnelFrame.End, streamId, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
        }
        catch
        {
            // tunnel already gone
        }
    }

    private async Task DispatchAsync(uint streamId, byte[] meta, StreamState state)
    {
        var ct = state.Cts.Token;
        try
        {
            using var doc = JsonDocument.Parse(meta);
            var root = doc.RootElement;
            var method = root.GetProperty("m").GetString()!;
            var path = root.GetProperty("p").GetString()!;
            var isWs = root.TryGetProperty("ws", out var w) && w.GetBoolean();

            // Privacy: path only, never the query string - Jellyfin URLs carry api_key/token.
            _log.LogDebug("FinnyShare {Method} {Path}{Ws}", method, path.Split('?')[0], isWs ? " [ws]" : string.Empty);

            if (isWs)
            {
                await ProxyWebSocketAsync(streamId, path, root, state, ct).ConfigureAwait(false);
            }
            else
            {
                await ProxyHttpAsync(streamId, method, path, root, state, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.LogDebug("FinnyShare stream {Id} reset by relay", streamId);
        }
        catch (Exception ex)
        {
            _log.LogWarning("FinnyShare stream {Id} failed: {Message}", streamId, ex.Message);
        }
        finally
        {
            _streams.TryRemove(new KeyValuePair<uint, StreamState>(streamId, state));

            // A reset stream needs no END - the relay already dropped it. Use None, not the
            // stream token, or the goodbye frame is cancelled along with the work.
            if (!ct.IsCancellationRequested)
            {
                try
                {
                    await SendAsync(TunnelFrame.End, streamId, ReadOnlyMemory<byte>.Empty, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // tunnel already gone
                }
            }

            state.Dispose();
        }
    }

    private async Task ProxyHttpAsync(
        uint streamId, string method, string path, JsonElement root, StreamState state, CancellationToken ct)
    {
        // Trust boundary: the relay supplies this path, so it must not be able to retarget
        // another host. See LocalTarget - "@evil.com/x" would otherwise resolve to evil.com.
        if (!LocalTarget.TryResolve(_localBase, path, out var target))
        {
            _log.LogWarning("FinnyShare refused a non-local target on stream {Id}", streamId);
            await SendAsync(TunnelFrame.Response, streamId, Meta(400), ct).ConfigureAwait(false);
            return;
        }

        using var req = new HttpRequestMessage(new HttpMethod(method), target);
        if (method is not ("GET" or "HEAD"))
        {
            req.Content = new StreamContent(new ChannelBodyStream(state.Body.Reader));
        }

        foreach (var h in root.GetProperty("h").EnumerateObject())
        {
            if (HopByHop.Contains(h.Name))
            {
                continue;
            }

            var vals = h.Value.EnumerateArray().Select(v => v.GetString()!).ToArray();
            if (!req.Headers.TryAddWithoutValidation(h.Name, vals))
            {
                req.Content?.Headers.TryAddWithoutValidation(h.Name, vals);
            }
        }

        var client = _http.CreateClient(Constants.HttpClientName);
        using var res = await client
            .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        await SendAsync(TunnelFrame.Response, streamId, BuildResponseMeta(res), ct).ConfigureAwait(false);

        var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var chunk = new byte[ChunkSize];
            int n;
            while ((n = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                await SendAsync(TunnelFrame.Data, streamId, chunk.AsMemory(0, n), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task ProxyWebSocketAsync(
        uint streamId, string path, JsonElement root, StreamState state, CancellationToken ct)
    {
        // Same trust boundary as the HTTP path. This used to concatenate the relay-supplied
        // path directly, which let "@evil.com/x" retarget the whole connection.
        if (!LocalTarget.TryResolveWebSocket(_localBase, path, out var target))
        {
            _log.LogWarning("FinnyShare refused a non-local websocket target on stream {Id}", streamId);
            await SendAsync(TunnelFrame.Response, streamId, Meta(400), ct).ConfigureAwait(false);
            return;
        }

        using var local = new ClientWebSocket();

        foreach (var h in root.GetProperty("h").EnumerateObject())
        {
            // ClientWebSocket writes the handshake headers itself and throws if we duplicate them.
            if (HopByHop.Contains(h.Name) || h.Name.StartsWith("Sec-WebSocket-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                local.Options.SetRequestHeader(h.Name, string.Join(",", h.Value.EnumerateArray().Select(v => v.GetString())));
            }
            catch (ArgumentException)
            {
                // header not settable on a handshake - skip it
            }
        }

        try
        {
            await local.ConnectAsync(target, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning("FinnyShare ws upgrade to {Path} failed: {Message}", path.Split('?')[0], ex.Message);
            await SendAsync(TunnelFrame.Response, streamId, Meta(502), ct).ConfigureAwait(false);
            return;
        }

        await SendAsync(TunnelFrame.Response, streamId, Meta(101), ct).ConfigureAwait(false);

        using var link = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var toRelay = PumpLocalToRelayAsync(streamId, local, link.Token);
        var toLocal = PumpRelayToLocalAsync(local, state, link.Token);
        await Task.WhenAny(toRelay, toLocal).ConfigureAwait(false);
        await link.CancelAsync().ConfigureAwait(false);
    }

    private async Task PumpLocalToRelayAsync(uint streamId, ClientWebSocket local, CancellationToken ct)
    {
        var buf = new byte[ChunkSize];
        try
        {
            while (local.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var msg = new MemoryStream();
                ValueWebSocketReceiveResult r;
                do
                {
                    r = await local.ReceiveAsync(buf.AsMemory(), ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    msg.Write(buf, 0, r.Count);
                }
                while (!r.EndOfMessage);

                var type = r.MessageType == WebSocketMessageType.Text ? TunnelFrame.WsText : TunnelFrame.WsBinary;
                await SendAsync(type, streamId, msg.GetBuffer().AsMemory(0, (int)msg.Length), ct)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // peer closed
        }
    }

    private static async Task PumpRelayToLocalAsync(ClientWebSocket local, StreamState state, CancellationToken ct)
    {
        try
        {
            await foreach (var (type, data) in state.Ws.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var kind = type == TunnelFrame.WsText ? WebSocketMessageType.Text : WebSocketMessageType.Binary;
                await local.SendAsync(data, kind, true, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // peer closed
        }
    }

    private static byte[] Meta(int status) =>
        Encoding.UTF8.GetBytes($"{{\"s\":{status},\"h\":{{}}}}");

    private static byte[] BuildResponseMeta(HttpResponseMessage res)
    {
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in res.Headers.Concat(res.Content.Headers))
        {
            if (HopByHop.Contains(h.Key))
            {
                continue;
            }

            headers[h.Key] = h.Value.ToArray();
        }

        return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["s"] = (int)res.StatusCode,
            ["h"] = headers,
        });
    }


    // ponytail: one lock serialises every stream's writes. Fine to ~100 Mbps;
    // swap for per-stream flow control if a large file starves small requests.
    private async Task SendAsync(byte type, uint streamId, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var ws = _ws ?? throw new InvalidOperationException("tunnel down");
        var frame = TunnelFrame.Encode(type, streamId, payload.Span);

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
