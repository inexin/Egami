using System.Numerics;

using Egami.Css;
using Egami.Drawing;
using Egami.Pixels;
using Egami.Svg.Reading;
using Egami.Svg.Rendering;
using Egami.Svg.Styling;
using Egami.Svg.Values;

namespace Egami.Svg.Resolving;

/// <summary>What resolving a paint server gave.</summary>
internal enum PaintResult : byte
{
    /// <summary>A paint.</summary>
    Paint,

    /// <summary>The server is valid but paints nothing (no stops, an empty bounding box, a zero-sized tile).</summary>
    None,

    /// <summary>The reference names no paint server; the fallback applies.</summary>
    Missing,
}

internal sealed partial class SvgResolver
{
    /// <summary>A gradient or pattern as a paint for a shape with the given bounding box.</summary>
    private PaintResult PaintServer(SvgElement server, RectF bounds, in SvgContext context, out Paint? paint)
    {
        paint = null;
        if (!server.IsSvgOrBare(_document))
        {
            return PaintResult.Missing;
        }

        return server.Name switch
        {
            "linearGradient" or "radialGradient" => Gradient(server, bounds, context, out paint),
            "pattern" => Pattern(server, bounds, context, out paint),
            _ => PaintResult.Missing,
        };
    }

    /// <summary>The chain of elements an href inheritance walks (gradients and patterns), without cycles, nearest first.</summary>
    private List<SvgElement> Chain(SvgElement start, string kind)
    {
        List<SvgElement> chain = [start];
        SvgElement current = start;
        while (chain.Count < SvgLimits.MaxReferenceDepth)
        {
            string? href = current.Attributes["href"] ?? current.Attributes.Get("href", SvgNamespaces.XLink);
            SvgElement? next = href is not null && SvgStyleValues.LocalId(href) is string id ? _document.FindById(id) : null;
            if (next is null || chain.Contains(next) || !next.IsSvgOrBare(_document))
            {
                break;
            }

            bool fits = kind == "gradient" ? next.Name is "linearGradient" or "radialGradient" : next.Name == kind;
            if (!fits)
            {
                break;
            }

            chain.Add(next);
            current = next;
        }

        return chain;
    }

    private static string? Inherited(List<SvgElement> chain, string attribute, string? ns = null)
    {
        foreach (SvgElement element in chain)
        {
            if (element.Attributes.Get(attribute, ns) is string value)
            {
                return value;
            }
        }

        return null;
    }

    private PaintResult Gradient(SvgElement gradient, RectF bounds, in SvgContext context, out Paint? paint)
    {
        paint = null;
        List<SvgElement> chain = Chain(gradient, "gradient");
        List<GradientStop> stops = Stops(chain);
        if (stops.Count == 0)
        {
            return PaintResult.None;
        }

        if (stops.Count == 1)
        {
            paint = new SolidPaint(stops[0].Color);
            return PaintResult.Paint;
        }

        bool boundingBox = Inherited(chain, "gradientUnits") is not "userSpaceOnUse";
        if (boundingBox && (bounds.Width <= 0 || bounds.Height <= 0))
        {
            return PaintResult.None;
        }

        TileMode tile = Inherited(chain, "spreadMethod") switch { "reflect" => TileMode.Mirror, "repeat" => TileMode.Repeat, _ => TileMode.Clamp };
        Matrix3x2 transform = Matrix3x2.Identity;
        if (Inherited(chain, "gradientTransform") is string gt && !SvgTransforms.TryParse(gt, out transform))
        {
            _report.Warn("Svg.BadAttribute", $"The gradient's gradientTransform \"{SvgGeometry.Clip(gt)}\" cannot be read; it is ignored.", gradient);
            transform = Matrix3x2.Identity;
        }

        if (!Matrix3x2.Invert(transform, out _))
        {
            transform = Matrix3x2.Identity;
        }

        transform = AboutOrigin(gradient, transform, context);
        Matrix3x2 local = boundingBox ? transform * new Matrix3x2(bounds.Width, 0, 0, bounds.Height, bounds.X, bounds.Y) : transform;

        SvgLengthContext lengths = context.Lengths;
        List<SvgElement> sameKind = chain.FindAll(e => e.Name == gradient.Name);
        float Coordinate(string name, string fallback, SvgAxis axis)
        {
            string text = Inherited(sameKind, name) ?? fallback;
            if (!SvgLength.TryParse(text, out SvgLength l))
            {
                _report.Warn("Svg.BadAttribute", $"The gradient's {name} \"{SvgGeometry.Clip(text)}\" is not a length; {fallback} is used.", gradient);
                _ = SvgLength.TryParse(fallback, out l);
            }

            return boundingBox ? l.ToFraction() : l.ToPixels(lengths, axis);
        }

        bool linear = gradient.Name == "linearGradient";
        if (linear)
        {
            Vector2 start = new(Coordinate("x1", "0%", SvgAxis.X), Coordinate("y1", "0%", SvgAxis.Y));
            Vector2 end = new(Coordinate("x2", "100%", SvgAxis.X), Coordinate("y2", "0%", SvgAxis.Y));
            if (start == end)
            {
                paint = new SolidPaint(stops[^1].Color);
                return PaintResult.Paint;
            }

            paint = new LinearGradient(start, end, [.. stops]) { Tile = tile, LocalMatrix = local, Premultiplied = false };
            return PaintResult.Paint;
        }

        Vector2 center = new(Coordinate("cx", "50%", SvgAxis.X), Coordinate("cy", "50%", SvgAxis.Y));
        float radius = Coordinate("r", "50%", SvgAxis.Other);
        Vector2 focal = new(
            Inherited(sameKind, "fx") is null ? center.X : Coordinate("fx", "50%", SvgAxis.X),
            Inherited(sameKind, "fy") is null ? center.Y : Coordinate("fy", "50%", SvgAxis.Y));
        float focalRadius = Coordinate("fr", "0%", SvgAxis.Other);
        focalRadius = Math.Max(focalRadius, 0);
        if (!(radius > 0))
        {
            // SVG 1.1: a zero radius paints the area with the last stop's color (resvg does the same for a negative one).
            paint = new SolidPaint(stops[^1].Color);
            return PaintResult.Paint;
        }

        if (focal == center && focalRadius == radius)
        {
            focalRadius = radius * 0.9999f;
        }

        paint = new ConicalGradient(focal, focalRadius, center, radius, [.. stops]) { Tile = tile, LocalMatrix = local, Premultiplied = false };
        return PaintResult.Paint;
    }

