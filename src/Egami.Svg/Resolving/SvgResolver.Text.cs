using System.Numerics;

using Egami.Css;
using Egami.Drawing;
using Egami.Fonts;
using Egami.Pixels;
using Egami.Svg.Styling;
using Egami.Svg.Text;
using Egami.Svg.Values;

namespace Egami.Svg.Resolving;

internal sealed partial class SvgResolver
{
    private SvgFonts? _fonts;

    private SvgFonts Fonts
    {
        get
        {
            if (_fonts is null)
            {
                _fonts = new SvgFonts(_options.Fonts, _report);
                if (_styler.FontFaces.Count > 0)
                {
                    _fonts.AddFontFaces(_styler.FontFaces, (href, element) =>
                    {
                        byte[]? data = SvgResources.Open(href, _options, out _, out string? problem);
                        if (data is null && problem is not null)
                        {
                            _report.Warn("Svg.FontNotLoaded", $"The @font-face source \"{SvgGeometry.Clip(href)}\" was not loaded: {problem}", element);
                        }

                        return data;
                    });
                }
            }

            return _fonts;
        }
    }

    /// <summary>
    /// A text element (stage C, question 11): its characters collected with white space processed, placed by the x, y, dx, dy
    /// and rotate lists, cut into chunks, laid out in their fonts and turned into glyph runs and decoration paths, with paints
    /// resolved against the text's box. In a clip path the glyphs become outlines.
    /// </summary>
    private SvgGroup? Text(SvgElement element, SvgStyle style, in SvgContext context)
    {
        // SVG 2 wrapped text: inline-size or shape-inside on the text element, in horizontal writing.
        if (style.Css is CssComputedStyle css && Keyword(css, "writing-mode") is null or "horizontal-tb" or "lr" or "lr-tb" or "rl" or "rl-tb")
        {
            List<string> shapes = [];
            if (css.TryGetValue("shape-inside", out IReadOnlyList<CssComponentValue>? inside))
            {
                foreach (CssComponentValue item in inside)
                {
                    if (CssUrl.TryParse(item, out CssUrl url))
                    {
                        shapes.Add(url.Url);
                    }
                }
            }

            float? inlineSize = css.TryGetValue("inline-size", out IReadOnlyList<CssComponentValue>? size) && SvgStyleValues.TryLength(size, out SvgLength l) &&
                l.ToPixels(context.Lengths, SvgAxis.X) is float px && px > 0 ? px : null;
            if (shapes.Count > 0 || inlineSize is not null)
            {
                return FlowText(element, style, context, inlineSize, shapes);
            }
        }

        SvgTextTree tree = SvgTextTree.Build(element, _document);
        Dictionary<SvgTextTree, (SvgStyle Style, CssComputedStyle Css)> styles = new(ReferenceEqualityComparer.Instance) { [tree] = (style, context.Css!) };
        StyleTree(tree, styles);

        int count = tree.CharCount();
        if (count == 0)
        {
            return null;
        }

        SvgCharPosition[] positions = Positions(tree, styles, count, context);
        float[] rotate = Rotations(tree, count);
        List<SvgTextChunk> chunks = Chunks(tree, styles, positions, context);
        bool vertical = Keyword(style.Css, "writing-mode") is "tb" or "tb-rl" or "vertical-rl" or "vertical-lr";
        (List<SvgLaidSpan> spans, RectF bounds) = SvgTextLayout.Layout(chunks, positions, rotate, vertical, Fonts.Fallback);
        if (spans.Count == 0)
        {
            return null;
        }

        Matrix3x2 transform = ElementTransform(element, style, bounds, context.Lengths);
        SvgContext own = Into(context, transform);

        // text-rendering: optimizeSpeed draws aliased, as resvg maps it to shape-rendering: crispEdges.
        bool antialias = Keyword(style.Css, "text-rendering") != "optimizespeed";
        List<SvgNode> children = [];
        foreach (SvgLaidSpan laid in spans)
        {
            SvgTextSpan span = laid.Span;
            if (!span.Visible)
            {
                continue;
            }

            if (context.InClip)
            {
                Path? outline = Outlines(laid.Glyphs);
                if (outline is not null)
                {
                    children.Add(new SvgPath(outline.WithFillRule(span.Style.ClipRule)) { Fill = new SolidPaint(new RgbaF32(0, 0, 0, 1)), Bounds = outline.TightBounds });
                }

                continue;
            }

            SvgLengthContext lengths = context.Lengths with { FontSize = span.Style.FontSize };
            (Paint? fill, Paint? strokePaint, Stroke? stroke) = TextPaints(span.Style, span.Element, bounds, lengths, own);
            AddDecoration(children, laid.Overline, span.Overline, bounds, lengths, own, antialias);
            AddDecoration(children, laid.Underline, span.Underline, bounds, lengths, own, antialias);
            if (fill is not null || strokePaint is not null)
            {
                AddGlyphs(children, laid.Glyphs, fill, strokePaint, stroke, span.Style.PaintOrder, antialias);
            }

            AddDecoration(children, laid.LineThrough, span.LineThrough, bounds, lengths, own, antialias);
        }

        if (children.Count == 0)
        {
            return new SvgGroup { Bounds = bounds, Transform = transform, Element = element, Id = element.Id };
        }

        return Decorate(new SvgGroup { Children = children, Bounds = bounds, Transform = transform }, element, style, context);
    }

