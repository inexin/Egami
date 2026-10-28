using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Egami.Video.Audio.Codecs.Mp3;

/// <summary>
/// The 32-band polyphase synthesis filterbank shared by all three layers (ISO/IEC 11172-3 section 2.4.3.2.2 and Annex A.2):
/// a 32-point DCT-II per time slot, then the windowed 512-tap sum, two slots at a time. Closely translated from minimp3's
/// mp3d_DCT_II, mp3d_synth and mp3d_synth_pair (CC0).
/// </summary>
[SkipLocalsInit]
internal static class Mp3Synthesis
{
    /// <summary>Scales the window's 2^16 fixed point and minimp3's 16-bit range to ±1.</summary>
    private const float Scale = 1f / 32768f;

    /// <summary>Runs the DCT-II on <paramref name="n"/> time slots; slot k's 32 subband samples sit at k, k + 18, ..., k + 31 * 18.</summary>
    public static void DctII(Span<float> grbuf, int n)
    {
        ReadOnlySpan<float> sec = Mp3Tables.Sec;
        int k = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            // Four time slots at once, one per lane, as minimp3's SIMD build does (the same arithmetic in the same order).
            Span<Vector128<float>> v = stackalloc Vector128<float>[4 * 8];
            ref Vector128<float> t = ref MemoryMarshal.GetReference(v);
            for (; k + 4 <= n; k += 4)
            {
                ref float y = ref MemoryMarshal.GetReference(grbuf[k..]);
                for (int i = 0; i < 8; i++)
                {
                    Vector128<float> x0 = Vector128.LoadUnsafe(ref y, (nuint)(i * 18));
                    Vector128<float> x1 = Vector128.LoadUnsafe(ref y, (nuint)((15 - i) * 18));
                    Vector128<float> x2 = Vector128.LoadUnsafe(ref y, (nuint)((16 + i) * 18));
                    Vector128<float> x3 = Vector128.LoadUnsafe(ref y, (nuint)((31 - i) * 18));
                    Vector128<float> t0 = x0 + x3;
                    Vector128<float> t1 = x1 + x2;
                    Vector128<float> t2 = (x1 - x2) * sec[(3 * i) + 0];
                    Vector128<float> t3 = (x0 - x3) * sec[(3 * i) + 1];
                    Unsafe.Add(ref t, i) = t0 + t1;
                    Unsafe.Add(ref t, 8 + i) = (t0 - t1) * sec[(3 * i) + 2];
                    Unsafe.Add(ref t, 16 + i) = t3 + t2;
                    Unsafe.Add(ref t, 24 + i) = (t3 - t2) * sec[(3 * i) + 2];
                }

                for (int i = 0; i < 4; i++)
                {
                    Dct8(ref Unsafe.Add(ref t, i * 8));
                }

                int p = 0;
                for (int i = 0; i < 7; i++, p += 4 * 18)
                {
                    Unsafe.Add(ref t, i).StoreUnsafe(ref y, (nuint)p);
                    (Unsafe.Add(ref t, 16 + i) + Unsafe.Add(ref t, 24 + i) + Unsafe.Add(ref t, 24 + i + 1)).StoreUnsafe(ref y, (nuint)(p + 18));
                    (Unsafe.Add(ref t, 8 + i) + Unsafe.Add(ref t, 8 + i + 1)).StoreUnsafe(ref y, (nuint)(p + 36));
                    (Unsafe.Add(ref t, 16 + i + 1) + Unsafe.Add(ref t, 24 + i) + Unsafe.Add(ref t, 24 + i + 1)).StoreUnsafe(ref y, (nuint)(p + 54));
                }

                Unsafe.Add(ref t, 7).StoreUnsafe(ref y, (nuint)p);
                (Unsafe.Add(ref t, 16 + 7) + Unsafe.Add(ref t, 24 + 7)).StoreUnsafe(ref y, (nuint)(p + 18));
                Unsafe.Add(ref t, 8 + 7).StoreUnsafe(ref y, (nuint)(p + 36));
                Unsafe.Add(ref t, 24 + 7).StoreUnsafe(ref y, (nuint)(p + 54));
            }
        }

