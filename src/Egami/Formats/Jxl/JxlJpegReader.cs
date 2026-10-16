namespace Egami.Formats.Jxl;

/// <summary>A JPEG component as JPEG XL recompression keeps it: its id, sampling, quantization table and coefficients.</summary>
internal sealed class JpegComponentData
{
    internal int Id { get; init; }

    internal int HSamp { get; init; }

    internal int VSamp { get; init; }

    /// <summary>Gets or sets the quantization table: its DQT slot while reading, then its index in <see cref="JpegData.Quant"/>.</summary>
    internal int QuantIndex { get; set; }

    internal int WidthInBlocks { get; set; }

    internal int HeightInBlocks { get; set; }

    /// <summary>Gets or sets the quantized coefficients, 64 a block in natural (row by row) order, blocks row by row.</summary>
    internal short[] Coeffs { get; set; } = [];
}

/// <summary>A DQT table: values in natural order, its slot and precision, and whether it ends its marker.</summary>
internal sealed record JpegQuantTable(int[] Values, int Index, int Precision, bool IsLast);

/// <summary>A DHT table as the jbrd box keeps it: counts per length (with the extra symbol 256 at the longest), and the symbols.</summary>
internal sealed record JpegHuffmanCode(int SlotId, int[] Counts, int[] Values, bool IsLast);

/// <summary>A scan: its components, spectral range and approximation, and what bit-exact reconstruction needs of its coding.</summary>
internal sealed class JpegScanData
{
    internal List<(int Component, int DcTable, int AcTable)> Components { get; } = [];

    internal int Ss { get; set; }

    internal int Se { get; set; }

    internal int Ah { get; set; }

    internal int Al { get; set; }

    internal List<int> ResetPoints { get; } = [];

    internal List<(int Block, int Runs)> ExtraZeroRuns { get; } = [];
}

/// <summary>Everything of a JPEG file that JPEG XL keeps to rebuild it byte for byte (libjxl's JPEGData).</summary>
internal sealed class JpegData
{
    internal int Width { get; set; }

    internal int Height { get; set; }

    internal int RestartInterval { get; set; }

    /// <summary>Gets each APP segment from its marker byte on (marker, length, payload).</summary>
    internal List<byte[]> AppData { get; } = [];

    /// <summary>Gets each APP segment's type in the jbrd box: 0 unknown, 1 ICC, 2 Exif, 3 XMP.</summary>
    internal List<uint> AppTypes { get; } = [];

    internal List<byte[]> ComData { get; } = [];

    internal List<JpegQuantTable> Quant { get; } = [];

    internal List<JpegHuffmanCode> Huffman { get; } = [];

    internal List<JpegComponentData> Components { get; } = [];

    internal List<JpegScanData> Scans { get; } = [];

    /// <summary>Gets the markers after SOI in order, 0xFF standing for bytes between markers.</summary>
    internal List<byte> MarkerOrder { get; } = [];

    internal List<byte[]> InterMarkerData { get; } = [];

    internal byte[] Tail { get; set; } = [];

    internal bool HasZeroPaddingBit { get; set; }

    internal List<bool> PaddingBits { get; } = [];

    internal bool IsProgressive { get; set; }
}

/// <summary>
/// Reads a JPEG into <see cref="JpegData"/>: markers, tables and segments as they are, and the scans decoded to coefficients
/// with the padding bits, reset points and extra zero runs bit-exact reconstruction needs. Translated from libjxl 0.12.0's
/// enc_jpeg_data_reader.cc (see THIRD-PARTY-NOTICES).
/// </summary>
internal static class JxlJpegReader
{
    private const int DcAlphabet = 12;
    private const int AcAlphabet = 256;
    private const int MaxComponents = 4;
    private const int MaxDhtMarkers = 512;

