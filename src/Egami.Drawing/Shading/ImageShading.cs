using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Egami.Drawing.Rasterization;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Shading;

/// <summary>
/// An <see cref="ImagePaint"/>'s colors: each device pixel center maps to image coordinates, where the sampling filter's kernel is
/// weighed over the texels around it (whole texels only, so Nearest and a whole-pixel shift copy exactly), each texel index brought
/// into the image by its axis's tile mode or, for Decal, read as transparent. When the image is shrunk by more than half, the
/// nearest smaller MipChain level is sampled instead. Texels are premultiplied, in linear light when the sampling asks. They are
/// converted from the whole image up front, or, when the fill's target allows (see <see cref="TexelRows"/>), row by row as the
/// shading reaches them.
/// </summary>
internal sealed class ImageShader : Shader
{
    private readonly PremulRgbaF32[] _texels;
    private readonly TexelRows? _rows;
    private readonly int _width;
    private readonly int _height;
    private readonly Matrix3x2 _inverse;
    private readonly TileMode _tileX;
    private readonly TileMode _tileY;
    private readonly ResampleFilter _filter;
    private readonly bool _linear;
    private readonly float _support;
    private readonly int _level;

    // GDI+'s high-quality modes: the kernel widened per image axis by the reduction, and its support on each axis.
    private readonly bool _boxed;
    private readonly float _stretchX;
    private readonly float _stretchY;
    private readonly float _supportX;
    private readonly float _supportY;

    // The high-quality modes' horizontal sums per thread, for an image mapped without rotation or skew (see ShadeBoxedRows).
    private ConcurrentDictionary<int, Dictionary<int, BoxedChunk>>? _boxedLines;

    private ImageShader(PremulRgbaF32[] texels, TexelRows? rows, int width, int height, Matrix3x2 inverse, ImagePaint paint, int level)
    {
        _texels = texels;
        _rows = rows;
        _level = level;
        _width = width;
        _height = height;
        _inverse = inverse;
        _tileX = paint.TileX;
        _tileY = paint.TileY;
        _filter = paint.Sampling.Filter;
        _linear = paint.Sampling.ColorSpace == ColorSpace.Linear;
        _support = Support(_filter);
        _boxed = _filter is ResampleFilter.GdiPlusHighQualityBilinear or ResampleFilter.GdiPlusHighQualityBicubic;
        if (_boxed)
        {
            // Texel units per device pixel along each image axis; GDI+ widens its kernel by the reduction, never below 1.
            _stretchX = MathF.Max(1, new Vector2(inverse.M11, inverse.M21).Length());
            _stretchY = MathF.Max(1, new Vector2(inverse.M12, inverse.M22).Length());
            float k = _filter == ResampleFilter.GdiPlusHighQualityBicubic ? 2 : 1;
            _supportX = (k * _stretchX) + 0.5f;
            _supportY = (k * _stretchY) + 0.5f;
        }
    }

    /// <summary>Gets a value indicating whether the shader reads the image's rows as it needs them instead of a converted copy.</summary>
    internal bool ReadsInPlace => _rows is not null;

    /// <summary>Returns whether the mapping is a whole-pixel shift with a filter that passes pixels through unchanged there.</summary>
    internal static bool IsPixelCopy(Matrix3x2 inverse, SamplingOptions sampling)
    {
        bool passes = sampling.Filter is ResampleFilter.Nearest or ResampleFilter.Box or ResampleFilter.Bilinear or ResampleFilter.Bicubic
            or ResampleFilter.Lanczos2 or ResampleFilter.Lanczos3 or ResampleFilter.Hermite or ResampleFilter.GdiPlusBilinear or ResampleFilter.GdiPlusBicubic;
        return passes && inverse.M11 == 1 && inverse.M22 == 1 && inverse.M12 == 0 && inverse.M21 == 0
            && inverse.M31 == MathF.Round(inverse.M31) && inverse.M32 == MathF.Round(inverse.M32);
    }

