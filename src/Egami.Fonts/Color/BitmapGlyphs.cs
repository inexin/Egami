using Egami.Fonts.Parsing;

namespace Egami.Fonts.Color;

/// <summary>
/// Apple's sbix (OpenType 1.9.1): strikes of PNG, JPEG or TIFF images per glyph with origin offsets in pixels, 'dupe' entries
/// followed; 'mask' and 'pdf ' images are not drawn.
/// </summary>
internal sealed class SbixTable
{
    private const int MaxDupes = 16;

    private readonly ReadOnlyMemory<byte> _data;
    private readonly long _offset;
    private readonly (int Ppem, int Offset)[] _strikes;
    private readonly int _glyphCount;

    private SbixTable(ReadOnlyMemory<byte> data, long offset, (int, int)[] strikes, int glyphCount)
    {
        _data = data;
        _offset = offset;
        _strikes = strikes;
        _glyphCount = glyphCount;
    }

    public static SbixTable Read(ReadOnlyMemory<byte> table, long offset, int glyphCount)
    {
        ReadOnlySpan<byte> data = table.Span;
        var r = new FontReader(data, offset);
        r.Skip(4);
        uint count = r.ReadUInt32();
        if (count > (uint)(data.Length / 4))
        {
            throw FontErrors.Corrupt(offset, "The sbix strike count is larger than the table.");
        }

        var strikes = new List<(int, int)>((int)count);
        for (int i = 0; i < count; i++)
        {
            int at = (int)r.ReadUInt32();
            if (at <= 0 || (long)at + 4 + ((glyphCount + 1L) * 4) > data.Length)
            {
                continue;
            }

            strikes.Add((BigEndian.UInt16(data, at), at));
        }

        strikes.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return new SbixTable(table, offset, [.. strikes], glyphCount);
    }

