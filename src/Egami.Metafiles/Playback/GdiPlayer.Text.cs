using System.Numerics;
using System.Text;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Pixels;

using static Egami.Metafiles.Reading.MetafileReader;

namespace Egami.Metafiles.Playback;

/// <summary>
/// Text records (design questions 8 and 9): the font is found by <see cref="MetafileFonts"/>; characters map through the face's
/// cmap without shaping, as GDI draws them, and each glyph goes where the record's dx advances put it (or the font's own
/// advances plus the character extra). Alignment, the update-current-position flag, escapement, opaque and clip rectangles,
/// underline and strikeout follow the device context. Glyph indices are used only with the face they were made for. Text is a
/// glyph run, or outlines with <see cref="MetafileRenderOptions.TextAsPaths"/>.
/// </summary>
internal sealed partial class GdiPlayer
{
    private const uint EtoOpaque = 0x2, EtoClipped = 0x4, EtoGlyphIndex = 0x10, EtoPdy = 0x2000, EtoNoRect = 0x100, EtoSmallChars = 0x200;

    private MetafileFonts? _fonts;

    private MetafileFonts Fonts => _fonts ??= new MetafileFonts(_options, _report);

    // ---- EMF ----

    private void PlayEmfText(int type, ReadOnlySpan<byte> r, long offset)
    {
        switch (type)
        {
            case 83: // EMR_EXTTEXTOUTA
            case 84: // EMR_EXTTEXTOUTW
                EmfText(r, 36, wide: type == 84);
                break;
            case 96: // EMR_POLYTEXTOUTA
            case 97: // EMR_POLYTEXTOUTW
                {
                    int n = (int)Math.Min(U32(r, 36), (uint)Math.Max(0, (r.Length - 40) / 40));
                    for (int i = 0; i < n; i++)
                    {
                        EmfText(r, 40 + (i * 40), wide: type == 97);
                    }

                    break;
                }

            case 108: // EMR_SMALLTEXTOUT
                {
                    int x = I32(r, 8), y = I32(r, 12), chars = I32(r, 16);
                    uint options = U32(r, 20);
                    int at = (options & EtoNoRect) != 0 ? 36 : 52;
                    RectF? rect = (options & EtoNoRect) != 0 ? null : LogicalRect(I32(r, 36), I32(r, 40), I32(r, 44), I32(r, 48));
                    bool small = (options & EtoSmallChars) != 0;
                    chars = Math.Clamp(chars, 0, (r.Length - at) / (small ? 1 : 2));
                    string text = small ? MetafileFonts.Decode(r.Slice(at, chars), _dc.Font.CharSet) : Encoding.Unicode.GetString(r.Slice(at, chars * 2));
                    DrawText(new Vector2(x, y), text, null, options & ~EtoGlyphIndex, rect, glyphIndices: (options & EtoGlyphIndex) != 0 ? Utf16Codes(r.Slice(at, chars * 2)) : null);
                    break;
                }

            default:
                Skip($"Text (EMF type {type})", offset);
                break;
        }
    }

    /// <summary>An EMRTEXT at <paramref name="at"/>: Reference, Chars, offString, Options, Rectangle, offDx (offsets from the record).</summary>
    private void EmfText(ReadOnlySpan<byte> r, int at, bool wide)
    {
        Vector2 reference = new(I32(r, at), I32(r, at + 4));
        int chars = I32(r, at + 8), offString = I32(r, at + 12), offDx = I32(r, at + 36);
        uint options = U32(r, at + 16);
        RectF? rect = (options & EtoNoRect) == 0 ? LogicalRect(I32(r, at + 20), I32(r, at + 24), I32(r, at + 28), I32(r, at + 32)) : null;
        int size = wide ? 2 : 1;
        if (chars <= 0 || offString < 0 || offString > r.Length || chars > (r.Length - offString) / size)
        {
            if (chars == 0 && rect is RectF only && (options & EtoOpaque) != 0)
            {
                OpaqueRect(only);
            }

            return;
        }

        ReadOnlySpan<byte> bytes = r.Slice(offString, chars * size);
        bool pdy = (options & EtoPdy) != 0;
        float[]? dx = null;
        if (offDx > 0 && offDx < r.Length)
        {
            int per = pdy ? 2 : 1;
            int n = Math.Min(chars * per, (r.Length - offDx) / 4);
            dx = new float[n];
            for (int i = 0; i < n; i++)
            {
                dx[i] = I32(r, offDx + (i * 4));
            }
        }

        bool glyphs = (options & EtoGlyphIndex) != 0;
        string text = glyphs ? "" : wide ? Encoding.Unicode.GetString(bytes) : MetafileFonts.Decode(bytes, _dc.Font.CharSet);
        DrawText(reference, text, dx, options, rect, glyphIndices: glyphs ? (wide ? Utf16Codes(bytes) : [.. bytes.ToArray().Select(b => (ushort)b)]) : null);
    }

