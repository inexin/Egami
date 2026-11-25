using System.Numerics;
using System.Text;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Metafiles.Writing;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Metafiles;

public sealed partial class EmfCanvas
{
    /// <summary>The longest face name GDI's LOGFONT holds, before its terminating zero.</summary>
    private const int MaxFaceName = 31;

    /// <summary>Each face's GDI family name (name ID 1), or null when it has none that GDI can hold.</summary>
    private readonly Dictionary<Typeface, string?> _families = [];

    /// <inheritdoc/>
    protected override void OnDrawGlyphRun(GlyphRun run, Vector2 origin, Paint paint)
    {
        if (Options.Text != EmfText.Glyphs || _recorder is not null || _quiet > 0)
        {
            base.OnDrawGlyphRun(run, origin, paint);
            return;
        }

        // Color glyphs are drawn as their layers and images, as on any canvas; the rest are written as text where they can be.
        run = DrawRendererGlyphs(run, origin, paint);
        if (run.Count == 0)
        {
            return;
        }

        Font font = run.Font;
        if (font.ColorGlyphs && (font.EffectiveTypeface.ColorFormats & ~ColorGlyphFormats.Svg) != 0)
        {
            run = DrawColorGlyphs(run, origin, paint);
            if (run.Count == 0)
            {
                return;
            }
        }

        if (!WriteText(run, origin, paint))
        {
            Path path = run.ToPath(origin);
            if (!path.IsEmpty)
            {
                OnDrawPath(path, null, font.Edging == FontEdging.Alias && paint.Antialias ? paint with { Antialias = false } : paint);
            }
        }
    }

    /// <summary>
    /// Writes a glyph run as text: an EMF+ font (the face's GDI family name, the em size in world units, bold and italic) drawn by
    /// DrawDriverString with glyph indices and positions, and in the GDI part, a LOGFONT selected for ExtTextOutW with
    /// ETO_GLYPH_INDEX and advances. Returns false, for outlines instead, for a face whose name GDI cannot hold, a synthesized,
    /// emboldened or variable instance, turned or scaled glyphs, a mirroring transform, or a paint that is not one color.
    /// </summary>
    private bool WriteText(GlyphRun run, Vector2 origin, Paint paint)
    {
        Font font = run.Font;
        Typeface face = font.Typeface;
        if (!ReferenceEquals(font.EffectiveTypeface, face) || face.Variations.Count > 0 || font.Embolden > 0 || (font.AllowSynthesis && font.Synthesis != FontSynthesis.None))
        {
            return false;
        }

        foreach (GlyphTransform t in run.Transforms)
        {
            if (!t.IsTranslation)
            {
                return false;
            }
        }

        RgbaF32? constant = Constant(paint);
        Matrix3x2 m = Matrix;
        if (constant is not RgbaF32 solid || Unsupported(paint, constant) is not null || Family(face) is not string family || m.GetDeterminant() < 0)
        {
            return false;
        }

        if (paint.BlendMode == BlendMode.Destination || !Invertible(m))
        {
            return true;
        }

        Rgba8 color = Color8(solid, paint.Opacity * _fold);
        if (color.A == 0)
        {
            return true;
        }

        ReadOnlySpan<ushort> glyphs = run.Glyphs;
        Vector2[] positions = new Vector2[run.Count];
        for (int i = 0; i < positions.Length; i++)
        {
            positions[i] = origin + run.Positions[i];
        }

        Paint drawn = paint with { Opacity = paint.Opacity * _fold };
        Remember(c => c.DrawGlyphRun(run, origin, drawn));
        TextBounds ink = run.Bounds;
        Touch(RectF.FromEdges(origin.X + ink.Left, origin.Y + ink.Top, origin.X + ink.Right, origin.Y + ink.Bottom), 1);
        bool bold = face.Style.Weight >= 600, italic = face.Style.Slant != FontSlant.Upright;
        bool aliased = font.Edging == FontEdging.Alias || !paint.Antialias;
        if (_plus)
        {
            // EmfPlusFont: version, em size, UnitWorld, style, reserved, the family name's length and the name.
            RecordBuffer f = new(24 + (family.Length * 2));
            f.U32(Egami.GdiPlus.GdiPlusData.Version).F32(font.Size).I32(0).I32((bold ? 1 : 0) | (italic ? 2 : 0)).U32(0).I32(family.Length);
            f.Bytes(Encoding.Unicode.GetBytes(family)).Align();
            int id = _w.PlusObject(6, f.ToArray());
            SetPlusWorld(m);
            SetTextHint(aliased ? 2 : 4); // TextRenderingHintSingleBitPerPixel or TextRenderingHintAntiAlias
            RecordBuffer b = new(16 + (glyphs.Length * 10));
            b.U32(Argb(color)).I32(0).I32(0).I32(glyphs.Length); // the color, glyph indices (no cmap lookup), no matrix
            foreach (ushort g in glyphs)
            {
                b.U16(g);
            }

            foreach (Vector2 p in positions) // right after the glyphs, unaligned
            {
                b.F32(p.X).F32(p.Y);
            }

            _w.Plus(0x4036, (ushort)(0x8000 | id), b.Span); // DrawDriverString with a color for its brush
        }

        if (_gdi)
        {
            WriteGdiText(font, family, face.Style.Weight, italic, aliased, glyphs, positions, color, m);
        }

        return true;
    }

