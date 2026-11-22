using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Egami.Video.Codecs.Encoding;

/// <summary>A frame's cheap statistics from <see cref="FrameAnalyzer"/>, the basis of keyframe, group and bit decisions.</summary>
/// <param name="Intra">The summed error of predicting each 16×16 block from its neighbours in the same frame.</param>
/// <param name="Inter">The summed error of the better of that and a motion-compensated copy of the frame before.</param>
/// <param name="Motion">The average motion vector length in pixels, times 16, over the blocks that predict from the frame before.</param>
/// <param name="Detail">The part of both errors that is detail finer than the half-size copy holds: noise and fine texture.</param>
internal readonly record struct FrameStats(long Intra, long Inter, int Motion, long Detail = 0)
{
    /// <summary>Gets how much of the frame's coarse picture the frame before does not explain, 0 to 1: above about 0.7 it is a new scene.</summary>
    public double InterRatio => Intra - Detail <= 0 ? 0 : (double)(Inter - Detail) / (Intra - Detail);
}

/// <summary>
/// The lookahead's analysis, on a half-size copy of the luma: per 8×8 block of that copy (16×16 in the frame), the error of
/// the best of DC, vertical and horizontal prediction from the source's own neighbours, and the error of a small motion search
/// into the frame before, seeded by the left and upper blocks' vectors. The result depends only on the frames, never on threads.
/// </summary>
internal sealed partial class FrameAnalyzer
{
    private const int Block = 8;
    private const int Range = 16;

    private readonly int _width;
    private readonly int _height;
    private readonly int _stride;
    private readonly int _rows;
    private readonly int _cols;
    private byte[] _current;
    private byte[] _previous;
    private readonly short[] _vectors;
    private readonly int[] _intraCosts;
    private readonly int[] _interCosts;
    private readonly int[] _detail;
    private readonly int[] _progress;
    private readonly long[] _rowSums;
    private bool _hasPrevious;
    private int _nextRow;

    public FrameAnalyzer(int width, int height)
    {
        int hw = (width + 1) / 2, hh = (height + 1) / 2;
        _cols = (hw + Block - 1) / Block;
        _rows = (hh + Block - 1) / Block;
        _width = _cols * Block;
        _height = _rows * Block;
        _stride = _width;
        _current = new byte[_stride * _height];
        _previous = new byte[_stride * _height];
        _vectors = new short[_cols * _rows * 2];
        _intraCosts = new int[_cols * _rows];
        _interCosts = new int[_cols * _rows];
        _detail = new int[_cols * _rows];
        _progress = new int[_rows];
        _rowSums = new long[_rows * 4];
    }

    /// <summary>
    /// Gets or sets how many threads analyze a frame: block rows run as a wavefront, each block after the one to its left and
    /// the one above, whose vectors seed its search, so the result is the same on any number.
    /// </summary>
    public int Parallelism { get; set; } = 1;

    /// <summary>Gets the columns of blocks: 8x8 on the half-size copy, 16x16 in the frame.</summary>
    public int Columns => _cols;

    /// <summary>Gets the rows of blocks.</summary>
    public int Rows => _rows;

    /// <summary>Copies the last analyzed frame's block costs and vectors, for <see cref="MacroblockTree"/>.</summary>
    /// <remarks>
    /// The costs are in frame samples, as <see cref="FrameStats"/>'s are: four times the half-size error plus the block's detail
    /// the half-size copy averaged away, which costs bits however the block is predicted.
    /// </remarks>
    public void CopyBlocks(BlockCosts target)
    {
        for (int i = 0; i < _intraCosts.Length; i++)
        {
            target.Intra[i] = (_intraCosts[i] * 4) + _detail[i];
            target.Inter[i] = (_interCosts[i] * 4) + _detail[i];
            target.Detail[i] = _detail[i];
        }

        _vectors.CopyTo(target.Vectors, 0);
    }

    /// <summary>Gets the size of the half-size copy <see cref="CopyHalf"/> writes.</summary>
    public int HalfLength => _current.Length;

    /// <summary>Copies the last analyzed frame's half-size luma, for <see cref="Distance"/> later.</summary>
    public void CopyHalf(byte[] target) => _current.CopyTo(target, 0);

