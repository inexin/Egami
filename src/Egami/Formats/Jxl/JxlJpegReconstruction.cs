using System.Buffers.Binary;
using System.IO.Compression;

namespace Egami.Formats.Jxl;

/// <summary>
/// JPEG bitstream reconstruction (ISO/IEC 18181-2 Annex B, design question 8): the jbrd box's header (markers, quantization and
/// Huffman tables, scans, padding bits) and its Brotli-compressed data (APP, COM and unknown segments and the bytes after EOI),
/// written back around the VarDCT coefficients as the original JPEG, byte for byte. Translated from jxl-oxide's jxl-jbr crate
/// (see THIRD-PARTY-NOTICES).
/// </summary>
internal sealed class JxlJbrd
{
    private static readonly byte[] IccHeader = "ICC_PROFILE\0"u8.ToArray();
    private static readonly byte[] ExifHeader = "Exif\0\0"u8.ToArray();
    private static readonly byte[] XmpHeader = "http://ns.adobe.com/xap/1.0/\0"u8.ToArray();

    internal bool IsGray { get; private init; }

    internal List<byte> Markers { get; } = [];

    internal List<(uint Type, int Length)> AppMarkers { get; } = [];

    internal List<int> ComLengths { get; } = [];

    internal List<(int Precision, int Index, bool IsLast)> QuantTables { get; } = [];

    internal List<(byte Id, int QuantIndex)> Components { get; } = [];

    internal List<JbrdHuffman> HuffmanCodes { get; } = [];

    internal List<JbrdScan> Scans { get; } = [];

    internal int RestartInterval { get; private set; }

    internal List<int> IntermarkerLengths { get; } = [];

    internal int TailLength { get; private set; }

    internal byte[]? Padding { get; private set; }

    internal int PaddingBits { get; private set; }

    /// <summary>Gets the decompressed data section: APP data, COM data, unknown-marker data, then the tail.</summary>
    internal byte[] Data { get; private set; } = [];

    internal int AppDataLength => AppMarkers.Where(m => m.Type == 0).Sum(m => m.Length);

    internal int IccLength => AppMarkers.Where(m => m.Type == 1).Sum(m => m.Length - 5 - IccHeader.Length);

    internal int ExifLength => AppMarkers.FirstOrDefault(m => m.Type == 2) is { Length: > 0 } m ? m.Length - 3 - ExifHeader.Length : 0;

    internal int XmpLength => AppMarkers.FirstOrDefault(m => m.Type == 3) is { Length: > 0 } m ? m.Length - 3 - XmpHeader.Length : 0;