    /// <summary>
    /// The GDI part of a glyph run: a LOGFONT whose height is the em in sixteenths, the text color and baseline alignment, then one
    /// ExtTextOutW with ETO_GLYPH_INDEX for each stretch of glyphs on one baseline, its advances giving each glyph's place.
    /// </summary>
    private void WriteGdiText(Font font, string family, int weight, bool italic, bool aliased, ReadOnlySpan<ushort> glyphs, Vector2[] positions, Rgba8 color, Matrix3x2 m)
    {
        RecordBuffer lf = new(92);
        lf.I32(-Math.Max(1, Fix(font.Size))).I32(0).I32(0).I32(0).I32(Math.Clamp(weight, 1, 1000)); // height (the em), width, escapement, orientation, weight
        lf.Bytes([(byte)(italic ? 1 : 0), 0, 0, 1, 0, 0, (byte)(aliased ? 3 : 4), 0]); // italic, underline, strikeout, DEFAULT_CHARSET, precisions, quality, pitch
        byte[] name = new byte[64];
        Encoding.Unicode.GetBytes(family, name);
        lf.Bytes(name);
        uint handle = _w.GdiObject(82, lf.Span, Selected); // EMR_EXTCREATEFONTINDIRECTW
        if (_file.Font != handle)
        {
            _w.Gdi(37, _w.Data().U32(handle).Span); // EMR_SELECTOBJECT
            _file.Font = handle;
        }

        uint colorRef = ColorRef(color);
        if (_file.TextColor != colorRef)
        {
            _w.Gdi(24, _w.Data().U32(colorRef).Span); // EMR_SETTEXTCOLOR
            _file.TextColor = colorRef;
        }

        if (_file.TextAlign != 24)
        {
            _w.Gdi(22, _w.Data().U32(24).Span); // EMR_SETTEXTALIGN: TA_BASELINE, TA_LEFT, TA_NOUPDATECP
            _file.TextAlign = 24;
        }

        SetGdiWorld(m);
        for (int start = 0; start < glyphs.Length;)
        {
            int y = Fix(positions[start].Y), end = start + 1;
            while (end < glyphs.Length && Fix(positions[end].Y) == y)
            {
                end++;
            }

            int n = end - start, stringBytes = ((n * 2) + 3) & ~3;
            RecordBuffer b = new(68 + stringBytes + (n * 4));
            float right = positions[end - 1].X + font.GetAdvance(glyphs[end - 1]);
            float top = positions[start].Y - font.Size, bottom = positions[start].Y + (font.Size / 2);
            b.Bytes(Bounds16(MapRect(RectF.FromEdges(MathF.Min(positions[start].X, right), top, MathF.Max(positions[start].X, right), bottom), m)));
            b.U32(2).F32(0).F32(0); // GM_ADVANCED, scales unused
            b.I32(Fix(positions[start].X)).I32(y).I32(n).U32(76).U32(0x10); // reference, count, string offset, ETO_GLYPH_INDEX
            b.I32(0).I32(0).I32(-1).I32(-1).U32((uint)(76 + stringBytes)); // no rectangle, then the advances' offset
            for (int i = start; i < end; i++)
            {
                b.U16(glyphs[i]);
            }

            b.Align();
            for (int i = start; i < end; i++)
            {
                int x = Fix(positions[i].X), next = i + 1 < end ? Fix(positions[i + 1].X) : Fix(positions[i].X + font.GetAdvance(glyphs[i]));
                b.I32(next - x);
            }

            _w.Gdi(84, b.Span); // EMR_EXTTEXTOUTW
            start = end;
        }
    }

    /// <summary>SetTextRenderingHint, which says whether EMF+ text is antialiased.</summary>
    private void SetTextHint(int hint)
    {
        if (_file.TextHint != hint)
        {
            _w.Plus(0x401F, (ushort)hint, []);
            _file.TextHint = hint;
        }
    }

    /// <summary>
    /// The family name GDI and GDI+ know a face by: its Windows family name (name ID 1, English first), which names a weight such as
    /// Light or SemiBold as its own family; null when it is missing or longer than a LOGFONT holds.
    /// </summary>
    private string? Family(Typeface face)
    {
        if (_families.TryGetValue(face, out string? cached))
        {
            return cached;
        }

        string? english = null, any = null;
        foreach (FontNameRecord r in face.GetNames())
        {
            if (r.NameId == 1 && !string.IsNullOrWhiteSpace(r.Value))
            {
                any ??= r.Value;
                if (r.PlatformId == 3 && r.LanguageId == 0x409)
                {
                    english ??= r.Value;
                }
            }
        }

        string? family = (english ?? any)?.TrimEnd('\0');
        family = family is { Length: > 0 and <= MaxFaceName } && !family.Contains('\0', StringComparison.Ordinal) ? family : null;
        _families[face] = family;
        return family;
    }
}
