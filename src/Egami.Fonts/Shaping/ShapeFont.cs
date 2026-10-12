// Translated in part from HarfBuzz's hb-ot-font.cc, hb-font.hh, hb-ot-metrics.cc and OT/glyf/Glyph.hh (MIT); see THIRD-PARTY-NOTICES.md.
using Egami.Fonts.Parsing;
using Egami.Fonts.Tables;
using Egami.Fonts.Variations;

namespace Egami.Fonts.Shaping;

/// <summary>A glyph's ink box as HarfBuzz reports it: bearings from the origin, a width and a (negative, y up) height.</summary>
internal struct ShapeExtents
{
    public int XBearing;
    public int YBearing;
    public int Width;
    public int Height;
}

/// <summary>
/// The font facts the shaper asks for, as HarfBuzz's ot font functions give them: in font units scaled to <see cref="XScale"/>
/// and <see cref="YScale"/> (the em, unless a test asks for another size), over a <see cref="Typeface"/> at its variation position.
/// </summary>
internal sealed class ShapeFont
{
    private const int UseMyMetrics = 0x0200;

    private readonly Typeface _face;
    private readonly long _xMult;
    private readonly long _yMult;
    private readonly float _xMultF;
    private readonly float _yMultF;
    private VerticalTables? _vertical;
    private float[]? _vvarScalars;
    private Tuple<int, int>? _hExtents; // A reference, so a face shared between threads never sees half a pair.

    public ShapeFont(Typeface face, int scale = 0)
    {
        _face = face;
        Layout = OpenTypeLayoutTables.For(face);
        UnitsPerEm = face.UnitsPerEm;
        XScale = YScale = scale > 0 ? scale : face.UnitsPerEm;
        _xMult = ((long)XScale << 16) / UnitsPerEm;
        _yMult = ((long)YScale << 16) / UnitsPerEm;
        _xMultF = (float)XScale / UnitsPerEm;
        _yMultF = (float)YScale / UnitsPerEm;
        if (face.Instance is { } instance)
        {
            NormalizedCoordinates = [.. instance.Coords.Select(c => (int)MathF.Round(c * 16384))];
            if (instance.Data.GdefStore is { } store && instance.GdefScalars is { } scalars)
            {
                VariationDelta = (outer, inner) => store.GetDelta(outer, inner, scalars);
            }
        }
    }

    /// <summary>Gets the variation coordinates in 2.14 units, one per axis; empty at the default instance.</summary>
    public int[] NormalizedCoordinates { get; } = [];

    /// <summary>Gets the GDEF variation store's delta for an outer and inner index, or null for a static face.</summary>
    public Func<int, int, float>? VariationDelta { get; }

    public Typeface Face => _face;

    public OpenTypeLayoutTables Layout { get; }

    public int UnitsPerEm { get; }

    /// <summary>Gets the horizontal scale: what an em measures in the positions the shaper gives.</summary>
    public int XScale { get; }

    /// <summary>Gets the vertical scale.</summary>
    public int YScale { get; }

    /// <summary>Gets the 16.16 multiplier <see cref="EmScaleX"/> applies, 0x10000 at the em.</summary>
    public long XMult => _xMult;

    public long YMult => _yMult;

    public float XMultF => _xMultF;

    public float YMultF => _yMultF;

    public int GlyphCount => _face.GlyphCount;

    private VerticalTables Vertical => _vertical ??= VerticalTables.For(_face);

    /// <summary>Scales a font-unit distance as HarfBuzz's em_scale_x: 16.16 fixed point, rounded.</summary>
    public int EmScaleX(int v) => (int)(((v * _xMult) + 32768) >> 16);

    public int EmScaleY(int v) => (int)(((v * _yMult) + 32768) >> 16);

    /// <summary>Scales a fractional distance as HarfBuzz's em_scalef, rounded as <see cref="RoundF"/>.</summary>
    public int EmScalefX(float v) => RoundF(v * _xMultF);

