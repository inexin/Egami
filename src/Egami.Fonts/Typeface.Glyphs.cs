using Egami.Fonts.Cff;
using Egami.Fonts.Layout;
using Egami.Fonts.Parsing;
using Egami.Fonts.Tables;
using Egami.Fonts.Variations;

namespace Egami.Fonts;

public sealed partial class Typeface
{
    private MaxpTable? _maxp;
    private GlyfTable? _glyf;
    private CffFont? _cff;
    private CmapTable? _cmap;
    private Box<HmtxTable?>? _hmtx;
    private Kerning? _kerning;
    private Box<FontExtents>? _extents;
    private float[]? _cffBounds;
    private int[]? _cffBoundsReady;
    private float[]? _cffWidths;

    /// <summary>Gets the face's vertical metrics and decoration positions in font units; see <see cref="FontExtents"/> for the rule.</summary>
    public FontExtents Extents => (Volatile.Read(ref _extents) ?? Publish(ref _extents, new Box<FontExtents>(ComputeExtents()))).Value;

    /// <summary>Gets the hhea table's ascender, descender and line gap; zeros when the font has no hhea.</summary>
    public VerticalMetrics HheaMetrics => Hhea is HheaTable h ? new VerticalMetrics(h.Ascender, h.Descender, h.LineGap) : default;

    /// <summary>Gets the OS/2 typo ascender, descender and line gap; zeros when the font has no OS/2.</summary>
    public VerticalMetrics TypoMetrics => new(Os2.TypoAscender, Os2.TypoDescender, Os2.TypoLineGap);

    /// <summary>Gets the OS/2 Windows ascent and descent (the descent made negative), with a zero line gap.</summary>
    public VerticalMetrics WinMetrics => new(Os2.WinAscent, -Os2.WinDescent, 0);

    /// <summary>Gets whether the face has pair kerning, from GPOS or the kern table.</summary>
    public bool HasKerning => !Kerning.IsEmpty;

    /// <summary>Gets whether the face maps characters to glyphs; false for a bare CFF font or a font without a usable cmap.</summary>
    public bool HasCharacterMap => !Cmap.IsEmpty;

    internal CmapTable Cmap => _base?.Cmap ?? Volatile.Read(ref _cmap) ?? Publish(ref _cmap, LoadTable(Tags.Cmap, (data, offset) => CmapTable.Read(data, offset, FontPage), CmapTable.Empty, "glyphs can be reached by id only"));

    internal Kerning Kerning => Volatile.Read(ref _kerning) ?? Publish(ref _kerning, _instance is null ? LoadKerning() : LoadVariedKerning());

    internal GlyfTable? Glyf => _glyf;

    /// <summary>
    /// Gets whether a glyph's contours may overlap one another: any glyph of a variable font, whose outlines keep their overlaps
    /// so they can vary, and a TrueType glyph marked OVERLAP_SIMPLE or OVERLAP_COMPOUND. Rasterizers that add up coverage draw
    /// such overlaps too dark at their edges unless the outline is merged first.
    /// </summary>
    internal bool MayOverlap(ushort glyph)
    {
        if (IsVariable)
        {
            return true;
        }

        if (_glyf is null)
        {
            return false;
        }

        try
        {
            ReadOnlySpan<byte> data = _glyf.GetGlyphData(glyph, out _);
            if (data.Length < 10)
            {
                return false;
            }

            int contours = BigEndian.Int16(data, 0);
            if (contours < 0)
            {
                return (BigEndian.UInt16(data, 10) & 0x0400) != 0;
            }

            int instructions = BigEndian.UInt16(data, 10 + (contours * 2));
            return contours > 0 && (BigEndian.UInt8(data, 12 + (contours * 2) + instructions) & 0x40) != 0;
        }
        catch (CorruptDataException)
        {
            return false;
        }
    }

    internal CffFont? Cff => _cff;

    internal MaxpTable? Maxp => _maxp;