    /// <summary>
    /// The error of predicting one analyzed frame's coarse picture from another's by the same search, block by block no worse
    /// than intra: how well a reference frame <paramref name="from"/> can stand in for <paramref name="to"/>, against the
    /// second value, <paramref name="to"/>'s intra error.
    /// </summary>
    public (long Inter, long Intra) Distance(byte[] from, byte[] to)
    {
        (byte[] current, byte[] previous) = (_current, _previous);
        (_current, _previous) = (to, from);
        Array.Clear(_vectors);
        Wavefront(DistanceRow);
        long intra = 0, inter = 0;
        for (int by = 0; by < _rows; by++)
        {
            intra += _rowSums[by * 4];
            inter += _rowSums[(by * 4) + 1];
        }

        (_current, _previous) = (current, previous);
        return (inter * 4, intra * 4);
    }

    private void DistanceRow(int by)
    {
        long intra = 0, inter = 0;
        for (int bx = 0; bx < _cols; bx++)
        {
            WaitAbove(by, bx);
            int x = bx * Block, y = by * Block;
            int intraCost = IntraCost(x, y);
            (int cost, int mx, int my) = Search(x, y, bx, by);
            int index = ((by * _cols) + bx) * 2;
            _vectors[index] = (short)mx;
            _vectors[index + 1] = (short)my;
            intra += intraCost;
            inter += Math.Min(cost, intraCost);
            Volatile.Write(ref _progress[by], bx + 1);
        }

        _rowSums[by * 4] = intra;
        _rowSums[(by * 4) + 1] = inter;
    }

