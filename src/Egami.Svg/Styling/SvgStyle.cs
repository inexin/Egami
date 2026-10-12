using System.Numerics;

using Egami.Css;
using Egami.Drawing;
using Egami.Operations;
using Egami.Svg.Reading;
using Egami.Svg.Values;

namespace Egami.Svg.Styling;

/// <summary>
/// The computed SVG properties of one element instance, typed. A property set on the element (by a style sheet, the style
/// attribute or a presentation attribute) is parsed from its computed CSS value; an inherited one is copied from the parent's
/// typed value, so a length in em keeps the size it had where it was set; anything else takes its initial value. Lengths in
/// percent stay relative: they resolve against the viewport where they are used.
/// </summary>
internal sealed class SvgStyle
{
    public static SvgStyle Initial { get; } = new();

    // Inherited.
    public CssColor Color = new(0, 0, 0);
    public SvgPaintSpec Fill = SvgPaintSpec.Black;
    public float FillOpacity = 1;
    public FillRule FillRule = FillRule.NonZero;
    public SvgPaintSpec Stroke = SvgPaintSpec.None;
    public float StrokeOpacity = 1;
    public SvgLength StrokeWidth = new(1, SvgUnit.None);
    public LineCap LineCap = LineCap.Butt;
    public LineJoin LineJoin = LineJoin.Miter;
    public bool MiterClip;
    public float MiterLimit = 4;
    public SvgLength[]? DashArray;
    public SvgLength DashOffset = SvgLength.Zero;
    public string? MarkerStart;
    public string? MarkerMid;
    public string? MarkerEnd;
    public SvgPaintOrder PaintOrder = SvgPaintOrder.FillStrokeMarkers;
    public bool Visible = true;
    public FillRule ClipRule = FillRule.NonZero;
    public bool LinearRgbFilters = true;
    public bool LinearRgbInterpolation;
    public SamplingKind ImageRendering = SamplingKind.Smooth;
    public bool CrispEdges;
    public float FontSize = 16;

    // Not inherited.
    public float Opacity = 1;
    public bool DisplayNone;
    public bool OverflowVisible = true;
    public string? ClipPath;
    public string? Mask;
    public SoftMaskKind MaskType = SoftMaskKind.Luminosity;
    public IReadOnlyList<CssComponentValue>? Filter;
    public BlendMode BlendMode = BlendMode.SourceOver;
    public bool Isolate;
    public CssColor StopColor = new(0, 0, 0);
    public float StopOpacity = 1;
    public CssColor FloodColor = new(0, 0, 0);
    public float FloodOpacity = 1;
    public CssColor LightingColor = new(1, 1, 1);
    public bool NonScalingStroke;
    public IReadOnlyList<CssComponentValue>? Transform;
    public IReadOnlyList<CssComponentValue>? TransformOrigin;
    public string TransformBox = "view-box";

    /// <summary>The computed CSS style this was made from, for text properties read later.</summary>
    public CssComputedStyle? Css;

    /// <summary>The kinds of image sampling image-rendering selects.</summary>
    public enum SamplingKind : byte
    {
        Smooth,
        Pixelated,
    }

