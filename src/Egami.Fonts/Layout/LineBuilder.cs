using System.Numerics;

using Egami.Fonts.Shaping;
using Egami.Fonts.Unicode;

namespace Egami.Fonts.Layout;

/// <summary>
/// Every character's advance from the paragraph's shaping, and the spacing after it, as prefix sums, so a line's width is a
/// subtraction until a break falls where shaping must be redone.
/// </summary>
internal sealed class AdvanceTable
{
    public AdvanceTable(RichText text, ResolvedStyle[] styles, ParagraphRun[] runs)
    {
        string s = text.Text;
        int n = s.Length;
        float[] advance = new float[n];
        float[] spacing = new float[n];
        Unsafe = new bool[n + 1];
        RunOf = new int[n];
        TabsBefore = new int[n + 1];
        for (int r = 0; r < runs.Length; r++)
        {
            ParagraphRun run = runs[r];
            Array.Fill(RunOf, r, run.Start, run.Length);
            if (run.Kind == RunKind.Placeholder)
            {
                advance[run.Start] = text.Placeholders[run.Placeholder].Width;
            }
            else if (run.Glyphs is GlyphBuffer glyphs)
            {
                for (int g = 0; g < glyphs.Count; g++)
                {
                    int cluster = glyphs.Clusters[g];
                    advance[cluster] += run.Upright ? -glyphs.Positions[g].YAdvance : glyphs.Positions[g].XAdvance;
                    if ((glyphs.Flags[g] & GlyphFlags.UnsafeToBreak) != 0 && cluster > run.Start)
                    {
                        Unsafe[cluster] = true;
                    }
                }
            }
        }

        // Letter spacing after each grapheme cluster (none in cursive scripts), word spacing on each word separator (CSS Text 3).
        foreach (TextSegment cluster in TextSegmenter.Graphemes(s))
        {
            ParagraphRun run = runs[RunOf[cluster.Start]];
            ResolvedStyle style = styles[text.SpanAt(cluster.Start) + 1];
            if (run.Kind is RunKind.Control)
            {
                continue;
            }

            float extra = 0;
            if (style.LetterSpacing != 0 && !IsCursive(run.Script))
            {
                extra += style.LetterSpacing;
            }

            if (style.WordSpacing != 0 && IsWordSeparator(TextSegmenter.CodePointAt(s, cluster.Start, out _)))
            {
                extra += style.WordSpacing;
            }

            spacing[cluster.Start] = extra;
        }

        Spacing = spacing;
        Prefix = new float[n + 1];
        for (int i = 0; i < n; i++)
        {
            Prefix[i + 1] = Prefix[i] + advance[i] + spacing[i];
            TabsBefore[i + 1] = TabsBefore[i] + (s[i] == '\t' ? 1 : 0);
        }

        Advance = advance;
    }

    /// <summary>The sum of advances and spacing before each index.</summary>
    public float[] Prefix { get; }

    /// <summary>The glyph advance of the cluster starting at each index (0 inside clusters).</summary>
    public float[] Advance { get; }

    /// <summary>The letter and word spacing after the cluster starting at each index.</summary>
    public float[] Spacing { get; }

    /// <summary>Whether a line breaking before an index inside a run must shape the pieces again.</summary>
    public bool[] Unsafe { get; }

    /// <summary>The run each UTF-16 index belongs to.</summary>
    public int[] RunOf { get; }

    /// <summary>The number of tabs before each index.</summary>
    public int[] TabsBefore { get; }

    /// <summary>The scripts CSS Text 3 calls cursive, which letter spacing leaves alone.</summary>
    public static bool IsCursive(Script script) =>
        script == Script.Arabic || script == Script.Syriac || script == Script.Mongolian || script == Script.Nko || script == Script.Mandaic
        || script == Script.PhagsPa || script == Script.HanifiRohingya;

    /// <summary>CSS Text 3's word separators.</summary>
    public static bool IsWordSeparator(int cp) => cp is 0x20 or 0xA0 or 0x1361 or 0x10100 or 0x10101 or 0x1039F or 0x1091F;
}

/// <summary>A line decided but not yet placed: its text range and its runs in visual order with relative positions.</summary>
internal sealed class LineDraft
{
    public required int Start { get; init; }

    /// <summary>Where the next line starts.</summary>
    public required int End { get; init; }

    public required bool HardBreak { get; init; }

    public required List<DraftRun> Runs { get; init; }

    /// <summary>The line's width without hanging spaces.</summary>
    public required float ContentWidth { get; init; }

    /// <summary>The width of hanging spaces, which sit past the content's end.</summary>
    public required float HangWidth { get; init; }

    public required float Ascent { get; init; }

    public required float Descent { get; init; }

    public required int ParagraphLevel { get; init; }

    /// <summary>The end of the line's content: hanging spaces and the hard break left out.</summary>
    public required int ContentEnd { get; init; }

    /// <summary>Whether the line ends with an ellipsis.</summary>
    public bool Ellipsized { get; init; }
}

/// <summary>A run of a draft line: glyphs (with spacing in their advances) or a glyphless box, at an x from the line's start.</summary>
internal sealed class DraftRun
{
    public required RunKind Kind { get; init; }

    public required int Start { get; init; }

    public required int Length { get; init; }

    public required int Level { get; init; }

    public required int Style { get; init; }

    public required Font Font { get; init; }

    public required ushort[] Glyphs { get; init; }

    public required int[] Clusters { get; init; }

    public required float[] Advances { get; init; }

    public required Vector2[] Offsets { get; init; }

    public required float Advance { get; init; }

    public required int Placeholder { get; init; }

    public required Script Script { get; init; }

    public required string? Language { get; init; }

    /// <summary>Whether the piece stands upright in a vertical line: its advances and offsets are vertical.</summary>
    public bool Upright { get; init; }

    public float X { get; set; }
}

/// <summary>Fills lines greedily at break opportunities, shapes pieces again where a break is unsafe, reorders each line's bidi
/// runs and places them (design stage C).</summary>
internal sealed class LineBuilder
{
    private const float Epsilon = 1e-4f;

    private readonly Paragraph _paragraph;
    private readonly string _text;
    private readonly AdvanceTable _m;
    private readonly Dictionary<(int, int), GlyphBuffer> _reshaped = [];
    private readonly Dictionary<Font, (GlyphBuffer Ltr, GlyphBuffer Rtl)?> _hyphens = [];
    private Dictionary<Font, (ushort Glyph, float Advance)?>? _tatweels;
    private bool[]? _safeForTatweel;
    private float _tabInterval = -1;

    public LineBuilder(Paragraph paragraph)
    {
        _paragraph = paragraph;
        _text = paragraph.Text;
        _m = paragraph.Measures;
    }

