using System.Buffers.Binary;

using Egami.Pixels;

namespace Egami.Video.Codecs.Raw;

/// <summary>
/// How uncompressed frames lay out their samples. Named by FourCC where one exists (Matroska ColourSpace, AVI biCompression):
/// I420, YV12, Y42B, 444P, Y800, NV12, NV21, YUY2, UYVY and v210; plain planar layouts at any bit depth with optional alpha
/// (the YUV4MPEG2 layout, little-endian 16-bit samples above 8 bits) have none. BGR24 is AVI's bottom-up BI_RGB.
/// </summary>
internal sealed record RawLayout(string? FourCC, ChromaFormat Chroma, int BitDepth, bool Alpha, RawLayout.Kind Shape)
{
    public enum Kind
    {
        Planar,
        PlanarSwapped,
        Packed,
        V210,
        BottomUpBgr24,
        TopDownBgr24,
    }

    public static RawLayout? FromFourCC(string fourCC) => fourCC switch
    {
        "I420" or "IYUV" => new(fourCC, ChromaFormat.Yuv420, 8, false, Kind.Planar),
        "YV12" => new(fourCC, ChromaFormat.Yuv420, 8, false, Kind.PlanarSwapped),
        "Y42B" => new(fourCC, ChromaFormat.Yuv422, 8, false, Kind.Planar),
        "444P" => new(fourCC, ChromaFormat.Yuv444, 8, false, Kind.Planar),
        "Y800" or "GREY" => new(fourCC, ChromaFormat.Yuv400, 8, false, Kind.Planar),
        "NV12" or "NV21" => new(fourCC, ChromaFormat.Yuv420, 8, false, Kind.Packed),
        "YUY2" or "YUYV" or "UYVY" => new(fourCC, ChromaFormat.Yuv422, 8, false, Kind.Packed),
        "v210" => new(fourCC, ChromaFormat.Yuv422, 10, false, Kind.V210),
        _ => null,
    };

    /// <summary>A plain planar layout, with the FourCC that names it when there is one.</summary>
    public static RawLayout Planar(ChromaFormat chroma, int bitDepth, bool alpha)
    {
        string? fourCC = (chroma, bitDepth, alpha) switch
        {
            (ChromaFormat.Yuv420, 8, false) => "I420",
            (ChromaFormat.Yuv422, 8, false) => "Y42B",
            (ChromaFormat.Yuv444, 8, false) => "444P",
            (ChromaFormat.Yuv400, 8, false) => "Y800",
            _ => null,
        };
        return new(fourCC, chroma, bitDepth, alpha, Kind.Planar);
    }

    /// <summary>The layout a track describes, or null when it is not uncompressed video this codec knows.</summary>
    public static RawLayout? ForTrack(VideoTrackInfo track)
    {
        if (track.CodecId == CodecIds.Uncompressed)
        {
            return track.FourCC is { } fourCC
                ? FromFourCC(fourCC)
                : track.ChromaFormat is { } chroma && track.BitDepth is >= 8 and <= 12
                    ? Planar(chroma, track.BitDepth, track.HasAlpha)
                    : null;
        }

        if (track.CodecId == CodecIds.VfwFourCC && track.CodecPrivate.Length >= 20)
        {
            ReadOnlySpan<byte> header = track.CodecPrivate.Span;
            uint compression = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
            int bitCount = BinaryPrimitives.ReadUInt16LittleEndian(header[14..]);
            if (compression == 0 && bitCount == 24)
            {
                // A positive height is bottom-up, a negative one top-down.
                return new(null, ChromaFormat.Yuv444, 8, false, BinaryPrimitives.ReadInt32LittleEndian(header[8..]) > 0 ? Kind.BottomUpBgr24 : Kind.TopDownBgr24);
            }
        }

        return null;
    }

    public int BytesPerSample => BitDepth > 8 ? 2 : 1;

