using System.Numerics;

using Egami.Colors;
using Egami.Drawing.Shading;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Rasterization;

/// <summary>
/// An ink raster's planes as a raster canvas's target, over a device rectangle: the canvas's own <see cref="InkRaster"/>, or a
/// layer of the same inks. Planes hold ink as printed over paper (premultiplied by the alpha plane), 0 to the depth's maximum.
/// The shared rasterizer gives coverage; this target turns it, with the paint's inks and alpha, into plane values.
/// </summary>
internal sealed class InkTarget : RasterTarget
{
    private readonly InkRaster _raster;
    private readonly bool _owned;
    private readonly InkPlate[] _planes;   // the plates, then alpha

    internal InkTarget(InkRaster raster, InkResolver resolver, Rect area, bool owned)
        : base(area)
    {
        _raster = raster;
        _owned = owned;
        Resolver = resolver;
        _planes = [.. raster.Plates, raster.Alpha];
    }

    /// <summary>Gets how colours become tints for these planes.</summary>
    internal InkResolver Resolver { get; }

    /// <summary>Gets the number of ink planes, alpha not counted.</summary>
    internal int Inks => _planes.Length - 1;

    internal bool Wide => _raster.Depth == InkDepth.Unorm16;

    internal override int BytesPerPixel => (_planes.Length) * (Wide ? 2 : 1);

    internal override ISpanSink? CreateSink(Paint paint, Matrix3x2 matrix, ColorSpace colorSpace, ClipMask? mask, byte scale)
    {
        if (KnockoutBackdrop is not null || GroupAlpha is not null)
        {
            throw new NotSupportedException("Knockout and non-isolated layers on an ink canvas come with Egami 2.6.5's group work.");
        }

        CheckMode(paint.BlendMode);
        if (paint is SolidPaint solid)
        {
            float[] tints = new float[Inks];
            float alpha = Resolver.Resolve(solid, tints);
            return alpha <= 0 && paint.BlendMode == BlendMode.SourceOver ? null : new InkSolidSink(this, tints, alpha * paint.Opacity, paint.BlendMode, mask, scale);
        }

        PixelShader<RgbaF32>? shader = paint.CreatePixelShader<RgbaF32>(matrix, colorSpace);
        return shader is null ? null : new InkShaderSink(this, shader, paint.Opacity, paint.BlendMode, mask, scale);
    }

    internal override RasterTarget CreateLayer(Rect area, EgamiContext context)
        => new InkTarget(new InkRaster(_raster.Inks, area.Width, area.Height, _raster.Depth, context), Resolver, area, owned: true);

    internal override void CopyFrom(RasterTarget source, Rect area)
    {
        InkTarget from = (InkTarget)source;
        for (int p = 0; p < _planes.Length; p++)
        {
            for (int y = area.Y; y < area.Bottom; y++)
            {
                if (Wide)
                {
                    from.Row16(p, y, area.X, area.Width).CopyTo(Row16(p, y, area.X, area.Width));
                }
                else
                {
                    from.Row8(p, y, area.X, area.Width).CopyTo(Row8(p, y, area.X, area.Width));
                }
            }
        }
    }

    internal override void ReadRow<T>(int y, int x, Span<T> destination) => throw new NotSupportedException("An ink canvas has no RGB pixels to read.");

    internal override void WriteRow<T>(int y, int x, ReadOnlySpan<T> source) => throw new NotSupportedException("An ink canvas has no RGB pixels to write.");

    /// <summary>Merges a layer of the same inks: source-over per plane with the layer's alpha, times opacity and the clip.</summary>
    internal override void Merge(RasterTarget layer, Rect area, ClipMask? mask, BlendMode mode, float opacity, ColorSpace colorSpace)
    {
        CheckMode(mode);
        InkTarget from = (InkTarget)layer;
        float[] k = new float[area.Width];
        float[] alpha = new float[area.Width];
        for (int y = area.Y; y < area.Bottom; y++)
        {
            ReadOnlySpan<byte> clip = mask is null ? default : mask.Row(y).Slice(area.X - mask.Bounds.X, area.Width);
            from.ReadPlane(Inks, y, area.X, alpha);
            for (int i = 0; i < k.Length; i++)
            {
                k[i] = opacity * (clip.IsEmpty ? 1 : clip[i] / 255f);
            }

            float[] source = new float[area.Width];
            for (int p = 0; p < _planes.Length; p++)
            {
                from.ReadPlane(p, y, area.X, source);
                if (Wide)
                {
                    Span<ushort> d = Row16(p, y, area.X, area.Width);
                    for (int i = 0; i < d.Length; i++)
                    {
                        float under = d[i] / 65535f;
                        d[i] = To16((source[i] * k[i]) + (under * (1 - (alpha[i] * k[i]))));
                    }
                }
                else
                {
                    Span<byte> d = Row8(p, y, area.X, area.Width);
                    for (int i = 0; i < d.Length; i++)
                    {
                        float under = d[i] / 255f;
                        d[i] = To8((source[i] * k[i]) + (under * (1 - (alpha[i] * k[i]))));
                    }
                }
            }
        }
    }

