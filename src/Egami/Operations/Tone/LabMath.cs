using System.Numerics;
using System.Runtime.CompilerServices;

namespace Egami.Operations.Tone;

/// <summary>
/// sRGB-encoded values to CIE L*a*b* and back, with the D65 white of sRGB (as ImageMagick's Lab), on floats: L* from 0 to 100,
/// a* and b* around 0.
/// </summary>
internal static class LabMath
{
    private const float Epsilon = 216f / 24389f;
    private const float Kappa = 24389f / 27f;

    // D65 white of sRGB, Y = 1.
    private const float Xn = 0.95047f, Zn = 1.08883f;

    /// <summary>Converts encoded sRGB to Lab.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector3 ToLab(float r, float g, float b)
    {
        float lr = SrgbFloat.Decode(r), lg = SrgbFloat.Decode(g), lb = SrgbFloat.Decode(b);
        float x = ((0.4123908f * lr) + (0.3575843f * lg) + (0.1804808f * lb)) / Xn;
        float y = (0.2126390f * lr) + (0.7151687f * lg) + (0.0721923f * lb);
        float z = ((0.0193308f * lr) + (0.1191948f * lg) + (0.9505322f * lb)) / Zn;
        float fx = F(x), fy = F(y), fz = F(z);
        return new Vector3((116 * fy) - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    /// <summary>Returns L* of encoded sRGB, from 0 to 100.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float Lightness(float r, float g, float b)
    {
        float y = (0.2126390f * SrgbFloat.Decode(r)) + (0.7151687f * SrgbFloat.Decode(g)) + (0.0721923f * SrgbFloat.Decode(b));
        return (116 * F(y)) - 16;
    }

    /// <summary>Converts Lab to encoded sRGB, each channel clamped to 0..1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector3 FromLab(Vector3 lab)
    {
        float fy = (lab.X + 16) / 116, fx = fy + (lab.Y / 500), fz = fy - (lab.Z / 200);
        float x = Finv(fx) * Xn, y = lab.X > Kappa * Epsilon ? fy * fy * fy : lab.X / Kappa, z = Finv(fz) * Zn;
        float lr = (3.2409699f * x) - (1.5373832f * y) - (0.4986108f * z);
        float lg = (-0.9692436f * x) + (1.8759675f * y) + (0.0415551f * z);
        float lb = (0.0556301f * x) - (0.2039770f * y) + (1.0569715f * z);
        return new Vector3(SrgbFloat.Encode(lr), SrgbFloat.Encode(lg), SrgbFloat.Encode(lb));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float F(float t) => t > Epsilon ? Cbrt(t) : ((Kappa * t) + 16) / 116;

    /// <summary>
    /// The cube root of a positive value: a guess from the float's exponent bits, then two Halley steps, which converge cubically to
    /// float precision, several times faster than the library's cube root.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float Cbrt(float x)
    {
        float y = BitConverter.Int32BitsToSingle((BitConverter.SingleToInt32Bits(x) / 3) + 709921077);
        float y3 = y * y * y;
        y *= (y3 + x + x) / (y3 + y3 + x);
        y3 = y * y * y;
        return y * ((y3 + x + x) / (y3 + y3 + x));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Finv(float f)
    {
        float f3 = f * f * f;
        return f3 > Epsilon ? f3 : ((116 * f) - 16) / Kappa;
    }
}

/// <summary>Maps L* (scaled to 0..1) through a curve and keeps a* and b*: equalization of lightness without a color shift.</summary>
internal sealed class LightnessStage(ToneCurve curve) : FilterStage
{
    internal override void Apply(Span<Vector4> pixels, FilterContext context)
    {
        float[]? table = curve.IsSmooth ? context.Table(curve) : null;
        foreach (ref Vector4 v in pixels)
        {
            Vector3 lab = LabMath.ToLab(v.X, v.Y, v.Z);
            float position = Clamp01(lab.X / 100) * (CurveStage.TableSize - 1);
            int i = (int)position;
            float l = (table is null ? (float)curve.Map(lab.X / 100) : table[i] + ((table[i + 1] - table[i]) * (position - i))) * 100;
            Vector3 c = LabMath.FromLab(new Vector3(l, lab.Y, lab.Z));
            v = new Vector4(c, v.W);
        }
    }


}

/// <summary>
/// ImageMagick's -white-balance: a* and b* each lose the image's mean a* or b* scaled by 1.1 × L*/100, so the cast goes from the
/// midtones and highlights and shadows keep their color.
/// </summary>
internal sealed class LabShiftStage(float meanA, float meanB) : FilterStage
{
    internal float MeanA => meanA;

    internal float MeanB => meanB;

    internal override void Apply(Span<Vector4> pixels, FilterContext context)
    {
        foreach (ref Vector4 v in pixels)
        {
            Vector3 lab = LabMath.ToLab(v.X, v.Y, v.Z);
            float k = 1.1f * lab.X / 100;
            Vector3 c = LabMath.FromLab(new Vector3(lab.X, lab.Y - (k * meanA), lab.Z - (k * meanB)));
            v = new Vector4(c, v.W);
        }
    }
}
