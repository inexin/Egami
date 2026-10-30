using System.Buffers;

using Egami.Pixels;

namespace Egami.Operations.Compose;

/// <summary>
/// Blends straight ink tints onto premultiplied ink planes, as ISO 32000-2 section 11.3 has it for subtractive spaces: the math
/// Egami.Drawing's ink canvas draws with and the PSD flattener flattens CMYK layers with. Planes hold ink premultiplied by alpha;
/// blending works on the complement 1 − tint, so Multiply darkens as on screen. Source-over is done per plane; every other mode
/// goes through the RGB blend kernels, three planes at a time as the red, green and blue of a straight colour, so each mode's
/// arithmetic is exactly the RGB canvas's. The non-separable modes take Cyan, Magenta and Yellow as one colour; Black then comes
/// from the backdrop (from the source for Luminosity), and spots and extra process inks blend Normal (PDF allows only separable
/// modes on spots). Difference and Exclusion blend spots Normal too, as Ghostscript and MuPDF do: on complements they would print
/// full ink where neither side has any. A plane a paint does not paint (overprint) keeps its colour, its value only rescaled to the
/// new alpha.
/// </summary>
internal static class InkBlend
{
    /// <summary>The planes of an ink set: how many, how many are process inks, which are Cyan, Magenta and Yellow, which is Black.</summary>
    /// <param name="Planes">The ink planes, without alpha.</param>
    /// <param name="ProcessPlanes">The process inks, the first planes.</param>
    /// <param name="Cmy">The planes of Cyan, Magenta and Yellow, or <see langword="null"/> when the set has not all three.</param>
    /// <param name="Black">The plane of Black, or −1.</param>
    internal readonly record struct InkLayout(int Planes, int ProcessPlanes, int[]? Cmy, int Black);