    /// <summary>The bytes one frame takes.</summary>
    public long FrameSize(int width, int height)
    {
        long w = width, h = height;
        return Shape switch
        {
            Kind.V210 => (w + 47) / 48 * 128 * h,
            Kind.BottomUpBgr24 or Kind.TopDownBgr24 => ((w * 3) + 3) / 4 * 4 * h,
            Kind.Packed => Chroma == ChromaFormat.Yuv420 ? (w * h) + (((w + 1) / 2) * 2 * ((h + 1) / 2)) : ((w + 1) / 2) * 4 * h,
            _ => PlaneSamples(w, h) * BytesPerSample,
        };
    }

    private long PlaneSamples(long w, long h)
    {
        long luma = w * h;
        long chroma = Chroma switch
        {
            ChromaFormat.Yuv420 => ((w + 1) / 2) * ((h + 1) / 2),
            ChromaFormat.Yuv422 => ((w + 1) / 2) * h,
            ChromaFormat.Yuv444 => w * h,
            _ => 0,
        };
        return luma + (2 * chroma) + (Alpha ? luma : 0);
    }

    /// <summary>Makes planes for a frame of this layout.</summary>
    public YuvRaster Create(int width, int height, YuvColor color, EgamiContext context) =>
        Shape is Kind.BottomUpBgr24 or Kind.TopDownBgr24
            ? YuvRaster.Create(width, height, ChromaFormat.Yuv444, new YuvColor(YuvMatrix.Identity, YuvRange.Full, color.Siting), context: context)
            : BitDepth == 8
                ? YuvRaster.Create(width, height, Chroma, color, Alpha, context)
                : YuvRaster.Create(width, height, Chroma, BitDepth, color, Alpha, context);

    /// <summary>Reads a frame's samples into planes made by <see cref="Create"/>.</summary>
    public void Read(ReadOnlySpan<byte> data, YuvRaster planes)
    {
        switch (Shape)
        {
            case Kind.Packed:
                planes.CopyFromPacked(PackedFormat(), data);
                break;
            case Kind.V210:
                ReadV210(data, (YuvRaster<ushort>)planes);
                break;
            case Kind.BottomUpBgr24 or Kind.TopDownBgr24:
                ReadBgr24(data, (YuvRaster<byte>)planes, Shape == Kind.TopDownBgr24);
                break;
            default:
                int position = 0;
                foreach (int plane in PlaneOrder())
                {
                    position += planes is YuvRaster<byte> b ? ReadPlane(data[position..], PlaneOf(b, plane)) : ReadPlane(data[position..], PlaneOf((YuvRaster<ushort>)planes, plane));
                }

                break;
        }
    }

    /// <summary>Writes a frame's samples from planes of this layout's format.</summary>
    public void Write(YuvRaster planes, Span<byte> data)
    {
        switch (Shape)
        {
            case Kind.Packed:
                planes.CopyToPacked(PackedFormat(), data);
                break;
            case Kind.V210:
                WriteV210((YuvRaster<ushort>)planes, data);
                break;
            case Kind.BottomUpBgr24 or Kind.TopDownBgr24:
                WriteBgr24((YuvRaster<byte>)planes, data, Shape == Kind.TopDownBgr24);
                break;
            default:
                int position = 0;
                foreach (int plane in PlaneOrder())
                {
                    position += planes is YuvRaster<byte> b ? WritePlane(PlaneOf(b, plane), data[position..]) : WritePlane(PlaneOf((YuvRaster<ushort>)planes, plane), data[position..]);
                }

                break;
        }
    }

    /// <summary>Whether planes match this layout's chroma format, depth and alpha, so <see cref="Write"/> can take them.</summary>
    public bool Matches(YuvRaster planes) => Shape is Kind.BottomUpBgr24 or Kind.TopDownBgr24
        ? planes.ChromaFormat == ChromaFormat.Yuv444 && planes.BitDepth == 8 && planes.Color.Matrix == YuvMatrix.Identity
        : planes.ChromaFormat == Chroma && planes.BitDepth == BitDepth && planes.HasAlpha == Alpha;

