using static Egami.Fonts.Shaping.OtData;

namespace Egami.Fonts.Shaping;

/// <summary>The glyph property bits the layout engine keeps per glyph, as HarfBuzz's.</summary>
internal static class GlyphProps
{
    public const int BaseGlyph = 0x02;
    public const int Ligature = 0x04;
    public const int Mark = 0x08;
    public const int ClassMask = BaseGlyph | Ligature | Mark;
    public const int Substituted = 0x10;
    public const int Ligated = 0x20;
    public const int Multiplied = 0x40;
    public const int Preserve = Substituted | Ligated | Multiplied;
}

/// <summary>The lookup flag bits.</summary>
internal static class LookupFlag
{
    public const int RightToLeft = 0x0001;
    public const int IgnoreBaseGlyphs = 0x0002;
    public const int IgnoreLigatures = 0x0004;
    public const int IgnoreMarks = 0x0008;
    public const int IgnoreFlags = 0x000E;
    public const int UseMarkFilteringSet = 0x0010;
    public const int MarkAttachmentType = 0xFF00;
}

/// <summary>The GDEF table: glyph classes, mark attachment classes, mark glyph sets, ligature carets and the variation store.</summary>
internal sealed class GdefTable
{
    public static readonly GdefTable Empty = new(ReadOnlyMemory<byte>.Empty);

    private readonly ReadOnlyMemory<byte> _data;
    private readonly int _glyphClassDef;
    private readonly int _markAttachClassDef;
    private readonly int _markGlyphSets;
    private readonly int _ligCaretList;
    private readonly int _markSetCount;

    /// <summary>A glyph's properties not looked up yet; real ones never set bit 0.</summary>
    private const ushort UnknownProps = 0xFFFF;

    private ushort[]? _props;

    public GdefTable(ReadOnlyMemory<byte> data)
    {
        _data = data;
        ReadOnlySpan<byte> d = data.Span;
        if (d.Length < 12)
        {
            return;
        }

        int major = U16(d, 0), minor = U16(d, 2);
        if (major != 1)
        {
            return;
        }

        _glyphClassDef = Off16(d, 0, 4);
        _ligCaretList = Off16(d, 0, 8);
        _markAttachClassDef = Off16(d, 0, 10);
        if (minor >= 2)
        {
            _markGlyphSets = Off16(d, 0, 12);
            if (_markGlyphSets != 0 && U16(d, _markGlyphSets) == 1)
            {
                _markSetCount = U16(d, _markGlyphSets + 2);
            }
        }

        if (minor >= 3)
        {
            VarStoreOffset = Off32(d, 0, 14);
        }
    }

    /// <summary>Gets whether the font assigns glyph classes.</summary>
    public bool HasGlyphClasses => _glyphClassDef != 0;

    /// <summary>Gets the number of mark glyph sets.</summary>
    public int MarkSetCount => _markSetCount;

    /// <summary>Gets the item variation store's offset, or 0.</summary>
    public int VarStoreOffset { get; }

    public ReadOnlyMemory<byte> Data => _data;

    public int LigCaretListOffset => _ligCaretList;

    /// <summary>
    /// The caret positions inside a ligature glyph from the LigCaretList, in font units along the run (formats 1 and 3, whose
    /// coordinates are fixed; format 2's contour points are left out).
    /// </summary>
    /// <returns>The number of carets written, 0 when the font gives none for the glyph.</returns>
    public int GetLigatureCarets(uint glyph, Span<short> carets)
    {
        ReadOnlySpan<byte> d = _data.Span;
        if (_ligCaretList == 0 || _ligCaretList + 4 > d.Length)
        {
            return 0;
        }

        int index = Coverage(d, Off16(d, _ligCaretList, _ligCaretList), glyph);
        if (index < 0 || index >= U16(d, _ligCaretList + 2))
        {
            return 0;
        }

        int ligGlyph = Off16(d, _ligCaretList, _ligCaretList + 4 + (index * 2));
        if (ligGlyph == 0 || ligGlyph + 2 > d.Length)
        {
            return 0;
        }

        int count = Math.Min(U16(d, ligGlyph), carets.Length);
        for (int i = 0; i < count; i++)
        {
            int value = Off16(d, ligGlyph, ligGlyph + 2 + (i * 2));
            if (value == 0 || value + 4 > d.Length || U16(d, value) == 2)
            {
                return 0;
            }

            carets[i] = S16(d, value + 2);
        }

        return count;
    }

    /// <summary>The glyph's class: GDEF class 1 to 4 (0 when unclassified).</summary>
    public int GetGlyphClass(uint glyph) => ClassOf(_data.Span, _glyphClassDef, glyph);

    /// <summary>The glyph's properties: base, ligature or mark bits, and a mark's attachment class in the high byte.</summary>
    public int GetGlyphProps(uint glyph)
    {
        // Each glyph's properties are looked up in the class tables once and kept; a glyph past every classed one has none.
        ushort[] props = Volatile.Read(ref _props) ?? CreateProps();
        if (glyph >= (uint)props.Length)
        {
            return 0;
        }

        int known = props[glyph];
        if (known == UnknownProps)
        {
            known = LookUpGlyphProps(glyph);
            props[glyph] = (ushort)known;
        }

        return known;
    }

    private ushort[] CreateProps()
    {
        ushort[] props = new ushort[LastClassedGlyph(_data.Span, _glyphClassDef) + 1];
        props.AsSpan().Fill(UnknownProps);
        return Interlocked.CompareExchange(ref _props, props, null) ?? props;
    }

    /// <summary>The highest glyph a class definition table gives a class, or −1.</summary>
    private static int LastClassedGlyph(ReadOnlySpan<byte> d, int t)
    {
        if (t == 0)
        {
            return -1;
        }

        switch (U16(d, t))
        {
            case 1:
                return U16(d, t + 4) == 0 ? -1 : U16(d, t + 2) + U16(d, t + 4) - 1;
            case 2:
                int last = -1;
                for (int i = 0, count = U16(d, t + 2); i < count; i++)
                {
                    last = Math.Max(last, U16(d, t + 4 + (i * 6) + 2));
                }

                return last;
            default:
                return -1;
        }
    }

    /// <summary>The glyph's properties from the class tables, as <see cref="GetGlyphProps"/> keeps them.</summary>
    internal int LookUpGlyphProps(uint glyph)
    {
        ReadOnlySpan<byte> d = _data.Span;
        switch (ClassOf(d, _glyphClassDef, glyph))
        {
            case 1:
                return GlyphProps.BaseGlyph;
            case 2:
                return GlyphProps.Ligature;
            case 3:
                return GlyphProps.Mark | (ClassOf(d, _markAttachClassDef, glyph) << 8);
            default:
                return 0;
        }
    }

    /// <summary>Whether mark glyph set <paramref name="set"/> covers the glyph.</summary>
    public bool MarkSetCovers(int set, uint glyph)
    {
        if ((uint)set >= (uint)_markSetCount)
        {
            return false;
        }

        ReadOnlySpan<byte> d = _data.Span;
        int coverage = Off32(d, _markGlyphSets, _markGlyphSets + 4 + (set * 4));
        return Coverage(d, coverage, glyph) >= 0;
    }

    /// <summary>Clears UseMarkFilteringSet when the lookup names a set the font does not have.</summary>
    public int SanitizeLookupProps(int props)
    {
        if ((props & LookupFlag.UseMarkFilteringSet) != 0 && (props >>> 16) >= _markSetCount)
        {
            props &= ~LookupFlag.UseMarkFilteringSet;
        }

        return props;
    }
}
