using System.Buffers;
using System.Buffers.Binary;
using System.Text;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Jpeg;

/// <summary>
/// Writes baseline JFIF files (T.81 annex F, sequential Huffman coding, 8-bit), one MCU row at a time: rows are converted to
/// YCbCr (or gray) the way <c>cjpeg</c> does, chroma is averaged down, and each block goes through the forward DCT, quantization
/// and the Huffman coder. With optimized tables the quantized blocks are kept for a second, coding pass. Only the first frame is
/// written.
/// </summary>
internal sealed class JpegWriter : IFormatWriter
{
    internal static readonly JpegWriter Instance = new();

    private const int ScaleBits = 16;
    private const int Half = 1 << (ScaleBits - 1);
    private const int CbCrOffset = 128 << ScaleBits;

    // libjpeg's fixed-point RGB to YCbCr coefficients (JFIF, full range BT.601).
    private static readonly int RY = Fix(0.29900);
    private static readonly int GY = Fix(0.58700);
    private static readonly int BY = Fix(0.11400);
    private static readonly int RCb = -Fix(0.16874);
    private static readonly int GCb = -Fix(0.33126);
    private static readonly int BCbRCr = Fix(0.5);
    private static readonly int GCr = -Fix(0.41869);
    private static readonly int BCr = -Fix(0.08131);

    private static ReadOnlySpan<byte> JfifId => "JFIF\0"u8;

    private static ReadOnlySpan<byte> ExifId => "Exif\0\0"u8;

    private static ReadOnlySpan<byte> XmpId => "http://ns.adobe.com/xap/1.0/\0"u8;

    private static ReadOnlySpan<byte> ExtendedXmpId => "http://ns.adobe.com/xmp/extension/\0"u8;

    private static ReadOnlySpan<byte> IccId => "ICC_PROFILE\0"u8;

    public void Write<TPixel>(Raster<TPixel> raster, Stream output, WriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentNullException.ThrowIfNull(output);
        JpegWriterOptions jpeg = options as JpegWriterOptions ?? new JpegWriterOptions { StripMetadata = options.StripMetadata };
        if (raster.Width > ushort.MaxValue || raster.Height > ushort.MaxValue)
        {
            throw new InvalidOperationException($"JPEG holds at most 65,535 × 65,535 pixels; the image is {raster.Width} × {raster.Height}.");
        }

        using Encoder<TPixel> encoder = new(raster, jpeg);
        using JpegBitWriter writer = new(output);
        writer.Marker(JpegMarker.Soi);
        WriteJfif(writer, jpeg.StripMetadata ? null : raster.Metadata.Resolution);
        if (!jpeg.StripMetadata)
        {
            WriteMetadata(writer, raster.Metadata);
        }

        encoder.WriteHeaders(writer);
        encoder.WriteScan(writer);
        writer.Marker(JpegMarker.Eoi);
        writer.Flush();
    }

    /// <summary>Returns a coefficient scaled by 2^16 and rounded, as libjpeg's FIX does.</summary>
    private static int Fix(double value) => (int)((value * (1 << ScaleBits)) + 0.5);

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

