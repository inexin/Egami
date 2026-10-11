using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Formats.Tiff;

/// <summary>
/// Writes TIFF and BigTIFF, written from the TIFF 6.0 specification and the BigTIFF design note: each raster at its own pixel
/// type, in strips of about 64 KB or square tiles, little-endian unless the TIFF source was big-endian, Deflate with a predictor by
/// default. Each page's data comes first and its IFD after it; the header's first-IFD offset and each page's link are patched once
/// known, so a stream that cannot seek is written into memory first.
/// </summary>
internal sealed class TiffWriter : IFormatWriter, IRowFormatWriter, IMultiImageWriter
{
    internal static readonly TiffWriter Instance = new();

    public void Write<TPixel>(Raster<TPixel> raster, Stream output, WriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        RowEncoding.Save(this, raster, output, options);
    }

    public RowEncoder<TPixel> Start<TPixel>(Stream output, EncodeStart<TPixel> start)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        TiffOutput file = new(output, TiffPlan.BigEndianFor(start.Metadata, start.Options), TiffPlan.LayoutFor(start.Options, start.Width, start.Height, TPixel.Layout, 1));
        return new TiffPageEncoder<TPixel>(file, start, pageIndex: 0, pageCount: 1, ownsFile: true);
    }

    public void WriteAll(IReadOnlyList<Raster> rasters, Stream output, WriterOptions options)
    {
        long pixels = 0;
        foreach (Raster raster in rasters)
        {
            pixels += (long)raster.Width * raster.Height * raster.Layout.BytesPerPixel;
        }

        bool big = TiffPlan.Big(options, pixels);
        using TiffOutput file = new(output, TiffPlan.BigEndianFor(rasters[0].Metadata, options), big);
        for (int i = 0; i < rasters.Count; i++)
        {
            rasters[i].Accept(new PageVisitor(file, options, i, rasters.Count));
        }

        file.Finish();
    }

    private sealed class PageVisitor(TiffOutput file, WriterOptions options, int index, int count) : IRasterVisitor<bool>
    {
        public bool Visit<TPixel>(Raster<TPixel> raster)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            raster.ThrowIfDisposed();
            EncodeStart<TPixel> start = new(raster.Width, raster.Height, raster.Metadata, options, raster.Context, raster, seekable: false);
            using TiffPageEncoder<TPixel> encoder = new(file, start, index, count, ownsFile: false);
            foreach (int _ in encoder.WriteWhole(raster))
            {
            }

            encoder.Complete();
            return true;
        }
    }
}

/// <summary>
/// The file being written: the caller's stream when it can seek, else memory copied to it at the end; the header, written first,
/// and the link to patch when each page's IFD is placed.
/// </summary>
internal sealed class TiffOutput : IDisposable
{
    private readonly Stream _target;
    private readonly Stream _stream;
    private readonly long _origin;
    private long _link;
    private bool _finished;

    public TiffOutput(Stream target, bool bigEndian, bool big)
    {
        _target = target;
        _stream = target.CanSeek ? target : new MemoryStream();
        _origin = _stream.Position;
        BigEndian = bigEndian;
        Big = big;
        Span<byte> header = stackalloc byte[16];
        header[0] = header[1] = bigEndian ? (byte)'M' : (byte)'I';
        Put16(header[2..], (ushort)(big ? 43 : 42));
        if (big)
        {
            Put16(header[4..], 8);
            Put16(header[6..], 0);
            Put64(header[8..], 0);
        }
        else
        {
            Put32(header[4..], 0);
        }

        _stream.Write(header[..(big ? 16 : 8)]);
        _link = big ? 8 : 4;
    }

    public bool BigEndian { get; }

    public bool Big { get; }

    /// <summary>Gets the position from the start of the file.</summary>
    public long Position => _stream.Position - _origin;

    public void Write(ReadOnlySpan<byte> bytes) => _stream.Write(bytes);

    /// <summary>Pads to an even offset (classic) or a multiple of 8 (BigTIFF), as values and IFDs are aligned.</summary>
    public void Align()
    {
        int unit = Big ? 8 : 2;
        int pad = (int)((unit - (Position % unit)) % unit);
        Span<byte> zeros = stackalloc byte[8];
        zeros.Clear();
        _stream.Write(zeros[..pad]);
    }

    /// <summary>Points the previous link (the header or the last IFD's next field) at <paramref name="ifd"/>, and remembers this IFD's next field.</summary>
    public void Link(long ifd, long next)
    {
        Span<byte> value = stackalloc byte[8];
        int size = Big ? 8 : 4;
        if (Big)
        {
            Put64(value, (ulong)ifd);
        }
        else
        {
            Put32(value, checked((uint)ifd));
        }

        long end = _stream.Position;
        _stream.Position = _origin + _link;
        _stream.Write(value[..size]);
        _stream.Position = end;
        _link = next;
    }

