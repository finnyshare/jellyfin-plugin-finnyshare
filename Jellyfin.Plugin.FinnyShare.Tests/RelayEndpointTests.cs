using Jellyfin.Plugin.FinnyShare;
using Xunit;

namespace Jellyfin.Plugin.FinnyShare.Tests;

/// <summary>
/// The installation credential is sent as an Authorization header on the tunnel handshake.
/// Over ws:// that header crosses the network in cleartext, so an insecure endpoint must be
/// a deliberate choice for local development, never something a misconfiguration falls into.
/// </summary>
public class RelayEndpointTests
{
    [Fact]
    public void DefaultEndpointIsEncrypted()
    {
        // The shipped default must be safe with no configuration at all.
        Assert.StartsWith("wss://", RelayEndpoint.Default, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("wss://relay.example/tunnel")]
    [InlineData("WSS://relay.example/tunnel")]
    public void AcceptsEncryptedEndpoints(string url)
    {
        Assert.True(RelayEndpoint.TryResolve(url, allowInsecure: false, out var uri));
        Assert.NotNull(uri);
    }

    [Theory]
    [InlineData("ws://relay.example/tunnel")]
    [InlineData("ws://relay:9000/tunnel")]
    public void RefusesPlaintextUnlessExplicitlyAllowed(string url)
    {
        // Refusing to connect is the right failure: a tunnel that silently downgrades would
        // leak the credential of every user who never set the variable.
        Assert.False(RelayEndpoint.TryResolve(url, allowInsecure: false, out var uri));
        Assert.Null(uri);

        // The local test rig opts in deliberately.
        Assert.True(RelayEndpoint.TryResolve(url, allowInsecure: true, out var dev));
        Assert.NotNull(dev);
    }

    [Theory]
    [InlineData("https://relay.example/tunnel")]
    [InlineData("file:///etc/passwd")]
    [InlineData("relay.example/tunnel")]
    [InlineData("")]
    [InlineData("not a url")]
    public void RefusesAnythingThatIsNotAWebSocketEndpoint(string url)
    {
        Assert.False(RelayEndpoint.TryResolve(url, allowInsecure: true, out _));
    }
}

/// <summary>
/// The control plane is where the installation token is *issued*. It was previously accepted
/// with no validation at all while the relay URL was rigorously checked, so an http:// control
/// URL received that credential silently in the clear.
/// </summary>
public class ControlEndpointTests
{
    [Fact]
    public void DefaultControlEndpointIsEncrypted()
    {
        Assert.StartsWith("https://", Constants.DefaultControlUrl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://finnyshare.space")]
    [InlineData("HTTPS://finnyshare.space")]
    public void AcceptsEncryptedControlEndpoints(string url)
    {
        Assert.True(RelayEndpoint.TryResolveControl(url, allowInsecure: false, out var uri));
        Assert.NotNull(uri);
    }

    [Theory]
    [InlineData("http://finnyshare.space")]
    [InlineData("http://127.0.0.1:9001")]
    public void RefusesPlaintextControlUnlessExplicitlyAllowed(string url)
    {
        Assert.False(RelayEndpoint.TryResolveControl(url, allowInsecure: false, out _));
        Assert.True(RelayEndpoint.TryResolveControl(url, allowInsecure: true, out _));
    }

    [Theory]
    [InlineData("ws://finnyshare.space")]
    [InlineData("file:///etc/passwd")]
    [InlineData("finnyshare.space")]
    [InlineData("")]
    public void RefusesAnythingThatIsNotHttp(string url)
    {
        Assert.False(RelayEndpoint.TryResolveControl(url, allowInsecure: true, out _));
    }
}