    private (Paint? Fill, Paint? StrokePaint, Stroke? Stroke) TextPaints(SvgStyle style, SvgElement element, RectF bounds, in SvgLengthContext lengths, in SvgContext context)
    {
        Paint? fill = style.Visible ? ResolvePaint(style.Fill, style, style.FillOpacity, bounds, element, context) : null;
        Paint? strokePaint = style.Visible ? ResolvePaint(style.Stroke, style, style.StrokeOpacity, bounds, element, context) : null;
        Stroke? stroke = strokePaint is null ? null : StrokeOf(style, lengths);
        return (fill, stroke is null ? null : strokePaint, stroke);
    }

    private void AddDecoration(List<SvgNode> children, Path? path, SvgTextDecoration? decoration, RectF bounds, in SvgLengthContext lengths, in SvgContext context, bool antialias)
    {
        if (path is null || decoration is null)
        {
            return;
        }

        (Paint? fill, Paint? strokePaint, Stroke? stroke) = TextPaints(decoration.Style, decoration.Element, bounds, lengths with { FontSize = decoration.Style.FontSize }, context);
        if (fill is not null || strokePaint is not null)
        {
            children.Add(new SvgPath(path) { Fill = fill, StrokePaint = strokePaint, Stroke = stroke, Bounds = path.TightBounds, Antialias = antialias });
        }
    }

    /// <summary>
    /// Glyph runs, one per font in turn; a glyph stretched or skewed (lengthAdjust="spacingAndGlyphs" under rotation) cannot be
    /// a run's glyph, so it becomes an outline.
    /// </summary>
    private static void AddGlyphs(List<SvgNode> children, List<SvgPlacedGlyph> glyphs, Paint? fill, Paint? strokePaint, Stroke? stroke, SvgPaintOrder order, bool antialias)
    {
        int i = 0;
        while (i < glyphs.Count)
        {
            if (!IsSimilarity(glyphs[i].Transform))
            {
                Path outline = Outline(glyphs[i]);
                children.Add(new SvgPath(outline) { Fill = fill, StrokePaint = strokePaint, Stroke = stroke, PaintOrder = order, Bounds = outline.TightBounds, Antialias = antialias });
                i++;
                continue;
            }

            int start = i;
            Font font = glyphs[i].Font;
            bool translations = true;
            while (i < glyphs.Count && glyphs[i].Font == font && IsSimilarity(glyphs[i].Transform))
            {
                Matrix3x2 m = glyphs[i].Transform;
                translations &= m.M11 == 1 && m.M12 == 0;
                i++;
            }

            int n = i - start;
            ushort[] ids = new ushort[n];
            for (int k = 0; k < n; k++)
            {
                ids[k] = glyphs[start + k].Glyph;
            }

            GlyphRun run;
            if (translations)
            {
                Vector2[] positions = new Vector2[n];
                for (int k = 0; k < n; k++)
                {
                    positions[k] = glyphs[start + k].Transform.Translation;
                }

                run = GlyphRun.Create(font, ids, positions);
            }
            else
            {
                GlyphTransform[] transforms = new GlyphTransform[n];
                for (int k = 0; k < n; k++)
                {
                    Matrix3x2 m = glyphs[start + k].Transform;
                    transforms[k] = new GlyphTransform(m.M11, m.M12, m.M31, m.M32);
                }

                run = GlyphRun.CreateTransformed(font, ids, transforms);
            }

            children.Add(new SvgTextRun(run) { Fill = fill, StrokePaint = strokePaint, Stroke = stroke, PaintOrder = order, Antialias = antialias, Bounds = new RectF(run.Bounds.Left, run.Bounds.Top, run.Bounds.Right - run.Bounds.Left, run.Bounds.Bottom - run.Bounds.Top) });
        }
    }

