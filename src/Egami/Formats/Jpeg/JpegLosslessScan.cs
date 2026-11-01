using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Formats.Jpeg;

/// <summary>
/// The coding half of the lossless JPEG writer (T.81 annex H), shared by the pixel path and the sample-row entry point: the
/// frame header, then, once every row of samples has come, the predictor choice, the differences, the Huffman tables made for
/// them (annex K.2) or the arithmetic coder, the scan and EOI. The rows are interleaved by component and rented from the shared
/// pool; disposing returns them. The bit writer is the caller's.
/// </summary>
internal sealed class JpegLosslessScan : IDisposable
{
    /// <summary>The most rows the automatic predictor choice looks at, spread through the image.</summary>
    private const int SampledRows = 64;

    private readonly JpegBitWriter _writer;
    private readonly int _width;
    private readonly int _height;
    private readonly int _count;
    private readonly int _precision;
    private readonly int _pointTransform;
    private readonly bool _arithmetic;
    private readonly byte[] _ids;

    internal JpegLosslessScan(JpegBitWriter writer, int width, int height, int count, int precision, int pointTransform, bool arithmetic, ReadOnlySpan<byte> ids)
    {
        _writer = writer;
        _width = width;
        _height = height;
        _count = count;
        _precision = precision;
        _pointTransform = pointTransform;
        _arithmetic = arithmetic;
        _ids = ids.ToArray();
        Rows = new ushort[height][];
    }

    /// <summary>Gets each row's samples, interleaved by component, set by the caller from the shared pool.</summary>
    internal ushort[][] Rows { get; }

    /// <summary>Gets the samples in a row.</summary>
    internal int Stride => _width * _count;

    public void Dispose()
    {
        ushort[][] rows = Rows;
        for (int y = 0; y < rows.Length; y++)
        {
            if (rows[y] is ushort[] row)
            {
                ArrayPool<ushort>.Shared.Return(row);
                rows[y] = null!;
            }
        }
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
    internal void WriteFrame()
    {
        byte[] sof = new byte[6 + (3 * _count)];
        sof[0] = (byte)_precision;
        BinaryPrimitives.WriteUInt16BigEndian(sof.AsSpan(1), (ushort)_height);
        BinaryPrimitives.WriteUInt16BigEndian(sof.AsSpan(3), (ushort)_width);
        sof[5] = (byte)_count;
        for (int i = 0; i < _count; i++)
        {
            sof[6 + (3 * i)] = _ids[i];
            sof[7 + (3 * i)] = 0x11;
        }

        JpegWriter.Segment(_writer, _arithmetic ? JpegMarker.Sof11 : JpegMarker.Sof3, sof);
    }

    /// <summary>
    /// Codes every row, which must all have come: with <paramref name="predictor"/>, or the one that codes smallest when it is
    /// <see langword="null"/>, and a restart interval of <paramref name="restartInterval"/> samples rounded up to whole rows. Ends
    /// with EOI and flushes the writer.
    /// </summary>
    internal void Complete(int? predictor, int restartInterval)
    {
        int chosen = predictor ?? ChoosePredictor();
        int restart = RestartInterval(restartInterval, _width);
        int restartRows = restart / _width;
        long[] counts = new long[_count * 17];
        Differences(chosen, restartRows, counts);
        if (_arithmetic)
        {
            WriteScanHeader(chosen, restart);
            JpegArithmeticEncoder coder = new(_writer);
            int stride = Stride;
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

                coder.EncodeDifferences(_count, Rows[y].AsSpan(0, stride), reset ? default : Rows[y - 1].AsSpan(0, stride));
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

            WriteScanHeader(chosen, restart);
            EmitHuffman(tables, restartRows);
        }

        _writer.Align();
        _writer.Marker(JpegMarker.Eoi);
        _writer.Flush();
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
            sos[1 + (2 * i)] = _ids[i];
            sos[2 + (2 * i)] = (byte)(i << 4);
        }

        sos[^3] = (byte)predictor;
        sos[^1] = (byte)_pointTransform;
        JpegWriter.Segment(_writer, JpegMarker.Sos, sos);
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
            ushort[] row = Rows[y];
            ushort[] above = Rows[y - 1];
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
                Span<ushort> row = Rows[y].AsSpan(0, stride);
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

            ReadOnlySpan<ushort> row = Rows[y].AsSpan(0, stride);
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
