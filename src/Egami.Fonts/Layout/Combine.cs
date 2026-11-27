using Egami.Fonts.Shaping;
using Egami.Fonts.Unicode;

namespace Egami.Fonts.Layout;

/// <summary>
/// Tate-chu-yoko (CSS <c>text-combine-upright</c>): short horizontal text, such as "12" or "!?", set upright in one em of a
/// vertical line. Set it on <see cref="TextStyle.CombineUpright"/>; horizontal lines and sideways text ignore it.
/// </summary>
/// <remarks>
/// The combined text is shaped horizontally and fit into one em: with the font's <c>hwid</c>, <c>twid</c> or <c>qwid</c>
/// feature for two, three or four characters where the font has it, then scaled down if it is still wider (the scale keeps the
/// glyphs' proportions). It breaks, justifies and takes an emphasis mark as one ideograph.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="tate-chu-yoko"/>
/// </example>
public readonly record struct TextCombine
{
    private TextCombine(TextCombineKind kind, int maxDigits)
    {
        Kind = kind;
        MaxDigits = maxDigits;
    }

    /// <summary>Gets no combining: the text stands as its characters' orientation says.</summary>
    public static TextCombine None => default;

    /// <summary>Gets combining of all the style's text: each stretch of it is one upright unit.</summary>
    public static TextCombine All => new(TextCombineKind.All, 0);

    /// <summary>Gets how the text combines.</summary>
    public TextCombineKind Kind { get; }

    /// <summary>Gets the longest run of ASCII digits <see cref="Digits"/> combines; 0 for the other kinds.</summary>
    public int MaxDigits { get; }

    /// <summary>Combining of each run of ASCII digits up to <paramref name="maxDigits"/> long, as CSS's <c>digits</c>: a longer run
    /// stays as it is.</summary>
    /// <param name="maxDigits">The longest run combined, 2 to 4.</param>
    /// <returns>The combining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxDigits"/> is not 2 to 4.</exception>
    public static TextCombine Digits(int maxDigits) =>
        maxDigits is >= 2 and <= 4 ? new(TextCombineKind.Digits, maxDigits) : throw new ArgumentOutOfRangeException(nameof(maxDigits), maxDigits, "Digits combine in runs of 2 to 4.");
}

/// <summary>How a <see cref="TextCombine"/> combines.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="tate-chu-yoko"/>
/// </example>
public enum TextCombineKind
{
    /// <summary>No combining.</summary>
    None = 0,

    /// <summary>All the text combines.</summary>
    All = 1,

    /// <summary>Short runs of ASCII digits combine.</summary>
    Digits = 2,
}

/// <summary>A vertical paragraph's tate-chu-yoko units: UTF-16 ranges that shape, break and justify as one upright ideograph.</summary>
internal sealed class CombineTable
{
    private static readonly FontTag[] WidthFeatures = [new("hwid"), new("twid"), new("qwid")];

    private readonly int[] _unitAt;
    private readonly int[] _ends;
    private readonly float[] _scales;

    private CombineTable(int[] unitAt, List<(int Start, int End)> units)
    {
        _unitAt = unitAt;
        _ends = [.. units.Select(u => u.End)];
        Starts = [.. units.Select(u => u.Start)];
        _scales = new float[units.Count];
        Array.Fill(_scales, 1f);
    }

    /// <summary>Each unit's first UTF-16 index, in order.</summary>
    public int[] Starts { get; }

    /// <summary>Finds the units of a vertical paragraph whose orientation lets text stand upright; null when there are none.</summary>
    public static CombineTable? Create(RichText rich, ParagraphStyle paragraph, ResolvedStyle[] styles)
    {
        if (paragraph.WritingMode == WritingMode.HorizontalTb || paragraph.TextOrientation == TextOrientation.Sideways)
        {
            return null;
        }

        string text = rich.Text;
        int[]? unitAt = null;
        List<(int, int)> units = [];
        int i = 0;
        while (i < text.Length)
        {
            int span = rich.SpanAt(i);
            TextCombine combine = styles[span + 1].Style.CombineUpright ?? TextCombine.None;
            int end = i;
            if (combine.Kind == TextCombineKind.All)
            {
                while (end < text.Length && rich.SpanAt(end) == span && Combinable(text, end))
                {
                    end = TextSegmenter.NextGraphemeBoundary(text, end);
                }
            }
            else if (combine.Kind == TextCombineKind.Digits && IsDigit(text[i]) && (i == 0 || !IsDigit(text[i - 1])))
            {
                while (end < text.Length && IsDigit(text[end]) && (styles[rich.SpanAt(end) + 1].Style.CombineUpright ?? TextCombine.None).Kind == TextCombineKind.Digits)
                {
                    end++;
                }

                if ((end < text.Length && IsDigit(text[end])) || end - i > combine.MaxDigits || (end < text.Length && !TextSegmenter.IsGraphemeBoundary(text, end)))
                {
                    end = i;
                }
            }

            if (end > i)
            {
                unitAt ??= CreateUnitAt(text.Length);
                Array.Fill(unitAt, units.Count, i, end - i);
                units.Add((i, end));
                i = end;
            }
            else
            {
                i = TextSegmenter.NextGraphemeBoundary(text, i);
            }
        }

        return unitAt is null ? null : new CombineTable(unitAt, units);
    }

