using System.Globalization;

using Egami.Fonts.Shaping;
using Egami.Fonts.Unicode;

namespace Egami.Fonts.Layout;

/// <summary>
/// A paragraph of styled text, prepared once to be laid out at any width: styles resolved, bidi levels and break opportunities
/// found, text itemized into runs and each run shaped. Immutable and safe to share between threads.
/// </summary>
/// <remarks>
/// Runs split where the bidi level, the script, the font (after fallback) or a style field that changes shaping changes; paint
/// fields never split a run. Hard breaks, other control characters and placeholders are runs of their own without glyphs.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="rich-text"/>
/// </example>
public sealed class Paragraph
{
    /// <summary>Egami's default font size, as CSS's.</summary>
    internal const float DefaultSize = 16;

    private Paragraph(RichText text, ParagraphStyle style, ResolvedStyle[] styles, ParagraphRun[] runs, LineBreak[] breaks, BidiData bidi, (Font, GlyphBuffer)[]? ellipses)
    {
        Ellipses = ellipses;
        Measures = new AdvanceTable(text, styles, runs);
        RichText = text;
        Style = style;
        Styles = styles;
        Runs = runs;
        Breaks = breaks;
        Bidi = bidi;
    }

    /// <summary>Gets the text.</summary>
    public string Text => RichText.Text;

    /// <summary>Gets the styled text the paragraph was made from.</summary>
    public RichText RichText { get; }

    /// <summary>Gets the paragraph style.</summary>
    public ParagraphStyle Style { get; }

    /// <summary>The resolved styles: index 0 for text outside spans, then one per span.</summary>
    internal ResolvedStyle[] Styles { get; }

    /// <summary>The runs in logical order, covering the text without gaps.</summary>
    internal ParagraphRun[] Runs { get; }

    /// <summary>The break opportunities, in order; the last is at the end of the text.</summary>
    internal LineBreak[] Breaks { get; }

    /// <summary>The bidi levels and paragraphs, for reordering each line.</summary>
    internal BidiData Bidi { get; }

    /// <summary>The advance and spacing of every character, for measuring lines.</summary>
    internal AdvanceTable Measures { get; }

    /// <summary>CSS's min-content and max-content widths, found once.</summary>
    internal (float Min, float Max) IntrinsicWidths => (_intrinsic ??= new Lazy<(float, float)>(() => new LineBuilder(this).IntrinsicWidths())).Value;

    private Lazy<(float, float)>? _intrinsic;

    /// <summary>The ellipsis shaped in each style, when a line limit and an ellipsis are set.</summary>
    internal (Font Font, GlyphBuffer Glyphs)[]? Ellipses { get; }

    /// <summary>Lays the paragraph out at a width.</summary>
    /// <param name="width">The layout box's width in canvas units; <see cref="float.PositiveInfinity"/> for no wrapping, in
    /// which case the box is as wide as the longest line.</param>
    /// <returns>The layout.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> is negative or not a number.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="layout"/>
    /// </example>
    public TextLayout Layout(float width)
    {
        if (float.IsNaN(width) || width < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "A width is 0 or more.");
        }

        LineFlow flow = new(this);
        List<int>? balanced = Style.LineFill == LineFill.Balanced && float.IsFinite(width) ? flow.Builder.BalancedEnds(width) : null;
        List<LineDraft> drafts = [];
        if (balanced is not null)
        {
            int start = 0;
            foreach (int end in balanced)
            {
                drafts.Add(flow.Builder.FillTo(start, end));
                start = end;
            }
        }
        else
        {
            // Lines stop at MaxLines, or before the line whose bottom would pass MaxHeight.
            float height = 0;
            while ((Style.MaxLines is not int max || drafts.Count < max) && flow.NextDraft(width) is LineDraft draft)
            {
                height += draft.Ascent + draft.Descent;
                if (Style.MaxHeight is float limit && height > limit + 1e-4f)
                {
                    flow.Rewind(draft.Start);
                    break;
                }

                drafts.Add(draft);
            }
        }

        // Text past the limit is hidden; the last kept line makes room for the ellipsis.
        int keptEnd = drafts.Count == 0 ? 0 : drafts[^1].End;
        bool exceeded = (Style.MaxLines is not null || Style.MaxHeight is not null) && keptEnd < Text.Length;
        int hidden = keptEnd;
        if (exceeded && Ellipses is not null && drafts.Count > 0)
        {
            drafts[^1] = flow.Builder.Ellipsize(drafts[^1], width, out hidden);
        }

