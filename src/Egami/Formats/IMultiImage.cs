namespace Egami.Formats;

/// <summary>
/// An optional interface for a format's reader whose files hold several images (TIFF pages, icon entries, DDS surfaces).
/// <see cref="Raster.LoadAll(Stream, EgamiContext?, ReaderOptions?)"/> asks it for one options value per image and loads each as
/// Load would, so every Load feature applies to each image. The options a reader returns name the image in a way its own
/// <see cref="IFormatReader"/> understands, usually a <see cref="ReaderOptions"/> subclass of the format's own.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Extending.cs" region="multi-image-format"/>
/// </example>
public interface IMultiImageReader
{
    /// <summary>Returns, from the file's structure alone, the options that load each image, in the file's order.</summary>
    /// <param name="input">The data, positioned at the start of the file.</param>
    /// <param name="options">The caller's options.</param>
    /// <returns>One options value per image; Load with each gives that image.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Extending.cs" region="multi-image-format"/>
    /// </example>
    IReadOnlyList<ReaderOptions> Images(EgamiInput input, ReaderOptions options);
}

/// <summary>A writer of a format that holds several images, for <see cref="Raster.SaveAll(IEnumerable{Raster}, Stream, FormatInfo, WriterOptions?)"/>.</summary>
internal interface IMultiImageWriter
{
    /// <summary>Writes every raster, in order, to one file.</summary>
    void WriteAll(IReadOnlyList<Raster> rasters, Stream output, WriterOptions options);
}
