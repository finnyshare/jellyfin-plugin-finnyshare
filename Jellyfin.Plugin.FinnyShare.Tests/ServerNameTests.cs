using Jellyfin.Plugin.FinnyShare;
using Xunit;

namespace Jellyfin.Plugin.FinnyShare.Tests;

/// <summary>
/// The Jellyfin server's name is chosen by its owner and travels to the relay as a handshake
/// header, so it is untrusted input on a header boundary: CR/LF would let it inject headers,
/// and an unbounded string would let it push arbitrary bulk through the control plane.
/// </summary>
public class ServerNameTests
{
    [Theory]
    [InlineData("finnyjelly", "finnyjelly")]
    [InlineData("Nick's Media Box", "Nick's Media Box")]
    [InlineData("  padded  ", "padded")]
    public void KeepsOrdinaryNames(string given, string want)
    {
        Assert.Equal(want, ServerName.Sanitize(given));
    }

    [Theory]
    [InlineData("evil\r\nX-Injected: 1")]
    [InlineData("evil\nX-Injected: 1")]
    [InlineData("evil\rX-Injected: 1")]
    [InlineData("tab\there")]
    [InlineData("null\0byte")]
    public void StripsAnythingThatCouldForgeAHeader(string given)
    {
        var got = ServerName.Sanitize(given);

        Assert.DoesNotContain('\r', got);
        Assert.DoesNotContain('\n', got);
        Assert.DoesNotContain('\0', got);
        Assert.DoesNotContain('\t', got);
    }

    [Fact]
    public void CapsTheLength()
    {
        Assert.True(ServerName.Sanitize(new string('x', 500)).Length <= ServerName.MaxLength);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    public void ReturnsEmptyWhenThereIsNoUsableName(string? given)
    {
        // The relay falls back to the assigned hostname; it must not receive whitespace
        // pretending to be a name.
        Assert.Equal(string.Empty, ServerName.Sanitize(given));
    }
}
