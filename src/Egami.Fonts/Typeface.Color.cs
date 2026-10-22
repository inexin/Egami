using System.IO.Compression;

using Egami.Fonts.Color;
using Egami.Fonts.Parsing;
using Egami.Pixels;

namespace Egami.Fonts;

public sealed partial class Typeface
{
    private static readonly FontTag s_colr = new("COLR");
    private static readonly FontTag s_cpal = new("CPAL");
    private static readonly FontTag s_sbix = new("sbix");
    private static readonly FontTag s_cbdt = new("CBDT");
    private static readonly FontTag s_cblc = new("CBLC");
    private static readonly FontTag s_svg = new("SVG ");

    private Box<ColrTable?>? _colr;
    private Box<CpalTable?>? _cpal;
    private float[]? _colrScalars;
    private Box<SbixTable?>? _sbixTable;
    private Box<CbdtTables?>? _cbdtTables;
    private FontPaletteInfo[]? _palettes;
    private int _colorFormats = -1;

    /// <summary>Gets the kinds of color glyph data the face holds; <see cref="ColorGlyphFormats.None"/> for a plain outline font.</summary>
    public ColorGlyphFormats ColorFormats
    {
        get
        {
            // Asked for every run itemized; worked out once per face.
            int known = Volatile.Read(ref Root._colorFormats);
            if (known >= 0)
            {
                return (ColorGlyphFormats)known;
            }

            ColorGlyphFormats formats = ColorGlyphFormats.None;
            if (Colr is ColrTable colr)
            {
                formats |= (colr.HasV0 ? ColorGlyphFormats.ColrV0 : 0) | (colr.HasV1 ? ColorGlyphFormats.ColrV1 : 0);
            }

            formats |= (Sbix is not null ? ColorGlyphFormats.Sbix : 0) | (Cbdt is not null ? ColorGlyphFormats.Cbdt : 0);
            formats |= HasTable(s_svg) ? ColorGlyphFormats.Svg : 0;
            Volatile.Write(ref Root._colorFormats, (int)formats);
            return formats;
        }
    }

    /// <summary>Gets the face's CPAL palettes; empty when it has none.</summary>
    public IReadOnlyList<FontPaletteInfo> Palettes => Root._palettes ??= Root.Cpal.Describe(Name);

    internal ColrTable? Colr => Root.LoadOnce(ref Root._colr, () => TryGetTable(s_colr, out ReadOnlyMemory<byte> t) ? ColrTable.Read(t, TableOffset(s_colr)) : null, s_colr, "glyphs draw as outlines");

    internal CpalTable Cpal => Root.LoadOnce(ref Root._cpal, () => TryGetTable(s_cpal, out ReadOnlyMemory<byte> t) ? CpalTable.Read(t.Span, TableOffset(s_cpal)) : CpalTable.Empty, s_cpal, "palette colors are transparent") ?? CpalTable.Empty;

    internal SbixTable? Sbix => Root.LoadOnce(ref Root._sbixTable, () => TryGetTable(s_sbix, out ReadOnlyMemory<byte> t) ? SbixTable.Read(t, TableOffset(s_sbix), GlyphCount) : null, s_sbix, "its glyphs draw as outlines");

    internal CbdtTables? Cbdt => Root.LoadOnce(ref Root._cbdtTables, () => TryGetTable(s_cblc, out ReadOnlyMemory<byte> l) && TryGetTable(s_cbdt, out ReadOnlyMemory<byte> d) ? CbdtTables.Read(l, d, TableOffset(s_cblc)) : null, s_cblc, "its glyphs draw as outlines");

    /// <summary>
    /// Gets a glyph's color description in the first format that has it: COLR version 1, then version 0, then sbix, then CBDT
    /// (OpenType SVG glyphs are not drawn; see <see cref="TryGetSvgDocument"/>). COLR graphs are read at this face's variation
    /// position with the palette's colors; bitmaps come from the strike for <paramref name="pixelsPerEm"/>.
    /// </summary>
    /// <param name="glyph">The glyph.</param>
    /// <param name="palette">The palette, or <see langword="null"/> for <see cref="FontPalette.Default"/>.</param>
    /// <param name="colorGlyph">The color glyph, or <see langword="null"/>.</param>
    /// <param name="pixelsPerEm">The size the glyph is drawn at, for choosing a bitmap strike: the smallest at or above it, else the largest; 0 for the largest.</param>
    /// <returns><see langword="true"/> if the glyph has color data; otherwise it draws as its outline.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="color"/>
    /// </example>
    public bool TryGetColorGlyph(ushort glyph, FontPalette? palette, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ColorGlyph? colorGlyph, float pixelsPerEm = 0)
    {
        colorGlyph = null;
        if (glyph >= GlyphCount)
        {
            return false;
        }

        try
        {
            if (Colr is ColrTable colr && (colr.HasV1Glyph(glyph) || colr.HasV0Glyph(glyph)))
            {
                var context = new ColorContext(PaletteColors(palette ?? FontPalette.Default), ColrScalars, message => Report(_options, "Font.BadColorGlyph", $"Glyph {glyph}: {message}", FaceOffset));
                colorGlyph = colr.ReadV1(glyph, context) ?? colr.ReadV0(glyph, context);
                if (colorGlyph is not null)
                {
                    return true;
                }
            }

            colorGlyph = Sbix?.Get(glyph, pixelsPerEm, UnitsPerEm) ?? (ColorGlyph?)Cbdt?.Get(glyph, pixelsPerEm, UnitsPerEm);
            return colorGlyph is not null;
        }
        catch (CorruptDataException e) when (!_options.Strict)
        {
            Report(_options, "Font.BadColorGlyph", $"The color data of glyph {glyph} cannot be read ({e.Message}); it draws as its outline.", FaceOffset);
            colorGlyph = null;
            return false;
        }
    }

