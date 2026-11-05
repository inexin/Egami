using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;

namespace Egami.Formats.Extended.Fits;

/// <summary>
/// Decodes tile-compressed images, written from the FITS tiled image compression convention (Pence, Seaman and White, as the
/// FITS Standard 4.0 registers it): each table row is one tile, its <c>COMPRESSED_DATA</c> a heap array coded with Rice
/// (<c>RICE_1</c>), gzip (<c>GZIP_1</c>, and <c>GZIP_2</c> with its bytes shuffled), IRAF's PLIO line lists (<c>PLIO_1</c>) or
/// nothing (<c>NOCOMPRESS</c>). Float images are quantized integers with a scale and zero a tile, and with the convention's
/// subtractive dithering undone; a tile that could not be quantized keeps its floats in <c>GZIP_COMPRESSED_DATA</c> or
/// <c>UNCOMPRESSED_DATA</c>.
/// </summary>
internal static class FitsTiles
{
    /// <summary>The convention's flag for a missing pixel in a quantized float tile.</summary>
    private const int NullValue = -2147483647;

    /// <summary>The flag SUBTRACTIVE_DITHER_2 uses for a pixel that was exactly 0.</summary>
    private const int ZeroValue = -2147483646;

    private static readonly double[] Random = MakeRandom();

    /// <summary>The supported compressions.</summary>
    internal static bool Supports(string? compression) => compression is "RICE_1" or "RICE_ONE" or "GZIP_1" or "GZIP_2" or "PLIO_1" or "NOCOMPRESS";

