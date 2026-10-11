using Egami.Pixels;

namespace Egami.Formats;

/// <summary>A reader that can decode a format's own YUV planes, for <see cref="YuvRaster.Load(Stream, EgamiContext?, ReaderOptions?)"/>.</summary>
internal interface IYuvFormatReader
{
    /// <summary>Decodes the planes, or throws <see cref="NotSupportedException"/> when this file has none.</summary>
    YuvRaster ReadYuv(EgamiInput input, ReaderOptions options);

    /// <summary>Reads the header and says which planes <see cref="ReadYuv"/> would give, if any.</summary>
    (RasterInfo Info, ChromaFormat? Format, int BitDepth, YuvColor Color) ProbeYuv(EgamiInput input, ReaderOptions options);
}