    /// <summary>EXIF (APP1), ICC (APP2 chunks), XMP (APP1, with extended XMP when large) and comments, in that order.</summary>
    private static void WriteMetadata(JpegBitWriter writer, RasterMetadata metadata)
    {
        // An EXIF block too large for one segment cannot be written; it is left out.
        ReadOnlySpan<byte> exif = metadata.Exif.Span;
        if (!exif.IsEmpty && exif.Length <= ushort.MaxValue - 2 - ExifId.Length)
        {
            Segment(writer, JpegMarker.App1, ExifId, exif);
        }

        ReadOnlySpan<byte> icc = metadata.IccProfile.Span;
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

        WriteXmp(writer, metadata.Xmp.Span, metadata.FormatDetails is JpegDetails details ? details.ExtendedXmp.Span : default);

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

    /// <summary>Returns the quantization table for a quality, scaled from <paramref name="example"/> with the IJG formula.</summary>
    private static ushort[] Scale(ReadOnlySpan<byte> example, int quality)
    {
        int factor = quality < 50 ? 5000 / quality : 200 - (2 * quality);
        ushort[] table = new ushort[64];
        for (int i = 0; i < 64; i++)
        {
            // Values above 255 would need 16-bit tables, which baseline does not allow.
            table[i] = (ushort)Math.Clamp(((example[i] * factor) + 50) / 100, 1, 255);
        }

        return table;
    }

    /// <summary>The encoding state for one image: its components, tables and the samples of the MCU row being coded.</summary>
    private sealed class Encoder<TPixel> : IDisposable
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly Raster<TPixel> _raster;
        private readonly JpegWriterOptions _options;
        private readonly bool _gray;
        private readonly int _width;
        private readonly int _height;
        private readonly int _maxH;
        private readonly int _maxV;
        private readonly int _mcusX;
        private readonly int _mcusY;
        private readonly int _paddedWidth;
        private readonly Plane[] _planes;
        private readonly int[][] _divisors;
        private readonly ushort[][] _quant;
        private readonly JpegHuffmanCodes[] _dc;
        private readonly JpegHuffmanCodes[] _ac;
        private byte[] _pixels;

        internal Encoder(Raster<TPixel> raster, JpegWriterOptions options)
        {
            _raster = raster;
            _options = options;
            _width = raster.Width;
            _height = raster.Height;
            _gray = options.ColorType == JpegColorType.Gray || TPixel.Layout.ChannelOrder is PixelChannelOrder.Gray or PixelChannelOrder.GrayAlpha;
            (_maxH, _maxV) = _gray ? (1, 1) : options.Subsampling switch
            {
                ChromaSubsampling.Full => (1, 1),
                ChromaSubsampling.HalfWidth => (2, 1),
                _ => (2, 2),
            };

            _mcusX = (_width + (8 * _maxH) - 1) / (8 * _maxH);
            _mcusY = (_height + (8 * _maxV) - 1) / (8 * _maxV);
            _paddedWidth = _mcusX * 8 * _maxH;
            _quant = _gray
                ? [Scale(JpegTables.LumaQuant, options.Quality)]
                : [Scale(JpegTables.LumaQuant, options.Quality), Scale(JpegTables.ChromaQuant, options.Quality)];
            _divisors = [.. _quant.Select(q => q.Select(v => v * 8).ToArray())];
            _dc = _gray ? [JpegHuffmanCodes.DcLuma] : [JpegHuffmanCodes.DcLuma, JpegHuffmanCodes.DcChroma];
            _ac = _gray ? [JpegHuffmanCodes.AcLuma] : [JpegHuffmanCodes.AcLuma, JpegHuffmanCodes.AcChroma];

            // Luma at the full padded size; chroma averaged down. Real blocks per component follow libjpeg's component sizes.
            _planes = _gray
                ? [new Plane(1, 1, 0, _paddedWidth, 8, _width, _height, _maxH, _maxV)]
                : [
                    new Plane(_maxH, _maxV, 0, _paddedWidth, 8 * _maxV, _width, _height, _maxH, _maxV),
                    new Plane(1, 1, 1, _paddedWidth / _maxH, 8, _width, _height, _maxH, _maxV),
                    new Plane(1, 1, 1, _paddedWidth / _maxH, 8, _width, _height, _maxH, _maxV),
                ];
            _pixels = ArrayPool<byte>.Shared.Rent(_paddedWidth * 8 * _maxV * (_gray ? 1 : 3));
        }

        /// <summary>DQT, SOF0, DHT and DRI, laid out as libjpeg does: one table per segment.</summary>
        internal void WriteHeaders(JpegBitWriter writer)
        {
            for (int t = 0; t < _quant.Length; t++)
            {
                byte[] dqt = new byte[65];
                dqt[0] = (byte)t;
                for (int k = 0; k < 64; k++)
                {
                    dqt[1 + k] = (byte)_quant[t][JpegTables.ZigZag[k]];
                }

                Segment(writer, JpegMarker.Dqt, dqt);
            }

            byte[] sof = new byte[6 + (3 * _planes.Length)];
            sof[0] = 8;
            BinaryPrimitives.WriteUInt16BigEndian(sof.AsSpan(1), (ushort)_height);
            BinaryPrimitives.WriteUInt16BigEndian(sof.AsSpan(3), (ushort)_width);
            sof[5] = (byte)_planes.Length;
            for (int i = 0; i < _planes.Length; i++)
            {
                sof[6 + (3 * i)] = (byte)(i + 1);
                sof[7 + (3 * i)] = (byte)((_planes[i].H << 4) | _planes[i].V);
                sof[8 + (3 * i)] = (byte)_planes[i].Table;
            }

            Segment(writer, JpegMarker.Sof0, sof);
        }

        /// <summary>Writes DHT, DRI, SOS and the entropy-coded data, after a counting pass when tables are optimized.</summary>
        internal void WriteScan(JpegBitWriter writer)
        {
            List<short[]>? stored = null;
            if (_options.OptimizeHuffman)
            {
                stored = [];
                long[][] dcCounts = [new long[256], new long[256]];
                long[][] acCounts = [new long[256], new long[256]];
                int[] predictors = new int[_planes.Length];
                int mcu = 0;
                for (int my = 0; my < _mcusY; my++)
                {
                    short[] row = ArrayPool<short>.Shared.Rent(BlocksPerRow() * 64);
                    stored.Add(row);
                    TransformRow(my, row);
                    int b = 0;
                    for (int mx = 0; mx < _mcusX; mx++, mcu++)
                    {
                        if (RestartDue(mcu))
                        {
                            Array.Clear(predictors);
                        }

                        for (int i = 0; i < _planes.Length; i++)
                        {
                            int t = _planes[i].Table;
                            for (int n = 0; n < _planes[i].BlocksPerMcu; n++, b++)
                            {
                                JpegBlockCoder.Count(row.AsSpan(b * 64, 64), ref predictors[i], dcCounts[t], acCounts[t]);
                            }
                        }
                    }
                }

                for (int t = 0; t < _dc.Length; t++)
                {
                    _dc[t] = JpegHuffmanCodes.Optimal(dcCounts[t]);
                    _ac[t] = JpegHuffmanCodes.Optimal(acCounts[t]);
                }
            }

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
                sos[1 + (2 * i)] = (byte)(i + 1);
                sos[2 + (2 * i)] = (byte)((_planes[i].Table << 4) | _planes[i].Table);
            }

            sos[^2] = 63;
            Segment(writer, JpegMarker.Sos, sos);

            short[] current = stored is null ? ArrayPool<short>.Shared.Rent(BlocksPerRow() * 64) : [];
            try
            {
                int[] predictors = new int[_planes.Length];
                int mcu = 0;
                int restart = 0;
                for (int my = 0; my < _mcusY; my++)
                {
                    short[] row = stored is null ? current : stored[my];
                    if (stored is null)
                    {
                        TransformRow(my, row);
                    }

                    int b = 0;
                    for (int mx = 0; mx < _mcusX; mx++, mcu++)
                    {
                        if (RestartDue(mcu))
                        {
                            writer.Align();
                            writer.Marker((byte)(JpegMarker.Rst0 + restart));
                            restart = (restart + 1) & 7;
                            Array.Clear(predictors);
                        }

                        for (int i = 0; i < _planes.Length; i++)
                        {
                            int t = _planes[i].Table;
                            for (int n = 0; n < _planes[i].BlocksPerMcu; n++, b++)
                            {
                                JpegBlockCoder.Encode(writer, row.AsSpan(b * 64, 64), ref predictors[i], _dc[t], _ac[t]);
                            }
                        }
                    }
                }

                writer.Align();
            }
            finally
            {
                if (stored is null)
                {
                    ArrayPool<short>.Shared.Return(current);
                }
                else
                {
                    foreach (short[] row in stored)
                    {
                        ArrayPool<short>.Shared.Return(row);
                    }
                }
            }
        }

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(Interlocked.Exchange(ref _pixels, null!));
            foreach (Plane plane in _planes)
            {
                plane.Dispose();
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

        /// <summary>Converts MCU row <paramref name="my"/> and codes its blocks into <paramref name="blocks"/>, in MCU order, zigzag.</summary>
        private void TransformRow(int my, short[] blocks)
        {
            int rows = 8 * _maxV;
            ConvertRows(my * rows, rows);
            int b = 0;
            for (int mx = 0; mx < _mcusX; mx++)
            {
                for (int i = 0; i < _planes.Length; i++)
                {
                    Plane p = _planes[i];
                    int[] divisors = _divisors[p.Table];
                    for (int v = 0; v < p.V; v++)
                    {
                        for (int h = 0; h < p.H; h++, b++)
                        {
                            Span<short> block = blocks.AsSpan(b * 64, 64);
                            int bx = (mx * p.H) + h;
                            int by = (my * p.V) + v;
                            if (bx < p.BlocksWide && by < p.BlocksHigh)
                            {
                                JpegFdct.Forward(p.Samples.AsSpan((v * 8 * p.Stride) + (bx * 8)), p.Stride, divisors, block);
                                for (int k = 1; k < 64; k++)
                                {
                                    // Baseline AC tables code at most 10 bits of magnitude.
                                    block[k] = Math.Clamp(block[k], (short)-1023, (short)1023);
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
        private void ConvertRows(int first, int count)
        {
            int channels = _gray ? 1 : 3;
            Span<byte> pixels = _pixels.AsSpan(0, _paddedWidth * count * channels);
            Span<Rgba8> rgba = _options.Background is null ? default : new Rgba8[_width];
            for (int r = 0; r < count; r++)
            {
                int y = Math.Min(first + r, _height - 1);
                Span<byte> target = pixels.Slice(r * _paddedWidth * channels, _paddedWidth * channels);
                ReadOnlySpan<TPixel> source = _raster.Row(y);
                if (_options.Background is Rgba8 background)
                {
                    PixelConvert.Convert(source, rgba);
                    Composite(rgba, background);
                }

                if (_gray)
                {
                    GrayRow(source, rgba, target);
                }
                else
                {
                    Span<Rgb8> rgb = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Rgb8>(target);
                    if (_options.Background is not null)
                    {
                        PixelConvert.Convert<Rgba8, Rgb8>(rgba, rgb[.._width]);
                    }
                    else
                    {
                        PixelConvert.Convert(source, rgb[.._width]);
                    }

                    rgb[_width..].Fill(rgb[_width - 1]);
                }
            }

            if (_gray)
            {
                Plane plane = _planes[0];
                for (int r = 0; r < count; r++)
                {
                    pixels.Slice(r * _paddedWidth, _paddedWidth).CopyTo(plane.Samples.AsSpan(r * plane.Stride));
                }

                return;
            }

            // Color conversion to the luma plane and full-size chroma rows, then chroma averaged down.
            Plane luma = _planes[0];
            Plane cb = _planes[1];
            Plane cr = _planes[2];
            byte[] fullCb = ArrayPool<byte>.Shared.Rent(_paddedWidth * count);
            byte[] fullCr = ArrayPool<byte>.Shared.Rent(_paddedWidth * count);
            try
            {
                for (int r = 0; r < count; r++)
                {
                    ReadOnlySpan<byte> rgb = pixels.Slice(r * _paddedWidth * 3, _paddedWidth * 3);
                    Span<byte> y = luma.Samples.AsSpan(r * luma.Stride, _paddedWidth);
                    Span<byte> b = fullCb.AsSpan(r * _paddedWidth, _paddedWidth);
                    Span<byte> c = fullCr.AsSpan(r * _paddedWidth, _paddedWidth);
                    for (int x = 0; x < _paddedWidth; x++)
                    {
                        int red = rgb[3 * x], green = rgb[(3 * x) + 1], blue = rgb[(3 * x) + 2];
                        y[x] = (byte)(((RY * red) + (GY * green) + (BY * blue) + Half) >> ScaleBits);
                        b[x] = (byte)(((RCb * red) + (GCb * green) + (BCbRCr * blue) + CbCrOffset + Half - 1) >> ScaleBits);
                        c[x] = (byte)(((BCbRCr * red) + (GCr * green) + (BCr * blue) + CbCrOffset + Half - 1) >> ScaleBits);
                    }
                }

                Downsample(fullCb, first, count, cb);
                Downsample(fullCr, first, count, cr);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(fullCb);
                ArrayPool<byte>.Shared.Return(fullCr);
            }
        }

        /// <summary>Gray from a row: copied from gray pixels, else luma from RGB with the same coefficients as YCbCr.</summary>
        private void GrayRow(ReadOnlySpan<TPixel> source, ReadOnlySpan<Rgba8> composited, Span<byte> target)
        {
            if (_options.Background is null && TPixel.Layout.ChannelOrder is PixelChannelOrder.Gray or PixelChannelOrder.GrayAlpha)
            {
                PixelConvert.Convert(source, System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Gray8>(target[.._width]));
            }
            else
            {
                Rgba8[] buffer = new Rgba8[_width];
                if (_options.Background is null)
                {
                    PixelConvert.Convert(source, buffer.AsSpan());
                }
                else
                {
                    composited.CopyTo(buffer);
                }

                for (int x = 0; x < _width; x++)
                {
                    Rgba8 p = buffer[x];
                    target[x] = (byte)(((RY * p.R) + (GY * p.G) + (BY * p.B) + Half) >> ScaleBits);
                }
            }

            target[_width.._paddedWidth].Fill(target[_width - 1]);
        }

        /// <summary>
        /// Averages full-size chroma rows down with libjpeg's alternating rounding bias. Below the image, as libjpeg does, the last
        /// averaged row repeats, rather than an average of repeated image rows.
        /// </summary>
        private void Downsample(byte[] full, int first, int rows, Plane plane)
        {
            int factorX = _maxH;
            int factorY = _maxV;
            int width = _paddedWidth / factorX;
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
                if (factorX == 1)
                {
                    row0[..width].CopyTo(output);
                }
                else if (factorY == 1)
                {
                    for (int x = 0, bias = 0; x < width; x++, bias ^= 1)
                    {
                        output[x] = (byte)((row0[2 * x] + row0[(2 * x) + 1] + bias) >> 1);
                    }
                }
                else
                {
                    ReadOnlySpan<byte> row1 = full.AsSpan(((r * 2) + 1) * _paddedWidth, _paddedWidth);
                    for (int x = 0, bias = 1; x < width; x++, bias ^= 3)
                    {
                        output[x] = (byte)((row0[2 * x] + row0[(2 * x) + 1] + row1[2 * x] + row1[(2 * x) + 1] + bias) >> 2);
                    }
                }
            }
        }

        /// <summary>Blends each pixel over <paramref name="background"/> by its alpha, then makes it opaque.</summary>
        private static void Composite(Span<Rgba8> pixels, Rgba8 background)
        {
            for (int x = 0; x < pixels.Length; x++)
            {
                Rgba8 p = pixels[x];
                int a = p.A;
                int inverse = 255 - a;
                pixels[x] = new Rgba8(
                    (byte)(((p.R * a) + (background.R * inverse) + 127) / 255),
                    (byte)(((p.G * a) + (background.G * inverse) + 127) / 255),
                    (byte)(((p.B * a) + (background.B * inverse) + 127) / 255),
                    255);
            }
        }
    }

    /// <summary>One component: its sampling, table, samples for one MCU row, and how many of its blocks are inside the image.</summary>
    private sealed class Plane : IDisposable
    {
        internal Plane(int h, int v, int table, int stride, int rows, int width, int height, int maxH, int maxV)
        {
            H = h;
            V = v;
            Table = table;
            Stride = stride;
            Samples = ArrayPool<byte>.Shared.Rent(stride * rows);

            // libjpeg's component size: the image scaled by the sampling ratio, rounded up, in whole blocks.
            BlocksWide = (int)(((((long)width * h) + maxH - 1) / maxH) + 7) / 8;
            BlocksHigh = (int)(((((long)height * v) + maxV - 1) / maxV) + 7) / 8;
        }

        internal int H { get; }

        internal int V { get; }

        internal int Table { get; }

        internal int Stride { get; }

        internal byte[] Samples { get; private set; }

        internal int BlocksWide { get; }

        internal int BlocksHigh { get; }

        internal int BlocksPerMcu => H * V;

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(Samples);
            Samples = [];
        }
    }
}