    /// <summary>Decides the line starting at <paramref name="start"/> for a width, greedily.</summary>
    public LineDraft Fill(int start, float width)
    {
        LineBreak[] breaks = _paragraph.Breaks;
        int k = FirstBreakAfter(start);
        int chosen = -1;
        for (int i = k; i < breaks.Length; i++)
        {
            LineBreak b = breaks[i];
            float w = Measure(start, ContentEnd(start, b.Index)) + (b.IsSoftHyphen && !b.IsMandatory ? HyphenWidth(b.Index) : 0);
            if (w > width + Epsilon)
            {
                break;
            }

            chosen = i;
            if (b.IsMandatory)
            {
                break;
            }
        }

        int end;
        bool hyphen = false;
        bool hard = false;
        if (chosen >= 0)
        {
            end = breaks[chosen].Index;
            hyphen = breaks[chosen].IsSoftHyphen && !breaks[chosen].IsMandatory;
            hard = breaks[chosen].IsMandatory && IsHardBreakAt(end - 1);
        }
        else
        {
            LineBreak first = breaks[k];
            end = first.Index;
            hard = first.IsMandatory && IsHardBreakAt(end - 1);
            hyphen = first.IsSoftHyphen && !first.IsMandatory;
            if (_paragraph.Style.LineBreaking.OverflowWrap != OverflowWrap.Normal)
            {
                // overflow-wrap: the piece is too wide, so break it between grapheme clusters, at least one per line.
                int fit = -1;
                int g = TextSegmenter.NextGraphemeBoundary(_text, start);
                while (g < first.Index)
                {
                    if (Measure(start, g) > width + Epsilon)
                    {
                        break;
                    }

                    fit = g;
                    g = TextSegmenter.NextGraphemeBoundary(_text, g);
                }

                if (g < first.Index || fit >= 0)
                {
                    end = fit >= 0 ? fit : TextSegmenter.NextGraphemeBoundary(_text, start);
                    hard = false;
                    hyphen = false;
                }
            }
        }

        return Build(start, end, hard, hyphen);
    }

    /// <summary>
    /// The line ends for <see cref="LineFill.Balanced"/> (CSS <c>text-wrap: balance</c>), or null to fill greedily: between hard
    /// breaks, the breaks that fit and make the sum of the squared space left on every line, the last included, smallest.
    /// Paragraphs that fill more than 6 lines greedily stay greedy.
    /// </summary>
    public List<int>? BalancedEnds(float width)
    {
        int count = CountLines(width);
        if (count <= 1 || count > 6)
        {
            return null;
        }

        LineBreak[] breaks = _paragraph.Breaks;
        List<int> ends = [];
        int segmentStart = 0;
        int k = 0;
        while (segmentStart < _text.Length)
        {
            // The candidates up to the next mandatory break (or the end).
            List<LineBreak> candidates = [];
            for (; k < breaks.Length; k++)
            {
                candidates.Add(breaks[k]);
                if (breaks[k].IsMandatory)
                {
                    k++;
                    break;
                }
            }

            int n = candidates.Count;
            double[] cost = new double[n + 1];
            int[] from = new int[n + 1];
            Array.Fill(cost, double.PositiveInfinity);
            cost[0] = 0;
            for (int j = 1; j <= n; j++)
            {
                LineBreak end = candidates[j - 1];
                for (int i = j - 1; i >= 0; i--)
                {
                    if (double.IsPositiveInfinity(cost[i]))
                    {
                        continue;
                    }

                    int start = i == 0 ? segmentStart : candidates[i - 1].Index;
                    float w = Measure(start, ContentEnd(start, end.Index)) + (end.IsSoftHyphen && !end.IsMandatory ? HyphenWidth(end.Index) : 0);
                    if (w > width + Epsilon)
                    {
                        // Longer lines from earlier starts only get wider; a single overflowing piece still has to go somewhere.
                        if (i < j - 1)
                        {
                            break;
                        }
                    }

                    double slack = Math.Max(0, width - w);
                    double c = cost[i] + (slack * slack);
                    if (c < cost[j])
                    {
                        cost[j] = c;
                        from[j] = i;
                    }
                }
            }

            List<int> segment = [];
            for (int j = n; j > 0; j = from[j])
            {
                segment.Add(candidates[j - 1].Index);
            }

            segment.Reverse();
            ends.AddRange(segment);
            segmentStart = candidates[^1].Index;
        }

        return ends;
    }

    /// <summary>Decides the line from <paramref name="start"/> to a chosen end.</summary>
    public LineDraft FillTo(int start, int end)
    {
        LineBreak b = _paragraph.Breaks[FirstBreakAfter(end - 1)];
        bool hard = b.Index == end && b.IsMandatory && IsHardBreakAt(end - 1);
        bool hyphen = b.Index == end && b.IsSoftHyphen && !b.IsMandatory;
        return Build(start, end, hard, hyphen);
    }

    private int CountLines(float width)
    {
        int count = 0;
        for (int start = 0; start < _text.Length; count++)
        {
            start = Fill(start, width).End;
        }

        return count;
    }

    private int FirstBreakAfter(int start)
    {
        LineBreak[] breaks = _paragraph.Breaks;
        int lo = 0, hi = breaks.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (breaks[mid].Index <= start)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private bool IsHardBreakAt(int i) => i >= 0 && i < _text.Length && IsHardBreakChar(_text[i]);

    private static bool IsHardBreakChar(char c) => c is '\n' or '\r' or '\u000B' or '\u000C' or '\u0085' or '\u2028' or '\u2029';

    /// <summary>Spaces that hang past the line's end (CSS <c>white-space: pre-wrap</c>): spaces, tabs and other space separators
    /// except the no-break ones.</summary>
    private static bool IsHangingSpace(char c) => c is ' ' or '\t' or '\u1680' or (>= '\u2000' and <= '\u2006') or (>= '\u2008' and <= '\u200A') or '\u205F' or '\u3000';

    /// <summary>The end of a line's content: its hard break and hanging spaces left out.</summary>
    private int ContentEnd(int start, int end)
    {
        int j = end;
        while (j > start && IsHardBreakChar(_text[j - 1]))
        {
            j--;
        }

        while (j > start && IsHangingSpace(_text[j - 1]))
        {
            j--;
        }

        return j;
    }

    /// <summary>The width of a line holding <c>[start, end)</c>, tabs and shaping again at unsafe ends included.</summary>
    public float Measure(int start, int end)
    {
        if (end <= start)
        {
            return 0;
        }

        float[] p = _m.Prefix;
        float width;
        if (_m.TabsBefore[end] == _m.TabsBefore[start])
        {
            width = p[end] - p[start];
        }
        else
        {
            float pen = 0;
            int cur = start;
            for (int i = start; i < end; i++)
            {
                if (_text[i] == '\t')
                {
                    pen += p[i] - p[cur];
                    pen += TabAdvance(pen, i, end);
                    cur = i + 1;
                }
            }

            width = pen + p[end] - p[cur];
        }

        // Pieces whose ends fall where shaping must be redone: replace their advances with the reshaped ones.
        int[] runOf = _m.RunOf;
        ParagraphRun first = _paragraph.Runs[runOf[start]];
        ParagraphRun last = _paragraph.Runs[runOf[end - 1]];
        bool unsafeStart = first.Kind == RunKind.Text && start > first.Start && _m.Unsafe[start];
        bool unsafeEnd = last.Kind == RunKind.Text && end < last.End && _m.Unsafe[end];
        if (unsafeStart || unsafeEnd)
        {
            if (first == last)
            {
                width += Correction(start, end);
            }
            else
            {
                if (unsafeStart)
                {
                    width += Correction(start, first.End);
                }

                if (unsafeEnd)
                {
                    width += Correction(last.Start, end);
                }
            }
        }

        return width;
    }

    /// <summary>The reshaped advance of a piece less the paragraph-shaped one.</summary>
    private float Correction(int start, int end)
    {
        float shaped = 0;
        for (int i = start; i < end; i++)
        {
            shaped += _m.Advance[i];
        }

        return Reshape(start, end).Advance - shaped;
    }

    /// <summary>Shapes a piece of a run again on its own, as HarfBuzz recommends at an unsafe break.</summary>
    private GlyphBuffer Reshape(int start, int end)
    {
        if (!_reshaped.TryGetValue((start, end), out GlyphBuffer? buffer))
        {
            ParagraphRun run = _paragraph.Runs[_m.RunOf[start]];
            buffer = new GlyphBuffer();
            TextShaper.ShapeRun(_text, start, end - start, run.Font, run.Properties, buffer);
            _reshaped[(start, end)] = buffer;
        }

        return buffer;
    }

    /// <summary>
    /// The advance of the tab at <paramref name="tab"/> with the pen at <paramref name="pen"/>: to the next tab stop, where the
    /// text up to the next tab (or <paramref name="limit"/>) starts, ends, centres or puts its first decimal separator by the
    /// stop's alignment; never backwards.
    /// </summary>
    private float TabAdvance(float pen, int tab, int limit)
    {
        TabStop stop = NextTabStop(pen);
        if (stop.Alignment == TabAlignment.Start)
        {
            return stop.Position - pen;
        }

        int end = tab + 1;
        while (end < limit && _text[end] != '\t')
        {
            end++;
        }

        float[] p = _m.Prefix;
        float segment = p[end] - p[tab + 1];
        float before = stop.Alignment switch
        {
            TabAlignment.End => segment,
            TabAlignment.Center => segment / 2,
            _ => DecimalOffset(tab + 1, end),
        };
        return Math.Max(0, stop.Position - before - pen);
    }

    /// <summary>The width from a segment's start to its first decimal separator (a full stop, comma or U+066B), or the whole
    /// segment when it has none.</summary>
    private float DecimalOffset(int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            if (_text[i] is '.' or ',' or '\u066B')
            {
                return _m.Prefix[i] - _m.Prefix[start];
            }
        }

        return _m.Prefix[end] - _m.Prefix[start];
    }

