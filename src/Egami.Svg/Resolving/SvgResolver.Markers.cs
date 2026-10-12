using System.Numerics;

using Egami.Css;
using Egami.Drawing;
using Egami.Svg.Styling;
using Egami.Svg.Values;

namespace Egami.Svg.Resolving;

internal sealed partial class SvgResolver
{
    /// <summary>
    /// The markers of a path, line, polyline or polygon: each marker's content resolved once (inheriting from the marker
    /// element, with context-fill and context-stroke the shape's paints), then placed at each vertex as resvg places it:
    /// translated to the vertex, turned to its angle, scaled by the stroke width (or the viewBox's fit into markerWidth by
    /// markerHeight) and moved by refX and refY; clipped to its viewport unless overflow is visible.
    /// </summary>
    private SvgGroup? Markers(SvgElement element, Path path, SvgStyle style, Paint? fill, Paint? stroke, in SvgContext context)
    {
        if (context.InClip || (style.MarkerStart is null && style.MarkerMid is null && style.MarkerEnd is null))
        {
            return null;
        }

        List<SvgNode> groups = [];
        AddMarkers(groups, element, path, style, style.MarkerStart, SvgMarkerKind.Start, fill, stroke, context);
        AddMarkers(groups, element, path, style, style.MarkerMid, SvgMarkerKind.Middle, fill, stroke, context);
        AddMarkers(groups, element, path, style, style.MarkerEnd, SvgMarkerKind.End, fill, stroke, context);
        return groups.Count == 0 ? null : new SvgGroup { Children = groups, Bounds = Bounds(groups) };
    }

    private void AddMarkers(List<SvgNode> groups, SvgElement element, Path path, SvgStyle style, string? url, SvgMarkerKind kind, Paint? fill, Paint? stroke, in SvgContext context)
    {
        if (url is null)
        {
            return;
        }

        SvgElement? marker = SvgStyleValues.LocalId(url) is string id ? _document.FindById(id) : null;
        if (marker is null || marker.Name != "marker" || !marker.IsSvgOrBare(_document))
        {
            _report.Warn("Svg.BadReference", $"The marker \"url({SvgGeometry.Clip(url)})\" names no marker; it is not drawn.", element);
            return;
        }

        float scale = 1;
        if (marker.Attributes["markerUnits"] != "userSpaceOnUse")
        {
            scale = style.StrokeWidth.ToPixels(context.Lengths, SvgAxis.Other);
            if (!(scale > 0))
            {
                return;
            }
        }

        SvgLengthContext lengths = context.Lengths;
        float refX = _geometry.Length(marker, "refX", lengths, SvgAxis.X, 0);
        float refY = _geometry.Length(marker, "refY", lengths, SvgAxis.Y, 0);
        float width = _geometry.Length(marker, "markerWidth", lengths, SvgAxis.X, 3);
        float height = _geometry.Length(marker, "markerHeight", lengths, SvgAxis.Y, 3);
        if (!(width > 0 && height > 0))
        {
            return;
        }

        RectF? viewBox = null;
        if (marker.Attributes["viewBox"] is string vbText)
        {
            if (!SvgViewBox.TryParse(vbText, out RectF vb) || vb.Width <= 0 || vb.Height <= 0)
            {
                return;
            }

            viewBox = vb;
        }

        Vector2 size = viewBox is RectF v ? Aspect(marker).Transform(v, new RectF(0, 0, width * scale, height * scale)) is var m ? new Vector2(m.M11, m.M22) : default : new Vector2(scale, scale);
        (string Kind, float Degrees) orient = Orient(marker);

        (SvgStyle markerStyle, CssComputedStyle markerCss) = OriginalStyle(marker);
        Vector2 viewport = viewBox?.Size ?? new Vector2(width, height);
        SvgContext inner = new(markerStyle, markerCss, lengths with { ViewportWidth = viewport.X, ViewportHeight = viewport.Y }, context.Depth + 1, new SvgContextPaint(fill, stroke), false);
        RectF? clip = markerStyle.OverflowVisible ? null : viewBox ?? new RectF(0, 0, width, height);

        // Gradients and patterns of the shape live in the shape's space: a marker's copy takes them through its own transform,
        // so its content is resolved once per vertex; solid paints let one content serve every vertex.
        bool perVertex = (fill is not null and not SolidPaint) || (stroke is not null and not SolidPaint);
        List<SvgNode>? shared = null;
        foreach ((Vector2 position, float auto) in SvgMarkerVertices.Vertices(path, kind))
        {
            float angle = orient.Kind switch
            {
                "auto" => auto,
                "auto-start-reverse" => kind == SvgMarkerKind.Start ? (auto + 180) % 360 : auto,
                _ => orient.Degrees,
            };
            Matrix3x2 transform = Matrix3x2.CreateTranslation(-refX, -refY) * Matrix3x2.CreateScale(size) * SvgTransforms.Rotate(angle) * Matrix3x2.CreateTranslation(position);
            List<SvgNode>? content = shared;
            if (content is null)
            {
                SvgContext vertex = inner;
                if (perVertex && Matrix3x2.Invert(transform, out Matrix3x2 inverse))
                {
                    vertex = inner with { ContextPaint = new SvgContextPaint(InSpace(fill, inverse), InSpace(stroke, inverse)) };
                }

                if (!Enter(marker, element))
                {
                    return;
                }

                try
                {
                    content = Children(marker, vertex);
                }
                finally
                {
                    Leave(marker);
                }

                if (!perVertex)
                {
                    shared = content;
                }
            }

            if (content.Count > 0)
            {
                groups.Add(new SvgGroup { Children = content, Transform = transform, ClipRect = clip, Bounds = Bounds(content), Element = marker, Id = marker.Id });
            }
        }
    }

    private static Paint? InSpace(Paint? paint, Matrix3x2 inverse) =>
        paint is null or SolidPaint ? paint : paint with { LocalMatrix = paint.LocalMatrix * inverse };

    /// <summary>The orient attribute: auto, auto-start-reverse, or an angle (a number of degrees, or deg, grad, rad or turn).</summary>
    private (string Kind, float Degrees) Orient(SvgElement marker)
    {
        string? text = marker.Attributes["orient"]?.Trim();
        if (text is null)
        {
            return ("angle", 0);
        }

        if (text is "auto" or "auto-start-reverse")
        {
            return (text, 0);
        }

        if (CssAngle.TryParse(text, out CssAngle angle))
        {
            return ("angle", (float)angle.Degrees);
        }

        if (SvgNumbers.TryParse(text, out double degrees))
        {
            return ("angle", (float)degrees);
        }

        _report.Warn("Svg.BadAttribute", $"The marker's orient \"{SvgGeometry.Clip(text)}\" cannot be read; 0 is used.", marker);
        return ("angle", 0);
    }
}
