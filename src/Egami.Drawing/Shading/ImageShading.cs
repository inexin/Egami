using System.Numerics;
using System.Runtime.CompilerServices;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Shading;

/// <summary>
/// An <see cref="ImagePaint"/>'s colors: each device pixel center maps to image coordinates, where the sampling filter's kernel is
/// weighed over the texels around it (whole texels only, so Nearest and a whole-pixel shift copy exactly), each texel index brought
/// into the image by its axis's tile mode or, for Decal, read as transparent. When the image is shrunk by more than half, the
/// nearest smaller MipChain level is sampled instead. Texels are premultiplied, in linear light when the sampling asks.
/// </summary>
internal sealed class ImageShader : Shader
{
    private readonly PremulRgbaF32[] _texels;
    private readonly int _width;
    private readonly int _height;
    private readonly Matrix3x2 _inverse;
    private readonly TileMode _tileX;
    private readonly TileMode _tileY;
    private readonly ResampleFilter _filter;
    private readonly bool _linear;
    private readonly float _support;
    private readonly int _level;

    private ImageShader(PremulRgbaF32[] texels, int width, int height, Matrix3x2 inverse, ImagePaint paint, int level)
    {
        _texels = texels;
        _level = level;
        _width = width;
        _height = height;
        _inverse = inverse;
        _tileX = paint.TileX;
        _tileY = paint.TileY;
        _filter = paint.Sampling.Filter;
        _linear = paint.Sampling.ColorSpace == ColorSpace.Linear;
        _support = Support(_filter);
    }

    /// <summary>Returns whether the mapping is a whole-pixel shift with a filter that passes pixels through unchanged there.</summary>
    internal static bool IsPixelCopy(Matrix3x2 inverse, SamplingOptions sampling)
    {
        bool passes = sampling.Filter is ResampleFilter.Nearest or ResampleFilter.Box or ResampleFilter.Bilinear or ResampleFilter.Bicubic
            or ResampleFilter.Lanczos2 or ResampleFilter.Lanczos3 or ResampleFilter.Hermite;
        return passes && inverse.M11 == 1 && inverse.M22 == 1 && inverse.M12 == 0 && inverse.M21 == 0
            && inverse.M31 == MathF.Round(inverse.M31) && inverse.M32 == MathF.Round(inverse.M32);
    }

    internal static ImageShader Create(ImagePaint paint, Matrix3x2 inverse)
    {
        Raster image = paint.Image;
        int level = Level(paint, inverse);
        Raster source = image;
        MipChain? chain = null;
        try
        {
            if (level > 0)
            {
                chain = MipChain.Build(image, paint.Sampling.ColorSpace);
                level = Math.Min(level, chain.Levels.Count - 1);
                source = chain.Levels[level];
            }

            // Image coordinates of the level: scaled by its size over the image's.
            float sx = source.Width / (float)image.Width, sy = source.Height / (float)image.Height;
            Matrix3x2 toLevel = inverse * Matrix3x2.CreateScale(sx, sy);
            PremulRgbaF32[] texels = Texels(source, paint.Sampling.ColorSpace == ColorSpace.Linear);
            return new ImageShader(texels, source.Width, source.Height, toLevel, paint, level);
        }
        finally
        {
            chain?.Dispose();
        }
    }

    /// <summary>
    /// Returns the shader of the same paint under another mapping, sharing this one's texels when the mapping samples the same mip
    /// level: a mesh's textured triangles and an atlas's sprites differ only in their mapping.
    /// </summary>
    internal ImageShader Rebase(ImagePaint paint, Matrix3x2 inverse)
    {
        if (Level(paint, inverse) != _level)
        {
            return Create(paint, inverse);
        }

        Matrix3x2 toLevel = inverse * Matrix3x2.CreateScale(_width / (float)paint.Image.Width, _height / (float)paint.Image.Height);
        return new ImageShader(_texels, _width, _height, toLevel, paint, _level);
    }

    public override void Shade(int y, int x, Span<PremulRgbaF32> row)
    {
        Vector2 p = Vector2.Transform(new Vector2(x + 0.5f, y + 0.5f), _inverse), step = new(_inverse.M11, _inverse.M12);
        Span<float> wx = stackalloc float[16], wy = stackalloc float[16];
        for (int i = 0; i < row.Length; i++)
        {
            Vector2 q = p + (step * i);
            row[i] = _filter == ResampleFilter.Nearest ? Fetch((int)MathF.Floor(q.X), (int)MathF.Floor(q.Y)) : Filtered(q, wx, wy);
        }

        if (_linear)
        {
            for (int i = 0; i < row.Length; i++)
            {
                row[i] = ToEncoded(row[i]);
            }
        }
    }

