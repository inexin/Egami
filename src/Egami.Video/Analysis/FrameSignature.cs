using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using Egami.Pixels;

namespace Egami.Video.Analysis;

/// <summary>
/// A frame boiled down for comparing with its neighbors: a 16 by 9 grid of mean Y, Cb and Cr and a 64-bin luma histogram, from
/// the decoded planes without converting to RGB. Luma runs from black level (0) to white level (1) whatever the bit depth and
/// range; chroma runs from 0 to 1 with neutral at about 0.5. The buffers are reused from frame to frame.
/// </summary>
internal sealed class FrameSignature
{
    public const int GridWidth = 16;
    public const int GridHeight = 9;
    public const int Cells = GridWidth * GridHeight;
    public const int Bins = 64;

    /// <summary>How many 8-bit rows a 16-bit column sum holds before it must be emptied into the cell sums (255 x 257 &lt; 65,536).</summary>
    private const int ColumnRows = 257;

    private readonly double[] _sums = new double[Cells];
    private readonly int[] _counts = new int[Cells];
    private ushort[] _columns = [];
    private byte[]? _binsOf8;
    private byte[]? _binsOf16;
    private int _binsDepth;
    private YuvRange _binsRange;

    /// <summary>Gets the mean luma of each cell, row by row.</summary>
    public double[] Y { get; } = new double[Cells];

    /// <summary>Gets the mean Cb of each cell.</summary>
    public double[] Cb { get; } = new double[Cells];

    /// <summary>Gets the mean Cr of each cell.</summary>
    public double[] Cr { get; } = new double[Cells];

    /// <summary>Gets the luma histogram, summing to 1.</summary>
    public double[] Histogram { get; } = new double[Bins];

    /// <summary>Gets the frame's mean luma.</summary>
    public double Luma { get; private set; }

    /// <summary>Measures a frame into this signature.</summary>
    /// <param name="raster">The frame's planes.</param>
    /// <param name="histogram">Whether to fill <see cref="Histogram"/> too; only the histogram method reads it.</param>
    public void Measure(YuvRaster raster, bool histogram = true)
    {
        int depth = raster.BitDepth;
        bool limited = raster.Color.Range == YuvRange.Limited;
        double max = (1 << depth) - 1;
        double black = limited ? 16 << (depth - 8) : 0;
        double white = limited ? 235 << (depth - 8) : max;
        if (raster is YuvRaster<byte> bytes)
        {
            Grid(bytes.Y, Y, black, white);
            Chroma(bytes.U, Cb, max);
            Chroma(bytes.V, Cr, max);
            if (histogram)
            {
                Histogram8(bytes.Y, raster.Color.Range);
            }
        }
        else if (raster is YuvRaster<ushort> words)
        {
            Grid(words.Y, Y, black, white);
            Chroma(words.U, Cb, max);
            Chroma(words.V, Cr, max);
            if (histogram)
            {
                Histogram16(words.Y, depth, raster.Color.Range);
            }
        }
        else
        {
            throw new NotSupportedException($"Planes of {raster.GetType().Name} are not supported.");
        }

        double luma = 0;
        for (int i = 0; i < Cells; i++)
        {
            luma += Y[i];
        }

        Luma = luma / Cells;
    }

    /// <summary>Copies another signature into this one.</summary>
    public void CopyFrom(FrameSignature other)
    {
        other.Y.CopyTo(Y, 0);
        other.Cb.CopyTo(Cb, 0);
        other.Cr.CopyTo(Cr, 0);
        other.Histogram.CopyTo(Histogram, 0);
        Luma = other.Luma;
    }

    /// <summary>
    /// The content difference from another frame, from 0 to 1: the mean absolute difference of the luma grid plus half of each
    /// chroma grid's, over 2, so luma counts as much as both chroma planes together.
    /// </summary>
    public double ContentDifference(FrameSignature other)
    {
        double y = 0, cb = 0, cr = 0;
        for (int i = 0; i < Cells; i++)
        {
            y += Math.Abs(Y[i] - other.Y[i]);
            cb += Math.Abs(Cb[i] - other.Cb[i]);
            cr += Math.Abs(Cr[i] - other.Cr[i]);
        }

        return Math.Min(1, (y + (0.5 * cb) + (0.5 * cr)) / Cells / 2);
    }

    /// <summary>The chi-square distance between the luma histograms, from 0 (same) to 1 (no bin shared).</summary>
    public double HistogramDistance(FrameSignature other)
    {
        double sum = 0;
        for (int i = 0; i < Bins; i++)
        {
            double a = Histogram[i], b = other.Histogram[i];
            if (a + b > 0)
            {
                double d = a - b;
                sum += d * d / (a + b);
            }
        }

        return Math.Clamp(sum / 2, 0, 1);
    }

