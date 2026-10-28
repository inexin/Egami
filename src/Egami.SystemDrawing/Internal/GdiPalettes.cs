using Egami.Pixels;
using Egami.Quantization;
using Egami.SystemDrawing.Imaging;

namespace Egami.SystemDrawing.Internal;

/// <summary>
/// GDI+'s palettes, measured in egami-compare (sysdrawing BitmapProbes): the default palettes of new indexed bitmaps, the fixed
/// halftone palettes, and the nearest-color mapping GDI+ converts to indexed formats with.
/// </summary>
internal static class GdiPalettes
{
    /// <summary>The 16 VGA colors, the default 4-bit palette and the start of the default 8-bit one.</summary>
    private static readonly uint[] Vga =
    [
        0xFF000000, 0xFF800000, 0xFF008000, 0xFF808000, 0xFF000080, 0xFF800080, 0xFF008080, 0xFF808080,
        0xFFC0C0C0, 0xFFFF0000, 0xFF00FF00, 0xFFFFFF00, 0xFF0000FF, 0xFFFF00FF, 0xFF00FFFF, 0xFFFFFFFF,
    ];

    /// <summary>The 8 system colors after the cube of the fixed halftone palettes.</summary>
    private static readonly uint[] System8 = [0xFFC0C0C0, 0xFF808080, 0xFF800000, 0xFF008000, 0xFF000080, 0xFF808000, 0xFF800080, 0xFF008080];

    /// <summary>The palette a new bitmap of an indexed format starts with, and its flags.</summary>
    public static ColorPalette Default(PixelFormat format) => format switch
    {
        PixelFormat.Format1bppIndexed => new ColorPalette((int)PaletteFlags.GrayScale, [Color.FromArgb(unchecked((int)0xFF000000)), Color.FromArgb(-1)]),
        PixelFormat.Format4bppIndexed => new ColorPalette(0, [.. Vga.Select(c => Color.FromArgb((int)c))]),
        PixelFormat.Format8bppIndexed => new ColorPalette((int)PaletteFlags.Halftone, Halftone8bpp()),
        _ => new ColorPalette((int)PaletteFlags.GrayScale, []),
    };

    /// <summary>The default 8-bit palette: the VGA colors, 24 transparent entries, then a 6 by 6 by 6 cube in steps of 0x33.</summary>
    private static Color[] Halftone8bpp()
    {
        Color[] entries = new Color[256];
        for (int i = 0; i < 16; i++)
        {
            entries[i] = Color.FromArgb((int)Vga[i]);
        }

        Cube(entries.AsSpan(40), [0, 0x33, 0x66, 0x99, 0xCC, 0xFF], [0, 0x33, 0x66, 0x99, 0xCC, 0xFF], [0, 0x33, 0x66, 0x99, 0xCC, 0xFF]);
        return entries;
    }

    /// <summary>The entries of one of ColorPalette's fixed palettes.</summary>
    public static Color[] Fixed(PaletteType type)
    {
        byte[] Levels(int n) => [.. Enumerable.Range(0, n).Select(i => (byte)(i * 255 / (n - 1)))];
        Color[] WithSystem(int n)
        {
            byte[] levels = Levels(n);
            Color[] entries = new Color[(n * n * n) + 8];
            Cube(entries, levels, levels, levels);
            for (int i = 0; i < 8; i++)
            {
                entries[(n * n * n) + i] = Color.FromArgb((int)System8[i]);
            }

            return entries;
        }

        switch (type)
        {
            case PaletteType.Custom:
                // GDI+ gives 256 transparent black entries.
                return new Color[256];
            case PaletteType.FixedBlackAndWhite:
                return [Color.FromArgb(unchecked((int)0xFF000000)), Color.FromArgb(-1)];
            case PaletteType.FixedHalftone8:
                return WithSystem(2);
            case PaletteType.FixedHalftone27:
                return WithSystem(3);
            case PaletteType.FixedHalftone64:
                return WithSystem(4);
            case PaletteType.FixedHalftone125:
                return WithSystem(5);
            case PaletteType.FixedHalftone216:
                return WithSystem(6);
            case PaletteType.FixedHalftone252:
                {
                    Color[] entries = new Color[252];
                    Cube(entries, Levels(6), Levels(7), Levels(6));
                    return entries;
                }

            case PaletteType.FixedHalftone256:
                {
                    // GDI+'s eight red and green levels end at 254.
                    byte[] eight = [0, 0x24, 0x48, 0x6D, 0x91, 0xB6, 0xDA, 0xFE];
                    Color[] entries = new Color[256];
                    Cube(entries, eight, eight, Levels(4));
                    return entries;
                }

            default:
                throw new ArgumentException("The palette type is not a fixed palette.", nameof(type));
        }
    }

