using Egami.Colors;
using Egami.Pixels;

namespace Egami.Formats.Jxl;

/// <summary>
/// Color for decoded frames: XYB to linear sRGB through the opsin inverse matrix (L.2), and the transfer functions that encode
/// linear light into the file's own color space. Ported from jxl-oxide's jxl-color (see THIRD-PARTY-NOTICES).
/// </summary>
internal static class JxlColor
{
    private static readonly float[] DefaultOpsin =
    [
        11.031566901960783f, -9.866943921568629f, -0.16462299647058826f,
        -3.254147380392157f, 4.418770392156863f, -0.16462299647058826f,
        -3.6588512862745097f, 2.7129230470588235f, 1.9459282392156863f,
        -0.0037930732552754493f, -0.0037930732552754493f, -0.0037930732552754493f,
    ];

    /// <summary>Converts X, Y, B planes in place to linear sRGB (R, G, B), with 1.0 at the intensity target scaled to 255 nits.</summary>
    internal static void XybToLinear(float[][] planes, int count, JxlImageHeader image)
    {
        float[] m = image.OpsinInverse ?? DefaultOpsin;
        float b0 = m[9];
        float b1 = m[10];
        float b2 = m[11];
        float c0 = MathF.Cbrt(b0);
        float c1 = MathF.Cbrt(b1);
        float c2 = MathF.Cbrt(b2);
        float scale = 255 / image.ToneMapping.IntensityTarget;
        float[] x = planes[0];
        float[] y = planes[1];
        float[] b = planes[2];
        for (int i = 0; i < count; i++)
        {
            float gl = y[i] + x[i] - c0;
            float gm = y[i] - x[i] - c1;
            float gs = b[i] - c2;
            float l = MathF.FusedMultiplyAdd(gl * gl, gl, b0) * scale;
            float mm = MathF.FusedMultiplyAdd(gm * gm, gm, b1) * scale;
            float s = MathF.FusedMultiplyAdd(gs * gs, gs, b2) * scale;
            x[i] = (m[0] * l) + (m[1] * mm) + (m[2] * s);
            y[i] = (m[3] * l) + (m[4] * mm) + (m[5] * s);
            b[i] = (m[6] * l) + (m[7] * mm) + (m[8] * s);
        }
    }

    /// <summary>
    /// Gets the color space the file describes (design question 4): its ICC profile when it has one, else the enumerated
    /// primaries, white point and transfer function.
    /// </summary>
    internal static ColorProfile Profile(JxlColorEncoding c, byte[]? icc)
    {
        if (icc is not null)
        {
            return ColorProfile.FromIcc(icc);
        }

        TransferFunction transfer = c.Gamma != 0 ? TransferFunction.Gamma(1e7 / c.Gamma) : c.TransferFunction switch
        {
            1 => TransferFunction.Rec709,
            8 => TransferFunction.Linear,
            16 => TransferFunction.Pq,
            17 => TransferFunction.Gamma(2.6),
            18 => TransferFunction.Hlg,
            _ => TransferFunction.Srgb,
        };
        Chromaticity white = c.WhitePoint switch
        {
            2 => new Chromaticity(c.WhiteXy!.Value.X, c.WhiteXy.Value.Y),
            10 => new Chromaticity(1.0 / 3, 1.0 / 3),
            11 => new Chromaticity(0.314, 0.351),
            _ => Chromaticity.D65,
        };
        if (c.ColorSpace == JxlColorSpace.Gray)
        {
            return ColorProfile.CreateGray(transfer, white);
        }

        ColorPrimaries primaries = c.Primaries switch
        {
            2 => new ColorPrimaries(Xy(c.PrimariesXy![0]), Xy(c.PrimariesXy[1]), Xy(c.PrimariesXy[2]), white),
            9 => ColorPrimaries.Rec2020 with { White = white },
            11 => ColorPrimaries.DisplayP3 with { White = white },
            _ => ColorPrimaries.Rec709 with { White = white },
        };
        if (!primaries.IsValid)
        {
            throw new CorruptDataException(JxlFormat.Info.Name, -1, "The color encoding's primaries cannot form a color space.");
        }

        return primaries.IsCloseTo(ColorPrimaries.Rec709) && transfer == TransferFunction.Srgb ? ColorProfile.Srgb
            : primaries.IsCloseTo(ColorPrimaries.Rec709) && transfer == TransferFunction.Linear ? ColorProfile.LinearSrgb
            : ColorProfile.Create(primaries, transfer);

        static Chromaticity Xy((double X, double Y) xy) => new(xy.X, xy.Y);
    }

