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

    /// <summary>Predicts an n by n block (n 4 or 8) in an Intra4x4 or Intra8x8 mode into <paramref name="dst"/>.</summary>
    internal static void PredictNxN(Span<byte> dst, int dstStride, int n, int mode, ReadOnlySpan<int> t, ReadOnlySpan<int> l, bool top, bool left)
    {
        // T(x) is p[x, -1] and L(y) p[-1, y], both with -1 the corner.
        int last = (2 * n) - 3;
        for (int y = 0; y < n; y++)
        {
            Span<byte> row = dst.Slice(y * dstStride, n);
            for (int x = 0; x < n; x++)
            {
                int v;
                switch (mode)
                {
                    case 0:
                        v = t[x + 1];
                        break;
                    case 1:
                        v = l[y + 1];
                        break;
                    case 2:
                        v = Dc(n, t, l, top, left);
                        break;
                    case 3:
                        v = x == n - 1 && y == n - 1
                            ? (t[(2 * n) - 1] + (3 * t[2 * n]) + 2) >> 2
                            : (t[x + y + 1] + (2 * t[x + y + 2]) + t[x + y + 3] + 2) >> 2;
                        break;
                    case 4:
                        v = x > y ? (t[x - y - 1] + (2 * t[x - y]) + t[x - y + 1] + 2) >> 2
                            : x < y ? (l[y - x - 1] + (2 * l[y - x]) + l[y - x + 1] + 2) >> 2
                            : (t[1] + (2 * t[0]) + l[1] + 2) >> 2;
                        break;
                    case 5:
                        {
                            int z = (2 * x) - y, h = x - (y >> 1);
                            v = z >= 0 && (z & 1) == 0 ? (t[h] + t[h + 1] + 1) >> 1
                                : z >= 0 ? (t[h - 1] + (2 * t[h]) + t[h + 1] + 2) >> 2
                                : z == -1 ? (l[1] + (2 * l[0]) + t[1] + 2) >> 2
                                : (l[y - (2 * x)] + (2 * l[y - (2 * x) - 1]) + l[y - (2 * x) - 2] + 2) >> 2;
                            break;
                        }

                    case 6:
                        {
                            int z = (2 * y) - x, h = y - (x >> 1);
                            v = z >= 0 && (z & 1) == 0 ? (l[h] + l[h + 1] + 1) >> 1
                                : z >= 0 ? (l[h - 1] + (2 * l[h]) + l[h + 1] + 2) >> 2
                                : z == -1 ? (l[1] + (2 * l[0]) + t[1] + 2) >> 2
                                : (t[x - (2 * y)] + (2 * t[x - (2 * y) - 1]) + t[x - (2 * y) - 2] + 2) >> 2;
                            break;
                        }

                    case 7:
                        {
                            int h = x + (y >> 1);
                            v = (y & 1) == 0 ? (t[h + 1] + t[h + 2] + 1) >> 1 : (t[h + 1] + (2 * t[h + 2]) + t[h + 3] + 2) >> 2;
                            break;
                        }

                    default:
                        {
                            int z = x + (2 * y), h = y + (x >> 1);
                            v = z > last ? l[n]
                                : z == last ? (l[n - 1] + (3 * l[n]) + 2) >> 2
                                : (z & 1) == 0 ? (l[h + 1] + l[h + 2] + 1) >> 1
                                : (l[h + 1] + (2 * l[h + 2]) + l[h + 3] + 2) >> 2;
                            break;
                        }
                }

                row[x] = (byte)v;
            }
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

        int dc = mode == 2 ? Dc(16, t, l, top, left) : 0;
        for (int y = 0; y < 16; y++)
        {
            Span<byte> row = dst.Slice(y * dstStride, 16);
            for (int x = 0; x < 16; x++)
            {
                row[x] = (byte)(mode == 0 ? t[x + 1] : mode == 1 ? l[y + 1] : dc);
            }
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
