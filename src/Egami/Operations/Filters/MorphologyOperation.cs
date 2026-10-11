using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using Egami.Operations.Geometry;
using Egami.Pixels;

namespace Egami.Operations.Filters;

/// <summary>
/// <see cref="RasterOps.Morphology(MorphologyOp, StructuringElement, int)"/>: flat morphology per channel on stored values, edges
/// repeating the edge pixel. Erosion takes the minimum of f(p + c) and dilation the maximum of f(p − c) over the shape's cells c,
/// as ImageMagick does, so opening and closing are idempotent. Types whose channels are straight bytes work on their bytes; the
/// others on straight floats. Each pass runs row runs: every source row gets a sparse table of running extremes over 1, 2, 4, …
/// pixels, so a run of any length is the extreme of two lookups, and the cost grows with the shape's height, not its area. A
/// rectangle instead takes one horizontal run per row and then van Herk and Gil-Werman's running extreme down the columns.
/// </summary>
internal sealed class MorphologyOperation : IRasterOperation, INeedsNewPlane, Scopes.INeighbourhoodStep
{
    /// <summary>The most iterations a call takes.</summary>
    internal const int MaxIterations = 100;

    // Above this many bytes of cached tables per thread, rows are tabled again for each run instead.
    private const long TableBudget = 32L << 20;

    private readonly MorphologyOp _op;
    private readonly StructuringElement _shape;
    private readonly int _iterations;

    internal MorphologyOperation(MorphologyOp op, StructuringElement shape, int iterations)
    {
        _op = op;
        _shape = shape;
        _iterations = iterations;
    }

    public (int X, int Y)? Reach
    {
        get
        {
            (int x, int y) = _shape.Reach;
            int passes = _op is MorphologyOp.Dilate or MorphologyOp.Erode or MorphologyOp.Gradient ? _iterations : 2 * _iterations;
            return (x * passes, y * passes);
        }
    }

    public bool NeedsNewPlane(Size input) => true;

    public Size OutputSize(Size input) => input;

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        IPixelMemorySource memory = (IPixelMemorySource?)input.Memory ?? input.Context.Memory;
        PixelPlane<TPixel> source = input.Source;
        int width = source.Width, height = source.Height;
        if (StoredRows<TPixel>.Bytes)
        {
            int lanes = Unsafe.SizeOf<TPixel>();
            Planes<byte> planes = new(
                new PixelRows<TPixel>(source),
                new PixelRows<TPixel>(input.Destination),
                () => new PixelRows<TPixel>(PixelPlane.Allocate<TPixel>(width, height, memory)));
            using (planes)
            {
                Apply(planes, width * lanes, height, lanes, StoredRows<TPixel>.AlphaByte, input);
            }

            return;
        }