    private static int[] CreateUnitAt(int length)
    {
        int[] unitAt = new int[length];
        Array.Fill(unitAt, -1);
        return unitAt;
    }

    private static bool IsDigit(char c) => c is >= '0' and <= '9';

    /// <summary>Whether a cluster can be part of a combined unit: not a tab, hard break, other control or placeholder.</summary>
    private static bool Combinable(string text, int index)
    {
        int cp = TextSegmenter.CodePointAt(text, index, out _);
        return cp != 0xFFFC && cp != '\t' && UnicodeData.CategoryOf(cp) != System.Globalization.UnicodeCategory.Control
            && cp is not (0x85 or 0x2028 or 0x2029);
    }

    /// <summary>The unit at a UTF-16 index, or -1.</summary>
    public int UnitAt(int index) => index >= 0 && index < _unitAt.Length ? _unitAt[index] : -1;

    /// <summary>The end of a unit.</summary>
    public int EndOf(int unit) => _ends[unit];

    /// <summary>Whether a boundary at <paramref name="index"/> falls inside a unit.</summary>
    public bool Splits(int index) => index > 0 && index < _unitAt.Length && _unitAt[index] >= 0 && _unitAt[index] == _unitAt[index - 1];

    /// <summary>The scale a unit starting at <paramref name="start"/> is drawn at; 1 when no unit starts there.</summary>
    public float ScaleAt(int start) => UnitAt(start) is int u and >= 0 && Starts[u] == start ? _scales[u] : 1;

    /// <summary>The text line breaking sees: each unit as ideographs, so it breaks as one (the breaks inside are dropped).</summary>
    public string BreakText(string text)
    {
        char[] chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (_unitAt[i] >= 0)
            {
                chars[i] = '一';
            }
        }

        return new string(chars);
    }

    /// <summary>
    /// Shapes a unit horizontally and fits it into one em of a vertical line: the glyphs as an upright run whose last glyph
    /// advances one em down the line, the others none, and whose offsets centre the text in the em square.
    /// </summary>
    public GlyphBuffer Shape(string text, int start, int length, Font font, RunProperties properties, float size)
    {
        int unit = _unitAt[start];
        int graphemes = 0;
        for (int g = start; g < start + length; g = TextSegmenter.NextGraphemeBoundary(text, g))
        {
            graphemes++;
        }

        List<FontFeature> features = [.. properties.Features ?? []];
        if (graphemes is >= 2 and <= 4 && HasFeature(font.Typeface, WidthFeatures[graphemes - 2]))
        {
            features.Add(new FontFeature(WidthFeatures[graphemes - 2], 1));
        }

        GlyphBuffer glyphs = new();
        TextShaper.ShapeRun(text, start, length, font, properties with { Direction = TextDirection.LeftToRight, Features = features }, glyphs);
        float width = glyphs.Advance;
        float scale = width > size && width > 0 ? size / width : 1;
        _scales[unit] = scale;
        (float a, float d, _) = LineBuilder.Rounded(font);
        float pen = 0;
        for (int g = 0; g < glyphs.Count; g++)
        {
            GlyphPosition p = glyphs.Positions[g];
            float x = (-scale * width / 2) + (scale * (pen + p.XOffset));
            float y = -(size / 2) - (scale * (a - d) / 2) + (scale * p.YOffset);
            glyphs.SetPosition(g, new GlyphPosition(0, g == glyphs.Count - 1 ? -size : 0, x, y));
            pen += p.XAdvance;
        }

        return glyphs;
    }

    private static bool HasFeature(Typeface face, FontTag feature)
    {
        OpenTypeLayout layout = face.Layout;
        foreach (FontTag script in layout.Scripts)
        {
            if (layout.GetFeatures(script).Contains(feature))
            {
                return true;
            }
        }

        return false;
    }
}
