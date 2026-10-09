namespace Egami.Quantization;

/// <summary>How the error of mapping pixels to a palette is spread.</summary>
public enum DitherMode
{
    /// <summary>Floyd–Steinberg error diffusion: the smoothest gradients; frames of an animation dither one row after another.</summary>
    FloydSteinberg,

    /// <summary>An 8×8 ordered (Bayer) pattern: stable between animation frames and parallel, at some cost in quality.</summary>
    Ordered,

    /// <summary>No dithering: each pixel takes the nearest palette color.</summary>
    None,
}

/// <summary>
/// How formats with at most 256 colors (GIF, and PNG with a palette) build their palette when an image has more colors than fit.
/// Images with few enough colors always keep them exactly, without dithering.
/// </summary>
public sealed record PaletteOptions
{
    private readonly int _maxColors = 256;

    /// <summary>Gets the default options: 256 colors, Floyd–Steinberg dithering, alpha threshold 128.</summary>
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
    public DitherMode Dither { get; init; }

    /// <summary>
    /// Gets the alpha below which a pixel is transparent, for formats whose transparency is on or off (GIF). Defaults to 128.
    /// </summary>
    public byte AlphaThreshold { get; init; } = 128;

    /// <summary>
    /// Gets whether a built palette maps each pixel to the color of the quantizer box it falls in instead of searching for the
    /// nearest color. Several times faster with dithering, but about 0.3 to 0.5 dB worse without it and up to 2 dB worse with
    /// it, since dithered colors can land in a box whose color is not the nearest. Defaults to <see langword="false"/>.
    /// Palettes that hold every color exactly, and palettes with partial alpha, always map exactly or to the nearest color.
    /// </summary>
    public bool FastMapping { get; init; }
}
