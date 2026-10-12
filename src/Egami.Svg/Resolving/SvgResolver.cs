using System.Numerics;

using Egami.Css;
using Egami.Drawing;
using Egami.Pixels;
using Egami.Svg.Reading;
using Egami.Svg.Styling;
using Egami.Svg.Values;

namespace Egami.Svg.Resolving;

/// <summary>What an element is resolved in: its parent's styles, the viewport lengths resolve against, the context paints.</summary>
internal readonly record struct SvgContext(
    SvgStyle Style,
    CssComputedStyle? Css,
    SvgLengthContext Lengths,
    int Depth,
    SvgContextPaint? ContextPaint,
    bool InClip);

/// <summary>The paints context-fill and context-stroke stand for: those of the shape a marker is on, or of the use element.</summary>
internal sealed record SvgContextPaint(Paint? Fill, Paint? Stroke);

/// <summary>
/// Turns the styled element tree into the render tree (stage A's resolve and simplify steps): units to pixels, percentages
/// against the nearest viewport, <c>use</c> and <c>symbol</c> expanded with depth and cycle checks, paint servers, clip paths,
/// masks and markers linked, shapes as paths. Every element copy counts toward the element limit, so a chain of uses cannot
/// multiply a small file into a huge tree.
/// </summary>
internal sealed partial class SvgResolver
{
    private static readonly HashSet<string> s_neverRendered = new(StringComparer.Ordinal)
    {
        "defs", "symbol", "clipPath", "mask", "marker", "pattern", "linearGradient", "radialGradient", "meshgradient", "filter",
        "style", "script", "title", "desc", "metadata", "font", "font-face", "cursor", "view", "solidcolor", "hatch",
    };

    /// <summary>The elements switch may pick: graphics and containers; unknown elements are passed over, as resvg drops them.</summary>
    private static readonly HashSet<string> s_switchable = new(StringComparer.Ordinal)
    {
        "g", "a", "svg", "use", "switch", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon", "image", "text", "foreignObject",
    };

    /// <summary>The SVG 1.1 feature strings requiredFeatures may name that Egami supports (resvg's list).</summary>
    private static readonly HashSet<string> s_features = new(StringComparer.Ordinal)
    {
        "http://www.w3.org/TR/SVG11/feature#SVGDOM-static", "http://www.w3.org/TR/SVG11/feature#SVG-static",
        "http://www.w3.org/TR/SVG11/feature#CoreAttribute", "http://www.w3.org/TR/SVG11/feature#Structure",
        "http://www.w3.org/TR/SVG11/feature#BasicStructure", "http://www.w3.org/TR/SVG11/feature#ContainerAttribute",
        "http://www.w3.org/TR/SVG11/feature#ConditionalProcessing", "http://www.w3.org/TR/SVG11/feature#Image",
        "http://www.w3.org/TR/SVG11/feature#Style", "http://www.w3.org/TR/SVG11/feature#Shape", "http://www.w3.org/TR/SVG11/feature#Text",
        "http://www.w3.org/TR/SVG11/feature#BasicText", "http://www.w3.org/TR/SVG11/feature#PaintAttribute",
        "http://www.w3.org/TR/SVG11/feature#BasicPaintAttribute", "http://www.w3.org/TR/SVG11/feature#OpacityAttribute",
        "http://www.w3.org/TR/SVG11/feature#GraphicsAttribute", "http://www.w3.org/TR/SVG11/feature#BasicGraphicsAttribute",
        "http://www.w3.org/TR/SVG11/feature#Marker", "http://www.w3.org/TR/SVG11/feature#Gradient",
        "http://www.w3.org/TR/SVG11/feature#Pattern", "http://www.w3.org/TR/SVG11/feature#Clip",
        "http://www.w3.org/TR/SVG11/feature#BasicClip", "http://www.w3.org/TR/SVG11/feature#Mask",
        "http://www.w3.org/TR/SVG11/feature#Filter", "http://www.w3.org/TR/SVG11/feature#BasicFilter",
        "http://www.w3.org/TR/SVG11/feature#XlinkAttribute",
    };

