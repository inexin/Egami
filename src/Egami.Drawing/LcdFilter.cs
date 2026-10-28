namespace Egami.Drawing;

/// <summary>
/// The five-tap filter LCD text spreads each subpixel's coverage with, across the subpixel and its two neighbours on each side.
/// The weights add up to 256. A wider filter has softer colour fringes; a narrower one is sharper and more coloured.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="lcd-look"/>
/// </example>
public sealed class LcdFilter : IEquatable<LcdFilter>
{
    private readonly int[] _weights;

    private LcdFilter(string name, int[] weights)
    {
        Name = name;
        _weights = weights;
    }

    /// <summary>Gets Egami's filter since 2.7.2 and the default: 16, 64, 96, 64, 16 (1, 4, 6, 4, 1 over 16).</summary>
    public static LcdFilter Default { get; } = new(nameof(Default), [16, 64, 96, 64, 16]);

    /// <summary>Gets a colour-balanced filter: 8, 77, 86, 77, 8, as FreeType's default LCD filter is described.</summary>
    public static LcdFilter Balanced { get; } = new(nameof(Balanced), [8, 77, 86, 77, 8]);

    /// <summary>Gets a light three-tap filter: 0, 85, 86, 85, 0, a box over the subpixel and its two neighbours.</summary>
    public static LcdFilter Light { get; } = new(nameof(Light), [0, 85, 86, 85, 0]);

    /// <summary>
    /// Gets the filter measured from GDI and GDI+ ClearType: each subpixel's coverage spread evenly over it and its two
    /// neighbours, the weights of <see cref="Light"/>.
    /// </summary>
    public static LcdFilter Gdi { get; } = new(nameof(Gdi), [0, 85, 86, 85, 0]);

    /// <summary>Gets the preset's name, or <c>Custom</c>.</summary>
    public string Name { get; }

    /// <summary>Gets the five weights from the farthest left neighbour to the farthest right; they add up to 256.</summary>
    public IReadOnlyList<int> Weights => _weights;

    /// <summary>A filter of five weights, normalised to add up to 256 (largest remainders rounding up).</summary>
    /// <param name="outerLeft">The weight two subpixels to the left.</param>
    /// <param name="left">The weight one subpixel to the left.</param>
    /// <param name="center">The subpixel's own weight.</param>
    /// <param name="right">The weight one subpixel to the right.</param>
    /// <param name="outerRight">The weight two subpixels to the right.</param>
    /// <returns>The filter.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A weight is negative or not finite, or they add up to 0.</exception>
    public static LcdFilter Custom(float outerLeft, float left, float center, float right, float outerRight)
    {
        float[] w = [outerLeft, left, center, right, outerRight];
        if (w.Any(v => !float.IsFinite(v) || v < 0) || w.Sum() <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(center), "The weights must be finite, at least 0 and not all 0.");
        }

        double sum = w.Sum(v => (double)v);
        double[] exact = [.. w.Select(v => v * 256 / sum)];
        int[] weights = [.. exact.Select(v => (int)Math.Floor(v))];
        int missing = 256 - weights.Sum();
        foreach (int i in Enumerable.Range(0, 5).OrderByDescending(i => exact[i] - weights[i]).ThenBy(i => i).Take(missing))
        {
            weights[i]++;
        }

        return new LcdFilter("Custom", weights);
    }

    /// <summary>A weight by tap, 0 to 4.</summary>
    internal int this[int tap] => _weights[tap];

    /// <inheritdoc/>
    public bool Equals(LcdFilter? other) => other is not null && _weights.AsSpan().SequenceEqual(other._weights);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as LcdFilter);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_weights[0], _weights[1], _weights[2], _weights[3], _weights[4]);

    /// <inheritdoc/>
    public override string ToString() => $"{Name} ({string.Join(", ", _weights)})";
}

/// <summary>How LCD text samples a glyph before filtering.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="lcd-look"/>
/// </example>
public enum LcdSampling
{
    /// <summary>Exact coverage of each subpixel's area, antialiased along both axes: Egami's sampling since 2.7.2.</summary>
    Smooth,

    /// <summary>
    /// As measured from GDI+ ClearType: exact coverage across the subpixels, but along the other axis each pixel takes the outline
    /// where it crosses the pixel's centre line, so edges along the stripes are not smoothed.
    /// </summary>
    Gdi,
}
