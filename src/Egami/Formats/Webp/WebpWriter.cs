using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Webp;

/// <summary>
/// Writes WebP. Both encoders need the whole image before their first byte (VP8L chooses transforms and codes from every pixel,
/// VP8 chooses token probabilities), so rows are gathered as ARGB and encoded at <see cref="RowEncoder{TPixel}.Complete"/>. The
/// container is the simple form when the image needs nothing else, and the extended form (VP8X) for metadata, kept chunks or
/// alpha in lossy data.
/// </summary>
internal sealed class WebpWriter : IFormatWriter, IRowFormatWriter
{
    internal static readonly WebpWriter Instance = new();

    public void Write<TPixel>(Raster<TPixel> raster, Stream output, WriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Streaming.RowEncoding.Save(this, raster, output, options);
    }

    public RowEncoder<TPixel> Start<TPixel>(Stream output, EncodeStart<TPixel> start)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        WebpWriterOptions webp = start.Options as WebpWriterOptions ?? WebpWriterOptions.Default;
        bool lossless = webp.Lossless ?? start.Metadata.FormatDetails is WebpDetails { Lossless: true };
        int max = lossless ? Vp8lEncoder.MaxSide : 16383;
        if (start.Width > max || start.Height > max)
        {
            throw new ArgumentOutOfRangeException(
                nameof(start), $"{(lossless ? "Lossless" : "Lossy")} WebP holds images up to {max} pixels a side; this one is {start.Width}×{start.Height}.");
        }

        if (!lossless)
        {
            throw new NotSupportedException("Lossy WebP writing is not supported yet; set WebpWriterOptions.Lossless to true.");
        }

