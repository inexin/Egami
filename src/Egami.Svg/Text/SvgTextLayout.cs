using System.Numerics;
using System.Text;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Fonts.Shaping;
using Egami.Fonts.Unicode;

namespace Egami.Svg.Text;

/// <summary>
/// Lays out a text element's chunks into positioned glyphs (SVG 2 section 11.5): each chunk is shaped whole (bidi, spans'
/// fonts, fallback), cut into clusters, spaced (letter-spacing, word-spacing, textLength), placed with x, y, dx, dy and rotate,
/// anchored, put on its textPath or turned for vertical writing, and shifted to its baseline; decorations follow each span.
/// Translated from resvg's usvg text layout (Apache-2.0 or MIT), the reference Egami.Svg is gated against; see
/// THIRD-PARTY-NOTICES.
/// </summary>
internal static class SvgTextLayout
{
    private const float Degrees = MathF.PI / 180;

    /// <summary>One grapheme cluster: its glyphs and how it is moved.</summary>
    private sealed class Cluster
    {
        public int Index;
        public int CodePoint;
        public float Width;
        public float Advance;
        public float TextLengthSpacing;
        public float Ascent;
        public float Descent;
        public bool HasRelativeShift;
        public bool Visible = true;
        public List<(Font Font, ushort Glyph, Vector2 Offset)> Glyphs = [];
        public Matrix3x2 Transform = Matrix3x2.Identity;
        public Matrix3x2 PathTransform = Matrix3x2.Identity;

        public float Height => Ascent - Descent;

        public Matrix3x2 Full => PathTransform * Transform;
    }

    public static (List<SvgLaidSpan> Spans, RectF Bounds) Layout(
        List<SvgTextChunk> chunks, SvgCharPosition[] positions, float[] rotate, bool vertical, FontFallback fallback)
    {
        List<SvgLaidSpan> spans = [];
        RectF? bounds = null;
        float lastX = 0, lastY = 0;
        foreach (SvgTextChunk chunk in chunks)
        {
            (float x, float y) = chunk.Path is null ? (chunk.X ?? lastX, chunk.Y ?? lastY) : (0, 0);
            string text = chunk.Text.ToString();
            int[] charIndex = CharIndexes(text);
            List<Cluster> clusters = Shape(chunk, text, fallback);
            if (clusters.Count == 0)
            {
                continue;
            }

            if (vertical)
            {
                ApplyWritingMode(clusters);
            }

            ApplyLetterSpacing(chunk, clusters);
            ApplyWordSpacing(chunk, clusters);
            ApplyLengthAdjust(chunk, clusters);
            (float endX, float endY) = chunk.Path is SvgTextPathFlow flow
                ? PlaceOnPath(chunk, flow, charIndex, positions, rotate, vertical, clusters)
                : PlaceHorizontally(chunk, charIndex, positions, rotate, vertical, clusters);

            Matrix3x2 textTransform = Matrix3x2.Identity;
            if (vertical && chunk.Path is null)
            {
                textTransform = RotateAt(90, x, y);
            }

            foreach (SvgTextSpan span in chunk.Spans)
            {
                Matrix3x2 spanTransform = Matrix3x2.CreateTranslation(x, y) * textTransform;
                if (chunk.Path is null)
                {
                    spanTransform = Matrix3x2.CreateTranslation(0, Baseline(span, vertical)) * spanTransform;
                }

                List<(float Width, Matrix3x2 Transform)> decorationSpans = DecorationSpans(span, clusters);
                SvgTextFont font = span.Font;
                Path? underline = null, overline = null, lineThrough = null;
                if (decorationSpans.Count > 0)
                {
                    if (span.Underline is not null)
                    {
                        underline = Decoration(vertical ? font.Height / 2 : -font.UnderlinePosition, font, decorationSpans, spanTransform);
                    }

                    if (span.Overline is not null)
                    {
                        overline = Decoration(vertical ? -font.Height / 2 : -font.Ascent, font, decorationSpans, spanTransform);
                    }

                    if (span.LineThrough is not null)
                    {
                        lineThrough = Decoration(vertical ? 0 : -font.LineThroughPosition, font, decorationSpans, spanTransform);
                    }
                }

                List<SvgPlacedGlyph> glyphs = [];
                bool any = false;
                foreach (Cluster cluster in clusters)
                {
                    if (!cluster.Visible)
                    {
                        continue;
                    }

                    // The text's box comes from the fonts' ascent and descent, not the glyphs' shapes.
                    float advance = cluster.Advance > 0 ? cluster.Advance : 1;
                    RectF box = Transform(new RectF(0, -cluster.Ascent, advance, cluster.Height), cluster.Full * Matrix3x2.CreateTranslation(x, y) * textTransform);
                    bounds = bounds is RectF b ? b.Union(box) : box;
                    if (!span.Contains(cluster.Index))
                    {
                        continue;
                    }

                    any = true;
                    Matrix3x2 clusterTransform = cluster.Full * spanTransform;
                    foreach ((Font f, ushort glyph, Vector2 offset) in cluster.Glyphs)
                    {
                        glyphs.Add(new SvgPlacedGlyph(f, glyph, Matrix3x2.CreateTranslation(offset) * clusterTransform));
                    }
                }

                if (any || underline is not null || overline is not null || lineThrough is not null)
                {
                    spans.Add(new SvgLaidSpan(span, glyphs, underline, overline, lineThrough));
                }
            }

            if (vertical && chunk.Path is null)
            {
                (endX, endY) = (endY, endX);
            }

            lastX = x + endX;
            lastY = y + endY;
        }

        return (spans, bounds ?? RectF.Empty);
    }