    private readonly SvgDocument _document;
    private readonly SvgLoadOptions _options;
    private SvgReport _report;
    private readonly SvgStyler _styler;
    private readonly SvgGeometry _geometry;
    private readonly SvgLimits _limits;
    private readonly IReadOnlyList<string> _languages;
    private readonly Dictionary<SvgElement, (SvgStyle Style, CssComputedStyle Css)> _original = new(IdentityComparer.Instance);
    private readonly HashSet<SvgElement> _active = new(IdentityComparer.Instance);

    /// <summary>The elements whose filters are being resolved, and whether an feImage named one of them.</summary>
    private readonly HashSet<SvgElement> _filtering = new(IdentityComparer.Instance);
    private bool _filterNamesItsUser;
    private readonly Dictionary<SvgElement, bool> _useCycles = new(IdentityComparer.Instance);
    private int _depth;
    private long _elements;

    public SvgResolver(SvgDocument document, SvgReport report, IReadOnlyList<string>? languages = null)
    {
        _document = document;
        _options = document.Options;
        _report = report;
        _limits = new SvgLimits(_options.EffectiveContext.Limits);
        _languages = languages ?? ["en"];
        Vector2 size = document.Size;
        _styler = new SvgStyler(document, report, new CssMediaContext { Width = size.X, Height = size.Y });
        _geometry = new SvgGeometry(report);
    }

    /// <summary>Resolves the whole document into its root group, in the root's user units scaled to <see cref="SvgDocument.Size"/>.</summary>
    public SvgGroup Resolve()
    {
        SvgElement root = _document.Root;
        Vector2 size = _document.Size;
        RectF? viewBox = _document.ViewBox;
        (SvgStyle style, CssComputedStyle css) = Style(root, SvgStyle.Initial, null);
        if (style.DisplayNone || !Conditions(root))
        {
            return new SvgGroup { ClipRect = new RectF(0, 0, size.X, size.Y) };
        }

        float rootFont = style.FontSize;
        Vector2 viewport = viewBox is RectF vb ? vb.Size : size;
        SvgLengthContext lengths = new(_options.Dpi, style.FontSize, rootFont, viewport.X, viewport.Y, viewport.X, viewport.Y);
        SvgContext context = new(style, css, lengths, 0, null, false);
        List<SvgNode> children = Children(root, context);
        RectF rootBox = new(0, 0, size.X, size.Y);
        Matrix3x2 transform = viewBox is RectF box ? Aspect(root).Transform(box, rootBox) : Matrix3x2.Identity;

        // The viewBox maps the content into the root's box; a transform on the root moves that box, about the box's center
        // unless transform-origin says otherwise (the root keeps CSS's initial origin), as resvg does.
        transform *= RootTransform(root, style, rootBox);
        SvgGroup content = Decorate(new SvgGroup { Children = children, Bounds = Bounds(children) }, root, style, context);
        List<SvgNode> layers = [content];
        if (css.TryGetValue("background-color", out IReadOnlyList<CssComponentValue>? background) &&
            SvgStyleValues.TryColor(background, out CssColor color, out _) && !color.IsCurrentColor && color.A > 0)
        {
            RectF area = viewBox ?? rootBox;
            layers.Insert(0, new SvgPath(Path.Rect(area)) { Fill = Solid(color, 1), Bounds = area });
        }

        SvgGroup mapped = new() { Children = layers, Transform = transform, Bounds = Bounds(layers) };
        return new SvgGroup { Children = [mapped], Bounds = Transformed(mapped), ClipRect = rootBox };
    }

