using System.Numerics;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing;

/// <summary>What an <see cref="Annotation"/> marks, as Skia's annotations do.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="annotations"/>
/// </example>
public enum AnnotationKind
{
    /// <summary>The area links to a URL.</summary>
    Url = 0,

    /// <summary>The area's top-left corner is a named destination that links can go to.</summary>
    NamedDestination = 1,

    /// <summary>The area links to a named destination in the same document.</summary>
    LinkToDestination = 2,
}

/// <summary>A link or a destination for <see cref="Canvas.DrawAnnotation"/>: what a document backend writes, and pixels never show.</summary>
/// <param name="Kind">What the annotation marks.</param>
/// <param name="Value">The URL, or the destination's name.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="annotations"/>
/// </example>
public sealed record class Annotation(AnnotationKind Kind, string Value)
{
    private readonly AnnotationKind _kind = Enum.IsDefined(Kind) ? Kind : throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null);
    private readonly string _value = CheckValue(Value);

    /// <summary>Gets what the annotation marks.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public AnnotationKind Kind
    {
        get => _kind;
        init => _kind = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, null);
    }

    /// <summary>Gets the URL, or the destination's name.</summary>
    /// <exception cref="ArgumentException">The value is empty.</exception>
    public string Value
    {
        get => _value;
        init => _value = CheckValue(value);
    }

    /// <summary>Returns a link to a URL.</summary>
    /// <param name="url">The URL.</param>
    /// <returns>The annotation.</returns>
    public static Annotation Url(string url) => new(AnnotationKind.Url, url);

    /// <summary>Returns a named destination.</summary>
    /// <param name="name">The destination's name.</param>
    /// <returns>The annotation.</returns>
    public static Annotation NamedDestination(string name) => new(AnnotationKind.NamedDestination, name);

    /// <summary>Returns a link to a named destination.</summary>
    /// <param name="name">The destination's name.</param>
    /// <returns>The annotation.</returns>
    public static Annotation LinkToDestination(string name) => new(AnnotationKind.LinkToDestination, name);

    private static string CheckValue(string value) => string.IsNullOrEmpty(value) ? throw new ArgumentException("An annotation needs a URL or a name.", nameof(value)) : value;
}

/// <summary>
/// A tag that brackets a run of drawing for a document backend, as PDF's marked content (BMC, BDC and EMC): a structure element of
/// tagged PDF (a paragraph, a heading, a figure), an artifact such as a page number, or a span with replacement text.
/// </summary>
/// <param name="Tag">The tag, such as P, H1, Figure, Span or Artifact: a PDF name without its slash.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="annotations"/>
/// </example>
public sealed record class MarkedContent(string Tag)
{
    private readonly string _tag = CheckTag(Tag);

    /// <summary>Gets the tag.</summary>
    /// <exception cref="ArgumentException">The tag is empty or holds a space, a slash or a control character.</exception>
    public string Tag
    {
        get => _tag;
        init => _tag = CheckTag(value);
    }

    /// <summary>
    /// Gets the caller's id for the structure element this content belongs to, which a tagged-PDF backend maps to a marked-content
    /// id in its structure tree, as Skia's structure node ids; <see langword="null"/> for content outside the structure.
    /// </summary>
    public int? NodeId { get; init; }

    /// <summary>Gets the text the content stands for, PDF's ActualText (a ligature's letters, a drawn word), or <see langword="null"/>.</summary>
    public string? ActualText { get; init; }

    /// <summary>Gets a description of the content for readers that cannot see it, PDF's Alt, or <see langword="null"/>.</summary>
    public string? AlternateText { get; init; }

    private static string CheckTag(string tag)
    {
        if (string.IsNullOrEmpty(tag) || tag.Any(c => c <= ' ' || c == '/' || c == 0x7f))
        {
            throw new ArgumentException("A tag is a PDF name without its slash: no spaces, slashes or control characters.", nameof(tag));
        }

        return tag;
    }
}

public abstract partial class Canvas
{
    private int _markedDepth;

    /// <summary>Gets the number of marked-content sequences begun and not yet ended.</summary>
    public int MarkedContentDepth => _markedDepth;

