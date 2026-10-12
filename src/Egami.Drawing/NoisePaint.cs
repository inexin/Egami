using System.Numerics;

using Egami.Drawing.Shading;
using Egami.Operations;

namespace Egami.Drawing;

/// <summary>The kind of noise a <see cref="NoisePaint"/> makes.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Paints.cs" region="noise"/>
/// </example>
public enum NoiseType
{
    /// <summary>SVG's fractalNoise: smooth clouds, each channel half the sum of the octaves' noise plus a half.</summary>
    FractalNoise,

    /// <summary>SVG's turbulence: the sum of the octaves' absolute noise, with sharp creases where it crosses zero.</summary>
    Turbulence,

    /// <summary>Simplex noise: smoother than the SVG kinds, with no grid-aligned artifacts; Egami's own, with no reference library.</summary>
    Simplex,
}

/// <summary>
/// Procedural noise as a paint. <see cref="Fractal"/> and <see cref="Turbulence"/> follow SVG 1.1's feTurbulence step by step
/// (its random table, lattice and gradients), so the pixels match Skia's noise shaders and browsers within rounding;
/// <see cref="Simplex"/> is smoother and can tile. Each of the four channels is its own noise, straight alpha included. The noise
/// lives in paint coordinates: it scales and turns with the canvas transform and <see cref="Paint.LocalMatrix"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Paints.cs" region="noise"/>
/// </example>
public sealed record class NoisePaint : Paint
{
    /// <summary>The most octaves a noise paint sums.</summary>
    public const int MaxOctaves = 32;

    private NoisePaint(NoiseType type, float baseFrequencyX, float baseFrequencyY, int octaves, int seed, Size? tileSize)
    {
        if (!(baseFrequencyX >= 0 && float.IsFinite(baseFrequencyX)))
        {
            throw new ArgumentOutOfRangeException(nameof(baseFrequencyX), baseFrequencyX, "A frequency must be finite and not negative.");
        }

        if (!(baseFrequencyY >= 0 && float.IsFinite(baseFrequencyY)))
        {
            throw new ArgumentOutOfRangeException(nameof(baseFrequencyY), baseFrequencyY, "A frequency must be finite and not negative.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(octaves);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(octaves, MaxOctaves);
        if (tileSize is Size size && (size.Width < 1 || size.Height < 1))
        {
            throw new ArgumentOutOfRangeException(nameof(tileSize), size, "A tile must be at least 1 × 1.");
        }

        Type = type;
        BaseFrequencyX = baseFrequencyX;
        BaseFrequencyY = baseFrequencyY;
        Octaves = octaves;
        Seed = seed;
        TileSize = tileSize;
    }

    /// <summary>Gets the kind of noise.</summary>
    public NoiseType Type { get; }

    /// <summary>Gets the horizontal frequency of the first octave, in cycles per paint unit.</summary>
    public float BaseFrequencyX { get; }

    /// <summary>Gets the vertical frequency of the first octave.</summary>
    public float BaseFrequencyY { get; }

    /// <summary>Gets the number of octaves summed, each twice the frequency and half the amplitude of the one before; 0 draws transparent black.</summary>
    public int Octaves { get; }

    /// <summary>Gets the seed of the random tables.</summary>
    public int Seed { get; }

    /// <summary>
    /// Gets the size of the tile the noise repeats over, from the paint origin, or <see langword="null"/> for noise that never
    /// repeats. For the SVG kinds this is feTurbulence's stitching (the frequencies are adjusted so the tile's edges meet); for
    /// simplex noise the tile is exact.
    /// </summary>
    public Size? TileSize { get; }

    /// <summary>Returns SVG feTurbulence's fractalNoise.</summary>
    /// <param name="baseFrequencyX">The horizontal frequency, as SVG's baseFrequency.</param>
    /// <param name="baseFrequencyY">The vertical frequency.</param>
    /// <param name="octaves">The number of octaves, as SVG's numOctaves, from 0 to 32.</param>
    /// <param name="seed">The seed.</param>
    /// <param name="stitchSize">The tile to stitch to, as SVG's stitchTiles over a tile of this size; <see langword="null"/> for none.</param>
    /// <returns>The paint.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A frequency is negative or not finite, the octaves are outside 0 to 32, or the tile is empty.</exception>
    public static NoisePaint Fractal(float baseFrequencyX, float baseFrequencyY, int octaves = 1, int seed = 0, Size? stitchSize = null)
        => new(NoiseType.FractalNoise, baseFrequencyX, baseFrequencyY, octaves, seed, stitchSize);

    /// <summary>Returns SVG feTurbulence's turbulence.</summary>
    /// <param name="baseFrequencyX">The horizontal frequency.</param>
    /// <param name="baseFrequencyY">The vertical frequency.</param>
    /// <param name="octaves">The number of octaves, from 0 to 32.</param>
    /// <param name="seed">The seed.</param>
    /// <param name="stitchSize">The tile to stitch to; <see langword="null"/> for none.</param>
    /// <returns>The paint.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A frequency is negative or not finite, the octaves are outside 0 to 32, or the tile is empty.</exception>
    public static NoisePaint Turbulence(float baseFrequencyX, float baseFrequencyY, int octaves = 1, int seed = 0, Size? stitchSize = null)
        => new(NoiseType.Turbulence, baseFrequencyX, baseFrequencyY, octaves, seed, stitchSize);

    /// <summary>
    /// Returns simplex noise: each channel a half plus half the octaves' sum, scaled so it stays within 0 to 1. With a tile size it
    /// repeats exactly over that tile, made by running the noise around a torus.
    /// </summary>
    /// <param name="frequency">The frequency of the first octave, in cycles per paint unit.</param>
    /// <param name="octaves">The number of octaves, from 0 to 32.</param>
    /// <param name="seed">The seed of the gradient table.</param>
    /// <param name="tileSize">The tile it repeats over; <see langword="null"/> for none.</param>
    /// <returns>The paint.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The frequency is negative or not finite, the octaves are outside 0 to 32, or the tile is empty.</exception>
    public static NoisePaint Simplex(float frequency, int octaves = 1, int seed = 0, Size? tileSize = null)
        => new(NoiseType.Simplex, frequency, frequency, octaves, seed, tileSize);

    /// <inheritdoc/>
    internal override Shader? CreateShader(Matrix3x2 toDevice, ColorSpace colorSpace)
    {
        if (!TryInverse(toDevice, out Matrix3x2 inverse))
        {
            return null;
        }

        return Type == NoiseType.Simplex ? new SimplexShader(this, inverse) : new TurbulenceShader(this, inverse);
    }
}
