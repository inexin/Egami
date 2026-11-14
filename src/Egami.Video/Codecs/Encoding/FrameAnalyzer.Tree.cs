using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Egami.Video.Codecs.Encoding;

/// <summary>
/// The block macroblock tree's estimates (<see cref="BlockTree"/>): a picture's half-size blocks predicted from the references
/// the coded structure gives it, not only the frame before.
/// </summary>
internal sealed partial class FrameAnalyzer
{
    /// <summary>The furthest a tree search reaches, in half-size pixels, however far apart the pictures are.</summary>
    private const int TreeRange = 48;

    /// <summary>
    /// Estimates each block of the picture whose half-size copy is <paramref name="target"/> (its own costs in
    /// <paramref name="own"/>, from <see cref="Analyze"/>) predicted from <paramref name="ref0"/>, <paramref name="ref1"/> or
    /// their mean, the references <paramref name="distance0"/> and <paramref name="distance1"/> frames away in display order
    /// (negative before it); <paramref name="ref1"/> null for a P picture. Costs are in frame samples like
    /// <see cref="BlockCosts"/>'s, inter at most intra.
    /// </summary>
    public void Estimate(byte[] target, BlockCosts own, byte[] ref0, int distance0, byte[]? ref1, int distance1, TreeCosts result)
    {
        result.Bi = ref1 is not null;
        for (int by = 0; by < _rows; by++)
        {
            for (int bx = 0; bx < _cols; bx++)
            {
                int i = (by * _cols) + bx, x = bx * Block, y = by * Block;
                int detail = own.Detail[i], intra = own.Intra[i];
                result.Intra[i] = intra;
                (int c0, int x0, int y0) = TreeSearch(target, ref0, x, y, bx, by, distance0, own, result.Vectors0);
                long best = ((long)c0 * 4) + detail;
                byte mode = 0;
                int x1 = 0, y1 = 0;
                if (ref1 is not null)
                {
                    (int c1, x1, y1) = TreeSearch(target, ref1, x, y, bx, by, distance1, own, result.Vectors1);
                    long cost1 = ((long)c1 * 4) + detail;
                    if (cost1 < best)
                    {
                        (best, mode) = (cost1, 1);
                    }

                    int bi = BiSad(target, ref0, ref1, x, y, x0, y0, x1, y1) + Charge(x0, y0) + Charge(x1, y1);
                    long costBi = ((long)bi * 4) + detail;
                    if (costBi < best)
                    {
                        (best, mode) = (costBi, 2);
                    }
                }

                result.Vectors0[i * 2] = (short)x0;
                result.Vectors0[(i * 2) + 1] = (short)y0;
                result.Vectors1[i * 2] = (short)x1;
                result.Vectors1[(i * 2) + 1] = (short)y1;
                result.Inter[i] = (int)Math.Min(best, intra);
                result.Modes[i] = best >= intra ? TreeCosts.IntraMode : mode;
            }
        }
    }

    /// <summary>
    /// A block's search into a reference <paramref name="distance"/> frames away: from zero, the left and upper blocks' vectors
    /// into the same reference, and its vector into the frame before scaled by the distance; then a diamond refinement.
    /// </summary>
    private (int Cost, int X, int Y) TreeSearch(byte[] target, byte[] reference, int x, int y, int bx, int by, int distance, BlockCosts own, short[] found)
    {
        int range = Math.Min(Range * Math.Max(Math.Abs(distance), 1), TreeRange);
        int bestX = 0, bestY = 0, best = SadAt(target, reference, x, y, 0, 0);
        int i = (by * _cols) + bx;
        TryAt(target, reference, x, y, own.Vectors[i * 2] * -distance, own.Vectors[(i * 2) + 1] * -distance, range, ref best, ref bestX, ref bestY);
        if (bx > 0)
        {
            TryAt(target, reference, x, y, found[(i - 1) * 2], found[((i - 1) * 2) + 1], range, ref best, ref bestX, ref bestY);
        }

        if (by > 0)
        {
            TryAt(target, reference, x, y, found[(i - _cols) * 2], found[((i - _cols) * 2) + 1], range, ref best, ref bestX, ref bestY);
        }

        for (int step = Math.Abs(distance) > 1 ? 8 : 4; step >= 1; step >>= 1)
        {
            bool moved = true;
            for (int round = 0; moved && round < 8; round++)
            {
                int cx = bestX, cy = bestY;
                moved = TryAt(target, reference, x, y, cx - step, cy, range, ref best, ref bestX, ref bestY);
                moved |= TryAt(target, reference, x, y, cx + step, cy, range, ref best, ref bestX, ref bestY);
                moved |= TryAt(target, reference, x, y, cx, cy - step, range, ref best, ref bestX, ref bestY);
                moved |= TryAt(target, reference, x, y, cx, cy + step, range, ref best, ref bestX, ref bestY);
            }
        }

        // Record the vector at once: the blocks to the right and below start from it.
        found[i * 2] = (short)bestX;
        found[(i * 2) + 1] = (short)bestY;
        return (best + Charge(bestX, bestY), bestX, bestY);
    }

    /// <summary>A vector's own bits, as the frame-to-frame search charges them.</summary>
    private static int Charge(int x, int y) => (Math.Abs(x) + Math.Abs(y)) * 2;

    private bool TryAt(byte[] target, byte[] reference, int x, int y, int mx, int my, int range, ref int best, ref int bestX, ref int bestY)
    {
        if (mx < -range || mx > range || my < -range || my > range || x + mx < 0 || y + my < 0 || x + mx + Block > _width || y + my + Block > _height)
        {
            return false;
        }

        int cost = SadAt(target, reference, x, y, mx, my);
        if (cost < best)
        {
            (best, bestX, bestY) = (cost, mx, my);
            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int SadAt(byte[] a, byte[] b, int x, int y, int mx, int my)
    {
        int offset = (y * _stride) + x, reference = ((y + my) * _stride) + x + mx;
        if (Vector128.IsHardwareAccelerated)
        {
            Vector128<ushort> sum = Vector128<ushort>.Zero;
            for (int i = 0; i < Block; i++)
            {
                Vector128<ushort> p = Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref a[offset + (i * _stride)])).AsByte());
                Vector128<ushort> q = Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref b[reference + (i * _stride)])).AsByte());
                sum += Vector128.Max(p, q) - Vector128.Min(p, q);
            }

            return Vector128.Sum(sum);
        }

        int total = 0;
        for (int i = 0; i < Block; i++)
        {
            for (int j = 0; j < Block; j++)
            {
                total += Math.Abs(a[offset + (i * _stride) + j] - b[reference + (i * _stride) + j]);
            }
        }

        return total;
    }

    /// <summary>The error of a block predicted by the rounded mean of two references at their vectors.</summary>
    private int BiSad(byte[] a, byte[] r0, byte[] r1, int x, int y, int x0, int y0, int x1, int y1)
    {
        int offset = (y * _stride) + x, at0 = ((y + y0) * _stride) + x + x0, at1 = ((y + y1) * _stride) + x + x1;
        int total = 0;
        for (int i = 0; i < Block; i++)
        {
            int row = i * _stride;
            for (int j = 0; j < Block; j++)
            {
                total += Math.Abs(a[offset + row + j] - ((r0[at0 + row + j] + r1[at1 + row + j] + 1) >> 1));
            }
        }

        return total;
    }
}