    /// <summary>
    /// Draws part of an encoded image, such as a JPEG, scaled to fill a local rectangle. A document backend may store the file's
    /// bytes as they are; other backends draw the decoded pixels, as <see cref="DrawImage(Raster, RectF, RectF, SamplingOptions?, Paint?)"/>.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <param name="source">The part of the image to draw, in its pixels; clipped to the image.</param>
    /// <param name="destination">Where it goes.</param>
    /// <param name="sampling">How the image is sampled; Egami's default sampling when <see langword="null"/>.</param>
    /// <param name="paint">The blend mode, opacity and antialiasing; its color is not used.</param>
    /// <exception cref="ArgumentException">A rectangle is not finite.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="encoded-image"/>
    /// </example>
    public void DrawImage(EncodedImage image, RectF source, RectF destination, SamplingOptions? sampling = null, Paint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        CheckLive();
        CheckRect(source, nameof(source));
        CheckRect(destination, nameof(destination));
        RectF clipped = source.Intersect(new RectF(0, 0, image.Width, image.Height));
        if (clipped.IsEmpty || destination.IsEmpty)
        {
            return;
        }

        if (clipped != source)
        {
            float sx = destination.Width / source.Width, sy = destination.Height / source.Height;
            destination = new RectF(destination.X + ((clipped.X - source.X) * sx), destination.Y + ((clipped.Y - source.Y) * sy), clipped.Width * sx, clipped.Height * sy);
        }

        OnDrawEncodedImage(image, clipped, destination, sampling ?? DefaultSampling, paint);
    }

    /// <summary>Draws a whole encoded image scaled to fill a local rectangle.</summary>
    /// <param name="image">The image.</param>
    /// <param name="destination">Where it goes.</param>
    /// <param name="sampling">How the image is sampled; Egami's default sampling when <see langword="null"/>.</param>
    /// <param name="paint">The blend mode, opacity and antialiasing; its color is not used.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="encoded-image"/>
    /// </example>
    public void DrawImage(EncodedImage image, RectF destination, SamplingOptions? sampling = null, Paint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        DrawImage(image, new RectF(0, 0, image.Width, image.Height), destination, sampling, paint);
    }

    /// <summary>
    /// Paints through an image used as a mask, as PDF's stencil masks (/ImageMask): the mask, scaled to fill a local rectangle,
    /// gives each pixel's coverage, and the paint fills what it covers. A mask with alpha covers by its alpha; one without, a 1-bit
    /// or gray image, covers by its gray level, so white paints and black does not. Scanned text and icons are drawn this way.
    /// </summary>
    /// <param name="mask">The mask image.</param>
    /// <param name="destination">Where the mask goes, in local coordinates.</param>
    /// <param name="paint">What fills the covered pixels, with its blend mode, opacity and antialiasing of the rectangle's edges.</param>
    /// <param name="sampling">How the mask is sampled; Egami's default sampling when <see langword="null"/>. Nearest keeps a 1-bit mask hard.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is not finite.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="image-mask"/>
    /// </example>
    public void DrawImageMask(Raster mask, RectF destination, Paint paint, SamplingOptions? sampling = null)
    {
        ArgumentNullException.ThrowIfNull(mask);
        Check(paint);
        CheckRect(destination, nameof(destination));
        if (!destination.IsEmpty)
        {
            OnDrawImageMask(mask, destination, paint, sampling ?? DefaultSampling);
        }
    }

    /// <summary>
    /// Marks an area with a link or a destination, as Skia's drawAnnotation: a PDF page writes a link annotation or a named
    /// destination; pixels show nothing. The area is in local coordinates, under the current transform.
    /// </summary>
    /// <param name="area">The area: the link's clickable rectangle, or for a destination the point at its top-left corner.</param>
    /// <param name="annotation">The link or destination.</param>
    /// <exception cref="ArgumentException"><paramref name="area"/> is not finite.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="annotations"/>
    /// </example>
    public void DrawAnnotation(RectF area, Annotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        CheckLive();
        CheckRect(area, nameof(area));
        OnDrawAnnotation(area, annotation);
    }

    /// <summary>
    /// Begins a marked-content sequence: everything drawn until the matching <see cref="EndMarkedContent"/> carries the tag, as
    /// PDF's BMC or BDC. Sequences nest; keep them nested with <see cref="Save"/> and <see cref="Restore"/>, as PDF requires.
    /// </summary>
    /// <param name="content">The tag and its properties.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="annotations"/>
    /// </example>
    public void BeginMarkedContent(MarkedContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        CheckLive();
        _markedDepth++;
        OnBeginMarkedContent(content);
    }

