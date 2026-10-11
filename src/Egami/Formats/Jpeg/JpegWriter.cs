using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;

using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Formats.Jpeg;

/// <summary>
/// Writes JPEG files with Huffman coding (T.81 annexes F and G), one MCU row at a time: rows are converted to YCbCr, gray, CMYK or
/// YCCK the way <c>cjpeg</c> does, chroma is averaged down, and each block goes through the forward DCT, quantization and the
/// Huffman coder. Baseline 8-bit files are coded as the rows come; with optimized tables, 12-bit samples or progressive scans the
/// quantized blocks are kept for later passes. Only the first frame is written.
/// </summary>
internal sealed class JpegWriter : IFormatWriter, IRowFormatWriter
{
    internal static readonly JpegWriter Instance = new();

    private static ReadOnlySpan<byte> JfifId => "JFIF\0"u8;

    private static ReadOnlySpan<byte> ExifId => "Exif\0\0"u8;

    private static ReadOnlySpan<byte> XmpId => "http://ns.adobe.com/xap/1.0/\0"u8;

    private static ReadOnlySpan<byte> PhotoshopId => "Photoshop 3.0\0"u8;

    private static ReadOnlySpan<byte> ExtendedXmpId => "http://ns.adobe.com/xmp/extension/\0"u8;

    private static ReadOnlySpan<byte> IccId => "ICC_PROFILE\0"u8;

    public void Write<TPixel>(Raster<TPixel> raster, Stream output, WriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        RowEncoding.Save(this, raster, output, options);
    }

    public RowEncoder<TPixel> Start<TPixel>(Stream output, EncodeStart<TPixel> start)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        JpegWriterOptions jpeg = start.Options as JpegWriterOptions ?? new JpegWriterOptions { StripMetadata = start.Options.StripMetadata };
        if (start.Width > ushort.MaxValue || start.Height > ushort.MaxValue)
        {
            throw new InvalidOperationException($"JPEG holds at most 65,535 × 65,535 pixels; the image is {start.Width} × {start.Height}.");
        }

