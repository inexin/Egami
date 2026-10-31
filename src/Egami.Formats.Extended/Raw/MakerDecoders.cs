using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;

namespace Egami.Formats.Extended.Raw;

/// <summary>
/// The maker sensor-data codecs, each written from its clean-room specification (a study-only session read LibRaw and rawspeed
/// for structure and described the formats in its own words; these decoders were written from those descriptions alone):
/// Pentax Huffman, Nikon Huffman with its curve, Sony ARW2 blocks, ARW1 and tiled lossless JPEG, Olympus, Panasonic RawFormat 4
/// to 8, Canon CR2's sliced lossless JPEG, and the plain packings several makers use.
/// </summary>
internal static class MakerDecoders
{
    public static void Decode(RawSensor sensor, RawTiff tiff, RawPixels pixels, ParallelOptions parallel, Action<int, string> damaged)
    {
        long offset = tiff.Origin + (sensor.Offsets.Length > 0 ? sensor.Offsets[0] : 0);
        // A count of 0 means the data runs to the end of the file (Panasonic).
        long count = sensor.Counts.Length > 0 && sensor.Counts.Sum() > 0 ? sensor.Counts.Sum() : tiff.Length - offset;
        if (offset <= 0 || offset >= tiff.Length)
        {
            throw new InvalidDataException("The raw data lies outside the file.");
        }

        if (count > tiff.Length - offset)
        {
            count = tiff.Length - offset;
            if (sensor.Codec is not (RawCodec.PanasonicV4 or RawCodec.PanasonicV5 or RawCodec.PanasonicV6 or RawCodec.PanasonicV7 or RawCodec.PanasonicV8))
            {
                damaged(0, "The raw data runs past the end of the file; what is missing reads as black.");
            }
        }

        ReadOnlySpan<byte> data = tiff.Data.AsSpan((int)offset, (int)count);
        switch (sensor.Codec)
        {
            case RawCodec.Unpacked16:
                Unpacked16(data, pixels, sensor.BigEndian);
                break;
            case RawCodec.Packed12Lsb:
                Packed12Lsb(data, pixels);
                break;
            case RawCodec.PentaxHuffman:
                Pentax(data, pixels, (PentaxTable?)sensor.CodecData, sensor.Bits);
                break;
            case RawCodec.Nikon:
                Nikon(data, pixels, (NikonMeta)sensor.CodecData!, sensor.Bits);
                break;
            case RawCodec.SonyArw2:
                SonyArw2(tiff.Data, (int)offset, (int)count, pixels, (ushort[])sensor.CodecData!, parallel);
                break;
            case RawCodec.SonyArw1:
                SonyArw1(data, pixels);
                break;
            case RawCodec.SonyLossless:
                SonyLossless(sensor, tiff, pixels, parallel, damaged);
                break;
            case RawCodec.Olympus:
                Olympus(data, pixels, (OlympusParams)sensor.CodecData!);
                break;
            case RawCodec.OlympusPacked:
                OlympusPacked(data, pixels, (OlympusPacking)sensor.CodecData!, sensor.BigEndian);
                break;
            case RawCodec.PanasonicV4:
            case RawCodec.PanasonicV5:
                PanasonicBlocks(data, pixels, sensor, parallel);
                break;
            case RawCodec.PanasonicV6:
                PanasonicV6(data, pixels, sensor.Bits, parallel);
                break;
            case RawCodec.PanasonicV7:
                PanasonicV7(data, pixels, sensor.Bits, parallel);
                break;
            case RawCodec.PanasonicV8:
                PanasonicV8(tiff, pixels, (PanasonicTags)sensor.CodecData!, parallel);
                break;
            case RawCodec.Canon:
                Canon(tiff.Data, (int)offset, (int)count, pixels, (CanonSlices)sensor.CodecData!, damaged);
                break;
            default:
                throw new NotSupportedException($"Egami does not read the {sensor.Codec} raw codec yet.");
        }
    }

    private static void Unpacked16(ReadOnlySpan<byte> data, RawPixels pixels, bool bigEndian)
    {
        Span<ushort> out16 = pixels.U16;
        int n = Math.Min(out16.Length, data.Length / 2);
        for (int i = 0; i < n; i++)
        {
            out16[i] = bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(data[(i * 2)..]) : BinaryPrimitives.ReadUInt16LittleEndian(data[(i * 2)..]);
        }
    }

    /// <summary>12-bit samples packed two in three bytes, least significant bits first (Sony spec, section 6).</summary>
    private static void Packed12Lsb(ReadOnlySpan<byte> data, RawPixels pixels)
    {
        Span<ushort> out16 = pixels.U16;
        for (int i = 0, p = 0; i + 1 < out16.Length && p + 2 < data.Length; i += 2, p += 3)
        {
            out16[i] = (ushort)(data[p] | ((data[p + 1] & 0xF) << 8));
            out16[i + 1] = (ushort)((data[p + 1] >> 4) | (data[p + 2] << 4));
        }
    }