        return new StillEncoder<TPixel>(output, start, webp, lossless);
    }

    /// <summary>Gathers a still image's rows as ARGB and encodes them at the end.</summary>
    private sealed class StillEncoder<TPixel> : RowEncoder<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly Stream _output;
        private readonly EncodeStart<TPixel> _start;
        private readonly WebpWriterOptions _options;
        private readonly bool _lossless;
        private readonly IMemoryOwner<byte> _argb;
        private int _rows;

        internal StillEncoder(Stream output, EncodeStart<TPixel> start, WebpWriterOptions options, bool lossless)
        {
            _output = output;
            _start = start;
            _options = options;
            _lossless = lossless;
            _argb = start.Context.Memory.Allocate(checked(start.Width * start.Height * 4), ClearMode.None);
        }

        private Span<uint> Argb => MemoryMarshal.Cast<byte, uint>(_argb.Memory.Span)[..(_start.Width * _start.Height)];

        public override void WriteRow(ReadOnlySpan<TPixel> row)
        {
            if (_rows == _start.Height)
            {
                throw new InvalidOperationException($"The image has {_start.Height} rows; no more can be written.");
            }

            PixelConvert.Convert<TPixel, Bgra8>(row, MemoryMarshal.Cast<uint, Bgra8>(Argb.Slice(_rows * _start.Width, _start.Width)));
            _rows++;
        }

        public override void Complete()
        {
            if (_rows != _start.Height)
            {
                throw new InvalidOperationException($"The image has {_start.Height} rows, but {_rows} were written.");
            }

            ReadOnlySpan<uint> argb = Argb;
            bool alpha = false;
            foreach (uint p in argb)
            {
                if (p < 0xFF000000)
                {
                    alpha = true;
                    break;
                }
            }

            using Vp8lBitWriter bits = new(argb.Length);
            Vp8lEncoder.Encode(argb, _start.Width, _start.Height, bits);
            Container.Write(_output, _start.Metadata, _options, new ImageChunk(WebpContainer.Vp8L, bits.Finish(), default, alpha), _start.Width, _start.Height);
        }

        protected override void Dispose(bool disposing)
        {
            _argb.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>An encoded image: its chunk type and payload, and for lossy data with alpha the ALPH payload.</summary>
    internal readonly ref struct ImageChunk(uint type, ReadOnlySpan<byte> data, ReadOnlySpan<byte> alpha, bool hasAlpha)
    {
        public uint Type { get; } = type;

        public ReadOnlySpan<byte> Data { get; } = data;

        public ReadOnlySpan<byte> Alpha { get; } = alpha;

        public bool HasAlpha { get; } = hasAlpha;
    }

    /// <summary>Writes the container around encoded image data, with the metadata chunks in the places RFC 9649 gives them.</summary>
    internal static class Container
    {
        /// <summary>Writes a still image's file.</summary>
        internal static void Write(Stream output, RasterMetadata metadata, WebpWriterOptions options, ImageChunk image, int width, int height)
        {
            Chunks chunks = Chunks.From(metadata, options);
            bool extended = chunks.Any || !image.Alpha.IsEmpty;
            long riff = 4 + ChunkBytes(image.Data.Length);
            if (!image.Alpha.IsEmpty)
            {
                riff += ChunkBytes(image.Alpha.Length);
            }

            if (extended)
            {
                riff += ChunkBytes(10) + chunks.Bytes;
            }

            if (riff > uint.MaxValue)
            {
                throw new InvalidOperationException("The encoded image is too large for a RIFF file (4 GB).");
            }

            Span<byte> header = stackalloc byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(header, WebpContainer.Riff);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)riff);
            BinaryPrimitives.WriteUInt32LittleEndian(header[8..], WebpContainer.Webp);
            output.Write(header);
            if (extended)
            {
                byte flags = (byte)((chunks.Icc.IsEmpty ? 0 : 0x20) | (image.HasAlpha ? 0x10 : 0) | (chunks.Exif.IsEmpty ? 0 : 0x08) | (chunks.Xmp.IsEmpty ? 0 : 0x04));
                WriteVp8X(output, flags, width, height);
                WriteChunk(output, WebpContainer.Iccp, chunks.Icc.Span);
                chunks.WriteOthers(output, WebpChunkPosition.BeforeImageData);
                if (!image.Alpha.IsEmpty)
                {
                    WriteChunk(output, WebpContainer.Alph, image.Alpha);
                }
            }

            WriteChunk(output, image.Type, image.Data);
            if (extended)
            {
                WriteChunk(output, WebpContainer.Exif, chunks.Exif.Span);
                WriteChunk(output, WebpContainer.Xmp, chunks.Xmp.Span);
                chunks.WriteOthers(output, WebpChunkPosition.AfterImageData);
            }
        }

        /// <summary>Returns the bytes a chunk takes: its 8-byte header, its data and the padding byte for odd sizes.</summary>
        internal static long ChunkBytes(long size) => 8 + size + (size & 1);

        internal static void WriteVp8X(Stream output, byte flags, int width, int height)
        {
            Span<byte> vp8x = stackalloc byte[10];
            vp8x.Clear();
            vp8x[0] = flags;
            vp8x[4] = (byte)(width - 1);
            vp8x[5] = (byte)((width - 1) >> 8);
            vp8x[6] = (byte)((width - 1) >> 16);
            vp8x[7] = (byte)(height - 1);
            vp8x[8] = (byte)((height - 1) >> 8);
            vp8x[9] = (byte)((height - 1) >> 16);
            WriteChunk(output, WebpContainer.Vp8X, vp8x);
        }

        /// <summary>Writes a chunk with its padding byte; an empty metadata chunk is not written at all.</summary>
        internal static void WriteChunk(Stream output, uint type, ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty && type is WebpContainer.Iccp or WebpContainer.Exif or WebpContainer.Xmp)
            {
                return;
            }

            Span<byte> header = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(header, type);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)data.Length);
            output.Write(header);
            output.Write(data);
            if ((data.Length & 1) != 0)
            {
                output.WriteByte(0);
            }
        }
    }

    /// <summary>The metadata chunks a file gets: ICC, EXIF (with the resolution in it), XMP and the kept unknown chunks.</summary>
    internal sealed class Chunks
    {
        private readonly List<WebpChunk> _others = [];

        internal ReadOnlyMemory<byte> Icc { get; private set; }

        internal ReadOnlyMemory<byte> Exif { get; private set; }

        internal ReadOnlyMemory<byte> Xmp { get; private set; }

        /// <summary>Gets whether any chunk besides the image is written, which needs the extended format.</summary>
        internal bool Any => !Icc.IsEmpty || !Exif.IsEmpty || !Xmp.IsEmpty || _others.Count > 0;

        /// <summary>Gets the bytes all these chunks take.</summary>
        internal long Bytes
        {
            get
            {
                long total = 0;
                foreach (ReadOnlyMemory<byte> data in (ReadOnlySpan<ReadOnlyMemory<byte>>)[Icc, Exif, Xmp])
                {
                    total += data.IsEmpty ? 0 : Container.ChunkBytes(data.Length);
                }

                foreach (WebpChunk chunk in _others)
                {
                    total += Container.ChunkBytes(chunk.Data.Length);
                }

                return total;
            }
        }

        internal static Chunks From(RasterMetadata metadata, WriterOptions options)
        {
            Chunks chunks = new();
            if (options.StripMetadata)
            {
                return chunks;
            }

            chunks.Icc = options.ProfileToWrite(metadata);
            chunks.Exif = ExifWithResolution(metadata);
            chunks.Xmp = metadata.Xmp;
            if (metadata.FormatDetails is WebpDetails details)
            {
                foreach (WebpChunk chunk in details.OtherChunks)
                {
                    if (chunk.Type.Length == 4 && IsWritable(chunk.Type))
                    {
                        chunks._others.Add(chunk);
                    }
                }
            }

            return chunks;
        }

        internal void WriteOthers(Stream output, WebpChunkPosition position)
        {
            foreach (WebpChunk chunk in _others)
            {
                if (chunk.Position == position)
                {
                    uint type = (uint)(chunk.Type[0] | (chunk.Type[1] << 8) | (chunk.Type[2] << 16) | (chunk.Type[3] << 24));
                    Container.WriteChunk(output, type, chunk.Data.Span);
                }
            }
        }

        /// <summary>
        /// Returns the EXIF block to write. WebP has no resolution field of its own, so <see cref="RasterMetadata.Resolution"/> goes
        /// into XResolution, YResolution and ResolutionUnit, in a minimal EXIF block when the raster has none; an EXIF block that
        /// already says the same is written back unchanged.
        /// </summary>
        private static ReadOnlyMemory<byte> ExifWithResolution(RasterMetadata metadata)
        {
            ExifData? exif = metadata.Exif;
            if (metadata.Resolution is not Resolution resolution || !(resolution.X > 0) || !(resolution.Y > 0))
            {
                return exif?.ToBytes() ?? default;
            }

            (double x, double y, ushort unit) = resolution.Unit switch
            {
                ResolutionUnit.PerCentimeter => (resolution.X, resolution.Y, (ushort)3),
                ResolutionUnit.PerMeter => (resolution.X / 100, resolution.Y / 100, (ushort)3),
                ResolutionUnit.None => (resolution.X, resolution.Y, (ushort)1),
                _ => (resolution.X, resolution.Y, (ushort)2),
            };
            if (exif is not null && WebpParser.ResolutionFrom(exif) is Resolution existing && Same(existing, new Resolution(x, y, Unit(unit))))
            {
                return exif.ToBytes();
            }

            ExifData edited = exif?.Copy() ?? new ExifData();
            edited.Set(ExifKey.XResolution, Fraction(x));
            edited.Set(ExifKey.YResolution, Fraction(y));
            edited.Set(ExifKey.ResolutionUnit, unit);
            return edited.ToBytes();
        }

        private static ResolutionUnit Unit(ushort unit) => unit switch { 3 => ResolutionUnit.PerCentimeter, 1 => ResolutionUnit.None, _ => ResolutionUnit.PerInch };

        private static bool Same(Resolution a, Resolution b) => a.Unit == b.Unit && Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6;

        /// <summary>Returns a density as a fraction: a whole number over 1, or over 10,000 for fractional densities.</summary>
        private static ExifFraction Fraction(double value)
        {
            if (value == Math.Floor(value) && value <= uint.MaxValue)
            {
                return new ExifFraction((long)value, 1);
            }

            double scaled = Math.Round(value * 10_000);
            return scaled <= uint.MaxValue ? new ExifFraction((long)scaled, 10_000) : new ExifFraction((long)Math.Min(value, uint.MaxValue), 1);
        }

        /// <summary>Returns whether a kept chunk may be written back: not a chunk type the writer makes itself.</summary>
        private static bool IsWritable(string type)
        {
            return type is not ("VP8 " or "VP8L" or "VP8X" or "ALPH" or "ANIM" or "ANMF" or "ICCP" or "EXIF" or "XMP ");
        }
    }
}
