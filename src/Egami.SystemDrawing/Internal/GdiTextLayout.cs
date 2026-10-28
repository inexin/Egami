using System.Numerics;

using Egami.Fonts.Shaping;
using Egami.SystemDrawing.Text;

using EgamiFonts = Egami.Fonts;

namespace Egami.SystemDrawing.Internal;

/// <summary>
/// GDI+'s string layout, as measured on Windows: lines broken at spaces and hyphens (or anywhere in a word too long for a line),
/// 1/6 em of padding on each side and advances widened by 3 % with the default format (none of either with
/// <see cref="StringFormat.GenericTypographic"/>), one line spacing between baselines, alignment, trimming of the last line that
/// fits, hotkey prefixes and tab stops. Coordinates are the caller's world units, the layout rectangle's corner at the origin.
/// </summary>
internal sealed class GdiTextLayout
{
    /// <summary>How much the default format widens design advances, as measured against StringFormat.GenericTypographic.</summary>
    private const float Widening = 1.03f;

    private static readonly ShapingOptions s_noLigatures = new()
    {
        Features = [new FontFeature(new EgamiFonts.FontTag("liga"), 0), new FontFeature(new EgamiFonts.FontTag("clig"), 0)],
    };

    private readonly string _text;
    private readonly int[] _source;
    private readonly bool[] _hotkey;
    private readonly float[] _advance;
    private readonly float[] _drawAdvance;
    private readonly List<(ushort Glyph, float Dx, float Dy)>?[] _glyphs;
    private readonly float _available;
    private readonly float _pad;
    private readonly float _em;
    private readonly StringFormat _format;
    private readonly EgamiFonts.Font _font;