    /// <summary>
    /// Computes an element's typed style from its computed CSS style and its parent's typed style. What cannot be read is
    /// reported once per property and takes the inherited or initial value.
    /// </summary>
    public static SvgStyle Compute(CssComputedStyle css, SvgStyle parent, float rootFontSize, float dpi, SvgElement element, SvgReport report)
    {
        SvgStyle s = new()
        {
            Css = css,
            Color = parent.Color,
            Fill = parent.Fill,
            FillOpacity = parent.FillOpacity,
            FillRule = parent.FillRule,
            Stroke = parent.Stroke,
            StrokeOpacity = parent.StrokeOpacity,
            StrokeWidth = parent.StrokeWidth,
            LineCap = parent.LineCap,
            LineJoin = parent.LineJoin,
            MiterClip = parent.MiterClip,
            MiterLimit = parent.MiterLimit,
            DashArray = parent.DashArray,
            DashOffset = parent.DashOffset,
            MarkerStart = parent.MarkerStart,
            MarkerMid = parent.MarkerMid,
            MarkerEnd = parent.MarkerEnd,
            PaintOrder = parent.PaintOrder,
            Visible = parent.Visible,
            ClipRule = parent.ClipRule,
            LinearRgbFilters = parent.LinearRgbFilters,
            LinearRgbInterpolation = parent.LinearRgbInterpolation,
            ImageRendering = parent.ImageRendering,
            CrispEdges = parent.CrispEdges,
            FontSize = parent.FontSize,
        };

        Reader r = new(css, element, report);

        // font-size first: em lengths below resolve against it.
        if (r.Own("font-size") is IReadOnlyList<CssComponentValue> fontSize)
        {
            if (SvgStyleParsing.TryFontSize(fontSize, parent.FontSize, rootFontSize, dpi, out float size))
            {
                s.FontSize = size;
            }
            else
            {
                r.Bad("font-size");
            }
        }

        SvgLengthContext fonts = new(dpi, s.FontSize, rootFontSize, 100, 100, 100, 100);
        if (r.Own("color") is { } color)
        {
            if (SvgStyleValues.TryColor(color, out CssColor c, out bool level4) && !c.IsCurrentColor)
            {
                s.Color = c;
            }
            else if (!(SvgStyleValues.IsKeyword(color, "currentcolor")))
            {
                r.Bad("color", level4);
            }
        }

        if (r.Own("fill") is { } fill)
        {
            s.Fill = r.Paint("fill", fill, SvgPaintSpec.Black);
        }

        if (r.Own("stroke") is { } stroke)
        {
            s.Stroke = r.Paint("stroke", stroke, SvgPaintSpec.None);
        }

        r.Opacity("fill-opacity", ref s.FillOpacity);
        r.Opacity("stroke-opacity", ref s.StrokeOpacity);
        r.Opacity("opacity", ref s.Opacity);
        r.Opacity("stop-opacity", ref s.StopOpacity);
        r.Opacity("flood-opacity", ref s.FloodOpacity);
        r.Rule("fill-rule", ref s.FillRule);
        r.Rule("clip-rule", ref s.ClipRule);

        if (r.Own("stroke-width") is { } width)
        {
            if (SvgStyleValues.TryLength(width, out SvgLength w))
            {
                s.StrokeWidth = Absolute(w, fonts);
            }
            else
            {
                r.Bad("stroke-width");
            }
        }

        if (r.Keyword("stroke-linecap") is string cap)
        {
            s.LineCap = cap switch { "round" => LineCap.Round, "square" => LineCap.Square, _ => LineCap.Butt };
        }

        if (r.Keyword("stroke-linejoin") is string join)
        {
            (s.LineJoin, s.MiterClip) = join switch
            {
                "round" => (LineJoin.Round, false),
                "bevel" => (LineJoin.Bevel, false),
                "miter-clip" => (LineJoin.Miter, true),
                "arcs" => (LineJoin.Miter, false),
                _ => (LineJoin.Miter, false),
            };
        }

        if (r.Own("stroke-miterlimit") is { } miter)
        {
            if (SvgStyleValues.TryNumber(miter, out float m) && m >= 1)
            {
                s.MiterLimit = m;
            }
            else
            {
                r.Bad("stroke-miterlimit");
            }
        }

        if (r.Own("stroke-dasharray") is { } dash)
        {
            List<SvgLength> lengths = [];
            if (SvgStyleValues.IsKeyword(dash, "none"))
            {
                s.DashArray = null;
            }
            else if (SvgStyleValues.TryLengthList(dash, lengths) && !lengths.Exists(l => l.Value < 0))
            {
                s.DashArray = [.. lengths.Select(l => Absolute(l, fonts))];
            }
            else
            {
                r.Bad("stroke-dasharray");
            }
        }

        if (r.Own("stroke-dashoffset") is { } offset)
        {
            if (SvgStyleValues.TryLength(offset, out SvgLength o))
            {
                s.DashOffset = Absolute(o, fonts);
            }
            else
            {
                r.Bad("stroke-dashoffset");
            }
        }

        r.Reference("marker-start", ref s.MarkerStart);
        r.Reference("marker-mid", ref s.MarkerMid);
        r.Reference("marker-end", ref s.MarkerEnd);
        r.Reference("clip-path", ref s.ClipPath);
        r.Reference("mask", ref s.Mask);

        if (r.Own("paint-order") is { } order)
        {
            if (SvgStyleValues.IsKeyword(order, "normal"))
            {
                s.PaintOrder = SvgPaintOrder.FillStrokeMarkers;
            }
            else if (SvgStyleParsing.TryPaintOrder(order, out SvgPaintOrder po))
            {
                s.PaintOrder = po;
            }
            else
            {
                r.Bad("paint-order");
            }
        }

        if (r.Keyword("visibility") is string visibility)
        {
            s.Visible = visibility == "visible";
        }

        if (r.Keyword("color-interpolation-filters") is string cif)
        {
            s.LinearRgbFilters = cif != "srgb";
        }

        if (r.Keyword("color-interpolation") is string ci)
        {
            s.LinearRgbInterpolation = ci == "linearrgb";
        }

        if (r.Keyword("image-rendering") is string ir)
        {
            s.ImageRendering = ir is "optimizespeed" or "pixelated" or "crisp-edges" ? SamplingKind.Pixelated : SamplingKind.Smooth;
        }

        if (r.Keyword("shape-rendering") is string sr)
        {
            s.CrispEdges = sr is "crispedges" or "optimizespeed";
        }

        if (r.Keyword("display") is string display)
        {
            s.DisplayNone = display == "none";
        }

        if (r.Keyword("overflow") is string overflow)
        {
            s.OverflowVisible = overflow is "visible" or "auto";
        }

        if (r.Keyword("mask-type") is string maskType)
        {
            s.MaskType = maskType == "alpha" ? SoftMaskKind.Alpha : SoftMaskKind.Luminosity;
        }

        if (r.Own("filter") is { } filter && !SvgStyleValues.IsKeyword(filter, "none"))
        {
            s.Filter = filter;
        }

        if (r.Keyword("mix-blend-mode") is string blend)
        {
            if (SvgStyleParsing.TryBlendMode(blend, out BlendMode mode))
            {
                s.BlendMode = mode;
            }
            else
            {
                r.Bad("mix-blend-mode");
            }
        }

        if (r.Keyword("isolation") is string isolation)
        {
            s.Isolate = isolation == "isolate";
        }

        r.Color("stop-color", ref s.StopColor, s.Color);
        r.Color("flood-color", ref s.FloodColor, s.Color);
        r.Color("lighting-color", ref s.LightingColor, s.Color);

        if (r.Keyword("vector-effect") is string effect)
        {
            s.NonScalingStroke = effect == "non-scaling-stroke";
        }

        if (r.Own("transform") is { } transform && !SvgStyleValues.IsKeyword(transform, "none"))
        {
            s.Transform = transform;
        }

        if (r.Own("transform-origin") is { } origin)
        {
            s.TransformOrigin = origin;
        }

        if (r.Keyword("transform-box") is string box)
        {
            s.TransformBox = box;
        }

        return s;
    }

