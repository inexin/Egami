using System.Runtime.Intrinsics;

using Egami.Colors;
using Egami.Pixels;

namespace Egami.Formats.Jxl;

/// <summary>
/// Color for decoded frames: XYB to linear sRGB through the opsin inverse matrix (L.2), and the transfer functions that encode
/// linear light into the file's own color space. Ported from jxl-oxide's jxl-color (see THIRD-PARTY-NOTICES).
/// </summary>
internal static class JxlColor
{
    // libjxl's TF_SRGB EncodedFromDisplay coefficients, lowest degree first (its EvalRationalPolynomial starts from the last).
    private static readonly float[] SrgbP = [-5.135152395e-04f, 5.287254571e-03f, 3.903842876e-01f, 1.474205315e+00f, 7.352629620e-01f];
    private static readonly float[] SrgbQ = [1.004519624e-02f, 3.036675394e-01f, 1.340816930e+00f, 9.258482155e-01f, 2.424867759e-02f];

    private static readonly float[] DefaultOpsin =
    [
        11.031566901960783f, -9.866943921568629f, -0.16462299647058826f,
        -3.254147380392157f, 4.418770392156863f, -0.16462299647058826f,
        -3.6588512862745097f, 2.7129230470588235f, 1.9459282392156863f,
        -0.0037930732552754493f, -0.0037930732552754493f, -0.0037930732552754493f,
    ];

    /// <summary>Converts X, Y, B planes in place to linear sRGB (R, G, B), with 1.0 at the intensity target scaled to 255 nits.</summary>
    internal static void XybToLinear(float[][] planes, int count, JxlImageHeader image) => XybToLinear(planes, 0, count, image, null);

    /// <summary>Gets the opsin inverse matrix and biases of the image (12 values: the matrix by rows, then the biases).</summary>
    internal static float[] Opsin(JxlImageHeader image) => image.OpsinInverse ?? DefaultOpsin;

    /// <summary>Converts samples <paramref name="start"/> to <paramref name="end"/> of the X, Y, B planes to linear sRGB.</summary>
    internal static void XybToLinear(float[][] planes, int start, int end, JxlImageHeader image, float[]? opsin)
        => XybToLinear(planes, start, end, opsin ?? image.OpsinInverse ?? DefaultOpsin, image.ToneMapping.IntensityTarget);

    /// <summary>Converts the first <paramref name="count"/> samples of the X, Y, B planes to linear sRGB with the default opsin inverse.</summary>
    internal static void XybToLinear(float[][] planes, int count, float intensityTarget) => XybToLinear(planes, 0, count, DefaultOpsin, intensityTarget);

