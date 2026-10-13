using System.Numerics;

using Egami.Drawing;
using Egami.Operations;

namespace Egami.Svg;

/// <summary>
/// A node of a document's render tree: the elements resolved for drawing, with styles applied, units in pixels, <c>use</c>
/// expanded, references linked and shapes turned into paths, as resvg's usvg tree. It is immutable, so a document that is not
/// being edited can be drawn on many threads at once.
/// </summary>
/// <remarks>
/// <see cref="SvgDocument.RenderTree"/> builds the tree on first use after a load or an edit and caches it. Drawing walks it and
/// calls only the public canvas API, so a raster canvas, a PDF page, <c>SvgCanvas</c> and a <see cref="PictureRecorder"/> receive
/// the same calls. A later 1.x release may add node kinds, so a <c>switch</c> over nodes should keep a default case.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="render-tree"/>
/// </example>
public abstract record class SvgNode
{
    private protected SvgNode()
    {
    }

    /// <summary>Gets the element this node was made from, or <see langword="null"/> for a node with no element of its own.</summary>
    public SvgElement? Element { get; init; }

    /// <summary>Gets the element's <c>id</c>, or <see langword="null"/>.</summary>
    public string? Id { get; init; }

    /// <summary>Gets the element's classes, from its <c>class</c> attribute.</summary>
    public IReadOnlyList<string> Classes { get; init; } = [];

    /// <summary>Gets the transform from this node's coordinates to its parent's.</summary>
    public Matrix3x2 Transform { get; init; } = Matrix3x2.Identity;

    /// <summary>Gets the opacity the node is drawn with as a whole, 0 to 1.</summary>
    public float Opacity { get; init; } = 1;

    /// <summary>Gets how the node blends with what lies below it (<c>mix-blend-mode</c>).</summary>
    public BlendMode BlendMode { get; init; } = BlendMode.SourceOver;

    /// <summary>Gets a value indicating whether the node starts its own transparency group (<c>isolation: isolate</c>).</summary>
    public bool Isolated { get; init; }

    /// <summary>Gets the clip path, in this node's coordinates, or <see langword="null"/>.</summary>
    public SvgClip? Clip { get; init; }

    /// <summary>Gets the mask, in this node's coordinates, or <see langword="null"/>.</summary>
    public SvgMask? Mask { get; init; }

    /// <summary>Gets the filter, in this node's coordinates, or <see langword="null"/>.</summary>
    public SvgFilter? Filter { get; init; }

    /// <summary>
    /// Gets the node's object bounding box in its own coordinates (before <see cref="Transform"/>): the geometry without strokes,
    /// markers or filters, as SVG's objectBoundingBox units use it. Empty for a node that draws nothing.
    /// </summary>
    public RectF Bounds { get; init; }

    /// <summary>Gets a value indicating whether drawing the node needs a layer: opacity, a blend mode, isolation or a mask.</summary>
    internal bool NeedsLayer => Opacity < 1 || BlendMode != BlendMode.SourceOver || Isolated || Mask is not null;
}

/// <summary>A group of nodes drawn in order: a <c>g</c>, <c>svg</c>, <c>a</c>, <c>switch</c>, a <c>use</c> instance or a marker.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="render-tree"/>
/// </example>
public sealed record class SvgGroup : SvgNode
{
    /// <summary>Gets the children, in drawing order.</summary>
    public IReadOnlyList<SvgNode> Children { get; init; } = [];

    /// <summary>Gets a rectangle the children are clipped to, in this group's coordinates (a nested <c>svg</c>'s viewport), or <see langword="null"/>.</summary>
    public RectF? ClipRect { get; init; }
}

/// <summary>Which of a shape's fill, stroke and markers is painted first (<c>paint-order</c>).</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="render-tree"/>
/// </example>
public enum SvgPaintOrder
{
    /// <summary>Fill, then stroke, then markers: the default.</summary>
    FillStrokeMarkers,

    /// <summary>Fill, markers, stroke.</summary>
    FillMarkersStroke,

    /// <summary>Stroke, fill, markers.</summary>
    StrokeFillMarkers,

    /// <summary>Stroke, markers, fill.</summary>
    StrokeMarkersFill,

    /// <summary>Markers, fill, stroke.</summary>
    MarkersFillStroke,

    /// <summary>Markers, stroke, fill.</summary>
    MarkersStrokeFill,
}

/// <summary>A shape: a 2.6 <see cref="Drawing.Path"/> with its fill, stroke and markers.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="render-tree"/>
/// </example>
public sealed record class SvgPath : SvgNode
{
    /// <summary>Initializes a new instance of the <see cref="SvgPath"/> class.</summary>
    /// <param name="path">The geometry, in this node's coordinates, with its fill rule.</param>
    public SvgPath(Path path)
    {
        ArgumentNullException.ThrowIfNull(path);
        Path = path;
    }

    /// <summary>Gets the geometry, in this node's coordinates; its <see cref="Drawing.Path.FillRule"/> is the shape's fill-rule.</summary>
    public Path Path { get; init; }

    /// <summary>Gets the fill, with fill-opacity applied, or <see langword="null"/> for none.</summary>
    public Paint? Fill { get; init; }

    /// <summary>Gets the stroke paint, with stroke-opacity applied, or <see langword="null"/> for none.</summary>
    public Paint? StrokePaint { get; init; }

    /// <summary>Gets the stroke geometry (width, caps, joins, dashes), or <see langword="null"/> when there is no stroke.</summary>
    public Stroke? Stroke { get; init; }