    private static bool IsSimilarity(Matrix3x2 m) =>
        MathF.Abs(m.M11 - m.M22) <= 1e-5f * MathF.Max(1, MathF.Abs(m.M11)) && MathF.Abs(m.M12 + m.M21) <= 1e-5f * MathF.Max(1, MathF.Abs(m.M12));

    private static Path Outline(SvgPlacedGlyph glyph) =>
        GlyphRun.Create(glyph.Font, [glyph.Glyph], [Vector2.Zero]).ToPath(Vector2.Zero).Transform(glyph.Transform);

    private static Path? Outlines(List<SvgPlacedGlyph> glyphs)
    {
        if (glyphs.Count == 0)
        {
            return null;
        }

        PathBuilder builder = new();
        foreach (SvgPlacedGlyph glyph in glyphs)
        {
            builder.AddPath(Outline(glyph));
        }

        return builder.ToPath();
    }

    /// <summary>The computed styles of every element node of a text tree.</summary>
    private void StyleTree(SvgTextTree node, Dictionary<SvgTextTree, (SvgStyle Style, CssComputedStyle Css)> styles)
    {
        (SvgStyle style, CssComputedStyle css) = styles[node];
        foreach (SvgTextTree child in node.Children)
        {
            if (child.Element is SvgElement element)
            {
                styles[child] = Style(element, style, css);
                StyleTree(child, styles);
            }
        }
    }

    private static string? Keyword(CssComputedStyle? css, string name) =>
        css is not null && css.TryGetValue(name, out IReadOnlyList<CssComponentValue>? values) ? SvgStyleValues.Keyword(values)?.ToLowerInvariant() : null;

    /// <summary>
    /// The x, y, dx and dy lists given to each character: an element's list covers its own characters and its descendants', and
    /// a descendant's list overrides it (textPath's lists are ignored).
    /// </summary>
    private static SvgCharPosition[] Positions(SvgTextTree tree, Dictionary<SvgTextTree, (SvgStyle Style, CssComputedStyle Css)> styles, int count, in SvgContext context)
    {
        SvgCharPosition[] list = new SvgCharPosition[count];
        int offset = 0;
        List<SvgLength> lengths = [];
        foreach (SvgTextTree node in tree.Descendants())
        {
            if (node.IsText)
            {
                offset += SvgTextTree.Count(node.Text);
                continue;
            }

            if (node.Kind == "textPath")
            {
                continue;
            }

            int chars = node.CharCount();
            SvgLengthContext l = context.Lengths with { FontSize = styles[node].Style.FontSize };
            foreach ((string name, SvgAxis axis) in (ReadOnlySpan<(string, SvgAxis)>)[("x", SvgAxis.X), ("y", SvgAxis.Y), ("dx", SvgAxis.X), ("dy", SvgAxis.Y)])
            {
                lengths.Clear();
                if (node.Element!.Attributes[name] is not string text || !SvgLength.TryParseList(text, lengths))
                {
                    continue;
                }

                for (int i = 0; i < Math.Min(lengths.Count, chars); i++)
                {
                    float v = lengths[i].ToPixels(l, axis);
                    ref SvgCharPosition p = ref list[offset + i];
                    switch (name)
                    {
                        case "x":
                            p.X = v;
                            break;
                        case "y":
                            p.Y = v;
                            break;
                        case "dx":
                            p.Dx = v;
                            break;
                        default:
                            p.Dy = v;
                            break;
                    }
                }
            }
        }

        return list;
    }

