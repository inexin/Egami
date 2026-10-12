using System.Numerics;

using Egami.Fonts.Unicode;

namespace Egami.Fonts.Shaping;

/// <summary>
/// Text shaped by <see cref="TextShaper.Shape(string, Font, ShapingOptions?)"/>: one <see cref="ShapedLine"/> per line between hard
/// breaks, each with its runs in visual order. Nothing wraps.
/// </summary>
/// <remarks>Immutable once returned; safe to share between threads.</remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Shaping.cs" region="shape"/>
/// </example>
public sealed class ShapedText
{
    internal ShapedText(string text, IReadOnlyList<ShapedLine> lines)
    {
        Text = text;
        Lines = lines;
    }

    /// <summary>Gets the text that was shaped.</summary>
    public string Text { get; }

    /// <summary>Gets the paragraph level of the first line: 0 for left to right, 1 for right to left.</summary>
    public int ParagraphLevel => Lines.Count == 0 ? 0 : Lines[0].ParagraphLevel;

    /// <summary>Gets the lines, split at <c>\n</c>, <c>\r\n</c>, <c>\r</c>, U+0085, U+2028 and U+2029.</summary>
    public IReadOnlyList<ShapedLine> Lines { get; }

    /// <summary>Gets the widest line's advance.</summary>
    public float Advance => Lines.Count == 0 ? 0 : Lines.Max(l => l.Advance);
}

/// <summary>One line of <see cref="ShapedText"/>: its range and its runs in visual order.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Shaping.cs" region="shape"/>
/// </example>
public sealed class ShapedLine
{
    internal ShapedLine(int start, int length, int paragraphLevel, IReadOnlyList<ShapedRun> runs, float ascent, float descent, float lineHeight)
    {
        Start = start;
        Length = length;
        ParagraphLevel = paragraphLevel;
        Runs = runs;
        Ascent = ascent;
        Descent = descent;
        LineHeight = lineHeight;
        Advance = runs.Sum(r => r.Advance);
    }

    /// <summary>Gets the index of the line's first UTF-16 unit, its break excluded.</summary>
    public int Start { get; }

    /// <summary>Gets the number of UTF-16 units in the line, its break excluded.</summary>
    public int Length { get; }

    /// <summary>Gets the line's paragraph level: 0 for left to right, 1 for right to left.</summary>
    public int ParagraphLevel { get; }

    /// <summary>Gets the runs from left to right (top to bottom for a vertical line).</summary>
    public IReadOnlyList<ShapedRun> Runs { get; }

    /// <summary>Gets the line's advance: the runs' advances summed.</summary>
    public float Advance { get; }

    /// <summary>Gets the highest ascent of the line's fonts, y up.</summary>
    public float Ascent { get; }

    /// <summary>Gets the lowest descent of the line's fonts, y up (negative below the baseline).</summary>
    public float Descent { get; }

    /// <summary>Gets the largest line height of the line's fonts.</summary>
    public float LineHeight { get; }
}

/// <summary>A run of a <see cref="ShapedLine"/>: one font, one bidi level, one script, one language, and its glyphs.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Shaping.cs" region="shape"/>
/// </example>
public sealed class ShapedRun
{
    internal ShapedRun(Font font, int bidiLevel, Script script, string? language, int start, int length, GlyphBuffer glyphs, bool vertical)
    {
        Font = font;
        BidiLevel = bidiLevel;
        Script = script;
        Language = language;
        Start = start;
        Length = length;
        Glyphs = glyphs;
        IsVertical = vertical;
        Advance = vertical ? -glyphs.Positions.ToArray().Sum(p => p.YAdvance) : glyphs.Advance;
    }

    /// <summary>Gets the font the run is shaped in: the text's, a span's or a fallback font.</summary>
    public Font Font { get; }

    /// <summary>Gets the run's bidi embedding level; odd levels run right to left.</summary>
    public int BidiLevel { get; }

    /// <summary>Gets the run's script.</summary>
    public Script Script { get; }

    /// <summary>Gets the run's BCP 47 language tag, or null.</summary>
    public string? Language { get; }

    /// <summary>Gets the index of the run's first UTF-16 unit.</summary>
    public int Start { get; }

    /// <summary>Gets the number of UTF-16 units in the run.</summary>
    public int Length { get; }

    /// <summary>Gets the shaped glyphs in visual order; read it, do not change it.</summary>
    public GlyphBuffer Glyphs { get; }

    /// <summary>Gets whether the run was shaped top to bottom.</summary>
    public bool IsVertical { get; }

    /// <summary>Gets the run's advance along the line: the glyph advances summed (downward for a vertical run).</summary>
    public float Advance { get; }

    /// <summary>Builds the glyph run that draws this run with its pen at <paramref name="origin"/>.</summary>
    /// <param name="origin">The pen position of the run's first glyph, on the baseline, y down.</param>
    /// <param name="text">The text the run was shaped from, for the run's clusters, or null.</param>
    /// <returns>The glyph run, with clusters when <paramref name="text"/> is given, and the run's level, script and language.</returns>
    /// <exception cref="ArgumentException">A cluster points outside <paramref name="text"/>.</exception>
    public GlyphRun ToGlyphRun(Vector2 origin, string? text = null)
    {
        ReadOnlySpan<GlyphPosition> positions = Glyphs.Positions;
        Vector2[] points = new Vector2[positions.Length];
        float x = origin.X, y = origin.Y;
        for (int i = 0; i < positions.Length; i++)
        {
            GlyphPosition p = positions[i];
            points[i] = new Vector2(x + p.XOffset, y - p.YOffset);
            x += p.XAdvance;
            y -= p.YAdvance;
        }

        if (text is not null)
        {
            foreach (int cluster in Glyphs.Clusters)
            {
                if ((uint)cluster > (uint)text.Length)
                {
                    throw new ArgumentException("A cluster points outside the text.", nameof(text));
                }
            }
        }

        float[] advances = [.. positions.ToArray().Select(p => p.XAdvance)];
        return new GlyphRun(Font, Glyphs.GlyphIds.ToArray(), points, text, text is null ? null : Glyphs.Clusters.ToArray(), null, advances)
        {
            BidiLevel = BidiLevel,
            Script = Script,
            Language = Language,
        };
    }
}
