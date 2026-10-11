namespace Egami.Operations;

/// <summary>
/// The noise <see cref="RasterOps.AddNoise"/> adds: ImageMagick's +noise kinds, with values on 0..1 and the amount as
/// ImageMagick's -attenuate. At amount 1 the formulas are ImageMagick's Q16 ones.
/// </summary>
public enum NoiseKind
{
    /// <summary>Normal noise of standard deviation 5/64 × amount (about 20 of 255 levels at amount 1), plus a small part that grows with the value.</summary>
    Gaussian,

    /// <summary>Uniform noise, ±amount / 128.</summary>
    Uniform,

    /// <summary>Salt and pepper: each value becomes 0 or 1, each with probability amount / 20.</summary>
    Impulse,

    /// <summary>Laplacian (two-sided exponential) noise of scale 5/128 × amount: sharper peaked and heavier tailed than Gaussian.</summary>
    Laplacian,

    /// <summary>Normal noise proportional to the value: v × (1 + amount / 4 × N(0, 1)), so dark areas stay clean.</summary>
    Multiplicative,

    /// <summary>Photon (shot) noise: a Poisson count of mean 12.5 × amount × v, scaled back, so the variance is v / (12.5 × amount).</summary>
    Poisson,
}
