using System.Numerics;
using System.Runtime.CompilerServices;

using Egami.Pixels;

namespace Egami.Colors;

/// <summary>
/// Converts between color values (<see cref="IColorValue{TSelf}"/>), through XYZ D65, and between rows of pixels and color values.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="color-values"/>
/// </example>
public static class ColorConvert
{
    private const int Chunk = 256;

    /// <summary>Converts a color value to another model.</summary>
    /// <typeparam name="TFrom">The model converted from.</typeparam>
    /// <typeparam name="TTo">The model converted to.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>The same color in <typeparamref name="TTo"/>.</returns>
    public static TTo Convert<TFrom, TTo>(TFrom value)
        where TFrom : struct, IColorValue<TFrom>
        where TTo : struct, IColorValue<TTo>
    {
        return value is TTo same ? same : TTo.FromXyz(value.ToXyz());
    }

    /// <summary>
    /// Converts pixels to color values. The pixels are in <paramref name="encoding"/> (sRGB when <see langword="null"/>), so the
    /// Lab of a Display P3 pixel is one argument away; alpha is ignored. 8-bit sRGB pixels to Lab, Oklab, HSL, HSV and YCbCr take
    /// a fast path.
    /// </summary>
    /// <typeparam name="TPixel">The pixel type.</typeparam>
    /// <typeparam name="TColor">The color value type.</typeparam>
    /// <param name="pixels">The pixels.</param>
    /// <param name="colors">Where to write the values, at least as long as <paramref name="pixels"/>.</param>
    /// <param name="encoding">The pixels' RGB encoding, or <see langword="null"/> for sRGB.</param>
    /// <exception cref="ArgumentException"><paramref name="colors"/> is shorter than <paramref name="pixels"/>.</exception>
    public static void FromPixels<TPixel, TColor>(ReadOnlySpan<TPixel> pixels, Span<TColor> colors, ColorProfile? encoding = null)
        where TPixel : unmanaged, IPixelType<TPixel>
        where TColor : struct, IColorValue<TColor>
    {
        if (colors.Length < pixels.Length)
        {
            throw new ArgumentException("The destination is shorter than the source.", nameof(colors));
        }

        if (encoding is not null && !encoding.IsEquivalentTo(ColorProfile.Srgb))
        {
            ColorTransform transform = ColorTransform.Create(encoding, ColorProfile.Srgb, Wide);
            Span<RgbaF32> srgb = stackalloc RgbaF32[Math.Min(Chunk, pixels.Length)];
            for (int start = 0; start < pixels.Length; start += Chunk)
            {
                int n = Math.Min(Chunk, pixels.Length - start);
                transform.Apply<TPixel, RgbaF32>(pixels.Slice(start, n), srgb[..n]);
                for (int i = 0; i < n; i++)
                {
                    colors[start + i] = TColor.From(srgb[i]);
                }
            }

            return;
        }

        if (typeof(TPixel) == typeof(Rgba8) && Fast.FromBytes(Unsafe.BitCast<ReadOnlySpan<TPixel>, ReadOnlySpan<Rgba8>>(pixels), colors))
        {
            return;
        }

        for (int i = 0; i < pixels.Length; i++)
        {
            Vector4 v = pixels[i].ToScaled();
            colors[i] = typeof(TPixel) == typeof(Rgba8) ? TColor.From(Unsafe.BitCast<TPixel, Rgba8>(pixels[i])) : TColor.From(new RgbaF32(v.X, v.Y, v.Z, v.W));
        }
    }

    /// <summary>
    /// Converts color values to opaque pixels in <paramref name="encoding"/> (sRGB when <see langword="null"/>). Integer pixel
    /// types clamp colors outside the encoding; float types keep them.
    /// </summary>
    /// <typeparam name="TColor">The color value type.</typeparam>
    /// <typeparam name="TPixel">The pixel type.</typeparam>
    /// <param name="colors">The values.</param>
    /// <param name="pixels">Where to write the pixels, at least as long as <paramref name="colors"/>.</param>
    /// <param name="encoding">The pixels' RGB encoding, or <see langword="null"/> for sRGB.</param>
    /// <exception cref="ArgumentException"><paramref name="pixels"/> is shorter than <paramref name="colors"/>.</exception>
    public static void ToPixels<TColor, TPixel>(ReadOnlySpan<TColor> colors, Span<TPixel> pixels, ColorProfile? encoding = null)
        where TColor : struct, IColorValue<TColor>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (pixels.Length < colors.Length)
        {
            throw new ArgumentException("The destination is shorter than the source.", nameof(pixels));
        }

        if (encoding is not null && !encoding.IsEquivalentTo(ColorProfile.Srgb))
        {
            ColorTransform transform = ColorTransform.Create(ColorProfile.Srgb, encoding, Wide);
            Span<RgbaF32> srgb = stackalloc RgbaF32[Math.Min(Chunk, colors.Length)];
            for (int start = 0; start < colors.Length; start += Chunk)
            {
                int n = Math.Min(Chunk, colors.Length - start);
                for (int i = 0; i < n; i++)
                {
                    srgb[i] = colors[start + i].ToRgbaF32();
                }

                transform.Apply<RgbaF32, TPixel>(srgb[..n], pixels.Slice(start, n));
            }

            return;
        }