    /// <summary>Gets the same space with linear light: the primaries kept (sRGB's when an ICC profile has none), gray kept.</summary>
    internal static ColorProfile LinearOf(ColorProfile profile)
    {
        return profile.Model == ColorModel.Gray ? ColorProfile.GrayLinear
            : profile.Primaries is ColorPrimaries p && !p.IsCloseTo(ColorPrimaries.Rec709) ? ColorProfile.Create(p, TransferFunction.Linear, "Linear")
            : ColorProfile.LinearSrgb;
    }

    /// <summary>
    /// Converts color planes with a transform: three planes in (one for a gray source), three out, the gray result put in all
    /// three for a gray target.
    /// </summary>
    internal static void Apply(ColorTransform transform, float[][] color, int count)
    {
        const int Chunk = 1024;
        bool grayIn = transform.Source.Model == ColorModel.Gray;
        bool grayOut = transform.Target.Model == ColorModel.Gray;
        RgbF32[] rgb = new RgbF32[Math.Min(Chunk, count)];
        GrayF32[] gray = new GrayF32[rgb.Length];
        for (int start = 0; start < count; start += Chunk)
        {
            int n = Math.Min(Chunk, count - start);
            if (grayIn)
            {
                for (int i = 0; i < n; i++)
                {
                    gray[i] = new GrayF32(color[0][start + i]);
                }
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    rgb[i] = new RgbF32(color[0][start + i], color[1][start + i], color[2][start + i]);
                }
            }

            switch (grayIn, grayOut)
            {
                case (false, false):
                    transform.Apply<RgbF32>(rgb.AsSpan(0, n));
                    break;
                case (false, true):
                    transform.Apply<RgbF32, GrayF32>(rgb.AsSpan(0, n), gray.AsSpan(0, n));
                    break;
                case (true, true):
                    transform.Apply<GrayF32>(gray.AsSpan(0, n));
                    break;
                default:
                    transform.Apply<GrayF32, RgbF32>(gray.AsSpan(0, n), rgb.AsSpan(0, n));
                    break;
            }

            if (grayOut)
            {
                for (int i = 0; i < n; i++)
                {
                    color[0][start + i] = gray[i].L;
                }
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    (color[0][start + i], color[1][start + i], color[2][start + i]) = (rgb[i].R, rgb[i].G, rgb[i].B);
                }
            }
        }

        if (grayOut && color.Length >= 3)
        {
            color[1] = color[0];
            color[2] = color[0];
        }
    }

    /// <summary>Gets whether linear sRGB converts to the color encoding by a transfer function alone (sRGB primaries, D65).</summary>
    internal static bool TransferOnly(JxlColorEncoding c)
    {
        return !c.WantIcc && c.WhitePoint == 1 && (c.ColorSpace == JxlColorSpace.Gray || c.Primaries == 1) && c.TransferFunction is not (16 or 18);
    }

    /// <summary>Encodes linear light with the color encoding's transfer function.</summary>
    internal static void Encode(float[] plane, int count, JxlColorEncoding c)
    {
        if (c.Gamma != 0 || c.TransferFunction == 17)
        {
            // As libjxl: samples at or below 10^-5 become 0.
            float g = c.Gamma != 0 ? c.Gamma / 1e7f : 1 / 2.6f;
            for (int i = 0; i < count; i++)
            {
                float v = plane[i];
                plane[i] = v <= 1e-5f ? 0 : MathF.Pow(v, g);
            }

            return;
        }

        switch (c.TransferFunction)
        {
            case 8:
                return;
            case 1:
                for (int i = 0; i < count; i++)
                {
                    // Negative values stay on the linear segment, as libjxl and jxl-oxide encode them.
                    float v = plane[i];
                    plane[i] = v <= 0.018f ? 4.5f * v : (1.099f * MathF.Pow(v, 0.45f)) - 0.099f;
                }

                return;
            default:
                for (int i = 0; i < count; i++)
                {
                    float v = plane[i];
                    float a = MathF.Abs(v);
                    plane[i] = MathF.CopySign(a <= 0.0031308f ? 12.92f * a : (1.055f * MathF.Pow(a, 1 / 2.4f)) - 0.055f, v);
                }

                return;
        }
    }
}

