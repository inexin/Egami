using Egami.Operations;

namespace Egami.Formats.Psd;

/// <summary>
/// The flattener's Lab: layers blend in Lab as Photoshop stores it (L*, a* and b* encoded, as fractions of the depth's full
/// scale), the separable modes on those values as for RGB, the non-separable ones on lightness, chroma and hue; the canvas
/// becomes sRGB at the end.
/// </summary>
internal sealed partial class PsdFlattener
{
    /// <summary>Gets the encoded value of a* and b* that is 0: 128 of 255, or 32768 of 65535.</summary>
    private float LabCenter => _file.Depth == 16 ? 32768f / 65535 : 128f / 255;

    /// <summary>Returns whether a mode is one Lab blends on lightness, chroma and hue rather than value by value.</summary>
    private static bool IsLabNonSeparable(BlendMode mode) => mode is BlendMode.Hue or BlendMode.Saturation or BlendMode.Color
        or BlendMode.Luminosity or BlendMode.DarkerColor or BlendMode.LighterColor;

    /// <summary>
    /// Puts into <paramref name="cb"/> the non-separable blend of encoded Lab <paramref name="cs"/> over it: Luminosity takes the
    /// source's L*, Color its a* and b*, Hue its hue angle at the backdrop's chroma, Saturation its chroma at the backdrop's
    /// hue (a gray backdrop stays gray), Darker and Lighter Color the pixel of the lesser or greater L*.
    /// </summary>
    internal static void LabBlend(BlendMode mode, Span<float> cb, ReadOnlySpan<float> cs, float center)
    {
        float ba = cb[1] - center, bb = cb[2] - center, sa = cs[1] - center, sb = cs[2] - center;
        switch (mode)
        {
            case BlendMode.Luminosity:
                cb[0] = cs[0];
                break;
            case BlendMode.Color:
                cb[1] = cs[1];
                cb[2] = cs[2];
                break;
            case BlendMode.Hue:
                {
                    float chroma = MathF.Sqrt((ba * ba) + (bb * bb)), source = MathF.Sqrt((sa * sa) + (sb * sb));
                    if (source > 0)
                    {
                        cb[1] = center + (sa / source * chroma);
                        cb[2] = center + (sb / source * chroma);
                    }

                    break;
                }

            case BlendMode.Saturation:
                {
                    float chroma = MathF.Sqrt((ba * ba) + (bb * bb)), source = MathF.Sqrt((sa * sa) + (sb * sb));
                    if (chroma > 0)
                    {
                        cb[1] = center + (ba / chroma * source);
                        cb[2] = center + (bb / chroma * source);
                    }

                    break;
                }

            case BlendMode.DarkerColor:
                if (cs[0] < cb[0])
                {
                    cs.CopyTo(cb);
                }

                break;
            case BlendMode.LighterColor:
                if (cs[0] > cb[0])
                {
                    cs.CopyTo(cb);
                }

                break;
        }
    }

    /// <summary>
    /// Blends a row of premultiplied encoded Lab pixels in a non-separable mode (see <see cref="LabBlend"/>), composited as the
    /// separable modes are: the source alone where there is no backdrop, the blend where both are.
    /// </summary>
    private void LabRow(BlendMode mode, ReadOnlySpan<float> s, Span<float> d, float opacity)
    {
        Span<float> cs = stackalloc float[3], cb = stackalloc float[3];
        float center = LabCenter;
        for (int i = 0; i < d.Length; i += 4)
        {
            float sa = s[i + 3] * opacity, da = d[i + 3];
            if (sa <= 0)
            {
                continue;
            }

            for (int c = 0; c < 3; c++)
            {
                cs[c] = s[i + c] * opacity / sa;
                cb[c] = da > 0 ? d[i + c] / da : 0;
            }

            LabBlend(mode, cb, cs, center);
            float both = sa * da;
            for (int c = 0; c < 3; c++)
            {
                d[i + c] = (s[i + c] * opacity * (1 - da)) + (d[i + c] * (1 - sa)) + (both * cb[c]);
            }

            d[i + 3] = sa + da - both;
        }
    }

    /// <summary>Returns encoded Lab, at the document's depth, of encoded sRGB.</summary>
    private (float L, float A, float B) LabOf(double r, double g, double b)
    {
        (double l, double a, double bb) = PsdLab.FromSrgb(r, g, b);
        (l, a, bb) = PsdLab.Encode(_file.Depth, l, a, bb);
        return ((float)l, (float)a, (float)bb);
    }

    /// <summary>Returns encoded sRGB of encoded Lab at the document's depth.</summary>
    private (double R, double G, double B) SrgbOf(float l, float a, float b)
    {
        (double dl, double da, double db) = PsdLab.Decode(_file.Depth, l, a, b);
        return PsdLab.ToSrgb(dl, da, db);
    }

    /// <summary>Turns a premultiplied encoded Lab canvas into premultiplied sRGB in place.</summary>
    private void LabToSrgb(float[] canvas)
    {
        for (int i = 0, n = 4 * _width * _height; i < n; i += 4)
        {
            float a = canvas[i + 3];
            if (a <= 0)
            {
                continue;
            }

            float k = Math.Min(a, 1);
            (double r, double g, double b) = SrgbOf(canvas[i] / a, canvas[i + 1] / a, canvas[i + 2] / a);
            (canvas[i], canvas[i + 1], canvas[i + 2]) = ((float)r * k, (float)g * k, (float)b * k);
        }
    }
}