    internal override void Filter(Action<RasterOps> filter) => throw new NotSupportedException("Layer filters run on RGB pixels; an ink canvas's layers take none.");

    internal override void Multiply(int y, int x, ReadOnlySpan<float> mask)
    {
        for (int p = 0; p < _planes.Length; p++)
        {
            if (Wide)
            {
                Span<ushort> d = Row16(p, y, x, mask.Length);
                for (int i = 0; i < d.Length; i++)
                {
                    d[i] = To16(d[i] / 65535f * mask[i]);
                }
            }
            else
            {
                Span<byte> d = Row8(p, y, x, mask.Length);
                for (int i = 0; i < d.Length; i++)
                {
                    d[i] = To8(d[i] / 255f * mask[i]);
                }
            }
        }
    }

    /// <summary>Draws an image as an image paint over its destination, so it is shaded, converted and blended like any paint.</summary>
    internal override void DrawImage(DrawTools tools, Raster image, RectF source, RectF destination, Matrix3x2 matrix, SamplingOptions sampling, Paint? paint, Rect clip, ClipMask? mask)
    {
        Matrix3x2 imageToLocal = Matrix3x2.CreateTranslation(-source.X, -source.Y) * Matrix3x2.CreateScale(destination.Width / source.Width, destination.Height / source.Height)
            * Matrix3x2.CreateTranslation(destination.X, destination.Y);
        ImagePaint fill = new(image, TileMode.Clamp, TileMode.Clamp, sampling)
        {
            LocalMatrix = imageToLocal,
            BlendMode = paint?.BlendMode ?? BlendMode.SourceOver,
            Opacity = paint?.Opacity ?? 1,
        };
        EdgeList edges = tools.Edges;
        edges.Clear();
        Flattener.Flatten(Path.Rect(destination), matrix, tools.Options.Tolerance, edges);
        if (CreateSink(fill, matrix, tools.Options.ColorSpace, mask, 255) is ISpanSink sink)
        {
            tools.Rasterizer.Fill(edges, clip, FillRule.NonZero, paint?.Antialias ?? true, sink, BytesPerPixel);
        }
    }

    public override void Dispose()
    {
        if (_owned)
        {
            _raster.Dispose();
        }

        KnockoutBackdrop?.Dispose();
        GroupBackdrop?.Dispose();
    }

    internal Span<byte> Row8(int plane, int y, int x, int length) => _planes[plane].Row8(y - Area.Y).Slice(x - Area.X, length);

    internal Span<ushort> Row16(int plane, int y, int x, int length) => _planes[plane].Row16(y - Area.Y).Slice(x - Area.X, length);

    /// <summary>Reads a plane's run as 0 to 1.</summary>
    internal void ReadPlane(int plane, int y, int x, Span<float> values)
    {
        if (Wide)
        {
            ReadOnlySpan<ushort> row = Row16(plane, y, x, values.Length);
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = row[i] / 65535f;
            }
        }
        else
        {
            ReadOnlySpan<byte> row = Row8(plane, y, x, values.Length);
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = row[i] / 255f;
            }
        }
    }

    internal static byte To8(float v) => v > 0 ? v < 1 ? (byte)((v * 255) + 0.5f) : (byte)255 : (byte)0;

    internal static ushort To16(float v) => v > 0 ? v < 1 ? (ushort)((v * 65535) + 0.5f) : (ushort)65535 : (ushort)0;

    private static void CheckMode(BlendMode mode)
    {
        if (mode is not (BlendMode.SourceOver or BlendMode.Source))
        {
            throw new NotSupportedException($"The blend mode {mode} on an ink canvas comes with Egami 2.6.5's blend mode work.");
        }
    }
}

