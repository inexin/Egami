using System.Numerics;

using Egami.Fonts.Unicode;
using Egami.Pixels;

namespace Egami.Fonts.Layout;

/// <summary>A piece of a decoration line along a text line: its left and right x.</summary>
/// <param name="Left">The piece's left x.</param>
/// <param name="Right">The piece's right x.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="decorations"/>
/// </example>
public readonly record struct DecorationSegment(float Left, float Right);

/// <summary>
/// One decoration line over a decorated stretch of one text line: an underline, overline or line-through at one position and
/// thickness for the whole stretch (the lowest underline and thickest line among its fonts, as CSS), cut where glyphs cross an
/// underline or overline when <see cref="TextDecoration.SkipInk"/> is Auto.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="decorations"/>
/// </example>
public sealed class DecorationShape
{
    internal DecorationShape(TextDecorationLines line, TextDecoration decoration, Rgba8 color, int lineIndex, int styleIndex, float top, float thickness, IReadOnlyList<DecorationSegment> segments)
    {
        Line = line;
        Style = decoration.Style;
        Color = decoration.Color ?? color;
        Paint = decoration.Paint;
        LineIndex = lineIndex;
        StyleIndex = styleIndex;
        Top = top;
        Thickness = thickness;
        Segments = segments;
    }

    /// <summary>Gets which line this is: one of underline, overline and line-through.</summary>
    public TextDecorationLines Line { get; }

    /// <summary>Gets how the line is drawn.</summary>
    public TextDecorationStyle Style { get; }

    /// <summary>Gets the line's color: the decoration's, else the text's.</summary>
    public Rgba8 Color { get; }

    /// <summary>Gets the line's paint, when the decoration has one.</summary>
    public ITextPaint? Paint { get; }

    /// <summary>Gets the index of the text line in <see cref="TextLayout.Lines"/>.</summary>
    public int LineIndex { get; }

    /// <summary>Gets the style of the decorated text: 0 for the paragraph's default style, else 1 plus the span's index.</summary>
    public int StyleIndex { get; }

    /// <summary>Gets the y of the line's top edge (for a double line, the first line's).</summary>
    public float Top { get; }

    /// <summary>Gets the line's thickness.</summary>
    public float Thickness { get; }

    /// <summary>Gets the pieces the line is drawn in, left to right, skip-ink gaps already cut.</summary>
    public IReadOnlyList<DecorationSegment> Segments { get; }

    /// <summary>
    /// Writes the line's geometry as filled shapes: a rectangle per segment for solid lines, two for double lines (a pixel apart, as
    /// Chromium draws them), round dots one thickness wide and one thickness apart for dotted lines, dashes three thicknesses long for dashed
    /// lines, and a wave of amplitude 1.5 thicknesses for wavy lines.
    /// </summary>
    /// <param name="sink">Where the outlines go, in layout coordinates with y down.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/> is null.</exception>
    public void AddTo(IGlyphOutlineSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        float t = Thickness;
        foreach (DecorationSegment s in Segments)
        {
            switch (Style)
            {
                case TextDecorationStyle.Double:
                    Rectangle(sink, s.Left, Top, s.Right, Top + t);
                    Rectangle(sink, s.Left, Top + t + 1, s.Right, Top + (2 * t) + 1);
                    break;
                case TextDecorationStyle.Dotted:
                    for (float x = s.Left; x + t <= s.Right + 0.001f; x += 2 * t)
                    {
                        Circle(sink, x + (t / 2), Top + (t / 2), t / 2);
                    }

                    break;
                case TextDecorationStyle.Dashed:
                    for (float x = s.Left; x < s.Right; x += 6 * t)
                    {
                        Rectangle(sink, x, Top, Math.Min(x + (3 * t), s.Right), Top + t);
                    }

                    break;
                case TextDecorationStyle.Wavy:
                    Wave(sink, s.Left, s.Right, Top + (t / 2), 1.5f * t, t);
                    break;
                default:
                    Rectangle(sink, s.Left, Top, s.Right, Top + t);
                    break;
            }
        }
    }

