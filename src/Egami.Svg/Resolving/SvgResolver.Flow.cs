using System.Numerics;
using System.Text;

using Egami.Css;
using Egami.Drawing;
using Egami.Fonts;
using Egami.Fonts.Layout;
using Egami.Fonts.Shaping;
using Egami.Svg.Styling;
using Egami.Svg.Text;
using Egami.Svg.Values;

namespace Egami.Svg.Resolving;

internal sealed partial class SvgResolver
{
    /// <summary>
    /// Wrapped text (SVG 2 section 11.4, question 12): with inline-size the text fills lines of that length from its x and y,
    /// aligned by text-anchor, as 2.8.2's paragraph layout lays them; with shape-inside each line band across the shapes (less
    /// shape-subtract with its shape-margin, and shape-padding inside) takes text in every free interval in turn, as Inkscape
    /// flows text in a shape. Position lists, textPath and dx and dy do not apply to wrapped text.
    /// </summary>
    private SvgGroup? FlowText(SvgElement element, SvgStyle style, in SvgContext context, float? inlineSize, List<string> shapes)
    {
        string whiteSpace = Keyword(style.Css, "white-space") ?? "normal";
        SvgTextTree tree = SvgTextTree.Build(element, _document, whiteSpace);
        Dictionary<SvgTextTree, (SvgStyle Style, CssComputedStyle Css)> styles = new(ReferenceEqualityComparer.Instance) { [tree] = (style, context.Css!) };
        StyleTree(tree, styles);
        int count = tree.CharCount();
        if (count == 0)
        {
            return null;
        }

        // One chunk: positions do not apply.
        List<SvgTextChunk> chunks = Chunks(tree, styles, new SvgCharPosition[count], context);
        StringBuilder text = new();
        List<SvgTextSpan> spans = [];
        foreach (SvgTextChunk chunk in chunks)
        {
            int offset = text.Length;
            text.Append(chunk.Text);
            foreach (SvgTextSpan span in chunk.Spans)
            {
                spans.Add(span.Copy(span.Start + offset, span.End + offset));
            }
        }

        if (spans.Count == 0)
        {
            return null;
        }

        // A run's style index is 0 for the default style (the first span's), else 1 plus its span's index.
        List<StyleSpan> styleSpans = [.. spans.Select(span => new StyleSpan(span.Start, span.End - span.Start, LayoutStyle(span, shapes.Count > 0)))];
        SvgTextSpan Owner(int index) => spans[Math.Clamp(index - 1, 0, spans.Count - 1)];

        string anchor = Keyword(style.Css, "text-anchor") ?? "start";
        string? align = Keyword(style.Css, "text-align");
        TextAlign textAlign = (shapes.Count > 0 && align is not null and not "start" ? align : anchor) switch
        {
            "middle" or "center" => TextAlign.Center,
            "end" or "right" => TextAlign.End,
            "justify" => TextAlign.Justify,
            _ => TextAlign.Start,
        };
        Paragraph paragraph = Paragraph.Create(new RichText(text.ToString(), styleSpans), new ParagraphStyle
        {
            Align = textAlign,
            DefaultStyle = styleSpans[0].Style,
            Fallback = Fonts.Fallback,
            Direction = Keyword(style.Css, "direction") == "rtl" ? Egami.Fonts.Unicode.TextDirection.RightToLeft : Egami.Fonts.Unicode.TextDirection.LeftToRight,
        });

        List<(GlyphRun Run, Vector2 Offset, SvgTextSpan Span)> runs = [];
        List<(Path Path, SvgTextSpan Span, TextDecorationLines Line)> decorations = [];
        if (shapes.Count == 0)
        {
            float x = element.Attributes["x"] is string xs && SvgLength.TryParse(FirstValue(xs), out SvgLength lx) ? lx.ToPixels(context.Lengths, SvgAxis.X) : 0;
            float y = element.Attributes["y"] is string ys && SvgLength.TryParse(FirstValue(ys), out SvgLength ly) ? ly.ToPixels(context.Lengths, SvgAxis.Y) : 0;
            float size = inlineSize!.Value;
            TextLayout layout = paragraph.Layout(size);
            float left = x - (anchor switch { "middle" => size / 2, "end" => size, _ => 0 });
            Vector2 origin = new(left, y - layout.FirstBaseline);
            foreach (TextLine line in layout.Lines)
            {
                foreach (LayoutRun run in line.Runs)
                {
                    if (run.Glyphs is GlyphRun g)
                    {
                        runs.Add((g, origin, Owner(run.StyleIndex)));
                    }
                }
            }

            foreach (DecorationShape shape in layout.Decorations)
            {
                decorations.Add((shape.ToPath(origin), Owner(shape.StyleIndex), shape.Line));
            }
        }
        else
        {
            FlowInShapes(paragraph, Owner, shapes, style, element, context, runs);
        }

        if (runs.Count == 0)
        {
            return null;
        }

        RectF bounds = RectF.Empty;
        bool any = false;
        foreach ((GlyphRun run, Vector2 offset, _) in runs)
        {
            TextBounds b = run.Bounds;
            RectF r = new(b.Left + offset.X, b.Top + offset.Y, b.Right - b.Left, b.Bottom - b.Top);
            bounds = any ? bounds.Union(r) : r;
            any = true;
        }

        Matrix3x2 transform = ElementTransform(element, style, bounds, context.Lengths);
        SvgContext own = Into(context, transform);
        bool antialias = Keyword(style.Css, "text-rendering") != "optimizespeed";
        List<SvgNode> children = [];
        foreach ((Path path, SvgTextSpan span, TextDecorationLines line) in decorations.Where(d => d.Line != TextDecorationLines.LineThrough))
        {
            AddDecoration(children, path, line == TextDecorationLines.Underline ? span.Underline : span.Overline, bounds, context.Lengths with { FontSize = span.Style.FontSize }, own, antialias);
        }

        foreach ((GlyphRun run, Vector2 offset, SvgTextSpan span) in runs)
        {
            if (!span.Visible)
            {
                continue;
            }

            (Paint? fill, Paint? strokePaint, Stroke? stroke) = TextPaints(span.Style, span.Element, bounds, context.Lengths with { FontSize = span.Style.FontSize }, own);
            if (fill is null && strokePaint is null)
            {
                continue;
            }

            GlyphRun moved = Moved(run, offset);
            if (context.InClip)
            {
                Path outline = moved.ToPath(Vector2.Zero);
                children.Add(new SvgPath(outline.WithFillRule(span.Style.ClipRule)) { Fill = new SolidPaint(new Pixels.RgbaF32(0, 0, 0, 1)), Bounds = outline.TightBounds });
                continue;
            }

            TextBounds mb = moved.Bounds;
            children.Add(new SvgTextRun(moved)
            {
                Fill = fill,
                StrokePaint = strokePaint,
                Stroke = stroke,
                PaintOrder = span.Style.PaintOrder,
                Antialias = antialias,
                Bounds = new RectF(mb.Left, mb.Top, mb.Right - mb.Left, mb.Bottom - mb.Top),
            });
        }

        foreach ((Path path, SvgTextSpan span, TextDecorationLines line) in decorations.Where(d => d.Line == TextDecorationLines.LineThrough))
        {
            AddDecoration(children, path, span.LineThrough, bounds, context.Lengths with { FontSize = span.Style.FontSize }, own, antialias);
        }

        return children.Count == 0 ? null : Decorate(new SvgGroup { Children = children, Bounds = bounds, Transform = transform }, element, style, context);
    }

