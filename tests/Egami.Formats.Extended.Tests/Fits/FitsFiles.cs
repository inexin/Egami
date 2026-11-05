using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Egami.Formats.Extended.Tests.Fits;

/// <summary>Builds FITS files from the standard's layout, and tile-compressed images from the tiled-image convention.</summary>
internal static class FitsFiles
{
    /// <summary>Returns a header block (or blocks) of the cards, with END, padded with spaces.</summary>
    public static byte[] Header(params string[] cards)
    {
        StringBuilder s = new();
        foreach (string card in cards.Append("END"))
        {
            s.Append(card.PadRight(80));
        }

        while (s.Length % 2880 != 0)
        {
            s.Append(' ');
        }

        return Encoding.ASCII.GetBytes(s.ToString());
    }

    /// <summary>Formats a value card in fixed format.</summary>
    public static string Card(string keyword, object value)
    {
        string text = value switch
        {
            bool b => (b ? "T" : "F").PadLeft(20),
            string s => "'" + s.PadRight(8) + "'",
            double d => d.ToString("E15", CultureInfo.InvariantCulture).PadLeft(20),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)!.PadLeft(20),
        };
        return keyword.PadRight(8) + "= " + text;
    }

    /// <summary>Pads data to whole blocks with zeros.</summary>
    public static byte[] Pad(byte[] data) => [.. data, .. new byte[(2880 - (data.Length % 2880)) % 2880]];

    /// <summary>Encodes values as big-endian samples of <paramref name="bitpix"/>.</summary>
    public static byte[] Samples(int bitpix, IEnumerable<double> values)
    {
        List<byte> bytes = [];
        Span<byte> b = stackalloc byte[8];
        foreach (double v in values)
        {
            switch (bitpix)
            {
                case 8:
                    bytes.Add((byte)v);
                    continue;
                case 16:
                    BinaryPrimitives.WriteInt16BigEndian(b, (short)v);
                    bytes.AddRange(b[..2].ToArray());
                    continue;
                case 32:
                    BinaryPrimitives.WriteInt32BigEndian(b, (int)v);
                    bytes.AddRange(b[..4].ToArray());
                    continue;
                case 64:
                    BinaryPrimitives.WriteInt64BigEndian(b, (long)v);
                    bytes.AddRange(b.ToArray());
                    continue;
                case -32:
                    BinaryPrimitives.WriteSingleBigEndian(b, (float)v);
                    bytes.AddRange(b[..4].ToArray());
                    continue;
                default:
                    BinaryPrimitives.WriteDoubleBigEndian(b, v);
                    bytes.AddRange(b.ToArray());
                    continue;
            }
        }

        return [.. bytes];
    }

    /// <summary>A primary HDU holding an image of the stored values.</summary>
    public static byte[] Image(int bitpix, long[] axes, IEnumerable<double> stored, params string[] extra)
    {
        List<string> cards = [Card("SIMPLE", true), Card("BITPIX", bitpix), Card("NAXIS", axes.Length)];
        cards.AddRange(axes.Select((a, i) => Card("NAXIS" + (i + 1).ToString(CultureInfo.InvariantCulture), a)));
        cards.AddRange(extra);
        return [.. Header([.. cards]), .. Pad(Samples(bitpix, stored))];
    }

    /// <summary>An IMAGE extension.</summary>
    public static byte[] Extension(int bitpix, long[] axes, IEnumerable<double> stored, params string[] extra)
    {
        List<string> cards = [Card("XTENSION", "IMAGE"), Card("BITPIX", bitpix), Card("NAXIS", axes.Length)];
        cards.AddRange(axes.Select((a, i) => Card("NAXIS" + (i + 1).ToString(CultureInfo.InvariantCulture), a)));
        cards.Add(Card("PCOUNT", 0));
        cards.Add(Card("GCOUNT", 1));
        cards.AddRange(extra);
        return [.. Header([.. cards]), .. Pad(Samples(bitpix, stored))];
    }

    /// <summary>A primary HDU with no data.</summary>
    public static byte[] Empty() => Header(Card("SIMPLE", true), Card("BITPIX", 8), Card("NAXIS", 0), Card("EXTEND", true));

    /// <summary>One tile's coded bytes, and its optional scale, zero and blank.</summary>
    public sealed record Tile(byte[] Data, double? Scale = null, double? Zero = null, int? Blank = null, byte[]? Floats = null);

    /// <summary>
    /// A tile-compressed image extension: a binary table with one row a tile and the coded bytes in the heap, as the tiled-image
    /// convention lays it out.
    /// </summary>
    public static byte[] Compressed(string type, int zbitpix, long[] axes, long[] tile, IReadOnlyList<Tile> tiles, char element = 'B', params string[] extra)
    {
        bool scaled = tiles.Any(t => t.Scale is not null);
        bool blanks = tiles.Any(t => t.Blank is not null);
        bool floats = tiles.Any(t => t.Floats is not null);
        List<(string Name, string Form, int Width)> columns = [("COMPRESSED_DATA", $"1P{element}", 8)];
        if (floats)
        {
            columns.Add(("GZIP_COMPRESSED_DATA", "1PB", 8));
        }

        if (scaled)
        {
            columns.Add(("ZSCALE", "1D", 8));
            columns.Add(("ZZERO", "1D", 8));
        }

        if (blanks)
        {
            columns.Add(("ZBLANK", "1J", 4));
        }

        int rowLength = columns.Sum(c => c.Width);
        List<byte> rows = [];
        List<byte> heap = [];
        Span<byte> b = stackalloc byte[8];
        int elementWidth = element switch { 'I' => 2, 'J' => 4, _ => 1 };
        foreach (Tile t in tiles)
        {
            BinaryPrimitives.WriteInt32BigEndian(b, t.Data.Length / elementWidth);
            BinaryPrimitives.WriteInt32BigEndian(b[4..], heap.Count);
            rows.AddRange(b.ToArray());
            heap.AddRange(t.Data);
            if (floats)
            {
                byte[] f = t.Floats ?? [];
                BinaryPrimitives.WriteInt32BigEndian(b, f.Length);
                BinaryPrimitives.WriteInt32BigEndian(b[4..], heap.Count);
                rows.AddRange(b.ToArray());
                heap.AddRange(f);
            }

            if (scaled)
            {
                BinaryPrimitives.WriteDoubleBigEndian(b, t.Scale ?? 1);
                rows.AddRange(b.ToArray());
                BinaryPrimitives.WriteDoubleBigEndian(b, t.Zero ?? 0);
                rows.AddRange(b.ToArray());
            }

            if (blanks)
            {
                BinaryPrimitives.WriteInt32BigEndian(b, t.Blank ?? 0);
                rows.AddRange(b[..4].ToArray());
            }
        }

        List<string> cards =
        [
            Card("XTENSION", "BINTABLE"), Card("BITPIX", 8), Card("NAXIS", 2), Card("NAXIS1", rowLength), Card("NAXIS2", tiles.Count),
            Card("PCOUNT", heap.Count), Card("GCOUNT", 1), Card("TFIELDS", columns.Count),
        ];
        for (int i = 0; i < columns.Count; i++)
        {
            cards.Add(Card("TTYPE" + (i + 1).ToString(CultureInfo.InvariantCulture), columns[i].Name));
            cards.Add(Card("TFORM" + (i + 1).ToString(CultureInfo.InvariantCulture), columns[i].Form));
        }

        cards.Add(Card("ZIMAGE", true));
        cards.Add(Card("ZCMPTYPE", type));
        cards.Add(Card("ZBITPIX", zbitpix));
        cards.Add(Card("ZNAXIS", axes.Length));
        for (int i = 0; i < axes.Length; i++)
        {
            cards.Add(Card("ZNAXIS" + (i + 1).ToString(CultureInfo.InvariantCulture), axes[i]));
            cards.Add(Card("ZTILE" + (i + 1).ToString(CultureInfo.InvariantCulture), tile[i]));
        }

        cards.AddRange(extra);
        return [.. Header([.. cards]), .. Pad([.. rows, .. heap])];
    }

    /// <summary>Gzips bytes.</summary>
    public static byte[] Gzip(byte[] data)
    {
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(data);
        }

        return output.ToArray();
    }

    /// <summary>Shuffles the bytes of big-endian values, all first bytes first, as GZIP_2 does before compressing.</summary>
    public static byte[] Shuffle(byte[] data, int width)
    {
        int n = data.Length / width;
        byte[] shuffled = new byte[data.Length];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < width; j++)
            {
                shuffled[(j * n) + i] = data[(i * width) + j];
            }
        }

        return shuffled;
    }

    /// <summary>
    /// Rice-codes values as the convention describes: the first value raw, then for each block of differences (folded to
    /// non-negative) a split fs chosen from their mean, low-entropy blocks of zeros as fs code 0, and wide ones raw.
    /// </summary>
    public static byte[] Rice(long[] values, int bytePix, int blockSize = 32)
    {
        (int fsBits, int fsMax) = bytePix switch { 1 => (3, 6), 2 => (4, 14), _ => (5, 25) };
        int bBits = bytePix * 8;
        ulong mask = bBits == 32 ? uint.MaxValue : (1UL << bBits) - 1;
        BitWriter w = new();
        ulong first = (ulong)values[0] & mask;
        for (int i = bytePix - 1; i >= 0; i--)
        {
            w.Bits((uint)((first >> (i * 8)) & 0xFF), 8);
        }

        ulong last = first;
        for (int i = 0; i < values.Length; i += blockSize)
        {
            int end = Math.Min(i + blockSize, values.Length);
            ulong[] diffs = new ulong[end - i];
            double sum = 0;
            for (int j = i; j < end; j++)
            {
                ulong v = (ulong)values[j] & mask;
                long d = (long)(((v - last) & mask) << (64 - bBits)) >> (64 - bBits);
                last = v;
                ulong folded = d < 0 ? (ulong)(~(d << 1)) & mask : (ulong)(d << 1) & mask;
                diffs[j - i] = folded;
                sum += folded;
            }

            int fs = 0;
            double mean = sum / diffs.Length;
            while (fs < fsMax && (1UL << (fs + 1)) <= mean)
            {
                fs++;
            }

            if (sum == 0)
            {
                w.Bits(0, fsBits);
            }
            else if (fs >= fsMax)
            {
                w.Bits((uint)(fsMax + 1), fsBits);
                foreach (ulong d in diffs)
                {
                    w.Bits((uint)d, bBits);
                }
            }
            else
            {
                w.Bits((uint)(fs + 1), fsBits);
                foreach (ulong d in diffs)
                {
                    ulong top = d >> fs;
                    for (ulong z = 0; z < top; z++)
                    {
                        w.Bits(0, 1);
                    }

                    w.Bits(1, 1);
                    w.Bits((uint)(d & ((1UL << fs) - 1)), fs);
                }
            }
        }

        return w.Bytes();
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _acc;
        private int _count;

        public void Bits(uint value, int n)
        {
            for (int i = n - 1; i >= 0; i--)
            {
                _acc = (_acc << 1) | (int)((value >> i) & 1);
                if (++_count == 8)
                {
                    _bytes.Add((byte)_acc);
                    _acc = 0;
                    _count = 0;
                }
            }
        }

        public byte[] Bytes() => _count == 0 ? [.. _bytes] : [.. _bytes, (byte)(_acc << (8 - _count))];
    }
}