        // Straight floats, four a pixel: the image is loaded into a float plane and the result stored back.
        Func<FloatRows> allocate = () => new FloatRows(PixelPlane.Allocate<RgbaF32>(width, height, memory));
        using FloatRows loaded = allocate();
        using FloatRows result = allocate();
        input.ForEachBand((start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                StoredRows<TPixel>.Load(source.Row(y), loaded[y]);
            }
        });
        using (Planes<float> planes = new(loaded, result, allocate))
        {
            int alpha = TPixel.Layout.HasAlpha && TPixel.Layout.ChannelOrder != PixelChannelOrder.Cmyk ? 3 : -1;
            Apply(planes, width * 4, height, 4, alpha, input);
        }

        input.ForEachBand((start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                StoredRows<TPixel>.Store(result[y], input.Destination.Row(y));
            }
        });
    }

    /// <summary>Runs the operation from <c>planes.Source</c> into <c>planes.Destination</c>.</summary>
    private void Apply<T, TPixel>(Planes<T> planes, int length, int height, int lanes, int alpha, OperationInput<TPixel> input)
        where T : unmanaged, INumber<T>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Pass<T> dilate = new(_shape, max: true, length, height, lanes, input.Context.MaxParallelism, input.CancellationToken);
        Pass<T> erode = new(_shape, max: false, length, height, lanes, input.Context.MaxParallelism, input.CancellationToken);
        int n = _iterations;
        switch (_op)
        {
            case MorphologyOp.Dilate:
                planes.Chain(planes.Source, planes.Destination, [.. Enumerable.Repeat(dilate, n)]);
                break;
            case MorphologyOp.Erode:
                planes.Chain(planes.Source, planes.Destination, [.. Enumerable.Repeat(erode, n)]);
                break;
            case MorphologyOp.Open:
                planes.Chain(planes.Source, planes.Destination, [.. Enumerable.Repeat(erode, n), .. Enumerable.Repeat(dilate, n)]);
                break;
            case MorphologyOp.Close:
                planes.Chain(planes.Source, planes.Destination, [.. Enumerable.Repeat(dilate, n), .. Enumerable.Repeat(erode, n)]);
                break;
            case MorphologyOp.Gradient:
                {
                    Rows<T> dilated = planes.Temporary();
                    planes.Chain(planes.Source, dilated, [.. Enumerable.Repeat(dilate, n)]);
                    planes.Chain(planes.Source, planes.Destination, [.. Enumerable.Repeat(erode, n)]);
                    Difference(dilated, planes.Destination, planes.Destination, planes.Source, length, height, lanes, alpha, input);
                    break;
                }

            case MorphologyOp.TopHat:
                planes.Chain(planes.Source, planes.Destination, [.. Enumerable.Repeat(erode, n), .. Enumerable.Repeat(dilate, n)]);
                Difference(planes.Source, planes.Destination, planes.Destination, planes.Source, length, height, lanes, alpha, input);
                break;
            default:
                planes.Chain(planes.Source, planes.Destination, [.. Enumerable.Repeat(dilate, n), .. Enumerable.Repeat(erode, n)]);
                Difference(planes.Destination, planes.Source, planes.Destination, planes.Source, length, height, lanes, alpha, input);
                break;
        }
    }

    /// <summary><c>output = a − b</c>, never below 0, with the alpha lane taken from <paramref name="original"/>.</summary>
    private static void Difference<T, TPixel>(Rows<T> a, Rows<T> b, Rows<T> output, Rows<T> original, int length, int height, int lanes, int alpha, OperationInput<TPixel> input)
        where T : unmanaged, INumber<T>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        RowRunner.Run(height, length * Unsafe.SizeOf<T>(), input.Context.MaxParallelism, (start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                Span<T> o = output[y][..length];
                MorphologyKernels.Subtract(a[y][..length], b[y][..length], o);
                if (alpha >= 0)
                {
                    ReadOnlySpan<T> s = original[y];
                    for (int i = alpha; i < length; i += lanes)
                    {
                        o[i] = s[i];
                    }
                }
            }
        }, input.CancellationToken);
    }

    /// <summary>Rows of elements, by index.</summary>
    internal abstract class Rows<T> : IDisposable
        where T : unmanaged
    {
        internal abstract Span<T> this[int y] { get; }

        public void Dispose() => Release();

        /// <summary>Frees the rows' plane, if this owns one.</summary>
        protected virtual void Release()
        {
        }
    }

    private sealed class PixelRows<TPixel>(PixelPlane<TPixel> plane) : Rows<byte>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        internal override Span<byte> this[int y] => MemoryMarshal.AsBytes(plane.Row(y));

        protected override void Release() => plane.Dispose();
    }

    private sealed class FloatRows(PixelPlane<RgbaF32> plane) : Rows<float>
    {
        internal override Span<float> this[int y] => MemoryMarshal.Cast<RgbaF32, float>(plane.Row(y));

        protected override void Release() => plane.Dispose();
    }

    /// <summary>The source, the destination, and temporary planes made as passes need them and disposed with this.</summary>
    private sealed class Planes<T>(Rows<T> source, Rows<T> destination, Func<Rows<T>> allocate) : IDisposable
        where T : unmanaged, INumber<T>
    {
        private readonly List<Rows<T>> _temporaries = [];
        private readonly List<Rows<T>> _free = [];

        internal Rows<T> Source => source;

        internal Rows<T> Destination => destination;

        /// <summary>Returns a temporary plane kept until this is disposed.</summary>
        internal Rows<T> Temporary()
        {
            Rows<T> rows = allocate();
            _temporaries.Add(rows);
            return rows;
        }

        /// <summary>Runs passes one after another from <paramref name="from"/> into <paramref name="to"/>, through two reused planes.</summary>
        internal void Chain(Rows<T> from, Rows<T> to, Pass<T>[] passes)
        {
            Rows<T> current = from;
            for (int i = 0; i < passes.Length; i++)
            {
                Rows<T> next = i == passes.Length - 1 ? to : Spare(current);
                passes[i].Run(current, next);
                current = next;
            }
        }

        public void Dispose()
        {
            foreach (Rows<T> rows in _temporaries)
            {
                rows.Dispose();
            }

            foreach (Rows<T> rows in _free)
            {
                rows.Dispose();
            }
        }

        private Rows<T> Spare(Rows<T> current)
        {
            foreach (Rows<T> rows in _free)
            {
                if (!ReferenceEquals(rows, current))
                {
                    return rows;
                }
            }

            Rows<T> made = allocate();
            _free.Add(made);
            return made;
        }
    }

    /// <summary>One dilation or erosion by the shape, from one plane into another.</summary>
    internal sealed class Pass<T>
        where T : unmanaged, INumber<T>
    {
        private readonly bool _max;
        private readonly int[] _runs;
        private readonly bool _rectangle;
        private readonly int _length;
        private readonly int _height;
        private readonly int _lanes;
        private readonly int _pad;
        private readonly int _levels;
        private readonly int _top;
        private readonly int _rows;
        private readonly int _threads;
        private readonly CancellationToken _ct;

        internal Pass(StructuringElement shape, bool max, int length, int height, int lanes, int threads, CancellationToken ct)
        {
            _max = max;
            _length = length;
            _height = height;
            _lanes = lanes;
            _threads = threads;
            _ct = ct;
            _rectangle = shape.IsRectangle;

            // Dilation reads f(p − c): the shape turned half a circle about its origin.
            ReadOnlySpan<int> runs = shape.Runs;
            _runs = new int[runs.Length];
            for (int i = 0; i < runs.Length; i += 3)
            {
                (_runs[i], _runs[i + 1], _runs[i + 2]) = max ? (-runs[i], -runs[i + 2], -runs[i + 1]) : (runs[i], runs[i + 1], runs[i + 2]);
            }

            int widest = 1, top = int.MaxValue, bottom = int.MinValue;
            for (int i = 0; i < _runs.Length; i += 3)
            {
                widest = Math.Max(widest, _runs[i + 2] - _runs[i + 1] + 1);
                _pad = Math.Max(_pad, Math.Max(Math.Abs(_runs[i + 1]), Math.Abs(_runs[i + 2])));
                top = Math.Min(top, _runs[i]);
                bottom = Math.Max(bottom, _runs[i]);
            }

            _levels = BitOperations.Log2((uint)widest) + 1;
            _top = top;
            _rows = bottom - top + 1;
        }

        private int Padded => _length + (2 * _pad * _lanes);

        internal void Run(Rows<T> from, Rows<T> to)
        {
            if (_rectangle)
            {
                Rectangle(from, to);
            }
            else
            {
                RowRuns(from, to);
            }
        }

        /// <summary>
        /// Copies row <paramref name="v"/> (clamped) into the middle of level 0 of <paramref name="table"/>, repeating its end pixels
        /// into the padding, and builds the levels above: level j holds the extreme over 2^j pixels from each position.
        /// </summary>
        private void Table(Rows<T> from, int v, Span<T> table, int levels)
        {
            int padded = Padded, lanes = _lanes;
            Span<T> level = table[..padded];
            ReadOnlySpan<T> row = from[Math.Clamp(v, 0, _height - 1)][.._length];
            row.CopyTo(level[(_pad * lanes)..]);
            ReadOnlySpan<T> first = row[..lanes], last = row[^lanes..];
            for (int i = 0; i < _pad; i++)
            {
                first.CopyTo(level[(i * lanes)..]);
                last.CopyTo(level[((_pad * lanes) + _length + (i * lanes))..]);
            }

            for (int j = 1; j < levels; j++)
            {
                int shift = (1 << (j - 1)) * lanes, valid = padded - (((1 << j) - 1) * lanes);
                ReadOnlySpan<T> below = table.Slice((j - 1) * padded, padded);
                MorphologyKernels.Extreme(below[..valid], below.Slice(shift, valid), table.Slice(j * padded, valid), _max);
            }
        }

        /// <summary>The extreme over pixels [x0, x1] from each output pixel, from a row's table, into or onto <paramref name="output"/>.</summary>
        private void Query(ReadOnlySpan<T> table, int x0, int x1, Span<T> output, bool first)
        {
            int count = x1 - x0 + 1, j = BitOperations.Log2((uint)count), padded = Padded;
            ReadOnlySpan<T> level = table.Slice(j * padded, padded);
            ReadOnlySpan<T> a = level.Slice((_pad + x0) * _lanes, _length), b = level.Slice((_pad + x1 - (1 << j) + 1) * _lanes, _length);
            if (first)
            {
                MorphologyKernels.Extreme(a, b, output, _max);
            }
            else
            {
                MorphologyKernels.Extreme(a, b, output, output, _max);
            }
        }

        /// <summary>Any shape: per output row, one table query per run, from a ring of tabled source rows.</summary>
        private void RowRuns(Rows<T> from, Rows<T> to)
        {
            int padded = Padded, tableLength = padded * _levels;
            bool cached = (long)_rows * tableLength * Unsafe.SizeOf<T>() <= TableBudget;
            int slots = cached ? _rows : 1;
            Chunks.Run(_height, (long)_length * _height * Unsafe.SizeOf<T>() * _rows, _threads, (start, end) =>
            {
                T[] buffer = ArrayPool<T>.Shared.Rent(slots * tableLength);
                try
                {
                    Span<T> tables = buffer.AsSpan(0, slots * tableLength);
                    int next = start + _top;
                    for (int y = start; y < end; y++)
                    {
                        if ((y & 15) == 0)
                        {
                            _ct.ThrowIfCancellationRequested();
                        }

                        Span<T> output = to[y][.._length];
                        if (cached)
                        {
                            for (; next < y + _top + _rows; next++)
                            {
                                Table(from, next, tables.Slice(Slot(next) * tableLength, tableLength), _levels);
                            }
                        }

                        for (int i = 0; i < _runs.Length; i += 3)
                        {
                            Span<T> table = tables.Slice(cached ? Slot(y + _runs[i]) * tableLength : 0, tableLength);
                            if (!cached)
                            {
                                Table(from, y + _runs[i], table, BitOperations.Log2((uint)(_runs[i + 2] - _runs[i + 1] + 1)) + 1);
                            }

                            Query(table, _runs[i + 1], _runs[i + 2], output, i == 0);
                        }
                    }
                }
                finally
                {
                    ArrayPool<T>.Shared.Return(buffer);
                }
            }, _ct);
        }

        private int Slot(int v) => ((v % _rows) + _rows) % _rows;

        /// <summary>
        /// A rectangle: the horizontal run of each source row into a block of rows, then down the columns van Herk and Gil-Werman's
        /// running extreme over the block: suffix extremes back from each segment's end and prefix extremes forward from its start,
        /// so each output is the extreme of one of each.
        /// </summary>
        private void Rectangle(Rows<T> from, Rows<T> to)
        {
            int x0 = _runs[1], x1 = _runs[2], window = _rows, top = _top;
            int padded = Padded, tableLength = padded * _levels, length = _length;

            // Blocks of output rows, at least a window tall, so each source row is filtered across about twice.
            int block = Math.Max(window, 32);
            int blocks = (_height + block - 1) / block;
            Chunks.Run(blocks, (long)length * _height * Unsafe.SizeOf<T>() * 3, _threads, (first, last) =>
            {
                int rows = block + window - 1;
                T[] tableBuffer = ArrayPool<T>.Shared.Rent(tableLength);
                T[] across = ArrayPool<T>.Shared.Rent(rows * length);
                T[] suffix = ArrayPool<T>.Shared.Rent(rows * length);
                try
                {
                    Span<T> table = tableBuffer.AsSpan(0, tableLength);
                    for (int b = first; b < last; b++)
                    {
                        _ct.ThrowIfCancellationRequested();
                        int y0 = b * block, count = Math.Min(block, _height - y0), needed = count + window - 1;

                        // Row k of the block holds source row y0 + top + k, filtered across.
                        for (int k = 0; k < needed; k++)
                        {
                            if (window == 1)
                            {
                                Table(from, y0 + top + k, table, _levels);
                                Query(table, x0, x1, to[y0 + k][..length], first: true);
                                continue;
                            }

                            Table(from, y0 + top + k, table, _levels);
                            Query(table, x0, x1, across.AsSpan(k * length, length), first: true);
                        }

                        if (window == 1)
                        {
                            continue;
                        }

                        // Segments of `window` rows from row 0: suffix extremes within each, then prefix extremes in place.
                        for (int s = 0; s < needed; s += window)
                        {
                            int end = Math.Min(s + window, needed) - 1;
                            across.AsSpan(end * length, length).CopyTo(suffix.AsSpan(end * length, length));
                            for (int k = end - 1; k >= s; k--)
                            {
                                MorphologyKernels.Extreme(across.AsSpan(k * length, length), suffix.AsSpan((k + 1) * length, length), suffix.AsSpan(k * length, length), _max);
                            }

                            for (int k = s + 1; k <= end; k++)
                            {
                                MorphologyKernels.Extreme(across.AsSpan(k * length, length), across.AsSpan((k - 1) * length, length), across.AsSpan(k * length, length), _max);
                            }
                        }

                        // Output k covers rows k to k + window − 1: the suffix from k and the prefix to its last row, or one of them
                        // alone when the window is a whole segment.
                        for (int k = 0; k < count; k++)
                        {
                            Span<T> output = to[y0 + k][..length];
                            int lastRow = k + window - 1;
                            if (k % window == 0)
                            {
                                suffix.AsSpan(k * length, length).CopyTo(output);
                            }
                            else
                            {
                                MorphologyKernels.Extreme(suffix.AsSpan(k * length, length), across.AsSpan(lastRow * length, length), output, _max);
                            }
                        }
                    }
                }
                finally
                {
                    ArrayPool<T>.Shared.Return(tableBuffer);
                    ArrayPool<T>.Shared.Return(across);
                    ArrayPool<T>.Shared.Return(suffix);
                }
            }, _ct);
        }
    }
}

