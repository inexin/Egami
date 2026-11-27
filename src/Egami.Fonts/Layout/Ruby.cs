using System.Globalization;

using Egami.Fonts.Shaping;
using Egami.Fonts.Unicode;

namespace Egami.Fonts.Layout;

/// <summary>
/// A ruby annotation: short text (furigana, pinyin, bopomofo, a gloss) set in small type beside a range of a
/// <see cref="RichText"/>, its base. One annotation is one ruby group: over a single character it is mono ruby, over several
/// group ruby; adjacent annotations make jukugo ruby when <see cref="RubyOptions.Merge"/> joins them.
/// </summary>
/// <remarks>
/// A base never breaks across lines and stays in one bidi run. The ruby text is not part of the paragraph's text: hit testing,
/// carets and selections stay on the base. Laid out ruby comes back as <see cref="LayoutRunKind.Ruby"/> runs in
/// <see cref="TextLine.AnnotationRuns"/> and as boxes from <see cref="TextLayout.GetRubyBounds"/>.
/// </remarks>
/// <param name="Start">The base's first UTF-16 index.</param>
/// <param name="Length">The base's length in UTF-16 units, at least 1.</param>
/// <param name="Text">The ruby text.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="ruby"/>
/// </example>
public sealed record class RubyAnnotation(int Start, int Length, string Text)
{
    /// <summary>Gets the index just past the base.</summary>
    public int End => Start + Length;

    /// <summary>
    /// Gets the ruby text's style; its unset fields come from the base's style, at <see cref="RubyOptions.SizeRatio"/> of its
    /// size. Backgrounds, decorations, baseline shift and line height are not taken from the base, and are not drawn for ruby.
    /// </summary>
    public TextStyle? Style { get; init; }

    /// <summary>Gets which side of the base the ruby goes on.</summary>
    public RubyPosition Position { get; init; }

    /// <summary>Gets how the narrower of base and ruby spreads over the wider.</summary>
    public RubyAlign Align { get; init; }

    /// <summary>Gets an extra gap between the base and the ruby, in canvas units (Word's <c>w:hpsRaise</c> less the ruby's
    /// size); null for none.</summary>
    public float? Offset { get; init; }
}

/// <summary>Where a <see cref="RubyAnnotation"/> sits (CSS <c>ruby-position</c>).</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="ruby"/>
/// </example>
public enum RubyPosition
{
    /// <summary>Above the base, or to its right in vertical lines.</summary>
    Over = 0,

    /// <summary>Below the base, or to its left in vertical lines.</summary>
    Under = 1,

    /// <summary>To the right of the base, set vertically, as bopomofo in Taiwan; the base's advance grows by the ruby's column. In
    /// vertical lines the same as <see cref="Over"/>.</summary>
    InterCharacter = 2,
}

/// <summary>How the narrower of a ruby group's base and ruby spreads over the wider (CSS <c>ruby-align</c>).</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="ruby"/>
/// </example>
public enum RubyAlign
{
    /// <summary>Space between characters and half as much at each end (JLREQ's 1:2:1); at a line's edge, none on that side.</summary>
    SpaceAround = 0,

    /// <summary>Centred, characters kept together.</summary>
    Center = 1,

    /// <summary>At the group's start, characters kept together.</summary>
    Start = 2,

    /// <summary>At the group's end, characters kept together.</summary>
    End = 3,

    /// <summary>Space between characters only (0:1:0); a single character is centred.</summary>
    SpaceBetween = 4,
}

/// <summary>Whether ruby wider than its base may hang over the characters beside it.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="ruby"/>
/// </example>
public enum RubyOverhang
{
    /// <summary>Over kana and punctuation on the same line by up to half a ruby character on each side, never over kanji or
    /// another ruby group (JLREQ 3.3); the line edge stops it.</summary>
    Auto = 0,

    /// <summary>Never: the base spreads to the ruby's width, as Word lays ruby out.</summary>
    None = 1,
}

/// <summary>How adjacent ruby annotations (one ending where the next starts, on the same side) lay out together.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="ruby"/>
/// </example>
public enum RubyMerge
{
    /// <summary>Each annotation is a group of its own (CSS <c>ruby-merge: separate</c>).</summary>
    Separate = 0,

    /// <summary>Adjacent annotations are one group: their ruby texts joined over their bases joined, which never break apart.</summary>
    Collapse = 1,