    /// <summary>Parses a jbrd box and decompresses its data section.</summary>
    internal static JxlJbrd Parse(byte[] box, SafetyLimits limits)
    {
        JxlBitReader r = new(box, 0, box.Length);
        JxlJbrd j = new() { IsGray = r.ReadBool() };
        int apps = 0, coms = 0, scans = 0, inter = 0;
        bool dri = false;
        while (j.Markers.Count == 0 || j.Markers[^1] != 0xD9)
        {
            if (j.Markers.Count > 1 << 16)
            {
                throw r.Corrupt("The jbrd box lists too many markers.");
            }

            byte m = (byte)(r.ReadBits(6) + 0xC0);
            switch (m)
            {
                case >= 0xE0 and <= 0xEF:
                    apps++;
                    break;
                case 0xFE:
                    coms++;
                    break;
                case 0xDA:
                    scans++;
                    break;
                case 0xFF:
                    inter++;
                    break;
                case 0xDD:
                    dri = true;
                    break;
            }

            j.Markers.Add(m);
        }

        for (int i = 0; i < apps; i++)
        {
            uint type = r.ReadU32(new JxlU32(0, 0, 1, 0, 2, 1, 4, 2));
            j.AppMarkers.Add((type, (int)r.ReadBits(16) + 1));
        }

        for (int i = 0; i < coms; i++)
        {
            j.ComLengths.Add((int)r.ReadBits(16) + 1);
        }

        int quantTables = (int)r.ReadBits(2) + 1;
        for (int i = 0; i < quantTables; i++)
        {
            j.QuantTables.Add(((int)r.ReadBits(1), (int)r.ReadBits(2), r.ReadBool()));
        }

        byte[] ids = r.ReadBits(2) switch
        {
            0 => [1],
            1 => [1, 2, 3],
            2 => [(byte)'R', (byte)'G', (byte)'B'],
            _ => [.. Enumerable.Range(0, (int)r.ReadBits(2) + 1).Select(_ => (byte)r.ReadBits(8))],
        };
        foreach (byte id in ids)
        {
            j.Components.Add((id, (int)r.ReadBits(2)));
        }

        uint huffman = r.ReadU32(new JxlU32(4, 0, 2, 3, 10, 4, 26, 6));
        for (int i = 0; i < huffman; i++)
        {
            j.HuffmanCodes.Add(JbrdHuffman.Read(r));
        }

        for (int i = 0; i < scans; i++)
        {
            j.Scans.Add(JbrdScan.Read(r));
        }

        j.RestartInterval = dri ? (int)r.ReadBits(16) : 0;
        foreach (JbrdScan scan in j.Scans)
        {
            scan.ReadMore(r);
        }

        for (int i = 0; i < inter; i++)
        {
            j.IntermarkerLengths.Add((int)r.ReadBits(16));
        }

        j.TailLength = (int)r.ReadU32(new JxlU32(0, 0, 1, 8, 257, 16, 65793, 22));
        if (r.ReadBool())
        {
            int bits = (int)r.ReadBits(24);
            byte[] padding = new byte[(bits + 7) / 8];
            for (int i = 0; i < bits / 8; i++)
            {
                padding[i] = (byte)r.ReadBits(8);
            }

            if (bits % 8 != 0)
            {
                padding[^1] = (byte)r.ReadBits(bits % 8);
            }

            (j.Padding, j.PaddingBits) = (padding, bits);
        }

        r.ZeroPadToByte();
        int start = (int)(r.BitPosition / 8);
        long expected = (long)j.AppDataLength + j.ComLengths.Sum() + j.IntermarkerLengths.Sum() + j.TailLength;
        if (expected > limits.MaxMetadataBytes)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxMetadataBytes), limits.MaxMetadataBytes, expected);
        }

        byte[] data = new byte[expected];
        if (expected > 0)
        {
            using MemoryStream compressed = new(box, start, box.Length - start, writable: false);
            using BrotliStream brotli = new(compressed, CompressionMode.Decompress);
            int read = 0;
            while (read < data.Length)
            {
                int n = brotli.Read(data, read, data.Length - read);
                if (n == 0)
                {
                    throw r.Corrupt("The jbrd box's data section is shorter than its header says.");
                }

                read += n;
            }

            if (brotli.ReadByte() >= 0)
            {
                throw r.Corrupt("The jbrd box's data section is longer than its header says.");
            }
        }

        j.Data = data;
        return j;
    }

    /// <summary>Writes the JPEG: the markers in order, each segment rebuilt from the header, the data section, the metadata boxes and the coefficients.</summary>
    internal void Write(Stream output, JxlFrameHeader frame, JxlVarDct varDct, byte[]? icc, byte[]? exif, byte[]? xmp)
    {
        icc ??= [];
        exif ??= [];
        xmp ??= [];
        if ((IccLength > 0 && IccLength != icc.Length) || (ExifLength > 0 && ExifLength != exif.Length) || (XmpLength > 0 && XmpLength != xmp.Length))
        {
            throw new CorruptDataException(JxlFormat.Info.Name, -1, "The jbrd box's ICC, Exif or XMP sizes do not match the file's metadata.");
        }

        JpegCoefficients coefficients = new(frame, varDct, IsGray);
        int appData = 0;
        int comData = AppDataLength;
        int interData = comData + ComLengths.Sum();
        int tailData = interData + IntermarkerLengths.Sum();
        int app = 0, com = 0, inter = 0, huffman = 0, quant = 0, scan = 0, iccMarker = 0, iccOffset = 0;
        int iccMarkers = AppMarkers.Count(m => m.Type == 1);
        bool progressive = false;
        int restart = 0;
        HuffmanTable?[] dc = new HuffmanTable?[4];
        HuffmanTable?[] ac = new HuffmanTable?[4];
        ushort[]? lastQuant = null;
        JbrdPadding padding = new(Padding, PaddingBits);
        output.Write([0xFF, 0xD8]);
        foreach (byte marker in Markers)
        {
            switch (marker)
            {
                case 0xC0 or 0xC1 or 0xC2 or 0xC9 or 0xCA:
                    {
                        progressive = marker is 0xC2 or 0xCA;
                        int length = 8 + (Components.Count * 3);
                        output.Write([0xFF, marker, (byte)(length >> 8), (byte)length, 8, (byte)(frame.Height >> 8), (byte)frame.Height, (byte)(frame.Width >> 8), (byte)frame.Width, (byte)Components.Count]);
                        int[] upsampling = Upsampling(frame);
                        for (int i = 0; i < Components.Count; i++)
                        {
                            int sampling = (i < 3 ? upsampling[i] : 0) switch { 1 => 0x22, 2 => 0x21, 3 => 0x12, _ => 0x11 };
                            output.Write([Components[i].Id, (byte)sampling, (byte)Components[i].QuantIndex]);
                        }

                        break;
                    }

                case 0xC4:
                    {
                        int count = HuffmanCodes.FindIndex(huffman, h => h.IsLast) - huffman + 1;
                        if (count <= 0)
                        {
                            throw new CorruptDataException(JxlFormat.Info.Name, -1, "A DHT marker of the jbrd box has no last table.");
                        }

                        List<JbrdHuffman> codes = HuffmanCodes.GetRange(huffman, count);
                        huffman += count;
                        int length = 2 + codes.Sum(h => 16 + h.Values.Length);
                        output.Write([0xFF, 0xC4, (byte)(length >> 8), (byte)length]);
                        foreach (JbrdHuffman h in codes)
                        {
                            byte[] head = new byte[17];
                            head[0] = (byte)(h.Id | (h.IsAc ? 0x10 : 0));
                            h.Counts.AsSpan(1).CopyTo(head.AsSpan(1));
                            for (int k = 16; k >= 1; k--)
                            {
                                if (head[k] != 0)
                                {
                                    head[k]--;
                                    break;
                                }
                            }

                            output.Write(head);
                            output.Write(h.Values.AsSpan(0, h.Values.Length - 1));
                            (h.IsAc ? ac : dc)[h.Id] = HuffmanTable.Build(h);
                        }

                        break;
                    }

                case >= 0xD0 and <= 0xD7:
                    output.Write([0xFF, marker]);
                    break;

                case 0xD9:
                    output.Write([0xFF, 0xD9]);
                    output.Write(Data.AsSpan(tailData, TailLength));
                    break;

                case 0xDA:
                    {
                        JbrdScan s = Scans[scan++];
                        if (s.Components.Count == 0)
                        {
                            throw new CorruptDataException(JxlFormat.Info.Name, -1, "An SOS marker of the jbrd box has no component.");
                        }

                        int length = 6 + (2 * s.Components.Count);
                        output.Write([0xFF, 0xDA, (byte)(length >> 8), (byte)length, (byte)s.Components.Count]);
                        foreach ((int comp, int acTable, int dcTable) in s.Components)
                        {
                            output.Write([Components[comp].Id, (byte)((dcTable << 4) | acTable)]);
                        }

                        output.Write([(byte)s.Ss, (byte)s.Se, (byte)((s.Ah << 4) | s.Al)]);
                        int type = !progressive ? 0 : s.Ah == 0 ? 1 : 2;
                        if (type == 0 && (s.Ss != 0 || s.Se != 63 || s.Al != 0 || s.Ah != 0))
                        {
                            throw new CorruptDataException(JxlFormat.Info.Name, -1, "A sequential JPEG scan of the jbrd box has progressive parameters.");
                        }

                        JpegScanWriter.Write(output, s, type, frame, coefficients, dc, ac, restart, padding);
                        break;
                    }

                case 0xDB:
                    {
                        int count = QuantTables.FindIndex(quant, q => q.IsLast) - quant + 1;
                        if (count <= 0)
                        {
                            throw new CorruptDataException(JxlFormat.Info.Name, -1, "A DQT marker of the jbrd box has no last table.");
                        }

                        List<(int Precision, int Index, bool IsLast)> tables = QuantTables.GetRange(quant, count);
                        quant += count;
                        int length = 2 + (65 * count) + (64 * tables.Count(t => t.Precision != 0));
                        output.Write([0xFF, 0xDB, (byte)(length >> 8), (byte)length]);
                        foreach ((int precision, int index, _) in tables)
                        {
                            int channel = Components.FindIndex(c => c.QuantIndex == index);
                            if (channel >= 0 && frame.YCbCr && channel <= 1)
                            {
                                channel ^= 1;
                            }

                            if (channel >= 0 && varDct.HfGlobal?.Matrices.JpegTables is int[][] jpeg && channel < jpeg.Length)
                            {
                                // The quantization tables are kept transposed for DCT8; DQT lists them in zigzag order.
                                lastQuant = new ushort[64];
                                int[] natural = JxlHfPass.Natural(0);
                                for (int i = 0; i < 64; i++)
                                {
                                    int pos = natural[i];
                                    lastQuant[i] = (ushort)jpeg[channel][(pos / 8) + (8 * (pos % 8))];
                                }
                            }

                            if (lastQuant is null)
                            {
                                throw new CorruptDataException(JxlFormat.Info.Name, -1, "A DQT table of the jbrd box has no quantization values in the codestream.");
                            }

                            if (precision == 0)
                            {
                                byte[] table = new byte[65];
                                table[0] = (byte)index;
                                for (int i = 0; i < 64; i++)
                                {
                                    table[i + 1] = (byte)lastQuant[i];
                                }

                                output.Write(table);
                            }
                            else
                            {
                                byte[] table = new byte[129];
                                table[0] = (byte)(index | (precision << 4));
                                for (int i = 0; i < 64; i++)
                                {
                                    BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(1 + (2 * i)), lastQuant[i]);
                                }

                                output.Write(table);
                            }
                        }

                        break;
                    }

                case 0xDD:
                    output.Write([0xFF, 0xDD, 0, 4, (byte)(RestartInterval >> 8), (byte)RestartInterval]);
                    restart = RestartInterval;
                    break;

                case >= 0xE0 and <= 0xEF:
                    {
                        (uint type, int length) = AppMarkers[app++];
                        byte[] size = [(byte)((length - 1) >> 8), (byte)(length - 1)];
                        switch (type)
                        {
                            case 0:
                                output.WriteByte(0xFF);
                                output.Write(Data.AsSpan(appData, length));
                                appData += length;
                                break;
                            case 1:
                                {
                                    int part = length - 5 - IccHeader.Length;
                                    output.Write([0xFF, 0xE2, size[0], size[1]]);
                                    output.Write(IccHeader);
                                    output.Write([(byte)(iccMarker + 1), (byte)iccMarkers]);
                                    output.Write(icc.AsSpan(iccOffset, part));
                                    iccMarker++;
                                    iccOffset += part;
                                    break;
                                }

                            case 2:
                                output.Write([0xFF, 0xE1, size[0], size[1]]);
                                output.Write(ExifHeader);
                                output.Write(exif);
                                break;
                            case 3:
                                output.Write([0xFF, 0xE1, size[0], size[1]]);
                                output.Write(XmpHeader);
                                output.Write(xmp);
                                break;
                            default:
                                throw new CorruptDataException(JxlFormat.Info.Name, -1, $"The jbrd box has an APP marker of unknown type {type}.");
                        }

                        break;
                    }

                case 0xFE:
                    {
                        // As for APP data, the stored COM segment starts with its marker byte.
                        int length = ComLengths[com++];
                        output.WriteByte(0xFF);
                        output.Write(Data.AsSpan(comData, length));
                        comData += length;
                        break;
                    }

                case 0xFF:
                    {
                        int length = IntermarkerLengths[inter++];
                        output.Write(Data.AsSpan(interData, length));
                        interData += length;
                        break;
                    }

                default:
                    throw new CorruptDataException(JxlFormat.Info.Name, -1, $"The jbrd box lists the unknown marker {marker:X2}.");
            }
        }
    }

    /// <summary>Gets the JPEG upsampling mode of Y, Cb and Cr (the frame lists Cb, Y, Cr).</summary>
    internal static int[] Upsampling(JxlFrameHeader frame)
    {
        int[] up = frame.YCbCr ? frame.JpegUpsampling : [0, 0, 0];
        return [up[1], up[0], up[2]];
    }
}