    private void Grid<T>(YuvPlane<T> plane, double[] grid, double black, double white)
        where T : unmanaged
    {
        Accumulate(plane);
        double scale = 1 / (white - black);
        for (int i = 0; i < Cells; i++)
        {
            grid[i] = _counts[i] > 0 ? ((_sums[i] / _counts[i]) - black) * scale : 0;
        }
    }

    private void Chroma<T>(YuvPlane<T>? plane, double[] grid, double max)
        where T : unmanaged
    {
        if (plane is null)
        {
            Array.Fill(grid, 0.5);
            return;
        }

        Accumulate(plane);
        for (int i = 0; i < Cells; i++)
        {
            grid[i] = _counts[i] > 0 ? _sums[i] / _counts[i] / max : 0.5;
        }
    }

    /// <summary>Sums the plane's samples per grid cell into <see cref="_sums"/> and counts them into <see cref="_counts"/>.</summary>
    private void Accumulate<T>(YuvPlane<T> plane)
        where T : unmanaged
    {
        Array.Clear(_sums);
        Array.Clear(_counts);
        int width = plane.Width, height = plane.Height;
        Span<int> edges = stackalloc int[GridWidth + 1];
        for (int c = 0; c <= GridWidth; c++)
        {
            edges[c] = c * width / GridWidth;
        }

        if (typeof(T) == typeof(byte))
        {
            AccumulateBytes((YuvPlane<byte>)(object)plane, edges);
            return;
        }

        for (int y = 0; y < height; y++)
        {
            int row = y * GridHeight / height * GridWidth;
            Span<T> samples = plane.Row(y);
            for (int c = 0; c < GridWidth; c++)
            {
                int from = edges[c], to = edges[c + 1];
                if (to > from)
                {
                    _sums[row + c] += typeof(T) == typeof(byte)
                        ? Sum(MemoryMarshal.Cast<T, byte>(samples)[from..to])
                        : Sum(MemoryMarshal.Cast<T, ushort>(samples)[from..to]);
                    _counts[row + c] += to - from;
                }
            }
        }
    }

    /// <summary>
    /// The 8-bit <see cref="Accumulate"/>: the rows of each band of cells are added column by column into 16-bit sums, eight
    /// columns at a time with vector adds, and each cell then sums its columns once per band. The sums are exact.
    /// </summary>
    private void AccumulateBytes(YuvPlane<byte> plane, ReadOnlySpan<int> edges)
    {
        int width = plane.Width, height = plane.Height;
        if (_columns.Length < width)
        {
            _columns = new ushort[width];
        }

        Span<ushort> columns = _columns.AsSpan(0, width);
        int band = 0, rows = 0;
        for (int y = 0; y < height; y++)
        {
            int next = y * GridHeight / height;
            if (next != band || rows == ColumnRows)
            {
                EmptyColumns(columns, band, rows, edges);
                band = next;
                rows = 0;
            }

            AddRow(plane.Row(y)[..width], columns);
            rows++;
        }

        EmptyColumns(columns, band, rows, edges);
    }

    /// <summary>Adds each column's sum to its cell in band <paramref name="band"/> and clears the columns.</summary>
    private void EmptyColumns(Span<ushort> columns, int band, int rows, ReadOnlySpan<int> edges)
    {
        if (rows == 0)
        {
            return;
        }

        int row = band * GridWidth;
        for (int c = 0; c < GridWidth; c++)
        {
            int from = edges[c], to = edges[c + 1];
            if (to > from)
            {
                _sums[row + c] += Sum(columns[from..to]);
                _counts[row + c] += (to - from) * rows;
            }
        }

        columns.Clear();
    }

    private static void AddRow(ReadOnlySpan<byte> row, Span<ushort> columns)
    {
        int x = 0;
        if (Vector256.IsHardwareAccelerated && row.Length >= Vector256<byte>.Count)
        {
            ref ushort sums = ref MemoryMarshal.GetReference(columns);
            for (; x <= row.Length - Vector256<byte>.Count; x += Vector256<byte>.Count)
            {
                (Vector256<ushort> lo, Vector256<ushort> hi) = Vector256.Widen(Vector256.Create(row.Slice(x, Vector256<byte>.Count)));
                (Vector256.LoadUnsafe(ref sums, (nuint)x) + lo).StoreUnsafe(ref sums, (nuint)x);
                (Vector256.LoadUnsafe(ref sums, (nuint)(x + Vector256<ushort>.Count)) + hi).StoreUnsafe(ref sums, (nuint)(x + Vector256<ushort>.Count));
            }
        }

        for (; x < row.Length; x++)
        {
            columns[x] += row[x];
        }
    }

