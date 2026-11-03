using Egami.Metadata;

namespace Egami.Formats.Extended.Ktx;

/// <summary>
/// KTX 1.1, the Khronos texture container for OpenGL: a header naming the OpenGL format, key/value data, then every mip level
/// of every array layer, cube face and depth slice, uncompressed or in BC1 to BC7 blocks, in either byte order. Load gives mip 0
/// of the first surface, turned upright and swizzled as the file's <c>KTXorientation</c> and <c>KTXswizzle</c> say; LoadAll and
/// <see cref="KtxReaderOptions"/> reach the others. Read only. Registered with <see cref="ExtendedContextExtensions.WithKtx"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="ktx-load"/>
/// </example>
public static class KtxFormat
{
    /// <summary>Gets the format's registration: name, MIME type, extension, signature and reader.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "KTX",
        MimeTypes = ["image/ktx"],
        Extensions = ["ktx"],
        SignatureLength = 12,
        Matches = Ktx1Header.Matches,
        Reader = new KtxReader(KtxContainer.Ktx1),
    };
}

/// <summary>
/// KTX 2.0, the Khronos texture container for Vulkan, glTF and the web: a header naming the Vulkan format, a level index, a
/// data format descriptor with the colour space, key/value data, then the mip levels smallest first, each optionally
/// supercompressed with Zstandard or zlib. Load gives mip 0 of the first surface, turned upright and swizzled as the file
/// says; LoadAll and <see cref="KtxReaderOptions"/> reach the others. Uncompressed and BC1 to BC7 textures read; ETC, ASTC and
/// Basis Universal ones are refused for now. Read only. Registered with <see cref="ExtendedContextExtensions.WithKtx2"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="ktx-load"/>
/// </example>
public static class Ktx2Format
{
    /// <summary>Gets the format's registration: name, MIME type, extension, signature and reader.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "KTX2",
        MimeTypes = ["image/ktx2"],
        Extensions = ["ktx2"],
        SignatureLength = 12,
        Matches = Ktx2Header.Matches,
        Reader = new KtxReader(KtxContainer.Ktx2),
    };
}

/// <summary>The kind of texture a KTX file holds.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="ktx-details"/>
/// </example>
public enum KtxDimension
{
    /// <summary>A row of texels: height 1.</summary>
    Texture1D,

    /// <summary>A 2D texture, an array of them or a cube map.</summary>
    Texture2D,

    /// <summary>A volume texture: a stack of depth slices that shrinks with each mip.</summary>
    Texture3D,
}

/// <summary>How a KTX 2 file's mip levels are compressed on top of their texture format.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="ktx-details"/>
/// </example>
public enum KtxSupercompression
{
    /// <summary>Not supercompressed; always so in KTX 1.</summary>
    None = 0,

    /// <summary>Basis Universal's BasisLZ, for ETC1S textures. Not read yet.</summary>
    BasisLZ = 1,

    /// <summary>Zstandard, each level on its own.</summary>
    Zstandard = 2,

    /// <summary>zlib, each level on its own.</summary>
    Zlib = 3,
}

/// <summary>How a KTX file's alpha channel is meant to be read.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="ktx-details"/>
/// </example>
public enum KtxAlphaMode
{
    /// <summary>Straight alpha, or no alpha.</summary>
    Straight,

    /// <summary>Colours premultiplied by alpha, as a KTX 2 data format descriptor can say. Egami un-premultiplies them on load.</summary>
    Premultiplied,
}

/// <summary>Details only KTX and KTX 2 have, kept in <see cref="RasterMetadata.FormatDetails"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="ktx-details"/>
/// </example>
public sealed class KtxDetails : FormatDetails
{
    /// <summary>
    /// Gets or sets the Vulkan format number, as KTX 2 stores it (37 is R8G8B8A8_UNORM, 145 BC7_UNORM_BLOCK). For a KTX 1 file,
    /// the Vulkan format its OpenGL format matches, or 0 for none.
    /// </summary>
    public int VkFormat { get; set; }

    /// <summary>Gets or sets a KTX 1 file's OpenGL internal format (0x8058 is RGBA8), or 0 for a KTX 2 file.</summary>
    public int GlInternalFormat { get; set; }

    /// <summary>Gets or sets the format's name: its Vulkan name without <c>VK_FORMAT_</c>, such as <c>BC7_SRGB_BLOCK</c>.</summary>
    public string FormatName { get; set; } = "";

    /// <summary>Gets or sets how the mip levels are compressed on top of the format.</summary>
    public KtxSupercompression Supercompression { get; set; }

    /// <summary>Gets or sets the texture's dimension.</summary>
    public KtxDimension Dimension { get; set; } = KtxDimension.Texture2D;

    /// <summary>Gets or sets whether the texture is a cube map: 6 faces in each layer.</summary>
    public bool IsCube { get; set; }

    /// <summary>Gets or sets the number of array layers, at least 1.</summary>
    public int Layers { get; set; } = 1;

    /// <summary>Gets or sets the number of faces in each layer: 6 for a cube map, else 1.</summary>
    public int Faces { get; set; } = 1;

