using Egami.Metadata;

namespace Egami.Formats.Extended.Dds;

/// <summary>
/// DDS (DirectDraw Surface), the texture container of Direct3D and most game engines: a header, then every mip level of every
/// face, array slice or depth slice, uncompressed or in BC1 to BC7 blocks. Load gives mip 0 of the first surface; LoadAll and
/// <see cref="DdsReaderOptions"/> reach the others. Save writes a texture with a full mip chain, SaveAll an array or a cube map
/// (<see cref="DdsWriterOptions"/>). Registered with <see cref="ExtendedContextExtensions.WithDds"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="dds-load"/>
/// </example>
public static class DdsFormat
{
    /// <summary>Gets the format's registration: name, MIME types, extensions, signature, reader and writer.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "DDS",
        MimeTypes = ["image/vnd-ms.dds", "image/x-dds"],
        Extensions = ["dds"],
        SignatureLength = 8,
        Matches = DdsHeader.Matches,
        Reader = new DdsReader(),
        Writer = new DdsWriter(),
    };
}

/// <summary>The kind of texture a DDS file holds.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="dds-details"/>
/// </example>
public enum DdsDimension
{
    /// <summary>A row of texels: height 1.</summary>
    Texture1D,

    /// <summary>A 2D texture, an array of them or a cube map.</summary>
    Texture2D,

    /// <summary>A volume texture: a stack of depth slices that shrinks with each mip.</summary>
    Texture3D,
}

/// <summary>How a DDS file's alpha channel is meant to be read, from its DX10 header.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="dds-details"/>
/// </example>
public enum DdsAlphaMode
{
    /// <summary>Not stated; Egami reads alpha as straight.</summary>
    Unknown,

    /// <summary>Straight alpha.</summary>
    Straight,

    /// <summary>Colours premultiplied by alpha (also DXT2 and DXT4). Egami un-premultiplies them on load.</summary>
    Premultiplied,

    /// <summary>Alpha is fully opaque.</summary>
    Opaque,

    /// <summary>The fourth channel is not alpha but data of the application's own.</summary>
    Custom,
}

/// <summary>Details only DDS has, kept in <see cref="RasterMetadata.FormatDetails"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="dds-details"/>
/// </example>
public sealed class DdsDetails : FormatDetails
{
    /// <summary>
    /// Gets or sets the DXGI format number, as Microsoft's <c>DXGI_FORMAT</c> lists it (98 is BC7_UNORM). A legacy header's
    /// format is given as the DXGI format it maps to, or 0 for bit masks and palettes that have none.
    /// </summary>
    public int DxgiFormat { get; set; }

    /// <summary>Gets or sets the format's name, such as <c>BC7_UNORM_SRGB</c>, <c>DXT1</c>-style legacy formats under their DXGI name, or a description of legacy bit masks.</summary>
    public string FormatName { get; set; } = "";

    /// <summary>Gets or sets the legacy header's FourCC, such as <c>DXT5</c>, or <see langword="null"/> for none.</summary>
    public string? FourCC { get; set; }

    /// <summary>Gets or sets whether the file has the DX10 header.</summary>
    public bool Dx10Header { get; set; }

    /// <summary>Gets or sets the texture's dimension.</summary>
    public DdsDimension Dimension { get; set; } = DdsDimension.Texture2D;

    /// <summary>Gets or sets whether the texture is a cube map.</summary>
    public bool IsCube { get; set; }

    /// <summary>Gets or sets the number of array elements; for a cube map, the number of cubes.</summary>
    public int ArraySize { get; set; } = 1;

    /// <summary>Gets or sets the depth of mip 0 of a volume texture, else 1.</summary>
    public int Depth { get; set; } = 1;

    /// <summary>Gets or sets the number of mip levels the file stores, at least 1.</summary>
    public int MipLevels { get; set; } = 1;

    /// <summary>Gets or sets how the alpha channel is meant to be read.</summary>
    public DdsAlphaMode AlphaMode { get; set; }

    /// <summary>Gets or sets whether the format is an <c>_SRGB</c> one, whose values are sRGB-encoded.</summary>
    public bool IsSrgb { get; set; }

    /// <summary>Gets or sets the surface this raster is: the index of its face or array slice, or its depth slice in a volume.</summary>
    public int Surface { get; set; }

    /// <summary>Gets or sets the mip level this raster is; 0 is full size.</summary>
    public int MipLevel { get; set; }

    /// <summary>Gets or sets the stored bytes of mips 1 and on, kept while the raster is unchanged, so a save rewrites them as they were.</summary>
    internal StoredMips? Mips { get; set; }

    /// <inheritdoc/>
    public override FormatDetails Copy()
    {
        return new DdsDetails
        {
            Mips = Mips,
            DxgiFormat = DxgiFormat,
            FormatName = FormatName,
            FourCC = FourCC,
            Dx10Header = Dx10Header,
            Dimension = Dimension,
            IsCube = IsCube,
            ArraySize = ArraySize,
            Depth = Depth,
            MipLevels = MipLevels,
            AlphaMode = AlphaMode,
            IsSrgb = IsSrgb,
            Surface = Surface,
            MipLevel = MipLevel,
        };
    }

    /// <inheritdoc/>
    protected override void PixelsChanged() => Mips = null;
}

/// <summary>The stored mips 1 and on of a surface a load read at mip 0, filled once its last row has decoded.</summary>
internal sealed class StoredMips
{
    /// <summary>Gets or sets the DXGI format they are stored in.</summary>
    public int Dxgi { get; set; }

    /// <summary>Gets or sets the size of mip 0.</summary>
    public Size Size { get; set; }

    /// <summary>Gets or sets the number of mips the bytes hold, after mip 0.</summary>
    public int Count { get; set; }

    /// <summary>Gets or sets the bytes, or <see langword="null"/> until the load has read them.</summary>
    public byte[]? Bytes { get; set; }
}

/// <summary>Options for reading DDS.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="dds-surfaces"/>
/// </example>
public sealed record DdsReaderOptions : ReaderOptions
{
    private readonly int? _surface;
    private readonly int? _mipLevel;

    /// <summary>
    /// Gets the surface Load returns, or <see langword="null"/> (the default) for the first: the index of a face or array slice
    /// (cube faces in the order +X, −X, +Y, −Y, +Z, −Z, each cube after the one before), or of a depth slice of a volume.
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
    /// <see cref="ReaderOptions.MinimumSize"/>. A volume's depth slice <see cref="Surface"/> at mip <c>m</c> is slice
    /// <c>Surface >> m</c> of that mip.
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
}