    private static void Rectangle(IGlyphOutlineSink sink, float x0, float y0, float x1, float y1)
    {
        if (x1 <= x0 || y1 <= y0)
        {
            return;
        }

        sink.MoveTo(x0, y0);
        sink.LineTo(x1, y0);
        sink.LineTo(x1, y1);
        sink.LineTo(x0, y1);
        sink.Close();
    }

    private static void Circle(IGlyphOutlineSink sink, float cx, float cy, float r)
    {
        const float K = 0.5522848f;
        sink.MoveTo(cx + r, cy);
        sink.CubicTo(cx + r, cy + (K * r), cx + (K * r), cy + r, cx, cy + r);
        sink.CubicTo(cx - (K * r), cy + r, cx - r, cy + (K * r), cx - r, cy);
        sink.CubicTo(cx - r, cy - (K * r), cx - (K * r), cy - r, cx, cy - r);
        sink.CubicTo(cx + (K * r), cy - r, cx + r, cy - (K * r), cx + r, cy);
        sink.Close();
    }

    /// <summary>A wave of quadratic arcs, a period every eight amplitudes, filled as a band one thickness tall.</summary>
    private static void Wave(IGlyphOutlineSink sink, float x0, float x1, float cy, float amplitude, float thickness)
    {
        if (x1 <= x0)
        {
            return;
        }

        float half = 4 * amplitude / 2;
        List<Vector2> centre = [];
        bool up = true;
        for (float x = x0; x < x1; x += half)
        {
            float next = Math.Min(x + half, x1);
            centre.Add(new Vector2(x, cy));
            centre.Add(new Vector2((x + next) / 2, cy + (up ? -amplitude * 2 : amplitude * 2) * ((next - x) / half)));
            up = !up;
        }

        centre.Add(new Vector2(x1, cy));
        float h = thickness / 2;
        sink.MoveTo(centre[0].X, centre[0].Y - h);
        for (int i = 1; i + 1 < centre.Count; i += 2)
        {
            sink.QuadTo(centre[i].X, centre[i].Y - h, centre[i + 1].X, centre[i + 1].Y - h);
        }

        sink.LineTo(centre[^1].X, centre[^1].Y + h);
        for (int i = centre.Count - 2; i - 1 >= 0; i -= 2)
        {
            sink.QuadTo(centre[i].X, centre[i].Y + h, centre[i - 1].X, centre[i - 1].Y + h);
        }

        sink.Close();
    }
}

/// <content>Decorations and the layout as outlines (design stage E).</content>
public sealed partial class TextLayout
{
    private DecorationShape[]? _decorations;

    /// <summary>
    /// Gets the decoration lines, line by line: underlines and overlines (with skip-ink gaps cut) and line-throughs, each over a
    /// stretch of text with one decoration. Vertical layouts have none yet.
    /// </summary>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="decorations"/>
    /// </example>
    public IReadOnlyList<DecorationShape> Decorations => _decorations ??= ComputeDecorations();

    /// <summary>
    /// Writes the whole layout as outlines: every glyph (color glyphs as their outlines) at its place, then the decorations.
    /// </summary>
    /// <param name="sink">Where the outlines go, in layout coordinates with y down.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/> is null.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="decorations"/>
    /// </example>
    public void ToPath(IGlyphOutlineSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        foreach (TextLine line in Lines)
        {
            foreach (LayoutRun run in line.Runs)
            {
                if (run.Glyphs is GlyphRun glyphs)
                {
                    AddGlyphs(glyphs, sink);
                }
            }
        }

        foreach (DecorationShape shape in Decorations)
        {
            shape.AddTo(sink);
        }
    }

    /// <summary>Writes a glyph run's outlines, each glyph scaled from font units and placed (and turned, for sideways glyphs).</summary>
    private static void AddGlyphs(GlyphRun run, IGlyphOutlineSink sink)
    {
        Font font = run.Font;
        float scale = font.Scale;
        ReadOnlySpan<Vector2> positions = run.Positions;
        ReadOnlySpan<GlyphTransform> transforms = run.Transforms;
        for (int g = 0; g < run.Count; g++)
        {
            Matrix3x2 m = transforms.Length > 0
                ? new Matrix3x2(transforms[g].ScaleCos * scale, transforms[g].ScaleSin * scale, transforms[g].ScaleSin * scale, -transforms[g].ScaleCos * scale, transforms[g].Tx, transforms[g].Ty)
                : new Matrix3x2(scale, 0, 0, -scale, positions[g].X, positions[g].Y);
            font.Typeface.GetGlyphOutline(run.Glyphs[g], new TransformSink(sink, m));
        }
    }

