using System.Numerics;

using Egami.Css;
using Egami.Drawing;
using Egami.Operations;
using Egami.Pixels;
using Egami.Svg.Filters;
using Egami.Svg.Styling;
using Egami.Svg.Values;

namespace Egami.Svg.Resolving;

/// <summary>
/// Filters (stage D, question 14): the filter property's url() references to filter elements and its CSS filter functions, each
/// resolved into a graph of primitives with every length in the filtered element's user units. The rules for regions, units,
/// inputs and defaults follow SVG 1.1 section 15 and Filter Effects 1 as resvg's usvg/src/parser/filter.rs reads them
/// (Apache-2.0 or MIT; see THIRD-PARTY-NOTICES).
/// </summary>
internal sealed partial class SvgResolver
{
    private static readonly HashSet<string> s_primitives = new(StringComparer.Ordinal)
    {
        "feBlend", "feColorMatrix", "feComponentTransfer", "feComposite", "feConvolveMatrix", "feDiffuseLighting", "feDisplacementMap",
        "feDropShadow", "feFlood", "feGaussianBlur", "feImage", "feMerge", "feMorphology", "feOffset", "feSpecularLighting", "feTile",
        "feTurbulence",
    };

    /// <summary>
    /// The filter of an element with these bounds. A list that cannot be read is ignored (the element draws unfiltered); a
    /// list whose references all name nothing usable sets <paramref name="hidden"/>, as resvg and browsers draw nothing then.
    /// </summary>
    private SvgFilter? Filter(SvgElement element, IReadOnlyList<CssComponentValue> value, SvgStyle style, RectF bounds, in SvgContext context, ref bool hidden)
    {
        List<SvgFilterGraph> graphs = [];
        bool invalidUrl = false;
        RectF? box = bounds.Width > 0 && bounds.Height > 0 ? bounds : null;
        foreach (CssComponentValue item in value)
        {
            if (item is CssTokenValue { Token.Kind: CssTokenKind.Whitespace })
            {
                continue;
            }

            if (CssUrl.TryParse(item, out CssUrl url))
            {
                SvgElement? target = SvgStyleValues.LocalId(url.Url) is string id ? _document.FindById(id) : null;
                SvgFilterGraph? graph = target is not null && target.Name == "filter" && target.IsSvgOrBare(_document) ? FilterElement(target, element, box, context) : null;
                if (graph is null)
                {
                    invalidUrl = true;
                    _report.Warn("Svg.BadReference", $"The filter \"url({SvgGeometry.Clip(url.Url)})\" names no usable filter.", element);
                }
                else
                {
                    graphs.Add(graph);
                }

                continue;
            }

            if (item is not CssFunction function || !TryFilterFunction(function, style, context, out SvgPrimitive? primitive, out bool largeRegion))
            {
                _report.Warn("Svg.BadStyle", $"The {element.Name}'s filter list cannot be read; the element is drawn unfiltered.", element);
                return null;
            }

            if (box is not RectF b)
            {
                continue;
            }

            // Filter functions have no region; resvg uses a generous one around the bounding box.
            RectF region = largeRegion
                ? new RectF(b.X - (0.5f * b.Width), b.Y - (0.5f * b.Height), 2 * b.Width, 2 * b.Height)
                : new RectF(b.X - (0.1f * b.Width), b.Y - (0.1f * b.Height), 1.2f * b.Width, 1.2f * b.Height);
            graphs.Add(new SvgFilterGraph(region, [primitive! with { Region = region }]));
        }

        if (graphs.Count == 0)
        {
            hidden |= invalidUrl;
            return null;
        }

        RectF union = graphs[0].Region;
        foreach (SvgFilterGraph g in graphs)
        {
            union = union.Union(g.Region);
        }

        return new SvgFilter { Region = union, Graphs = graphs };
    }

