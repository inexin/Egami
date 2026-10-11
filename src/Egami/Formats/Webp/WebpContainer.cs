using System.Buffers.Binary;

namespace Egami.Formats.Webp;

/// <summary>The RIFF container of a WebP file (RFC 9649 section 2): chunk types and the simple layout.</summary>
internal static class WebpContainer
{
    /// <summary>Gets the chunk FourCCs as little-endian numbers.</summary>
    internal const uint Riff = 0x46464952;
    internal const uint Webp = 0x50424557;
    internal const uint Vp8 = 0x20385056;
    internal const uint Vp8L = 0x4C385056;
    internal const uint Vp8X = 0x58385056;
    internal const uint Alph = 0x48504C41;
    internal const uint Anim = 0x4D494E41;
    internal const uint Anmf = 0x464D4E41;
    internal const uint Iccp = 0x50434349;
    internal const uint Exif = 0x46495845;
    internal const uint Xmp = 0x20504D58;

    /// <summary>Writes a simple-format file: RIFF, WEBP and one image chunk, padded to an even length.</summary>
    internal static void WriteSimple(Stream output, uint chunk, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[20];
        int padded = payload.Length + (payload.Length & 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header, Riff);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)(12 + padded));
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], Webp);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], chunk);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], (uint)payload.Length);
        output.Write(header);
        output.Write(payload);
        if ((payload.Length & 1) != 0)
        {
            output.WriteByte(0);
        }
    }
}
