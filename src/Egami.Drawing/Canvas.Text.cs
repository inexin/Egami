using System.Numerics;

using Egami.Fonts;
using Egami.Fonts.Shaping;

namespace Egami.Drawing;

public abstract partial class Canvas
{
    /// <summary>
    /// Draws text: <see cref="Font.Shape(string, ShapingOptions?)"/> shapes it (ligatures, kerning, marks, the script rules, bidi
    /// order, one line per hard break), then <see cref="DrawGlyphRun(GlyphRun, Vector2, Paint)"/> draws the run. With fallback or
    /// span fonts in <paramref name="options"/>, the text is shaped by <see cref="TextShaper.Shape(string, Font, ShapingOptions?)"/>
    /// and drawn as <see cref="DrawShapedText(ShapedText, Vector2, Paint)"/> draws it. Nothing wraps; without fallback a character
    /// the face lacks draws as its .notdef glyph.
    /// </summary>
    /// <param name="text">The text; empty text draws nothing.</param>
    /// <param name="origin">The left end of the first line's alphabetic baseline, in local coordinates (for a vertical line, the
    /// first glyph's vertical origin).</param>
    /// <param name="font">The face and size, the size in local units per em.</param>
    /// <param name="paint">The paint.</param>
    /// <param name="options">The direction, language, script, features, fallback and spans, or <see langword="null"/> for
    /// <see cref="ShapingOptions.Default"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="font"/> has no typeface, or <paramref name="origin"/> is not finite.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="caption"/>
    /// </example>
    public void DrawText(string text, Vector2 origin, Font font, Paint paint, ShapingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        CheckFont(font);
        Check(paint);
        FinitePoint(origin, nameof(origin));
        if (text.Length == 0)
        {
            return;
        }

        ShapingOptions o = options ?? ShapingOptions.Default;
        if (o.Fallback.Families.Count > 0 || o.Fallback.Resolver is not null || (o.Spans?.Any(s => s.Font is not null) ?? false))
        {
            DrawShaped(TextShaper.Shape(text, font, o), origin, paint);
            return;
        }

        GlyphRun run = font.Shape(text, o);
        if (run.Count > 0)
        {
            OnDrawGlyphRun(run, origin, paint);
        }
    }

    /// <summary>
    /// Draws text shaped once by <see cref="TextShaper.Shape(string, Font, ShapingOptions?)"/>, so it can be measured, kept and drawn
    /// again without shaping it twice: each line one line height below the one before (a vertical line one line height to the
    /// left), its runs from left to right, each run a <see cref="GlyphRun"/> in its own font.
    /// </summary>
    /// <param name="text">The shaped text.</param>
    /// <param name="origin">The left end of the first line's alphabetic baseline, in local coordinates.</param>
    /// <param name="paint">The paint.</param>
    /// <exception cref="ArgumentException"><paramref name="origin"/> is not finite.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="shaped"/>
    /// </example>
    public void DrawShapedText(ShapedText text, Vector2 origin, Paint paint)
    {
        ArgumentNullException.ThrowIfNull(text);
        Check(paint);
        FinitePoint(origin, nameof(origin));
        DrawShaped(text, origin, paint);
    }