        DctIIScalar(grbuf, k, n, sec);
    }

    /// <summary>The 8-point DCT of mp3d_DCT_II's second stage on four lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Dct8(ref Vector128<float> x)
    {
        Vector128<float> x0 = x, x1 = Unsafe.Add(ref x, 1), x2 = Unsafe.Add(ref x, 2), x3 = Unsafe.Add(ref x, 3);
        Vector128<float> x4 = Unsafe.Add(ref x, 4), x5 = Unsafe.Add(ref x, 5), x6 = Unsafe.Add(ref x, 6), x7 = Unsafe.Add(ref x, 7), xt;
        xt = x0 - x7;
        x0 += x7;
        x7 = x1 - x6;
        x1 += x6;
        x6 = x2 - x5;
        x2 += x5;
        x5 = x3 - x4;
        x3 += x4;
        x4 = x0 - x3;
        x0 += x3;
        x3 = x1 - x2;
        x1 += x2;
        x = x0 + x1;
        Unsafe.Add(ref x, 4) = (x0 - x1) * 0.70710677f;
        x5 += x6;
        x6 = (x6 + x7) * 0.70710677f;
        x7 += xt;
        x3 = (x3 + x4) * 0.70710677f;
        x5 -= x7 * 0.198912367f; // rotate by PI/8
        x7 += x5 * 0.382683432f;
        x5 -= x7 * 0.198912367f;
        x0 = xt - x6;
        xt += x6;
        Unsafe.Add(ref x, 1) = (xt + x7) * 0.50979561f;
        Unsafe.Add(ref x, 2) = (x4 + x3) * 0.54119611f;
        Unsafe.Add(ref x, 3) = (x0 - x5) * 0.60134488f;
        Unsafe.Add(ref x, 5) = (x0 + x5) * 0.89997619f;
        Unsafe.Add(ref x, 6) = (x4 - x3) * 1.30656302f;
        Unsafe.Add(ref x, 7) = (xt - x7) * 2.56291556f;
    }

    private static void DctIIScalar(Span<float> grbuf, int k, int n, ReadOnlySpan<float> sec)
    {
        Span<float> t = stackalloc float[4 * 8];
        for (; k < n; k++)
        {
            Span<float> y = grbuf[k..];
            for (int i = 0; i < 8; i++)
            {
                float x0 = y[i * 18];
                float x1 = y[(15 - i) * 18];
                float x2 = y[(16 + i) * 18];
                float x3 = y[(31 - i) * 18];
                float t0 = x0 + x3;
                float t1 = x1 + x2;
                float t2 = (x1 - x2) * sec[(3 * i) + 0];
                float t3 = (x0 - x3) * sec[(3 * i) + 1];
                t[i] = t0 + t1;
                t[8 + i] = (t0 - t1) * sec[(3 * i) + 2];
                t[16 + i] = t3 + t2;
                t[24 + i] = (t3 - t2) * sec[(3 * i) + 2];
            }

            for (int i = 0; i < 4; i++)
            {
                Span<float> x = t.Slice(i * 8, 8);
                float x0 = x[0], x1 = x[1], x2 = x[2], x3 = x[3], x4 = x[4], x5 = x[5], x6 = x[6], x7 = x[7], xt;
                xt = x0 - x7;
                x0 += x7;
                x7 = x1 - x6;
                x1 += x6;
                x6 = x2 - x5;
                x2 += x5;
                x5 = x3 - x4;
                x3 += x4;
                x4 = x0 - x3;
                x0 += x3;
                x3 = x1 - x2;
                x1 += x2;
                x[0] = x0 + x1;
                x[4] = (x0 - x1) * 0.70710677f;
                x5 += x6;
                x6 = (x6 + x7) * 0.70710677f;
                x7 += xt;
                x3 = (x3 + x4) * 0.70710677f;
                x5 -= x7 * 0.198912367f; // rotate by PI/8
                x7 += x5 * 0.382683432f;
                x5 -= x7 * 0.198912367f;
                x0 = xt - x6;
                xt += x6;
                x[1] = (xt + x7) * 0.50979561f;
                x[2] = (x4 + x3) * 0.54119611f;
                x[3] = (x0 - x5) * 0.60134488f;
                x[5] = (x0 + x5) * 0.89997619f;
                x[6] = (x4 - x3) * 1.30656302f;
                x[7] = (xt - x7) * 2.56291556f;
            }

            int p = 0;
            for (int i = 0; i < 7; i++, p += 4 * 18)
            {
                y[p] = t[i];
                y[p + 18] = t[16 + i] + t[24 + i] + t[24 + i + 1];
                y[p + 36] = t[8 + i] + t[8 + i + 1];
                y[p + 54] = t[16 + i + 1] + t[24 + i] + t[24 + i + 1];
            }

            y[p] = t[7];
            y[p + 18] = t[16 + 7] + t[24 + 7];
            y[p + 36] = t[8 + 7];
            y[p + 54] = t[24 + 7];
        }
    }

    /// <summary>
    /// Windows two time slots (<paramref name="xl"/>[0] and [1], with the second channel 576 floats on) into 64 samples per
    /// channel. <paramref name="lins"/> holds the 15 earlier slot pairs before the new ones, 64 floats each.
    /// </summary>
    public static void Synth(ReadOnlySpan<float> xl, Span<float> dstl, Span<float> dstr, int channels, Span<float> lins)
    {
        ReadOnlySpan<float> xr = xl[(576 * (channels - 1))..];
        ReadOnlySpan<float> win = Mp3Tables.SynthWindow;
        const int Z = 15 * 64;

        lins[Z + (4 * 15)] = xl[18 * 16];
        lins[Z + (4 * 15) + 1] = xr[18 * 16];
        lins[Z + (4 * 15) + 2] = xl[0];
        lins[Z + (4 * 15) + 3] = xr[0];

        lins[Z + (4 * 31)] = xl[1 + (18 * 16)];
        lins[Z + (4 * 31) + 1] = xr[1 + (18 * 16)];
        lins[Z + (4 * 31) + 2] = xl[1];
        lins[Z + (4 * 31) + 3] = xr[1];


        // The four lanes are left and right of the first slot, then of the second: one vector per window tap, as minimp3's SIMD
        // build does, with the same products and sums in the same order.
        ref float lin = ref MemoryMarshal.GetReference(lins);
        ref float w = ref MemoryMarshal.GetReference(win);
        ref float l = ref MemoryMarshal.GetReference(xl);
        ref float r = ref MemoryMarshal.GetReference(xr);
        ref float pl = ref MemoryMarshal.GetReference(dstl[..64]);
        ref float pr = ref MemoryMarshal.GetReference(dstr[..64]);
        SynthPair(ref pr, ref Unsafe.Add(ref lin, (4 * 15) + 1));
        SynthPair(ref Unsafe.Add(ref pr, 32), ref Unsafe.Add(ref lin, (4 * 15) + 64 + 1));
        SynthPair(ref pl, ref Unsafe.Add(ref lin, 4 * 15));
        SynthPair(ref Unsafe.Add(ref pl, 32), ref Unsafe.Add(ref lin, (4 * 15) + 64));
        for (int i = 14; i >= 0; i--, w = ref Unsafe.Add(ref w, 16))
        {
            int zi = Z + (4 * i);
            Unsafe.Add(ref lin, zi) = Unsafe.Add(ref l, 18 * (31 - i));
            Unsafe.Add(ref lin, zi + 1) = Unsafe.Add(ref r, 18 * (31 - i));
            Unsafe.Add(ref lin, zi + 2) = Unsafe.Add(ref l, 1 + (18 * (31 - i)));
            Unsafe.Add(ref lin, zi + 3) = Unsafe.Add(ref r, 1 + (18 * (31 - i)));
            Unsafe.Add(ref lin, Z + (4 * (i + 16))) = Unsafe.Add(ref l, 1 + (18 * (1 + i)));
            Unsafe.Add(ref lin, Z + (4 * (i + 16)) + 1) = Unsafe.Add(ref r, 1 + (18 * (1 + i)));
            Unsafe.Add(ref lin, Z + (4 * (i - 16)) + 2) = Unsafe.Add(ref l, 18 * (1 + i));
            Unsafe.Add(ref lin, Z + (4 * (i - 16)) + 3) = Unsafe.Add(ref r, 18 * (1 + i));

            // minimp3's S0, then S2 on odd taps (a's sign flipped) and S1 on even ones.
            ref float z = ref Unsafe.Add(ref lin, zi);
            Vector128<float> vz = Vector128.LoadUnsafe(ref z);
            Vector128<float> vy = Vector128.LoadUnsafe(ref Unsafe.Subtract(ref z, 15 * 64));
            Vector128<float> w0 = Vector128.Create(w);
            Vector128<float> w1 = Vector128.Create(Unsafe.Add(ref w, 1));
            Vector128<float> b = (vz * w1) + (vy * w0);
            Vector128<float> a = (vz * w0) - (vy * w1);
            Tap(ref z, ref w, 1, ref a, ref b, odd: true);
            Tap(ref z, ref w, 2, ref a, ref b, odd: false);
            Tap(ref z, ref w, 3, ref a, ref b, odd: true);
            Tap(ref z, ref w, 4, ref a, ref b, odd: false);
            Tap(ref z, ref w, 5, ref a, ref b, odd: true);
            Tap(ref z, ref w, 6, ref a, ref b, odd: false);
            Tap(ref z, ref w, 7, ref a, ref b, odd: true);
            a *= Scale;
            b *= Scale;
            Unsafe.Add(ref pr, 15 - i) = a.GetElement(1);
            Unsafe.Add(ref pr, 17 + i) = b.GetElement(1);
            Unsafe.Add(ref pl, 15 - i) = a.GetElement(0);
            Unsafe.Add(ref pl, 17 + i) = b.GetElement(0);
            Unsafe.Add(ref pr, 47 - i) = a.GetElement(3);
            Unsafe.Add(ref pr, 49 + i) = b.GetElement(3);
            Unsafe.Add(ref pl, 47 - i) = a.GetElement(2);
            Unsafe.Add(ref pl, 49 + i) = b.GetElement(2);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Tap(ref float z, ref float w, int k, ref Vector128<float> a, ref Vector128<float> b, bool odd)
    {
        Vector128<float> vz = Vector128.LoadUnsafe(ref Unsafe.Subtract(ref z, k * 64));
        Vector128<float> vy = Vector128.LoadUnsafe(ref Unsafe.Subtract(ref z, (15 - k) * 64));
        Vector128<float> w0 = Vector128.Create(Unsafe.Add(ref w, 2 * k));
        Vector128<float> w1 = Vector128.Create(Unsafe.Add(ref w, (2 * k) + 1));
        b += (vz * w1) + (vy * w0);
        a += odd ? (vy * w1) - (vz * w0) : (vz * w0) - (vy * w1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SynthPair(ref float pcm, ref float z)
    {
        float a;
        a = (Unsafe.Add(ref z, 14 * 64) - z) * 29;
        a += (Unsafe.Add(ref z, 1 * 64) + Unsafe.Add(ref z, 13 * 64)) * 213;
        a += (Unsafe.Add(ref z, 12 * 64) - Unsafe.Add(ref z, 2 * 64)) * 459;
        a += (Unsafe.Add(ref z, 3 * 64) + Unsafe.Add(ref z, 11 * 64)) * 2037;
        a += (Unsafe.Add(ref z, 10 * 64) - Unsafe.Add(ref z, 4 * 64)) * 5153;
        a += (Unsafe.Add(ref z, 5 * 64) + Unsafe.Add(ref z, 9 * 64)) * 6574;
        a += (Unsafe.Add(ref z, 8 * 64) - Unsafe.Add(ref z, 6 * 64)) * 37489;
        a += Unsafe.Add(ref z, 7 * 64) * 75038;
        pcm = a * Scale;

        z = ref Unsafe.Add(ref z, 2);
        a = Unsafe.Add(ref z, 14 * 64) * 104;
        a += Unsafe.Add(ref z, 12 * 64) * 1567;
        a += Unsafe.Add(ref z, 10 * 64) * 9727;
        a += Unsafe.Add(ref z, 8 * 64) * 64019;
        a += Unsafe.Add(ref z, 6 * 64) * -9975;
        a += Unsafe.Add(ref z, 4 * 64) * -45;
        a += Unsafe.Add(ref z, 2 * 64) * 146;
        a += z * -5;
        Unsafe.Add(ref pcm, 16) = a * Scale;
    }
}
