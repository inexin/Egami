using System.Buffers.Binary;
using System.Text;

namespace Egami.Video.Tests.Containers;

/// <summary>Builds EBML elements by hand from RFC 8794, independently of the library's own writer.</summary>
internal static class EbmlBuilder
{
    public static byte[] Element(uint id, params byte[][] children) => Element(id, children.SelectMany(c => c).ToArray());

    public static byte[] Element(uint id, byte[] body) => [.. Id(id), .. Size(body.Length), .. body];

    /// <summary>A master element with an unknown size (all ones in a one-byte VINT... here an 8-byte one).</summary>
    public static byte[] UnknownSize(uint id, params byte[][] children) =>
        [.. Id(id), 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, .. children.SelectMany(c => c)];

    public static byte[] Uint(uint id, ulong value)
    {
        byte[] body = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(body, value);
        int skip = 0;
        while (skip < 7 && body[skip] == 0)
        {
            skip++;
        }

        return Element(id, body[skip..]);
    }

    public static byte[] Text(uint id, string value) => Element(id, Encoding.UTF8.GetBytes(value));

    public static byte[] Float(uint id, double value)
    {
        byte[] body = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(body, value);
        return Element(id, body);
    }

    public static byte[] Id(uint id) =>
        id >= 0x1000000 ? [(byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id]
        : id >= 0x10000 ? [(byte)(id >> 16), (byte)(id >> 8), (byte)id]
        : id >= 0x100 ? [(byte)(id >> 8), (byte)id]
        : [(byte)id];

    public static byte[] Size(long size)
    {
        int length = 1;
        while (size >= (1L << (7 * length)) - 1)
        {
            length++;
        }

        byte[] bytes = new byte[length];
        for (int i = length - 1; i >= 0; i--)
        {
            bytes[i] = (byte)size;
            size >>= 8;
        }

        bytes[0] |= (byte)(0x80 >> (length - 1));
        return bytes;
    }

    /// <summary>A SimpleBlock body: track VINT, 16-bit relative time, flags, then the (possibly laced) data.</summary>
    public static byte[] SimpleBlock(int track, short time, byte flags, byte[] data) =>
        Element(0xA3, [.. Size(track), (byte)(time >> 8), (byte)time, flags, .. data]);

    /// <summary>A minimal Matroska file: EBML header, a Segment with Info and Tracks, then the given clusters.</summary>
    public static byte[] MatroskaFile(string docType, byte[][] trackEntries, params byte[][] clusters) =>
    [
        .. Element(0x1A45DFA3, Text(0x4282, docType), Uint(0x4287, 4), Uint(0x4285, 2)),
        .. Element(0x18538067,
            [
                Element(0x1549A966, Uint(0x2AD7B1, 1_000_000)),
                Element(0x1654AE6B, trackEntries),
                .. clusters,
            ]),
    ];

    public static byte[] AudioTrack(int number, string codec, long defaultDuration = 0) => Element(0xAE,
    [
        Uint(0xD7, (ulong)number),
        Uint(0x83, 2),
        Text(0x86, codec),
        .. defaultDuration > 0 ? (byte[][])[Uint(0x23E383, (ulong)defaultDuration)] : [],
        Element(0xE1, Float(0xB5, 48000), Uint(0x9F, 2)),
    ]);

    public static byte[] Cluster(long timestamp, params byte[][] blocks) => Element(0x1F43B675, [Uint(0xE7, (ulong)timestamp), .. blocks]);
}