        float boxWidth = float.IsFinite(width) ? width : drafts.Count == 0 ? 0 : drafts.Max(d => d.ContentWidth);
        List<TextLine> lines = new(drafts.Count);
        float top = 0;
        float blockSize = drafts.Sum(d => d.Ascent + d.Descent);
        for (int i = 0; i < drafts.Count; i++)
        {
            TextLine line = flow.Builder.Finish(drafts[i], boxWidth, top, Style.TrimEdges && i == 0, Style.TrimEdges && i == drafts.Count - 1, blockSize);
            lines.Add(line);
            top += line.Height;
        }

        return new TextLayout(this, boxWidth, lines, exceeded ? new TextSegment(hidden, Text.Length - hidden) : null);
    }

    /// <summary>Starts laying the paragraph out one line at a time, each at its own width.</summary>
    /// <returns>A flow positioned at the first line.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="flow"/>
    /// </example>
    public LineFlow CreateFlow() => new(this);

    /// <summary>
    /// Gets the text style a run with <see cref="LayoutRun.StyleIndex"/> (or a decoration with
    /// <see cref="DecorationShape.StyleIndex"/>) was laid out in: the default style for 0, else that span's style with the
    /// default's values filled in where it sets none.
    /// </summary>
    /// <param name="styleIndex">0 for the default style, else 1 plus the span's index in <see cref="RichText.Spans"/>.</param>
    /// <returns>The style.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="styleIndex"/> is not a style of this paragraph.</exception>
    public TextStyle GetStyle(int styleIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(styleIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(styleIndex, Styles.Length);
        return Styles[styleIndex].Style;
    }

    /// <summary>Prepares a plain-text paragraph in the paragraph's default style.</summary>
    /// <param name="text">The text.</param>
    /// <param name="style">The paragraph style; its <see cref="ParagraphStyle.DefaultStyle"/> must name font families.</param>
    /// <returns>The paragraph.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> or <paramref name="style"/> is null.</exception>
    /// <exception cref="ArgumentException">No font families are given.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="rich-text"/>
    /// </example>
    public static Paragraph Create(string text, ParagraphStyle style)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Create(new RichText(text, null, Enumerable.Repeat(new Placeholder(0, 0), text.AsSpan().Count('\uFFFC'))), style);
    }

    /// <summary>Prepares a paragraph of styled text.</summary>
    /// <param name="text">The styled text.</param>
    /// <param name="style">The paragraph style; text and spans take unset fields from its <see cref="ParagraphStyle.DefaultStyle"/>.</param>
    /// <returns>The paragraph.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> or <paramref name="style"/> is null.</exception>
    /// <exception cref="ArgumentException">Some text has no font families in its style or the default style.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="rich-text"/>
    /// </example>
    public static Paragraph Create(RichText text, ParagraphStyle style)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(style.DefaultStyle, nameof(style));

        FontCache fonts = new();
        ResolvedStyle[] styles = new ResolvedStyle[text.Spans.Count + 1];
        styles[0] = ResolvedStyle.Resolve(style.DefaultStyle, null, text.Spans.Count < text.Text.Length || text.Spans.Count == 0, fonts);
        for (int i = 0; i < text.Spans.Count; i++)
        {
            styles[i + 1] = ResolvedStyle.Resolve(text.Spans[i].Style.InheritFrom(style.DefaultStyle), styles[0], true, fonts);
        }

        if (text.Text.Length > 0 && styles[0].Families.Count == 0 && CoversOutsideSpans(text))
        {
            throw new ArgumentException("Text outside the spans has no font families: set ParagraphStyle.DefaultStyle.FontFamilies.", nameof(style));
        }

        ShapingGroups.Assign(styles);

        string s = text.Text;
        LineBreak[] breaks = [.. Collect(LineBreaks.Enumerate(s, LineBreakOptionsFor(style, styles[0])))];
        BidiData bidi = BidiData.Resolve(s, style.WritingMode == WritingMode.HorizontalTb ? style.Direction : TextDirection.LeftToRight);
        ParagraphRun[] runs = Itemizer.Itemize(text, style, styles, bidi, fonts);
        (Font, GlyphBuffer)[]? ellipses = !string.IsNullOrEmpty(style.Ellipsis) && (style.MaxLines is not null || style.MaxHeight is not null) && styles.All(st => st.Primary is not null)
            ? [.. styles.Select(st => Itemizer.ShapeAlone(style.Ellipsis, st, style.Fallback, fonts))]
            : null;
        return new Paragraph(text, style, styles, runs, breaks, bidi, ellipses);
    }

    private static bool CoversOutsideSpans(RichText text)
    {
        int covered = 0;
        foreach (StyleSpan span in text.Spans)
        {
            covered += span.Length;
        }

        return covered < text.Text.Length;
    }

    /// <summary>The line breaking options with the default style's language when the options name none.</summary>
    private static LineBreakOptions LineBreakOptionsFor(ParagraphStyle style, ResolvedStyle defaults) =>
        style.LineBreaking.Language is null && defaults.Language is not null ? style.LineBreaking with { Language = defaults.Language } : style.LineBreaking;

    private static List<LineBreak> Collect(LineBreakEnumerator breaks)
    {
        List<LineBreak> list = [];
        foreach (LineBreak b in breaks)
        {
            list.Add(b);
        }

        return list;
    }
}

