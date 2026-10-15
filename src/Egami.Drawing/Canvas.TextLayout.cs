using System.Numerics;

using Egami.Fonts;
using Egami.Fonts.Layout;
using Egami.Pixels;

namespace Egami.Drawing;

/// <summary>Where <see cref="Canvas.DrawText(string, RectF, ParagraphStyle, VerticalAlign)"/> puts a paragraph across its lines, in a
/// rectangle taller (or, for vertical lines, wider) than the lines.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/ParagraphLayout.cs" region="draw"/>
/// </example>
public enum VerticalAlign
{
    /// <summary>At the start of the block: the top, or for vertical lines the right side (<see cref="WritingMode.VerticalRl"/>) or the left (<see cref="WritingMode.VerticalLr"/>).</summary>
    Top = 0,

    /// <summary>In the middle.</summary>
    Center = 1,

    /// <summary>At the end of the block: the bottom, or the far side for vertical lines.</summary>
    Bottom = 2,
}

/// <summary>Draws a placeholder's content where <see cref="Canvas.DrawTextLayout"/> left room for it.</summary>
/// <param name="canvas">The canvas being drawn on.</param>
/// <param name="index">The placeholder's index in the rich text.</param>
/// <param name="box">The placeholder's box, in the canvas's local coordinates.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/ParagraphLayout.cs" region="draw"/>
/// </example>
public delegate void PlaceholderPainter(Canvas canvas, int index, RectF box);

public abstract partial class Canvas
{
    /// <summary>
    /// Draws a laid-out paragraph with its top-left corner at <paramref name="origin"/>, line by line in CSS's order: text shadows
    /// (glyphs and decorations again, offset and blurred), run backgrounds, underlines and overlines, glyphs and placeholders,
    /// then line-throughs. Each run uses its style's <see cref="TextStyle.Paint"/> or <see cref="TextStyle.Color"/> (black when
    /// neither is set), each decoration its own paint or color. Lines outside <paramref name="visibleRect"/> and the clip are
    /// skipped. An ellipsis is drawn as marked content whose ActualText is the text it stands for on its line, and a soft
    /// hyphen's hyphen as one whose ActualText is the soft hyphen, so text extracted from a PDF stays the paragraph's.
    /// </summary>
    /// <param name="layout">The layout.</param>
    /// <param name="origin">Where the layout's top-left corner goes, in local coordinates.</param>
    /// <param name="visibleRect">The part worth drawing, in local coordinates, or <see langword="null"/> for the clip's bounds alone.</param>
    /// <param name="placeholderPainter">Draws each placeholder, or <see langword="null"/> to leave them empty.</param>
    /// <exception cref="ArgumentNullException"><paramref name="layout"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="origin"/> is not finite, or a style's paint is not an Egami.Drawing
    /// <see cref="Paint"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/ParagraphLayout.cs" region="draw"/>
    /// </example>
    public void DrawTextLayout(TextLayout layout, Vector2 origin, RectF? visibleRect = null, PlaceholderPainter? placeholderPainter = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        FinitePoint(origin, nameof(origin));
        Paragraph paragraph = layout.Paragraph;
        bool vertical = paragraph.Style.WritingMode != WritingMode.HorizontalTb;
        ILookup<int, DecorationShape> decorations = layout.Decorations.ToLookup(d => d.LineIndex);
        for (int li = 0; li < layout.Lines.Count; li++)
        {
            TextLine line = layout.Lines[li];
            float margin = line.Height + ShadowReach(paragraph, line);
            RectF band = vertical
                ? RectF.FromEdges(origin.X + line.Top - margin, origin.Y + line.Left - margin, origin.X + line.Bottom + margin, origin.Y + line.Left + line.Width + margin)
                : RectF.FromEdges(origin.X + line.Left - margin, origin.Y + line.Top - margin, origin.X + line.Left + line.Width + margin, origin.Y + line.Bottom + margin);
            if (QuickReject(band) || (visibleRect is RectF visible && !visible.IntersectsWith(band)))
            {
                continue;
            }

            DrawLine(layout, li, origin, vertical, decorations[li], placeholderPainter);
        }
    }