    private YuvPackedFormat PackedFormat() => FourCC switch
    {
        "NV12" => YuvPackedFormat.Nv12,
        "NV21" => YuvPackedFormat.Nv21,
        "UYVY" => YuvPackedFormat.Uyvy,
        _ => YuvPackedFormat.Yuyv,
    };

    private int[] PlaneOrder() => Chroma == ChromaFormat.Yuv400
        ? Alpha ? [0, 3] : [0]
        : Shape == Kind.PlanarSwapped ? [0, 2, 1] : Alpha ? [0, 1, 2, 3] : [0, 1, 2];

    private static YuvPlane<T> PlaneOf<T>(YuvRaster<T> raster, int plane)
        where T : unmanaged => plane switch
        {
            0 => raster.Y,
            1 => raster.U!,
            2 => raster.V!,
            _ => raster.A!,
        };

    private static int ReadPlane(ReadOnlySpan<byte> data, YuvPlane<byte> plane)
    {
        int w = plane.Width;
        for (int y = 0; y < plane.Height; y++)
        {
            data.Slice(y * w, w).CopyTo(plane.Row(y));
        }

        return w * plane.Height;
    }

    private static int ReadPlane(ReadOnlySpan<byte> data, YuvPlane<ushort> plane)
    {
        int w = plane.Width;
        for (int y = 0; y < plane.Height; y++)
        {
            Span<ushort> row = plane.Row(y);
            ReadOnlySpan<byte> source = data.Slice(y * w * 2, w * 2);
            for (int x = 0; x < w; x++)
            {
                row[x] = BinaryPrimitives.ReadUInt16LittleEndian(source[(x * 2)..]);
            }
        }

        return w * plane.Height * 2;
    }

    private static int WritePlane(YuvPlane<byte> plane, Span<byte> data)
    {
        int w = plane.Width;
        for (int y = 0; y < plane.Height; y++)
        {
            plane.Row(y).CopyTo(data.Slice(y * w, w));
        }

        return w * plane.Height;
    }

    private static int WritePlane(YuvPlane<ushort> plane, Span<byte> data)
    {
        int w = plane.Width;
        for (int y = 0; y < plane.Height; y++)
        {
            Span<ushort> row = plane.Row(y);
            Span<byte> target = data.Slice(y * w * 2, w * 2);
            for (int x = 0; x < w; x++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(target[(x * 2)..], row[x]);
            }
        }

        return w * plane.Height * 2;
    }

    // v210 (10-bit 4:2:2): six pixels in four little-endian 32-bit words, three 10-bit samples each in the order
    // Cb0 Y0 Cr0 | Y1 Cb2 Y2 | Cr2 Y3 Cb4 | Y4 Cr4 Y5; rows padded to a multiple of 48 pixels (128 bytes).
    private static void ReadV210(ReadOnlySpan<byte> data, YuvRaster<ushort> planes)
    {
        int width = planes.Width;
        int stride = (width + 47) / 48 * 128;
        Span<ushort> samples = stackalloc ushort[12];
        for (int y = 0; y < planes.Height; y++)
        {
            ReadOnlySpan<byte> row = data.Slice(y * stride, stride);
            Span<ushort> yRow = planes.Y.Row(y), uRow = planes.U!.Row(y), vRow = planes.V!.Row(y);
            for (int group = 0; group * 6 < width; group++)
            {
                for (int word = 0; word < 4; word++)
                {
                    uint value = BinaryPrimitives.ReadUInt32LittleEndian(row[((group * 16) + (word * 4))..]);
                    samples[word * 3] = (ushort)(value & 0x3FF);
                    samples[(word * 3) + 1] = (ushort)((value >> 10) & 0x3FF);
                    samples[(word * 3) + 2] = (ushort)((value >> 20) & 0x3FF);
                }

                // Sample order: Cb0 Y0 Cr0 Y1 Cb2 Y2 Cr2 Y3 Cb4 Y4 Cr4 Y5.
                for (int i = 0; i < 3; i++)
                {
                    int x = (group * 6) + (i * 2);
                    int c = (group * 3) + i;
                    if (c < uRow.Length)
                    {
                        uRow[c] = samples[i * 4];
                        vRow[c] = samples[(i * 4) + 2];
                    }

                    if (x < width)
                    {
                        yRow[x] = samples[(i * 4) + 1];
                    }

                    if (x + 1 < width)
                    {
                        yRow[x + 1] = samples[(i * 4) + 3];
                    }
                }
            }
        }
    }