    /// <summary>Ends the marked-content sequence begun last, as PDF's EMC.</summary>
    /// <exception cref="InvalidOperationException">No sequence is open.</exception>
    public void EndMarkedContent()
    {
        CheckLive();
        if (_markedDepth == 0)
        {
            throw new InvalidOperationException("EndMarkedContent was called without a matching BeginMarkedContent.");
        }

        _markedDepth--;
        OnEndMarkedContent();
    }

    /// <summary>
    /// Draws part of an encoded image into a local rectangle; the source lies inside the image. A PDF backend can embed a JPEG's
    /// bytes as they are. By default the image is decoded once and drawn with <see cref="OnDrawImage"/>.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <param name="source">The part to draw, inside the image.</param>
    /// <param name="destination">Where it goes, in local coordinates.</param>
    /// <param name="sampling">How it is sampled.</param>
    /// <param name="paint">The blend mode, opacity and antialiasing, or <see langword="null"/> for source-over.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="encoded-image"/>
    /// </example>
    protected virtual void OnDrawEncodedImage(EncodedImage image, RectF source, RectF destination, SamplingOptions sampling, Paint? paint)
        => OnDrawImage(image.Decode(), source, destination, sampling, paint);

    /// <summary>
    /// Paints through an image mask. A PDF backend writes it as a stencil mask. By default the paint fills the rectangle in a layer
    /// and the mask's coverage, as an image drawn with <see cref="BlendMode.DestinationIn"/>, cuts it out.
    /// </summary>
    /// <param name="mask">The mask image.</param>
    /// <param name="destination">Where the mask goes, in local coordinates; not empty.</param>
    /// <param name="paint">What fills the covered pixels.</param>
    /// <param name="sampling">How the mask is sampled.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="image-mask"/>
    /// </example>
    protected virtual void OnDrawImageMask(Raster mask, RectF destination, Paint paint, SamplingOptions sampling)
    {
        using Raster<Rgba8> coverage = MaskCoverage(mask);
        int count = SaveLayer(new LayerOptions { Bounds = destination, Opacity = paint.Opacity, BlendMode = paint.BlendMode });
        try
        {
            FillRect(destination, paint with { BlendMode = BlendMode.SourceOver, Opacity = 1 });
            DrawImage(coverage, destination, sampling, new SolidPaint(new RgbaF32(1, 1, 1, 1)) { BlendMode = BlendMode.DestinationIn, Antialias = paint.Antialias });
        }
        finally
        {
            RestoreToCount(count);
        }
    }

    /// <summary>A mask's coverage as white with alpha: its own alpha when it has one, else its gray level.</summary>
    private protected static Raster<Rgba8> MaskCoverage(Raster mask)
    {
        bool alpha = mask.Layout.HasAlpha;
        using Raster<Rgba8> pixels = mask.As<Rgba8>();
        Raster<Rgba8> coverage = Raster.Create<Rgba8>(mask.Width, mask.Height);
        for (int y = 0; y < mask.Height; y++)
        {
            ReadOnlySpan<Rgba8> from = pixels.Row(y);
            Span<Rgba8> to = coverage.Row(y);
            for (int x = 0; x < from.Length; x++)
            {
                Rgba8 p = from[x];
                byte a = alpha ? p.A : (byte)(((p.R * 54) + (p.G * 183) + (p.B * 19) + 128) >> 8);
                to[x] = new Rgba8(255, 255, 255, a);
            }
        }

        return coverage;
    }

    /// <summary>Marks an area with a link or a destination. By default nothing happens: pixels show no annotations.</summary>
    /// <param name="area">The area, in local coordinates under <see cref="Matrix"/>.</param>
    /// <param name="annotation">The link or destination.</param>
    protected virtual void OnDrawAnnotation(RectF area, Annotation annotation)
    {
    }

    /// <summary>Begins a marked-content sequence. By default nothing happens.</summary>
    /// <param name="content">The tag and its properties.</param>
    protected virtual void OnBeginMarkedContent(MarkedContent content)
    {
    }

    /// <summary>Ends the marked-content sequence begun last. By default nothing happens.</summary>
    protected virtual void OnEndMarkedContent()
    {
    }

    /// <summary>Ends every marked-content sequence still open, for <see cref="Dispose()"/> and a recorder's finish.</summary>
    private protected void EndOpenMarkedContent()
    {
        while (_markedDepth > 0)
        {
            EndMarkedContent();
        }
    }
}
