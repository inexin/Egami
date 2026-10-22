using System.Numerics;
using System.Runtime.CompilerServices;

namespace Egami.Video.Codecs.Vp9;

/// <summary>
/// VP9 intra prediction (vp9_reconintra.c and vpx_dsp/intrapred.c): the edge pixels with VP9's availability and frame-edge rules
/// (127 above and 129 left of the frame at 8 bit, half the range minus or plus one above that), then the ten predictors. 4x4
/// blocks use libvpx's own 4x4 forms of the directional predictors, which differ from the general ones.
/// </summary>
[SkipLocalsInit]
internal static class Vp9Intra<T>
    where T : unmanaged, IBinaryInteger<T>
{
    /// <summary>
    /// Predicts one transform block in place at <c>pixels[dst]</c>. <c>haveTop</c> and <c>haveLeft</c> say whether the row above
    /// and the column to the left exist (inside the block, or a neighbor in the frame and tile); <c>haveRight</c> whether the
    /// pixels above and to the right belong to this block (used for 4x4 only); <c>extendRight</c> and <c>extendBottom</c> whether
    /// the block reaches past the frame's edge (libvpx's mb_to_right_edge and mb_to_bottom_edge below 0), when edge pixels past the
    /// frame width or height, rounded up to 8, are repeated.
    /// </summary>
    public static void Predict(Span<T> pixels, int dst, int stride, int mode, int txSize, bool haveTop, bool haveLeft, bool haveRight,
        bool extendRight, bool extendBottom, int x0, int y0, int frameWidth, int frameHeight, int bitDepth)
    {
        int bs = 4 << txSize;
        int baseValue = 128 << (bitDepth - 8);
        int extend = Vp9Tables.ExtendModes[mode];
        Span<int> left = stackalloc int[32];
        Span<int> aboveData = stackalloc int[64 + 16];
        const int A = 16;

        if ((extend & Vp9Tables.NeedLeft) != 0)
        {
            if (haveLeft)
            {
                int count = bs;
                if (extendBottom && y0 + bs > frameHeight)
                {
                    count = frameHeight - y0;
                }

                int i = 0;
                for (; i < count; i++)
                {
                    left[i] = int.CreateTruncating(pixels[dst + (i * stride) - 1]);
                }

                for (; i < bs; i++)
                {
                    left[i] = int.CreateTruncating(pixels[dst + ((count - 1) * stride) - 1]);
                }
            }
            else
            {
                left[..bs].Fill(baseValue + 1);
            }
        }

        if ((extend & Vp9Tables.NeedAbove) != 0)
        {
            if (haveTop)
            {
                int aboveRef = dst - stride;
                if (extendRight && x0 + bs > frameWidth)
                {
                    int r = frameWidth - x0;
                    Copy(pixels, aboveRef, aboveData[A..], r);
                    aboveData.Slice(A + r, x0 + bs - frameWidth).Fill(aboveData[A + r - 1]);
                }
                else
                {
                    Copy(pixels, aboveRef, aboveData[A..], bs);
                }

                aboveData[A - 1] = haveLeft ? int.CreateTruncating(pixels[aboveRef - 1]) : baseValue + 1;
            }
            else
            {
                aboveData.Slice(A, bs).Fill(baseValue - 1);
                aboveData[A - 1] = baseValue - 1;
            }
        }

        if ((extend & Vp9Tables.NeedAboveRight) != 0)
        {
            if (haveTop)
            {
                int aboveRef = dst - stride;
                if (extendRight)
                {
                    if (x0 + (2 * bs) <= frameWidth)
                    {
                        if (haveRight && bs == 4)
                        {
                            Copy(pixels, aboveRef, aboveData[A..], 2 * bs);
                        }
                        else
                        {
                            Copy(pixels, aboveRef, aboveData[A..], bs);
                            aboveData.Slice(A + bs, bs).Fill(aboveData[A + bs - 1]);
                        }
                    }
                    else if (x0 + bs <= frameWidth)
                    {
                        int r = frameWidth - x0;
                        if (haveRight && bs == 4)
                        {
                            Copy(pixels, aboveRef, aboveData[A..], r);
                            aboveData.Slice(A + r, x0 + (2 * bs) - frameWidth).Fill(aboveData[A + r - 1]);
                        }
                        else
                        {
                            Copy(pixels, aboveRef, aboveData[A..], bs);
                            aboveData.Slice(A + bs, bs).Fill(aboveData[A + bs - 1]);
                        }
                    }
                    else
                    {
                        int r = frameWidth - x0;
                        Copy(pixels, aboveRef, aboveData[A..], r);
                        aboveData.Slice(A + r, x0 + (2 * bs) - frameWidth).Fill(aboveData[A + r - 1]);
                    }
                }
                else
                {
                    Copy(pixels, aboveRef, aboveData[A..], bs);
                    if (bs == 4 && haveRight)
                    {
                        Copy(pixels, aboveRef + bs, aboveData[(A + bs)..], bs);
                    }
                    else
                    {
                        aboveData.Slice(A + bs, bs).Fill(aboveData[A + bs - 1]);
                    }
                }

                aboveData[A - 1] = haveLeft ? int.CreateTruncating(pixels[aboveRef - 1]) : baseValue + 1;
            }
            else
            {
                aboveData.Slice(A, bs * 2).Fill(baseValue - 1);
                aboveData[A - 1] = baseValue - 1;
            }
        }

        Span<T> d = pixels[dst..];
        ReadOnlySpan<int> above = aboveData[(A - 1)..];
        int max = (1 << bitDepth) - 1;
        switch (mode)
        {
            case Vp9Tables.DcPred:
                Dc(d, stride, bs, above, left, haveLeft, haveTop, baseValue);
                break;
            case Vp9Tables.VPred:
                for (int r = 0; r < bs; r++)
                {
                    for (int c = 0; c < bs; c++)
                    {
                        d[(r * stride) + c] = T.CreateTruncating(above[1 + c]);
                    }
                }

                break;
            case Vp9Tables.HPred:
                for (int r = 0; r < bs; r++)
                {
                    d.Slice(r * stride, bs).Fill(T.CreateTruncating(left[r]));
                }

                break;
            case Vp9Tables.TmPred:
                {
                    int topLeft = above[0];
                    for (int r = 0; r < bs; r++)
                    {
                        for (int c = 0; c < bs; c++)
                        {
                            d[(r * stride) + c] = T.CreateTruncating(Math.Clamp(left[r] + above[1 + c] - topLeft, 0, max));
                        }
                    }

                    break;
                }

            default:
                if (bs == 4)
                {
                    Directional4x4(d, stride, mode, above, left);
                }
                else
                {
                    Directional(d, stride, bs, mode, above, left);
                }

                break;
        }
    }

    private static void Copy(Span<T> pixels, int from, Span<int> to, int count)
    {
        for (int i = 0; i < count; i++)
        {
            to[i] = int.CreateTruncating(pixels[from + i]);
        }
    }

    private static void Dc(Span<T> d, int stride, int bs, ReadOnlySpan<int> above, ReadOnlySpan<int> left, bool haveLeft, bool haveTop, int baseValue)
    {
        int value;
        if (haveLeft && haveTop)
        {
            int sum = 0;
            for (int i = 0; i < bs; i++)
            {
                sum += above[1 + i] + left[i];
            }

            value = (sum + bs) / (2 * bs);
        }
        else if (haveLeft)
        {
            int sum = 0;
            for (int i = 0; i < bs; i++)
            {
                sum += left[i];
            }

            value = (sum + (bs >> 1)) / bs;
        }
        else if (haveTop)
        {
            int sum = 0;
            for (int i = 0; i < bs; i++)
            {
                sum += above[1 + i];
            }

            value = (sum + (bs >> 1)) / bs;
        }
        else
        {
            value = baseValue;
        }

        T v = T.CreateTruncating(value);
        for (int r = 0; r < bs; r++)
        {
            d.Slice(r * stride, bs).Fill(v);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Avg2(int a, int b) => (a + b + 1) >> 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Avg3(int a, int b, int c) => (a + (2 * b) + c + 2) >> 2;

    /// <summary>The general directional predictors for 8x8 and up. <paramref name="aboveM1"/>[0] is the top-left pixel.</summary>
    private static void Directional(Span<T> dst, int stride, int bs, int mode, ReadOnlySpan<int> aboveM1, ReadOnlySpan<int> left)
    {
        ReadOnlySpan<int> above = aboveM1[1..];
        int topLeft = aboveM1[0];
        Span<int> p = stackalloc int[32 * 32];
        switch (mode)
        {
            case Vp9Tables.D207Pred:
                for (int r = 0; r < bs - 1; r++)
                {
                    p[r * bs] = Avg2(left[r], left[r + 1]);
                }

                p[(bs - 1) * bs] = left[bs - 1];
                for (int r = 0; r < bs - 2; r++)
                {
                    p[(r * bs) + 1] = Avg3(left[r], left[r + 1], left[r + 2]);
                }

                p[((bs - 2) * bs) + 1] = Avg3(left[bs - 2], left[bs - 1], left[bs - 1]);
                p[((bs - 1) * bs) + 1] = left[bs - 1];
                for (int c = 0; c < bs - 2; c++)
                {
                    p[((bs - 1) * bs) + 2 + c] = left[bs - 1];
                }

                for (int r = bs - 2; r >= 0; r--)
                {
                    for (int c = 0; c < bs - 2; c++)
                    {
                        p[(r * bs) + 2 + c] = p[((r + 1) * bs) + c];
                    }
                }

                break;
            case Vp9Tables.D63Pred:
                for (int c = 0; c < bs; c++)
                {
                    p[c] = Avg2(above[c], above[c + 1]);
                    p[bs + c] = Avg3(above[c], above[c + 1], above[c + 2]);
                }

                for (int r = 2, size = bs - 2; r < bs; r += 2, size--)
                {
                    for (int c = 0; c < size; c++)
                    {
                        p[(r * bs) + c] = p[(r >> 1) + c];
                        p[((r + 1) * bs) + c] = p[bs + (r >> 1) + c];
                    }

                    for (int c = size; c < bs; c++)
                    {
                        p[(r * bs) + c] = above[bs - 1];
                        p[((r + 1) * bs) + c] = above[bs - 1];
                    }
                }

                break;
            case Vp9Tables.D45Pred:
                {
                    int aboveRight = above[bs - 1];
                    for (int x = 0; x < bs - 1; x++)
                    {
                        p[x] = Avg3(above[x], above[x + 1], above[x + 2]);
                    }

                    p[bs - 1] = aboveRight;
                    for (int x = 1, size = bs - 2; x < bs; x++, size--)
                    {
                        for (int c = 0; c < size; c++)
                        {
                            p[(x * bs) + c] = p[x + c];
                        }

                        for (int c = size; c < bs; c++)
                        {
                            p[(x * bs) + c] = aboveRight;
                        }
                    }

                    break;
                }

            case Vp9Tables.D117Pred:
                for (int c = 0; c < bs; c++)
                {
                    p[c] = Avg2(c == 0 ? topLeft : above[c - 1], above[c]);
                }

                p[bs] = Avg3(left[0], topLeft, above[0]);
                for (int c = 1; c < bs; c++)
                {
                    p[bs + c] = Avg3(c == 1 ? topLeft : above[c - 2], above[c - 1], above[c]);
                }

                p[2 * bs] = Avg3(topLeft, left[0], left[1]);
                for (int r = 3; r < bs; r++)
                {
                    p[r * bs] = Avg3(left[r - 3], left[r - 2], left[r - 1]);
                }

                for (int r = 2; r < bs; r++)
                {
                    for (int c = 1; c < bs; c++)
                    {
                        p[(r * bs) + c] = p[((r - 2) * bs) + c - 1];
                    }
                }

                break;
            case Vp9Tables.D135Pred:
                {
                    Span<int> border = stackalloc int[32 + 32 - 1];
                    for (int i = 0; i < bs - 2; i++)
                    {
                        border[i] = Avg3(left[bs - 3 - i], left[bs - 2 - i], left[bs - 1 - i]);
                    }

                    border[bs - 2] = Avg3(topLeft, left[0], left[1]);
                    border[bs - 1] = Avg3(left[0], topLeft, above[0]);
                    border[bs] = Avg3(topLeft, above[0], above[1]);
                    for (int i = 0; i < bs - 2; i++)
                    {
                        border[bs + 1 + i] = Avg3(above[i], above[i + 1], above[i + 2]);
                    }

                    for (int i = 0; i < bs; i++)
                    {
                        for (int c = 0; c < bs; c++)
                        {
                            p[(i * bs) + c] = border[bs - 1 - i + c];
                        }
                    }

                    break;
                }

            default: // D153
                p[0] = Avg2(topLeft, left[0]);
                for (int r = 1; r < bs; r++)
                {
                    p[r * bs] = Avg2(left[r - 1], left[r]);
                }

                p[1] = Avg3(left[0], topLeft, above[0]);
                p[bs + 1] = Avg3(topLeft, left[0], left[1]);
                for (int r = 2; r < bs; r++)
                {
                    p[(r * bs) + 1] = Avg3(left[r - 2], left[r - 1], left[r]);
                }

                for (int c = 0; c < bs - 2; c++)
                {
                    p[2 + c] = Avg3(c == 0 ? topLeft : above[c - 1], above[c], above[c + 1]);
                }

                for (int r = 1; r < bs; r++)
                {
                    for (int c = 0; c < bs - 2; c++)
                    {
                        p[(r * bs) + 2 + c] = p[((r - 1) * bs) + c];
                    }
                }

                break;
        }

        for (int r = 0; r < bs; r++)
        {
            Span<T> row = dst.Slice(r * stride, bs);
            for (int c = 0; c < bs; c++)
            {
                row[c] = T.CreateTruncating(p[(r * bs) + c]);
            }
        }
    }

    /// <summary>libvpx's 4x4 forms of the directional predictors.</summary>
    private static void Directional4x4(Span<T> dst, int stride, int mode, ReadOnlySpan<int> aboveM1, ReadOnlySpan<int> left)
    {
        int x = aboveM1[0];
        int a = aboveM1[1], b = aboveM1[2], c = aboveM1[3], d = aboveM1[4], e = aboveM1[5], f = aboveM1[6], g = aboveM1[7], h = aboveM1[8];
        int i = left[0], j = left[1], k = left[2], l = left[3];
        Span<int> p = stackalloc int[16];
        switch (mode)
        {
            case Vp9Tables.D207Pred:
                p[0] = Avg2(i, j);
                p[2] = p[4] = Avg2(j, k);
                p[6] = p[8] = Avg2(k, l);
                p[1] = Avg3(i, j, k);
                p[3] = p[5] = Avg3(j, k, l);
                p[7] = p[9] = Avg3(k, l, l);
                p[11] = p[10] = p[12] = p[13] = p[14] = p[15] = l;
                break;
            case Vp9Tables.D63Pred:
                p[0] = Avg2(a, b);
                p[1] = p[8] = Avg2(b, c);
                p[2] = p[9] = Avg2(c, d);
                p[3] = p[10] = Avg2(d, e);
                p[11] = Avg2(e, f);
                p[4] = Avg3(a, b, c);
                p[5] = p[12] = Avg3(b, c, d);
                p[6] = p[13] = Avg3(c, d, e);
                p[7] = p[14] = Avg3(d, e, f);
                p[15] = Avg3(e, f, g);
                break;
            case Vp9Tables.D45Pred:
                p[0] = Avg3(a, b, c);
                p[1] = p[4] = Avg3(b, c, d);
                p[2] = p[5] = p[8] = Avg3(c, d, e);
                p[3] = p[6] = p[9] = p[12] = Avg3(d, e, f);
                p[7] = p[10] = p[13] = Avg3(e, f, g);
                p[11] = p[14] = Avg3(f, g, h);
                p[15] = h;
                break;
            case Vp9Tables.D117Pred:
                p[0] = p[9] = Avg2(x, a);
                p[1] = p[10] = Avg2(a, b);
                p[2] = p[11] = Avg2(b, c);
                p[3] = Avg2(c, d);
                p[12] = Avg3(k, j, i);
                p[8] = Avg3(j, i, x);
                p[4] = p[13] = Avg3(i, x, a);
                p[5] = p[14] = Avg3(x, a, b);
                p[6] = p[15] = Avg3(a, b, c);
                p[7] = Avg3(b, c, d);
                break;
            case Vp9Tables.D135Pred:
                p[12] = Avg3(j, k, l);
                p[13] = p[8] = Avg3(i, j, k);
                p[14] = p[9] = p[4] = Avg3(x, i, j);
                p[15] = p[10] = p[5] = p[0] = Avg3(a, x, i);
                p[11] = p[6] = p[1] = Avg3(b, a, x);
                p[7] = p[2] = Avg3(c, b, a);
                p[3] = Avg3(d, c, b);
                break;
            default: // D153
                p[0] = p[6] = Avg2(i, x);
                p[4] = p[10] = Avg2(j, i);
                p[8] = p[14] = Avg2(k, j);
                p[12] = Avg2(l, k);
                p[3] = Avg3(a, b, c);
                p[2] = Avg3(x, a, b);
                p[1] = p[7] = Avg3(i, x, a);
                p[5] = p[11] = Avg3(j, i, x);
                p[9] = p[15] = Avg3(k, j, i);
                p[13] = Avg3(l, k, j);
                break;
        }

        for (int r = 0; r < 4; r++)
        {
            for (int col = 0; col < 4; col++)
            {
                dst[(r * stride) + col] = T.CreateTruncating(p[(r * 4) + col]);
            }
        }
    }
}