/// <summary>
/// Blends straight ink tints with an alpha through coverage, every plane at once: source-over leaves t·a + d·(1 − a) on each ink
/// plane and a + d·(1 − a) on alpha, with a the paint's alpha times opacity and coverage; Source replaces by coverage.
/// </summary>
internal sealed class InkSolidSink(InkTarget target, float[] tints, float alpha, BlendMode mode, ClipMask? mask, byte scale) : RasterSink(mask, scale)
{
    protected override void Write(int y, int x, ReadOnlySpan<byte> coverage)
    {
        Span<float> a = stackalloc float[coverage.Length];
        for (int i = 0; i < a.Length; i++)
        {
            a[i] = coverage[i] / 255f;
        }

        InkBlend.Blend(target, y, x, a, tints, alpha, mode);
    }
}

/// <summary>Blends a shaded RGB paint: each run is shaded in straight RGBA, converted to the process inks, and blended like a solid.</summary>
internal sealed class InkShaderSink(InkTarget target, PixelShader<RgbaF32> shader, float opacity, BlendMode mode, ClipMask? mask, byte scale) : RasterSink(mask, scale)
{
    public override bool FillsDirectly => Plain;

    protected override void Write(int y, int x, ReadOnlySpan<byte> coverage) => Run(y, x, coverage.Length, coverage);

    protected override bool TryFill(int y, int x, int length)
    {
        Run(y, x, length, default);
        return true;
    }

    private void Run(int y, int x, int length, ReadOnlySpan<byte> coverage)
    {
        int inks = target.Inks;
        Span<RgbaF32> colors = stackalloc RgbaF32[Shader.Chunk];
        float[] tints = new float[Shader.Chunk * inks];
        Span<float> a = stackalloc float[Shader.Chunk];
        for (int i = 0; i < length; i += Shader.Chunk)
        {
            int n = Math.Min(Shader.Chunk, length - i);
            shader.Shade(y, x + i, colors[..n]);
            target.Resolver.ResolveRgb(colors[..n], tints.AsSpan(0, n * inks));
            for (int j = 0; j < n; j++)
            {
                a[j] = colors[j].A * opacity * (coverage.IsEmpty ? 1 : coverage[i + j] / 255f);
            }

            InkBlend.BlendInterleaved(target, y, x + i, a[..n], tints.AsSpan(0, n * inks), mode);
        }
    }
}

/// <summary>The per-plane arithmetic of the ink sinks.</summary>
internal static class InkBlend
{
    /// <summary>One colour (tints per plane, an alpha) through per-pixel coverage.</summary>
    internal static void Blend(InkTarget target, int y, int x, ReadOnlySpan<float> coverage, ReadOnlySpan<float> tints, float alpha, BlendMode mode)
    {
        int n = coverage.Length;
        for (int p = 0; p <= tints.Length; p++)
        {
            float t = p < tints.Length ? tints[p] : 1;   // the alpha plane takes 1, so it gains a·(1 − d)
            if (target.Wide)
            {
                Span<ushort> d = target.Row16(p, y, x, n);
                for (int i = 0; i < n; i++)
                {
                    d[i] = InkTarget.To16(Mix(t, alpha, d[i] / 65535f, coverage[i], mode));
                }
            }
            else
            {
                Span<byte> d = target.Row8(p, y, x, n);
                for (int i = 0; i < n; i++)
                {
                    d[i] = InkTarget.To8(Mix(t, alpha, d[i] / 255f, coverage[i], mode));
                }
            }
        }
    }

    /// <summary>A run of colours, tints interleaved per pixel, each pixel's alpha already times opacity and coverage.</summary>
    internal static void BlendInterleaved(InkTarget target, int y, int x, ReadOnlySpan<float> alpha, ReadOnlySpan<float> tints, BlendMode mode)
    {
        int n = alpha.Length, inks = target.Inks;
        for (int p = 0; p <= inks; p++)
        {
            if (target.Wide)
            {
                Span<ushort> d = target.Row16(p, y, x, n);
                for (int i = 0; i < n; i++)
                {
                    float t = p < inks ? tints[(i * inks) + p] : 1;
                    d[i] = InkTarget.To16(Mix(t, 1, d[i] / 65535f, alpha[i], mode));
                }
            }
            else
            {
                Span<byte> d = target.Row8(p, y, x, n);
                for (int i = 0; i < n; i++)
                {
                    float t = p < inks ? tints[(i * inks) + p] : 1;
                    d[i] = InkTarget.To8(Mix(t, 1, d[i] / 255f, alpha[i], mode));
                }
            }
        }
    }

    // Source-over of a straight tint t at alpha a·c over the premultiplied value d; Source replaces by coverage c.
    private static float Mix(float t, float a, float d, float c, BlendMode mode)
    {
        if (mode == BlendMode.Source)
        {
            return (t * a * c) + (d * (1 - c));
        }

        float s = a * c;
        return (t * s) + (d * (1 - s));
    }
}