    internal HmtxTable? Hmtx => _base is not null ? _base.Hmtx : (Volatile.Read(ref _hmtx) ?? Publish(ref _hmtx, new Box<HmtxTable?>(LoadHmtx()))).Value;

    /// <summary>Maps a Unicode code point to a glyph id through the character map.</summary>
    /// <param name="codePoint">The code point, 0 to 0x10FFFF.</param>
    /// <returns>The glyph, or 0 (.notdef) when the face has none for the code point.</returns>
    public ushort GetGlyph(int codePoint)
    {
        int glyph = Cmap.Map(codePoint);
        return (uint)glyph < (uint)GlyphCount ? (ushort)glyph : (ushort)0;
    }

    /// <summary>Maps a code point followed by a variation selector, such as U+FE0E, through the format 14 subtable.</summary>
    /// <param name="codePoint">The base code point.</param>
    /// <param name="variationSelector">The selector, U+FE00 to U+FE0F or U+E0100 to U+E01EF.</param>
    /// <returns>The glyph for the sequence, else the base glyph, else 0.</returns>
    public ushort GetGlyph(int codePoint, int variationSelector)
    {
        int glyph = Cmap.MapVariation(codePoint, variationSelector);
        return glyph > 0 && glyph < GlyphCount ? (ushort)glyph : GetGlyph(codePoint);
    }

    /// <summary>Maps a code point to a glyph, saying whether the face has one.</summary>
    /// <param name="codePoint">The code point.</param>
    /// <param name="glyph">The glyph, or 0 when there is none.</param>
    /// <returns><see langword="true"/> if the face maps the code point to a glyph other than .notdef.</returns>
    public bool TryGetGlyph(int codePoint, out ushort glyph)
    {
        glyph = GetGlyph(codePoint);
        return glyph != 0;
    }

    /// <summary>
    /// Maps text to glyphs, one per code point: surrogate pairs become one glyph, and a variation selector changes the glyph before
    /// it instead of adding one.
    /// </summary>
    /// <param name="text">The UTF-16 text.</param>
    /// <param name="glyphs">The glyphs, at least as long as <paramref name="text"/>.</param>
    /// <returns>The number of glyphs written.</returns>
    /// <exception cref="ArgumentException"><paramref name="glyphs"/> is shorter than <paramref name="text"/>.</exception>
    public int MapCodePoints(ReadOnlySpan<char> text, Span<ushort> glyphs)
    {
        if (glyphs.Length < text.Length)
        {
            throw new ArgumentException("The glyph span must be at least as long as the text.", nameof(glyphs));
        }

        int count = 0, previous = -1;
        for (int i = 0; i < text.Length; i++)
        {
            int cp = ReadCodePoint(text, ref i);
            if (IsVariationSelector(cp) && previous >= 0 && count > 0)
            {
                glyphs[count - 1] = GetGlyph(previous, cp);
                continue;
            }

            glyphs[count++] = GetGlyph(cp);
            previous = cp;
        }

        return count;
    }

    /// <summary>Sends a glyph's outline to <paramref name="sink"/> in font units with y up.</summary>
    /// <param name="glyph">The glyph id.</param>
    /// <param name="sink">The receiver.</param>
    /// <returns>
    /// <see langword="true"/> if the outline was sent (a glyph without ink sends nothing); <see langword="false"/> if the glyph id is
    /// past <see cref="GlyphCount"/> or its data is damaged, which raises <c>Font.BadGlyph</c> (or throws in strict mode).
    /// </returns>
    public bool GetGlyphOutline(ushort glyph, IGlyphOutlineSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        OutlineRecorder recorder = OutlineRecorder.Take();
        try
        {
            if (!TryRecordOutline(glyph, recorder))
            {
                return false;
            }

            recorder.ReplayTo(sink);
            return true;
        }
        finally
        {
            recorder.Release();
        }
    }

