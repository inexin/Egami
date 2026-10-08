using System.Buffers;
using System.Runtime.InteropServices;

using Egami.Pixels;

namespace Egami.Formats.Jpeg;

/// <summary>
/// Decodes a sequential (baseline or extended) Huffman-coded JPEG one MCU row at a time: entropy decoding, dequantization and IDCT
/// into a strip of samples per component, then upsampling and color conversion of the rows above into the raster. Only three MCU
/// rows of samples per component are held, so memory is the raster plus a few rows.
/// </summary>
internal sealed class JpegDecoder<TPixel> : IDisposable
    where TPixel : unmanaged, IPixelType<TPixel>
{
    private readonly JpegHeader _header;
    private readonly ReaderOptions _options;
    private readonly Raster<TPixel> _raster;
    private readonly Component[] _components;
    private readonly int _maxH;
    private readonly int _maxV;
    private readonly int _mcusX;
    private readonly int _mcusY;
    private readonly int _mcuRows;
    private readonly bool _fast;
    private readonly int _channels;
    private byte[] _rowBuffer;
    private byte[] _sampleBuffer;
    private bool _warned;

    internal JpegDecoder(JpegHeader header, ReaderOptions options, Raster<TPixel> raster, bool fastUpsampling)
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
        _mcuRows = 8 * _maxV;
        _channels = single ? 1 : 3;
        _components = new Component[header.Components.Length];
        for (int i = 0; i < _components.Length; i++)
        {
            JpegFrameComponent c = header.Components[i];
            int h = single ? 1 : c.H;
            int v = single ? 1 : c.V;
            ushort[] quant = header.QuantTables[c.QuantTable]
                ?? throw new CorruptDataException(JpegFormat.Info.Name, -1, $"Component {c.Id} uses quantization table {c.QuantTable}, which is missing.");
            _components[i] = new Component(
                h, v, Natural(quant), _mcusX * h * 8, v * 8,
                (int)(((long)header.Width * h) + _maxH - 1) / _maxH, (int)(((long)header.Height * v) + _maxV - 1) / _maxV);
        }

        _rowBuffer = ArrayPool<byte>.Shared.Rent(header.Width * Math.Max(_channels, _components.Length));
        _sampleBuffer = ArrayPool<byte>.Shared.Rent(header.Width * _components.Length);
    }

    /// <summary>Decodes the scan whose header has just been read, filling the raster.</summary>
    internal void DecodeScan(EgamiInput input, JpegScan scan)
    {
        if (scan.Components.Length != _components.Length)
        {
            throw new UnsupportedFormatException("Baseline JPEG with one scan per component (non-interleaved) is not supported yet.");
        }

        JpegHuffmanTable[] dc = new JpegHuffmanTable[scan.Components.Length];
        JpegHuffmanTable[] ac = new JpegHuffmanTable[scan.Components.Length];
        Component[] order = new Component[scan.Components.Length];
        for (int i = 0; i < scan.Components.Length; i++)
        {
            JpegScanComponent sc = scan.Components[i];
            order[i] = _components[sc.ComponentIndex];
            dc[i] = new JpegHuffmanTable(_header.DcTables[sc.DcTable] ?? throw Missing("DC", sc.DcTable));
            ac[i] = new JpegHuffmanTable(_header.AcTables[sc.AcTable] ?? throw Missing("AC", sc.AcTable));
        }

        using JpegBitReader reader = new(input);
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
                    if (!reader.Restart(nextRestart))
                    {
                        Problem("A restart marker is missing or out of order; the data was resynchronized at the next one.");
                        // An out-of-order restart marker was consumed and decoding resumes; any other marker ends the data.
                        ended = reader.Marker != 0;
                    }

                    nextRestart = reader.LastRestart >= 0 ? reader.LastRestart + 1 : nextRestart + 1;
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

                            int x = ((mx * c.H) + h) * 8;
                            JpegIdct.Transform(block, c.Quant, strip[((v * 8 * c.StripWidth) + x)..], c.StripWidth);
                        }
                    }
                }

                // Running into a restart marker early only loses the rest of this interval; any other marker or the end of the
                // file ends the data.
                if (!ended && reader.PastEnd && !reader.AtRestartMarker)
                {
                    ended = true;
                    Problem("The image data ends early. The rest of the image is gray.");
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
        int last = Math.Min(_header.Height, first + _mcuRows);
        int width = _header.Width;
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
        int hr = _maxH / c.H;
        int vr = _maxV / c.V;
        int width = _header.Width;
        int row = y / vr;
        ReadOnlySpan<byte> cur = c.Row(row);
        if (hr == 1 && vr == 1)
        {
            cur[..width].CopyTo(output);
            return;
        }

        if (_fast || (hr != 1 && hr != 2) || (vr != 1 && vr != 2) || hr * c.H != _maxH || vr * c.V != _maxV)
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

    /// <summary>One component's sampling, quantization table and the ring of three MCU-row strips of samples.</summary>
    private sealed class Component(int h, int v, ushort[] quant, int stripWidth, int stripHeight, int width, int height) : IDisposable
    {
        private byte[] _strips = ArrayPool<byte>.Shared.Rent(3 * stripWidth * stripHeight);

        internal int H => h;

        internal int V => v;

        internal ushort[] Quant => quant;

        internal int StripWidth => stripWidth;

        /// <summary>Gets the component's real width in samples, before MCU padding.</summary>
        internal int Width => width;

        /// <summary>Gets the component's real height in samples, before MCU padding.</summary>
        internal int Height => height;

        /// <summary>Gets the strip that holds MCU row <paramref name="my"/>.</summary>
        internal Span<byte> Strip(int my)
        {
            return _strips.AsSpan(my % 3 * stripWidth * stripHeight, stripWidth * stripHeight);
        }

        /// <summary>Gets sample row <paramref name="row"/>, which must be in one of the three strips held.</summary>
        internal ReadOnlySpan<byte> Row(int row)
        {
            return Strip(row / stripHeight).Slice(row % stripHeight * stripWidth, stripWidth);
        }

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(Interlocked.Exchange(ref _strips, null!));
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