/// <summary>A Huffman table of the jbrd box: counts per code length (with one extra symbol), and the symbols.</summary>
internal sealed record JbrdHuffman(bool IsAc, int Id, bool IsLast, byte[] Counts, byte[] Values)
{
    internal static JbrdHuffman Read(JxlBitReader r)
    {
        bool isAc = r.ReadBool();
        int id = (int)r.ReadBits(2);
        bool isLast = r.ReadBool();
        byte[] counts = new byte[17];
        int total = 0;
        for (int i = 0; i < 17; i++)
        {
            uint count = r.ReadU32(new JxlU32(0, 0, 1, 0, 2, 3, 0, 8));
            counts[i] = (byte)count;
            total += (int)count;
        }

        if (total > 257 || total == 0)
        {
            throw r.Corrupt($"A jbrd Huffman table has {total} symbols.");
        }

        byte[] values = new byte[total];
        for (int i = 0; i < total; i++)
        {
            values[i] = (byte)r.ReadU32(new JxlU32(0, 2, 4, 2, 8, 4, 1, 8));
        }

        return new JbrdHuffman(isAc, id, isLast, counts, values);
    }
}

/// <summary>A scan of the jbrd box: spectral range, successive approximation, components, and its reset points and extra zero runs.</summary>
internal sealed class JbrdScan
{
    internal int Ss { get; private init; }