    /// <summary>
    /// Runs <paramref name="row"/> on each block row: in order on one thread, else as a wavefront, workers taking rows in order
    /// and each block waiting for the block above (<see cref="WaitAbove"/>, after which the row marks its progress).
    /// </summary>
    private void Wavefront(Action<int> row)
    {
        Array.Clear(_progress);
        int workers = Math.Min(Parallelism, _rows);
        if (workers <= 1)
        {
            for (int by = 0; by < _rows; by++)
            {
                row(by);
            }

            return;
        }

        // Rows are handed out in order, so the earliest unfinished row always belongs to a running worker and can go on.
        _nextRow = 0;
        Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, _ =>
        {
            for (int by = Interlocked.Increment(ref _nextRow) - 1; by < _rows; by = Interlocked.Increment(ref _nextRow) - 1)
            {
                row(by);
            }
        });
    }

    /// <summary>Waits until the block above (<paramref name="bx"/>, <paramref name="by"/> - 1) is done.</summary>
    private void WaitAbove(int by, int bx)
    {
        if (by == 0 || Parallelism <= 1)
        {
            return;
        }

        SpinWait spin = default;
        while (Volatile.Read(ref _progress[by - 1]) <= bx)
        {
            spin.SpinOnce(sleep1Threshold: -1);
        }
    }

    /// <summary>Forgets the frame before, so the next frame is analyzed as the first of a stream.</summary>
    public void Reset() => _hasPrevious = false;

    /// <summary>Analyzes the next frame in display order.</summary>
    public FrameStats Analyze(YuvRaster<byte> picture)
    {
        (_current, _previous) = (_previous, _current);
        long detail = Downscale(picture.Y, _current);
        Wavefront(AnalyzeRow);
        long intra = 0, inter = 0, motion = 0, moving = 0;
        for (int by = 0; by < _rows; by++)
        {
            intra += _rowSums[by * 4];
            inter += _rowSums[(by * 4) + 1];
            motion += _rowSums[(by * 4) + 2];
            moving += _rowSums[(by * 4) + 3];
        }

        _hasPrevious = true;

        // Half-size errors stand for four times the samples; the detail the half-size copy averaged away (noise, fine texture)
        // costs bits whichever way a frame is predicted. Vectors are in half-size pixels, so twice as long in the frame.
        return new FrameStats((intra * 4) + detail, (inter * 4) + detail, moving == 0 ? 0 : (int)(motion * 2 * 16 / moving), detail);
    }

    private void AnalyzeRow(int by)
    {
        long intra = 0, inter = 0, motion = 0, moving = 0;
        {
            for (int bx = 0; bx < _cols; bx++)
            {
                WaitAbove(by, bx);
                int x = bx * Block, y = by * Block;
                int intraCost = IntraCost(x, y);
                int best = intraCost;
                int index = ((by * _cols) + bx) * 2;
                _vectors[index] = 0;
                _vectors[index + 1] = 0;
                if (_hasPrevious)
                {
                    (int cost, int mx, int my) = Search(x, y, bx, by);
                    if (cost < best)
                    {
                        best = cost;
                        _vectors[index] = (short)mx;
                        _vectors[index + 1] = (short)my;
                        motion += Math.Abs(mx) + Math.Abs(my);
                        moving++;
                    }
                }

                intra += intraCost;
                inter += best;
                _intraCosts[index / 2] = intraCost;
                _interCosts[index / 2] = best;
                Volatile.Write(ref _progress[by], bx + 1);
            }
        }

        _rowSums[by * 4] = intra;
        _rowSums[(by * 4) + 1] = inter;
        _rowSums[(by * 4) + 2] = motion;
        _rowSums[(by * 4) + 3] = moving;
    }

    /// <summary>Averages 2 × 2 blocks into the half-size copy and returns how far the samples were from their averages, summed.</summary>
    private long Downscale(YuvPlane<byte> luma, byte[] target)
    {
        int lw = luma.Width, lh = luma.Height;
        long detail = 0;
        Array.Clear(_detail);
        for (int y = 0; y < _height; y++)
        {
            int blockRow = (y >> 3) * _cols;
            int y0 = Math.Min(2 * y, lh - 1), y1 = Math.Min((2 * y) + 1, lh - 1);
            ReadOnlySpan<byte> r0 = luma.Row(y0), r1 = luma.Row(y1);
            Span<byte> row = target.AsSpan(y * _stride, _width);
            int x = 0;
            if (Vector128.IsHardwareAccelerated && BitConverter.IsLittleEndian)
            {
                // Eight half-size samples, one 8x8 detail block, at a time while both source rows hold the sixteen samples
                // whole; the same integer arithmetic as below.
                Vector128<ushort> low = Vector128.Create((ushort)0xFF), two = Vector128.Create((ushort)2);
                for (; x + 8 <= _width && (2 * x) + 16 <= lw; x += 8)
                {
                    Vector128<ushort> v0 = Vector128.Create(r0.Slice(2 * x, 16)).AsUInt16(), v1 = Vector128.Create(r1.Slice(2 * x, 16)).AsUInt16();
                    Vector128<ushort> a = v0 & low, b = Vector128.ShiftRightLogical(v0, 8), c = v1 & low, d = Vector128.ShiftRightLogical(v1, 8);
                    Vector128<ushort> mean = Vector128.ShiftRightLogical(a + b + c + d + two, 2);
                    Vector128<ushort> d4 = (Vector128.Max(a, mean) - Vector128.Min(a, mean)) + (Vector128.Max(b, mean) - Vector128.Min(b, mean))
                        + (Vector128.Max(c, mean) - Vector128.Min(c, mean)) + (Vector128.Max(d, mean) - Vector128.Min(d, mean));
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(row.Slice(x, 8), Vector128.Narrow(mean, mean).AsUInt64().ToScalar());
                    int sum = Vector128.Sum(d4);
                    detail += sum;
                    _detail[blockRow + (x >> 3)] += sum;
                }
            }

            for (; x < _width; x++)
            {
                int x0 = Math.Min(2 * x, lw - 1), x1 = Math.Min((2 * x) + 1, lw - 1);
                int a = r0[x0], b = r0[x1], c = r1[x0], d = r1[x1];
                int mean = (a + b + c + d + 2) >> 2;
                row[x] = (byte)mean;
                int d4 = Math.Abs(a - mean) + Math.Abs(b - mean) + Math.Abs(c - mean) + Math.Abs(d - mean);
                detail += d4;
                _detail[blockRow + (x >> 3)] += d4;
            }
        }

        return detail;
    }

    private int IntraCost(int x, int y)
    {
        ReadOnlySpan<byte> image = _current;
        int dc = 0, count = 0;
        if (y > 0)
        {
            ReadOnlySpan<byte> above = image.Slice(((y - 1) * _stride) + x, Block);
            foreach (byte b in above)
            {
                dc += b;
            }

            count += Block;
        }

        if (x > 0)
        {
            for (int i = 0; i < Block; i++)
            {
                dc += image[((y + i) * _stride) + x - 1];
            }

            count += Block;
        }

        dc = count == 0 ? 128 : (dc + (count / 2)) / count;
        int dcCost = 0, vCost = y > 0 ? 0 : int.MaxValue, hCost = x > 0 ? 0 : int.MaxValue;
        for (int i = 0; i < Block; i++)
        {
            ReadOnlySpan<byte> row = image.Slice(((y + i) * _stride) + x, Block);
            int left = x > 0 ? image[((y + i) * _stride) + x - 1] : 0;
            for (int j = 0; j < Block; j++)
            {
                int s = row[j];
                dcCost += Math.Abs(s - dc);
                if (y > 0)
                {
                    vCost += Math.Abs(s - image[((y - 1) * _stride) + x + j]);
                }

                if (x > 0)
                {
                    hCost += Math.Abs(s - left);
                }
            }
        }

        return Math.Min(dcCost, Math.Min(vCost, hCost));
    }

    private (int Cost, int X, int Y) Search(int x, int y, int bx, int by)
    {
        int bestX = 0, bestY = 0;
        int best = Sad(x, y, 0, 0);
        if (bx > 0)
        {
            Try(x, y, _vectors[((by * _cols) + bx - 1) * 2], _vectors[(((by * _cols) + bx - 1) * 2) + 1], ref best, ref bestX, ref bestY);
        }

        if (by > 0)
        {
            int i = (((by - 1) * _cols) + bx) * 2;
            Try(x, y, _vectors[i], _vectors[i + 1], ref best, ref bestX, ref bestY);
        }

        for (int step = 4; step >= 1; step >>= 1)
        {
            bool moved = true;
            for (int round = 0; moved && round < 8; round++)
            {
                moved = false;
                int cx = bestX, cy = bestY;
                moved |= Try(x, y, cx - step, cy, ref best, ref bestX, ref bestY);
                moved |= Try(x, y, cx + step, cy, ref best, ref bestX, ref bestY);
                moved |= Try(x, y, cx, cy - step, ref best, ref bestX, ref bestY);
                moved |= Try(x, y, cx, cy + step, ref best, ref bestX, ref bestY);
            }
        }

        // A vector costs bits of its own; a small charge keeps still areas at zero.
        int charge = (Math.Abs(bestX) + Math.Abs(bestY)) * 2;
        return (best + charge, bestX, bestY);
    }

    private bool Try(int x, int y, int mx, int my, ref int best, ref int bestX, ref int bestY)
    {
        if (mx < -Range || mx > Range || my < -Range || my > Range || x + mx < 0 || y + my < 0 || x + mx + Block > _width || y + my + Block > _height)
        {
            return false;
        }

        int cost = Sad(x, y, mx, my);
        if (cost < best)
        {
            (best, bestX, bestY) = (cost, mx, my);
            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Sad(int x, int y, int mx, int my)
    {
        ReadOnlySpan<byte> a = _current, b = _previous;
        int offset = (y * _stride) + x, reference = ((y + my) * _stride) + x + mx;
        if (Vector128.IsHardwareAccelerated)
        {
            Vector128<ushort> sum = Vector128<ushort>.Zero;
            for (int i = 0; i < Block; i++)
            {
                Vector128<ushort> p = Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.AsRef(in a[offset + (i * _stride)]))).AsByte());
                Vector128<ushort> q = Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.AsRef(in b[reference + (i * _stride)]))).AsByte());
                sum += Vector128.Max(p, q) - Vector128.Min(p, q);
            }

            return Vector128.Sum(sum);
        }

        int total = 0;
        for (int i = 0; i < Block; i++)
        {
            ReadOnlySpan<byte> ra = a.Slice(offset + (i * _stride), Block), rb = b.Slice(reference + (i * _stride), Block);
            for (int j = 0; j < Block; j++)
            {
                total += Math.Abs(ra[j] - rb[j]);
            }
        }

        return total;
    }
}
