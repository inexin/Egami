using System.Numerics;
using System.Text;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Fonts.Layout;
using Egami.GdiPlus;
using Egami.Pixels;

using static Egami.Metafiles.Reading.MetafileReader;

namespace Egami.Metafiles.Playback;

/// <summary>
/// EMF+ text: DrawString lays the string out in its layout rectangle through Egami's paragraph layout (alignment, line
/// alignment, wrapping, trimming with an ellipsis, tab stops, the hotkey prefix), and DrawDriverString places glyphs at the
/// given positions. Faces resolve as for GDI text (design question 8).
/// </summary>
internal sealed partial class EmfPlusPlayer
{
    private MetafileFonts? _fonts;

    private MetafileFonts Fonts => _fonts ??= new MetafileFonts(_options, _report);

    /// <summary>The font's em size in world units: its own unit, through the page unit and scale.</summary>
    private float EmWorld(PlusFont font) =>
        font.Unit is 0 ? font.EmSize : font.EmSize * UnitScale(font.Unit) / UnitScale(_state.PageUnit) / _state.PageScale;

    private Paint TextPaint(ushort flags, uint brush)
    {
        PlusFill fill = BrushPaint(flags, brush);
        Paint paint = fill.Paint ?? Solid(new Rgba8(0, 0, 0, 255));

        // TextRenderingHint SingleBitPerPixel (1, 2) draws aliased text; the others antialias.
        return paint with { Antialias = _state.TextHint is not (1 or 2) };
    }

    /// <summary>DrawString: font in the flags' low byte; brush, string format, length, layout rectangle, then the string.</summary>
    private void DrawString(ushort flags, ReadOnlySpan<byte> d)
    {
        if (_objects[flags & 0xFF] is not PlusFont font)
        {
            return;
        }

        uint brush = U32(d, 0), formatId = U32(d, 4);
        int length = I32(d, 8);
        RectF layout = new(F32(d, 12), F32(d, 16), F32(d, 20), F32(d, 24));
        if (length <= 0 || length > (d.Length - 28) / 2)
        {
            return;
        }

        string text = Encoding.Unicode.GetString(d.Slice(28, length * 2));
        GdiPlusStringFormat format = formatId < 64 && _objects[formatId] is PlusStringFormat f ? f.Format : new GdiPlusStringFormat();

        // Show's underline is not drawn.
        text = format.DisplayText(text);

        MetafileFonts.Resolved? resolved = Fonts.Resolve(font.Family, (font.Style & 1) != 0 ? 700 : 400, (font.Style & 2) != 0);
        if (resolved is null || !Fonts.Collection.TryGetFamily(resolved.Typeface.FamilyName, out FontFamily family))
        {
            return;
        }

        float em = EmWorld(font);
        bool noWrap = (format.Flags & GdiPlusStringFormat.NoWrap) != 0, noClip = (format.Flags & GdiPlusStringFormat.NoClip) != 0, vertical = (format.Flags & GdiPlusStringFormat.DirectionVertical) != 0;
        TextDecoration? decoration = (font.Style & 12) switch
        {
            4 => new TextDecoration { Lines = TextDecorationLines.Underline },
            8 => new TextDecoration { Lines = TextDecorationLines.LineThrough },
            12 => new TextDecoration { Lines = TextDecorationLines.Underline | TextDecorationLines.LineThrough },
            _ => null,
        };
        ParagraphStyle style = new()
        {
            DefaultStyle = new TextStyle
            {
                FontFamilies = [family],
                Size = em,
                FontStyle = new FontStyle((font.Style & 1) != 0 ? 700 : 400, 5, (font.Style & 2) != 0 ? FontSlant.Italic : FontSlant.Upright),
                Paint = TextPaint(flags, brush),
                Decoration = decoration,
            },
            Align = format.Alignment switch { GdiPlusStringFormat.AlignCenter => TextAlign.Center, GdiPlusStringFormat.AlignFar => TextAlign.End, _ => TextAlign.Start },
            Direction = (format.Flags & GdiPlusStringFormat.DirectionRightToLeft) != 0 ? Egami.Fonts.Unicode.TextDirection.RightToLeft : Egami.Fonts.Unicode.TextDirection.LeftToRight,
            MaxLines = noWrap ? 1 : null,
            MaxHeight = layout.Height > 0 && !noClip ? layout.Height : null,
            Ellipsis = format.Ellipsis ? "…" : null,
            TabStops = Tabs(format, em),
        };

        // GDI+ leaves a sixth of the em as a margin on each side of the layout box.
        float margin = em / 6;
        float width = layout.Width > 0 && !noWrap ? Math.Max(1, layout.Width - (2 * margin)) : 1e6f;
        RectF box = new(layout.X + margin, layout.Y, width, layout.Height);
        VerticalAlign align = format.LineAlignment switch { GdiPlusStringFormat.AlignCenter => VerticalAlign.Center, GdiPlusStringFormat.AlignFar => VerticalAlign.Bottom, _ => VerticalAlign.Top };
        if (layout.Width <= 0 && format.Alignment != 0)
        {
            WarnOnce("Metafile.TextApproximated", "A DrawString without a layout width is aligned from its origin.", "text-nowidth");
        }

        int count = _canvas.Save();
        try
        {
            _canvas.Concat(Full);
            if (layout.Width > 0 && layout.Height > 0 && !noClip)
            {
                _canvas.ClipRect(layout, ClipOp.Intersect, antialias: Smooth);
            }

            if (layout.Height <= 0)
            {
                box = box with { Height = 1e6f };
                align = VerticalAlign.Top;
            }

            _ = vertical;
            _canvas.DrawText(text, box, style, align);
        }
        finally
        {
            _canvas.RestoreToCount(count);
        }
    }

