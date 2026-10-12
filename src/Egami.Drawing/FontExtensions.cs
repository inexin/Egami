using System.Numerics;

using Egami.Fonts;

namespace Egami.Drawing;

/// <summary>Glyphs and text as <see cref="Path"/>s in canvas units, y down, for clipping, stroking or measuring them as shapes.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="paths"/>
/// </example>
public static class FontExtensions
{
    /// <summary>Gets a glyph's outline at the font's size, with the glyph's origin at (0, 0) on the baseline, y down.</summary>
    /// <param name="font">The font.</param>
    /// <param name="glyph">The glyph id.</param>
    /// <returns>The outline; empty for a glyph without ink.</returns>
    public static Path GetGlyphPath(this Font font, ushort glyph)
    {
        Typeface face = font.Typeface ?? throw new ArgumentException("The font has no typeface.", nameof(font));
        var builder = new PathBuilder();
        var sink = new PathSink(builder, font.Scale, 0, 0);
        face.GetGlyphOutline(glyph, sink);
        return builder.ToPath();
    }

    /// <summary>Gets the outline of a line of text as <see cref="Canvas.DrawText(string, Vector2, Font, Paint)"/> lays it out.</summary>
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

    /// <summary>Gets the outline of every glyph of a run, placed at its position relative to <paramref name="origin"/>.</summary>
    /// <param name="run">The run.</param>
    /// <param name="origin">Where the run's origin goes.</param>
    /// <returns>The outline, in one path.</returns>
    public static Path ToPath(this GlyphRun run, Vector2 origin)
    {
        ArgumentNullException.ThrowIfNull(run);
        var builder = new PathBuilder();
        Font font = run.Font;
        ReadOnlySpan<ushort> glyphs = run.Glyphs;
        ReadOnlySpan<Vector2> positions = run.Positions;
        for (int i = 0; i < glyphs.Length; i++)
        {
            Vector2 at = origin + positions[i];
            font.Typeface.GetGlyphOutline(glyphs[i], new PathSink(builder, font.Scale, at.X, at.Y));
        }

        return builder.ToPath();
    }

    /// <summary>Converts text bounds to a rectangle.</summary>
    /// <param name="bounds">The bounds.</param>
    /// <returns>The rectangle.</returns>
    public static RectF ToRectF(this TextBounds bounds) => RectF.FromEdges(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);

    /// <summary>Scales font units, flips y and moves the origin while building a path.</summary>
    private sealed class PathSink(PathBuilder builder, float scale, float x, float y) : IGlyphOutlineSink
    {
        public void MoveTo(float px, float py) => builder.MoveTo(X(px), Y(py));

        public void LineTo(float px, float py) => builder.LineTo(X(px), Y(py));

        public void QuadTo(float cx, float cy, float px, float py) => builder.QuadTo(X(cx), Y(cy), X(px), Y(py));

        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float px, float py)
            => builder.CubicTo(X(c1x), Y(c1y), X(c2x), Y(c2y), X(px), Y(py));

        public void Close() => builder.Close();

        private float X(float v) => x + (v * scale);

        private float Y(float v) => y - (v * scale);
    }
}