    /// <summary>Each character's rotation: an element's rotate list, its last value repeated over the rest of its characters.</summary>
    private static float[] Rotations(SvgTextTree tree, int count)
    {
        float[] list = new float[count];
        float last = 0;
        int offset = 0;
        List<double> values = [];
        foreach (SvgTextTree node in tree.Descendants())
        {
            if (node.IsText)
            {
                offset += SvgTextTree.Count(node.Text);
                continue;
            }

            values.Clear();
            if (node.Element!.Attributes["rotate"] is not string text || !SvgNumbers.TryParseList(text, values))
            {
                continue;
            }

            int chars = node.CharCount();
            for (int i = 0; i < chars; i++)
            {
                if (i < values.Count)
                {
                    last = (float)values[i];
                }

                list[offset + i] = last;
            }
        }

        return list;
    }

    private sealed class ChunkState
    {
        public int Chars;
        public int ChunkLength;
        public bool Split;
        public SvgTextPathFlow? Flow;

        public List<SvgTextChunk> Chunks { get; } = [];
    }

    private List<SvgTextChunk> Chunks(SvgTextTree tree, Dictionary<SvgTextTree, (SvgStyle Style, CssComputedStyle Css)> styles, SvgCharPosition[] positions, in SvgContext context)
    {
        ChunkState state = new();
        CollectChunks(tree, tree, styles, positions, state, context);
        return state.Chunks;
    }

    private void CollectChunks(
        SvgTextTree parent, SvgTextTree root, Dictionary<SvgTextTree, (SvgStyle Style, CssComputedStyle Css)> styles, SvgCharPosition[] positions,
        ChunkState state, in SvgContext context)
    {
        foreach (SvgTextTree child in parent.Children)
        {
            if (!child.IsText)
            {
                if (child.Kind == "textPath")
                {
                    SvgTextPathFlow? flow = TextPath(child.Element!, styles[child].Style, context);
                    if (flow is null)
                    {
                        state.Chars += child.CharCount();
                        continue;
                    }

                    state.Flow = flow;
                    state.Split = true;
                }

                CollectChunks(child, root, styles, positions, state, context);
                state.Flow = null;
                if (child.Kind == "textPath")
                {
                    state.Split = true;
                }

                continue;
            }

            (SvgStyle style, CssComputedStyle css) = styles[parent];
            int chars = SvgTextTree.Count(child.Text);
            if (style.DisplayNone || !(style.FontSize > 0) || child.Text.Length == 0 || !Conditions(parent.Element!))
            {
                state.Chars += chars;
                continue;
            }

            SvgTextSpan? span = Span(parent, root, styles, context);
            if (span is null)
            {
                state.Chars += chars;
                continue;
            }

            string anchor = Keyword(css, "text-anchor") ?? "start";
            bool newSpan = true;
            string text = child.Text;
            for (int i = 0; i < text.Length;)
            {
                int length = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
                SvgCharPosition position = state.Chars < positions.Length ? positions[state.Chars] : default;
                bool newChunk = position.X is not null || position.Y is not null || state.Split || state.Chunks.Count == 0;
                state.Split = false;
                if (newChunk)
                {
                    state.ChunkLength = 0;
                    SvgTextChunk chunk = new() { X = position.X, Y = position.Y, Anchor = anchor, Path = state.Flow, FirstChar = state.Chars };
                    chunk.Text.Append(text, i, length);
                    chunk.Spans.Add(span.Copy(0, length));
                    state.Chunks.Add(chunk);
                }
                else
                {
                    SvgTextChunk chunk = state.Chunks[^1];
                    chunk.Text.Append(text, i, length);
                    if (newSpan)
                    {
                        chunk.Spans.Add(span.Copy(state.ChunkLength, state.ChunkLength + length));
                    }
                    else
                    {
                        chunk.Spans[^1].End += length;
                    }
                }

                newSpan = false;
                state.Chars++;
                state.ChunkLength += length;
                i += length;
            }
        }
    }