    private TabStop NextTabStop(float pen)
    {
        ParagraphStyle style = _paragraph.Style;
        if (style.TabStops is { } stops)
        {
            foreach (TabStop stop in stops.OrderBy(s => s.Position))
            {
                if (stop.Position > pen + Epsilon)
                {
                    return stop;
                }
            }
        }

        float interval = TabInterval();
        if (interval <= 0)
        {
            return new TabStop(pen);
        }

        // CSS Text 3: a tab advances to the next stop at least half a space away.
        float next = MathF.Floor(pen / interval) * interval + interval;
        if (next - pen < SpaceWidth() / 2)
        {
            next += interval;
        }

        return new TabStop(next);
    }

    private float TabInterval()
    {
        if (_tabInterval < 0)
        {
            ResolvedStyle d = _paragraph.Styles[0];
            _tabInterval = _paragraph.Style.TabSize * (SpaceWidth() + d.LetterSpacing + d.WordSpacing);
        }

        return _tabInterval;
    }

    private float SpaceWidth()
    {
        Font? font = _paragraph.Styles[0].Primary;
        if (font is not Font f)
        {
            return 0;
        }

        ushort space = f.Typeface.GetGlyph(' ');
        return f.GetAdvance(space);
    }

    /// <summary>The hyphen a line ending at a soft hyphen before <paramref name="end"/> shows, shaped in that character's run.</summary>
    private float HyphenWidth(int end) => HyphenGlyphs(end) is GlyphBuffer g ? g.Advance : 0;

    private GlyphBuffer? HyphenGlyphs(int end)
    {
        string? hyphen = _paragraph.Style.Hyphen;
        if (string.IsNullOrEmpty(hyphen) || end <= 0)
        {
            return null;
        }

        ParagraphRun run = _paragraph.Runs[_m.RunOf[end - 1]];
        Font font = run.Font;
        if (!_hyphens.TryGetValue(font, out var shaped))
        {
            string text = hyphen;
            if (text == "\u2010" && font.Typeface.GetGlyph(0x2010) == 0)
            {
                text = "-";
            }

            GlyphBuffer ltr = new();
            GlyphBuffer rtl = new();
            TextShaper.ShapeRun(text, 0, text.Length, font, run.Properties with { Direction = TextDirection.LeftToRight }, ltr);
            TextShaper.ShapeRun(text, 0, text.Length, font, run.Properties with { Direction = TextDirection.RightToLeft }, rtl);
            shaped = (ltr, rtl);
            _hyphens[font] = shaped;
        }

        return shaped is { } pair ? ((run.Level & 1) != 0 ? pair.Rtl : pair.Ltr) : null;
    }

    /// <summary>Builds the draft of the line <c>[start, end)</c>: pieces per run and level, glyphs shaped again where a break is
    /// unsafe, the hyphen of a taken soft hyphen, runs reordered (UAX #9 L1 and L2) and placed from x = 0.</summary>
    private LineDraft Build(int start, int end, bool hard, bool hyphen)
    {
        BidiData bidi = _paragraph.Bidi;
        int paragraphLevel = bidi.ParagraphLevelAt(start);
        int length = end - start;
        byte[] levels = new byte[length];
        bidi.Levels.AsSpan(start, length).CopyTo(levels);
        if (length > 0)
        {
            BidiResolver.ApplyLineRule(bidi.Classes.AsSpan(start, length), levels, paragraphLevel);
        }

        int contentEnd = ContentEnd(start, end);
        List<DraftRun> pieces = [];
        float pen = 0;
        int i = start;
        while (i < end)
        {
            ParagraphRun run = _paragraph.Runs[_m.RunOf[i]];
            int pieceEnd = Math.Min(run.End, end);
            byte level = levels[i - start];
            int j = i + 1;
            while (j < pieceEnd && levels[j - start] == level)
            {
                j++;
            }

            pieceEnd = j;
            DraftRun piece = MakePiece(run, i, pieceEnd, level, ref pen, hyphen && pieceEnd == end && run.Kind == RunKind.Text ? HyphenGlyphs(end) : null, end);
            pieces.Add(piece);
            i = pieceEnd;
        }

        // UAX #9 L2: reverse each sequence at or above each odd level, highest first.
        if (pieces.Count > 1)
        {
            int highest = pieces.Max(p => p.Level);
            int lowestOdd = pieces.Min(p => p.Level) | 1;
            for (int level = highest; level >= lowestOdd; level--)
            {
                for (int a = 0; a < pieces.Count; a++)
                {
                    if (pieces[a].Level < level)
                    {
                        continue;
                    }

                    int b = a;
                    while (b < pieces.Count && pieces[b].Level >= level)
                    {
                        b++;
                    }

                    pieces.Reverse(a, b - a);
                    a = b;
                }
            }
        }

        float x = 0;
        foreach (DraftRun piece in pieces)
        {
            piece.X = x;
            x += piece.Advance;
        }

        float content = Measure(start, contentEnd) + (hyphen ? HyphenWidth(end) : 0);
        (float ascent, float descent) = LineMetrics(pieces, start);
        return new LineDraft
        {
            Start = start,
            End = end,
            HardBreak = hard,
            Runs = pieces,
            ContentWidth = content,
            HangWidth = Math.Max(0, x - content),
            Ascent = ascent,
            Descent = descent,
            ParagraphLevel = paragraphLevel,
            ContentEnd = contentEnd,
        };
    }