    /// <summary>
    /// Lays text out to the width of <paramref name="rect"/> (its height for vertical lines) and draws it there, placed across the
    /// lines by <paramref name="verticalAlign"/>: the same as <see cref="Paragraph.Layout(float)"/> and
    /// <see cref="DrawTextLayout"/>. Nothing is clipped; lines past the rectangle are drawn unless the style limits them.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="rect">The rectangle, in local coordinates.</param>
    /// <param name="style">The paragraph's style.</param>
    /// <param name="verticalAlign">Where the lines go across the rectangle.</param>
    /// <returns>The layout drawn, for hit testing and measuring.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> or <paramref name="style"/> is null.</exception>
    /// <exception cref="ArgumentException">The text has no font families, or a style's paint is not an Egami.Drawing <see cref="Paint"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/ParagraphLayout.cs" region="draw"/>
    /// </example>
    public TextLayout DrawText(string text, RectF rect, ParagraphStyle style, VerticalAlign verticalAlign = VerticalAlign.Top)
    {
        ArgumentNullException.ThrowIfNull(text);
        return DrawText(new RichText(text), rect, style, verticalAlign);
    }

    /// <summary>
    /// Lays rich text out to the width of <paramref name="rect"/> (its height for vertical lines) and draws it there, placed across
    /// the lines by <paramref name="verticalAlign"/>: the same as <see cref="Paragraph.Layout(float)"/> and
    /// <see cref="DrawTextLayout"/>. Nothing is clipped; lines past the rectangle are drawn unless the style limits them.
    /// </summary>
    /// <param name="text">The text with its spans and placeholders.</param>
    /// <param name="rect">The rectangle, in local coordinates.</param>
    /// <param name="style">The paragraph's style.</param>
    /// <param name="verticalAlign">Where the lines go across the rectangle.</param>
    /// <returns>The layout drawn, for hit testing and measuring.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> or <paramref name="style"/> is null.</exception>
    /// <exception cref="ArgumentException">Some text has no font families, or a style's paint is not an Egami.Drawing <see cref="Paint"/>.</exception>
    public TextLayout DrawText(RichText text, RectF rect, ParagraphStyle style, VerticalAlign verticalAlign = VerticalAlign.Top)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(style);
        bool vertical = style.WritingMode != WritingMode.HorizontalTb;
        TextLayout layout = Paragraph.Create(text, style).Layout(vertical ? rect.Height : rect.Width);
        float factor = verticalAlign switch
        {
            VerticalAlign.Center => 0.5f,
            VerticalAlign.Bottom => 1,
            _ => 0,
        };
        Vector2 origin;
        if (vertical)
        {
            // The block runs right to left in VerticalRl: Top puts the lines at the right edge.
            float slack = rect.Width - layout.Width;
            origin = new Vector2(rect.X + (style.WritingMode == WritingMode.VerticalRl ? slack * (1 - factor) : slack * factor), rect.Y);
        }
        else
        {
            origin = new Vector2(rect.X, rect.Y + ((rect.Height - layout.Height) * factor));
        }