    /// <summary>The character (code point) index of each UTF-16 index of a chunk's text.</summary>
    private static int[] CharIndexes(string text)
    {
        int[] map = new int[text.Length + 1];
        int n = 0;
        for (int i = 0; i < text.Length; i++)
        {
            map[i] = n;
            if (!(char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])))
            {
                n++;
            }
        }

        map[text.Length] = n;
        return map;
    }

    /// <summary>
    /// Shapes a chunk: the whole chunk in each span's font, so kerning and ligatures carry across spans, each cluster then
    /// taking the glyphs its own span's font gave it (resvg's process_chunk); clusters are in visual order.
    /// </summary>
    private static List<Cluster> Shape(SvgTextChunk chunk, string text, FontFallback fallback)
    {
        List<Cluster>? clusters = null;
        Dictionary<(Font, bool, bool), List<Cluster>> shapings = [];
        foreach (SvgTextSpan span in chunk.Spans)
        {
            if (!shapings.TryGetValue((span.Font.Font, span.SmallCaps, span.Kerning), out List<Cluster>? shaped))
            {
                shaped = ShapeWith(text, span, fallback);
                shapings[(span.Font.Font, span.SmallCaps, span.Kerning)] = shaped;
            }

            if (clusters is null)
            {
                clusters = [.. shaped];
                continue;
            }

            // A cluster of this span takes the glyphs this span's font gave its characters, which may be more clusters (a
            // ligature the first font made and this one does not) or fewer (the other way round).
            List<Cluster> merged = [];
            List<(int Start, int End)> covered = [];
            foreach (Cluster c in clusters)
            {
                if (!span.Contains(c.Index))
                {
                    merged.Add(c);
                    continue;
                }

                if (covered.Any(r => c.Index >= r.Start && c.Index < r.End))
                {
                    continue;
                }

                int end = ClusterEnd(clusters, c, text.Length);
                List<Cluster> own = [.. shaped.Where(o => o.Index >= c.Index && o.Index < end)];
                if (own.Count == 0 && shaped.FirstOrDefault(o => o.Index < c.Index && ClusterEnd(shaped, o, text.Length) > c.Index) is Cluster wider)
                {
                    own.Add(wider);
                }

                if (own.Count == 0)
                {
                    merged.Add(c);
                    continue;
                }

                foreach (Cluster o in own)
                {
                    if (!merged.Contains(o))
                    {
                        merged.Add(o);
                        covered.Add((o.Index, ClusterEnd(shaped, o, text.Length)));
                    }
                }
            }

            clusters = merged;
        }

        List<Cluster> result = [];
        foreach (Cluster c in clusters ?? [])
        {
            if (chunk.SpanAt(c.Index) is SvgTextSpan span)
            {
                result.Add(new Cluster
                {
                    Index = c.Index,
                    CodePoint = c.CodePoint,
                    Width = c.Width,
                    Advance = c.Advance,
                    Ascent = span.Font.Ascent,
                    Descent = span.Font.Descent,
                    Glyphs = [.. c.Glyphs],
                });
            }
        }

        return result;
    }

    /// <summary>The UTF-16 index after a cluster's characters: the next larger cluster start in the same shaping.</summary>
    private static int ClusterEnd(List<Cluster> clusters, Cluster cluster, int length)
    {
        int end = length;
        foreach (Cluster c in clusters)
        {
            if (c.Index > cluster.Index && c.Index < end)
            {
                end = c.Index;
            }
        }

        return end;
    }

    private static List<Cluster> ShapeWith(string text, SvgTextSpan span, FontFallback fallback)
    {
        List<FontFeature> features = [];
        if (span.SmallCaps)
        {
            features.Add(new FontFeature(new FontTag("smcp")));
        }

        if (!span.Kerning)
        {
            features.Add(new FontFeature(new FontTag("kern"), 0));
        }

        ShapedText shaped = TextShaper.Shape(text, span.Font.Font, new ShapingOptions
        {
            Fallback = fallback,
            Direction = TextDirection.LeftToRight,
            Features = features.Count > 0 ? features : null,
        });

        List<Cluster> clusters = [];
        foreach (ShapedLine line in shaped.Lines)
        {
            foreach (ShapedRun run in line.Runs)
            {
                GlyphBuffer buffer = run.Glyphs;
                ReadOnlySpan<int> clusterIds = buffer.Clusters;
                ReadOnlySpan<ushort> ids = buffer.GlyphIds;
                ReadOnlySpan<GlyphPosition> pos = buffer.Positions;
                Cluster? current = null;
                float x = 0;
                for (int i = 0; i < buffer.Count; i++)
                {
                    int index = clusterIds[i];
                    if (current is null || current.Index != index)
                    {
                        current = new Cluster { Index = index, CodePoint = char.ConvertToUtf32(text, index) };
                        clusters.Add(current);
                        x = 0;
                    }

                    GlyphPosition p = pos[i];
                    current.Glyphs.Add((run.Font, ids[i], new Vector2(x + p.XOffset, -p.YOffset)));
                    x += p.XAdvance;
                    current.Advance += p.XAdvance;
                    current.Width = Math.Max(current.Width, p.XAdvance);
                }
            }
        }

        return clusters;
    }

    /// <summary>Upright characters in vertical text stand on the vertical baseline; the rest are turned with the line.</summary>
    private static void ApplyWritingMode(List<Cluster> clusters)
    {
        foreach (Cluster cluster in clusters)
        {
            VerticalOrientation orientation = UnicodeData.GetVerticalOrientation(cluster.CodePoint);
            float middle = (cluster.Ascent + cluster.Descent) / 2;
            if (orientation is VerticalOrientation.Upright or VerticalOrientation.TransformedUpright)
            {
                cluster.PathTransform = RotateAt(-90, cluster.Width / 2, -middle) * Matrix3x2.CreateTranslation(0, middle);
                cluster.Ascent = cluster.Width / 2;
                cluster.Descent = -cluster.Width / 2;
            }
            else
            {
                cluster.Transform = Matrix3x2.CreateTranslation(0, middle) * cluster.Transform;
            }
        }
    }

    private static void ApplyLetterSpacing(SvgTextChunk chunk, List<Cluster> clusters)
    {
        if (!chunk.Spans.Any(s => s.LetterSpacing != 0))
        {
            return;
        }

        for (int i = 0; i < clusters.Count; i++)
        {
            Cluster cluster = clusters[i];
            if (!SupportsLetterSpacing(UnicodeData.GetScript(cluster.CodePoint)) || chunk.SpanAt(cluster.Index) is not SvgTextSpan span)
            {
                continue;
            }

            // Not after the last cluster: it would move the box and the anchor.
            if (i != clusters.Count - 1)
            {
                cluster.Advance += span.LetterSpacing;
            }

            if (!(cluster.Advance >= 0))
            {
                cluster.Width = 0;
                cluster.Advance = 0;
                cluster.Glyphs.Clear();
            }
        }
    }

    private static void ApplyWordSpacing(SvgTextChunk chunk, List<Cluster> clusters)
    {
        if (!chunk.Spans.Any(s => s.WordSpacing != 0))
        {
            return;
        }

        foreach (Cluster cluster in clusters)
        {
            if (cluster.CodePoint is 0x20 or 0xA0 or 0x1361 or 0x10100 or 0x10101 or 0x1039F or 0x1091F && chunk.SpanAt(cluster.Index) is SvgTextSpan span)
            {
                cluster.Advance += span.WordSpacing;
            }
        }
    }

    private static void ApplyLengthAdjust(SvgTextChunk chunk, List<Cluster> clusters)
    {
        bool horizontal = chunk.Path is null;
        foreach (SvgTextSpan span in chunk.Spans)
        {
            if (span.TextLength is not float target)
            {
                continue;
            }

            List<int> indexes = [];
            for (int i = 0; i < clusters.Count; i++)
            {
                if (span.Contains(clusters[i].Index))
                {
                    indexes.Add(i);
                }
            }

            if (indexes.Count == 0)
            {
                continue;
            }

            // The clusters' own widths: textLength replaces letter-spacing and word-spacing.
            float width = indexes.Sum(i => clusters[i].Width);
            if (!span.SpacingAndGlyphs)
            {
                float factor = indexes.Count > 1 ? (target - width) / (indexes.Count - 1) : 0;
                foreach (int i in indexes)
                {
                    clusters[i].Advance = clusters[i].Width + factor;
                    clusters[i].TextLengthSpacing = factor;
                }
            }
            else
            {
                float factor = target / width;
                if (!(factor >= 0.001f))
                {
                    continue;
                }

                foreach (int i in indexes)
                {
                    clusters[i].Transform = Matrix3x2.CreateScale(factor, 1) * clusters[i].Transform;
                    if (!horizontal)
                    {
                        clusters[i].Advance *= factor;
                        clusters[i].Width *= factor;
                    }
                }
            }
        }
    }

    /// <summary>dx and dy move a character and everything after it in logical order; clusters are in visual order.</summary>
    private static (Vector2[] Shifts, int[] Logical) RelativeShifts(SvgTextChunk chunk, int[] charIndex, SvgCharPosition[] positions, List<Cluster> clusters)
    {
        int[] logical = [.. Enumerable.Range(0, clusters.Count).OrderBy(i => clusters[i].Index)];
        Vector2[] shifts = new Vector2[clusters.Count];
        Vector2 shift = Vector2.Zero;
        foreach (int i in logical)
        {
            int cp = chunk.FirstChar + charIndex[clusters[i].Index];
            if (cp < positions.Length)
            {
                shift += new Vector2(positions[cp].Dx ?? 0, positions[cp].Dy ?? 0);
            }

            shifts[i] = shift;
        }

        return (shifts, logical);
    }

    private static (float X, float Y) PlaceHorizontally(
        SvgTextChunk chunk, int[] charIndex, SvgCharPosition[] positions, float[] rotate, bool vertical, List<Cluster> clusters)
    {
        (Vector2[] shifts, int[] logical) = RelativeShifts(chunk, charIndex, positions, clusters);
        int first = logical[0], last = logical[^1];
        Vector2 lastShift = shifts[last];
        if (vertical)
        {
            for (int i = 0; i < shifts.Length; i++)
            {
                shifts[i] = new Vector2(shifts[i].Y, -shifts[i].X);
            }
        }

        for (int i = 1; i < clusters.Count; i++)
        {
            if (shifts[i] != shifts[i - 1])
            {
                clusters[i].HasRelativeShift = true;
            }
        }

        float left = float.PositiveInfinity, right = float.NegativeInfinity, advance = 0;
        for (int i = 0; i < clusters.Count; i++)
        {
            float start = advance + shifts[i].X;
            float end = start + clusters[i].Advance - clusters[i].TextLengthSpacing;
            left = Math.Min(left, Math.Min(start, end));
            right = Math.Max(right, Math.Max(start, end));
            advance += clusters[i].Advance;
        }

        float x = shifts[first].X - left + Anchor(chunk.Anchor, right - left);
        for (int i = 0; i < clusters.Count; i++)
        {
            Cluster cluster = clusters[i];
            cluster.Transform = Matrix3x2.CreateTranslation(x + shifts[i].X, shifts[i].Y) * cluster.Transform;
            int cp = chunk.FirstChar + charIndex[cluster.Index];
            if (cp < rotate.Length && rotate[cp] != 0)
            {
                cluster.Transform = Matrix3x2.CreateRotation(rotate[cp] * Degrees) * cluster.Transform;
                cluster.HasRelativeShift = true;
            }

            x += cluster.Advance;
        }

        Vector2 end2 = vertical ? new Vector2(lastShift.Y, -lastShift.X) : lastShift;
        return (x + end2.X, end2.Y);
    }

    private static float Anchor(string anchor, float width) => anchor switch
    {
        "middle" => -width / 2,
        "end" => -width,
        _ => 0,
    };

    /// <summary>
    /// The span's baseline offset from the text's (y down): baseline-shift, then alignment-baseline when set, else
    /// dominant-baseline, with the fixed metrics Chromium uses.
    /// </summary>
    public static float Baseline(SvgTextSpan span, bool vertical)
    {
        SvgTextFont font = span.Font;
        float shift = 0;
        for (int i = span.BaselineShift.Count - 1; i >= 0; i--)
        {
            (SvgBaselineShiftKind kind, float value) = span.BaselineShift[i];
            shift += kind switch
            {
                SvgBaselineShiftKind.Sub => -font.Subscript,
                SvgBaselineShiftKind.Super => font.Superscript,
                SvgBaselineShiftKind.Length => value,
                _ => 0,
            };
        }

        float result = -shift;
        if (!vertical)
        {
            result += span.AlignmentBaseline is "auto" or "baseline"
                ? font.BaselineOffset(span.DominantBaseline)
                : font.BaselineOffset(span.AlignmentBaseline);
        }

        return result;
    }

    private static (float X, float Y) PlaceOnPath(
        SvgTextChunk chunk, SvgTextPathFlow flow, int[] charIndex, SvgCharPosition[] positions, float[] rotate, bool vertical, List<Cluster> clusters)
    {
        (Vector2[] shifts, _) = RelativeShifts(chunk, charIndex, positions, clusters);
        float chunkOffset = vertical ? chunk.Y ?? 0 : chunk.X ?? 0;
        float total = clusters.Sum(c => c.Advance);
        float startOffset = chunkOffset + flow.StartOffset + Anchor(chunk.Anchor, total);
        PathMeasure measure = new(flow.Path);
        float lastX = 0, lastY = 0, advance = startOffset;
        for (int i = 0; i < clusters.Count; i++)
        {
            Cluster cluster = clusters[i];
            float half = cluster.Width / 2;
            float offset = advance + shifts[i].X + half;
            advance += cluster.Advance;
            if (!(offset >= 0) || !Point(measure, offset, out Vector2 point, out float angle))
            {
                cluster.Visible = false;
                continue;
            }

            // Each cluster breaks the decoration line on a path.
            cluster.HasRelativeShift = true;
            Matrix3x2 original = cluster.Transform;
            Matrix3x2 t = RotateAt(angle, half, 0) * Matrix3x2.CreateTranslation(point.X - half, point.Y);
            float baseline = chunk.SpanAt(cluster.Index) is SvgTextSpan span ? -Baseline(span, vertical) : 0;
            float dy = shifts[i].Y;
            if (dy != 0 || baseline != 0)
            {
                t = Matrix3x2.CreateTranslation(0, dy - baseline) * t;
            }

            int cp = chunk.FirstChar + charIndex[cluster.Index];
            if (cp < rotate.Length && rotate[cp] != 0)
            {
                t = Matrix3x2.CreateRotation(rotate[cp] * Degrees) * t;
            }

            cluster.Transform = original * t;
            lastX = point.X + cluster.Advance;
            lastY = point.Y;
        }

        return (lastX, lastY);
    }

    /// <summary>The point at a distance along a path's contours (all of them, one after another) and the tangent's angle in degrees.</summary>
    private static bool Point(PathMeasure measure, float distance, out Vector2 point, out float angle)
    {
        float start = 0;
        foreach (ContourMeasure contour in measure.Contours)
        {
            if (distance <= start + contour.Length)
            {
                float d = Math.Clamp(distance - start, 0, contour.Length);
                point = contour.GetPosition(d);
                Vector2 tangent = contour.GetTangent(d);
                angle = MathF.Atan2(tangent.Y, tangent.X) / Degrees;
                return true;
            }

            start += contour.Length;
        }

        point = default;
        angle = 0;
        return false;
    }

    /// <summary>The runs of a span's clusters one decoration line covers: a new one wherever dx, dy, rotate or a path breaks it.</summary>
    private static List<(float Width, Matrix3x2 Transform)> DecorationSpans(SvgTextSpan span, List<Cluster> clusters)
    {
        List<(float, Matrix3x2)> result = [];
        bool started = false;
        float width = 0;
        Matrix3x2 transform = Matrix3x2.Identity;
        foreach (Cluster cluster in clusters)
        {
            if (span.Contains(cluster.Index))
            {
                if (started && cluster.HasRelativeShift)
                {
                    started = false;
                    result.Add((width, transform));
                }

                if (!started)
                {
                    width = cluster.Advance;
                    started = true;
                    transform = cluster.Transform;
                }
                else
                {
                    width += cluster.Advance;
                }
            }
            else if (started)
            {
                result.Add((width, transform));
                started = false;
            }
        }

        if (started)
        {
            result.Add((width, transform));
        }

        return result;
    }

    private static Path? Decoration(float dy, SvgTextFont font, List<(float Width, Matrix3x2 Transform)> spans, Matrix3x2 spanTransform)
    {
        float thickness = font.UnderlineThickness;
        PathBuilder builder = new();
        bool any = false;
        foreach ((float width, Matrix3x2 transform) in spans)
        {
            if (!(width > 0) || !(thickness > 0))
            {
                continue;
            }

            Matrix3x2 m = Matrix3x2.CreateTranslation(0, dy) * transform * spanTransform;
            Vector2 a = Vector2.Transform(new Vector2(0, -thickness / 2), m), b = Vector2.Transform(new Vector2(width, -thickness / 2), m);
            Vector2 c = Vector2.Transform(new Vector2(width, thickness / 2), m), d = Vector2.Transform(new Vector2(0, thickness / 2), m);
            builder.MoveTo(a).LineTo(b).LineTo(c).LineTo(d).Close();
            any = true;
        }

        return any ? builder.ToPath() : null;
    }

    private static Matrix3x2 RotateAt(float degrees, float x, float y) => Matrix3x2.CreateRotation(degrees * Degrees, new Vector2(x, y));

    private static RectF Transform(RectF r, Matrix3x2 m) => RectF.FromPoints(
    [
        Vector2.Transform(new Vector2(r.Left, r.Top), m),
        Vector2.Transform(new Vector2(r.Right, r.Top), m),
        Vector2.Transform(new Vector2(r.Left, r.Bottom), m),
        Vector2.Transform(new Vector2(r.Right, r.Bottom), m),
    ]);

    private static bool SupportsLetterSpacing(Script script) =>
        script != Script.Arabic && script != Script.Syriac && script != Script.Nko && script != Script.Manichaean &&
        script != Script.PsalterPahlavi && script != Script.Mandaic && script != Script.Mongolian && script != Script.PhagsPa &&
        script != Script.Devanagari && script != Script.Bengali && script != Script.Gurmukhi && script != Script.Modi &&
        script != Script.Sharada && script != Script.SylotiNagri && script != Script.Tirhuta && script != Script.Ogham;
}
