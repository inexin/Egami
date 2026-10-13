using System.Buffers;
using System.Numerics;

using Egami.Drawing.Rasterization;
using Egami.Drawing.Shading;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing;

public sealed partial class RasterCanvas<TPixel>
{
    /// <inheritdoc/>
    /// <remarks>
    /// The triangles' union is filled once through the exact-area rasterizer, so only its outer edges are antialiased and shared
    /// edges leave no cracks; each pixel takes its color from the triangle that owns it.
    /// </remarks>
    protected override void OnDrawMesh(Mesh mesh, Paint paint, BlendMode colorBlend, ReadOnlySpan<GradientStop> valueStops)
    {
        ImageShader? images = null;
        DrawMesh(mesh, paint, colorBlend, valueStops, ref images);
    }

    /// <inheritdoc/>
    /// <remarks>Each sprite is two textured triangles filled on its own; the sheet's texels are prepared once for the whole call.</remarks>
    protected override void OnDrawAtlas(Raster image, ReadOnlySpan<SpriteTransform> transforms, ReadOnlySpan<RectF> sources, ReadOnlySpan<RgbaF32> colors, BlendMode colorBlend, SamplingOptions sampling, Paint? paint)
    {
        ImagePaint sprite = SpritePaint(image, sampling, paint);
        ImageShader? images = null;
        Raster? typed = null;
        bool copies = colors.IsEmpty && ImageShader.IsPixelCopy(Matrix3x2.Identity, sampling);
        try
        {
            for (int i = 0; i < transforms.Length; i++)
            {
                RectF source = sources[i];
                if (source.IsEmpty || _clip.Bounds.IsEmpty)
                {
                    continue;
                }

                // A sprite only moved by whole pixels, untinted, is its own pixels blended in place, as DrawImage does.
                Matrix3x2 m = transforms[i].ToMatrix() * Matrix;
                if (copies && m.M11 == 1 && m.M22 == 1 && m.M12 == 0 && m.M21 == 0 && m.M31 == MathF.Round(m.M31) && m.M32 == MathF.Round(m.M32)
                    && source.X == MathF.Round(source.X) && source.Y == MathF.Round(source.Y) && source.Width == MathF.Round(source.Width) && source.Height == MathF.Round(source.Height))
                {
                    typed ??= image as Raster<TPixel> ?? image.As<TPixel>();
                    RectF clipped = source.Intersect(new RectF(0, 0, image.Width, image.Height));
                    if (!clipped.IsEmpty)
                    {
                        _target.DrawImage(_tools, typed, clipped, new RectF(clipped.X - source.X, clipped.Y - source.Y, clipped.Width, clipped.Height), m, sampling, paint, _clip.Bounds, _clip.Mask);
                    }

                    continue;
                }

                if (SpriteMesh(transforms[i], source, colors.IsEmpty ? null : colors[i]) is Mesh mesh)
                {
                    DrawMesh(mesh, sprite, colorBlend, default, ref images);
                }
            }
        }
        finally
        {
            if (typed is not null && !ReferenceEquals(typed, image))
            {
                typed.Dispose();
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The cells are one mesh, each cell's triangles textured from a copy of its part of the image, so samples stay inside the cell
    /// as in Skia and neighboring cells still meet without seams; fixed-color cells are part of the same mesh.
    /// </remarks>
    protected override void OnDrawLattice(Raster image, Lattice lattice, RectF destination, SamplingOptions sampling, Paint? paint)
    {
        Rect bounds = lattice.Bounds ?? new Rect(0, 0, image.Width, image.Height);
        if (lattice.CellKinds is null && destination.Width == bounds.Width && destination.Height == bounds.Height)
        {
            OnDrawImage(image, bounds, destination, sampling, paint);
            return;
        }

        List<Vector2> positions = [], tex = [];
        List<int> indices = [];
        List<Paint> paints = [];
        List<Raster> crops = [];
        try
        {
            foreach (LatticeCell cell in LatticeCells(image, lattice, destination))
            {
                Paint cellPaint;
                RectF source;
                if (cell.Kind == LatticeCellKind.FixedColor)
                {
                    cellPaint = FixedColorPaint(cell.Color, paint);
                    source = new RectF(0, 0, 1, 1);
                }
                else
                {
                    Rect part = cell.Source.RoundOut();
                    Raster crop = image.Derive(ops => ops.Crop(part));
                    crops.Add(crop);
                    cellPaint = SpritePaint(crop, sampling, paint);
                    source = new RectF(0, 0, part.Width, part.Height);
                }

                AddQuad(positions, tex, indices, cell.Destination, source);
                paints.Add(cellPaint);
                paints.Add(cellPaint);
            }

            if (indices.Count > 0)
            {
                ImageShader? images = null;
                Paint first = paints[0] with { BlendMode = paint?.BlendMode ?? BlendMode.SourceOver, Opacity = paint?.Opacity ?? 1, Antialias = paint?.Antialias ?? true };
                DrawMesh(new Mesh(MeshMode.Triangles, [.. positions], texCoords: [.. tex], indices: [.. indices]), first, BlendMode.Modulate, default, ref images, [.. paints]);
            }
        }
        finally
        {
            foreach (Raster crop in crops)
            {
                crop.Dispose();
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>The rectangle's coverage is multiplied by the mask's, sampled at each pixel, and the paint fills through it: no layer.</remarks>
    protected override void OnDrawImageMask(Raster mask, RectF destination, SamplingOptions sampling, Paint paint)
    {
        if (_clip.Bounds.IsEmpty)
        {
            return;
        }

        using Raster<Rgba8> coverage = MaskCoverage(mask);
        Matrix3x2 m = Matrix;
        ImagePaint maskPaint = new(coverage, TileMode.Decal, TileMode.Decal, sampling)
        {
            LocalMatrix = Matrix3x2.CreateScale(destination.Width / mask.Width, destination.Height / mask.Height) * Matrix3x2.CreateTranslation(destination.X, destination.Y),
        };
        if (maskPaint.CreateShader(m, ColorSpace.Encoded) is not Shader shader
            || _target.CreateSink(paint with { Blur = null }, m, Options.ColorSpace, _clip.Mask, 255) is not ISpanSink sink)
        {
            return;
        }

        _edges.Clear();
        Flattener.Flatten(Path.Rect(destination), m, Options.Tolerance, _edges);
        _rasterizer.Fill(_edges, _clip.Bounds, FillRule.NonZero, paint.Antialias, new MaskedSink(sink, shader), _target.BytesPerPixel);
    }

    private void DrawMesh(Mesh mesh, Paint paint, BlendMode colorBlend, ReadOnlySpan<GradientStop> valueStops, ref ImageShader? images, Paint[]? trianglePaints = null)
    {
        if (_clip.Bounds.IsEmpty)
        {
            return;
        }

        Matrix3x2 m = Matrix;
        ReadOnlySpan<Vector2> positions = mesh.Positions.Span;
        Vector2[] device = ArrayPool<Vector2>.Shared.Rent(positions.Length);
        int[] kept = ArrayPool<int>.Shared.Rent(mesh.TriangleCount * 3);
        Paint[]? keptPaints = trianglePaints is null ? null : new Paint[mesh.TriangleCount];
        try
        {
            for (int i = 0; i < positions.Length; i++)
            {
                device[i] = Vector2.Transform(positions[i], m);
            }

            // The union of the triangles, each turned the same way, so nonzero winding covers each pixel once.
            _edges.Clear();
            int count = 0;
            for (int t = 0; t < mesh.TriangleCount; t++)
            {
                (int a, int b, int c) = mesh.Triangle(t);
                Vector2 p0 = device[a], p1 = device[b], p2 = device[c];
                float area = ((p1.X - p0.X) * (p2.Y - p0.Y)) - ((p1.Y - p0.Y) * (p2.X - p0.X));
                if (!(MathF.Abs(area) > 0) || !float.IsFinite(area))
                {
                    continue;
                }

                if (area < 0)
                {
                    (p1, p2) = (p2, p1);
                }

                _edges.Add(p0, p1);
                _edges.Add(p1, p2);
                _edges.Add(p2, p0);
                if (keptPaints is not null)
                {
                    keptPaints[count / 3] = trianglePaints![t];
                }

                kept[count++] = a;
                kept[count++] = b;
                kept[count++] = c;
            }

            if (count == 0)
            {
                return;
            }

            RectF reach = _edges.Bounds.Inflate(1, 1);
            Rect area2 = _clip.Bounds.Intersect(reach.Intersect(new RectF(-1e8f, -1e8f, 2e8f, 2e8f)).RoundOut());
            if (area2.IsEmpty)
            {
                return;
            }

            using MeshShader shader = MeshShader.Create(mesh, kept.AsSpan(0, count), device.AsSpan(0, positions.Length), area2, paint, colorBlend, valueStops, m, Options.ColorSpace, ref images, keptPaints);
            MeshShadingPaint shading = new(shader) { BlendMode = paint.BlendMode, Opacity = paint.Opacity, Antialias = paint.Antialias, Dither = paint.Dither };
            if (_target.CreateSink(shading, m, Options.ColorSpace, _clip.Mask, 255) is ISpanSink sink)
            {
                _rasterizer.Fill(_edges, area2, FillRule.NonZero, paint.Antialias, sink, _target.BytesPerPixel);
            }
        }
        finally
        {
            ArrayPool<Vector2>.Shared.Return(device);
            ArrayPool<int>.Shared.Return(kept);
        }
    }
}
