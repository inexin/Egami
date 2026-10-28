using System.Numerics;

using Egami.Drawing;
using Egami.SystemDrawing.Internal;
using Egami.SystemDrawing.Text;

using EgamiFonts = Egami.Fonts;

namespace Egami.SystemDrawing;

/// <summary>DrawString, MeasureString and MeasureCharacterRanges, laid out as GDI+ lays text out.</summary>
public sealed partial class Graphics
{
    /// <summary>The outline of laid-out text in world coordinates, with its underlines and strikeouts, from a layout box's corner.</summary>
    internal static Path TextOutline(GdiTextLayout layout, Font font, Vector2 corner, bool glyphs = true)
    {
        PathBuilder builder = new();
        Matrix3x2 place = layout.Turn * Matrix3x2.CreateTranslation(corner);
        float top = layout.Top;
        EgamiFonts.FontExtents extents = layout.Font.Extents;
        float em = layout.Font.Size;
        float underlineY = extents.UnderlineThickness > 0 ? -extents.UnderlinePosition : em / 10;
        float underlineH = extents.UnderlineThickness > 0 ? extents.UnderlineThickness : em / 14;
        float strikeY = extents.StrikeoutSize > 0 ? -extents.StrikeoutPosition : -em / 4;
        float strikeH = extents.StrikeoutSize > 0 ? extents.StrikeoutSize : em / 14;
        foreach (GdiTextLayout.Line line in layout.Lines)
        {
            if (glyphs && layout.Run(line, top) is EgamiFonts.GlyphRun run)
            {
                builder.AddPath(run.ToPath(Vector2.Zero), place);
            }

            float baseline = top + line.Top + layout.Ascent;
            if (line.Width > 0 && font.Underline)
            {
                builder.AddPath(Path.Rect(new RectF(line.X, baseline + underlineY, line.Width, underlineH)), place);
            }

            if (line.Width > 0 && font.Strikeout)
            {
                builder.AddPath(Path.Rect(new RectF(line.X, baseline + strikeY, line.Width, strikeH)), place);
            }
        }

        foreach ((GdiTextLayout.Line line, float x0, float x1) in layout.Hotkeys())
        {
            float baseline = top + line.Top + layout.Ascent;
            builder.AddPath(Path.Rect(new RectF(x0, baseline + underlineY, x1 - x0, underlineH)), place);
        }

        return builder.ToPath(FillRule.NonZero);
    }

    /// <summary>Draws text in a layout rectangle (zero width or height for none) with a brush.</summary>
    internal void DrawStringCore(ReadOnlySpan<char> s, Font font, Brush brush, RectangleF layoutRectangle, StringFormat? format)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(brush);
        Alive();
        if (s.IsEmpty)
        {
            return;
        }

        format ??= StringFormat.GenericDefault;
        bool clearType = ClearTypes(brush, format);
        GdiTextLayout layout = GdiTextLayout.Create(s, font, font.EmIn(this), layoutRectangle.Size, format, clearType ? TextTransform.M11 : 0);
        Vector2 corner = new(layoutRectangle.X, layoutRectangle.Y);
        Path? clip = null;
        if ((format.FormatFlags & StringFormatFlags.NoClip) == 0 && layoutRectangle.Width > 0 && layoutRectangle.Height > 0)
        {
            clip = Region.Rect(layoutRectangle).Transform(TextTransform);
        }

