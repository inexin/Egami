using System.Numerics;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing;

public abstract partial class Canvas
{
    private static readonly SamplingOptions FallbackSampling = new() { Filter = ResampleFilter.Nearest };

    /// <summary>
    /// Draws an image stretched to a rectangle while its corners keep their size: the edges stretch one way and the center both
    /// ways, as Skia's drawImageNine. When the destination is smaller than the corners, they shrink in proportion. It is
    /// <see cref="DrawImageLattice"/> with <see cref="Lattice.NinePatch(Rect)"/>.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <param name="center">The part of the image that stretches both ways, in its pixels.</param>
    /// <param name="destination">Where the image goes, in local coordinates.</param>
    /// <param name="sampling">How the image is sampled; Egami's default sampling when <see langword="null"/>.</param>
    /// <param name="paint">The blend mode, opacity and antialiasing; its color is not used.</param>
    /// <exception cref="ArgumentException"><paramref name="center"/> is empty or lies outside the image, or <paramref name="destination"/> is not finite.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="lattice"/>
    /// </example>
    public void DrawImageNinePatch(Raster image, Rect center, RectF destination, SamplingOptions? sampling = null, Paint? paint = null)
        => DrawImageLattice(image, Lattice.NinePatch(center), destination, sampling, paint);

    /// <summary>
    /// Draws an image through a <see cref="Lattice"/>: fixed columns and rows keep their size, stretchable ones share the rest of
    /// the destination, and cells may be left out or filled with a color. Neighboring cells meet without seams: the raster backend
    /// draws the cells as one textured mesh, with antialiasing on its outer edge only.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <param name="lattice">The grid.</param>
    /// <param name="destination">Where the image goes, in local coordinates.</param>
    /// <param name="sampling">How the image is sampled; Egami's default sampling when <see langword="null"/>.</param>
    /// <param name="paint">The blend mode, opacity and antialiasing; its color is not used.</param>
    /// <exception cref="ArgumentException">The lattice does not fit the image, or <paramref name="destination"/> is not finite.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="lattice"/>
    /// </example>
    public void DrawImageLattice(Raster image, Lattice lattice, RectF destination, SamplingOptions? sampling = null, Paint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(lattice);
        CheckLive();
        CheckRect(destination, nameof(destination));
        lattice.Check(image, nameof(lattice));
        if (!destination.IsEmpty)
        {
            OnDrawLattice(image, lattice, destination, sampling ?? DefaultSampling, paint);
        }
    }

    /// <summary>
    /// Draws many sprites from one image in one call, as Skia's drawAtlas: sprite i is <paramref name="sources"/>[i] of the image,
    /// placed by <paramref name="transforms"/>[i], and, when there are colors, combined with <paramref name="colors"/>[i] by
    /// <paramref name="colorBlend"/>, the color as the destination and the sprite as the source. Each sprite blends with what lies
    /// below on its own, so overlapping particles add up. Samples near a sprite's edge may read the image just outside its source
    /// rectangle, as in Skia; leave a pixel of space between sprites when that matters.
    /// </summary>
    /// <param name="image">The sprite sheet.</param>
    /// <param name="transforms">Where each sprite goes.</param>
    /// <param name="sources">Each sprite's part of the image, in its pixels.</param>
    /// <param name="colors">One straight-alpha color per sprite, or empty for none.</param>
    /// <param name="colorBlend">How a sprite combines with its color.</param>
    /// <param name="sampling">How the image is sampled; Egami's default sampling when <see langword="null"/>.</param>
    /// <param name="cull">Bounds of all the sprites, in local coordinates, when the caller knows them: a call outside the clip is skipped at once.</param>
    /// <param name="paint">The blend mode, opacity and antialiasing; its color is not used.</param>
    /// <exception cref="ArgumentException">The lists' lengths differ, or a number is not finite.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="atlas"/>
    /// </example>
    public void DrawAtlas(
        Raster image,
        ReadOnlySpan<SpriteTransform> transforms,
        ReadOnlySpan<RectF> sources,
        ReadOnlySpan<RgbaF32> colors = default,
        BlendMode colorBlend = BlendMode.Modulate,
        SamplingOptions? sampling = null,
        RectF? cull = null,
        Paint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        CheckLive();
        if (sources.Length != transforms.Length)
        {
            throw new ArgumentException("There must be one source rectangle per transform.", nameof(sources));
        }

        if (!colors.IsEmpty && colors.Length != transforms.Length)
        {
            throw new ArgumentException("There must be one color per transform, or none.", nameof(colors));
        }

        CheckBlend(colorBlend, nameof(colorBlend));
        foreach (SpriteTransform t in transforms)
        {
            if (!t.IsFinite)
            {
                throw new ArgumentException("A transform must be finite.", nameof(transforms));
            }
        }

        foreach (RectF s in sources)
        {
            CheckRect(s, nameof(sources));
        }

        foreach (RgbaF32 c in colors)
        {
            CheckColor(c, nameof(colors));
        }

        if (cull is RectF bounds)
        {
            CheckRect(bounds, nameof(cull));
            if (QuickReject(bounds))
            {
                return;
            }
        }

        if (!transforms.IsEmpty)
        {
            OnDrawAtlas(image, transforms, sources, colors, colorBlend, sampling ?? DefaultSampling, paint);
        }
    }

