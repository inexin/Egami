using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Video.Codecs.H264;

/// <summary>
/// Intra prediction for the encoder (8.3.1.2, 8.3.2.2, 8.3.3, 8.3.4), from neighbouring samples gathered once per block:
/// <c>top</c> holds p[x, −1] for x = −1 (the corner) to 2n − 1 at index x + 1, <c>left</c> p[−1, y] for y = −1 to n − 1 at
/// index y + 1. Missing top-right samples are the last top sample repeated, as the standard substitutes them.
/// </summary>
internal static class H264IntraPredict
{
    /// <summary>The Intra4x4 and Intra8x8 modes' needs: bit 0 top, bit 1 left, bit 2 the corner.</summary>
    internal static ReadOnlySpan<byte> NxNNeeds => [1, 2, 0, 1, 7, 7, 7, 1, 2];

    /// <summary>Whether an Intra4x4 or Intra8x8 mode can be used with the neighbours there are.</summary>
    internal static bool NxNAllowed(int mode, bool top, bool left, bool corner)
    {
        int need = NxNNeeds[mode];
        return ((need & 1) == 0 || top) && ((need & 2) == 0 || left) && ((need & 4) == 0 || corner);
    }

    /// <summary>Whether an Intra16x16 mode (0 vertical, 1 horizontal, 2 DC, 3 plane) can be used.</summary>
    internal static bool Allowed16x16(int mode, bool top, bool left, bool corner) => mode switch
    {
        0 => top,
        1 => left,
        2 => true,
        _ => top && left && corner,
    };

    /// <summary>Whether a chroma mode (0 DC, 1 horizontal, 2 vertical, 3 plane) can be used.</summary>
    internal static bool AllowedChroma(int mode, bool top, bool left, bool corner) => mode switch
    {
        0 => true,
        1 => left,
        2 => top,
        _ => top && left && corner,
    };

    /// <summary>
    /// Gathers a block's neighbours from a plane: n samples above and n more above right (repeated from the last when
    /// <paramref name="topRight"/> is false), n to the left, and the corner.
    /// </summary>
    internal static void Gather(byte[] plane, int at, int stride, int n, bool top, bool topRight, bool left, bool corner, Span<int> t, Span<int> l)
    {
        if (top)
        {
            int above = at - stride;
            for (int x = 0; x < n; x++)
            {
                t[x + 1] = plane[above + x];
            }

            for (int x = n; x < 2 * n; x++)
            {
                t[x + 1] = topRight ? plane[above + x] : t[n];
            }
        }

        if (left)
        {
            for (int y = 0; y < n; y++)
            {
                l[y + 1] = plane[at + (y * stride) - 1];
            }
        }

        int c = corner ? plane[at - stride - 1] : 0;
        t[0] = l[0] = c;
    }

    /// <summary>Filters the neighbours of an 8x8 block in place (8.3.2.2.1).</summary>
    internal static void Filter8x8(Span<int> t, Span<int> l, bool top, bool left, bool corner)
    {
        Span<int> ft = stackalloc int[17];
        Span<int> fl = stackalloc int[9];
        if (top)
        {
            ft[1] = corner ? (t[0] + (2 * t[1]) + t[2] + 2) >> 2 : ((3 * t[1]) + t[2] + 2) >> 2;
            for (int x = 1; x < 15; x++)
            {
                ft[x + 1] = (t[x] + (2 * t[x + 1]) + t[x + 2] + 2) >> 2;
            }

            ft[16] = (t[15] + (3 * t[16]) + 2) >> 2;
        }

        if (corner)
        {
            ft[0] = !top && left ? ((3 * t[0]) + l[1] + 2) >> 2
                : top && !left ? ((3 * t[0]) + t[1] + 2) >> 2
                : top && left ? (t[1] + (2 * t[0]) + l[1] + 2) >> 2
                : t[0];
        }

        if (left)
        {
            fl[1] = corner ? (l[0] + (2 * l[1]) + l[2] + 2) >> 2 : ((3 * l[1]) + l[2] + 2) >> 2;
            for (int y = 1; y < 7; y++)
            {
                fl[y + 1] = (l[y] + (2 * l[y + 1]) + l[y + 2] + 2) >> 2;
            }

            fl[8] = (l[7] + (3 * l[8]) + 2) >> 2;
        }

        if (top)
        {
            ft[1..17].CopyTo(t[1..17]);
        }

        if (left)
        {
            fl[1..9].CopyTo(l[1..9]);
        }

        if (corner)
        {
            t[0] = l[0] = ft[0];
        }
    }

    /// <summary>The size of the edge filters <see cref="Edges"/> works out for <see cref="PredictNxN"/>.</summary>
    internal const int EdgeSize = 48;

    // Where the 3-tap filter F(k) and the 2-tap mean A(k) of the edge start, k from −8 on (see Edges).
    private const int FAt = 8;
    private const int AAt = 24 + 8;