    /// <summary>A filter element: its region, then its primitives (from the first filter of its href chain that has any).</summary>
    private SvgFilterGraph? FilterElement(SvgElement filter, SvgElement user, RectF? box, in SvgContext context)
    {
        List<SvgElement> chain = Chain(filter, "filter");
        bool boundingBox = Inherited(chain, "filterUnits") is not "userSpaceOnUse";
        bool primitiveBox = Inherited(chain, "primitiveUnits") == "objectBoundingBox";
        if ((boundingBox || primitiveBox) && box is null)
        {
            return null;
        }

        SvgLengthContext lengths = context.Lengths;
        RectF b = box ?? default;
        float Region(string name, string fallback, SvgAxis axis)
        {
            if (!(Inherited(chain, name) is string text && SvgLength.TryParse(text, out SvgLength l)))
            {
                _ = SvgLength.TryParse(fallback, out l);
            }

            if (!boundingBox)
            {
                return l.ToPixels(lengths, axis);
            }

            float f = l.ToFraction();
            return name switch
            {
                "x" => b.X + (f * b.Width),
                "y" => b.Y + (f * b.Height),
                "width" => f * b.Width,
                _ => f * b.Height,
            };
        }

        RectF region = new(Region("x", "-10%", SvgAxis.X), Region("y", "-10%", SvgAxis.Y), Region("width", "120%", SvgAxis.X), Region("height", "120%", SvgAxis.Y));
        if (!(region.Width > 0 && region.Height > 0))
        {
            return null;
        }

        SvgElement? owner = chain.Find(e => e.Elements().Any());
        if (owner is null || !Enter(filter, user))
        {
            return null;
        }

        try
        {
            List<SvgPrimitive> primitives = [];
            Dictionary<string, int> results = new(StringComparer.Ordinal);
            Vector2 scale = primitiveBox ? b.Size : Vector2.One;
            foreach (SvgElement child in owner.Elements())
            {
                if (!child.IsSvgOrBare(_document) || !s_primitives.Contains(child.Name))
                {
                    continue;
                }

                RectF? sub = Subregion(child, primitiveBox, region, b, lengths);
                if (sub is not RectF subregion)
                {
                    break;
                }

                SvgStyle style = OriginalStyle(child).Style;
                SvgPrimitive? primitive = Primitive(child, subregion, style.LinearRgbFilters, style, scale, primitiveBox, b, primitives, results, context);
                if (primitive is null)
                {
                    continue;
                }

                if (child.Attributes["result"] is string result && result.Length > 0)
                {
                    results[result] = primitives.Count;
                }

                primitives.Add(primitive);
            }

            return primitives.Count == 0 ? null : new SvgFilterGraph(region, primitives);
        }
        finally
        {
            Leave(filter);
        }
    }

    /// <summary>A primitive's subregion: its x, y, width and height, each defaulting to the filter region's (resvg's rule).</summary>
    private static RectF? Subregion(SvgElement fe, bool primitiveBox, RectF region, RectF box, SvgLengthContext lengths)
    {
        float? Read(string name, SvgAxis axis)
        {
            if (fe.Attributes[name] is not string text || !SvgLength.TryParse(text, out SvgLength l))
            {
                return null;
            }

            return primitiveBox ? l.ToFraction() : l.ToPixels(lengths, axis);
        }

        float? x = Read("x", SvgAxis.X), y = Read("y", SvgAxis.Y), w = Read("width", SvgAxis.X), h = Read("height", SvgAxis.Y);
        if (fe.Name is "feFlood" or "feImage" && primitiveBox)
        {
            RectF r = new(x ?? 0, y ?? 0, w ?? 1, h ?? 1);
            return r.Width > 0 && r.Height > 0 ? new RectF(box.X + (r.X * box.Width), box.Y + (r.Y * box.Height), r.Width * box.Width, r.Height * box.Height) : null;
        }

        if (primitiveBox)
        {
            RectF fractions = new(x ?? 0, y ?? 0, w ?? 1, h ?? 1);
            if (!(fractions.Width > 0 && fractions.Height > 0))
            {
                return null;
            }

            return new RectF(region.X + (fractions.X * region.Width), region.Y + (fractions.Y * region.Height), fractions.Width * region.Width, fractions.Height * region.Height);
        }

        RectF result = new(x ?? region.X, y ?? region.Y, w ?? region.Width, h ?? region.Height);
        return result.Width > 0 && result.Height > 0 ? result : null;
    }