    /// <summary>
    /// Turns a gradientTransform or patternTransform about the element's transform-origin, in user units (percentages of the
    /// nearest viewport), as resvg resolves it.
    /// </summary>
    private Matrix3x2 AboutOrigin(SvgElement element, Matrix3x2 transform, in SvgContext context)
    {
        if (transform.IsIdentity || OriginalStyle(element).Style.TransformOrigin is not IReadOnlyList<CssComponentValue> origin)
        {
            return transform;
        }

        RectF box = new(0, 0, context.Lengths.ViewportWidth, context.Lengths.ViewportHeight);
        return SvgCssTransforms.TryOrigin(origin, box, out Vector2 o) && o != Vector2.Zero
            ? Matrix3x2.CreateTranslation(-o) * transform * Matrix3x2.CreateTranslation(o)
            : transform;
    }

    /// <summary>The stops of the first gradient in the chain that has any: offsets clamped to 0 to 1 and never decreasing.</summary>
    private List<GradientStop> Stops(List<SvgElement> chain)
    {
        List<GradientStop> stops = [];
        foreach (SvgElement gradient in chain)
        {
            float last = 0;
            foreach (SvgElement stop in gradient.Elements("stop"))
            {
                // A missing or unreadable offset repeats the previous one, as resvg reads stops.
                float offset = last;
                if (stop.Attributes["offset"] is string text)
                {
                    ReadOnlySpan<char> t = text.AsSpan().Trim();
                    bool percent = t.EndsWith("%");
                    if (SvgNumbers.TryParse(percent ? t[..^1] : t, out double value))
                    {
                        offset = (float)(percent ? value / 100 : value);
                    }
                    else
                    {
                        _report.Warn("Svg.BadAttribute", $"The stop's offset \"{SvgGeometry.Clip(text)}\" is not a number; the previous offset is used.", stop);
                    }
                }

                offset = Math.Max(Math.Clamp(offset, 0, 1), last);
                last = offset;
                SvgStyle style = OriginalStyle(stop).Style;
                CssColor color = style.StopColor;
                stops.Add(new GradientStop(offset, new RgbaF32(color.R, color.G, color.B, color.A * style.StopOpacity)));
            }

            // Of three stops at one offset the middle one cannot show.
            for (int i = 0; i + 2 < stops.Count;)
            {
                if (stops[i].Offset == stops[i + 1].Offset && stops[i + 1].Offset == stops[i + 2].Offset)
                {
                    stops.RemoveAt(i + 1);
                }
                else
                {
                    i++;
                }
            }

            if (stops.Count > 0)
            {
                for (int i = 0; i + 1 < stops.Count; i++)
                {
                    if (stops[i].Offset == 0 && stops[i + 1].Offset == 0)
                    {
                        stops[i + 1] = new GradientStop(float.Epsilon * 8, stops[i + 1].Color);
                    }
                }

                for (int i = 1; i < stops.Count; i++)
                {
                    if (stops[i - 1].Offset >= stops[i].Offset)
                    {
                        float at = stops[i - 1].Offset;
                        stops[i - 1] = new GradientStop(Math.Max(0, at - 1e-6f), stops[i - 1].Color);
                        stops[i] = new GradientStop(at, stops[i].Color);
                    }
                }

                break;
            }
        }

        return stops;
    }

