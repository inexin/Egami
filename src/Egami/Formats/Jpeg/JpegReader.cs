using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Jpeg;

/// <summary>
/// Reads JPEG files: baseline, extended and progressive, Huffman or arithmetic-coded, 8 or 12-bit, and lossless at 2 to 16 bits;
/// gray, YCbCr, RGB, CMYK and YCCK. Hierarchical files are refused.
/// </summary>
internal sealed partial class JpegReader : IFormatReader, Streaming.INativeScaling, IMultiImageReader
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
            Layout = options.Hdr is not null && MultiPictures(header) > 1 ? RgbaF16.Layout : JpegSegmentReader.Layout(header),
            FrameCount = 1,
            ImageCount = Math.Max(1, MultiPictures(header)),
            HasMetadata = header.HasMetadata,
            EstimatedBytes = JpegSegmentReader.EstimateBytes(header, scale),
            Orientation = header.Metadata.Exif?.Orientation ?? ImageOrientation.Normal,
            Access = RasterAccess.Streamed,
            WorkingBytes = WorkingBytes(header, options, scale),
        };
    }

    /// <summary>Lists the primary image and every other JPEG image of the file's MPF index (HDR Design question 13).</summary>
    public IReadOnlyList<ReaderOptions> Images(EgamiInput input, ReaderOptions options)
    {
        JpegHeader header = JpegSegmentReader.ReadHeader(input, options with { ReadMetadata = true });
        IReadOnlyList<MpfImage> images = header.Details.MultiPicture?.Images ?? [];
        List<ReaderOptions> list = [options];
        JpegReaderOptions jpeg = options as JpegReaderOptions ?? new JpegReaderOptions
        {
            Recovery = options.Recovery,
            ReadMetadata = options.ReadMetadata,
            ConvertColorTo = options.ConvertColorTo,
            Frames = options.Frames,
            MinimumSize = options.MinimumSize,
            Region = options.Region,
            Format = options.Format,
        };
        for (int i = 1; i < images.Count; i++)
        {
            if (images[i].DataFormat == 0 && images[i].Offset > 0)
            {
                list.Add(jpeg with { MultiPictureIndex = i });
            }
        }

        return list;
    }

    /// <summary>
    /// Reads the base image and its gain map, and returns the HDR rendition at the headroom asked for (HDR Design question 8);
    /// a file without a gain map gives its base image.
    /// </summary>
    private Raster ReadHdr(EgamiInput input, ReaderOptions options, HdrDecode hdr)
    {
        Raster sdr = Read(input, options);
        if (sdr.Metadata.GainMap is not Colors.GainMap map)
        {
            return sdr;
        }

        using (sdr)
        {
            Raster<RgbaF16> result = map.Apply(sdr, hdr.Headroom);
            RasterMetadata metadata = result.Metadata;
            metadata.FormatName = sdr.Metadata.FormatName;
            metadata.FormatDetails = sdr.Metadata.FormatDetails;
            metadata.Exif = sdr.Metadata.Exif;
            metadata.Xmp = sdr.Metadata.Xmp;
            metadata.Resolution = sdr.Metadata.Resolution;
            metadata.WarningList.Prepend(sdr.Metadata.WarningList);
            return result;
        }
    }

    /// <summary>Counts the primary and the other JPEG images of the MPF index, or returns 0 when there is none.</summary>
    private static int MultiPictures(JpegHeader header)
    {
        IReadOnlyList<MpfImage> images = header.Details.MultiPicture?.Images ?? [];
        int count = images.Count == 0 ? 0 : 1;
        for (int i = 1; i < images.Count; i++)
        {
            count += images[i].DataFormat == 0 && images[i].Offset > 0 ? 1 : 0;
        }

        return count;
    }

    public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (options.Hdr is not null)
        {
            Raster hdr = Read(input, options);
            if (hdr is Raster<TPixel> typed)
            {
                return typed;
            }

            using (hdr)
            {
                return hdr.As<TPixel>();
            }
        }

        return Decode<TPixel>(input, JpegSegmentReader.ReadHeader(input, options), options);
    }

    public Raster Read(EgamiInput input, ReaderOptions options)
    {
        if (options.Hdr is HdrDecode hdr && options is not JpegReaderOptions { MultiPictureIndex: > 0 })
        {
            return ReadHdr(input, options with { Hdr = null, ReadMetadata = true }, hdr);
        }

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
        if (header.Lossless)
        {
            return DecodeLossless<TPixel>(input, header, options, fastUpsampling);
        }

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
            ReadGainMap(input, header, options, raster.Metadata);
            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    /// <summary>Decodes a lossless file at full size: every scan into the planes, then each row converted into the raster.</summary>
    private static Raster<TPixel> DecodeLossless<TPixel>(EgamiInput input, JpegHeader header, ReaderOptions options, bool fastUpsampling)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Raster<TPixel> raster = input.CreateRaster<TPixel>(header.Width, header.Height);
        try
        {
            header.Details.ScaleDenominator = 1;
            using (JpegLosslessDecoder decoder = new(header, options, raster.Metadata.WarningList, fastUpsampling))
            {
                decoder.Decode(input);
                for (int y = 0; y < raster.Height; y++)
                {
                    if ((y & 63) == 0)
                    {
                        input.CancellationToken.ThrowIfCancellationRequested();
                    }

                    decoder.ReadRow(y, 0, raster.Row(y));
                }
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

    /// <summary>Reads the gain map of an Ultra HDR, ISO 21496-1 or Apple file into the metadata, when one is there.</summary>
    private static void ReadGainMap(EgamiInput input, JpegHeader header, ReaderOptions options, RasterMetadata target)
    {
        if (options.ReadMetadata && options is not JpegReaderOptions { MultiPictureIndex: > 0 } && header.Details.MultiPicture is { Images.Count: > 1 })
        {
            JpegGainMap.Read(input, header, target);
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
        target.WarningList.Prepend(source.WarningList);
    }
}