    private GdiTextLayout(string text, int[] source, bool[] hotkey, Font font, float em, SizeF box, StringFormat format, float gridScale)
    {
        _text = text;
        _source = source;
        _hotkey = hotkey;
        _em = em;
        _format = format;
        bool typographic = format.Typographic;
        _pad = typographic ? 0 : em / 6;
        Box = box;
        _available = box.Width > 0 ? MathF.Max(0, box.Width - (2 * _pad)) : float.PositiveInfinity;
        (int emHeight, int ascent, int descent, int spacing) = font.FontFamily.Metrics(font.Style);
        LineSpacing = em * spacing / emHeight;
        Ascent = em * ascent / emHeight;
        Descent = em * descent / emHeight;
        _font = font.ToEgamiFont(em, kerning: false);

        // Advances per character from the shaped paragraphs: widened design advances for the layout, and for drawing the hinted
        // advances at the device size GDI+ places glyphs by (grid-fitted, or near design with the typographic format).
        _advance = new float[text.Length];
        _drawAdvance = gridScale > 0 ? new float[text.Length] : _advance;
        _glyphs = new List<(ushort, float, float)>?[text.Length];
        EgamiFonts.Font? draw = gridScale > 0
            ? font.ToEgamiFont(em * gridScale, kerning: false) with
            {
                Hinting = EgamiFonts.FontHinting.Full,
                HintingTarget = EgamiFonts.TrueTypeTarget.GdiPlus,
                Metrics = typographic ? EgamiFonts.FontMetrics.GridFittedToDesign : EgamiFonts.FontMetrics.GridFitted,
            }
            : null;
        float widen = typographic ? 1 : Widening;
        int start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || text[i] is '\n' or '\r' or '\t')
            {
                if (i > start)
                {
                    Shape(_font, start, i, 1, widen, _advance, draw is null);
                    if (draw is not null)
                    {
                        Shape(draw.Value, start, i, 1 / gridScale, 1, _drawAdvance, true);
                    }
                }

                start = i + 1;
            }
        }

        Lines = [];
        Build();
    }

    /// <summary>Gets the layout box: the rectangle's size, zero along an axis it does not bound.</summary>
    public SizeF Box { get; }

    /// <summary>Gets the distance between baselines.</summary>
    public float LineSpacing { get; }

    /// <summary>Gets the ascent: from a line's top to its baseline.</summary>
    public float Ascent { get; }

    /// <summary>Gets the descent below the baseline.</summary>
    public float Descent { get; }

    /// <summary>Gets the font the glyphs are drawn with, at the em size in world units.</summary>
    public EgamiFonts.Font Font => _font;

    /// <summary>Gets the lines, top to bottom.</summary>
    public List<Line> Lines { get; }

    /// <summary>Gets a value indicating whether the lines run top to bottom, turned a quarter clockwise.</summary>
    public bool Vertical => (_format.FormatFlags & StringFormatFlags.DirectionVertical) != 0;

    /// <summary>Gets the number of characters of the original text the lines hold.</summary>
    public int CharactersFitted { get; private set; }

    /// <summary>Lays a string out in a box (zero for an unbounded side), sizes in world units.</summary>
    /// <param name="text">The text.</param>
    /// <param name="font">The font.</param>
    /// <param name="em">The em size in world units.</param>
    /// <param name="box">The layout box; vertical formats swap its sides.</param>
    /// <param name="format">The format, or null for the default.</param>
    /// <param name="gridScale">Device pixels per world unit to place glyphs by hinted advances at, or 0 to place them as measured.</param>
    /// <returns>The layout.</returns>
    public static GdiTextLayout Create(ReadOnlySpan<char> text, Font font, float em, SizeF box, StringFormat? format, float gridScale = 0)
    {
        format ??= StringFormat.GenericDefault;
        if ((format.FormatFlags & StringFormatFlags.DirectionVertical) != 0)
        {
            box = new SizeF(box.Height, box.Width);
        }

        // The hotkey prefix: an ampersand marks the next character (Show underlines it); two make one ampersand.
        var display = new System.Text.StringBuilder(text.Length);
        List<int> source = new(text.Length);
        List<bool> hotkey = new(text.Length);
        bool prefix = format.HotkeyPrefix != HotkeyPrefix.None;
        for (int i = 0; i < text.Length; i++)
        {
            bool mark = false;
            if (prefix && text[i] == '&' && i + 1 < text.Length)
            {
                i++;
                mark = text[i] != '&' && format.HotkeyPrefix == HotkeyPrefix.Show;
            }

            display.Append(text[i]);
            source.Add(i);
            hotkey.Add(mark);
        }

        return new GdiTextLayout(display.ToString(), [.. source], [.. hotkey], font, em, box, format, gridScale);
    }

    /// <summary>Gets the size MeasureString reports: the widest line and the padding, the lines and an eighth em.</summary>
    public SizeF Measure()
    {
        if (_text.Length == 0)
        {
            return SizeF.Empty;
        }

        float width = 0;
        foreach (Line line in Lines)
        {
            width = MathF.Max(width, line.Width);
        }

        float extra = _format.Typographic ? 0 : _em / 8;
        float height = Lines.Count == 1 && Box.Width <= 0 ? Ascent + Descent + extra : (Lines.Count * LineSpacing) + extra;
        if (Lines.Count == 0)
        {
            height = 0;
        }

        SizeF size = new(width + (2 * _pad), height);
        return Vertical ? new SizeF(size.Height, size.Width) : size;
    }

    /// <summary>The vertical offset of the first line for the line alignment.</summary>
    public float Top
    {
        get
        {
            if (Box.Height <= 0 && _format.LineAlignment == StringAlignment.Near)
            {
                return 0;
            }

            float block = Lines.Count * LineSpacing;
            float room = Box.Height > 0 ? Box.Height : 0;
            return _format.LineAlignment switch
            {
                StringAlignment.Center => (room - block) / 2,
                StringAlignment.Far => room - block,
                _ => 0,
            };
        }
    }

    /// <summary>Maps a point of the unturned layout into the box: vertical text runs down from the right edge.</summary>
    public Vector2 Place(Vector2 p) => Vertical ? new Vector2(Box.Height - p.Y, p.X) : p;

    /// <summary>The turn of vertical text: a quarter clockwise.</summary>
    public Matrix3x2 Turn => Vertical ? new Matrix3x2(0, 1, -1, 0, Box.Height, 0) : Matrix3x2.Identity;

    /// <summary>The glyphs of a line, positioned from the layout's origin (y down, the baseline at y = 0 for the first line).</summary>
    public EgamiFonts.GlyphRun? Run(Line line, float top)
    {
        List<ushort> glyphs = [];
        List<Vector2> positions = [];
        float baseline = top + line.Top + Ascent;
        foreach (Item item in line.Items)
        {
            List<(ushort Glyph, float Dx, float Dy)>? cluster = item.Char < 0 ? item.Ellipsis : _glyphs[item.Char];
            if (cluster is null)
            {
                continue;
            }

            foreach ((ushort g, float dx, float dy) in cluster)
            {
                glyphs.Add(g);
                positions.Add(new Vector2(line.X + item.X + dx, baseline + dy));
            }
        }

        return glyphs.Count == 0 ? null : EgamiFonts.GlyphRun.Create(_font, [.. glyphs], [.. positions]);
    }

    /// <summary>The underlines the hotkey prefix asks for: (left, right) per line, in layout coordinates.</summary>
    public IEnumerable<(Line Line, float X0, float X1)> Hotkeys()
    {
        foreach (Line line in Lines)
        {
            foreach (Item item in line.Items)
            {
                if (item.Char >= 0 && _hotkey[item.Char])
                {
                    yield return (line, line.X + item.X, line.X + item.X + item.Advance);
                }
            }
        }
    }

    /// <summary>The spans of a range of the original text, per line: (line, left, right) in layout coordinates.</summary>
    public IEnumerable<(Line Line, float X0, float X1)> Spans(int first, int length)
    {
        foreach (Line line in Lines)
        {
            float x0 = float.PositiveInfinity, x1 = float.NegativeInfinity;
            foreach (Item item in line.Items)
            {
                if (item.Char >= 0 && _source[item.Char] >= first && _source[item.Char] < first + length)
                {
                    x0 = MathF.Min(x0, line.X + item.X);
                    x1 = MathF.Max(x1, line.X + item.X + item.Advance);
                }
            }

            if (x0 <= x1)
            {
                yield return (line, x0, x1);
            }
        }
    }

    private void Shape(EgamiFonts.Font font, int start, int end, float scale, float widen, float[] advances, bool keepGlyphs)
    {
        string piece = _text[start..end];
        EgamiFonts.GlyphRun run = font.Shape(piece, s_noLigatures);
        ReadOnlySpan<ushort> glyphs = run.Glyphs;
        ReadOnlySpan<Vector2> positions = run.Positions;
        ReadOnlySpan<int> clusters = run.Clusters;
        for (int g = 0; g < glyphs.Length; g++)
        {
            int c = Math.Clamp(clusters[g], 0, piece.Length - 1) + start;
            float next = g + 1 < glyphs.Length ? positions[g + 1].X : run.Advance;
            advances[c] += (next - positions[g].X) * scale * widen;
            if (!keepGlyphs)
            {
                continue;
            }

            List<(ushort, float, float)> list = _glyphs[c] ??= [];
            float clusterStart = positions[g].X;
            for (int h = g - 1; h >= 0 && clusters[h] == clusters[g]; h--)
            {
                clusterStart = positions[h].X;
            }

            list.Add((glyphs[g], (positions[g].X - clusterStart) * scale, positions[g].Y * scale));
        }
    }

    private bool Wraps => (_format.FormatFlags & StringFormatFlags.NoWrap) == 0 && !float.IsPositiveInfinity(_available);

    private bool Space(int i) => _text[i] is ' ' or '　' or '\t';

    private void Build()
    {
        // Paragraphs: one per line break, \r\n counting once.
        List<(int Start, int End)> paragraphs = [];
        int start = 0;
        for (int i = 0; i <= _text.Length; i++)
        {
            if (i == _text.Length || _text[i] is '\n' or '\r')
            {
                paragraphs.Add((start, i));
                if (i + 1 < _text.Length && _text[i] == '\r' && _text[i + 1] == '\n')
                {
                    i++;
                }

                start = i + 1;
            }
        }

        if (_text.Length == 0)
        {
            return;
        }

        int maxLines = int.MaxValue;
        StringFormatFlags flags = _format.FormatFlags;
        if (Box.Height > 0)
        {
            float fit = Box.Height / LineSpacing;
            if ((flags & StringFormatFlags.LineLimit) != 0)
            {
                maxLines = (int)MathF.Floor(fit + 1e-4f);
            }
            else if ((flags & StringFormatFlags.NoClip) == 0)
            {
                maxLines = Math.Max(1, (int)MathF.Round(fit));
            }
        }

        foreach ((int ps, int pe) in paragraphs)
        {
            int i = ps;
            do
            {
                if (Lines.Count >= maxLines)
                {
                    return;
                }

                bool last = Lines.Count == maxLines - 1;
                int end = last && _format.Trimming != StringTrimming.None ? pe : Break(i, pe);
                Line line = Trim(i, end, last && end < pe);
                if (last && _format.Trimming != StringTrimming.None && Width(i, pe) > _available + 0.01f)
                {
                    line = Trim(i, pe, true);
                }
                else if (!Wraps && !float.IsPositiveInfinity(_available) && line.Width > _available + 0.01f)
                {
                    line = Trim(i, end, true);
                }

                Add(line);
                CharactersFitted = line.End > 0 ? _source[line.End - 1] + 1 : CharactersFitted;
                i = end;
            }
            while (i < pe);
        }

        if (paragraphs.Count > 0 && paragraphs[^1].End < _text.Length)
        {
            CharactersFitted = _source.Length > 0 ? _source[^1] + 1 : 0;
        }
    }

    private void Add(Line line)
    {
        // Glyphs go where the drawing advances put them; a tab keeps the stop it reached.
        float pen = 0, inked = 0;
        int previous = -1;
        for (int k = 0; k < line.Items.Count; k++)
        {
            Item item = line.Items[k];
            if (item.Char >= 0 && previous >= 0 && _text.AsSpan(previous, item.Char - previous).Contains('\t'))
            {
                pen = MathF.Max(pen, item.X);
            }

            float advance = item.Char >= 0 ? _drawAdvance[item.Char] : item.Advance;
            line.Items[k] = item with { X = pen, Advance = advance };
            pen += advance;
            previous = item.Char >= 0 ? item.Char : previous;
            if (item.Char < 0 || !Space(item.Char))
            {
                inked = pen;
            }
        }

        // Alignment goes by the width as placed.
        float placed = ReferenceEquals(_drawAdvance, _advance) ? line.Width : inked;

        line.Top = Lines.Count * LineSpacing;
        bool rtl = (_format.FormatFlags & StringFormatFlags.DirectionRightToLeft) != 0;
        StringAlignment align = _format.Alignment;
        if (rtl)
        {
            align = align == StringAlignment.Near ? StringAlignment.Far : align == StringAlignment.Far ? StringAlignment.Near : align;
        }

        float room = float.IsPositiveInfinity(_available) ? 0 : _available;
        line.X = _pad + (float.IsPositiveInfinity(_available)
            ? align switch { StringAlignment.Center => -(placed / 2) - _pad, StringAlignment.Far => -placed - (2 * _pad), _ => 0 }
            : align switch { StringAlignment.Center => (room - placed) / 2, StringAlignment.Far => room - placed, _ => 0 });
        Lines.Add(line);
    }

    /// <summary>Where a line starting at <paramref name="start"/> breaks: after the last space or hyphen that fits, else mid-word.</summary>
    private int Break(int start, int end)
    {
        if (!Wraps)
        {
            return end;
        }

        float x = 0;
        int lastBreak = -1;
        for (int j = start; j < end; j++)
        {
            float a = Advance(j, x);
            if (!Space(j) && j > start && x + a > _available + 0.01f)
            {
                if (lastBreak > start)
                {
                    return lastBreak;
                }

                return j;
            }

            x += a;
            if (Space(j) || _text[j] == '-')
            {
                lastBreak = j + 1;
            }
        }

        return end;
    }

    /// <summary>A character's advance at a pen position: a tab moves to the next tab stop.</summary>
    private float Advance(int i, float x)
    {
        if (_text[i] != '\t')
        {
            return _advance[i];
        }

        float[] stops = _format.TabPositions();
        foreach (float stop in stops)
        {
            if (stop > x + 0.01f)
            {
                return stop - x;
            }
        }

        if (stops.Length > 0)
        {
            return _font.GetAdvance(_font.Typeface.GetGlyph(' '));
        }

        float step = 4 * _em;
        return (MathF.Floor((x / step) + 1e-4f) + 1) * step - x;
    }

    /// <summary>The width of characters laid out from a line start, without trailing spaces unless the format measures them.</summary>
    private float Width(int start, int end)
    {
        float x = 0, inked = 0;
        for (int j = start; j < end; j++)
        {
            x += Advance(j, x);
            if (!Space(j))
            {
                inked = x;
            }
        }

        return (_format.FormatFlags & StringFormatFlags.MeasureTrailingSpaces) != 0 ? x : inked;
    }

    /// <summary>A line of characters, cut to the available width by the trimming when <paramref name="cut"/> is set.</summary>
    private Line Trim(int start, int end, bool cut)
    {
        if (!cut || Width(start, end) <= _available + 0.01f)
        {
            return Chars(start, end, end);
        }

        StringTrimming trimming = _format.Trimming;
        if (trimming == StringTrimming.None)
        {
            return Chars(start, Break(start, end), end);
        }

        bool ellipsis = trimming is StringTrimming.EllipsisCharacter or StringTrimming.EllipsisWord or StringTrimming.EllipsisPath;
        (List<(ushort, float, float)> glyphs, float width) dots = ellipsis ? Ellipsis() : ([], 0);
        float room = _available - dots.width;
        if (trimming == StringTrimming.EllipsisPath)
        {
            // Keep the start and the end: the part after the last separator, or as much of each half as fits.
            int sep = _text.LastIndexOfAny(['\\', '/'], end - 1, end - start);
            int tail = sep > start ? sep : end;
            float half = room / 2;
            if (sep <= start)
            {
                float w = 0;
                while (tail > start && w + _advance[tail - 1] <= half)
                {
                    w += _advance[--tail];
                }
            }

            float tailWidth = 0;
            for (int j = tail; j < end; j++)
            {
                tailWidth += _advance[j];
            }

            int head = Fit(start, tail, room - tailWidth, false);
            Line path = Chars(start, head, end);
            float x = Width(start, head);
            path.Items.Add(new Item(-1, x, dots.width) { Ellipsis = dots.glyphs });
            x += dots.width;
            for (int j = tail; j < end; j++)
            {
                path.Items.Add(new Item(j, x, _advance[j]));
                x += _advance[j];
            }

            path.Width = x;
            return path;
        }

        bool words = trimming is StringTrimming.Word or StringTrimming.EllipsisWord;
        int keep = Fit(start, end, room, words);
        Line line = Chars(start, keep, end);
        if (ellipsis)
        {
            float x = Width(start, keep);
            line.Items.RemoveAll(item => item.X >= x);
            line.Items.Add(new Item(-1, x, dots.width) { Ellipsis = dots.glyphs });
            line.Width = x + dots.width;
        }

        return line;
    }

    /// <summary>How many characters from <paramref name="start"/> fit in a width, ending at a word break when asked to.</summary>
    private int Fit(int start, int end, float width, bool words)
    {
        float x = 0;
        int lastBreak = -1, j = start;
        for (; j < end; j++)
        {
            float a = Advance(j, x);
            if (!Space(j) && x + a > width + 0.01f)
            {
                break;
            }

            x += a;
            if (Space(j))
            {
                lastBreak = j;
            }
        }

        return words && j < end && lastBreak > start ? lastBreak : j;
    }

    /// <summary>A line of the characters from <paramref name="start"/> to <paramref name="end"/>, covering the source up to <paramref name="covers"/>.</summary>
    private Line Chars(int start, int end, int covers)
    {
        Line line = new() { End = end };
        float x = 0;
        for (int j = start; j < end; j++)
        {
            float a = Advance(j, x);
            if (_text[j] != '\t')
            {
                line.Items.Add(new Item(j, x, a));
            }

            x += a;
        }

        line.Width = Width(start, end);
        return line;
    }

    private (List<(ushort, float, float)> Glyphs, float Width) Ellipsis()
    {
        float k = _format.Typographic ? 1 : Widening;
        ushort glyph = _font.Typeface.GetGlyph('…');
        if (glyph != 0)
        {
            return ([(glyph, 0, 0)], _font.GetAdvance(glyph) * k);
        }

        ushort dot = _font.Typeface.GetGlyph('.');
        float a = _font.GetAdvance(dot) * k;
        return ([(dot, 0, 0), (dot, a, 0), (dot, 2 * a, 0)], 3 * a);
    }

    /// <summary>One line: its characters with their pen positions, its width, where it starts and its top.</summary>
    internal sealed class Line
    {
        public List<Item> Items { get; } = [];

        /// <summary>Gets or sets the width, without trailing spaces unless the format measures them.</summary>
        public float Width { get; set; }

        /// <summary>Gets or sets the left of the line, the padding and alignment included.</summary>
        public float X { get; set; }

        /// <summary>Gets or sets the top of the line, below the layout's top.</summary>
        public float Top { get; set; }

        /// <summary>Gets the end of the characters it covers, a trimmed line covering the rest of its paragraph.</summary>
        public int End { get; init; }
    }

    /// <summary>A character on a line, or the ellipsis (character -1), at a pen position from the line's left.</summary>
    internal sealed record Item(int Char, float X, float Advance)
    {
        public List<(ushort Glyph, float Dx, float Dy)>? Ellipsis { get; init; }
    }
}