    /// <summary>A primitive's input: in or in2 naming SourceGraphic, SourceAlpha or an earlier result; unknown names fall back to the previous result.</summary>
    private static SvgFilterInput Input(SvgElement fe, string attribute, List<SvgPrimitive> primitives, Dictionary<string, int> results)
    {
        string? text = fe.Attributes[attribute]?.Trim();
        switch (text)
        {
            case null:
                break;
            case "SourceGraphic":
                return SvgFilterInput.Graphic;
            case "SourceAlpha":
                return new SvgFilterInput(SvgFilterSource.SourceAlpha);
            case "BackgroundImage" or "BackgroundAlpha" or "FillPaint" or "StrokePaint":
                // Not supported by resvg or browsers; SourceGraphic stands in, as resvg does.
                return SvgFilterInput.Graphic;
            default:
                if (results.TryGetValue(text, out int index))
                {
                    return new SvgFilterInput(SvgFilterSource.Result, index);
                }

                break;
        }

        return primitives.Count == 0 ? SvgFilterInput.Graphic : new SvgFilterInput(SvgFilterSource.Result, primitives.Count - 1);
    }

    private SvgPrimitive? Primitive(
        SvgElement fe, RectF region, bool linear, SvgStyle style, Vector2 scale, bool primitiveBox, RectF box, List<SvgPrimitive> primitives,
        Dictionary<string, int> results, in SvgContext context)
    {
        SvgFilterInput In() => Input(fe, "in", primitives, results);
        SvgFilterInput In2() => Input(fe, "in2", primitives, results);
        float Number(string name, float fallback) => fe.Attributes[name] is string t && SvgNumbers.TryParse(t, out double v) ? (float)v : fallback;
        List<double>? List(string name)
        {
            if (fe.Attributes[name] is not string text)
            {
                return null;
            }

            List<double> values = [];
            return SvgNumbers.TryParseList(text, values) ? values : [];
        }

        switch (fe.Name)
        {
            case "feGaussianBlur":
            {
                (float sx, float sy) = StdDeviation(fe, scale, 0);
                return new SvgBlur(region, linear, In(), sx, sy);
            }

            case "feOffset":
                return new SvgOffset(region, linear, In(), Number("dx", 0) * scale.X, Number("dy", 0) * scale.Y);
            case "feDropShadow":
            {
                (float sx, float sy) = StdDeviation(fe, scale, 2);
                RgbaF32 color = Color(style.FloodColor, style.FloodOpacity);
                return new SvgDropShadow(region, linear, In(), Number("dx", 2) * scale.X, Number("dy", 2) * scale.Y, sx, sy, color);
            }

            case "feFlood":
                return new SvgFlood(region, linear, Color(style.FloodColor, style.FloodOpacity));
            case "feMerge":
            {
                List<SvgFilterInput> inputs = [];
                foreach (SvgElement node in fe.Elements("feMergeNode"))
                {
                    inputs.Add(Input(node, "in", primitives, results));
                }

                return new SvgMerge(region, linear, inputs);
            }

            case "feBlend":
            {
                string mode = fe.Attributes["mode"]?.Trim() ?? "normal";
                return new SvgBlend(region, linear, In(), In2(), SvgStyleParsing.TryBlendMode(mode, out BlendMode m) ? m : BlendMode.SourceOver);
            }

            case "feComposite":
            {
                SvgCompositeOperator op = fe.Attributes["operator"]?.Trim() switch
                {
                    "in" => SvgCompositeOperator.In,
                    "out" => SvgCompositeOperator.Out,
                    "atop" => SvgCompositeOperator.Atop,
                    "xor" => SvgCompositeOperator.Xor,
                    "lighter" => SvgCompositeOperator.Lighter,
                    "arithmetic" => SvgCompositeOperator.Arithmetic,
                    _ => SvgCompositeOperator.Over,
                };
                return new SvgComposite(region, linear, In(), In2(), op, new Vector4(Number("k1", 0), Number("k2", 0), Number("k3", 0), Number("k4", 0)));
            }

            case "feColorMatrix":
                return new SvgColorMatrix(region, linear, In(), ColorMatrixValues(fe.Attributes["type"]?.Trim(), List("values")));
            case "feComponentTransfer":
            {
                SvgTransferFunction r = SvgTransferFunction.Identity, g = r, b = r, a = r;
                foreach (SvgElement func in fe.Elements())
                {
                    SvgTransferFunction? f = Transfer(func);
                    switch (func.Name)
                    {
                        case "feFuncR" when f is not null: r = f; break;
                        case "feFuncG" when f is not null: g = f; break;
                        case "feFuncB" when f is not null: b = f; break;
                        case "feFuncA" when f is not null: a = f; break;
                    }
                }

                return new SvgComponentTransfer(region, linear, In(), r, g, b, a);
            }

            case "feConvolveMatrix":
                return (SvgPrimitive?)Convolve(fe, region, linear, In(), List) ?? Dummy(region, linear);
            case "feMorphology":
            {
                bool dilate = fe.Attributes["operator"]?.Trim() == "dilate";
                float rx = 1, ry = 1;
                if (List("radius") is List<double> radius)
                {
                    (double x, double y) = radius.Count switch { 1 => (radius[0], radius[0]), 2 => (radius[0], radius[1]), _ => (0d, 0d) };
                    if (x == 0 && y == 0)
                    {
                        (x, y) = (1, 1);
                    }
                    else if (x == 0)
                    {
                        x = 1;
                    }
                    else if (y == 0)
                    {
                        y = 1;
                    }

                    if (x > 0 && y > 0)
                    {
                        (rx, ry) = ((float)x, (float)y);
                    }
                }

                return new SvgMorphology(region, linear, In(), dilate, rx * scale.X, ry * scale.Y);
            }

            case "feDiffuseLighting":
            case "feSpecularLighting":
            {
                bool specular = fe.Name == "feSpecularLighting";
                float exponent = Number("specularExponent", 1);
                if (specular && exponent is < 1 or > 128)
                {
                    return Dummy(region, linear);
                }

                SvgLight? light = Light(fe);
                if (light is null)
                {
                    return Dummy(region, linear);
                }

                RgbaF32 color = Color(style.LightingColor, 1);
                return new SvgLighting(
                    region, linear, In(), specular, Number("surfaceScale", 1), Number(specular ? "specularConstant" : "diffuseConstant", 1), exponent, color, light);
            }

            case "feDisplacementMap":
            {
                static ColorChannel Channel(string? text) => text?.Trim() switch
                {
                    "R" => ColorChannel.Red,
                    "G" => ColorChannel.Green,
                    "B" => ColorChannel.Blue,
                    _ => ColorChannel.Alpha,
                };

                return new SvgDisplacement(
                    region, linear, In(), In2(), Number("scale", 0) * ((scale.X + scale.Y) / 2), Channel(fe.Attributes["xChannelSelector"]), Channel(fe.Attributes["yChannelSelector"]));
            }

            case "feTurbulence":
            {
                float fx = 0, fy = 0;
                if (List("baseFrequency") is List<double> frequency && frequency.Count is 1 or 2)
                {
                    (double x, double y) = frequency.Count == 2 ? (frequency[0], frequency[1]) : (frequency[0], frequency[0]);
                    if (x >= 0 && y >= 0)
                    {
                        (fx, fy) = ((float)x, (float)y);
                    }
                }

                int octaves = (int)MathF.Round(Math.Max(0, Number("numOctaves", 1)));
                return new SvgTurbulence(
                    region, linear, fx, fy, octaves, (int)MathF.Truncate(Number("seed", 0)), fe.Attributes["stitchTiles"]?.Trim() == "stitch",
                    fe.Attributes["type"]?.Trim() == "fractalNoise");
            }

            case "feTile":
                return new SvgTile(region, linear, In());
            case "feImage":
                return new SvgFeImage(region, linear, FeImage(fe, region, style, context));
            default:
                return null;
        }
    }

