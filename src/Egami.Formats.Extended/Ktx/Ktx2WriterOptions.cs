namespace Egami.Formats.Extended.Ktx;

/// <summary>
/// The formats the KTX 2 writer writes, each a Vulkan <c>VkFormat</c> (or a Basis Universal format, VkFormat 0);
/// <see cref="Ktx2WriterOptions.Srgb"/> picks the _SRGB form where there is one.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="ktx-save"/>
/// </example>
public enum KtxTextureFormat
{
    /// <summary>R8G8B8A8_UNORM (or _SRGB): 8-bit RGBA.</summary>
    Rgba8,

    /// <summary>B8G8R8A8_UNORM (or _SRGB): 8-bit BGRA.</summary>
    Bgra8,

    /// <summary>R8G8B8_UNORM (or _SRGB): 8-bit RGB.</summary>
    Rgb8,

    /// <summary>R8_UNORM (or _SRGB): one 8-bit channel, the raster's gray.</summary>
    R8,

    /// <summary>R8G8_UNORM (or _SRGB): red and green, 8 bits each, as two-channel normal maps keep them.</summary>
    Rg8,

    /// <summary>R16_UNORM: one 16-bit channel, the raster's gray.</summary>
    R16,

    /// <summary>R16G16_UNORM: red and green, 16 bits each.</summary>
    Rg16,

    /// <summary>R16G16B16_UNORM: 16-bit RGB.</summary>
    Rgb16,

    /// <summary>R16G16B16A16_UNORM: 16-bit RGBA.</summary>
    Rgba16,

    /// <summary>R16G16B16A16_SFLOAT: half-float RGBA.</summary>
    RgbaF16,

    /// <summary>R32_SFLOAT: one float channel, the raster's gray.</summary>
    R32F,

    /// <summary>R32G32B32_SFLOAT: float RGB.</summary>
    RgbF32,

    /// <summary>R32G32B32A32_SFLOAT: float RGBA.</summary>
    RgbaF32,

    /// <summary>A2B10G10R10_UNORM_PACK32: 10-bit RGB and 2-bit alpha.</summary>
    Rgb10A2,

    /// <summary>B10G11R11_UFLOAT_PACK32: unsigned 11-, 11- and 10-bit floats, for HDR colour without alpha.</summary>
    R11G11B10F,

    /// <summary>E5B9G9R9_UFLOAT_PACK32: three 9-bit mantissas sharing an exponent, for HDR colour without alpha.</summary>
    Rgb9E5,

    /// <summary>R5G6B5_UNORM_PACK16: 16-bit colour.</summary>
    Bgr565,

    /// <summary>A1R5G5B5_UNORM_PACK16: 15-bit colour and 1-bit alpha.</summary>
    Bgra5551,

    /// <summary>A4R4G4B4_UNORM_PACK16: 4 bits a channel.</summary>
    Bgra4444,

    /// <summary>BC1_RGBA_UNORM_BLOCK (or _SRGB): RGB with 1-bit alpha, 4 bits a pixel.</summary>
    Bc1,

    /// <summary>BC2_UNORM_BLOCK (or _SRGB): RGB with explicit 4-bit alpha, 8 bits a pixel.</summary>
    Bc2,

    /// <summary>BC3_UNORM_BLOCK (or _SRGB): RGB with interpolated alpha, 8 bits a pixel.</summary>
    Bc3,

    /// <summary>BC4_UNORM_BLOCK: one channel, the raster's gray, 4 bits a pixel.</summary>
    Bc4,

    /// <summary>BC5_UNORM_BLOCK: red and green, for normal maps, 8 bits a pixel.</summary>
    Bc5,

    /// <summary>BC6H_UFLOAT_BLOCK: unsigned half-float RGB for HDR, 8 bits a pixel; negatives become 0.</summary>
    Bc6h,

    /// <summary>BC6H_SFLOAT_BLOCK: signed half-float RGB for HDR, 8 bits a pixel.</summary>
    Bc6hSigned,

    /// <summary>BC7_UNORM_BLOCK (or _SRGB): RGBA at 8 bits a pixel, the best quality of the 8-bit BC formats.</summary>
    Bc7,

