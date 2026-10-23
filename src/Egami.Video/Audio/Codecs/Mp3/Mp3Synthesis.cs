using System.Runtime.CompilerServices;

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
        Span<float> t = stackalloc float[4 * 8];
        for (int k = 0; k < n; k++)
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

        SynthPair(dstr, lins[((4 * 15) + 1)..]);
        SynthPair(dstr[32..], lins[((4 * 15) + 64 + 1)..]);
        SynthPair(dstl, lins[(4 * 15)..]);
        SynthPair(dstl[32..], lins[((4 * 15) + 64)..]);

        Span<float> a = stackalloc float[4];
        Span<float> b = stackalloc float[4];
        for (int i = 14; i >= 0; i--)
        {
            int zi = Z + (4 * i);
            lins[zi] = xl[18 * (31 - i)];
            lins[zi + 1] = xr[18 * (31 - i)];
            lins[zi + 2] = xl[1 + (18 * (31 - i))];
            lins[zi + 3] = xr[1 + (18 * (31 - i))];
            lins[Z + (4 * (i + 16))] = xl[1 + (18 * (1 + i))];
            lins[Z + (4 * (i + 16)) + 1] = xr[1 + (18 * (1 + i))];
            lins[Z + (4 * (i - 16)) + 2] = xl[18 * (1 + i)];
            lins[Z + (4 * (i - 16)) + 3] = xr[18 * (1 + i)];

            int w = 0;
            for (int k = 0; k < 8; k++)
            {
                float w0 = win[w++];
                float w1 = win[w++];
                int vz = zi - (k * 64);
                int vy = zi - ((15 - k) * 64);
                for (int j = 0; j < 4; j++)
                {
                    float z = lins[vz + j];
                    float yv = lins[vy + j];
                    if (k == 0)
                    {
                        b[j] = (z * w1) + (yv * w0);
                        a[j] = (z * w0) - (yv * w1);
                    }
                    else if ((k & 1) == 0)
                    {
                        b[j] += (z * w1) + (yv * w0);
                        a[j] += (z * w0) - (yv * w1);
                    }
                    else
                    {
                        b[j] += (z * w1) + (yv * w0);
                        a[j] += (yv * w1) - (z * w0);
                    }
                }
            }

            win = win[16..];
            dstr[15 - i] = a[1] * Scale;
            dstr[17 + i] = b[1] * Scale;
            dstl[15 - i] = a[0] * Scale;
            dstl[17 + i] = b[0] * Scale;
            dstr[47 - i] = a[3] * Scale;
            dstr[49 + i] = b[3] * Scale;
            dstl[47 - i] = a[2] * Scale;
            dstl[49 + i] = b[2] * Scale;
        }
    }

    private static void SynthPair(Span<float> pcm, ReadOnlySpan<float> z)
    {
        float a;
        a = (z[14 * 64] - z[0]) * 29;
        a += (z[1 * 64] + z[13 * 64]) * 213;
        a += (z[12 * 64] - z[2 * 64]) * 459;
        a += (z[3 * 64] + z[11 * 64]) * 2037;
        a += (z[10 * 64] - z[4 * 64]) * 5153;
        a += (z[5 * 64] + z[9 * 64]) * 6574;
        a += (z[8 * 64] - z[6 * 64]) * 37489;
        a += z[7 * 64] * 75038;
        pcm[0] = a * Scale;

        z = z[2..];
        a = z[14 * 64] * 104;
        a += z[12 * 64] * 1567;
        a += z[10 * 64] * 9727;
        a += z[8 * 64] * 64019;
        a += z[6 * 64] * -9975;
        a += z[4 * 64] * -45;
        a += z[2 * 64] * 146;
        a += z[0 * 64] * -5;
        pcm[16] = a * Scale;
    }
}
