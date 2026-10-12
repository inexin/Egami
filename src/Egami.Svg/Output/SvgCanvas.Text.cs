using System.Globalization;
using System.Numerics;
using System.Text;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Svg.Output;

namespace Egami.Svg;

public sealed partial class SvgCanvas
{
    /// <inheritdoc/>
    protected override void OnDrawGlyphRun(GlyphRun run, Vector2 origin, Paint paint)
    {
        if (Recording())
        {
            _recorder!.DrawGlyphRun(run, origin, paint);
            return;
        }

        if (_quiet > 0)
        {
            return;
        }

        Font font = run.Font;
        Typeface face = font.EffectiveTypeface;
        if (font.Size <= 0)
        {
            return;
        }

        // Color glyphs are drawn with canvas calls by the default, the rest of the run as one path.
        if (font.ColorGlyphs && (face.ColorFormats & ~ColorGlyphFormats.Svg) != 0 && HasColorGlyph(run, face))
        {
            base.OnDrawGlyphRun(run, origin, paint);
            return;
        }

        bool done = Options.Text == SvgTextMode.Text && run.Text is not null && !run.Clusters.IsEmpty && run.Transforms.IsEmpty
            ? WriteText(run, origin, paint)
            : WriteGlyphs(run, origin, paint);
        if (!done)
        {
            base.OnDrawGlyphRun(run, origin, paint);
        }
    }

    /// <summary>The slant of synthetic italic, tan 14 degrees, as Egami.Fonts draws it.</summary>
    private static readonly float ItalicSkew = MathF.Tan(14 * MathF.PI / 180);

    /// <summary>How far synthetic bold grows outlines at a size, as Egami.Fonts draws it: em/24 at 9 units down to em/32 at 36.</summary>
    private static float BoldOutset(float size)
    {
        float em = MathF.Abs(size);
        float factor = em <= 9 ? 1 / 24f : em >= 36 ? 1 / 32f : (1 / 24f) + (((1 / 32f) - (1 / 24f)) * (em - 9) / 27);
        return em * factor;
    }

    private static bool HasColorGlyph(GlyphRun run, Typeface face)
    {
        foreach (ushort glyph in run.Glyphs)
        {
            if (face.HasColorGlyph(glyph))
            {
                return true;
            }
        }

        return false;
    }

    private static FontSynthesis Applied(Font font) => font.AllowSynthesis ? font.Synthesis : FontSynthesis.None;

    /// <summary>
    /// Writes a run as uses of glyph outlines in defs, each glyph's outline written once at the em size in font units (at the
    /// font's size when synthetic bold, whose outset depends on it). Only a solid paint without blur: other paints are in the
    /// glyph's own coordinates under a use. Returns <see langword="false"/> to draw the run as a path instead.
    /// </summary>
    private bool WriteGlyphs(GlyphRun run, Vector2 origin, Paint paint)
    {
        if (paint is not SolidPaint solid || paint.Blur is { Sigma: > 0 })
        {
            return false;
        }

        (bool skip, string? blend) = Blend(paint.BlendMode, paint);
        if (skip)
        {
            return true;
        }

        Font font = run.Font;
        Typeface face = font.EffectiveTypeface;
        FontSynthesis synthesis = Applied(font);
        bool bold = (synthesis & FontSynthesis.Bold) != 0;
        float size = bold ? font.Size : face.UnitsPerEm;
        float s = font.Size / size;
        Font outlineFont = new(face, size) { OpticalSizing = OpticalSizing.None, Synthesis = synthesis };
        bool transformed = !run.Transforms.IsEmpty;
        float opacity = Math.Clamp(solid.Color.A, 0, 1) * paint.Opacity;
        bool clipped = OpenClip();
        _body.Open("g", new SvgAttrs()
            .Add("transform", Num.Transform(transformed ? Matrix : Matrix3x2.CreateScale(s) * Matrix))
            .Add("fill", SvgNumbers.Color(solid.Color.R, solid.Color.G, solid.Color.B))
            .Add("fill-opacity", opacity < 1 ? SvgNumbers.Unit(opacity) : null)
            .Add("shape-rendering", font.Edging == FontEdging.Alias || !paint.Antialias ? "crispEdges" : null)
            .Add("style", Style(BlendDeclaration(blend))));
        ReadOnlySpan<ushort> glyphs = run.Glyphs;
        for (int i = 0; i < glyphs.Length; i++)
        {
            ushort glyph = glyphs[i];
            string? id = _out.Glyph(face, glyph, synthesis, bold ? font.Size : 0, () => outlineFont.GetGlyphPath(glyph));
            if (id is null)
            {
                continue;
            }

            SvgAttrs a = new SvgAttrs().Add("xlink:href", "#" + id);
            if (transformed)
            {
                a.Add("transform", Num.Transform(Matrix3x2.CreateScale(s) * run.Transforms[i].ToMatrix() * Matrix3x2.CreateTranslation(origin)));
            }
            else
            {
                Vector2 p = (origin + run.Positions[i]) / s;
                string x = Num.F(p.X), y = Num.F(p.Y);
                a.Add("x", x == "0" ? null : x).Add("y", y == "0" ? null : y);
            }

            _body.Empty("use", a);
        }

        _body.Close();
        CloseClip(clipped);
        return true;
    }

