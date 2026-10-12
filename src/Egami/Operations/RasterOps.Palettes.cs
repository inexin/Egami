using Egami.Quantization;

namespace Egami.Operations;

/// <summary>Palette steps: reducing an image to few colors, or mapping it onto a given palette.</summary>
public sealed partial class RasterOps
{
    /// <summary>
    /// Reduces each frame to at most <see cref="PaletteOptions.MaxColors"/> colors, as a palette format would, but keeps the
    /// pixel type: an Rgba16 image stays Rgba16 with at most that many distinct values. Each frame gets its own palette (its
    /// own colors when they fit, Wu's quantizer otherwise) unless <see cref="PaletteOptions.FixedPalette"/> fixes one, and is
    /// dithered as the options say. Use <see cref="Raster.ToIndexed"/> for the indices themselves.
    /// </summary>
    /// <param name="options">The color limit, dithering, refinement and matching, or <see langword="null"/> for <see cref="PaletteOptions.Default"/>.</param>
    /// <returns>This builder.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Palettes.cs" region="quantize"/>
    /// </example>
    public RasterOps Quantize(PaletteOptions? options = null)
    {
        return Add(new QuantizeOperation(options ?? PaletteOptions.Default));
    }

    /// <summary>
    /// Maps every pixel onto <paramref name="palette"/>, as ImageMagick's -remap: a brand palette, a display's inks, or the
    /// palette of another image. The pixel type is kept, and so is each pixel's alpha when the palette is opaque.
    /// </summary>
    /// <param name="palette">The palette.</param>
    /// <param name="dither">How the error is spread. Defaults to <see cref="DitherMode.FloydSteinberg"/>.</param>
    /// <param name="strength">How strongly, from 0 (the nearest color) to 1 (the default).</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="palette"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="dither"/> is not defined, or <paramref name="strength"/> is outside 0 to 1.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Palettes.cs" region="remap"/>
    /// </example>
    public RasterOps Remap(Palette palette, DitherMode dither = DitherMode.FloydSteinberg, float strength = 1)
    {
        ArgumentNullException.ThrowIfNull(palette);
        return Add(new QuantizeOperation(new PaletteOptions { FixedPalette = palette, Dither = dither, DitherStrength = strength }));
    }
}