    private void DrawShaped(ShapedText text, Vector2 origin, Paint paint)
    {
        Vector2 line = origin;
        foreach (ShapedLine shapedLine in text.Lines)
        {
            Vector2 pen = line;
            foreach (ShapedRun run in shapedLine.Runs)
            {
                GlyphRun glyphs = run.ToGlyphRun(Vector2.Zero, text.Text);
                if (glyphs.Count > 0)
                {
                    OnDrawGlyphRun(glyphs, pen, paint);
                }

                pen += run.IsVertical ? new Vector2(0, run.Advance) : new Vector2(run.Advance, 0);
            }

            bool vertical = shapedLine.Runs.Count > 0 && shapedLine.Runs[0].IsVertical;
            line += vertical ? new Vector2(-shapedLine.LineHeight, 0) : new Vector2(0, shapedLine.LineHeight);
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

    /// <summary>Draws positioned glyphs, such as a run from <see cref="Font.Shape(string, ShapingOptions?)"/> or a layout engine.</summary>
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
        run = DrawRendererGlyphs(run, origin, paint);
        if (run.Count == 0)
        {
            return;
        }

        Font font = run.Font;
        Typeface face = font.EffectiveTypeface;
        if (font.ColorGlyphs && (face.ColorFormats & ~ColorGlyphFormats.Svg) != 0)
        {
            run = DrawColorGlyphs(run, origin, paint);
            if (run.Count == 0)
            {
                return;
            }
        }

        Path path = run.ToPath(origin);
        if (!path.IsEmpty)
        {
            OnDrawPath(path, null, font.Edging == FontEdging.Alias && paint.Antialias ? paint with { Antialias = false } : paint);
        }
    }

    /// <summary>
    /// Draws a run's color glyphs (COLR, sbix, CBDT) with canvas calls (clips, fills, gradients, layers and images) and returns the
    /// run of the glyphs left to draw as outlines.
    /// </summary>
    private protected GlyphRun DrawColorGlyphs(GlyphRun run, Vector2 origin, Paint paint)
    {
        Font font = run.Font;
        Typeface face = font.EffectiveTypeface;
        Matrix3x2 m = Matrix;
        float pixelsPerEm = font.Size * MathF.Sqrt(MathF.Abs(m.GetDeterminant()));
        ReadOnlySpan<ushort> glyphs = run.Glyphs;
        ReadOnlySpan<GlyphTransform> transforms = run.Transforms;
        List<int>? plain = null;
        for (int i = 0; i < glyphs.Length; i++)
        {
            if (face.HasColorGlyph(glyphs[i]) && face.TryGetColorGlyph(glyphs[i], font.Palette, out ColorGlyph? color, pixelsPerEm))
            {
                plain ??= [.. Enumerable.Range(0, i)];
                if (transforms.IsEmpty)
                {
                    ColorGlyphDrawing.Draw(this, font, face, color, origin + run.Positions[i], paint);
                }
                else
                {
                    ColorGlyphDrawing.Draw(this, font, face, color, origin, paint, transforms[i].ToMatrix());
                }
            }
            else
            {
                plain?.Add(i);
            }
        }

        return plain is null ? run : Subrun(run, plain);
    }

    /// <summary>Gets the renderer for color glyph formats this canvas does not draw itself; none on a canvas without options.</summary>
    private protected virtual IColorGlyphRenderer? ColorGlyphRenderer => null;

    /// <summary>
    /// Draws the glyphs of a run that <see cref="ColorGlyphRenderer"/> draws (those without a COLR version 1 glyph, which comes
    /// first) and returns the run of the rest.
    /// </summary>
    private protected GlyphRun DrawRendererGlyphs(GlyphRun run, Vector2 origin, Paint paint)
    {
        Font font = run.Font;
        Typeface face = font.EffectiveTypeface;
        if (ColorGlyphRenderer is not IColorGlyphRenderer renderer || !font.ColorGlyphs || (face.ColorFormats & renderer.Formats) == 0)
        {
            return run;
        }

        ReadOnlySpan<ushort> glyphs = run.Glyphs;
        ReadOnlySpan<GlyphTransform> transforms = run.Transforms;
        bool colrV1 = (face.ColorFormats & ColorGlyphFormats.ColrV1) != 0;
        List<int>? rest = null;
        for (int i = 0; i < glyphs.Length; i++)
        {
            bool first = colrV1 && face.HasColorGlyph(glyphs[i]) && face.TryGetColorGlyph(glyphs[i], font.Palette, out ColorGlyph? color) && color.Format == ColorGlyphFormats.ColrV1;
            Matrix3x2 transform = transforms.IsEmpty ? Matrix3x2.CreateTranslation(origin + run.Positions[i]) : transforms[i].ToMatrix() * Matrix3x2.CreateTranslation(origin);
            if (!first && renderer.TryDraw(this, font, glyphs[i], transform, paint))
            {
                rest ??= [.. Enumerable.Range(0, i)];
            }
            else
            {
                rest?.Add(i);
            }
        }

        return rest is null ? run : Subrun(run, rest);
    }

    /// <summary>The glyphs of a run at the given indices, with their positions or transforms, text and clusters.</summary>
    private protected static GlyphRun Subrun(GlyphRun run, List<int> indices)
    {
        ushort[] glyphs = [.. indices.Select(i => run.Glyphs[i])];
        int[] clusters = run.Clusters.IsEmpty ? [] : [.. indices.Select(i => run.Clusters[i])];
        if (!run.Transforms.IsEmpty)
        {
            GlyphTransform[] transforms = [.. indices.Select(i => run.Transforms[i])];
            return GlyphRun.CreateTransformed(run.Font, glyphs, transforms, run.Text, clusters);
        }

        Vector2[] positions = [.. indices.Select(i => run.Positions[i])];
        return GlyphRun.Create(run.Font, glyphs, positions, run.Text, clusters);
    }

    private static void CheckFont(Font font)
    {
        if (font.Typeface is null)
        {
            throw new ArgumentException("The font has no typeface; create it with a constructor, not default.", nameof(font));
        }
    }
}
