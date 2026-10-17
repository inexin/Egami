using System.Runtime.Intrinsics;

using Egami.Colors;
using Egami.Pixels;

namespace Egami.Formats.Jxl;

/// <summary>
/// The lossy encoder's input (Phase 2.9.7): a raster in linear sRGB light through the 2.1.2 color pipeline, then XYB through
/// the opsin absorbance matrix, a cube root and the bias the decoder takes off again (ISO/IEC 18181-1 L.2 run forwards). As in
/// libjxl's ToXYB (enc_xyb, see THIRD-PARTY-NOTICES), linear sRGB is the opsin matrix's own space: wide-gamut colors become
/// sRGB values outside 0 to 1, which the matrix maps into the cone responses, and responses below zero are clamped.
/// </summary>
internal static class JxlXyb
{
    // libjxl's kOpsinAbsorbanceMatrix (rows sum to 1) and kOpsinAbsorbanceBias; their inverse is the decoder's default opsin.
    private static readonly float[] Absorbance =
    [
        0.30f, 0.622f, 0.078f,
        0.23f, 0.692f, 0.078f,
        0.24342268924547819f, 0.20476744424496821f, 0.55180986650955360f,
    ];

    private const float Bias = 0.0037930732552754493f;

    /// <summary>
    /// Gets the raster's color as linear sRGB planes, 1.0 the SDR white: RGB through its own profile; gray as its linear light
    /// in all three planes; CMYK through its profile with the black channel as libjxl keeps it (0 full ink, 1 none).
    /// </summary>
    internal static JxlLinearImage ToLinear(Raster raster)
    {
        JxlLinearRows rows = new(raster.Width, raster.Height, raster.Metadata.GetColorProfile(), raster.Layout);
        using Raster<RgbaF32> floats = raster.As<RgbaF32>();
        using Raster<Cmyk16>? inks = rows.HasBlack ? raster.As<Cmyk16>() : null;
        for (int y = 0; y < raster.Height; y++)
        {
            if (inks is not null)
            {
                rows.Add(inks.Row(y), y);
            }
            else
            {
                rows.Add(floats.Row(y), y);
            }
        }

        return rows.Image;
    }

    /// <summary>
    /// Converts linear sRGB planes in place to X, Y and B. <paramref name="intensityTarget"/> is the nits of 1.0 in the file, so
    /// that the decoder's 255 / intensity_target scaling gives the same light back (255 for SDR).
    /// </summary>
    internal static void LinearToXyb(float[][] planes, int start, int end, float intensityTarget)
    {
        float mul = intensityTarget / 255f;
        float[] m = new float[9];
        for (int i = 0; i < 9; i++)
        {
            m[i] = Absorbance[i] * mul;
        }

        float offset = -MathF.Cbrt(Bias);
        float[] r = planes[0];
        float[] g = planes[1];
        float[] b = planes[2];
        int at = start;
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<float> bias = Vector256.Create(Bias);
            Vector256<float> zero = Vector256<float>.Zero;
            Vector256<float> half = Vector256.Create(0.5f);
            Vector256<float> off = Vector256.Create(offset);
            for (; at + 8 <= end; at += 8)
            {
                Vector256<float> vr = Vector256.Create(r.AsSpan(at, 8));
                Vector256<float> vg = Vector256.Create(g.AsSpan(at, 8));
                Vector256<float> vb = Vector256.Create(b.AsSpan(at, 8));
                Vector256<float> l = Vector256.Max(zero, (Vector256.Create(m[0]) * vr) + (Vector256.Create(m[1]) * vg) + (Vector256.Create(m[2]) * vb) + bias);
                Vector256<float> mm = Vector256.Max(zero, (Vector256.Create(m[3]) * vr) + (Vector256.Create(m[4]) * vg) + (Vector256.Create(m[5]) * vb) + bias);
                Vector256<float> s = Vector256.Max(zero, (Vector256.Create(m[6]) * vr) + (Vector256.Create(m[7]) * vg) + (Vector256.Create(m[8]) * vb) + bias);
                l = Cbrt(l) + off;
                mm = Cbrt(mm) + off;
                s = Cbrt(s) + off;
                (half * (l - mm)).CopyTo(r.AsSpan(at, 8));
                (half * (l + mm)).CopyTo(g.AsSpan(at, 8));
                s.CopyTo(b.AsSpan(at, 8));
            }
        }