    /// <summary>
    /// Writes a run as a text element: the font's family, weight, style and size, and each cluster's characters at its glyph's
    /// position, so the viewer's text lines up with Egami's even in another copy of the font. A right-to-left run is one chunk
    /// anchored at its end. Returns <see langword="false"/> when the paint needs a path.
    /// </summary>
    private bool WriteText(GlyphRun run, Vector2 origin, Paint paint)
    {
        if (paint.Blur is { Sigma: > 0 })
        {
            return false;
        }

        (bool skip, string? blend) = Blend(paint.BlendMode, paint);
        if (skip)
        {
            return true;
        }

        RectF bounds = run.Bounds.ToRectF().Offset(origin);
        if (!TryPaint(paint, bounds.IsEmpty ? new RectF(origin.X, origin.Y, 1, 1) : bounds, out string? value, out float opacity, out _))
        {
            return false;
        }

        if (value is null)
        {
            return true;
        }

        Font font = run.Font;
        Typeface face = font.EffectiveTypeface;
        string text = run.Text!;
        EmbeddedFace? embedded = Options.EmbedSubset ? _out.Face(face) : null;
        StringBuilder inner = new();
        StringBuilder used = new();
        float? baseline = origin.Y;
        if ((run.BidiLevel & 1) == 1)
        {
            (int start, int end) = RunRange(run, text, face);
            string chars = text[start..end];
            used.Append(chars);
            inner.Append("<tspan x=\"").Append(Num.F(origin.X + run.Advance)).Append("\" y=\"").Append(Num.F(origin.Y)).Append("\" text-anchor=\"end\">");
            SvgMarkup.Escape(inner, chars, attribute: false);
            inner.Append("</tspan>");
        }
        else
        {
            foreach ((int glyph, int start, int end) in Chunks(run, text, face))
            {
                Vector2 p = origin + run.Positions[glyph];
                baseline = p.Y == baseline ? baseline : null;
                string chars = text[start..end];
                used.Append(chars);
                inner.Append("<tspan x=\"").Append(Num.F(p.X)).Append("\" y=\"").Append(Num.F(p.Y)).Append("\">");
                SvgMarkup.Escape(inner, chars, attribute: false);
                inner.Append("</tspan>");
            }
        }

        if (embedded is not null)
        {
            embedded.Glyphs.UnionWith(run.Glyphs.ToArray());
            embedded.Text.Append(used);
        }

        // Synthesis as Egami draws it, whatever the viewer would synthesize: bold as a stroke of the outset on each side, italic
        // as a 14-degree slant about the baseline (as font-style when the glyphs are not on one baseline).
        FontSynthesis synthesis = Applied(font);
        FontStyle style = face.Style;
        bool italic = (synthesis & FontSynthesis.Italic) != 0;
        bool slanted = italic && baseline is not null;
        string? slant = (italic && !slanted) || style.Slant == FontSlant.Italic ? "italic" : style.Slant == FontSlant.Oblique ? "oblique" : null;
        Matrix3x2 transform = slanted ? new Matrix3x2(1, 0, -ItalicSkew, 1, ItalicSkew * baseline!.Value, 0) * Matrix : Matrix;
        float? outset = (synthesis & FontSynthesis.Bold) != 0 ? BoldOutset(font.Size) : null;
        string family = CssString(face.FamilyName);
        if (embedded is { Embeddable: true })
        {
            family = CssString(embedded.Family) + ", " + family;
        }

        SvgAttrs a = new SvgAttrs()
            .Add("xml:space", "preserve")
            .Add("transform", Num.Transform(transform))
            .Add("font-family", family)
            .Add("font-size", Num.F(font.Size))
            .Add("font-weight", style.Weight != 400 ? style.Weight.ToString(CultureInfo.InvariantCulture) : null)
            .Add("font-style", slant)
            .Add("fill", value)
            .Add("fill-opacity", outset is null && opacity < 1 ? SvgNumbers.Unit(opacity) : null)
            .Add("stroke", outset is null ? null : value)
            .Add("stroke-width", outset is float o ? Num.F(2 * o) : null)
            .Add("stroke-linejoin", outset is null ? null : "round")
            .Add("opacity", outset is not null && opacity < 1 ? SvgNumbers.Unit(opacity) : null) // Fill and stroke fade as one.
            .Add("style", Style(BlendDeclaration(blend)));
        StringBuilder sb = new("<text");
        a.WriteTo(sb);
        sb.Append('>').Append(inner).Append("</text>");
        bool clipped = OpenClip();
        _body.Raw(sb.ToString());
        CloseClip(clipped);
        return true;
    }

