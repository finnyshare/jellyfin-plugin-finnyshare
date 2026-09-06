using Jellyfin.Plugin.FinnyShare;
using Xunit;

namespace Jellyfin.Plugin.FinnyShare.Tests;

/// <summary>
/// Whether to wait for credit is decided once, from the handshake. Getting it wrong in one
/// direction stalls other viewers; in the other it hangs every response on the first byte
/// waiting for a grant an older relay will never send. Neither failure logs anything useful,
/// so the decision is pinned here.
/// </summary>
public class TunnelFlowTests
{
    [Fact]
    public void ConfirmedWhenTheRelayEchoesTheHeader()
    {
        var headers = new Dictionary<string, IEnumerable<string>>
        {
            [TunnelFlow.Header] = new[] { TunnelFlow.Credit },
        };

        Assert.True(TunnelFlow.IsConfirmed(headers));
    }

    [Fact]
    public void ConfirmedRegardlessOfHeaderCasing()
    {
        // HTTP header names are case-insensitive and nothing guarantees which casing a
        // proxy or a runtime hands back. Matching only one spelling would silently disable
        // flow control against a relay that does support it.
        var headers = new Dictionary<string, IEnumerable<string>>
        {
            ["x-finnyshare-flow"] = new[] { "credit" },
        };

        Assert.True(TunnelFlow.IsConfirmed(headers));
    }

    [Fact]
    public void NotConfirmedByAnOlderRelayThatSaysNothing()
    {
        // The dangerous case: waiting for credit here would hang every response forever.
        var headers = new Dictionary<string, IEnumerable<string>>
        {
            ["Server"] = new[] { "nginx" },
        };

        Assert.False(TunnelFlow.IsConfirmed(headers));
    }

    [Fact]
    public void NotConfirmedWhenTheRuntimeCollectedNoHeaders()
    {
        Assert.False(TunnelFlow.IsConfirmed(null));
    }

    [Fact]
    public void NotConfirmedByAnUnknownFlowScheme()
    {
        var headers = new Dictionary<string, IEnumerable<string>>
        {
            [TunnelFlow.Header] = new[] { "something-we-do-not-implement" },
        };

        Assert.False(TunnelFlow.IsConfirmed(headers));
    }
}