    /// <summary>A transparent flood, what a primitive that cannot run gives, as resvg does.</summary>
    private static SvgFlood Dummy(RectF region, bool linear) => new(region, linear, default);

    private static RgbaF32 Color(CssColor color, float opacity) => new(color.R, color.G, color.B, color.A * opacity);

    /// <summary>stdDeviation: one or two non-negative numbers (otherwise the default), times the primitive units' scale.</summary>
    private static (float X, float Y) StdDeviation(SvgElement fe, Vector2 scale, float fallback)
    {
        float x = fallback, y = fallback;
        if (fe.Attributes["stdDeviation"] is string text)
        {
            List<double> values = [];
            if (SvgNumbers.TryParseList(text, values) && values.Count is 1 or 2)
            {
                (x, y) = values.Count == 2 ? ((float)values[0], (float)values[1]) : ((float)values[0], (float)values[0]);
            }
            else
            {
                (x, y) = (0, 0);
            }
        }

        return (Math.Max(0, x * scale.X), Math.Max(0, y * scale.Y));
    }

    /// <summary>feColorMatrix's matrix for its type and values; a matrix whose values cannot be used is the identity.</summary>
    private static float[] ColorMatrixValues(string? type, List<double>? values)
    {
        switch (type)
        {
            case "saturate":
            {
                float s = values is { Count: > 0 } ? Math.Clamp((float)values[0], 0, 1) : 1;
                return Saturate(s);
            }

            case "hueRotate":
                return HueRotate(values is { Count: > 0 } ? (float)values[0] : 0);
            case "luminanceToAlpha":
                return [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.2125f, 0.7154f, 0.0721f, 0, 0];
            default:
                if (values is { Count: 20 })
                {
                    return [.. values.Select(v => (float)v)];
                }

                return [1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0];
        }
    }