    /// <summary>Jukugo ruby: each annotation keeps its own place while every ruby fits its base, else the run is laid out as one
    /// group; lines may still break between the annotations, each part keeping its own ruby.</summary>
    Auto = 2,
}

/// <summary>How a paragraph lays out ruby (<see cref="ParagraphStyle.Ruby"/>).</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="ruby"/>
/// </example>
public sealed record class RubyOptions
{
    private readonly float _sizeRatio = 0.5f;

    /// <summary>Gets the default options: half-size ruby, overhang, separate groups.</summary>
    public static RubyOptions Default { get; } = new();

    /// <summary>Gets the ruby's size as a share of its base's, where the annotation's style sets no size.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not finite or not more than 0.</exception>
    public float SizeRatio
    {
        get => _sizeRatio;
        init => _sizeRatio = float.IsFinite(value) && value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "A size ratio is more than 0.");
    }

    /// <summary>Gets whether ruby wider than its base hangs over the characters beside it.</summary>
    public RubyOverhang Overhang { get; init; }

    /// <summary>Gets how adjacent annotations lay out together.</summary>
    public RubyMerge Merge { get; init; }
}

/// <summary>A piece of an annotation's ruby text shaped in one font and orientation.</summary>
internal readonly record struct RubySegment(Font Font, GlyphBuffer Glyphs, bool Upright, float Advance);

/// <summary>
/// A ruby box on a line: annotations <see cref="First"/> to <see cref="Last"/> laid out as one group over the base
/// <c>[Start, End)</c>, <see cref="Base"/> wide unspaced and <see cref="Width"/> wide on the line; the ruby
/// (<see cref="Ruby"/> wide) reaches <see cref="HangStart"/> and <see cref="HangEnd"/> past the box over its neighbours.
/// </summary>
internal readonly record struct RubyBox(int First, int Last, int Start, int End, float Base, float Ruby, float Width, float HangStart, float HangEnd)
{
    /// <summary>The width the box adds to its base's.</summary>
    public float Extra => Width - Base;
}

/// <summary>A ruby box placed on a draft line: the piece its left edge starts, and on which side it meets a line edge.</summary>
internal sealed class DraftRuby
{
    public required RubyBox Box { get; init; }

    /// <summary>The box's visually leftmost base piece.</summary>
    public required DraftRun Anchor { get; init; }

    /// <summary>The bidi level of the box's base.</summary>
    public required int Level { get; init; }

    /// <summary>Whether the box touches the line's left edge.</summary>
    public required bool AtLeft { get; init; }

    /// <summary>Whether the box touches the line's right edge.</summary>
    public required bool AtRight { get; init; }

    /// <summary>The anchor's x once the line is placed.</summary>
    public float X { get; set; }
}

/// <summary>
/// A paragraph's ruby: each annotation's style, shaped text and metrics, the chains adjacent annotations make, and the width
/// every chain adds to its base, as prefix sums, so a line with ruby is measured as fast as one without until a line edge cuts
/// a chain.
/// </summary>
internal sealed class RubyTable
{
    private const float Epsilon = 1e-4f;

    private readonly string _text;
    private readonly AdvanceTable _m;
    private readonly int[] _annotationAt;
    private readonly int[] _chainOf;
    private readonly (int First, int Last)[] _chains;
    private readonly float[] _full;
    private readonly float[] _extraBefore;

    private RubyTable(string text, AdvanceTable measures, RubyOptions options, RubyAnnotation[] annotations, Entry[] entries, bool vertical)
    {
        _text = text;
        _m = measures;
        Options = options;
        Annotations = annotations;
        Entries = entries;
        Vertical = vertical;

        int n = text.Length;
        _annotationAt = new int[n];
        Array.Fill(_annotationAt, -1);
        _chainOf = new int[annotations.Length];
        List<(int, int)> chains = [];
        for (int i = 0; i < annotations.Length; i++)
        {
            Array.Fill(_annotationAt, i, annotations[i].Start, annotations[i].Length);
            bool joins = i > 0 && options.Merge != RubyMerge.Separate && annotations[i - 1].End == annotations[i].Start
                && entries[i - 1].Position == entries[i].Position && entries[i].Position != RubyPosition.InterCharacter;
            if (joins)
            {
                chains[^1] = (chains[^1].Item1, i);
            }
            else
            {
                chains.Add((i, i));
            }

            _chainOf[i] = chains.Count - 1;
        }

        _chains = [.. chains];
        _full = new float[_chains.Length];
        _extraBefore = new float[n + 1];
        for (int c = 0; c < _chains.Length; c++)
        {
            _full[c] = SumExtra(_chains[c].First, _chains[c].Last, 0, n);
            _extraBefore[annotations[_chains[c].Last].End] += _full[c];
        }

        for (int i = 0; i < n; i++)
        {
            _extraBefore[i + 1] += _extraBefore[i];
        }
    }

