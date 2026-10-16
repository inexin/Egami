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
    private static readonly uint Thmb = FourCC.Of("thmb");

    /// <summary>
    /// Reads image item <paramref name="itemId"/> (see <see cref="AvifDetails.Items"/>): an av01 item or a grid, converted with its
    /// own color into its natural pixel type, as Load converts the primary image; its own clean aperture cropped.
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
    internal static List<AvifItem> Items(AvifFile file)
    {
        HeifFile heif = file.Heif;
        HashSet<uint> alpha = [.. file.AlphaCells.Select(i => i.Id)];
        if (file.Alpha is HeifItem a)
        {
            alpha.Add(a.Id);
        }

        // Every input of a derived image (dimg), and every tmap item, is derived; alpha and gain map roles come first.
        HashSet<uint> derived = [.. heif.Items.SelectMany(i => i.ReferencesOf(AvifFile.Dimg)), .. heif.Items.Where(i => i.Type == AvifFile.Tmap).Select(i => i.Id)];
        derived.Remove(file.Primary.Id);
        uint gainMap = file.ToneMap is HeifItem tmap ? tmap.ReferencesOf(AvifFile.Dimg)[1] : 0;

        List<AvifItem> items = [];
        foreach (HeifItem item in heif.Items)
        {
            string? auxType = null;
            Size size = default;
            foreach ((HeifProperty property, _) in heif.PropertiesOf(item))
            {
                if (property.Type == AvifProperties.AuxC)
                {
                    auxType = AvifProperties.ReadAuxiliaryType(heif, property);
                }
                else if (property.Type == AvifProperties.Ispe)
                {
                    (int w, int h) = AvifProperties.ReadSpatialExtents(heif, property);
                    size = new Size(w, h);
                }
            }

            uint[] auxl = item.ReferencesOf(AvifFile.Auxl);
            AvifItemRole role = item.Id == file.Primary.Id ? AvifItemRole.Primary
                : alpha.Contains(item.Id) ? AvifItemRole.Alpha
                : item.Id == gainMap ? AvifItemRole.GainMap
                : item.ReferencesOf(Thmb).Length > 0 ? AvifItemRole.Thumbnail
                : auxl.Length > 0 && auxType is "urn:mpeg:mpegB:cicp:systems:auxiliary:depth" or "urn:mpeg:hevc:2015:auxid:2" ? AvifItemRole.Depth
                : auxl.Length > 0 ? AvifItemRole.Auxiliary
                : derived.Contains(item.Id) ? AvifItemRole.Derived
                : item.Type == AvifFile.Exif || item.Type == FourCC.Mime ? AvifItemRole.Metadata
                : AvifItemRole.Other;
            items.Add(new AvifItem
            {
                Id = item.Id,
                Type = FourCC.ToString(item.Type),
                Role = role,
                Size = size,
                AuxiliaryType = auxType,
                Hidden = item.Hidden,
                IsImage = item.Type == AvifFile.Av01 || item.Type == AvifFile.Grid,
            });
        }

        return items;
    }
}
