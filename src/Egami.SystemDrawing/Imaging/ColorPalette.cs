using Egami.SystemDrawing.Internal;

namespace Egami.SystemDrawing.Imaging;

/// <summary>
/// The colors an indexed bitmap's pixels point into. <see cref="Image.Palette"/> hands out a copy: change its entries and set it
/// back to change the bitmap's palette.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="palettes"/>
/// </example>
public sealed class ColorPalette
{
    /// <summary>Initializes a new instance of the <see cref="ColorPalette"/> class with the given colors.</summary>
    /// <param name="customColors">The colors, at most 256.</param>
    public ColorPalette(Color[] customColors)
        : this(0, customColors)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ColorPalette"/> class with one of the fixed palettes.</summary>
    /// <param name="fixedPaletteType">The palette.</param>
    /// <exception cref="ArgumentException">The type is not a palette type. <see cref="PaletteType.Custom"/> gives 256 transparent black entries.</exception>
    public ColorPalette(PaletteType fixedPaletteType)
        : this((int)fixedPaletteType << 8, GdiPalettes.Fixed(fixedPaletteType))
    {
    }

    internal ColorPalette(int flags, Color[] entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Flags = flags;
        Entries = entries;
    }

    /// <summary>Gets the palette's flags, a combination of <see cref="PaletteFlags"/> values.</summary>
    public int Flags { get; }

    /// <summary>Gets the colors.</summary>
    public Color[] Entries { get; }

    /// <summary>Builds a palette of at most <paramref name="colors"/> colors that suits a bitmap's pixels.</summary>
    /// <param name="colors">The number of colors, 2 to 256.</param>
    /// <param name="useTransparentColor">Whether the first entry is transparent.</param>
    /// <param name="bitmap">The bitmap whose colors the palette is for.</param>
    /// <returns>The palette.</returns>
    public static ColorPalette CreateOptimalPalette(int colors, bool useTransparentColor, Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentOutOfRangeException.ThrowIfLessThan(colors, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(colors, 256);
        return new ColorPalette(0, GdiPalettes.Optimal(bitmap, colors, useTransparentColor));
    }

    /// <summary>A copy with its own entries.</summary>
    internal ColorPalette Copy() => new(Flags, (Color[])Entries.Clone());
}