    /// <summary>
    /// ETC1, stored as ETC2_R8G8B8_UNORM_BLOCK (or _SRGB), of which it is the subset every ETC1 decoder reads: RGB at 4 bits a
    /// pixel.
    /// </summary>
    Etc1,

    /// <summary>ETC2_R8G8B8_UNORM_BLOCK (or _SRGB): RGB at 4 bits a pixel, with ETC2's T, H and planar modes.</summary>
    Etc2Rgb,

    /// <summary>ETC2_R8G8B8A1_UNORM_BLOCK (or _SRGB): RGB with 1-bit alpha (punch-through) at 4 bits a pixel.</summary>
    Etc2RgbA1,

    /// <summary>ETC2_R8G8B8A8_UNORM_BLOCK (or _SRGB): RGB with EAC alpha, 8 bits a pixel.</summary>
    Etc2Rgba,

    /// <summary>EAC_R11_UNORM_BLOCK: one 11-bit channel, the raster's gray, 4 bits a pixel.</summary>
    EacR11,

    /// <summary>EAC_R11_SNORM_BLOCK: one signed 11-bit channel; the raster's 0 to 1 maps to −1 to 1, as the reader maps back.</summary>
    EacR11Signed,

    /// <summary>EAC_R11G11_UNORM_BLOCK: red and green, 11 bits each, for normal maps, 8 bits a pixel.</summary>
    EacRg11,

    /// <summary>EAC_R11G11_SNORM_BLOCK: signed red and green; the raster's 0 to 1 maps to −1 to 1, as the reader maps back.</summary>
    EacRg11Signed,

    /// <summary>ASTC_4x4_UNORM_BLOCK (or _SRGB): LDR ASTC, 8 bits a pixel.</summary>
    Astc4x4,

    /// <summary>ASTC_5x4_UNORM_BLOCK (or _SRGB): LDR ASTC, 6.4 bits a pixel.</summary>
    Astc5x4,

    /// <summary>ASTC_5x5_UNORM_BLOCK (or _SRGB): LDR ASTC, 5.12 bits a pixel.</summary>
    Astc5x5,

    /// <summary>ASTC_6x5_UNORM_BLOCK (or _SRGB): LDR ASTC, 4.27 bits a pixel.</summary>
    Astc6x5,

    /// <summary>ASTC_6x6_UNORM_BLOCK (or _SRGB): LDR ASTC, 3.56 bits a pixel.</summary>
    Astc6x6,

    /// <summary>ASTC_8x5_UNORM_BLOCK (or _SRGB): LDR ASTC, 3.2 bits a pixel.</summary>
    Astc8x5,

    /// <summary>ASTC_8x6_UNORM_BLOCK (or _SRGB): LDR ASTC, 2.67 bits a pixel.</summary>
    Astc8x6,

    /// <summary>ASTC_8x8_UNORM_BLOCK (or _SRGB): LDR ASTC, 2 bits a pixel.</summary>
    Astc8x8,

    /// <summary>ASTC_10x5_UNORM_BLOCK (or _SRGB): LDR ASTC, 2.56 bits a pixel.</summary>
    Astc10x5,

    /// <summary>ASTC_10x6_UNORM_BLOCK (or _SRGB): LDR ASTC, 2.13 bits a pixel.</summary>
    Astc10x6,

    /// <summary>ASTC_10x8_UNORM_BLOCK (or _SRGB): LDR ASTC, 1.6 bits a pixel.</summary>
    Astc10x8,

    /// <summary>ASTC_10x10_UNORM_BLOCK (or _SRGB): LDR ASTC, 1.28 bits a pixel.</summary>
    Astc10x10,

    /// <summary>ASTC_12x10_UNORM_BLOCK (or _SRGB): LDR ASTC, 1.07 bits a pixel.</summary>
    Astc12x10,

    /// <summary>ASTC_12x12_UNORM_BLOCK (or _SRGB): LDR ASTC, 0.89 bits a pixel.</summary>
    Astc12x12,