    /// <summary>Gets a glyph's advance width in font units: hmtx, or for a bare CFF font the given or charstring width.</summary>
    /// <param name="glyph">The glyph id.</param>
    /// <returns>The advance; 0 for a glyph id past <see cref="GlyphCount"/>.</returns>
    public float GetAdvance(ushort glyph)
    {
        if (glyph >= GlyphCount)
        {
            return 0;
        }

        if (_instance is not null)
        {
            return VariedAdvance(glyph);
        }

        if (Hmtx is HmtxTable hmtx)
        {
            return hmtx.GetAdvance(glyph);
        }

        if (_cff is not null)
        {
            return CffWidth(glyph);
        }

        return UnitsPerEm / 2f;
    }

    /// <summary>Gets a glyph's left side bearing in font units: hmtx, or the left edge of its outline when there is no hmtx.</summary>
    /// <param name="glyph">The glyph id.</param>
    /// <returns>The bearing; 0 for a glyph id past <see cref="GlyphCount"/>.</returns>
    public float GetLeftSideBearing(ushort glyph)
    {
        if (glyph >= GlyphCount)
        {
            return 0;
        }

        if (_instance is not null)
        {
            return VariedLeftSideBearing(glyph);
        }

        return Hmtx is HmtxTable hmtx ? hmtx.GetLeftSideBearing(glyph) : GetGlyphExtents(glyph).XMin;
    }

    /// <summary>
    /// Gets the box around a glyph's ink in font units: from the glyph header for TrueType, from the curves' extremes for CFF.
    /// </summary>
    /// <param name="glyph">The glyph id.</param>
    /// <returns>The box; empty for a glyph without ink, past <see cref="GlyphCount"/> or damaged.</returns>
    public GlyphExtents GetGlyphExtents(ushort glyph)
    {
        if (glyph >= GlyphCount)
        {
            return GlyphExtents.Empty;
        }

        if (_instance is not null)
        {
            return Varied(glyph).Bounds;
        }

        if (_glyf is not null)
        {
            try
            {
                GlyphExtents header = _glyf.GetHeaderBounds(glyph);
                if (header.XMin <= header.XMax && header.YMin <= header.YMax)
                {
                    return header;
                }
            }
            catch (CorruptDataException) when (!_options.Strict)
            {
                return GlyphExtents.Empty;
            }
        }

        return CffBounds(glyph);
    }

    /// <summary>Gets the pair kerning between two glyphs in font units: GPOS pair positioning, else the kern table.</summary>
    /// <param name="left">The first glyph.</param>
    /// <param name="right">The glyph after it.</param>
    /// <returns>The adjustment to the first glyph's advance, usually negative; 0 when the pair is not kerned.</returns>
    public float GetKerning(ushort left, ushort right) => Kerning.Get(left, right);

    /// <summary>The exact box of a glyph's outline slanted by x += <paramref name="skew"/> times y, in font units.</summary>
    internal GlyphExtents GetSkewedExtents(ushort glyph, float skew)
    {
        OutlineRecorder recorder = OutlineRecorder.Take();
        try
        {
            if (!TryRecordOutline(glyph, recorder))
            {
                return GlyphExtents.Empty;
            }

            recorder.Skew(skew);
            return recorder.ComputeBounds();
        }
        finally
        {
            recorder.Release();
        }
    }

    internal bool TryRecordOutline(int glyph, OutlineRecorder recorder)
    {
        if ((uint)glyph >= (uint)GlyphCount)
        {
            return false;
        }

        if (_instance is not null)
        {
            VariedGlyph varied = Varied(glyph);
            recorder.Append(varied.Verbs, varied.Coordinates);
            return !ReferenceEquals(varied, VariedGlyph.Empty);
        }

        try
        {
            if (_glyf is not null)
            {
                _glyf.GetOutline(glyph, recorder);
            }
            else if (_cff is not null)
            {
                CharStringInterpreter.For(_cff).Draw(glyph, recorder);
            }

            return true;
        }
        catch (CorruptDataException e)
        {
            recorder.Clear();
            if (_options.Strict)
            {
                throw;
            }

            Report(_options, "Font.BadGlyph", $"Glyph {glyph} cannot be read ({e.Message}); it draws empty.", FaceOffset);
            return false;
        }
    }