    /// <summary>Returns the shader of a paint under a device-to-paint mapping.</summary>
    /// <param name="paint">The paint.</param>
    /// <param name="inverse">The device-to-paint mapping.</param>
    /// <param name="target">
    /// The pixels the fill writes, when known: an image that shares no memory with them and is mapped without turning is read row
    /// by row as the shading needs it, instead of converted whole first.
    /// </param>
    internal static ImageShader Create(ImagePaint paint, Matrix3x2 inverse, RasterTarget? target = null)
    {
        Raster image = paint.Image;
        int level = Level(paint, inverse);
        if (level == 0 && target is not null && inverse.M12 == 0 && inverse.M21 == 0
            && TexelRows.Create(image, target, paint.Sampling.ColorSpace == ColorSpace.Linear) is TexelRows rows)
        {
            ImageShader shader = new([], rows, image.Width, image.Height, inverse, paint, 0);
            rows.Slots = shader.RowsAtOnce();
            return shader;
        }

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
            return new ImageShader(texels, null, source.Width, source.Height, toLevel, paint, level);
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
        if (Level(paint, inverse) != _level || _rows is not null)
        {
            return Create(paint, inverse);
        }

        Matrix3x2 toLevel = inverse * Matrix3x2.CreateScale(_width / (float)paint.Image.Width, _height / (float)paint.Image.Height);
        return new ImageShader(_texels, null, _width, _height, toLevel, paint, _level);
    }

    public override void Shade(int y, int x, Span<PremulRgbaF32> row)
    {
        Vector2 p = Vector2.Transform(new Vector2(x + 0.5f, y + 0.5f), _inverse), step = new(_inverse.M11, _inverse.M12);
        if (_rows is not null)
        {
            ShadeInPlace(y, x, p, step, row);
        }
        else if (_boxed && _inverse.M12 == 0 && _inverse.M21 == 0 && row.Length <= Chunk)
        {
            ShadeBoxedRows(x, p, step, row);
        }
        else if (_boxed)
        {
            ShadeBoxed(p, step, row);
        }
        else
        {
            Span<float> wx = stackalloc float[FilteredTaps], wy = stackalloc float[FilteredTaps];
            for (int i = 0; i < row.Length; i++)
            {
                Vector2 q = p + (step * i);
                row[i] = _filter == ResampleFilter.Nearest ? Fetch((int)MathF.Floor(q.X), (int)MathF.Floor(q.Y)) : Filtered(q, wx, wy);
            }
        }

        if (_linear)
        {
            for (int i = 0; i < row.Length; i++)
            {
                row[i] = ToEncoded(row[i]);
            }
        }
    }

    /// <summary>
    /// Device → image scale: the longer of the inverse's columns. Past 2, the mip level that brings it under 2; GDI+'s Bilinear and Bicubic never
    /// take a level, and its high-quality modes only past a reduction of 8.
    /// </summary>
    private static int Level(ImagePaint paint, Matrix3x2 inverse)
    {
        float footprint = MathF.Max(new Vector2(inverse.M11, inverse.M12).Length(), new Vector2(inverse.M21, inverse.M22).Length());
        switch (paint.Sampling.Filter)
        {
            case ResampleFilter.Nearest or ResampleFilter.GdiPlusBilinear or ResampleFilter.GdiPlusBicubic:
                // GDI+'s Bilinear and Bicubic keep their taps at every scale, skipping pixels when shrinking.
                return 0;
            case ResampleFilter.GdiPlusHighQualityBilinear or ResampleFilter.GdiPlusHighQualityBicubic:
                // The widened kernel from the full image, up to a reduction of 8; past that from the level that brings it to 4.
                return !(footprint > MaxBoxedStretch) ? 0 : (int)MathF.Floor(MathF.Log2(footprint / (MaxBoxedStretch / 2)));
            default:
                return !(footprint >= 2) ? 0 : (int)MathF.Floor(MathF.Log2(footprint));
        }
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
        // The common types convert each row straight into the texels, as As<PremulRgbaF32> converts it; every texel is written, so
        // the array needs no clearing.
        PremulRgbaF32[] texels = GC.AllocateUninitializedArray<PremulRgbaF32>(source.Width * source.Height);
        bool direct = source switch
        {
            Raster<Bgra8> r => Convert(r, texels),
            Raster<Rgba8> r => Convert(r, texels),
            Raster<PremulBgra8> r => Convert(r, texels),
            Raster<PremulRgba8> r => Convert(r, texels),
            Raster<Bgr8> r => Convert(r, texels),
            Raster<Rgb8> r => Convert(r, texels),
            Raster<PremulRgbaF32> r => Convert(r, texels),
            _ => false,
        };
        if (!direct)
        {
            using Raster<PremulRgbaF32> premul = source.As<PremulRgbaF32>();
            Convert(premul, texels);
        }

        if (linear)
        {
            ToLinear(texels);
        }

        return texels;
    }