    internal int Se { get; private init; }

    internal int Al { get; private init; }

    internal int Ah { get; private init; }

    internal List<(int Component, int AcTable, int DcTable)> Components { get; } = [];

    internal HashSet<int> ResetPoints { get; } = [];

    internal Dictionary<int, int> ExtraZeroRuns { get; } = [];

    internal static JbrdScan Read(JxlBitReader r)
    {
        int count = (int)r.ReadBits(2) + 1;
        JbrdScan s = new() { Ss = (int)r.ReadBits(6), Se = (int)r.ReadBits(6), Al = (int)r.ReadBits(4), Ah = (int)r.ReadBits(4) };
        for (int i = 0; i < count; i++)
        {
            s.Components.Add(((int)r.ReadBits(2), (int)r.ReadBits(2), (int)r.ReadBits(2)));
        }

        // The last pass needed; not used for writing.
        r.ReadU32(new JxlU32(0, 0, 1, 0, 2, 0, 3, 3));
        if (s.Se > 63 || s.Ss > s.Se)
        {
            throw r.Corrupt($"A jbrd scan covers coefficients {s.Ss} to {s.Se}.");
        }

        return s;
    }

    internal void ReadMore(JxlBitReader r)
    {
        uint resets = r.ReadU32(new JxlU32(0, 0, 1, 2, 4, 4, 20, 16));
        long last = -1;
        for (uint i = 0; i < resets; i++)
        {
            uint diff = r.ReadU32(new JxlU32(0, 0, 1, 3, 9, 5, 41, 28));
            last = last < 0 ? diff : last + diff + 1;
            if (last > 3 << 26)
            {
                throw r.Corrupt("A jbrd reset point is too large.");
            }

            ResetPoints.Add((int)last);
        }

        uint runs = r.ReadU32(new JxlU32(0, 0, 1, 2, 4, 4, 20, 16));
        last = -1;
        for (uint i = 0; i < runs; i++)
        {
            uint count = r.ReadU32(new JxlU32(1, 0, 2, 2, 5, 4, 20, 8));
            uint length = r.ReadU32(new JxlU32(0, 0, 1, 3, 9, 5, 41, 28));
            last = last < 0 ? length : last + length + 1;
            if (last > 3 << 26)
            {
                throw r.Corrupt("A jbrd extra zero run is too large.");
            }

            ExtraZeroRuns[(int)last] = (int)count;
        }
    }
}

/// <summary>The padding bits the original JPEG put at byte ends, read a few at a time.</summary>
internal sealed class JbrdPadding(byte[]? bits, int count)
{
    private int _position;