    /// <summary>GDI+ tab stops: the first offset, then intervals, each from the last; the default is four ems.</summary>
    private static List<TabStop>? Tabs(GdiPlusStringFormat format, float em)
    {
        if (format.TabStops.Length == 0)
        {
            return null;
        }

        _ = em;
        return [.. format.TabPositions().Select(at => new TabStop(at))];
    }

    /// <summary>
    /// DrawDriverString: font in the flags' low byte; brush, options (1: the values are characters to map through the cmap),
    /// a transform flag, the glyph count, the glyphs, their positions, and the optional transform.
    /// </summary>
    private void DrawDriverString(ushort flags, ReadOnlySpan<byte> d)
    {
        if (_objects[flags & 0xFF] is not PlusFont font)
        {
            return;
        }

        uint brush = U32(d, 0);
        int options = I32(d, 4), matrixPresent = I32(d, 8), count = I32(d, 12);
        if (count <= 0 || count > (d.Length - 16) / 10)
        {
            return;
        }

        MetafileFonts.Resolved? resolved = Fonts.Resolve(font.Family, (font.Style & 1) != 0 ? 700 : 400, (font.Style & 2) != 0);
        if (resolved is null)
        {
            return;
        }

        bool cmap = (options & 1) != 0;
        if (!cmap && !resolved.Exact)
        {
            WarnOnce("Metafile.GlyphIndices", $"Glyph indices of '{font.Family}', which is not installed, are skipped (design question 9).", "glyphs:" + font.Family);
            return;
        }

        ushort[] glyphs = new ushort[count];
        Vector2[] positions = new Vector2[count];
        StringBuilder chars = new();
        for (int i = 0; i < count; i++)
        {
            ushort value = U16(d, 16 + (i * 2));
            glyphs[i] = cmap ? resolved.Typeface.GetGlyph(value) : value;
            if (cmap)
            {
                chars.Append((char)value);
            }
        }

        int at = 16 + (count * 2);
        for (int i = 0; i < count; i++)
        {
            positions[i] = new Vector2(F32(d, at + (i * 8)), F32(d, at + (i * 8) + 4));
        }

        at += count * 8;
        Matrix3x2 transform = matrixPresent != 0 && at + 24 <= d.Length ? Matrix(d, at) : Matrix3x2.Identity;
        Font egamiFont = new(resolved.Typeface, EmWorld(font)) { Synthesis = resolved.Synthesis, Kerning = false };
        string? text = cmap && chars.Length == count ? chars.ToString() : null;
        GlyphRun run = GlyphRun.Create(egamiFont, glyphs, positions, text, text is null ? default : Enumerable.Range(0, count).ToArray());
        int saved = _canvas.Save();
        try
        {
            _canvas.Concat(transform * Full);
            Paint paint = TextPaint(flags, brush);
            if (_options.TextAsPaths)
            {
                _canvas.FillPath(run.ToPath(Vector2.Zero), paint);
            }
            else
            {
                _canvas.DrawGlyphRun(run, Vector2.Zero, paint);
            }
        }
        finally
        {
            _canvas.RestoreToCount(saved);
        }
    }
}