    private DraftRun MakePiece(ParagraphRun run, int start, int end, int level, ref float pen, GlyphBuffer? hyphen, int lineEnd)
    {
        ResolvedStyle style = _paragraph.Styles[run.Style];
        List<ushort> glyphs = [];
        List<int> clusters = [];
        List<float> advances = [];
        List<Vector2> offsets = [];
        float advance = 0;
        switch (run.Kind)
        {
            case RunKind.Text:
                {
                    bool again = (start > run.Start && _m.Unsafe[start]) || (end < run.End && _m.Unsafe[end]);
                    GlyphBuffer buffer = again ? Reshape(start, end) : run.Glyphs!;
                    bool rtl = (run.Level & 1) != 0;
                    List<int> order = [];
                    for (int g = 0; g < buffer.Count; g++)
                    {
                        int c = buffer.Clusters[g];
                        if (c >= start && c < end)
                        {
                            order.Add(g);
                        }
                    }

                    // Spacing goes on the logically last glyph of each cluster: the visually last in left-to-right runs, the visually
                    // first in right-to-left ones.
                    HashSet<int> spaced = [];
                    float[] extraOf = new float[order.Count];
                    for (int o = 0; o < order.Count; o++)
                    {
                        int idx = rtl ? o : order.Count - 1 - o;
                        int c = buffer.Clusters[order[idx]];
                        if (spaced.Add(c))
                        {
                            extraOf[idx] = _m.Spacing[c];
                        }
                    }

                    if (hyphen is not null && rtl)
                    {
                        AddGlyphs(hyphen, lineEnd - 1, glyphs, clusters, advances, offsets, ref advance);
                    }

                    for (int o = 0; o < order.Count; o++)
                    {
                        int g = order[o];
                        GlyphPosition p = buffer.Positions[g];
                        float along = run.Upright ? -p.YAdvance : p.XAdvance;
                        glyphs.Add(buffer.GlyphIds[g]);
                        clusters.Add(buffer.Clusters[g]);
                        advances.Add(along + extraOf[o]);
                        offsets.Add(new Vector2(p.XOffset, p.YOffset));
                        advance += along + extraOf[o];
                    }

                    if (hyphen is not null && !rtl)
                    {
                        AddGlyphs(hyphen, lineEnd - 1, glyphs, clusters, advances, offsets, ref advance);
                    }

                    break;
                }

            case RunKind.Placeholder:
                advance = _paragraph.RichText.Placeholders[run.Placeholder].Width + _m.Spacing[start];
                break;
            case RunKind.Tab:
                advance = TabAdvance(pen, start, lineEnd);
                break;
        }

        pen += advance;
        return new DraftRun
        {
            Kind = run.Kind,
            Start = start,
            Length = end - start,
            Level = level,
            Style = _paragraph.RichText.SpanAt(start) + 1,
            Font = run.Font,
            Glyphs = [.. glyphs],
            Clusters = [.. clusters],
            Advances = [.. advances],
            Offsets = [.. offsets],
            Advance = advance,
            Placeholder = run.Placeholder,
            Script = run.Script,
            Language = style.Language,
            Upright = run.Upright,
        };
    }

    private static void AddGlyphs(GlyphBuffer buffer, int cluster, List<ushort> glyphs, List<int> clusters, List<float> advances, List<Vector2> offsets, ref float advance)
    {
        for (int g = 0; g < buffer.Count; g++)
        {
            GlyphPosition p = buffer.Positions[g];
            glyphs.Add(buffer.GlyphIds[g]);
            clusters.Add(cluster);
            advances.Add(p.XAdvance);
            offsets.Add(new Vector2(p.XOffset, p.YOffset));
            advance += p.XAdvance;
        }
    }

    /// <summary>
    /// The line box above and below the baseline (CSS Inline Layout 3): the root inline box (the paragraph's default style, the
    /// strut CSS gives every line), the <see cref="ParagraphStyle.Strut"/>, and each run's inline box, moved by its baseline
    /// shift; placeholders by their alignment, those aligned to the line box's top or bottom stretching it last. With
    /// <see cref="Strut.Force"/> the strut alone sets the line box.
    /// </summary>
    private (float Ascent, float Descent) LineMetrics(List<DraftRun> pieces, int start)
    {
        float top, bottom;
        (top, bottom) = RootBox();
        if (StrutBox() is (float StrutTop, float StrutBottom) strut)
        {
            if (_paragraph.Style.Strut!.Force)
            {
                return (-strut.StrutTop, strut.StrutBottom);
            }

            top = Math.Min(top, strut.StrutTop);
            bottom = Math.Max(bottom, strut.StrutBottom);
        }

        List<Placeholder> stretching = [];
        foreach (DraftRun piece in pieces)
        {
            ResolvedStyle style = _paragraph.Styles[piece.Style];
            float t, b;
            if (piece.Kind == RunKind.Placeholder)
            {
                Placeholder box = _paragraph.RichText.Placeholders[piece.Placeholder];
                if (box.Alignment is PlaceholderAlignment.Top or PlaceholderAlignment.Bottom)
                {
                    stretching.Add(box);
                    continue;
                }

                (t, b) = PlaceholderBox(box);
            }
            else
            {
                (t, b) = Box(piece.Font, style.LineHeight);
            }

            t += style.BaselineShift;
            b += style.BaselineShift;
            top = Math.Min(top, t);
            bottom = Math.Max(bottom, b);
        }

        float ascent = -top, descent = bottom;
        foreach (Placeholder box in stretching)
        {
            if (box.Height > ascent + descent)
            {
                if (box.Alignment == PlaceholderAlignment.Top)
                {
                    descent = box.Height - ascent;
                }
                else
                {
                    ascent = box.Height - descent;
                }
            }
        }

        return (ascent, descent);
    }

    /// <summary>The root inline box: the default style's primary font and line height.</summary>
    private (float Top, float Bottom) RootBox()
    {
        ResolvedStyle root = _paragraph.Styles[0];
        return root.Primary is Font f ? Box(f, root.LineHeight) : (0, 0);
    }

    private (float StrutTop, float StrutBottom)? StrutBox()
    {
        if (_paragraph.Style.Strut is not Strut strut)
        {
            return null;
        }

        ResolvedStyle root = _paragraph.Styles[0];
        IReadOnlyList<FontFamily> families = strut.FontFamilies ?? root.Families;
        if (families.Count == 0)
        {
            return null;
        }

        FontMatch match = families[0].Match(strut.FontStyle ?? root.FontStyle);
        Font font = new(match.Typeface, strut.Size ?? root.Size) { Synthesis = match.Synthesis };
        return Box(font, strut.LineHeight);
    }

    /// <summary>Whether lines run vertically.</summary>
    private bool Vertical => _paragraph.Style.WritingMode != WritingMode.HorizontalTb;

    /// <summary>Whether vertical lines align on the central baseline (CSS's for mixed and upright text) rather than the alphabetic.</summary>
    private bool Central => Vertical && _paragraph.Style.TextOrientation != TextOrientation.Sideways;

    /// <summary>An inline box on the line's baseline: <see cref="InlineBox"/>, or about the central baseline in vertical lines.</summary>
    private (float Top, float Bottom) Box(Font font, LineHeight lineHeight) => Central ? CentralBox(font, lineHeight) : InlineBox(font, lineHeight);