    /// <summary>
    /// Decodes every tile of <paramref name="hdu"/> from its table and heap, <paramref name="data"/>, into the physical values of
    /// the whole image, the first axis fastest. Returns the number of tiles that were damaged and left NaN.
    /// </summary>
    public static int Decode<T>(ReadOnlySpan<byte> data, FitsHdu hdu, Span<T> image)
        where T : unmanaged, IFloatingPointIeee754<T>
    {
        FitsTable table = hdu.Table!;
        FitsHeader header = hdu.Header;
        int naxis = hdu.Axes.Count;
        long[] axes = [.. hdu.Axes];
        long[] tile = new long[naxis];
        long[] tiles = new long[naxis];
        long count = 1;
        for (int i = 0; i < naxis; i++)
        {
            tile[i] = Math.Clamp(header.GetInteger("ZTILE" + (i + 1).ToString(CultureInfo.InvariantCulture)) ?? (i == 0 ? axes[0] : 1), 1, axes[i]);
            tiles[i] = (axes[i] + tile[i] - 1) / tile[i];
            count *= tiles[i];
        }

        string compression = hdu.Compression ?? "";
        bool isFloat = hdu.Bitpix < 0;
        FitsColumn? compressed = Column(table, "COMPRESSED_DATA");
        FitsColumn? gzipFloats = Column(table, "GZIP_COMPRESSED_DATA");
        FitsColumn? plain = Column(table, "UNCOMPRESSED_DATA");
        FitsColumn? scaleColumn = Column(table, "ZSCALE");
        FitsColumn? zeroColumn = Column(table, "ZZERO");
        FitsColumn? blankColumn = Column(table, "ZBLANK");
        double? scaleKey = header.GetReal("ZSCALE");
        double? zeroKey = header.GetReal("ZZERO");
        long? blankKey = header.GetInteger("ZBLANK");
        bool quantized = isFloat && (scaleColumn is not null || scaleKey is not null);
        string quantize = header.GetString("ZQUANTIZ")?.Trim() ?? "NO_DITHER";
        int dither = quantize switch
        {
            "SUBTRACTIVE_DITHER_1" => 1,
            "SUBTRACTIVE_DITHER_2" => 2,
            _ => 0,
        };
        long dither0 = header.GetInteger("ZDITHER0") ?? 1;
        int blockSize = (int)Math.Clamp(Parameter(header, "BLOCKSIZE") ?? 32, 1, 1 << 16);
        int bytePix = (int)(Parameter(header, "BYTEPIX") ?? 4);
        int intBytes = quantized ? 4 : Math.Abs(hdu.Bitpix) / 8;

        long tileMax = 1;
        for (int i = 0; i < naxis; i++)
        {
            tileMax *= tile[i];
        }

        long[] values = new long[(int)Math.Min(tileMax, int.MaxValue)];
        byte[] scratch = [];
        int damaged = 0;
        long[] at = new long[naxis];
        long[] size = new long[naxis];
        for (long k = 0; k < count; k++)
        {
            // The tile's place: tiles go along the first axis fastest.
            long rest = k;
            int n = 1;
            for (int i = 0; i < naxis; i++)
            {
                long t = rest % tiles[i];
                rest /= tiles[i];
                at[i] = t * tile[i];
                size[i] = Math.Min(tile[i], axes[i] - at[i]);
                n *= (int)size[i];
            }

            Span<long> v = values.AsSpan(0, n);
            bool ok = k < table.Rows;
            bool floats = false;
            ReadOnlySpan<byte> bytes = default;
            char element = ' ';
            double scale = 1, zero = 0;
            long? blank = blankKey ?? (isFloat ? null : hdu.Blank);
            if (ok)
            {
                int row = (int)k;
                if (blankColumn is not null)
                {
                    blank = (long)Scalar(data, table, blankColumn, row);
                }

                scale = scaleColumn is not null ? Scalar(data, table, scaleColumn, row) : scaleKey ?? 1;
                zero = zeroColumn is not null ? Scalar(data, table, zeroColumn, row) : zeroKey ?? 0;
                ok = Heap(data, table, compressed, row, out bytes, out element);
                if (ok && bytes.IsEmpty)
                {
                    // A tile that could not be compressed or quantized keeps its values in one of the other columns.
                    if (gzipFloats is not null && Heap(data, table, gzipFloats, row, out bytes, out element) && !bytes.IsEmpty)
                    {
                        floats = true;
                        ok = Gunzip(bytes, n * (isFloat ? Math.Abs(hdu.Bitpix) / 8 : intBytes), ref scratch);
                        bytes = scratch;
                        element = isFloat ? (hdu.Bitpix == -32 ? 'E' : 'D') : element;
                    }
                    else if (plain is not null && Heap(data, table, plain, row, out bytes, out element) && !bytes.IsEmpty)
                    {
                        floats = element is 'E' or 'D';
                    }
                    else
                    {
                        ok = false;
                    }
                }
            }

            if (ok && floats)
            {
                ok = Raw(bytes, element, n, v, out bool real);
                if (ok)
                {
                    Place(image, axes, at, size, v, real, real ? 1 : hdu.Scale, real ? 0 : hdu.Zero, real ? null : blank);
                }
            }
            else if (ok)
            {
                if (!bytes.IsEmpty)
                {
                    ok = compression switch
                    {
                        "RICE_1" or "RICE_ONE" => Rice(bytes, v, bytePix, blockSize),
                        "GZIP_1" => Gunzip(bytes, n * intBytes, ref scratch) && Integers(scratch, intBytes, n, v, hdu.Bitpix == 8 && !quantized, false),
                        "GZIP_2" => Gunzip(bytes, n * intBytes, ref scratch) && Integers(scratch, intBytes, n, v, hdu.Bitpix == 8 && !quantized, true),
                        "PLIO_1" => Plio(bytes, v),
                        "NOCOMPRESS" => Raw(bytes, element, n, v, out _),
                        _ => false,
                    };
                }

                if (ok && quantized)
                {
                    Dequantize(image, axes, at, size, v, scale, zero, blank ?? NullValue, dither, (k + dither0 - 1) % 10000);
                }
                else if (ok && isFloat && compression is "GZIP_1" or "GZIP_2")
                {
                    // Floats that were not quantized: the gzip stream holds their IEEE bits (shuffled for GZIP_2), lossless.
                    if (hdu.Bitpix == -32)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            v[i] = BitConverter.DoubleToInt64Bits(BitConverter.Int32BitsToSingle((int)v[i]));
                        }
                    }

                    Place(image, axes, at, size, v, true, 1, 0, null);
                }
                else if (ok)
                {
                    Place(image, axes, at, size, v, false, hdu.Scale, hdu.Zero, blank);
                }
            }