    /// <summary>Gets whether a glyph has color data in any format this face draws (not counting SVG), without reading it.</summary>
    /// <param name="glyph">The glyph.</param>
    /// <returns><see langword="true"/> if it has.</returns>
    public bool HasColorGlyph(ushort glyph)
    {
        if (glyph >= GlyphCount)
        {
            return false;
        }

        try
        {
            return (Colr is ColrTable colr && (colr.HasV1Glyph(glyph) || colr.HasV0Glyph(glyph))) || Sbix?.HasGlyph(glyph) == true || Cbdt?.HasGlyph(glyph) == true;
        }
        catch (CorruptDataException) when (!_options.Strict)
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the OpenType SVG document holding a glyph, decompressed when it is gzipped, for callers that draw SVG themselves; Egami
    /// draws such glyphs from their COLR, bitmap or outline data instead (SVG drawing arrives with Egami.Svg). The glyph's
    /// element in it has the id <c>glyph</c> followed by the glyph id.
    /// </summary>
    /// <param name="glyph">The glyph.</param>
    /// <param name="document">The document's UTF-8 bytes.</param>
    /// <param name="firstGlyph">The first glyph the document covers.</param>
    /// <param name="lastGlyph">The last glyph the document covers.</param>
    /// <returns><see langword="true"/> if the face has an SVG document for the glyph.</returns>
    /// <exception cref="CorruptDataException">The document is damaged and strict loading is set.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="color"/>
    /// </example>
    public bool TryGetSvgDocument(ushort glyph, out ReadOnlyMemory<byte> document, out ushort firstGlyph, out ushort lastGlyph)
    {
        document = default;
        firstGlyph = lastGlyph = 0;
        ReadOnlySpan<byte> svg = GetTableSpan(s_svg, out int offset);
        if (offset < 0)
        {
            return false;
        }

        try
        {
            int list = (int)BigEndian.UInt32(svg, 2);
            int count = BigEndian.UInt16(svg, list);
            int lo = 0, hi = count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int at = list + 2 + (mid * 12);
                int start = BigEndian.UInt16(svg, at), end = BigEndian.UInt16(svg, at + 2);
                if (glyph < start)
                {
                    hi = mid - 1;
                }
                else if (glyph > end)
                {
                    lo = mid + 1;
                }
                else
                {
                    uint docOffset = BigEndian.UInt32(svg, at + 4), length = BigEndian.UInt32(svg, at + 8);
                    ReadOnlySpan<byte> doc = BigEndian.Slice(svg, list + (long)docOffset, length);
                    document = doc.Length >= 2 && doc[0] == 0x1F && doc[1] == 0x8B ? Gunzip(doc, offset) : _data.Slice(offset + list + (int)docOffset, (int)length);
                    (firstGlyph, lastGlyph) = ((ushort)start, (ushort)end);
                    return true;
                }
            }

            return false;
        }
        catch (CorruptDataException e) when (!_options.Strict)
        {
            Report(_options, "Font.BadColorGlyph", $"The SVG table is damaged ({e.Message}).", offset);
            return false;
        }
    }

    private float[]? ColrScalars => _instance is not null && Colr?.Store is Variations.ItemVariationStore store ? _colrScalars ??= store.ComputeScalars(_instance.Coords) : null;

    private Rgba8[] PaletteColors(FontPalette palette)
    {
        CpalTable cpal = Cpal;
        int index = cpal.Choose(palette);
        Rgba8[] colors = index < 0 ? [] : (Rgba8[])cpal.Palettes[index].Clone();
        if (palette.Overrides.Count == 0)
        {
            return colors;
        }

        int size = Math.Max(colors.Length, palette.Overrides.Max(o => o.Index) + 1);
        Array.Resize(ref colors, size);
        foreach (FontPaletteEntry entry in palette.Overrides)
        {
            if (entry.Index >= 0)
            {
                colors[entry.Index] = entry.Color.ToRgba8();
            }
        }

        return colors;
    }

    private ReadOnlyMemory<byte> Gunzip(ReadOnlySpan<byte> doc, long offset)
    {
        long limit = _options.EffectiveContext.Limits.MaxTotalBytes;
        try
        {
            using var source = new MemoryStream(doc.ToArray(), writable: false);
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            using var output = new MemoryStream();
            byte[] chunk = new byte[16384];
            int read;
            while ((read = gzip.Read(chunk, 0, chunk.Length)) > 0)
            {
                output.Write(chunk, 0, read);
                if (output.Length > limit)
                {
                    throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), limit, output.Length);
                }
            }

            return output.ToArray();
        }
        catch (InvalidDataException e)
        {
            throw FontErrors.Corrupt(offset, $"An SVG document does not gunzip ({e.Message}).");
        }
    }

    // A table read once and kept, damage reported and turned into the fallback.
    private T? LoadOnce<T>(ref Box<T?>? field, Func<T?> load, FontTag tag, string defaultText)
    {
        Box<T?>? box = Volatile.Read(ref field);
        if (box is not null)
        {
            return box.Value;
        }

        T? value;
        try
        {
            value = load();
        }
        catch (CorruptDataException e) when (!_options.Strict)
        {
            Repair("Font.BadTable", $"The {tag} table is damaged ({e.Message}); {defaultText}.", TableOffset(tag));
            value = default;
        }

        return Publish(ref field, new Box<T?>(value)).Value;
    }
}