    private static ushort[] Utf16Codes(ReadOnlySpan<byte> bytes)
    {
        ushort[] codes = new ushort[bytes.Length / 2];
        for (int i = 0; i < codes.Length; i++)
        {
            codes[i] = U16(bytes, i * 2);
        }

        return codes;
    }

    private static RectF LogicalRect(int l, int t, int r, int b) => new(l, t, r - l, b - t);

    // ---- WMF ----

    private void PlayWmfText(int function, ReadOnlySpan<byte> p, long offset)
    {
        if (function == 0x0521)
        {
            // META_TEXTOUT: StringLength, the string (padded to a word), YStart, XStart.
            int length = Math.Clamp((int)U16(p, 0), 0, Math.Max(0, p.Length - 2));
            int padded = (length + 1) & ~1;
            if (2 + padded + 4 > p.Length)
            {
                return;
            }

            string line = MetafileFonts.Decode(p.Slice(2, length), _dc.Font.CharSet);
            DrawText(new Vector2(I16(p, 2 + padded + 2), I16(p, 2 + padded)), line, null, 0, null, null);
            return;
        }

        // META_EXTTEXTOUT: Y, X, StringLength, fwOpts, an optional rectangle, the string (padded), optional dx values.
        int y = I16(p, 0), x = I16(p, 2), count = U16(p, 4);
        uint options = U16(p, 6);
        int at = 8;
        RectF? rect = null;
        if ((options & (EtoOpaque | EtoClipped)) != 0 && p.Length >= 16)
        {
            rect = LogicalRect(I16(p, 8), I16(p, 10), I16(p, 12), I16(p, 14));
            at = 16;
        }

        count = Math.Clamp(count, 0, Math.Max(0, p.Length - at));
        ReadOnlySpan<byte> bytes = p.Slice(at, count);
        at += (count + 1) & ~1;
        float[]? dx = null;
        if (p.Length - at >= count * 2 && count > 0)
        {
            dx = new float[count];
            for (int i = 0; i < count; i++)
            {
                dx[i] = I16(p, at + (i * 2));
            }
        }

        bool glyphs = (options & EtoGlyphIndex) != 0;
        string text = glyphs ? "" : MetafileFonts.Decode(bytes, _dc.Font.CharSet);
        DrawText(new Vector2(x, y), text, dx, options, rect, glyphs ? [.. bytes.ToArray().Select(b => (ushort)b)] : null);
        _ = offset;
    }

    // ---- Drawing ----

    /// <summary>ExtTextOut's opaque rectangle: the background color over the logical rectangle.</summary>
    private void OpaqueRect(RectF rect) =>
        _canvas.FillRect(DeviceRectOf(rect), new SolidPaint(_dc.BkColor) { Antialias = !_aliased });

    private RectF DeviceRectOf(RectF rect)
    {
        Vector2 a = Dev(new Vector2(rect.X, rect.Y)), b = Dev(new Vector2(rect.Right, rect.Bottom));
        return new RectF(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Abs(b.X - a.X), MathF.Abs(b.Y - a.Y));
    }

