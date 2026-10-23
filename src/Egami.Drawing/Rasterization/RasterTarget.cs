using System.Numerics;

using Egami.Drawing.Shading;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Rasterization;

/// <summary>
/// Pixels a raster canvas draws into, over a device rectangle: the canvas's own raster, or a layer. A layer is premultiplied in the
/// canvas's class, PremulRgba8 for 8-bit canvases and PremulRgbaF32 for deeper ones, and covers only the device area it needs, so
/// its pixel (0, 0) is device pixel <see cref="Area"/>.Position. Everything that depends on the pixel type is here.
/// </summary>
internal abstract class RasterTarget(Rect area) : IDisposable
{
    /// <summary>Gets the device rectangle the pixels cover.</summary>
    internal Rect Area { get; } = area;

    /// <summary>
    /// Gets or sets, for a knockout layer, a copy of what the layer started as: each call composites with it and replaces the layer
    /// by its coverage. <see langword="null"/> for every other target.
    /// </summary>
    internal RasterTarget? KnockoutBackdrop { get; set; }

    /// <summary>
    /// Gets or sets, for a non-isolated layer merged with a blend mode, a copy of the backdrop the layer started as, and
    /// <see cref="GroupAlpha"/> the alpha of what was drawn into it alone; the merge removes the backdrop's part first, as PDF's
    /// non-isolated groups require (ISO 32000 11.4.8). <see langword="null"/> for every other target.
    /// </summary>
    internal RasterTarget? GroupBackdrop { get; set; }

    /// <summary>Gets or sets the group alpha, one value per pixel of <see cref="Area"/>, when <see cref="GroupBackdrop"/> is set.</summary>
    internal float[]? GroupAlpha { get; set; }

    /// <summary>Unions alpha values into <see cref="GroupAlpha"/> from device (<paramref name="x"/>, <paramref name="y"/>).</summary>
    internal void AddGroupAlpha(int y, int x, ReadOnlySpan<float> alpha)
    {
        float[] g = GroupAlpha!;
        int at = ((y - Area.Y) * Area.Width) + (x - Area.X);
        for (int i = 0; i < alpha.Length; i++)
        {
            float a = Math.Clamp(alpha[i], 0, 1);
            g[at + i] += a * (1 - g[at + i]);
        }
    }

    /// <summary>
    /// Gets or sets, for a layer inside a knockout layer, the layer's shape: the union of the coverage of everything drawn into it,
    /// one value per pixel of <see cref="Area"/>. Merging the layer replaces the knockout layer's content by this shape, not by
    /// the layer's alpha (ISO 32000 11.4.6.2). <see langword="null"/> for every other target.
    /// </summary>
    internal float[]? Shape { get; set; }

    /// <summary>Unions shape values into <see cref="Shape"/> from device (<paramref name="x"/>, <paramref name="y"/>).</summary>
    internal void AddShape(int y, int x, ReadOnlySpan<float> shape)
    {
        float[] s = Shape!;
        int at = ((y - Area.Y) * Area.Width) + (x - Area.X);
        for (int i = 0; i < shape.Length; i++)
        {
            float v = Math.Clamp(shape[i], 0, 1);
            s[at + i] += v * (1 - s[at + i]);
        }
    }

    /// <summary>
    /// Mixes these pixels with <paramref name="before"/>'s over <paramref name="area"/>: each pixel moves from what
    /// <paramref name="before"/> holds toward what this holds by its weight, one per pixel of the area, row by row.
    /// </summary>
    internal virtual void Mix(RasterTarget before, Rect area, ReadOnlySpan<float> weight)
    {
        PremulRgbaF32[] row = new PremulRgbaF32[area.Width], from = new PremulRgbaF32[area.Width];
        for (int y = area.Y; y < area.Bottom; y++)
        {
            ReadRow<PremulRgbaF32>(y, area.X, row);
            before.ReadRow<PremulRgbaF32>(y, area.X, from);
            ReadOnlySpan<float> w = weight.Slice((y - area.Y) * area.Width, area.Width);
            for (int i = 0; i < row.Length; i++)
            {
                PremulRgbaF32 a = from[i], b = row[i];
                float c = w[i];
                row[i] = new PremulRgbaF32(a.R + ((b.R - a.R) * c), a.G + ((b.G - a.G) * c), a.B + ((b.B - a.B) * c), a.A + ((b.A - a.A) * c));
            }

            WriteRow<PremulRgbaF32>(y, area.X, row);
        }
    }

