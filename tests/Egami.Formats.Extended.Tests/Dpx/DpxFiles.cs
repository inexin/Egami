namespace Egami.Formats.Extended.Tests.Dpx;

/// <summary>
/// Builds DPX files by hand, to reach what Egami's writer does not write: packings 0 and 2, 1-bit and float samples, CbYCr, run-length
/// coding, several elements and other orientations. The sample layouts are the ones ImageMagick and ffmpeg read (checked against
/// both as black boxes when this was written).
/// </summary>
internal sealed class DpxFiles
{
    public const int Offset = 2048;

    /// <summary>One image element to build.</summary>
    public sealed record Element(int Descriptor, int Bits, int Packing, uint[] Samples, int Encoding = 0, int Transfer = 2, int Colorimetric = 2);

    /// <summary>Builds a file of the elements, each a <paramref name="width"/> × <paramref name="height"/> image.</summary>
    public static byte[] Build(int width, int height, bool big, int orientation, params Element[] elements)
    {
        List<byte[]> data = [.. elements.Select(e => e.Encoding == 1 ? Pack(e.Samples, e.Bits, e.Packing, big, e.Samples.Length, e.Descriptor) : Rows(e, width, height, big))];
        byte[] file = new byte[Offset + data.Sum(d => d.Length)];
        Span<byte> h = file;
        "SDPX"u8.CopyTo(h);
        if (!big)
        {
            "XPDS"u8.CopyTo(h);
        }

        U32(h, 4, Offset, big);
        "V2.0"u8.CopyTo(h[8..]);
        U32(h, 16, (uint)file.Length, big);
        U32(h, 24, 1664, big);
        U32(h, 28, 384, big);
        U16(h, 768, (ushort)orientation, big);
        U16(h, 770, (ushort)elements.Length, big);
        U32(h, 772, (uint)width, big);
        U32(h, 776, (uint)height, big);
        int at = Offset;
        for (int i = 0; i < elements.Length; i++)
        {
            Element e = elements[i];
            int b = 780 + (i * 72);
            U32(h, b + 12, e.Bits >= 32 ? 0 : (uint)((1L << e.Bits) - 1), big);
            h[b + 20] = (byte)e.Descriptor;
            h[b + 21] = (byte)e.Transfer;
            h[b + 22] = (byte)e.Colorimetric;
            h[b + 23] = (byte)e.Bits;
            U16(h, b + 24, (ushort)e.Packing, big);
            U16(h, b + 26, (ushort)e.Encoding, big);
            U32(h, b + 28, (uint)at, big);
            data[i].CopyTo(file, at);
            at += data[i].Length;
        }

        U32(h, 1920, 0x01020304, big);
        U32(h, 1724, BitConverter.SingleToUInt32Bits(24f), big);
        return file;
    }

    /// <summary>Returns the element's rows, each started on a 32-bit word.</summary>
    private static byte[] Rows(Element e, int width, int height, bool big)
    {
        int perRow = e.Samples.Length / height;
        List<byte> bytes = [];
        for (int y = 0; y < height; y++)
        {
            byte[] row = Pack(e.Samples.AsSpan(y * perRow, perRow).ToArray(), e.Bits, e.Packing, big, perRow, e.Descriptor);
            bytes.AddRange(row);
            bytes.AddRange(new byte[(4 - (row.Length % 4)) % 4]);
        }

        return [.. bytes];
    }

    /// <summary>Packs samples as ST 268 lays them out for the depth and packing.</summary>
    public static byte[] Pack(uint[] samples, int bits, int packing, bool big, int count, int descriptor)
    {
        List<byte> bytes = [];
        switch (bits)
        {
            case 8:
                bytes.AddRange(samples.Select(s => (byte)s));
                break;
            case 16:
                foreach (uint s in samples)
                {
                    bytes.AddRange(Word16((ushort)s, big));
                }

                break;
            case 12 when packing is 1 or 2:
                foreach (uint s in samples)
                {
                    bytes.AddRange(Word16((ushort)(packing == 1 ? s << 4 : s), big));
                }

                break;
            case 10 when packing is 1 or 2:
                bool lowFirst = descriptor is >= 1 and <= 8;
                for (int i = 0; i < count; i += 3)
                {
                    uint a = samples[i], b = i + 1 < count ? samples[i + 1] : 0, c = i + 2 < count ? samples[i + 2] : 0;
                    if (lowFirst)
                    {
                        (a, c) = (c, a);
                    }

                    uint word = packing == 1 ? (a << 22) | (b << 12) | (c << 2) : (a << 20) | (b << 10) | c;
                    bytes.AddRange(Word32(word, big));
                }

                break;
            case 32:
                foreach (uint s in samples)
                {
                    bytes.AddRange(Word32(s, big));
                }

                break;
            default:
                {
                    // Packed from the lowest bit of each 32-bit word up.
                    ulong acc = 0;
                    int have = 0;
                    foreach (uint s in samples)
                    {
                        acc |= (ulong)s << have;
                        have += bits;
                        while (have >= 32)
                        {
                            bytes.AddRange(Word32((uint)acc, big));
                            acc >>= 32;
                            have -= 32;
                        }
                    }

                    if (have > 0)
                    {
                        bytes.AddRange(Word32((uint)acc, big));
                    }

                    break;
                }
        }

        return [.. bytes];
    }

    private static byte[] Word16(ushort v, bool big) => big ? [(byte)(v >> 8), (byte)v] : [(byte)v, (byte)(v >> 8)];

    private static byte[] Word32(uint v, bool big) => big ? [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v] : [(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)];

    private static void U32(Span<byte> h, int at, uint v, bool big) => Word32(v, big).CopyTo(h[at..]);

    private static void U16(Span<byte> h, int at, ushort v, bool big) => Word16(v, big).CopyTo(h[at..]);
}