    /// <summary>Copies a file written into memory to the caller's stream.</summary>
    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        if (!ReferenceEquals(_stream, _target))
        {
            _stream.Position = 0;
            _stream.CopyTo(_target);
        }
    }

    public void Put16(Span<byte> b, ushort v)
    {
        if (BigEndian)
        {
            BinaryPrimitives.WriteUInt16BigEndian(b, v);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        }
    }

    public void Put32(Span<byte> b, uint v)
    {
        if (BigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(b, v);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        }
    }

    public void Put64(Span<byte> b, ulong v)
    {
        if (BigEndian)
        {
            BinaryPrimitives.WriteUInt64BigEndian(b, v);
        }
        else
        {
            BinaryPrimitives.WriteUInt64LittleEndian(b, v);
        }
    }

    public void Dispose()
    {
        if (!ReferenceEquals(_stream, _target))
        {
            _stream.Dispose();
        }
    }
}

/// <summary>How a page is stored: the pixel type its rows are converted to, and the tags that describe it.</summary>
internal enum TiffStore
{
    Gray8,
    GrayAlpha8,
    Gray16,
    GrayAlpha16,
    GrayF32,
    Rgb8,
    Rgba8,
    Rgb16,
    Rgba16,
    RgbF32,
    RgbaF32,
    RgbaF16,
    Cmyk8,
    Cmyk16,
    PremulRgba8,
    PremulRgbaF32,
}