    /// <summary>Device → image scale: the longer of the inverse's columns. Past 2, the mip level that brings it under 2.</summary>
    private static int Level(ImagePaint paint, Matrix3x2 inverse)
    {
        float footprint = MathF.Max(new Vector2(inverse.M11, inverse.M12).Length(), new Vector2(inverse.M21, inverse.M22).Length());
        return paint.Sampling.Filter == ResampleFilter.Nearest || !(footprint >= 2) ? 0 : (int)MathF.Floor(MathF.Log2(footprint));
    }

    /// <summary>Maps a texel index into 0..size − 1 by a tile mode, or −1 for Decal outside.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Tile(int i, int size, TileMode mode)
    {
        if ((uint)i < (uint)size)
        {
            return i;
        }

        switch (mode)
        {
            case TileMode.Clamp:
                return i < 0 ? 0 : size - 1;
            case TileMode.Repeat:
                int r = i % size;
                return r < 0 ? r + size : r;
            case TileMode.Mirror:
                int period = 2 * size, m = i % period;
                m = m < 0 ? m + period : m;
                return m < size ? m : period - 1 - m;
            default:
                return -1;
        }
    }

    private static PremulRgbaF32[] Texels(Raster source, bool linear)
    {
        using Raster<PremulRgbaF32> premul = source.As<PremulRgbaF32>();
        PremulRgbaF32[] texels = new PremulRgbaF32[source.Width * source.Height];
        for (int y = 0; y < source.Height; y++)
        {
            premul.Row(y).CopyTo(texels.AsSpan(y * source.Width, source.Width));
        }

        if (linear)
        {
            for (int i = 0; i < texels.Length; i++)
            {
                PremulRgbaF32 c = texels[i];
                float k = c.A > 0 ? 1 / c.A : 0;
                texels[i] = new PremulRgbaF32(SrgbCurve.ToLinear(c.R * k) * c.A, SrgbCurve.ToLinear(c.G * k) * c.A, SrgbCurve.ToLinear(c.B * k) * c.A, c.A);
            }
        }

        return texels;
    }

    private static PremulRgbaF32 ToEncoded(PremulRgbaF32 c)
    {
        if (c.A <= 0)
        {
            return default;
        }

        float k = 1 / c.A;
        return new PremulRgbaF32(
            Math.Clamp(SrgbCurve.ToEncoded(c.R * k), 0, 1) * c.A, Math.Clamp(SrgbCurve.ToEncoded(c.G * k), 0, 1) * c.A, Math.Clamp(SrgbCurve.ToEncoded(c.B * k), 0, 1) * c.A, c.A);
    }

    private static float Support(ResampleFilter filter) => filter switch
    {
        ResampleFilter.Box => 0.5f,
        ResampleFilter.Bilinear or ResampleFilter.Hermite => 1,
        ResampleFilter.Lanczos3 => 3,
        _ => 2,
    };

    /// <summary>The filter's kernel at distance <paramref name="x"/> texels.</summary>
    private static float Kernel(ResampleFilter filter, float x)
    {
        x = MathF.Abs(x);
        switch (filter)
        {
            case ResampleFilter.Box:
                return x < 0.5f ? 1 : 0;
            case ResampleFilter.Bilinear:
                return x < 1 ? 1 - x : 0;
            case ResampleFilter.Hermite:
                return x < 1 ? (((2 * x) - 3) * x * x) + 1 : 0;
            case ResampleFilter.Mitchell:
                return Cubic(x, 1 / 3f, 1 / 3f);
            case ResampleFilter.Spline:
                return Cubic(x, 1, 0);
            case ResampleFilter.Lanczos2:
                return x < 2 ? Sinc(x) * Sinc(x / 2) : 0;
            case ResampleFilter.Lanczos3:
                return x < 3 ? Sinc(x) * Sinc(x / 3) : 0;
            case ResampleFilter.Gaussian:
                return x < 2 ? MathF.Exp(-2 * x * x) : 0;
            default:
                return Cubic(x, 0, 0.5f);
        }
    }

    /// <summary>The Mitchell–Netravali family of cubics with parameters B and C.</summary>
    private static float Cubic(float x, float b, float c)
    {
        if (x < 1)
        {
            return (((12 - (9 * b) - (6 * c)) * x * x * x) + ((-18 + (12 * b) + (6 * c)) * x * x) + (6 - (2 * b))) / 6;
        }

        if (x < 2)
        {
            return (((-b - (6 * c)) * x * x * x) + (((6 * b) + (30 * c)) * x * x) + (((-12 * b) - (48 * c)) * x) + ((8 * b) + (24 * c))) / 6;
        }

        return 0;
    }

    private static float Sinc(float x) => x == 0 ? 1 : MathF.Sin(MathF.PI * x) / (MathF.PI * x);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private PremulRgbaF32 Fetch(int ix, int iy)
    {
        int tx = Tile(ix, _width, _tileX), ty = Tile(iy, _height, _tileY);
        return tx < 0 || ty < 0 ? default : _texels[(ty * _width) + tx];
    }