    /// <summary>The annotations, sorted by start.</summary>
    public RubyAnnotation[] Annotations { get; }

    /// <summary>What each annotation lays out with.</summary>
    public Entry[] Entries { get; }

    public RubyOptions Options { get; }

    public bool Vertical { get; }

    /// <summary>
    /// Resolves, shapes and measures a paragraph's ruby; the ruby styles are appended to <paramref name="styles"/> from
    /// <paramref name="firstStyle"/> on, one per annotation.
    /// </summary>
    public static RubyTable Create(RichText rich, ParagraphStyle paragraph, ResolvedStyle[] styles, int firstStyle, AdvanceTable measures, FontCache fonts)
    {
        RubyAnnotation[] annotations = [.. rich.Ruby];
        bool vertical = paragraph.WritingMode != WritingMode.HorizontalTb;
        bool central = vertical && paragraph.TextOrientation != TextOrientation.Sideways;
        Entry[] entries = new Entry[annotations.Length];
        for (int i = 0; i < annotations.Length; i++)
        {
            RubyAnnotation a = annotations[i];
            int baseStyle = rich.SpanAt(a.Start) + 1;
            ResolvedStyle based = styles[baseStyle];
            ResolvedStyle ruby = styles[firstStyle + i];
            RubyPosition position = vertical && a.Position == RubyPosition.InterCharacter ? RubyPosition.Over : a.Position;
            bool column = !vertical && position == RubyPosition.InterCharacter;
            (RubySegment[] segments, int[] clusters) = Shape(a.Text, ruby, paragraph, fonts, column);
            float spacing = ruby.LetterSpacing;
            float advance = 0;
            foreach (RubySegment s in segments)
            {
                advance += s.Advance;
            }

            advance += spacing * (clusters.Length - 1);
            (float ra, float rd, _) = ruby.Primary is Font rf ? LineBuilder.Rounded(rf) : (0, 0, 0);
            (float ba, float bd, _) = based.Primary is Font bf ? LineBuilder.Rounded(bf) : (0, 0, 0);
            float shift = based.BaselineShift;
            float offset = a.Offset ?? 0;
            float thickness = ra + rd;

            // JLREQ 3.3 and CSS Ruby 1: the ruby's ideographic em box stands on its base's, the offset beyond. Each em box is
            // the font's (BASE ideo and idtp in horizontal lines, else centred between ascent and descent), or centred on the
            // central baseline in vertical lines.
            float baseSize = based.Primary is Font bs ? bs.Size : 0, rubySize = ruby.Primary is Font rs ? rs.Size : 0;
            (float over, float under) = central || based.Primary is not Font bem ? (baseSize / 2, baseSize / 2) : IdeographicEmBox.Edges(bem);
            (float rubyOver, float rubyUnder) = central || ruby.Primary is not Font rem ? (rubySize / 2, rubySize / 2) : IdeographicEmBox.Edges(rem);
            float anchor, top = float.PositiveInfinity, bottom = float.NegativeInfinity;
            if (column)
            {
                anchor = 0;
            }
            else if (position == RubyPosition.Over)
            {
                anchor = shift - over - offset - rubyUnder;
                top = central ? anchor - (thickness / 2) : anchor - ra;
            }
            else
            {
                anchor = shift + under + offset + rubyOver;
                bottom = central ? anchor + (thickness / 2) : anchor + rd;
            }

            entries[i] = new Entry
            {
                Style = firstStyle + i,
                BaseStyle = baseStyle,
                Position = position,
                Column = column,
                Segments = segments,
                Clusters = clusters,
                Advance = column ? ruby.Size : advance,
                ColumnLength = column ? advance : 0,
                Size = ruby.Size,
                Spacing = spacing,
                Ascent = ra,
                Descent = rd,
                BaseAscent = ba,
                BaseDescent = bd,
                Top = top,
                Bottom = bottom,
                Anchor = anchor,
            };
        }

        return new RubyTable(rich.Text, measures, paragraph.Ruby, annotations, entries, vertical);
    }