    /// <summary>The clusters of a left-to-right run in glyph order: the first glyph of each and its characters.</summary>
    private static List<(int Glyph, int Start, int End)> Chunks(GlyphRun run, string text, Typeface face)
    {
        ReadOnlySpan<int> clusters = run.Clusters;
        int[] starts = [.. clusters.ToArray().Where(c => c >= 0 && c < text.Length).Distinct().Order()];
        List<(int, int, int)> chunks = [];
        HashSet<int> seen = [];
        for (int i = 0; i < clusters.Length; i++)
        {
            int c = clusters[i];
            if (c < 0 || c >= text.Length || !seen.Add(c))
            {
                continue;
            }

            int next = Array.BinarySearch(starts, c) + 1;
            int end = next < starts.Length ? starts[next] : LastClusterEnd(text, c, face);
            chunks.Add((i, c, end));
        }

        return chunks;
    }

    private static (int Start, int End) RunRange(GlyphRun run, string text, Typeface face)
    {
        int min = int.MaxValue, max = -1;
        foreach (int c in run.Clusters)
        {
            if (c >= 0 && c < text.Length)
            {
                min = Math.Min(min, c);
                max = Math.Max(max, c);
            }
        }

        return max < 0 ? (0, 0) : (min, LastClusterEnd(text, max, face));
    }

    /// <summary>
    /// Where the last cluster of a run ends: its grapheme, and the characters after it the face can draw up to a space or line
    /// break (a ligature's other letters), since a run does not say where its text ends.
    /// </summary>
    private static int LastClusterEnd(string text, int start, Typeface face)
    {
        int end = start + StringInfo.GetNextTextElementLength(text, start);
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && !char.IsControl(text[end]))
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(end), out Rune rune, out _) != System.Buffers.OperationStatus.Done || !face.TryGetGlyph(rune.Value, out _))
            {
                break;
            }

            end += StringInfo.GetNextTextElementLength(text, end);
        }

        return end;
    }

    private static string CssString(string value)
    {
        StringBuilder sb = new("'");
        foreach (char c in value)
        {
            if (c is '\'' or '\\')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.Append('\'').ToString();
    }

    /// <summary>The @font-face rules of the faces drawn as text, each with a subset of the glyphs used, or <see langword="null"/>.</summary>
    private string? FontFaces()
    {
        StringBuilder css = new();
        foreach (EmbeddedFace entry in _out.Faces)
        {
            Typeface face = entry.Face;
            if (!entry.Embeddable)
            {
                _out.Report(DiagnosticLevel.Warning, "Svg.FontNotEmbedded", $"The license of {face.FullName} does not allow embedding a subset; its text names the font only.");
                continue;
            }

            HashSet<ushort> glyphs = [.. entry.Glyphs];
            string used = entry.Text.ToString();
            for (int i = 0; i < used.Length;)
            {
                if (Rune.DecodeFromUtf16(used.AsSpan(i), out Rune rune, out int length) == System.Buffers.OperationStatus.Done && face.TryGetGlyph(rune.Value, out ushort g))
                {
                    glyphs.Add(g);
                }

                i += Math.Max(1, length);
            }

            FontSubset? subset = Subset(face, glyphs);
            if (subset is null)
            {
                _out.Report(DiagnosticLevel.Warning, "Svg.FontNotEmbedded", $"{face.FullName} could not be subset; its text names the font only.");
                continue;
            }

            bool trueType = subset.Format == SubsetFormat.TrueType;
            FontStyle style = face.Style;
            css.Append("@font-face{font-family:").Append(CssString(entry.Family))
                .Append(";src:url(").Append(SvgOutput.DataUri(trueType ? "font/ttf" : "font/otf", subset.Data.Span))
                .Append(") format(").Append(trueType ? "'truetype'" : "'opentype'")
                .Append(");font-weight:").Append(style.Weight.ToString(CultureInfo.InvariantCulture))
                .Append(";font-style:").Append(style.Slant switch { FontSlant.Italic => "italic", FontSlant.Oblique => "oblique", _ => "normal" })
                .Append('}');
        }

        return css.Length > 0 ? css.ToString() : null;
    }

    /// <summary>
    /// A subset the viewer can shape again (the glyphs GSUB can reach, with old ids, GSUB and GDEF kept; no GPOS, since each
    /// cluster is placed), or a plain one, or none.
    /// </summary>
    private static FontSubset? Subset(Typeface face, HashSet<ushort> glyphs)
    {
        SubsetFormat format = face.Outlines == FontOutlines.TrueType ? SubsetFormat.TrueType : SubsetFormat.OpenTypeCff;
        FontTag[] layout = [.. new[] { new FontTag("GSUB"), new FontTag("GDEF") }.Where(t => face.TryGetTable(t, out _))];
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (attempt == 0 && layout.Length > 0)
                {
                    HashSet<ushort> closed = [.. glyphs, .. face.Layout.GetClosure(glyphs)];
                    return FontSubsetter.Subset(face, closed, new SubsetOptions { Format = format, RetainGlyphIds = true, KeepTables = layout });
                }

                return FontSubsetter.Subset(face, glyphs, new SubsetOptions { Format = format });
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException or EgamiException)
            {
            }
        }

        return null;
    }
}