    /// <summary>
    /// Draws a triangle mesh. The triangles' union is filled once, with antialiasing on its outer edges only, so meshes show no
    /// cracks between triangles; where triangles overlap, the later one's color shows. Each pixel's color comes from its
    /// triangle: the paint (mapped through the texture coordinates when the mesh has them), combined with the interpolated vertex
    /// color by <paramref name="colorBlend"/>, the vertex color as the destination and the paint as the source. A solid paint's color
    /// is not used when the mesh has colors or values, as in Skia: its alpha scales them. Vertex colors interpolate in the canvas's
    /// color space, premultiplied; values interpolate and then look up <paramref name="valueStops"/>.
    /// </summary>
    /// <param name="mesh">The mesh, in local coordinates.</param>
    /// <param name="paint">The paint, with the blend mode, opacity and antialiasing of the whole mesh.</param>
    /// <param name="colorBlend">How the vertex colors combine with the paint.</param>
    /// <param name="valueStops">The stops vertex values map through, as a gradient's, clamped to 0 to 1; needed when the mesh has values.</param>
    /// <exception cref="ArgumentException">The mesh has values and no stops are given.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="mesh"/>
    /// </example>
    public void DrawMesh(Mesh mesh, Paint paint, BlendMode colorBlend = BlendMode.Modulate, ReadOnlySpan<GradientStop> valueStops = default)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        Check(paint);
        CheckBlend(colorBlend, nameof(colorBlend));
        if (!mesh.Values.IsEmpty && valueStops.IsEmpty)
        {
            throw new ArgumentException("A mesh with values needs the stops they map through.", nameof(valueStops));
        }