    private static void XybToLinear(float[][] planes, int start, int end, float[] m, float intensityTarget)
    {
        // A target matrix folded into the opsin inverse, as libjxl does, rounds as libjxl does.
        float b0 = m[9];
        float b1 = m[10];
        float b2 = m[11];
        float c0 = MathF.Cbrt(b0);
        float c1 = MathF.Cbrt(b1);
        float c2 = MathF.Cbrt(b2);
        float scale = 255 / intensityTarget;
        float[] x = planes[0];
        float[] y = planes[1];
        float[] b = planes[2];
        int i = start;
        if (System.Runtime.Intrinsics.X86.Fma.IsSupported)
        {
            // The same operations as below, eight samples at a time.
            Vector256<float> vc0 = Vector256.Create(c0);
            Vector256<float> vc1 = Vector256.Create(c1);
            Vector256<float> vc2 = Vector256.Create(c2);
            Vector256<float> vb0 = Vector256.Create(b0);
            Vector256<float> vb1 = Vector256.Create(b1);
            Vector256<float> vb2 = Vector256.Create(b2);
            Vector256<float> vs = Vector256.Create(scale);
            for (; i + 8 <= end; i += 8)
            {
                Vector256<float> vx = Vector256.Create(x.AsSpan(i, 8));
                Vector256<float> vy = Vector256.Create(y.AsSpan(i, 8));
                Vector256<float> vb = Vector256.Create(b.AsSpan(i, 8));
                Vector256<float> gl = vy + vx - vc0;
                Vector256<float> gm = vy - vx - vc1;
                Vector256<float> gs = vb - vc2;
                Vector256<float> l = System.Runtime.Intrinsics.X86.Fma.MultiplyAdd(gl * gl, gl, vb0) * vs;
                Vector256<float> mm = System.Runtime.Intrinsics.X86.Fma.MultiplyAdd(gm * gm, gm, vb1) * vs;
                Vector256<float> ss = System.Runtime.Intrinsics.X86.Fma.MultiplyAdd(gs * gs, gs, vb2) * vs;
                ((Vector256.Create(m[0]) * l) + (Vector256.Create(m[1]) * mm) + (Vector256.Create(m[2]) * ss)).CopyTo(x.AsSpan(i, 8));
                ((Vector256.Create(m[3]) * l) + (Vector256.Create(m[4]) * mm) + (Vector256.Create(m[5]) * ss)).CopyTo(y.AsSpan(i, 8));
                ((Vector256.Create(m[6]) * l) + (Vector256.Create(m[7]) * mm) + (Vector256.Create(m[8]) * ss)).CopyTo(b.AsSpan(i, 8));
            }
        }

        for (; i < end; i++)
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
        Apply(transform, color, 0, count);
        if (transform.Target.Model == ColorModel.Gray && color.Length >= 3)
        {
            color[1] = color[0];
            color[2] = color[0];
        }
    }