/// <summary>What a run is.</summary>
internal enum RunKind : byte
{
    /// <summary>Shaped text.</summary>
    Text,

    /// <summary>A placeholder's U+FFFC: no glyph, the placeholder's width.</summary>
    Placeholder,

    /// <summary>A tab: no glyph, its advance found at layout.</summary>
    Tab,

    /// <summary>A hard break or another control character: no glyph, no advance.</summary>
    Control,
}

/// <summary>A run of a paragraph: one bidi level, script, shaping group and font, shaped as one piece.</summary>
internal sealed class ParagraphRun(int start, int length, RunKind kind, int level, Script script, int style, Font font, GlyphBuffer? glyphs, int placeholder, RunProperties properties)
{
    public int Start { get; } = start;

    public int Length { get; } = length;

    public int End => Start + Length;

    public RunKind Kind { get; } = kind;

    public int Level { get; } = level;

    public Script Script { get; } = script;

    /// <summary>The style of the run's first character; paint fields may change inside the run.</summary>
    public int Style { get; } = style;

    public Font Font { get; } = font;

    /// <summary>The glyphs, for <see cref="RunKind.Text"/> runs.</summary>
    public GlyphBuffer? Glyphs { get; } = glyphs;

    /// <summary>The placeholder's index, for <see cref="RunKind.Placeholder"/> runs.</summary>
    public int Placeholder { get; } = placeholder;

    /// <summary>What the run was shaped with, to shape a piece of it again.</summary>
    public RunProperties Properties { get; } = properties;

    /// <summary>Whether the run stands upright in a vertical line, shaped top to bottom.</summary>
    public bool Upright { get; init; }
}

/// <summary>Bidi levels and paragraphs copied out of a <see cref="BidiResult"/>, owned by the paragraph.</summary>
internal sealed class BidiData(byte[] levels, BidiClass[] classes, (int Start, int End, int Level)[] paragraphs)
{
    /// <summary>The resolved level of each UTF-16 unit, before the line rules.</summary>
    public byte[] Levels { get; } = levels;

    /// <summary>The bidi class of each UTF-16 unit.</summary>
    public BidiClass[] Classes { get; } = classes;

    /// <summary>The bidi paragraphs: UTF-16 ranges and their levels.</summary>
    public (int Start, int End, int Level)[] Paragraphs { get; } = paragraphs;

    /// <summary>The paragraph level at a UTF-16 index (the last paragraph's at the end of the text).</summary>
    public int ParagraphLevelAt(int index)
    {
        foreach ((int start, int end, int level) in Paragraphs)
        {
            if (index < end)
            {
                return level;
            }
        }

        return Paragraphs.Length == 0 ? 0 : Paragraphs[^1].Level;
    }

    public static BidiData Resolve(string text, TextDirection direction)
    {
        using BidiResult result = Unicode.Bidi.Resolve(text, direction);
        (int, int, int)[] paragraphs = result.Paragraphs.Count > 0 ? [.. result.Paragraphs] : [(0, 0, result.ParagraphLevel)];
        return new BidiData(result.Levels.ToArray(), result.Classes.ToArray(), paragraphs);
    }
}