    private static float[] Saturate(float s) =>
    [
        0.213f + (0.787f * s), 0.715f - (0.715f * s), 0.072f - (0.072f * s), 0, 0,
        0.213f - (0.213f * s), 0.715f + (0.285f * s), 0.072f - (0.072f * s), 0, 0,
        0.213f - (0.213f * s), 0.715f - (0.715f * s), 0.072f + (0.928f * s), 0, 0,
        0, 0, 0, 1, 0,
    ];

    private static float[] HueRotate(float degrees)
    {
        float a = degrees * MathF.PI / 180, c = MathF.Cos(a), s = MathF.Sin(a);
        return
        [
            0.213f + (c * 0.787f) - (s * 0.213f), 0.715f - (c * 0.715f) - (s * 0.715f), 0.072f - (c * 0.072f) + (s * 0.928f), 0, 0,
            0.213f - (c * 0.213f) + (s * 0.143f), 0.715f + (c * 0.285f) + (s * 0.140f), 0.072f - (c * 0.072f) - (s * 0.283f), 0, 0,
            0.213f - (c * 0.213f) - (s * 0.787f), 0.715f - (c * 0.715f) + (s * 0.715f), 0.072f + (c * 0.928f) + (s * 0.072f), 0, 0,
            0, 0, 0, 1, 0,
        ];
    }

    private static SvgTransferFunction? Transfer(SvgElement func)
    {
        string? type = func.Attributes["type"]?.Trim();
        if (type is not ("identity" or "table" or "discrete" or "linear" or "gamma"))
        {
            return null;
        }

        float Number(string name, float fallback) => func.Attributes[name] is string t && SvgNumbers.TryParse(t, out double v) ? (float)v : fallback;
        List<double> table = [];
        if (func.Attributes["tableValues"] is string text)
        {
            _ = SvgNumbers.TryParseList(text, table);
        }

        return new SvgTransferFunction(type, [.. table.Select(v => (float)v)], Number("slope", 1), Number("intercept", 0), Number("amplitude", 1), Number("exponent", 1), Number("offset", 0));
    }

    private static SvgConvolve? Convolve(SvgElement fe, RectF region, bool linear, SvgFilterInput input, Func<string, List<double>?> list)
    {
        int orderX = 3, orderY = 3;
        if (list("order") is List<double> order && order.Count > 0)
        {
            int x = (int)order[0], y = order.Count > 1 ? (int)order[1] : x;
            if (x > 0 && y > 0)
            {
                (orderX, orderY) = (x, y);
            }
        }

        float[] kernel = list("kernelMatrix") is List<double> k && k.Count == orderX * orderY ? [.. k.Select(v => (float)v)] : new float[orderX * orderY];
        float sum = MathF.Round(kernel.Sum() * 1_000_000) / 1_000_000;
        if (sum == 0)
        {
            sum = 1;
        }

        float divisor = fe.Attributes["divisor"] is string d && SvgNumbers.TryParse(d, out double dv) ? (float)dv : sum;
        if (divisor == 0)
        {
            return null;
        }

        int Target(string name, int size)
        {
            float t = fe.Attributes[name] is string text && SvgNumbers.TryParse(text, out double v) ? (float)v : MathF.Floor(size / 2f);
            return (int)t;
        }

        int targetX = Target("targetX", orderX), targetY = Target("targetY", orderY);
        if (targetX < 0 || targetX >= orderX || targetY < 0 || targetY >= orderY)
        {
            return null;
        }

        EdgeMode edge = fe.Attributes["edgeMode"]?.Trim() switch { "none" => EdgeMode.Transparent, "wrap" => EdgeMode.Wrap, _ => EdgeMode.Clamp };
        float bias = fe.Attributes["bias"] is string bt && SvgNumbers.TryParse(bt, out double bv) ? (float)bv : 0;
        return new SvgConvolve(region, linear, input, orderX, orderY, kernel, divisor, bias, targetX, targetY, edge, fe.Attributes["preserveAlpha"]?.Trim() == "true");
    }

