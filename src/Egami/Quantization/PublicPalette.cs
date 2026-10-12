using System.Buffers;

using Egami.Pixels;

namespace Egami.Quantization;

/// <summary>
/// An ordered list of up to 256 colors to map images onto: a brand palette, a display's inks, or one built from an image with
/// Wu's quantizer. Use it as <see cref="PaletteOptions.Palette"/> for the GIF, PNG, BMP and TGA writers, with
/// <see cref="Operations.RasterOps.Remap"/>, or to find the nearest color yourself.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Palettes.cs" region="remap"/>
/// </example>
public sealed class Palette
{
    private readonly Rgba8[] _colors;
    private readonly bool _withAlpha;

    private Palette(Rgba8[] colors, int? transparentIndex)
    {
        _colors = colors;
        TransparentIndex = transparentIndex;
        for (int i = 0; i < colors.Length; i++)
        {
            _withAlpha |= i != transparentIndex && colors[i].A != 255;
        }
    }

    /// <summary>Gets the colors, in order.</summary>
    public IReadOnlyList<Rgba8> Colors => _colors;

    /// <summary>Gets the number of colors, 1 to 256.</summary>
    public int Count => _colors.Length;

    /// <summary>
    /// Gets the index of the entry fully transparent pixels take, or <see langword="null"/> when there is none. That entry is
    /// never the nearest color of a visible pixel.
    /// </summary>
    public int? TransparentIndex { get; }

    /// <summary>Creates a palette of the given colors, in that order.</summary>
    /// <param name="colors">1 to 256 colors; they may repeat.</param>
    /// <param name="transparentIndex">The entry fully transparent pixels take, or <see langword="null"/> (the default) for none.</param>
    /// <returns>The palette.</returns>
    /// <exception cref="ArgumentException"><paramref name="colors"/> is empty or has more than 256 colors.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="transparentIndex"/> is not an index of <paramref name="colors"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Palettes.cs" region="remap"/>
    /// </example>
    public static Palette FromColors(ReadOnlySpan<Rgba8> colors, int? transparentIndex = null)
    {
        if (colors.Length is < 1 or > 256)
        {
            throw new ArgumentException($"A palette has 1 to 256 colors; {colors.Length} were given.", nameof(colors));
        }

        if (transparentIndex is int t && (t < 0 || t >= colors.Length))
        {
            throw new ArgumentOutOfRangeException(nameof(transparentIndex), t, $"The transparent index must be below {colors.Length}.");
        }

        return new Palette(colors.ToArray(), transparentIndex);
    }

    /// <summary>
    /// Builds a palette for every frame of an image together: its own colors when they fit, Wu's quantizer otherwise, polished
    /// by <see cref="PaletteOptions.RefineIterations"/>. Alpha is kept in the colors, as for a palette PNG.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <param name="options">The color limit and refinement, or <see langword="null"/> for <see cref="PaletteOptions.Default"/>; a fixed palette in them is returned as it is.</param>
    /// <returns>The palette.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Palettes.cs" region="build"/>
    /// </example>
    public static Palette Build(Raster image, PaletteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        options ??= PaletteOptions.Default;
        if (options.Palette is Palette given)
        {
            return given;
        }

        int width = image.Width, height = image.Height;
        Rgba8[] row = ArrayPool<Rgba8>.Shared.Rent(width);
        try
        {
            BuiltPalette built = PaletteBuilder.Build(image.FrameCount * height, r =>
            {
                image.ReadFrameRow(r / height, r % height, 0, row.AsSpan(0, width));
                return row.AsMemory(0, width);
            }, options, binaryAlpha: false);
            built.Release();
            return new Palette(built.Colors, null);
        }
        finally
        {
            ArrayPool<Rgba8>.Shared.Return(row);
        }
    }

    /// <summary>Builds a palette of at most <paramref name="maxColors"/> colors for an image, as <see cref="Build"/> with default options.</summary>
    /// <param name="image">The image.</param>
    /// <param name="maxColors">The most colors, 2 to 256. Defaults to 256.</param>
    /// <returns>The palette.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxColors"/> is outside 2 to 256.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    public static Palette FromRaster(Raster image, int maxColors = 256)
    {
        return Build(image, new PaletteOptions { MaxColors = maxColors });
    }