    /// <summary>Brings premultiplied encoded texels into linear light.</summary>
    internal static void ToLinear(Span<PremulRgbaF32> texels)
    {
        for (int i = 0; i < texels.Length; i++)
        {
            PremulRgbaF32 c = texels[i];
            float k = c.A > 0 ? 1 / c.A : 0;
            texels[i] = new PremulRgbaF32(SrgbCurve.ToLinear(c.R * k) * c.A, SrgbCurve.ToLinear(c.G * k) * c.A, SrgbCurve.ToLinear(c.B * k) * c.A, c.A);
        }
    }

    private static bool Convert<TPixel>(Raster<TPixel> source, PremulRgbaF32[] texels)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        for (int y = 0; y < source.Height; y++)
        {
            PixelConvert.Convert<TPixel, PremulRgbaF32>(source.Row(y), texels.AsSpan(y * source.Width, source.Width));
        }

        return true;
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
        ResampleFilter.Bilinear or ResampleFilter.Hermite or ResampleFilter.GdiPlusBilinear => 1,
        ResampleFilter.Lanczos3 => 3,
        ResampleFilter.GdiPlusHighQualityBilinear => 1.5f,
        ResampleFilter.GdiPlusHighQualityBicubic => 2.5f,
        _ => 2,
    };

    /// <summary>The most taps a GDI+ high-quality axis takes: its kernel widened by up to <see cref="MaxBoxedStretch"/>.</summary>
    private const int MaxBoxedTaps = 40;

    /// <summary>The most taps the other filters take on an axis.</summary>
    private const int FilteredTaps = 16;

    /// <summary>The widest reduction GDI+'s high-quality modes take from the full image; past it they start from a mip level.</summary>
    private const float MaxBoxedStretch = 8;

    /// <summary>
    /// GDI+'s high-quality kernels (<see cref="ResampleFilter.GdiPlusHighQualityBilinear"/> and
    /// <see cref="ResampleFilter.GdiPlusHighQualityBicubic"/>): the tent or Keys cubic with a = −1, widened by
    /// <paramref name="stretch"/>, averaged over a box one texel wide around <paramref name="x"/>.
    /// </summary>
    private static float BoxedWeight(bool cubic, float x, float stretch)
    {
        return stretch * (Integral(cubic, (x + 0.5f) / stretch) - Integral(cubic, (x - 0.5f) / stretch));

        // The kernel's integral from 0 to t, odd in t.
        static float Integral(bool cubic, float t)
        {
            float a = MathF.Abs(t), sign = MathF.Sign(t);
            if (!cubic)
            {
                return sign * (a < 1 ? a - (a * a / 2) : 0.5f);
            }

            static float Inner(float u) => (u * u * u * u / 4) - (2 * u * u * u / 3) + u;
            static float Outer(float u) => (-u * u * u * u / 4) + (5 * u * u * u / 3) - (4 * u * u) + (4 * u);
            return sign * (a < 1 ? Inner(a) : Inner(1) + Outer(MathF.Min(a, 2)) - Outer(1));
        }
    }

    // GDI+'s high-quality modes, kept out of Shade's loop so the other filters run the same code as before.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ShadeBoxed(Vector2 p, Vector2 step, Span<PremulRgbaF32> row)
    {
        Span<float> wx = stackalloc float[MaxBoxedTaps], wy = stackalloc float[MaxBoxedTaps];
        for (int i = 0; i < row.Length; i++)
        {
            row[i] = FilteredBoxed(p + (step * i), wx, wy);
        }
    }

    /// <summary>The source rows a call of <see cref="ShadeInPlace"/> may hold at once: the kernel's height, plus room to reuse rows.</summary>
    private int RowsAtOnce() => _filter == ResampleFilter.Nearest ? 2
        : _boxed ? Math.Min((int)MathF.Ceiling(2 * _supportY) + 2, MaxBoxedTaps + 1)
        : Math.Min((int)MathF.Ceiling(2 * _support) + 2, FilteredTaps + 1);

    /// <summary>
    /// Shades from the image's rows, read as needed into the thread's <see cref="RowRing"/>. The mapping has no rotation or skew, so
    /// every pixel of the row reads the same source rows; the arithmetic is <see cref="Filtered"/>'s and <see cref="ShadeBoxedRows"/>'s,
    /// tap for tap, so the pixels are the ones the converted image gives.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ShadeInPlace(int y, int x, Vector2 p, Vector2 step, Span<PremulRgbaF32> row)
    {
        TexelRows rows = _rows!;
        RowRing ring = rows.Ring();
        if (_boxed)
        {
            for (int i = 0; i < row.Length; i += Chunk)
            {
                Vector2 q = i == 0 ? p : Vector2.Transform(new Vector2(x + i + 0.5f, y + 0.5f), _inverse);
                ShadeBoxedRows(x + i, q, step, row.Slice(i, Math.Min(Chunk, row.Length - i)), ring);
            }

            return;
        }

        PremulRgbaF32[] data = ring.Data;
        if (_filter == ResampleFilter.Nearest)
        {
            int ty = Tile((int)MathF.Floor(p.Y), _height, _tileY);
            int start = ty < 0 ? -1 : ring.Offset(rows, ty);
            for (int i = 0; i < row.Length; i++)
            {
                Vector2 q = p + (step * i);
                int tx = Tile((int)MathF.Floor(q.X), _width, _tileX);
                row[i] = start < 0 || tx < 0 ? default : data[start + tx];
            }

            return;
        }

        float v = p.Y - 0.5f;
        int y0 = (int)MathF.Floor(v - _support) + 1;
        int ny = Math.Min((int)MathF.Floor(v + _support) - y0 + 1, FilteredTaps);
        Span<float> wx = stackalloc float[FilteredTaps], wy = stackalloc float[FilteredTaps];
        Span<int> starts = stackalloc int[FilteredTaps];
        float sumY = 0;
        for (int j = 0; j < ny; j++)
        {
            wy[j] = Kernel(_filter, v - (y0 + j));
            sumY += wy[j];
            int ty = Tile(y0 + j, _height, _tileY);
            starts[j] = ty < 0 || wy[j] == 0 ? -1 : ring.Offset(rows, ty);
        }

        for (int i = 0; i < row.Length; i++)
        {
            Vector2 q = p + (step * i);
            float u = q.X - 0.5f;
            int x0 = (int)MathF.Floor(u - _support) + 1;
            int nx = Math.Min((int)MathF.Floor(u + _support) - x0 + 1, FilteredTaps);
            float sumX = 0;
            for (int k = 0; k < nx; k++)
            {
                wx[k] = Kernel(_filter, u - (x0 + k));
                sumX += wx[k];
            }

            if (sumX == 0 || sumY == 0)
            {
                row[i] = default;
                continue;
            }

            Vector4 acc = default;
            for (int j = 0; j < ny; j++)
            {
                if (wy[j] == 0)
                {
                    continue;
                }

                Vector4 line = default;
                int start = starts[j];
                for (int k = 0; k < nx; k++)
                {
                    if (wx[k] != 0)
                    {
                        int tx = Tile(x0 + k, _width, _tileX);
                        PremulRgbaF32 t = start < 0 || tx < 0 ? default : data[start + tx];
                        line += new Vector4(t.R, t.G, t.B, t.A) * wx[k];
                    }
                }

                acc += line * wy[j];
            }

            acc /= sumX * sumY;
            float a = Math.Clamp(acc.W, 0, 1);
            row[i] = new PremulRgbaF32(Math.Clamp(acc.X, 0, a), Math.Clamp(acc.Y, 0, a), Math.Clamp(acc.Z, 0, a), a);
        }
    }

    /// <summary>
    /// The high-quality modes for an image mapped without rotation or skew: a pixel's weights across depend only on its column and
    /// those down only on its row, so the weighted sum across each source row (the inner loop of <see cref="FilteredBoxed"/>) is the
    /// same for every device row that reads that source row. Each thread keeps those sums per run of columns for the last source
    /// rows it read and adds them up down the column in the same order, so every pixel is exactly what FilteredBoxed gives.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ShadeBoxedRows(int x, Vector2 p, Vector2 step, Span<PremulRgbaF32> row, RowRing? ring = null)
    {
        Dictionary<int, BoxedChunk> chunks = LazyInitializer.EnsureInitialized(ref _boxedLines).GetOrAdd(Environment.CurrentManagedThreadId, static _ => []);
        if (!chunks.TryGetValue(x, out BoxedChunk? chunk) || chunk.Length != row.Length || chunk.Px != p.X)
        {
            chunk = new BoxedChunk(this, p, step, row.Length);
            chunks[x] = chunk;
        }

        bool cubic = _filter == ResampleFilter.GdiPlusHighQualityBicubic;
        float v = p.Y - 0.5f;
        int y0 = (int)MathF.Floor(v - _supportY) + 1;
        int ny = Math.Min((int)MathF.Floor(v + _supportY) - y0 + 1, MaxBoxedTaps);
        Span<float> wy = stackalloc float[MaxBoxedTaps];
        float sumY = 0;
        for (int j = 0; j < ny; j++)
        {
            wy[j] = BoxedWeight(cubic, v - (y0 + j), _stretchY);
            sumY += wy[j];
        }

        for (int i = 0; i < row.Length; i++)
        {
            float sumX = chunk.SumX[i];
            if (sumX == 0 || sumY == 0)
            {
                row[i] = default;
                continue;
            }

            Vector4 acc = default;
            for (int j = 0; j < ny; j++)
            {
                acc += chunk.Line(this, y0 + j, ring)[i] * wy[j];
            }

            acc /= sumX * sumY;
            float a = Math.Clamp(acc.W, 0, 1);
            row[i] = new PremulRgbaF32(Math.Clamp(acc.X, 0, a), Math.Clamp(acc.Y, 0, a), Math.Clamp(acc.Z, 0, a), a);
        }
    }

    // GDI+'s high-quality modes: separable weights from the widened, box-averaged kernel on each axis.
    private PremulRgbaF32 FilteredBoxed(Vector2 q, Span<float> wx, Span<float> wy)
    {
        bool cubic = _filter == ResampleFilter.GdiPlusHighQualityBicubic;
        float u = q.X - 0.5f, v = q.Y - 0.5f;
        int x0 = (int)MathF.Floor(u - _supportX) + 1, y0 = (int)MathF.Floor(v - _supportY) + 1;
        int nx = Math.Min((int)MathF.Floor(u + _supportX) - x0 + 1, wx.Length), ny = Math.Min((int)MathF.Floor(v + _supportY) - y0 + 1, wy.Length);
        float sumX = 0, sumY = 0;
        for (int i = 0; i < nx; i++)
        {
            wx[i] = BoxedWeight(cubic, u - (x0 + i), _stretchX);
            sumX += wx[i];
        }

        for (int j = 0; j < ny; j++)
        {
            wy[j] = BoxedWeight(cubic, v - (y0 + j), _stretchY);
            sumY += wy[j];
        }

        if (sumX == 0 || sumY == 0)
        {
            return default;
        }

        // Taps inside the image read its rows directly; the tile modes only matter at the edges.
        bool inside = x0 >= 0 && y0 >= 0 && x0 + nx <= _width && y0 + ny <= _height;
        Vector4 acc = default;
        for (int j = 0; j < ny; j++)
        {
            Vector4 line = default;
            if (inside)
            {
                ReadOnlySpan<Vector4> texels = MemoryMarshal.Cast<PremulRgbaF32, Vector4>(_texels.AsSpan(((y0 + j) * _width) + x0, nx));
                for (int i = 0; i < nx; i++)
                {
                    line += texels[i] * wx[i];
                }
            }
            else
            {
                for (int i = 0; i < nx; i++)
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
            case ResampleFilter.GdiPlusBilinear:
                return x < 1 ? 1 - x : 0;
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

    /// <summary>
    /// One thread's run of device columns for <see cref="ShadeBoxedRows"/>: each column's taps and weights across, and the weighted
    /// sums across the source rows read most recently, one slot per row in a ring as tall as the kernel reaches.
    /// </summary>
    private sealed class BoxedChunk
    {
        private readonly int[] _x0;
        private readonly int[] _nx;
        private readonly float[] _wx;
        private readonly int[] _tags;
        private readonly Vector4[] _lines;

        public BoxedChunk(ImageShader shader, Vector2 p, Vector2 step, int length)
        {
            Length = length;
            Px = p.X;
            _x0 = new int[length];
            _nx = new int[length];
            _wx = new float[length * MaxBoxedTaps];
            SumX = new float[length];
            bool cubic = shader._filter == ResampleFilter.GdiPlusHighQualityBicubic;
            for (int i = 0; i < length; i++)
            {
                // The same column position, taps and weights FilteredBoxed takes for this pixel.
                Vector2 q = p + (step * i);
                float u = q.X - 0.5f;
                int x0 = (int)MathF.Floor(u - shader._supportX) + 1;
                int nx = Math.Min((int)MathF.Floor(u + shader._supportX) - x0 + 1, MaxBoxedTaps);
                float sum = 0;
                for (int k = 0; k < nx; k++)
                {
                    float w = BoxedWeight(cubic, u - (x0 + k), shader._stretchX);
                    _wx[(i * MaxBoxedTaps) + k] = w;
                    sum += w;
                }

                _x0[i] = x0;
                _nx[i] = nx;
                SumX[i] = sum;
            }

            int rows = Math.Min((int)MathF.Ceiling(2 * shader._supportY) + 2, MaxBoxedTaps + 1);
            _tags = new int[rows];
            Array.Fill(_tags, int.MinValue);
            _lines = new Vector4[rows * length];
        }

        public int Length { get; }

        public float Px { get; }

        public float[] SumX { get; }

        /// <summary>The weighted sums across source row <paramref name="iy"/> for each column, computed on first use.</summary>
        public ReadOnlySpan<Vector4> Line(ImageShader shader, int iy, RowRing? ring)
        {
            int slot = (int)((uint)(iy - int.MinValue) % (uint)_tags.Length);
            Span<Vector4> line = _lines.AsSpan(slot * Length, Length);
            if (_tags[slot] == iy)
            {
                return line;
            }

            // The source row's texels, in the image or the shading thread's rows; −1 for a Decal row outside.
            bool rowInside = (uint)iy < (uint)shader._height;
            int ty = Tile(iy, shader._height, shader._tileY);
            PremulRgbaF32[] data = ring is null ? shader._texels : ring.Data;
            int start = ty < 0 ? -1 : ring is null ? ty * shader._width : ring.Offset(shader._rows!, ty);
            for (int i = 0; i < Length; i++)
            {
                Vector4 sum = default;
                ReadOnlySpan<float> wx = _wx.AsSpan(i * MaxBoxedTaps, MaxBoxedTaps);
                int x0 = _x0[i], nx = _nx[i];
                if (rowInside && x0 >= 0 && x0 + nx <= shader._width)
                {
                    ReadOnlySpan<Vector4> texels = MemoryMarshal.Cast<PremulRgbaF32, Vector4>(data.AsSpan(start + x0, nx));
                    for (int k = 0; k < nx; k++)
                    {
                        sum += texels[k] * wx[k];
                    }
                }
                else
                {
                    for (int k = 0; k < nx; k++)
                    {
                        int tx = Tile(x0 + k, shader._width, shader._tileX);
                        PremulRgbaF32 t = start < 0 || tx < 0 ? default : data[start + tx];
                        sum += new Vector4(t.R, t.G, t.B, t.A) * wx[k];
                    }
                }

                line[i] = sum;
            }

            _tags[slot] = iy;
            return line;
        }
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
        // Every pixel is written, so the array needs no clearing; an image already in the target's type is copied without a converted
        // raster in between.
        _pixels = GC.AllocateUninitializedArray<TPixel>(_width * _height);
        Raster<TPixel>? converted = image as Raster<TPixel>;
        bool owned = converted is null;
        converted ??= image.As<TPixel>();
        try
        {
            for (int y = 0; y < _height; y++)
            {
                converted.Row(y).CopyTo(_pixels.AsSpan(y * _width, _width));
            }
        }
        finally
        {
            if (owned)
            {
                converted.Dispose();
            }
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

/// <summary>
/// An image's rows as premultiplied texels, converted when a shader first reads them instead of all up front. Used only when the
/// image shares no memory with the pixels the fill writes, so the rows read are the ones the draw started with. Each shading
/// thread keeps the rows it read last in a <see cref="RowRing"/>.
/// </summary>
internal abstract class TexelRows
{
    /// <summary>The fewest pixels an image needs for row reading; smaller images convert whole, which costs less than the checks.</summary>
    private const long MinPixels = 128 * 128;

    /// <summary>The largest ring a thread keeps between draws (1 MB); larger ones belong to their draw.</summary>
    private const long RetainedTexels = 1 << 16;

    private static long s_next;

    [ThreadStatic]
    private static RowRing? t_ring;

    private readonly bool _linear;
    private ConcurrentDictionary<int, RowRing>? _rings;

    private protected TexelRows(int width, bool linear)
    {
        Width = width;
        _linear = linear;
        Id = Interlocked.Increment(ref s_next);
    }

    /// <summary>Gets the draw's own number, which tells a thread's ring whose rows it holds.</summary>
    internal long Id { get; }

    /// <summary>Gets the row width in texels.</summary>
    internal int Width { get; }

    /// <summary>Gets or sets how many rows a ring holds: at least as many as one shading call reads at once.</summary>
    internal int Slots { get; set; }

    /// <summary>Returns the rows of an image in one of the common pixel types that shares no memory with the target, or <see langword="null"/>.</summary>
    internal static TexelRows? Create(Raster image, RasterTarget target, bool linear)
    {
        if ((long)image.Width * image.Height < MinPixels)
        {
            return null;
        }

        return image switch
        {
            Raster<Bgra8> r => Of(r, target, linear),
            Raster<Rgba8> r => Of(r, target, linear),
            Raster<PremulBgra8> r => Of(r, target, linear),
            Raster<PremulRgba8> r => Of(r, target, linear),
            Raster<Bgr8> r => Of(r, target, linear),
            Raster<Rgb8> r => Of(r, target, linear),
            Raster<PremulRgbaF32> r => Of(r, target, linear),
            _ => null,
        };

        static TexelRows? Of<TPixel>(Raster<TPixel> raster, RasterTarget target, bool linear)
            where TPixel : unmanaged, IPixelType<TPixel>
            => target.Disjoint(raster.Frames[0].Plane) ? new TexelRows<TPixel>(raster, linear) : null;
    }

    /// <summary>Returns the calling thread's ring, holding rows of this image.</summary>
    internal RowRing Ring()
    {
        if (Slots * (long)Width > RetainedTexels)
        {
            return LazyInitializer.EnsureInitialized(ref _rings).GetOrAdd(Environment.CurrentManagedThreadId, static _ => new RowRing()).Claim(this);
        }

        return (t_ring ??= new RowRing()).Claim(this);
    }

    /// <summary>Converts row <paramref name="y"/> into <paramref name="texels"/>, as the whole image would convert.</summary>
    internal void Read(int y, Span<PremulRgbaF32> texels)
    {
        Convert(y, texels);
        if (_linear)
        {
            ImageShader.ToLinear(texels);
        }
    }

    private protected abstract void Convert(int y, Span<PremulRgbaF32> texels);
}

/// <summary>The rows of a raster of <typeparamref name="TPixel"/>.</summary>
internal sealed class TexelRows<TPixel>(Raster<TPixel> raster, bool linear) : TexelRows(raster.Width, linear)
    where TPixel : unmanaged, IPixelType<TPixel>
{
    private protected override void Convert(int y, Span<PremulRgbaF32> texels) => PixelConvert.Convert<TPixel, PremulRgbaF32>(raster.Row(y), texels);
}

/// <summary>
/// One thread's converted rows of one image: a few slots, each holding a row, the least recently used one replaced. A shading call
/// reads at most <see cref="TexelRows.Slots"/> rows, so the rows it took stay while it runs.
/// </summary>
internal sealed class RowRing
{
    private long _owner;
    private int _width;
    private int _slots;
    private int[] _tags = [];
    private long[] _stamps = [];
    private long _clock;

    /// <summary>Gets the slots' texels, row after row.</summary>
    internal PremulRgbaF32[] Data { get; private set; } = [];

    /// <summary>Makes the ring hold rows of <paramref name="rows"/>, emptying it when it held another image's.</summary>
    internal RowRing Claim(TexelRows rows)
    {
        if (_owner != rows.Id)
        {
            _owner = rows.Id;
            _width = rows.Width;
            _slots = rows.Slots;
            if (_tags.Length < _slots)
            {
                _tags = new int[_slots];
                _stamps = new long[_slots];
            }

            if (Data.Length < _slots * _width)
            {
                Data = GC.AllocateUninitializedArray<PremulRgbaF32>(_slots * _width);
            }

            _tags.AsSpan(0, _slots).Fill(-1);
        }

        return this;
    }

    /// <summary>Returns where row <paramref name="y"/> starts in <see cref="Data"/>, converting it first if no slot holds it.</summary>
    internal int Offset(TexelRows rows, int y)
    {
        int oldest = 0;
        for (int s = 0; s < _slots; s++)
        {
            if (_tags[s] == y)
            {
                _stamps[s] = ++_clock;
                return s * _width;
            }

            if (_stamps[s] < _stamps[oldest])
            {
                oldest = s;
            }
        }

        rows.Read(y, Data.AsSpan(oldest * _width, _width));
        _tags[oldest] = y;
        _stamps[oldest] = ++_clock;
        return oldest * _width;
    }
}