    /// <summary>An inline box about the central baseline: the font's rounded ascent and descent split evenly on each side (the
    /// over half rounded down), then the leading as <see cref="InlineBox"/> splits it.</summary>
    internal static (float Top, float Bottom) CentralBox(Font font, LineHeight lineHeight)
    {
        (float a, float d, float gap) = Rounded(font);
        float over = MathF.Floor((a + d) / 2), under = a + d - over;
        float height = lineHeight.Kind switch
        {
            LineHeightKind.Multiple => LayoutUnit(lineHeight.Value * font.Size),
            LineHeightKind.Absolute => LayoutUnit(lineHeight.Value),
            _ => a + d + gap,
        };
        float leading = height - (a + d);
        float half = MathF.Floor(leading / 2);
        return (-(over + half), under + (leading - half));
    }

    /// <summary>The top of a placeholder's box on a line with the given top, baseline (moved by the style's shift) and bottom.</summary>
    private float PlaceholderTop(Placeholder box, float top, float baseline, float bottom) => box.Alignment switch
    {
        PlaceholderAlignment.Top => top,
        PlaceholderAlignment.Bottom => bottom - box.Height,
        _ => baseline + PlaceholderBox(box).Top,
    };

    /// <summary>The widths CSS calls min-content and max-content: the widest piece between break opportunities (with its
    /// hyphen; grapheme clusters with <see cref="OverflowWrap.Anywhere"/>), and the widest line between hard breaks.</summary>
    public (float Min, float Max) IntrinsicWidths()
    {
        float min = 0, max = 0;
        int start = 0;
        int lineStart = 0;
        bool anywhere = _paragraph.Style.LineBreaking.OverflowWrap == OverflowWrap.Anywhere;
        foreach (LineBreak b in _paragraph.Breaks)
        {
            if (anywhere)
            {
                for (int g = start; g < b.Index; g = TextSegmenter.NextGraphemeBoundary(_text, g))
                {
                    int next = TextSegmenter.NextGraphemeBoundary(_text, g);
                    min = Math.Max(min, Measure(g, ContentEnd(g, next)));
                }
            }
            else
            {
                min = Math.Max(min, Measure(start, ContentEnd(start, b.Index)) + (b.IsSoftHyphen && !b.IsMandatory ? HyphenWidth(b.Index) : 0));
            }

            start = b.Index;
            if (b.IsMandatory)
            {
                max = Math.Max(max, Measure(lineStart, ContentEnd(lineStart, b.Index)));
                lineStart = b.Index;
            }
        }

        if (lineStart < _text.Length)
        {
            max = Math.Max(max, Measure(lineStart, ContentEnd(lineStart, _text.Length)));
        }

        // Negative letter spacing can make a whole line narrower than its widest piece; max-content is never below min-content,
        // as Blink clamps it.
        return (min, Math.Max(min, max));
    }

    /// <summary>A placeholder's box relative to the baseline, for the alignments that hang off the baseline.</summary>
    private (float Top, float Bottom) PlaceholderBox(Placeholder box) => box.Alignment switch
    {
        PlaceholderAlignment.Baseline => (-box.BaselineOffset, box.Height - box.BaselineOffset),
        PlaceholderAlignment.AboveBaseline => (-box.Height, 0),
        PlaceholderAlignment.BelowBaseline => (0, box.Height),

        // CSS vertical-align: middle: the box's middle half the root font's x-height above the baseline.
        _ => (-(RootXHeight() / 2) - (box.Height / 2), -(RootXHeight() / 2) + (box.Height / 2)),
    };

    private float RootXHeight() => _paragraph.Styles[0].Primary is Font f ? f.Extents.XHeight : 0;

    /// <summary>
    /// A text inline box relative to the baseline: the font's ascent and descent rounded to whole units, as browsers round them,
    /// then the half-leading from the line height, the top half rounded down; a computed line height is floored to 1/64 unit,
    /// browsers' layout unit, so stacked lines do not drift. <see cref="LineHeight.Normal"/> adds the font's rounded line gap.
    /// </summary>
    internal static (float Top, float Bottom) InlineBox(Font font, LineHeight lineHeight)
    {
        (float a, float d, float gap) = Rounded(font);
        float height = lineHeight.Kind switch
        {
            LineHeightKind.Multiple => LayoutUnit(lineHeight.Value * font.Size),
            LineHeightKind.Absolute => LayoutUnit(lineHeight.Value),
            _ => a + d + gap,
        };
        float leading = height - (a + d);
        float half = MathF.Floor(leading / 2);
        return (-(a + half), d + (leading - half));
    }

    /// <summary>A font's ascent, descent and line gap rounded to whole units, as browsers keep them.</summary>
    internal static (float Ascent, float Descent, float LineGap) Rounded(Font font)
    {
        FontExtents e = font.Extents;
        return (MathF.Floor(e.Ascent + 0.5f), MathF.Floor(-e.Descent + 0.5f), MathF.Floor(e.LineGap + 0.5f));
    }

    /// <summary>A length floored to 1/64, browsers' layout unit.</summary>
    internal static float LayoutUnit(float value) => MathF.Floor(value * 64) / 64;

