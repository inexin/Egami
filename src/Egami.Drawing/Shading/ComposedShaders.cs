using System.Numerics;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Shading;

/// <summary>Another shader's colors scaled by an opacity: a composed paint's part with its own <see cref="Paint.Opacity"/>.</summary>
internal sealed class OpacityShader(Shader shader, float opacity) : Shader
{
    public override void Shade(int y, int x, Span<PremulRgbaF32> row)
    {
        shader.Shade(y, x, row);
        for (int i = 0; i < row.Length; i++)
        {
            PremulRgbaF32 c = row[i];
            row[i] = new PremulRgbaF32(c.R * opacity, c.G * opacity, c.B * opacity, c.A * opacity);
        }
    }
}

/// <summary>Two shaders blended per pixel with Egami's Compositor, the source onto the destination.</summary>
internal sealed class BlendShader(Shader? destination, Shader? source, BlendMode mode, ColorSpace colorSpace) : Shader
{
    public override void Shade(int y, int x, Span<PremulRgbaF32> row)
    {
        Span<PremulRgbaF32> top = stackalloc PremulRgbaF32[Chunk];
        for (int start = 0; start < row.Length; start += Chunk)
        {
            int n = Math.Min(Chunk, row.Length - start);
            Span<PremulRgbaF32> under = row.Slice(start, n), over = top[..n];
            Fill(destination, y, x + start, under);
            Fill(source, y, x + start, over);
            Compositor.Blend(mode, over, under, 1, colorSpace);
        }
    }

    private static void Fill(Shader? shader, int y, int x, Span<PremulRgbaF32> row)
    {
        if (shader is null)
        {
            row.Clear();
        }
        else
        {
            shader.Shade(y, x, row);
        }
    }
}

/// <summary>A color matrix on another shader's unpremultiplied colors, clamped and premultiplied again.</summary>
internal sealed class ColorMatrixShader : Shader
{
    private readonly Shader? _source;
    private readonly float[] _m = new float[20];

    internal ColorMatrixShader(Shader? source, ColorMatrix matrix)
    {
        _source = source;
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 5; c++)
            {
                _m[(r * 5) + c] = matrix[r, c];
            }
        }
    }

    public override void Shade(int y, int x, Span<PremulRgbaF32> row)
    {
        if (_source is null)
        {
            row.Clear();
        }
        else
        {
            _source.Shade(y, x, row);
        }

        float[] m = _m;
        for (int i = 0; i < row.Length; i++)
        {
            PremulRgbaF32 c = row[i];
            float k = c.A > 0 ? 1 / c.A : 0;
            float r = c.R * k, g = c.G * k, b = c.B * k, a = c.A;
            float nr = Math.Clamp((m[0] * r) + (m[1] * g) + (m[2] * b) + (m[3] * a) + m[4], 0, 1);
            float ng = Math.Clamp((m[5] * r) + (m[6] * g) + (m[7] * b) + (m[8] * a) + m[9], 0, 1);
            float nb = Math.Clamp((m[10] * r) + (m[11] * g) + (m[12] * b) + (m[13] * a) + m[14], 0, 1);
            float na = Math.Clamp((m[15] * r) + (m[16] * g) + (m[17] * b) + (m[18] * a) + m[19], 0, 1);
            row[i] = new PremulRgbaF32(nr * na, ng * na, nb * na, na);
        }
    }
}

/// <summary>A <see cref="CustomPaint"/>'s rows, with the colors clamped to valid premultiplied values afterwards.</summary>
internal sealed class CustomShader(CustomPaint paint, Matrix3x2 inverse) : Shader
{
    public override void Shade(int y, int x, Span<PremulRgbaF32> row)
    {
        paint.Shade(y, x, row, inverse);
        for (int i = 0; i < row.Length; i++)
        {
            PremulRgbaF32 c = row[i];
            float a = float.IsNaN(c.A) ? 0 : Math.Clamp(c.A, 0, 1);
            row[i] = new PremulRgbaF32(Clamp(c.R, a), Clamp(c.G, a), Clamp(c.B, a), a);
        }
    }

    private static float Clamp(float v, float a) => float.IsNaN(v) ? 0 : Math.Clamp(v, 0, a);
}
