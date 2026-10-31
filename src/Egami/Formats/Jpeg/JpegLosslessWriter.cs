using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Egami.Pixels;

namespace Egami.Formats.Jpeg;

/// <summary>
/// Writes lossless JPEG (T.81 annex H): SOF3 with Huffman tables made for the image (annex K.2), or SOF11 with the arithmetic
/// coder and the two-dimensional contexts of H.1.2.3. The samples are the pixels' own, gray, RGB or CMYK (inverted, as Adobe
/// files hold it), at 2 to 16 bits with no subsampling, all interleaved in one scan. They are held until every row has come, so
/// the predictor can be chosen and the Huffman symbols counted, and then coded.
/// </summary>
internal sealed class JpegLosslessWriter<TPixel> : RowEncoder<TPixel>
    where TPixel : unmanaged, IPixelType<TPixel>
{
    /// <summary>The most rows the automatic predictor choice looks at, spread through the image.</summary>
    private const int SampledRows = 64;

    private readonly JpegBitWriter _writer;
    private readonly JpegWriterOptions _options;
    private readonly Kind _kind;
    private readonly int _width;
    private readonly int _height;
    private readonly int _count;
    private readonly int _precision;
    private readonly int _pointTransform;
    private readonly ushort[][] _rows;
    private readonly ushort[] _wide;
    private readonly Rgb16[]? _composited;
    private readonly byte[]? _narrow;
    private int _rowsIn;

    internal JpegLosslessWriter(Stream output, EncodeStart<TPixel> start, JpegWriterOptions jpeg)
    {
        JpegLosslessOptions lossless = jpeg.Lossless!;
        if (jpeg.Progressive)
        {
            throw new ArgumentException("JPEG: a lossless file has no progressive form (that would be hierarchical JPEG); leave Progressive off.", nameof(jpeg));
        }

        _kind = jpeg.ColorType switch
        {
            JpegColorType.Ycck => throw new ArgumentException("JPEG: a lossless file is written as gray, RGB or CMYK, never YCCK.", nameof(jpeg)),
            JpegColorType.Gray => Kind.Gray,
            JpegColorType.Cmyk => Kind.Cmyk,
            _ => TPixel.Layout.ChannelOrder switch
            {
                PixelChannelOrder.Gray or PixelChannelOrder.GrayAlpha => Kind.Gray,
                PixelChannelOrder.Cmyk => Kind.Cmyk,
                _ => Kind.Rgb,
            },
        };

        int source = TPixel.Layout.ChannelType == PixelChannelType.Unorm8 ? 8 : 16;
        _precision = lossless.Precision ?? source;
        _pointTransform = lossless.PointTransform;
        if (_pointTransform >= _precision)
        {
            throw new ArgumentException($"JPEG: the point transform ({_pointTransform}) must be below the precision ({_precision} bits).", nameof(jpeg));
        }

        _options = jpeg;
        _width = start.Width;
        _height = start.Height;
        _count = _kind switch { Kind.Gray => 1, Kind.Rgb => 3, _ => 4 };
        long held = (long)_width * _height * _count * sizeof(ushort);
        if (held > start.Context.Limits.MaxTotalBytes)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), start.Context.Limits.MaxTotalBytes, held);
        }

        SaveKeeps alpha = jpeg.Background is null ? SaveKeeps.None : SaveKeeps.Alpha;
        SaveKeeps cmyk = _kind == Kind.Cmyk ? SaveKeeps.Cmyk : SaveKeeps.None;
        SaveLosses.Report(start, "JPEG", alpha | cmyk | SaveKeeps.Wide | SaveKeeps.Exif | SaveKeeps.Xmp | SaveKeeps.Iptc | SaveKeeps.Icc | SaveKeeps.Comment | SaveKeeps.Resolution | SaveKeeps.GainMap);
        if (_precision < source)
        {
            SaveLosses.Report(start.Context, "Save.DepthReduced", $"Lossless JPEG was written at {_precision} bits; the {source}-bit samples were rounded to it.");
        }

        if (_pointTransform > 0)
        {
            SaveLosses.Report(start.Context, "Save.DepthReduced", $"Lossless JPEG with point transform {_pointTransform} drops the low {_pointTransform} bits of every sample; the file is not lossless.");
        }

        if (!jpeg.StripMetadata && jpeg.KeepGainMap && start.Metadata.GainMap is not null)
        {
            SaveLosses.Report(start.Context, "Save.GainMapDropped", "A lossless JPEG carries no gain map, since no HDR viewer decodes its base image; the image was written without it.");
        }

        _rows = new ushort[_height][];
        _wide = ArrayPool<ushort>.Shared.Rent(_width * 4);
        _composited = jpeg.Background is null ? null : new Rgb16[_width];

        // 8-bit pixels written at 8 bits as their own components convert straight to bytes, which is the same as the 16-bit
        // path's widen and round back.
        Kind natural = TPixel.Layout.ChannelOrder switch
        {
            PixelChannelOrder.Gray or PixelChannelOrder.GrayAlpha => Kind.Gray,
            PixelChannelOrder.Cmyk => Kind.Cmyk,
            _ => Kind.Rgb,
        };
        _narrow = source == 8 && _precision == 8 && jpeg.Background is null && _kind == natural ? ArrayPool<byte>.Shared.Rent(_width * _count) : null;
        _writer = new JpegBitWriter(output);
        _writer.Marker(JpegMarker.Soi);
        if (_kind == Kind.Gray)
        {
            JpegWriter.WriteJfif(_writer, jpeg.StripMetadata ? null : start.Metadata.Resolution);
        }
        else
        {
            // Adobe transform 0: the components are RGB or CMYK as they are, not YCbCr or YCCK.
            JpegWriter.WriteAdobe(_writer, 0);
        }

        if (!jpeg.StripMetadata)
        {
            JpegWriter.WriteMetadata(_writer, start.Metadata, start.Options.ProfileToWrite(start.Metadata), start.Context);
        }

        WriteFrame();
    }

    /// <summary>Which components the file holds.</summary>
    private enum Kind
    {
        Gray,
        Rgb,
        Cmyk,
    }

    /// <summary>The component ids: 1 for gray, R, G, B and C, M, Y, K as libjpeg names them.</summary>
    private ReadOnlySpan<byte> Ids => _kind switch { Kind.Gray => [1], Kind.Rgb => "RGB"u8, _ => "CMYK"u8 };

    public override void WriteRow(ReadOnlySpan<TPixel> row)
    {
        if (_rowsIn >= _height)
        {
            throw new InvalidOperationException("Every row of the image has been written.");
        }

        ushort[] samples = ArrayPool<ushort>.Shared.Rent(_width * _count);
        _rows[_rowsIn++] = samples;
        Samples(row, samples.AsSpan(0, _width * _count));
    }

    public override void Complete()
    {
        if (_rowsIn != _height)
        {
            throw new InvalidOperationException($"The image has {_height} rows; {_rowsIn} were written.");
        }

        int predictor = _options.Lossless!.Predictor ?? ChoosePredictor();
        int restart = RestartInterval(_options.RestartInterval, _width);
        int restartRows = restart / _width;
        long[] counts = new long[_count * 17];
        Differences(predictor, restartRows, counts);
        if (_options.Arithmetic)
        {
            WriteScanHeader(predictor, restart);
            JpegArithmeticEncoder coder = new(_writer);
            for (int y = 0; y < _height; y++)
            {
                bool reset = y == 0 || (restartRows > 0 && y % restartRows == 0);
                if (y > 0 && reset)
                {
                    coder.Finish();
                    _writer.Align();
                    _writer.Marker((byte)(JpegMarker.Rst0 + (((y / restartRows) - 1) & 7)));
                    coder.Restart();
                }

                coder.EncodeDifferences(_count, _rows[y].AsSpan(0, _width * _count), reset ? default : _rows[y - 1].AsSpan(0, _width * _count));
            }

            coder.Finish();
        }
        else
        {
            JpegHuffmanCodes[] tables = new JpegHuffmanCodes[_count];
            Span<long> frequencies = stackalloc long[256];
            for (int i = 0; i < _count; i++)
            {
                frequencies.Clear();
                counts.AsSpan(i * 17, 17).CopyTo(frequencies);
                tables[i] = JpegHuffmanCodes.Optimal(frequencies);
                byte[] dht = new byte[17 + tables[i].Symbols.Length];
                dht[0] = (byte)i;
                tables[i].Counts.CopyTo(dht, 1);
                tables[i].Symbols.CopyTo(dht, 17);
                JpegWriter.Segment(_writer, JpegMarker.Dht, dht);
            }

            WriteScanHeader(predictor, restart);
            EmitHuffman(tables, restartRows);
        }

        _writer.Align();
        _writer.Marker(JpegMarker.Eoi);
        _writer.Flush();
    }

    protected override void Dispose(bool disposing)
    {
        for (int y = 0; y < _rows.Length; y++)
        {
            if (_rows[y] is ushort[] row)
            {
                ArrayPool<ushort>.Shared.Return(row);
                _rows[y] = null!;
            }
        }

        ArrayPool<ushort>.Shared.Return(_wide);
        if (_narrow is not null)
        {
            ArrayPool<byte>.Shared.Return(_narrow);
        }
        _writer.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>
    /// Returns the restart interval to write: T.81 asks a lossless file's interval to be a whole number of MCU rows (here, of
    /// pixel rows), so the one asked for is rounded up to whole rows, no more than fit the 16-bit field.
    /// </summary>
    internal static int RestartInterval(int asked, int width)
    {
        if (asked == 0)
        {
            return 0;
        }

        int rows = Math.Min((asked + width - 1) / width, ushort.MaxValue / width);
        return rows * width;
    }

    /// <summary>The frame header: SOF3, or SOF11 when arithmetic-coded; every component sampled 1×1, with no quantization table.</summary>
    private void WriteFrame()
    {
        byte[] sof = new byte[6 + (3 * _count)];
        sof[0] = (byte)_precision;
        BinaryPrimitives.WriteUInt16BigEndian(sof.AsSpan(1), (ushort)_height);
        BinaryPrimitives.WriteUInt16BigEndian(sof.AsSpan(3), (ushort)_width);
        sof[5] = (byte)_count;
        for (int i = 0; i < _count; i++)
        {
            sof[6 + (3 * i)] = Ids[i];
            sof[7 + (3 * i)] = 0x11;
        }

        JpegWriter.Segment(_writer, _options.Arithmetic ? JpegMarker.Sof11 : JpegMarker.Sof3, sof);
    }

    /// <summary>DRI, when there is a restart interval, and SOS: the predictor in Ss and the point transform in Al (T.81 H.2.1).</summary>
    private void WriteScanHeader(int predictor, int restart)
    {
        if (restart > 0)
        {
            Span<byte> dri = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(dri, (ushort)restart);
            JpegWriter.Segment(_writer, JpegMarker.Dri, dri);
        }

        byte[] sos = new byte[1 + (2 * _count) + 3];
        sos[0] = (byte)_count;
        for (int i = 0; i < _count; i++)
        {
            sos[1 + (2 * i)] = Ids[i];
            sos[2 + (2 * i)] = (byte)(i << 4);
        }

        sos[^3] = (byte)predictor;
        sos[^1] = (byte)_pointTransform;
        JpegWriter.Segment(_writer, JpegMarker.Sos, sos);
    }

    /// <summary>
    /// Converts one row of pixels to the file's samples, interleaved: composited onto the background when there is one, narrowed
    /// to the precision with rounding, CMYK inverted, then shifted down by the point transform.
    /// </summary>
    private void Samples(ReadOnlySpan<TPixel> row, Span<ushort> samples)
    {
        if (_narrow is byte[] narrow)
        {
            Span<byte> bytes = narrow.AsSpan(0, samples.Length);
            switch (_kind)
            {
                case Kind.Gray:
                    PixelConvert.Convert(row, MemoryMarshal.Cast<byte, Gray8>(bytes));
                    break;
                case Kind.Rgb:
                    PixelConvert.Convert(row, MemoryMarshal.Cast<byte, Rgb8>(bytes));
                    break;
                default:
                    PixelConvert.Convert(row, MemoryMarshal.Cast<byte, Cmyk8>(bytes));
                    break;
            }

            int pt = _pointTransform;
            int flip = _kind == Kind.Cmyk ? 0xFF : 0;
            for (int i = 0; i < bytes.Length; i++)
            {
                samples[i] = (ushort)((bytes[i] ^ flip) >> pt);
            }

            return;
        }

        Span<ushort> wide = _wide.AsSpan(0, _width * _count);
        if (_options.Background is Rgba8 background)
        {
            Span<Rgba16> rgba = MemoryMarshal.Cast<ushort, Rgba16>(_wide.AsSpan(0, _width * 4));
            PixelConvert.Convert(row, rgba);
            int br = background.R * 257, bg = background.G * 257, bb = background.B * 257;
            Rgb16[] rgb = _composited!;
            for (int x = 0; x < _width; x++)
            {
                Rgba16 p = rgba[x];
                long a = p.A, rest = 65535 - p.A;
                rgb[x] = new Rgb16(
                    (ushort)(((p.R * a) + (br * rest) + 32767) / 65535),
                    (ushort)(((p.G * a) + (bg * rest) + 32767) / 65535),
                    (ushort)(((p.B * a) + (bb * rest) + 32767) / 65535));
            }

            Convert<Rgb16>(rgb, wide);
        }
        else
        {
            Convert(row, wide);
        }

        int max = (1 << _precision) - 1;
        int shift = _pointTransform;
        bool invert = _kind == Kind.Cmyk;
        for (int i = 0; i < samples.Length; i++)
        {
            int value = _precision == 16 ? wide[i] : (int)(((wide[i] * (long)max) + 32767) / 65535);
            samples[i] = (ushort)((invert ? max - value : value) >> shift);
        }
    }

    /// <summary>Converts pixels to the file's components at 16 bits.</summary>
    private void Convert<TFrom>(ReadOnlySpan<TFrom> row, Span<ushort> wide)
        where TFrom : unmanaged, IPixelType<TFrom>
    {
        switch (_kind)
        {
            case Kind.Gray:
                PixelConvert.Convert(row, MemoryMarshal.Cast<ushort, Gray16>(wide));
                break;
            case Kind.Rgb:
                PixelConvert.Convert(row, MemoryMarshal.Cast<ushort, Rgb16>(wide));
                break;
            default:
                PixelConvert.Convert(row, MemoryMarshal.Cast<ushort, Cmyk16>(wide));
                break;
        }
    }

    /// <summary>
    /// Picks the predictor that codes the image smallest: on up to <see cref="SampledRows"/> rows spread through the image, each
    /// predictor's differences are sorted into their categories, and the cost is the categories' entropy plus their extra bits.
    /// The first of equal costs wins.
    /// </summary>
    private int ChoosePredictor()
    {
        if (_height < 2 || _width < 2)
        {
            return 1;
        }

        int step = Math.Max(1, (_height - 1) / SampledRows);
        Span<long> histogram = stackalloc long[7 * 17];
        histogram.Clear();
        Span<long> extra = stackalloc long[7];
        extra.Clear();
        int n = _count;
        for (int y = 1; y < _height; y += step)
        {
            ushort[] row = _rows[y];
            ushort[] above = _rows[y - 1];
            for (int j = n; j < _width * n; j++)
            {
                int a = row[j - n], b = above[j], c = above[j - n], s = row[j];
                for (int p = 1; p <= 7; p++)
                {
                    int category = Category(Difference(s, Predict(p, a, b, c)));
                    histogram[((p - 1) * 17) + category]++;
                    extra[p - 1] += category == 16 ? 0 : category;
                }
            }
        }

        int best = 1;
        double bestCost = double.MaxValue;
        for (int p = 1; p <= 7; p++)
        {
            Span<long> counts = histogram.Slice((p - 1) * 17, 17);
            long total = 0;
            foreach (long count in counts)
            {
                total += count;
            }

            double cost = extra[p - 1];
            foreach (long count in counts)
            {
                if (count > 0)
                {
                    cost += count * Math.Log2((double)total / count);
                }
            }

            if (cost < bestCost)
            {
                bestCost = cost;
                best = p;
            }
        }

        return best;
    }

    /// <summary>
    /// Replaces every row's samples with their differences from the prediction (T.81 H.1.2.1), modulo 2^16, counting each
    /// component's categories: the first line of the image and of each restart interval predicted from the left, its first
    /// sample from 2^(P - Pt - 1), the first column of other lines from above. The interval is a whole number of rows.
    /// </summary>
    private void Differences(int predictor, int restartRows, long[] counts)
    {
        switch (predictor)
        {
            case 1: Differences<Left>(restartRows, counts); break;
            case 2: Differences<Above>(restartRows, counts); break;
            case 3: Differences<AboveLeft>(restartRows, counts); break;
            case 4: Differences<Plane>(restartRows, counts); break;
            case 5: Differences<LeftGradient>(restartRows, counts); break;
            case 6: Differences<AboveGradient>(restartRows, counts); break;
            default: Differences<Average>(restartRows, counts); break;
        }
    }

    private void Differences<TPredictor>(int restartRows, long[] counts)
        where TPredictor : struct, IPredictor
    {
        int n = _count;
        int stride = _width * n;
        int initial = 1 << (_precision - _pointTransform - 1);
        ushort[] current = ArrayPool<ushort>.Shared.Rent(stride);
        ushort[] above = ArrayPool<ushort>.Shared.Rent(stride);
        try
        {
            for (int y = 0; y < _height; y++)
            {
                Span<ushort> row = _rows[y].AsSpan(0, stride);
                row.CopyTo(current);
                ReadOnlySpan<ushort> s = current.AsSpan(0, stride);
                ReadOnlySpan<ushort> up = above.AsSpan(0, stride);
                bool reset = y == 0 || (restartRows > 0 && y % restartRows == 0);
                for (int i = 0; i < n; i++)
                {
                    row[i] = (ushort)(s[i] - (reset ? initial : up[i]));
                }

                if (reset)
                {
                    for (int j = n; j < stride; j++)
                    {
                        row[j] = (ushort)(s[j] - s[j - n]);
                    }
                }
                else
                {
                    for (int j = n; j < stride; j++)
                    {
                        row[j] = (ushort)(s[j] - TPredictor.Predict(s[j - n], up[j], up[j - n]));
                    }
                }

                Count(row, n, counts);
                (current, above) = (above, current);
            }
        }
        finally
        {
            ArrayPool<ushort>.Shared.Return(current);
            ArrayPool<ushort>.Shared.Return(above);
        }
    }

    /// <summary>Adds a row of differences to each component's category counts.</summary>
    private static void Count(ReadOnlySpan<ushort> row, int n, long[] counts)
    {
        ref long first = ref MemoryMarshal.GetArrayDataReference(counts);
        for (int j = 0, i = 0; j < row.Length; j++)
        {
            Unsafe.Add(ref first, (i * 17) + Category(Signed(row[j])))++;
            if (++i == n)
            {
                i = 0;
            }
        }
    }

    /// <summary>
    /// Writes every difference as its category's code and, below 16, that many low bits (negative values less one), the bit
    /// buffer held in locals, with a restart marker before each interval but the first.
    /// </summary>
    private void EmitHuffman(JpegHuffmanCodes[] tables, int restartRows)
    {
        int n = _count;
        int stride = _width * n;
        int[][] entries = [.. tables.Select(t => t.Entries)];
        JpegBitWriter writer = _writer;
        writer.Load(out ulong bits, out int count);
        for (int y = 0; y < _height; y++)
        {
            if (y > 0 && restartRows > 0 && y % restartRows == 0)
            {
                writer.Store(bits, count);
                writer.Align();
                writer.Marker((byte)(JpegMarker.Rst0 + (((y / restartRows) - 1) & 7)));
                writer.Load(out bits, out count);
            }

            ReadOnlySpan<ushort> row = _rows[y].AsSpan(0, stride);
            for (int j = 0, i = 0; j < row.Length; j++)
            {
                int difference = Signed(row[j]);
                int category = Category(difference);
                int entry = entries[i][category];
                int size = category & 15;
                int length = (entry >> 16) + size;
                uint payload = ((uint)(entry & 0xFFFF) << size) | ((uint)(difference - (int)((uint)difference >> 31)) & ((1u << size) - 1));
                bits = (bits << length) | payload;
                count += length;
                if (count >= 32)
                {
                    writer.Drain32(ref bits, ref count);
                }

                if (++i == n)
                {
                    i = 0;
                }
            }
        }

        writer.Store(bits, count);
    }

    /// <summary>A stored difference as -32767 to 32768: 0x8000 is 32768, the only value of category 16 (T.81 H.1.2.2).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Signed(ushort stored) => stored == 0x8000 ? 32768 : (short)stored;

    /// <summary>The predictors of T.81 table H.1, from the samples to the left (a), above (b) and above left (c).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Predict(int predictor, int a, int b, int c) => predictor switch
    {
        1 => a,
        2 => b,
        3 => c,
        4 => a + b - c,
        5 => a + ((b - c) >> 1),
        6 => b + ((a - c) >> 1),
        _ => (a + b) >> 1,
    };

    /// <summary>The difference modulo 2^16, as -32767 to 32768 (T.81 H.1.2.2).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Difference(int sample, int prediction)
    {
        int difference = (sample - prediction) & 0xFFFF;
        return difference > 0x8000 ? difference - 0x10000 : difference;
    }

    /// <summary>The difference's category, its magnitude in bits (table H.2); 16 is 32768 alone.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Category(int difference)
    {
        return 32 - BitOperations.LeadingZeroCount((uint)Math.Abs(difference));
    }

    /// <summary>A predictor of T.81 table H.1, as a type, so each gets its own loop.</summary>
    private interface IPredictor
    {
        static abstract int Predict(int a, int b, int c);
    }

    private readonly struct Left : IPredictor
    {
        public static int Predict(int a, int b, int c) => a;
    }

    private readonly struct Above : IPredictor
    {
        public static int Predict(int a, int b, int c) => b;
    }

    private readonly struct AboveLeft : IPredictor
    {
        public static int Predict(int a, int b, int c) => c;
    }

    private readonly struct Plane : IPredictor
    {
        public static int Predict(int a, int b, int c) => a + b - c;
    }

    private readonly struct LeftGradient : IPredictor
    {
        public static int Predict(int a, int b, int c) => a + ((b - c) >> 1);
    }

    private readonly struct AboveGradient : IPredictor
    {
        public static int Predict(int a, int b, int c) => b + ((a - c) >> 1);
    }

    private readonly struct Average : IPredictor
    {
        public static int Predict(int a, int b, int c) => (a + b) >> 1;
    }
}