    private PremulRgbaF32 Filtered(Vector2 q, Span<float> wx, Span<float> wy)
    {
        // Texel i's center is at i + 0.5; the taps are the texels whose centers lie within the support.
        float u = q.X - 0.5f, v = q.Y - 0.5f;
        int x0 = (int)MathF.Floor(u - _support) + 1, y0 = (int)MathF.Floor(v - _support) + 1;
        int nx = Math.Min((int)MathF.Floor(u + _support) - x0 + 1, wx.Length), ny = Math.Min((int)MathF.Floor(v + _support) - y0 + 1, wy.Length);
        float sumX = 0, sumY = 0;
        for (int i = 0; i < nx; i++)
        {
            wx[i] = Kernel(_filter, u - (x0 + i));
            sumX += wx[i];
        }

        for (int j = 0; j < ny; j++)
        {
            wy[j] = Kernel(_filter, v - (y0 + j));
            sumY += wy[j];
        }

        if (sumX == 0 || sumY == 0)
        {
            return default;
        }

        Vector4 acc = default;
        for (int j = 0; j < ny; j++)
        {
            if (wy[j] == 0)
            {
                continue;
            }

            Vector4 line = default;
            for (int i = 0; i < nx; i++)
            {
                if (wx[i] != 0)
                {
                    PremulRgbaF32 t = Fetch(x0 + i, y0 + j);
                    line += new Vector4(t.R, t.G, t.B, t.A) * wx[i];
                }
            }

            acc += line * wy[j];
        }

        acc /= sumX * sumY;
        float a = Math.Clamp(acc.W, 0, 1);
        return new PremulRgbaF32(Math.Clamp(acc.X, 0, a), Math.Clamp(acc.Y, 0, a), Math.Clamp(acc.Z, 0, a), a);
    }
}

/// <summary>
/// An <see cref="ImagePaint"/> under a whole-pixel shift: rows of the image, already in the target's pixel type, copied with the
/// tile modes applied, so a pattern fill costs a copy.
/// </summary>
internal sealed class ImageCopyShader<TPixel> : PixelShader<TPixel>
    where TPixel : unmanaged, IPixelType<TPixel>
{
    private readonly TPixel[] _pixels;
    private readonly int _width;
    private readonly int _height;
    private readonly int _dx;
    private readonly int _dy;
    private readonly TileMode _tileX;
    private readonly TileMode _tileY;

    internal ImageCopyShader(ImagePaint paint, Matrix3x2 inverse)
    {
        Raster image = paint.Image;
        _width = image.Width;
        _height = image.Height;
        _dx = (int)inverse.M31;
        _dy = (int)inverse.M32;
        _tileX = paint.TileX;
        _tileY = paint.TileY;
        _pixels = new TPixel[_width * _height];
        using Raster<TPixel> converted = image.As<TPixel>();
        for (int y = 0; y < _height; y++)
        {
            converted.Row(y).CopyTo(_pixels.AsSpan(y * _width, _width));
        }

        IsOpaque = !TPixel.Layout.HasAlpha || (!image.Layout.HasAlpha && _tileX != TileMode.Decal && _tileY != TileMode.Decal);
    }

    public override bool IsOpaque { get; }

    public override void Shade(int y, int x, Span<TPixel> row)
    {
        int ty = ImageShader.Tile(y + _dy, _height, _tileY);
        if (ty < 0)
        {
            row.Clear();
            return;
        }

        ReadOnlySpan<TPixel> line = _pixels.AsSpan(ty * _width, _width);
        int i = 0;
        while (i < row.Length)
        {
            int ix = x + i + _dx;
            int tx = ImageShader.Tile(ix, _width, _tileX);
            if (tx < 0)
            {
                row[i++] = default;
                continue;
            }

            // A run inside one copy of the image moves as a block.
            int run = Math.Min(row.Length - i, _width - tx);
            if (_tileX == TileMode.Mirror && Mirrored(ix))
            {
                for (int k = 0; k < Math.Min(row.Length - i, tx + 1); k++)
                {
                    row[i + k] = line[tx - k];
                }

                i += Math.Min(row.Length - i, tx + 1);
                continue;
            }

            if ((uint)ix >= (uint)_width && _tileX == TileMode.Clamp)
            {
                // Past an edge a clamped row repeats the edge pixel until the image starts or the row ends.
                int until = ix < 0 ? Math.Min(row.Length - i, -ix) : row.Length - i;
                row.Slice(i, until).Fill(line[tx]);
                i += until;
                continue;
            }

            line.Slice(tx, run).CopyTo(row.Slice(i, run));
            i += run;
        }
    }

    private bool Mirrored(int ix)
    {
        int period = 2 * _width, m = ix % period;
        m = m < 0 ? m + period : m;
        return m >= _width;
    }
}