    internal bool Has => bits is not null;

    internal uint Read(int n)
    {
        uint v = 0;
        for (int i = 0; i < n; i++)
        {
            if (_position >= count)
            {
                throw new CorruptDataException(JxlFormat.Info.Name, -1, "The jbrd box has fewer padding bits than the JPEG needs.");
            }

            // LSB first within each byte, as the bit reader read them.
            int bit = (bits![_position / 8] >> (_position % 8)) & 1;
            v |= (uint)bit << i;
            _position++;
        }

        return v;
    }
}

/// <summary>A JPEG Huffman table built for writing: each symbol's code length and code, left-aligned in 64 bits.</summary>
internal sealed class HuffmanTable
{
    private readonly byte[] _lengths = new byte[256];
    private readonly ulong[] _codes = new ulong[256];

    internal static readonly HuffmanTable Empty = new();

    internal static HuffmanTable Build(JbrdHuffman h)
    {
        List<byte> lengths = [];
        for (int len = 0; len < 17; len++)
        {
            for (int k = 0; k < h.Counts[len]; k++)
            {
                lengths.Add((byte)len);
            }
        }

        // The last symbol is the extra one jbrd adds; it gets no code.
        lengths.RemoveAt(lengths.Count - 1);
        HuffmanTable t = new();
        ulong next = 0;
        int previous = lengths.Count > 0 ? lengths[0] : 0;
        for (int i = 0; i < lengths.Count; i++)
        {
            int len = lengths[i];
            if (len != previous)
            {
                next <<= len - previous;
                previous = len;
            }

            byte value = h.Values[i];
            t._lengths[value] = (byte)len;
            t._codes[value] = len == 0 ? 0 : next << (64 - len);
            next++;
        }

        return t;
    }

    internal (int Length, ulong Code) Lookup(int symbol)
    {
        int length = _lengths[symbol];
        return length == 0
            ? throw new CorruptDataException(JxlFormat.Info.Name, -1, $"The JPEG's Huffman table has no code for symbol {symbol:X2}.")
            : (length, _codes[symbol]);
    }
}

/// <summary>The quantized JPEG coefficients, frame-wide per component (Y, Cb, Cr), with integer chroma from luma undone.</summary>
internal sealed class JpegCoefficients
{
    private const int CflBits = 11;
    private const int CflDefaultFactor = 84;

    internal JpegCoefficients(JxlFrameHeader frame, JxlVarDct v, bool gray)
    {
        if (v.HfGlobal?.Matrices.JpegTables is not int[][] jpeg || jpeg.Length < 3)
        {
            throw new UnsupportedFormatException("The JPEG XL frame has no JPEG quantization tables, so the JPEG cannot be rebuilt.");
        }

        if (!v.Subsampled && (v.Global.ColourFactor != CflDefaultFactor || v.Global.BaseCorrelationX != 0 || v.Global.BaseCorrelationB != 0))
        {
            throw new UnsupportedFormatException("The JPEG XL frame's chroma from luma cannot be undone exactly, so the JPEG cannot be rebuilt.");
        }

        // Component order Y, Cb, Cr against the frame's planes Cb, Y, Cr (for RGB JPEGs, G, R, B against R, G, B).
        Planes = [1, 0, 2];
        Coefficients = [(int[])v.Coefficients(1).Clone(), (int[])v.Coefficients(0).Clone(), (int[])v.Coefficients(2).Clone()];
        Lf = [v.LfQuant(1), v.LfQuant(0), v.LfQuant(2)];
        Strides = [v.PlaneBlocksX(1), v.PlaneBlocksX(0), v.PlaneBlocksX(2)];
        DcOffset = frame.YCbCr ? [0, 0, 0] : [1024 / jpeg[1][0], 1024 / jpeg[0][0], 1024 / jpeg[2][0]];
        if (!gray && !v.Subsampled)
        {
            IntegerCfl(v, jpeg);
        }
    }

    /// <summary>Gets each component's plane index in the frame (X, Y, B order).</summary>
    internal int[] Planes { get; }

    /// <summary>Gets each component's coefficients, frame-wide, a block's 64 at its 8×8 place.</summary>
    internal int[][] Coefficients { get; }

    /// <summary>Gets each component's quantized DC, one a block.</summary>
    internal int[][] Lf { get; }

    /// <summary>Gets each component's width in blocks.</summary>
    internal int[] Strides { get; }

    internal int[] DcOffset { get; }

    /// <summary>Adds the luma share back to the chroma coefficients in fixed point, as libjxl does (copied from libjxl through jxl-oxide).</summary>
    private void IntegerCfl(JxlVarDct v, int[][] jpeg)
    {
        int[] y = Coefficients[0];
        int stride = Strides[0] * 8;
        int height = y.Length / stride;
        int rounding = 1 << (CflBits - 1);
        foreach ((int component, int[] factors, int[] quant) in (ReadOnlySpan<(int, int[], int[])>)[(1, v.XFromY, jpeg[0]), (2, v.BFromY, jpeg[2])])
        {
            int[] ratio = new int[64];
            for (int i = 0; i < 64; i++)
            {
                ratio[i] = (1 << CflBits) * jpeg[1][i] / quant[i];
            }

            int[] c = Coefficients[component];
            for (int py = 0; py < height; py++)
            {
                int tileRow = (py / 64) * v.Tiles;
                int qy = py % 8;
                for (int px = 0; px < stride; px++)
                {
                    int factor = factors[tileRow + (px / 64)];
                    int q = ratio[qy + (8 * (px % 8))];
                    int scale = factor * (1 << CflBits) / CflDefaultFactor;
                    int qScale = ((q * scale) + rounding) >> CflBits;
                    int at = (py * stride) + px;
                    c[at] += ((y[at] * qScale) + rounding) >> CflBits;
                }
            }
        }
    }
}