        // ClearType draws the glyphs with GDI+'s hints, filter and gamma (Egami.Drawing's preset); the lines are filled.
        clearType = clearType && DrawClearType(layout, corner, (SolidBrush)brush, clip);
        FillText(brush, TextOutline(layout, font, corner, glyphs: !clearType), clip);
    }

    /// <summary>Whether text draws as ClearType: the hint asks for it, the brush is opaque and solid, the text horizontal and unrotated.</summary>
    private bool ClearTypes(Brush brush, StringFormat format)
    {
        Matrix3x2 m = TextTransform;
        return State.TextHint is TextRenderingHint.ClearTypeGridFit or TextRenderingHint.SystemDefault && brush is SolidBrush { Color.A: 255 }
            && (format.FormatFlags & StringFormatFlags.DirectionVertical) == 0 && m.M12 == 0 && m.M21 == 0 && m.M11 > 0
            && MathF.Abs(m.M11 - m.M22) <= 1e-4f * m.M11 && State.Compositing == Drawing2D.CompositingMode.SourceOver;
    }

    /// <summary>
    /// Draws a layout's glyphs as GDI+ draws ClearTypeGridFit text: hinted for GDI+, grid-fitted, LCD-filtered and blended per
    /// channel with a gamma of 1 + TextContrast / 10. False when a pixel of the area is not opaque, and the caller fills the outlines
    /// instead.
    /// </summary>
    private bool DrawClearType(GdiTextLayout layout, Vector2 corner, SolidBrush solid, Path? clip)
    {
        Matrix3x2 m = TextTransform;
        float scale = m.M11;
        Matrix3x2 place = Matrix3x2.CreateTranslation(corner) * m;
        EgamiFonts.Font device = new EgamiFonts.Font(layout.Font.Typeface, layout.Font.Size * scale) { Synthesis = layout.Font.Synthesis, Kerning = false }.WithGdiPlusLook();
        List<EgamiFonts.GlyphRun> runs = [];
        float top = layout.Top, x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        foreach (GdiTextLayout.Line line in layout.Lines)
        {
            if (layout.Run(line, top) is not EgamiFonts.GlyphRun run)
            {
                continue;
            }

            Vector2[] positions = new Vector2[run.Count];
            for (int i = 0; i < positions.Length; i++)
            {
                positions[i] = Vector2.Transform(run.Positions[i], place);
            }

            runs.Add(EgamiFonts.GlyphRun.Create(device, run.Glyphs, positions));
            float em = layout.Font.Size;
            Vector2 a = Vector2.Transform(new Vector2(line.X - em, top + line.Top - em), place);
            Vector2 b = Vector2.Transform(new Vector2(line.X + line.Width + (2 * em), top + line.Top + layout.LineSpacing + em), place);
            (x0, y0, x1, y1) = (MathF.Min(x0, a.X), MathF.Min(y0, a.Y), MathF.Max(x1, b.X), MathF.Max(y1, b.Y));
        }

        if (runs.Count == 0)
        {
            return true;
        }

        RectF bounds = new(x0, y0, x1 - x0, y1 - y0);
        if (!Sane(bounds))
        {
            return true;
        }

        Path? outer = EffectiveClip;
        if (outer is not null && outer.IsEmpty)
        {
            return true;
        }

        bounds = outer is null ? bounds : Intersect(bounds, outer.Bounds);
        bounds = clip is null ? bounds : Intersect(bounds, clip.Bounds);
        CanvasOptions options = CanvasOptions.GdiPlusClearType with { TextGamma = 1 + (State.TextContrast / 10f) };
        SolidPaint paint = new(new Pixels.Rgba8(solid.Color.R, solid.Color.G, solid.Color.B, 255));
        return Surface.DrawOpaque(_image.Current, bounds, options, canvas =>
        {
            if (outer is not null)
            {
                canvas.ClipPath(outer, ClipOp.Intersect, antialias: false);
            }

            if (clip is not null)
            {
                canvas.ClipPath(clip, ClipOp.Intersect, antialias: false);
            }

            foreach (EgamiFonts.GlyphRun run in runs)
            {
                canvas.DrawGlyphRun(run, Vector2.Zero, paint);
            }
        });
    }

    /// <summary>Measures text laid out in a box (zero for an unbounded side).</summary>
    internal SizeF MeasureStringCore(ReadOnlySpan<char> s, Font font, SizeF layoutArea, StringFormat? format, out int charactersFitted, out int linesFilled)
    {
        ArgumentNullException.ThrowIfNull(font);
        Alive();
        charactersFitted = 0;
        linesFilled = 0;
        if (s.IsEmpty)
        {
            return SizeF.Empty;
        }

        GdiTextLayout layout = GdiTextLayout.Create(s, font, font.EmIn(this), layoutArea, format);
        charactersFitted = layout.CharactersFitted;
        linesFilled = layout.Lines.Count;
        return layout.Measure();
    }

    /// <summary>Measures where ranges of text fall, as regions of whole device pixels.</summary>
    internal Region[] MeasureCharacterRangesCore(ReadOnlySpan<char> text, Font font, RectangleF layoutRect, StringFormat? format)
    {
        ArgumentNullException.ThrowIfNull(font);
        Alive();
        format ??= StringFormat.GenericDefault;
        ReadOnlySpan<CharacterRange> ranges = format.Ranges;
        if (ranges.IsEmpty)
        {
            return [];
        }

        // GDI+ measures ranges with the hinted advances at the device size.
        GdiTextLayout layout = GdiTextLayout.Create(text, font, font.EmIn(this), layoutRect.Size, format, DeviceScale);
        Matrix3x2 place = layout.Turn * Matrix3x2.CreateTranslation(layoutRect.X, layoutRect.Y);
        Matrix3x2 toDevice = place * ImageTransform;
        Matrix3x2.Invert(ImageTransform, out Matrix3x2 toWorld);
        float top = layout.Top;
        Region[] regions = new Region[ranges.Length];
        for (int r = 0; r < ranges.Length; r++)
        {
            CharacterRange range = ranges[r];
            if (range.First < 0 || range.Length < 0 || range.First + range.Length > text.Length)
            {
                throw GdiErrors.InvalidParameter();
            }

            PathBuilder builder = new();
            foreach ((GdiTextLayout.Line line, float x0, float x1) in layout.Spans(range.First, range.Length))
            {
                Vector2 a = Vector2.Transform(new Vector2(x0, top + line.Top), toDevice);
                Vector2 b = Vector2.Transform(new Vector2(x1, top + line.Top + layout.LineSpacing), toDevice);
                float l = MathF.Round(MathF.Min(a.X, b.X)), t = MathF.Round(MathF.Min(a.Y, b.Y));
                float rr = MathF.Round(MathF.Max(a.X, b.X)), bb = MathF.Round(MathF.Max(a.Y, b.Y));
                if (rr > l && bb > t)
                {
                    builder.AddPath(Path.Rect(new RectF(l, t, rr - l, bb - t)), toWorld);
                }
            }

            regions[r] = new Region(builder.IsEmpty ? Path.Empty : builder.ToPath(FillRule.NonZero));
        }

        return regions;
    }

    /// <summary>Device pixels per world unit, for measuring ranges by the hinted advances at the device size, as GDI+ does.</summary>
    private float DeviceScale
    {
        get
        {
            float scale = MathF.Sqrt(MathF.Abs(ImageTransform.GetDeterminant()));
            return scale > 0 && float.IsFinite(scale) ? scale : 1;
        }
    }

    /// <summary>World coordinates to device pixels for text: GDI+ gives glyphs none of the shape pixel offset.</summary>
    internal Matrix3x2 TextTransform => ImageTransform;

    /// <summary>Fills text outlines, smoothed as the text rendering hint says rather than the smoothing mode.</summary>
    private void FillText(Brush brush, Path world, Path? clip)
    {
        if (world.IsEmpty)
        {
            return;
        }

        Path device = world.Transform(TextTransform);
        if (!Sane(device.Bounds))
        {
            return;
        }

        bool smooth = State.TextHint is not (TextRenderingHint.SingleBitPerPixel or TextRenderingHint.SingleBitPerPixelGridFit);
        BrushFill fill = brush.Fill(this);
        RectF bounds = clip is null ? device.Bounds : Intersect(device.Bounds, clip.Bounds);
        Draw(bounds, canvas =>
        {
            if (clip is not null)
            {
                canvas.ClipPath(clip, ClipOp.Intersect, antialias: false);
            }

            if (fill.Mesh is Mesh mesh)
            {
                canvas.ClipPath(device, ClipOp.Intersect, antialias: smooth);
                canvas.DrawMesh(mesh, new SolidPaint(new Pixels.Rgba8(255, 255, 255, 255)) { Antialias = smooth, BlendMode = Blend });
            }
            else if (fill.Paint is Paint paint)
            {
                canvas.FillPath(device, paint with { Antialias = smooth, BlendMode = Blend });
            }
        });
    }
}