    private static readonly int[] NaturalOrder =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28, 35, 42,
        49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
        // Extra entries so a run past the end of a block stays in range (as libjxl's table).
        63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63,
    ];

    private static readonly bool[] ValidMarker =
    [
        true, true, true, false, true, false, false, false, false, false, false, false, false, false, false, false, true, true, true,
        true, true, true, true, true, false, true, true, true, false, true, false, false, true, true, true, true, true, true, true,
        true, true, true, true, true, true, true, true, true, false, false, false, false, false, false, false, false, false, false,
        false, false, false, false, true, false,
    ];

    /// <summary>Gets the zigzag order: the natural position of each coded coefficient.</summary>
    internal static ReadOnlySpan<int> Zigzag => NaturalOrder.AsSpan(0, 64);

    /// <summary>
    /// Reads a whole JPEG, or throws <see cref="CorruptDataException"/>, <see cref="UnsupportedFormatException"/> or, before any
    /// scan is decoded, <see cref="LimitExceededException"/> when the frame is larger than <paramref name="limits"/> allow.
    /// </summary>
    internal static JpegData Read(byte[] data, SafetyLimits limits)
    {
        JpegData jpg = new();
        int len = data.Length;
        int pos = 0;
        if (len < 2 || data[0] != 0xFF || data[1] != 0xD8)
        {
            throw Corrupt(0, "The file does not start with a JPEG SOI marker.");
        }

        pos = 2;
        CheckFrameType(data);
        HuffmanLut?[] dcLut = new HuffmanLut?[4];
        HuffmanLut?[] acLut = new HuffmanLut?[4];
        ushort[,] progression = new ushort[MaxComponents, 64];
        bool foundSof = false, foundSos = false, foundDri = false;
        int marker;
        do
        {
            int skipped = FindNextMarker(data, pos);
            if (skipped > 0)
            {
                jpg.MarkerOrder.Add(0xFF);
                jpg.InterMarkerData.Add(data[pos..(pos + skipped)]);
                pos += skipped;
            }

            if (pos + 2 > len || data[pos] != 0xFF)
            {
                throw Corrupt(pos, "A JPEG marker was expected.");
            }

            marker = data[pos + 1];
            pos += 2;
            switch (marker)
            {
                case 0xC0 or 0xC1 or 0xC2:
                    jpg.IsProgressive = marker == 0xC2;
                    ReadSof(data, ref pos, jpg);
                    CheckLimits(jpg, limits);
                    foundSof = true;
                    break;
                case 0xC4:
                    ReadDht(data, ref pos, jpg, dcLut, acLut);
                    break;
                case >= 0xD0 and <= 0xD7:
                case 0xD9:
                    break;
                case 0xDA:
                    if (!foundSof)
                    {
                        throw Corrupt(pos, "A JPEG scan comes before the frame header.");
                    }

                    ReadScan(data, ref pos, jpg, dcLut, acLut, progression);
                    foundSos = true;
                    break;
                case 0xDB:
                    ReadDqt(data, ref pos, jpg);
                    break;
                case 0xDD:
                    if (foundDri)
                    {
                        throw new UnsupportedFormatException("The JPEG has two DRI markers, which JPEG XL recompression does not keep.");
                    }

                    foundDri = true;
                    {
                        int start = pos;
                        Need(data, pos, 4);
                        int markerLen = U16(data, ref pos);
                        jpg.RestartInterval = U16(data, ref pos);
                        EndOf(start, markerLen, pos);
                    }

                    break;
                case >= 0xE0 and <= 0xEF:
                    jpg.AppData.Add(Segment(data, ref pos));
                    break;
                case 0xFE:
                    jpg.ComData.Add(Segment(data, ref pos));
                    break;
                default:
                    throw new UnsupportedFormatException($"The JPEG has marker {marker:X2}, which JPEG XL recompression does not take (arithmetic coding, lossless or hierarchical JPEG).");
            }

            jpg.MarkerOrder.Add((byte)marker);
        }
        while (marker != 0xD9);

        if (!foundSof || !foundSos)
        {
            throw Corrupt(pos, "The JPEG has no frame header or no scan.");
        }

        if (pos < len)
        {
            jpg.Tail = data[pos..];
        }

        // Components refer to quantization tables by their place in the list from here on.
        foreach (JpegComponentData c in jpg.Components)
        {
            int index = jpg.Quant.FindIndex(q => q.Index == c.QuantIndex);
            if (index < 0)
            {
                throw Corrupt(pos, $"The JPEG has no quantization table {c.QuantIndex}.");
            }

            c.QuantIndex = index;
        }

        if (jpg.Huffman.Count == 0 || jpg.Huffman.Count >= MaxDhtMarkers)
        {
            throw Corrupt(pos, $"The JPEG has {jpg.Huffman.Count} Huffman tables.");
        }

        return jpg;
    }

    /// <summary>Refuses arithmetic-coded, lossless and hierarchical JPEGs up front: their SOF markers come before any scan.</summary>
    private static void CheckFrameType(byte[] data)
    {
        int p = 2;
        while (p + 4 <= data.Length && data[p] == 0xFF)
        {
            int m = data[p + 1];
            if (m == 0xFF)
            {
                p++;
                continue;
            }

            if (m is 0xDE or (>= 0xC3 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC)))
            {
                throw new UnsupportedFormatException($"The JPEG's frame type (marker {m:X2}: arithmetic coding, lossless or hierarchical) has no JPEG XL recompression.");
            }

            if (m is 0xDA or (>= 0xC0 and <= 0xC2))
            {
                return;
            }

            p += 2 + ((data[p + 2] << 8) | data[p + 3]);
        }
    }

    private static void CheckLimits(JpegData jpg, SafetyLimits limits)
    {
        long pixels = (long)jpg.Width * jpg.Height;
        if (jpg.Width > limits.MaxWidth || jpg.Height > limits.MaxHeight || pixels > limits.MaxPixels)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxPixels), limits.MaxPixels, pixels);
        }

        long bytes = jpg.Components.Sum(c => (long)c.WidthInBlocks * c.HeightInBlocks * 64 * sizeof(short));
        if (bytes > limits.MaxTotalBytes)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), limits.MaxTotalBytes, bytes);
        }
    }

    private static CorruptDataException Corrupt(long offset, string message) => new("JPEG", offset, message);

    private static void Need(byte[] data, int pos, int n)
    {
        if (pos + n > data.Length)
        {
            throw Corrupt(pos, "The JPEG ends inside a marker segment.");
        }
    }

    private static void EndOf(int start, int markerLen, int pos)
    {
        if (start + markerLen != pos)
        {
            throw Corrupt(pos, "A JPEG marker segment's length does not match its contents.");
        }
    }

    private static int U16(byte[] data, ref int pos)
    {
        int v = (data[pos] << 8) | data[pos + 1];
        pos += 2;
        return v;
    }

    /// <summary>Gets an APP or COM segment from its marker byte on, as the jbrd box stores it.</summary>
    private static byte[] Segment(byte[] data, ref int pos)
    {
        Need(data, pos, 2);
        int markerLen = U16(data, ref pos);
        if (markerLen < 2)
        {
            throw Corrupt(pos, "A JPEG segment is shorter than its length field.");
        }

        Need(data, pos, markerLen - 2);
        byte[] s = data[(pos - 3)..(pos - 3 + markerLen + 1)];
        pos += markerLen - 2;
        return s;
    }

    private static void ReadSof(byte[] data, ref int pos, JpegData jpg)
    {
        if (jpg.Width != 0)
        {
            throw Corrupt(pos, "The JPEG has two frame headers.");
        }

        int start = pos;
        Need(data, pos, 8);
        int markerLen = U16(data, ref pos);
        int precision = data[pos++];
        int height = U16(data, ref pos);
        int width = U16(data, ref pos);
        int count = data[pos++];
        if (precision != 8)
        {
            throw new UnsupportedFormatException($"The JPEG has {precision}-bit samples; JPEG XL recompression takes 8-bit JPEGs.");
        }

        if (height < 1 || width < 1 || count < 1 || count > MaxComponents)
        {
            throw Corrupt(pos, $"The JPEG frame is {width} by {height} with {count} components.");
        }

        Need(data, pos, 3 * count);
        jpg.Width = width;
        jpg.Height = height;
        bool[] seen = new bool[256];
        int maxH = 1, maxV = 1;
        for (int i = 0; i < count; i++)
        {
            int id = data[pos++];
            if (seen[id])
            {
                throw Corrupt(pos, $"The JPEG frame lists component {id} twice.");
            }

            seen[id] = true;
            int factor = data[pos++];
            int h = factor >> 4, v = factor & 15;
            if (h is < 1 or > 15 || v is < 1 or > 15)
            {
                throw Corrupt(pos, $"A JPEG component has sampling factors {h} by {v}.");
            }

            jpg.Components.Add(new JpegComponentData { Id = id, HSamp = h, VSamp = v, QuantIndex = data[pos++] });
            maxH = Math.Max(maxH, h);
            maxV = Math.Max(maxV, v);
        }

        int mcuRows = Ceil(height, maxV * 8);
        int mcuCols = Ceil(width, maxH * 8);
        foreach (JpegComponentData c in jpg.Components)
        {
            if (maxH % c.HSamp != 0 || maxV % c.VSamp != 0)
            {
                throw new UnsupportedFormatException("The JPEG's subsampling ratios are not whole numbers.");
            }

            c.WidthInBlocks = mcuCols * c.HSamp;
            c.HeightInBlocks = mcuRows * c.VSamp;
        }

        EndOf(start, markerLen, pos);
    }

    private static int Ceil(int a, int b) => (a + b - 1) / b;

    private static void ReadDht(byte[] data, ref int pos, JpegData jpg, HuffmanLut?[] dcLut, HuffmanLut?[] acLut)
    {
        int start = pos;
        Need(data, pos, 2);
        int markerLen = U16(data, ref pos);
        if (markerLen == 2)
        {
            // An empty DHT marker: kept as a table with no symbols.
            jpg.Huffman.Add(new JpegHuffmanCode(0, new int[17], [], true));
            return;
        }

        while (pos < start + markerLen)
        {
            Need(data, pos, 17);
            int slot = data[pos++];
            bool ac = (slot & 0x10) != 0;
            int index = ac ? slot - 0x10 : slot;
            if (index is < 0 or > 3)
            {
                throw Corrupt(pos, $"A JPEG Huffman table has slot {slot:X2}.");
            }

            int[] counts = new int[17];
            int total = 0;
            int space = 1 << 16;
            int maxDepth = 1;
            for (int i = 1; i <= 16; i++)
            {
                int count = data[pos++];
                if (count != 0)
                {
                    maxDepth = i;
                }

                counts[i] = count;
                total += count;
                space -= count * (1 << (16 - i));
            }

            if (total > (ac ? AcAlphabet : DcAlphabet))
            {
                throw Corrupt(pos, $"A JPEG Huffman table has {total} symbols.");
            }

            Need(data, pos, total);
            int[] values = new int[total + 1];
            bool[] seen = new bool[256];
            for (int i = 0; i < total; i++)
            {
                int value = data[pos++];
                if ((!ac && value >= DcAlphabet) || seen[value])
                {
                    throw Corrupt(pos, $"A JPEG Huffman table has symbol {value} out of range or twice.");
                }

                seen[value] = true;
                values[i] = value;
            }

            // The extra all-ones code, which libjxl adds so the code is complete.
            counts[maxDepth]++;
            values[total] = AcAlphabet;
            space -= 1 << (16 - maxDepth);
            if (space < 0)
            {
                throw Corrupt(pos, "A JPEG Huffman table's code lengths overflow.");
            }

            bool isLast = pos == start + markerLen;
            (ac ? acLut : dcLut)[index] = new HuffmanLut(counts, values);
            jpg.Huffman.Add(new JpegHuffmanCode(slot, counts, values, isLast));
        }

        EndOf(start, markerLen, pos);
    }

    private static void ReadDqt(byte[] data, ref int pos, JpegData jpg)
    {
        int start = pos;
        Need(data, pos, 2);
        int markerLen = U16(data, ref pos);
        if (markerLen == 2)
        {
            throw Corrupt(pos, "A JPEG DQT marker has no table.");
        }

        while (pos < start + markerLen && jpg.Quant.Count < 4)
        {
            Need(data, pos, 1);
            int b = data[pos++];
            int precision = b >> 4;
            int index = b & 15;
            if (precision > 1 || index > 3)
            {
                throw Corrupt(pos, $"A JPEG quantization table has precision {precision} and slot {index}.");
            }

            Need(data, pos, (precision + 1) * 64);
            int[] values = new int[64];
            for (int i = 0; i < 64; i++)
            {
                int v = precision != 0 ? U16(data, ref pos) : data[pos++];
                if (v == 0)
                {
                    throw Corrupt(pos, "A JPEG quantization table has a zero.");
                }

                values[NaturalOrder[i]] = v;
            }

            jpg.Quant.Add(new JpegQuantTable(values, index, precision, pos == start + markerLen));
        }

        EndOf(start, markerLen, pos);
    }

    private static JpegScanData ReadSos(byte[] data, ref int pos, JpegData jpg)
    {
        int start = pos;
        Need(data, pos, 3);
        int markerLen = U16(data, ref pos);
        int count = data[pos++];
        if (count < 1 || count > jpg.Components.Count)
        {
            throw Corrupt(pos, $"A JPEG scan has {count} components.");
        }

        JpegScanData scan = new();
        Need(data, pos, 2 * count);
        bool[] seen = new bool[256];
        for (int i = 0; i < count; i++)
        {
            int id = data[pos++];
            if (seen[id])
            {
                throw Corrupt(pos, $"A JPEG scan lists component {id} twice.");
            }

            seen[id] = true;
            int comp = jpg.Components.FindLastIndex(c => c.Id == id);
            if (comp < 0)
            {
                throw Corrupt(pos, $"A JPEG scan has unknown component {id}.");
            }

            int tables = data[pos++];
            int dc = tables >> 4, ac = tables & 15;
            if (dc > 3 || ac > 3)
            {
                throw Corrupt(pos, $"A JPEG scan uses Huffman tables {dc} and {ac}.");
            }

            scan.Components.Add((comp, dc, ac));
        }

        Need(data, pos, 3);
        scan.Ss = data[pos++];
        scan.Se = data[pos++];
        if (scan.Ss > 63 || scan.Se < scan.Ss || scan.Se > 63)
        {
            throw Corrupt(pos, $"A JPEG scan covers coefficients {scan.Ss} to {scan.Se}.");
        }

        int a = data[pos++];
        scan.Ah = a >> 4;
        scan.Al = a & 15;
        foreach ((int _, int dc, int ac) in scan.Components)
        {
            bool hasDc = jpg.Huffman.Any(h => h.SlotId == dc);
            bool hasAc = jpg.Huffman.Any(h => h.SlotId == ac + 16);
            if ((scan.Ss == 0 && !hasDc) || (scan.Se > 0 && !hasAc))
            {
                throw Corrupt(pos, "A JPEG scan uses a Huffman table not defined before it.");
            }
        }

        jpg.Scans.Add(scan);
        EndOf(start, markerLen, pos);
        return scan;
    }

    private static void ReadScan(byte[] data, ref int pos, JpegData jpg, HuffmanLut?[] dcLut, HuffmanLut?[] acLut, ushort[,] progression)
    {
        JpegScanData scan = ReadSos(data, ref pos, jpg);
        bool interleaved = scan.Components.Count > 1;
        int maxH = jpg.Components.Max(c => c.HSamp);
        int maxV = jpg.Components.Max(c => c.VSamp);
        int mcuRows = Ceil(jpg.Height, maxV * 8);
        int mcuCols = Ceil(jpg.Width, maxH * 8);
        if (!interleaved)
        {
            JpegComponentData c = jpg.Components[scan.Components[0].Component];
            mcuCols = Ceil(jpg.Width * c.HSamp, 8 * maxH);
            mcuRows = Ceil(jpg.Height * c.VSamp, 8 * maxV);
        }

        bool progressive = jpg.IsProgressive;
        int al = progressive ? scan.Al : 0;
        int ah = progressive ? scan.Ah : 0;
        int ss = progressive ? scan.Ss : 0;
        int se = progressive ? scan.Se : 63;
        int scanMask = ah == 0 ? 0xFFFF << al : 1 << al;
        int refineMask = (1 << al) - 1;
        foreach ((int comp, _, _) in scan.Components)
        {
            for (int k = ss; k <= se; k++)
            {
                if ((progression[comp, k] & scanMask) != 0 || (progression[comp, k] & refineMask) != 0)
                {
                    throw Corrupt(pos, "JPEG scans overlap or come out of order.");
                }

                progression[comp, k] |= (ushort)scanMask;
            }
        }

        if (al > 10)
        {
            throw Corrupt(pos, $"A JPEG scan has Al = {al}.");
        }

        foreach (JpegComponentData c in jpg.Components)
        {
            if (c.Coeffs.Length == 0)
            {
                long blocks = (long)c.WidthInBlocks * c.HeightInBlocks;
                if (blocks * 64 > Array.MaxLength)
                {
                    throw new LimitExceededException(nameof(SafetyLimits.MaxPixels), Array.MaxLength / 64, blocks);
                }

                c.Coeffs = new short[blocks * 64];
            }
        }

        short[] lastDc = new short[MaxComponents];
        BitState br = new(data, pos);
        int restartsToGo = jpg.RestartInterval;
        int nextRestart = 0;
        int eobrun = -1;
        int blockScanIndex = 0;
        for (int mcuY = 0; mcuY < mcuRows; mcuY++)
        {
            for (int mcuX = 0; mcuX < mcuCols; mcuX++)
            {
                if (jpg.RestartInterval > 0)
                {
                    if (restartsToGo == 0)
                    {
                        int at = br.Finish(jpg);
                        if (at + 2 > data.Length || data[at] != 0xFF || data[at + 1] != 0xD0 + nextRestart)
                        {
                            throw Corrupt(at, "A JPEG restart marker is missing.");
                        }

                        br.Reset(at + 2);
                        nextRestart = (nextRestart + 1) & 7;
                        restartsToGo = jpg.RestartInterval;
                        Array.Clear(lastDc);
                        if (eobrun > 0)
                        {
                            throw Corrupt(at, "A JPEG end-of-band run crosses a restart marker.");
                        }

                        eobrun = -1;
                    }

                    restartsToGo--;
                }

                foreach ((int comp, int dcTable, int acTable) in scan.Components)
                {
                    JpegComponentData c = jpg.Components[comp];
                    HuffmanLut? dc = dcLut[dcTable];
                    HuffmanLut? ac = acLut[acTable];
                    int ny = interleaved ? c.VSamp : 1;
                    int nx = interleaved ? c.HSamp : 1;
                    for (int iy = 0; iy < ny; iy++)
                    {
                        for (int ix = 0; ix < nx; ix++)
                        {
                            int by = (mcuY * ny) + iy;
                            int bx = (mcuX * nx) + ix;
                            if (bx >= c.WidthInBlocks || by >= c.HeightInBlocks)
                            {
                                throw Corrupt(br.Position, "A JPEG scan runs past its component.");
                            }

                            Span<short> coeffs = c.Coeffs.AsSpan(((by * c.WidthInBlocks) + bx) * 64, 64);
                            bool reset = false;
                            int zeroRuns = 0;
                            if (ah == 0)
                            {
                                DecodeBlock(dc, ac, ss, se, al, ref eobrun, ref reset, ref zeroRuns, br, ref lastDc[comp], coeffs);
                            }
                            else
                            {
                                RefineBlock(ac, ss, se, al, ref eobrun, ref reset, br, coeffs);
                            }

                            if (reset)
                            {
                                scan.ResetPoints.Add(blockScanIndex);
                            }

                            if (zeroRuns > 0)
                            {
                                scan.ExtraZeroRuns.Add((blockScanIndex, zeroRuns));
                            }

                            blockScanIndex++;
                        }
                    }
                }
            }
        }

        if (eobrun > 0)
        {
            throw Corrupt(br.Position, "A JPEG end-of-band run is too long.");
        }

        pos = br.Finish(jpg);
        if (pos > data.Length)
        {
            throw Corrupt(pos, "The JPEG ends inside a scan.");
        }
    }

    private static int Extend(int x, int s)
    {
        int half = 1 << (s - 1);
        return x >= half ? x : x - (1 << s) + 1;
    }

    private static int Symbol(HuffmanLut? table, BitState br)
    {
        if (table is null)
        {
            throw Corrupt(br.Position, "A JPEG scan uses an undefined Huffman table.");
        }

        br.Fill();
        int peek = (int)(br.Value >> (br.BitsLeft - 16)) & 0xFFFF;
        int length = table.Lengths[peek];
        if (length == 0)
        {
            throw Corrupt(br.Position, "A JPEG scan has an invalid Huffman code.");
        }

        br.BitsLeft -= length;
        return table.Values[peek];
    }

    private static void DecodeBlock(HuffmanLut? dcTable, HuffmanLut? acTable, int ss, int se, int al, ref int eobrun, ref bool reset, ref int zeroRuns, BitState br, ref short lastDc, Span<short> coeffs)
    {
        int am = 1 << al;
        bool eobAllowed = ss > 0;
        if (ss == 0)
        {
            int s = Symbol(dcTable, br);
            if (s >= DcAlphabet)
            {
                throw Corrupt(br.Position, $"A JPEG DC coefficient has symbol {s}.");
            }

            int diff = s > 0 ? Extend(br.ReadBits(s), s) : 0;
            int coeff = diff + lastDc;
            int dc = coeff * am;
            if (dc != (short)dc)
            {
                throw Corrupt(br.Position, $"A JPEG DC coefficient is {dc}.");
            }

            coeffs[0] = (short)dc;
            lastDc = (short)coeff;
            ss++;
        }

        if (ss > se)
        {
            return;
        }

        if (eobrun > 0)
        {
            eobrun--;
            return;
        }

        zeroRuns = 0;
        for (int k = ss; k <= se; k++)
        {
            int sr = Symbol(acTable, br);
            if (sr >= AcAlphabet)
            {
                throw Corrupt(br.Position, $"A JPEG AC coefficient has symbol {sr}.");
            }

            int r = sr >> 4;
            int s = sr & 15;
            if (s > 0)
            {
                k += r;
                if (k > se)
                {
                    throw Corrupt(br.Position, "A JPEG AC coefficient falls outside its band.");
                }

                if (s + al >= DcAlphabet)
                {
                    throw Corrupt(br.Position, "A JPEG AC coefficient is out of range.");
                }

                coeffs[NaturalOrder[k]] = (short)(Extend(br.ReadBits(s), s) * am);
                zeroRuns = 0;
            }
            else if (r == 15)
            {
                k += 15;
                zeroRuns++;
            }
            else
            {
                if (eobAllowed && k == ss && eobrun == 0)
                {
                    reset = true;
                }

                eobrun = 1 << r;
                if (r > 0)
                {
                    if (!eobAllowed)
                    {
                        throw Corrupt(br.Position, "A JPEG end-of-band run crosses the DC coefficient.");
                    }

                    eobrun += br.ReadBits(r);
                }

                break;
            }
        }

        eobrun--;
    }

    private static void RefineBlock(HuffmanLut? acTable, int ss, int se, int al, ref int eobrun, ref bool reset, BitState br, Span<short> coeffs)
    {
        int am = 1 << al;
        bool eobAllowed = ss > 0;
        if (ss == 0)
        {
            int s = br.ReadBits(1);
            coeffs[0] = unchecked((short)((ushort)coeffs[0] | (ushort)(s * am)));
            ss++;
        }

        if (ss > se)
        {
            return;
        }

        int p1 = am;
        int m1 = -am;
        int k = ss;
        bool inZeroRun = false;
        if (eobrun <= 0)
        {
            for (; k <= se; k++)
            {
                int s = Symbol(acTable, br);
                if (s >= AcAlphabet)
                {
                    throw Corrupt(br.Position, $"A JPEG AC refinement has symbol {s}.");
                }

                int r = s >> 4;
                s &= 15;
                if (s != 0)
                {
                    if (s != 1)
                    {
                        throw Corrupt(br.Position, $"A JPEG AC refinement has size {s}.");
                    }

                    s = br.ReadBits(1) != 0 ? p1 : m1;
                    inZeroRun = false;
                }
                else
                {
                    if (r != 15)
                    {
                        if (eobAllowed && k == ss && eobrun == 0)
                        {
                            reset = true;
                        }

                        eobrun = 1 << r;
                        if (r > 0)
                        {
                            if (!eobAllowed)
                            {
                                throw Corrupt(br.Position, "A JPEG end-of-band run crosses the DC coefficient.");
                            }

                            eobrun += br.ReadBits(r);
                        }

                        break;
                    }

                    inZeroRun = true;
                }

                do
                {
                    int at = NaturalOrder[k];
                    int coef = coeffs[at];
                    if (coef != 0)
                    {
                        if (br.ReadBits(1) != 0 && (coef & p1) == 0)
                        {
                            coef += coef >= 0 ? p1 : m1;
                        }

                        coeffs[at] = (short)coef;
                    }
                    else if (--r < 0)
                    {
                        break;
                    }

                    k++;
                }
                while (k <= se);

                if (s != 0)
                {
                    if (k > se)
                    {
                        throw Corrupt(br.Position, "A JPEG AC refinement falls outside its band.");
                    }

                    coeffs[NaturalOrder[k]] = (short)s;
                }
            }
        }

        if (inZeroRun)
        {
            throw Corrupt(br.Position, "A JPEG zero run comes right before the end of a band.");
        }

        if (eobrun > 0)
        {
            for (; k <= se; k++)
            {
                int at = NaturalOrder[k];
                int coef = coeffs[at];
                if (coef != 0)
                {
                    if (br.ReadBits(1) != 0 && (coef & p1) == 0)
                    {
                        coef += coef >= 0 ? p1 : m1;
                    }

                    coeffs[at] = (short)coef;
                }
            }
        }

        eobrun--;
    }

    private static int FindNextMarker(byte[] data, int pos)
    {
        int skipped = 0;
        while (pos + 1 < data.Length && (data[pos] != 0xFF || data[pos + 1] < 0xC0 || !ValidMarker[data[pos + 1] - 0xC0]))
        {
            pos++;
            skipped++;
        }

        return skipped;
    }

    /// <summary>A Huffman decoding table indexed by the next 16 bits: each entry's symbol and code length (0 for no code).</summary>
    private sealed class HuffmanLut
    {
        internal HuffmanLut(int[] counts, int[] values)
        {
            Lengths = new byte[1 << 16];
            Values = new ushort[1 << 16];
            int code = 0;
            int k = 0;
            for (int length = 1; length <= 16; length++)
            {
                for (int i = 0; i < counts[length]; i++)
                {
                    int first = code << (16 - length);
                    int n = 1 << (16 - length);
                    Lengths.AsSpan(first, n).Fill((byte)length);
                    Values.AsSpan(first, n).Fill((ushort)values[k++]);
                    code++;
                }

                code <<= 1;
            }
        }

        internal byte[] Lengths { get; }

        internal ushort[] Values { get; }
    }

    /// <summary>The scan bit reader: 0xFF00 stuffing undone, zeros fed past the next marker (libjxl's BitReaderState).</summary>
    private sealed class BitState
    {
        private readonly byte[] _data;
        private int _pos;
        private int _nextMarker;

        internal BitState(byte[] data, int pos)
        {
            _data = data;
            Reset(pos);
        }

        internal ulong Value { get; private set; }

        internal int BitsLeft { get; set; }

        internal int Position => _pos;

        internal void Reset(int pos)
        {
            _pos = pos;
            Value = 0;
            BitsLeft = 0;
            _nextMarker = _data.Length - 2;
            Fill();
        }

        internal void Fill()
        {
            if (BitsLeft <= 16)
            {
                while (BitsLeft <= 56)
                {
                    Value = (Value << 8) | (uint)NextByte();
                    BitsLeft += 8;
                }
            }
        }

        internal int ReadBits(int n)
        {
            Fill();
            int v = (int)((Value >> (BitsLeft - n)) & ((1UL << n) - 1));
            BitsLeft -= n;
            return v;
        }

        /// <summary>Records the padding bits of the last byte, steps back over the bytes read ahead and gives the position after the scan data.</summary>
        internal int Finish(JpegData jpg)
        {
            int padBits = BitsLeft & 7;
            if (padBits > 0)
            {
                ulong mask = (1UL << padBits) - 1;
                ulong bits = (Value >> (BitsLeft - padBits)) & mask;
                if (bits != mask)
                {
                    jpg.HasZeroPaddingBit = true;
                }

                for (int i = padBits - 1; i >= 0; i--)
                {
                    jpg.PaddingBits.Add(((bits >> i) & 1) != 0);
                }
            }

            int unused = BitsLeft >> 3;
            while (unused-- > 0)
            {
                _pos--;
                if (_pos < _nextMarker && _data[_pos] == 0 && _data[_pos - 1] == 0xFF)
                {
                    _pos--;
                }
            }

            if (_pos > _nextMarker)
            {
                throw Corrupt(_pos, "A JPEG scan ends unexpectedly.");
            }

            return _pos;
        }

        private byte NextByte()
        {
            if (_pos >= _nextMarker)
            {
                _pos++;
                return 0;
            }

            byte c = _data[_pos++];
            if (c == 0xFF)
            {
                if (_data[_pos] == 0)
                {
                    _pos++;
                }
                else
                {
                    _nextMarker = _pos - 1;
                }
            }

            return c;
        }
    }
}