    /// <summary>Resolves one element and what it holds, as if drawn in place, for Draw by id; null when it draws nothing.</summary>
    public SvgNode? ResolveElement(SvgElement element)
    {
        SvgElement? parent = element.Parent;
        (SvgStyle style, CssComputedStyle? css) = parent is null ? (SvgStyle.Initial, null) : OriginalStyle(parent);
        SvgContext context = new(style, css, RootLengths(), 0, null, false);
        if (element.Name is "symbol" && element.IsSvgOrBare(_document))
        {
            // Drawn alone, a symbol's viewport is its viewBox at scale 1, so Draw can fit the viewBox into its box.
            SvgLength side = new(100, SvgUnit.Percent);
            if (element.Attributes["viewBox"] is string vb && SvgViewBox.TryParse(vb, out RectF box) && box.Width > 0 && box.Height > 0)
            {
                return Symbol(element, null, context, new SvgLength(box.Width, SvgUnit.Px), new SvgLength(box.Height, SvgUnit.Px)) is SvgGroup g
                    ? g with { Transform = Matrix3x2.CreateTranslation(box.X, box.Y) * g.Transform }
                    : null;
            }

            return Symbol(element, null, context, side, side);
        }

        return Element(element, context);
    }

    private SvgLengthContext RootLengths()
    {
        Vector2 size = _document.ViewBox?.Size ?? _document.Size;
        return new SvgLengthContext(_options.Dpi, 16, 16, size.X, size.Y, size.X, size.Y);
    }

    /// <summary>Resolves the rendered children of an element, in order.</summary>
    private List<SvgNode> Children(SvgElement parent, in SvgContext context)
    {
        List<SvgNode> nodes = [];
        foreach (SvgContent item in parent.Children)
        {
            if (item is SvgElement child && Element(child, context) is SvgNode node)
            {
                nodes.Add(node);
            }
        }

        return nodes;
    }

    /// <summary>Resolves one element in a context, or null when it draws nothing.</summary>
    private SvgNode? Element(SvgElement element, in SvgContext context)
    {
        if (!element.IsSvgOrBare(_document))
        {
            return null;
        }

        if (++_elements > _limits.MaxElements)
        {
            throw new SvgException(element.Line, element.Column, $"The document expands to more than {_limits.MaxElements:N0} elements (use copies count).");
        }

        if (s_neverRendered.Contains(element.Name))
        {
            return null;
        }

        if (!Conditions(element))
        {
            return null;
        }

        (SvgStyle style, CssComputedStyle css) = Style(element, context.Style, context.Css);
        if (style.DisplayNone)
        {
            return null;
        }

        SvgContext inner = context with
        {
            Style = style,
            Css = css,
            Lengths = context.Lengths with { FontSize = style.FontSize },
        };

        switch (element.Name)
        {
            case "g":
            case "a":
                return Group(element, style, inner);
            case "switch":
                return Switch(element, style, inner);
            case "svg":
                return NestedSvg(element, style, inner);
            case "use":
                return Use(element, style, inner);
            case "path":
            case "rect":
            case "circle":
            case "ellipse":
            case "line":
            case "polyline":
            case "polygon":
                return Shape(element, style, inner);
            case "image":
                return Image(element, style, inner);
            case "text":
                return Text(element, style, inner);
            case "foreignObject":
                _report.Note("Svg.Unsupported", "foreignObject holds content for an HTML engine; it is skipped.", element);
                return null;
            default:
                return null;
        }
    }

    private SvgGroup? Group(SvgElement element, SvgStyle style, in SvgContext context)
    {
        List<SvgNode> children = Children(element, IntoElement(context, element, style));
        if (children.Count == 0 && style.Filter is null)
        {
            return null;
        }

        RectF bounds = Bounds(children);
        return Decorate(new SvgGroup { Children = children, Bounds = bounds, Transform = ElementTransform(element, style, bounds, context.Lengths) }, element, style, context);
    }

    private SvgGroup? Switch(SvgElement element, SvgStyle style, in SvgContext context)
    {
        foreach (SvgElement child in element.Elements())
        {
            if (!child.IsSvgOrBare(_document) || !s_switchable.Contains(child.Name) || !Conditions(child))
            {
                continue;
            }

            SvgNode? node = Element(child, IntoElement(context, element, style));
            if (node is null)
            {
                return null;
            }

            RectF bounds = Transformed(node);
            return Decorate(new SvgGroup { Children = [node], Bounds = bounds, Transform = ElementTransform(element, style, bounds, context.Lengths) }, element, style, context);
        }

        return null;
    }

