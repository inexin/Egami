using Egami.Analysis;
using Egami.Quantization;

namespace Egami;

/// <summary>Palettes: an image as indices, and its dominant colors.</summary>
public abstract partial class Raster
{
    /// <summary>
    /// Returns one frame as palette indices and the palette they refer to, as a palette PNG would store it: the frame's own
    /// colors when they fit, Wu's quantizer otherwise, or the fixed <see cref="PaletteOptions.FixedPalette"/>, dithered as the options say.
    /// </summary>
    /// <param name="options">The color limit, dithering and palette, or <see langword="null"/> for <see cref="PaletteOptions.Default"/>.</param>
    /// <param name="frame">The frame, 0 (the default) for the first.</param>
    /// <returns>The indexed image. The caller must dispose it.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frame"/> is not a frame of the image.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Palettes.cs" region="indexed"/>
    /// </example>
    public IndexedImage ToIndexed(PaletteOptions? options = null, int frame = 0)
    {
        CheckFrame(frame);
        IndexedFrame mapped = IndexedFrame.Map(Width, Height, (y, row) => ReadFrameRow(frame, y, 0, row), options ?? PaletteOptions.Default, CancellationToken.None);
        return new IndexedImage(mapped, Width, Height);
    }

    /// <summary>
    /// Returns the main colors of the first frame with the share of pixels each stands for, largest first: k-means in Oklab on
    /// a sample of at most <see cref="DominantColorOptions.MaxSamples"/> pixels, seeded with Wu's palette so the answer is the
    /// same every run, with near-identical clusters merged. Transparent pixels are left out.
    /// </summary>
    /// <param name="count">The most colors wanted, 1 to 128. Defaults to 5. Fewer come back when the image has fewer distinct colors.</param>
    /// <param name="options">The sampling, passes and merging, or <see langword="null"/> for <see cref="DominantColorOptions.Default"/>.</param>
    /// <returns>The colors, largest share first; empty when every pixel is transparent.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is outside 1 to 128.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Palettes.cs" region="dominant"/>
    /// </example>
    public IReadOnlyList<WeightedColor> DominantColors(int count = 5, DominantColorOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 128);
        return DominantColorFinder.Find(this, count, options ?? DominantColorOptions.Default);
    }
}