    /// <summary>Places a draft: its content starts at the start edge (left, or right in right-to-left paragraphs), its top at
    /// <paramref name="top"/>.</summary>
    public TextLine Finish(LineDraft draft, float width, float top, bool trimTop = false, bool trimBottom = false, float blockSize = float.NaN)
    {
        // CSS text-box-trim: the first line's box starts at the root font's ascent, the last one's ends at its descent.
        float lineAscent = draft.Ascent, lineDescent = draft.Descent;
        if ((trimTop || trimBottom) && _paragraph.Styles[0].Primary is Font root)
        {
            (float a, float d, _) = Rounded(root);
            lineAscent = trimTop ? a : lineAscent;
            lineDescent = trimBottom ? d : lineDescent;
        }

        bool rtl = (draft.ParagraphLevel & 1) != 0;
        float boxWidth = float.IsFinite(width) ? width : draft.ContentWidth;

        // Alignment: the last line and lines before a hard break use AlignLast; justified lines stretch from the start edge.
        TextAlignLast alignment = Alignment(draft.HardBreak || draft.End >= _text.Length);
        Dictionary<(DraftRun, int), float>? expansion = null;
        Dictionary<DraftRun, List<Tatweels>>? tatweels = null;
        float contentWidth = draft.ContentWidth;
        float free = boxWidth - draft.ContentWidth;
        if (alignment == TextAlignLast.Justify && free > 0 && float.IsFinite(width))
        {
            expansion = Justify(draft, free, out tatweels);
            if (expansion is not null)
            {
                contentWidth = boxWidth;
            }
        }

        bool leftEdge = alignment switch
        {
            TextAlignLast.Left => true,
            TextAlignLast.Right => false,
            TextAlignLast.End => rtl,
            _ => !rtl,
        };
        float left = alignment == TextAlignLast.Center ? (boxWidth - contentWidth) / 2 : leftEdge ? 0 : boxWidth - contentWidth;

        // Hanging spaces sit past the content's end: right of it in left-to-right paragraphs, left of it in right-to-left ones.
        float origin = rtl ? left - draft.HangWidth : left;
        float baseline = top + lineAscent;
        List<LayoutRun> runs = new(draft.Runs.Count);
        float stretch = 0;
        foreach (DraftRun piece in draft.Runs)
        {
            float x = origin + piece.X + stretch;
            float[] advances = piece.Advances;
            ushort[] ids = piece.Glyphs;
            int[] clusters = piece.Clusters;
            Vector2[] offsets = piece.Offsets;
            float pieceAdvance = piece.Advance;
            if (expansion is not null)
            {
                advances = (float[])piece.Advances.Clone();
                for (int g = 0; g < advances.Length; g++)
                {
                    if (expansion.TryGetValue((piece, g), out float extra))
                    {
                        advances[g] += extra;
                        pieceAdvance += extra;
                        stretch += extra;
                    }
                }

                if (piece.Kind == RunKind.Placeholder && expansion.TryGetValue((piece, -1), out float boxExtra))
                {
                    pieceAdvance += boxExtra;
                    stretch += boxExtra;
                }
            }

            if (tatweels is not null && piece.Kind == RunKind.Text)
            {
                float added = InsertTatweels(piece, tatweels, ref ids, ref clusters, ref advances, ref offsets);
                pieceAdvance += added;
                stretch += added;
            }

            float shift = _paragraph.Styles[piece.Style].BaselineShift;
            GlyphRun? glyphs = null;
            if (piece.Kind == RunKind.Text && !Vertical)
            {
                Vector2[] points = new Vector2[ids.Length];
                float pen = x;
                for (int g = 0; g < points.Length; g++)
                {
                    points[g] = new Vector2(pen + offsets[g].X, baseline + shift - offsets[g].Y);
                    pen += advances[g];
                }

                glyphs = new GlyphRun(piece.Font, ids, points, _text, clusters, null, advances)
                {
                    BidiLevel = piece.Level,
                    Script = piece.Script,
                    Language = piece.Language,
                };
            }
            else if (piece.Kind == RunKind.Text)
            {
                glyphs = VerticalGlyphs(piece, advances, x, ToPhysical(baseline + shift, top, lineAscent + lineDescent, blockSize));
            }

            LayoutRunKind kind = piece.Kind switch
            {
                RunKind.Text => LayoutRunKind.Text,
                RunKind.Placeholder => LayoutRunKind.Placeholder,
                RunKind.Tab => LayoutRunKind.Tab,
                _ => LayoutRunKind.Control,
            };
            runs.Add(new LayoutRun(kind, piece.Start, piece.Length, piece.Level, piece.Style, x, pieceAdvance, glyphs, piece.Kind == RunKind.Placeholder ? piece.Placeholder : -1)
            {
                BoxTop = piece.Kind == RunKind.Placeholder ? PlaceholderTop(_paragraph.RichText.Placeholders[piece.Placeholder], top, baseline + shift, top + lineAscent + lineDescent) : 0,
            });
        }

        if (Vertical)
        {
            // In vertical lines the block axis is x: the line's over side is its right edge, its under side its left.
            float over = ToPhysical(top, top, lineAscent + lineDescent, blockSize);
            return new TextLine(draft.Start, draft.End - draft.Start, over - lineAscent - lineDescent, lineDescent, lineAscent, left, contentWidth, draft.HardBreak, draft.Ellipsized, runs);
        }

        return new TextLine(draft.Start, draft.End - draft.Start, top, lineAscent, lineDescent, left, contentWidth, draft.HardBreak, draft.Ellipsized, runs);
    }

    /// <summary>
    /// A block-axis position of a vertical line as an x: lines stack leftwards from the right edge in
    /// <see cref="WritingMode.VerticalRl"/> and rightwards from the left in <see cref="WritingMode.VerticalLr"/>; inside a line,
    /// the over side (the logical top) is on the right in both.
    /// </summary>
    private float ToPhysical(float position, float lineTop, float thickness, float blockSize)
    {
        float offset = position - lineTop;
        float lineRight = _paragraph.Style.WritingMode == WritingMode.VerticalRl
            ? (float.IsNaN(blockSize) ? thickness : blockSize) - lineTop
            : lineTop + thickness;
        return lineRight - offset;
    }

    /// <summary>
    /// The glyphs of a vertical piece: upright ones on the central baseline with their vertical offsets, sideways ones turned a
    /// quarter clockwise with their alphabetic baseline where the em box centres on the central baseline (or on the line's own
    /// baseline with <see cref="TextOrientation.Sideways"/>).
    /// </summary>
    private GlyphRun VerticalGlyphs(DraftRun piece, float[] advances, float start, float baselineX)
    {
        int n = piece.Glyphs.Length;
        float pen = start;
        if (piece.Upright)
        {
            Vector2[] points = new Vector2[n];
            for (int g = 0; g < n; g++)
            {
                points[g] = new Vector2(baselineX + piece.Offsets[g].X, pen - piece.Offsets[g].Y);
                pen += advances[g];
            }

            return new GlyphRun(piece.Font, piece.Glyphs, points, _text, piece.Clusters, null, advances)
            {
                BidiLevel = piece.Level,
                Script = piece.Script,
                Language = piece.Language,
            };
        }

        (float a, float d, _) = Rounded(piece.Font);
        float alphabetic = Central ? baselineX - ((a - d) / 2) : baselineX;
        GlyphTransform[] transforms = new GlyphTransform[n];
        Vector2[] origins = new Vector2[n];
        for (int g = 0; g < n; g++)
        {
            origins[g] = new Vector2(alphabetic + piece.Offsets[g].Y, pen + piece.Offsets[g].X);
            transforms[g] = GlyphTransform.FromRotationScale(1, 90, origins[g]);
            pen += advances[g];
        }

        return new GlyphRun(piece.Font, piece.Glyphs, origins, _text, piece.Clusters, transforms, advances)
        {
            BidiLevel = piece.Level,
            Script = piece.Script,
            Language = piece.Language,
        };
    }

    /// <summary>
    /// The last kept line made room for the ellipsis: whole grapheme clusters dropped from its logical end until the text and the
    /// ellipsis (shaped in the style of the last kept character) fit the width, and the ellipsis placed at the line's end in the
    /// paragraph's direction. As in Chromium's line clamp, a line that has room keeps its trailing spaces before the ellipsis.
    /// <paramref name="hidden"/> is where the hidden text starts.
    /// </summary>
    public LineDraft Ellipsize(LineDraft line, float width, out int hidden)
    {
        int cut = line.End;
        while (cut > line.Start && IsHardBreakAt(cut - 1))
        {
            cut--;
        }

        int styleIndex = _paragraph.RichText.SpanAt(Math.Max(line.Start, cut - 1)) + 1;
        (Font font, GlyphBuffer glyphs) = _paragraph.Ellipses![styleIndex];
        float room = glyphs.Advance;
        while (cut > line.Start && float.IsFinite(width) && Measure(line.Start, cut) + room > width + Epsilon)
        {
            cut = PreviousGrapheme(cut, line.Start);
        }

        int content = cut;
        hidden = content;
        LineDraft kept = Build(line.Start, content, false, false);
        bool rtl = (kept.ParagraphLevel & 1) != 0;
        List<ushort> ids = [];
        List<int> clusters = [];
        List<float> advances = [];
        List<Vector2> offsets = [];
        float advance = 0;
        AddGlyphs(glyphs, content, ids, clusters, advances, offsets, ref advance);
        DraftRun ellipsis = new()
        {
            Kind = RunKind.Text,
            Start = content,
            Length = 0,
            Level = kept.ParagraphLevel,
            Style = styleIndex,
            Font = font,
            Glyphs = [.. ids],
            Clusters = [.. clusters],
            Advances = [.. advances],
            Offsets = [.. offsets],
            Advance = advance,
            Placeholder = -1,
            Script = Script.Common,
            Language = _paragraph.Styles[styleIndex].Language,
        };
        List<DraftRun> runs = [.. kept.Runs];
        runs.Insert(rtl ? 0 : runs.Count, ellipsis);
        float x = 0;
        foreach (DraftRun run in runs)
        {
            run.X = x;
            x += run.Advance;
        }

        (float ascent, float descent) = LineMetrics(runs, line.Start);
        return new LineDraft
        {
            Start = line.Start,
            End = line.End,
            HardBreak = false,
            Runs = runs,
            ContentWidth = Measure(line.Start, content) + advance,
            HangWidth = 0,
            Ascent = Math.Max(ascent, line.Ascent),
            Descent = Math.Max(descent, line.Descent),
            ParagraphLevel = kept.ParagraphLevel,
            ContentEnd = content,
            Ellipsized = true,
        };
    }