/// <summary>A text style with every field resolved, and the fonts its families give.</summary>
internal sealed class ResolvedStyle
{
    public required TextStyle Style { get; init; }

    public required IReadOnlyList<FontFamily> Families { get; init; }

    public required float Size { get; init; }

    public required FontStyle FontStyle { get; init; }

    public required IReadOnlyList<FontVariation> Variations { get; init; }

    public required string? Language { get; init; }

    public required IReadOnlyList<FontFeature> Features { get; init; }

    public required float LetterSpacing { get; init; }

    public required float WordSpacing { get; init; }

    public required LineHeight LineHeight { get; init; }

    public required float BaselineShift { get; init; }

    /// <summary>The first family's match, with the variations applied: the style's primary font.</summary>
    public required Font? Primary { get; init; }

    /// <summary>The index of the group of styles that shape alike; runs split only where it changes.</summary>
    public int ShapingGroup { get; set; }

    public static ResolvedStyle Resolve(TextStyle style, ResolvedStyle? defaults, bool needed, FontCache fonts)
    {
        IReadOnlyList<FontFamily> families = style.FontFamilies ?? [];
        float size = style.Size ?? Paragraph.DefaultSize;
        if (!float.IsFinite(size) || size < 0)
        {
            throw new ArgumentException($"A font size of {size} is not 0 or more.", nameof(style));
        }

        FontStyle fontStyle = style.FontStyle ?? FontStyle.Normal;
        IReadOnlyList<FontVariation> variations = style.Variations ?? [];
        Font? primary = families.Count > 0 ? fonts.Get(families[0].Match(fontStyle), size, variations) : null;
        if (needed && defaults is not null && families.Count == 0)
        {
            throw new ArgumentException("A span has no font families and the paragraph's default style names none.", nameof(style));
        }

        return new ResolvedStyle
        {
            Style = style,
            Families = families,
            Size = size,
            FontStyle = fontStyle,
            Variations = variations,
            Language = style.Language,
            Features = style.Features ?? [],
            LetterSpacing = style.LetterSpacing ?? 0,
            WordSpacing = style.WordSpacing ?? 0,
            LineHeight = style.LineHeight ?? LineHeight.Normal,
            BaselineShift = style.BaselineShift ?? 0,
            Primary = primary,
        };
    }

    /// <summary>Whether two styles shape text the same way: same fonts, size, style, variations, language, features and
    /// letter spacing (which turns ligatures off).</summary>
    public bool ShapesLike(ResolvedStyle other) =>
        Families.SequenceEqual(other.Families) && Size == other.Size && FontStyle == other.FontStyle && Variations.SequenceEqual(other.Variations)
        && Language == other.Language && Features.SequenceEqual(other.Features) && (LetterSpacing == 0) == (other.LetterSpacing == 0);
}

/// <summary>Numbers the groups of styles that shape alike.</summary>
internal static class ShapingGroups
{
    public static void Assign(ResolvedStyle[] styles)
    {
        List<ResolvedStyle> representatives = [];
        foreach (ResolvedStyle style in styles)
        {
            int group = representatives.FindIndex(r => r.ShapesLike(style));
            if (group < 0)
            {
                group = representatives.Count;
                representatives.Add(style);
            }

            style.ShapingGroup = group;
        }
    }
}

/// <summary>The fonts a paragraph uses: one <see cref="Font"/> per face, size and synthesis, variations applied once.</summary>
internal sealed class FontCache
{
    private readonly Dictionary<(Typeface, float, FontSynthesis), Font> _fonts = [];
    private readonly Dictionary<(Typeface, IReadOnlyList<FontVariation>), Typeface> _varied = new(VariedComparer.Instance);

    public Font Get(FontMatch match, float size, IReadOnlyList<FontVariation> variations) => Get(match.Typeface, match.Synthesis, size, variations);

    public Font Get(Typeface face, FontSynthesis synthesis, float size, IReadOnlyList<FontVariation> variations)
    {
        if (variations.Count > 0 && face.IsVariable)
        {
            if (!_varied.TryGetValue((face, variations), out Typeface? varied))
            {
                varied = face.WithVariations([.. variations]);
                _varied[(face, variations)] = varied;
            }

            face = varied;
        }

        if (!_fonts.TryGetValue((face, size, synthesis), out Font font))
        {
            font = new Font(face, size) { Synthesis = synthesis };
            _fonts[(face, size, synthesis)] = font;
        }

        return font;
    }

