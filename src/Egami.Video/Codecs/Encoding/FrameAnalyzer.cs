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
internal sealed class FrameAnalyzer
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
    private bool _hasPrevious;

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
        long intra = 0, inter = 0;
        Array.Clear(_vectors);
        for (int by = 0; by < _rows; by++)
        {
            for (int bx = 0; bx < _cols; bx++)
            {
                int x = bx * Block, y = by * Block;
                int intraCost = IntraCost(x, y);
                (int cost, int mx, int my) = Search(x, y, bx, by);
                int index = ((by * _cols) + bx) * 2;
                _vectors[index] = (short)mx;
                _vectors[index + 1] = (short)my;
                intra += intraCost;
                inter += Math.Min(cost, intraCost);
            }
        }

        (_current, _previous) = (current, previous);
        return (inter * 4, intra * 4);
    }

    /// <summary>Forgets the frame before, so the next frame is analyzed as the first of a stream.</summary>
    public void Reset() => _hasPrevious = false;

    /// <summary>Analyzes the next frame in display order.</summary>
    public FrameStats Analyze(YuvRaster<byte> picture)
    {
        (_current, _previous) = (_previous, _current);
        long detail = Downscale(picture.Y, _current);
        long intra = 0, inter = 0, motion = 0, moving = 0;
        for (int by = 0; by < _rows; by++)
        {
            for (int bx = 0; bx < _cols; bx++)
            {
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
            }
        }

        _hasPrevious = true;

        // Half-size errors stand for four times the samples; the detail the half-size copy averaged away (noise, fine texture)
        // costs bits whichever way a frame is predicted. Vectors are in half-size pixels, so twice as long in the frame.
        return new FrameStats((intra * 4) + detail, (inter * 4) + detail, moving == 0 ? 0 : (int)(motion * 2 * 16 / moving), detail);
    }

    /// <summary>Averages 2 × 2 blocks into the half-size copy and returns how far the samples were from their averages, summed.</summary>
    private long Downscale(YuvPlane<byte> luma, byte[] target)
    {
        int lw = luma.Width, lh = luma.Height;
        long detail = 0;
        for (int y = 0; y < _height; y++)
        {
            int y0 = Math.Min(2 * y, lh - 1), y1 = Math.Min((2 * y) + 1, lh - 1);
            ReadOnlySpan<byte> r0 = luma.Row(y0), r1 = luma.Row(y1);
            Span<byte> row = target.AsSpan(y * _stride, _width);
            for (int x = 0; x < _width; x++)
            {
                int x0 = Math.Min(2 * x, lw - 1), x1 = Math.Min((2 * x) + 1, lw - 1);
                int a = r0[x0], b = r0[x1], c = r1[x0], d = r1[x1];
                int mean = (a + b + c + d + 2) >> 2;
                row[x] = (byte)mean;
                detail += Math.Abs(a - mean) + Math.Abs(b - mean) + Math.Abs(c - mean) + Math.Abs(d - mean);
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