    private int PreviousGrapheme(int index, int floor)
    {
        int previous = floor;
        for (int g = floor; g < index; g = TextSegmenter.NextGraphemeBoundary(_text, g))
        {
            previous = g;
        }

        return previous;
    }

    /// <summary>A line's alignment: <see cref="ParagraphStyle.Align"/>, or for the last line and lines before a hard break
    /// <see cref="ParagraphStyle.AlignLast"/>, whose Auto is the start for justified paragraphs.</summary>
    private TextAlignLast Alignment(bool last)
    {
        ParagraphStyle style = _paragraph.Style;
        TextAlignLast align = style.Align switch
        {
            TextAlign.End => TextAlignLast.End,
            TextAlign.Left => TextAlignLast.Left,
            TextAlign.Right => TextAlignLast.Right,
            TextAlign.Center => TextAlignLast.Center,
            TextAlign.Justify => TextAlignLast.Justify,
            _ => TextAlignLast.Start,
        };
        if (!last)
        {
            return align;
        }

        return style.AlignLast == TextAlignLast.Auto ? (align == TextAlignLast.Justify ? TextAlignLast.Start : align) : style.AlignLast;
    }

    /// <summary>
    /// The extra advance each justification opportunity gets, keyed by piece and glyph (-1 for a placeholder's box), or null
    /// when the line has none. Opportunities sit between grapheme clusters of the content: after word separators
    /// (<see cref="TextJustify.InterWord"/> and <see cref="TextJustify.Kashida"/>), between any two clusters outside cursive scripts
    /// (<see cref="TextJustify.InterCharacter"/>), or, for <see cref="TextJustify.Auto"/>, after word separators and on both sides
    /// of Chinese, Japanese and Korean characters, as browsers justify; never after the content's last cluster. With
    /// <see cref="TextJustify.Kashida"/>, <paramref name="tatweels"/> first takes what whole tatweels can (see <see cref="Kashida"/>).
    /// </summary>
    private Dictionary<(DraftRun, int), float>? Justify(LineDraft draft, float free, out Dictionary<DraftRun, List<Tatweels>>? tatweels)
    {
        tatweels = null;
        TextJustify mode = _paragraph.Style.Justify;
        if (mode == TextJustify.None)
        {
            return null;
        }

        // Each content cluster's logically last glyph (or a placeholder's box), in logical order.
        List<(int Cluster, DraftRun Piece, int Glyph)> clusters = [];
        foreach (DraftRun piece in draft.Runs)
        {
            if (piece.Kind == RunKind.Placeholder && piece.Start < draft.ContentEnd)
            {
                clusters.Add((piece.Start, piece, -1));
                continue;
            }

            bool rtl = (piece.Level & 1) != 0;
            for (int g = 0; g < piece.Glyphs.Length; g++)
            {
                int c = piece.Clusters[g];
                if (c >= draft.ContentEnd)
                {
                    continue;
                }

                // The logically last glyph of a cluster: the visually last in left-to-right pieces, the first in right-to-left.
                bool lastOfCluster = rtl ? g == 0 || piece.Clusters[g - 1] != c : g == piece.Glyphs.Length - 1 || piece.Clusters[g + 1] != c;
                if (lastOfCluster)
                {
                    clusters.Add((c, piece, g));
                }
            }
        }

        clusters.Sort((a, b) => a.Cluster.CompareTo(b.Cluster));
        bool kashida = mode == TextJustify.Kashida && !Vertical;
        List<(DraftRun Piece, int Glyph)> opportunities = [];
        List<KashidaPoint>? points = kashida ? [] : null;
        KashidaPoint? best = null;
        for (int i = 0; i + 1 < clusters.Count; i++)
        {
            int cp = TextSegmenter.CodePointAt(_text, clusters[i].Cluster, out _);
            int next = TextSegmenter.CodePointAt(_text, clusters[i + 1].Cluster, out _);
            bool separator = AdvanceTable.IsWordSeparator(cp);
            bool opportunity = mode switch
            {
                TextJustify.InterWord or TextJustify.Kashida => separator,
                TextJustify.InterCharacter => separator || (!AdvanceTable.IsCursive(clusters[i].Piece.Script) && !AdvanceTable.IsCursive(clusters[i + 1].Piece.Script)),
                _ => separator || IsCjk(cp) || IsCjk(next),
            };
            if (opportunity)
            {
                opportunities.Add((clusters[i].Piece, clusters[i].Glyph));
            }

            if (kashida)
            {
                // One point per word: the best safe one, the later of equals.
                if (KashidaPointAt(clusters[i], clusters[i + 1]) is KashidaPoint point && (best is null || point.Priority <= best.Value.Priority))
                {
                    best = point;
                }

                if (separator && best is not null)
                {
                    points!.Add(best.Value);
                    best = null;
                }
            }
        }

        if (best is not null)
        {
            points!.Add(best.Value);
        }

        float remaining = free;
        if (points is { Count: > 0 })
        {
            tatweels = PlaceTatweels(points, Kashida.Cap(_paragraph.Style.Kashida), opportunities.Count > 0, ref remaining);
        }

        if (opportunities.Count == 0)
        {
            return tatweels is null ? null : [];
        }

        float each = Math.Max(0, remaining) / opportunities.Count;
        Dictionary<(DraftRun, int), float> result = [];
        foreach ((DraftRun piece, int glyph) in opportunities)
        {
            result[(piece, glyph)] = result.GetValueOrDefault((piece, glyph)) + each;
        }

        return result;
    }