    private static SvgLight? Light(SvgElement fe)
    {
        foreach (SvgElement child in fe.Elements())
        {
            float Number(string name, float fallback) => child.Attributes[name] is string t && SvgNumbers.TryParse(t, out double v) ? (float)v : fallback;
            switch (child.Name)
            {
                case "feDistantLight":
                    return new SvgDistantLight(Number("azimuth", 0), Number("elevation", 0));
                case "fePointLight":
                    return new SvgPointLight(new Vector3(Number("x", 0), Number("y", 0), Number("z", 0)));
                case "feSpotLight":
                {
                    float exponent = Number("specularExponent", 1);
                    float? cone = child.Attributes["limitingConeAngle"] is string c && SvgNumbers.TryParse(c, out double cv) ? (float)cv : null;
                    return new SvgSpotLight(
                        new Vector3(Number("x", 0), Number("y", 0), Number("z", 0)),
                        new Vector3(Number("pointsAtX", 0), Number("pointsAtY", 0), Number("pointsAtZ", 0)),
                        exponent > 0 ? exponent : 1,
                        cone);
                }
            }
        }

        return null;
    }

    /// <summary>feImage's content: a referenced element drawn as in place, or an image file fitted into the subregion.</summary>
    private SvgNode? FeImage(SvgElement fe, RectF region, SvgStyle style, in SvgContext context)
    {
        string? href = fe.Attributes["href"] ?? fe.Attributes.Get("href", SvgNamespaces.XLink);
        if (href is null)
        {
            return null;
        }

        if (!href.TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase) && SvgStyleValues.LocalId(href) is string id && href.TrimStart().StartsWith('#'))
        {
            SvgElement? target = _document.FindById(id);
            if (target is null || !Enter(target, fe))
            {
                return null;
            }

            try
            {
                return Element(target, context);
            }
            finally
            {
                Leave(target);
            }
        }

        // An image file: placed in the subregion like an image element of that size.
        SvgElement image = new("image");
        image.Attributes.Set("href", href);
        image.Attributes.Set("x", region.X);
        image.Attributes.Set("y", region.Y);
        image.Attributes.Set("width", region.Width);
        image.Attributes.Set("height", region.Height);
        if (fe.Attributes["preserveAspectRatio"] is string par)
        {
            image.Attributes["preserveAspectRatio"] = par;
        }

