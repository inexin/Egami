using System.Numerics;

using Egami.Fonts;

namespace Egami.Drawing;

public abstract partial class Canvas
{
    /// <summary>
    /// Draws a line of text: <see cref="Font.Shape(string)"/> turns it into glyphs (character map, pair kerning, newlines), then
    /// <see cref="DrawGlyphRun(GlyphRun, Vector2, Paint)"/> draws them. There is no wrapping, alignment or font fallback; a
    /// character the face lacks draws as its .notdef glyph.
    /// </summary>
    /// <param name="text">The text; empty text draws nothing.</param>
    /// <param name="origin">The left end of the first line's alphabetic baseline, in local coordinates.</param>
    /// <param name="font">The face and size, the size in local units per em.</param>
    /// <param name="paint">The paint.</param>
    /// <exception cref="ArgumentException"><paramref name="font"/> has no typeface, or <paramref name="origin"/> is not finite.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="caption"/>
    /// </example>
    public void DrawText(string text, Vector2 origin, Font font, Paint paint)
    {
        ArgumentNullException.ThrowIfNull(text);
        CheckFont(font);
        Check(paint);
        FinitePoint(origin, nameof(origin));
        if (text.Length == 0)
        {
            return;
        }

        GlyphRun run = font.Shape(text);
        if (run.Count > 0)
        {
            OnDrawGlyphRun(run, origin, paint);
        }
    }

    /// <summary>
    /// Draws text along a path by SVG's textPath rules, through <see cref="FontExtensions.ShapeOnPath(Font, string, Path, TextOnPathOptions?)"/>
    /// and <see cref="DrawGlyphRun(GlyphRun, Vector2, Paint)"/>: each glyph's midpoint sits on the path, turned to its direction, and
    /// glyphs past either end are left out. The result is a glyph run, so backends that write text keep it as text.
    /// </summary>
    /// <param name="text">The text; newlines draw nothing.</param>
    /// <param name="path">The path, in local coordinates.</param>
    /// <param name="font">The face and size.</param>
    /// <param name="paint">The paint.</param>
    /// <param name="options">Where the text starts and sits, or <see langword="null"/> for the defaults.</param>
    /// <exception cref="ArgumentException"><paramref name="font"/> has no typeface.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="path"/>
    /// </example>
    public void DrawTextOnPath(string text, Path path, Font font, Paint paint, TextOnPathOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(path);
        CheckFont(font);
        Check(paint);
        if (text.Length == 0)
        {
            return;
        }

        GlyphRun run = font.ShapeOnPath(text, path, options);
        if (run.Count > 0)
        {
            OnDrawGlyphRun(run, Vector2.Zero, paint);
        }
    }

    /// <summary>Draws positioned glyphs, such as a run from <see cref="Font.Shape(string)"/> or a layout engine.</summary>
    /// <param name="run">The glyphs.</param>
    /// <param name="origin">Where the run's origin goes, in local coordinates.</param>
    /// <param name="paint">The paint.</param>
    /// <exception cref="ArgumentException"><paramref name="origin"/> is not finite.</exception>
    public void DrawGlyphRun(GlyphRun run, Vector2 origin, Paint paint)
    {
        ArgumentNullException.ThrowIfNull(run);
        Check(paint);
        FinitePoint(origin, nameof(origin));
        if (run.Count > 0)
        {
            OnDrawGlyphRun(run, origin, paint);
        }
    }

    /// <summary>
    /// Draws a glyph run. The default turns the glyphs into one path, with the font's synthetic bold and italic, and calls
    /// <see cref="OnDrawPath(Path, Stroke?, Paint)"/>, aliased for <see cref="FontEdging.Alias"/>, so every backend draws text; a
    /// backend with text of its own, such as a PDF page writing text operators, overrides it and can use
    /// <see cref="GlyphRun.Text"/> and <see cref="GlyphRun.Clusters"/> to keep the text searchable.
    /// </summary>
    /// <param name="run">The glyphs, never empty.</param>
    /// <param name="origin">Where the run's origin goes, in local coordinates.</param>
    /// <param name="paint">The paint.</param>
    protected virtual void OnDrawGlyphRun(GlyphRun run, Vector2 origin, Paint paint)
    {
        Path path = run.ToPath(origin);
        if (!path.IsEmpty)
        {
            OnDrawPath(path, null, run.Font.Edging == FontEdging.Alias && paint.Antialias ? paint with { Antialias = false } : paint);
        }
    }

    private static void CheckFont(Font font)
    {
        if (font.Typeface is null)
        {
            throw new ArgumentException("The font has no typeface; create it with a constructor, not default.", nameof(font));
        }
    }
}
