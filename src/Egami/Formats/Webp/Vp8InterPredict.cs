using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Egami.Formats.Webp;

/// <summary>
/// VP8 inter prediction (RFC 6386 section 18): a block of a reference plane displaced by a motion vector in eighths of a pixel,
/// whole pixels copied, fractions through the six-tap filters (version 0) or the bilinear ones (versions 1 to 3), horizontally
/// first. Each filter pass rounds and clamps to 0 to 255, as libvpx's does, so the result is the same whatever the block size. A
/// block that reads past the plane's border is first gathered with its coordinates clamped to the plane, which is what the border
/// would hold had it been wide enough. With vectors, eight pixels go at a time in 16-bit lanes: the six-tap filters' positive taps
/// and their negative ones are summed apart (neither sum leaves 16 bits unsigned) and subtracted with saturation, which rounds and
/// clamps exactly as the scalar filters do. Vector loads read up to 16 bytes past the pixels used, so the planes carry
/// <see cref="Slack"/> spare bytes at their end.
/// </summary>
internal static class Vp8InterPredict
{
    /// <summary>The bytes a plane needs past its last pixel for the vector loads.</summary>
    internal const int Slack = 32;

    /// <summary>
    /// Predicts the <paramref name="w"/> × <paramref name="h"/> block at (<paramref name="x"/>, <paramref name="y"/>) of a plane
    /// <paramref name="width"/> × <paramref name="height"/> with <paramref name="border"/> pixels of edge copies around it, moved by
    /// (<paramref name="mvCol"/>, <paramref name="mvRow"/>) eighths, into <paramref name="dst"/>.
    /// </summary>
    [SkipLocalsInit]
    internal static void Predict(ReadOnlySpan<byte> plane, int origin, int stride, int width, int height, int border, int x, int y, int w, int h, int mvRow, int mvCol, bool sixTap, Span<byte> dst, int dstAt, int dstStride)
    {
        int ix = x + (mvCol >> 3);
        int iy = y + (mvRow >> 3);
        int fx = mvCol & 7;
        int fy = mvRow & 7;
        int before = (fx | fy) == 0 || !sixTap ? 0 : 2;
        int after = (fx | fy) == 0 ? 0 : sixTap ? 3 : 1;
        if (ix - before >= -border && ix + w - 1 + after < width + border && iy - before >= -border && iy + h - 1 + after < height + border)
        {
            Filter(plane, origin + (iy * stride) + ix, stride, w, h, fx, fy, sixTap, dst, dstAt, dstStride);
            return;
        }

        // Far outside: gather the block and its taps with clamped coordinates.
        int gw = w + 5;
        Span<byte> gathered = stackalloc byte[(gw * (h + 5)) + Slack];
        for (int r = 0; r < h + 5; r++)
        {
            int sy = Math.Clamp(iy - 2 + r, 0, height - 1);
            int row = origin + (sy * stride);
            for (int c = 0; c < gw; c++)
            {
                gathered[(r * gw) + c] = plane[row + Math.Clamp(ix - 2 + c, 0, width - 1)];
            }
        }

        Filter(gathered, (2 * gw) + 2, gw, w, h, fx, fy, sixTap, dst, dstAt, dstStride);
    }

