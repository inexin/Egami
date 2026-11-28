using System.Numerics;
using System.Text;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Fonts.Layout;
using Egami.Fonts.Shaping;
using Egami.Pixels;

using Path = Egami.Drawing.Path;

namespace Egami.Video.Subtitles.Rendering;

/// <summary>
/// The script-wide settings layout needs: the script resolution positions and sizes are in, whether borders and shadows scale
/// with it, the wrap style, and for WebVTT the CSS look (sizes are em sizes, boxes hug each line).
/// </summary>
internal sealed record ScriptSettings(double PlayResX, double PlayResY, bool ScaledBorderAndShadow, int WrapStyle, Size? LayoutRes, bool Css)
{
    /// <summary>Reads the settings of a document: ASS's [Script Info], and the built-in ones of SRT and WebVTT.</summary>
    public static ScriptSettings Of(SubtitleDocument document, Size? authored)
    {
        if (document.Format == SubtitleFormat.WebVtt)
        {
            return new ScriptSettings(384, 288, true, 0, authored, true);
        }

        if (document.Format == SubtitleFormat.Srt)
        {
            return new ScriptSettings(384, 288, true, 0, authored, false);
        }

        double x = Number(document, "PlayResX"), y = Number(document, "PlayResY");
        (x, y) = (x, y) switch
        {
            ( > 0, > 0) => (x, y),
            ( > 0, _) => (x, x == 1280 ? 1024 : x * 3 / 4),
            (_, > 0) => (y == 1024 ? 1280 : y * 4 / 3, y),
            _ => (384, 288),
        };
        string? scaled = Text(document, "ScaledBorderAndShadow");
        double lx = Number(document, "LayoutResX"), ly = Number(document, "LayoutResY");
        Size? layout = lx > 0 && ly > 0 ? new Size((int)lx, (int)ly) : authored;
        int wrap = (int)Number(document, "WrapStyle");
        return new ScriptSettings(x, y, scaled is not null && scaled.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase), wrap is >= 0 and <= 3 ? wrap : 0, layout, false);
    }

    private static string? Text(SubtitleDocument document, string key)
    {
        foreach (KeyValuePair<string, string> pair in document.ScriptInfo)
        {
            if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static double Number(SubtitleDocument document, string key) =>
        double.TryParse(Text(document, key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0;
}

/// <summary>
/// Lays out one cue and draws it into an <see cref="CueImage"/>: lines broken the way libass breaks them (greedy, then the
/// balancing of wrap styles 0 and 3), each line shaped by Egami.Fonts, placed by its alignment and margins, then drawn as
/// shadow, outline (or box) and fill.
/// </summary>
internal sealed class CueLayout
{
    private readonly FontResolver _fonts;
    private readonly ScriptSettings _script;
    private readonly Dictionary<Typeface, float> _emRatios = [];

    /// <summary>
    /// Kerning and standard ligatures off, as libass shapes ASS for compatibility with VSFilter, which drew with GDI and neither
    /// kerned nor joined letters.
    /// </summary>
    private static readonly IReadOnlyList<FontFeature> AssFeatures =
        [new FontFeature(new FontTag("kern"), 0), new FontFeature(new FontTag("liga"), 0), new FontFeature(new FontTag("clig"), 0)];

    public CueLayout(FontResolver fonts, ScriptSettings script)
    {
        _fonts = fonts;
        _script = script;
    }

    /// <summary>One shaped line: its layout, width, ascent and descent before ScaleX and ScaleY.</summary>
    private sealed record Line(TextLayout? Layout, float Width, float Ascent, float Descent, float LastAdvance = 0);

    /// <summary>Draws a cue for a frame of a size, or returns <see langword="null"/> when nothing shows.</summary>
    public CueImage? Draw(SubtitleCue cue, SubtitleStyle style, int width, int height, EgamiContext egami)
    {
        double scaleX = width / _script.PlayResX, scaleY = height / _script.PlayResY;
        double fontScale = _script.Css ? 1 : scaleY;
        double stretch = _script.LayoutRes is Size authored && authored.Width > 0 && authored.Height > 0
            ? ((double)width / authored.Width) / ((double)height / authored.Height) : 1;
        float sx = (float)(style.ScaleX / 100 * stretch), sy = (float)(style.ScaleY / 100);
        if (sx <= 0 || sy <= 0)
        {
            return null;
        }

        // The text and its style runs; ASS drawings (\p) are left for the full ASS renderer.
        var text = new StringBuilder();
        var runs = new List<(int Start, int Length, TextStyle Style)>();
        foreach (SubtitleSpan span in cue.Spans)
        {
            if (span.IsDrawing || span.Text.Length == 0)
            {
                continue;
            }

            TextStyle? textStyle = StyleOf(span, style, fontScale, _script.Css ? 1 : scaleX, height);
            if (textStyle is null)
            {
                continue;
            }

            runs.Add((text.Length, span.Text.Length, textStyle));
            text.Append(span.Text);
        }

        string all = text.ToString();
        if (all.Trim().Length == 0 || runs.Count == 0)
        {
            return null;
        }

        int marginLeft = cue.MarginLeft != 0 ? cue.MarginLeft : style.MarginLeft;
        int marginRight = cue.MarginRight != 0 ? cue.MarginRight : style.MarginRight;
        int marginVertical = cue.MarginVertical != 0 ? cue.MarginVertical : style.MarginVertical;
        float left = (float)(marginLeft * scaleX), right = (float)(width - (marginRight * scaleX));
        float maxWidth = Math.Max(1, right - left);
        // Fields a span leaves unset come from the paragraph's default style, so it carries no decoration or language of its own.
        ParagraphStyle paragraph = new() { Fallback = _fonts.Fallback, DefaultStyle = runs[0].Style with { Decoration = null, Language = null } };

        // Lines: each hard break (\N) starts a segment, and each segment wraps to the margins.
        var lines = new List<Line>();
        int segmentStart = 0;
        while (segmentStart <= all.Length)
        {
            int end = all.IndexOf('\n', segmentStart);
            if (end < 0)
            {
                end = all.Length;
            }

            foreach ((int a, int b) in Wrap(all, segmentStart, end, runs, paragraph, maxWidth / sx))
            {
                lines.Add(Shape(all, a, b, runs, paragraph));
            }

            segmentStart = end + 1;
        }

        // Vertical place: the lines stack at their ascent plus descent, the block sits by the alignment and margin.
        float total = 0;
        foreach (Line line in lines)
        {
            total += (line.Ascent + line.Descent) * sy;
        }

        SubtitleAlignment alignment = cue.Alignment ?? style.Alignment;
        int row = ((int)alignment - 1) / 3, column = ((int)alignment - 1) % 3;
        float top = row switch
        {
            0 => (float)(height - (marginVertical * scaleY)) - total,
            2 => (float)(marginVertical * scaleY),
            _ => (height - total) / 2,
        };

        bool box = style.BorderStyle == 3;
        // Scaled borders and shadows scale as the script's x and y do (libass), so they are wider than tall on a 4:3 script
        // shown at 16:9; WebVTT's are in frame pixels.
        bool scaled = _script.ScaledBorderAndShadow && !_script.Css;
        float border = (float)(Math.Max(0, style.Outline) * (_script.ScaledBorderAndShadow ? scaleY : 1));
        float borderX = (float)(Math.Max(0, style.Outline) * (scaled ? scaleX : _script.ScaledBorderAndShadow ? scaleY : 1));
        float shadow = (float)(style.Shadow * (_script.ScaledBorderAndShadow ? scaleY : 1));
        float shadowX = (float)(style.Shadow * (scaled ? scaleX : _script.ScaledBorderAndShadow ? scaleY : 1));
        if (box && !_script.Css)
        {
            // libass pads an opaque box by at least a pixel.
            border = Math.Max(1, border);
            borderX = Math.Max(1, borderX);
        }

        var placed = new List<(Line Line, Vector2 Origin)>(lines.Count);
        RectF bounds = default;
        bool any = false;
        float y = top;
        foreach (Line line in lines)
        {
            float baseline = y + (line.Ascent * sy);
            float w = line.Width * sx;
            float x = column switch
            {
                0 => left,
                2 => right - w,
                _ => ((left + right) / 2) - (w / 2),
            };
            y += (line.Ascent + line.Descent) * sy;
            if (line.Layout is null)
            {
                continue;
            }

            placed.Add((line, new Vector2(x, baseline)));
            RectF extent = new(x, baseline - (line.Ascent * sy), w, (line.Ascent + line.Descent) * sy);
            bounds = any ? Union(bounds, extent) : extent;
            any = true;
        }

        if (!any)
        {
            return null;
        }

        // The shapes, in frame pixels: each line's glyphs and decorations with their colors, its outline and its box.
        Matrix3x2 Place(Vector2 origin) => Matrix3x2.CreateScale(sx, sy) * Matrix3x2.CreateTranslation(origin);
        var fills = new List<(Path Path, Rgba8 Color)>();
        var outline = new PathBuilder();
        var boxes = new PathBuilder();
        foreach ((Line line, Vector2 origin) in placed)
        {
            TextLayout layout = line.Layout!;
            TextLine first = layout.Lines[0];
            var at = new Vector2(-first.Left, -first.Baseline);
            Matrix3x2 matrix = Place(origin);
            foreach (TextLine textLine in layout.Lines)
            {
                foreach (LayoutRun run in textLine.Runs)
                {
                    if (run.Glyphs is GlyphRun glyphs && glyphs.Count > 0)
                    {
                        Path path = glyphs.ToPath(at).Transform(matrix);
                        fills.Add((path, layout.Paragraph.GetStyle(run.StyleIndex).Color ?? style.PrimaryColor));
                        outline.AddPath(path);
                        bounds = Union(bounds, path.Bounds);
                    }
                }
            }

            foreach (DecorationShape decoration in layout.Decorations)
            {
                // libass lines each glyph's advance, so a decorated stretch ends without the letter spacing after its last glyph.
                float spacing = layout.Paragraph.GetStyle(decoration.StyleIndex).LetterSpacing ?? 0;
                var pieces = new PathBuilder();
                for (int i = 0; i < decoration.Segments.Count; i++)
                {
                    DecorationSegment segment = decoration.Segments[i];
                    float end = i == decoration.Segments.Count - 1 ? Math.Max(segment.Left, segment.Right - spacing) : segment.Right;
                    pieces.AddRect(new RectF(segment.Left + at.X, decoration.Top + at.Y, end - segment.Left, decoration.Thickness));
                }

                Path path = pieces.ToPath().Transform(matrix);
                fills.Add((path, decoration.Color));
                outline.AddPath(path);
                bounds = Union(bounds, path.Bounds);
            }

            if (box)
            {
                RectF lineBox = BoxOf(line, origin, sx, sy, borderX, border);
                boxes.AddRect(lineBox);
                bounds = Union(bounds, lineBox);
            }
        }

        Path outlinePath = outline.ToPath();
        Path boxPath = boxes.ToPath();
        float reach = Math.Max(border, borderX) + 2;
        RectF area = new(bounds.X - reach, bounds.Y - reach, bounds.Width + (2 * reach), bounds.Height + (2 * reach));
        if (shadow != 0 || shadowX != 0)
        {
            area = Union(area, new RectF(area.X + shadowX, area.Y + shadow, area.Width, area.Height));
        }

        int x0 = Math.Max(0, (int)Math.Floor(area.X)), y0 = Math.Max(0, (int)Math.Floor(area.Y));
        int x1 = Math.Min(width, (int)Math.Ceiling(area.Right)), y1 = Math.Min(height, (int)Math.Ceiling(area.Bottom));
        if (x1 <= x0 || y1 <= y0)
        {
            return null;
        }

        Raster<Rgba8> raster = Raster.Create<Rgba8>(x1 - x0, y1 - y0, egami);
        try
        {
            using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
            {
                canvas.Translate(-x0, -y0);
                Stroke? pen = border > 0 ? new Stroke(2 * border) { Join = LineJoin.Round, Cap = LineCap.Round } : null;
                if (box)
                {
                    // BorderStyle 3: an opaque box in the outline color around each line, its shadow in the back color.
                    if (shadow != 0 && style.BackColor.A > 0)
                    {
                        Layer(canvas, style.BackColor, c =>
                        {
                            c.Translate(shadowX, shadow);
                            c.FillPath(boxPath, Opaque(style.BackColor));
                        });
                    }

                    if (style.OutlineColor.A > 0)
                    {
                        Layer(canvas, style.OutlineColor, c => c.FillPath(boxPath, Opaque(style.OutlineColor)));
                    }
                }
                else
                {
                    // BorderStyle 1: the shadow is the text with its outline, moved; the outline is the text widened.
                    if (shadow != 0 && style.BackColor.A > 0)
                    {
                        Layer(canvas, style.BackColor, c =>
                        {
                            c.Translate(shadowX, shadow);
                            Widened(c, outlinePath, pen, borderX / border, Opaque(style.BackColor));
                        });
                    }

                    if (pen is not null && style.OutlineColor.A > 0)
                    {
                        Layer(canvas, style.OutlineColor, c => Widened(c, outlinePath, pen, borderX / border, Opaque(style.OutlineColor)));
                    }
                }

                foreach ((Path path, Rgba8 color) in fills)
                {
                    if (color.A > 0)
                    {
                        canvas.FillPath(path, color);
                    }
                }
            }

            return CueImage.From(raster, x0, y0);
        }
        finally
        {
            raster.Dispose();
        }
    }

    /// <summary>Fills a path and strokes it with a pen <paramref name="stretch"/> times as wide across as it is tall.</summary>
    private static void Widened(Canvas canvas, Path path, Stroke? pen, float stretch, Rgba8 color)
    {
        canvas.FillPath(path, color);
        if (pen is null)
        {
            return;
        }

        if (!float.IsFinite(stretch) || stretch <= 0 || Math.Abs(stretch - 1) < 1e-4f)
        {
            canvas.StrokePath(path, pen, color);
            return;
        }

        canvas.Save();
        canvas.Scale(stretch, 1);
        canvas.StrokePath(path.Transform(Matrix3x2.CreateScale(1 / stretch, 1)), pen, color);
        canvas.Restore();
    }

    /// <summary>Draws opaque shapes in a layer merged at the color's alpha, so overlapping shapes fade as one.</summary>
    private static void Layer(Canvas canvas, Rgba8 color, Action<Canvas> draw)
    {
        if (color.A == 255)
        {
            canvas.Save();
            draw(canvas);
            canvas.Restore();
            return;
        }

        canvas.SaveLayer(color.A / 255f);
        draw(canvas);
        canvas.Restore();
    }

    private static Rgba8 Opaque(Rgba8 color) => new(color.R, color.G, color.B, 255);

    private static RectF Union(RectF a, RectF b)
    {
        float l = Math.Min(a.X, b.X), t = Math.Min(a.Y, b.Y);
        return new RectF(l, t, Math.Max(a.Right, b.Right) - l, Math.Max(a.Bottom, b.Bottom) - t);
    }

    /// <summary>The Egami.Fonts style of a span: its family, the size in frame pixels, weight, slant, color and decorations.</summary>
    private TextStyle? StyleOf(SubtitleSpan span, SubtitleStyle style, double fontScale, double spacingScale, int height)
    {
        FontFamily? family = _fonts.Resolve(span.Font ?? style.FontName);
        if (family is null)
        {
            return null;
        }

        var fontStyle = new FontStyle(span.Bold ? 700 : 400, 5, span.Italic ? FontSlant.Italic : FontSlant.Upright);
        double size = span.FontSize ?? style.FontSize;
        float em;
        if (_script.Css)
        {
            // WebVTT: CSS sizes are em sizes, 5% of the video's height by default (W3C WebVTT, the default ::cue font).
            em = (float)(size / 288 * height);
        }
        else
        {
            // ASS sizes are cell heights, as Windows GDI takes them: the Windows ascent plus descent is the size.
            em = (float)(size * fontScale * EmRatio(family.Match(fontStyle).Typeface));
        }

        if (!(em > 0) || !float.IsFinite(em))
        {
            return null;
        }

        TextDecorationLines lines = (span.Underline ? TextDecorationLines.Underline : TextDecorationLines.None)
            | (span.Strikeout ? TextDecorationLines.LineThrough : TextDecorationLines.None);
        return new TextStyle
        {
            FontFamilies = [family],
            Size = Math.Min(em, 4096),
            FontStyle = fontStyle,
            Color = span.Color ?? style.PrimaryColor,
            Decoration = lines == TextDecorationLines.None ? null : new TextDecoration { Lines = lines },
            LetterSpacing = style.Spacing == 0 ? null : (float)(style.Spacing * spacingScale), // libass spaces letters in script x units
            Language = span.Language,
            Features = _script.Css ? null : AssFeatures,
        };
    }

    /// <summary>The em size of a face whose Windows ascent plus descent is one pixel.</summary>
    private float EmRatio(Typeface face)
    {
        if (!_emRatios.TryGetValue(face, out float ratio))
        {
            VerticalMetrics win = face.WinMetrics, hhea = face.HheaMetrics;
            int cell = win.Ascent - win.Descent;
            if (cell <= 0)
            {
                cell = hhea.Ascent - hhea.Descent;
            }

            ratio = cell > 0 ? (float)face.UnitsPerEm / cell : 1;
            _emRatios[face] = ratio;
        }

        return ratio;
    }

    /// <summary>
    /// Breaks a segment of the text into lines no wider than <paramref name="maxWidth"/> at UAX #14 break opportunities: fills
    /// each line in turn, then for wrap styles 0 and 3 moves the last word of a line down while that evens out the pair.
    /// </summary>
    private List<(int Start, int End)> Wrap(string text, int start, int end, List<(int Start, int Length, TextStyle Style)> runs, ParagraphStyle paragraph, float maxWidth)
    {
        var result = new List<(int, int)>();
        if (end - start == 0 || _script.WrapStyle == 2)
        {
            result.Add((start, end));
            return result;
        }

        string segment = text[start..end];
        float[] advances = Advances(segment, Slice(runs, start, end), paragraph);
        var prefix = new float[advances.Length + 1];
        for (int i = 0; i < advances.Length; i++)
        {
            prefix[i + 1] = prefix[i] + advances[i];
        }

        float Width(int a, int b)
        {
            while (b > a && IsSpace(segment[b - 1]))
            {
                b--;
            }

            while (a < b && IsSpace(segment[a]))
            {
                a++;
            }

            if (b == a)
            {
                return 0;
            }

            // A line fits when its letters do: the spacing after the last one is left out (libass).
            float spacing = paragraph.DefaultStyle.LetterSpacing ?? 0;
            foreach ((int runStart, int runLength, TextStyle runStyle) in runs)
            {
                if (start + b - 1 >= runStart && start + b - 1 < runStart + runLength)
                {
                    spacing = runStyle.LetterSpacing ?? spacing;
                    break;
                }
            }

            return prefix[b] - prefix[a] - spacing;
        }

        var breaks = new List<int>();
        foreach (LineBreak lineBreak in LineBreaks.Enumerate(segment))
        {
            if (lineBreak.Index > 0)
            {
                breaks.Add(lineBreak.Index);
            }
        }

        // Greedy: as much as fits on each line; a word wider than the line stays whole and overflows.
        var ends = new List<int>();
        int lineStart = 0, k = 0;
        while (lineStart < segment.Length && k < breaks.Count)
        {
            int best = -1;
            for (int j = k; j < breaks.Count; j++)
            {
                if (Width(lineStart, breaks[j]) <= maxWidth || best < 0 && j == k)
                {
                    best = j;
                }
                else
                {
                    break;
                }
            }

            ends.Add(breaks[best]);
            lineStart = breaks[best];
            k = best + 1;
        }

        if (_script.WrapStyle is 0 or 3 && ends.Count > 1)
        {
            // Balance: move a break back one opportunity while that brings the two lines' widths closer (style 0), or while
            // the upper line is still the wider (style 3, which wants the lower line wider).
            bool moved;
            do
            {
                moved = false;
                for (int i = 1; i < ends.Count; i++)
                {
                    int a1 = i > 1 ? ends[i - 2] : 0, b1 = ends[i - 1], b2 = ends[i];
                    int index = breaks.IndexOf(b1);
                    if (index <= 0 || breaks[index - 1] <= a1)
                    {
                        continue;
                    }

                    int candidate = breaks[index - 1];
                    float l1 = Width(a1, b1), l2 = Width(b1, b2), n1 = Width(a1, candidate), n2 = Width(candidate, b2);
                    if (n2 > maxWidth || n1 <= 0)
                    {
                        continue;
                    }

                    bool better = Math.Abs(n1 - n2) < Math.Abs(l1 - l2);
                    if (better)
                    {
                        ends[i - 1] = candidate;
                        moved = true;
                    }
                }
            }
            while (moved);
        }

        int from = 0;
        foreach (int e in ends)
        {
            result.Add((start + from, start + e));
            from = e;
        }

        return result;
    }

    private static bool IsSpace(char c) => c is ' ' or '\t' or '　' or '​';

    /// <summary>The advance of each character of a text laid out on one line: each glyph's advance goes to its cluster's first character.</summary>
    private static float[] Advances(string text, List<StyleSpan> spans, ParagraphStyle paragraph)
    {
        var advances = new float[text.Length];
        TextLayout layout = Paragraph.Create(new RichText(text, spans), paragraph).Layout(float.PositiveInfinity);
        foreach (TextLine line in layout.Lines)
        {
            foreach (LayoutRun run in line.Runs)
            {
                if (run.Glyphs is not GlyphRun glyphs || glyphs.Count == 0)
                {
                    continue;
                }

                ReadOnlySpan<Vector2> positions = glyphs.Positions;
                ReadOnlySpan<int> clusters = glyphs.Clusters;
                for (int g = 0; g < glyphs.Count; g++)
                {
                    float next = g + 1 < glyphs.Count ? positions[g + 1].X : glyphs.Advance;
                    int cluster = clusters.Length > g ? clusters[g] : 0;
                    if ((uint)cluster < (uint)advances.Length)
                    {
                        advances[cluster] += Math.Abs(next - positions[g].X);
                    }
                }
            }
        }

        return advances;
    }

    /// <summary>The style runs that fall in [start, end), moved to start at 0.</summary>
    private static List<StyleSpan> Slice(List<(int Start, int Length, TextStyle Style)> runs, int start, int end)
    {
        var spans = new List<StyleSpan>();
        foreach ((int s, int length, TextStyle style) in runs)
        {
            int a = Math.Max(s, start), b = Math.Min(s + length, end);
            if (b > a)
            {
                spans.Add(new StyleSpan(a - start, b - a, style));
            }
        }

        return spans;
    }

    /// <summary>
    /// The opaque box (<c>BorderStyle</c> 3) of a line placed with its baseline at <paramref name="origin"/>. WebVTT's is the line's
    /// extent padded by the border. libass's grows with <c>ScaleX</c> and <c>ScaleY</c> as if scaled twice: it hangs from the
    /// line's top, (ascent + descent) × ScaleY² tall, and its last glyph's box is that glyph's advance × ScaleX² wide (measured on
    /// FFmpeg 8.1's <c>subtitles</c> filter).
    /// </summary>
    private RectF BoxOf(Line line, Vector2 origin, float sx, float sy, float borderX, float border)
    {
        float top = origin.Y - (line.Ascent * sy);
        if (_script.Css)
        {
            return new RectF(origin.X - borderX, top - border, (line.Width * sx) + (2 * borderX), ((line.Ascent + line.Descent) * sy) + (2 * border));
        }

        float width = (line.Width * sx) + (line.LastAdvance * sx * (sx - 1));
        float height = (line.Ascent + line.Descent) * sy * sy;
        return new RectF(origin.X - borderX, top - border, width + (2 * borderX), height + (2 * border));
    }

    /// <summary>Shapes one line, without the spaces at its ends, and measures its width and the tallest font's ascent and descent.</summary>
    private static Line Shape(string text, int start, int end, List<(int Start, int Length, TextStyle Style)> runs, ParagraphStyle paragraph)
    {
        while (end > start && IsSpace(text[end - 1]))
        {
            end--;
        }

        while (start < end && IsSpace(text[start]))
        {
            start++;
        }

        List<StyleSpan> spans = Slice(runs, start, Math.Max(end, start + 1));
        if (end == start)
        {
            // An empty line (\N\N) is as tall as the font at its place.
            TextStyle style = spans.Count > 0 ? spans[0].Style : paragraph.DefaultStyle;
            (float a, float d) = Metrics(style);
            return new Line(null, 0, a, d);
        }

        TextLayout layout = Paragraph.Create(new RichText(text[start..end], spans), paragraph).Layout(float.PositiveInfinity);
        float ascent = 0, descent = 0, width = 0;
        foreach (TextLine line in layout.Lines)
        {
            width = Math.Max(width, line.Width);
            foreach (LayoutRun run in line.Runs)
            {
                if (run.Glyphs is GlyphRun glyphs)
                {
                    (float a, float d) = Metrics(glyphs.Font);
                    ascent = Math.Max(ascent, a);
                    descent = Math.Max(descent, d);
                }
            }
        }

        if (ascent == 0 && descent == 0)
        {
            (ascent, descent) = Metrics(spans[0].Style);
        }

        // libass counts the spacing after a line's last letter in its width.
        float lastAdvance = 0;
        TextLine lastLine = layout.Lines[^1];
        for (int r = lastLine.Runs.Count - 1; r >= 0; r--)
        {
            if (lastLine.Runs[r].Glyphs is GlyphRun glyphs && glyphs.Count > 0)
            {
                lastAdvance = glyphs.Advance - glyphs.Positions[glyphs.Count - 1].X;
                break;
            }
        }

        return new Line(layout, width, ascent, descent, lastAdvance);
    }

    private static (float Ascent, float Descent) Metrics(TextStyle style)
    {
        if (style.FontFamilies is not { Count: > 0 } families || style.Size is not float size)
        {
            return (0, 0);
        }

        return Metrics(new Font(families[0].Match(style.FontStyle ?? FontStyle.Normal).Typeface, size));
    }

    /// <summary>
    /// A font's ascent and descent in pixels, as GDI and libass take them: OS/2's Windows ascent and descent, else hhea's, else
    /// OS/2's typographic ones. With the size rule of <see cref="EmRatio"/>, a line is as tall as the ASS font size.
    /// </summary>
    private static (float Ascent, float Descent) Metrics(Font font)
    {
        Typeface face = font.Typeface;
        VerticalMetrics m = face.WinMetrics;
        if (m.Ascent == 0 && m.Descent == 0)
        {
            m = face.HheaMetrics;
        }

        if (m.Ascent == 0 && m.Descent == 0)
        {
            m = face.TypoMetrics;
        }

        return (m.Ascent * font.Scale, -m.Descent * font.Scale);
    }
}