    private sealed class VariedComparer : IEqualityComparer<(Typeface, IReadOnlyList<FontVariation>)>
    {
        public static readonly VariedComparer Instance = new();

        public bool Equals((Typeface, IReadOnlyList<FontVariation>) x, (Typeface, IReadOnlyList<FontVariation>) y) =>
            ReferenceEquals(x.Item1, y.Item1) && x.Item2.SequenceEqual(y.Item2);

        public int GetHashCode((Typeface, IReadOnlyList<FontVariation>) obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Item1);
    }
}

/// <summary>Splits a paragraph into runs and shapes them (design stage B).</summary>
internal static class Itemizer
{
    private static readonly FontFeature[] NoOptionalLigatures =
    [
        new(new FontTag("liga"), 0), new(new FontTag("clig"), 0), new(new FontTag("dlig"), 0), new(new FontTag("hlig"), 0), new(new FontTag("calt"), 0),
    ];

    private readonly record struct Pending(int Start, int Length, RunKind Kind, int Level, Script Script, int Style, int Group, Font Font, int Placeholder, bool Upright = false);

    /// <summary>
    /// Whether a cluster stands upright in a vertical line: by its first character's Vertical_Orientation (UAX #50) with
    /// <see cref="TextOrientation.Mixed"/> (U, Tu and Tr upright, as browsers set Tr with vertical alternates), always with
    /// <see cref="TextOrientation.Upright"/>, never with <see cref="TextOrientation.Sideways"/> or in horizontal lines.
    /// </summary>
    private static bool IsUpright(ParagraphStyle paragraph, int cp) => paragraph.WritingMode != WritingMode.HorizontalTb && paragraph.TextOrientation switch
    {
        TextOrientation.Upright => true,
        TextOrientation.Sideways => false,
        _ => UnicodeData.GetVerticalOrientation(cp) != VerticalOrientation.Rotated,
    };

    public static ParagraphRun[] Itemize(RichText rich, ParagraphStyle paragraph, ResolvedStyle[] styles, BidiData bidi, FontCache fonts)
    {
        string text = rich.Text;
        List<ScriptRun> scripts = [];
        ScriptItemizer.Itemize(text, scripts);
        int scriptIndex = 0;
        int placeholder = 0;
        List<Pending> pending = [];
        foreach (TextSegment cluster in TextSegmenter.Graphemes(text))
        {
            while (scriptIndex + 1 < scripts.Count && cluster.Start >= scripts[scriptIndex].Start + scripts[scriptIndex].Length)
            {
                scriptIndex++;
            }

            int styleIndex = rich.SpanAt(cluster.Start) + 1;
            ResolvedStyle style = styles[styleIndex];
            Script script = scripts.Count > 0 ? scripts[scriptIndex].Script : Script.Common;
            int level = bidi.Levels[cluster.Start];
            int cp = TextSegmenter.CodePointAt(text, cluster.Start, out _);
            ReadOnlySpan<char> clusterText = text.AsSpan(cluster.Start, cluster.Length);

            RunKind kind = cp == 0xFFFC && placeholder < rich.Placeholders.Count ? RunKind.Placeholder
                : cp == '\t' ? RunKind.Tab
                : UnicodeData.CategoryOf(cp) == UnicodeCategory.Control || IsHardBreak(cp) ? RunKind.Control
                : RunKind.Text;
            if (kind != RunKind.Text)
            {
                // Placeholders, tabs and breaks are runs of their own, in the style's primary font for their line box.
                Font own = style.Primary ?? styles[0].Primary ?? default;
                pending.Add(new Pending(cluster.Start, cluster.Length, kind, level, script, styleIndex, style.ShapingGroup, own, kind == RunKind.Placeholder ? placeholder++ : -1));
                continue;
            }

            Font font = ChooseFont(clusterText, style, paragraph.Fallback, script, fonts);
            bool upright = IsUpright(paragraph, cp);
            if (pending.Count > 0 && pending[^1] is { Kind: RunKind.Text } last && last.Level == level && last.Script == script
                && last.Group == style.ShapingGroup && last.Font == font && last.Upright == upright && last.Start + last.Length == cluster.Start)
            {
                pending[^1] = last with { Length = last.Length + cluster.Length };
                continue;
            }

            pending.Add(new Pending(cluster.Start, cluster.Length, RunKind.Text, level, script, styleIndex, style.ShapingGroup, font, -1, upright));
        }

        ParagraphRun[] runs = new ParagraphRun[pending.Count];
        for (int i = 0; i < runs.Length; i++)
        {
            Pending p = pending[i];
            GlyphBuffer? glyphs = null;
            RunProperties properties = default;
            if (p.Kind == RunKind.Text)
            {
                ResolvedStyle style = styles[p.Style];
                List<FontFeature> features = [.. style.Features];
                if (style.LetterSpacing != 0)
                {
                    features.AddRange(NoOptionalLigatures);
                }

                properties = new()
                {
                    Script = p.Script.IsShared ? null : p.Script,
                    Language = style.Language,
                    Direction = p.Upright ? TextDirection.TopToBottom : (p.Level & 1) != 0 ? TextDirection.RightToLeft : TextDirection.LeftToRight,
                    Features = features,
                };
                glyphs = new GlyphBuffer();
                TextShaper.ShapeRun(text, p.Start, p.Length, p.Font, properties, glyphs);
            }

            runs[i] = new ParagraphRun(p.Start, p.Length, p.Kind, p.Level, p.Script, p.Style, p.Font, glyphs, p.Placeholder, properties) { Upright = p.Upright };
        }

        return runs;
    }