    internal static int ReadCodePoint(ReadOnlySpan<char> text, ref int i)
    {
        char c = text[i];
        if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
        {
            return char.ConvertToUtf32(c, text[++i]);
        }

        return char.IsSurrogate(c) ? 0xFFFD : c;
    }

    internal static bool IsVariationSelector(int cp) => cp is (>= 0xFE00 and <= 0xFE0F) or (>= 0xE0100 and <= 0xE01EF);

    private float CffWidth(int glyph)
    {
        if (_cffOptions?.Widths is IReadOnlyList<float> given && glyph < given.Count)
        {
            return given[glyph];
        }

        float[] widths = Volatile.Read(ref _cffWidths) ?? Publish(ref _cffWidths, NewNaNArray(GlyphCount));
        float width = Volatile.Read(ref widths[glyph]);
        if (float.IsNaN(width))
        {
            try
            {
                width = (float)CharStringInterpreter.For(_cff!).GetWidth(glyph);
            }
            catch (CorruptDataException) when (!_options.Strict)
            {
                width = (float)_cff!.Privates[_cff.FontDictOf(glyph)].DefaultWidthX;
            }

            Volatile.Write(ref widths[glyph], width);
        }

        return width;
    }

    private GlyphExtents CffBounds(int glyph)
    {
        if (_cff is null)
        {
            return GlyphExtents.Empty;
        }

        float[] bounds = Volatile.Read(ref _cffBounds) ?? Publish(ref _cffBounds, new float[GlyphCount * 4]);
        int[] ready = Volatile.Read(ref _cffBoundsReady) ?? Publish(ref _cffBoundsReady, new int[GlyphCount]);
        int at = glyph * 4;
        if (Volatile.Read(ref ready[glyph]) == 0)
        {
            OutlineRecorder recorder = OutlineRecorder.Take();
            try
            {
                GlyphExtents e = TryRecordOutline(glyph, recorder) ? recorder.ComputeBounds() : GlyphExtents.Empty;
                bounds[at] = e.XMin;
                bounds[at + 1] = e.YMin;
                bounds[at + 2] = e.XMax;
                bounds[at + 3] = e.YMax;
                Volatile.Write(ref ready[glyph], 1);
                return e;
            }
            finally
            {
                recorder.Release();
            }
        }

        return new GlyphExtents(bounds[at], bounds[at + 1], bounds[at + 2], bounds[at + 3]);
    }

    private static float[] NewNaNArray(int length)
    {
        var array = new float[length];
        Array.Fill(array, float.NaN);
        return array;
    }

    private HmtxTable? LoadHmtx()
    {
        if (IsBareCff)
        {
            return null;
        }

        if (Hhea is not HheaTable hhea)
        {
            return null;
        }

        return LoadTable<HmtxTable?>(Tags.Hmtx, (data, offset) => HmtxTable.Read(data, offset, hhea.NumberOfHMetrics, GlyphCount, Repair), null, "advances are half an em, or the CFF widths");
    }

    private Kerning LoadKerning()
    {
        if (IsBareCff)
        {
            return Kerning.None;
        }

        ReadOnlySpan<byte> gpos = GetTableSpan(Tags.Gpos, out int gposOffset);
        ReadOnlySpan<byte> kern = GetTableSpan(Tags.Kern, out int kernOffset);
        try
        {
            return Kerning.Read(gpos, gposOffset, kern, kernOffset);
        }
        catch (CorruptDataException e) when (!_options.Strict)
        {
            Repair("Font.BadTable", $"The kerning data is damaged ({e.Message}); text is not kerned.", gposOffset >= 0 ? gposOffset : kernOffset);
            return Kerning.None;
        }
    }

