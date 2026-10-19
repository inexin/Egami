using Egami.Formats;
using Egami.Formats.Heic;

namespace Egami.Heic;

/// <summary>
/// Reads the images a HEIC file holds besides the one Load gives: thumbnails, depth maps, gain maps and other auxiliary images,
/// such as an iPhone's portrait and sky mattes. <see cref="HeicDetails.Items"/> lists them.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Heic.cs" region="items"/>
/// </example>
public static class HeicImage
{
    /// <summary>
    /// Reads image item <paramref name="itemId"/> (see <see cref="HeicDetails.Items"/>): an hvc1 item or a grid, converted with its
    /// own color into its natural pixel type, as Load converts the primary image; its own clean aperture is cropped, and its own
    /// rotation and mirroring are given as the orientation (<c>AutoOrient</c> applies them).
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="itemId">The item's id.</param>
    /// <param name="options">The general options; the frame range is ignored.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>. Egami.Heic reads the item whether or not the context registers it.</param>
    /// <returns>The image. The caller must dispose it.</returns>
    /// <exception cref="ArgumentException">The file has no such item, or it is not an image item.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules, including data of another format.</exception>
    /// <exception cref="UnsupportedFormatException">The item uses something Egami.Heic does not decode.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Heic.cs" region="items"/>
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
    /// <param name="options">The general options; the frame range is ignored.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>. Egami.Heic reads the item whether or not the context registers it.</param>
    /// <returns>The image. The caller must dispose it.</returns>
    /// <exception cref="ArgumentException">The file has no such item, or it is not an image item.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules, including data of another format.</exception>
    /// <exception cref="UnsupportedFormatException">The item uses something Egami.Heic does not decode.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Heic.cs" region="items"/>
    /// </example>
    public static Raster ReadItem(Stream stream, uint itemId, ReaderOptions? options = null, EgamiContext? context = null)
    {
        ReaderOptions read = (options ?? ReaderOptions.Default) with { Format = HeicFormat.Info };
        HeicReader reader = (HeicReader)HeicFormat.Info.Reader!;
        return FormatPipeline.Run(stream, context ?? EgamiContext.Default, read, (_, input) => reader.ReadItem(input, read, itemId), CancellationToken.None);
    }
}