/// <summary>The choices for one page: what its samples are, how they are compressed, and how they are laid out.</summary>
internal sealed record TiffPlan(TiffStore Store, int Photometric, int Samples, int Bits, int Format, int Extra, int Compression, int Predictor,
    int TileSize, bool AssociatedFromStraight)
{
    /// <summary>Gets the bytes of a pixel as stored.</summary>
    public int PixelBytes => Samples * Bits / 8;

    /// <summary>Chooses how <typeparamref name="TPixel"/> is stored, from the design's table, and the compression and layout from the options and a TIFF source.</summary>
    public static TiffPlan For<TPixel>(RasterMetadata metadata, WriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        TiffWriterOptions tiff = options as TiffWriterOptions ?? TiffWriterOptions.Default;
        TiffDetails? source = metadata.FormatDetails as TiffDetails;
        TiffStore store = StoreFor<TPixel>();
        bool associated = false;
        if (tiff.AssociatedAlpha)
        {
            (store, associated) = store switch
            {
                TiffStore.Rgba8 => (TiffStore.PremulRgba8, true),
                TiffStore.RgbaF32 => (TiffStore.PremulRgbaF32, true),
                TiffStore.GrayAlpha8 or TiffStore.GrayAlpha16 or TiffStore.Rgba16 or TiffStore.RgbaF16 => (store, true),
                _ => (store, false),
            };
        }

        (int photometric, int samples, int bits, int format, int extra) = store switch
        {
            TiffStore.Gray8 => (1, 1, 8, 1, -1),
            TiffStore.GrayAlpha8 => (1, 2, 8, 1, associated ? 1 : 2),
            TiffStore.Gray16 => (1, 1, 16, 1, -1),
            TiffStore.GrayAlpha16 => (1, 2, 16, 1, associated ? 1 : 2),
            TiffStore.GrayF32 => (1, 1, 32, 3, -1),
            TiffStore.Rgb8 => (2, 3, 8, 1, -1),
            TiffStore.Rgba8 => (2, 4, 8, 1, 2),
            TiffStore.Rgb16 => (2, 3, 16, 1, -1),
            TiffStore.Rgba16 => (2, 4, 16, 1, associated ? 1 : 2),
            TiffStore.RgbF32 => (2, 3, 32, 3, -1),
            TiffStore.RgbaF32 => (2, 4, 32, 3, 2),
            TiffStore.RgbaF16 => (2, 4, 16, 3, associated ? 1 : 2),
            TiffStore.Cmyk8 => (5, 4, 8, 1, -1),
            TiffStore.Cmyk16 => (5, 4, 16, 1, -1),
            TiffStore.PremulRgba8 => (2, 4, 8, 1, 1),
            _ => (2, 4, 32, 3, 1),
        };

        int compression = tiff.Compression is TiffCompression chosen ? (int)chosen : SourceCompression(source);
        if (compression is TiffCodes.AdobeDeflate)
        {
            compression = TiffCodes.Deflate;
        }

        if (compression is not (TiffCodes.None or TiffCodes.Lzw or TiffCodes.Deflate or TiffCodes.PackBits))
        {
            throw new NotSupportedException($"TIFF: writing {TiffCodes.Name(compression)} compression is not supported yet.");
        }

        int predictor = 1;
        if (compression is TiffCodes.Lzw or TiffCodes.Deflate)
        {
            TiffPredictor? wanted = tiff.Predictor ?? (tiff.Compression is null ? source?.Predictor : null);
            predictor = wanted switch
            {
                TiffPredictor.None => 1,
                TiffPredictor.Horizontal when format != 3 => 2,
                TiffPredictor.FloatingPoint when format == 3 => 3,
                _ => format == 3 ? 3 : 2,
            };
            if (wanted is TiffPredictor.Horizontal && format == 3 && tiff.Predictor is not null)
            {
                predictor = 2;
            }
        }

        int tile = tiff.TileSize ?? (source is { TileWidth: int w, TileHeight: int h } && w == h && w % 16 == 0 ? w : 0);
        return new TiffPlan(store, photometric, samples, bits, format, extra, compression, predictor, tile, associated && store is not (TiffStore.PremulRgba8 or TiffStore.PremulRgbaF32));
    }

    /// <summary>Returns a TIFF source's compression when the writer can write it, else Deflate.</summary>
    private static int SourceCompression(TiffDetails? source) => source?.Compression switch
    {
        TiffCompression.None => TiffCodes.None,
        TiffCompression.Lzw => TiffCodes.Lzw,
        TiffCompression.PackBits => TiffCodes.PackBits,
        _ => TiffCodes.Deflate,
    };

    /// <summary>Returns the stored type for each Egami pixel type: its own where TIFF has it, widened where it does not.</summary>
    public static TiffStore StoreFor<TPixel>()
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Type t = typeof(TPixel);
        if (t == typeof(Gray8) || t == typeof(Alpha8))
        {
            return TiffStore.Gray8;
        }

        if (t == typeof(GrayAlpha8))
        {
            return TiffStore.GrayAlpha8;
        }

        if (t == typeof(Gray16))
        {
            return TiffStore.Gray16;
        }

        if (t == typeof(GrayAlpha16))
        {
            return TiffStore.GrayAlpha16;
        }

        if (t == typeof(GrayF32))
        {
            return TiffStore.GrayF32;
        }

        if (t == typeof(Cmyk8))
        {
            return TiffStore.Cmyk8;
        }

        if (t == typeof(Cmyk16))
        {
            return TiffStore.Cmyk16;
        }

        if (t == typeof(PremulRgba8) || t == typeof(PremulBgra8))
        {
            return TiffStore.PremulRgba8;
        }

        if (t == typeof(PremulRgbaF32))
        {
            return TiffStore.PremulRgbaF32;
        }

        if (t == typeof(RgbaF16))
        {
            return TiffStore.RgbaF16;
        }

        PixelLayoutInfo layout = TPixel.Layout;
        bool alpha = layout.HasAlpha;
        return layout.ChannelType switch
        {
            PixelChannelType.Float32 or PixelChannelType.Float16 => alpha ? TiffStore.RgbaF32 : TiffStore.RgbF32,
            PixelChannelType.Unorm16 => alpha ? TiffStore.Rgba16 : TiffStore.Rgb16,
            PixelChannelType.Packed when layout.MaxChannelBits > 8 => alpha ? TiffStore.Rgba16 : TiffStore.Rgb16,
            _ => alpha ? TiffStore.Rgba8 : TiffStore.Rgb8,
        };
    }

    /// <summary>Returns whether a TIFF source was big-endian, which a re-save keeps.</summary>
    public static bool BigEndianFor(RasterMetadata metadata, WriterOptions options) =>
        metadata.FormatDetails is TiffDetails { ByteOrder: TiffByteOrder.BigEndian } && options is not null;

    /// <summary>Chooses classic TIFF or BigTIFF for one page of this size, throwing for classic TIFF when it cannot fit.</summary>
    public static bool LayoutFor(WriterOptions options, int width, int height, PixelLayoutInfo layout, int frames) =>
        Big(options, (long)width * height * layout.BytesPerPixel * frames);

    /// <summary>Chooses classic TIFF or BigTIFF for <paramref name="pixelBytes"/> of uncompressed pixels.</summary>
    public static bool Big(WriterOptions options, long pixelBytes)
    {
        TiffLayout layout = (options as TiffWriterOptions)?.Layout ?? TiffLayout.Classic;
        const long Limit = (4L << 30) - (64 << 20);
        return layout switch
        {
            TiffLayout.Big => true,
            TiffLayout.Auto => pixelBytes > Limit,
            _ => pixelBytes > Limit
                ? throw new NotSupportedException($"TIFF: {pixelBytes} bytes of pixels may not fit classic TIFF's 4 GB. Set TiffWriterOptions.Layout to Big or Auto.")
                : false,
        };
    }
}