    private FontExtents ComputeExtents()
    {
        Os2Table os2 = Os2;
        HheaTable? hhea = Hhea;
        bool haveOs2 = !Os2Missing;

        // A position on a variable font varies the OS/2 and post values through MVAR, rounded as an instancer writes them.
        int typoAscender = WithMvar(os2.TypoAscender, "hasc"), typoDescender = WithMvar(os2.TypoDescender, "hdsc"), typoLineGap = WithMvar(os2.TypoLineGap, "hlgp");
        int winAscent = WithMvar(os2.WinAscent, "hcla"), winDescent = WithMvar(os2.WinDescent, "hcld");
        int ascent, descent, lineGap;
        if (haveOs2 && os2.UseTypoMetrics && (typoAscender != 0 || typoDescender != 0))
        {
            (ascent, descent, lineGap) = (typoAscender, typoDescender, typoLineGap);
        }
        else if (hhea is not null && (hhea.Ascender != 0 || hhea.Descender != 0))
        {
            (ascent, descent, lineGap) = (hhea.Ascender, hhea.Descender, hhea.LineGap);
        }
        else if (haveOs2 && (typoAscender != 0 || typoDescender != 0))
        {
            (ascent, descent, lineGap) = (typoAscender, typoDescender, typoLineGap);
        }
        else if (haveOs2 && (winAscent != 0 || winDescent != 0))
        {
            (ascent, descent, lineGap) = (winAscent, -winDescent, 0);
        }
        else
        {
            (ascent, descent, lineGap) = (_head.YMax, _head.YMin, 0);
            if (ascent == 0 && descent == 0)
            {
                (ascent, descent) = ((int)(UnitsPerEm * 0.8), -(int)(UnitsPerEm * 0.2));
            }
        }

        int os2CapHeight = WithMvar(os2.CapHeight, "cpht"), os2XHeight = WithMvar(os2.XHeight, "xhgt");
        float capHeight = haveOs2 && os2.Version >= 2 && os2CapHeight > 0 ? os2CapHeight : InkTop('H', ascent * 0.7f);
        float xHeight = haveOs2 && os2.Version >= 2 && os2XHeight > 0 ? os2XHeight : InkTop('x', ascent * 0.5f);
        PostTable post = Post;
        int postThickness = WithMvar(post.UnderlineThickness, "unds"), postPosition = WithMvar(post.UnderlinePosition, "undo");
        int os2StrikeoutSize = WithMvar(os2.StrikeoutSize, "strs"), os2StrikeoutPosition = WithMvar(os2.StrikeoutPosition, "stro");
        float underlineThickness = postThickness > 0 ? postThickness : UnitsPerEm / 14f;
        float underlinePosition = postPosition != 0 ? postPosition : -UnitsPerEm / 10f;
        float strikeoutSize = haveOs2 && os2StrikeoutSize > 0 ? os2StrikeoutSize : underlineThickness;
        float strikeoutPosition = haveOs2 && os2StrikeoutPosition != 0 ? os2StrikeoutPosition : (xHeight / 2) + (strikeoutSize / 2);
        float italicAngle = IsBareCff ? (float)(_cff?.Top.GetNumber(1202, 0) ?? 0) : post.ItalicAngle;
        var box = new GlyphExtents(_head.XMin, _head.YMin, _head.XMax, _head.YMax);
        return new FontExtents(ascent, descent, lineGap, capHeight, xHeight, underlinePosition, underlineThickness, strikeoutPosition, strikeoutSize, italicAngle, box);

        int WithMvar(int value, string tag) => _instance is null ? value : (int)MathF.Floor(value + _instance.Mvar(tag) + 0.5f);

        float InkTop(char c, float fallback)
        {
            ushort glyph = GetGlyph(c);
            if (glyph == 0)
            {
                return fallback;
            }

            GlyphExtents e = GetGlyphExtents(glyph);
            return e.IsEmpty ? fallback : e.YMax;
        }
    }
}