    /// <summary>Gets or sets the depth of mip 0 of a volume texture, else 1.</summary>
    public int Depth { get; set; } = 1;

    /// <summary>Gets or sets the number of mip levels the file stores, at least 1.</summary>
    public int MipLevels { get; set; } = 1;

    /// <summary>Gets or sets whether the values are sRGB-encoded: an <c>_SRGB</c> format, or a KTX 2 descriptor with the sRGB transfer.</summary>
    public bool IsSrgb { get; set; }

    /// <summary>Gets or sets how the alpha channel is meant to be read.</summary>
    public KtxAlphaMode AlphaMode { get; set; }

    /// <summary>
    /// Gets or sets the stored orientation in KTX 2's form: <c>r</c> or <c>l</c> for the direction of x, <c>d</c> or <c>u</c> for
    /// y, and for a volume <c>i</c> or <c>o</c> for z, such as <c>rd</c> (rows top-down, the default) or <c>ru</c> (bottom-up).
    /// A load turns the texture to <c>rd</c> unless <see cref="KtxReaderOptions.KeepStoredLayout"/> is set.
    /// </summary>
    public string Orientation { get; set; } = "rd";

    /// <summary>
    /// Gets or sets the file's channel swizzle, four of <c>r</c>, <c>g</c>, <c>b</c>, <c>a</c>, <c>0</c> and <c>1</c>, such as
    /// <c>rrr1</c> for luminance stored in red. <c>rgba</c> (the default) changes nothing. A load applies it unless
    /// <see cref="KtxReaderOptions.KeepStoredLayout"/> is set.
    /// </summary>
    public string Swizzle { get; set; } = "rgba";

    /// <summary>Gets or sets the surface this raster is: layer-major, then face (+X, −X, +Y, −Y, +Z, −Z), then depth slice.</summary>
    public int Surface { get; set; }

    /// <summary>Gets or sets the mip level this raster is; 0 is full size.</summary>
    public int MipLevel { get; set; }

    /// <summary>
    /// Gets or sets the file's other key/value pairs, such as <c>KTXwriter</c>, their values as UTF-8 text without the closing
    /// NUL. Values that are not text are left out; <c>KTXorientation</c> and <c>KTXswizzle</c> are in their own properties.
    /// </summary>
    public IReadOnlyDictionary<string, string> KeyValues { get; set; } = new Dictionary<string, string>();

    /// <inheritdoc/>
    public override FormatDetails Copy()
    {
        return new KtxDetails
        {
            VkFormat = VkFormat,
            GlInternalFormat = GlInternalFormat,
            FormatName = FormatName,
            Supercompression = Supercompression,
            Dimension = Dimension,
            IsCube = IsCube,
            Layers = Layers,
            Faces = Faces,
            Depth = Depth,
            MipLevels = MipLevels,
            IsSrgb = IsSrgb,
            AlphaMode = AlphaMode,
            Orientation = Orientation,
            Swizzle = Swizzle,
            Surface = Surface,
            MipLevel = MipLevel,
            KeyValues = KeyValues,
        };
    }
}

/// <summary>Options for reading KTX and KTX 2.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="ktx-surfaces"/>
/// </example>
public sealed record KtxReaderOptions : ReaderOptions
{
    private readonly int? _surface;
    private readonly int? _mipLevel;

    /// <summary>
    /// Gets the surface Load returns, or <see langword="null"/> (the default) for the first. Surfaces count layer-major, then
    /// face (+X, −X, +Y, −Y, +Z, −Z), then depth slice: surface <c>(layer × faces + face) × depth + slice</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int? Surface
    {
        get => _surface;
        init
        {
            if (value is int v)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(v);
            }

            _surface = value;
        }
    }

    /// <summary>
    /// Gets the mip level Load returns, or <see langword="null"/> (the default) for mip 0, or the smallest that covers
    /// <see cref="ReaderOptions.MinimumSize"/>. A volume's depth slice at mip <c>m</c> is slice <c>slice >> m</c> of that mip.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int? MipLevel
    {
        get => _mipLevel;
        init
        {
            if (value is int v)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(v);
            }

            _mipLevel = value;
        }
    }

    /// <summary>
    /// Gets whether LoadAll also returns every mip level, each after the surface it belongs to. Defaults to
    /// <see langword="false"/>: mip 0 of each surface only.
    /// </summary>
    public bool IncludeMipLevels { get; init; }

    /// <summary>
    /// Gets whether a two-channel normal map (BC5, R8G8, R16G16) gets its blue channel rebuilt as the normal's Z,
    /// sqrt(1 − x² − y²), instead of 0. Defaults to <see langword="false"/>.
    /// </summary>
    public bool ReconstructNormalZ { get; init; }

    /// <summary>
    /// Gets whether rows, columns and channels come out as stored, without the file's orientation and swizzle applied. Defaults
    /// to <see langword="false"/>: textures come out top-down with the swizzle applied. <see cref="KtxDetails"/> keeps both
    /// either way.
    /// </summary>
    public bool KeepStoredLayout { get; init; }
}
