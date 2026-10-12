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
    private readonly GlyphTransform[]? _transforms;
    private readonly int[]? _clusters;
    private TextBounds? _bounds;

    internal GlyphRun(Font font, ushort[] glyphs, Vector2[] positions, string? text, int[]? clusters, GlyphTransform[]? transforms = null)
    {
        Font = font;
        _glyphs = glyphs;
        _positions = positions;
        Text = text;
        _clusters = clusters;
        _transforms = transforms;
    }

    /// <summary>Gets the font every glyph is drawn in.</summary>
    public Font Font { get; }

    /// <summary>Gets the number of glyphs.</summary>
    public int Count => _glyphs.Length;

    /// <summary>Gets the glyph ids.</summary>
    public ReadOnlySpan<ushort> Glyphs => _glyphs;

    /// <summary>
    /// Gets each glyph's origin on its baseline, relative to the run's origin, y down; for a run with <see cref="Transforms"/>, the
    /// translation of each glyph's transform.
    /// </summary>
    public ReadOnlySpan<Vector2> Positions => _positions;

    /// <summary>
    /// Gets each glyph's rotation, scale and origin, which replace <see cref="Positions"/> when present (a run from
    /// <see cref="CreateTransformed"/>, such as text on a path); empty for a run of plain positions.
    /// </summary>
    public ReadOnlySpan<GlyphTransform> Transforms => _transforms;

    /// <summary>Gets the source text, or <see langword="null"/> when the run was built from glyphs alone.</summary>
    public string? Text { get; }

    /// <summary>Gets the index in <see cref="Text"/> each glyph came from; empty when there is no text.</summary>
    public ReadOnlySpan<int> Clusters => _clusters;

    /// <summary>Gets the end of the last glyph's advance on its line: the pen position after the run (for a transformed run, along x from each glyph's origin).</summary>
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

        CheckClusters(text, clusters, glyphs.Length);
        return new GlyphRun(font, glyphs.ToArray(), positions.ToArray(), text, clusters.IsEmpty ? null : clusters.ToArray());
    }

    /// <summary>
    /// Creates a run whose glyphs each have their own rotation, uniform scale and origin, as Skia's RSXform runs: text on a path,
    /// or labels around a dial. Glyphs that only move are drawn as any run's; turned or scaled ones fill as paths.
    /// </summary>
    /// <param name="font">The font.</param>
    /// <param name="glyphs">The glyph ids.</param>
    /// <param name="transforms">One transform per glyph, relative to the run's origin, y down.</param>
    /// <param name="text">The source text, or <see langword="null"/>.</param>
    /// <param name="clusters">The index in <paramref name="text"/> of each glyph; empty when <paramref name="text"/> is null.</param>
    /// <returns>The run, holding copies of the spans.</returns>
    /// <exception cref="ArgumentException">The spans differ in length, a transform is not finite, or clusters are given without text or point outside it.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="path"/>
    /// </example>
    public static GlyphRun CreateTransformed(Font font, ReadOnlySpan<ushort> glyphs, ReadOnlySpan<GlyphTransform> transforms, string? text = null, ReadOnlySpan<int> clusters = default)
    {
        ArgumentNullException.ThrowIfNull(font.Typeface, nameof(font));
        if (glyphs.Length != transforms.Length)
        {
            throw new ArgumentException("There must be one transform per glyph.", nameof(transforms));
        }

        var positions = new Vector2[transforms.Length];
        for (int i = 0; i < transforms.Length; i++)
        {
            GlyphTransform t = transforms[i];
            if (!float.IsFinite(t.ScaleCos) || !float.IsFinite(t.ScaleSin) || !float.IsFinite(t.Tx) || !float.IsFinite(t.Ty))
            {
                throw new ArgumentException("A transform is not finite.", nameof(transforms));
            }

            positions[i] = new Vector2(t.Tx, t.Ty);
        }

        CheckClusters(text, clusters, glyphs.Length);
        return new GlyphRun(font, glyphs.ToArray(), positions, text, clusters.IsEmpty ? null : clusters.ToArray(), transforms.ToArray());
    }

    private static void CheckClusters(string? text, ReadOnlySpan<int> clusters, int count)
    {
        if (text is null)
        {
            if (!clusters.IsEmpty)
            {
                throw new ArgumentException("Clusters need the text they index.", nameof(clusters));
            }
        }
        else if (!clusters.IsEmpty)
        {
            if (clusters.Length != count)
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
    }

    private TextBounds ComputeBounds()
    {
        TextBounds bounds = TextBounds.Empty;
        for (int i = 0; i < _glyphs.Length; i++)
        {
            GlyphExtents e = Font.GetGlyphExtents(_glyphs[i]);
            if (e.IsEmpty)
            {
                continue;
            }

            if (_transforms is null)
            {
                Vector2 p = _positions[i];
                bounds = bounds.Union(new TextBounds(p.X + e.XMin, p.Y - e.YMax, p.X + e.XMax, p.Y - e.YMin));
                continue;
            }

            // The box of the glyph's box turned: its four corners through the transform, y down.
            Matrix3x2 m = _transforms[i].ToMatrix();
            Vector2 a = Vector2.Transform(new Vector2(e.XMin, -e.YMax), m), b = Vector2.Transform(new Vector2(e.XMax, -e.YMax), m);
            Vector2 c = Vector2.Transform(new Vector2(e.XMin, -e.YMin), m), d = Vector2.Transform(new Vector2(e.XMax, -e.YMin), m);
            Vector2 min = Vector2.Min(Vector2.Min(a, b), Vector2.Min(c, d)), max = Vector2.Max(Vector2.Max(a, b), Vector2.Max(c, d));
            bounds = bounds.Union(new TextBounds(min.X, min.Y, max.X, max.Y));
        }

        return bounds;
    }
}