            if (!ok)
            {
                damaged++;
                v.Clear();
                Fill(image, axes, at, size, T.NaN);
            }
        }

        return damaged;
    }

    /// <summary>
    /// Returns the convention's table of 10,000 uniform random numbers, Park and Miller's minimal standard generator from seed 1,
    /// each held at single precision as the convention's table is: the dither subtracted is that float, so values match other
    /// readers to the last bit.
    /// </summary>
    internal static double[] MakeRandom() => [.. Seeds().Select(s => (double)(float)(s / 2147483647))];

    /// <summary>The last seed of the generator, which the convention gives as a check: 1043618065.</summary>
    internal static double LastSeed => Seeds()[^1];

    private static double[] Seeds()
    {
        double[] seeds = new double[10000];
        const double A = 16807, M = 2147483647;
        double seed = 1;
        for (int i = 0; i < seeds.Length; i++)
        {
            double temp = A * seed;
            seed = temp - (M * Math.Floor(temp / M));
            seeds[i] = seed;
        }

        return seeds;
    }

    private static FitsColumn? Column(FitsTable table, string name) => table.Columns.TryGetValue(name, out FitsColumn? c) ? c : null;

    /// <summary>Returns a <c>ZNAMEi</c> parameter's <c>ZVALi</c>, such as BLOCKSIZE or BYTEPIX.</summary>
    private static long? Parameter(FitsHeader header, string name)
    {
        for (int i = 1; i < 100; i++)
        {
            string n = i.ToString(CultureInfo.InvariantCulture);
            string? key = header.GetString("ZNAME" + n)?.Trim();
            if (key is null)
            {
                return null;
            }

            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return header.GetInteger("ZVAL" + n);
            }
        }

        return null;
    }

    /// <summary>Reads a scalar column of a row: ZSCALE and ZZERO are doubles, ZBLANK an integer.</summary>
    private static double Scalar(ReadOnlySpan<byte> data, FitsTable table, FitsColumn column, int row)
    {
        long at = ((long)row * table.RowLength) + column.Offset;
        long width = FitsTable.Width(column.Type, column.Element);
        if (at < 0 || at + width > data.Length)
        {
            return double.NaN;
        }

        ReadOnlySpan<byte> b = data.Slice((int)at, (int)width);
        return column.Type switch
        {
            'D' => BinaryPrimitives.ReadDoubleBigEndian(b),
            'E' => BinaryPrimitives.ReadSingleBigEndian(b),
            'J' => BinaryPrimitives.ReadInt32BigEndian(b),
            'K' => BinaryPrimitives.ReadInt64BigEndian(b),
            'I' => BinaryPrimitives.ReadInt16BigEndian(b),
            'B' => b[0],
            _ => double.NaN,
        };
    }

    /// <summary>Returns a variable-length array column's bytes for a row from the heap; false when the descriptor points outside it.</summary>
    private static bool Heap(ReadOnlySpan<byte> data, FitsTable table, FitsColumn? column, int row, out ReadOnlySpan<byte> bytes, out char element)
    {
        bytes = default;
        element = ' ';
        if (column is null || column.Type is not ('P' or 'Q'))
        {
            return false;
        }

        element = column.Element;
        long at = ((long)row * table.RowLength) + column.Offset;
        int width = column.Type == 'P' ? 8 : 16;
        if (at + width > data.Length)
        {
            return false;
        }

        ReadOnlySpan<byte> d = data.Slice((int)at, width);
        long count = column.Type == 'P' ? (uint)BinaryPrimitives.ReadInt32BigEndian(d) : BinaryPrimitives.ReadInt64BigEndian(d);
        long offset = column.Type == 'P' ? (uint)BinaryPrimitives.ReadInt32BigEndian(d[4..]) : BinaryPrimitives.ReadInt64BigEndian(d[8..]);
        long length = count * FitsTable.Width(element, ' ');
        if (count < 0 || offset < 0 || length < 0 || offset + length > table.HeapLength || table.Heap + offset + length > data.Length)
        {
            return false;
        }

        bytes = data.Slice((int)(table.Heap + offset), (int)length);
        return true;
    }

    /// <summary>Decompresses gzip (or zlib) data into at least <paramref name="length"/> bytes of <paramref name="buffer"/>.</summary>
    private static bool Gunzip(ReadOnlySpan<byte> bytes, int length, ref byte[] buffer)
    {
        if (buffer.Length < length)
        {
            buffer = new byte[length];
        }

        try
        {
            using MemoryStream input = new(bytes.ToArray());
            using Stream inflate = bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B ? new GZipStream(input, CompressionMode.Decompress) : new ZLibStream(input, CompressionMode.Decompress);
            int got = 0;
            while (got < length)
            {
                int read = inflate.Read(buffer, got, length - got);
                if (read == 0)
                {
                    return false;
                }

                got += read;
            }

            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>Reads big-endian integers of <paramref name="width"/> bytes, unshuffled first for GZIP_2.</summary>
    private static bool Integers(ReadOnlySpan<byte> bytes, int width, int n, Span<long> values, bool unsigned, bool shuffled)
    {
        Span<byte> b = stackalloc byte[8];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < width; j++)
            {
                b[j] = shuffled ? bytes[(j * n) + i] : bytes[(i * width) + j];
            }

            values[i] = width switch
            {
                1 => unsigned ? b[0] : (sbyte)b[0],
                2 => BinaryPrimitives.ReadInt16BigEndian(b),
                4 => BinaryPrimitives.ReadInt32BigEndian(b),
                _ => BinaryPrimitives.ReadInt64BigEndian(b),
            };
        }

        return true;
    }

    /// <summary>Reads a raw heap array of type <paramref name="element"/>: integers into <paramref name="values"/>, floats as their bits.</summary>
    private static bool Raw(ReadOnlySpan<byte> bytes, char element, int n, Span<long> values, out bool real)
    {
        real = element is 'E' or 'D';
        long width = FitsTable.Width(element, ' ');
        if (width <= 0 || bytes.Length < n * width)
        {
            return false;
        }

        for (int i = 0; i < n; i++)
        {
            ReadOnlySpan<byte> b = bytes.Slice((int)(i * width), (int)width);
            values[i] = element switch
            {
                'B' => b[0],
                'I' => BinaryPrimitives.ReadInt16BigEndian(b),
                'J' => BinaryPrimitives.ReadInt32BigEndian(b),
                'K' => BinaryPrimitives.ReadInt64BigEndian(b),
                'E' => BitConverter.DoubleToInt64Bits(BinaryPrimitives.ReadSingleBigEndian(b)),
                'D' => BitConverter.DoubleToInt64Bits(BinaryPrimitives.ReadDoubleBigEndian(b)),
                _ => 0,
            };
        }

        return true;
    }

    /// <summary>
    /// Decodes Rice-coded differences: the first value raw in <paramref name="bytePix"/> bytes, then blocks of
    /// <paramref name="blockSize"/> differences, each block an fs code (3, 4 or 5 bits) that says whether its differences are all 0,
    /// stored raw, or split into a unary high part and fs low bits; differences are folded to non-negative (zigzag).
    /// </summary>
    internal static bool Rice(ReadOnlySpan<byte> src, Span<long> dst, int bytePix, int blockSize)
    {
        (int fsBits, int fsMax) = bytePix switch
        {
            1 => (3, 6),
            2 => (4, 14),
            4 => (5, 25),
            _ => (0, 0),
        };
        if (fsBits == 0 || src.Length < bytePix)
        {
            return false;
        }

        int bBits = bytePix * 8;
        uint mask = bBits == 32 ? uint.MaxValue : (1u << bBits) - 1;
        uint last = 0;
        for (int i = 0; i < bytePix; i++)
        {
            last = (last << 8) | src[i];
        }

        BitReader bits = new(src[bytePix..]);
        for (int i = 0; i < dst.Length; i += blockSize)
        {
            int end = Math.Min(i + blockSize, dst.Length);
            if (!bits.Get(fsBits, out uint code))
            {
                return false;
            }

            int fs = (int)code - 1;
            for (int j = i; j < end; j++)
            {
                uint diff;
                if (fs < 0)
                {
                    diff = 0;
                }
                else if (fs == fsMax)
                {
                    if (!bits.Get(bBits, out diff))
                    {
                        return false;
                    }
                }
                else
                {
                    if (!bits.Zeros(out int high) || !bits.Get(fs, out uint low))
                    {
                        return false;
                    }

                    diff = ((uint)high << fs) | low;
                }

                diff = (diff & 1) != 0 ? ~(diff >> 1) : diff >> 1;
                last = (last + diff) & mask;
                dst[j] = bytePix switch
                {
                    1 => last,
                    2 => (short)last,
                    _ => (int)last,
                };
            }
        }

        return true;
    }

    /// <summary>
    /// Decodes an IRAF PLIO line list of 16-bit words: a header giving the list's length and first instruction, then
    /// instructions of a 4-bit opcode and 12 bits of data that write runs of zeros, runs or single pixels of the current value,
    /// or change that value.
    /// </summary>
    internal static bool Plio(ReadOnlySpan<byte> src, Span<long> dst)
    {
        int words = src.Length / 2;
        if (words < 3)
        {
            return false;
        }

        int length, first;
        if (Word(src, 2) > 0)
        {
            length = Word(src, 2);
            first = 3;
        }
        else
        {
            if (words < 5)
            {
                return false;
            }

            length = (Word(src, 4) << 15) + Word(src, 3);
            first = Word(src, 1);
            if (first < 3)
            {
                return false;
            }
        }

        length = Math.Min(length, words);
        long pv = 1;
        int x = 0;
        dst.Clear();
        for (int ip = first; ip < length; ip++)
        {
            int word = Word(src, ip) & 0xFFFF;
            int opcode = word >> 12;
            int data = word & 0xFFF;
            switch (opcode)
            {
                case 0:
                case 4:
                case 5:
                    int run = Math.Min(data, dst.Length - x);
                    if (opcode == 4)
                    {
                        dst.Slice(x, run).Fill(pv);
                    }
                    else if (opcode == 5 && run == data && data > 0)
                    {
                        dst[x + data - 1] = pv;
                    }

                    x += run;
                    break;
                case 1:
                    if (ip + 1 >= length)
                    {
                        return false;
                    }

                    pv = ((long)(Word(src, ip + 1) & 0xFFFF) << 12) + data;
                    ip++;
                    break;
                case 2:
                    pv += data;
                    break;
                case 3:
                    pv -= data;
                    break;
                case 6:
                case 7:
                    pv += opcode == 7 ? -data : data;
                    if (x < dst.Length)
                    {
                        dst[x++] = pv;
                    }

                    break;
                default:
                    return false;
            }

            if (x >= dst.Length)
            {
                break;
            }
        }

        return true;
    }

    private static int Word(ReadOnlySpan<byte> src, int i) => BinaryPrimitives.ReadInt16BigEndian(src[(i * 2)..]);

    /// <summary>Writes a tile's integers (or float bits) into the image as physical values.</summary>
    private static void Place<T>(Span<T> image, long[] axes, long[] at, long[] size, ReadOnlySpan<long> v, bool real, double scale, double zero, long? blank)
        where T : unmanaged, IFloatingPointIeee754<T>
    {
        int i = 0;
        foreach (long start in Rows(axes, at, size))
        {
            Span<T> row = image.Slice((int)start, (int)size[0]);
            for (int x = 0; x < row.Length; x++, i++)
            {
                long s = v[i];
                row[x] = real ? T.CreateTruncating(BitConverter.Int64BitsToDouble(s)) : s == blank ? T.NaN : T.CreateTruncating((s * scale) + zero);
            }
        }
    }

    /// <summary>Undoes a float tile's quantization, with the convention's subtractive dither when asked.</summary>
    private static void Dequantize<T>(Span<T> image, long[] axes, long[] at, long[] size, ReadOnlySpan<long> v, double scale, double zero, long blank, int dither, long seed)
        where T : unmanaged, IFloatingPointIeee754<T>
    {
        int iseed = (int)Math.Max(seed, 0);
        int next = (int)(Random[iseed] * 500);
        int i = 0;
        foreach (long start in Rows(axes, at, size))
        {
            Span<T> row = image.Slice((int)start, (int)size[0]);
            for (int x = 0; x < row.Length; x++, i++)
            {
                long s = v[i];
                if (s == blank)
                {
                    row[x] = T.NaN;
                }
                else if (dither == 0)
                {
                    row[x] = T.CreateTruncating((s * scale) + zero);
                }
                else if (dither == 2 && s == ZeroValue)
                {
                    row[x] = T.Zero;
                }
                else
                {
                    row[x] = T.CreateTruncating(((s - Random[next] + 0.5) * scale) + zero);
                }

                if (dither != 0 && ++next == 10000)
                {
                    iseed = (iseed + 1) % 10000;
                    next = (int)(Random[iseed] * 500);
                }
            }
        }
    }

    private static void Fill<T>(Span<T> image, long[] axes, long[] at, long[] size, T value)
        where T : unmanaged
    {
        foreach (long start in Rows(axes, at, size))
        {
            image.Slice((int)start, (int)size[0]).Fill(value);
        }
    }

    /// <summary>Returns where each row of a tile starts in the image, in the tile's order.</summary>
    private static IEnumerable<long> Rows(long[] axes, long[] at, long[] size)
    {
        int naxis = axes.Length;
        long[] pos = new long[naxis];
        long rows = 1;
        for (int i = 1; i < naxis; i++)
        {
            rows *= size[i];
        }

        for (long r = 0; r < rows; r++)
        {
            long rest = r;
            long offset = at[0];
            long stride = axes[0];
            for (int i = 1; i < naxis; i++)
            {
                pos[i] = at[i] + (rest % size[i]);
                rest /= size[i];
                offset += pos[i] * stride;
                stride *= axes[i];
            }

            yield return offset;
        }
    }

    /// <summary>Reads bits from the most significant end of each byte.</summary>
    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private ulong _acc;
        private int _count;
        private int _pos;

        public bool Get(int n, out uint value)
        {
            value = 0;
            if (n == 0)
            {
                return true;
            }

            Fill();
            if (_count < n)
            {
                return false;
            }

            value = (uint)(_acc >> (64 - n));
            _acc <<= n;
            _count -= n;
            return true;
        }

        /// <summary>Counts the zero bits before the next 1 bit and reads past that 1.</summary>
        public bool Zeros(out int zeros)
        {
            zeros = 0;
            while (true)
            {
                Fill();
                if (_count == 0)
                {
                    return false;
                }

                int lz = BitOperations.LeadingZeroCount(_acc);
                if (lz < _count)
                {
                    zeros += lz;
                    _acc = lz == 63 ? 0 : _acc << (lz + 1);
                    _count -= lz + 1;
                    return true;
                }

                zeros += _count;
                _acc = 0;
                _count = 0;
                if (zeros > 1 << 26)
                {
                    return false;
                }
            }
        }

        private void Fill()
        {
            while (_count <= 56 && _pos < _data.Length)
            {
                _acc |= (ulong)_data[_pos++] << (56 - _count);
                _count += 8;
            }
        }
    }
}
