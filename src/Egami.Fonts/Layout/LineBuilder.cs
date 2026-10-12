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
                    advance[cluster] += glyphs.Positions[g].XAdvance;
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
                        glyphs.Add(buffer.GlyphIds[g]);
                        clusters.Add(buffer.Clusters[g]);
                        advances.Add(p.XAdvance + extraOf[o]);
                        offsets.Add(new Vector2(p.XOffset, p.YOffset));
                        advance += p.XAdvance + extraOf[o];
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
                (t, b) = InlineBox(piece.Font, style.LineHeight);
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
        return root.Primary is Font f ? InlineBox(f, root.LineHeight) : (0, 0);
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
        return InlineBox(font, strut.LineHeight);
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
    public TextLine Finish(LineDraft draft, float width, float top, bool trimTop = false, bool trimBottom = false)
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
        float left = rtl ? boxWidth - draft.ContentWidth : 0;

        // Hanging spaces sit past the content's end: right of it in left-to-right paragraphs, left of it in right-to-left ones.
        float origin = rtl ? left - draft.HangWidth : left;
        float baseline = top + lineAscent;
        List<LayoutRun> runs = new(draft.Runs.Count);
        foreach (DraftRun piece in draft.Runs)
        {
            float x = origin + piece.X;
            float shift = _paragraph.Styles[piece.Style].BaselineShift;
            GlyphRun? glyphs = null;
            if (piece.Kind == RunKind.Text)
            {
                Vector2[] points = new Vector2[piece.Glyphs.Length];
                float pen = x;
                for (int g = 0; g < points.Length; g++)
                {
                    points[g] = new Vector2(pen + piece.Offsets[g].X, baseline + shift - piece.Offsets[g].Y);
                    pen += piece.Advances[g];
                }

                glyphs = new GlyphRun(piece.Font, piece.Glyphs, points, _text, piece.Clusters, null, piece.Advances)
                {
                    BidiLevel = piece.Level,
                    Script = piece.Script,
                    Language = piece.Language,
                };
            }

            LayoutRunKind kind = piece.Kind switch
            {
                RunKind.Text => LayoutRunKind.Text,
                RunKind.Placeholder => LayoutRunKind.Placeholder,
                RunKind.Tab => LayoutRunKind.Tab,
                _ => LayoutRunKind.Control,
            };
            runs.Add(new LayoutRun(kind, piece.Start, piece.Length, piece.Level, piece.Style, x, piece.Advance, glyphs, piece.Kind == RunKind.Placeholder ? piece.Placeholder : -1));
        }

        return new TextLine(draft.Start, draft.End - draft.Start, top, lineAscent, lineDescent, left, draft.ContentWidth, draft.HardBreak, false, runs);
    }
}
