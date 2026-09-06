using System.Buffers.Binary;

namespace Jellyfin.Plugin.FinnyShare;

/// <summary>
/// The FinnyShare tunnel wire format.
///
/// Frame layout: [type:1][streamId:4 big-endian][payload...]
///
/// The Go relay encodes and decodes these same bytes. Both sides' test suites read
/// protocol-testdata/frames.json, so any change here fails both builds together.
/// </summary>
public static class TunnelFrame
{
    /// <summary>Relay to plugin: {"m":method,"p":path,"h":headers,"ws":bool}.</summary>
    public const byte Request = 1;

    /// <summary>Plugin to relay: {"s":status,"h":headers}. 101 accepts a WebSocket upgrade.</summary>
    public const byte Response = 2;

    /// <summary>Either direction: raw body bytes.</summary>
    public const byte Data = 3;

    /// <summary>Either direction: end of body. NOT end of stream.</summary>
    public const byte End = 4;

    /// <summary>Either direction: one WebSocket text frame.</summary>
    public const byte WsText = 5;

    /// <summary>Either direction: one WebSocket binary frame.</summary>
    public const byte WsBinary = 6;

    /// <summary>Either direction: peer abandoned the stream, stop producing immediately.</summary>
    public const byte Reset = 7;

    /// <summary>Fixed size of the frame header.</summary>
    public const int HeaderLength = 5;

    /// <summary>One decoded frame. Payload aliases the source buffer; copy if you keep it.</summary>
    public readonly record struct Decoded(byte Type, uint StreamId, ReadOnlyMemory<byte> Payload);

    /// <summary>Serialises a frame.</summary>
    public static byte[] Encode(byte type, uint streamId, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[HeaderLength + payload.Length];
        frame[0] = type;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1, 4), streamId);
        payload.CopyTo(frame.AsSpan(HeaderLength));
        return frame;
    }

    /// <summary>
    /// Parses a frame. Returns false rather than throwing on a short buffer: a peer controls
    /// these bytes, and a malformed frame must not take the plugin down.
    /// </summary>
    public static bool TryDecode(ReadOnlyMemory<byte> buffer, out Decoded frame)
    {
        if (buffer.Length < HeaderLength)
        {
            frame = default;
            return false;
        }

        frame = new Decoded(
            buffer.Span[0],
            BinaryPrimitives.ReadUInt32BigEndian(buffer.Span.Slice(1, 4)),
            buffer[HeaderLength..]);
        return true;
    }
}