    public bool HasGlyph(int glyph)
    {
        foreach ((_, int strike) in _strikes)
        {
            if (Entry(strike, glyph).Length > 8)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The glyph's image in the strike chosen for <paramref name="pixelsPerEm"/> (0 for the largest), or <see langword="null"/>.</summary>
    public BitmapColorGlyph? Get(ushort glyph, float pixelsPerEm, int unitsPerEm)
    {
        foreach (int s in BitmapStrikes.Order(_strikes.Select(x => x.Ppem).ToArray(), pixelsPerEm))
        {
            (int ppem, int strike) = _strikes[s];
            int target = glyph;
            for (int hop = 0; hop <= MaxDupes; hop++)
            {
                ReadOnlySpan<byte> entry = Entry(strike, target);
                if (entry.Length < 8)
                {
                    break;
                }

                var type = new FontTag(BigEndian.UInt32(entry, 4));
                if (type == new FontTag("dupe"))
                {
                    target = entry.Length >= 10 ? BigEndian.UInt16(entry, 8) : -1;
                    if (target < 0 || target >= _glyphCount)
                    {
                        break;
                    }

                    continue;
                }

                BitmapGlyphEncoding? encoding = type.ToString() switch
                {
                    "png " => BitmapGlyphEncoding.Png,
                    "jpg " => BitmapGlyphEncoding.Jpeg,
                    "tiff" => BitmapGlyphEncoding.Tiff,
                    _ => null,
                };
                if (encoding is null || ppem == 0)
                {
                    break;
                }

                int glyphOffset = StartOf(strike, target);
                ReadOnlyMemory<byte> image = _data.Slice(glyphOffset + 8, entry.Length - 8);
                (int width, int height) = BitmapStrikes.Size(image, encoding.Value);
                if (width <= 0 || height <= 0)
                {
                    break;
                }

                float scale = unitsPerEm / (float)ppem;
                float left = BigEndian.Int16(entry, 0) * scale, bottom = BigEndian.Int16(entry, 2) * scale;
                var bounds = new GlyphExtents(left, bottom, left + (width * scale), bottom + (height * scale));
                return new BitmapColorGlyph(glyph, ColorGlyphFormats.Sbix, image, encoding.Value, width, height, ppem, bounds);
            }
        }

        return null;
    }

    private int StartOf(int strike, int glyph) => strike + (int)BigEndian.UInt32(_data.Span, strike + 4 + (glyph * 4));

    private ReadOnlySpan<byte> Entry(int strike, int glyph)
    {
        if ((uint)glyph >= (uint)_glyphCount)
        {
            return default;
        }

        ReadOnlySpan<byte> data = _data.Span;
        uint start = BigEndian.UInt32(data, strike + 4 + (glyph * 4)), end = BigEndian.UInt32(data, strike + 8 + (glyph * 4));
        if (end <= start || strike + (long)end > data.Length)
        {
            return default;
        }

        return data.Slice(strike + (int)start, (int)(end - start));
    }
}

/// <summary>
/// Google's CBLC and CBDT: bitmap sizes, index subtables (formats 1 to 5) and image formats 17, 18 and 19 (PNG) and the
/// byte-aligned 32-bit formats 1 and 6 (premultiplied BGRA) placed by small or big glyph metrics.
/// </summary>
internal sealed class CbdtTables
{
    private readonly ReadOnlyMemory<byte> _cblc;
    private readonly ReadOnlyMemory<byte> _cbdt;
    private readonly Size[] _sizes;

    private CbdtTables(ReadOnlyMemory<byte> cblc, ReadOnlyMemory<byte> cbdt, Size[] sizes)
    {
        _cblc = cblc;
        _cbdt = cbdt;
        _sizes = sizes;
    }

    public static CbdtTables Read(ReadOnlyMemory<byte> cblc, ReadOnlyMemory<byte> cbdt, long offset)
    {
        ReadOnlySpan<byte> data = cblc.Span;
        var r = new FontReader(data, offset);
        r.Skip(4);
        uint count = r.ReadUInt32();
        if (count > (uint)(data.Length / 48))
        {
            throw FontErrors.Corrupt(offset, "The CBLC size count is larger than the table.");
        }

        var sizes = new List<Size>((int)count);
        for (int i = 0; i < count; i++)
        {
            int at = 8 + (i * 48);
            int arrayOffset = (int)BigEndian.UInt32(data, at);
            int tables = (int)Math.Min(BigEndian.UInt32(data, at + 8), (uint)(data.Length / 8));
            int start = BigEndian.UInt16(data, at + 40), end = BigEndian.UInt16(data, at + 42);
            int ppem = BigEndian.UInt8(data, at + 45), depth = BigEndian.UInt8(data, at + 46);
            if (depth == 32 && ppem > 0)
            {
                sizes.Add(new Size(ppem, arrayOffset, tables, start, end));
            }
        }

        sizes.Sort((a, b) => a.Ppem.CompareTo(b.Ppem));
        return new CbdtTables(cblc, cbdt, [.. sizes]);
    }

    public bool HasGlyph(int glyph)
    {
        foreach (Size size in _sizes)
        {
            if (glyph >= size.Start && glyph <= size.End && Locate(size, glyph).Format != 0)
            {
                return true;
            }
        }

        return false;
    }

    public BitmapColorGlyph? Get(ushort glyph, float pixelsPerEm, int unitsPerEm)
    {
        foreach (int s in BitmapStrikes.Order(_sizes.Select(x => x.Ppem).ToArray(), pixelsPerEm))
        {
            Size size = _sizes[s];
            if (glyph < size.Start || glyph > size.End)
            {
                continue;
            }

            Location at = Locate(size, glyph);
            if (at.Format == 0 || at.Length <= 0)
            {
                continue;
            }

            if (Image(glyph, size, at, unitsPerEm) is BitmapColorGlyph image)
            {
                return image;
            }
        }

        return null;
    }

    private BitmapColorGlyph? Image(ushort glyph, Size size, Location at, int unitsPerEm)
    {
        ReadOnlySpan<byte> data = _cbdt.Span;
        if (at.Offset < 0 || (long)at.Offset + at.Length > data.Length)
        {
            return null;
        }

        ReadOnlySpan<byte> entry = data.Slice(at.Offset, at.Length);
        int width, height, bearingX, bearingY, dataStart;
        switch (at.Format)
        {
            case 17:
            case 1:
                if (entry.Length < 5)
                {
                    return null;
                }

                (height, width, bearingX, bearingY) = (entry[0], entry[1], (sbyte)entry[2], (sbyte)entry[3]);
                dataStart = 5;
                break;
            case 18:
            case 6:
                if (entry.Length < 8)
                {
                    return null;
                }

                (height, width, bearingX, bearingY) = (entry[0], entry[1], (sbyte)entry[2], (sbyte)entry[3]);
                dataStart = 8;
                break;
            case 19:
                if (at.Metrics is not BigMetrics m)
                {
                    return null;
                }

                (height, width, bearingX, bearingY) = (m.Height, m.Width, m.BearingX, m.BearingY);
                dataStart = 0;
                break;
            default:
                return null;
        }

        ReadOnlyMemory<byte> image;
        BitmapGlyphEncoding encoding;
        if (at.Format is 17 or 18 or 19)
        {
            if (dataStart + 4 > entry.Length)
            {
                return null;
            }

            uint length = BigEndian.UInt32(entry, dataStart);
            if (length > entry.Length - dataStart - 4)
            {
                return null;
            }

            image = _cbdt.Slice(at.Offset + dataStart + 4, (int)length);
            encoding = BitmapGlyphEncoding.Png;
        }
        else
        {
            long length = (long)width * height * 4;
            if (length > entry.Length - dataStart)
            {
                return null;
            }

            image = _cbdt.Slice(at.Offset + dataStart, (int)length);
            encoding = BitmapGlyphEncoding.PremultipliedBgra;
        }

        if (width <= 0 || height <= 0)
        {
            return null;
        }

        float scale = unitsPerEm / (float)size.Ppem;
        var bounds = new GlyphExtents(bearingX * scale, (bearingY - height) * scale, (bearingX + width) * scale, bearingY * scale);
        return new BitmapColorGlyph(glyph, ColorGlyphFormats.Cbdt, image, encoding, width, height, size.Ppem, bounds);
    }

    // Finds a glyph's image through the size's index subtable array.
    private Location Locate(Size size, int glyph)
    {
        ReadOnlySpan<byte> data = _cblc.Span;
        for (int t = 0; t < size.Tables; t++)
        {
            int rec = size.ArrayOffset + (t * 8);
            if (rec + 8 > data.Length)
            {
                break;
            }

            int first = BigEndian.UInt16(data, rec), last = BigEndian.UInt16(data, rec + 2);
            if (glyph < first || glyph > last)
            {
                continue;
            }

            int sub = size.ArrayOffset + (int)BigEndian.UInt32(data, rec + 4);
            int indexFormat = BigEndian.UInt16(data, sub), imageFormat = BigEndian.UInt16(data, sub + 2);
            int imageData = (int)BigEndian.UInt32(data, sub + 4);
            int k = glyph - first;
            switch (indexFormat)
            {
                case 1:
                    {
                        uint a = BigEndian.UInt32(data, sub + 8 + (k * 4)), b = BigEndian.UInt32(data, sub + 12 + (k * 4));
                        return new Location(imageFormat, imageData + (int)a, (int)(b - a), null);
                    }

                case 2:
                    {
                        int imageSize = (int)BigEndian.UInt32(data, sub + 8);
                        return new Location(imageFormat, imageData + (k * imageSize), imageSize, BigMetrics.Read(data, sub + 12));
                    }

                case 3:
                    {
                        int a = BigEndian.UInt16(data, sub + 8 + (k * 2)), b = BigEndian.UInt16(data, sub + 10 + (k * 2));
                        return new Location(imageFormat, imageData + a, b - a, null);
                    }

                case 4:
                    {
                        int count = (int)BigEndian.UInt32(data, sub + 8);
                        for (int i = 0; i < count; i++)
                        {
                            int pair = sub + 12 + (i * 4);
                            if (BigEndian.UInt16(data, pair) == glyph)
                            {
                                int a = BigEndian.UInt16(data, pair + 2), b = BigEndian.UInt16(data, pair + 6);
                                return new Location(imageFormat, imageData + a, b - a, null);
                            }
                        }

                        return default;
                    }

                case 5:
                    {
                        int imageSize = (int)BigEndian.UInt32(data, sub + 8);
                        BigMetrics metrics = BigMetrics.Read(data, sub + 12);
                        int count = (int)BigEndian.UInt32(data, sub + 20);
                        for (int i = 0; i < count; i++)
                        {
                            if (BigEndian.UInt16(data, sub + 24 + (i * 2)) == glyph)
                            {
                                return new Location(imageFormat, imageData + (i * imageSize), imageSize, metrics);
                            }
                        }

                        return default;
                    }

                default:
                    return default;
            }
        }

        return default;
    }

    private readonly record struct Size(int Ppem, int ArrayOffset, int Tables, int Start, int End);

    private readonly record struct Location(int Format, int Offset, int Length, BigMetrics? Metrics);

    private readonly record struct BigMetrics(int Height, int Width, int BearingX, int BearingY)
    {
        public static BigMetrics Read(ReadOnlySpan<byte> data, int at)
            => new(BigEndian.Slice(data, at, 8)[0], data[at + 1], (sbyte)data[at + 2], (sbyte)data[at + 3]);
    }
}

/// <summary>Strike choice and image sizes shared by sbix and CBDT.</summary>
internal static class BitmapStrikes
{
    /// <summary>
    /// The strikes to try, best first: the smallest at or above the size, then larger ones, then smaller ones from the largest
    /// down; a size of 0 asks for the largest first.
    /// </summary>
    public static IEnumerable<int> Order(int[] ppems, float pixelsPerEm)
    {
        if (ppems.Length == 0)
        {
            yield break;
        }

        int first = pixelsPerEm <= 0 ? ppems.Length : Array.FindIndex(ppems, p => p >= pixelsPerEm);
        if (first < 0)
        {
            first = ppems.Length;
        }

        for (int i = first; i < ppems.Length; i++)
        {
            yield return i;
        }

        for (int i = first - 1; i >= 0; i--)
        {
            yield return i;
        }
    }

    /// <summary>An encoded image's pixel size: PNG's IHDR read directly, other formats probed by Egami's decoders.</summary>
    public static (int Width, int Height) Size(ReadOnlyMemory<byte> image, BitmapGlyphEncoding encoding)
    {
        ReadOnlySpan<byte> data = image.Span;
        if (encoding == BitmapGlyphEncoding.Png && data.Length >= 24 && data[12] == 'I' && data[13] == 'H' && data[14] == 'D' && data[15] == 'R')
        {
            return ((int)BigEndian.UInt32(data, 16), (int)BigEndian.UInt32(data, 20));
        }

        try
        {
            Formats.RasterInfo info = Raster.Probe(image);
            return (info.Width, info.Height);
        }
        catch (EgamiException)
        {
            return (0, 0);
        }
    }
}