        if (typeof(TPixel) == typeof(Rgba8) && Fast.ToBytes(colors, Unsafe.BitCast<Span<TPixel>, Span<Rgba8>>(pixels)))
        {
            return;
        }

        for (int i = 0; i < colors.Length; i++)
        {
            pixels[i] = typeof(TPixel) == typeof(Rgba8) ? Unsafe.BitCast<Rgba8, TPixel>(colors[i].ToRgba8()) : TPixel.FromScaled(Scaled(colors[i].ToRgbaF32()));
        }
    }

    // Between encodings the values are exact colors, kept outside 0..1 and never tone mapped.
    private static ColorTransformOptions Wide { get; } = new() { Intent = RenderingIntent.RelativeColorimetric, ToneMapping = ToneMapping.Clip };

    private static Vector4 Scaled(RgbaF32 c) => new(c.R, c.G, c.B, 1);

    /// <summary>Single-precision loops for 8-bit sRGB pixels, decoding through the 256-entry table.</summary>
    private static class Fast
    {
        private static readonly float[] Linear = Srgb.LinearFrom8;

        // Linear sRGB to XYZ D65, and on to D50 for Lab, with Lab's white folded in; and to Ottosson's LMS for Oklab.
        private static readonly float[] ToLab = Floats(Matrix3d.Diagonal(new Vector3d(1 / ColorValueMath.D50.X, 1, 1 / ColorValueMath.D50.Z)) * ColorValueMath.D65ToD50 * ColorValueMath.RgbToXyz);
        private static readonly float[] ToLms = Floats(new Matrix3d(0.4122214708, 0.5363325363, 0.0514459929, 0.2119034982, 0.6806995451, 0.1073969566, 0.0883024619, 0.2817188376, 0.6299787005));
        private static readonly float[] LmsToOklab = Floats(new Matrix3d(0.2104542553, 0.7936177850, -0.0040720468, 1.9779984951, -2.4285922050, 0.4505937099, 0.0259040371, 0.7827717662, -0.8086757660));

        // The way back: XYZ D50 (Lab's white folded in) to linear sRGB, and Oklab to LMS to linear sRGB.
        private static readonly float[] FromLab = Floats((Matrix3d.Diagonal(new Vector3d(1 / ColorValueMath.D50.X, 1, 1 / ColorValueMath.D50.Z)) * ColorValueMath.D65ToD50 * ColorValueMath.RgbToXyz).Inverse());
        private static readonly float[] OklabToLms = Floats(new Matrix3d(0.2104542553, 0.7936177850, -0.0040720468, 1.9779984951, -2.4285922050, 0.4505937099, 0.0259040371, 0.7827717662, -0.8086757660).Inverse());
        private static readonly float[] LmsToRgb = Floats(new Matrix3d(0.4122214708, 0.5363325363, 0.0514459929, 0.2119034982, 0.6806995451, 0.1073969566, 0.0883024619, 0.2817188376, 0.6299787005).Inverse());

        public static bool FromBytes<TColor>(ReadOnlySpan<Rgba8> pixels, Span<TColor> colors)
            where TColor : struct, IColorValue<TColor>
        {
            float[] t = Linear;
            if (typeof(TColor) == typeof(LabColor))
            {
                float[] m = ToLab;
                Span<LabColor> lab = Unsafe.BitCast<Span<TColor>, Span<LabColor>>(colors);
                for (int i = 0; i < pixels.Length; i++)
                {
                    Rgba8 p = pixels[i];
                    float r = t[p.R], g = t[p.G], b = t[p.B];
                    float fx = F((m[0] * r) + (m[1] * g) + (m[2] * b)), fy = F((m[3] * r) + (m[4] * g) + (m[5] * b)), fz = F((m[6] * r) + (m[7] * g) + (m[8] * b));
                    lab[i] = new LabColor((116 * fy) - 16, 500 * (fx - fy), 200 * (fy - fz));
                }

                return true;
            }

            if (typeof(TColor) == typeof(OklabColor))
            {
                float[] m = ToLms, o = LmsToOklab;
                Span<OklabColor> lab = Unsafe.BitCast<Span<TColor>, Span<OklabColor>>(colors);
                for (int i = 0; i < pixels.Length; i++)
                {
                    Rgba8 p = pixels[i];
                    float r = t[p.R], g = t[p.G], b = t[p.B];
                    float l = Cbrt((m[0] * r) + (m[1] * g) + (m[2] * b)), s = Cbrt((m[3] * r) + (m[4] * g) + (m[5] * b)), q = Cbrt((m[6] * r) + (m[7] * g) + (m[8] * b));
                    lab[i] = new OklabColor((o[0] * l) + (o[1] * s) + (o[2] * q), (o[3] * l) + (o[4] * s) + (o[5] * q), (o[6] * l) + (o[7] * s) + (o[8] * q));
                }

                return true;
            }

            if (typeof(TColor) == typeof(HslColor) || typeof(TColor) == typeof(HsvColor) || typeof(TColor) == typeof(YCbCrColor))
            {
                for (int i = 0; i < pixels.Length; i++)
                {
                    Rgba8 p = pixels[i];
                    int max = Math.Max(p.R, Math.Max(p.G, p.B)), min = Math.Min(p.R, Math.Min(p.G, p.B)), d = max - min;
                    float hue = d == 0 ? 0 : 60f * (max == p.R ? ((p.G - p.B) / (float)d) + (p.G < p.B ? 6 : 0) : max == p.G ? ((p.B - p.R) / (float)d) + 2 : ((p.R - p.G) / (float)d) + 4);
                    if (typeof(TColor) == typeof(HslColor))
                    {
                        int sum = max + min;
                        float s = d == 0 || sum == 0 || sum == 510 ? 0 : d / (float)(255 - Math.Abs(sum - 255));
                        colors[i] = Unsafe.BitCast<HslColor, TColor>(new HslColor(hue, s, sum / 510f));
                    }
                    else if (typeof(TColor) == typeof(HsvColor))
                    {
                        colors[i] = Unsafe.BitCast<HsvColor, TColor>(new HsvColor(hue, max == 0 ? 0 : d / (float)max, max / 255f));
                    }
                    else
                    {
                        float y = ((0.299f * p.R) + (0.587f * p.G) + (0.114f * p.B)) / 255f;
                        colors[i] = Unsafe.BitCast<YCbCrColor, TColor>(new YCbCrColor(y, (((p.B / 255f) - y) / 1.772f) + 0.5f, (((p.R / 255f) - y) / 1.402f) + 0.5f));
                    }
                }

                return true;
            }

            return false;
        }

        public static bool ToBytes<TColor>(ReadOnlySpan<TColor> colors, Span<Rgba8> pixels)
            where TColor : struct, IColorValue<TColor>
        {
            if (typeof(TColor) == typeof(LabColor))
            {
                float[] m = FromLab;
                ReadOnlySpan<LabColor> lab = Unsafe.BitCast<ReadOnlySpan<TColor>, ReadOnlySpan<LabColor>>(colors);
                for (int i = 0; i < lab.Length; i++)
                {
                    LabColor c = lab[i];
                    float fy = (c.L + 16) / 116, fx = fy + (c.A / 500), fz = fy - (c.B / 200);
                    float x = G(fx), y = G(fy), z = G(fz);
                    pixels[i] = new Rgba8(Encode((m[0] * x) + (m[1] * y) + (m[2] * z)), Encode((m[3] * x) + (m[4] * y) + (m[5] * z)), Encode((m[6] * x) + (m[7] * y) + (m[8] * z)), 255);
                }

                return true;
            }

            if (typeof(TColor) == typeof(OklabColor))
            {
                float[] o = OklabToLms, m = LmsToRgb;
                ReadOnlySpan<OklabColor> lab = Unsafe.BitCast<ReadOnlySpan<TColor>, ReadOnlySpan<OklabColor>>(colors);
                for (int i = 0; i < lab.Length; i++)
                {
                    OklabColor c = lab[i];
                    float l = (o[0] * c.L) + (o[1] * c.A) + (o[2] * c.B), s = (o[3] * c.L) + (o[4] * c.A) + (o[5] * c.B), q = (o[6] * c.L) + (o[7] * c.A) + (o[8] * c.B);
                    (l, s, q) = (l * l * l, s * s * s, q * q * q);
                    pixels[i] = new Rgba8(Encode((m[0] * l) + (m[1] * s) + (m[2] * q)), Encode((m[3] * l) + (m[4] * s) + (m[5] * q)), Encode((m[6] * l) + (m[7] * s) + (m[8] * q)), 255);
                }

                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float F(float t) => t > 216f / 24389 ? Cbrt(t) : ((24389f / 27 * t) + 16) / 116;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float G(float f) => f > 6f / 29 ? f * f * f : ((116 * f) - 16) / (24389f / 27);

        // Linear 0..1 to the nearest 8-bit sRGB value, through 1.0's interpolated table.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte Encode(float linear) => Srgb.FromLinearTo8(linear);

        /// <summary>
        /// A cube root for x ≥ 0: an exponent-thirding guess from the float's bits, then two Newton steps, within 2e-7 relative of
        /// the exact root (0 for 0).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Cbrt(float x)
        {
            if (!(x > 0))
            {
                return x < 0 ? -Cbrt(-x) : 0;
            }

            float y = BitConverter.Int32BitsToSingle(0x2A51_4067 + (BitConverter.SingleToInt32Bits(x) / 3));
            y -= ((y * y * y) - x) / (3 * y * y);
            y -= ((y * y * y) - x) / (3 * y * y);
            return y - (((y * y * y) - x) / (3 * y * y));
        }

        private static float[] Floats(Matrix3d m) => [(float)m[0, 0], (float)m[0, 1], (float)m[0, 2], (float)m[1, 0], (float)m[1, 1], (float)m[1, 2], (float)m[2, 0], (float)m[2, 1], (float)m[2, 2]];
    }
}