    /// <summary>The ruby style of an annotation: its own style over the base's, at the size ratio, without the fields ruby does
    /// not take from its base.</summary>
    public static TextStyle StyleOf(RubyAnnotation annotation, TextStyle baseStyle, float ratio)
    {
        TextStyle parent = baseStyle with
        {
            Size = (baseStyle.Size ?? Paragraph.DefaultSize) * ratio,
            Background = null,
            BackgroundPaint = null,
            Decoration = null,
            LetterSpacing = null,
            WordSpacing = null,
            LineHeight = null,
            BaselineShift = null,
            Emphasis = null,
            CombineUpright = null,
        };
        return (annotation.Style ?? parent).InheritFrom(parent);
    }

    /// <summary>Shapes ruby text: clusters in the font CSS matching picks, upright or sideways in vertical lines (always upright
    /// for an inter-character column), one segment per font and orientation. Also returns the grapheme starts and the end.</summary>
    private static (RubySegment[] Segments, int[] Clusters) Shape(string text, ResolvedStyle style, ParagraphStyle paragraph, FontCache fonts, bool column)
    {
        List<RubySegment> segments = [];
        List<int> clusters = [];
        if (style.Primary is null || text.Length == 0)
        {
            return ([], [0]);
        }

        int segmentStart = 0;
        Font segmentFont = default;
        bool segmentUpright = false;
        foreach (TextSegment cluster in TextSegmenter.Graphemes(text))
        {
            clusters.Add(cluster.Start);
            int cp = TextSegmenter.CodePointAt(text, cluster.Start, out _);
            Font font = Itemizer.ChooseFont(text.AsSpan(cluster.Start, cluster.Length), style, paragraph.Fallback, Script.Common, fonts);
            bool upright = column || Itemizer.IsUpright(paragraph, cp);
            if (cluster.Start > 0 && (font != segmentFont || upright != segmentUpright))
            {
                segments.Add(ShapeSegment(text, segmentStart, cluster.Start, segmentFont, segmentUpright, style));
                segmentStart = cluster.Start;
            }

            (segmentFont, segmentUpright) = (font, upright);
        }

        segments.Add(ShapeSegment(text, segmentStart, text.Length, segmentFont, segmentUpright, style));
        clusters.Add(text.Length);
        return ([.. segments], [.. clusters]);
    }

    private static RubySegment ShapeSegment(string text, int start, int end, Font font, bool upright, ResolvedStyle style)
    {
        GlyphBuffer glyphs = new();
        RunProperties properties = new()
        {
            Language = style.Language,
            Features = style.Features,
            Direction = upright ? TextDirection.TopToBottom : TextDirection.LeftToRight,
        };
        TextShaper.ShapeRun(text, start, end - start, font, properties, glyphs);
        float advance = 0;
        foreach (GlyphPosition p in glyphs.Positions)
        {
            advance += upright ? -p.YAdvance : p.XAdvance;
        }

        return new RubySegment(font, glyphs, upright, advance);
    }

    /// <summary>The non-mandatory break opportunities a ruby base removes: inside an annotation, and with
    /// <see cref="RubyMerge.Collapse"/> between annotations of one chain.</summary>
    public bool SplitsUnit(int index)
    {
        if (index <= 0 || index >= _text.Length)
        {
            return false;
        }

        int a = _annotationAt[index];
        if (a < 0)
        {
            return false;
        }

        if (Annotations[a].Start < index)
        {
            return true;
        }

        return Options.Merge == RubyMerge.Collapse && a > 0 && _chainOf[a - 1] == _chainOf[a];
    }

    /// <summary>The width ruby adds to the line content <c>[start, end)</c>: every box of the annotations wholly inside it, with
    /// overhang only over characters inside it.</summary>
    public float Extra(int start, int end)
    {
        if (end <= start)
        {
            return 0;
        }

        float extra = _extraBefore[end] - _extraBefore[start];
        int a1 = _annotationAt[start];
        int a2 = _annotationAt[end - 1];
        int c1 = a1 < 0 ? -1 : _chainOf[a1];
        int c2 = a2 < 0 ? -1 : _chainOf[a2];
        if (c1 >= 0)
        {
            extra += Part(c1, start, end) - (Annotations[_chains[c1].Last].End <= end ? _full[c1] : 0);
        }

        if (c2 >= 0 && c2 != c1)
        {
            extra += Part(c2, start, end) - (Annotations[_chains[c2].Last].End <= end ? _full[c2] : 0);
        }

        return extra;
    }

