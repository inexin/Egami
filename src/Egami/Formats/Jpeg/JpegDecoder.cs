using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;

using Egami.Pixels;

namespace Egami.Formats.Jpeg;

/// <summary>
/// Decodes a Huffman-coded JPEG. A sequential file whose first scan holds every component is decoded one MCU row at a time:
/// entropy decoding, dequantization and IDCT into a strip of samples per component, then upsampling and color conversion of the
/// rows above into the raster, so memory is the raster plus a few rows. Progressive files and sequential files with a scan per
/// component are decoded into a buffer of every coefficient first, scan by scan, and converted the same way after the last scan.
/// </summary>
internal sealed class JpegDecoder<TPixel> : IDisposable
    where TPixel : unmanaged, IPixelType<TPixel>
{
    /// <summary>The most scans a file may have. Real progressive files have about 10; more only slows a hostile file down.</summary>
    internal const int MaxScans = 1000;

    private readonly JpegHeader _header;
    private readonly ReaderOptions _options;
    private readonly Raster<TPixel> _raster;
    private readonly Component[] _components;
    private readonly int _maxH;
    private readonly int _maxV;
    private readonly int _mcusX;
    private readonly int _mcusY;
    private readonly int _mcuRows;
    private readonly int _blockSize;
    private readonly int _width;
    private readonly int _height;
    private readonly bool _fast;
    private readonly int _channels;
    private byte[] _rowBuffer;
    private byte[] _sampleBuffer;
    private bool _warned;

    /// <summary>Creates a decoder whose output is the image scaled by 1/<paramref name="scale"/> (1, 2, 4 or 8).</summary>
    internal JpegDecoder(JpegHeader header, ReaderOptions options, Raster<TPixel> raster, bool fastUpsampling, int scale = 1)
    {
        _header = header;
        _options = options;
        _raster = raster;
        _fast = fastUpsampling;
        bool single = header.Components.Length == 1;

        // A one-component frame is coded non-interleaved: its MCU is one block whatever its sampling factors say.
        _maxH = single ? 1 : header.MaxH;
        _maxV = single ? 1 : header.MaxV;
        _mcusX = (header.Width + (8 * _maxH) - 1) / (8 * _maxH);
        _mcusY = (header.Height + (8 * _maxV) - 1) / (8 * _maxV);

        // At 1/n scale a full-resolution block becomes 8/n samples a side.
        _blockSize = 8 / scale;
        _width = ScaledSize(header.Width, scale);
        _height = ScaledSize(header.Height, scale);
        _mcuRows = _blockSize * _maxV;
        _channels = single ? 1 : 3;
        _components = new Component[header.Components.Length];
        for (int i = 0; i < _components.Length; i++)
        {
            JpegFrameComponent c = header.Components[i];
            int h = single ? 1 : c.H;
            int v = single ? 1 : c.V;

            // Subsampled components are scaled up in the IDCT where they can be, a doubling at a time while it still divides
            // evenly in both directions, so a scaled decode upsamples less or not at all; this is libjpeg's choice too.
            int size = _blockSize;
            while (size < 8 && (_maxH * _blockSize) % (h * size * 2) == 0 && (_maxV * _blockSize) % (v * size * 2) == 0)
            {
                size *= 2;
            }

            _components[i] = new Component(
                h, v, size, _maxH * _blockSize, _maxV * _blockSize, _mcusX * h * size,
                (int)(((long)header.Width * h * size) + (_maxH * 8) - 1) / (_maxH * 8), (int)(((long)header.Height * v * size) + (_maxV * 8) - 1) / (_maxV * 8));
        }

        _rowBuffer = ArrayPool<byte>.Shared.Rent(header.Width * Math.Max(_channels, _components.Length));
        _sampleBuffer = ArrayPool<byte>.Shared.Rent(header.Width * _components.Length);
    }

    /// <summary>Returns the output size of a dimension at 1/<paramref name="scale"/> scale, rounded up as libjpeg does.</summary>
    internal static int ScaledSize(int size, int scale)
    {
        return (size + scale - 1) / scale;
    }

    /// <summary>Decodes the image data from the first scan, whose header has just been read, to the end, filling the raster.</summary>
    internal void Decode(EgamiInput input)
    {
        using JpegBitReader reader = new(input);
        JpegScan scan = _header.FirstScan!;
        if (!_header.IsProgressive && scan.Components.Length == _components.Length)
        {
            DecodeStreaming(input, reader, scan);
            return;
        }

        foreach (Component c in _components)
        {
            c.AllocateCoefficients(input.Memory, _mcusX * c.H, _mcusY * c.V);
        }

        int scans = 0;
        while (true)
        {
            if (++scans > MaxScans)
            {
                throw new CorruptDataException(JpegFormat.Info.Name, -1, string.Create(CultureInfo.InvariantCulture, $"The file has more than {MaxScans:N0} scans."));
            }

            input.CancellationToken.ThrowIfCancellationRequested();
            if (!DecodeBufferedScan(reader, scan) || NextScan(input, reader) is not JpegScan next)
            {
                break;
            }

            scan = next;
        }

        for (int i = 0; i < _components.Length; i++)
        {
            // A component no scan reached is all zeros, mid-gray, and still needs a table.
            LatchQuant(i);
        }

        for (int my = 0; my < _mcusY; my++)
        {
            input.CancellationToken.ThrowIfCancellationRequested();
            foreach (Component c in _components)
            {
                Span<byte> strip = c.Strip(my);
                for (int v = 0; v < c.V; v++)
                {
                    int by = (my * c.V) + v;
                    for (int bx = 0; bx < _mcusX * c.H; bx++)
                    {
                        c.Idct(c.Block(bx, by), strip[(((v * c.StripWidth) + bx) * c.BlockSize)..]);
                    }
                }
            }

            if (my > 0)
            {
                EmitRows(my - 1);
            }
        }

        EmitRows(_mcusY - 1);
    }

    public void Dispose()
    {
        foreach (Component c in _components)
        {
            c.Dispose();
        }

        ArrayPool<byte>.Shared.Return(Interlocked.Exchange(ref _rowBuffer, null!));
        ArrayPool<byte>.Shared.Return(Interlocked.Exchange(ref _sampleBuffer, null!));
    }

    /// <summary>Converts a quantization table from zigzag to natural order.</summary>
    private static ushort[] Natural(ushort[] zigzag)
    {
        ushort[] natural = new ushort[64];
        for (int k = 0; k < 64; k++)
        {
            natural[JpegTables.ZigZag[k]] = zigzag[k];
        }

        return natural;
    }

    private static CorruptDataException Missing(string kind, int slot)
    {
        return new CorruptDataException(JpegFormat.Info.Name, -1, $"The scan uses {kind} Huffman table {slot}, which is missing.");
    }

    /// <summary>Decodes one block's coefficients into natural order (T.81 F.2.2).</summary>
    private static void DecodeBlock(JpegBitReader reader, JpegHuffmanTable dc, JpegHuffmanTable ac, ref int predictor, Span<short> block)
    {
        block.Clear();
        int s = dc.Decode(reader);
        predictor += reader.ReceiveExtend(s & 15);
        block[0] = (short)predictor;
        ReadOnlySpan<byte> zigzag = JpegTables.ZigZag;
        for (int k = 1; k < 64; k++)
        {
            int rs = ac.Decode(reader);
            int r = rs >> 4;
            s = rs & 15;
            if (s == 0)
            {
                if (r != 15)
                {
                    break;
                }

                k += 15;
                continue;
            }

            k += r;
            int value = reader.ReceiveExtend(s);
            if (k < 64)
            {
                block[zigzag[k]] = (short)value;
            }
        }
    }

    /// <summary>The first DC scan of a progressive file (T.81 G.1.2.1): the DC difference, scaled up by the point transform.</summary>
    private static void DecodeDcFirst(JpegBitReader reader, JpegHuffmanTable dc, ref int predictor, Span<short> block, int al)
    {
        int s = dc.Decode(reader);
        predictor += reader.ReceiveExtend(s & 15);
        block[0] = (short)(predictor << al);
    }

    /// <summary>A DC refinement scan: one more bit of the DC coefficient.</summary>
    private static void DecodeDcRefine(JpegBitReader reader, Span<short> block, int al)
    {
        if (reader.Read(1) != 0)
        {
            block[0] |= (short)(1 << al);
        }
    }

    /// <summary>The first scan of an AC band (T.81 G.1.2.2), with end-of-band runs that span blocks.</summary>
    private static void DecodeAcFirst(JpegBitReader reader, JpegHuffmanTable ac, ref int eobRun, Span<short> block, int ss, int se, int al)
    {
        if (eobRun > 0)
        {
            eobRun--;
            return;
        }

        ReadOnlySpan<byte> zigzag = JpegTables.ZigZag;
        for (int k = ss; k <= se; k++)
        {
            int rs = ac.Decode(reader);
            int r = rs >> 4;
            int s = rs & 15;
            if (s != 0)
            {
                k += r;
                int value = reader.ReceiveExtend(s);
                if (k <= se)
                {
                    block[zigzag[k]] = (short)(value << al);
                }
            }
            else if (r == 15)
            {
                k += 15;
            }
            else
            {
                // EOBn: this block and 2^r + extra bits - 1 more end here.
                eobRun = (1 << r) - 1;
                if (r > 0)
                {
                    eobRun += reader.Read(r);
                }

                break;
            }
        }
    }

    /// <summary>
    /// An AC refinement scan (T.81 G.1.2.3): one correction bit for each coefficient already nonzero, and new coefficients of
    /// magnitude one placed by runs that count only the coefficients still zero.
    /// </summary>
    private static void DecodeAcRefine(JpegBitReader reader, JpegHuffmanTable ac, ref int eobRun, Span<short> block, int ss, int se, int al)
    {
        ReadOnlySpan<byte> zigzag = JpegTables.ZigZag;
        int p1 = 1 << al;
        int m1 = -1 << al;
        int k = ss;
        if (eobRun == 0)
        {
            for (; k <= se; k++)
            {
                int rs = ac.Decode(reader);
                int r = rs >> 4;
                int s = rs & 15;
                if (s != 0)
                {
                    // The size is always 1 here; the next bit is the sign.
                    s = reader.Read(1) != 0 ? p1 : m1;
                }
                else if (r != 15)
                {
                    eobRun = 1 << r;
                    if (r > 0)
                    {
                        eobRun += reader.Read(r);
                    }

                    break;
                }

                // Skip r zero coefficients, correcting the nonzero ones passed; the new coefficient goes in the next zero one.
                for (; k <= se; k++)
                {
                    ref short coefficient = ref block[zigzag[k]];
                    if (coefficient != 0)
                    {
                        Refine(reader, ref coefficient, p1, m1);
                    }
                    else if (--r < 0)
                    {
                        break;
                    }
                }

                if (s != 0 && k <= 63)
                {
                    block[zigzag[k]] = (short)s;
                }
            }
        }

        if (eobRun > 0)
        {
            // The band ends in this block: only the corrections of the nonzero coefficients remain.
            for (; k <= se; k++)
            {
                ref short coefficient = ref block[zigzag[k]];
                if (coefficient != 0)
                {
                    Refine(reader, ref coefficient, p1, m1);
                }
            }

            eobRun--;
        }
    }

    /// <summary>Applies a correction bit: adds the new bit to the magnitude unless it is there already.</summary>
    private static void Refine(JpegBitReader reader, ref short coefficient, int p1, int m1)
    {
        if (reader.Read(1) != 0 && (coefficient & p1) == 0)
        {
            coefficient = (short)(coefficient + (coefficient >= 0 ? p1 : m1));
        }
    }

    /// <summary>Decodes a sequential file whose first scan holds every component, one MCU row at a time.</summary>
    private void DecodeStreaming(EgamiInput input, JpegBitReader reader, JpegScan scan)
    {
        JpegHuffmanTable[] dc = new JpegHuffmanTable[scan.Components.Length];
        JpegHuffmanTable[] ac = new JpegHuffmanTable[scan.Components.Length];
        Component[] order = new Component[scan.Components.Length];
        for (int i = 0; i < scan.Components.Length; i++)
        {
            JpegScanComponent sc = scan.Components[i];
            order[i] = _components[sc.ComponentIndex];
            LatchQuant(sc.ComponentIndex);
            dc[i] = new JpegHuffmanTable(_header.DcTables[sc.DcTable] ?? throw Missing("DC", sc.DcTable));
            ac[i] = new JpegHuffmanTable(_header.AcTables[sc.AcTable] ?? throw Missing("AC", sc.AcTable));
        }

        reader.BeginScan();
        Span<short> block = stackalloc short[64];
        Span<int> predictors = stackalloc int[order.Length];
        int restart = _header.RestartInterval;
        int nextRestart = 0;
        int mcu = 0;
        bool ended = false;
        for (int my = 0; my < _mcusY; my++)
        {
            input.CancellationToken.ThrowIfCancellationRequested();
            for (int mx = 0; mx < _mcusX; mx++, mcu++)
            {
                if (restart > 0 && mcu > 0 && mcu % restart == 0 && !ended)
                {
                    ended = !RestartInterval(reader, ref nextRestart);
                    predictors.Clear();
                }

                for (int i = 0; i < order.Length; i++)
                {
                    Component c = order[i];
                    Span<byte> strip = c.Strip(my);
                    for (int v = 0; v < c.V; v++)
                    {
                        for (int h = 0; h < c.H; h++)
                        {
                            if (ended)
                            {
                                // After the data ends, the rest of the image is mid-gray: every coefficient 0.
                                block.Clear();
                            }
                            else
                            {
                                DecodeBlock(reader, dc[i], ac[i], ref predictors[i], block);
                            }

                            c.Idct(block, strip[(((v * c.StripWidth) + (mx * c.H) + h) * c.BlockSize)..]);
                        }
                    }
                }

                ended = ended || EndedEarly(reader, "The image data ends early. The rest of the image is gray.");
            }

            if (my > 0)
            {
                EmitRows(my - 1);
            }
        }

        EmitRows(_mcusY - 1);
    }

    /// <summary>
    /// Decodes one scan into the coefficient buffer. Returns false if the data ended early, so no scan can follow; the coefficients
    /// so far are kept, as libjpeg does.
    /// </summary>
    private bool DecodeBufferedScan(JpegBitReader reader, JpegScan scan)
    {
        int count = scan.Components.Length;
        int ss = scan.SpectralStart;
        int se = scan.SpectralEnd;
        int ah = scan.ApproxHigh;
        int al = scan.ApproxLow;
        bool progressive = _header.IsProgressive;
        if (progressive && ((ss == 0 && se != 0) || (ss > 0 && count != 1) || (ah != 0 && al != ah - 1)))
        {
            throw new CorruptDataException(JpegFormat.Info.Name, -1, $"A progressive scan has an invalid band ({ss} to {se}) or bit position ({ah}, {al}).");
        }

        // Which kind of scan: 0 sequential, 1 DC first, 2 DC refine, 3 AC first, 4 AC refine.
        int kind = !progressive ? 0 : ss == 0 ? (ah == 0 ? 1 : 2) : (ah == 0 ? 3 : 4);
        JpegHuffmanTable?[] dc = new JpegHuffmanTable?[count];
        JpegHuffmanTable?[] ac = new JpegHuffmanTable?[count];
        Component[] order = new Component[count];
        for (int i = 0; i < count; i++)
        {
            JpegScanComponent sc = scan.Components[i];
            order[i] = _components[sc.ComponentIndex];
            LatchQuant(sc.ComponentIndex);
            if (kind is 0 or 1)
            {
                dc[i] = new JpegHuffmanTable(_header.DcTables[sc.DcTable] ?? throw Missing("DC", sc.DcTable));
            }

            if (kind is 0 or 3 or 4)
            {
                ac[i] = new JpegHuffmanTable(_header.AcTables[sc.AcTable] ?? throw Missing("AC", sc.AcTable));
            }
        }

        reader.BeginScan();
        Span<int> predictors = stackalloc int[count];
        int eobRun = 0;
        int restart = _header.RestartInterval;
        int nextRestart = 0;
        int mcu = 0;

        // One component is coded non-interleaved: an MCU is one block, over the blocks the component really covers. Several are
        // interleaved in whole MCUs, padding blocks included.
        bool single = count == 1;
        int unitsX = single ? NonInterleavedBlocks(_header.Width, order[0].H, _maxH) : _mcusX;
        int unitsY = single ? NonInterleavedBlocks(_header.Height, order[0].V, _maxV) : _mcusY;
        for (int uy = 0; uy < unitsY; uy++)
        {
            for (int ux = 0; ux < unitsX; ux++, mcu++)
            {
                if (restart > 0 && mcu > 0 && mcu % restart == 0)
                {
                    if (!RestartInterval(reader, ref nextRestart))
                    {
                        return false;
                    }

                    predictors.Clear();
                    eobRun = 0;
                }

                for (int i = 0; i < count; i++)
                {
                    Component c = order[i];
                    int blocksX = single ? 1 : c.H;
                    int blocksY = single ? 1 : c.V;
                    for (int v = 0; v < blocksY; v++)
                    {
                        for (int h = 0; h < blocksX; h++)
                        {
                            Span<short> block = single ? c.Block(ux, uy) : c.Block((ux * c.H) + h, (uy * c.V) + v);
                            switch (kind)
                            {
                                case 0:
                                    DecodeBlock(reader, dc[i]!, ac[i]!, ref predictors[i], block);
                                    break;
                                case 1:
                                    DecodeDcFirst(reader, dc[i]!, ref predictors[i], block, al);
                                    break;
                                case 2:
                                    DecodeDcRefine(reader, block, al);
                                    break;
                                case 3:
                                    DecodeAcFirst(reader, ac[i]!, ref eobRun, block, ss, se, al);
                                    break;
                                default:
                                    DecodeAcRefine(reader, ac[i]!, ref eobRun, block, ss, se, al);
                                    break;
                            }
                        }
                    }
                }

                if (EndedEarly(reader, "The image data ends early. The scans so far were decoded; the rest of the image has less detail or is gray."))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Reads the segments after a scan up to the next scan header and returns it, or null at the end of the image. Tables and the
    /// restart interval may change between scans; anything else is skipped.
    /// </summary>
    private JpegScan? NextScan(EgamiInput input, JpegBitReader reader)
    {
        int marker = reader.EndScan();
        while (true)
        {
            if (marker == JpegBitReader.EndOfFile)
            {
                Problem("The file ends before its end-of-image marker; the scans so far were decoded.");
                return null;
            }

            if (marker == JpegMarker.Eoi)
            {
                return null;
            }

            if (!JpegMarker.IsStandalone((byte)marker))
            {
                int high = reader.ReadSegmentByte();
                int low = reader.ReadSegmentByte();
                int length = (high << 8) + low - 2;
                if (low < 0 || length < 0)
                {
                    Problem("A segment between scans is cut off or has an invalid length.");
                    return null;
                }

                byte[] payload = new byte[length];
                for (int i = 0; i < length; i++)
                {
                    int b = reader.ReadSegmentByte();
                    if (b < 0)
                    {
                        Problem("A segment between scans is cut off.");
                        return null;
                    }

                    payload[i] = (byte)b;
                }

                switch (marker)
                {
                    case JpegMarker.Sos:
                        return JpegSegmentReader.ParseScan(input, _header, payload, -1);
                    case JpegMarker.Dht:
                        JpegSegmentReader.ReadHuffmanTables(input, _header, payload, -1);
                        break;
                    case JpegMarker.Dqt:
                        JpegSegmentReader.ReadQuantTables(input, _header, payload, -1);
                        break;
                    case JpegMarker.Dri:
                        _header.RestartInterval = payload.Length >= 2 ? (payload[0] << 8) | payload[1] : throw input.Corrupt(-1, "The restart interval segment is too short.");
                        break;
                    case JpegMarker.Dnl:
                        break;
                    case >= JpegMarker.Sof0 and <= JpegMarker.Sof15 when marker is not (JpegMarker.Dht or JpegMarker.Dac or 0xC8):
                        throw input.Corrupt(-1, "The file has more than one frame header (SOF).");
                }
            }

            marker = reader.NextMarker(out bool junk);
            if (junk)
            {
                Problem("There are stray bytes between segments after a scan; they were skipped.");
            }
        }
    }

    /// <summary>Returns the blocks a non-interleaved scan codes across a dimension: those the full-size component covers.</summary>
    private static int NonInterleavedBlocks(int size, int factor, int max)
    {
        long samples = (((long)size * factor) + max - 1) / max;
        return (int)((samples + 7) / 8);
    }

    /// <summary>Uses the quantization table a component has when it first appears in a scan, as libjpeg does.</summary>
    private void LatchQuant(int index)
    {
        Component c = _components[index];
        if (c.Quant is null)
        {
            int slot = _header.Components[index].QuantTable;
            ushort[] quant = _header.QuantTables[slot]
                ?? throw new CorruptDataException(JpegFormat.Info.Name, -1, $"Component {_header.Components[index].Id} uses quantization table {slot}, which is missing.");
            c.Quant = Natural(quant);
        }
    }

    /// <summary>Handles a restart interval boundary. Returns false if the data ended instead.</summary>
    private bool RestartInterval(JpegBitReader reader, ref int nextRestart)
    {
        bool ended = false;
        if (!reader.Restart(nextRestart))
        {
            Problem("A restart marker is missing or out of order; the data was resynchronized at the next one.");
            // An out-of-order restart marker was consumed and decoding resumes; any other marker ends the data.
            ended = reader.Marker != 0;
        }

        nextRestart = reader.LastRestart >= 0 ? reader.LastRestart + 1 : nextRestart + 1;
        return !ended;
    }

    /// <summary>
    /// Returns whether the data ran out within the MCU just decoded. Running into a restart marker early only loses the rest of
    /// its interval; any other marker or the end of the file ends the data.
    /// </summary>
    private bool EndedEarly(JpegBitReader reader, string message)
    {
        if (reader.PastEnd && !reader.AtRestartMarker)
        {
            Problem(message);
            return true;
        }

        return false;
    }

    private void Problem(string message)
    {
        if (_options.Recovery == RecoveryMode.Strict)
        {
            throw new CorruptDataException(JpegFormat.Info.Name, -1, message);
        }

        if (!_warned)
        {
            _raster.Metadata.Warnings.Add(message);
            _warned = true;
        }
    }

    /// <summary>Upsamples, converts and writes the output rows of MCU row <paramref name="my"/>, using its neighbors for context.</summary>
    private void EmitRows(int my)
    {
        int first = my * _mcuRows;
        int last = Math.Min(_height, first + _mcuRows);
        int width = _width;
        Span<byte> samples = _sampleBuffer.AsSpan(0, width * _components.Length);
        for (int y = first; y < last; y++)
        {
            for (int i = 0; i < _components.Length; i++)
            {
                Upsample(_components[i], y, samples.Slice(i * width, width));
            }

            WriteRow(y, samples, width);
        }
    }

    /// <summary>Produces one full-width row of a component for output row <paramref name="y"/>.</summary>
    private void Upsample(Component c, int y, Span<byte> output)
    {
        int hr = c.HorizontalRatio;
        int vr = c.VerticalRatio;
        int width = _width;
        int row = y / vr;
        ReadOnlySpan<byte> cur = c.Row(row);
        if (hr == 1 && vr == 1)
        {
            cur[..width].CopyTo(output);
            return;
        }

        // libjpeg also replicates at 1/8 scale, where a block is one sample and there is nothing to interpolate within it.
        if (_fast || _blockSize == 1 || (hr != 1 && hr != 2) || (vr != 1 && vr != 2) || !c.WholeRatios)
        {
            // Replicated samples: each component sample covers hr × vr output pixels.
            if (hr == 1)
            {
                cur[..width].CopyTo(output);
            }
            else if (hr == 2)
            {
                for (int x = 0; x < width >> 1; x++)
                {
                    byte s = cur[x];
                    output[2 * x] = s;
                    output[(2 * x) + 1] = s;
                }

                if ((width & 1) != 0)
                {
                    output[width - 1] = cur[width >> 1];
                }
            }
            else
            {
                for (int x = 0; x < width; x++)
                {
                    output[x] = cur[x / hr];
                }
            }

            return;
        }

        int n = c.Width;
        if (vr == 2)
        {
            // Triangle filter vertically: 3/4 of the nearer row, 1/4 of the next one out (replicated past the edges).
            bool upper = (y & 1) == 0;
            int other = Math.Clamp(upper ? row - 1 : row + 1, 0, c.Height - 1);
            ReadOnlySpan<byte> near = c.Row(other);
            if (hr == 1)
            {
                int bias = upper ? 1 : 2;
                for (int x = 0; x < width; x++)
                {
                    output[x] = (byte)(((3 * cur[x]) + near[x] + bias) >> 2);
                }

                return;
            }

            // Both directions at once, on column sums, as libjpeg's h2v2 filter does.
            int thisSum = (3 * cur[0]) + near[0];
            if (n == 1)
            {
                output[0] = (byte)((thisSum * 4 + 8) >> 4);
                if (width > 1)
                {
                    output[1] = (byte)((thisSum * 4 + 7) >> 4);
                }

                return;
            }

            int nextSum = (3 * cur[1]) + near[1];
            output[0] = (byte)(((thisSum * 4) + 8) >> 4);
            output[1] = (byte)(((thisSum * 3) + nextSum + 7) >> 4);
            int lastSum = thisSum;
            thisSum = nextSum;
            for (int i = 1; i < n - 1; i++)
            {
                nextSum = (3 * cur[i + 1]) + near[i + 1];
                Put(output, 2 * i, ((thisSum * 3) + lastSum + 8) >> 4);
                Put(output, (2 * i) + 1, ((thisSum * 3) + nextSum + 7) >> 4);
                lastSum = thisSum;
                thisSum = nextSum;
            }

            Put(output, 2 * (n - 1), ((thisSum * 3) + lastSum + 8) >> 4);
            Put(output, (2 * (n - 1)) + 1, ((thisSum * 4) + 7) >> 4);
            return;
        }

        // Horizontal only (4:2:2): 3/4 of the sample and 1/4 of its neighbor on the side of the output pixel.
        if (n == 1)
        {
            output[0] = cur[0];
            if (width > 1)
            {
                output[1] = cur[0];
            }

            return;
        }

        output[0] = cur[0];
        output[1] = (byte)(((cur[0] * 3) + cur[1] + 2) >> 2);
        for (int i = 1; i < n - 1; i++)
        {
            Put(output, 2 * i, ((cur[i] * 3) + cur[i - 1] + 1) >> 2);
            Put(output, (2 * i) + 1, ((cur[i] * 3) + cur[i + 1] + 2) >> 2);
        }

        Put(output, 2 * (n - 1), ((cur[n - 1] * 3) + cur[n - 2] + 1) >> 2);
        Put(output, (2 * (n - 1)) + 1, cur[n - 1]);
    }

    /// <summary>Writes a byte if it falls inside the image; the last upsampled column of an odd width does not.</summary>
    private static void Put(Span<byte> output, int x, int value)
    {
        if ((uint)x < (uint)output.Length)
        {
            output[x] = (byte)value;
        }
    }

    /// <summary>Converts one row of component samples to RGB or gray and stores it in the raster.</summary>
    private void WriteRow(int y, ReadOnlySpan<byte> samples, int width)
    {
        Span<TPixel> target = _raster.Row(y);
        if (_channels == 1)
        {
            ReadOnlySpan<byte> gray = samples[..width];
            if (typeof(TPixel) == typeof(Gray8))
            {
                gray.CopyTo(MemoryMarshal.AsBytes(target));
            }
            else
            {
                PixelConvert.Convert(MemoryMarshal.Cast<byte, Gray8>(gray), target);
            }

            return;
        }

        Span<byte> rgb = typeof(TPixel) == typeof(Rgb8) ? MemoryMarshal.AsBytes(target) : _rowBuffer.AsSpan(0, width * 3);
        JpegColor.ToRgb(_header.Details.ColorSpace, samples, width, rgb);
        if (typeof(TPixel) != typeof(Rgb8))
        {
            PixelConvert.Convert(MemoryMarshal.Cast<byte, Rgb8>(rgb), target);
        }
    }

    /// <summary>
    /// One component's sampling, quantization table and the ring of three MCU-row strips of samples, plus, for a buffered decode,
    /// every coefficient in natural order, in chunks of whole block rows that each fit one memory block.
    /// </summary>
    private sealed class Component(int h, int v, int blockSize, int mcuWidth, int mcuHeight, int stripWidth, int width, int height) : IDisposable
    {
        private readonly int _stripHeight = v * blockSize;
        private byte[] _strips = ArrayPool<byte>.Shared.Rent(3 * stripWidth * v * blockSize);
        private IMemoryOwner<byte>?[] _chunks = [];
        private int _blocksX;
        private int _rowsPerChunk = 1;

        internal int H => h;

        internal int V => v;

        /// <summary>Gets or sets the quantization table in natural order, fixed when the component first appears in a scan.</summary>
        internal ushort[]? Quant { get; set; }

        internal int StripWidth => stripWidth;

        /// <summary>Gets the side of a block in samples after the IDCT: 8 at full scale, less in a scaled decode.</summary>
        internal int BlockSize => blockSize;

        /// <summary>Gets how many output pixels across one sample covers.</summary>
        internal int HorizontalRatio => mcuWidth / (h * blockSize);

        /// <summary>Gets how many output rows one sample row covers.</summary>
        internal int VerticalRatio => mcuHeight / (v * blockSize);

        /// <summary>Gets whether both ratios are whole numbers; odd sampling factors such as 3 can leave a remainder.</summary>
        internal bool WholeRatios => mcuWidth % (h * blockSize) == 0 && mcuHeight % (v * blockSize) == 0;

        /// <summary>Gets the component's real width in samples, before MCU padding.</summary>
        internal int Width => width;

        /// <summary>Gets the component's real height in samples, before MCU padding.</summary>
        internal int Height => height;

        /// <summary>Gets the strip that holds MCU row <paramref name="my"/>.</summary>
        internal Span<byte> Strip(int my)
        {
            return _strips.AsSpan(my % 3 * stripWidth * _stripHeight, stripWidth * _stripHeight);
        }

        /// <summary>Gets sample row <paramref name="row"/>, which must be in one of the three strips held.</summary>
        internal ReadOnlySpan<byte> Row(int row)
        {
            return Strip(row / _stripHeight).Slice(row % _stripHeight * stripWidth, stripWidth);
        }

        /// <summary>Dequantizes and transforms one block into <paramref name="target"/>, at this component's block size.</summary>
        internal void Idct(ReadOnlySpan<short> block, Span<byte> target)
        {
            switch (blockSize)
            {
                case 8:
                    JpegIdct.Transform(block, Quant, target, stripWidth);
                    break;
                case 4:
                    JpegIdct.Transform4(block, Quant, target, stripWidth);
                    break;
                case 2:
                    JpegIdct.Transform2(block, Quant, target, stripWidth);
                    break;
                default:
                    JpegIdct.Transform1(block, Quant, target, stripWidth);
                    break;
            }
        }

        /// <summary>Allocates zeroed coefficients for <paramref name="blocksX"/> × <paramref name="blocksY"/> blocks, within the load's memory budget.</summary>
        internal void AllocateCoefficients(BudgetedMemorySource memory, int blocksX, int blocksY)
        {
            int rowBytes = blocksX * 64 * sizeof(short);
            memory.Check((long)rowBytes * blocksY);
            _blocksX = blocksX;
            _rowsPerChunk = Math.Clamp(memory.MaxBlockBytes / rowBytes, 1, blocksY);
            _chunks = new IMemoryOwner<byte>?[(blocksY + _rowsPerChunk - 1) / _rowsPerChunk];
            for (int i = 0; i < _chunks.Length; i++)
            {
                int rows = Math.Min(_rowsPerChunk, blocksY - (i * _rowsPerChunk));
                _chunks[i] = memory.Allocate(rows * rowBytes, ClearMode.Zero);
            }
        }

        /// <summary>Gets the 64 coefficients of block (<paramref name="bx"/>, <paramref name="by"/>).</summary>
        internal Span<short> Block(int bx, int by)
        {
            Span<short> chunk = MemoryMarshal.Cast<byte, short>(_chunks[by / _rowsPerChunk]!.Memory.Span);
            return chunk.Slice((((by % _rowsPerChunk) * _blocksX) + bx) * 64, 64);
        }

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(Interlocked.Exchange(ref _strips, null!));
            foreach (IMemoryOwner<byte>? chunk in _chunks)
            {
                chunk?.Dispose();
            }

            _chunks = [];
        }
    }
}

/// <summary>Color conversion from a JPEG's components to RGB, in the fixed point libjpeg uses (16 fraction bits, rounded).</summary>
internal static class JpegColor
{
    private const int ScaleBits = 16;
    private const int Half = 1 << (ScaleBits - 1);

    private static readonly int[] CrToR = Table(1.40200, Half);
    private static readonly int[] CbToB = Table(1.77200, Half);
    private static readonly int[] CrToG = Table(-0.71414, 0, shift: false);
    private static readonly int[] CbToG = Table(-0.34414, Half, shift: false);

    /// <summary>
    /// Converts a row of planar component samples (component i at <c>samples[i * width..]</c>) to packed RGB. YCbCr uses the
    /// full-range BT.601 (JFIF) equations; CMYK and YCCK as Adobe stores them (inverted) use R = C × K / 255.
    /// </summary>
    internal static void ToRgb(JpegColorSpace space, ReadOnlySpan<byte> samples, int width, Span<byte> rgb)
    {
        ReadOnlySpan<byte> a = samples[..width];
        ReadOnlySpan<byte> b = samples.Slice(width, width);
        ReadOnlySpan<byte> c = samples.Slice(2 * width, width);
        switch (space)
        {
            case JpegColorSpace.Rgb:
                for (int x = 0; x < width; x++)
                {
                    rgb[3 * x] = a[x];
                    rgb[(3 * x) + 1] = b[x];
                    rgb[(3 * x) + 2] = c[x];
                }

                break;
            case JpegColorSpace.Cmyk or JpegColorSpace.Ycck:
                ReadOnlySpan<byte> k = samples.Slice(3 * width, width);
                for (int x = 0; x < width; x++)
                {
                    int c0 = a[x], m0 = b[x], y0 = c[x];
                    if (space == JpegColorSpace.Ycck)
                    {
                        // YCCK holds the inverted CMY as YCbCr: convert, then invert back.
                        int r = Clamp(a[x] + CrToR[c[x]]);
                        int g = Clamp(a[x] + ((CbToG[b[x]] + CrToG[c[x]]) >> ScaleBits));
                        int bl = Clamp(a[x] + CbToB[b[x]]);
                        c0 = 255 - r;
                        m0 = 255 - g;
                        y0 = 255 - bl;
                    }

                    rgb[3 * x] = (byte)(((c0 * k[x]) + 127) / 255);
                    rgb[(3 * x) + 1] = (byte)(((m0 * k[x]) + 127) / 255);
                    rgb[(3 * x) + 2] = (byte)(((y0 * k[x]) + 127) / 255);
                }

                break;
            default:
                for (int x = 0; x < width; x++)
                {
                    int y = a[x], cb = b[x], cr = c[x];
                    rgb[3 * x] = Clamp(y + CrToR[cr]);
                    rgb[(3 * x) + 1] = Clamp(y + ((CbToG[cb] + CrToG[cr]) >> ScaleBits));
                    rgb[(3 * x) + 2] = Clamp(y + CbToB[cb]);
                }

                break;
        }
    }

    private static int[] Table(double factor, int add, bool shift = true)
    {
        int f = (int)((factor * (1 << ScaleBits)) + (factor < 0 ? -0.5 : 0.5));
        int[] table = new int[256];
        for (int i = 0; i < 256; i++)
        {
            int value = (f * (i - 128)) + add;
            table[i] = shift ? value >> ScaleBits : value;
        }

        return table;
    }

    private static byte Clamp(int value)
    {
        return (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
    }
}