/// <summary>Writes a JPEG scan's entropy-coded data: sequential, progressive first or progressive refinement (translated from jxl-oxide).</summary>
internal static class JpegScanWriter
{
    private static readonly int[] HSamples = [1, 2, 2, 1];
    private static readonly int[] VSamples = [1, 2, 1, 2];
    private static readonly int[] RgbOrder = [1, 0, 2];

    internal static void Write(Stream output, JbrdScan s, int type, JxlFrameHeader frame, JpegCoefficients data, HuffmanTable?[] dcTables, HuffmanTable?[] acTables, int restartInterval, JbrdPadding padding)
    {
        int[] up = JxlJbrd.Upsampling(frame);
        int count = s.Components.Count;
        int[] hs = [.. s.Components.Select(c => HSamples[up[c.Component]])];
        int[] vs = [.. s.Components.Select(c => VSamples[up[c.Component]])];
        int maxH = System.Numerics.BitOperations.TrailingZeroCount(hs.Max());
        int maxV = System.Numerics.BitOperations.TrailingZeroCount(vs.Max());
        int w8 = (int)((((frame.Width + 7) / 8) + maxH) >> maxH);
        int h8 = (int)((((frame.Height + 7) / 8) + maxV) >> maxV);
        if (count == 1)
        {
            // A scan of one component covers that component's own blocks (ITU-T T.81 A.2.2): ⌈⌈X·Hi/Hmax⌉/8⌉ of them.
            int[] allH = [.. up.Select(m => HSamples[m])];
            int[] allV = [.. up.Select(m => VSamples[m])];
            long width = ((frame.Width * hs[0]) + allH.Max() - 1) / allH.Max();
            long height = ((frame.Height * vs[0]) + allV.Max() - 1) / allV.Max();
            w8 = (int)((width + 7) / 8);
            h8 = (int)((height + 7) / 8);
            (hs, vs) = ([1], [1]);
        }

        int ss = Math.Max(s.Ss, 1);
        int se = s.Se + 1;
        int al = s.Al;
        int[] natural = JxlHfPass.Natural(0);
        ScanState state = new(count);
        int block = 0;
        short[] acs = new short[Math.Max(0, se - ss)];
        for (int y8 = 0; y8 < h8; y8++)
        {
            for (int x8 = 0; x8 < w8; x8++)
            {
                int mcu = x8 + (w8 * y8);
                if (restartInterval > 0 && mcu != 0 && mcu % restartInterval == 0)
                {
                    state.Restart(output, padding);
                }

                for (int k = 0; k < count; k++)
                {
                    (int component, int acIndex, int dcIndex) = s.Components[k];
                    HuffmanTable dc = dcTables[dcIndex] ?? HuffmanTable.Empty;
                    HuffmanTable ac = acTables[acIndex] ?? HuffmanTable.Empty;
                    state.LastAc ??= ac;
                    int idx = frame.YCbCr ? component : RgbOrder[component];
                    int[] coeffs = data.Coefficients[idx];
                    int[] lf = data.Lf[idx];
                    int stride = data.Strides[idx];
                    for (int dy = 0; dy < vs[k]; dy++)
                    {
                        int by = (y8 * vs[k]) + dy;
                        for (int dx = 0; dx < hs[k]; dx++)
                        {
                            int bx = (x8 * hs[k]) + dx;
                            short? dcCoeff = null;
                            if (s.Ss == 0)
                            {
                                int q = Math.Clamp(Saturate(lf[(by * stride) + bx] - data.DcOffset[idx]), -2047, 2047);
                                dcCoeff = (short)(q >> al);
                            }

                            int origin = (by * 8 * stride * 8) + (bx * 8);
                            for (int i = ss; i < se; i++)
                            {
                                int pos = natural[i];
                                short coeff = (short)coeffs[origin + ((pos / 8) * stride * 8) + (pos % 8)];
                                acs[i - ss] = coeff < 0 ? (short)-(-coeff >> al) : (short)(coeff >> al);
                            }

                            int? extra = s.ExtraZeroRuns.TryGetValue(block, out int e) ? e : null;
                            if (s.ResetPoints.Contains(block))
                            {
                                state.EmitEobRun();
                            }

                            switch (type)
                            {
                                case 0:
                                    Sequential(state, k, dc, ac, dcCoeff!.Value, acs, extra);
                                    break;
                                case 1:
                                    ProgressiveFirst(state, k, dc, ac, dcCoeff, acs, extra);
                                    break;
                                default:
                                    Refinement(state, ac, dcCoeff, acs, extra);
                                    break;
                            }

                            block++;
                        }
                    }
                }
            }
        }

        state.Flush(output, padding);
    }