    /// <summary>A nested svg: a new viewport at x, y of width by height, the viewBox mapped into it, clipped unless overflow is visible.</summary>
    private SvgGroup? NestedSvg(SvgElement element, SvgStyle style, in SvgContext context) =>
        Viewport(element, element, style, context, null, null, isSymbol: false);

    private SvgGroup? Viewport(SvgElement element, SvgElement content, SvgStyle style, in SvgContext context, SvgLength? useWidth, SvgLength? useHeight, bool isSymbol)
    {
        SvgLengthContext lengths = context.Lengths;
        float x = isSymbol ? 0 : _geometry.Length(element, "x", lengths, SvgAxis.X, 0);
        float y = isSymbol ? 0 : _geometry.Length(element, "y", lengths, SvgAxis.Y, 0);
        SvgLength full = new(100, SvgUnit.Percent);
        SvgLength wl = useWidth ?? (_geometry.Read(content, "width", out SvgLength w) ? w : full);
        SvgLength hl = useHeight ?? (_geometry.Read(content, "height", out SvgLength h) ? h : full);
        float width = wl.ToPixels(lengths, SvgAxis.X);
        float height = hl.ToPixels(lengths, SvgAxis.Y);
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        RectF viewport = new(0, 0, width, height);
        Matrix3x2 viewBoxTransform = Matrix3x2.Identity;
        Vector2 inner = new(width, height);
        if (content.Attributes["viewBox"] is string vbText)
        {
            if (SvgViewBox.TryParse(vbText, out RectF viewBox))
            {
                if (viewBox.Width <= 0 || viewBox.Height <= 0)
                {
                    return null;
                }

                viewBoxTransform = Aspect(content).Transform(viewBox, viewport);
                inner = viewBox.Size;
            }
            else
            {
                _report.Warn("Svg.BadAttribute", $"The {content.Name}'s viewBox \"{SvgGeometry.Clip(vbText)}\" cannot be read; it is ignored.", content);
            }
        }

        SvgContext children = context with { Lengths = lengths with { ViewportWidth = inner.X, ViewportHeight = inner.Y } };
        if (Movable(children))
        {
            Matrix3x2 outer = Matrix3x2.CreateTranslation(x, y) * (isSymbol ? Matrix3x2.Identity : ElementTransform(element, style, RectF.Empty, context.Lengths));
            children = Into(Into(children, outer), viewBoxTransform);
        }
        List<SvgNode> nodes = Children(content, children);
        if (nodes.Count == 0)
        {
            return null;
        }

        SvgGroup scaled = new() { Children = nodes, Transform = viewBoxTransform, Bounds = Bounds(nodes) };
        RectF bounds = Transformed(scaled);
        Matrix3x2 transform = Matrix3x2.CreateTranslation(x, y) * (isSymbol ? Matrix3x2.Identity : ElementTransform(element, style, bounds, context.Lengths));
        SvgGroup group = new()
        {
            Children = [scaled],
            Transform = transform,
            Bounds = bounds,
            ClipRect = style.OverflowVisible ? null : viewport,
        };
        return Decorate(group, element, style, context);
    }

