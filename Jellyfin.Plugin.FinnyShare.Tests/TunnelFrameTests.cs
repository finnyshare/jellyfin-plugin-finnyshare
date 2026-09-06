using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.FinnyShare;
using Xunit;

namespace Jellyfin.Plugin.FinnyShare.Tests;

/// <summary>
/// The plugin (C#) and the relay (Go) must produce identical bytes. Neither language's own
/// tests can detect drift between them, so both suites read this same vector file. Change a
/// vector and both builds go red together - that is the entire point.
/// </summary>
public class TunnelFrameTests
{
    private sealed record Vector(string Name, byte Type, uint StreamId, byte[] Payload, string Bytes);

    private static (Dictionary<string, byte> Types, List<Vector> Vectors) LoadVectors()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText("frames.json"));
        var root = doc.RootElement;

        var types = new Dictionary<string, byte>();
        foreach (var t in root.GetProperty("frameTypes").EnumerateObject())
        {
            types[t.Name] = t.Value.GetByte();
        }

        var vectors = new List<Vector>();
        foreach (var v in root.GetProperty("vectors").EnumerateArray())
        {
            // Credit carries a big-endian uint32, so a vector may state its payload as hex
            // instead of as text.
            var payload = v.TryGetProperty("payloadHex", out var hex)
                ? Convert.FromHexString(hex.GetString()!)
                : Encoding.UTF8.GetBytes(v.GetProperty("payloadUtf8").GetString()!);

            vectors.Add(new Vector(
                v.GetProperty("name").GetString()!,
                v.GetProperty("type").GetByte(),
                v.GetProperty("streamId").GetUInt32(),
                payload,
                v.GetProperty("bytes").GetString()!));
        }

        Assert.NotEmpty(vectors);
        return (types, vectors);
    }

    [Fact]
    public void FrameTypeNumbersMatchTheSharedContract()
    {
        var (types, _) = LoadVectors();

        // A renumbered frame type would break every deployed plugin against a new relay.
        Assert.Equal(TunnelFrame.Request, types["REQUEST"]);
        Assert.Equal(TunnelFrame.Response, types["RESPONSE"]);
        Assert.Equal(TunnelFrame.Data, types["DATA"]);
        Assert.Equal(TunnelFrame.End, types["END"]);
        Assert.Equal(TunnelFrame.WsText, types["WS_TEXT"]);
        Assert.Equal(TunnelFrame.WsBinary, types["WS_BINARY"]);
        Assert.Equal(TunnelFrame.Reset, types["RESET"]);
        Assert.Equal(TunnelFrame.Credit, types["CREDIT"]);
    }

    [Fact]
    public void EncodeProducesTheSharedBytes()
    {
        var (_, vectors) = LoadVectors();

        foreach (var v in vectors)
        {
            var encoded = TunnelFrame.Encode(v.Type, v.StreamId, v.Payload);
            Assert.Equal(v.Bytes, Convert.ToHexString(encoded).ToLowerInvariant());
        }
    }

    [Fact]
    public void DecodeReadsTheSharedBytes()
    {
        var (_, vectors) = LoadVectors();

        foreach (var v in vectors)
        {
            var raw = Convert.FromHexString(v.Bytes);

            Assert.True(TunnelFrame.TryDecode(raw, out var frame), v.Name);
            Assert.Equal(v.Type, frame.Type);
            Assert.Equal(v.StreamId, frame.StreamId);
            Assert.Equal(v.Payload, frame.Payload.ToArray());
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void DecodeRejectsFramesShorterThanTheHeader(int length)
    {
        // A peer controls these bytes. Reading past a short buffer would take down the
        // plugin, and with it the user's remote access.
        Assert.False(TunnelFrame.TryDecode(new byte[length], out _));
    }
}
