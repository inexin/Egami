using System.Numerics;

namespace Egami.Operations;

/// <summary>
/// A general warp: a mapping from each output pixel back to the source point it shows. Egami samples the points with the same
/// sampler as <see cref="RasterOps.Transform(Homography, TransformOptions?)"/>, so every warp gets antialiasing where it shrinks
/// the image, and edge modes. Positions are in pixels with the origin at the top-left corner of the top-left pixel, so pixel
/// centers sit at +0.5.
/// </summary>
/// <remarks>
/// <see cref="Map"/> is called for every output row, from several threads at once, so an implementation must be stateless or
/// immutable. The sampler finds each pixel's footprint from the points its neighbours map to, so a warp only maps points, never
/// derivatives.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Warps.cs" region="custom-warp"/>
/// </example>
public interface IWarp
{
    /// <summary>Returns the size of the warped image for an input of <paramref name="input"/>.</summary>
    /// <param name="input">The input size.</param>
    /// <returns>The output size; return <paramref name="input"/> for a warp that keeps the size.</returns>
    Size OutputSize(Size input);

    /// <summary>
    /// Fills <paramref name="source"/> with the source point of every output pixel center in row <paramref name="y"/>: element x
    /// is where output point (x + 0.5, y + 0.5) comes from. A NaN coordinate marks a pixel with no source, which shows the
    /// background.
    /// </summary>
    /// <param name="y">The output row.</param>
    /// <param name="input">The input size.</param>
    /// <param name="source">One point per output column.</param>
    void Map(int y, Size input, Span<Vector2> source);
}

/// <summary>Makes warps from functions.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Warps.cs" region="custom-warp"/>
/// </example>
public static class Warp
{
    /// <summary>
    /// Returns a warp that calls <paramref name="map"/> for each output pixel center. Simple for experiments; an <see cref="IWarp"/>
    /// that maps whole rows gives the same pixels faster.
    /// </summary>
    /// <param name="map">Returns the source point for an output point; it may run on several threads at once.</param>
    /// <param name="outputSize">The output size, or <see langword="null"/> (the default) to keep the input's.</param>
    /// <returns>The warp.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="map"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="outputSize"/> is not at least 1 × 1.</exception>
    public static IWarp FromFunction(Func<Vector2, Vector2> map, Size? outputSize = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (outputSize is Size size && (size.Width < 1 || size.Height < 1))
        {
            throw new ArgumentOutOfRangeException(nameof(outputSize), size, "The output size must be at least 1 × 1.");
        }

        return new FunctionWarp(map, outputSize);
    }

    private sealed class FunctionWarp(Func<Vector2, Vector2> map, Size? outputSize) : IWarp
    {
        public Size OutputSize(Size input) => outputSize ?? input;

        public void Map(int y, Size input, Span<Vector2> source)
        {
            float cy = y + 0.5f;
            for (int x = 0; x < source.Length; x++)
            {
                source[x] = map(new Vector2(x + 0.5f, cy));
            }
        }
    }
}
