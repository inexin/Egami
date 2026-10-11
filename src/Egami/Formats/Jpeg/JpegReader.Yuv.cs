using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Jpeg;

/// <content>Decoding into planes, for <see cref="YuvRaster.Load(Stream, EgamiContext?, ReaderOptions?)"/>.</content>
internal sealed partial class JpegReader : IYuvFormatReader
{
    public YuvRaster ReadYuv(EgamiInput input, ReaderOptions options)
    {
        JpegHeader header = JpegSegmentReader.ReadHeader(input, options);
        ChromaFormat format = PlaneFormat(header) ?? throw new NotSupportedException(header.Wide || header.Lossless
            ? $"This JPEG is {(header.Lossless ? "lossless" : $"{header.Precision}-bit")}, which has no 8-bit YUV planes; load it with Raster.Load."
            : $"This JPEG is {header.ColorSpace}, which has no YUV planes; load it with Raster.Load to convert it.");
        int scale = JpegSegmentReader.ScaleFor(header, options);
        int width = JpegDecoder<Gray8>.ScaledSize(header.Width, scale), height = JpegDecoder<Gray8>.ScaledSize(header.Height, scale);
        if (width < 1 || height < 1)
        {
            throw new CorruptDataException(JpegFormat.Info.Name, -1, $"The image size {width}×{height} is invalid.");
        }

        YuvRaster<byte> planes = YuvRaster<byte>.Allocate(width, height, format, 8, YuvColor.Jpeg, alpha: false, new RasterMetadata(), input.Context, ClearMode.None);
        try
        {
            header.Details.ScaleDenominator = scale;
            using (JpegDecoder<Gray8> decoder = new(header, options, planes, scale))
            {
                decoder.Decode(input);
            }

            CopyMetadata(header, planes.Metadata);
            return planes;
        }
        catch
        {
            planes.Dispose();
            throw;
        }
    }

    public (RasterInfo Info, ChromaFormat? Format, int BitDepth, YuvColor Color) ProbeYuv(EgamiInput input, ReaderOptions options)
    {
        JpegHeader header = JpegSegmentReader.ReadHeader(input, options);
        int scale = JpegSegmentReader.ScaleFor(header, options);
        RasterInfo info = new()
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
        return (info, PlaneFormat(header), 8, YuvColor.Jpeg);
    }

    /// <summary>
    /// Returns the planes a file decodes to: gray is 4:0:0; YCbCr with luma at the highest sampling and both chroma components
    /// halved both ways, halved across, or not at all is 4:2:0, 4:2:2 or 4:4:4; any other YCbCr sampling (4:1:1, 4:4:0, mixes) is
    /// replicated up to 4:4:4. Other color spaces have no YUV planes.
    /// </summary>
    private static ChromaFormat? PlaneFormat(JpegHeader header)
    {
        JpegColorSpace space = header.ColorSpace;
        if (header.Wide || header.Lossless)
        {
            // Deeper samples have no 8-bit planes, and lossless files have no DCT-domain planes to hand over.
            return null;
        }

        if (space == JpegColorSpace.Gray)
        {
            return ChromaFormat.Yuv400;
        }

        if (space != JpegColorSpace.YCbCr)
        {
            return null;
        }

        JpegFrameComponent y = header.Components[0], u = header.Components[1], v = header.Components[2];
        int maxH = header.MaxH, maxV = header.MaxV;
        if (y.H != maxH || y.V != maxV || u.H != v.H || u.V != v.V || maxH % u.H != 0 || maxV % u.V != 0)
        {
            return ChromaFormat.Yuv444;
        }

        return (maxH / u.H, maxV / u.V) switch
        {
            (2, 2) => ChromaFormat.Yuv420,
            (2, 1) => ChromaFormat.Yuv422,
            _ => ChromaFormat.Yuv444,
        };
    }
}
