using System.Numerics;

using Egami.Fonts;
using Egami.Fonts.Layout;

namespace Egami.Drawing;

/// <summary>Glyphs and text as <see cref="Path"/>s in canvas units, y down, for clipping, stroking or measuring them as shapes.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="paths"/>
/// </example>
public static partial class FontExtensions
{
    /// <summary>
    /// Gets a glyph's outline at the font's size, with the glyph's origin at (0, 0) on the baseline, y down: slanted 14 degrees
    /// for synthetic italic and outset for synthetic bold when <see cref="Font.Synthesis"/> asks and <see cref="Font.AllowSynthesis"/>
    /// allows.
    /// </summary>
    /// <param name="font">The font.</param>
    /// <param name="glyph">The glyph id.</param>
    /// <returns>The outline; empty for a glyph without ink.</returns>
    public static Path GetGlyphPath(this Font font, ushort glyph)
    {
        if (font.Typeface is null)
        {
            throw new ArgumentException("The font has no typeface.", nameof(font));
        }

        var builder = new PathBuilder();
        AddGlyph(builder, font, font.EffectiveTypeface, glyph, Vector2.Zero);
        return Synthesize(builder.ToPath(), font);
    }

    /// <summary>Gets the outline of a line of text as <see cref="Canvas.DrawText(string, Vector2, Font, Paint, Fonts.Shaping.ShapingOptions?)"/> lays it out.</summary>
    /// <param name="font">The font.</param>
    /// <param name="text">The text.</param>
    /// <param name="origin">The left end of the first baseline.</param>
    /// <returns>The outline of every glyph, in one path.</returns>
    public static Path GetTextPath(this Font font, string text, Vector2 origin)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (font.Typeface is null)
        {
            throw new ArgumentException("The font has no typeface.", nameof(font));
        }

        return font.Shape(text).ToPath(origin);
    }

    /// <summary>
    /// Gets the outline of every glyph of a run, placed at its position relative to <paramref name="origin"/>, with the font's
    /// synthetic bold and italic.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="origin">Where the run's origin goes.</param>
    /// <returns>The outline, in one path.</returns>
    public static Path ToPath(this GlyphRun run, Vector2 origin)
    {
        ArgumentNullException.ThrowIfNull(run);
        var builder = new PathBuilder();
        Font font = run.Font;
        Typeface face = font.EffectiveTypeface;
        ReadOnlySpan<ushort> glyphs = run.Glyphs;
        ReadOnlySpan<Vector2> positions = run.Positions;
        ReadOnlySpan<GlyphTransform> transforms = run.Transforms;
        if (transforms.IsEmpty)
        {
            for (int i = 0; i < glyphs.Length; i++)
            {
                AddGlyph(builder, font, face, glyphs[i], origin + positions[i]);
            }

            return Synthesize(builder.ToPath(), font);
        }

        // Each glyph's outline, synthesis included, through its own transform, then to the run's origin.
        Matrix3x2 toOrigin = Matrix3x2.CreateTranslation(origin);
        for (int i = 0; i < glyphs.Length; i++)
        {
            var glyph = new PathBuilder();
            AddGlyph(glyph, font, face, glyphs[i], Vector2.Zero);
            builder.AddPath(Synthesize(glyph.ToPath(), font), transforms[i].ToMatrix() * toOrigin);
        }

        return builder.ToPath();
    }

    // A glyph's outline at a position, slanted for synthetic italic about its own baseline origin.
    private static void AddGlyph(PathBuilder builder, Font font, Typeface face, ushort glyph, Vector2 at)
    {
        float skew = (font.AppliedSynthesis & FontSynthesis.Italic) != 0 ? Synthesis.Skew : 0;
        face.GetGlyphOutline(glyph, new PathSink(builder, font.Scale, at.X, at.Y, skew));
    }

    // Synthetic bold outsets the whole outline with round joins, by em/24 at 9 units per em down to em/32 at 36 and above.
    private static Path Synthesize(Path path, Font font)
        => (font.AppliedSynthesis & FontSynthesis.Bold) != 0 && !path.IsEmpty ? path.Inflate(Synthesis.BoldOutset(font.Size)) : path;

    /// <summary>Converts text bounds to a rectangle.</summary>
    /// <param name="bounds">The bounds.</param>
    /// <returns>The rectangle.</returns>
    public static RectF ToRectF(this TextBounds bounds) => RectF.FromEdges(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);

    /// <summary>
    /// Gets a laid-out paragraph as one path: every glyph run's outline with its font's synthetic bold and italic, then every
    /// decoration, the layout's top-left corner at <paramref name="origin"/>.
    /// </summary>
    /// <param name="layout">The layout.</param>
    /// <param name="origin">Where the layout's top-left corner goes.</param>
    /// <returns>The outline, in one path.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/ParagraphLayout.cs" region="draw"/>
    /// </example>
    public static Path ToPath(this TextLayout layout, Vector2 origin)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var builder = new PathBuilder();
        foreach (TextLine line in layout.Lines)
        {
            foreach (LayoutRun run in line.Runs)
            {
                if (run.Glyphs is GlyphRun glyphs && glyphs.Count > 0)
                {
                    builder.AddPath(glyphs.ToPath(origin));
                }
            }
        }

        foreach (DecorationShape shape in layout.Decorations)
        {
            shape.AddTo(new OffsetSink(builder, origin));
        }

        return builder.ToPath();
    }

    /// <summary>Gets a decoration line's outline, the layout's top-left corner at <paramref name="origin"/>.</summary>
    /// <param name="shape">The decoration.</param>
    /// <param name="origin">Where the layout's top-left corner goes.</param>
    /// <returns>The outline.</returns>
    public static Path ToPath(this DecorationShape shape, Vector2 origin)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var builder = new PathBuilder();
        shape.AddTo(new OffsetSink(builder, origin));
        return builder.ToPath();
    }

    /// <summary>Moves layout coordinates (y down already) to an origin while building a path.</summary>
    private sealed class OffsetSink(PathBuilder builder, Vector2 origin) : IGlyphOutlineSink
    {
        public void MoveTo(float x, float y) => builder.MoveTo(origin.X + x, origin.Y + y);

        public void LineTo(float x, float y) => builder.LineTo(origin.X + x, origin.Y + y);

        public void QuadTo(float cx, float cy, float x, float y) => builder.QuadTo(origin.X + cx, origin.Y + cy, origin.X + x, origin.Y + y);

        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
            => builder.CubicTo(origin.X + c1x, origin.Y + c1y, origin.X + c2x, origin.Y + c2y, origin.X + x, origin.Y + y);

        public void Close() => builder.Close();
    }

    /// <summary>Scales font units, slants, flips y and moves the origin while building a path.</summary>
    private sealed class PathSink(PathBuilder builder, float scale, float x, float y, float skew = 0) : IGlyphOutlineSink
    {
        public void MoveTo(float px, float py) => builder.MoveTo(X(px, py), Y(py));

        public void LineTo(float px, float py) => builder.LineTo(X(px, py), Y(py));

        public void QuadTo(float cx, float cy, float px, float py) => builder.QuadTo(X(cx, cy), Y(cy), X(px, py), Y(py));

        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float px, float py)
            => builder.CubicTo(X(c1x, c1y), Y(c1y), X(c2x, c2y), Y(c2y), X(px, py), Y(py));

        public void Close() => builder.Close();

        private float X(float vx, float vy) => x + ((vx + (skew * vy)) * scale);

        private float Y(float v) => y - (v * scale);
    }
}