    /// <summary>
    /// Draws a text record: characters (or glyph indices) placed along the baseline by dx (logical units) or the font's
    /// advances, from the reference point aligned by the text-align flags, rotated by the escapement.
    /// </summary>
    private void DrawText(Vector2 reference, string text, float[]? dx, uint options, RectF? rect, ushort[]? glyphIndices)
    {
        GdiFont f = _dc.Font;
        bool symbol = f.CharSet == 2;
        MetafileFonts.Resolved? resolved = Fonts.Resolve(f.Face, f.Weight, f.Italic);
        if (rect is RectF opaque && (options & EtoOpaque) != 0)
        {
            OpaqueRect(opaque);
        }

        int count = glyphIndices?.Length ?? text.Length;
        if (resolved is null || count == 0)
        {
            return;
        }

        if (glyphIndices is not null && !resolved.Exact)
        {
            WarnOnce("Metafile.GlyphIndices", $"Text given as glyph indices of '{f.Face}', which is not installed, is skipped (design question 9).", key: "glyphs:" + f.Face);
            return;
        }

        Typeface typeface = resolved.Typeface;
        VerticalMetrics win = typeface.WinMetrics;
        float cell = win.Ascent - win.Descent;
        if (cell <= 0)
        {
            cell = typeface.UnitsPerEm;
        }

        // The em size in logical units: a negative height is the em, a positive one the cell (ascent plus descent).
        float em = f.Height < 0 ? -f.Height : f.Height > 0 ? f.Height * typeface.UnitsPerEm / cell : 12 * 96 / 72f;
        if (f.Stock && MathF.Abs(Full.M22) > 0)
        {
            // A stock font is realized in reference device pixels, whatever the mapping.
            em = em * MathF.Abs(_deviceScale.Y) / MathF.Abs(Full.M22);
        }

        Font font = new(typeface, em) { Synthesis = resolved.Synthesis, Kerning = false };
        float scale = em / typeface.UnitsPerEm;
        float ascent = win.Ascent * scale, descent = -win.Descent * scale;

        // The glyphs and their advances along the baseline, in logical units.
        ushort[] glyphs = new ushort[count];
        float[] advances = new float[count];
        int[] clusters = new int[count];
        for (int i = 0; i < count; i++)
        {
            glyphs[i] = glyphIndices is not null ? glyphIndices[i]
                : symbol ? MetafileFonts.SymbolGlyph(typeface, f.Face, text[i], resolved.Exact)
                : char.IsSurrogate(text[i]) ? (ushort)0 : typeface.GetGlyph(text[i]);
            clusters[i] = i;
            int per = (options & EtoPdy) != 0 ? 2 : 1;
            advances[i] = dx is not null && (i * per) < dx.Length ? dx[i * per] : font.GetAdvance(glyphs[i]) + _dc.TextCharExtra;
        }

        float total = advances.Sum();
        uint align = _dc.TextAlign;
        bool updateCp = (align & 1) != 0;
        Vector2 origin = updateCp ? _dc.Position : reference;
        float startX = (align & 6) switch
        {
            6 => -total / 2, // TA_CENTER
            2 => -total, // TA_RIGHT
            _ => 0,
        };
        float baseline = (align & 24) switch
        {
            24 => 0, // TA_BASELINE
            8 => -descent, // TA_BOTTOM
            _ => ascent, // TA_TOP
        };

        // Text space: x along the baseline, y down, in logical units from the reference point; the escapement turns it
        // counterclockwise on the page, and the text stays upright even where the mapping flips an axis.
        Matrix3x2 full = Full;
        Vector2 sx = new(full.M11, full.M12), sy = new(full.M21, full.M22);
        float flipX = full.M11 < 0 && full.M12 == 0 ? -1 : 1, flipY = full.M22 < 0 && full.M21 == 0 ? -1 : 1;
        Matrix3x2 linear = new(sx.X * flipX, sx.Y * flipX, sy.X * flipY, sy.Y * flipY, 0, 0);
        float angle = -f.Escapement / 10f * MathF.PI / 180;
        Matrix3x2 textToDevice = Matrix3x2.CreateRotation(angle) * linear * Matrix3x2.CreateTranslation(Vector2.Transform(origin, full) + Center);

        Vector2[] positions = new Vector2[count];
        float pen = startX;
        for (int i = 0; i < count; i++)
        {
            positions[i] = new Vector2(pen, baseline);
            pen += advances[i];
        }

        if (updateCp)
        {
            // TA_UPDATECP moves the current position past the text, in the direction of the escapement.
            float along = (align & 6) switch { 6 => 0, 2 => -total, _ => total };
            Vector2 move = Vector2.TransformNormal(new Vector2(along, 0), Matrix3x2.CreateRotation(angle) * new Matrix3x2(flipX, 0, 0, flipY, 0, 0));
            _dc.Position = origin + move;
        }

        int saved = _canvas.Save();
        try
        {
            if (rect is RectF clip && (options & EtoClipped) != 0)
            {
                _canvas.ClipRect(DeviceRectOf(clip), ClipOp.Intersect, antialias: !_aliased);
            }

            _canvas.Concat(textToDevice);
            if (_dc.BkMode == 2 && (options & EtoOpaque) == 0)
            {
                // An opaque background mode fills the text's cell behind it.
                _canvas.FillRect(new RectF(startX, baseline - ascent, total, ascent + descent), new SolidPaint(_dc.BkColor) { Antialias = !_aliased });
            }

            SolidPaint paint = new(_dc.TextColor) { Antialias = !_aliased };
            GlyphRun run = GlyphRun.Create(font, glyphs, positions, glyphIndices is null && text.Length == count ? text : null, glyphIndices is null && text.Length == count ? clusters : default);
            if (_options.TextAsPaths)
            {
                _canvas.FillPath(run.ToPath(Vector2.Zero), paint);
            }
            else
            {
                _canvas.DrawGlyphRun(run, Vector2.Zero, paint);
            }

            FontExtents extents = font.Extents;
            if (f.Underline)
            {
                float thick = Math.Max(extents.UnderlineThickness, em / 20);
                _canvas.FillRect(new RectF(startX, baseline - extents.UnderlinePosition, total, thick), paint);
            }

            if (f.StrikeOut)
            {
                float thick = Math.Max(extents.StrikeoutSize, em / 20);
                _canvas.FillRect(new RectF(startX, baseline - extents.StrikeoutPosition, total, thick), paint);
            }
        }
        finally
        {
            _canvas.RestoreToCount(saved);
        }
    }
}