    /// <summary>
    /// Works out once per n by n block the filtered edge every directional Intra4x4 and Intra8x8 mode reads (8.3.1.2.4 to
    /// 8.3.1.2.9, 8.3.2.2.4 to 8.3.2.2.9): along the edge e(k), which is p[k − 1, −1] for k ≥ 0 and p[−1, −k − 1] for k ≤ 0
    /// (so e(0) is the corner), F(k) = (e(k − 1) + 2e(k) + e(k + 1) + 2) >> 2 and A(k) = (e(k) + e(k + 1) + 1) >> 1. Samples a
    /// mode cannot use (a missing neighbour) go in too and are never read.
    /// </summary>
    [SkipLocalsInit]
    internal static void Edges(int n, ReadOnlySpan<int> t, ReadOnlySpan<int> l, Span<byte> edges)
    {
        // The edge from e(−n) to e(2n), e(k) at index k + n.
        Span<int> e = stackalloc int[25];
        for (int k = 1; k <= n; k++)
        {
            e[n - k] = l[k];
        }

        for (int k = 0; k <= 2 * n; k++)
        {
            e[n + k] = t[k];
        }

        for (int k = -n; k < 2 * n; k++)
        {
            int at = n + k;
            if (k > -n)
            {
                edges[FAt + k] = (byte)((e[at - 1] + (2 * e[at]) + e[at + 1] + 2) >> 2);
            }

            edges[AAt + k] = (byte)((e[at] + e[at + 1] + 1) >> 1);
        }
    }

    /// <summary>
    /// Predicts an n by n block (n 4 or 8) in an Intra4x4 or Intra8x8 mode into <paramref name="dst"/>, from its neighbours and
    /// the edge filters <see cref="Edges"/> worked out from them.
    /// </summary>
    internal static void PredictNxN(byte[] dst, int dstStride, int n, int mode, ReadOnlySpan<int> t, ReadOnlySpan<int> l, bool top, bool left, ReadOnlySpan<byte> edges)
    {
        ReadOnlySpan<byte> f = edges[FAt..], a = edges[AAt..];
        ReadOnlySpan<byte> fl = edges[(FAt - n)..], al = edges[(AAt - n)..];
        switch (mode)
        {
            case 0:
                for (int x = 0; x < n; x++)
                {
                    dst[x] = (byte)t[x + 1];
                }

                for (int y = 1; y < n; y++)
                {
                    Row(dst, y * dstStride, dst, 0, n);
                }

                return;
            case 1:
                for (int y = 0; y < n; y++)
                {
                    dst.AsSpan(y * dstStride, n).Fill((byte)l[y + 1]);
                }

                return;
            case 2:
                {
                    byte dc = (byte)Dc(n, t, l, top, left);
                    for (int y = 0; y < n; y++)
                    {
                        dst.AsSpan(y * dstStride, n).Fill(dc);
                    }

                    return;
                }

            case 3:
                // Diagonal down left: F(x + y + 2), the last sample (t(2n − 1) + 3t(2n) + 2) >> 2.
                for (int y = 0; y < n; y++)
                {
                    Row(dst, y * dstStride, f, y + 2, n);
                }

                dst[((n - 1) * dstStride) + n - 1] = (byte)((t[(2 * n) - 1] + (3 * t[2 * n]) + 2) >> 2);
                return;
            case 4:
                // Diagonal down right: F(x − y).
                for (int y = 0; y < n; y++)
                {
                    Row(dst, y * dstStride, fl, n - y, n);
                }

                return;
            case 5:
                // Vertical right, by zVR = 2x − y.
                for (int y = 0; y < n; y++)
                {
                    int row = y * dstStride;
                    for (int x = 0; x < n; x++)
                    {
                        int z = (2 * x) - y, h = x - (y >> 1);
                        dst[row + x] = z < 0 ? fl[n + z + 1] : (z & 1) == 0 ? a[h] : f[h];
                    }
                }

                return;
            case 6:
                // Horizontal down, by zHD = 2y − x.
                for (int y = 0; y < n; y++)
                {
                    int row = y * dstStride;
                    for (int x = 0; x < n; x++)
                    {
                        int z = (2 * y) - x, h = y - (x >> 1);
                        dst[row + x] = z < 0 ? f[-z - 1] : (z & 1) == 0 ? al[n - h - 1] : fl[n - h];
                    }
                }

                return;
            case 7:
                // Vertical left: A(x + y / 2 + 1) on even rows, F(x + (y − 1) / 2 + 2) on odd ones.
                for (int y = 0; y < n; y++)
                {
                    Row(dst, y * dstStride, (y & 1) == 0 ? a : f, (y >> 1) + 1 + (y & 1), n);
                }

                return;
            default:
                {
                    // Horizontal up, by zHU = x + 2y.
                    int last = (2 * n) - 3;
                    byte end = (byte)l[n], before = (byte)((l[n - 1] + (3 * l[n]) + 2) >> 2);
                    for (int y = 0; y < n; y++)
                    {
                        int row = y * dstStride;
                        for (int x = 0; x < n; x++)
                        {
                            int z = x + (2 * y), h = y + (x >> 1);
                            dst[row + x] = z > last ? end : z == last ? before : (z & 1) == 0 ? al[n - h - 2] : fl[n - h - 2];
                        }
                    }

                    return;
                }
        }
    }

