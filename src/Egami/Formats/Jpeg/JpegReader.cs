using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Jpeg;

/// <summary>Reads JPEG files: baseline and progressive Huffman-coded, 8-bit, gray, YCbCr, RGB, CMYK and YCCK.</summary>
internal sealed class JpegReader : IFormatReader
{
    internal static JpegReader Instance { get; } = new();

    /// <summary>Returns whether <paramref name="header"/> starts like a JPEG: SOI then the first byte of another marker.</summary>
    internal static bool Matches(ReadOnlySpan<byte> header)
    {
        return header.Length >= 3 && header[0] == 0xFF && header[1] == JpegMarker.Soi && header[2] == 0xFF;
    }

    public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        JpegHeader header = JpegSegmentReader.ReadHeader(input, options);
        int scale = JpegSegmentReader.ScaleFor(header, options.TargetSize);
        return new RasterInfo
        {
            Format = JpegFormat.Info,
            Width = header.Width,
            Height = header.Height,
            Layout = JpegSegmentReader.Layout(header),
            FrameCount = 1,
            HasMetadata = header.HasMetadata,
            EstimatedBytes = JpegSegmentReader.EstimateBytes(header, scale),
            Orientation = ExifOrientation.Read(header.Metadata.Exif.Span),
        };
    }

    public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        JpegSegmentReader.ReadHeader(input, options);
        throw NotYet();
    }

    public Raster Read(EgamiInput input, ReaderOptions options)
    {
        JpegSegmentReader.ReadHeader(input, options);
        throw NotYet();
    }

    /// <summary>Pixel decoding is built in the next tasks of Phase 1.3; until then only Probe reads JPEG.</summary>
    private static UnsupportedFormatException NotYet()
    {
        return new UnsupportedFormatException("Decoding JPEG pixels is not available yet; Raster.Probe reads JPEG headers.");
    }
}
