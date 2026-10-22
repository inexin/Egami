using Egami.Colors;
using Egami.Formats.Heif;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Heic;

/// <summary>What an item of a HEIC file is to the image Load gives.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Heic.cs" region="items"/>
/// </example>
public enum HeicItemRole
{
    /// <summary>The primary image, which Load gives.</summary>
    Primary,

    /// <summary>A thumbnail of the primary image (a thmb reference).</summary>
    Thumbnail,

    /// <summary>The primary image's alpha, or part of it.</summary>
    Alpha,

    /// <summary>A depth map of the primary image (an auxiliary image of a depth type).</summary>
    Depth,

    /// <summary>Another auxiliary image of the primary image, such as a matte; see <see cref="HeicItem.AuxiliaryType"/>.</summary>
    Auxiliary,

    /// <summary>A gain map image: of a tone-mapped derived image (tmap), or Apple's HDRGainMap auxiliary image.</summary>
    GainMap,

    /// <summary>An image another image is made from or derived from it: a grid cell, an overlay layer, or the tmap item itself.</summary>
    Derived,

    /// <summary>Metadata: an Exif or XMP item.</summary>
    Metadata,

    /// <summary>Anything else.</summary>
    Other,
}

/// <summary>An item of a HEIC file, as <see cref="HeicDetails.Items"/> lists them.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Heic.cs" region="items"/>
/// </example>
public sealed record HeicItem
{
    /// <summary>Gets the item_ID, which Egami.Heic's <c>HeicImage.ReadItem</c> takes.</summary>
    public uint Id { get; init; }

    /// <summary>Gets the item type: hvc1, grid, iovl, tmap, Exif, mime, ...</summary>
    public string Type { get; init; } = "";

    /// <summary>Gets what the item is to the primary image.</summary>
    public HeicItemRole Role { get; init; }

    /// <summary>Gets the size its ispe property gives, or 0 × 0 when it has none.</summary>
    public Size Size { get; init; }

    /// <summary>Gets the auxiliary type URN of an auxiliary image (auxC), such as Apple's mattes, or <see langword="null"/>.</summary>
    public string? AuxiliaryType { get; init; }

    /// <summary>Gets whether the item is hidden (not meant to be shown on its own).</summary>
    public bool Hidden { get; init; }

    /// <summary>Gets whether Egami.Heic's <c>HeicImage.ReadItem</c> can decode it: an hvc1 or grid image item.</summary>
    public bool IsImage { get; init; }
}

/// <summary>What a HEIC file holds besides its pixels: brands, HEVC profile, color, grid and items.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Heic.cs" region="items"/>
/// </example>
public sealed class HeicDetails : FormatDetails
{
    /// <summary>Gets or sets the ftyp box's major brand, such as heic or heix.</summary>
    public string MajorBrand { get; set; } = "heic";

    /// <summary>Gets or sets the ftyp box's compatible brands, such as mif1, heic and miaf.</summary>
    public IReadOnlyList<string> CompatibleBrands { get; set; } = [];

    /// <summary>Gets or sets the HEVC profile (general_profile_idc): 1 Main, 2 Main 10, 3 Main Still Picture, 4 range extensions.</summary>
    public int Profile { get; set; }

    /// <summary>Gets or sets the HEVC level (general_level_idc): 30 times the level number, so 93 is level 3.1.</summary>
    public int Level { get; set; }

    /// <summary>Gets or sets the HEVC tier: 0 (Main) or 1 (High).</summary>
    public int Tier { get; set; }

    /// <summary>Gets or sets the bits a sample has: 8, 10 or 12.</summary>
    public int BitDepth { get; set; } = 8;

    /// <summary>Gets or sets the chroma format: 4:0:0 (monochrome), 4:2:0, 4:2:2 or 4:4:4.</summary>
    public ChromaFormat Chroma { get; set; } = ChromaFormat.Yuv420;

    /// <summary>
    /// Gets or sets the color code points (ITU-T H.273) and range: the nclx colr property's when there is one, otherwise the HEVC
    /// stream's own (the SPS's VUI).
    /// </summary>
    public ColorCodePoints CodePoints { get; set; } = new(2, 2, 2, false);

    /// <summary>Gets or sets whether the image has an alpha item.</summary>
    public bool HasAlpha { get; set; }

    /// <summary>Gets or sets whether the color is premultiplied by the alpha (a prem item reference). Egami loads straight alpha.</summary>
    public bool AlphaPremultiplied { get; set; }

    /// <summary>Gets or sets the rows of a grid image, or 1.</summary>
    public int GridRows { get; set; } = 1;

    /// <summary>Gets or sets the columns of a grid image, or 1.</summary>
    public int GridColumns { get; set; } = 1;

    /// <summary>Gets or sets whether the file holds an image sequence (the msf1 brand).</summary>
    public bool IsSequence { get; set; }

    /// <summary>
    /// Gets or sets every item of the file with what it is to the primary image; read an image item with Egami.Heic's
    /// <c>HeicImage.ReadItem</c>.
    /// </summary>
    public IReadOnlyList<HeicItem> Items { get; set; } = [];

    /// <inheritdoc/>
    public override FormatDetails Copy()
    {
        return new HeicDetails
        {
            MajorBrand = MajorBrand,
            CompatibleBrands = CompatibleBrands,
            Profile = Profile,
            Level = Level,
            Tier = Tier,
            BitDepth = BitDepth,
            Chroma = Chroma,
            CodePoints = CodePoints,
            HasAlpha = HasAlpha,
            AlphaPremultiplied = AlphaPremultiplied,
            GridRows = GridRows,
            GridColumns = GridColumns,
            IsSequence = IsSequence,
            Items = Items,
        };
    }

    /// <summary>Gets the details of a file whose structure has been read, with the color the decode found.</summary>
    internal static HeicDetails From(HeicFile file, ColorCodePoints codePoints)
    {
        return new HeicDetails
        {
            MajorBrand = IsoTypes.ToString(file.Heif.MajorBrand),
            CompatibleBrands = [.. file.Heif.CompatibleBrands.Select(IsoTypes.ToString)],
            Profile = file.Config.Profile,
            Level = file.Config.Level,
            Tier = file.Config.Tier,
            BitDepth = file.BitDepth,
            Chroma = file.Chroma,
            CodePoints = codePoints,
            HasAlpha = file.Alpha is not null,
            AlphaPremultiplied = file.AlphaPremultiplied,
            GridRows = file.GridSize.Rows,
            GridColumns = file.GridSize.Columns,
            IsSequence = file.Heif.HasBrand(IsoTypes.Of("msf1")),
            Items = [.. file.ItemRoles().Select(r => new HeicItem
            {
                Id = r.Item.Id,
                Type = IsoTypes.ToString(r.Item.Type),
                Role = (HeicItemRole)r.Role,
                Size = r.Size,
                AuxiliaryType = r.AuxiliaryType,
                Hidden = r.Item.Hidden,
                IsImage = r.Item.Type == HeicFile.Hvc1 || r.Item.Type == HeifImage.Grid,
            })],
        };
    }
}
