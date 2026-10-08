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
        return Decode<TPixel>(input, JpegSegmentReader.ReadHeader(input, options), options);
    }

    public Raster Read(EgamiInput input, ReaderOptions options)
    {
        JpegHeader header = JpegSegmentReader.ReadHeader(input, options);
        return header.Components.Length == 1 ? Decode<Gray8>(input, header, options) : Decode<Rgb8>(input, header, options);
    }

    /// <summary>Decodes with libjpeg's default look, or replicated chroma when <see cref="JpegReaderOptions.FastUpsampling"/> says so.</summary>
    internal static Raster<TPixel> Decode<TPixel>(EgamiInput input, JpegHeader header, ReaderOptions options, bool fastUpsampling = false)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        fastUpsampling |= options is JpegReaderOptions { FastUpsampling: true };
        if (header.IsProgressive)
        {
            throw new UnsupportedFormatException("Decoding progressive JPEG is not available yet.");
        }

        Raster<TPixel> raster = input.CreateRaster<TPixel>(header.Width, header.Height);
        try
        {
            using (JpegDecoder<TPixel> decoder = new(header, options, raster, fastUpsampling))
            {
                decoder.DecodeScan(input, header.FirstScan!);
            }

            CopyMetadata(header, raster.Metadata);
            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    private static void CopyMetadata(JpegHeader header, RasterMetadata target)
    {
        RasterMetadata source = header.Metadata;
        target.FormatName = JpegFormat.Info.Name;
        target.FormatDetails = header.Details;
        target.Resolution = source.Resolution;
        target.Exif = source.Exif;
        target.Xmp = source.Xmp;
        target.IccProfile = source.IccProfile;
        target.ColorSpace = source.ColorSpace == ColorSpaceHint.Unknown ? ColorSpaceHint.Srgb : source.ColorSpace;
        foreach (TextEntry entry in source.Text)
        {
            target.Text.Add(entry);
        }

        // Header warnings come first; the decoder added its own to the target already.
        for (int i = source.Warnings.Count - 1; i >= 0; i--)
        {
            target.Warnings.Insert(0, source.Warnings[i]);
        }
    }
}
