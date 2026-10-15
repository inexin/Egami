using Egami.Css;

namespace Egami.Svg.Styling;

/// <summary>
/// The CSS properties SVG uses, with whether they inherit and their initial values (SVG 2, CSS Masking, Filter Effects,
/// Compositing, CSS Fonts and Text), and which attributes are presentation attributes for them. Each property validates its
/// value, so an invalid declaration or presentation attribute is dropped by the cascade and the next one wins, as CSS says.
/// </summary>
internal static class SvgProperties
{
    /// <summary>The geometry properties (SVG 2 section 13) and the elements whose attributes of that name are presentation attributes.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Geometry = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["cx"] = ["circle", "ellipse"],
        ["cy"] = ["circle", "ellipse"],
        ["r"] = ["circle"],
        ["rx"] = ["ellipse", "rect"],
        ["ry"] = ["ellipse", "rect"],
        ["x"] = ["rect", "image", "svg", "use", "foreignObject", "symbol"],
        ["y"] = ["rect", "image", "svg", "use", "foreignObject", "symbol"],
        ["width"] = ["rect", "image", "svg", "use", "foreignObject", "symbol"],
        ["height"] = ["rect", "image", "svg", "use", "foreignObject", "symbol"],
        ["d"] = ["path"],
    };

    private static readonly (string Name, bool Inherited, string Initial, Func<IReadOnlyList<CssComponentValue>, bool>? Validate)[] s_properties =
    [
        ("color", true, "black", Color),
        ("fill", true, "black", Paint),
        ("fill-opacity", true, "1", Opacity),
        ("fill-rule", true, "nonzero", Keywords("nonzero", "evenodd")),
        ("stroke", true, "none", Paint),
        ("stroke-opacity", true, "1", Opacity),
        ("stroke-width", true, "1", Length),
        ("stroke-linecap", true, "butt", Keywords("butt", "round", "square")),
        ("stroke-linejoin", true, "miter", Keywords("miter", "miter-clip", "round", "bevel", "arcs")),
        ("stroke-miterlimit", true, "4", v => SvgStyleValues.TryNumber(v, out float n) && n >= 1),
        ("stroke-dasharray", true, "none", DashArray),
        ("stroke-dashoffset", true, "0", Length),
        ("marker-start", true, "none", Reference),
        ("marker-mid", true, "none", Reference),
        ("marker-end", true, "none", Reference),
        ("paint-order", true, "normal", PaintOrder),
        ("visibility", true, "visible", Keywords("visible", "hidden", "collapse")),
        ("clip-rule", true, "nonzero", Keywords("nonzero", "evenodd")),
        ("color-interpolation", true, "sRGB", Keywords("auto", "srgb", "linearrgb")),
        ("color-interpolation-filters", true, "linearRGB", Keywords("auto", "srgb", "linearrgb")),
        ("color-rendering", true, "auto", Keywords("auto", "optimizespeed", "optimizequality")),
        ("image-rendering", true, "auto", Keywords("auto", "optimizespeed", "optimizequality", "smooth", "high-quality", "crisp-edges", "pixelated")),
        ("shape-rendering", true, "auto", Keywords("auto", "optimizespeed", "crispedges", "geometricprecision")),
        ("text-rendering", true, "auto", null),
        ("pointer-events", true, "visiblePainted", null),
        ("cursor", true, "auto", null),
        ("font-family", true, "serif", null),
        ("font-size", true, "medium", null),
        ("font-style", true, "normal", null),
        ("font-weight", true, "normal", null),
        ("font-stretch", true, "normal", null),
        ("font-variant", true, "normal", null),
        ("font-variant-caps", true, "normal", null),
        ("font-variant-ligatures", true, "normal", null),
        ("font-variant-numeric", true, "normal", null),
        ("font-variant-east-asian", true, "normal", null),
        ("font-variant-position", true, "normal", null),
        ("font-kerning", true, "auto", null),
        ("font-feature-settings", true, "normal", null),
        ("font-variation-settings", true, "normal", null),
        ("font-size-adjust", true, "none", null),
        ("font-synthesis", true, "weight style", null),
        ("letter-spacing", true, "normal", null),
        ("word-spacing", true, "normal", null),
        ("text-anchor", true, "start", Keywords("start", "middle", "end")),
        ("writing-mode", true, "horizontal-tb", null),
        ("direction", true, "ltr", Keywords("ltr", "rtl")),
        ("text-orientation", true, "mixed", null),
        ("glyph-orientation-vertical", true, "auto", null),
        ("glyph-orientation-horizontal", true, "0deg", null),
        ("white-space", true, "normal", null),
        ("line-height", true, "normal", null),
        ("dominant-baseline", true, "auto", null),
        ("text-transform", true, "none", null),
        ("text-align", true, "start", null),
        ("text-align-last", true, "auto", null),
        ("text-indent", true, "0", null),
        ("tab-size", true, "8", null),
        ("hyphens", true, "manual", null),
        ("overflow-wrap", true, "normal", null),
        ("word-break", true, "normal", null),
        ("line-break", true, "auto", null),
        ("text-decoration-skip-ink", true, "auto", null),
        ("opacity", false, "1", Opacity),
        ("background-color", false, "transparent", Color),
        ("display", false, "inline", null),
        ("clip-path", false, "none", null),
        ("clip", false, "auto", null),
        ("mask", false, "none", null),
        ("mask-type", false, "luminance", Keywords("luminance", "alpha")),
        ("filter", false, "none", null),
        ("mix-blend-mode", false, "normal", null),
        ("isolation", false, "auto", Keywords("auto", "isolate")),
        ("transform", false, "none", null),
        ("transform-origin", false, "0 0", null),
        ("transform-box", false, "view-box", Keywords("content-box", "border-box", "fill-box", "stroke-box", "view-box")),
        ("stop-color", false, "black", Color),
        ("stop-opacity", false, "1", Opacity),
        ("flood-color", false, "black", Color),
        ("flood-opacity", false, "1", Opacity),
        ("lighting-color", false, "white", Color),
        ("vector-effect", false, "none", Keywords("none", "non-scaling-stroke", "non-scaling-size", "non-rotation", "fixed-position")),
        ("alignment-baseline", false, "auto", null),
        ("baseline-shift", false, "0", null),
        ("text-decoration", false, "none", null),
        ("text-decoration-line", false, "none", null),
        ("text-decoration-color", false, "currentcolor", null),
        ("text-decoration-style", false, "solid", null),
        ("text-decoration-fill", false, "currentcolor", null),
        ("text-decoration-stroke", false, "none", null),
        ("unicode-bidi", false, "normal", null),
        ("inline-size", false, "auto", null),
        ("shape-inside", false, "none", null),
        ("shape-subtract", false, "none", null),
        ("shape-padding", false, "0", null),
        ("shape-margin", false, "0", null),
        ("enable-background", false, "accumulate", null),
        ("cx", false, "0", Length),
        ("cy", false, "0", Length),
        ("r", false, "0", Length),
        ("rx", false, "auto", LengthOrAuto),
        ("ry", false, "auto", LengthOrAuto),
        ("x", false, "0", Length),
        ("y", false, "0", Length),
        ("width", false, "auto", LengthOrAuto),
        ("height", false, "auto", LengthOrAuto),
        ("d", false, "none", null),
    ];

    /// <summary>
    /// The attributes that are presentation attributes on every SVG element (SVG 2 section 6.6): every property above except
    /// the geometry ones, the text-layout ones CSS alone sets, and transform, whose attribute has its own syntax.
    /// </summary>
    public static readonly HashSet<string> Presentation = new(StringComparer.Ordinal)
    {
        "alignment-baseline", "baseline-shift", "clip-path", "clip-rule", "color", "color-interpolation", "color-interpolation-filters",
        "color-rendering", "cursor", "direction", "display", "dominant-baseline", "fill", "fill-opacity", "fill-rule", "filter",
        "flood-color", "flood-opacity", "font-family", "font-size", "font-size-adjust", "font-stretch", "font-style", "font-variant",
        "font-weight", "glyph-orientation-horizontal", "glyph-orientation-vertical", "image-rendering", "letter-spacing",
        "lighting-color", "marker-end", "marker-mid", "marker-start", "mask", "mask-type", "opacity", "overflow",
        "paint-order", "pointer-events", "shape-rendering", "stop-color", "stop-opacity", "stroke", "stroke-dasharray",
        "stroke-dashoffset", "stroke-linecap", "stroke-linejoin", "stroke-miterlimit", "stroke-opacity", "stroke-width", "text-anchor",
        "text-decoration", "text-overflow", "text-rendering", "transform-origin", "unicode-bidi", "vector-effect", "visibility",
        "white-space", "word-spacing", "writing-mode", "clip", "enable-background",
        "inline-size", "shape-inside", "shape-subtract", "shape-padding", "shape-margin", "text-orientation",
    };

    /// <summary>Builds the registry: CSS's defaults plus every SVG property, and SVG's <c>marker</c> shorthand.</summary>
    public static CssPropertyRegistry CreateRegistry()
    {
        CssPropertyRegistry registry = CssPropertyRegistry.CreateDefault();
        foreach ((string name, bool inherited, string initial, Func<IReadOnlyList<CssComponentValue>, bool>? validate) in s_properties)
        {
            registry.Register(new CssPropertyDefinition(name, inherited, initial) { Validate = validate });
        }

        registry.RegisterShorthand(new CssShorthandDefinition("marker", ["marker-start", "marker-mid", "marker-end"], value =>
            Reference(value) ? [new CssDeclaration("marker-start", value), new CssDeclaration("marker-mid", value), new CssDeclaration("marker-end", value)] : null));
        return registry;
    }

    private static bool Color(IReadOnlyList<CssComponentValue> v) => CssColor.TryParse(v, out _);

    private static bool Paint(IReadOnlyList<CssComponentValue> v) => SvgStyleValues.TryPaint(v, out _, out bool level4) || level4;

    private static bool Opacity(IReadOnlyList<CssComponentValue> v) => SvgStyleValues.TryOpacity(v, out _);

    private static bool Length(IReadOnlyList<CssComponentValue> v) => SvgStyleValues.TryLength(v, out _);

    private static bool LengthOrAuto(IReadOnlyList<CssComponentValue> v) => SvgStyleValues.IsKeyword(v, "auto") || SvgStyleValues.TryLength(v, out _);

    private static bool NonNegativeLength(IReadOnlyList<CssComponentValue> v) => SvgStyleValues.TryLength(v, out Values.SvgLength l) && l.Value >= 0;

    private static bool Reference(IReadOnlyList<CssComponentValue> v) => SvgStyleValues.TryReference(v, out _);

    private static bool DashArray(IReadOnlyList<CssComponentValue> v) =>
        SvgStyleValues.IsKeyword(v, "none") || (SvgStyleValues.TryLengthList(v, []) && !HasNegative(v));

    private static bool HasNegative(IReadOnlyList<CssComponentValue> v)
    {
        List<Values.SvgLength> lengths = [];
        return SvgStyleValues.TryLengthList(v, lengths) && lengths.Exists(l => l.Value < 0);
    }

    private static bool PaintOrder(IReadOnlyList<CssComponentValue> v) => SvgStyleValues.IsKeyword(v, "normal") || SvgStyleParsing.TryPaintOrder(v, out _);

    private static Func<IReadOnlyList<CssComponentValue>, bool> Keywords(params string[] allowed) =>
        v => SvgStyleValues.Keyword(v) is string k && Array.Exists(allowed, a => a.Equals(k, StringComparison.OrdinalIgnoreCase));
}
