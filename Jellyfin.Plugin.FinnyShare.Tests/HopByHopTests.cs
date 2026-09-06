using Jellyfin.Plugin.FinnyShare;
using Xunit;

namespace Jellyfin.Plugin.FinnyShare.Tests;

public class HopByHopTests
{
    [Theory]
    [InlineData("Connection")]
    [InlineData("Keep-Alive")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Upgrade")]
    [InlineData("Proxy-Authenticate")]
    [InlineData("Proxy-Authorization")]
    [InlineData("TE")]
    [InlineData("Trailer")]
    [InlineData("Host")]
    public void DropsConnectionScopedHeaders(string name)
    {
        Assert.True(HopByHop.Contains(name), $"{name} must not cross the tunnel");
    }

    [Theory]
    [InlineData("transfer-encoding")]
    [InlineData("TRANSFER-ENCODING")]
    [InlineData("Transfer-Encoding")]
    public void MatchesRegardlessOfCase(string name)
    {
        // HTTP field names are case-insensitive; a case-sensitive check would let a peer
        // slip a hop-by-hop header through simply by changing its capitalisation.
        Assert.True(HopByHop.Contains(name));
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("X-Emby-Token")]
    [InlineData("Range")]
    [InlineData("Content-Type")]
    [InlineData("Content-Length")]
    [InlineData("If-None-Match")]
    [InlineData("User-Agent")]
    [InlineData("X-Forwarded-For")]
    public void KeepsHeadersJellyfinNeeds(string name)
    {
        // Range drives media seeking and Authorization carries the Jellyfin session; losing
        // either silently breaks playback or logs every viewer out.
        Assert.False(HopByHop.Contains(name), $"{name} must reach Jellyfin");
    }
}
