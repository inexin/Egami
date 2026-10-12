using System.Numerics;

using Egami.Css;
using Egami.Drawing;
using Egami.Svg.Styling;
using Egami.Svg.Values;

namespace Egami.Svg.Resolving;

internal sealed partial class SvgResolver
{
    /// <summary>A shape: its path, fill and stroke paints (paint servers resolved against its bounding box), stroke and markers.</summary>
    private SvgNode? Shape(SvgElement element, SvgStyle style, in SvgContext context)
    {
        _geometryStyle = (element, style.Css);
        Path? path;
        try
        {
            path = SvgShapes.Build(element, _geometry, context.Lengths, _limits.MaxPathCommands, _report);
        }
        finally
        {
            _geometryStyle = default;
        }

        if (path is null)
        {
            return null;
        }

        FillRule rule = context.InClip ? style.ClipRule : style.FillRule;
        path = path.WithFillRule(rule);
        RectF bounds = path.TightBounds;
        if (context.InClip)
        {
            if (!style.Visible)
            {
                return null;
            }

            return Decorate(new SvgPath(path) { Fill = new SolidPaint(new Egami.Pixels.RgbaF32(0, 0, 0, 1)), Bounds = bounds, Transform = ElementTransform(element, style, bounds, context.Lengths) }, element, style, context);
        }

        Matrix3x2 transform = ElementTransform(element, style, bounds, context.Lengths);
        SvgContext own = Into(context, transform);
        Paint? fill = style.Visible ? ResolvePaint(style.Fill, style, style.FillOpacity, bounds, element, own) : null;
        Paint? strokePaint = style.Visible ? ResolvePaint(style.Stroke, style, style.StrokeOpacity, bounds, element, own) : null;
        Stroke? stroke = null;
        if (strokePaint is not null)
        {
            stroke = StrokeOf(style, context.Lengths);
            if (stroke is null)
            {
                strokePaint = null;
            }
        }

        SvgGroup? markers = Markers(element, path, style, fill, strokePaint, context);
        if (fill is null && strokePaint is null && markers is null)
        {
            // Nothing to paint, but the shape still counts for its parent's bounding box.
            return new SvgGroup { Bounds = bounds, Transform = ElementTransform(element, style, bounds, context.Lengths), Element = element, Id = element.Id };
        }

        SvgPath node = new(path)
        {
            Fill = fill,
            StrokePaint = strokePaint,
            Stroke = stroke,
            Markers = markers,
            PaintOrder = style.PaintOrder,
            NonScalingStroke = style.NonScalingStroke,
            Antialias = !style.CrispEdges,
            Bounds = bounds,
            Transform = transform,
        };
        return Decorate(node, element, style, context);
    }

    /// <summary>The stroke geometry, or null when the width is zero or the dashes draw nothing.</summary>
    private static Stroke? StrokeOf(SvgStyle style, in SvgLengthContext lengths)
    {
        float width = style.StrokeWidth.Value < 0 ? 0 : style.StrokeWidth.ToPixels(lengths, SvgAxis.Other);
        if (!(width > 0) || !float.IsFinite(width))
        {
            return null;
        }

        DashPattern? dash = null;
        if (style.DashArray is SvgLength[] array)
        {
            int n = array.Length % 2 == 1 ? array.Length * 2 : array.Length;
            float[] intervals = new float[n];
            float sum = 0;
            for (int i = 0; i < n; i++)
            {
                intervals[i] = array[i % array.Length].ToPixels(lengths, SvgAxis.Other);
                if (intervals[i] < 0 || !float.IsFinite(intervals[i]))
                {
                    intervals = [];
                    break;
                }

                sum += intervals[i];
            }

            // A pattern that sums to zero draws a solid line, as SVG says.
            if (intervals.Length > 0 && sum > 0)
            {
                dash = new DashPattern(intervals, style.DashOffset.ToPixels(lengths, SvgAxis.Other));
            }
        }

        return new Stroke(width)
        {
            Cap = style.LineCap,
            Join = style.LineJoin,
            MiterLimit = style.MiterLimit,
            Dash = dash,
        };
    }

    /// <summary>
    /// The paints context-fill and context-stroke take inside a use of this element: gradients and patterns over
    /// <paramref name="bounds"/> (the use's bounding box), moved into the copy's coordinates by <paramref name="toContent"/>.
    /// </summary>
    private SvgContextPaint ContextPaints(SvgStyle style, in SvgContext context, RectF? bounds, Matrix3x2 toContent)
    {
        Paint? Resolve(SvgPaintSpec spec, float opacity, SvgContext ctx)
        {
            if (spec.Kind == SvgPaintKind.Server && bounds is null)
            {
                return null;
            }

            Paint? paint = ResolvePaint(spec, style, opacity, bounds ?? RectF.Empty, null, ctx);
            return paint is null || paint is SolidPaint || toContent.IsIdentity ? paint : paint with { LocalMatrix = paint.LocalMatrix * toContent };
        }

        return new SvgContextPaint(Resolve(style.Fill, style.FillOpacity, context), Resolve(style.Stroke, style.StrokeOpacity, context));
    }

    /// <summary>A fill or stroke value as a paint for a shape with these bounds, or null for none.</summary>
    private Paint? ResolvePaint(SvgPaintSpec spec, SvgStyle style, float opacity, RectF bounds, SvgElement? element, in SvgContext context)
    {
        switch (spec.Kind)
        {
            case SvgPaintKind.None:
                return null;
            case SvgPaintKind.Color:
                return Solid(spec.Color, opacity);
            case SvgPaintKind.CurrentColor:
                return Solid(style.Color, opacity);
            case SvgPaintKind.ContextFill:
                return WithOpacity(context.ContextPaint?.Fill, opacity);
            case SvgPaintKind.ContextStroke:
                return WithOpacity(context.ContextPaint?.Stroke, opacity);
        }

        SvgElement? server = SvgStyleValues.LocalId(spec.Url!) is string id ? _document.FindById(id) : null;
        Paint? paint = null;
        PaintResult result = server is null ? PaintResult.Missing : PaintServer(server, bounds, context, out paint);
        switch (result)
        {
            case PaintResult.Paint:
                return WithOpacity(paint, opacity);
            case PaintResult.None when spec.FallbackKind == SvgPaintKind.None:
                return null;
        }

        if (result == PaintResult.Missing && element is not null)
        {
            _report.Warn("Svg.BadReference", $"The paint \"url({SvgGeometry.Clip(spec.Url!)})\" names no gradient or pattern; {(spec.FallbackKind == SvgPaintKind.None ? "nothing is painted" : "its fallback is used")}.", element);
        }

        return spec.FallbackKind switch
        {
            SvgPaintKind.Color => Solid(spec.FallbackColor, opacity),
            SvgPaintKind.CurrentColor => Solid(style.Color, opacity),
            _ => null,
        };
    }

    private static Paint? WithOpacity(Paint? paint, float opacity) =>
        paint is null ? null : opacity >= 1 ? paint : paint with { Opacity = paint.Opacity * opacity };
}