    /// <summary>
    /// Returns the index of the color nearest to <paramref name="color"/>, by squared distance in encoded RGB (plus alpha when
    /// the palette has translucent colors); a fully transparent color takes <see cref="TransparentIndex"/> when there is one.
    /// </summary>
    /// <param name="color">The color.</param>
    /// <returns>The index; the first one on a tie.</returns>
    public int NearestIndex(Rgba8 color)
    {
        if (TransparentIndex is int t && color.A == 0)
        {
            return t;
        }

        int best = -1;
        int bestDistance = int.MaxValue;
        for (int i = 0; i < _colors.Length; i++)
        {
            if (i == TransparentIndex)
            {
                continue;
            }

            Rgba8 c = _colors[i];
            int dr = c.R - color.R, dg = c.G - color.G, db = c.B - color.B;
            int distance = (dr * dr) + (dg * dg) + (db * db);
            if (_withAlpha)
            {
                int da = c.A - color.A;
                distance += da * da;
            }

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best < 0 ? 0 : best;
    }

    /// <summary>Returns the palette color nearest to <paramref name="color"/>; see <see cref="NearestIndex"/>.</summary>
    /// <param name="color">The color.</param>
    /// <returns>The nearest color.</returns>
    public Rgba8 Nearest(Rgba8 color) => _colors[NearestIndex(color)];

    /// <summary>Gets whether alpha takes part in color distances.</summary>
    internal bool WithAlpha => _withAlpha;

    /// <summary>Returns the palette ready for mapping.</summary>
    internal BuiltPalette ToBuilt() => new([.. _colors], TransparentIndex ?? -1, false, null);
}

/// <summary>Ready-made palettes.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Palettes.cs" region="ready-made"/>
/// </example>
public static class Palettes
{
    /// <summary>Gets the 216 web-safe colors: each channel 0, 51, 102, 153, 204 or 255, red slowest.</summary>
    public static Palette WebSafe { get; } = Uniform(6, 6, 6);

    /// <summary>Gets black and white.</summary>
    public static Palette BlackAndWhite { get; } = Palette.FromColors([Rgba8.Black, Rgba8.White]);

    /// <summary>Returns evenly spaced grays from black to white.</summary>
    /// <param name="levels">The number of grays, 2 to 256.</param>
    /// <returns>The palette, darkest first.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="levels"/> is outside 2 to 256.</exception>
    public static Palette Grayscale(int levels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(levels, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(levels, 256);
        Rgba8[] colors = new Rgba8[levels];
        for (int i = 0; i < levels; i++)
        {
            byte v = Level(i, levels);
            colors[i] = new Rgba8(v, v, v, 255);
        }

        return Palette.FromColors(colors);
    }

    /// <summary>
    /// Returns a color cube with evenly spaced levels per channel, such as 8, 8 and 4 for the classic 3-3-2 palette; red varies
    /// slowest and blue fastest.
    /// </summary>
    /// <param name="red">Levels of red, at least 2.</param>
    /// <param name="green">Levels of green, at least 2.</param>
    /// <param name="blue">Levels of blue, at least 2.</param>
    /// <returns>The palette.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A level count is less than 2, or their product is more than 256.</exception>
    public static Palette Uniform(int red, int green, int blue)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(red, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(green, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(blue, 2);
        if ((long)red * green * blue > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(red), $"The cube has {(long)red * green * blue} colors; a palette holds at most 256.");
        }

        List<Rgba8> colors = [];
        for (int r = 0; r < red; r++)
        {
            for (int g = 0; g < green; g++)
            {
                for (int b = 0; b < blue; b++)
                {
                    colors.Add(new Rgba8(Level(r, red), Level(g, green), Level(b, blue), 255));
                }
            }
        }

        return Palette.FromColors([.. colors]);
    }

    private static byte Level(int i, int levels) => (byte)(((i * 255) + ((levels - 1) / 2)) / (levels - 1));
}