    private static void WriteV210(YuvRaster<ushort> planes, Span<byte> data)
    {
        int width = planes.Width;
        int stride = (width + 47) / 48 * 128;
        data[..(stride * planes.Height)].Clear();
        Span<ushort> samples = stackalloc ushort[12];
        for (int y = 0; y < planes.Height; y++)
        {
            Span<byte> row = data.Slice(y * stride, stride);
            Span<ushort> yRow = planes.Y.Row(y), uRow = planes.U!.Row(y), vRow = planes.V!.Row(y);
            for (int group = 0; group * 6 < width; group++)
            {
                samples.Clear();
                for (int i = 0; i < 3; i++)
                {
                    int x = (group * 6) + (i * 2);
                    int c = (group * 3) + i;
                    samples[i * 4] = c < uRow.Length ? uRow[c] : (ushort)0;
                    samples[(i * 4) + 2] = c < vRow.Length ? vRow[c] : (ushort)0;
                    samples[(i * 4) + 1] = x < width ? yRow[x] : (ushort)0;
                    samples[(i * 4) + 3] = x + 1 < width ? yRow[x + 1] : (ushort)0;
                }

                for (int word = 0; word < 4; word++)
                {
                    uint value = (uint)(samples[word * 3] & 0x3FF) | ((uint)(samples[(word * 3) + 1] & 0x3FF) << 10) | ((uint)(samples[(word * 3) + 2] & 0x3FF) << 20);
                    BinaryPrimitives.WriteUInt32LittleEndian(row[((group * 16) + (word * 4))..], value);
                }
            }
        }
    }

    // AVI BI_RGB 24-bit: bottom-up (or, with a negative height, top-down) rows of B, G, R padded to 4 bytes. Planes hold G, B, R (the identity matrix).
    private static void ReadBgr24(ReadOnlySpan<byte> data, YuvRaster<byte> planes, bool topDown)
    {
        int stride = ((planes.Width * 3) + 3) / 4 * 4;
        for (int y = 0; y < planes.Height; y++)
        {
            ReadOnlySpan<byte> row = data.Slice((topDown ? y : planes.Height - 1 - y) * stride, planes.Width * 3);
            Span<byte> g = planes.Y.Row(y), b = planes.U!.Row(y), r = planes.V!.Row(y);
            for (int x = 0; x < planes.Width; x++)
            {
                b[x] = row[x * 3];
                g[x] = row[(x * 3) + 1];
                r[x] = row[(x * 3) + 2];
            }
        }
    }

    private static void WriteBgr24(YuvRaster<byte> planes, Span<byte> data, bool topDown)
    {
        int stride = ((planes.Width * 3) + 3) / 4 * 4;
        data[..(stride * planes.Height)].Clear();
        for (int y = 0; y < planes.Height; y++)
        {
            Span<byte> row = data.Slice((topDown ? y : planes.Height - 1 - y) * stride, planes.Width * 3);
            Span<byte> g = planes.Y.Row(y), b = planes.U!.Row(y), r = planes.V!.Row(y);
            for (int x = 0; x < planes.Width; x++)
            {
                row[x * 3] = b[x];
                row[(x * 3) + 1] = g[x];
                row[(x * 3) + 2] = r[x];
            }
        }
    }
}