    // Copies n (4 or 8) bytes.
    private static void Row(byte[] dst, int at, ReadOnlySpan<byte> src, int from, int n)
    {
        if (n == 4)
        {
            MemoryMarshal.Write(dst.AsSpan(at, 4), MemoryMarshal.Read<uint>(src.Slice(from, 4)));
        }
        else
        {
            MemoryMarshal.Write(dst.AsSpan(at, 8), MemoryMarshal.Read<ulong>(src.Slice(from, 8)));
        }
    }

    private static int Dc(int n, ReadOnlySpan<int> t, ReadOnlySpan<int> l, bool top, bool left)
    {
        int st = 0, sl = 0;
        for (int i = 1; i <= n; i++)
        {
            st += t[i];
            sl += l[i];
        }

        int log = n == 4 ? 2 : n == 8 ? 3 : 4;
        return top && left ? (st + sl + n) >> (log + 1)
            : left ? (sl + (n >> 1)) >> log
            : top ? (st + (n >> 1)) >> log
            : 128;
    }

    /// <summary>Predicts a 16x16 luma block in an Intra16x16 mode.</summary>
    internal static void Predict16x16(Span<byte> dst, int dstStride, int mode, ReadOnlySpan<int> t, ReadOnlySpan<int> l, bool top, bool left)
    {
        if (mode == 3)
        {
            int h = 0, v = 0;
            for (int i = 0; i < 8; i++)
            {
                h += (i + 1) * (t[9 + i] - t[7 - i]);
                v += (i + 1) * (l[9 + i] - l[7 - i]);
            }

            Plane(dst, dstStride, 16, 16, 16 * (l[16] + t[16]), ((5 * h) + 32) >> 6, ((5 * v) + 32) >> 6, 7, 7);
            return;
        }

        if (mode == 0)
        {
            Span<byte> first = dst[..16];
            for (int x = 0; x < 16; x++)
            {
                first[x] = (byte)t[x + 1];
            }

            for (int y = 1; y < 16; y++)
            {
                first.CopyTo(dst.Slice(y * dstStride, 16));
            }

            return;
        }

        byte dc = (byte)(mode == 2 ? Dc(16, t, l, top, left) : 0);
        for (int y = 0; y < 16; y++)
        {
            dst.Slice(y * dstStride, 16).Fill(mode == 1 ? (byte)l[y + 1] : dc);
        }
    }

    /// <summary>Predicts an 8x8 chroma block of 4:2:0 in a chroma mode.</summary>
    internal static void PredictChroma(Span<byte> dst, int dstStride, int mode, ReadOnlySpan<int> t, ReadOnlySpan<int> l, bool top, bool left)
    {
        if (mode == 3)
        {
            int h = 0, v = 0;
            for (int i = 0; i < 4; i++)
            {
                h += (i + 1) * (t[5 + i] - t[3 - i]);
                v += (i + 1) * (l[5 + i] - l[3 - i]);
            }

            Plane(dst, dstStride, 8, 8, 16 * (l[8] + t[8]), ((34 * h) + 32) >> 6, ((34 * v) + 32) >> 6, 3, 3);
            return;
        }

        for (int by = 0; by < 2; by++)
        {
            for (int bx = 0; bx < 2; bx++)
            {
                int value = 0;
                if (mode == 0)
                {
                    int st = 0, sl = 0;
                    for (int i = 0; i < 4; i++)
                    {
                        st += t[(bx * 4) + i + 1];
                        sl += l[(by * 4) + i + 1];
                    }

                    bool both = (bx == 0) == (by == 0);
                    value = both ? (top && left ? (st + sl + 4) >> 3 : left ? (sl + 2) >> 2 : top ? (st + 2) >> 2 : 128)
                        : bx > 0 ? (top ? (st + 2) >> 2 : left ? (sl + 2) >> 2 : 128)
                        : (left ? (sl + 2) >> 2 : top ? (st + 2) >> 2 : 128);
                }

                for (int y = 0; y < 4; y++)
                {
                    Span<byte> row = dst.Slice((((by * 4) + y) * dstStride) + (bx * 4), 4);
                    for (int x = 0; x < 4; x++)
                    {
                        row[x] = (byte)(mode == 0 ? value : mode == 1 ? l[(by * 4) + y + 1] : t[(bx * 4) + x + 1]);
                    }
                }
            }
        }
    }

    private static void Plane(Span<byte> dst, int dstStride, int width, int height, int a, int b, int c, int cx, int cy)
    {
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = dst.Slice(y * dstStride, width);
            for (int x = 0; x < width; x++)
            {
                row[x] = (byte)Math.Clamp((a + (b * (x - cx)) + (c * (y - cy)) + 16) >> 5, 0, 255);
            }
        }
    }
}