        return new RowWriter<TPixel>(output, start, jpeg);
    }

    /// <summary>
    /// Encodes 8-bit planes straight, with no color conversion or chroma downsampling: the planes' chroma format sets the
    /// sampling (4:0:0 or <see cref="JpegColorType.Gray"/> writes luma alone), and the planes are taken as JPEG's YCbCr.
    /// </summary>
    internal static void WritePlanes(YuvRaster<byte> planes, Stream output, JpegWriterOptions options)
    {
        if (planes.Width > ushort.MaxValue || planes.Height > ushort.MaxValue)
        {
            throw new InvalidOperationException($"JPEG holds at most 65,535 × 65,535 pixels; the image is {planes.Width} × {planes.Height}.");
        }

        EncodeStart<Rgb8> start = new(planes.Width, planes.Height, planes.Metadata, options, planes.Context, whole: null, seekable: false);
        using Encoder<Rgb8> encoder = new(start, options, planes);
        using JpegBitWriter writer = new(output);
        writer.Marker(JpegMarker.Soi);
        encoder.WriteIdentification(writer, options.StripMetadata ? null : planes.Metadata.Resolution);
        if (!options.StripMetadata)
        {
            WriteMetadata(writer, planes.Metadata, options.ProfileToWrite(planes.Metadata));
        }

        encoder.WriteHeaders(writer);
        foreach (int _ in encoder.WriteScan(writer))
        {
        }

        encoder.EndScan(writer);
        writer.Marker(JpegMarker.Eoi);
        writer.Flush();
    }

    /// <summary>
    /// Writes the markers before the scan at once; Save then codes the whole image, and a streamed encode codes each MCU row
    /// once its rows have come (keeping them all for the second pass when tables are optimized).
    /// </summary>
    private sealed class RowWriter<TPixel> : RowEncoder<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly Encoder<TPixel> _encoder;
        private readonly JpegBitWriter _writer;

        internal RowWriter(Stream output, EncodeStart<TPixel> start, JpegWriterOptions jpeg)
        {
            _encoder = new(start, jpeg);
            _writer = new(output);
            _writer.Marker(JpegMarker.Soi);
            _encoder.WriteIdentification(_writer, jpeg.StripMetadata ? null : start.Metadata.Resolution);
            if (!jpeg.StripMetadata)
            {
                WriteMetadata(_writer, start.Metadata, start.Options.ProfileToWrite(start.Metadata));
            }

            _encoder.WriteHeaders(_writer);
            if (start.Streaming)
            {
                _encoder.StartStream(_writer);
            }
        }

        public override void WriteRow(ReadOnlySpan<TPixel> row)
        {
            _encoder.StreamRow(_writer, row);
        }

        public override IEnumerable<int> WriteWhole(Raster<TPixel> raster)
        {
            return _encoder.WriteScan(_writer);
        }

        public override void Complete()
        {
            _encoder.EndScan(_writer);
            _writer.Marker(JpegMarker.Eoi);
            _writer.Flush();
        }

        protected override void Dispose(bool disposing)
        {
            _encoder.Dispose();
            _writer.Dispose();
            base.Dispose(disposing);
        }
    }

    private static void Segment(JpegBitWriter writer, byte marker, ReadOnlySpan<byte> payload)
    {
        writer.Marker(marker);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)(payload.Length + 2));
        writer.Raw(length);
        writer.Raw(payload);
    }

    private static void Segment(JpegBitWriter writer, byte marker, ReadOnlySpan<byte> id, ReadOnlySpan<byte> payload)
    {
        byte[] data = new byte[id.Length + payload.Length];
        id.CopyTo(data);
        payload.CopyTo(data.AsSpan(id.Length));
        Segment(writer, marker, data);
    }

    /// <summary>JFIF APP0, version 1.01, with the density from <paramref name="resolution"/> or 1:1 without a unit.</summary>
    private static void WriteJfif(JpegBitWriter writer, Resolution? resolution)
    {
        Span<byte> data = stackalloc byte[14];
        JfifId.CopyTo(data);
        data[5] = 1;
        data[6] = 1;
        (byte unit, ushort x, ushort y) = resolution is Resolution r && r.X > 0 && r.Y > 0
            ? (r.Unit switch { ResolutionUnit.PerInch => (byte)1, ResolutionUnit.PerCentimeter => (byte)2, _ => (byte)0 }, Density(r.X), Density(r.Y))
            : ((byte)0, (ushort)1, (ushort)1);
        data[7] = unit;
        BinaryPrimitives.WriteUInt16BigEndian(data[8..], x);
        BinaryPrimitives.WriteUInt16BigEndian(data[10..], y);
        Segment(writer, JpegMarker.App0, data);
    }

    private static ushort Density(double value) => (ushort)Math.Clamp(Math.Round(value), 1, ushort.MaxValue);

    /// <summary>
    /// Adobe APP14, version 100, with the color transform: 0 for CMYK (or RGB), 2 for YCCK. libjpeg writes it in place of JFIF for
    /// four components, and readers take it to mean the values are inverted, as Photoshop writes them.
    /// </summary>
    private static void WriteAdobe(JpegBitWriter writer, byte transform)
    {
        ReadOnlySpan<byte> data = [(byte)'A', (byte)'d', (byte)'o', (byte)'b', (byte)'e', 0, 100, 0, 0, 0, 0, transform];
        Segment(writer, JpegMarker.App14, data);
    }

    /// <summary>
    /// EXIF (APP1), ICC (APP2 chunks), XMP (APP1, with extended XMP when large), Photoshop resources with the IPTC (APP13) and
    /// comments, in that order.
    /// </summary>
    private static void WriteMetadata(JpegBitWriter writer, RasterMetadata metadata, ReadOnlyMemory<byte> profile)
    {
        // An EXIF block too large for one segment loses its thumbnail first; if it is still too large it is left out.
        if (metadata.Exif is ExifData exifData)
        {
            int max = ushort.MaxValue - 2 - ExifId.Length;
            ReadOnlyMemory<byte> exif = exifData.ToBytes();
            if (exif.Length > max)
            {
                ExifData smaller = exifData.Copy();
                smaller.RemoveThumbnail();
                exif = smaller.ToBytes();
            }

            if (exif.Length <= max)
            {
                Segment(writer, JpegMarker.App1, ExifId, exif.Span);
            }
        }

        ReadOnlySpan<byte> icc = profile.Span;
        const int IccChunk = ushort.MaxValue - 2 - 14;
        int chunks = (icc.Length + IccChunk - 1) / IccChunk;
        if (chunks is > 0 and <= 255)
        {
            for (int i = 0; i < chunks; i++)
            {
                ReadOnlySpan<byte> part = icc.Slice(i * IccChunk, Math.Min(IccChunk, icc.Length - (i * IccChunk)));
                byte[] data = new byte[IccId.Length + 2 + part.Length];
                IccId.CopyTo(data);
                data[IccId.Length] = (byte)(i + 1);
                data[IccId.Length + 1] = (byte)chunks;
                part.CopyTo(data.AsSpan(IccId.Length + 2));
                Segment(writer, JpegMarker.App2, data);
            }
        }

        JpegDetails? details = metadata.FormatDetails as JpegDetails;
        WriteXmp(writer, metadata.Xmp.Span, details is null ? default : details.ExtendedXmp.Span);
        WritePhotoshop(writer, metadata, details?.PhotoshopResources ?? default);

        foreach (TextEntry entry in metadata.Text)
        {
            if (entry.Key != "Comment")
            {
                continue;
            }

            byte[] text = Encoding.UTF8.GetBytes(entry.Value);
            const int MaxComment = ushort.MaxValue - 2;
            for (int at = 0; at < text.Length; at += MaxComment)
            {
                Segment(writer, JpegMarker.Com, text.AsSpan(at, Math.Min(MaxComment, text.Length - at)));
            }
        }
    }

    /// <summary>
    /// The Photoshop resource block in APP13 segments of up to 64 KB: as read when the IPTC is unchanged, else with the IPTC
    /// resource and its digest replaced; a new block when there was none and there is IPTC.
    /// </summary>
    private static void WritePhotoshop(JpegBitWriter writer, RasterMetadata metadata, ReadOnlyMemory<byte> resources)
    {
        ReadOnlySpan<byte> iim = metadata.IptcBytes.Span;
        ReadOnlySpan<byte> block = resources.IsEmpty
            ? (iim.IsEmpty ? default : PhotoshopResources.Create(iim))
            : PhotoshopResources.FindIptc(resources, null).Span.SequenceEqual(iim) ? resources.Span : PhotoshopResources.WithIptc(resources.Span, iim);
        const int MaxPart = ushort.MaxValue - 2 - 14;
        for (int at = 0; at < block.Length; at += MaxPart)
        {
            ReadOnlySpan<byte> part = block.Slice(at, Math.Min(MaxPart, block.Length - at));
            byte[] data = new byte[PhotoshopId.Length + part.Length];
            PhotoshopId.CopyTo(data);
            part.CopyTo(data.AsSpan(PhotoshopId.Length));
            Segment(writer, JpegMarker.App13, data);
        }
    }

    /// <summary>
    /// The XMP packet in one APP1 segment. A packet too large for one segment moves whole into extended XMP, behind a small
    /// standard packet that names it by the MD5 of its bytes, as the XMP specification part 3 describes; an extended part read
    /// from a JPEG is written back after the standard packet it came with.
    /// </summary>
    private static void WriteXmp(JpegBitWriter writer, ReadOnlySpan<byte> xmp, ReadOnlySpan<byte> extended)
    {
        if (xmp.IsEmpty)
        {
            return;
        }

        int maxStandard = ushort.MaxValue - 2 - XmpId.Length;
        if (xmp.Length > maxStandard)
        {
            extended = xmp;
            xmp = Encoding.UTF8.GetBytes(
                "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
                + $"<rdf:Description rdf:about=\"\" xmlns:xmpNote=\"http://ns.adobe.com/xmp/note/\" xmpNote:HasExtendedXMP=\"{Guid(extended)}\"/>"
                + "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>");
        }

        Segment(writer, JpegMarker.App1, XmpId, xmp);
        if (extended.IsEmpty)
        {
            return;
        }

        byte[] guid = Encoding.ASCII.GetBytes(Guid(extended));
        const int Chunk = 65400;
        for (int at = 0; at < extended.Length; at += Chunk)
        {
            ReadOnlySpan<byte> part = extended.Slice(at, Math.Min(Chunk, extended.Length - at));
            byte[] data = new byte[ExtendedXmpId.Length + 40 + part.Length];
            ExtendedXmpId.CopyTo(data);
            guid.CopyTo(data.AsSpan(ExtendedXmpId.Length));
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(ExtendedXmpId.Length + 32), (uint)extended.Length);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(ExtendedXmpId.Length + 36), (uint)at);
            part.CopyTo(data.AsSpan(ExtendedXmpId.Length + 40));
            Segment(writer, JpegMarker.App1, data);
        }
    }

    /// <summary>The GUID that links extended XMP to its standard packet: the MD5 of the extended bytes, as 32 uppercase hex digits.</summary>
#pragma warning disable CA5351 // The XMP specification names MD5 for this identifier; it is not used for security.
    private static string Guid(ReadOnlySpan<byte> extended) => Convert.ToHexString(System.Security.Cryptography.MD5.HashData(extended));