    /// <summary>
    /// Blends <paramref name="n"/> pixels. <paramref name="tints"/> holds every plane's straight tint per pixel
    /// (<paramref name="stride"/> = the plane count), or one colour for all (<paramref name="stride"/> = 0); <paramref name="alpha"/>
    /// the source alpha per pixel, or one for all; <paramref name="coverage"/> one byte per pixel, or empty for full;
    /// <paramref name="painted"/> one flag per plane, or empty for every plane. <paramref name="d"/> is the backdrop and
    /// <paramref name="r"/> receives the result, both plane by plane, <paramref name="n"/> values each, alpha last, premultiplied,
    /// 0 to 1. <paramref name="x"/> and <paramref name="y"/> place the first pixel, for <see cref="BlendMode.Dissolve"/>.
    /// </summary>
    internal static void Mix(
        InkLayout layout, int n, ReadOnlySpan<float> tints, int stride, ReadOnlySpan<float> alpha, ReadOnlySpan<byte> coverage,
        float opacity, BlendMode mode, ReadOnlySpan<bool> painted, ReadOnlySpan<float> d, Span<float> r, int x = 0, int y = 0)
    {
        int planes = layout.Planes;
        ReadOnlySpan<float> ad = d.Slice(planes * n, n);
        Span<float> ar = r.Slice(planes * n, n);
        if (mode == BlendMode.SourceOver)
        {
            for (int i = 0; i < n; i++)
            {
                float s = Alpha(alpha, i) * opacity * Cover(coverage, i);
                ar[i] = s + (ad[i] * (1 - s));
            }

            for (int p = 0; p < planes; p++)
            {
                bool paints = painted.IsEmpty || painted[p];
                for (int i = 0; i < n; i++)
                {
                    int at = (p * n) + i;
                    if (paints)
                    {
                        float s = Alpha(alpha, i) * opacity * Cover(coverage, i);
                        r[at] = (Tint(tints, stride, i, p) * s) + (d[at] * (1 - s));
                    }
                    else
                    {
                        r[at] = ad[i] > 0 ? ar[i] * Math.Clamp(d[at] / ad[i], 0, 1) : 0;
                    }
                }
            }

            return;
        }

        bool nonSeparable = BlendReference.IsNonSeparable(mode);
        int[]? cmy = layout.Cmy;
        int black = layout.Black;
        bool[] done = new bool[planes];
        bool alphaSet = false;
        RgbaF32[] src = ArrayPool<RgbaF32>.Shared.Rent(n), dst = ArrayPool<RgbaF32>.Shared.Rent(n);
        try
        {
            // Triples through the RGB kernels: Cyan, Magenta and Yellow for the non-separable modes, every plane otherwise.
            List<int[]> triples = [];
            if (nonSeparable)
            {
                if (cmy is not null)
                {
                    triples.Add(cmy);
                }
            }
            else
            {
                // Difference and Exclusion would put full ink where neither side has any; on spots they blend Normal, as
                // Ghostscript and MuPDF have them.
                int blended = mode is BlendMode.Difference or BlendMode.Exclusion ? layout.ProcessPlanes : planes;
                for (int p = 0; p < blended; p += 3)
                {
                    triples.Add([p, Math.Min(p + 1, blended - 1), Math.Min(p + 2, blended - 1)]);
                }
            }

            BlendRows<RgbaF32> rows = new(mode, opacity, null);
            bool changes = mode != BlendMode.Destination && !(opacity == 0 && BlendReference.KeepsDestinationAtZeroOpacity(mode));
            foreach (int[] triple in triples)
            {
                for (int i = 0; i < n; i++)
                {
                    src[i] = new RgbaF32(1 - Tint(tints, stride, i, triple[0]), 1 - Tint(tints, stride, i, triple[1]), 1 - Tint(tints, stride, i, triple[2]), Alpha(alpha, i));
                    dst[i] = new RgbaF32(Colour(d, ad, n, triple[0], i), Colour(d, ad, n, triple[1], i), Colour(d, ad, n, triple[2], i), ad[i]);
                }

                if (changes)
                {
                    rows.Blend(src.AsSpan(0, n), dst.AsSpan(0, n), coverage, x, y);
                }

                for (int i = 0; i < n; i++)
                {
                    RgbaF32 c = dst[i];
                    float a = Math.Clamp(c.A, 0, 1);
                    if (!alphaSet)
                    {
                        ar[i] = a;
                    }

                    r[(triple[0] * n) + i] = a * Math.Clamp(1 - c.R, 0, 1);
                    r[(triple[1] * n) + i] = a * Math.Clamp(1 - c.G, 0, 1);
                    r[(triple[2] * n) + i] = a * Math.Clamp(1 - c.B, 0, 1);
                }

                alphaSet = true;
                foreach (int p in triple)
                {
                    done[p] = true;
                }
            }

            // The rest: Normal, or Black from the backdrop under the non-separable modes but Luminosity.
            for (int p = 0; p < planes; p++)
            {
                if (done[p])
                {
                    continue;
                }

                bool keepBackdrop = nonSeparable && p == black && mode != BlendMode.Luminosity && cmy is not null;
                for (int i = 0; i < n; i++)
                {
                    float c = Cover(coverage, i), sa = Alpha(alpha, i) * opacity, ba = ad[i];
                    float cb = Colour(d, ad, n, p, i), cs = 1 - Tint(tints, stride, i, p);
                    float blended = keepBackdrop ? cb : cs;
                    float full = sa + ba - (sa * ba);
                    float colour = ((1 - sa) * ba * cb) + ((1 - ba) * sa * cs) + (sa * ba * blended);
                    float resultAlpha = ((1 - c) * ba) + (c * full);
                    float resultColour = ((1 - c) * ba * cb) + (c * colour);
                    if (!alphaSet)
                    {
                        ar[i] = resultAlpha;
                    }

                    r[(p * n) + i] = Math.Clamp(resultAlpha - resultColour, 0, 1);
                }

                alphaSet = true;
            }
        }
        finally
        {
            ArrayPool<RgbaF32>.Shared.Return(src);
            ArrayPool<RgbaF32>.Shared.Return(dst);
        }

        // Planes the paint leaves alone keep their colour under the new alpha.
        if (!painted.IsEmpty)
        {
            for (int p = 0; p < planes; p++)
            {
                if (!painted[p])
                {
                    for (int i = 0; i < n; i++)
                    {
                        int at = (p * n) + i;
                        r[at] = ad[i] > 0 ? ar[i] * Math.Clamp(d[at] / ad[i], 0, 1) : 0;
                    }
                }
            }
        }
    }

    private static float Tint(ReadOnlySpan<float> tints, int stride, int i, int p) => tints[(stride * i) + p];

    private static float Alpha(ReadOnlySpan<float> alpha, int i) => alpha.Length == 1 ? alpha[0] : alpha[i];

    private static float Cover(ReadOnlySpan<byte> coverage, int i) => coverage.IsEmpty ? 1 : coverage[i] / 255f;

    // A plane's straight complement (1 − tint): paper where nothing is drawn.
    private static float Colour(ReadOnlySpan<float> d, ReadOnlySpan<float> ad, int n, int p, int i) => ad[i] > 0 ? 1 - Math.Clamp(d[(p * n) + i] / ad[i], 0, 1) : 1;
}