    /// <summary>A use: the referenced element copied in place, inheriting from the use, moved by x and y.</summary>
    private SvgGroup? Use(SvgElement element, SvgStyle style, in SvgContext context)
    {
        SvgElement? target = Reference(element, "use");
        if (target is null)
        {
            return null;
        }

        if (!Enter(target, element, isUse: true))
        {
            return null;
        }

        try
        {
            float x = _geometry.Length(element, "x", context.Lengths, SvgAxis.X, 0);
            float y = _geometry.Length(element, "y", context.Lengths, SvgAxis.Y, 0);
            SvgLength? width = _geometry.Read(element, "width", out SvgLength w) ? w : null;
            SvgLength? height = _geometry.Read(element, "height", out SvgLength h) ? h : null;
            SvgContext inner = context with { Depth = context.Depth + 1, ContextPaint = ContextPaints(style, context, null, Matrix3x2.Identity) };
            SvgNode? node;
            if (style.Fill.Kind == SvgPaintKind.Server || style.Stroke.Kind == SvgPaintKind.Server)
            {
                // context-fill and context-stroke take the use's gradient or pattern over the use's bounding box, which is known
                // only once the copy is resolved: a first, silent pass measures it.
                long elements = _elements;
                SvgReport report = _report;
                _report = new SvgReport(strict: false);
                try
                {
                    node = UseContent(target, style, inner, context, width, height);
                }
                finally
                {
                    _report = report;
                    _elements = elements;
                }

                if (node is not null)
                {
                    RectF measured = Transformed(node).Offset(new Vector2(x, y));
                    inner = inner with { ContextPaint = ContextPaints(style, context, measured, Matrix3x2.CreateTranslation(-x, -y)) };
                }
            }

            node = UseContent(target, style, inner, context, width, height);
            if (node is null)
            {
                return null;
            }

            RectF bounds = Transformed(node);
            Matrix3x2 transform = Matrix3x2.CreateTranslation(x, y) * ElementTransform(element, style, bounds, context.Lengths);
            return Decorate(new SvgGroup { Children = [node], Transform = transform, Bounds = bounds }, element, style, context);
        }
        finally
        {
            Leave(target, isUse: true);
        }
    }

    private SvgNode? UseContent(SvgElement target, SvgStyle style, in SvgContext inner, in SvgContext context, SvgLength? width, SvgLength? height)
    {
        if (target.Name == "symbol" && target.IsSvgOrBare(_document))
        {
            return Symbol(target, null, inner, width, height);
        }

        if (target.Name == "svg" && target.IsSvgOrBare(_document))
        {
            (SvgStyle ts, CssComputedStyle tc) = Style(target, style, context.Css);
            return ts.DisplayNone ? null : Viewport(target, target, ts, inner with { Style = ts, Css = tc }, width, height, isSymbol: false);
        }

        return Element(target, inner);
    }

    /// <summary>A symbol instance: a viewport sized by the use (or the symbol's own width and height, or 100%).</summary>
    private SvgGroup? Symbol(SvgElement symbol, SvgElement? use, in SvgContext context, SvgLength? width, SvgLength? height)
    {
        (SvgStyle style, CssComputedStyle css) = Style(symbol, context.Style, context.Css);
        if (style.DisplayNone)
        {
            return null;
        }

        return Viewport(symbol, symbol, style, context with { Style = style, Css = css }, width, height, isSymbol: true);
    }

    /// <summary>The element a reference attribute (href) names, reported when missing.</summary>
    private SvgElement? Reference(SvgElement element, string what)
    {
        string? href = element.Attributes["href"] ?? element.Attributes.Get("href", SvgNamespaces.XLink);
        if (href is null)
        {
            return null;
        }

        string? id = SvgStyleValues.LocalId(href);
        SvgElement? target = id is null ? null : _document.FindById(id);
        if (target is null)
        {
            _report.Warn("Svg.BadReference", $"The {what}'s href \"{SvgGeometry.Clip(href)}\" names no element in this document; it draws nothing.", element);
        }

        return target;
    }

    /// <summary>
    /// Marks a marker, pattern, clip path or mask as being expanded; false (reported) when it already is (a reference cycle) or
    /// references nest too deep. A use is checked statically instead (<see cref="UseCycles"/>), so one element may be used again
    /// inside a marker of its own copy, as resvg allows.
    /// </summary>
    private bool Enter(SvgElement target, SvgElement from, bool isUse = false)
    {
        if (_depth >= SvgLimits.MaxReferenceDepth)
        {
            _report.Warn("Svg.BadReference", $"References nest more than {SvgLimits.MaxReferenceDepth} deep at <{from.Name}>; the rest is not drawn.", from);
            return false;
        }

        if (isUse)
        {
            if (UseCycles(target, from))
            {
                _report.Warn("Svg.BadReference", $"The <{from.Name}> refers to an element that holds it, or uses lead back to it; the reference is not drawn.", from);
                return false;
            }
        }
        else if (!_active.Add(target))
        {
            _report.Warn("Svg.BadReference", $"The <{from.Name}> is part of a reference cycle; it is not drawn.", from);
            return false;
        }

        _depth++;
        return true;
    }