/// <summary>
/// The color space decoded frames are put in and how (design question 4). XYB goes to linear sRGB, then into an enumerated space
/// as libjxl does (a matrix through XYZ D50 for other primaries or white points, the luminance for gray, then the transfer
/// function), or into an ICC profile through the 2.1.2 pipeline; <see cref="JxlReaderOptions.LinearOutput"/> stops before the
/// transfer function. Like djxl, the conformance decode gives linear sRGB where libjxl cannot reach the file's space (an ICC
/// profile, or gray with another white than D65).
/// </summary>
internal sealed class JxlOutput
{
    private JxlOutput(ColorProfile profile, bool linear)
    {
        (Profile, Linear) = (profile, linear);
    }

    /// <summary>Gets the space of the output.</summary>
    internal ColorProfile Profile { get; }

    /// <summary>Gets whether the output is linear light.</summary>
    internal bool Linear { get; }

    /// <summary>Gets the conversion from the decoded color (linear sRGB for XYB, the file's space otherwise), or <see langword="null"/>.</summary>
    internal ColorTransform? Transform { get; private init; }

    /// <summary>Gets the linear sRGB to linear target matrix, row by row, or <see langword="null"/> for none.</summary>
    internal float[]? Matrix { get; private init; }

    /// <summary>Gets whether XYB output is gray: the luminance of linear sRGB.</summary>
    internal bool Gray { get; private init; }

    /// <summary>Gets the encoding whose transfer function XYB output takes, or <see langword="null"/> to stay linear.</summary>
    internal JxlColorEncoding? Curve { get; private init; }

    internal static JxlOutput For(JxlFile file, bool linear, EgamiContext context, bool likeDjxl = false)
    {
        JxlImageHeader h = file.Header;
        if (h.ExtraChannels.Any(e => e.Type == JxlExtraChannelType.Black))
        {
            // CMYK stays ink: the ICC profile on the raster describes it.
            return new JxlOutput(ColorProfile.Srgb, false);
        }

        JxlColorEncoding c = h.Color;
        bool gray = h.ColorChannels == 1;
        ColorProfile own = JxlColor.Profile(c, file.Icc);
        ColorProfile target = linear ? JxlColor.LinearOf(own) : own;
        ColorTransformOptions options = new()
        {
            Intent = (RenderingIntent)(file.Icc is null ? c.RenderingIntent : 1),
            ToneMapping = ToneMapping.Clip,
            ReferenceWhiteNits = h.ToneMapping.IntensityTarget,
        };
        if (!h.XybEncoded)
        {
            return new JxlOutput(target, linear) { Transform = linear && !own.IsEquivalentTo(target) ? ColorTransform.Create(own, target, options, context) : null };
        }

        if (likeDjxl && (file.Icc is not null || (gray && c.WhitePoint != 1)))
        {
            return new JxlOutput(gray ? ColorProfile.GrayLinear : ColorProfile.LinearSrgb, true) { Gray = gray };
        }

        if (file.Icc is not null)
        {
            return new JxlOutput(target, linear) { Transform = ColorTransform.Create(ColorProfile.LinearSrgb, target, options, context) };
        }

        if (!linear && c.Gamma == 0 && c.TransferFunction is 16 or 18)
        {
            throw new UnsupportedFormatException("Egami does not convert JPEG XL XYB to PQ or HLG yet.");
        }

        float[]? matrix = null;
        if (!gray && (c.Primaries != 1 || c.WhitePoint != 1))
        {
            // As libjxl: linear sRGB to XYZ D50, then to the target's primaries adapted from D50 to its white (Bradford both ways).
            Vector3d d50 = Chromaticity.D50.ToXyz();
            ColorPrimaries p = own.Primaries!;
            Matrix3d srgbToD50 = Matrix3d.Bradford(Chromaticity.D65.ToXyz(), d50) * ColorPrimaries.Rec709.ToXyz();
            Matrix3d targetToD50 = Matrix3d.Bradford(p.White.ToXyz(), d50) * p.ToXyz();
            Matrix3d m = targetToD50.Inverse() * srgbToD50;
            matrix = [.. Enumerable.Range(0, 9).Select(i => (float)m[i / 3, i % 3])];
        }

        return new JxlOutput(target, linear) { Matrix = matrix, Gray = gray, Curve = linear ? null : c };
    }
}