    private void Histogram8(YuvPlane<byte> plane, YuvRange range)
    {
        if (_binsOf8 is null || _binsRange != range)
        {
            _binsOf8 = BinTable(8, range);
            _binsRange = range;
        }

        Array.Clear(Histogram);
        byte[] bins = _binsOf8;
        long total = 0;
        for (int y = 0; y < plane.Height; y += 2)
        {
            Span<byte> row = plane.Row(y);
            for (int x = 0; x < row.Length; x += 2)
            {
                Histogram[bins[row[x]]]++;
            }

            total += (row.Length + 1) / 2;
        }

        Normalize(total);
    }

    private void Histogram16(YuvPlane<ushort> plane, int depth, YuvRange range)
    {
        if (_binsOf16 is null || _binsDepth != depth || _binsRange != range)
        {
            _binsOf16 = BinTable(depth, range);
            _binsDepth = depth;
            _binsRange = range;
        }

        Array.Clear(Histogram);
        byte[] bins = _binsOf16;
        int top = bins.Length - 1;
        long total = 0;
        for (int y = 0; y < plane.Height; y += 2)
        {
            Span<ushort> row = plane.Row(y);
            for (int x = 0; x < row.Length; x += 2)
            {
                Histogram[bins[Math.Min((int)row[x], top)]]++;
            }

            total += (row.Length + 1) / 2;
        }

        Normalize(total);
    }

    private void Normalize(long total)
    {
        if (total == 0)
        {
            return;
        }

        double scale = 1.0 / total;
        for (int i = 0; i < Bins; i++)
        {
            Histogram[i] *= scale;
        }
    }

    /// <summary>Maps each sample value to its histogram bin, from black level to white level.</summary>
    private static byte[] BinTable(int depth, YuvRange range)
    {
        int size = 1 << depth;
        bool limited = range == YuvRange.Limited;
        double black = limited ? 16 << (depth - 8) : 0;
        double white = limited ? 235 << (depth - 8) : size - 1;
        byte[] table = new byte[size];
        for (int v = 0; v < size; v++)
        {
            double level = Math.Clamp((v - black) / (white - black), 0, 1);
            table[v] = (byte)Math.Min(Bins - 1, (int)(level * Bins));
        }

        return table;
    }

    private static long Sum(ReadOnlySpan<byte> samples)
    {
        long sum = 0;
        int i = 0;
        if (Vector256.IsHardwareAccelerated && samples.Length >= Vector256<byte>.Count)
        {
            Vector256<ulong> total = Vector256<ulong>.Zero;
            for (; i <= samples.Length - Vector256<byte>.Count; i += Vector256<byte>.Count)
            {
                Vector256<byte> v = Vector256.Create(samples.Slice(i, Vector256<byte>.Count));
                (Vector256<ushort> lo, Vector256<ushort> hi) = Vector256.Widen(v);
                Vector256<ushort> pairs = lo + hi;
                (Vector256<uint> a, Vector256<uint> b) = Vector256.Widen(pairs);
                (Vector256<ulong> c, Vector256<ulong> d) = Vector256.Widen(a + b);
                total += c + d;
            }

            sum = (long)Vector256.Sum(total);
        }
        else if (Vector128.IsHardwareAccelerated && samples.Length >= Vector128<byte>.Count)
        {
            Vector128<uint> total = Vector128<uint>.Zero;
            for (; i <= samples.Length - Vector128<byte>.Count; i += Vector128<byte>.Count)
            {
                Vector128<byte> v = Vector128.Create(samples.Slice(i, Vector128<byte>.Count));
                (Vector128<ushort> lo, Vector128<ushort> hi) = Vector128.Widen(v);
                (Vector128<uint> a, Vector128<uint> b) = Vector128.Widen(lo + hi);
                total += a + b;
            }

            sum = Vector128.Sum(total);
        }

        for (; i < samples.Length; i++)
        {
            sum += samples[i];
        }

        return sum;
    }

    private static long Sum(ReadOnlySpan<ushort> samples)
    {
        long sum = 0;
        int i = 0;
        if (Vector128.IsHardwareAccelerated && samples.Length >= Vector128<ushort>.Count)
        {
            Vector128<ulong> total = Vector128<ulong>.Zero;
            for (; i <= samples.Length - Vector128<ushort>.Count; i += Vector128<ushort>.Count)
            {
                Vector128<ushort> v = Vector128.Create(samples.Slice(i, Vector128<ushort>.Count));
                (Vector128<uint> a, Vector128<uint> b) = Vector128.Widen(v);
                (Vector128<ulong> c, Vector128<ulong> d) = Vector128.Widen(a + b);
                total += c + d;
            }

            sum = (long)Vector128.Sum(total);
        }

        for (; i < samples.Length; i++)
        {
            sum += samples[i];
        }

        return sum;
    }
}