#pragma warning restore CA5351

    /// <summary>
    /// Returns the quantization table for a quality, scaled from <paramref name="example"/> with the IJG formula. Values above 255
    /// would need 16-bit tables, which baseline does not allow; 12-bit files take them, up to 32,767, as libjpeg's do.
    /// </summary>
    private static ushort[] Scale(ReadOnlySpan<byte> example, int quality, bool wide)
    {
        int factor = quality < 50 ? 5000 / quality : 200 - (2 * quality);
        ushort[] table = new ushort[64];
        for (int i = 0; i < 64; i++)
        {
            table[i] = (ushort)Math.Clamp(((example[i] * factor) + 50) / 100, 1, wide ? short.MaxValue : 255);
        }

        return table;
    }

    /// <summary>
    /// The luma and chroma quantization tables for one quality, with the forms the transform uses. The last ones built are kept,
    /// since a program usually saves many images at one quality.
    /// </summary>
    private sealed class QuantTables
    {
        private static QuantTables? s_last;

        private QuantTables(int quality, bool wide)
        {
            Quality = quality;
            Wide = wide;
            Quant = [Scale(JpegTables.LumaQuant, quality, wide), Scale(JpegTables.ChromaQuant, quality, wide)];
            Divisors = new int[2][];
            Reciprocals = new ushort[2][];
            for (int t = 0; t < 2 && !wide; t++)
            {
                Divisors[t] = new int[64];
                for (int k = 0; k < 64; k++)
                {
                    Divisors[t][k] = Quant[t][k] * 8;
                }

                Reciprocals[t] = JpegFdct.Reciprocals(Divisors[t]);
            }
        }

        internal int Quality { get; }

        internal bool Wide { get; }

        internal ushort[][] Quant { get; }

        internal int[][] Divisors { get; }

        internal ushort[][] Reciprocals { get; }

        internal static QuantTables For(int quality, bool wide)
        {
            QuantTables? last = Volatile.Read(ref s_last);
            if (last is null || last.Quality != quality || last.Wide != wide)
            {
                last = new QuantTables(quality, wide);
                Volatile.Write(ref s_last, last);
            }

            return last;
        }
    }

    /// <summary>The components a file is written with.</summary>
    private enum Space
    {
        Gray,
        YCbCr,
        Cmyk,
        Ycck,
    }

    /// <summary>The encoding state for one image: its components, tables and the samples of the MCU row being coded.</summary>
    private sealed class Encoder<TPixel> : IDisposable
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly RowAt _rowAt;
        private readonly JpegWriterOptions _options;
        private readonly Space _space;
        private readonly bool _gray;
        private readonly bool _wide;
        private readonly bool _optimize;
        private readonly bool _keepAll;
        private readonly int _width;
        private readonly int _height;
        private readonly int _maxH;
        private readonly int _maxV;
        private readonly int _mcusX;
        private readonly int _mcusY;
        private readonly int _paddedWidth;
        private readonly Plane[] _planes;
        private readonly Workspace _main;
        private readonly int _workers;
        private readonly int[][] _divisors;
        private readonly ushort[][] _reciprocals;
        private readonly ushort[][] _quant;
        private readonly JpegHuffmanCodes[] _dc;
        private readonly JpegHuffmanCodes[] _ac;
        private readonly int[] _predictors;
        private readonly TPixel[]? _window;
        private readonly List<short[]>? _kept;
        private readonly YuvRaster<byte>? _yuv;
        private short[]? _blocks;
        private int _mcu;
        private int _restart;
        private int _rowsIn;

        internal Encoder(EncodeStart<TPixel> start, JpegWriterOptions options, YuvRaster<byte>? planes = null)
        {
            Raster<TPixel>? raster = start.Whole;
            _options = options;
            _width = start.Width;
            _height = start.Height;
            _yuv = planes;
            _space = ChooseSpace(options, planes);
            _gray = _space == Space.Gray;
            _wide = options.Precision == 12;
            if (planes is not null && _wide)
            {
                throw new ArgumentException("JPEG: 8-bit planes are written at 8 bits; leave Precision at 8.", nameof(options));
            }

            // The standard Huffman tables code 8-bit magnitudes only, so 12-bit files always get their own, as in libjpeg-turbo.
            _optimize = options.OptimizeHuffman || _wide;
            _keepAll = _optimize || options.Progressive;
            (_maxH, _maxV) = _space is Space.Gray or Space.Cmyk ? (1, 1) : planes is not null ? planes.ChromaFormat switch
            {
                ChromaFormat.Yuv444 => (1, 1),
                ChromaFormat.Yuv422 => (2, 1),
                _ => (2, 2),
            }
            : options.Subsampling switch
            {
                ChromaSubsampling.Subsample444 => (1, 1),
                ChromaSubsampling.Subsample422 => (2, 1),
                _ => (2, 2),
            };

            _mcusX = (_width + (8 * _maxH) - 1) / (8 * _maxH);
            _mcusY = (_height + (8 * _maxV) - 1) / (8 * _maxV);
            _paddedWidth = _mcusX * 8 * _maxH;
            QuantTables tables = QuantTables.For(options.Quality, _wide);

            // CMYK, like gray, uses the luma tables for every component; YCCK uses the chroma ones for Cb and Cr (libjpeg's choice).
            bool single = _space is Space.Gray or Space.Cmyk;
            int count = single ? 1 : 2;
            (_quant, _divisors, _reciprocals) = (tables.Quant[..count], tables.Divisors[..count], tables.Reciprocals[..count]);
            _dc = single ? [JpegHuffmanCodes.DcLuma] : [JpegHuffmanCodes.DcLuma, JpegHuffmanCodes.DcChroma];
            _ac = single ? [JpegHuffmanCodes.AcLuma] : [JpegHuffmanCodes.AcLuma, JpegHuffmanCodes.AcChroma];

            _main = CreateWorkspace();
            _planes = _main.Planes;
            _predictors = new int[_planes.Length];

            // The new modes that hold every quantized block count them against the memory limit (optimized baseline tables have
            // always held them unchecked, and still do).
            if (_wide || options.Progressive)
            {
                long held = (long)BlocksPerRow() * 64 * sizeof(short) * _mcusY;
                if (held > start.Context.Limits.MaxTotalBytes)
                {
                    throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), start.Context.Limits.MaxTotalBytes, held);
                }
            }

            // Large images transform MCU rows on other threads while this one Huffman-codes the rows before them. Streamed rows
            // come one MCU row at a time, so there is nothing to transform ahead.
            _workers = (raster is not null || planes is not null) && start.Context.MaxParallelism > 1 && (long)_width * _height >= ParallelPixels && _mcusY > 1
                ? Math.Min(start.Context.MaxParallelism - 1, MaxWorkers)
                : 0;
            if (raster is not null)
            {
                _rowAt = y => raster.Row(y);
            }
            else if (planes is not null)
            {
                _rowAt = _ => throw new InvalidOperationException("Planes are encoded without rows.");
            }
            else
            {
                int rows = 8 * _maxV;
                TPixel[] window = _window = ArrayPool<TPixel>.Shared.Rent(_width * rows);
                _rowAt = y => window.AsSpan((y % rows) * _width, _width);
                _kept = _keepAll ? [] : null;
            }
        }

        /// <summary>Picks the components to write from the options and the pixel type (or the planes' chroma format).</summary>
        private static Space ChooseSpace(JpegWriterOptions options, YuvRaster<byte>? planes)
        {
            if (planes is not null)
            {
                return options.ColorType switch
                {
                    JpegColorType.Cmyk or JpegColorType.Ycck => throw new ArgumentException("JPEG: YUV planes are written as YCbCr or gray, not CMYK.", nameof(options)),
                    JpegColorType.Gray => Space.Gray,
                    _ => planes.ChromaFormat == ChromaFormat.Yuv400 ? Space.Gray : Space.YCbCr,
                };
            }

            return options.ColorType switch
            {
                JpegColorType.Gray => Space.Gray,
                JpegColorType.Cmyk => Space.Cmyk,
                JpegColorType.Ycck => Space.Ycck,
                _ => TPixel.Layout.ChannelOrder switch
                {
                    PixelChannelOrder.Gray or PixelChannelOrder.GrayAlpha => Space.Gray,
                    PixelChannelOrder.Cmyk => Space.Cmyk,
                    _ => Space.YCbCr,
                },
            };
        }

        /// <summary>The component id in the frame and scans: 1, 2, 3 (and 4) as libjpeg numbers them, but C, M, Y, K for CMYK.</summary>
        private int ComponentId(int i) => _space == Space.Cmyk ? "CMYK"[i] : i + 1;

        /// <summary>JFIF APP0 for gray and YCbCr; Adobe APP14 for CMYK and YCCK, which JFIF does not cover, as libjpeg writes.</summary>
        internal void WriteIdentification(JpegBitWriter writer, Resolution? resolution)
        {
            if (_space is Space.Cmyk or Space.Ycck)
            {
                WriteAdobe(writer, _space == Space.Cmyk ? (byte)0 : (byte)2);
            }
            else
            {
                WriteJfif(writer, resolution);
            }
        }

        /// <summary>Returns image row <paramref name="y"/>.</summary>
        private delegate ReadOnlySpan<TPixel> RowAt(int y);

        /// <summary>Images with fewer pixels than this are encoded on one thread.</summary>
        private const long ParallelPixels = 1 << 20;

        /// <summary>
        /// The most threads transforming rows for the one coding them; past this the Huffman coder is the limit, and each more
        /// thread only costs memory.
        /// </summary>
        private const int MaxWorkers = 4;

        /// <summary>
        /// DQT and the frame header (SOF0 for baseline, SOF1 for 12-bit sequential, SOF2 for progressive), laid out as libjpeg does:
        /// one table per segment, 16-bit entries only for a table that needs them.
        /// </summary>
        internal void WriteHeaders(JpegBitWriter writer)
        {
            for (int t = 0; t < _quant.Length; t++)
            {
                bool sixteen = _quant[t].Any(q => q > byte.MaxValue);
                byte[] dqt = new byte[sixteen ? 129 : 65];
                dqt[0] = (byte)((sixteen ? 0x10 : 0) | t);
                for (int k = 0; k < 64; k++)
                {
                    ushort q = _quant[t][JpegTables.ZigZag[k]];
                    if (sixteen)
                    {
                        BinaryPrimitives.WriteUInt16BigEndian(dqt.AsSpan(1 + (2 * k)), q);
                    }
                    else
                    {
                        dqt[1 + k] = (byte)q;
                    }
                }

                Segment(writer, JpegMarker.Dqt, dqt);
            }

            byte[] sof = new byte[6 + (3 * _planes.Length)];
            sof[0] = (byte)(_wide ? 12 : 8);
            BinaryPrimitives.WriteUInt16BigEndian(sof.AsSpan(1), (ushort)_height);
            BinaryPrimitives.WriteUInt16BigEndian(sof.AsSpan(3), (ushort)_width);
            sof[5] = (byte)_planes.Length;
            for (int i = 0; i < _planes.Length; i++)
            {
                sof[6 + (3 * i)] = (byte)ComponentId(i);
                sof[7 + (3 * i)] = (byte)((_planes[i].H << 4) | _planes[i].V);
                sof[8 + (3 * i)] = (byte)_planes[i].Table;
            }

            Segment(writer, _options.Progressive ? JpegMarker.Sof2 : _wide ? JpegMarker.Sof1 : JpegMarker.Sof0, sof);
        }

        /// <summary>
        /// Writes DHT, DRI, SOS and the entropy-coded data, after a counting pass when tables are optimized, yielding after each MCU
        /// row; a progressive file transforms every row first (yielding after each) and then writes its scans.
        /// </summary>
        internal IEnumerable<int> WriteScan(JpegBitWriter writer)
        {
            using RowProducer rows = new(this, keepAll: _keepAll);
            if (_options.Progressive)
            {
                for (int my = 0; my < _mcusY; my++)
                {
                    rows.Get(my);
                    yield return my;
                }

                WriteProgressive(writer, rows.Get);
                yield break;
            }

            if (_optimize)
            {
                Optimize(rows.Get);
            }

            BeginScan(writer);
            for (int my = 0; my < _mcusY; my++)
            {
                CodeRow(writer, rows.Get(my));
                rows.Release(my);
                yield return my;
            }
        }

        /// <summary>Starts a streamed scan: with the standard tables its markers go out now, before any row.</summary>
        internal void StartStream(JpegBitWriter writer)
        {
            if (!_keepAll)
            {
                BeginScan(writer);
            }
        }

        /// <summary>
        /// Takes the next image row of a streamed encode. Each full MCU row (and the last, padded) is transformed, then coded, or
        /// kept for the counting pass when tables are optimized.
        /// </summary>
        internal void StreamRow(JpegBitWriter writer, ReadOnlySpan<TPixel> row)
        {
            int rows = 8 * _maxV;
            row.CopyTo(_window.AsSpan((_rowsIn % rows) * _width, _width));
            _rowsIn++;
            if (_rowsIn % rows != 0 && _rowsIn != _height)
            {
                return;
            }

            int length = BlocksPerRow() * 64;
            short[] blocks = _kept is not null ? ArrayPool<short>.Shared.Rent(length) : _blocks ??= ArrayPool<short>.Shared.Rent(length);
            TransformRow((_rowsIn - 1) / rows, blocks, _main);
            if (_kept is not null)
            {
                _kept.Add(blocks);
            }
            else
            {
                CodeRow(writer, blocks);
            }
        }

        /// <summary>
        /// Ends the scan: a streamed encode with optimized tables counts and codes its kept rows now, and a progressive one writes
        /// its scans.
        /// </summary>
        internal void EndScan(JpegBitWriter writer)
        {
            if (_kept is not null && _options.Progressive)
            {
                WriteProgressive(writer, my => _kept[my]);
            }
            else if (_kept is not null)
            {
                Optimize(my => _kept[my]);
                BeginScan(writer);
                foreach (short[] row in _kept)
                {
                    CodeRow(writer, row);
                }
            }

            writer.Align();
        }

        /// <summary>Counts every symbol of every MCU row and replaces the standard tables with optimal ones.</summary>
        private void Optimize(Func<int, short[]> rows)
        {
            Span<long> counts = stackalloc long[4 * 256];
            counts.Clear();
            Span<int> predictors = stackalloc int[_planes.Length];
            predictors.Clear();
            int mcu = 0;
            for (int my = 0; my < _mcusY; my++)
            {
                short[] row = rows(my);
                int b = 0;
                for (int mx = 0; mx < _mcusX; mx++, mcu++)
                {
                    if (RestartDue(mcu))
                    {
                        predictors.Clear();
                    }

                    for (int i = 0; i < _planes.Length; i++)
                    {
                        int t = _planes[i].Table;
                        for (int n = 0; n < _planes[i].BlocksPerMcu; n++, b++)
                        {
                            JpegBlockCoder.Count(row.AsSpan(b * 64, 64), ref predictors[i], counts.Slice(t * 512, 256), counts.Slice((t * 512) + 256, 256));
                        }
                    }
                }
            }

            for (int t = 0; t < _dc.Length; t++)
            {
                _dc[t] = JpegHuffmanCodes.Optimal(counts.Slice(t * 512, 256));
                _ac[t] = JpegHuffmanCodes.Optimal(counts.Slice((t * 512) + 256, 256));
            }
        }

        /// <summary>Writes DRI and every progressive scan from the transformed MCU rows.</summary>
        private void WriteProgressive(JpegBitWriter writer, Func<int, short[]> rows)
        {
            if (_options.RestartInterval > 0)
            {
                Span<byte> dri = stackalloc byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(dri, (ushort)_options.RestartInterval);
                Segment(writer, JpegMarker.Dri, dri);
            }

            JpegProgressiveEncoder.Component[] components = new JpegProgressiveEncoder.Component[_planes.Length];
            int offset = 0;
            for (int i = 0; i < _planes.Length; i++)
            {
                Plane p = _planes[i];
                components[i] = new(ComponentId(i), p.H, p.V, p.Table, p.BlocksWide, p.BlocksHigh, offset);
                offset += p.BlocksPerMcu;
            }

            JpegProgressiveEncoder encoder = new(components, _mcusX, _mcusY, _options.RestartInterval, rows);
            encoder.Write(writer, JpegProgressiveEncoder.Script(components.Length, _space == Space.YCbCr), Segment);
        }

        /// <summary>Writes DHT, DRI and SOS, and starts coding from the first MCU.</summary>
        private void BeginScan(JpegBitWriter writer)
        {
            for (int t = 0; t < _dc.Length; t++)
            {
                WriteHuffman(writer, 0, t, _dc[t]);
                WriteHuffman(writer, 1, t, _ac[t]);
            }

            if (_options.RestartInterval > 0)
            {
                Span<byte> dri = stackalloc byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(dri, (ushort)_options.RestartInterval);
                Segment(writer, JpegMarker.Dri, dri);
            }

            byte[] sos = new byte[1 + (2 * _planes.Length) + 3];
            sos[0] = (byte)_planes.Length;
            for (int i = 0; i < _planes.Length; i++)
            {
                sos[1 + (2 * i)] = (byte)ComponentId(i);
                sos[2 + (2 * i)] = (byte)((_planes[i].Table << 4) | _planes[i].Table);
            }

            sos[^2] = 63;
            Segment(writer, JpegMarker.Sos, sos);
            Array.Clear(_predictors);
            _mcu = 0;
            _restart = 0;
        }

        /// <summary>Huffman-codes one transformed MCU row, with a restart marker where one is due.</summary>
        private void CodeRow(JpegBitWriter writer, short[] row)
        {
            // Locals, not fields, in the loop: the coder is the slowest stage of a parallel encode.
            int[] predictors = _predictors;
            Plane[] planes = _planes;
            JpegHuffmanCodes[] dc = _dc;
            JpegHuffmanCodes[] ac = _ac;
            int mcu = _mcu;
            int b = 0;
            for (int mx = 0; mx < _mcusX; mx++, mcu++)
            {
                if (RestartDue(mcu))
                {
                    writer.Align();
                    writer.Marker((byte)(JpegMarker.Rst0 + _restart));
                    _restart = (_restart + 1) & 7;
                    Array.Clear(predictors);
                }

                for (int i = 0; i < planes.Length; i++)
                {
                    int t = planes[i].Table;
                    for (int n = 0; n < planes[i].BlocksPerMcu; n++, b++)
                    {
                        JpegBlockCoder.Encode(writer, row.AsSpan(b * 64, 64), ref predictors[i], dc[t], ac[t]);
                    }
                }
            }

            _mcu = mcu;
        }

        public void Dispose()
        {
            _main.Dispose();
            if (_window is not null)
            {
                ArrayPool<TPixel>.Shared.Return(_window);
            }

            if (_blocks is not null)
            {
                ArrayPool<short>.Shared.Return(_blocks);
            }

            foreach (short[] row in _kept ?? [])
            {
                ArrayPool<short>.Shared.Return(row);
            }
        }

        private static void WriteHuffman(JpegBitWriter writer, int tableClass, int slot, JpegHuffmanCodes codes)
        {
            byte[] dht = new byte[17 + codes.Symbols.Length];
            dht[0] = (byte)((tableClass << 4) | slot);
            codes.Counts.CopyTo(dht, 1);
            codes.Symbols.CopyTo(dht, 17);
            Segment(writer, JpegMarker.Dht, dht);
        }

        private bool RestartDue(int mcu) => _options.RestartInterval > 0 && mcu > 0 && mcu % _options.RestartInterval == 0;

        private int BlocksPerRow() => _mcusX * _planes.Sum(p => p.BlocksPerMcu);

        /// <summary>
        /// Planes and conversion buffers for one thread: luma (and YCCK's black) at the padded width, chroma averaged down, CMYK's
        /// four inks at full size.
        /// </summary>
        private Workspace CreateWorkspace()
        {
            Plane Full(int table) => new(_maxH, _maxV, table, _paddedWidth, 8 * _maxV, _width, _height, _maxH, _maxV, _wide);
            Plane Chroma() => new(1, 1, 1, _paddedWidth / _maxH, 8, _width, _height, _maxH, _maxV, _wide);
            Plane[] planes = _space switch
            {
                Space.Gray => [Full(0)],
                Space.Cmyk => [Full(0), Full(0), Full(0), Full(0)],
                Space.Ycck => [Full(0), Chroma(), Chroma(), Full(0)],
                _ => [Full(0), Chroma(), Chroma()],
            };
            return new Workspace(planes, _width, _paddedWidth * 8 * _maxV, _wide);
        }

        /// <summary>
        /// Hands transformed MCU rows to the coder in order. On one thread each row is transformed when asked for. With workers,
        /// they take rows in turn into a ring of buffers a few rows ahead of the coder, which waits for each row in order and frees
        /// its buffer once coded; for optimized tables every row is kept for the second pass instead of a ring.
        /// </summary>
        private sealed class RowProducer : IDisposable
        {
            private readonly Encoder<TPixel> _encoder;
            private readonly bool _keepAll;
            private readonly short[][] _buffers;
            private readonly byte[] _ready;
            private readonly Task[] _tasks;
            private readonly Workspace[] _workspaces;
            private int _next = -1;
            private int _nextWorkspace = -1;
            private int _transformed;
            private int _released;
            private bool _stop;
            private Exception? _failure;

            internal RowProducer(Encoder<TPixel> encoder, bool keepAll)
            {
                _encoder = encoder;
                _keepAll = keepAll;
                int rows = encoder._mcusY;
                int count = keepAll ? rows : encoder._workers == 0 ? 1 : Math.Min(rows, 4 * encoder._workers);
                int length = encoder.BlocksPerRow() * 64;
                _buffers = new short[count][];
                for (int i = 0; i < count; i++)
                {
                    _buffers[i] = ArrayPool<short>.Shared.Rent(length);
                }

                _ready = encoder._workers == 0 ? [] : new byte[rows];

                // Workspaces are rented and returned on this thread: a worker thread ends with the encode, and arrays returned
                // on it would stay in its thread-local pool cache and be lost.
                _workspaces = new Workspace[encoder._workers];
                for (int i = 0; i < _workspaces.Length; i++)
                {
                    _workspaces[i] = encoder.CreateWorkspace();
                }

                _tasks = new Task[encoder._workers];
                for (int i = 0; i < _tasks.Length; i++)
                {
                    _tasks[i] = Task.Factory.StartNew(Work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                }
            }

            /// <summary>Returns MCU row <paramref name="my"/>, transformed; rows are asked for in order.</summary>
            internal short[] Get(int my)
            {
                short[] buffer = _buffers[my % _buffers.Length];
                if (_tasks.Length == 0)
                {
                    // Kept rows are transformed on the first pass, while the counting that follows still finds them in cache.
                    if (!_keepAll || my >= _transformed)
                    {
                        _encoder.TransformRow(my, buffer, _encoder._main);
                        _transformed = my + 1;
                    }

                    return buffer;
                }

                SpinWait wait = default;
                while (Volatile.Read(ref _ready[my]) == 0)
                {
                    if (Volatile.Read(ref _failure) is Exception failure)
                    {
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
                    }

                    wait.SpinOnce(sleep1Threshold: -1);
                }

                return buffer;
            }

            /// <summary>Says row <paramref name="my"/> is coded, so its buffer can take a later row.</summary>
            internal void Release(int my)
            {
                Volatile.Write(ref _released, my + 1);
            }

            public void Dispose()
            {
                Volatile.Write(ref _stop, true);
                foreach (Task task in _tasks)
                {
                    try
                    {
                        task.Wait();
                    }
                    catch (AggregateException)
                    {
                        // Reported through Get.
                    }
                }

                foreach (short[] buffer in _buffers)
                {
                    ArrayPool<short>.Shared.Return(buffer);
                }

                foreach (Workspace workspace in _workspaces)
                {
                    workspace.Dispose();
                }
            }

            private void Work()
            {
                Workspace workspace = _workspaces[Interlocked.Increment(ref _nextWorkspace)];
                try
                {
                    while (!Volatile.Read(ref _stop))
                    {
                        int my = Interlocked.Increment(ref _next);
                        if (my >= _encoder._mcusY)
                        {
                            return;
                        }

                        // A ring buffer is free once the row that used it before is coded.
                        SpinWait wait = default;
                        while (!_keepAll && my - Volatile.Read(ref _released) >= _buffers.Length)
                        {
                            if (Volatile.Read(ref _stop))
                            {
                                return;
                            }

                            wait.SpinOnce(sleep1Threshold: -1);
                        }

                        _encoder.TransformRow(my, _buffers[my % _buffers.Length], workspace);
                        Volatile.Write(ref _ready[my], 1);
                    }
                }
                catch (Exception e)
                {
                    Interlocked.CompareExchange(ref _failure, e, null);
                }
            }
        }

        /// <summary>Converts MCU row <paramref name="my"/> and codes its blocks into <paramref name="blocks"/>, in MCU order, each in natural order.</summary>
        private void TransformRow(int my, short[] blocks, Workspace workspace)
        {
            int rows = 8 * _maxV;
            ConvertRows(my * rows, rows, workspace);
            Plane[] planes = workspace.Planes;
            bool vector = Avx2.IsSupported;
            int b = 0;
            for (int mx = 0; mx < _mcusX; mx++)
            {
                for (int i = 0; i < planes.Length; i++)
                {
                    Plane p = planes[i];
                    int[] divisors = _divisors[p.Table];
                    ushort[] reciprocals = _reciprocals[p.Table];
                    for (int v = 0; v < p.V; v++)
                    {
                        for (int h = 0; h < p.H; h++, b++)
                        {
                            Span<short> block = blocks.AsSpan(b * 64, 64);
                            int bx = (mx * p.H) + h;
                            int by = (my * p.V) + v;
                            if (bx < p.BlocksWide && by < p.BlocksHigh)
                            {
                                if (_wide)
                                {
                                    JpegFdct12.Forward(p.Wide.AsSpan((v * 8 * p.Stride) + (bx * 8)), p.Stride, _quant[p.Table], block);
                                    continue;
                                }

                                ReadOnlySpan<byte> samples = p.Samples.AsSpan((v * 8 * p.Stride) + (bx * 8));
                                if (vector)
                                {
                                    JpegFdct.ForwardAvx2(samples, p.Stride, reciprocals, block);
                                }
                                else
                                {
                                    JpegFdct.Forward(samples, p.Stride, divisors, block);
                                }
                            }
                            else
                            {
                                // Padding blocks outside the image (libjpeg's dummy blocks): flat, with the DC of the block before.
                                block.Clear();
                                block[0] = blocks[(b - 1) * 64];
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Converts image rows <paramref name="first"/> to <paramref name="first"/> + <paramref name="count"/> into the component
        /// planes, repeating the last row and column of the image to fill whole MCUs, and averages chroma down.
        /// </summary>
        private void ConvertRows(int first, int count, Workspace workspace)
        {
            if (_yuv is not null)
            {
                CopyPlanes(_yuv, first, count, workspace);
                return;
            }

            if (_wide)
            {
                ConvertRows12(first, count, workspace);
                return;
            }

            Plane[] planes = workspace.Planes;
            Plane luma = planes[0];
            bool subsampled = !_gray && (_maxH > 1 || _maxV > 1);
            for (int r = 0; r < count; r++)
            {
                int y = Math.Min(first + r, _height - 1);
                Span<byte> lumaRow = luma.Samples.AsSpan(r * luma.Stride, _paddedWidth);
                if (_gray && _options.Background is null && TPixel.Layout.ChannelOrder is PixelChannelOrder.Gray or PixelChannelOrder.GrayAlpha)
                {
                    PixelConvert.Convert<TPixel, Gray8>(_rowAt(y), MemoryMarshal.Cast<byte, Gray8>(lumaRow[.._width]));
                    lumaRow[_width..].Fill(lumaRow[_width - 1]);
                    continue;
                }

                if (_space is Space.Cmyk or Space.Ycck)
                {
                    InkRow(y, r, workspace, subsampled);
                    continue;
                }

                ReadOnlySpan<byte> rgb = RgbRow(y, workspace);
                if (_gray)
                {
                    JpegEncodeKernels.Luma(rgb, lumaRow[.._width]);
                    lumaRow[_width..].Fill(lumaRow[_width - 1]);
                    continue;
                }

                // Chroma goes straight to its plane at full sampling, or to full-size rows to be averaged down.
                Span<byte> cb = subsampled ? workspace.FullCb.AsSpan(r * _paddedWidth, _paddedWidth) : planes[1].Samples.AsSpan(r * planes[1].Stride, _paddedWidth);
                Span<byte> cr = subsampled ? workspace.FullCr.AsSpan(r * _paddedWidth, _paddedWidth) : planes[2].Samples.AsSpan(r * planes[2].Stride, _paddedWidth);
                JpegEncodeKernels.YCbCr(rgb, lumaRow[.._width], cb[.._width], cr[.._width]);
                lumaRow[_width..].Fill(lumaRow[_width - 1]);
                cb[_width..].Fill(cb[_width - 1]);
                cr[_width..].Fill(cr[_width - 1]);
            }

            if (subsampled)
            {
                Downsample(workspace.FullCb, first, count, planes[1]);
                Downsample(workspace.FullCr, first, count, planes[2]);
            }
        }

        /// <summary>
        /// Converts image row <paramref name="y"/> to ink and writes it as row <paramref name="r"/> of the planes: CMYK as the four
        /// inks inverted, as Adobe stores them; YCCK as libjpeg converts those stored values, their cyan, magenta and yellow inverted
        /// again (which gives back the inks) taken for RGB and converted to YCbCr, with the black as stored.
        /// </summary>
        private void InkRow(int y, int r, Workspace workspace, bool subsampled)
        {
            Plane[] planes = workspace.Planes;
            Span<byte> ink = workspace.Rgba.AsSpan(0, _width * 4);
            if (_options.Background is null)
            {
                PixelConvert.Convert(_rowAt(y), MemoryMarshal.Cast<byte, Cmyk8>(ink));
            }
            else
            {
                PixelConvert.Convert(MemoryMarshal.Cast<byte, Rgb8>(RgbRow(y, workspace)), MemoryMarshal.Cast<byte, Cmyk8>(ink));
            }

            int inks = _space == Space.Cmyk ? 4 : 0;
            for (int i = 0; i < inks; i++)
            {
                Span<byte> row = planes[i].Samples.AsSpan(r * planes[i].Stride, _paddedWidth);
                for (int x = 0; x < _width; x++)
                {
                    row[x] = (byte)~ink[(4 * x) + i];
                }

                Pad(row, _width);
            }

            if (_space == Space.Cmyk)
            {
                return;
            }

            Span<byte> rgb = workspace.Rgb.AsSpan(0, _width * 3);
            Span<byte> black = planes[3].Samples.AsSpan(r * planes[3].Stride, _paddedWidth);
            for (int x = 0; x < _width; x++)
            {
                rgb[3 * x] = ink[4 * x];
                rgb[(3 * x) + 1] = ink[(4 * x) + 1];
                rgb[(3 * x) + 2] = ink[(4 * x) + 2];
                black[x] = (byte)~ink[(4 * x) + 3];
            }

            Span<byte> luma = planes[0].Samples.AsSpan(r * planes[0].Stride, _paddedWidth);
            Span<byte> cb = subsampled ? workspace.FullCb.AsSpan(r * _paddedWidth, _paddedWidth) : planes[1].Samples.AsSpan(r * planes[1].Stride, _paddedWidth);
            Span<byte> cr = subsampled ? workspace.FullCr.AsSpan(r * _paddedWidth, _paddedWidth) : planes[2].Samples.AsSpan(r * planes[2].Stride, _paddedWidth);
            JpegEncodeKernels.YCbCr(rgb, luma[.._width], cb[.._width], cr[.._width]);
            Pad(luma, _width);
            Pad(cb, _width);
            Pad(cr, _width);
            Pad(black, _width);
        }

        /// <summary>
        /// <see cref="ConvertRows"/> for 12-bit output: rows go through 16-bit pixels (gray, RGB or CMYK), are narrowed to 12 bits and
        /// converted as the 8-bit path converts them, in 12-bit arithmetic.
        /// </summary>
        private void ConvertRows12(int first, int count, Workspace workspace)
        {
            Plane[] planes = workspace.Planes;
            bool subsampled = _space is Space.YCbCr or Space.Ycck && (_maxH > 1 || _maxV > 1);
            for (int r = 0; r < count; r++)
            {
                int y = Math.Min(first + r, _height - 1);
                Span<ushort> luma = planes[0].Wide.AsSpan(r * planes[0].Stride, _paddedWidth);
                Span<ushort> cb = default, cr = default;
                if (_space is Space.YCbCr or Space.Ycck)
                {
                    cb = subsampled ? workspace.WideCb.AsSpan(r * _paddedWidth, _paddedWidth) : planes[1].Wide.AsSpan(r * planes[1].Stride, _paddedWidth);
                    cr = subsampled ? workspace.WideCr.AsSpan(r * _paddedWidth, _paddedWidth) : planes[2].Wide.AsSpan(r * planes[2].Stride, _paddedWidth);
                }

                switch (_space)
                {
                    case Space.Gray when _options.Background is null && TPixel.Layout.ChannelOrder is PixelChannelOrder.Gray or PixelChannelOrder.GrayAlpha:
                        PixelConvert.Convert(_rowAt(y), MemoryMarshal.Cast<ushort, Gray16>(luma[.._width]));
                        JpegEncodeKernels.To12(luma[.._width]);
                        break;
                    case Space.Gray or Space.YCbCr:
                        Span<ushort> rgb = MemoryMarshal.Cast<Rgb16, ushort>(Rgb16Row(y, workspace));
                        JpegEncodeKernels.To12(rgb);
                        JpegEncodeKernels.YCbCr12(rgb, luma[.._width], _gray ? default : cb[.._width], _gray ? default : cr[.._width]);
                        break;
                    default:
                        Span<ushort> ink = workspace.Wide4.AsSpan(0, _width * 4);
                        if (_options.Background is null)
                        {
                            PixelConvert.Convert(_rowAt(y), MemoryMarshal.Cast<ushort, Cmyk16>(ink));
                        }
                        else
                        {
                            PixelConvert.Convert<Rgb16, Cmyk16>(Rgb16Row(y, workspace), MemoryMarshal.Cast<ushort, Cmyk16>(ink));
                        }

                        JpegEncodeKernels.To12(ink);
                        InkRow12(ink, r, planes, luma, cb, cr, workspace);
                        break;
                }

                Pad(luma, _width);
                if (!cb.IsEmpty)
                {
                    Pad(cb, _width);
                    Pad(cr, _width);
                }
            }

            if (subsampled)
            {
                Downsample12(workspace.WideCb, first, count, planes[1]);
                Downsample12(workspace.WideCr, first, count, planes[2]);
            }
        }

        /// <summary>Writes 12-bit inks to the planes, as <see cref="InkRow"/> does at 8 bits; luma, Cb and Cr are YCCK's.</summary>
        private void InkRow12(ReadOnlySpan<ushort> ink, int r, Plane[] planes, Span<ushort> luma, Span<ushort> cb, Span<ushort> cr, Workspace workspace)
        {
            const int Max = 4095;
            if (_space == Space.Cmyk)
            {
                for (int i = 0; i < 4; i++)
                {
                    Span<ushort> row = i == 0 ? luma : planes[i].Wide.AsSpan(r * planes[i].Stride, _paddedWidth);
                    for (int x = 0; x < _width; x++)
                    {
                        row[x] = (ushort)(Max - ink[(4 * x) + i]);
                    }

                    if (i > 0)
                    {
                        Pad(row, _width);
                    }
                }

                return;
            }

            Span<ushort> rgb = workspace.Wide3.AsSpan(0, _width * 3);
            Span<ushort> black = planes[3].Wide.AsSpan(r * planes[3].Stride, _paddedWidth);
            for (int x = 0; x < _width; x++)
            {
                rgb[3 * x] = ink[4 * x];
                rgb[(3 * x) + 1] = ink[(4 * x) + 1];
                rgb[(3 * x) + 2] = ink[(4 * x) + 2];
                black[x] = (ushort)(Max - ink[(4 * x) + 3]);
            }

            JpegEncodeKernels.YCbCr12(rgb, luma[.._width], cb[.._width], cr[.._width]);
            Pad(black, _width);
        }

        /// <summary>
        /// Returns row <paramref name="y"/> as 16-bit RGB in the workspace, composited onto <see cref="JpegWriterOptions.Background"/>
        /// when there is one.
        /// </summary>
        private Span<Rgb16> Rgb16Row(int y, Workspace workspace)
        {
            Span<Rgb16> rgb = MemoryMarshal.Cast<ushort, Rgb16>(workspace.Wide3.AsSpan(0, _width * 3));
            if (_options.Background is not Rgba8 background)
            {
                PixelConvert.Convert(_rowAt(y), rgb);
                return rgb;
            }

            Span<Rgba16> rgba = MemoryMarshal.Cast<ushort, Rgba16>(workspace.Wide4.AsSpan(0, _width * 4));
            PixelConvert.Convert(_rowAt(y), rgba);
            int br = background.R * 257, bg = background.G * 257, bb = background.B * 257;
            for (int x = 0; x < _width; x++)
            {
                Rgba16 p = rgba[x];
                long a = p.A, rest = 65535 - p.A;
                rgb[x] = new Rgb16(
                    (ushort)(((p.R * a) + (br * rest) + 32767) / 65535),
                    (ushort)(((p.G * a) + (bg * rest) + 32767) / 65535),
                    (ushort)(((p.B * a) + (bb * rest) + 32767) / 65535));
            }

            return rgb;
        }

        /// <summary><see cref="Downsample"/> for 12-bit samples.</summary>
        private void Downsample12(ushort[] full, int first, int rows, Plane plane)
        {
            int factorY = _maxV;
            int width = _paddedWidth / _maxH;
            int last = ((_height + factorY - 1) / factorY) - 1 - (first / factorY);
            for (int r = 0; r < rows / factorY; r++)
            {
                Span<ushort> output = plane.Wide.AsSpan(r * plane.Stride, width);
                if (r > last)
                {
                    plane.Wide.AsSpan(last * plane.Stride, width).CopyTo(output);
                    continue;
                }

                ReadOnlySpan<ushort> row0 = full.AsSpan(r * factorY * _paddedWidth, _paddedWidth);
                ReadOnlySpan<ushort> row1 = factorY == 2 ? full.AsSpan(((r * 2) + 1) * _paddedWidth, _paddedWidth) : row0;
                JpegEncodeKernels.Downsample12(row0, row1, factorY == 2, output);
            }
        }

        /// <summary>Repeats the last sample of the image across the padding to the MCU edge.</summary>
        private static void Pad<T>(Span<T> row, int width)
        {
            row[width..].Fill(row[width - 1]);
        }

        /// <summary>
        /// Copies the image rows <paramref name="first"/> to <paramref name="first"/> + <paramref name="count"/> of each plane, and
        /// the chroma rows they cover, into the component planes, repeating the last sample of each row and the last row of each
        /// plane to fill whole MCUs, as libjpeg pads raw data.
        /// </summary>
        private void CopyPlanes(YuvRaster<byte> source, int first, int count, Workspace workspace)
        {
            Plane[] planes = workspace.Planes;
            for (int i = 0; i < planes.Length; i++)
            {
                YuvPlane<byte> plane = i == 0 ? source.Y : i == 1 ? source.U! : source.V!;
                int factorY = i == 0 ? 1 : _maxV, width = i == 0 ? _paddedWidth : _paddedWidth / _maxH;
                for (int r = 0; r < count / factorY; r++)
                {
                    Span<byte> output = planes[i].Samples.AsSpan(r * planes[i].Stride, width);
                    ReadOnlySpan<byte> row = plane.Row(Math.Min((first / factorY) + r, plane.Height - 1));
                    row.CopyTo(output);
                    output[row.Length..].Fill(row[^1]);
                }
            }
        }

        /// <summary>Returns row <paramref name="y"/> as packed RGB bytes: the raster's own row when it is Rgb8, else a converted copy.</summary>
        private ReadOnlySpan<byte> RgbRow(int y, Workspace workspace)
        {
            byte[] converted = workspace.Rgb;
            ReadOnlySpan<TPixel> source = _rowAt(y);
            if (_options.Background is Rgba8 background)
            {
                Span<Rgba8> rgba = MemoryMarshal.Cast<byte, Rgba8>(workspace.Rgba.AsSpan(0, _width * 4));
                PixelConvert.Convert(source, rgba);
                Operations.Compose.FlattenKernels.Flatten8(rgba, background);
                Span<Rgb8> composited = MemoryMarshal.Cast<byte, Rgb8>(converted.AsSpan(0, _width * 3));
                PixelConvert.Convert<Rgba8, Rgb8>(rgba, composited);
                return converted.AsSpan(0, _width * 3);
            }

            if (typeof(TPixel) == typeof(Rgb8))
            {
                return MemoryMarshal.AsBytes(source);
            }

            PixelConvert.Convert(source, MemoryMarshal.Cast<byte, Rgb8>(converted.AsSpan(0, _width * 3)));
            return converted.AsSpan(0, _width * 3);
        }

        /// <summary>
        /// Averages full-size chroma rows down with libjpeg's alternating rounding bias. Below the image, as libjpeg does, the last
        /// averaged row repeats, rather than an average of repeated image rows.
        /// </summary>
        private void Downsample(byte[] full, int first, int rows, Plane plane)
        {
            int factorY = _maxV;
            int width = _paddedWidth / _maxH;
            int last = ((_height + factorY - 1) / factorY) - 1 - (first / factorY);
            for (int r = 0; r < rows / factorY; r++)
            {
                Span<byte> output = plane.Samples.AsSpan(r * plane.Stride, width);
                if (r > last)
                {
                    plane.Samples.AsSpan(last * plane.Stride, width).CopyTo(output);
                    continue;
                }

                ReadOnlySpan<byte> row0 = full.AsSpan(r * factorY * _paddedWidth, _paddedWidth);
                ReadOnlySpan<byte> row1 = factorY == 2 ? full.AsSpan(((r * 2) + 1) * _paddedWidth, _paddedWidth) : row0;
                JpegEncodeKernels.Downsample(row0, row1, factorY == 2, output);
            }
        }
    }

    /// <summary>One thread's planes and row buffers for converting MCU rows; the 16-bit ones only for 12-bit output.</summary>
    private sealed class Workspace : IDisposable
    {
        internal Workspace(Plane[] planes, int width, int fullChroma, bool wide)
        {
            Planes = planes;
            Rgb = ArrayPool<byte>.Shared.Rent(width * 3);
            Rgba = ArrayPool<byte>.Shared.Rent(width * 4);
            FullCb = ArrayPool<byte>.Shared.Rent(fullChroma);
            FullCr = ArrayPool<byte>.Shared.Rent(fullChroma);
            if (wide)
            {
                Wide3 = ArrayPool<ushort>.Shared.Rent(width * 3);
                Wide4 = ArrayPool<ushort>.Shared.Rent(width * 4);
                WideCb = ArrayPool<ushort>.Shared.Rent(fullChroma);
                WideCr = ArrayPool<ushort>.Shared.Rent(fullChroma);
            }
        }

        internal Plane[] Planes { get; }

        internal byte[] Rgb { get; private set; }

        internal byte[] Rgba { get; private set; }

        internal byte[] FullCb { get; private set; }

        internal byte[] FullCr { get; private set; }

        internal ushort[] Wide3 { get; private set; } = [];

        internal ushort[] Wide4 { get; private set; } = [];

        internal ushort[] WideCb { get; private set; } = [];

        internal ushort[] WideCr { get; private set; } = [];

        public void Dispose()
        {
            foreach (Plane plane in Planes)
            {
                plane.Dispose();
            }

            ArrayPool<byte>.Shared.Return(Rgb);
            ArrayPool<byte>.Shared.Return(Rgba);
            ArrayPool<byte>.Shared.Return(FullCb);
            ArrayPool<byte>.Shared.Return(FullCr);
            (Rgb, Rgba, FullCb, FullCr) = ([], [], [], []);
            foreach (ushort[] buffer in (ushort[][])[Wide3, Wide4, WideCb, WideCr])
            {
                if (buffer.Length > 0)
                {
                    ArrayPool<ushort>.Shared.Return(buffer);
                }
            }

            (Wide3, Wide4, WideCb, WideCr) = ([], [], [], []);
        }
    }

    /// <summary>
    /// One component: its sampling, table, samples for one MCU row (bytes, or 16-bit for 12-bit output), and how many of its
    /// blocks are inside the image.
    /// </summary>
    private sealed class Plane : IDisposable
    {
        internal Plane(int h, int v, int table, int stride, int rows, int width, int height, int maxH, int maxV, bool wide)
        {
            H = h;
            V = v;
            Table = table;
            Stride = stride;
            if (wide)
            {
                Wide = ArrayPool<ushort>.Shared.Rent(stride * rows);
            }
            else
            {
                Samples = ArrayPool<byte>.Shared.Rent(stride * rows);
            }

            // libjpeg's component size: the image scaled by the sampling ratio, rounded up, in whole blocks.
            BlocksWide = (int)(((((long)width * h) + maxH - 1) / maxH) + 7) / 8;
            BlocksHigh = (int)(((((long)height * v) + maxV - 1) / maxV) + 7) / 8;
        }

        internal int H { get; }

        internal int V { get; }

        internal int Table { get; }

        internal int Stride { get; }

        internal byte[] Samples { get; private set; } = [];

        internal ushort[] Wide { get; private set; } = [];

        internal int BlocksWide { get; }

        internal int BlocksHigh { get; }

        internal int BlocksPerMcu => H * V;

        public void Dispose()
        {
            if (Samples.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(Samples);
            }

            if (Wide.Length > 0)
            {
                ArrayPool<ushort>.Shared.Return(Wide);
            }

            (Samples, Wide) = ([], []);
        }
    }
}