    /// <summary>The style of the characters directly inside a text or tspan node, with its font.</summary>
    private SvgTextSpan? Span(SvgTextTree node, SvgTextTree root, Dictionary<SvgTextTree, (SvgStyle Style, CssComputedStyle Css)> styles, in SvgContext context)
    {
        (SvgStyle style, CssComputedStyle css) = styles[node];
        SvgElement element = node.Element!;
        SvgLengthContext lengths = context.Lengths with { FontSize = style.FontSize };

        // The font: family list, weight (bolder and lighter step from the parent's), stretch, slant and variations.
        // font-family's initial value stands for SvgFontOptions.DefaultFamily, as resvg's default family.
        bool familySet = false;
        for (CssComputedStyle? c = css; c is not null && !familySet; c = c.Parent)
        {
            familySet = c.IsSetOnElement("font-family");
        }

        CssFontFamilyList? families = familySet && css.TryGetValue("font-family", out IReadOnlyList<CssComponentValue>? familyValues) && CssFontFamilyList.TryParse(familyValues, out CssFontFamilyList? list) ? list : null;
        int weight = Weight(css);
        string? fontStyle = Keyword(css, "font-style");
        FontSlant slant = fontStyle switch { "italic" => FontSlant.Italic, "oblique" => FontSlant.Oblique, _ => FontSlant.Upright };
        int width = Keyword(css, "font-stretch") switch
        {
            "ultra-condensed" => 1,
            "extra-condensed" => 2,
            "condensed" or "narrower" => 3,
            "semi-condensed" => 4,
            "semi-expanded" => 6,
            "expanded" or "wider" => 7,
            "extra-expanded" => 8,
            "ultra-expanded" => 9,
            _ => 5,
        };
        Typeface? face = Fonts.Match(families, new FontStyle(weight, width, slant), element);
        if (face is null)
        {
            return null;
        }

        List<FontVariation> variations = Variations(css);
        if (face.IsVariable)
        {
            if (!variations.Any(v => v.Axis == new FontTag("wght")))
            {
                variations.Add(new FontVariation("wght", weight));
            }

            float[] percents = [50, 62.5f, 75, 87.5f, 100, 112.5f, 125, 150, 200];
            if (!variations.Any(v => v.Axis == new FontTag("wdth")) && width != 5)
            {
                variations.Add(new FontVariation("wdth", percents[width - 1]));
            }

            if (slant == FontSlant.Italic && !variations.Any(v => v.Axis == new FontTag("ital")))
            {
                variations.Add(new FontVariation("ital", 1));
            }
            else if (slant == FontSlant.Oblique && !variations.Any(v => v.Axis == new FontTag("slnt")))
            {
                variations.Add(new FontVariation("slnt", -12));
            }

            if (Keyword(css, "font-optical-sizing") != "none" && !variations.Any(v => v.Axis == new FontTag("opsz")))
            {
                variations.Add(new FontVariation("opsz", style.FontSize));
            }
        }

        if (variations.Count > 0)
        {
            face = face.WithVariations([.. variations]);
        }

        bool kerning = Keyword(css, "font-kerning") != "none";
        if (element.Attributes["kerning"] is string kerningText && SvgLength.TryParse(kerningText, out SvgLength k) && k.Value == 0)
        {
            kerning = false;
        }

        string dominant = Keyword(css, "dominant-baseline") ?? "auto";
        if (dominant == "no-change")
        {
            dominant = node.Parent is SvgTextTree p && styles.TryGetValue(p, out var ps) ? Keyword(ps.Css, "dominant-baseline") ?? "auto" : "auto";
        }

        float? textLength = null;
        if (element.Attributes["textLength"] is string tl && SvgLength.TryParse(tl, out SvgLength length) && length.Value >= 0)
        {
            textLength = length.ToPixels(lengths, SvgAxis.Other);
        }

        return new SvgTextSpan
        {
            Element = element,
            Style = style,
            Font = new SvgTextFont(new Font(face, style.FontSize) { Kerning = kerning }),
            SmallCaps = Keyword(css, "font-variant-caps") == "small-caps" || Keyword(css, "font-variant") == "small-caps",
            Kerning = kerning,
            Visible = style.Visible,
            DominantBaseline = dominant,
            AlignmentBaseline = Keyword(css, "alignment-baseline") ?? "auto",
            BaselineShift = BaselineShifts(node, root, styles, context),
            LetterSpacing = Spacing(node, root, styles, "letter-spacing", context),
            WordSpacing = Spacing(node, root, styles, "word-spacing", context),
            TextLength = textLength,
            SpacingAndGlyphs = element.Attributes["lengthAdjust"]?.Trim() == "spacingAndGlyphs",
            Underline = Decoration(node, root, styles, "underline"),
            Overline = Decoration(node, root, styles, "overline"),
            LineThrough = Decoration(node, root, styles, "line-through"),
        };
    }

