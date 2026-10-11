using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Jpeg;

/// <summary>
/// Reads JPEG files: baseline, extended and progressive, Huffman or arithmetic-coded, 8 or 12-bit, and lossless at 2 to 16 bits;
/// gray, YCbCr, RGB, CMYK and YCCK. Hierarchical files are refused.
/// </summary>
internal sealed partial class JpegReader : IFormatReader, Streaming.INativeScaling
{
    internal static JpegReader Instance { get; } = new();

    /// <summary>Gets the IDCT's largest reduction, 1/8.</summary>
    public int MaxNativeScale => 8;

    /// <summary>Returns whether <paramref name="header"/> starts like a JPEG: SOI then the first byte of another marker.</summary>
    internal static bool Matches(ReadOnlySpan<byte> header)
    {
        return header.Length >= 3 && header[0] == 0xFF && header[1] == JpegMarker.Soi && header[2] == 0xFF;
    }

    public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        JpegHeader header = JpegSegmentReader.ReadHeader(input, options);
        int scale = JpegSegmentReader.ScaleFor(header, options);
        return new RasterInfo
        {
            Format = JpegFormat.Info,
            Width = header.Width,
            Height = header.Height,
            Layout = JpegSegmentReader.Layout(header),
            FrameCount = 1,
            HasMetadata = header.HasMetadata,
            EstimatedBytes = JpegSegmentReader.EstimateBytes(header, scale),
            Orientation = header.Metadata.Exif?.Orientation ?? ImageOrientation.Normal,
            Access = RasterAccess.Streamed,
            WorkingBytes = WorkingBytes(header, options, scale),
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
        int count = header.Components.Length;
        if (header.Wide)
        {
            return count == 1 ? Decode<Gray16>(input, header, options) : count == 3 ? Decode<Rgb16>(input, header, options) : Decode<Cmyk16>(input, header, options);
        }

        return count == 1 ? Decode<Gray8>(input, header, options) : Decode<Rgb8>(input, header, options);
    }

    /// <summary>Decodes with libjpeg's default look, or replicated chroma when <see cref="JpegReaderOptions.FastUpsampling"/> says so.</summary>
    internal static Raster<TPixel> Decode<TPixel>(EgamiInput input, JpegHeader header, ReaderOptions options, bool fastUpsampling = false, bool forcePipeline = false)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        fastUpsampling |= options is JpegReaderOptions { FastUpsampling: true };
        int scale = JpegSegmentReader.ScaleFor(header, options);
        Raster<TPixel> raster = input.CreateRaster<TPixel>(JpegDecoder<TPixel>.ScaledSize(header.Width, scale), JpegDecoder<TPixel>.ScaledSize(header.Height, scale));
        try
        {
            header.Details.ScaleDenominator = scale;
            using (JpegDecoder<TPixel> decoder = new(header, options, raster, fastUpsampling, scale) { ForcePipeline = forcePipeline })
            {
                decoder.Decode(input);
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
        target.ShareIptc(source);
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
            target.WarningList.Insert(0, source.Warnings[i]);
        }
    }
}