    private static string FirstValue(string list)
    {
        string[] parts = list.Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0] : "";
    }

    private static GlyphRun Moved(GlyphRun run, Vector2 offset)
    {
        if (offset == Vector2.Zero)
        {
            return run;
        }

        if (run.Transforms.Length > 0)
        {
            GlyphTransform[] transforms = [.. run.Transforms.ToArray().Select(t => t with { Tx = t.Tx + offset.X, Ty = t.Ty + offset.Y })];
            return GlyphRun.CreateTransformed(run.Font, run.Glyphs, transforms, run.Text, run.Clusters);
        }

        Vector2[] positions = [.. run.Positions.ToArray().Select(p => p + offset)];
        return GlyphRun.Create(run.Font, run.Glyphs, positions, run.Text, run.Clusters);
    }

    /// <summary>A span's style for paragraph layout: its face's family, style, size, spacing, features and decorations.</summary>
    private TextStyle LayoutStyle(SvgTextSpan span, bool inShape)
    {
        Font font = span.Font.Font;
        List<FontFeature> features = [];
        if (span.SmallCaps)
        {
            features.Add(new FontFeature(new FontTag("smcp")));
        }

        if (!span.Kerning)
        {
            features.Add(new FontFeature(new FontTag("kern"), 0));
        }

        TextDecorationLines lines = (span.Underline is null ? 0 : TextDecorationLines.Underline) |
            (span.Overline is null ? 0 : TextDecorationLines.Overline) | (span.LineThrough is null ? 0 : TextDecorationLines.LineThrough);
        FontFamily? family = Fonts.FamilyOf(font.Typeface);
        // In a shape, line-height: normal is 1.25 em, as Inkscape (which writes such text) lays it out.
        LineHeight? lineHeight = inShape ? LineHeight.Multiple(1.25f) : null;
        if (span.Style.Css is CssComputedStyle css && css.TryGetValue("line-height", out IReadOnlyList<CssComponentValue>? lh) && !SvgStyleValues.IsKeyword(lh, "normal"))
        {
            if (SvgStyleValues.TryNumber(lh, out float factor))
            {
                lineHeight = LineHeight.Multiple(factor);
            }
            else if (SvgStyleValues.TryLength(lh, out SvgLength length))
            {
                lineHeight = length.IsPercent ? LineHeight.Multiple(length.Value / 100) : LineHeight.Absolute(length.ToPixels(new SvgLengthContext(_options.Dpi, span.Style.FontSize, 16, 0, 0, 0, 0), SvgAxis.Other));
            }
        }

        return new TextStyle
        {
            FontFamilies = family is null ? null : [family],
            FontStyle = font.Typeface.Style,
            Size = font.Size,
            Variations = font.Typeface.Variations.Count > 0 ? font.Typeface.Variations : null,
            Features = features.Count > 0 ? features : null,
            LetterSpacing = span.LetterSpacing,
            WordSpacing = span.WordSpacing,
            LineHeight = lineHeight,
            Decoration = lines == 0 ? null : new TextDecoration { Lines = lines },
        };
    }

    /// <summary>
    /// Flows a paragraph into shapes (shape-inside), Inkscape's way: line boxes one line height apart from the top of the area,
    /// each cut to the intervals where its em box lies wholly inside the area, each interval taking the next line at its width
    /// when the line's first word fits.
    /// </summary>
    private void FlowInShapes(
        Paragraph paragraph, Func<int, SvgTextSpan> owners, List<string> shapes, SvgStyle style, SvgElement element, in SvgContext context,
        List<(GlyphRun Run, Vector2 Offset, SvgTextSpan Span)> runs)
    {
        // Each shape is filled in turn (SVG 2: the text continues in the next shape); shape-subtract and the padding apply to all.
        List<Path> areas = [];
        foreach (string url in shapes)
        {
            if (ShapeGeometry(url, context) is Path p && !p.IsEmpty)
            {
                areas.Add(p);
            }
        }

        if (areas.Count == 0)
        {
            _report.Warn("Svg.BadReference", "The text's shape-inside names no shape; the text is not drawn.", element);
            return;
        }

        SvgLengthContext lengths = context.Lengths with { FontSize = style.FontSize };
        float margin = Length(style.Css, "shape-margin", lengths);
        Path? cut = null;
        if (style.Css is CssComputedStyle css && css.TryGetValue("shape-subtract", out IReadOnlyList<CssComponentValue>? subtract))
        {
            foreach (CssComponentValue item in subtract)
            {
                if (CssUrl.TryParse(item, out CssUrl url) && ShapeGeometry(url.Url, context) is Path c)
                {
                    Path grown = margin > 0 ? c.Op(new Stroke(margin * 2) { Join = LineJoin.Round }.ToPath(c), PathOp.Union) : c;
                    cut = cut is null ? grown : cut.Op(grown, PathOp.Union);
                }
            }
        }

        float padding = Length(style.Css, "shape-padding", lengths);
        LineFlow flow = paragraph.CreateFlow();
        TextStyle first = paragraph.GetStyle(0);
        SvgTextFont font = owners(0).Font;
        float lineHeight = font.Font.Size * 1.25f;
        if (first.LineHeight is LineHeight lh)
        {
            lineHeight = lh.Kind == LineHeightKind.Absolute ? lh.Value : lh.Value * (first.Size ?? 16);
        }

        // Inkscape centres an em box scaled from the font's ascent and descent in the line height.
        float em = font.Font.Size;
        float baseline = ((lineHeight - em) / 2) + (em * font.Ascent / font.Height);
        TextAlign align = paragraph.Style.Align;
        foreach (Path shape in areas)
        {
            Path area = cut is null ? shape : shape.Op(cut, PathOp.Difference);
            if (padding > 0)
            {
                area = area.Op(new Stroke(padding * 2) { Join = LineJoin.Round }.ToPath(area), PathOp.Difference);
            }

            if (area.IsEmpty)
            {
                continue;
            }

            List<Vector2[]> polygons = Polygons(area);
            RectF box = area.TightBounds;
            for (float y = box.Top; !flow.IsComplete && y + lineHeight <= box.Bottom + 0.01f;)
            {
                bool placed = false;
                float advance = lineHeight;
                foreach ((float left, float right) in Free(polygons, y + ((lineHeight - em) / 2), y + ((lineHeight + em) / 2)))
                {
                    float width = right - left;
                    int position = flow.Position;
                    if (flow.IsComplete || !(width > 0) || flow.NextLine(width) is not TextLine line)
                    {
                        continue;
                    }

                    // A line whose first word does not fit the interval waits for a wider one.
                    if (line.Width > width + 0.01f)
                    {
                        flow.Rewind(position);
                        continue;
                    }

                    float shift = align switch
                    {
                        TextAlign.Center => (width - line.Width) / 2,
                        TextAlign.End or TextAlign.Right => width - line.Width,
                        _ => 0,
                    };
                    // A line holding larger text is as high as its largest line height, its baseline as low as the lowest.
                    float lineBaseline = baseline, height = lineHeight;
                    foreach (LayoutRun run in line.Runs)
                    {
                        TextStyle rs = paragraph.GetStyle(run.StyleIndex);
                        SvgTextFont rf = owners(run.StyleIndex).Font;
                        float rh = rs.LineHeight is LineHeight r ? (r.Kind == LineHeightKind.Absolute ? r.Value : r.Value * rf.Font.Size) : rf.Font.Size * 1.25f;
                        height = Math.Max(height, rh);
                        lineBaseline = Math.Max(lineBaseline, ((rh - rf.Font.Size) / 2) + (rf.Font.Size * rf.Ascent / rf.Height));
                    }

                    Vector2 offset = new(left + shift - line.Left, y + lineBaseline - line.Baseline);
                    advance = Math.Max(advance, height);
                    placed = true;
                    foreach (LayoutRun run in line.Runs)
                    {
                        if (run.Glyphs is GlyphRun g)
                        {
                            runs.Add((g, offset, owners(run.StyleIndex)));
                        }
                    }
                }

                // Lines sit on a grid of whole line heights from the shape's top, as Inkscape steps them.
                y += placed ? advance : lineHeight;
            }
        }
    }

    private static float Length(CssComputedStyle? css, string name, in SvgLengthContext lengths) =>
        css is not null && css.TryGetValue(name, out IReadOnlyList<CssComponentValue>? v) && SvgStyleValues.TryLength(v, out SvgLength l) ? Math.Max(0, l.ToPixels(lengths, SvgAxis.Other)) : 0;

    /// <summary>A shape element's outline in the text's coordinates, its own transform applied.</summary>
    private Path? ShapeGeometry(string url, in SvgContext context)
    {
        if (SvgStyleValues.LocalId(url) is not string id || _document.FindById(id) is not SvgElement target || !target.IsSvgOrBare(_document))
        {
            return null;
        }

        (SvgStyle targetStyle, _) = OriginalStyle(target);
        _geometryStyle = (target, targetStyle.Css);
        Path? path;
        try
        {
            path = SvgShapes.Build(target, _geometry, context.Lengths, _limits.MaxPathCommands, _report);
        }
        finally
        {
            _geometryStyle = default;
        }

        if (path is null)
        {
            return null;
        }

        Matrix3x2 m = ElementTransform(target, targetStyle, path.TightBounds, context.Lengths);
        return m.IsIdentity ? path : path.Transform(m);
    }

    /// <summary>The area's outline as polygons, its curves cut into short lines.</summary>
    private static List<Vector2[]> Polygons(Path area)
    {
        List<Vector2[]> polygons = [];
        foreach (ContourMeasure contour in new PathMeasure(area, forceClosed: true).Contours)
        {
            int steps = Math.Clamp((int)MathF.Ceiling(contour.Length / 0.5f), 3, 4096);
            Vector2[] points = new Vector2[steps];
            for (int i = 0; i < steps; i++)
            {
                points[i] = contour.GetPosition(contour.Length * i / steps);
            }

            polygons.Add(points);
        }

        return polygons;
    }

    /// <summary>The x intervals, left to right, where a band from y0 to y1 lies wholly inside the polygons (sampled at its top, middle and bottom).</summary>
    private static List<(float Left, float Right)> Free(List<Vector2[]> polygons, float y0, float y1)
    {
        List<(float, float)>? result = null;
        foreach (float y in (ReadOnlySpan<float>)[y0 + 0.01f, (y0 + y1) / 2, y1 - 0.01f])
        {
            List<float> xs = [];
            foreach (Vector2[] polygon in polygons)
            {
                for (int i = 0; i < polygon.Length; i++)
                {
                    Vector2 a = polygon[i], b = polygon[(i + 1) % polygon.Length];
                    if ((a.Y <= y && b.Y > y) || (b.Y <= y && a.Y > y))
                    {
                        xs.Add(a.X + ((y - a.Y) / (b.Y - a.Y) * (b.X - a.X)));
                    }
                }
            }

            xs.Sort();
            List<(float, float)> line = [];
            for (int i = 0; i + 1 < xs.Count; i += 2)
            {
                line.Add((xs[i], xs[i + 1]));
            }

            result = result is null ? line : Intersect(result, line);
        }

        return result ?? [];
    }

    private static List<(float, float)> Intersect(List<(float L, float R)> a, List<(float L, float R)> b)
    {
        List<(float, float)> result = [];
        foreach ((float l1, float r1) in a)
        {
            foreach ((float l2, float r2) in b)
            {
                float l = Math.Max(l1, l2), r = Math.Min(r1, r2);
                if (r > l)
                {
                    result.Add((l, r));
                }
            }
        }

        result.Sort();
        return result;
    }
}