    /// <summary>
    /// Spreads a line's free space over its words' kashida points: whole tatweels, a round at a time over the words, best points
    /// first, up to <paramref name="cap"/> each. What is left stays in <paramref name="remaining"/> for the separators; with none,
    /// one more tatweel, overlapping the letter after it, takes it in the word with fewest. (Kept out of
    /// <see cref="Justify"/> so that its lambdas allocate nothing on lines without kashida.)
    /// </summary>
    private static Dictionary<DraftRun, List<Tatweels>>? PlaceTatweels(List<KashidaPoint> points, int cap, bool separators, ref float remaining)
    {
        int[] counts = new int[points.Count];
        float[] last = new float[points.Count];
        int[] order = [.. Enumerable.Range(0, points.Count).OrderBy(k => points[k].Priority)];
        for (int round = 0; round < cap; round++)
        {
            bool took = false;
            foreach (int k in order)
            {
                if (points[k].Width <= remaining + Epsilon)
                {
                    counts[k]++;
                    remaining -= points[k].Width;
                    took = true;
                }
            }

            if (!took)
            {
                break;
            }
        }

        for (int k = 0; k < points.Count; k++)
        {
            last[k] = points[k].Width;
        }

        if (!separators && remaining > Epsilon)
        {
            int k = order.MinBy(k => counts[k]);
            counts[k]++;
            last[k] = remaining;
            remaining = 0;
        }

        Dictionary<DraftRun, List<Tatweels>>? tatweels = null;
        for (int k = 0; k < points.Count; k++)
        {
            if (counts[k] > 0)
            {
                KashidaPoint point = points[k];
                tatweels ??= [];
                if (!tatweels.TryGetValue(point.Piece, out List<Tatweels>? list))
                {
                    list = [];
                    tatweels[point.Piece] = list;
                }

                list.Add(new Tatweels(InsertionIndex(point.Piece, point.AfterGlyph), point.Glyph, point.Cluster, counts[k], point.Width, last[k]));
            }
        }

        return tatweels;
    }

    /// <summary>
    /// The kashida point before the cluster <paramref name="after"/>, or null: both clusters in one text piece of a script that joins
    /// with U+0640, the shaping marking the cluster safe for a tatweel, and the piece's font having one. Tatweels go just before the
    /// cluster's glyphs in logical order, so after them visually in right-to-left pieces, and join the cluster before.
    /// </summary>
    private KashidaPoint? KashidaPointAt((int Cluster, DraftRun Piece, int Glyph) before, (int Cluster, DraftRun Piece, int Glyph) after)
    {
        DraftRun piece = after.Piece;
        if (before.Piece != piece || piece.Kind != RunKind.Text || piece.Upright || !Kashida.Joins(piece.Script) || after.Glyph < 0
            || !SafeForTatweel(after.Cluster) || TatweelOf(piece.Font) is not (ushort glyph, float width))
        {
            return null;
        }

        return new KashidaPoint(piece, after.Glyph, before.Cluster, Kashida.Priority(_text, before.Cluster, after.Cluster), glyph, width);
    }

    /// <summary>Where in a piece's visual order tatweels go before the cluster of <paramref name="glyph"/> (its logically last
    /// glyph): after the cluster's glyphs in right-to-left pieces, before them in left-to-right ones.</summary>
    private static int InsertionIndex(DraftRun piece, int glyph)
    {
        int cluster = piece.Clusters[glyph];
        int g = glyph;
        if ((piece.Level & 1) != 0)
        {
            while (g < piece.Clusters.Length && piece.Clusters[g] == cluster)
            {
                g++;
            }

            return g;
        }

        while (g > 0 && piece.Clusters[g - 1] == cluster)
        {
            g--;
        }

        return g;
    }

    /// <summary>Whether the paragraph's shaping marked the cluster starting at an index safe for a tatweel before it.</summary>
    private bool SafeForTatweel(int cluster)
    {
        if (_safeForTatweel is null)
        {
            bool[] safe = new bool[_text.Length];
            foreach (ParagraphRun run in _paragraph.Runs)
            {
                if (run.Glyphs is GlyphBuffer glyphs && run.Properties.ProduceSafeToInsertTatweel)
                {
                    for (int g = 0; g < glyphs.Count; g++)
                    {
                        if ((glyphs.Flags[g] & GlyphFlags.SafeToInsertTatweel) != 0)
                        {
                            safe[glyphs.Clusters[g]] = true;
                        }
                    }
                }
            }

            _safeForTatweel = safe;
        }

        return _safeForTatweel[cluster];
    }

    /// <summary>A font's tatweel glyph and advance, or null when it has none.</summary>
    private (ushort Glyph, float Advance)? TatweelOf(Font font)
    {
        _tatweels ??= [];
        if (!_tatweels.TryGetValue(font, out var tatweel))
        {
            ushort glyph = font.Typeface.GetGlyph(Kashida.Tatweel);
            float advance = glyph == 0 ? 0 : font.GetAdvance(glyph);
            tatweel = advance > 0 ? (glyph, advance) : null;
            _tatweels[font] = tatweel;
        }

        return tatweel;
    }

    /// <summary>Puts a piece's tatweels into its glyph arrays and returns their total advance.</summary>
    private static float InsertTatweels(DraftRun piece, Dictionary<DraftRun, List<Tatweels>> tatweels, ref ushort[] ids, ref int[] clusters, ref float[] advances, ref Vector2[] offsets)
    {
        if (!tatweels.TryGetValue(piece, out List<Tatweels>? list))
        {
            return 0;
        }

        list.Sort((a, b) => a.Index.CompareTo(b.Index));
        int n = ids.Length + list.Sum(t => t.Count);
        ushort[] newIds = new ushort[n];
        int[] newClusters = new int[n];
        float[] newAdvances = new float[n];
        Vector2[] newOffsets = new Vector2[n];
        float added = 0;
        int o = 0;
        int next = 0;
        for (int g = 0; g <= ids.Length; g++)
        {
            while (next < list.Count && list[next].Index == g)
            {
                Tatweels t = list[next++];
                for (int k = 0; k < t.Count; k++)
                {
                    float advance = k == t.Count - 1 ? t.Last : t.Advance;
                    newIds[o] = t.Glyph;
                    newClusters[o] = t.Cluster;
                    newAdvances[o] = advance;
                    added += advance;
                    o++;
                }
            }

            if (g < ids.Length)
            {
                newIds[o] = ids[g];
                newClusters[o] = clusters[g];
                newAdvances[o] = advances[g];
                newOffsets[o] = offsets[g];
                o++;
            }
        }

        ids = newIds;
        clusters = newClusters;
        advances = newAdvances;
        offsets = newOffsets;
        return added;
    }

    /// <summary>A word's kashida point: tatweels go before the cluster of the piece's glyph <see cref="AfterGlyph"/> and join
    /// <see cref="Cluster"/>.</summary>
    private readonly record struct KashidaPoint(DraftRun Piece, int AfterGlyph, int Cluster, int Priority, ushort Glyph, float Width);

    /// <summary>Tatweels put into a piece before its glyph <see cref="Index"/> (in visual order): <see cref="Count"/> glyphs, each
    /// <see cref="Advance"/> wide but the last, which is <see cref="Last"/> wide (less where it overlaps the letter after it).</summary>
    private readonly record struct Tatweels(int Index, ushort Glyph, int Cluster, int Count, float Advance, float Last);

    /// <summary>Chinese, Japanese and Korean characters, which justify between each other: ideographs, kana, Hangul, Bopomofo
    /// and their fullwidth punctuation and symbols.</summary>
    private static bool IsCjk(int cp) =>
        cp is (>= 0x1100 and <= 0x11FF) or (>= 0x2E80 and <= 0x2FDF) or (>= 0x3000 and <= 0x9FFF) or (>= 0xA960 and <= 0xA97F)
            or (>= 0xAC00 and <= 0xD7FF) or (>= 0xF900 and <= 0xFAFF) or (>= 0xFE30 and <= 0xFE4F) or (>= 0xFF00 and <= 0xFFEF)
            or (>= 0x20000 and <= 0x3FFFF);
}