    /// <summary>font-weight along the element's ancestors, bolder and lighter stepping as Chromium does (400 to 700, 400 to 200).</summary>
    private static int Weight(CssComputedStyle? css)
    {
        if (css is null)
        {
            return 400;
        }

        int parent = Weight(css.Parent);
        if (!css.IsSetOnElement("font-weight") || !css.TryGetValue("font-weight", out IReadOnlyList<CssComponentValue>? values))
        {
            return parent;
        }

        string? keyword = SvgStyleValues.Keyword(values)?.ToLowerInvariant();
        if (keyword == "bolder")
        {
            return Math.Clamp(parent + (parent == 400 ? 300 : 100), 100, 900);
        }

        if (keyword == "lighter")
        {
            return Math.Clamp(parent - (parent == 400 ? 200 : 100), 100, 900);
        }

        return CssFontWeight.TryParse(values, out CssFontWeight w) && w.Kind == CssFontWeightKind.Absolute ? (int)Math.Clamp(w.Value, 1, 1000) : parent;
    }

    /// <summary>font-variation-settings: "tag" number pairs separated by commas.</summary>
    private static List<FontVariation> Variations(CssComputedStyle css)
    {
        List<FontVariation> result = [];
        if (!css.TryGetValue("font-variation-settings", out IReadOnlyList<CssComponentValue>? values))
        {
            return result;
        }

        string? tag = null;
        foreach (CssComponentValue value in values)
        {
            if (value is not CssTokenValue { Token: var t })
            {
                continue;
            }

            if (t.Kind == CssTokenKind.String)
            {
                tag = t.Value.Length == 4 ? t.Value : null;
            }
            else if (t.Kind == CssTokenKind.Number && tag is not null)
            {
                result.Add(new FontVariation(tag, (float)t.Number));
                tag = null;
            }
        }

        return result;
    }

    /// <summary>letter-spacing or word-spacing: resolved where it is set, so em lengths take that element's font size.</summary>
    private static float Spacing(SvgTextTree node, SvgTextTree root, Dictionary<SvgTextTree, (SvgStyle Style, CssComputedStyle Css)> styles, string name, in SvgContext context)
    {
        for (SvgTextTree? n = node; n is not null; n = n.Parent)
        {
            if (n.IsText || !styles.TryGetValue(n, out var s))
            {
                continue;
            }

            if (s.Css.IsSetOnElement(name) || n == root)
            {
                if (!s.Css.TryGetValue(name, out IReadOnlyList<CssComponentValue>? values) || SvgStyleValues.IsKeyword(values, "normal"))
                {
                    return 0;
                }

                return SvgStyleValues.TryLength(values, out SvgLength l) ? l.ToPixels(context.Lengths with { FontSize = s.Style.FontSize }, SvgAxis.Other) : 0;
            }
        }

        return 0;
    }