    private DecorationShape[] ComputeDecorations()
    {
        if (Vertical)
        {
            return [];
        }

        List<DecorationShape> shapes = [];
        string text = Paragraph.Text;
        RichText rich = Paragraph.RichText;
        for (int li = 0; li < Lines.Count; li++)
        {
            TextLine line = Lines[li];
            // Chromium decorates the spaces hanging at a wrap too; only the line break itself goes undecorated.
            int contentEnd = line.End;
            while (contentEnd > line.Start && text[contentEnd - 1] is '\n' or '\r' or '\u2028' or '\u2029' or '\u000B' or '\u000C' or '\u0085')
            {
                contentEnd--;
            }

            // Stretches: visually adjacent clusters with the same decoration.
            List<(TextDecoration Decoration, int Style, float Left, float Right, List<(Font Font, GlyphRun Run, int Glyph)> Glyphs)> stretches = [];
            foreach (LayoutRun run in line.Runs)
            {
                if (run.Glyphs is not GlyphRun glyphs)
                {
                    if (run.Kind == LayoutRunKind.Tab && Decoration(run.Start) is (TextDecoration td, int ts))
                    {
                        Extend(stretches, td, ts, run.X, run.X + run.Advance, null);
                    }
                    else
                    {
                        stretches.Add((null!, -1, run.X, run.X, []));
                    }

                    continue;
                }

                float pen = run.X;
                for (int g = 0; g < glyphs.Count; g++)
                {
                    float advance = glyphs.AdvanceAt(g);
                    int cluster = glyphs.Clusters[g];
                    if (cluster < contentEnd && (run.Length > 0) && Decoration(cluster) is (TextDecoration d, int s))
                    {
                        Extend(stretches, d, s, pen, pen + advance, (glyphs.Font, glyphs, g));
                    }
                    else
                    {
                        stretches.Add((null!, -1, pen, pen, []));
                    }

                    pen += advance;
                }
            }

            foreach (var stretch in stretches.Where(s => s.Decoration is not null))
            {
                Font[] fonts = [.. stretch.Glyphs.Select(g => g.Font).Distinct()];
                if (fonts.Length == 0 && Paragraph.Styles[stretch.Style].Primary is Font primary)
                {
                    fonts = [primary];
                }

                if (fonts.Length == 0)
                {
                    continue;
                }

                TextDecoration d = stretch.Decoration;
                Rgba8 color = Paragraph.Styles[stretch.Style].Style.Color ?? new Rgba8(0, 0, 0, 255);
                foreach (TextDecorationLines kind in new[] { TextDecorationLines.Underline, TextDecorationLines.Overline, TextDecorationLines.LineThrough })
                {
                    if ((d.Lines & kind) == 0)
                    {
                        continue;
                    }

                    (float top, float thickness, float dilation) = Place(kind, d, fonts, line.Baseline);
                    IReadOnlyList<DecorationSegment> segments = [new DecorationSegment(stretch.Left, stretch.Right)];
                    if (kind != TextDecorationLines.LineThrough && d.SkipInk == TextDecorationSkipInk.Auto)
                    {
                        segments = SkipInk(stretch.Left, stretch.Right, top, thickness, dilation, stretch.Glyphs);
                    }

                    shapes.Add(new DecorationShape(kind, d, color, li, stretch.Style, top, thickness, segments));
                }
            }
        }

        return [.. shapes];
    }

    private (TextDecoration, int)? Decoration(int index)
    {
        int style = Paragraph.RichText.SpanAt(index) + 1;
        return Paragraph.Styles[style].Style.Decoration is TextDecoration d && d.Lines != TextDecorationLines.None ? (d, style) : null;
    }

