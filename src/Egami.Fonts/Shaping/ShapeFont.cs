namespace Egami.Fonts.Shaping;

/// <summary>A glyph's ink box as HarfBuzz reports it: bearings from the origin, a width and a (negative, y up) height.</summary>
internal struct ShapeExtents
{
    public int XBearing;
    public int YBearing;
    public int Width;
    public int Height;
}

/// <summary>The font facts the shaper asks for, in font units, over a <see cref="Typeface"/> and its layout tables.</summary>
internal sealed class ShapeFont
{
    private readonly Typeface _face;

    public ShapeFont(Typeface face)
    {
        _face = face;
        Layout = OpenTypeLayoutTables.For(face);
        UnitsPerEm = face.UnitsPerEm;
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

    public int GlyphCount => _face.GlyphCount;

    /// <summary>The cmap glyph of a code point; false when the font has none.</summary>
    public bool TryGetNominalGlyph(int codePoint, out uint glyph)
    {
        // As HarfBuzz, a mapped glyph id is used as the font gives it, even past the glyph count.
        int g = _face.Cmap.Map(codePoint);
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
        int g = _face.Cmap.MapVariation(codePoint, selector);
        if (g > 0)
        {
            glyph = (uint)g;
            return true;
        }

        glyph = 0;
        return false;
    }

    public int GetHAdvance(uint glyph) => glyph < _face.GlyphCount ? (int)MathF.Round(_face.GetAdvance((ushort)glyph)) : 0;

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

        int xMin = (int)MathF.Floor(e.XMin);
        int yMax = (int)MathF.Ceiling(e.YMax);
        extents.XBearing = xMin;
        extents.YBearing = yMax;
        extents.Width = (int)MathF.Ceiling(e.XMax) - xMin;
        extents.Height = (int)MathF.Floor(e.YMin) - yMax;
        return true;
    }
}