    /// <summary>Gets a value indicating whether the stroke is drawn in device pixels (<c>vector-effect: non-scaling-stroke</c>).</summary>
    public bool NonScalingStroke { get; init; }

    /// <summary>Gets the markers placed on the shape, or <see langword="null"/>.</summary>
    public SvgGroup? Markers { get; init; }

    /// <summary>Gets the order the fill, stroke and markers are painted in.</summary>
    public SvgPaintOrder PaintOrder { get; init; }

    /// <summary>Gets a value indicating whether edges are antialiased (<c>shape-rendering</c> other than crispEdges).</summary>
    public bool Antialias { get; init; } = true;
}

/// <summary>
/// Text: one run of positioned glyphs in one font with one fill and stroke, drawn with <see cref="Canvas.DrawGlyphRun"/> so a
/// PDF or SVG canvas keeps real text. A text element becomes a group of these and of the paths of its decorations.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="render-tree"/>
/// </example>
public sealed record class SvgTextRun : SvgNode
{
    /// <summary>Initializes a new instance of the <see cref="SvgTextRun"/> class.</summary>
    /// <param name="run">The glyphs, positioned in this node's coordinates.</param>
    public SvgTextRun(Fonts.GlyphRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        Run = run;
    }

    /// <summary>Gets the glyphs, positioned (and turned, for rotate or textPath) in this node's coordinates.</summary>
    public Fonts.GlyphRun Run { get; init; }

    /// <summary>Gets the fill, with fill-opacity applied, or <see langword="null"/> for none.</summary>
    public Paint? Fill { get; init; }

    /// <summary>Gets the stroke paint, with stroke-opacity applied, or <see langword="null"/> for none.</summary>
    public Paint? StrokePaint { get; init; }

    /// <summary>Gets the stroke geometry, or <see langword="null"/> when there is no stroke.</summary>
    public Stroke? Stroke { get; init; }

    /// <summary>Gets the order the fill and stroke are painted in.</summary>
    public SvgPaintOrder PaintOrder { get; init; }

    /// <summary>Gets a value indicating whether glyph edges are antialiased (<c>text-rendering</c> other than optimizeSpeed).</summary>
    public bool Antialias { get; init; } = true;
}

/// <summary>An image: a raster drawn into a rectangle, or another SVG document drawn as a group.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="render-tree"/>
/// </example>
public sealed record class SvgImage : SvgNode
{
    /// <summary>Gets the raster, still encoded, or <see langword="null"/> for an SVG image.</summary>
    public EncodedImage? Raster { get; init; }

    /// <summary>Gets the SVG document, or <see langword="null"/> for a raster image.</summary>
    public SvgDocument? Document { get; init; }

    /// <summary>Gets where the image's view is drawn, after preserveAspectRatio, in this node's coordinates.</summary>
    public RectF Destination { get; init; }

    /// <summary>Gets the part of the image drawn into <see cref="Destination"/>, in the image's pixels (or the document's user units).</summary>
    public RectF Source { get; init; }

    /// <summary>Gets a rectangle the image is clipped to (the image element's viewport), or <see langword="null"/>.</summary>
    public RectF? ClipRect { get; init; }

    /// <summary>Gets how the raster is sampled (<c>image-rendering</c>).</summary>
    public SamplingOptions? Sampling { get; init; }
}

/// <summary>A clip path, in the coordinates of the node it clips: the union of its shapes, each with its own clip-rule.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="render-tree"/>
/// </example>
public sealed record class SvgClip
{
    /// <summary>Initializes a new instance of the <see cref="SvgClip"/> class.</summary>
    /// <param name="path">The clip geometry: the union of the clip path's shapes, in the clipped node's coordinates.</param>
    public SvgClip(Path path)
    {
        ArgumentNullException.ThrowIfNull(path);
        Path = path;
    }

    /// <summary>Gets the clip geometry, in the clipped node's coordinates.</summary>
    public Path Path { get; init; }

    /// <summary>Gets the clip path's own content when it cannot be one path (text, or shapes clipped on their own), drawn as an alpha mask; or <see langword="null"/>.</summary>
    public SvgGroup? Content { get; init; }
}

/// <summary>A mask, in the coordinates of the node it masks: content drawn into a region, used by luminance or alpha.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="render-tree"/>
/// </example>
public sealed record class SvgMask
{
    /// <summary>Initializes a new instance of the <see cref="SvgMask"/> class.</summary>
    /// <param name="content">The mask's content.</param>
    public SvgMask(SvgGroup content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Content = content;
    }

    /// <summary>Gets the mask's content, in the masked node's coordinates.</summary>
    public SvgGroup Content { get; init; }

    /// <summary>Gets the mask region outside which the mask is zero, in the masked node's coordinates.</summary>
    public RectF Region { get; init; }

    /// <summary>Gets whether the mask uses its content's luminance (the default) or alpha (<c>mask-type</c>).</summary>
    public SoftMaskKind Kind { get; init; } = SoftMaskKind.Luminosity;

    /// <summary>Gets a mask on the mask itself, or <see langword="null"/>.</summary>
    public SvgMask? Mask { get; init; }
}

/// <summary>
/// A filter, in the coordinates of the node it filters: the filter elements and CSS filter functions of its filter property, run
/// one after the other.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="render-tree"/>
/// </example>
public sealed record class SvgFilter
{
    /// <summary>Gets the union of the filters' regions, in the filtered node's coordinates: nothing outside it is drawn.</summary>
    public RectF Region { get; init; }

    /// <summary>Gets the filters, in order, each with its region and primitives.</summary>
    internal IReadOnlyList<Filters.SvgFilterGraph> Graphs { get; init; } = [];
}