    public int EmScalefY(float v) => RoundF(v * _yMultF);

    /// <summary>HarfBuzz's roundf, which it defines as floorf (x + .5f): halves round up.</summary>
    public static int RoundF(float v) => (int)MathF.Floor(v + .5f);

    /// <summary>The cmap glyph of a code point; false when the font has none.</summary>
    public bool TryGetNominalGlyph(int codePoint, out uint glyph)
    {
        // As HarfBuzz, a mapped glyph id is used as the font gives it, even past the glyph count.
        int g = Layout.Cmap.Map(codePoint);
        if (g > 0)
        {
            glyph = (uint)g;
            return true;
        }

        glyph = 0;
        return false;
    }

    public bool HasGlyph(int codePoint) => TryGetNominalGlyph(codePoint, out _);

    /// <summary>The cmap format 14 glyph of a variation sequence; false when the font does not list the sequence.</summary>
    public bool TryGetVariationGlyph(int codePoint, int selector, out uint glyph)
    {
        int g = Layout.Cmap.MapVariation(codePoint, selector);
        if (g > 0)
        {
            glyph = (uint)g;
            return true;
        }

        glyph = 0;
        return false;
    }

    /// <summary>A glyph's horizontal advance, as hb_ot_get_glyph_h_advances: hmtx, varied by HVAR, else by gvar's phantom points.</summary>
    public int GetHAdvance(uint glyph)
    {
        if (glyph >= _face.GlyphCount)
        {
            return 0;
        }

        if (_face.Instance is not { } instance || _face.Root.Hmtx is not HmtxTable hmtx)
        {
            return EmScaleX((int)MathF.Round(_face.GetAdvance((ushort)glyph)));
        }

        int advance = hmtx.GetAdvance((int)glyph);
        if (instance.Data.Hvar is HvarTable hvar)
        {
            return EmScaleX(Math.Max(0, advance + RoundF(hvar.AdvanceDelta((int)glyph, instance.HvarScalars!))));
        }

        if (_face.Glyf is not null && instance.Data.Gvar is not null && TryGetPhantoms(glyph, out Phantoms p))
        {
            return EmScaleX(Math.Max(0, RoundF(p.Right - p.Left)));
        }

        return EmScaleX(advance);
    }

    /// <summary>
    /// A glyph's vertical advance (negative, y up), as hb_ot_get_glyph_v_advances: vmtx, varied by VVAR, else by gvar's phantom
    /// points; without vmtx, the font's ascender to descender.
    /// </summary>
    public int GetVAdvance(uint glyph)
    {
        VerticalTables vertical = Vertical;
        if (!vertical.HasVmtx)
        {
            (int ascender, int descender) = GetHExtents();
            return descender - ascender;
        }

        int advance = vertical.GetAdvance(glyph);
        if (_face.Instance is { } instance)
        {
            if (vertical.Store is not null)
            {
                _vvarScalars ??= vertical.Store.ComputeScalars(instance.Coords);
                return EmScaleY(-Math.Max(0, advance + RoundF(vertical.AdvanceDelta(glyph, _vvarScalars))));
            }

            if (_face.Glyf is not null && instance.Data.Gvar is not null)
            {
                if (glyph >= _face.GlyphCount)
                {
                    return 0;
                }

                return EmScaleY(-(TryGetPhantoms(glyph, out Phantoms p) ? Math.Max(0, RoundF(p.Top - p.Bottom)) : UnitsPerEm));
            }
        }

        return EmScaleY(-advance);
    }