        return Image(image, style, context with { Style = style });
    }

    /// <summary>A CSS filter function (Filter Effects 1 section 13) as one primitive on SourceGraphic, in sRGB.</summary>
    private static bool TryFilterFunction(CssFunction function, SvgStyle style, in SvgContext context, out SvgPrimitive? primitive, out bool largeRegion)
    {
        primitive = null;
        largeRegion = false;
        List<CssComponentValue> args = [.. function.Arguments.Where(a => a is not CssTokenValue { Token.Kind: CssTokenKind.Whitespace or CssTokenKind.Comma })];
        RectF none = default;
        string name = function.Name.ToLowerInvariant();
        float Amount(float fallback, float max = float.PositiveInfinity)
        {
            if (args.Count == 0)
            {
                return fallback;
            }

            return args[0] is CssTokenValue { Token: var t } && t.Kind is CssTokenKind.Number or CssTokenKind.Percentage
                ? Math.Min((float)(t.Kind == CssTokenKind.Percentage ? t.Number / 100 : t.Number), max)
                : float.NaN;
        }

        switch (name)
        {
            case "grayscale":
            {
                float a = Amount(1, 1);
                if (!(a >= 0))
                {
                    return false;
                }

                float r = 1 - a;
                primitive = new SvgColorMatrix(none, false, SvgFilterInput.Graphic,
                [
                    0.2126f + (0.7874f * r), 0.7152f - (0.7152f * r), 0.0722f - (0.0722f * r), 0, 0,
                    0.2126f - (0.2126f * r), 0.7152f + (0.2848f * r), 0.0722f - (0.0722f * r), 0, 0,
                    0.2126f - (0.2126f * r), 0.7152f - (0.7152f * r), 0.0722f + (0.9278f * r), 0, 0,
                    0, 0, 0, 1, 0,
                ]);
                return true;
            }

            case "sepia":
            {
                float a = Amount(1, 1);
                if (!(a >= 0))
                {
                    return false;
                }

                float r = 1 - a;
                primitive = new SvgColorMatrix(none, false, SvgFilterInput.Graphic,
                [
                    0.393f + (0.607f * r), 0.769f - (0.769f * r), 0.189f - (0.189f * r), 0, 0,
                    0.349f - (0.349f * r), 0.686f + (0.314f * r), 0.168f - (0.168f * r), 0, 0,
                    0.272f - (0.272f * r), 0.534f - (0.534f * r), 0.131f + (0.869f * r), 0, 0,
                    0, 0, 0, 1, 0,
                ]);
                return true;
            }

            case "saturate":
            {
                float a = Amount(1);
                if (!(a >= 0))
                {
                    return false;
                }

                primitive = new SvgColorMatrix(none, false, SvgFilterInput.Graphic, Saturate(a));
                return true;
            }

            case "hue-rotate":
            {
                float degrees = 0;
                if (args.Count > 0)
                {
                    if (!CssAngle.TryParse(args[0], allowNumber: args[0] is CssTokenValue { Token: { Kind: CssTokenKind.Number, Number: 0 } }, out CssAngle angle))
                    {
                        return false;
                    }

                    degrees = (float)angle.Degrees;
                }

                primitive = new SvgColorMatrix(none, false, SvgFilterInput.Graphic, HueRotate(degrees));
                return true;
            }

            case "invert":
            {
                float a = Amount(1, 1);
                if (!(a >= 0))
                {
                    return false;
                }

                SvgTransferFunction f = new("table", [a, 1 - a], 1, 0, 1, 1, 0);
                primitive = new SvgComponentTransfer(none, false, SvgFilterInput.Graphic, f, f, f, SvgTransferFunction.Identity);
                return true;
            }

            case "opacity":
            {
                float a = Amount(1, 1);
                if (!(a >= 0))
                {
                    return false;
                }

                SvgTransferFunction i = SvgTransferFunction.Identity;
                primitive = new SvgComponentTransfer(none, false, SvgFilterInput.Graphic, i, i, i, new SvgTransferFunction("table", [0, a], 1, 0, 1, 1, 0));
                return true;
            }

            case "brightness":
            {
                float a = Amount(1);
                if (!(a >= 0))
                {
                    return false;
                }

                SvgTransferFunction f = new("linear", [], a, 0, 1, 1, 0);
                primitive = new SvgComponentTransfer(none, false, SvgFilterInput.Graphic, f, f, f, SvgTransferFunction.Identity);
                return true;
            }

            case "contrast":
            {
                float a = Amount(1);
                if (!(a >= 0))
                {
                    return false;
                }

                SvgTransferFunction f = new("linear", [], a, (-0.5f * a) + 0.5f, 1, 1, 0);
                primitive = new SvgComponentTransfer(none, false, SvgFilterInput.Graphic, f, f, f, SvgTransferFunction.Identity);
                return true;
            }

            case "blur":
            {
                float sigma = 0;
                if (args.Count > 0)
                {
                    if (!SvgStyleValues.TryLength(args[0], out SvgLength l) || l.Value < 0 || l.IsPercent)
                    {
                        return false;
                    }

                    sigma = l.ToPixels(context.Lengths, SvgAxis.Other);
                }

                primitive = new SvgBlur(none, false, SvgFilterInput.Graphic, sigma, sigma);
                largeRegion = true;
                return true;
            }

            case "drop-shadow":
            {
                CssColor color = style.Color;
                List<float> lengths = [];
                foreach (CssComponentValue arg in args)
                {
                    if (SvgStyleValues.TryLength(arg, out SvgLength l) && !l.IsPercent)
                    {
                        lengths.Add(l.ToPixels(context.Lengths, SvgAxis.Other));
                    }
                    else if (SvgStyleValues.TryColor([arg], out CssColor c, out _))
                    {
                        color = c.IsCurrentColor ? style.Color : c;
                    }
                    else
                    {
                        return false;
                    }
                }

                if (lengths.Count is < 2 or > 3 || (lengths.Count == 3 && lengths[2] < 0))
                {
                    return false;
                }

                float s = lengths.Count == 3 ? lengths[2] : 0;
                primitive = new SvgDropShadow(none, false, SvgFilterInput.Graphic, lengths[0], lengths[1], s, s, new RgbaF32(color.R, color.G, color.B, color.A));
                largeRegion = true;
                return true;
            }

            default:
                return false;
        }
    }
}
