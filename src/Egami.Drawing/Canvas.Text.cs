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
    /// Draws a glyph run. The default turns the glyphs into one path and calls <see cref="OnDrawPath(Path, Stroke?, Paint)"/>, so
    /// every backend draws text; a backend with text of its own, such as a PDF page writing text operators, overrides it and can
    /// use <see cref="GlyphRun.Text"/> and <see cref="GlyphRun.Clusters"/> to keep the text searchable.
    /// </summary>
    /// <param name="run">The glyphs, never empty.</param>
    /// <param name="origin">Where the run's origin goes, in local coordinates.</param>
    /// <param name="paint">The paint.</param>
    protected virtual void OnDrawGlyphRun(GlyphRun run, Vector2 origin, Paint paint)
    {
        Path path = run.ToPath(origin);
        if (!path.IsEmpty)
        {
            OnDrawPath(path, null, paint);
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