    /// <summary>What a chain cut by a line's edges adds: its annotations wholly inside <c>[start, end)</c>.</summary>
    private float Part(int chain, int start, int end)
    {
        (int p, int q) = Inside(chain, start, end);
        return p > q ? 0 : SumExtra(p, q, start, end);
    }

    /// <summary>The first and last annotations of a chain wholly inside <c>[start, end)</c>; first past last for none.</summary>
    private (int First, int Last) Inside(int chain, int start, int end)
    {
        (int p, int q) = _chains[chain];
        while (p <= q && Annotations[p].Start < start)
        {
            p++;
        }

        while (q >= p && Annotations[q].End > end)
        {
            q--;
        }

        return (p, q);
    }

    /// <summary>Adds the boxes of the annotations wholly inside the line content <c>[start, end)</c> to <paramref name="boxes"/>, in
    /// order.</summary>
    public void Boxes(int start, int end, List<RubyBox> boxes)
    {
        // The first annotation ending after the line's start: annotations never overlap, so their ends rise with their starts.
        int lo = 0, hi = Annotations.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (Annotations[mid].End <= start)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        for (int a = lo; a < Annotations.Length && Annotations[a].Start < end;)
        {
            (int p, int q) = Inside(_chainOf[a], start, end);
            if (p <= q)
            {
                AddBoxes(p, q, start, end, boxes);
            }

            a = _chains[_chainOf[a]].Last + 1;
        }
    }

    private float SumExtra(int p, int q, int start, int end)
    {
        if (Joined(p, q))
        {
            return Box(p, q, start, end).Extra;
        }

        float sum = 0;
        for (int i = p; i <= q; i++)
        {
            sum += Box(i, i, start, end).Extra;
        }

        return sum;
    }

    private void AddBoxes(int p, int q, int start, int end, List<RubyBox> boxes)
    {
        if (Joined(p, q))
        {
            boxes.Add(Box(p, q, start, end));
            return;
        }

        for (int i = p; i <= q; i++)
        {
            boxes.Add(Box(i, i, start, end));
        }
    }

    /// <summary>Whether annotations of one chain lay out as one group: always with <see cref="RubyMerge.Collapse"/>, and with
    /// <see cref="RubyMerge.Auto"/> when one's ruby is wider than its base.</summary>
    private bool Joined(int p, int q)
    {
        if (p >= q)
        {
            return false;
        }

        if (Options.Merge == RubyMerge.Collapse)
        {
            return true;
        }

        for (int i = p; i <= q; i++)
        {
            if (Entries[i].Advance > BaseWidth(Annotations[i].Start, Annotations[i].End) + Epsilon)
            {
                return true;
            }
        }

        return false;
    }

    private float BaseWidth(int start, int end) => _m.Prefix[end] - _m.Prefix[start];

    /// <summary>The box of annotations <paramref name="p"/> to <paramref name="q"/> as one group on a line whose content is
    /// <c>[lineStart, lineEnd)</c>.</summary>
    private RubyBox Box(int p, int q, int lineStart, int lineEnd)
    {
        int start = Annotations[p].Start, end = Annotations[q].End;
        float b = BaseWidth(start, end);
        float r = 0;
        for (int i = p; i <= q; i++)
        {
            r += Entries[i].Advance;
        }

        if (Entries[p].Column)
        {
            return new RubyBox(p, q, start, end, b, r, b + r, 0, 0);
        }

        float extra = Math.Max(0, r - b);
        float hangStart = 0, hangEnd = 0;
        if (extra > Epsilon && Options.Overhang == RubyOverhang.Auto)
        {
            if (start - 1 >= lineStart && CanHangOver(start - 1))
            {
                hangStart = Math.Min(extra / 2, Entries[p].Size / 2);
            }

            if (end < lineEnd && CanHangOver(end))
            {
                hangEnd = Math.Min(extra / 2, Entries[q].Size / 2);
            }
        }

        return new RubyBox(p, q, start, end, b, r, b + extra - hangStart - hangEnd, hangStart, hangEnd);
    }