    /// <summary>A pattern: its tile recorded once as a picture and repeated, so it stays vector on a PDF page.</summary>
    private PaintResult Pattern(SvgElement pattern, RectF bounds, in SvgContext context, out Paint? paint)
    {
        paint = null;
        List<SvgElement> chain = Chain(pattern, "pattern");
        SvgElement? content = chain.Find(e => e.Elements().Any());
        if (content is null)
        {
            return PaintResult.None;
        }

        bool boundingBox = Inherited(chain, "patternUnits") is not "userSpaceOnUse";
        bool contentBoundingBox = Inherited(chain, "patternContentUnits") == "objectBoundingBox";
        if ((boundingBox || contentBoundingBox) && (bounds.Width <= 0 || bounds.Height <= 0))
        {
            return PaintResult.None;
        }

        SvgLengthContext lengths = context.Lengths;
        float Value(string name, SvgAxis axis)
        {
            string? text = Inherited(chain, name);
            if (text is null || !SvgLength.TryParse(text, out SvgLength l))
            {
                l = SvgLength.Zero;
            }

            if (!boundingBox)
            {
                return l.ToPixels(lengths, axis);
            }

            float offset = name == "x" ? bounds.X : name == "y" ? bounds.Y : 0;
            return offset + (l.ToFraction() * (axis == SvgAxis.X ? bounds.Width : bounds.Height));
        }

        RectF tile = new(Value("x", SvgAxis.X), Value("y", SvgAxis.Y), Value("width", SvgAxis.X), Value("height", SvgAxis.Y));
        if (!(tile.Width > 0 && tile.Height > 0))
        {
            return PaintResult.None;
        }

        Matrix3x2 contentTransform = Matrix3x2.Identity;
        RectF? viewBox = null;
        if (Inherited(chain, "viewBox") is string vbText && SvgViewBox.TryParse(vbText, out RectF vb))
        {
            if (vb.Width <= 0 || vb.Height <= 0)
            {
                return PaintResult.None;
            }

            viewBox = vb;
            SvgAspectRatio aspect = Inherited(chain, "preserveAspectRatio") is string par && SvgAspectRatio.TryParse(par, out SvgAspectRatio a) ? a : SvgAspectRatio.Default;
            contentTransform = aspect.Transform(vb, new RectF(0, 0, tile.Width, tile.Height));
        }
        else if (contentBoundingBox)
        {
            // Content in bounding box units is scaled by the box but not moved: the tile itself sits at the box's corner.
            contentTransform = Matrix3x2.CreateScale(bounds.Width, bounds.Height);
        }

        Matrix3x2 patternTransform = Matrix3x2.Identity;
        if (Inherited(chain, "patternTransform") is string pt && !SvgTransforms.TryParse(pt, out patternTransform))
        {
            _report.Warn("Svg.BadAttribute", $"The pattern's patternTransform \"{SvgGeometry.Clip(pt)}\" cannot be read; it is ignored.", pattern);
            patternTransform = Matrix3x2.Identity;
        }

        if (!Matrix3x2.Invert(patternTransform, out _))
        {
            patternTransform = Matrix3x2.Identity;
        }

        patternTransform = AboutOrigin(pattern, patternTransform, context);

        if (!Enter(pattern, pattern))
        {
            return PaintResult.None;
        }

        SvgGroup group;
        try
        {
            (SvgStyle style, CssComputedStyle css) = OriginalStyle(content);
            Vector2 viewport = viewBox?.Size ?? (contentBoundingBox ? Vector2.One : new Vector2(lengths.ViewportWidth, lengths.ViewportHeight));
            SvgContext inner = new(style, css, lengths with { ViewportWidth = viewport.X, ViewportHeight = viewport.Y }, context.Depth + 1, null, false);
            List<SvgNode> children = Children(content, inner);
            group = new SvgGroup { Children = children, Transform = contentTransform, Bounds = Bounds(children) };
        }
        finally
        {
            Leave(pattern);
        }

        // The tile is recorded with its corner at the origin, content coordinates relative to it, as resvg places pattern content.
        RectF cell = new(0, 0, tile.Width, tile.Height);
        using PictureRecorder recorder = new(cell);
        recorder.ClipRect(cell);
        SvgRenderer.Draw(recorder, group, SvgRenderOptions.Default);
        paint = new PicturePaint(recorder.Finish(), TileMode.Repeat, TileMode.Repeat, cell) { LocalMatrix = Matrix3x2.CreateTranslation(tile.X, tile.Y) * patternTransform };
        return PaintResult.Paint;
    }
}