    private static int Saturate(int v) => Math.Clamp(v, short.MinValue, short.MaxValue);

    private static int BitLength(int v) => v == 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)v);

    private static void Dc(ScanState state, int component, HuffmanTable dc, short coeff)
    {
        short diff = state.Predict(component, coeff);
        int magnitude = diff < 0 ? -diff : diff;
        int length = BitLength(magnitude);
        int raw = diff < 0 ? -magnitude - 1 : magnitude;
        (int len, ulong code) = dc.Lookup(length);
        state.Writer.WriteHuffman(code, len);
        state.Writer.WriteRaw((ulong)(uint)raw & ((1UL << length) - 1), length);
    }

    private static void Ac(ScanState state, HuffmanTable ac, int run, short coeff)
    {
        while (run >= 16)
        {
            (int zl, ulong zc) = ac.Lookup(0xF0);
            state.Writer.WriteHuffman(zc, zl);
            run -= 16;
        }

        int magnitude = coeff < 0 ? -coeff : coeff;
        int length = BitLength(magnitude);
        int raw = coeff < 0 ? ~magnitude : magnitude;
        (int len, ulong code) = ac.Lookup((run << 4) | length);
        state.Writer.WriteHuffman(code, len);
        state.Writer.WriteRaw((ulong)(uint)raw & ((1UL << length) - 1), length);
    }

    private static void Sequential(ScanState state, int component, HuffmanTable dc, HuffmanTable ac, short dcCoeff, ReadOnlySpan<short> acs, int? extra)
    {
        Dc(state, component, dc, dcCoeff);
        int at = 0;
        while (at < acs.Length)
        {
            int next = acs[at..].IndexOfAnyExcept((short)0);
            if (next < 0)
            {
                break;
            }

            Ac(state, ac, next, acs[at + next]);
            at += next + 1;
        }

        int zeros = acs.Length - at;
        if (extra is int runs)
        {
            (int zl, ulong zc) = ac.Lookup(0xF0);
            for (int i = 0; i < runs; i++)
            {
                state.Writer.WriteHuffman(zc, zl);
            }

            zeros -= runs * 16;
        }

        if (zeros > 0)
        {
            (int len, ulong code) = ac.Lookup(0);
            state.Writer.WriteHuffman(code, len);
        }
    }

    private static void ProgressiveFirst(ScanState state, int component, HuffmanTable dc, HuffmanTable ac, short? dcCoeff, ReadOnlySpan<short> acs, int? extra)
    {
        if (dcCoeff is short d)
        {
            short diff = state.Predict(component, d);
            int magnitude = diff < 0 ? -diff : diff;
            int length = BitLength(magnitude);
            int raw = diff < 0 ? -magnitude - 1 : magnitude;
            state.EmitEobRun();
            (int len, ulong code) = dc.Lookup(length);
            state.Writer.WriteHuffman(code, len);
            state.Writer.WriteRaw((ulong)(uint)raw & ((1UL << length) - 1), length);
        }

        int at = 0;
        while (at < acs.Length)
        {
            int next = acs[at..].IndexOfAnyExcept((short)0);
            if (next < 0)
            {
                break;
            }

            state.EmitEobRun();
            Ac(state, ac, next, acs[at + next]);
            at += next + 1;
        }

        int zeros = acs.Length - at;
        if (extra is int runs)
        {
            state.EmitEobRun();
            (int zl, ulong zc) = ac.Lookup(0xF0);
            for (int i = 0; i < runs; i++)
            {
                state.Writer.WriteHuffman(zc, zl);
            }

            zeros -= runs * 16;
        }

        if (state.EobRun == 0)
        {
            state.LastAc = ac;
        }

        if (zeros > 0)
        {
            state.EobRun++;
            if (state.EobRun >= 32767)
            {
                state.EmitEobRun();
            }
        }
    }

    private static void Refinement(ScanState state, HuffmanTable ac, short? dcCoeff, ReadOnlySpan<short> acs, int? extra)
    {
        if (dcCoeff is short d)
        {
            state.EmitEobRun();
            state.Writer.WriteRaw((ulong)(d & 1), 1);
        }

        int at = 0;
        while (at < acs.Length)
        {
            int next = -1;
            for (int i = at; i < acs.Length; i++)
            {
                if (acs[i] is 1 or -1)
                {
                    next = i - at;
                    break;
                }
            }

            if (next < 0)
            {
                break;
            }

            state.EmitEobRun();
            int zeroRuns = 0;
            int refinementLength = 0;
            ulong refinementBits = 0;
            foreach (short coeff in acs.Slice(at, next))
            {
                if (coeff == 0)
                {
                    zeroRuns++;
                    if (zeroRuns == 16)
                    {
                        (int zl, ulong zc) = ac.Lookup(0xF0);
                        state.Writer.WriteHuffman(zc, zl);
                        state.Writer.WriteRaw(refinementBits, refinementLength);
                        zeroRuns = 0;
                        refinementLength = 0;
                    }
                }
                else
                {
                    refinementBits = (refinementBits << 1) | (uint)(coeff & 1);
                    refinementLength++;
                }
            }

            short value = acs[at + next];
            at += next + 1;
            (int len, ulong code) = ac.Lookup((zeroRuns << 4) | 1);
            state.Writer.WriteHuffman(code, len);
            state.Writer.WriteRaw(value == 1 ? 1UL : 0UL, 1);
            state.Writer.WriteRaw(refinementBits, refinementLength);
        }

        int remainingZrl = extra ?? 0;
        if (remainingZrl > 0)
        {
            state.EmitEobRun();
        }

        (int zrlLength, ulong zrlCode) = remainingZrl > 0 ? ac.Lookup(0xF0) : (0, 0UL);
        int runs = 0;
        int bitLength = 0;
        ulong bits = 0;
        foreach (short coeff in acs[at..])
        {
            if (coeff == 0)
            {
                runs++;
                if (remainingZrl > 0 && runs == 16)
                {
                    state.Writer.WriteHuffman(zrlCode, zrlLength);
                    state.Writer.WriteRaw(bits, bitLength);
                    runs = 0;
                    bitLength = 0;
                    remainingZrl--;
                }
            }
            else
            {
                bits = (bits << 1) | (uint)(coeff & 1);
                bitLength++;
            }
        }

        for (int i = 0; i < remainingZrl; i++)
        {
            state.Writer.WriteHuffman(zrlCode, zrlLength);
            state.Writer.WriteRaw(bits, bitLength);
            runs = 0;
            bitLength = 0;
        }

        if (state.EobRun == 0)
        {
            state.LastAc = ac;
        }

        if (runs > 0 || bitLength > 0)
        {
            state.EobRun++;
            state.Refinements.Add((bits, bitLength));
            if (state.EobRun >= 32767)
            {
                state.EmitEobRun();
            }
        }
    }

    private sealed class ScanState(int components)
    {
        private readonly short[] _dcPrediction = new short[components];
        private int _restartMarker;

        internal JpegEntropyWriter Writer { get; private set; } = new();

        internal int EobRun { get; set; }

        internal HuffmanTable? LastAc { get; set; }

        internal List<(ulong Bits, int Length)> Refinements { get; } = [];

        internal short Predict(int component, short coeff)
        {
            short diff = (short)(coeff - _dcPrediction[component]);
            _dcPrediction[component] = coeff;
            return diff;
        }

        internal void EmitEobRun()
        {
            if (EobRun == 0)
            {
                return;
            }

            HuffmanTable ac = LastAc ?? throw new CorruptDataException(JxlFormat.Info.Name, -1, "An end-of-band run comes before any AC table.");
            int n = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)EobRun);
            (int len, ulong code) = ac.Lookup(n << 4);
            Writer.WriteHuffman(code, len);
            Writer.WriteRaw((ulong)(EobRun & ((1 << n) - 1)), n);
            EobRun = 0;
            foreach ((ulong bits, int length) in Refinements)
            {
                Writer.WriteRaw(bits, length);
            }

            Refinements.Clear();
        }

        internal void Flush(Stream output, JbrdPadding padding)
        {
            EmitEobRun();
            JpegEntropyWriter writer = Writer;
            Writer = new JpegEntropyWriter();
            int needed = writer.PaddingBits;
            if (needed != 0)
            {
                writer.WriteRaw(padding.Has ? padding.Read(needed) : uint.MaxValue & ((1u << needed) - 1), needed);
            }

            writer.Finish(output);
        }

        internal void Restart(Stream output, JbrdPadding padding)
        {
            Array.Clear(_dcPrediction);
            Flush(output, padding);
            output.Write([0xFF, (byte)(0xD0 + _restartMarker)]);
            _restartMarker = (_restartMarker + 1) % 8;
        }
    }
}