    /// <summary>
    /// Basis Universal ETC1S with BasisLZ supercompression: shared endpoint and selector codebooks
    /// (<see cref="Ktx2WriterOptions.BasisQuality"/> sets their size), the smallest files, transcodable to any GPU format. An
    /// alpha channel gets its own slices when the raster has transparent pixels.
    /// </summary>
    Etc1s,

    /// <summary>
    /// Basis Universal UASTC LDR 4 × 4: 8 bits a pixel, near BC7 in quality, transcodable to any GPU format; usually paired with
    /// <see cref="KtxSupercompression.Zstandard"/> and <see cref="Ktx2WriterOptions.UastcRdo"/>.
    /// </summary>
    Uastc,

    /// <summary>R8_UINT: unsigned integers.</summary>
    R8UInt,

    /// <summary>R8_SINT: signed integers. The raster's 0 to the full scale map to the type's minimum to maximum, as the reader maps back.</summary>
    R8SInt,

    /// <summary>R8G8_UINT: unsigned integers.</summary>
    Rg8UInt,

    /// <summary>R8G8_SINT: signed integers. The raster's 0 to the full scale map to the type's minimum to maximum, as the reader maps back.</summary>
    Rg8SInt,

    /// <summary>R8G8B8A8_UINT: unsigned integers.</summary>
    Rgba8UInt,

    /// <summary>R8G8B8A8_SINT: signed integers. The raster's 0 to the full scale map to the type's minimum to maximum, as the reader maps back.</summary>
    Rgba8SInt,

    /// <summary>R16_UINT: unsigned integers.</summary>
    R16UInt,

    /// <summary>R16_SINT: signed integers. The raster's 0 to the full scale map to the type's minimum to maximum, as the reader maps back.</summary>
    R16SInt,

    /// <summary>R16G16_UINT: unsigned integers.</summary>
    Rg16UInt,

    /// <summary>R16G16_SINT: signed integers. The raster's 0 to the full scale map to the type's minimum to maximum, as the reader maps back.</summary>
    Rg16SInt,

    /// <summary>R16G16B16A16_UINT: unsigned integers.</summary>
    Rgba16UInt,

    /// <summary>R16G16B16A16_SINT: signed integers. The raster's 0 to the full scale map to the type's minimum to maximum, as the reader maps back.</summary>
    Rgba16SInt,

    /// <summary>R32_UINT: unsigned integers. The raster's float values round to the nearest integer and clamp to the type's range.</summary>
    R32UInt,

    /// <summary>R32_SINT: signed integers. The raster's float values round to the nearest integer and clamp to the type's range.</summary>
    R32SInt,

    /// <summary>R32G32_UINT: unsigned integers. The raster's float values round to the nearest integer and clamp to the type's range.</summary>
    Rg32UInt,

    /// <summary>R32G32_SINT: signed integers. The raster's float values round to the nearest integer and clamp to the type's range.</summary>
    Rg32SInt,

    /// <summary>R32G32B32_UINT: unsigned integers. The raster's float values round to the nearest integer and clamp to the type's range.</summary>
    Rgb32UInt,

    /// <summary>R32G32B32_SINT: signed integers. The raster's float values round to the nearest integer and clamp to the type's range.</summary>
    Rgb32SInt,

    /// <summary>R32G32B32A32_UINT: unsigned integers. The raster's float values round to the nearest integer and clamp to the type's range.</summary>
    Rgba32UInt,

    /// <summary>R32G32B32A32_SINT: signed integers. The raster's float values round to the nearest integer and clamp to the type's range.</summary>
    Rgba32SInt,

    /// <summary>D16_UNORM: 16-bit depth, the raster's gray.</summary>
    D16,

    /// <summary>D32_SFLOAT: float depth, the raster's gray.</summary>
    D32F,
}

/// <summary>Options for writing KTX 2.</summary>
/// <remarks>
/// Re-saving a raster loaded from KTX or KTX 2 keeps its format and sRGB flag for every option left at <see langword="null"/>
/// when the writer writes that format; a raster loaded from DDS keeps its block format the same way.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="ktx-save"/>
/// </example>
public sealed class Ktx2WriterOptions : WriterOptions
{
    private readonly int _mipLevels;
    private readonly int _effort = 4;
    private readonly int _compressionLevel;
    private readonly int _basisQuality = 128;
    private readonly float _uastcRdo;