        for (; at < end; at++)
        {
            float l = MathF.Max(0, (m[0] * r[at]) + (m[1] * g[at]) + (m[2] * b[at]) + Bias);
            float mm = MathF.Max(0, (m[3] * r[at]) + (m[4] * g[at]) + (m[5] * b[at]) + Bias);
            float s = MathF.Max(0, (m[6] * r[at]) + (m[7] * g[at]) + (m[8] * b[at]) + Bias);
            l = CbrtScalar(l) + offset;
            mm = CbrtScalar(mm) + offset;
            s = CbrtScalar(s) + offset;
            r[at] = 0.5f * (l - mm);
            g[at] = 0.5f * (l + mm);
            b[at] = s;
        }
    }

    /// <summary>The cube root of a non-negative value: a bit-level first guess and three Newton steps, as <see cref="Cbrt"/>.</summary>
    internal static float CbrtScalar(float x)
    {
        if (x <= 0)
        {
            return 0;
        }

        float y = BitConverter.Int32BitsToSingle((int)(BitConverter.SingleToInt32Bits(x) * (1f / 3)) + 0x2A517D3C);
        for (int i = 0; i < 3; i++)
        {
            y -= ((y * y * y) - x) / (3 * y * y);
        }

        return y;
    }

    // Eight cube roots at once with the same steps as CbrtScalar, so both paths give the same bits; zero stays zero.
    private static Vector256<float> Cbrt(Vector256<float> x)
    {
        Vector256<float> y = (Vector256.ConvertToInt32(Vector256.ConvertToSingle(x.AsInt32()) * Vector256.Create(1f / 3)) + Vector256.Create(0x2A517D3C)).AsSingle();
        Vector256<float> three = Vector256.Create(3f);
        for (int i = 0; i < 3; i++)
        {
            y -= ((y * y * y) - x) / (three * y * y);
        }

        return Vector256.ConditionalSelect(Vector256.GreaterThan(x, Vector256<float>.Zero), y, Vector256<float>.Zero);
    }
}

/// <summary>
/// Fills <see cref="JxlLinearImage"/> row by row, as a row encoder receives them: each row through the 2.1.2 transform from the
/// raster's profile to linear sRGB (linear gray for gray rasters), and the black ink of CMYK rows. PQ and HLG rows keep their
/// highlights, as libjxl's encoder takes them: display light over the intensity target (10,000 cd/m² for PQ, 1,000 for HLG,
/// after the HLG OOTF), then into sRGB primaries.
/// </summary>
internal sealed class JxlLinearRows
{
    private readonly ColorTransform _transform;
    private readonly ColorTransform? _primaries;
    private readonly bool _pq;
    private readonly float[] _luminances = [0.2627f, 0.6780f, 0.0593f];
    private readonly int _width;
    private readonly RgbF32[] _rgb;
    private readonly GrayF32[] _gray;
    private readonly Cmyk16[]? _inks;

    internal JxlLinearRows(int width, int height, ColorProfile profile, PixelLayoutInfo layout)
    {
        _width = width;
        bool gray = layout.ChannelOrder is PixelChannelOrder.Gray or PixelChannelOrder.GrayAlpha || profile.Model == ColorModel.Gray;
        if (profile.IsHdr && !gray && profile.Primaries is ColorPrimaries primaries)
        {
            // The signal is decoded here; the 2.1.2 transform only changes the linear primaries.
            _pq = profile.Transfer!.Equals(TransferFunction.Pq);
            IntensityTarget = _pq ? 10000 : 1000;
            _primaries = ColorTransform.Create(ColorProfile.Create(primaries, TransferFunction.Linear), ColorProfile.LinearSrgb);
            RgbF32[] unit = [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)];
            RgbF32[] srgb = new RgbF32[3];
            _primaries.Apply<RgbF32, RgbF32>(unit, srgb);
            for (int i = 0; i < 3; i++)
            {
                _luminances[i] = (0.2126f * srgb[i].R) + (0.7152f * srgb[i].G) + (0.0722f * srgb[i].B);
            }
        }