    private static bool IsHardBreak(int cp) => cp is 0x0A or 0x0B or 0x0C or 0x0D or 0x85 or 0x2028 or 0x2029;

    /// <summary>Shapes a short string (an ellipsis) on its own in a style, in the font CSS matching picks for its first cluster.</summary>
    public static (Font, GlyphBuffer) ShapeAlone(string text, ResolvedStyle style, FontFallback fallback, FontCache fonts)
    {
        int first = TextSegmenter.NextGraphemeBoundary(text, 0);
        Font font = ChooseFont(text.AsSpan(0, first), style, fallback, Script.Common, fonts);
        GlyphBuffer glyphs = new();
        TextShaper.ShapeRun(text, 0, text.Length, font, new RunProperties { Language = style.Language, Features = style.Features }, glyphs);
        return (font, glyphs);
    }

    /// <summary>
    /// The font for one grapheme cluster, as CSS matches fonts: the style's families in order, then the paragraph's fallback
    /// families and resolver, the first that maps every character, emoji presentation preferring color faces; else the primary
    /// font, which draws .notdef. Spaces and punctuation take the first family that has them, as browsers do, not the font of
    /// the text around them.
    /// </summary>
    private static Font ChooseFont(ReadOnlySpan<char> cluster, ResolvedStyle style, FontFallback fallback, Script script, FontCache fonts)
    {
        Font primary = style.Primary!.Value;

        bool emoji = TextItemizer.WantsEmoji(cluster);
        bool textPresentation = !emoji && cluster.Contains('\uFE0E');
        Font? anyMapping = null;
        for (int i = 0; i < style.Families.Count; i++)
        {
            Font candidate = i == 0 ? primary : fonts.Get(style.Families[i].Match(style.FontStyle), style.Size, style.Variations);
            if (Consider(candidate, cluster, emoji, textPresentation, ref anyMapping))
            {
                return candidate;
            }
        }

        foreach (FontFamily family in fallback.Families)
        {
            Font candidate = fonts.Get(family.Match(style.FontStyle), style.Size, style.Variations);
            if (Consider(candidate, cluster, emoji, textPresentation, ref anyMapping))
            {
                return candidate;
            }
        }

        if (anyMapping is Font found)
        {
            return found;
        }

        if (fallback.Resolver is { } resolver && resolver.TryResolve(cluster, script, style.Language, style.FontStyle, out Typeface? resolved) && resolved is not null)
        {
            return fonts.Get(resolved, FontSynthesis.None, style.Size, style.Variations);
        }

        return primary;
    }

    private static bool Consider(Font candidate, ReadOnlySpan<char> cluster, bool emoji, bool textPresentation, ref Font? anyMapping)
    {
        if (!TextItemizer.Maps(candidate.Typeface, cluster))
        {
            return false;
        }

        bool color = (candidate.Typeface.ColorFormats & ~ColorGlyphFormats.Svg) != 0;
        if (emoji ? color : !textPresentation || !color)
        {
            return true;
        }

        anyMapping ??= candidate;
        return false;
    }

}