    /// <summary>A most-significant-bit-first reader of plain bytes, no stuffing; past the end it reads zeros.</summary>
    private ref struct MsbBits(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _pos;
        private ulong _buffer;
        private int _count;

        public readonly bool Overrun => _pos > _data.Length + 8;

        public uint Peek(int n)
        {
            Fill();
            return n == 0 ? 0 : (uint)(_buffer >> (64 - n));
        }

        public void Skip(int n)
        {
            _buffer <<= n;
            _count -= n;
        }

        public uint Read(int n)
        {
            if (n == 0)
            {
                return 0;
            }

            uint v = Peek(n);
            Skip(n);
            return v;
        }

        /// <summary>Counts zero bits up to and including the terminating one bit, at most <paramref name="max"/> zeros (the one is then not read).</summary>
        public int Zeros(int max)
        {
            int zeros = 0;
            while (zeros < max)
            {
                Fill();
                int lead = BitOperations.LeadingZeroCount(_buffer);
                if (lead >= 56)
                {
                    lead = Math.Min(56, max - zeros);
                    Skip(lead);
                    zeros += lead;
                    if (_pos > _data.Length + 16)
                    {
                        return zeros;
                    }

                    continue;
                }

                int take = Math.Min(lead, max - zeros);
                Skip(take);
                zeros += take;
                if (zeros < max)
                {
                    Skip(1);
                    return zeros;
                }
            }

            return zeros;
        }

        private void Fill()
        {
            while (_count <= 56)
            {
                ulong b = _pos < _data.Length ? _data[_pos] : 0UL;
                _pos++;
                _buffer |= b << (56 - _count);
                _count += 8;
            }
        }
    }

    /// <summary>A Huffman decoder from a 16-bit peek: (length, symbol) per peek value.</summary>
    private sealed class PeekTable
    {
        private readonly ushort[] _entries;

        public PeekTable(int bits)
        {
            Bits = bits;
            _entries = new ushort[1 << bits];
        }

        public int Bits { get; }

        /// <summary>Builds the canonical table of JPEG-style counts per length and symbols (T.81 Annex C).</summary>
        public static PeekTable Canonical(ReadOnlySpan<byte> counts, ReadOnlySpan<byte> symbols)
        {
            PeekTable t = new(16);
            int code = 0, k = 0;
            for (int length = 1; length <= 16; length++)
            {
                for (int i = 0; i < counts[length - 1] && k < symbols.Length; i++, k++, code++)
                {
                    t.Set(code, length, symbols[k]);
                }

                code <<= 1;
            }

            return t;
        }

        public void Set(int code, int length, int symbol)
        {
            int shift = Bits - length;
            for (int j = 0; j < 1 << shift; j++)
            {
                int at = (code << shift) | j;
                if (_entries[at] == 0)
                {
                    _entries[at] = (ushort)((length << 8) | symbol);
                }
            }
        }

        /// <summary>Decodes one symbol; returns −1 for a code the table does not have.</summary>
        public int Decode(ref MsbBits bits)
        {
            int e = _entries[bits.Peek(Bits)];
            if (e == 0)
            {
                return -1;
            }

            bits.Skip(e >> 8);
            return e & 0xFF;
        }
    }

    /// <summary>The JPEG sign extension of r read in <paramref name="length"/> bits (T.81 F.2.2.1).</summary>
    private static int Extend(uint r, int length) => length == 0 ? 0 : (r & (1u << (length - 1))) == 0 ? (int)r - ((1 << length) - 1) : (int)r;

    /// <summary>Pentax PEF Huffman (PEF spec, sections 2 to 4).</summary>
    private static void Pentax(ReadOnlySpan<byte> data, RawPixels pixels, PentaxTable? table, int bits)
    {
        PeekTable codes;
        if (table is not null)
        {
            byte[] b = table.Bytes;
            int U(int at) => table.BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at)) : BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
            int depth = U(0) + 12;
            if (depth is < 1 or > 15 || 14 + (3 * depth) > b.Length)
            {
                throw new InvalidDataException("The Pentax Huffman table is not valid.");
            }

