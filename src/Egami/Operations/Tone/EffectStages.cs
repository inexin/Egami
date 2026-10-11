using System.Numerics;
using System.Runtime.CompilerServices;

using Egami.Pixels;

namespace Egami.Operations.Tone;

/// <summary>sRGB decoding and encoding on floats through 4,096-entry tables read with linear interpolation.</summary>
internal static class SrgbFloat
{
    private const int Size = 4096;

    private static readonly float[] DecodeTable = Build(Pixels.Srgb.ToLinear);
    private static readonly float[] EncodeTable = Build(Pixels.Srgb.FromLinear);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float Decode(float encoded) => Read(DecodeTable, encoded);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float Encode(float linear) => Read(EncodeTable, linear);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Read(float[] table, float x)
    {
        float position = FilterStage.Clamp01(x) * (Size - 1);
        int i = (int)position;
        float f = position - i;
        return table[i] + ((table[i + 1] - table[i]) * f);
    }

    private static float[] Build(Func<double, double> function)
    {
        float[] table = new float[Size + 1];
        for (int i = 0; i < Size; i++)
        {
            table[i] = (float)function(i / (double)(Size - 1));
        }

        table[Size] = table[Size - 1];
        return table;
    }
}

/// <summary>
/// Threshold on one value per pixel (luminance, saturation or chroma): pixels above the level take the upper color's red, green
/// and blue, the others the lower color's; alpha is kept. The per-channel mode is a <see cref="CurveStage"/> instead. Values and
/// the level are compared as ImageMagick's Q16 build compares them: in 16-bit units, with intensity summed from the 16-bit samples,
/// so a pixel exactly at the level falls on the side ImageMagick puts it.
/// </summary>
internal sealed class ThresholdStage(ThresholdMode mode, double level, Rgba8 below, Rgba8 above) : FilterStage
{
    private readonly Vector3 _below = new(below.R / 255f, below.G / 255f, below.B / 255f);
    private readonly Vector3 _above = new(above.R / 255f, above.G / 255f, above.B / 255f);

    internal ThresholdMode Mode => mode;

    /// <summary>Gets the level in 16-bit units.</summary>
    internal double Level => level;

    internal Rgba8 LowColor => below;

    internal Rgba8 HighColor => above;

    internal override void Apply(Span<Vector4> pixels, FilterContext context)
    {
        foreach (ref Vector4 v in pixels)
        {
            Vector3 c = Above(Measure(mode, v.X, v.Y, v.Z), level) ? _above : _below;
            v = new Vector4(c, v.W);
        }
    }

    /// <summary>
    /// A level from 0 to 1 in 16-bit units, as ImageMagick reads -threshold's percentage: the caller's 0.45f as 0.45 (not the
    /// float's 0.449999988), times 65,536.
    /// </summary>
    internal static double Quantum(float level) => (double)(decimal)level * 65536;

    /// <summary>The level of a histogram bin in 16-bit units, as -auto-threshold passes it on.</summary>
    internal static double Quantum(int bin) => 65535.0 * (100.0 * bin / 255.0) / 100.0;

    /// <summary>Whether a value in 16-bit units takes the upper color: strictly above the level, as ImageMagick's -threshold.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool Above(double value, double level) => value > level;

    /// <summary>A sample in 16-bit units: the nearest 16-bit value, as ImageMagick holds an 8- or 16-bit image.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double Sample(double value) => Math.Round(Math.Clamp(value, 0, 1) * 65535);

    /// <summary>The value a mode compares, in 16-bit units.</summary>
    internal static double Measure(ThresholdMode mode, double r, double g, double b)
    {
        if (mode == ThresholdMode.Luminance)
        {
            // ImageMagick's intensity, so a level from FindThreshold splits pixels exactly as -auto-threshold does.
            return (0.212656 * Sample(r)) + (0.715158 * Sample(g)) + (0.072186 * Sample(b));
        }

        return 65535 * Fraction(mode, r, g, b);
    }

    /// <summary>The value a mode compares, from 0 to 1.</summary>
    internal static double Fraction(ThresholdMode mode, double r, double g, double b)
    {
        switch (mode)
        {
            case ThresholdMode.Saturation:
                {
                    double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                    double divisor = 1 - Math.Abs(max + min - 1);
                    return max == min || divisor <= 0 ? 0 : Math.Clamp((max - min) / divisor, 0, 1);
                }

            case ThresholdMode.MaxChroma:
                {
                    double cb = (-0.168736 * r) - (0.331264 * g) + (0.5 * b);
                    double cr = (0.5 * r) - (0.418688 * g) - (0.081312 * b);
                    return Math.Clamp(2 * Math.Max(Math.Abs(cb), Math.Abs(cr)), 0, 1);
                }

            default:
                // ImageMagick's intensity, so a level from FindThreshold splits pixels exactly as -auto-threshold does.
                return ToneMath.Intensity(r, g, b);
        }
    }
}