    /// <summary>
    /// Turns a non-isolated layer into the group result PDF composites: the backdrop's contribution removed by
    /// C = Cn + (Cn − C0)·(α0 / αg − α0), with the group alpha αg.
    /// </summary>
    internal virtual void RemoveGroupBackdrop()
    {
        if (GroupBackdrop is not RasterTarget backdrop || GroupAlpha is not float[] g)
        {
            return;
        }

        PremulRgbaF32[] row = new PremulRgbaF32[Area.Width], first = new PremulRgbaF32[Area.Width];
        for (int y = Area.Y; y < Area.Bottom; y++)
        {
            ReadRow<PremulRgbaF32>(y, Area.X, row);
            backdrop.ReadRow<PremulRgbaF32>(y, Area.X, first);
            int at = (y - Area.Y) * Area.Width;
            for (int i = 0; i < row.Length; i++)
            {
                float ag = g[at + i];
                if (ag <= 0)
                {
                    row[i] = default;
                    continue;
                }

                PremulRgbaF32 n = row[i], b = first[i];
                float an = n.A, a0 = b.A;
                float k = an > 0 ? 1 / an : 0, k0 = a0 > 0 ? 1 / a0 : 0;
                float f = (a0 / ag) - a0;
                float r = Math.Clamp((n.R * k) + (((n.R * k) - (b.R * k0)) * f), 0, 1);
                float gr = Math.Clamp((n.G * k) + (((n.G * k) - (b.G * k0)) * f), 0, 1);
                float bl = Math.Clamp((n.B * k) + (((n.B * k) - (b.B * k0)) * f), 0, 1);
                row[i] = new PremulRgbaF32(r * ag, gr * ag, bl * ag, ag);
            }

            WriteRow<PremulRgbaF32>(y, Area.X, row);
        }
    }

    /// <summary>Gets the size of one pixel, which decides when a fill bands across cores.</summary>
    internal abstract int BytesPerPixel { get; }

    /// <summary>Returns the sink that blends a paint into these pixels, or <see langword="null"/> when the paint draws nothing.</summary>
    internal abstract ISpanSink? CreateSink(Paint paint, Matrix3x2 matrix, ColorSpace colorSpace, ClipMask? mask, byte scale);

    /// <summary>Returns an empty layer over <paramref name="area"/> in this canvas's layer pixel type.</summary>
    internal abstract RasterTarget CreateLayer(Rect area, EgamiContext context);

    /// <summary>Returns an empty layer for a call to SaveLayer with <paramref name="options"/>; by default the same as any layer.</summary>
    internal virtual RasterTarget CreateLayer(Rect area, EgamiContext context, LayerOptions options) => CreateLayer(area, context);

    /// <summary>Reads a run of alpha values from device (<paramref name="x"/>, <paramref name="y"/>).</summary>
    internal virtual void ReadAlpha(int y, int x, Span<float> alpha)
    {
        PremulRgbaF32[] row = new PremulRgbaF32[alpha.Length];
        ReadRow<PremulRgbaF32>(y, x, row);
        for (int i = 0; i < row.Length; i++)
        {
            alpha[i] = row[i].A;
        }
    }

    /// <summary>Copies the pixels of <paramref name="area"/> (device coordinates, inside both) from <paramref name="source"/>.</summary>
    internal abstract void CopyFrom(RasterTarget source, Rect area);

    /// <summary>Reads a run of device pixels converted to <typeparamref name="T"/>.</summary>
    internal abstract void ReadRow<T>(int y, int x, Span<T> destination)
        where T : unmanaged, IPixelType<T>;

    /// <summary>Writes a run of device pixels converted from <typeparamref name="T"/>.</summary>
    internal abstract void WriteRow<T>(int y, int x, ReadOnlySpan<T> source)
        where T : unmanaged, IPixelType<T>;