    /// <summary>Fills entries with every mix of the levels, blue changing fastest, then green, then red.</summary>
    private static void Cube(Span<Color> entries, byte[] red, byte[] green, byte[] blue)
    {
        int i = 0;
        foreach (byte r in red)
        {
            foreach (byte g in green)
            {
                foreach (byte b in blue)
                {
                    entries[i++] = Color.FromArgb(255, r, g, b);
                }
            }
        }
    }

    /// <summary>
    /// The lookup GDI+ maps colors to palette indices with: a table of 16 levels a channel (the color's top 4 bits), each cell
    /// holding the palette entry nearest its representative color (level × 17) by squared distance in red, green and blue, the
    /// first index on a tie, alpha ignored. Measured exactly for Clone and LockBits into all three indexed formats.
    /// </summary>
    public static byte[] InverseTable(ReadOnlySpan<uint> palette)
    {
        byte[] table = new byte[4096];
        for (int cell = 0; cell < 4096; cell++)
        {
            int r = (cell >> 8) * 17, g = ((cell >> 4) & 15) * 17, b = (cell & 15) * 17;
            int best = 0, bestDistance = int.MaxValue;
            for (int i = 0; i < palette.Length; i++)
            {
                uint c = palette[i];
                int dr = (int)((c >> 16) & 0xFF) - r, dg = (int)((c >> 8) & 0xFF) - g, db = (int)(c & 0xFF) - b;
                int distance = (dr * dr) + (dg * dg) + (db * db);
                if (distance < bestDistance)
                {
                    best = i;
                    bestDistance = distance;
                }
            }

            table[cell] = (byte)best;
        }

        return table;
    }

    /// <summary>The palette index nearest a color by squared distance, the first on a tie, alpha ignored.</summary>
    public static int Nearest(ReadOnlySpan<uint> palette, uint argb)
    {
        int r = (int)((argb >> 16) & 0xFF), g = (int)((argb >> 8) & 0xFF), b = (int)(argb & 0xFF);
        int best = 0, bestDistance = int.MaxValue;
        for (int i = 0; i < palette.Length; i++)
        {
            uint c = palette[i];
            int dr = (int)((c >> 16) & 0xFF) - r, dg = (int)((c >> 8) & 0xFF) - g, db = (int)(c & 0xFF) - b;
            int distance = (dr * dr) + (dg * dg) + (db * db);
            if (distance < bestDistance)
            {
                best = i;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>The palette index of a color through an <see cref="InverseTable"/>.</summary>
    public static int Lookup(byte[] table, uint argb) => table[((argb >> 12) & 0xF00) | ((argb >> 8) & 0xF0) | ((argb >> 4) & 0xF)];

    /// <summary>The palette Clone gives an indexed copy of a bitmap: black and white, the 16-color or the 224-color halftone palette.</summary>
    public static ColorPalette ForClone(PixelFormat format) => format switch
    {
        PixelFormat.Format1bppIndexed => new ColorPalette((int)PaletteFlags.GrayScale, Fixed(PaletteType.FixedBlackAndWhite)),
        PixelFormat.Format4bppIndexed => new ColorPalette((int)PaletteFlags.Halftone, Fixed(PaletteType.FixedHalftone8)),
        _ => new ColorPalette((int)PaletteFlags.Halftone, Fixed(PaletteType.FixedHalftone216)),
    };

    /// <summary>A palette built from a bitmap's colors, with Egami's palette builder; the first entry transparent when asked.</summary>
    public static Color[] Optimal(Bitmap bitmap, int colors, bool transparent)
    {
        using Raster<Rgba8> raster = bitmap.ToRgba8();
        for (int y = 0; y < raster.Height; y++)
        {
            foreach (ref Rgba8 c in raster.Row(y))
            {
                c.A = 255;
            }
        }

        Palette palette = Palette.Build(raster, new PaletteOptions { MaxColors = transparent ? colors - 1 : colors, Dither = DitherMode.None });
        List<Color> entries = [.. palette.Colors.Select(c => Color.FromArgb(255, c.R, c.G, c.B))];
        if (transparent)
        {
            entries.Add(Color.FromArgb(0));
        }

        return [.. entries];
    }
}
