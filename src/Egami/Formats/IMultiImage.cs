namespace Egami.Formats;

/// <summary>
/// A reader of a format that holds several images (TIFF pages, icon entries). <see cref="Raster.LoadAll(Stream, EgamiContext?, ReaderOptions?)"/>
/// asks it for one options value per image and loads each as Load would, so every Load feature applies to each image.
/// </summary>
internal interface IMultiImageReader
{
    /// <summary>Returns, from the file's structure alone, the options that load each image, in the file's order.</summary>
    /// <param name="input">The data, positioned at the start of the file.</param>
    /// <param name="options">The caller's options.</param>
    IReadOnlyList<ReaderOptions> Images(EgamiInput input, ReaderOptions options);
}

/// <summary>A writer of a format that holds several images, for <see cref="Raster.SaveAll(IEnumerable{Raster}, Stream, FormatInfo, WriterOptions?)"/>.</summary>
internal interface IMultiImageWriter
{
    /// <summary>Writes every raster, in order, to one file.</summary>
    void WriteAll(IReadOnlyList<Raster> rasters, Stream output, WriterOptions options);
}