/// <summary>The element loops of morphology, on bytes or floats, 32 bytes at a time.</summary>
internal static class MorphologyKernels
{
    /// <summary><c>output[i] = max or min(a[i], b[i])</c> over <paramref name="output"/>'s length.</summary>
    internal static void Extreme<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b, Span<T> output, bool max)
        where T : unmanaged, INumber<T>
    {
        int n = output.Length, i = 0;
        if (a.Length < n || b.Length < n)
        {
            throw new ArgumentException("The inputs are too short.");
        }

        ref T ra = ref MemoryMarshal.GetReference(a);
        ref T rb = ref MemoryMarshal.GetReference(b);
        ref T ro = ref MemoryMarshal.GetReference(output);
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i <= n - Vector256<T>.Count; i += Vector256<T>.Count)
            {
                Vector256<T> x = Vector256.LoadUnsafe(ref ra, (nuint)i), y = Vector256.LoadUnsafe(ref rb, (nuint)i);
                (max ? Vector256.Max(x, y) : Vector256.Min(x, y)).StoreUnsafe(ref ro, (nuint)i);
            }
        }

        for (; i < n; i++)
        {
            T x = a[i], y = b[i];
            output[i] = max ? (x > y ? x : y) : (x < y ? x : y);
        }
    }

    /// <summary><c>output[i] = max or min(previous[i], a[i], b[i])</c>; <paramref name="previous"/> may be <paramref name="output"/>.</summary>
    internal static void Extreme<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b, ReadOnlySpan<T> previous, Span<T> output, bool max)
        where T : unmanaged, INumber<T>
    {
        int n = output.Length, i = 0;
        if (a.Length < n || b.Length < n || previous.Length < n)
        {
            throw new ArgumentException("The inputs are too short.");
        }

        ref T ra = ref MemoryMarshal.GetReference(a);
        ref T rb = ref MemoryMarshal.GetReference(b);
        ref T rp = ref MemoryMarshal.GetReference(previous);
        ref T ro = ref MemoryMarshal.GetReference(output);
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i <= n - Vector256<T>.Count; i += Vector256<T>.Count)
            {
                Vector256<T> x = Vector256.LoadUnsafe(ref ra, (nuint)i), y = Vector256.LoadUnsafe(ref rb, (nuint)i), p = Vector256.LoadUnsafe(ref rp, (nuint)i);
                (max ? Vector256.Max(p, Vector256.Max(x, y)) : Vector256.Min(p, Vector256.Min(x, y))).StoreUnsafe(ref ro, (nuint)i);
            }
        }

        for (; i < n; i++)
        {
            T x = a[i], y = b[i], p = previous[i];
            T e = max ? (x > y ? x : y) : (x < y ? x : y);
            output[i] = max ? (p > e ? p : e) : (p < e ? p : e);
        }
    }

    /// <summary><c>output[i] = a[i] − min(a[i], b[i])</c>: the difference, never below 0. <paramref name="output"/> may be either input.</summary>
    internal static void Subtract<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b, Span<T> output)
        where T : unmanaged, INumber<T>
    {
        int n = output.Length, i = 0;
        if (a.Length < n || b.Length < n)
        {
            throw new ArgumentException("The inputs are too short.");
        }

        ref T ra = ref MemoryMarshal.GetReference(a);
        ref T rb = ref MemoryMarshal.GetReference(b);
        ref T ro = ref MemoryMarshal.GetReference(output);
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i <= n - Vector256<T>.Count; i += Vector256<T>.Count)
            {
                Vector256<T> x = Vector256.LoadUnsafe(ref ra, (nuint)i), y = Vector256.LoadUnsafe(ref rb, (nuint)i);
                (x - Vector256.Min(x, y)).StoreUnsafe(ref ro, (nuint)i);
            }
        }

        for (; i < n; i++)
        {
            T x = a[i], y = b[i];
            output[i] = x - (x < y ? x : y);
        }
    }
}