        if (mesh.TriangleCount > 0)
        {
            OnDrawMesh(mesh, paint, colorBlend, valueStops);
        }
    }

    /// <summary>
    /// Draws a Coons or tensor-product patch: the surface is cut into triangles under the current transform until each strays less
    /// than a quarter of a device pixel from it, and drawn as a mesh, so <see cref="DrawMesh"/>'s rules apply. Where the patch folds
    /// over itself, larger v covers smaller v, and for equal v larger u covers smaller u, as PDF requires.
    /// </summary>
    /// <param name="patch">The patch, in local coordinates.</param>
    /// <param name="paint">The paint; an opaque white solid paint when <see langword="null"/>, which draws the corner colors as they are.</param>
    /// <param name="colorBlend">How the corner colors combine with the paint.</param>
    /// <param name="valueStops">The stops corner values map through; needed when the patch has values.</param>
    /// <exception cref="ArgumentException">The patch has values and no stops are given.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="patch"/>
    /// </example>
    public void DrawPatch(Patch patch, Paint? paint = null, BlendMode colorBlend = BlendMode.Modulate, ReadOnlySpan<GradientStop> valueStops = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        CheckLive();
        CheckBlend(colorBlend, nameof(colorBlend));
        if (!patch.CornerValues.IsEmpty && valueStops.IsEmpty)
        {
            throw new ArgumentException("A patch with values needs the stops they map through.", nameof(valueStops));
        }

        OnDrawPatch(patch, paint ?? WhitePaint, colorBlend, valueStops);
    }

    /// <summary>
    /// Replays a recorded <see cref="Drawing"/>: inside a <see cref="Save"/> and <see cref="Restore"/>, with <paramref name="matrix"/>
    /// added to the transform, so it stays sharp at any scale. A paint with an opacity below 1 or a blend mode other than
    /// source-over merges the drawing as one layer bounded by its <see cref="Drawing.Bounds"/>, as Skia's drawPicture does.
    /// </summary>
    /// <param name="drawing">The drawing.</param>
    /// <param name="matrix">A transform applied before the canvas's, or <see langword="null"/> for none.</param>
    /// <param name="paint">The opacity and blend mode of the whole drawing, or <see langword="null"/> to replay it directly.</param>
    /// <param name="cacheAsImage">
    /// Whether to draw the drawing as an image rendered at the current scale and kept with the drawing, for a drawing repeated
    /// many times at one scale (a map's symbols, a game's sprites). The image is resampled under rotation and skew.
    /// </param>
    /// <exception cref="ArgumentException">An element of <paramref name="matrix"/> is not finite.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="recording"/>
    /// </example>
    public void DrawDrawing(Drawing drawing, Matrix3x2? matrix = null, Paint? paint = null, bool cacheAsImage = false)
    {
        ArgumentNullException.ThrowIfNull(drawing);
        CheckLive();
        Matrix3x2 m = matrix is Matrix3x2 given ? FiniteMatrix(given, nameof(matrix)) : Matrix3x2.Identity;
        if (drawing.CallCount > 0)
        {
            OnDrawDrawing(drawing, m, paint, cacheAsImage);
        }
    }

    /// <summary>
    /// Draws an image through a lattice; the lattice fits the image. By default each cell is an <see cref="OnDrawImage"/> call of
    /// its part of the image, sampled only inside that part, and each fixed-color cell an <see cref="OnDrawRect"/> call; a PDF
    /// backend gets one image per cell this way. The raster backend draws the cells as one mesh instead, so they meet without seams.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <param name="lattice">The grid.</param>
    /// <param name="destination">Where the image goes, in local coordinates; not empty.</param>
    /// <param name="sampling">How the image is sampled.</param>
    /// <param name="paint">The blend mode, opacity and antialiasing, or <see langword="null"/>.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="lattice"/>
    /// </example>
    protected virtual void OnDrawLattice(Raster image, Lattice lattice, RectF destination, SamplingOptions sampling, Paint? paint)
    {
        foreach (LatticeCell cell in LatticeCells(image, lattice, destination))
        {
            if (cell.Kind == LatticeCellKind.FixedColor)
            {
                OnDrawRect(cell.Destination, null, FixedColorPaint(cell.Color, paint));
            }
            else
            {
                OnDrawImage(image, cell.Source, cell.Destination, sampling, paint);
            }
        }
    }

    /// <summary>
    /// Draws sprites from one image; the lists have equal lengths and colors is empty or as long. By default a sprite without a
    /// color is a <see cref="DrawImage(Raster, RectF, RectF, SamplingOptions?, Paint?)"/> under its transform, and a tinted one a
    /// two-triangle textured <see cref="OnDrawMesh"/> call, one sprite at a time.
    /// </summary>
    /// <param name="image">The sprite sheet.</param>
    /// <param name="transforms">Where each sprite goes.</param>
    /// <param name="sources">Each sprite's part of the image.</param>
    /// <param name="colors">One color per sprite, or empty.</param>
    /// <param name="colorBlend">How a sprite combines with its color.</param>
    /// <param name="sampling">How the image is sampled.</param>
    /// <param name="paint">The blend mode, opacity and antialiasing, or <see langword="null"/>.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="atlas"/>
    /// </example>
    protected virtual void OnDrawAtlas(Raster image, ReadOnlySpan<SpriteTransform> transforms, ReadOnlySpan<RectF> sources, ReadOnlySpan<RgbaF32> colors, BlendMode colorBlend, SamplingOptions sampling, Paint? paint)
    {
        for (int i = 0; i < transforms.Length; i++)
        {
            RectF source = sources[i];
            if (colors.IsEmpty)
            {
                int count = Save();
                Concat(transforms[i].ToMatrix());
                DrawImage(image, source, new RectF(0, 0, source.Width, source.Height), sampling, paint);
                RestoreToCount(count);
            }
            else if (!source.IsEmpty && SpriteMesh(transforms[i], source, colors[i]) is Mesh sprite)
            {
                OnDrawMesh(sprite, SpritePaint(image, sampling, paint), colorBlend, default);
            }
        }
    }

    /// <summary>
    /// Draws a mesh with at least one triangle; a mesh with values comes with stops. A PDF backend can write it as a free-form or
    /// lattice-form shading (types 4 and 5). By default it is drawn through <see cref="RasterizeFallback"/> at one raster pixel per
    /// device unit, over the mesh's bounds, and placed with <see cref="OnDrawImage"/>.
    /// </summary>
    /// <param name="mesh">The mesh, in local coordinates.</param>
    /// <param name="paint">The paint.</param>
    /// <param name="colorBlend">How the vertex colors combine with the paint.</param>
    /// <param name="valueStops">The stops vertex values map through, or empty.</param>
    protected virtual void OnDrawMesh(Mesh mesh, Paint paint, BlendMode colorBlend, ReadOnlySpan<GradientStop> valueStops)
    {
        GradientStop[] stops = valueStops.ToArray();
        Paint flat = paint with { BlendMode = BlendMode.SourceOver, Opacity = 1 };
        int count = Save();
        try
        {
            ClipRect(mesh.Bounds.Inflate(1, 1));
            FallbackImage? image = RasterizeFallback(c => c.DrawMesh(mesh, flat, colorBlend, stops));
            if (image is not null)
            {
                ResetMatrix();
                DrawImage(image.Image, image.DeviceBounds, FallbackSampling, new SolidPaint(new RgbaF32(1, 1, 1, 1)) { BlendMode = paint.BlendMode, Opacity = paint.Opacity });
            }
        }
        finally
        {
            RestoreToCount(count);
        }
    }

    /// <summary>
    /// Draws a patch. A PDF backend can write it as a Coons or tensor-product shading (types 6 and 7). By default it is cut into
    /// triangles under the current transform and drawn with <see cref="OnDrawMesh"/>.
    /// </summary>
    /// <param name="patch">The patch, in local coordinates.</param>
    /// <param name="paint">The paint.</param>
    /// <param name="colorBlend">How the corner colors combine with the paint.</param>
    /// <param name="valueStops">The stops corner values map through, or empty.</param>
    protected virtual void OnDrawPatch(Patch patch, Paint paint, BlendMode colorBlend, ReadOnlySpan<GradientStop> valueStops)
    {
        if (patch.Tessellate(Matrix) is Mesh mesh)
        {
            OnDrawMesh(mesh, paint, colorBlend, valueStops);
        }
    }

    /// <summary>
    /// Draws a recorded drawing. A PDF backend can write it once as a form XObject and reference it each time. By default it saves
    /// the state, adds the matrix, starts a layer when the paint asks for one, replays the drawing through the public calls (or
    /// draws its cached image) and restores.
    /// </summary>
    /// <param name="drawing">The drawing, with at least one call.</param>
    /// <param name="matrix">The transform added before the canvas's.</param>
    /// <param name="paint">The opacity and blend mode of the whole drawing, or <see langword="null"/>.</param>
    /// <param name="cacheAsImage">Whether to draw the drawing's cached image instead of its calls.</param>
    protected virtual void OnDrawDrawing(Drawing drawing, Matrix3x2 matrix, Paint? paint, bool cacheAsImage)
    {
        int count = Save();
        try
        {
            SetMatrix(Compose(matrix, Matrix));
            if (paint is not null && (paint.Opacity < 1 || paint.BlendMode != BlendMode.SourceOver))
            {
                SaveLayer(new LayerOptions { Opacity = paint.Opacity, BlendMode = paint.BlendMode, Bounds = drawing.Bounds });
            }

            float scale = MathF.Sqrt(MathF.Abs(Matrix.GetDeterminant()));
            if (cacheAsImage && scale > 0 && float.IsFinite(scale))
            {
                (Raster<RgbaF32> image, RectF destination) = drawing.CachedImage(scale);
                DrawImage(image, destination, CacheSampling);
            }
            else
            {
                drawing.Playback(this);
            }
        }
        finally
        {
            RestoreToCount(count);
        }
    }

    private static readonly SolidPaint WhitePaint = new(new RgbaF32(1, 1, 1, 1));

    /// <summary>
    /// <paramref name="first"/> then <paramref name="then"/>, or, when the product overflows, the zero matrix, under which nothing
    /// draws: a replayed drawing's transforms are data that may not compose with every canvas's.
    /// </summary>
    internal static Matrix3x2 Compose(Matrix3x2 first, Matrix3x2 then)
    {
        Matrix3x2 m = first * then;
        return float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M31) && float.IsFinite(m.M32)
            ? m
            : new Matrix3x2(0, 0, 0, 0, 0, 0);
    }

    private static readonly SamplingOptions CacheSampling = new() { Filter = ResampleFilter.Bilinear };

    /// <summary>The image paint a sprite or a lattice cell is drawn with: clamped at the image's edges, with the call's blend mode, opacity and antialiasing.</summary>
    private protected static ImagePaint SpritePaint(Raster image, SamplingOptions sampling, Paint? paint)
        => new(image, TileMode.Clamp, TileMode.Clamp, sampling)
        {
            BlendMode = paint?.BlendMode ?? BlendMode.SourceOver,
            Opacity = paint?.Opacity ?? 1,
            Antialias = paint?.Antialias ?? true,
        };

    /// <summary>A sprite as two textured triangles, each vertex with the sprite's color, or none.</summary>
    private protected static Mesh? SpriteMesh(SpriteTransform transform, RectF source, RgbaF32? color)
    {
        Matrix3x2 m = transform.ToMatrix();
        float w = source.Width, h = source.Height;
        Vector2[] positions = [Vector2.Transform(Vector2.Zero, m), Vector2.Transform(new Vector2(w, 0), m), Vector2.Transform(new Vector2(w, h), m), Vector2.Transform(new Vector2(0, h), m)];
        if (!positions.All(p => float.IsFinite(p.X) && float.IsFinite(p.Y)))
        {
            return null;
        }

        Vector2[] tex = [new(source.Left, source.Top), new(source.Right, source.Top), new(source.Right, source.Bottom), new(source.Left, source.Bottom)];
        RgbaF32[] colors = color is RgbaF32 c ? [c, c, c, c] : [];
        return new Mesh(MeshMode.Triangles, positions, colors, tex, indices: [0, 1, 2, 0, 2, 3]);
    }

    /// <summary>
    /// The cells a lattice draws, row by row: the image cells with a source and a destination that are not empty, and the
    /// fixed-color cells with a destination that is not empty. Nothing when the layout overflows.
    /// </summary>
    private protected static List<LatticeCell> LatticeCells(Raster image, Lattice lattice, RectF destination)
    {
        Rect bounds = lattice.Bounds ?? new Rect(0, 0, image.Width, image.Height);
        int[] xDivs = [.. lattice.XDivs], yDivs = [.. lattice.YDivs];
        (float[] sx, float[] dx) = Lattice.Layout(xDivs, bounds.X, bounds.Right, destination.Left, destination.Right);
        (float[] sy, float[] dy) = Lattice.Layout(yDivs, bounds.Y, bounds.Bottom, destination.Top, destination.Bottom);
        List<LatticeCell> cells = [];
        if (!dx.All(float.IsFinite) || !dy.All(float.IsFinite))
        {
            return cells;
        }

        for (int row = 0; row + 1 < sy.Length; row++)
        {
            for (int column = 0; column + 1 < sx.Length; column++)
            {
                RectF cell = RectF.FromEdges(dx[column], dy[row], dx[column + 1], dy[row + 1]);
                RectF source = RectF.FromEdges(sx[column], sy[row], sx[column + 1], sy[row + 1]);
                LatticeCellKind kind = lattice.Kind(column, row);
                if (cell.IsEmpty || kind == LatticeCellKind.Transparent || (kind == LatticeCellKind.Default && source.IsEmpty))
                {
                    continue;
                }

                cells.Add(new LatticeCell(kind, source, cell, lattice.Color(column, row)));
            }
        }

        return cells;
    }

    private protected static SolidPaint FixedColorPaint(RgbaF32 color, Paint? paint)
        => new(color) { BlendMode = paint?.BlendMode ?? BlendMode.SourceOver, Opacity = paint?.Opacity ?? 1, Antialias = paint?.Antialias ?? true };

    private protected static void AddQuad(List<Vector2> positions, List<Vector2> tex, List<int> indices, RectF cell, RectF source)
    {
        int b = positions.Count;
        positions.Add(new Vector2(cell.Left, cell.Top));
        positions.Add(new Vector2(cell.Right, cell.Top));
        positions.Add(new Vector2(cell.Right, cell.Bottom));
        positions.Add(new Vector2(cell.Left, cell.Bottom));
        tex.Add(new Vector2(source.Left, source.Top));
        tex.Add(new Vector2(source.Right, source.Top));
        tex.Add(new Vector2(source.Right, source.Bottom));
        tex.Add(new Vector2(source.Left, source.Bottom));
        indices.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
    }

    /// <summary>One cell of a laid-out lattice: what it draws, its part of the image and where it goes.</summary>
    private protected readonly record struct LatticeCell(LatticeCellKind Kind, RectF Source, RectF Destination, RgbaF32 Color);

    private static void CheckBlend(BlendMode mode, string name)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(name, mode, null);
        }
    }

    private static void CheckColor(RgbaF32 c, string name)
    {
        if (!(float.IsFinite(c.R) && float.IsFinite(c.G) && float.IsFinite(c.B) && float.IsFinite(c.A)))
        {
            throw new ArgumentException("A color must be finite.", name);
        }
    }
}