    /// <summary>Gets the default options.</summary>
    public static new Ktx2WriterOptions Default { get; } = new();

    /// <summary>
    /// Gets the format to write, or <see langword="null"/> (the default) for a KTX, KTX 2 or DDS source's own format when it is
    /// writable, else the uncompressed one nearest the raster's pixel type (<see cref="KtxTextureFormat.Rgba8"/> for 8-bit
    /// colour, <see cref="KtxTextureFormat.R8"/> for 8-bit gray, <see cref="KtxTextureFormat.RgbaF16"/> for half floats and so
    /// on). A block format is chosen only from a texture source.
    /// </summary>
    public KtxTextureFormat? Format { get; init; }

    /// <summary>
    /// Gets how many mip levels to write: 0 (the default) for the full chain down to 1 × 1, 1 for mip 0 alone, or that many, at
    /// most the full chain. Mips average 2 × 2 blocks of premultiplied pixels (<see cref="MipChain"/>), in linear light for an
    /// sRGB format. An integer or depth format writes mip 0 alone unless a count is given, since averaging its values rarely
    /// means anything.
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
    /// Gets whether to write the format's _SRGB form, or <see langword="null"/> (the default) to write it when the source texture
    /// was sRGB, or else when the raster is tagged sRGB. The 8-bit colour formats, BC1 to BC3, BC7, ETC1, ETC2, ASTC and the
    /// Basis formats have one; for the others the default falls back to UNORM, and <see langword="true"/> throws.
    /// </summary>
    public bool? Srgb { get; init; }

    /// <summary>Gets whether to write colour premultiplied by alpha, flagged so in the data format descriptor. Defaults to <see langword="false"/>.</summary>
    public bool PremultipliedAlpha { get; init; }

    /// <summary>
    /// Gets whether SaveAll writes its rasters as cube maps, six faces a cube in the order +X, −X, +Y, −Y, +Z, −Z, rather than as
    /// a texture array. The rasters must then be square and a multiple of six. Defaults to <see langword="false"/>.
    /// </summary>
    public bool Cube { get; init; }

    /// <summary>
    /// Gets the supercompression of each mip level: <see cref="KtxSupercompression.None"/> (the default),
    /// <see cref="KtxSupercompression.Zstandard"/> or <see cref="KtxSupercompression.Zlib"/>. <see cref="KtxTextureFormat.Etc1s"/>
    /// always takes <see cref="KtxSupercompression.BasisLZ"/>, which no other format can.
    /// </summary>
    public KtxSupercompression Supercompression { get; init; }

    /// <summary>
    /// Gets the Zstandard (1 to 22) or zlib (1 to 9) level, or 0 (the default) for the scheme's default: Zstandard
    /// <see cref="Compression.Zstandard.DefaultLevel"/>, zlib 6.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 22.</exception>
    public int CompressionLevel
    {
        get => _compressionLevel;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 22);
            _compressionLevel = value;
        }
    }

    /// <summary>
    /// Gets the quality of <see cref="KtxTextureFormat.Etc1s"/>, 1 to 255: it sets the size of the endpoint and selector
    /// codebooks, from 16 entries to 16128. Defaults to 128 (about 1000 entries).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 1 to 255.</exception>
    public int BasisQuality
    {
        get => _basisQuality;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 255);
            _basisQuality = value;
        }
    }

    /// <summary>
    /// Gets the rate-distortion trade for <see cref="KtxTextureFormat.Uastc"/>: 0 (the default) leaves the blocks alone; higher
    /// values let blocks repeat bytes of their neighbours so Zstandard compresses them better, at a loss of quality. About 1 makes
    /// files a few per cent smaller, 16 about a third smaller and 3 to 4 dB lower.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not a number.</exception>
    public float UastcRdo
    {
        get => _uastcRdo;
        init
        {
            if (!(value >= 0) || float.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The UASTC rate-distortion trade is 0 or more.");
            }

            _uastcRdo = value;
        }
    }
}