    private static void Extend(List<(TextDecoration Decoration, int Style, float Left, float Right, List<(Font Font, GlyphRun Run, int Glyph)> Glyphs)> stretches, TextDecoration d, int style, float left, float right, (Font, GlyphRun, int)? glyph)
    {
        if (stretches.Count > 0 && ReferenceEquals(stretches[^1].Decoration, d) && Math.Abs(stretches[^1].Right - left) < 0.01f)
        {
            var last = stretches[^1];
            if (glyph is { } add)
            {
                last.Glyphs.Add(add);
            }

            stretches[^1] = (last.Decoration, last.Style, last.Left, Math.Max(last.Right, right), last.Glyphs);
            return;
        }

        stretches.Add((d, style, left, right, glyph is { } g ? [g] : []));
    }

    /// <summary>
    /// The line's top and thickness for a stretch, as Chromium places them: one thickness for all lines, a tenth of the largest
    /// font size (or the decoration's) floored to whole pixels and at least one; the underline half a thickness (rounded up, at
    /// least a pixel) below the baseline, or the decoration's offset below it; the overline on the highest rounded ascent; the
    /// line-through centred a third of the first font's rounded ascent above the baseline, on a whole pixel. Skip-ink gaps
    /// widen by the thickness before flooring.
    /// </summary>
    internal static (float Top, float Thickness, float Dilation) Place(TextDecorationLines kind, TextDecoration d, IReadOnlyList<Font> fonts, float baseline)
    {
        float raw = d.Thickness ?? Math.Max(1, fonts.Max(f => f.Size) / 10);
        float thickness = Math.Max(1, MathF.Floor(raw));
        switch (kind)
        {
            case TextDecorationLines.Underline:
                return (baseline + (d.UnderlineOffset is float offset ? MathF.Round(offset) : Math.Max(1, MathF.Ceiling(raw / 2))), thickness, raw);
            case TextDecorationLines.Overline:
                return (baseline - fonts.Max(f => LineBuilder.Rounded(f).Ascent) - thickness, thickness, raw);
            default:
                return (baseline - MathF.Round((LineBuilder.Rounded(fonts[0]).Ascent / 3) + (thickness / 2)), thickness, raw);
        }
    }

    /// <summary>
    /// The segments left of a line once glyph ink crossing its band is cut out, each crossing widened by
    /// <paramref name="dilation"/> on each side, as Chromium cuts them; Chinese, Japanese and Korean glyphs and color glyphs never cut.
    /// </summary>
    private List<DecorationSegment> SkipInk(float left, float right, float top, float thickness, float dilation, List<(Font Font, GlyphRun Run, int Glyph)> glyphs)
    {
        List<(float L, float R)> gaps = [];
        string text = Paragraph.Text;
        foreach ((Font font, GlyphRun run, int g) in glyphs)
        {
            int cp = TextSegmenter.CodePointAt(text, run.Clusters[g], out _);
            if (IsCjk(cp) || font.Typeface.HasColorGlyph(run.Glyphs[g]))
            {
                continue;
            }

            InterceptSink sink = new(top, top + thickness);
            Vector2 p = run.Positions[g];
            font.Typeface.GetGlyphOutline(run.Glyphs[g], new TransformSink(sink, new Matrix3x2(font.Scale, 0, 0, -font.Scale, p.X, p.Y)));
            if (sink.Min <= sink.Max)
            {
                gaps.Add((sink.Min - dilation, sink.Max + dilation));
            }
        }

        List<DecorationSegment> segments = [];
        float x = left;
        foreach ((float l, float r) in gaps.OrderBy(g => g.L))
        {
            if (l > x)
            {
                segments.Add(new DecorationSegment(x, Math.Min(l, right)));
            }

            x = Math.Max(x, r);
        }

        if (x < right)
        {
            segments.Add(new DecorationSegment(x, right));
        }

        return [.. segments.Where(s => s.Right - s.Left > 0.01f)];
    }

