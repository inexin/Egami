namespace Egami.Quantization;

/// <summary>
/// How the error of mapping pixels to a palette is spread: error diffusion (Floyd–Steinberg and eight other published kernels, and
/// Riemersma's Hilbert-curve walk) or an ordered pattern (Bayer 2, 4, 8 and 16, and 3 × 3). The values are fixed.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/AnimationAndMetadata.cs" region="gif"/>
/// </example>
public enum DitherMode
{
    /// <summary>Floyd–Steinberg error diffusion: the smoothest gradients; frames of an animation dither one row after another.</summary>
    FloydSteinberg,

    /// <summary>An 8×8 ordered (Bayer) pattern: stable between animation frames and parallel, at some cost in quality.</summary>
    Ordered,

    /// <summary>No dithering: each pixel takes the nearest palette color.</summary>
    None,

    /// <summary>Atkinson's error diffusion (Apple, 1984): spreads six eighths of the error, so flat areas keep more contrast.</summary>
    Atkinson,

    /// <summary>Burkes' error diffusion (1988): two rows, 32nds.</summary>
    Burkes,

    /// <summary>Jarvis, Judice and Ninke's error diffusion (1976): three rows, 48ths; smooth but slow.</summary>
    JarvisJudiceNinke,

    /// <summary>Sierra's three-row error diffusion (1989), 32nds.</summary>
    Sierra3,

    /// <summary>Sierra's two-row error diffusion, 16ths.</summary>
    Sierra2,

    /// <summary>Sierra's Filter Lite: three neighbours, quarters; nearly as smooth as Floyd–Steinberg and faster.</summary>
    SierraLite,

    /// <summary>Stevenson and Arce's error diffusion (1985), four rows, 200ths.</summary>
    StevensonArce,

    /// <summary>Stucki's error diffusion (1981): three rows, 42nds; sharper than Jarvis, Judice and Ninke.</summary>
    Stucki,

    /// <summary>Riemersma's dithering (1998): error diffusion along a Hilbert curve with a 16-pixel memory, so no direction shows.</summary>
    Riemersma,

    /// <summary>A 2 × 2 ordered (Bayer) pattern: coarse, visible cross-hatching.</summary>
    Bayer2x2,

    /// <summary>A 4 × 4 ordered (Bayer) pattern.</summary>
    Bayer4x4,

    /// <summary>A 16 × 16 ordered (Bayer) pattern: the finest steps, for large palettes.</summary>
    Bayer16x16,

    /// <summary>A 3 × 3 ordered pattern.</summary>
    Ordered3x3,
}

/// <summary>
/// How formats with at most 256 colors (GIF, PNG with a palette, and indexed BMP and TGA) build their palette when an image has more colors than fit.
/// Images with few enough colors always keep them exactly, without dithering.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/AnimationAndMetadata.cs" region="gif"/>
/// </example>
public sealed record PaletteOptions
{
    private readonly DitherMode _dither;

    private readonly int _maxColors = 256;

    private readonly float _strength = 1;

    private readonly int _refine;

    /// <summary>Gets the default options: 256 colors, Floyd–Steinberg dithering at full strength, alpha threshold 128.</summary>
    public static PaletteOptions Default { get; } = new();

    /// <summary>Gets the most colors the palette may have, 2 to 256. Defaults to 256; a transparent entry counts as one.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 2 to 256.</exception>
    public int MaxColors
    {
        get => _maxColors;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 2);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 256);
            _maxColors = value;
        }
    }

    /// <summary>Gets how mapping error is spread. Defaults to <see cref="DitherMode.FloydSteinberg"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="DitherMode"/>.</exception>
    public DitherMode Dither
    {
        get => _dither;
        init => _dither = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The dither mode is not defined.");
    }

    /// <summary>
    /// Gets the alpha below which a pixel is transparent, for formats whose transparency is on or off (GIF). Defaults to 128.
    /// </summary>
    public byte AlphaThreshold { get; init; } = 128;

    /// <summary>
    /// Gets a fixed palette to map to instead of building one, or <see langword="null"/> (the default) to build one from the
    /// image. Every writer that takes these options honours it, and throws when the palette has more colors than its format allows.
    /// </summary>
    public Palette? FixedPalette { get; init; }

    /// <summary>
    /// Gets how strongly the dither works, from 0 (none: the nearest color) to 1 (the default, full strength): the share of the
    /// error that is diffused, or the height of the ordered pattern.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 1.</exception>
    public float DitherStrength
    {
        get => _strength;
        init => _strength = value is >= 0 and <= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The strength must be from 0 to 1.");
    }

    /// <summary>
    /// Gets whether error diffusion alternates direction from row to row (serpentine scanning), which breaks up the diagonal
    /// streaks plain left-to-right diffusion can leave. Defaults to <see langword="false"/>.
    /// </summary>
    public bool Serpentine { get; init; }

    /// <summary>
    /// Gets how many k-means passes polish the built palette, moving each color to the mean of the pixels nearest it (Lloyd's
    /// algorithm on every fourth pixel), from 0 (the default) to 32. 4 to 8 passes gain a few tenths of a decibel at some cost;
    /// they never make the palette fit worse.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 32.</exception>
    public int RefineIterations
    {
        get => _refine;
        init => _refine = value is >= 0 and <= 32 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The passes must be from 0 to 32.");
    }

    /// <summary>
    /// Gets whether each color is matched to its exact nearest palette color. By default a 64-level lookup table answers for each
    /// small cube of colors at once, which is faster and can pick a color that is not quite the nearest;
    /// the default keeps every 1.x output unchanged.
    /// </summary>
    public bool ExactMatching { get; init; }

    /// <summary>Gets a custom error-diffusion kernel used instead of <see cref="Dither"/>, or <see langword="null"/> (the default).</summary>
    public ErrorDiffusionKernel? Kernel { get; init; }

    /// <summary>Gets whether the options ask for exactly what 1.x did, so the original mapping code runs.</summary>
    internal bool IsClassic => FixedPalette is null && _strength == 1 && !Serpentine && _refine == 0 && !ExactMatching && Kernel is null;
}
