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
        ISpanSink? sink = CreatePaintSink(paint, matrix, colorSpace, mask, scale);
        return sink is not null && GroupAlpha is not null ? new InkGroupAlphaSink(this, sink) : sink;
    }

    private ISpanSink? CreatePaintSink(Paint paint, Matrix3x2 matrix, ColorSpace colorSpace, ClipMask? mask, byte scale)
    {
        switch (paint)
        {
            case InkImagePaint image:
                return image.TryDeviceToImage(matrix, out Matrix3x2 inverse) ? new InkImageSink(this, image, inverse, mask, scale) : null;
            case Gradient gradient when gradient.Stops.Any(s => s.Ink is not null) && gradient.CreateGeometryFor(matrix) is GradientGeometry geometry:
                return new InkGradientSink(this, gradient, geometry, paint, mask, scale);
            case MeshShadingPaint { Shader.Inks: InkColor[] inks } shading:
                return new InkMeshSink(this, shading.Shader, inks, paint, mask, scale);
        }

        if (paint is SolidPaint solid)
        {
            float[] tints = new float[Inks];
            float alpha = Resolver.Resolve(solid, tints);
            return alpha <= 0 && paint.BlendMode == BlendMode.SourceOver ? null : new InkSolidSink(this, tints, alpha, paint, mask, scale);
        }

        PixelShader<RgbaF32>? shader = paint.CreatePixelShader<RgbaF32>(matrix, colorSpace);
        return shader is null ? null : new InkShaderSink(this, shader, paint, mask, scale);
    }

    internal override RasterTarget CreateLayer(Rect area, EgamiContext context)
        => new InkTarget(new InkRaster(_raster.Inks, area.Width, area.Height, _raster.Depth, context), Resolver, area, owned: true);

    /// <summary>A layer of the same inks, or with <see cref="LayerOptions.ColorSpace"/> one that blends its process part in that space.</summary>
    internal override RasterTarget CreateLayer(Rect area, EgamiContext context, LayerOptions options)
    {
        if (options.ColorSpace is not ColorProfile space || Resolver.Space is not null)
        {
            return CreateLayer(area, context);
        }

        (InkResolver resolver, InkSet layout) = Resolver.ForSpace(space);
        return new InkTarget(new InkRaster(layout, area.Width, area.Height, _raster.Depth, context), resolver, area, owned: true);
    }

    internal override void ReadAlpha(int y, int x, Span<float> alpha) => ReadPlane(Inks, y, x, alpha);

    /// <summary>
    /// Turns a non-isolated layer into the group result PDF composites, per plane in complement space:
    /// C = Cn + (Cn − C0)·(α0 / αg − α0), with the group alpha αg.
    /// </summary>
    internal override void RemoveGroupBackdrop()
    {
        if (GroupBackdrop is not InkTarget backdrop || GroupAlpha is not float[] g)
        {
            return;
        }

        int n = Area.Width, planes = Inks;
        float[] now = new float[n * (planes + 1)], first = new float[n * (planes + 1)];
        for (int y = Area.Y; y < Area.Bottom; y++)
        {
            ReadAll(y, Area.X, n, now);
            backdrop.ReadAll(y, Area.X, n, first);
            int at = (y - Area.Y) * n;
            for (int i = 0; i < n; i++)
            {
                float ag = g[at + i], an = now[(planes * n) + i], a0 = first[(planes * n) + i];
                float f = ag > 0 ? (a0 / ag) - a0 : 0;
                for (int p = 0; p < planes; p++)
                {
                    float cn = an > 0 ? 1 - (now[(p * n) + i] / an) : 1;
                    float c0 = a0 > 0 ? 1 - (first[(p * n) + i] / a0) : 1;
                    float c = Math.Clamp(cn + ((cn - c0) * f), 0, 1);
                    now[(p * n) + i] = ag > 0 ? ag * (1 - c) : 0;
                }

                now[(planes * n) + i] = Math.Max(ag, 0);
            }

            WriteAll(y, Area.X, n, now);
        }
    }

    internal override void CopyFrom(RasterTarget source, Rect area)
    {
        InkTarget from = (InkTarget)source;
        if (!ReferenceEquals(from.Resolver, Resolver))
        {
            CopyConverted(from, area);
            return;
        }

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

    /// <summary>
    /// Merges a layer with a blend mode, opacity and the clip: its plates as straight tints over these; a layer that blended in
    /// another space converts into these inks first, its spots as they are.
    /// </summary>
    internal override void Merge(RasterTarget layer, Rect area, ClipMask? mask, BlendMode mode, float opacity, ColorSpace colorSpace)
    {
        InkTarget from = (InkTarget)layer;
        bool convert = !ReferenceEquals(from.Resolver, Resolver) && from.Resolver.Space is not null;
        int n = area.Width, planes = Inks, theirs = from.Inks;
        float[] tints = new float[n * planes];
        float[] own = new float[theirs];
        float[] values = new float[n * (theirs + 1)];
        float[] alpha = new float[n];
        for (int y = area.Y; y < area.Bottom; y++)
        {
            ReadOnlySpan<byte> clip = mask is null ? default : mask.Row(y).Slice(area.X - mask.Bounds.X, n);
            from.ReadAll(y, area.X, n, values);
            for (int i = 0; i < n; i++)
            {
                float a = values[(theirs * n) + i];
                alpha[i] = a;
                Span<float> to = tints.AsSpan(i * planes, planes);
                Span<float> straight = convert ? own : to;
                for (int p = 0; p < theirs; p++)
                {
                    straight[p] = a > 0 ? Math.Clamp(values[(p * n) + i] / a, 0, 1) : 0;
                }

                if (convert)
                {
                    from.Resolver.ToCanvas(own, to);
                }
            }

            InkCompositor.Composite(this, y, area.X, n, tints, planes, alpha, clip, opacity, mode, default);
        }
    }

    // A non-isolated layer in another space starts as the backdrop converted into that space.
    private void CopyConverted(InkTarget from, Rect area)
    {
        int n = area.Width, theirs = from.Inks, planes = Inks;
        float[] values = new float[n * (theirs + 1)], result = new float[n * (planes + 1)];
        float[] straight = new float[theirs], mine = new float[planes];
        for (int y = area.Y; y < area.Bottom; y++)
        {
            from.ReadAll(y, area.X, n, values);
            for (int i = 0; i < n; i++)
            {
                float a = values[(theirs * n) + i];
                for (int p = 0; p < theirs; p++)
                {
                    straight[p] = a > 0 ? Math.Clamp(values[(p * n) + i] / a, 0, 1) : 0;
                }

                Resolver.FromCanvas(straight, mine);
                for (int p = 0; p < planes; p++)
                {
                    result[(p * n) + i] = mine[p] * a;
                }

                result[(planes * n) + i] = a;
            }

            WriteAll(y, area.X, n, result);
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

    /// <summary>
    /// Draws an image as plates: converted to the process inks through its profile (CMYK in the canvas's own profile, or untagged,
    /// unchanged), then sampled and blended like an ink image.
    /// </summary>
    internal override void DrawImage(DrawTools tools, Raster image, RectF source, RectF destination, Matrix3x2 matrix, SamplingOptions sampling, Paint? paint, Rect clip, ClipMask? mask)
    {
        using InkRaster plates = InkImages.ToPlates(image, Resolver, _raster.Depth, tools.Context);
        DrawInks(tools, plates, source, destination, matrix, sampling, paint, clip, mask);
    }

    /// <summary>Draws an ink image's plates over a destination, mapped by name.</summary>
    internal void DrawInks(DrawTools tools, InkRaster image, RectF source, RectF destination, Matrix3x2 matrix, SamplingOptions sampling, Paint? paint, Rect clip, ClipMask? mask)
    {
        Matrix3x2 imageToLocal = Matrix3x2.CreateTranslation(-source.X, -source.Y) * Matrix3x2.CreateScale(destination.Width / source.Width, destination.Height / source.Height)
            * Matrix3x2.CreateTranslation(destination.X, destination.Y);
        InkImagePaint fill = new(image, sampling)
        {
            LocalMatrix = imageToLocal,
            BlendMode = paint?.BlendMode ?? BlendMode.SourceOver,
            Opacity = paint?.Opacity ?? 1,
            Overprint = paint?.Overprint ?? Overprint.Off,
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

    /// <summary>Reads every plane's run, plane by plane (alpha last), as 0 to 1.</summary>
    internal void ReadAll(int y, int x, int n, Span<float> values)
    {
        for (int p = 0; p < _planes.Length; p++)
        {
            ReadPlane(p, y, x, values.Slice(p * n, n));
        }
    }

    /// <summary>Writes every plane's run from values laid out as <see cref="ReadAll"/> gives them.</summary>
    internal void WriteAll(int y, int x, int n, ReadOnlySpan<float> values)
    {
        for (int p = 0; p < _planes.Length; p++)
        {
            ReadOnlySpan<float> v = values.Slice(p * n, n);
            if (Wide)
            {
                Span<ushort> row = Row16(p, y, x, n);
                for (int i = 0; i < n; i++)
                {
                    row[i] = To16(v[i]);
                }
            }
            else
            {
                Span<byte> row = Row8(p, y, x, n);
                for (int i = 0; i < n; i++)
                {
                    row[i] = To8(v[i]);
                }
            }
        }
    }

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
}