    /// <summary>
    /// A glyph's vertical origin relative to its horizontal one, as hb_ot_get_glyph_v_origins: x at half the advance, y from VORG
    /// (varied by VVAR), else the top phantom point of a TrueType glyph with vmtx, else centering the ink on the font's height.
    /// </summary>
    public void GetVOrigin(uint glyph, out int x, out int y)
    {
        x = GetHAdvance(glyph) / 2;
        VerticalTables vertical = Vertical;
        if (vertical.HasVorg)
        {
            float origin = vertical.GetYOrigin(glyph);
            if (_face.Instance is { } instance && vertical.Store is not null)
            {
                _vvarScalars ??= vertical.Store.ComputeScalars(instance.Coords);
                origin += vertical.OriginDelta(glyph, _vvarScalars);
            }

            y = EmScalefY(origin);
            return;
        }

        if (vertical.HasVmtx && _face.Glyf is not null)
        {
            y = glyph >= _face.GlyphCount ? 0 : EmScalefY(TryGetPhantoms(glyph, out Phantoms p) ? p.Top : UnitsPerEm);
            return;
        }

        (int ascender, int descender) = GetHExtents();
        if (TryGetExtents(glyph, out ShapeExtents extents))
        {
            y = (int)Math.Clamp(extents.YBearing + (((long)ascender - descender + extents.Height) >> 1), int.MinValue, int.MaxValue);
        }
        else
        {
            y = ascender;
        }
    }

    /// <summary>
    /// The font's ascender and descender, as hb_ot_get_font_h_extents with HarfBuzz's fallback: OS/2's typographic values when it
    /// asks for them, else hhea's, varied by MVAR; without either, 0.8 and -0.2 em.
    /// </summary>
    public (int Ascender, int Descender) GetHExtents()
    {
        if (Volatile.Read(ref _hExtents) is { } known)
        {
            return (known.Item1, known.Item2);
        }

        VariationInstance? instance = _face.Instance;
        Os2Table os2 = _face.Os2;
        bool os2HasData = !_face.Os2Missing && (os2.WeightClass != 0 || os2.WidthClass != 0 || os2.FirstCharIndex != 0 || os2.LastCharIndex != 0);
        HheaTable? hhea = _face.Hhea;
        float? ascender = null, descender = null;
        if (os2HasData && os2.UseTypoMetrics)
        {
            ascender = os2.TypoAscender;
            descender = os2.TypoDescender;
        }
        else if (hhea is not null)
        {
            ascender = hhea.Ascender;
            descender = hhea.Descender;
        }

        (int, int) result;
        if (ascender is float a && descender is float d)
        {
            a += instance?.Mvar("hasc") ?? 0;
            d += instance?.Mvar("hdsc") ?? 0;
            result = (EmScalefY(MathF.Abs(a)), EmScalefY(-MathF.Abs(d)));
        }
        else
        {
            int fallback = (int)(YScale * .8);
            result = (fallback, fallback - YScale);
        }

        Volatile.Write(ref _hExtents, Tuple.Create(result.Item1, result.Item2));
        return result;
    }

    /// <summary>A glyph's ink box; false past the glyph count. TrueType's header box, or at a variation position the varied box rounded.</summary>
    public bool TryGetExtents(uint glyph, out ShapeExtents extents)
    {
        extents = default;
        if (glyph >= _face.GlyphCount)
        {
            return false;
        }

        GlyphExtents e = _face.GetGlyphExtents((ushort)glyph);
        if (e.XMin > e.XMax || e.YMin > e.YMax)
        {
            return true;
        }

        if (_face.Instance is not null)
        {
            // HarfBuzz rounds a varied outline's box (glyf and CFF2 alike).
            extents.XBearing = RoundF(e.XMin);
            extents.YBearing = RoundF(e.YMax);
            extents.Width = RoundF(e.XMax) - extents.XBearing;
            extents.Height = RoundF(e.YMin) - extents.YBearing;
        }
        else
        {
            int xMin = (int)MathF.Floor(e.XMin);
            int yMax = (int)MathF.Ceiling(e.YMax);
            extents.XBearing = xMin;
            extents.YBearing = yMax;
            extents.Width = (int)MathF.Ceiling(e.XMax) - xMin;
            extents.Height = (int)MathF.Floor(e.YMin) - yMax;
        }

        if (_xMult != 0x10000 || _yMult != 0x10000)
        {
            // As scale_glyph_extents.
            float x1 = extents.XBearing * _xMultF, y1 = extents.YBearing * _yMultF;
            float x2 = x1 + (extents.Width * _xMultF), y2 = y1 + (extents.Height * _yMultF);
            extents.XBearing = (int)MathF.Floor(x1);
            extents.YBearing = (int)MathF.Floor(y1);
            extents.Width = (int)MathF.Ceiling(x2) - extents.XBearing;
            extents.Height = (int)MathF.Ceiling(y2) - extents.YBearing;
        }

        return true;
    }