        _transform = ColorTransform.Create(profile, gray ? ColorProfile.GrayLinear : ColorProfile.LinearSrgb);
        _rgb = new RgbF32[width];
        _gray = new GrayF32[width];
        bool cmyk = profile.Model == ColorModel.Cmyk;
        _inks = cmyk ? new Cmyk16[width] : null;
        int count = width * height;
        Image = new JxlLinearImage(width, height, [new float[count], new float[count], new float[count]], gray, cmyk ? new float[count] : null);
    }

    /// <summary>Gets whether the rows carry a black ink, which <see cref="Add"/> then wants as CMYK pixels.</summary>
    internal bool HasBlack => _inks is not null;

    /// <summary>Gets the image the rows are written into.</summary>
    internal JxlLinearImage Image { get; }

    /// <summary>SMPTE ST 2084's EOTF over 10,000 cd/m², or the HLG inverse OETF (scene light), of a signal.</summary>
    private float Eotf(float e)
    {
        if (_pq)
        {
            double p = Math.Pow(Math.Max(e, 0), 4096.0 / (2523 * 128));
            return (float)Math.Pow(Math.Max(p - (3424.0 / 4096), 0) / ((2413.0 / 4096 * 32) - (2392.0 / 4096 * 32 * p)), 16384.0 / 2610);
        }

        double v = Math.Max(e, 0);
        return (float)(v <= 0.5 ? v * v / 3 : (Math.Exp((v - 0.55991073) / 0.17883277) + 0.28466892) / 12);
    }

    /// <summary>Gets the cd/m² that 1.0 in the planes stands for: 255 for SDR, as libjxl's SetIntensityTarget for PQ and HLG.</summary>
    internal float IntensityTarget { get; } = 255;

    /// <summary>Converts row <paramref name="y"/> into the planes.</summary>
    internal void Add<TPixel>(ReadOnlySpan<TPixel> row, int y)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int at = y * _width;
        float[][] color = Image.Color;
        if (Image.IsGray)
        {
            _transform.Apply<TPixel, GrayF32>(row, _gray);
            for (int x = 0; x < _width; x++)
            {
                float l = _gray[x].L;
                (color[0][at + x], color[1][at + x], color[2][at + x]) = (l, l, l);
            }

            return;
        }

        if (_primaries is not null)
        {
            PixelConvert.Convert(row, _rgb.AsSpan());
            for (int x = 0; x < _width; x++)
            {
                RgbF32 p = _rgb[x];
                float r = Eotf(p.R), g = Eotf(p.G), b = Eotf(p.B);
                if (!_pq)
                {
                    // The HLG OOTF at 1,000 cd/m²: system gamma 1.2 on the scene luminance.
                    float ys = (_luminances[0] * r) + (_luminances[1] * g) + (_luminances[2] * b);
                    float k = ys > 0 ? MathF.Pow(ys, 0.2f) : 0;
                    (r, g, b) = (r * k, g * k, b * k);
                }

                _rgb[x] = new RgbF32(r, g, b);
            }

            _primaries.Apply<RgbF32, RgbF32>(_rgb, _rgb);
        }
        else
        {
            _transform.Apply<TPixel, RgbF32>(row, _rgb);
        }

        for (int x = 0; x < _width; x++)
        {
            (color[0][at + x], color[1][at + x], color[2][at + x]) = (_rgb[x].R, _rgb[x].G, _rgb[x].B);
        }

        if (_inks is not null)
        {
            PixelConvert.Convert(row, _inks.AsSpan());
            float[] black = Image.Black!;
            for (int x = 0; x < _width; x++)
            {
                black[at + x] = 1 - (_inks[x].K / 65535f);
            }
        }
    }
}

/// <summary>A raster's color in linear sRGB planes, row after row, and its black channel for CMYK.</summary>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
/// <param name="Color">Red, green and blue (the same three for gray), 1.0 the SDR white.</param>
/// <param name="IsGray">Whether the raster was gray.</param>
/// <param name="Black">The black channel, 0 full ink and 1 none, or <see langword="null"/>.</param>
internal sealed record JxlLinearImage(int Width, int Height, float[][] Color, bool IsGray, float[]? Black);