    /// <summary>Blends a layer's pixels over <paramref name="area"/> into these, through the clip's coverage.</summary>
    internal abstract void Merge(RasterTarget layer, Rect area, ClipMask? mask, BlendMode mode, float opacity, ColorSpace colorSpace);

    /// <summary>Runs an operation chain on a layer's pixels; the chain must keep the size.</summary>
    internal abstract void Filter(Action<RasterOps> filter);

    /// <summary>Multiplies a layer's premultiplied pixels from device (<paramref name="x"/>, <paramref name="y"/>) by mask values from 0 to 1.</summary>
    internal abstract void Multiply(int y, int x, ReadOnlySpan<float> mask);

    /// <summary>Draws an image under a transform through the clip.</summary>
    internal abstract void DrawImage(DrawTools tools, Raster image, RectF source, RectF destination, Matrix3x2 matrix, SamplingOptions sampling, Paint? paint, Rect clip, ClipMask? mask);

    public abstract void Dispose();
}

/// <summary>The rasterizer, edge list, settings and context a raster canvas draws with.</summary>
internal sealed class DrawTools(CoverageRasterizer rasterizer, EdgeList edges, CanvasOptions options, EgamiContext context)
{
    internal CoverageRasterizer Rasterizer { get; } = rasterizer;

    internal EdgeList Edges { get; } = edges;

    internal CanvasOptions Options { get; } = options;

    internal EgamiContext Context { get; } = context;
}