    /// <summary>Whether ruby may hang over the character at a UTF-16 index: kana or punctuation outside any ruby base.</summary>
    private bool CanHangOver(int index)
    {
        if (_annotationAt[index] >= 0)
        {
            return false;
        }

        if (char.IsLowSurrogate(_text[index]) && index > 0 && char.IsHighSurrogate(_text[index - 1]))
        {
            index--;
        }

        int cp = TextSegmenter.CodePointAt(_text, index, out _);
        if (cp is (>= 0x3041 and <= 0x30FF) or (>= 0x31F0 and <= 0x31FF) or (>= 0xFF66 and <= 0xFF9F))
        {
            return true;
        }

        return UnicodeData.CategoryOf(cp) is UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or UnicodeCategory.OtherPunctuation
            or UnicodeCategory.DashPunctuation or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation
            or UnicodeCategory.ConnectorPunctuation;
    }

    /// <summary>
    /// Where <paramref name="free"/> space goes around <paramref name="count"/> items spread by an alignment, in visual order:
    /// before the first, between each two and after the last. With <see cref="RubyAlign.SpaceAround"/> a side at a line edge
    /// gets none.
    /// </summary>
    public static void Gaps(Span<float> gaps, int count, float free, RubyAlign align, bool atLeft, bool atRight)
    {
        gaps.Clear();
        if (count <= 0 || free <= 0)
        {
            return;
        }

        switch (align)
        {
            case RubyAlign.Start:
                gaps[count] = free;
                return;
            case RubyAlign.End:
                gaps[0] = free;
                return;
            case RubyAlign.SpaceBetween when count > 1:
                for (int k = 1; k < count; k++)
                {
                    gaps[k] = free / (count - 1);
                }

                return;
            case RubyAlign.SpaceAround:
                float left = atLeft ? 0 : 1, right = atRight ? 0 : 1;
                float total = left + right + (2 * (count - 1));
                if (total > 0)
                {
                    gaps[0] = free * left / total;
                    gaps[count] = free * right / total;
                    for (int k = 1; k < count; k++)
                    {
                        gaps[k] = free * 2 / total;
                    }

                    return;
                }

                break;
        }

        gaps[0] = gaps[count] = free / 2;
    }

    /// <summary>What an annotation lays out with.</summary>
    internal sealed class Entry
    {
        /// <summary>The ruby style's index in the paragraph's styles.</summary>
        public required int Style { get; init; }

        public required int BaseStyle { get; init; }

        /// <summary>The side, <see cref="RubyPosition.InterCharacter"/> as <see cref="RubyPosition.Over"/> in vertical lines.</summary>
        public required RubyPosition Position { get; init; }

        /// <summary>Whether the ruby is an inter-character column beside its base in a horizontal line.</summary>
        public required bool Column { get; init; }

        public required RubySegment[] Segments { get; init; }

        /// <summary>The ruby text's grapheme starts, then its length.</summary>
        public required int[] Clusters { get; init; }

        /// <summary>The ruby's advance along the line (letter spacing included), or for a column its width.</summary>
        public required float Advance { get; init; }

        /// <summary>A column's length down the line.</summary>
        public required float ColumnLength { get; init; }

        public required float Size { get; init; }

        public required float Spacing { get; init; }

        public required float Ascent { get; init; }

        public required float Descent { get; init; }

        public required float BaseAscent { get; init; }

        public required float BaseDescent { get; init; }

        /// <summary>The ruby's over edge relative to the baseline (+∞ when it adds nothing above).</summary>
        public required float Top { get; init; }

        /// <summary>The ruby's under edge relative to the baseline (−∞ when it adds nothing below).</summary>
        public required float Bottom { get; init; }

        /// <summary>Where the ruby's glyphs sit relative to the base's baseline: its alphabetic baseline, or its central one in
        /// vertical lines with mixed or upright orientation.</summary>
        public required float Anchor { get; init; }
    }
}

/// <summary>A line draft's pieces when it has ruby: the ruby boxes ride along, so drafts without ruby carry nothing.</summary>
internal sealed class RubyPieces(List<DraftRuby> rubies) : List<DraftRun>
{
    public List<DraftRuby> Rubies { get; } = rubies;
}