    /// <summary>The four phantom points of a TrueType glyph: left and right x, top and bottom y.</summary>
    private readonly record struct Phantoms(float Left, float Right, float Top, float Bottom);

    /// <summary>
    /// A TrueType glyph's phantom points at this position, as Glyph::get_points sets and varies them: from hmtx, vmtx and the glyph
    /// header, moved by gvar, a composite taking a USE_MY_METRICS component's.
    /// </summary>
    private bool TryGetPhantoms(uint glyph, out Phantoms phantoms)
    {
        try
        {
            phantoms = GetPhantoms((int)glyph, 0);
            return true;
        }
        catch (CorruptDataException)
        {
            phantoms = default;
            return false;
        }
    }

    private Phantoms GetPhantoms(int glyph, int depth)
    {
        if (depth > GlyfTable.MaxCompositeDepth)
        {
            throw FontErrors.Corrupt(-1, "Composite glyphs nest too deeply.");
        }

        GlyfTable glyf = _face.Glyf!;
        HmtxTable? hmtx = _face.Root.Hmtx;
        VerticalTables vertical = Vertical;
        ReadOnlySpan<byte> data = glyf.GetGlyphData(glyph, out long offset);
        int contours = 0, xMin = 0, yMax = 0, pointCount = 0;
        List<GlyphComponent>? components = null;
        if (!data.IsEmpty)
        {
            var r = new FontReader(data, offset);
            contours = r.ReadInt16();
            int x0 = r.ReadInt16(), y0 = r.ReadInt16(), x1 = r.ReadInt16(), y1 = r.ReadInt16();
            xMin = Math.Min(x0, x1);
            yMax = Math.Max(y0, y1);
            if (contours > 0)
            {
                r.Skip(2 * (contours - 1));
                pointCount = r.ReadUInt16() + 1;
            }
            else if (contours < 0)
            {
                components = GlyfTable.ReadComponents(ref r);
                pointCount = components.Count;
            }
        }

        int lsb = hmtx?.GetLeftSideBearing(glyph) ?? 0;
        int hDelta = xMin - lsb;
        int vOrigin = yMax + vertical.GetTopBearing((uint)glyph);
        float left = hDelta, right = (hmtx?.GetAdvance(glyph) ?? 0) + hDelta;
        float top = vOrigin, bottom = vOrigin - vertical.GetAdvance((uint)glyph);
        if (_face.Instance is { } instance && instance.Data.Gvar is GvarTable gvar)
        {
            int n = pointCount + 4;
            float[] x = new float[n], y = new float[n], dx = new float[n], dy = new float[n];
            x[n - 4] = left;
            x[n - 3] = right;
            y[n - 2] = top;
            y[n - 1] = bottom;
            if (gvar.AddDeltas(glyph, instance.Coords, x, y, [], dx, dy))
            {
                left += dx[n - 4];
                right += dx[n - 3];
                top += dy[n - 2];
                bottom += dy[n - 1];
            }
        }

        Phantoms result = new(left, right, top, bottom);
        if (components is not null)
        {
            foreach (GlyphComponent component in components)
            {
                if ((component.Flags & UseMyMetrics) != 0)
                {
                    result = GetPhantoms(component.Glyph, depth + 1);
                }
            }
        }

        return result;
    }
}