/// <summary>A <see cref="RasterTarget"/> of one pixel type.</summary>
/// <typeparam name="TPixel">The pixel type.</typeparam>
internal sealed class RasterTarget<TPixel> : RasterTarget
    where TPixel : unmanaged, IPixelType<TPixel>
{
    private readonly Raster<TPixel>? _owned;
    private PixelPlane<TPixel> _plane;

    /// <summary>Initializes a new instance of the <see cref="RasterTarget{TPixel}"/> class over a canvas's plane, which it does not own.</summary>
    internal RasterTarget(PixelPlane<TPixel> plane)
        : base(new Rect(0, 0, plane.Width, plane.Height))
    {
        _plane = plane;
    }

    /// <summary>Initializes a new instance of the <see cref="RasterTarget{TPixel}"/> class as a layer that owns its raster.</summary>
    private RasterTarget(Raster<TPixel> owned, Rect area)
        : base(area)
    {
        _owned = owned;
        _plane = owned.Frames[0].Plane;
    }

    internal override int BytesPerPixel => System.Runtime.CompilerServices.Unsafe.SizeOf<TPixel>();

    private static bool EightBit => TPixel.Layout.ChannelType == PixelChannelType.Unorm8;

    /// <summary>Returns the run of <paramref name="length"/> pixels from device (<paramref name="x"/>, <paramref name="y"/>).</summary>
    internal Span<TPixel> Row(int y, int x, int length) => _plane.Row(y - Area.Y).Slice(x - Area.X, length);

    internal override ISpanSink? CreateSink(Paint paint, Matrix3x2 matrix, ColorSpace colorSpace, ClipMask? mask, byte scale)
    {
        // In a knockout layer the paint composites fully with the layer's first content, and the coverage only chooses how much of
        // that result replaces what is there.
        ISpanSink? sink;
        if (KnockoutBackdrop is RasterTarget backdrop)
        {
            ISpanSink? inner = CreatePaintSink(paint, matrix, colorSpace, null, 255);
            sink = inner is null ? null : new KnockoutSink<TPixel>(this, backdrop, inner, mask, scale);
        }
        else if (GroupAlpha is not null)
        {
            ISpanSink? inner = CreatePaintSink(paint, matrix, colorSpace, mask, scale);
            sink = inner is null ? null : new GroupAlphaSink<TPixel>(this, inner);
        }
        else
        {
            sink = CreatePaintSink(paint, matrix, colorSpace, mask, scale);
        }

        return sink is not null && Shape is not null ? new ShapeSink(this, sink, mask, scale) : sink;
    }

    private ISpanSink? CreatePaintSink(Paint paint, Matrix3x2 matrix, ColorSpace colorSpace, ClipMask? mask, byte scale)
    {
        if (paint is SolidPaint solid)
        {
            return new SolidSink<TPixel>(this, solid, colorSpace, mask, scale);
        }

        // A target without alpha blends shaded paints in a type with alpha, so their transparent parts stay transparent.
        if (TPixel.Layout.HasAlpha)
        {
            return Shaded<TPixel>(paint, matrix, colorSpace, mask, scale);
        }

        return EightBit ? Shaded<Rgba8>(paint, matrix, colorSpace, mask, scale) : Shaded<RgbaF32>(paint, matrix, colorSpace, mask, scale);
    }

    internal override RasterTarget CreateLayer(Rect area, EgamiContext context)
        => EightBit ? RasterTarget<PremulRgba8>.Layer(area, context) : RasterTarget<PremulRgbaF32>.Layer(area, context);

    internal override void CopyFrom(RasterTarget source, Rect area)
    {
        for (int y = area.Y; y < area.Bottom; y++)
        {
            source.ReadRow(y, area.X, Row(y, area.X, area.Width));
        }
    }

    internal override void ReadRow<T>(int y, int x, Span<T> destination)
    {
        Span<TPixel> row = Row(y, x, destination.Length);
        if (typeof(T) == typeof(TPixel))
        {
            row.CopyTo(System.Runtime.InteropServices.MemoryMarshal.Cast<T, TPixel>(destination));
        }
        else
        {
            PixelConvert.Convert<TPixel, T>(row, destination);
        }
    }

    internal override void WriteRow<T>(int y, int x, ReadOnlySpan<T> source)
    {
        Span<TPixel> row = Row(y, x, source.Length);
        if (typeof(T) == typeof(TPixel))
        {
            System.Runtime.InteropServices.MemoryMarshal.Cast<T, TPixel>(source).CopyTo(row);
        }
        else
        {
            PixelConvert.Convert<T, TPixel>(source, row);
        }
    }

    internal override void Merge(RasterTarget layer, Rect area, ClipMask? mask, BlendMode mode, float opacity, ColorSpace colorSpace)
    {
        if (TPixel.Layout.HasAlpha)
        {
            MergeAs<TPixel>(layer, area, mask, mode, opacity, colorSpace);
        }
        else if (EightBit)
        {
            MergeAs<Rgba8>(layer, area, mask, mode, opacity, colorSpace);
        }
        else
        {
            MergeAs<RgbaF32>(layer, area, mask, mode, opacity, colorSpace);
        }
    }

    internal override void Filter(Action<RasterOps> filter)
    {
        Raster<TPixel> raster = _owned ?? throw new InvalidOperationException("Only a layer can be filtered.");
        raster.Apply(filter);
        if (raster.Width != Area.Width || raster.Height != Area.Height)
        {
            throw new InvalidOperationException(
                $"A layer filter must keep the size, but it made the {Area.Width}×{Area.Height} layer {raster.Width}×{raster.Height}; for a drop shadow, set DropShadowOptions.Expand to false.");
        }

        _plane = raster.Frames[0].Plane;
    }

    internal override void Multiply(int y, int x, ReadOnlySpan<float> mask)
    {
        Span<TPixel> row = Row(y, x, mask.Length);
        if (typeof(TPixel) == typeof(PremulRgbaF32))
        {
            Span<PremulRgbaF32> p = System.Runtime.InteropServices.MemoryMarshal.Cast<TPixel, PremulRgbaF32>(row);
            for (int i = 0; i < p.Length; i++)
            {
                float m = mask[i];
                p[i] = new PremulRgbaF32(p[i].R * m, p[i].G * m, p[i].B * m, p[i].A * m);
            }
        }
        else if (typeof(TPixel) == typeof(PremulRgba8))
        {
            Span<PremulRgba8> p = System.Runtime.InteropServices.MemoryMarshal.Cast<TPixel, PremulRgba8>(row);
            for (int i = 0; i < p.Length; i++)
            {
                int m = (int)MathF.Round(mask[i] * 255);
                p[i] = new PremulRgba8(Mul(p[i].R, m), Mul(p[i].G, m), Mul(p[i].B, m), Mul(p[i].A, m));
            }
        }
        else
        {
            throw new InvalidOperationException("Only a layer can be masked.");
        }
    }

    internal override void DrawImage(DrawTools tools, Raster image, RectF source, RectF destination, Matrix3x2 matrix, SamplingOptions sampling, Paint? paint, Rect clip, ClipMask? mask)
    {
        if (KnockoutBackdrop is not null || GroupAlpha is not null || Shape is not null)
        {
            DrawImageAsPaint(tools, image, source, destination, matrix, sampling, paint, clip, mask);
            return;
        }

        BlendMode mode = paint?.BlendMode ?? BlendMode.SourceOver;
        float opacity = paint?.Opacity ?? 1;
        bool antialias = paint?.Antialias ?? true;
        ColorSpace colorSpace = tools.Options.ColorSpace;

        // Image pixel (u, v) → device: scale and move the source rectangle onto the destination, then the canvas transform.
        float sx = destination.Width / source.Width, sy = destination.Height / source.Height;
        Matrix3x2 toDevice = Matrix3x2.CreateTranslation(-source.X, -source.Y) * Matrix3x2.CreateScale(sx, sy)
            * Matrix3x2.CreateTranslation(destination.X, destination.Y) * matrix;

        // A transform that collapses the image (a zero scale, or one that overflowed) covers no area: nothing to draw.
        float det = toDevice.GetDeterminant();
        if (!(MathF.Abs(det) > 0 && float.IsFinite(det) && float.IsFinite(toDevice.M31) && float.IsFinite(toDevice.M32)))
        {
            return;
        }

        Raster<TPixel>? converted = image as Raster<TPixel>;
        bool owned = converted is null;
        converted ??= image.As<TPixel>();
        try
        {
            if (IsIntegerPlacement(toDevice, source, out Point offset))
            {
                DrawPlaced(converted, source.RoundOut(), offset, clip, mask, mode, opacity, colorSpace);
                return;
            }

            // The destination's device outline gives the edge coverage; the transform runs over the part of it inside the clip.
            EdgeList edges = tools.Edges;
            Path area = Path.Rect(destination).Transform(matrix);
            edges.Clear();
            Flattener.Flatten(area, Matrix3x2.Identity, tools.Options.Tolerance, edges);
            Rect viewport = clip.Intersect(edges.Bounds.RoundOut());
            if (viewport.IsEmpty)
            {
                return;
            }

            // The source is cut to the whole pixels it uses, so samples near its edges read only pixels inside it.
            Rect crop = source.RoundOut();
            Matrix3x2 cropToDevice = Matrix3x2.CreateTranslation(crop.X, crop.Y) * toDevice;
            Homography placement = Homography.FromAffine(cropToDevice);
            if (!placement.TryInvert(out _))
            {
                return;
            }
            TransformOptions options = new()
            {
                Filter = sampling.Filter,
                Edges = sampling.Edges == EdgeMode.Transparent ? EdgeMode.Clamp : sampling.Edges,
                Background = sampling.Background,
                ColorSpace = sampling.ColorSpace,
                Viewport = viewport,
            };
            using Raster<TPixel> placed = converted.Derive(ops =>
            {
                if (crop != new Rect(0, 0, converted.Width, converted.Height))
                {
                    ops.Crop(crop);
                }

                ops.Transform(cropToDevice, options);
            });
            ImageSink<TPixel> sink = new(this, placed, viewport.Position, mode, opacity, colorSpace, mask);
            tools.Rasterizer.Fill(edges, viewport, FillRule.NonZero, antialias, sink, BytesPerPixel);
        }
        finally
        {
            if (owned)
            {
                converted.Dispose();
            }
        }
    }

    public override void Dispose()
    {
        _owned?.Dispose();
        KnockoutBackdrop?.Dispose();
        GroupBackdrop?.Dispose();
    }

    /// <summary>Draws an image as an image paint filling its destination, so it goes through <see cref="CreateSink"/>: a knockout layer's way.</summary>
    private void DrawImageAsPaint(DrawTools tools, Raster image, RectF source, RectF destination, Matrix3x2 matrix, SamplingOptions sampling, Paint? paint, Rect clip, ClipMask? mask)
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

    private static RasterTarget<TPixel> Layer(Rect area, EgamiContext context) => new(Raster.Create<TPixel>(area.Width, area.Height, context), area);

    private static byte Mul(byte value, int factor)
    {
        int p = (value * factor) + 128;
        return (byte)((p + (p >> 8)) >> 8);
    }

    /// <summary>Whether the image maps onto whole device pixels one to one: no scale, no turn, a whole-pixel shift.</summary>
    private static bool IsIntegerPlacement(Matrix3x2 m, RectF source, out Point offset)
    {
        offset = default;
        if (m.M11 != 1 || m.M22 != 1 || m.M12 != 0 || m.M21 != 0 || m.M31 != MathF.Round(m.M31) || m.M32 != MathF.Round(m.M32))
        {
            return false;
        }

        if (source.X != MathF.Round(source.X) || source.Y != MathF.Round(source.Y) || source.Width != MathF.Round(source.Width) || source.Height != MathF.Round(source.Height))
        {
            return false;
        }

        offset = new Point((int)m.M31, (int)m.M32);
        return true;
    }

    private ShaderSink<TPixel, TSource>? Shaded<TSource>(Paint paint, Matrix3x2 matrix, ColorSpace colorSpace, ClipMask? mask, byte scale)
        where TSource : unmanaged, IPixelType<TSource>
    {
        PixelShader<TSource>? shader = paint.CreatePixelShader<TSource>(matrix, colorSpace);
        return shader is null ? null : new ShaderSink<TPixel, TSource>(this, shader, paint, colorSpace, mask, scale);
    }

    private void MergeAs<TWork>(RasterTarget layer, Rect area, ClipMask? mask, BlendMode mode, float opacity, ColorSpace colorSpace)
        where TWork : unmanaged, IPixelType<TWork>
    {
        bool same = typeof(TWork) == typeof(TPixel);
        TWork[] source = new TWork[area.Width];
        TWork[]? under = same ? null : new TWork[area.Width];
        for (int y = area.Y; y < area.Bottom; y++)
        {
            layer.ReadRow<TWork>(y, area.X, source);
            Span<TPixel> row = Row(y, area.X, area.Width);
            Span<TWork> destination = same ? System.Runtime.InteropServices.MemoryMarshal.Cast<TPixel, TWork>(row) : under;
            if (!same)
            {
                PixelConvert.Convert<TPixel, TWork>(row, destination);
            }

            if (mask is not null)
            {
                ReadOnlySpan<byte> coverage = mask.Row(y).Slice(area.X - mask.Bounds.X, area.Width);
                Compositor.Blend(mode, source, destination, coverage, opacity, colorSpace);
            }
            else
            {
                Compositor.Blend(mode, source, destination, opacity, colorSpace);
            }

            if (!same)
            {
                PixelConvert.Convert<TWork, TPixel>(destination, row);
            }
        }
    }

    /// <summary>Blends source rows straight onto the destination, as 2.5.1's DrawImage does, through the clip mask when there is one.</summary>
    private void DrawPlaced(Raster<TPixel> image, Rect source, Point offset, Rect clip, ClipMask? mask, BlendMode mode, float opacity, ColorSpace colorSpace)
    {
        // The source rectangle lands at its own position plus the offset.
        Rect target = new Rect(source.X + offset.X, source.Y + offset.Y, source.Width, source.Height).Intersect(clip);
        if (target.IsEmpty)
        {
            return;
        }

        for (int y = target.Y; y < target.Bottom; y++)
        {
            ReadOnlySpan<TPixel> from = image.Row(y - offset.Y).Slice(target.X - offset.X, target.Width);
            Span<TPixel> to = Row(y, target.X, target.Width);
            if (mask is not null)
            {
                Compositor.Blend(mode, from, to, mask.Row(y).Slice(target.X - mask.Bounds.X, target.Width), opacity, colorSpace);
            }
            else
            {
                Compositor.Blend(mode, from, to, opacity, colorSpace);
            }
        }
    }
}
