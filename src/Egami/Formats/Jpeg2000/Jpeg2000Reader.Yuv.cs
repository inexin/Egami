using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Jpeg2000;

/// <content>Decoding into planes, for <see cref="YuvRaster.Load(Stream, EgamiContext?, ReaderOptions?)"/>, and sYCC through them.</content>
internal sealed partial class Jpeg2000Reader : IYuvFormatReader
{
    /// <summary>JPEG 2000's YCC: BT.601 full range, chroma co-sited with the top-left luma sample (T.800 B.2).</summary>
    internal static YuvColor Sycc => new(YuvMatrix.Bt601, YuvRange.Full, ChromaSiting.TopLeft);

    public YuvRaster ReadYuv(EgamiInput input, ReaderOptions options)
    {
        return Run(input, options, headerOnly: false, (file, stream) =>
        {
            J2kPixels pixels = J2kPixels.Create(file, stream.Components, Mode(options));
            (ChromaFormat format, int depth) = PlaneFormat(stream, pixels) ?? throw new NotSupportedException(
                "This JPEG 2000 image is not sYCC or gray at 8, 10, 12 or 16 bits with 4:2:0, 4:2:2 or 4:4:4 sampling, so it has no YUV planes; load it with Raster.Load.");
            J2kImage image = Decode(input, stream, options, pixels.Needed(stream.Components.Length, stream.MainCod.Mct == 1));
            YuvRaster planes;
            try
            {
                planes = depth == 8 ? Planes<byte>(image, format, depth, input.Context) : Planes<ushort>(image, format, depth, input.Context);
            }
            finally
            {
                image.Release();
            }

            try
            {
                Describe(planes.Metadata, file, stream, options);
                return planes;
            }
            catch
            {
                planes.Dispose();
                throw;
            }
        });
    }

    public (RasterInfo Info, ChromaFormat? Format, int BitDepth, YuvColor Color) ProbeYuv(EgamiInput input, ReaderOptions options)
    {
        return Run(input, options, headerOnly: true, (file, stream) =>
        {
            J2kPixels pixels = J2kPixels.Create(file, stream.Components, Mode(options));
            (ChromaFormat Format, int Depth)? planes = PlaneFormat(stream, pixels);
            return (Info(file, stream, options), planes?.Format, planes?.Depth ?? 8, Sycc);
        });
    }

    /// <summary>
    /// Returns the planes an image has: gray is 4:0:0; plain sYCC with full-size luma and both chroma components halved both
    /// ways, halved across or not at all is 4:2:0, 4:2:2 or 4:4:4. Samples are 8, 10, 12 or 16 bits, and the image starts at the
    /// reference grid's origin.
    /// </summary>
    internal static (ChromaFormat Format, int Depth)? PlaneFormat(J2kCodestream stream, J2kPixels pixels)
    {
        J2kComponent[] c = stream.Components;
        if (stream.OffsetX != 0 || stream.OffsetY != 0 || c[0].Precision is not (8 or 10 or 12 or 16) || c[0].Signed || c[0].Dx != 1 || c[0].Dy != 1)
        {
            return null;
        }

        if (pixels.Model == J2kModel.Gray && c.Length == 1)
        {
            return (ChromaFormat.Yuv400, c[0].Precision);
        }

        if (!pixels.PlainSycc || c[1] != c[2] || c[1].Precision != c[0].Precision)
        {
            return null;
        }

        return (c[1].Dx, c[1].Dy) switch
        {
            (2, 2) => (ChromaFormat.Yuv420, c[0].Precision),
            (2, 1) => (ChromaFormat.Yuv422, c[0].Precision),
            (1, 1) => (ChromaFormat.Yuv444, c[0].Precision),
            _ => null,
        };
    }

    /// <summary>Copies decoded components into YUV planes.</summary>
    private static YuvRaster<TSample> Planes<TSample>(J2kImage image, ChromaFormat format, int depth, EgamiContext context)
        where TSample : unmanaged
    {
        YuvRaster<TSample> planes = YuvRaster<TSample>.Allocate(image.Widths[0], image.Heights[0], format, depth, Sycc, alpha: false, new RasterMetadata(), context, ClearMode.None);
        try
        {
            Copy(image, 0, planes.Y);
            if (planes.U is { } u && planes.V is { } v)
            {
                Copy(image, 1, u);
                Copy(image, 2, v);
            }

            return planes;
        }
        catch
        {
            planes.Dispose();
            throw;
        }
    }

    private static void Copy<TSample>(J2kImage image, int c, YuvPlane<TSample> plane)
        where TSample : unmanaged
    {
        int width = Math.Min(plane.Width, image.Widths[c]);
        int height = Math.Min(plane.Height, image.Heights[c]);
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<int> source = image.Planes[c].AsSpan(y * image.Widths[c], width);
            Span<TSample> target = plane.Row(y);
            if (typeof(TSample) == typeof(byte))
            {
                Span<byte> bytes = System.Runtime.InteropServices.MemoryMarshal.Cast<TSample, byte>(target);
                for (int x = 0; x < width; x++)
                {
                    bytes[x] = (byte)source[x];
                }
            }
            else
            {
                Span<ushort> words = System.Runtime.InteropServices.MemoryMarshal.Cast<TSample, ushort>(target);
                for (int x = 0; x < width; x++)
                {
                    words[x] = (ushort)source[x];
                }
            }
        }
    }
}