    /// <summary>baseline-shift on the element and each ancestor up to the text element, nearest first.</summary>
    private static List<(SvgBaselineShiftKind, float)> BaselineShifts(SvgTextTree node, SvgTextTree root, Dictionary<SvgTextTree, (SvgStyle Style, CssComputedStyle Css)> styles, in SvgContext context)
    {
        List<(SvgBaselineShiftKind, float)> shifts = [];
        for (SvgTextTree? n = node; n is not null && n != root; n = n.Parent)
        {
            if (n.IsText || !styles.TryGetValue(n, out var s) || !s.Css.IsSetOnElement("baseline-shift") || !s.Css.TryGetValue("baseline-shift", out IReadOnlyList<CssComponentValue>? values))
            {
                continue;
            }

            switch (SvgStyleValues.Keyword(values)?.ToLowerInvariant())
            {
                case "sub":
                    shifts.Add((SvgBaselineShiftKind.Sub, 0));
                    continue;
                case "super":
                    shifts.Add((SvgBaselineShiftKind.Super, 0));
                    continue;
                case "baseline":
                    shifts.Add((SvgBaselineShiftKind.Baseline, 0));
                    continue;
            }

            if (SvgStyleValues.TryLength(values, out SvgLength l))
            {
                float v = l.IsPercent ? s.Style.FontSize * l.Value / 100 : l.ToPixels(context.Lengths with { FontSize = s.Style.FontSize }, SvgAxis.Other);
                shifts.Add((SvgBaselineShiftKind.Length, v));
            }
        }

        return shifts.All(s => s.Item1 == SvgBaselineShiftKind.Baseline) ? [] : shifts;
    }

    /// <summary>
    /// A text decoration a node has: declared on it or an ancestor (even outside the text element), painted with the fill and
    /// stroke of the nearest node inside the text element that declares it, or of the text element.
    /// </summary>
    private static SvgTextDecoration? Decoration(SvgTextTree node, SvgTextTree root, Dictionary<SvgTextTree, (SvgStyle Style, CssComputedStyle Css)> styles, string line)
    {
        static bool Declares(CssComputedStyle? css, string line)
        {
            foreach (string name in (ReadOnlySpan<string>)["text-decoration", "text-decoration-line"])
            {
                if (css is not null && css.IsSetOnElement(name) && css.TryGetValue(name, out IReadOnlyList<CssComponentValue>? values) &&
                    values.Any(v => v is CssTokenValue { Token: var t } && t.IsIdent(line)))
                {
                    return true;
                }
            }

            return false;
        }

        bool anywhere = false;
        for (CssComputedStyle? css = styles[node].Css; css is not null; css = css.Parent)
        {
            if (Declares(css, line))
            {
                anywhere = true;
                break;
            }
        }

        if (!anywhere)
        {
            return null;
        }

        for (SvgTextTree? n = node; n is not null; n = n.Parent)
        {
            if (n.IsText || !styles.TryGetValue(n, out var s))
            {
                continue;
            }

            if (Declares(s.Css, line) || n == root)
            {
                return new SvgTextDecoration(n.Element!, s.Style);
            }
        }

        return null;
    }

    /// <summary>A textPath's path (its href's shape or its own path attribute) in the text's coordinates, and its startOffset.</summary>
    private SvgTextPathFlow? TextPath(SvgElement textPath, SvgStyle style, in SvgContext context)
    {
        Path? path = null;
        if (textPath.Attributes["path"] is string d && Path.TryParseSvg(d, out Path? own))
        {
            path = own;
        }
        else
        {
            string? href = textPath.Attributes["href"] ?? textPath.Attributes.Get("href", SvgNamespaces.XLink);
            if (href is not null && SvgStyleValues.LocalId(href) is string id && _document.FindById(id) is SvgElement target && target.IsSvgOrBare(_document))
            {
                (SvgStyle targetStyle, _) = OriginalStyle(target);
                _geometryStyle = (target, targetStyle.Css);
                try
                {
                    path = SvgShapes.Build(target, _geometry, context.Lengths, _limits.MaxPathCommands, _report);
                }
                finally
                {
                    _geometryStyle = default;
                }

                if (path is not null)
                {
                    Matrix3x2 m = ElementTransform(target, targetStyle, path.TightBounds, context.Lengths);
                    path = m.IsIdentity ? path : path.Transform(m);
                }
            }
        }

        if (path is null || path.IsEmpty)
        {
            _report.Warn("Svg.BadReference", "A textPath names no path; its characters are not drawn.", textPath);
            return null;
        }

        float offset = 0;
        if (textPath.Attributes["startOffset"] is string so && SvgLength.TryParse(so, out SvgLength start))
        {
            offset = start.IsPercent ? new PathMeasure(path).TotalLength * start.Value / 100 : start.ToPixels(context.Lengths with { FontSize = style.FontSize }, SvgAxis.Other);
        }

        return new SvgTextPathFlow(path, offset);
    }
}