    private static bool IsCjk(int cp) =>
        cp is (>= 0x1100 and <= 0x11FF) or (>= 0x2E80 and <= 0x2FDF) or (>= 0x3000 and <= 0x9FFF) or (>= 0xAC00 and <= 0xD7FF)
            or (>= 0xF900 and <= 0xFAFF) or (>= 0xFF00 and <= 0xFFEF) or (>= 0x20000 and <= 0x3FFFF);

    /// <summary>Passes outlines on through an affine transform.</summary>
    private sealed class TransformSink(IGlyphOutlineSink inner, Matrix3x2 m) : IGlyphOutlineSink
    {
        public void MoveTo(float x, float y)
        {
            Vector2 p = Vector2.Transform(new Vector2(x, y), m);
            inner.MoveTo(p.X, p.Y);
        }

        public void LineTo(float x, float y)
        {
            Vector2 p = Vector2.Transform(new Vector2(x, y), m);
            inner.LineTo(p.X, p.Y);
        }

        public void QuadTo(float cx, float cy, float x, float y)
        {
            Vector2 c = Vector2.Transform(new Vector2(cx, cy), m);
            Vector2 p = Vector2.Transform(new Vector2(x, y), m);
            inner.QuadTo(c.X, c.Y, p.X, p.Y);
        }

        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
        {
            Vector2 a = Vector2.Transform(new Vector2(c1x, c1y), m);
            Vector2 b = Vector2.Transform(new Vector2(c2x, c2y), m);
            Vector2 p = Vector2.Transform(new Vector2(x, y), m);
            inner.CubicTo(a.X, a.Y, b.X, b.Y, p.X, p.Y);
        }

        public void Close() => inner.Close();
    }

    /// <summary>The leftmost and rightmost x where an outline (flattened) lies inside a horizontal band.</summary>
    private sealed class InterceptSink(float top, float bottom) : IGlyphOutlineSink
    {
        private Vector2 _current;
        private Vector2 _start;

        public float Min { get; private set; } = float.MaxValue;

        public float Max { get; private set; } = float.MinValue;

        public void MoveTo(float x, float y) => _current = _start = new Vector2(x, y);

        public void LineTo(float x, float y)
        {
            Segment(_current, new Vector2(x, y));
            _current = new Vector2(x, y);
        }

        public void QuadTo(float cx, float cy, float x, float y)
        {
            Vector2 p0 = _current, c = new(cx, cy), p1 = new(x, y);
            const int N = 12;
            Vector2 previous = p0;
            for (int i = 1; i <= N; i++)
            {
                float t = i / (float)N, u = 1 - t;
                Vector2 q = (u * u * p0) + (2 * u * t * c) + (t * t * p1);
                Segment(previous, q);
                previous = q;
            }

            _current = p1;
        }

        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
        {
            Vector2 p0 = _current, a = new(c1x, c1y), b = new(c2x, c2y), p1 = new(x, y);
            const int N = 16;
            Vector2 previous = p0;
            for (int i = 1; i <= N; i++)
            {
                float t = i / (float)N, u = 1 - t;
                Vector2 q = (u * u * u * p0) + (3 * u * u * t * a) + (3 * u * t * t * b) + (t * t * t * p1);
                Segment(previous, q);
                previous = q;
            }

            _current = p1;
        }

        public void Close()
        {
            Segment(_current, _start);
            _current = _start;
        }

        /// <summary>Clips a segment to the band and widens the range by the clipped part's x extent.</summary>
        private void Segment(Vector2 a, Vector2 b)
        {
            float y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y);
            if (y1 < top || y0 > bottom)
            {
                return;
            }

            float dy = b.Y - a.Y;
            float t0 = 0, t1 = 1;
            if (Math.Abs(dy) > 1e-6f)
            {
                float ta = (top - a.Y) / dy, tb = (bottom - a.Y) / dy;
                t0 = Math.Max(0, Math.Min(ta, tb));
                t1 = Math.Min(1, Math.Max(ta, tb));
                if (t0 > t1)
                {
                    return;
                }
            }

            float xa = a.X + ((b.X - a.X) * t0), xb = a.X + ((b.X - a.X) * t1);
            Min = Math.Min(Min, Math.Min(xa, xb));
            Max = Math.Max(Max, Math.Max(xa, xb));
        }
    }
}