    private void Leave(SvgElement target, bool isUse = false)
    {
        _depth--;
        if (!isUse)
        {
            _active.Remove(target);
        }
    }

    /// <summary>
    /// Whether using <paramref name="target"/> from <paramref name="use"/> loops: the target holds the use, or a use inside the
    /// target leads, through other uses, back to the target or one of its ancestors.
    /// </summary>
    private bool UseCycles(SvgElement target, SvgElement use)
    {
        for (SvgElement? e = use; e is not null; e = e.Parent)
        {
            if (ReferenceEquals(e, target))
            {
                return true;
            }
        }

        if (_useCycles.TryGetValue(target, out bool known))
        {
            return known;
        }

        bool cycle = false;
        HashSet<SvgElement> seen = new(IdentityComparer.Instance) { target };
        Stack<SvgElement> pending = new();
        pending.Push(target);
        while (pending.Count > 0 && !cycle)
        {
            SvgElement current = pending.Pop();
            foreach (SvgElement inner in current.Descendants().Prepend(current))
            {
                if (inner.Name != "use" || !inner.IsSvgOrBare(_document))
                {
                    continue;
                }

                string? href = inner.Attributes["href"] ?? inner.Attributes.Get("href", SvgNamespaces.XLink);
                if (href is null || SvgStyleValues.LocalId(href) is not string id || _document.FindById(id) is not SvgElement next)
                {
                    continue;
                }

                for (SvgElement? e = target; e is not null; e = e.Parent)
                {
                    if (ReferenceEquals(e, next))
                    {
                        cycle = true;
                    }
                }

                if (!cycle && seen.Add(next))
                {
                    pending.Push(next);
                }
            }
        }

        _useCycles[target] = cycle;
        return cycle;
    }

    /// <summary>An element's computed styles where its parent's are given.</summary>
    private (SvgStyle Style, CssComputedStyle Css) Style(SvgElement element, SvgStyle parent, CssComputedStyle? parentCss)
    {
        CssComputedStyle css = _styler.Computed(element, parentCss);
        return (SvgStyle.Compute(css, parent, 16, _options.Dpi, element, _report), css);
    }

    /// <summary>An element's styles inheriting along its own ancestors, as paint servers, clip paths, masks and markers use them.</summary>
    private (SvgStyle Style, CssComputedStyle Css) OriginalStyle(SvgElement element)
    {
        if (_original.TryGetValue(element, out (SvgStyle, CssComputedStyle) cached))
        {
            return cached;
        }

        (SvgStyle style, CssComputedStyle? css) parent = element.Parent is SvgElement p ? OriginalStyle(p) : (SvgStyle.Initial, null);
        (SvgStyle, CssComputedStyle) result = Style(element, parent.style, parent.css);
        _original[element] = result;
        return result;
    }

    /// <summary>The conditional processing attributes (SVG 2 section 5.8): requiredExtensions, systemLanguage, requiredFeatures.</summary>
    private bool Conditions(SvgElement element)
    {
        // Egami supports no extensions, so any requiredExtensions, even an empty one, fails, as in browsers.
        if (element.Attributes["requiredExtensions"] is not null)
        {
            return false;
        }

        if (element.Attributes["requiredFeatures"] is string features)
        {
            foreach (string feature in features.Split(' '))
            {
                if (!s_features.Contains(feature))
                {
                    return false;
                }
            }
        }

        if (element.Attributes["systemLanguage"] is string languages)
        {
            bool any = false;
            foreach (string language in languages.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (string wanted in _languages)
                {
                    if (LanguageMatches(language, wanted))
                    {
                        any = true;
                    }
                }
            }

            if (!any)
            {
                return false;
            }
        }

        return true;
    }

    private static bool LanguageMatches(string tag, string wanted) =>
        tag.Equals(wanted, StringComparison.OrdinalIgnoreCase) ||
        (wanted.Length > tag.Length && wanted.StartsWith(tag, StringComparison.OrdinalIgnoreCase) && wanted[tag.Length] == '-') ||
        (tag.Length > wanted.Length && tag.StartsWith(wanted, StringComparison.OrdinalIgnoreCase) && tag[wanted.Length] == '-');

