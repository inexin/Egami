namespace Egami.Formats.Extended.Dds;

/// <summary>The formats the DDS writer writes, each a DXGI format; <see cref="DdsWriterOptions.Srgb"/> picks the _SRGB form where there is one.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="dds-save"/>
/// </example>
public enum DdsTextureFormat
{
    /// <summary>R8G8B8A8_UNORM: 8-bit RGBA. Legacy header A8B8G8R8.</summary>
    Rgba8,

    /// <summary>B8G8R8A8_UNORM: 8-bit BGRA. Legacy header A8R8G8B8.</summary>
    Bgra8,

    /// <summary>B8G8R8X8_UNORM: 8-bit BGR with an unused byte. Legacy header X8R8G8B8.</summary>
    Bgrx8,

    /// <summary>R8_UNORM: one 8-bit channel, the raster's gray. Legacy header L8.</summary>
    R8,

    /// <summary>R8G8_UNORM: red and green, 8 bits each, as two-channel normal maps keep them.</summary>
    Rg8,

    /// <summary>A8_UNORM: alpha only. Legacy header A8.</summary>
    A8,

    /// <summary>R16_UNORM: one 16-bit channel, the raster's gray. Legacy header L16.</summary>
    R16,

    /// <summary>R16G16_UNORM: red and green, 16 bits each. Legacy header G16R16.</summary>
    Rg16,

    /// <summary>R16G16B16A16_UNORM: 16-bit RGBA. Legacy header D3DFMT 36.</summary>
    Rgba16,

    /// <summary>R16G16B16A16_FLOAT: half-float RGBA.</summary>
    RgbaF16,

    /// <summary>R32_FLOAT: one float channel, the raster's gray.</summary>
    R32F,

    /// <summary>R32G32B32_FLOAT: float RGB.</summary>
    RgbF32,

    /// <summary>R32G32B32A32_FLOAT: float RGBA.</summary>
    RgbaF32,

    /// <summary>R10G10B10A2_UNORM: 10-bit RGB and 2-bit alpha.</summary>
    Rgb10A2,

    /// <summary>R11G11B10_FLOAT: unsigned 11-, 11- and 10-bit floats, for HDR colour without alpha.</summary>
    R11G11B10F,

    /// <summary>R9G9B9E5_SHAREDEXP: three 9-bit mantissas sharing an exponent, for HDR colour without alpha.</summary>
    Rgb9E5,

    /// <summary>B5G6R5_UNORM: 16-bit colour. Legacy header R5G6B5.</summary>
    Bgr565,

    /// <summary>B5G5R5A1_UNORM: 15-bit colour and 1-bit alpha. Legacy header A1R5G5B5.</summary>
    Bgra5551,

    /// <summary>B4G4R4A4_UNORM: 4 bits a channel. Legacy header A4R4G4B4.</summary>
    Bgra4444,

    /// <summary>BC1 (DXT1): RGB with 1-bit alpha, 4 bits a pixel.</summary>
    Bc1,

    /// <summary>BC2 (DXT3): RGB with explicit 4-bit alpha, 8 bits a pixel.</summary>
    Bc2,

    /// <summary>BC3 (DXT5): RGB with interpolated alpha, 8 bits a pixel.</summary>
    Bc3,

    /// <summary>BC4 (ATI1): one channel, the raster's gray, 4 bits a pixel.</summary>
    Bc4,

    /// <summary>BC5 (ATI2): red and green, for normal maps, 8 bits a pixel.</summary>
    Bc5,

    /// <summary>BC7: RGBA at 8 bits a pixel, with eight modes and partitions; the best quality of the 8-bit block formats. DX10 header.</summary>
    Bc7,

    /// <summary>BC6H_UF16: unsigned half-float RGB for HDR, 8 bits a pixel; negatives become 0. DX10 header.</summary>
    Bc6h,

    /// <summary>BC6H_SF16: signed half-float RGB for HDR, 8 bits a pixel. DX10 header.</summary>
    Bc6hSigned,
}

/// <summary>Options for writing DDS.</summary>
/// <remarks>
/// Re-saving a raster loaded from DDS keeps its format and sRGB flag for every option left at <see langword="null"/>, and a
/// raster loaded at mip 0 with its mips, and unchanged by operations since, writes the file's own mips 1 and on rather than new
/// ones (mip 0 is always written from the pixels).
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="dds-save"/>
/// </example>
public sealed class DdsWriterOptions : WriterOptions
{
    private readonly int _mipLevels;
    private readonly int _effort = 4;

    /// <summary>Gets the default options.</summary>
    public static new DdsWriterOptions Default { get; } = new();

    /// <summary>
    /// Gets the format to write, or <see langword="null"/> (the default) for a DDS source's own format when it is writable, else
    /// the one nearest the raster's pixel type (<see cref="DdsTextureFormat.Rgba8"/> for 8-bit colour, <see cref="DdsTextureFormat.R8"/>
    /// for 8-bit gray, <see cref="DdsTextureFormat.RgbaF16"/> for half floats and so on). A block format is chosen only from a
    /// DDS source.
    /// </summary>
    public DdsTextureFormat? Format { get; init; }

    /// <summary>
    /// Gets how many mip levels to write: 0 (the default) for the full chain down to 1 × 1, 1 for mip 0 alone, or that many, at
    /// most the full chain. Mips average 2 × 2 blocks of premultiplied pixels (<see cref="MipChain"/>), in linear light for an
    /// sRGB format.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int MipLevels
    {
        get => _mipLevels;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _mipLevels = value;
        }
    }

    /// <summary>
    /// Gets how hard the block encoders search, 0 (fastest) to 6 (best), on WebP's scale. Defaults to 4. The output is the same
    /// for any number of threads.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 6.</exception>
    public int Effort
    {
        get => _effort;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 6);
            _effort = value;
        }
    }

    /// <summary>
    /// Gets whether to write the format's _SRGB form, or <see langword="null"/> (the default) to write it when the DDS source was
    /// sRGB. Only <see cref="DdsTextureFormat.Rgba8"/>, <see cref="DdsTextureFormat.Bgra8"/>, <see cref="DdsTextureFormat.Bgrx8"/>
    /// and BC1 to BC3 have one; an sRGB format takes the DX10 header.
    /// </summary>
    public bool? Srgb { get; init; }

    /// <summary>
    /// Gets whether to write the DX10 header even when the format has a legacy one. Defaults to <see langword="false"/>: the
    /// legacy header, which old tools read, whenever the format, sRGB flag, alpha mode and layout allow it.
    /// </summary>
    public bool Dx10Header { get; init; }

    /// <summary>
    /// Gets whether to write colour premultiplied by alpha: the DX10 header's premultiplied alpha mode, or DXT2 and DXT4 for BC2
    /// and BC3 under the legacy header. Defaults to <see langword="false"/>.
    /// </summary>
    public bool PremultipliedAlpha { get; init; }

    /// <summary>
    /// Gets whether SaveAll writes its rasters as cube maps, six faces a cube in the order +X, −X, +Y, −Y, +Z, −Z, rather than as
    /// a texture array. The rasters must then be square and a multiple of six. Defaults to <see langword="false"/>.
    /// </summary>
    public bool Cube { get; init; }
}