/// <summary>
/// A glyph's placement in a transformed <see cref="GlyphRun"/>: a rotation and uniform scale, then its origin, as Skia's RSXform.
/// A point (x, y) of the glyph, y down, goes to (ScaleCos x - ScaleSin y + Tx, ScaleSin x + ScaleCos y + Ty).
/// </summary>
/// <param name="ScaleCos">The scale times the cosine of the turn; 1 for an upright glyph at the font's size.</param>
/// <param name="ScaleSin">The scale times the sine of the turn (clockwise on a y-down canvas); 0 for an upright glyph.</param>
/// <param name="Tx">The glyph origin's x, relative to the run's origin.</param>
/// <param name="Ty">The glyph origin's y, relative to the run's origin, y down.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="path"/>
/// </example>
public readonly record struct GlyphTransform(float ScaleCos, float ScaleSin, float Tx, float Ty)
{
    /// <summary>Creates a placement that scales and turns a glyph about its origin and puts the origin at a position.</summary>
    /// <param name="scale">The scale; 1 for the font's size.</param>
    /// <param name="degrees">The clockwise turn on a y-down canvas.</param>
    /// <param name="origin">Where the glyph's origin goes, relative to the run's origin.</param>
    /// <returns>The placement.</returns>
    public static GlyphTransform FromRotationScale(float scale, float degrees, Vector2 origin)
    {
        float radians = degrees * MathF.PI / 180;
        return new GlyphTransform(scale * MathF.Cos(radians), scale * MathF.Sin(radians), origin.X, origin.Y);
    }

    /// <summary>Gets whether the glyph is only moved, not turned or scaled.</summary>
    public bool IsTranslation => ScaleCos == 1 && ScaleSin == 0;

    /// <summary>Returns the transform as a matrix from the glyph's y-down coordinates to the run's.</summary>
    /// <returns>The matrix.</returns>
    public Matrix3x2 ToMatrix() => new(ScaleCos, ScaleSin, -ScaleSin, ScaleCos, Tx, Ty);
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