/// <summary>Skia's high-contrast accessibility filter: grayscale, brightness or lightness inversion and contrast, in linear light.</summary>
internal sealed class HighContrastStage(bool grayscale, ContrastInvert invert, float contrast) : FilterStage
{
    // Skia pins the contrast inside the open interval and maps it to a factor around mid-gray.
    private readonly float _factor = Factor(contrast);

    internal static float Factor(float contrast)
    {
        float c = Math.Clamp(contrast, -1 + 1e-6f, 1 - 1e-6f);
        return (1 + c) / (1 - c);
    }

    internal override void Apply(Span<Vector4> pixels, FilterContext context)
    {
        foreach (ref Vector4 v in pixels)
        {
            float r = SrgbFloat.Decode(v.X), g = SrgbFloat.Decode(v.Y), b = SrgbFloat.Decode(v.Z);
            if (grayscale)
            {
                r = g = b = (0.2126f * r) + (0.7152f * g) + (0.0722f * b);
            }

            if (invert == ContrastInvert.Brightness)
            {
                r = 1 - r;
                g = 1 - g;
                b = 1 - b;
            }
            else if (invert == ContrastInvert.Lightness)
            {
                (float h, float s, float l) = ToHsl(r, g, b);
                (r, g, b) = FromHsl(h, s, 1 - l);
            }

            r = Clamp01(0.5f + ((r - 0.5f) * _factor));
            g = Clamp01(0.5f + ((g - 0.5f) * _factor));
            b = Clamp01(0.5f + ((b - 0.5f) * _factor));
            v = new Vector4(SrgbFloat.Encode(r), SrgbFloat.Encode(g), SrgbFloat.Encode(b), v.W);
        }
    }

    internal static (float H, float S, float L) ToHsl(float r, float g, float b)
    {
        float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
        float l = (max + min) / 2;
        if (max == min)
        {
            return (0, 0, l);
        }

        float d = max - min;
        float s = l > 0.5f ? d / (2 - max - min) : d / (max + min);
        float h = max == r ? ((g - b) / d) + (g < b ? 6 : 0) : max == g ? ((b - r) / d) + 2 : ((r - g) / d) + 4;
        return (h / 6, s, l);
    }

    internal static (float R, float G, float B) FromHsl(float h, float s, float l)
    {
        if (s == 0)
        {
            return (l, l, l);
        }

        float q = l < 0.5f ? l * (1 + s) : l + s - (l * s);
        float p = (2 * l) - q;
        return (Hue(p, q, h + (1f / 3)), Hue(p, q, h), Hue(p, q, h - (1f / 3)));

        static float Hue(float p, float q, float t)
        {
            t = t < 0 ? t + 1 : t > 1 ? t - 1 : t;
            return t < 1f / 6 ? p + ((q - p) * 6 * t) : t < 0.5f ? q : t < 2f / 3 ? p + ((q - p) * ((2f / 3) - t) * 6) : p;
        }
    }
}

/// <summary>A 3 × 3 matrix on red, green and blue in linear light: decode sRGB, multiply, clamp, encode. Alpha is kept.</summary>
internal sealed class LinearMatrixStage(float[] matrix) : FilterStage
{
    internal ReadOnlySpan<float> Matrix => matrix;

    internal override void Apply(Span<Vector4> pixels, FilterContext context)
    {
        float m0 = matrix[0], m1 = matrix[1], m2 = matrix[2], m3 = matrix[3], m4 = matrix[4], m5 = matrix[5], m6 = matrix[6], m7 = matrix[7], m8 = matrix[8];
        foreach (ref Vector4 v in pixels)
        {
            float r = SrgbFloat.Decode(v.X), g = SrgbFloat.Decode(v.Y), b = SrgbFloat.Decode(v.Z);
            v = new Vector4(
                SrgbFloat.Encode((m0 * r) + (m1 * g) + (m2 * b)),
                SrgbFloat.Encode((m3 * r) + (m4 * g) + (m5 * b)),
                SrgbFloat.Encode((m6 * r) + (m7 * g) + (m8 * b)),
                v.W);
        }
    }
}

/// <summary>A 3D table on red, green and blue; alpha is kept.</summary>
internal sealed class LutStage(Lut3D lut, LutInterpolation interpolation) : FilterStage
{
    internal Lut3D Lut => lut;

    internal LutInterpolation Interpolation => interpolation;

    internal override void Apply(Span<Vector4> pixels, FilterContext context)
    {
        foreach (ref Vector4 v in pixels)
        {
            Vector3 c = lut.EvaluateCore(v.X, v.Y, v.Z, interpolation);
            v = new Vector4(Clamp01(c.X), Clamp01(c.Y), Clamp01(c.Z), v.W);
        }
    }
}
