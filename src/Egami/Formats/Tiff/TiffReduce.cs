using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Pixels;

namespace Egami.Formats.Tiff;

/// <summary>
/// The writer's lossless reduction (question 7), as the PNG writer's: one pass over the pixels finds the smallest form that keeps
/// every value: 1 bit when every pixel is opaque black or white, gray when red, green and blue are equal, a palette of up to 256
/// colors for opaque images (TIFF palettes have no alpha), and 8 bits when every 16-bit sample is a multiple of 257.
/// </summary>
internal static class TiffReduce
{
    public static (TiffStore Store, Rgb8[] Palette) Analyze<TPixel>(Raster<TPixel> raster, TiffStore store, bool allowPalette)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (store is TiffStore.GrayF32 or TiffStore.RgbF32 or TiffStore.RgbaF32 or TiffStore.RgbaF16 or TiffStore.Cmyk8 or TiffStore.Cmyk16
            or TiffStore.PremulRgba8 or TiffStore.PremulRgbaF32)
        {
            return (store, []);
        }

        if (typeof(TPixel) == typeof(Gray8))
        {
            // Gray8 is bilevel when every value is 0 or 255, else it stays Gray8: a palette never beats gray.
            for (int y = 0; y < raster.Height; y++)
            {
                if (MemoryMarshal.AsBytes(raster.Row(y)).IndexOfAnyExcept((byte)0, (byte)255) >= 0)
                {
                    return (TiffStore.Gray8, []);
                }
            }

            return (TiffStore.Bilevel, []);
        }

        bool opaque = true;
        bool gray = true;
        bool bilevel = true;
        bool narrow = true;
        Dictionary<int, int> colors = allowPalette ? new(257) : [];
        bool fewColors = allowPalette;
        Rgba16[] row = new Rgba16[raster.Width];
        for (int y = 0; y < raster.Height; y++)
        {
            PixelConvert.Convert<TPixel, Rgba16>(raster.Row(y), row);
            foreach (Rgba16 p in row)
            {
                opaque &= p.A == 65535;
                gray &= p.R == p.G && p.G == p.B;
                bilevel &= p.R is 0 or 65535 && p.R == p.G && p.G == p.B && p.A == 65535;
                narrow &= p.R % 257 == 0 && p.G % 257 == 0 && p.B % 257 == 0 && p.A % 257 == 0;
                if (fewColors)
                {
                    int key = ((p.R / 257) << 16) | ((p.G / 257) << 8) | (p.B / 257);
                    if (!colors.ContainsKey(key))
                    {
                        colors[key] = colors.Count;
                        fewColors = colors.Count <= 256;
                    }
                }
            }

            if (!opaque && !gray && !narrow && !fewColors)
            {
                break;
            }
        }

        bool wide = store is TiffStore.Gray16 or TiffStore.GrayAlpha16 or TiffStore.Rgb16 or TiffStore.Rgba16;
        bool eight = !wide || narrow;
        if (bilevel)
        {
            return (TiffStore.Bilevel, []);
        }

        if (gray)
        {
            return (opaque ? (eight ? TiffStore.Gray8 : TiffStore.Gray16) : (eight ? TiffStore.GrayAlpha8 : TiffStore.GrayAlpha16), []);
        }

        if (fewColors && opaque && narrow && allowPalette)
        {
            Rgb8[] palette = new Rgb8[colors.Count];
            foreach ((int key, int index) in colors)
            {
                palette[index] = new Rgb8((byte)(key >> 16), (byte)(key >> 8), (byte)key);
            }

            return (TiffStore.Palette, palette);
        }

        return (opaque ? (eight ? TiffStore.Rgb8 : TiffStore.Rgb16) : (eight ? TiffStore.Rgba8 : TiffStore.Rgba16), []);
    }

    /// <summary>Packs a row as 1-bit WhiteIsZero (a black pixel is a 1 bit) or as palette indexes of the plan's bits.</summary>
    public static void Pack<TPixel>(ReadOnlySpan<TPixel> row, Span<byte> target, TiffPlan plan, Dictionary<int, int>? lookup, Span<Rgb8> scratch)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (plan.Store == TiffStore.Bilevel && typeof(TPixel) == typeof(Gray8))
        {
            PackGray(MemoryMarshal.AsBytes(row), target);
            return;
        }

        PixelConvert.Convert(row, scratch);
        target.Clear();
        int bits = plan.Bits;
        for (int x = 0; x < row.Length; x++)
        {
            Rgb8 p = scratch[x];
            int value = plan.Store == TiffStore.Bilevel ? (p.R < 128 ? 1 : 0) : lookup![(p.R << 16) | (p.G << 8) | p.B];
            int bit = x * bits;
            target[bit >> 3] |= (byte)(value << (8 - bits - (bit & 7)));
        }
    }

    /// <summary>Packs gray bytes 8 to a byte, a value below 128 a 1 bit.</summary>
    private static void PackGray(ReadOnlySpan<byte> gray, Span<byte> target)
    {
        int x = 0;
        int at = 0;
        for (; x + 8 <= gray.Length; x += 8)
        {
            // Each byte's high bit (white) gathered into the top byte, the first pixel highest; inverted, black is a 1 bit.
            ulong white = (BinaryPrimitives.ReadUInt64BigEndian(gray[x..]) & 0x8080808080808080UL) * 0x0002040810204081UL;
            target[at++] = (byte)~(white >> 56);
        }

        if (x < gray.Length)
        {
            int last = 0;
            for (int bit = 7; x < gray.Length; x++, bit--)
            {
                last |= (gray[x] < 128 ? 1 : 0) << bit;
            }

            target[at] = (byte)last;
        }
    }

    /// <summary>Returns the palette's lookup from a packed color to its index.</summary>
    public static Dictionary<int, int> Lookup(Rgb8[] palette)
    {
        Dictionary<int, int> lookup = new(palette.Length);
        for (int i = 0; i < palette.Length; i++)
        {
            lookup[(palette[i].R << 16) | (palette[i].G << 8) | palette[i].B] = i;
        }

        return lookup;
    }

    /// <summary>Returns the ColorMap tag's values: all reds, then greens, then blues, 16 bits each, padded to 2^bits entries.</summary>
    public static ulong[] ColorMap(Rgb8[] palette, int bits)
    {
        int entries = 1 << bits;
        ulong[] map = new ulong[3 * entries];
        for (int i = 0; i < palette.Length; i++)
        {
            map[i] = palette[i].R * 257UL;
            map[entries + i] = palette[i].G * 257UL;
            map[(2 * entries) + i] = palette[i].B * 257UL;
        }

        return map;
    }
}
