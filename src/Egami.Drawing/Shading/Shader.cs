using System.Runtime.CompilerServices;

using Egami.Pixels;

namespace Egami.Drawing.Shading;

/// <summary>
/// A paint's colors in device space: premultiplied float colors of pixel centers, row by row. Rows are shaded in parallel bands,
/// so <see cref="Shade"/> must be safe to call from several threads at once, and a pixel's color depends only on its position,
/// so the result is the same at any core count.
/// </summary>
internal abstract class Shader
{
    /// <summary>The longest run shaded into stack scratch at once.</summary>
    internal const int Chunk = 128;

    /// <summary>Gets a value indicating whether every pixel it gives is opaque.</summary>
    public virtual bool IsOpaque => false;

    /// <summary>Fills <paramref name="row"/> with the colors of pixels (<paramref name="x"/> + i, <paramref name="y"/>), sampled at their centers.</summary>
    public abstract void Shade(int y, int x, Span<PremulRgbaF32> row);
}

/// <summary>A paint's colors in a target's pixel type, ready for Egami's Compositor.</summary>
/// <typeparam name="TPixel">The target's pixel type.</typeparam>
internal abstract class PixelShader<TPixel>
    where TPixel : unmanaged, IPixelType<TPixel>
{
    /// <summary>Gets a value indicating whether every pixel it gives is opaque, so a covered source-over run can be written directly.</summary>
    public abstract bool IsOpaque { get; }

    /// <summary>Fills <paramref name="row"/> with the colors of pixels (<paramref name="x"/> + i, <paramref name="y"/>).</summary>
    public abstract void Shade(int y, int x, Span<TPixel> row);
}

/// <summary>A float shader converted to the target's pixels, with ordered dithering on 8-bit targets when the paint asks.</summary>
internal sealed class ConvertingShader<TPixel>(Shader shader, bool dither) : PixelShader<TPixel>
    where TPixel : unmanaged, IPixelType<TPixel>
{
    private readonly bool _dither = dither && TPixel.Layout.ChannelType == PixelChannelType.Unorm8;

    public override bool IsOpaque => shader.IsOpaque;

    public override void Shade(int y, int x, Span<TPixel> row)
    {
        Span<PremulRgbaF32> colors = stackalloc PremulRgbaF32[Shader.Chunk];
        for (int i = 0; i < row.Length; i += Shader.Chunk)
        {
            int n = Math.Min(Shader.Chunk, row.Length - i);
            Span<PremulRgbaF32> part = colors[..n];
            shader.Shade(y, x + i, part);
            if (_dither)
            {
                Dithering.ToPixels(y, x + i, part, row.Slice(i, n));
            }
            else
            {
                PixelConvert.Convert<PremulRgbaF32, TPixel>(part, row.Slice(i, n));
            }
        }
    }
}

/// <summary>One color everywhere: the shader of a <see cref="SolidPaint"/> inside a composed paint.</summary>
internal sealed class SolidShader(RgbaF32 color) : Shader
{
    private readonly PremulRgbaF32 _color = Premultiply(color);

    public override bool IsOpaque => _color.A >= 1;

    public override void Shade(int y, int x, Span<PremulRgbaF32> row) => row.Fill(_color);

    /// <summary>Clamps a straight color to 0 to 1 and premultiplies it.</summary>
    internal static PremulRgbaF32 Premultiply(RgbaF32 c)
    {
        float a = Math.Clamp(c.A, 0, 1);
        return new PremulRgbaF32(Math.Clamp(c.R, 0, 1) * a, Math.Clamp(c.G, 0, 1) * a, Math.Clamp(c.B, 0, 1) * a, a);
    }
}

/// <summary>
/// Rounds premultiplied float colors to 8 bits through a 4×4 ordered (Bayer) pattern: each pixel gets an offset from −15/32 to
/// 15/32 of a level before rounding, so a value already on a level never moves and soft ramps lose their bands.
/// </summary>
internal static class Dithering
{
    // The 4×4 Bayer matrix, as offsets (b + 0.5) / 16 − 0.5 of one level out of 255.
    private static readonly float[] Offsets = Build();

    /// <summary>Gets the offset, in 0..1 units, added at device pixel (x, y).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float Offset(int x, int y) => Offsets[((y & 3) << 2) | (x & 3)];

    /// <summary>Converts a row to 8-bit target pixels with the pattern.</summary>
    internal static void ToPixels<TPixel>(int y, int x, ReadOnlySpan<PremulRgbaF32> colors, Span<TPixel> row)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (TPixel.Layout.Alpha == AlphaKind.Premultiplied)
        {
            Span<PremulRgba8> premul = stackalloc PremulRgba8[colors.Length];
            for (int i = 0; i < colors.Length; i++)
            {
                PremulRgbaF32 c = colors[i];
                float o = Offset(x + i, y);
                byte a = Round(c.A);
                premul[i] = new PremulRgba8(Math.Min(Round(c.R + o), a), Math.Min(Round(c.G + o), a), Math.Min(Round(c.B + o), a), a);
            }

            PixelConvert.Convert<PremulRgba8, TPixel>(premul, row);
            return;
        }

        Span<Rgba8> straight = stackalloc Rgba8[colors.Length];
        for (int i = 0; i < colors.Length; i++)
        {
            PremulRgbaF32 c = colors[i];
            float o = Offset(x + i, y);
            if (c.A <= 0)
            {
                straight[i] = default;
                continue;
            }

            float k = 1 / c.A;
            straight[i] = new Rgba8(Round((c.R * k) + o), Round((c.G * k) + o), Round((c.B * k) + o), Round(c.A));
        }

        PixelConvert.Convert<Rgba8, TPixel>(straight, row);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Round(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255, MidpointRounding.AwayFromZero), 0, 255);

    private static float[] Build()
    {
        ReadOnlySpan<int> bayer = [0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5];
        float[] offsets = new float[16];
        for (int i = 0; i < 16; i++)
        {
            offsets[i] = (((bayer[i] + 0.5f) / 16) - 0.5f) / 255;
        }

        return offsets;
    }
}

/// <summary>The sRGB transfer curve, extended to values outside 0 to 1 by symmetry, for gradient interpolation in linear light.</summary>
internal static class SrgbCurve
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float ToLinear(float v)
    {
        float a = MathF.Abs(v);
        float r = a <= 0.04045f ? a / 12.92f : MathF.Pow((a + 0.055f) / 1.055f, 2.4f);
        return MathF.CopySign(r, v);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float ToEncoded(float v)
    {
        float a = MathF.Abs(v);
        float r = a <= 0.0031308f ? a * 12.92f : (1.055f * MathF.Pow(a, 1 / 2.4f)) - 0.055f;
        return MathF.CopySign(r, v);
    }
}