        DrawTextLayout(layout, origin, null, null);
        return layout;
    }

    private void DrawLine(TextLayout layout, int li, Vector2 origin, bool vertical, IEnumerable<DecorationShape> decorations, PlaceholderPainter? placeholderPainter)
    {
        Paragraph paragraph = layout.Paragraph;
        TextLine line = layout.Lines[li];
        DecorationShape[] shapes = [.. decorations];

        // Shadows: everything a style draws, again, offset and blurred, the first shadow on top as CSS stacks them.
        foreach (LayoutRun run in line.Runs)
        {
            if (run.Glyphs is GlyphRun glyphs && paragraph.GetStyle(run.StyleIndex).Shadows is { Count: > 0 } shadows)
            {
                for (int s = shadows.Count - 1; s >= 0; s--)
                {
                    OnDrawGlyphRun(glyphs, origin + shadows[s].Offset, ShadowPaint(shadows[s]));
                }
            }
        }

        foreach (DecorationShape shape in shapes)
        {
            if (paragraph.GetStyle(shape.StyleIndex).Shadows is { Count: > 0 } shadows)
            {
                for (int s = shadows.Count - 1; s >= 0; s--)
                {
                    FillDecoration(shape, origin + shadows[s].Offset, ShadowPaint(shadows[s]));
                }
            }
        }

        // Backgrounds: each run's piece of the line box.
        foreach (LayoutRun run in line.Runs)
        {
            TextStyle style = paragraph.GetStyle(run.StyleIndex);
            if ((style.BackgroundPaint is not null || style.Background is not null) && run.Advance > 0)
            {
                RectF box = vertical
                    ? RectF.FromEdges(origin.X + line.Top, origin.Y + run.X, origin.X + line.Bottom, origin.Y + run.X + run.Advance)
                    : RectF.FromEdges(origin.X + run.X, origin.Y + line.Top, origin.X + run.X + run.Advance, origin.Y + line.Bottom);
                FillRect(box, PaintOf(style.BackgroundPaint, style.Background ?? default));
            }
        }

        foreach (DecorationShape shape in shapes.Where(s => s.Line != TextDecorationLines.LineThrough))
        {
            FillDecoration(shape, origin, DecorationPaint(shape));
        }

        foreach (LayoutRun run in line.Runs)
        {
            if (run.Kind == LayoutRunKind.Placeholder)
            {
                if (placeholderPainter is not null && run.Placeholder < layout.PlaceholderRects.Count)
                {
                    TextBounds box = layout.PlaceholderRects[run.Placeholder];
                    placeholderPainter(this, run.Placeholder, RectF.FromEdges(origin.X + box.Left, origin.Y + box.Top, origin.X + box.Right, origin.Y + box.Bottom));
                }

                continue;
            }

            if (run.Glyphs is not GlyphRun glyphs || glyphs.Count == 0)
            {
                continue;
            }

            TextStyle style = paragraph.GetStyle(run.StyleIndex);
            Paint paint = PaintOf(style.Paint, style.Color ?? new Rgba8(0, 0, 0, 255));
            if (line.Ellipsized && run.Length == 0)
            {
                // The ellipsis stands for the rest of its line's text.
                int end = Math.Max(run.Start, line.Start + line.Length);
                BeginMarkedContent(new MarkedContent("Span") { ActualText = paragraph.Text[run.Start..end] });
                OnDrawGlyphRun(glyphs, origin, paint);
                EndMarkedContent();
            }
            else if (SoftHyphenGlyphs(paragraph.Text, line, glyphs) is List<int> hyphen)
            {
                List<int> rest = [.. Enumerable.Range(0, glyphs.Count).Except(hyphen)];
                if (rest.Count > 0)
                {
                    OnDrawGlyphRun(Subrun(glyphs, rest), origin, paint);
                }

                BeginMarkedContent(new MarkedContent("Span") { ActualText = "\u00AD" });
                OnDrawGlyphRun(Subrun(glyphs, hyphen), origin, paint);
                EndMarkedContent();
            }
            else
            {
                OnDrawGlyphRun(glyphs, origin, paint);
            }
        }

        foreach (DecorationShape shape in shapes.Where(s => s.Line == TextDecorationLines.LineThrough))
        {
            FillDecoration(shape, origin, DecorationPaint(shape));
        }
    }

    /// <summary>The glyphs a line ending at a soft hyphen added for its hyphen: those of the soft hyphen's cluster.</summary>
    private static List<int>? SoftHyphenGlyphs(string text, TextLine line, GlyphRun glyphs)
    {
        int end = line.Start + line.Length;
        if (end == 0 || text[end - 1] != '\u00AD' || glyphs.Clusters.IsEmpty)
        {
            return null;
        }

        List<int> indices = [];
        for (int g = 0; g < glyphs.Count; g++)
        {
            if (glyphs.Clusters[g] == end - 1)
            {
                indices.Add(g);
            }
        }

        return indices.Count > 0 ? indices : null;
    }

    private void FillDecoration(DecorationShape shape, Vector2 origin, Paint paint)
    {
        Path path = shape.ToPath(origin);
        if (!path.IsEmpty)
        {
            OnDrawPath(path, null, paint);
        }
    }

    private static Paint DecorationPaint(DecorationShape shape) => PaintOf(shape.Paint, shape.Color);

    /// <summary>A CSS text shadow's paint: its color, blurred with a standard deviation of half the blur radius.</summary>
    private static SolidPaint ShadowPaint(TextShadow shadow) =>
        new SolidPaint(shadow.Color) { Blur = shadow.BlurRadius > 0 ? new MaskBlur(MathF.Min(shadow.BlurRadius / 2, MaskBlur.MaxSigma)) : null };

    private static Paint PaintOf(ITextPaint? paint, Rgba8 color) => paint switch
    {
        null => new SolidPaint(color),
        Paint p => p,
        _ => throw new ArgumentException($"A text style's paint is a {paint.GetType().Name}; drawing needs an Egami.Drawing Paint.", nameof(paint)),
    };

    /// <summary>How far a line's shadows reach past its glyphs: offsets plus three standard deviations of blur.</summary>
    private static float ShadowReach(Paragraph paragraph, TextLine line)
    {
        float reach = 0;
        foreach (LayoutRun run in line.Runs)
        {
            if (paragraph.GetStyle(run.StyleIndex).Shadows is { Count: > 0 } shadows)
            {
                foreach (TextShadow s in shadows)
                {
                    reach = MathF.Max(reach, MathF.Max(MathF.Abs(s.Offset.X), MathF.Abs(s.Offset.Y)) + (1.5f * s.BlurRadius));
                }
            }
        }

        return reach;
    }
}
