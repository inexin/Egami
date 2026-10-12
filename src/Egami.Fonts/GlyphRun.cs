using System.Numerics;

namespace Egami.Fonts;

/// <summary>
/// Positioned glyphs of one font, ready to draw: glyph ids, one position per glyph relative to the run's origin (the first
/// baseline's left end, y down, in canvas units), and optionally the source text with the UTF-16 index each glyph came from, which
/// lets a PDF writer keep text searchable.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="caption"/>
/// </example>
public sealed class GlyphRun
{
    private readonly ushort[] _glyphs;
    private readonly Vector2[] _positions;
    private readonly int[]? _clusters;
    private TextBounds? _bounds;

    internal GlyphRun(Font font, ushort[] glyphs, Vector2[] positions, string? text, int[]? clusters)
    {
        Font = font;
        _glyphs = glyphs;
        _positions = positions;
        Text = text;
        _clusters = clusters;
    }

    /// <summary>Gets the font every glyph is drawn in.</summary>
    public Font Font { get; }

    /// <summary>Gets the number of glyphs.</summary>
    public int Count => _glyphs.Length;

    /// <summary>Gets the glyph ids.</summary>
    public ReadOnlySpan<ushort> Glyphs => _glyphs;

    /// <summary>Gets each glyph's origin on its baseline, relative to the run's origin, y down.</summary>
    public ReadOnlySpan<Vector2> Positions => _positions;

    /// <summary>Gets the source text, or <see langword="null"/> when the run was built from glyphs alone.</summary>
    public string? Text { get; }

    /// <summary>Gets the index in <see cref="Text"/> each glyph came from; empty when there is no text.</summary>
    public ReadOnlySpan<int> Clusters => _clusters;

    /// <summary>Gets the end of the last glyph's advance on its line: the pen position after the run.</summary>
    public float Advance
    {
        get
        {
            float widest = 0;
            for (int i = 0; i < _glyphs.Length; i++)
            {
                widest = MathF.Max(widest, _positions[i].X + Font.GetAdvance(_glyphs[i]));
            }

            return widest;
        }
    }

    /// <summary>Gets the exact box around the run's ink, relative to its origin, y down; empty when nothing has ink.</summary>
    public TextBounds Bounds => _bounds ??= ComputeBounds();

    /// <summary>Creates a run from glyphs and positions, such as a layout engine's or a PDF content stream's.</summary>
    /// <param name="font">The font.</param>
    /// <param name="glyphs">The glyph ids.</param>
    /// <param name="positions">One position per glyph, relative to the run's origin, y down.</param>
    /// <param name="text">The source text, or <see langword="null"/>.</param>
    /// <param name="clusters">The index in <paramref name="text"/> of each glyph; empty when <paramref name="text"/> is null.</param>
    /// <returns>The run, holding copies of the spans.</returns>
    /// <exception cref="ArgumentException">The spans differ in length, or clusters are given without text or point outside it.</exception>
    public static GlyphRun Create(Font font, ReadOnlySpan<ushort> glyphs, ReadOnlySpan<Vector2> positions, string? text = null, ReadOnlySpan<int> clusters = default)
    {
        ArgumentNullException.ThrowIfNull(font.Typeface, nameof(font));
        if (glyphs.Length != positions.Length)
        {
            throw new ArgumentException("There must be one position per glyph.", nameof(positions));
        }

        if (text is null)
        {
            if (!clusters.IsEmpty)
            {
                throw new ArgumentException("Clusters need the text they index.", nameof(clusters));
            }
        }
        else if (!clusters.IsEmpty)
        {
            if (clusters.Length != glyphs.Length)
            {
                throw new ArgumentException("There must be one cluster per glyph.", nameof(clusters));
            }

            foreach (int c in clusters)
            {
                if ((uint)c > (uint)text.Length)
                {
                    throw new ArgumentException("A cluster points outside the text.", nameof(clusters));
                }
            }
        }

        return new GlyphRun(font, glyphs.ToArray(), positions.ToArray(), text, clusters.IsEmpty ? null : clusters.ToArray());
    }

    private TextBounds ComputeBounds()
    {
        TextBounds bounds = TextBounds.Empty;
        for (int i = 0; i < _glyphs.Length; i++)
        {
            GlyphExtents e = Font.GetGlyphExtents(_glyphs[i]);
            if (!e.IsEmpty)
            {
                Vector2 p = _positions[i];
                bounds = bounds.Union(new TextBounds(p.X + e.XMin, p.Y - e.YMax, p.X + e.XMax, p.Y - e.YMin));
            }
        }

        return bounds;
    }
}

/// <summary>A box in canvas units with y down, such as the ink of a <see cref="GlyphRun"/> relative to its origin.</summary>
/// <param name="Left">The left edge.</param>
/// <param name="Top">The top edge.</param>
/// <param name="Right">The right edge.</param>
/// <param name="Bottom">The bottom edge.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="measure"/>
/// </example>
public readonly record struct TextBounds(float Left, float Top, float Right, float Bottom)
{
    /// <summary>Gets the empty box.</summary>
    public static TextBounds Empty => default;

    /// <summary>Gets the width.</summary>
    public float Width => Right - Left;

    /// <summary>Gets the height.</summary>
    public float Height => Bottom - Top;

    /// <summary>Gets whether the box has no area.</summary>
    public bool IsEmpty => Right <= Left || Bottom <= Top;

    /// <summary>Returns the smallest box holding this box and <paramref name="other"/>; an empty box adds nothing.</summary>
    /// <param name="other">The other box.</param>
    /// <returns>The union.</returns>
    public TextBounds Union(TextBounds other)
    {
        if (other.IsEmpty)
        {
            return this;
        }

        if (IsEmpty)
        {
            return other;
        }

        return new TextBounds(MathF.Min(Left, other.Left), MathF.Min(Top, other.Top), MathF.Max(Right, other.Right), MathF.Max(Bottom, other.Bottom));
    }

    /// <summary>Returns the box moved by an offset.</summary>
    /// <param name="offset">The offset.</param>
    /// <returns>The moved box.</returns>
    public TextBounds Offset(Vector2 offset) => new(Left + offset.X, Top + offset.Y, Right + offset.X, Bottom + offset.Y);
}

/// <summary>The size of text as <see cref="Font.MeasureText(string)"/> lays it out, in canvas units.</summary>
/// <param name="Advance">The widest line's advance: where the pen ends on that line.</param>
/// <param name="InkBounds">The exact box around the ink, relative to the first baseline's left end, y down.</param>
/// <param name="LineCount">The number of lines.</param>
/// <param name="Ascent">The font's ascent, positive.</param>
/// <param name="Descent">The font's descent, negative.</param>
/// <param name="LineHeight">The distance from one baseline to the next.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="measure"/>
/// </example>
public readonly record struct TextMeasure(float Advance, TextBounds InkBounds, int LineCount, float Ascent, float Descent, float LineHeight)
{
    /// <summary>Gets the height of the line boxes: the first line's ascent to the last line's descent.</summary>
    public float Height => Ascent - Descent + ((LineCount - 1) * LineHeight);
}