    private SvgAspectRatio Aspect(SvgElement element)
    {
        if (element.Attributes["preserveAspectRatio"] is string text)
        {
            if (SvgAspectRatio.TryParse(text, out SvgAspectRatio aspect))
            {
                return aspect;
            }

            _report.Warn("Svg.BadAttribute", $"The {element.Name}'s preserveAspectRatio \"{SvgGeometry.Clip(text)}\" cannot be read; xMidYMid meet is used.", element);
        }

        return SvgAspectRatio.Default;
    }

    private Matrix3x2 RootTransform(SvgElement root, SvgStyle style, RectF box)
    {
        if (root.Attributes["transform"] is not string text)
        {
            return Matrix3x2.Identity;
        }

        if (!SvgTransforms.TryParse(text, out Matrix3x2 transform))
        {
            _report.Warn("Svg.BadAttribute", $"The root's transform \"{SvgGeometry.Clip(text)}\" cannot be read; it is ignored.", root);
            return Matrix3x2.Identity;
        }

        Vector2 origin = box.Center;
        if (style.TransformOrigin is IReadOnlyList<CssComponentValue> o && SvgCssTransforms.TryOrigin(o, box, out Vector2 set))
        {
            origin = set;
        }

        return Matrix3x2.CreateTranslation(-origin) * transform * Matrix3x2.CreateTranslation(origin);
    }

    /// <summary>
    /// The element's transform: the CSS transform property when a style sheet sets it, else the transform attribute, turned
    /// about transform-origin in the box transform-box picks (the nearest viewport by default, as SVG 2's UA sheet says).
    /// </summary>
    private Matrix3x2 ElementTransform(SvgElement element, SvgStyle style, RectF bounds, in SvgLengthContext lengths)
    {
        RectF box = style.TransformBox is "fill-box" or "content-box" or "stroke-box" or "border-box" ? bounds : new RectF(0, 0, lengths.ViewportWidth, lengths.ViewportHeight);
        Matrix3x2 transform = Matrix3x2.Identity;
        string attribute = element.Name is "linearGradient" or "radialGradient" ? "gradientTransform" : element.Name == "pattern" ? "patternTransform" : "transform";
        if (style.Transform is IReadOnlyList<CssComponentValue> css && style.Css?.IsSetOnElement("transform") == true && SvgCssTransforms.TryResolve(css, box, out Matrix3x2 m))
        {
            transform = m;
        }
        else if (element.Attributes[attribute] is string text)
        {
            if (!SvgTransforms.TryParse(text, out transform))
            {
                _report.Warn("Svg.BadAttribute", $"The {element.Name}'s {attribute} \"{SvgGeometry.Clip(text)}\" cannot be read; it is ignored.", element);
                transform = Matrix3x2.Identity;
            }
        }

        if (!transform.IsIdentity && style.TransformOrigin is IReadOnlyList<CssComponentValue> origin && SvgCssTransforms.TryOrigin(origin, box, out Vector2 o) && o != Vector2.Zero)
        {
            transform = Matrix3x2.CreateTranslation(-o) * transform * Matrix3x2.CreateTranslation(o);
        }

        return transform;
    }