/// <summary>Writes JPEG entropy-coded bits MSB first, a 0 byte stuffed after each 0xFF.</summary>
internal sealed class JpegEntropyWriter
{
    private readonly List<byte> _output = [];
    private ulong _buffer;
    private int _bits;

    internal int PaddingBits => (8 - (_bits % 8)) % 8;

    /// <summary>Writes the top <paramref name="length"/> bits of <paramref name="code"/> (left-aligned).</summary>
    internal void WriteHuffman(ulong code, int length)
    {
        if (length == 0)
        {
            return;
        }

        _buffer |= code >> _bits;
        _bits += length;
        if (_bits >= 64)
        {
            int extra = _bits - 64;
            ulong full = _buffer;
            _bits -= 64;
            _buffer = extra == 0 ? 0 : code << (length - extra);
            for (int i = 0; i < 8; i++)
            {
                Emit((byte)(full >> (56 - (8 * i))));
            }
        }
    }

    /// <summary>Writes the low <paramref name="length"/> bits of <paramref name="value"/>.</summary>
    internal void WriteRaw(ulong value, int length)
    {
        if (length != 0)
        {
            WriteHuffman(value << (64 - length), length);
        }
    }

    internal void Finish(Stream output)
    {
        int bytes = (_bits + 7) / 8;
        for (int i = 0; i < bytes; i++)
        {
            Emit((byte)(_buffer >> (56 - (8 * i))));
        }

        output.Write(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_output));
    }

    private void Emit(byte b)
    {
        _output.Add(b);
        if (b == 0xFF)
        {
            _output.Add(0);
        }
    }
}
