using Egami.Formats.Heif;

namespace Egami.Formats.Avif;

/// <summary>What an item of an AVIF file is to the image Load gives (design question 13).</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Avif.cs" region="items"/>
/// </example>
public enum AvifItemRole
{
    /// <summary>The primary image, which Load gives.</summary>
    Primary,

    /// <summary>A thumbnail of the primary image (a thmb reference).</summary>
    Thumbnail,

    /// <summary>The primary image's alpha, or part of it.</summary>
    Alpha,

    /// <summary>A depth map of the primary image (an auxiliary image of the depth type).</summary>
    Depth,

    /// <summary>Another auxiliary image of the primary image, such as a matte; see <see cref="AvifItem.AuxiliaryType"/>.</summary>
    Auxiliary,

    /// <summary>The gain map image of a tone-mapped derived image (tmap).</summary>
    GainMap,

    /// <summary>An image another image is made from or derived from it: a grid cell, an overlay layer, or the tmap item itself.</summary>
    Derived,

    /// <summary>Metadata: an Exif or XMP item.</summary>
    Metadata,

    /// <summary>Anything else.</summary>
    Other,
}

/// <summary>An item of an AVIF file, as <see cref="AvifDetails.Items"/> lists them.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Avif.cs" region="items"/>
/// </example>
public sealed record AvifItem
{
    /// <summary>Gets the item_ID, which <see cref="AvifImage.ReadItem(string, uint, ReaderOptions?, EgamiContext?)"/> takes.</summary>
    public uint Id { get; init; }

    /// <summary>Gets the item type: av01, grid, iovl, tmap, Exif, mime, ...</summary>
    public string Type { get; init; } = "";

    /// <summary>Gets what the item is to the primary image.</summary>
    public AvifItemRole Role { get; init; }

    /// <summary>Gets the size its ispe property gives, or 0 × 0 when it has none.</summary>
    public Size Size { get; init; }

    /// <summary>Gets the auxiliary type URN of an auxiliary image (auxC), or <see langword="null"/>.</summary>
    public string? AuxiliaryType { get; init; }

    /// <summary>Gets whether the item is hidden (not meant to be shown on its own).</summary>
    public bool Hidden { get; init; }

    /// <summary>Gets whether <see cref="AvifImage.ReadItem(string, uint, ReaderOptions?, EgamiContext?)"/> can decode it: an av01 or grid image item.</summary>
    public bool IsImage { get; init; }
}

/// <summary>Reads the images an AVIF file holds besides the one Load gives: thumbnails, depth maps and other auxiliary images.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Avif.cs" region="items"/>
/// </example>
public static class AvifImage
{
    /// <summary>
    /// Reads image item <paramref name="itemId"/> (see <see cref="AvifDetails.Items"/>): an av01 item or a grid, converted with its
    /// own color into its natural pixel type, as Load converts the primary image; its own clean aperture cropped, and its own
    /// rotation and mirroring (irot, imir) given as the orientation, which <c>AutoOrient</c> applies.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="itemId">The item's id.</param>
    /// <param name="options">The general options (<see cref="AvifReaderOptions.FilmGrain"/> applies); the frame range is ignored.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <returns>The image. The caller must dispose it.</returns>
    /// <exception cref="ArgumentException">The file has no such item, or it is not an image item.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules, including data of another format.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Avif.cs" region="items"/>
    /// </example>
    public static Raster ReadItem(string path, uint itemId, ReaderOptions? options = null, EgamiContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        using FileStream stream = File.OpenRead(path);
        return ReadItem(stream, itemId, options, context);
    }

    /// <summary>Reads image item <paramref name="itemId"/> from a stream; see <see cref="ReadItem(string, uint, ReaderOptions?, EgamiContext?)"/>.</summary>
    /// <param name="stream">The file's data, from its start.</param>
    /// <param name="itemId">The item's id.</param>
    /// <param name="options">The general options (<see cref="AvifReaderOptions.FilmGrain"/> applies); the frame range is ignored.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <returns>The image. The caller must dispose it.</returns>
    /// <exception cref="ArgumentException">The file has no such item, or it is not an image item.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules, including data of another format.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Avif.cs" region="items"/>
    /// </example>
    public static Raster ReadItem(Stream stream, uint itemId, ReaderOptions? options = null, EgamiContext? context = null)
    {
        ReaderOptions read = (options ?? ReaderOptions.Default) with { Format = AvifFormat.Info };
        return FormatPipeline.Run(stream, context ?? EgamiContext.Default, read, (_, input) => AvifReader.ReadItem(input, read, itemId), CancellationToken.None);
    }

    /// <summary>Lists a file's items with their roles.</summary>
    internal static List<AvifItem> Items(AvifFile file) => [.. file.ItemRoles().Select(r => new AvifItem
    {
        Id = r.Item.Id,
        Type = FourCC.ToString(r.Item.Type),
        Role = (AvifItemRole)r.Role,
        Size = r.Size,
        AuxiliaryType = r.AuxiliaryType,
        Hidden = r.Item.Hidden,
        IsImage = r.Item.Type == AvifFile.Av01 || r.Item.Type == AvifFile.Grid,
    })];
}
