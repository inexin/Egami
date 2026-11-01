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
        SaveReporter? reporter = SaveReporter.Current;
        for (int i = 0; i < rasters.Count; i++)
        {
            // A save's progress: each page's rows fill its share.
            reporter?.Part((double)i / rasters.Count, (double)(i + 1) / rasters.Count);
            rasters[i].Accept(new PageVisitor(file, options, i, rasters.Count));
            reporter?.Frames(i + 1, rasters.Count);
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
    Bilevel,
    Palette,
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
    /// <summary>Gets the bytes of a pixel as stored, for whole-byte samples.</summary>
    public int PixelBytes => Samples * Bits / 8;

    /// <summary>Gets the palette of a reduced page: 256 entries at most, in index order.</summary>
    public Rgb8[] Palette { get; init; } = [];

    /// <summary>Gets the quality of JPEG and lossy WebP compression.</summary>
    public int Quality { get; init; } = 75;

    /// <summary>Gets whether WebP or JPEG units are lossless: lossless WebP, or lossless JPEG (SOF3).</summary>
    public bool Lossless { get; init; }

    /// <summary>Returns the bytes of a stored row of <paramref name="width"/> pixels.</summary>
    public int RowBytes(int width) => (int)((((long)width * Samples * Bits) + 7) / 8);

    /// <summary>
    /// Chooses how <typeparamref name="TPixel"/> is stored, from the design's table (or the smallest exact form, with Reduce), and
    /// the compression and layout from the options and a TIFF source. <paramref name="whole"/> is the image when Save has it.
    /// </summary>
    public static TiffPlan For<TPixel>(RasterMetadata metadata, WriterOptions options, Raster<TPixel>? whole)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        TiffWriterOptions tiff = options as TiffWriterOptions ?? TiffWriterOptions.Default;
        TiffDetails? source = metadata.FormatDetails as TiffDetails;
        int compression = tiff.Compression is TiffCompression chosen ? (int)chosen : SourceCompression(source, tiff);
        if (compression is TiffCodes.AdobeDeflate)
        {
            compression = TiffCodes.Deflate;
        }

        if (compression is not (TiffCodes.None or TiffCodes.Lzw or TiffCodes.Deflate or TiffCodes.Zstd or TiffCodes.Lzma or TiffCodes.PackBits
            or TiffCodes.CcittGroup3 or TiffCodes.CcittGroup4 or TiffCodes.Jpeg or TiffCodes.Webp))
        {
            throw new ArgumentException($"TIFF: {TiffCodes.Name(compression)} compression is read only; it cannot be written.", nameof(options));
        }

        bool ccitt = compression is TiffCodes.CcittGroup3 or TiffCodes.CcittGroup4;
        TiffStore store = StoreFor<TPixel>();
        Rgb8[] palette = [];
        if (ccitt && tiff.Compression is null && !tiff.Reduce && source is { BitsPerSample: 1, Unchanged: true })
        {
            // A 1-bit source unchanged since loading is bilevel without looking, so Save and a streamed encode agree.
            store = TiffStore.Bilevel;
        }
        else if ((tiff.Reduce || ccitt) && whole is not null)
        {
            (store, palette) = TiffReduce.Analyze(whole, store, allowPalette: tiff.Reduce && !ccitt && compression is not (TiffCodes.Jpeg or TiffCodes.Webp));
        }

        bool kept = tiff.Compression is null;
        bool lossless = false;

        // Lossless JPEG is asked for as WebP's lossless form is, with quality 100, or kept from a lossless JPEG source.
        bool losslessJpeg = compression == TiffCodes.Jpeg && (tiff.Quality == 100 || (kept && source is { Compression: TiffCompression.Jpeg, JpegLossless: true }));
        if (ccitt && store != TiffStore.Bilevel)
        {
            if (!kept)
            {
                throw new ArgumentException("TIFF: CCITT compression takes 1-bit images only, where every pixel is black or white.", nameof(options));
            }

            // A source's compression that no longer suits the pixels gives way to the default.
            (compression, ccitt) = (TiffCodes.Deflate, false);
        }

        if (kept && compression == TiffCodes.Jpeg && store is not (TiffStore.Rgb8 or TiffStore.Rgb16 or TiffStore.RgbF32 or TiffStore.Gray8
            or TiffStore.Gray16 or TiffStore.GrayF32) && !(losslessJpeg && store is TiffStore.Cmyk8 or TiffStore.Cmyk16))
        {
            compression = TiffCodes.Deflate;
        }

        if (kept && compression == TiffCodes.Webp && store is TiffStore.Cmyk8 or TiffStore.Cmyk16)
        {
            compression = TiffCodes.Deflate;
        }

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

        int paletteBits = palette.Length switch
        {
            <= 2 => 1,
            <= 4 => 2,
            <= 16 => 4,
            _ => 8,
        };
        (int photometric, int samples, int bits, int format, int extra) = store switch
        {
            TiffStore.Bilevel => (0, 1, 1, 1, -1),
            TiffStore.Palette => (3, 1, paletteBits, 1, -1),
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

        if (compression == TiffCodes.Jpeg && losslessJpeg)
        {
            // Lossless JPEG (SOF3) keeps 8 and 16-bit samples as they are, RGB as RGB and the inks as they are; floats go to 16 bits.
            (store, photometric, samples, bits, format, extra) = store switch
            {
                TiffStore.Gray8 => (TiffStore.Gray8, 1, 1, 8, 1, -1),
                TiffStore.Gray16 or TiffStore.GrayF32 => (TiffStore.Gray16, 1, 1, 16, 1, -1),
                TiffStore.Rgb8 => (TiffStore.Rgb8, 2, 3, 8, 1, -1),
                TiffStore.Rgb16 or TiffStore.RgbF32 => (TiffStore.Rgb16, 2, 3, 16, 1, -1),
                TiffStore.Cmyk8 => (TiffStore.Cmyk8, 5, 4, 8, 1, -1),
                TiffStore.Cmyk16 => (TiffStore.Cmyk16, 5, 4, 16, 1, -1),
                _ => throw new ArgumentException("TIFF: lossless JPEG compression takes gray, RGB and CMYK images without alpha.", nameof(options)),
            };
            lossless = true;
        }
        else if (compression == TiffCodes.Jpeg)
        {
            if (store is TiffStore.Rgb8 or TiffStore.Rgb16 or TiffStore.RgbF32)
            {
                // RGB goes in as YCbCr 4:2:0, as libtiff's JPEGCOLORMODE does; deeper RGB is narrowed to 8 bits first.
                (store, photometric, samples, bits, format, extra) = (TiffStore.Rgb8, 6, 3, 8, 1, -1);
            }
            else if (store is TiffStore.Gray8 or TiffStore.Gray16 or TiffStore.GrayF32)
            {
                (store, photometric, samples, bits, format, extra) = (TiffStore.Gray8, 1, 1, 8, 1, -1);
            }
            else if (store is TiffStore.Cmyk8 or TiffStore.Cmyk16)
            {
                // Four components with no Adobe marker and the inks as they are, as libtiff writes separated JPEG.
                (store, photometric, samples, bits, format, extra) = (TiffStore.Cmyk8, 5, 4, 8, 1, -1);
            }
            else
            {
                throw new ArgumentException("TIFF: JPEG compression takes gray and RGB images without alpha.", nameof(options));
            }
        }

        if (compression == TiffCodes.Webp)
        {
            if (store is TiffStore.Cmyk8 or TiffStore.Cmyk16)
            {
                throw new ArgumentException("TIFF: WebP compression takes RGB and RGBA images.", nameof(options));
            }

            bool alpha = store is TiffStore.Rgba8 or TiffStore.Rgba16 or TiffStore.RgbaF32 or TiffStore.GrayAlpha8 or TiffStore.GrayAlpha16
                or TiffStore.RgbaF16 or TiffStore.PremulRgba8 or TiffStore.PremulRgbaF32;
            (store, photometric, samples, bits, format, extra, associated) = alpha
                ? (TiffStore.Rgba8, 2, 4, 8, 1, 2, false)
                : (TiffStore.Rgb8, 2, 3, 8, 1, -1, false);
            lossless = tiff.Quality == 100 || (tiff.Compression is null && source is { Compression: TiffCompression.Webp, WebpLossless: true });
        }

        int predictor = 1;
        if (compression is TiffCodes.Lzw or TiffCodes.Deflate or TiffCodes.Zstd or TiffCodes.Lzma && bits >= 8 && store != TiffStore.Palette)
        {
            TiffPredictor? wanted = tiff.Predictor ?? (tiff.Compression is null ? source?.Predictor : null);
            predictor = wanted switch
            {
                TiffPredictor.None => 1,
                TiffPredictor.Horizontal => 2,
                TiffPredictor.FloatingPoint when format == 3 => 3,
                _ => format == 3 ? 3 : 2,
            };
        }

        int tile = tiff.TileSize ?? (source is { TileWidth: int w, TileHeight: int h } && w == h && w % 16 == 0 ? w : 0);
        return new TiffPlan(store, photometric, samples, bits, format, extra, compression, predictor, tile,
            associated && store is not (TiffStore.PremulRgba8 or TiffStore.PremulRgbaF32))
        {
            Palette = palette,
            Quality = tiff.Quality,
            Lossless = lossless,
        };
    }

    /// <summary>
    /// Returns a TIFF source's compression when the writer can write it and the options leave it, else Deflate. JPEG and WebP are
    /// kept only while the raster is unchanged since loading, to avoid a second lossy pass.
    /// </summary>
    private static int SourceCompression(TiffDetails? source, TiffWriterOptions options) => source?.Compression switch
    {
        TiffCompression.None => TiffCodes.None,
        TiffCompression.Lzw => TiffCodes.Lzw,
        TiffCompression.Zstd => TiffCodes.Zstd,
        TiffCompression.Lzma => TiffCodes.Lzma,
        TiffCompression.PackBits => TiffCodes.PackBits,
        TiffCompression.CcittGroup3 when source.BitsPerSample == 1 && (options.Reduce || source.Unchanged) => TiffCodes.CcittGroup3,
        TiffCompression.CcittGroup4 when source.BitsPerSample == 1 && (options.Reduce || source.Unchanged) => TiffCodes.CcittGroup4,
        TiffCompression.Jpeg when source.Unchanged || source.JpegLossless => TiffCodes.Jpeg,
        TiffCompression.Webp when source.Unchanged => TiffCodes.Webp,
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