    /// <summary>Adds what every rendered element may carry: id, classes, opacity, blending, isolation, clip path, mask and filter.</summary>
    private TNode Decorate<TNode>(TNode node, SvgElement element, SvgStyle style, in SvgContext context)
        where TNode : SvgNode
    {
        RectF bounds = node.Bounds;
        bool hidden = false;
        SvgClip? clip = style.ClipPath is string clipUrl && !context.InClip ? ClipPath(element, clipUrl, bounds, context, ref hidden) : null;
        SvgMask? mask = style.Mask is string maskUrl && !context.InClip ? Mask(element, maskUrl, bounds, context, ref hidden) : null;
        SvgFilter? filter = style.Filter is IReadOnlyList<CssComponentValue> filterValue && !context.InClip ? Filter(element, filterValue, style, bounds, context, ref hidden) : null;
        if (hidden)
        {
            return node with { Opacity = 0 };
        }

        string? classes = element.Attributes["class"];
        return node with
        {
            Element = element,
            Id = element.Id,
            Classes = classes is null ? [] : classes.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries),
            Opacity = context.InClip ? 1 : style.Opacity,
            BlendMode = context.InClip ? Operations.BlendMode.SourceOver : style.BlendMode,
            Isolated = !context.InClip && style.Isolate,
            Clip = clip,
            Mask = mask,
            Filter = filter,
        };
    }

    /// <summary>The union of nodes' bounds in their parent's coordinates.</summary>
    internal static RectF Bounds(IReadOnlyList<SvgNode> nodes)
    {
        RectF bounds = RectF.Empty;
        bool any = false;
        foreach (SvgNode node in nodes)
        {
            RectF b = Transformed(node);
            if (b.Width <= 0 && b.Height <= 0 && b.X == 0 && b.Y == 0)
            {
                continue;
            }

            bounds = any ? bounds.Union(b) : b;
            any = true;
        }

        return bounds;
    }

    /// <summary>A node's bounds in its parent's coordinates.</summary>
    internal static RectF Transformed(SvgNode node)
    {
        RectF b = node.Bounds;
        if (node.Transform.IsIdentity)
        {
            return b;
        }

        Span<Vector2> corners =
        [
            Vector2.Transform(new Vector2(b.Left, b.Top), node.Transform),
            Vector2.Transform(new Vector2(b.Right, b.Top), node.Transform),
            Vector2.Transform(new Vector2(b.Left, b.Bottom), node.Transform),
            Vector2.Transform(new Vector2(b.Right, b.Bottom), node.Transform),
        ];
        return RectF.FromPoints(corners);
    }

    /// <summary>
    /// The context for content in a coordinate system <paramref name="transform"/> maps into the current one: context-fill and
    /// context-stroke gradients and patterns, defined in the context element's space, are moved into the new one.
    /// </summary>
    private static SvgContext Into(in SvgContext context, Matrix3x2 transform)
    {
        if (!Movable(context) || transform.IsIdentity || !Matrix3x2.Invert(transform, out Matrix3x2 inverse))
        {
            return context;
        }

        SvgContextPaint paints = context.ContextPaint!;

        static Paint? Move(Paint? paint, Matrix3x2 inverse) => paint is null or SolidPaint ? paint : paint with { LocalMatrix = paint.LocalMatrix * inverse };
        return context with { ContextPaint = new SvgContextPaint(Move(paints.Fill, inverse), Move(paints.Stroke, inverse)) };
    }

    /// <summary>Whether the context holds a gradient or pattern context paint that coordinate changes move.</summary>
    private static bool Movable(in SvgContext context) =>
        context.ContextPaint is SvgContextPaint p && (p.Fill is not (null or SolidPaint) || p.Stroke is not (null or SolidPaint));

    /// <summary>The context for an element's children, its transform applied to context paints when there are any to move.</summary>
    private SvgContext IntoElement(in SvgContext context, SvgElement element, SvgStyle style) =>
        Movable(context) ? Into(context, ElementTransform(element, style, RectF.Empty, context.Lengths)) : context;

    /// <summary>A computed color with an opacity as a solid paint.</summary>
    internal static SolidPaint Solid(CssColor color, float opacity) => new(new RgbaF32(color.R, color.G, color.B, color.A * opacity));

    /// <summary>The element whose computed style <see cref="GeometryFromStyle"/> reads, set while its shape is built.</summary>
    private (SvgElement? Element, CssComputedStyle? Css) _geometryStyle;

    /// <summary>A geometry property set on the element by a style sheet or style attribute (presentation attributes are read as attributes).</summary>
    private string? GeometryFromStyle(SvgElement element, string name)
    {
        if (!ReferenceEquals(element, _geometryStyle.Element) || _geometryStyle.Css is not CssComputedStyle css || !css.IsSetOnElement(name))
        {
            return null;
        }

        return css.GetValueText(name);
    }
}