    /// <summary>Converts samples <paramref name="first"/> to <paramref name="end"/>; a gray result lands in plane 0 only.</summary>
    internal static void Apply(ColorTransform transform, float[][] color, int first, int end)
    {
        int count = end - first;
        const int Chunk = 1024;
        bool grayIn = transform.Source.Model == ColorModel.Gray;
        bool grayOut = transform.Target.Model == ColorModel.Gray;
        RgbF32[] rgb = new RgbF32[Math.Min(Chunk, count)];
        GrayF32[] gray = new GrayF32[rgb.Length];
        for (int at = 0; at < count; at += Chunk)
        {
            int start = first + at;
            int n = Math.Min(Chunk, count - at);
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

    }

    /// <summary>
    /// Takes display light to scene light for HLG output with libjxl's inverse OOTF: each pixel scaled by its luminance to the
    /// power γ − 1, where γ = 1.111^−log2(peak / 1000) / 1.2 for a display <paramref name="peak"/> in nits.
    /// </summary>
    internal static void HlgToSceneLight(Span<float> r, Span<float> g, Span<float> b, float[] luminances, float peak)
    {
        float exponent = ((1 / 1.2f) * MathF.Pow(1.111f, -MathF.Log2(peak / 1000f))) - 1;
        if (exponent is > -0.01f and < 0.01f)
        {
            return;
        }

        for (int i = 0; i < r.Length; i++)
        {
            float y = (luminances[0] * r[i]) + (luminances[1] * g[i]) + (luminances[2] * b[i]);
            float ratio = MathF.Min(MathF.Pow(y, exponent), 1e9f);
            r[i] *= ratio;
            g[i] *= ratio;
            b[i] *= ratio;
        }
    }

    /// <summary>Converts Cb, Y, Cr planes in place to R, G, B with the JPEG (JFIF) matrix, Y offset by 128/255, as libjxl does.</summary>
    internal static void YCbCrToRgb(float[][] planes, int count, JxlParallel parallel)
    {
        const float C128 = 128f / 255;
        const float CrCr = 1.402f;
        const float CgCb = -0.114f * 1.772f / 0.587f;
        const float CgCr = -0.299f * 1.402f / 0.587f;
        const float CbCb = 1.772f;
        float[] cb = planes[0];
        float[] yy = planes[1];
        float[] cr = planes[2];
        const int Chunk = 1 << 15;
        parallel.For((count + Chunk - 1) / Chunk, k =>
        {
            for (int i = k * Chunk; i < Math.Min(count, (k + 1) * Chunk); i++)
            {
                float y = yy[i] + C128;
                float b = cb[i];
                float r = cr[i];
                cb[i] = MathF.FusedMultiplyAdd(CrCr, r, y);
                yy[i] = MathF.FusedMultiplyAdd(CgCr, r, MathF.FusedMultiplyAdd(CgCb, b, y));
                cr[i] = MathF.FusedMultiplyAdd(CbCb, b, y);
            }
        });
    }

    /// <summary>Gets whether linear sRGB converts to the color encoding by a transfer function alone (sRGB primaries, D65).</summary>
    internal static bool TransferOnly(JxlColorEncoding c)
    {
        return !c.WantIcc && c.WhitePoint == 1 && (c.ColorSpace == JxlColorSpace.Gray || c.Primaries == 1) && c.TransferFunction is not (16 or 18);
    }

    /// <summary>Encodes linear light with the color encoding's transfer function.</summary>
    internal static void Encode(float[] plane, int count, JxlColorEncoding c) => Encode(plane.AsSpan(0, count), c, 255);

    /// <summary>
    /// Encodes linear light with the color encoding's transfer function; for PQ, 1.0 is <paramref name="intensityTarget"/> nits,
    /// as libjxl scales it. HLG's inverse OOTF is <see cref="HlgToSceneLight"/>, applied first.
    /// </summary>
    internal static void Encode(Span<float> plane, JxlColorEncoding c, float intensityTarget)
    {
        int count = plane.Length;
        if (c.Gamma != 0 || c.TransferFunction == 17)
        {
            // As libjxl: samples at or below 10^-5 become 0.
            JxlFastMath.EncodeGamma(plane, c.Gamma != 0 ? c.Gamma / 1e7f : 1 / 2.6f);
            return;
        }

        switch (c.TransferFunction)
        {
            case 8:
                return;
            case 16:
                for (int i = 0; i < count; i++)
                {
                    float v = plane[i];
                    if (v == 0)
                    {
                        continue;
                    }

                    // SMPTE ST 2084 of the display light, sign kept (libjxl's TF_PQ).
                    double xp = Math.Pow(Math.Abs(v) * (intensityTarget / 10000.0), 2610.0 / 16384);
                    double e = Math.Pow((3424.0 / 4096 + (xp * (2413.0 / 4096 * 32))) / (1 + (xp * (2392.0 / 4096 * 32))), 2523.0 / 4096 * 128);
                    plane[i] = MathF.CopySign((float)e, v);
                }

                return;
            case 18:
                for (int i = 0; i < count; i++)
                {
                    // The HLG OETF (ITU-R BT.2100), sign kept (libjxl's TF_HLG).
                    float v = plane[i];
                    double a = Math.Abs(v);
                    double e = a <= 1.0 / 12 ? Math.Sqrt(3 * a) : (0.17883277 * Math.Log((12 * a) - (1 - (4 * 0.17883277)))) + 0.5599107295;
                    plane[i] = v == 0 ? 0 : MathF.CopySign((float)e, v);
                }

                return;
            case 1:
                // Negative values stay on the linear segment, as libjxl and jxl-oxide encode them.
                JxlFastMath.Encode709(plane);
                return;
            default:
                EncodeSrgb(plane);
                return;
        }
    }

    /// <summary>
    /// The sRGB curve as libjxl's high-precision TF_SRGB computes it: a rational polynomial in √x above 0.0031308 (error about
    /// 5e-7), 12.92·x below, sign kept. Several samples at a time with AVX and FMA, the rest with the same operations.
    /// </summary>
    private static void EncodeSrgb(Span<float> plane)
    {
        int i = 0;
        if (System.Runtime.Intrinsics.X86.Fma.IsSupported)
        {
            ref float start = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(plane);
            for (; i + 8 <= plane.Length; i += 8)
            {
                Vector256<float> v = Vector256.LoadUnsafe(ref start, (nuint)i);
                Vector256<float> sign = v & Vector256.Create(-0f);
                Vector256<float> x = Vector256.AndNot(v, Vector256.Create(-0f));
                Vector256<float> r = Vector256.Sqrt(x);
                Vector256<float> yp = Vector256.Create(SrgbP[4]);
                Vector256<float> yq = Vector256.Create(SrgbQ[4]);
                for (int k = 3; k >= 0; k--)
                {
                    yp = System.Runtime.Intrinsics.X86.Fma.MultiplyAdd(yp, r, Vector256.Create(SrgbP[k]));
                    yq = System.Runtime.Intrinsics.X86.Fma.MultiplyAdd(yq, r, Vector256.Create(SrgbQ[k]));
                }

                Vector256<float> magnitude = Vector256.ConditionalSelect(Vector256.GreaterThan(x, Vector256.Create(0.0031308f)), yp / yq, x * Vector256.Create(12.92f));
                (magnitude | sign).StoreUnsafe(ref start, (nuint)i);
            }
        }

        for (; i < plane.Length; i++)
        {
            float v = plane[i];
            float x = MathF.Abs(v);
            float r = MathF.Sqrt(x);
            float yp = SrgbP[4];
            float yq = SrgbQ[4];
            for (int k = 3; k >= 0; k--)
            {
                yp = MathF.FusedMultiplyAdd(yp, r, SrgbP[k]);
                yq = MathF.FusedMultiplyAdd(yq, r, SrgbQ[k]);
            }

            plane[i] = MathF.CopySign(x > 0.0031308f ? yp / yq : x * 12.92f, v);
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

    /// <summary>Gets the opsin inverse with <see cref="Matrix"/> folded in (12 values, as the header's), or <see langword="null"/>.</summary>
    internal float[]? Opsin { get; private init; }

    /// <summary>Gets whether XYB output is gray: the luminance of linear sRGB.</summary>
    internal bool Gray { get; private init; }

    /// <summary>Gets the encoding whose transfer function XYB output takes, or <see langword="null"/> to stay linear.</summary>
    internal JxlColorEncoding? Curve { get; private init; }

    /// <summary>Gets the luminance of each target primary, for the HLG OOTF.</summary>
    internal float[] Luminances { get; private init; } = [0.2126f, 0.7152f, 0.0722f];

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

        float[]? matrix = null;
        float[] luminances = [0.2126f, 0.7152f, 0.0722f];
        float[]? folds = null;
        if (!gray && (c.Primaries != 1 || c.WhitePoint != 1))
        {
            // As libjxl: linear sRGB to XYZ D50, then to the target's primaries adapted from D50 to its white (Bradford both ways).
            Vector3d d50 = Chromaticity.D50.ToXyz();
            ColorPrimaries p = own.Primaries!;
            Matrix3d srgbToD50 = Matrix3d.Bradford(Chromaticity.D65.ToXyz(), d50) * ColorPrimaries.Rec709.ToXyz();
            Matrix3d targetToD50 = Matrix3d.Bradford(p.White.ToXyz(), d50) * p.ToXyz();
            Matrix3d m = targetToD50.Inverse() * srgbToD50;
            matrix = [.. Enumerable.Range(0, 9).Select(i => (float)m[i / 3, i % 3])];
            float[] o = JxlColor.Opsin(h);
            Matrix3d opsin = new(o[0], o[1], o[2], o[3], o[4], o[5], o[6], o[7], o[8]);
            Matrix3d folded = m * opsin;
            folds = [.. Enumerable.Range(0, 9).Select(i => (float)folded[i / 3, i % 3]), o[9], o[10], o[11]];
            Matrix3d toXyz = p.ToXyz();
            luminances = [(float)toXyz[1, 0], (float)toXyz[1, 1], (float)toXyz[1, 2]];
        }

        return new JxlOutput(target, linear) { Matrix = matrix, Opsin = folds, Gray = gray, Curve = linear ? null : c, Luminances = luminances };
    }
}
