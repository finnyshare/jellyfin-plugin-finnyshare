using Jellyfin.Plugin.FinnyShare;
using Xunit;

namespace Jellyfin.Plugin.FinnyShare.Tests;

/// <summary>
/// The plugin must forward to the configured local Jellyfin and nothing else (plan §21).
/// It does not get to trust the relay: the relay is remote, replaceable, and the plugin is
/// the half that runs inside someone's home network. If a path can redirect the target
/// host, FinnyShare becomes an SSRF gateway and the cheapest open proxy on the internet.
/// </summary>
public class LocalTargetTests
{
    private const string LocalBase = "http://127.0.0.1:8096";

    [Theory]
    [InlineData("/System/Info/Public")]
    [InlineData("/Videos/abc/stream?static=true&api_key=x")]
    [InlineData("/a%20b/c")]
    [InlineData("/")]
    public void AcceptsOrdinaryPathsAndKeepsThemLocal(string path)
    {
        Assert.True(LocalTarget.TryResolve(LocalBase, path, out var uri));
        Assert.Equal("127.0.0.1", uri!.Host);
        Assert.Equal(8096, uri.Port);
    }

    [Fact]
    public void PreservesPathAndQueryExactly()
    {
        Assert.True(LocalTarget.TryResolve(LocalBase, "/Items?ids=1,2&fields=Path", out var uri));
        Assert.Equal("/Items", uri!.AbsolutePath);
        Assert.Equal("?ids=1,2&fields=Path", uri.Query);
    }

    [Theory]
    // Userinfo trick: "http://127.0.0.1:8096" + "@evil.com/x" parses with 127.0.0.1:8096 as
    // the *credentials* and evil.com as the host. This one is not theoretical.
    [InlineData("@evil.com/x")]
    [InlineData("@evil.com:80/x")]
    // Protocol-relative: resolves against the base as a new authority under URI resolution.
    [InlineData("//evil.com/x")]
    [InlineData("//evil.com")]
    // Absolute URLs must never be honoured, whatever the scheme.
    [InlineData("http://evil.com/x")]
    [InlineData("https://evil.com/x")]
    [InlineData("file:///etc/passwd")]
    // Backslashes are treated as separators by some parsers.
    [InlineData("\\\\evil.com/x")]
    // Anything that is not an absolute path is not something Jellyfin would ever be asked.
    [InlineData("System/Info")]
    [InlineData("")]
    [InlineData(" ")]
    public void RejectsAnythingThatCouldRetargetAnotherHost(string path)
    {
        Assert.False(LocalTarget.TryResolve(LocalBase, path, out var uri),
            $"path {path} was accepted; it must never leave the local endpoint");
        Assert.Null(uri);
    }

    [Fact]
    public void RejectsPathsEvenWhenTheyResolveToAnotherLoopbackPort()
    {
        // Other services on localhost are still not ours to reach - a home server may run
        // admin panels, routers, or cloud metadata endpoints on neighbouring ports.
        Assert.False(LocalTarget.TryResolve(LocalBase, "@127.0.0.1:9000/x", out _));
    }
}

/// <summary>
/// The WebSocket proxy path must be restricted exactly like the HTTP one. It previously was
/// not: it concatenated the relay-supplied path onto the local base, so "@evil.com/x" parsed
/// the local address as userinfo and reached evil.com instead. Three independent audits found
/// it, and nothing caught it because no test exercised the caller rather than the helper.
/// </summary>
public class WebSocketTargetTests
{
    private const string LocalBase = "http://127.0.0.1:8096";

    [Fact]
    public void ResolvesToTheLocalJellyfinOverWs()
    {
        Assert.True(LocalTarget.TryResolveWebSocket(LocalBase, "/socket?api_key=x", out var uri));
        Assert.Equal("ws", uri!.Scheme);
        Assert.Equal("127.0.0.1", uri.Host);
        Assert.Equal(8096, uri.Port);
        Assert.Equal("/socket", uri.AbsolutePath);
    }

    [Fact]
    public void UsesWssWhenTheLocalEndpointIsHttps()
    {
        Assert.True(LocalTarget.TryResolveWebSocket("https://127.0.0.1:8920", "/socket", out var uri));
        Assert.Equal("wss", uri!.Scheme);
    }

    [Theory]
    [InlineData("@evil.com/x")]
    [InlineData("@192.168.1.1/")]
    [InlineData("@169.254.169.254/latest/meta-data/")]
    [InlineData("@127.0.0.1:9000/x")]
    [InlineData("//evil.com/x")]
    [InlineData("ws://evil.com/x")]
    [InlineData("wss://evil.com/x")]
    [InlineData("http://evil.com/x")]
    [InlineData("socket")]
    [InlineData("")]
    public void RefusesAnyPathThatCouldLeaveTheLocalEndpoint(string path)
    {
        Assert.False(LocalTarget.TryResolveWebSocket(LocalBase, path, out var uri),
            $"path {path} was accepted for a WebSocket upgrade");
        Assert.Null(uri);
    }
}