            codes = new PeekTable(12);
            for (int c = 0; c < depth; c++)
            {
                int length = b[14 + (2 * depth) + c];
                if (length is < 1 or > 12)
                {
                    throw new InvalidDataException("The Pentax Huffman table has a code length outside 1 to 12.");
                }

                codes.Set(U(14 + (2 * c)) >> (12 - length), length, c);
            }
        }
        else
        {
            codes = PeekTable.Canonical([0, 2, 3, 1, 1, 1, 1, 1, 1, 2, 0, 0, 0, 0, 0, 0], [3, 4, 2, 5, 1, 6, 0, 7, 8, 9, 10, 11, 12]);
        }

        MsbBits reader = new(data);
        int width = pixels.Width, height = pixels.Height, max = (1 << bits) - 1;
        for (int y = 0; y < height; y++)
        {
            Span<ushort> row = pixels.U16Row(y);
            Span<int> pred = [y >= 2 ? pixels.U16Row(y - 2)[0] : 0, y >= 2 && width > 1 ? pixels.U16Row(y - 2)[1] : 0];
            for (int x = 0; x < width; x++)
            {
                int s = codes.Decode(ref reader);
                if (s < 0 || reader.Overrun)
                {
                    throw new InvalidDataException($"The Pentax data is damaged at row {y}.");
                }

                pred[x & 1] += Extend(reader.Read(s), s);
                if ((uint)pred[x & 1] > (uint)max)
                {
                    throw new InvalidDataException($"The Pentax data decodes outside the sample range at row {y}.");
                }

                row[x] = (ushort)pred[x & 1];
            }
        }
    }

    private static readonly (byte[] Counts, byte[] Symbols)[] NikonTables =
    [
        ([0, 1, 5, 1, 1, 1, 1, 1, 1, 2, 0, 0, 0, 0, 0, 0], [5, 4, 3, 6, 2, 7, 1, 0, 8, 9, 11, 10, 12]),
        ([0, 1, 5, 1, 1, 1, 1, 1, 1, 2, 0, 0, 0, 0, 0, 0], [0x39, 0x5A, 0x38, 0x27, 0x16, 5, 4, 3, 2, 1, 0, 11, 12, 12]),
        ([0, 1, 4, 2, 3, 1, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0], [5, 4, 6, 3, 7, 2, 8, 1, 9, 0, 10, 11, 12]),
        ([0, 1, 4, 3, 1, 1, 1, 1, 1, 2, 0, 0, 0, 0, 0, 0], [5, 6, 4, 7, 8, 3, 9, 2, 1, 0, 10, 11, 12, 13, 14]),
        ([0, 1, 5, 1, 1, 1, 1, 1, 1, 1, 2, 0, 0, 0, 0, 0], [8, 0x5C, 0x4B, 0x3A, 0x29, 7, 6, 5, 4, 3, 2, 1, 0, 13, 14]),
        ([0, 1, 4, 2, 2, 3, 1, 2, 0, 0, 0, 0, 0, 0, 0, 0], [7, 6, 8, 5, 9, 4, 10, 3, 11, 12, 2, 0, 1, 13, 14]),
    ];

    /// <summary>Nikon NEF Huffman with its curve and split row (NEF spec, sections 3 to 6).</summary>
    private static void Nikon(ReadOnlySpan<byte> data, RawPixels pixels, NikonMeta meta, int bits)
    {
        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0x10 && data[2] == 0xFF && data[3] == 0x50)
        {
            throw new NotSupportedException("Egami does not read Nikon's High Efficiency (JPEG XS) raw data.");
        }

        byte[] m = meta.Bytes;
        int U(int at) => at + 2 <= m.Length ? (meta.BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(at)) : BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(at))) : 0;
        int ver0 = m.Length > 0 ? m[0] : 0, ver1 = m.Length > 1 ? m[1] : 0;
        int at = 2 + (ver0 == 0x49 || ver1 == 0x58 ? 2110 : 0);

        // The initial vertical predictors in LibRaw's order (open point of the spec): (even, even), (even, odd), (odd, even), (odd, odd).
        int[,] vpred = { { U(at), U(at + 2) }, { U(at + 4), U(at + 6) } };
        int csize = U(at + 8);
        int max = (1 << bits) & 0x7FFF;
        ushort[] curve = new ushort[0x10000];
        for (int i = 0; i < curve.Length; i++)
        {
            curve[i] = (ushort)Math.Min(i, 65535);
        }

        bool stepped = false;
        if (ver0 == 0x44 && (ver1 is 0x20 or 0x40) && csize > 1)
        {
            int step = max / (csize - 1);
            if (ver1 == 0x20 || step > 3)
            {
                stepped = true;
                if (ver1 == 0x40)
                {
                    step /= 4;
                    max /= 4;
                }

                for (int i = 0; i < csize && i * step < curve.Length; i++)
                {
                    curve[i * step] = (ushort)U(at + 10 + (2 * i));
                }

                for (int i = 0; i < max; i++)
                {
                    int a = i - (i % step);
                    if (a + step < curve.Length)
                    {
                        curve[i] = (ushort)(((curve[a] * (step - (i % step))) + (curve[a + step] * (i % step))) / step);
                    }
                }
            }
        }

        if (!stepped && ver0 != 0x46 && csize is > 0 and <= 0x4001)
        {
            for (int i = 0; i < csize; i++)
            {
                curve[i] = (ushort)U(at + 10 + (2 * i));
            }
        }

        int split = ver0 == 0x44 && (ver1 is 0x20 or 0x40) ? U(562) : 0;
        int tree = (ver0 == 0x46 ? 2 : 0) + (bits == 14 ? 3 : 0);
        PeekTable codes = PeekTable.Canonical(NikonTables[tree].Counts, NikonTables[tree].Symbols);
        MsbBits reader = new(data);
        int width = pixels.Width, height = pixels.Height;
        Span<int> hpred = stackalloc int[2];
        for (int y = 0; y < height; y++)
        {
            if (split > 0 && y == split && split < height)
            {
                codes = PeekTable.Canonical(NikonTables[tree + 1].Counts, NikonTables[tree + 1].Symbols);
            }

            Span<ushort> row = pixels.U16Row(y);
            for (int x = 0; x < width; x++)
            {
                int s = codes.Decode(ref reader);
                if (s < 0 || reader.Overrun)
                {
                    throw new InvalidDataException($"The Nikon data is damaged at row {y}.");
                }

                int length = s & 15, shl = s >> 4;
                int r = (int)reader.Read(length - shl);
                int d = (((2 * r) + 1) << shl) >> 1;
                if (length > 0 && (d & (1 << (length - 1))) == 0)
                {
                    d -= (1 << length) - (shl == 0 ? 1 : 0);
                }

                if (length == 0)
                {
                    d = 0;
                }

                if (x < 2)
                {
                    vpred[y & 1, x] += d;
                    hpred[x] = vpred[y & 1, x];
                }
                else
                {
                    hpred[x & 1] += d;
                }

                row[x] = curve[Math.Clamp(hpred[x & 1], 0, 0x3FFF)];
            }
        }
    }

    /// <summary>Sony ARW2: per row, 16-byte blocks of 16 same-parity pixels (Sony spec, sections 2 and 3).</summary>
    private static void SonyArw2(byte[] file, int offset, int count, RawPixels pixels, ushort[] curve, ParallelOptions parallel)
    {
        int width = pixels.Width;
        RawParallel.For(0, pixels.Height, parallel, y =>
        {
            Span<ushort> row = pixels.U16Row(y);
            Span<int> p = stackalloc int[16];
            int rowStart = offset + (y * width);
            if (rowStart + width > offset + count)
            {
                return;
            }

            for (int c = 0; c + 32 <= width; c += 32)
            {
                for (int half = 0; half < 2; half++)
                {
                    ReadOnlySpan<byte> block = file.AsSpan(rowStart + c + (half * 16), 16);
                    UInt128 bits = BinaryPrimitives.ReadUInt128LittleEndian(block);
                    int max = (int)(bits & 0x7FF), min = (int)((bits >> 11) & 0x7FF);
                    int imax = (int)((bits >> 22) & 0xF), imin = (int)((bits >> 26) & 0xF);
                    int sh = 0;
                    while (sh < 4 && (0x80 << sh) <= max - min)
                    {
                        sh++;
                    }

                    int field = 30;
                    for (int i = 0; i < 16; i++)
                    {
                        if (i == imax)
                        {
                            p[i] = max;
                        }
                        else if (i == imin)
                        {
                            p[i] = min;
                        }
                        else
                        {
                            p[i] = Math.Min(0x7FF, min + ((int)((bits >> field) & 0x7F) << sh));
                            field += 7;
                        }

                        row[c + half + (2 * i)] = curve[2 * p[i]];
                    }
                }
            }
        });
    }

    /// <summary>Sony ARW1: one bit stream, columns right to left, even rows then odd, one running predictor (Sony spec, section 4).</summary>
    private static void SonyArw1(ReadOnlySpan<byte> data, RawPixels pixels)
    {
        MsbBits reader = new(data);
        int width = pixels.Width, height = pixels.Height, encoded = height + 8;
        int pred = 0;
        for (int x = width - 1; x >= 0; x--)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                for (int y = pass; y < encoded; y += 2)
                {
                    int length;
                    uint b = reader.Read(2);
                    if (b == 3)
                    {
                        length = 1;
                    }
                    else if (b == 2)
                    {
                        length = 2;
                    }
                    else if (b == 1)
                    {
                        length = reader.Read(1) == 1 ? 0 : 3;
                    }
                    else
                    {
                        length = 4;
                        while (length < 17 && reader.Read(1) == 0)
                        {
                            length++;
                        }
                    }

                    pred += Extend(reader.Read(length), length);
                    if (reader.Overrun || (uint)pred >= 4096)
                    {
                        throw new InvalidDataException("The ARW1 data is damaged.");
                    }

                    if (y < height)
                    {
                        pixels.U16Row(y)[x] = (ushort)pred;
                    }
                }
            }
        }
    }

    /// <summary>Sony's lossless compressed tiles: a 4-component lossless JPEG per tile, each sample one 2 × 2 quad (Sony spec, section 5).</summary>
    private static void SonyLossless(RawSensor sensor, RawTiff tiff, RawPixels pixels, ParallelOptions parallel, Action<int, string> damaged)
    {
        int across = sensor.UnitsAcross, units = Math.Min(across * sensor.UnitsDown, Math.Min(sensor.Offsets.Length, sensor.Counts.Length));
        List<(int, string)> reports = [];
        RawParallel.For(0, units, parallel, u =>
        {
            int x0 = u % across * sensor.UnitWidth, y0 = u / across * sensor.UnitHeight;
            long at = tiff.Origin + sensor.Offsets[u];
            long n = Math.Min(sensor.Counts[u], tiff.Length - at);
            try
            {
                LosslessJpeg jpeg = LosslessJpeg.OpenReused(tiff.Data, (int)at, (int)n);
                ushort[] buffer = ArrayPool<ushort>.Shared.Rent((int)jpeg.SampleCount);
                try
                {
                    Span<ushort> s = buffer.AsSpan(0, (int)jpeg.SampleCount);
                    s.Clear();
                    jpeg.Decode(s);
                    int fw = jpeg.Width, comps = jpeg.Components;
                    for (int jr = 0; jr < jpeg.Height; jr++)
                    {
                        for (int jc = 0; jc < fw; jc++)
                        {
                            for (int c = 0; c < Math.Min(comps, 4); c++)
                            {
                                int y = y0 + (2 * jr) + (c >> 1), x = x0 + (2 * jc) + (c & 1);
                                if (y < pixels.Height && x < pixels.Width)
                                {
                                    pixels.U16Row(y)[x] = s[(((jr * fw) + jc) * comps) + c];
                                }
                            }
                        }
                    }
                }
                finally
                {
                    ArrayPool<ushort>.Shared.Return(buffer);
                }
            }
            catch (InvalidDataException e)
            {
                lock (reports)
                {
                    reports.Add((u, $"Tile {u} is damaged ({e.Message})."));
                }
            }
        });
        foreach ((int u, string message) in reports.OrderBy(r => r.Item1))
        {
            damaged(u, message);
        }
    }

    /// <summary>Olympus ORF, the general algorithm driven by the 14 parameters (ORF spec, sections 2 to 5).</summary>
    private static void Olympus(ReadOnlySpan<byte> data, RawPixels pixels, OlympusParams p)
    {
        ushort[] t = p.T;
        int t640 = t[0], t641 = t[1], t642 = t[2], t643 = t[3], t644 = t[4], t645 = t[5], t646 = t[6];
        int t647 = t[7], t648 = t[8], t649 = t[9], t650 = t[10], t651 = t[11], t652 = t[12], t653 = t[13];
        int header = t653 + 2;
        if (data.Length < header || data[t653 - 1] != 1)
        {
            throw new InvalidDataException("The ORF compressed stream header is not what its parameters say.");
        }

        MsbBits reader = new(data[header..]);
        int width = pixels.Width, height = pixels.Height;
        Span<int> mag = stackalloc int[2], run = stackalloc int[2], k = stackalloc int[2];
        for (int y = 0; y < height; y++)
        {
            mag.Clear();
            run.Clear();
            k.Clear();
            Span<ushort> row = pixels.U16Row(y);
            ReadOnlySpan<ushort> up = y >= 2 ? pixels.U16Row(y - 2) : default;
            for (int x = 0; x < width; x++)
            {
                int c = x & 1;
                run[c] = x >= 2 && mag[c] <= 1 << t645 ? run[c] + 1 : 0;
                int wbits;
                if (x < 2)
                {
                    wbits = t649;
                }
                else
                {
                    int hb = 32 - BitOperations.LeadingZeroCount((uint)(mag[c] & 0xFFFF));
                    (int a, int b) = run[c] >= t646 ? (t647, t648) : (t649, t650);
                    wbits = a + Math.Max(hb - b, -1) + 1;
                }

                wbits = Math.Min(wbits, t651);
                if (wbits >= 16)
                {
                    throw new InvalidDataException($"The ORF data is damaged at row {y}.");
                }

                int sign = (int)reader.Read(1);
                int x643 = (int)reader.Read(t643);
                int x640 = (int)reader.Read(t640);
                int high = reader.Zeros(t652);
                if (high >= t652)
                {
                    high = (int)reader.Read(15 - wbits);
                    reader.Skip(1);
                }

                int low = (int)reader.Read(wbits);
                int m = (high << wbits) | low;
                int r = sign == 0 ? m : ~m;
                int v = x >= 2 ? k[c] : 0;
                int g, kv;
                if (t643 == 0)
                {
                    g = r;
                    kv = 0;
                }
                else if (t643 == 1)
                {
                    g = x643 + (2 * (v + r));
                    kv = v + (g >> 1);
                }
                else
                {
                    g = x643 + (4 * (v + r));
                    kv = ((g >> 1) & ~1) + v + (g >> 2);
                }

                k[c] = t644 == 15 ? 0 : kv >> t644;
                mag[c] = m;
                int wv = x >= 2 ? row[x - 2] >> t640 : t642;
                int nv = y >= 2 ? up[x] >> t640 : t642;
                int nw = x >= 2 && y >= 2 ? up[x - 2] >> t640 : t642;
                int dw = wv - nw, dn = nv - nw;
                int pred;
                if (dw != 0 && dn != 0 && (dw < 0) != (dn < 0))
                {
                    pred = Math.Abs(dw) > 1 << t641 || Math.Abs(dn) > 1 << t641 ? wv + nv - nw : (wv + nv) >> 1;
                }
                else
                {
                    pred = Math.Abs(dw) > Math.Abs(dn) ? wv : nv;
                }

                row[x] = (ushort)(x640 + ((g + pred) << t640));
                if (reader.Overrun)
                {
                    throw new InvalidDataException($"The ORF data ends at row {y}.");
                }
            }
        }
    }

    private static void OlympusPacked(ReadOnlySpan<byte> data, RawPixels pixels, OlympusPacking packing, bool bigEndian)
    {
        int width = pixels.Width, height = pixels.Height;
        switch (packing)
        {
            case OlympusPacking.LeftAligned:
                for (int y = 0; y < height; y++)
                {
                    Span<ushort> row = pixels.U16Row(y);
                    for (int x = 0; x < width && ((y * width) + x + 1) * 2 <= data.Length; x++)
                    {
                        int at = ((y * width) + x) * 2;
                        row[x] = (ushort)((bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(data[at..]) : BinaryPrimitives.ReadUInt16LittleEndian(data[at..])) >> 4);
                    }
                }

                break;
            case OlympusPacking.WithControl:
                {
                    int rowBytes = (width * 3 / 2) + ((width + 2) / 10);
                    for (int y = 0; y < height; y++)
                    {
                        Span<ushort> row = pixels.U16Row(y);
                        int p = y * rowBytes;
                        for (int x = 0; x + 1 < width && p + 2 < data.Length; x += 2)
                        {
                            row[x] = (ushort)(data[p] | ((data[p + 1] & 0xF) << 8));
                            row[x + 1] = (ushort)((data[p + 1] >> 4) | (data[p + 2] << 4));
                            p += 3;
                            if ((x + 2) % 10 == 0)
                            {
                                p++;
                            }
                        }
                    }

                    break;
                }

            default:
                {
                    // Msb32 and interlaced: 12-bit MSB-first packing; interlaced files hold the even rows first and the odd
                    // rows from the next 2048-byte boundary.
                    int rowBytes = width * 3 / 2;
                    int oddStart = packing == OlympusPacking.Interlaced ? (((height + 1) / 2 * rowBytes) + 2047) & ~2047 : 0;
                    for (int y = 0; y < height; y++)
                    {
                        int start = packing == OlympusPacking.Interlaced ? (y % 2 == 0 ? y / 2 * rowBytes : oddStart + (y / 2 * rowBytes)) : y * rowBytes;
                        if (start + rowBytes > data.Length)
                        {
                            break;
                        }

                        RawDecoder.Ints(data[start..], 12, true, pixels.U16Row(y));
                    }

                    break;
                }
        }
    }

    /// <summary>Panasonic RawFormat 4 and 5: 0x4000-byte blocks with their halves swapped, 16-byte packets (RW2 spec, sections 2 to 4).</summary>
    private static void PanasonicBlocks(ReadOnlySpan<byte> data, RawPixels pixels, RawSensor sensor, ParallelOptions parallel)
    {
        PanasonicTags tags = (PanasonicTags)sensor.CodecData!;
        bool v5 = sensor.Codec == RawCodec.PanasonicV5;
        int perPacket = v5 ? (sensor.Bits == 14 ? 9 : 10) : 14;
        int split = tags.Modern ? 0x1FF8 : 0;
        long total = (long)pixels.Width * pixels.Height;
        int packetsPerBlock = 0x4000 / 16;
        long packets = (total + perPacket - 1) / perPacket;
        int blocks = (int)((packets + packetsPerBlock - 1) / packetsPerBlock);
        byte[] file = data.ToArray();
        RawParallel.For(0, blocks, parallel, b =>
        {
            byte[] block = ArrayPool<byte>.Shared.Rent(0x4000 + 1);
            try
            {
                Array.Clear(block, 0, 0x4001);
                int start = b * 0x4000;
                int length = Math.Min(0x4000, file.Length - start);
                if (length <= 0)
                {
                    return;
                }

                // Logical order: the bytes from the split on, then the bytes before it.
                int tail = Math.Max(0, Math.Min(length, 0x4000) - split);
                file.AsSpan(start + split, Math.Max(0, Math.Min(tail, length - split))).CopyTo(block);
                file.AsSpan(start, Math.Min(split, length)).CopyTo(block.AsSpan(0x4000 - split));
                Span<ushort> all = pixels.U16;
                for (int k = 0; k < packetsPerBlock; k++)
                {
                    long pixel = ((long)b * packetsPerBlock + k) * perPacket;
                    if (pixel >= total)
                    {
                        break;
                    }

                    UInt128 bits = BinaryPrimitives.ReadUInt128LittleEndian(block.AsSpan(k * 16, 16));
                    if (v5)
                    {
                        int w = sensor.Bits == 14 ? 14 : 12;
                        for (int i = 0; i < perPacket && pixel + i < total; i++)
                        {
                            all[(int)(pixel + i)] = (ushort)((bits >> (i * w)) & (UInt128)((1 << w) - 1));
                        }
                    }
                    else
                    {
                        PanasonicV4Packet(bits, all, pixel, total);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(block);
            }
        });
    }

    /// <summary>One RawFormat 4 packet: 14 pixels from a 128-bit value read from its top bit down (RW2 spec, section 3).</summary>
    private static void PanasonicV4Packet(UInt128 bits, Span<ushort> output, long pixel, long total)
    {
        int left = 128;
        int Read(int n)
        {
            left -= n;
            return (int)((bits >> left) & (UInt128)((1 << n) - 1));
        }

        Span<int> pred = stackalloc int[2], nz = stackalloc int[2];
        int sh = 0;
        for (int p = 0; p < 14; p++)
        {
            int c = p & 1;
            if (p % 3 == 2)
            {
                sh = 4 >> (3 - Read(2));
            }

            if (nz[c] != 0)
            {
                int j = Read(8);
                if (j != 0)
                {
                    pred[c] -= 0x80 << sh;
                    if (pred[c] < 0 || sh == 4)
                    {
                        pred[c] &= (1 << sh) - 1;
                    }

                    pred[c] += j << sh;
                }
            }
            else
            {
                nz[c] = Read(8);
                if (nz[c] != 0 || p > 11)
                {
                    pred[c] = (nz[c] << 4) | Read(4);
                }
            }

            if (pixel + p < total)
            {
                output[(int)(pixel + p)] = (ushort)Math.Clamp(pred[c], 0, 65535);
            }
        }
    }

    private static readonly int[] V6Bits12 = [12, 12, 2, 8, 8, 8, 2, 8, 8, 8, 2, 8, 8, 8, 2, 8, 8, 8];
    private static readonly int[] V6Bits14 = [14, 14, 2, 10, 10, 10, 2, 10, 10, 10, 2, 10, 10, 10];

    /// <summary>Panasonic RawFormat 6: per row, 16-byte packets of fields taken from the top bit down (RW2 spec, section 5).</summary>
    private static void PanasonicV6(ReadOnlySpan<byte> data, RawPixels pixels, int bits, ParallelOptions parallel)
    {
        bool b14 = bits == 14;
        int per = b14 ? 11 : 14;
        int[] sizes = b14 ? V6Bits14 : V6Bits12;
        int width = pixels.Width, packets = width / per;
        byte[] file = data.ToArray();
        int b0 = b14 ? 0x200 : 0x80, bcmp = b14 ? 0x2000 : 0x800, smax = b14 ? 0xFFFF : 0x3FFF, mask = b14 ? 0x3FFF : 0xFFF;
        RawParallel.For(0, pixels.Height, parallel, y =>
        {
            Span<ushort> row = pixels.U16Row(y);
            Span<int> first = stackalloc int[2], last = stackalloc int[2];
            for (int k = 0; k < packets; k++)
            {
                int at = ((y * packets) + k) * 16;
                if (at + 16 > file.Length)
                {
                    return;
                }

                UInt128 v = BinaryPrimitives.ReadUInt128LittleEndian(file.AsSpan(at, 16));
                int left = 128, field = 0;
                int Next()
                {
                    int n = sizes[field++];
                    left -= n;
                    return (int)((v >> left) & (UInt128)((1 << n) - 1));
                }

                first.Clear();
                last.Clear();
                int mul = 0, baseValue = 0;
                for (int pix = 0; pix < per; pix++)
                {
                    int c = pix & 1;
                    if (pix % 3 == 2)
                    {
                        int b = Next();
                        if (b == 3)
                        {
                            b = 4;
                        }

                        baseValue = b0 << b;
                        mul = 1 << b;
                    }

                    int e = Next();
                    if (first[c] != 0)
                    {
                        e *= mul;
                        if (baseValue < bcmp && last[c] > baseValue)
                        {
                            e += last[c] - baseValue;
                        }

                        last[c] = e;
                    }
                    else
                    {
                        first[c] = e;
                        if (e != 0)
                        {
                            last[c] = e;
                        }
                        else
                        {
                            e = last[c];
                        }
                    }

                    int outValue = e < 15 ? 0 : e - 15 <= smax ? e - 15 : mask;
                    row[(k * per) + pix] = (ushort)outValue;
                }
            }
        });
    }

    /// <summary>Panasonic RawFormat 7: per row, 16-byte packets read from bit 0 up, 9 (14-bit) or 10 (12-bit) pixels (RW2 spec, section 6).</summary>
    private static void PanasonicV7(ReadOnlySpan<byte> data, RawPixels pixels, int bits, ParallelOptions parallel)
    {
        int w = bits == 12 ? 12 : 14, per = bits == 12 ? 10 : 9;
        int width = pixels.Width, packets = (width + per - 1) / per;
        byte[] file = data.ToArray();
        RawParallel.For(0, pixels.Height, parallel, y =>
        {
            Span<ushort> row = pixels.U16Row(y);
            for (int k = 0; k < packets; k++)
            {
                int at = ((y * packets) + k) * 16;
                if (at + 16 > file.Length)
                {
                    return;
                }

                UInt128 v = BinaryPrimitives.ReadUInt128LittleEndian(file.AsSpan(at, 16));
                for (int i = 0; i < per && (k * per) + i < width; i++)
                {
                    row[(k * per) + i] = (ushort)((v >> (i * w)) & (UInt128)((1 << w) - 1));
                }
            }
        });
    }

    /// <summary>Panasonic RawFormat 8: Huffman strips, 2 × 2 quads in row pairs (RW2 spec, section 7).</summary>
    private static void PanasonicV8(RawTiff tiff, RawPixels pixels, PanasonicTags tags, ParallelOptions parallel)
    {
        RawIfd ifd = tags.Ifd;
        // The vectors are byte blobs: a little-endian u16 count, then that many u16 or u32 values.
        long[]? Vector(int tag, int size)
        {
            ReadOnlySpan<byte> raw = ifd.Bytes(tag);
            if (raw.Length < 2)
            {
                return null;
            }

            int count = Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(raw), (raw.Length - 2) / size);
            long[] values = new long[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = size == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(raw[(2 + (4 * i))..]) : BinaryPrimitives.ReadUInt16LittleEndian(raw[(2 + (2 * i))..]);
            }

            return values;
        }

        ReadOnlySpan<byte> table = ifd.Bytes(0x0040);
        if (table.Length < 2)
        {
            throw new InvalidDataException("The RW2 has no Huffman table.");
        }

        int n = BinaryPrimitives.ReadUInt16LittleEndian(table);
        if (n is < 1 or > 17 || table.Length < 2 + (4 * n))
        {
            throw new InvalidDataException("The RW2 Huffman table is not valid.");
        }

        if (Vector(0x0041, 2) is long[] shifts && shifts.Any(s => s != 0))
        {
            throw new NotSupportedException("Egami reads RW2 RawFormat 8 only without per-symbol shifts.");
        }

        // First match in index order, from a 16-bit peek (the codes need not be a prefix code).
        ushort[] lookup = new ushort[1 << 16];
        for (int peek = 0; peek < lookup.Length; peek++)
        {
            for (int i = 0; i < n; i++)
            {
                int length = BinaryPrimitives.ReadUInt16LittleEndian(table[(2 + (4 * i))..]);
                int code = BinaryPrimitives.ReadUInt16LittleEndian(table[(4 + (4 * i))..]) & ((1 << length) - 1);
                if (length is >= 1 and <= 16 && peek >> (16 - length) == code)
                {
                    lookup[peek] = (ushort)((length << 8) | i | 0x8000);
                    break;
                }
            }
        }

        long[] offsets = Vector(0x0044, 4) ?? [], positions = Vector(0x0045, 4) ?? [], sizes = Vector(0x0046, 4) ?? [];
        long[] widths = Vector(0x0047, 2) ?? [], heights = Vector(0x0048, 2) ?? [];
        int strips = (int)Math.Min(new[] { offsets.Length, positions.Length, sizes.Length, widths.Length, heights.Length }.Min(), Math.Max(1, ifd.Int(0x0042, 1) * ifd.Int(0x0043, 1)));
        int[] seed = [(int)ifd.Int(0x003C), (int)ifd.Int(0x003D), (int)ifd.Int(0x003E), (int)ifd.Int(0x003F)];
        RawParallel.For(0, strips, parallel, s =>
        {
            long at = offsets[s];
            int bytes = (int)Math.Min((sizes[s] + 7) / 8, tiff.Length - at);
            if (at <= 0 || bytes <= 0)
            {
                return;
            }

            byte[] reversed = ArrayPool<byte>.Shared.Rent(bytes);
            try
            {
                for (int i = 0; i < bytes; i++)
                {
                    reversed[i] = ReverseBits(tiff.Data[at + i]);
                }

                MsbBits reader = new(reversed.AsSpan(0, bytes));
                int x0 = (int)(positions[s] & 0xFFFF), y0 = (int)(positions[s] >> 16), sw = (int)widths[s], shh = (int)heights[s];

                // Quad positions: top-left (red), bottom-left, top-right, bottom-right (blue); greens as LibRaw maps them.
                Span<int> pred = [seed[0], seed[2], seed[1], seed[3]];
                Span<int> rowStart = stackalloc int[4];
                for (int pair = 0; pair + 1 < shh; pair += 2)
                {
                    for (int qx = 0; qx + 1 < sw; qx += 2)
                    {
                        for (int q = 0; q < 4; q++)
                        {
                            int e = lookup[reader.Peek(16)];
                            if (e == 0)
                            {
                                throw new InvalidDataException("The RW2 Huffman data has a code the table does not have.");
                            }

                            reader.Skip((e >> 8) & 0x1F);
                            int k = e & 0xFF;
                            int d = Extend(reader.Read(k), k);
                            pred[q] = Math.Clamp(pred[q] + d, 0, 65535);
                            int y = y0 + pair + (q & 1), x = x0 + qx + (q >> 1);
                            if (y < pixels.Height && x < pixels.Width)
                            {
                                pixels.U16Row(y)[x] = (ushort)pred[q];
                            }

                            if (qx == 0)
                            {
                                rowStart[q] = pred[q];
                            }
                        }
                    }

                    rowStart.CopyTo(pred);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(reversed);
            }
        });
    }

    private static byte ReverseBits(byte b) => (byte)((((b * 0x0802u) & 0x22110u) | ((b * 0x8020u) & 0x88440u)) * 0x10101u >> 16);

    /// <summary>Canon CR2: one lossless JPEG frame whose values fill vertical slices of the image in turn (CR2 spec, sections 3 and 4).</summary>
    private static void Canon(byte[] file, int offset, int count, RawPixels pixels, CanonSlices slices, Action<int, string> damaged)
    {
        LosslessJpeg jpeg = LosslessJpeg.Open(file, offset, count);
        if (jpeg.H0 != 1 || jpeg.V0 != 1)
        {
            throw new NotSupportedException("Egami does not read Canon sRAW or mRAW files yet.");
        }

        int width = pixels.Width, height = pixels.Height;
        if (jpeg.SampleCount < (long)width * height)
        {
            throw new InvalidDataException("The CR2 lossless JPEG frame is smaller than the image.");
        }

        ushort[] flat = ArrayPool<ushort>.Shared.Rent((int)jpeg.SampleCount);
        try
        {
            Span<ushort> values = flat.AsSpan(0, (int)jpeg.SampleCount);
            values.Clear();
            if (!jpeg.Decode(values))
            {
                damaged(0, "The CR2 lossless JPEG ends early; what is missing reads as black.");
            }

            long[] s = slices.Values;
            int sliceCount = s.Length == 3 ? (int)s[0] : 0, sliceWidth = s.Length == 3 ? (int)s[1] : 0, lastWidth = s.Length == 3 ? (int)s[2] : 0;
            if (sliceWidth <= 0)
            {
                for (int y = 0; y < height; y++)
                {
                    values.Slice(y * width, width).CopyTo(pixels.U16Row(y));
                }

                return;
            }

            if ((sliceCount * sliceWidth) + lastWidth != width)
            {
                throw new InvalidDataException("The CR2 slices do not add up to the image width.");
            }

            int j = 0;
            for (int i = 0; i <= sliceCount; i++)
            {
                int w = i < sliceCount ? sliceWidth : lastWidth;
                int x0 = i * sliceWidth;
                for (int y = 0; y < height; y++)
                {
                    values.Slice(j, w).CopyTo(pixels.U16Row(y).Slice(x0, w));
                    j += w;
                }
            }
        }
        finally
        {
            ArrayPool<ushort>.Shared.Return(flat);
        }
    }
}