    /// <summary>
    /// Predicts the 8 × 8 blocks of U and V that one vector moves together, as <see cref="Predict"/> would one at a time: with
    /// 256-bit vectors and the six-tap filters each row of U and of V shares one vector.
    /// </summary>
    [SkipLocalsInit]
    internal static void PredictChroma(ReadOnlySpan<byte> u, ReadOnlySpan<byte> v, int origin, int stride, int width, int height, int border, int x, int y, int mvRow, int mvCol, bool sixTap, Span<byte> dstU, Span<byte> dstV, int dstAt, int dstStride)
    {
        int ix = x + (mvCol >> 3);
        int iy = y + (mvRow >> 3);
        int fx = mvCol & 7;
        int fy = mvRow & 7;
        if (!sixTap || (fx | fy) == 0 || !Vector256.IsHardwareAccelerated || ix - 2 < -border || ix + 8 + 2 >= width + border || iy - 2 < -border || iy + 8 + 2 >= height + border)
        {
            Predict(u, origin, stride, width, height, border, x, y, 8, 8, mvRow, mvCol, sixTap, dstU, dstAt, dstStride);
            Predict(v, origin, stride, width, height, border, x, y, 8, 8, mvRow, mvCol, sixTap, dstV, dstAt, dstStride);
            return;
        }

        int at = origin + (iy * stride) + ix;
        _ = u.Slice(at - (2 * stride) - 2, (12 * stride) + 16);
        _ = v.Slice(at - (2 * stride) - 2, (12 * stride) + 16);
        _ = dstU.Slice(dstAt, (7 * dstStride) + 8);
        _ = dstV.Slice(dstAt, (7 * dstStride) + 8);
        ref byte su = ref Unsafe.Add(ref MemoryMarshal.GetReference(u), at);
        ref byte sv = ref Unsafe.Add(ref MemoryMarshal.GetReference(v), at);
        ref byte du = ref Unsafe.Add(ref MemoryMarshal.GetReference(dstU), dstAt);
        ref byte dv = ref Unsafe.Add(ref MemoryMarshal.GetReference(dstV), dstAt);
        if (fy == 0)
        {
            PairPass(ref su, ref sv, stride, 1, 8, new Taps256(fx), ref du, ref dv, dstStride);
            return;
        }

        if (fx == 0)
        {
            PairPass(ref su, ref sv, stride, stride, 8, new Taps256(fy), ref du, ref dv, dstStride);
            return;
        }

        // Horizontal over 13 rows into U and V side by side, then vertical.
        Span<byte> first = stackalloc byte[(16 * 13) + Slack];
        ref byte f = ref MemoryMarshal.GetReference(first);
        PairPass(ref Unsafe.Subtract(ref su, 2 * stride), ref Unsafe.Subtract(ref sv, 2 * stride), stride, 1, 13, new Taps256(fx), ref f, ref Unsafe.Add(ref f, 8), 16);
        Taps256 t = new(fy);
        Vector256<ushort> m2 = Lanes16(ref f, 0), m1 = Lanes16(ref f, 16), z = Lanes16(ref f, 32), n1 = Lanes16(ref f, 48), n2 = Lanes16(ref f, 64);
        for (int r = 0; r < 8; r++)
        {
            Vector256<ushort> n3 = Lanes16(ref f, (r + 5) * 16);
            Vector128<byte> o = t.Apply(m2, m1, z, n1, n2, n3);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref du, r * dstStride), o.AsUInt64().ToScalar());
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dv, r * dstStride), o.AsUInt64().GetElement(1));
            (m2, m1, z, n1, n2) = (m1, z, n1, n2, n3);
        }
    }

    /// <summary>One six-tap pass over 8 pixels of U and 8 of V at a time, written to their own rows.</summary>
    private static void PairPass(ref byte u, ref byte v, int stride, int step, int h, Taps256 t, ref byte du, ref byte dv, int dstStride)
    {
        for (int r = 0; r < h; r++)
        {
            ref byte pu = ref Unsafe.Add(ref u, r * stride);
            ref byte pv = ref Unsafe.Add(ref v, r * stride);
            Vector128<byte> o = t.Apply(Pair(ref pu, ref pv, -2 * step), Pair(ref pu, ref pv, -step), Pair(ref pu, ref pv, 0), Pair(ref pu, ref pv, step), Pair(ref pu, ref pv, 2 * step), Pair(ref pu, ref pv, 3 * step));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref du, r * dstStride), o.AsUInt64().ToScalar());
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dv, r * dstStride), o.AsUInt64().GetElement(1));
        }
    }

    /// <summary>Loads 8 bytes of U and 8 of V at the same offset into 16 16-bit lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ushort> Pair(ref byte u, ref byte v, int offset) =>
        Vector256.WidenLower(Vector128.Create(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref u, offset)), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref v, offset))).AsByte().ToVector256Unsafe());

    /// <summary>A six-tap filter's taps in 16 lanes, the negative ones negated.</summary>
    private readonly struct Taps256
    {
        private readonly Vector256<ushort> _f0, _f1, _f2, _f3, _f4, _f5;

        internal Taps256(int filter)
        {
            ReadOnlySpan<short> taps = Vp8Tables.SixTap.AsSpan(filter * 6, 6);
            _f0 = Vector256.Create((ushort)taps[0]);
            _f1 = Vector256.Create((ushort)-taps[1]);
            _f2 = Vector256.Create((ushort)taps[2]);
            _f3 = Vector256.Create((ushort)taps[3]);
            _f4 = Vector256.Create((ushort)-taps[4]);
            _f5 = Vector256.Create((ushort)taps[5]);
        }

        /// <summary>Filters 16 positions from the pixels at taps −2 to +3: rounded, shifted by 7, clamped to 0 to 255.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Vector128<byte> Apply(Vector256<ushort> m2, Vector256<ushort> m1, Vector256<ushort> z, Vector256<ushort> p1, Vector256<ushort> p2, Vector256<ushort> p3)
        {
            Vector256<ushort> pos = (m2 * _f0) + (z * _f2) + (p1 * _f3) + (p3 * _f5) + Vector256.Create((ushort)64);
            Vector256<ushort> neg = (m1 * _f1) + (p2 * _f4);
            Vector256<ushort> r = Vector256.Min(Vector256.ShiftRightLogical(Vector256.Max(pos, neg) - neg, 7), Vector256.Create((ushort)255));
            return Vector256.Narrow(r, r).GetLower();
        }
    }

    private static void Filter(ReadOnlySpan<byte> src, int at, int stride, int w, int h, int fx, int fy, bool sixTap, Span<byte> dst, int dstAt, int dstStride)
    {
        if ((fx | fy) == 0)
        {
            // A whole-pixel vector copies the block: rows of 16, 8 or 4 bytes as one load and store each, rather than a call to
            // the general copy for every row, once the whole block is known to lie inside both spans.
            _ = src.Slice(at, ((h - 1) * stride) + w);
            _ = dst.Slice(dstAt, ((h - 1) * dstStride) + w);
            ref byte s = ref Unsafe.Add(ref MemoryMarshal.GetReference(src), at);
            ref byte d = ref Unsafe.Add(ref MemoryMarshal.GetReference(dst), dstAt);
            if (w == 16)
            {
                for (int r = 0; r < h; r++)
                {
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, r * dstStride), Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref s, r * stride)));
                }
            }
            else if (w == 8)
            {
                for (int r = 0; r < h; r++)
                {
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, r * dstStride), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref s, r * stride)));
                }
            }
            else if (w == 4)
            {
                for (int r = 0; r < h; r++)
                {
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, r * dstStride), Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref s, r * stride)));
                }
            }
            else
            {
                for (int r = 0; r < h; r++)
                {
                    MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref s, r * stride), w).CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.Add(ref d, r * dstStride), w));
                }
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            ref byte s = ref Unsafe.Add(ref MemoryMarshal.GetReference(src), at);
            ref byte d = ref Unsafe.Add(ref MemoryMarshal.GetReference(dst), dstAt);
            if (sixTap)
            {
                SixTapVector(ref s, stride, w, h, fx, fy, ref d, dstStride);
            }
            else
            {
                BilinearVector(ref s, stride, w, h, fx, fy, ref d, dstStride);
            }
        }
        else if (sixTap)
        {
            SixTap(src, at, stride, w, h, fx, fy, dst, dstAt, dstStride);
        }
        else
        {
            Bilinear(src, at, stride, w, h, fx, fy, dst, dstAt, dstStride);
        }
    }

    private static void SixTap(ReadOnlySpan<byte> src, int at, int stride, int w, int h, int fx, int fy, Span<byte> dst, int dstAt, int dstStride)
    {
        ReadOnlySpan<short> hf = Vp8Tables.SixTap.AsSpan(fx * 6, 6);
        ReadOnlySpan<short> vf = Vp8Tables.SixTap.AsSpan(fy * 6, 6);
        if (fy == 0)
        {
            Pass(src, at, stride, 1, w, h, hf, dst, dstAt, dstStride);
            return;
        }

        if (fx == 0)
        {
            Pass(src, at, stride, stride, w, h, vf, dst, dstAt, dstStride);
            return;
        }

        // Horizontal over the rows the vertical taps need (−2 to +3), then vertical.
        Span<byte> first = stackalloc byte[16 * 21];
        Pass(src, at - (2 * stride), stride, 1, w, h + 5, hf, first, 0, 16);
        Pass(first, 2 * 16, 16, 16, w, h, vf, dst, dstAt, dstStride);
    }

    /// <summary>One six-tap pass: taps at −2 to +3 steps of <paramref name="step"/>, rounded, shifted by 7 and clamped.</summary>
    private static void Pass(ReadOnlySpan<byte> src, int at, int stride, int step, int w, int h, ReadOnlySpan<short> f, Span<byte> dst, int dstAt, int dstStride)
    {
        int f0 = f[0], f1 = f[1], f2 = f[2], f3 = f[3], f4 = f[4], f5 = f[5];
        for (int r = 0; r < h; r++)
        {
            int s = at + (r * stride);
            int d = dstAt + (r * dstStride);
            for (int c = 0; c < w; c++, s++)
            {
                int v = (src[s - (2 * step)] * f0) + (src[s - step] * f1) + (src[s] * f2) + (src[s + step] * f3) + (src[s + (2 * step)] * f4) + (src[s + (3 * step)] * f5) + 64;
                dst[d + c] = Vp8Dsp.Clip8(v >> 7);
            }
        }
    }

    private static void Bilinear(ReadOnlySpan<byte> src, int at, int stride, int w, int h, int fx, int fy, Span<byte> dst, int dstAt, int dstStride)
    {
        int h0 = Vp8Tables.Bilinear[2 * fx], h1 = Vp8Tables.Bilinear[(2 * fx) + 1];
        int v0 = Vp8Tables.Bilinear[2 * fy], v1 = Vp8Tables.Bilinear[(2 * fy) + 1];

        // Horizontal over h + 1 rows, then vertical; neither can leave 0 to 255.
        Span<byte> first = stackalloc byte[16 * 17];
        for (int r = 0; r <= h; r++)
        {
            int s = at + (r * stride);
            for (int c = 0; c < w; c++)
            {
                first[(r * 16) + c] = (byte)(((src[s + c] * h0) + (src[s + c + 1] * h1) + 64) >> 7);
            }
        }

        for (int r = 0; r < h; r++)
        {
            int d = dstAt + (r * dstStride);
            for (int c = 0; c < w; c++)
            {
                dst[d + c] = (byte)(((first[(r * 16) + c] * v0) + (first[((r + 1) * 16) + c] * v1) + 64) >> 7);
            }
        }
    }

    [SkipLocalsInit]
    private static void SixTapVector(ref byte src, int stride, int w, int h, int fx, int fy, ref byte dst, int dstStride)
    {
        if (fy == 0)
        {
            SixTapPass(ref src, stride, 1, w, h, fx, ref dst, dstStride);
            return;
        }

        if (fx == 0)
        {
            SixTapPass(ref src, stride, stride, w, h, fy, ref dst, dstStride);
            return;
        }

        Span<byte> first = stackalloc byte[(16 * 21) + Slack];
        ref byte f = ref MemoryMarshal.GetReference(first);
        SixTapPass(ref Unsafe.Subtract(ref src, 2 * stride), stride, 1, w, h + 5, fx, ref f, 16);
        SixTapPass(ref Unsafe.Add(ref f, 2 * 16), 16, 16, w, h, fy, ref dst, dstStride);
    }

    /// <summary>One six-tap pass on 8 pixels at a time, 16 on a 16-wide block: taps at -2 to +3 steps of <paramref name="step"/>.</summary>
    private static void SixTapPass(ref byte src, int stride, int step, int w, int h, int filter, ref byte dst, int dstStride)
    {
        ReadOnlySpan<short> taps = Vp8Tables.SixTap.AsSpan(filter * 6, 6);
        if (w == 16)
        {
            SixTapPass16(ref src, stride, step, h, taps, ref dst, dstStride);
            return;
        }

        // The taps' signs are the same in every filter: + - + + - +.
        Vector128<ushort> f0 = Vector128.Create((ushort)taps[0]), f1 = Vector128.Create((ushort)-taps[1]), f2 = Vector128.Create((ushort)taps[2]);
        Vector128<ushort> f3 = Vector128.Create((ushort)taps[3]), f4 = Vector128.Create((ushort)-taps[4]), f5 = Vector128.Create((ushort)taps[5]);
        Vector128<ushort> round = Vector128.Create((ushort)64), max = Vector128.Create((ushort)255);
        for (int r = 0; r < h; r++)
        {
            ref byte row = ref Unsafe.Add(ref src, r * stride);
            ref byte o = ref Unsafe.Add(ref dst, r * dstStride);
            for (int c = 0; c < w; c += 8)
            {
                ref byte p = ref Unsafe.Add(ref row, c);
                Vector128<ushort> pos = (Lanes(ref p, -2 * step) * f0) + (Lanes(ref p, 0) * f2) + (Lanes(ref p, step) * f3) + (Lanes(ref p, 3 * step) * f5) + round;
                Vector128<ushort> neg = (Lanes(ref p, -step) * f1) + (Lanes(ref p, 2 * step) * f4);
                Vector128<ushort> v = Vector128.Min(Vector128.ShiftRightLogical(Vector128.Max(pos, neg) - neg, 7), max);
                Store(Vector128.Narrow(v, v), ref Unsafe.Add(ref o, c), Math.Min(8, w - c));
            }
        }
    }

    private static void SixTapPass16(ref byte src, int stride, int step, int h, ReadOnlySpan<short> taps, ref byte dst, int dstStride)
    {
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<ushort> f0 = Vector256.Create((ushort)taps[0]), f1 = Vector256.Create((ushort)-taps[1]), f2 = Vector256.Create((ushort)taps[2]);
            Vector256<ushort> f3 = Vector256.Create((ushort)taps[3]), f4 = Vector256.Create((ushort)-taps[4]), f5 = Vector256.Create((ushort)taps[5]);
            Vector256<ushort> round = Vector256.Create((ushort)64), max = Vector256.Create((ushort)255);
            if (step == stride)
            {
                // Vertical: consecutive rows share five of their six input rows, so each row loads one.
                Vector256<ushort> m2 = Lanes16(ref src, -2 * stride), m1 = Lanes16(ref src, -stride), z = Lanes16(ref src, 0);
                Vector256<ushort> n1 = Lanes16(ref src, stride), n2 = Lanes16(ref src, 2 * stride);
                for (int r = 0; r < h; r++)
                {
                    Vector256<ushort> n3 = Lanes16(ref src, (r + 3) * stride);
                    Vector256<ushort> pos = (m2 * f0) + (z * f2) + (n1 * f3) + (n3 * f5) + round;
                    Vector256<ushort> neg = (m1 * f1) + (n2 * f4);
                    Vector256<ushort> v = Vector256.Min(Vector256.ShiftRightLogical(Vector256.Max(pos, neg) - neg, 7), max);
                    Vector256.Narrow(v, v).GetLower().StoreUnsafe(ref Unsafe.Add(ref dst, r * dstStride));
                    (m2, m1, z, n1, n2) = (m1, z, n1, n2, n3);
                }

                return;
            }

            for (int r = 0; r < h; r++)
            {
                ref byte p = ref Unsafe.Add(ref src, r * stride);
                Vector256<ushort> pos = (Lanes16(ref p, -2 * step) * f0) + (Lanes16(ref p, 0) * f2) + (Lanes16(ref p, step) * f3) + (Lanes16(ref p, 3 * step) * f5) + round;
                Vector256<ushort> neg = (Lanes16(ref p, -step) * f1) + (Lanes16(ref p, 2 * step) * f4);
                Vector256<ushort> v = Vector256.Min(Vector256.ShiftRightLogical(Vector256.Max(pos, neg) - neg, 7), max);
                Vector256.Narrow(v, v).GetLower().StoreUnsafe(ref Unsafe.Add(ref dst, r * dstStride));
            }

            return;
        }

        Vector128<ushort> g0 = Vector128.Create((ushort)taps[0]), g1 = Vector128.Create((ushort)-taps[1]), g2 = Vector128.Create((ushort)taps[2]);
        Vector128<ushort> g3 = Vector128.Create((ushort)taps[3]), g4 = Vector128.Create((ushort)-taps[4]), g5 = Vector128.Create((ushort)taps[5]);
        Vector128<ushort> round128 = Vector128.Create((ushort)64), max128 = Vector128.Create((ushort)255);
        for (int r = 0; r < h; r++)
        {
            ref byte p = ref Unsafe.Add(ref src, r * stride);
            (Vector128<ushort> a0, Vector128<ushort> b0) = Vector128.Widen(Vector128.LoadUnsafe(ref Unsafe.Add(ref p, -2 * step)));
            (Vector128<ushort> a1, Vector128<ushort> b1) = Vector128.Widen(Vector128.LoadUnsafe(ref Unsafe.Add(ref p, -step)));
            (Vector128<ushort> a2, Vector128<ushort> b2) = Vector128.Widen(Vector128.LoadUnsafe(ref p));
            (Vector128<ushort> a3, Vector128<ushort> b3) = Vector128.Widen(Vector128.LoadUnsafe(ref Unsafe.Add(ref p, step)));
            (Vector128<ushort> a4, Vector128<ushort> b4) = Vector128.Widen(Vector128.LoadUnsafe(ref Unsafe.Add(ref p, 2 * step)));
            (Vector128<ushort> a5, Vector128<ushort> b5) = Vector128.Widen(Vector128.LoadUnsafe(ref Unsafe.Add(ref p, 3 * step)));
            Vector128<ushort> posA = (a0 * g0) + (a2 * g2) + (a3 * g3) + (a5 * g5) + round128, negA = (a1 * g1) + (a4 * g4);
            Vector128<ushort> posB = (b0 * g0) + (b2 * g2) + (b3 * g3) + (b5 * g5) + round128, negB = (b1 * g1) + (b4 * g4);
            Vector128<ushort> va = Vector128.Min(Vector128.ShiftRightLogical(Vector128.Max(posA, negA) - negA, 7), max128);
            Vector128<ushort> vb = Vector128.Min(Vector128.ShiftRightLogical(Vector128.Max(posB, negB) - negB, 7), max128);
            Vector128.Narrow(va, vb).StoreUnsafe(ref Unsafe.Add(ref dst, r * dstStride));
        }
    }

    /// <summary>Loads the 16 bytes at <paramref name="p"/> + <paramref name="offset"/> into 16-bit lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ushort> Lanes16(ref byte p, int offset) => Vector256.WidenLower(Vector128.LoadUnsafe(ref Unsafe.Add(ref p, offset)).ToVector256Unsafe());

    [SkipLocalsInit]
    private static void BilinearVector(ref byte src, int stride, int w, int h, int fx, int fy, ref byte dst, int dstStride)
    {
        Vector128<ushort> h0 = Vector128.Create((ushort)Vp8Tables.Bilinear[2 * fx]), h1 = Vector128.Create((ushort)Vp8Tables.Bilinear[(2 * fx) + 1]);
        Vector128<ushort> v0 = Vector128.Create((ushort)Vp8Tables.Bilinear[2 * fy]), v1 = Vector128.Create((ushort)Vp8Tables.Bilinear[(2 * fy) + 1]);
        Vector128<ushort> round = Vector128.Create((ushort)64);
        Span<byte> first = stackalloc byte[(16 * 17) + Slack];
        ref byte f = ref MemoryMarshal.GetReference(first);
        for (int r = 0; r <= h; r++)
        {
            ref byte row = ref Unsafe.Add(ref src, r * stride);
            for (int c = 0; c < w; c += 8)
            {
                ref byte p = ref Unsafe.Add(ref row, c);
                Vector128<ushort> v = Vector128.ShiftRightLogical((Lanes(ref p, 0) * h0) + (Lanes(ref p, 1) * h1) + round, 7);
                Store(Vector128.Narrow(v, v), ref Unsafe.Add(ref f, (r * 16) + c), 8);
            }
        }

        for (int r = 0; r < h; r++)
        {
            ref byte o = ref Unsafe.Add(ref dst, r * dstStride);
            for (int c = 0; c < w; c += 8)
            {
                ref byte p = ref Unsafe.Add(ref f, (r * 16) + c);
                Vector128<ushort> v = Vector128.ShiftRightLogical((Lanes(ref p, 0) * v0) + (Lanes(ref p, 16) * v1) + round, 7);
                Store(Vector128.Narrow(v, v), ref Unsafe.Add(ref o, c), Math.Min(8, w - c));
            }
        }
    }

    /// <summary>Loads the 8 bytes at <paramref name="p"/> + <paramref name="offset"/> into 16-bit lanes (reading 16).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> Lanes(ref byte p, int offset) => Vector128.WidenLower(Vector128.LoadUnsafe(ref Unsafe.Add(ref p, offset)));

    /// <summary>Stores the first 4 or 8 bytes of <paramref name="v"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store(Vector128<byte> v, ref byte at, int count)
    {
        if (count == 8)
        {
            Unsafe.WriteUnaligned(ref at, v.AsUInt64().ToScalar());
        }
        else
        {
            Unsafe.WriteUnaligned(ref at, v.AsUInt32().ToScalar());
        }
    }
}