    /// <summary>Resolves font-relative and physical units now, as CSS computed values do; percentages stay for later.</summary>
    private static SvgLength Absolute(SvgLength length, in SvgLengthContext fonts) =>
        length.Unit is SvgUnit.Percent or SvgUnit.None or SvgUnit.Px or SvgUnit.Vw or SvgUnit.Vh or SvgUnit.Vmin or SvgUnit.Vmax
            ? length
            : new SvgLength(length.ToPixels(fonts, SvgAxis.Other), SvgUnit.Px);

    /// <summary>Reads the properties set on one element and reports values that cannot be used.</summary>
    private readonly struct Reader(CssComputedStyle css, SvgElement element, SvgReport report)
    {
        public IReadOnlyList<CssComponentValue>? Own(string name) =>
            css.IsSetOnElement(name) && css.TryGetValue(name, out IReadOnlyList<CssComponentValue>? value) ? value : null;

        public string? Keyword(string name) => Own(name) is { } value ? SvgStyleValues.Keyword(value)?.ToLowerInvariant() : null;

        public void Bad(string name, bool level4 = false) => report.Warn(
            level4 ? "Svg.UnsupportedColor" : "Svg.BadStyle",
            level4
                ? $"The {element.Name}'s {name} uses CSS Color 4 syntax, which SVG here does not read; the initial value is used."
                : $"The {element.Name}'s {name} \"{Resolving.SvgGeometry.Clip(css.GetValueText(name) ?? "")}\" cannot be used; it is ignored.",
            element);

        public SvgPaintSpec Paint(string name, IReadOnlyList<CssComponentValue> value, SvgPaintSpec initial)
        {
            if (SvgStyleValues.TryPaint(value, out SvgPaintSpec paint, out bool level4))
            {
                return paint;
            }

            Bad(name, level4);
            return initial;
        }

        public void Opacity(string name, ref float field)
        {
            if (Own(name) is { } value)
            {
                if (SvgStyleValues.TryOpacity(value, out float o))
                {
                    field = o;
                }
                else
                {
                    Bad(name);
                }
            }
        }

        public void Rule(string name, ref FillRule field)
        {
            if (Keyword(name) is string k)
            {
                field = k == "evenodd" ? FillRule.EvenOdd : FillRule.NonZero;
            }
        }

        public void Reference(string name, ref string? field)
        {
            if (Own(name) is { } value)
            {
                if (SvgStyleValues.TryReference(value, out string? url))
                {
                    field = string.IsNullOrEmpty(url) ? null : url;
                }
                else
                {
                    Bad(name);
                }
            }
        }

        public void Color(string name, ref CssColor field, CssColor current)
        {
            if (Own(name) is { } value)
            {
                if (SvgStyleValues.TryColor(value, out CssColor c, out bool level4))
                {
                    field = c.IsCurrentColor ? current : c;
                }
                else
                {
                    Bad(name, level4);
                }
            }
        }
    }
}
