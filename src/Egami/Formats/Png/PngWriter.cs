using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Formats.Png;

/// <summary>
/// Writes PNG, non-interlaced, from the W3C PNG Specification (Third Edition), and APNG for rasters with more than one frame. It
/// streams: one row at a time is filtered and compressed, and IDAT (or fdAT) chunks are written as compressed data fills them.
/// </summary>
internal sealed class PngWriter : IFormatWriter, IRowFormatWriter
{
    internal static readonly PngWriter Instance = new();

    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> XmpKeyword => "XML:com.adobe.xmp"u8;

    public void Write<TPixel>(Raster<TPixel> raster, Stream output, WriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        RowEncoding.Save(this, raster, output, options);
    }

    public RowEncoder<TPixel> Start<TPixel>(Stream output, EncodeStart<TPixel> start)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PngWriterOptions png = start.Options as PngWriterOptions ?? new PngWriterOptions { StripMetadata = start.Options.StripMetadata };
        Raster<TPixel>? raster = start.Whole;
        if (png.ColorType == PngColorType.Palette)
        {
            start.Refuse("PNG", "Building a palette", "Choose another ColorType.");
        }
        else if (png.Reduce && png.ColorType is null && png.BitDepth is null)
        {
            start.Refuse("PNG", "Reduce", "Leave Reduce off, or set ColorType and BitDepth.");
        }

        bool animated = raster is not null && raster.FrameCount > 1 && !png.FirstFrameOnly;
        if (raster is not null && png.ColorType == PngColorType.Palette && NeedsQuantizing(raster, png, animated))
        {
            Raster<Rgba8> quantized = Quantize(raster, png, animated);
            try
            {
                return new Quantized<TPixel>(quantized, Start(output, RowEncoding.Whole(quantized, png)));
            }
            catch
            {
                quantized.Dispose();
                throw;
            }
        }

        return new Encoder<TPixel>(output, start, png, PlanOutput(raster, png, animated), animated);
    }

    /// <summary>
    /// Writes everything before the image data at once, then a still image's rows into IDAT as they come, or frames as fcTL with
    /// IDAT or fdAT; then the chunks after the image data and IEND.
    /// </summary>
    private sealed class Encoder<TPixel> : RowEncoder<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly Stream _output;
        private readonly PngWriterOptions _options;
        private readonly PngHeader _header;
        private readonly Plan _plan;
        private readonly PngDetails? _original;
        private readonly bool _sameFormat;
        private readonly bool _hdrWritten;
        private readonly RasterMetadata _metadata;
        private readonly int? _frameCount;
        private readonly bool _seekable;
        private readonly bool _animated;
        private readonly bool _hasAlpha;
        private readonly bool _streaming;
        private readonly IPixelMemorySource _memory;
        private ImageData<TPixel>? _image;
        private int _frames;
        private uint _sequence;
        private long _actlAt = -1;
        private Frame<TPixel>? _previous;
        private PixelPlane<TPixel>? _kept;
        private TPixel[]? _patch;

        internal Encoder(Stream output, EncodeStart<TPixel> start, PngWriterOptions png, Plan plan, bool animated)
        {
            _output = output;
            _options = png;
            _plan = plan;
            _metadata = start.Metadata;
            _animated = animated;
            _frameCount = start.Whole?.FrameCount ?? png.FrameCount;
            _seekable = start.Seekable;
            _streaming = start.Streaming;
            _memory = start.Context.Memory;
            _header = new(start.Width, start.Height, plan.BitDepth, plan.ColorType, Interlaced: false);
            _hasAlpha = plan.ColorType is PngColorType.Rgba or PngColorType.GrayAlpha && TPixel.Layout.HasAlpha;
            bool strip = png.StripMetadata;
            _hdrWritten = !strip && start.Metadata.Hdr is not null;
            _original = start.Metadata.FormatDetails as PngDetails;
            _sameFormat = _original is not null && _original.ColorType == plan.ColorType && _original.BitDepth == plan.BitDepth
                && (plan.ColorType != PngColorType.Palette || _original.Palette.Span.SequenceEqual(plan.Palette));

            output.Write(Signature);
            Span<byte> ihdr = stackalloc byte[13];
            BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)_header.Width);
            BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], (uint)_header.Height);
            ihdr[8] = (byte)_header.BitDepth;
            ihdr[9] = (byte)_header.ColorType;
            WriteChunk(output, PngChunkType.Ihdr, ihdr);

            if (!strip)
            {
                WriteColorChunks(output, start.Metadata, _original, start.Options.ProfileToWrite(start.Metadata));
                WriteMetadataChunks(output, start.Metadata, _original);
                WriteKeptChunks(output, _original, PngChunkPosition.BeforePalette, _sameFormat, start.Metadata.Hdr is not null);
            }

            if (plan.ColorType == PngColorType.Palette)
            {
                byte[] plte = new byte[plan.Palette.Length * 3];
                for (int i = 0; i < plan.Palette.Length; i++)
                {
                    plte[i * 3] = plan.Palette[i].R;
                    plte[(i * 3) + 1] = plan.Palette[i].G;
                    plte[(i * 3) + 2] = plan.Palette[i].B;
                }

                WriteChunk(output, PngChunkType.Plte, plte);
                int transparent = Array.FindLastIndex(plan.Palette, c => c.A != 255) + 1;
                if (transparent > 0)
                {
                    WriteChunk(output, PngChunkType.Trns, [.. plan.Palette[..transparent].Select(c => c.A)]);
                }
            }

            if (!strip)
            {
                WriteKeptChunks(output, _original, PngChunkPosition.BeforeImageData, _sameFormat, start.Metadata.Hdr is not null);
            }
        }

        /// <summary>Gets whether frames make an APNG: not when only the first frame is wanted, or one frame is declared.</summary>
        public override bool Animates => !_options.FirstFrameOnly && _options.FrameCount != 1;

        public override void WriteRow(ReadOnlySpan<TPixel> row)
        {
            _image ??= new ImageData<TPixel>(new IdatChunkStream(_output, _options.MaxIdatSize, PngChunkType.Idat, null), _options, _header, _plan, ownsData: true);
            _image.Row(row);
        }

        /// <summary>
        /// Writes one frame. Frame 0 is the IDAT image (so the default image is always a frame). Each later frame stores the
        /// rectangle that differs from the frame before; it replaces those pixels (blend Source), or, when it has alpha, its
        /// changed pixels are all opaque and enough are unchanged, it is drawn Over with the unchanged ones made transparent, which
        /// compresses better. Either way the canvas then equals the frame, so no frame needs disposing.
        /// </summary>
        public override void WriteFrame(Frame<TPixel> frame)
        {
            int i = _frames;
            if (i == 0)
            {
                WriteAnimationControl();
            }

            Rect bounds = i == 0 ? new Rect(0, 0, _header.Width, _header.Height) : Changed(_previous!, frame);
            bool over = i > 0 && _hasAlpha && PreferOver(_previous!, frame, bounds);
            WriteFrameControl(_output, _sequence++, bounds, frame.Metadata.Delay, over);
            PngHeader frameHeader = _header with { Width = bounds.Width, Height = bounds.Height };
            using IdatChunkStream data = i == 0
                ? new(_output, _options.MaxIdatSize, PngChunkType.Idat, null)
                : new(_output, _options.MaxIdatSize, PngChunkType.Fdat, () => _sequence++);
            Frame<TPixel> previous = _previous ?? frame;
            TPixel[] patch = _patch ??= ArrayPool<TPixel>.Shared.Rent(_header.Width);
            WriteImageData(y =>
            {
                Span<TPixel> row = frame.Row(bounds.Y + y).Slice(bounds.X, bounds.Width);
                if (!over)
                {
                    return row;
                }

                // Drawn Over: pixels the frame before already shows become fully transparent.
                Span<TPixel> below = previous.Row(bounds.Y + y).Slice(bounds.X, bounds.Width);
                Span<TPixel> result = patch.AsSpan(0, bounds.Width);
                for (int x = 0; x < result.Length; x++)
                {
                    result[x] = Same(row[x], below[x]) ? default : row[x];
                }

                return result;
            }, data, _options, frameHeader, _plan);
            Keep(frame);
            _frames++;
        }

        public override IEnumerable<int> WriteWhole(Raster<TPixel> raster)
        {
            if (!_animated)
            {
                foreach (int y in base.WriteWhole(raster))
                {
                    yield return y;
                }

                yield break;
            }

            for (int i = 0; i < raster.FrameCount; i++)
            {
                WriteFrame(raster.Frames[i]);
                yield return i;
            }
        }

        public override void Complete()
        {
            if (_image is not null)
            {
                _image.Finish();
            }
            else if (_frames > 0)
            {
                if (_frameCount is int declared && declared != _frames)
                {
                    throw new InvalidOperationException($"PngWriterOptions.FrameCount says {declared} frames, but {_frames} were written.");
                }

                if (_actlAt >= 0)
                {
                    // The count was not known before the first frame: fill it in now.
                    long end = _output.Position;
                    _output.Position = _actlAt;
                    WriteAnimationChunk(_frames);
                    _output.Position = end;
                }
            }

            if (!_options.StripMetadata)
            {
                WriteKeptChunks(_output, _original, PngChunkPosition.AfterImageData, _sameFormat, _hdrWritten);
            }

            WriteChunk(_output, PngChunkType.Iend, []);
        }

        protected override void Dispose(bool disposing)
        {
            _image?.Dispose();
            _kept?.Dispose();
            if (_patch is not null)
            {
                ArrayPool<TPixel>.Shared.Return(_patch);
            }

            base.Dispose(disposing);
        }

        /// <summary>Writes acTL with the frame count, or a placeholder filled in at the end on a seekable stream.</summary>
        private void WriteAnimationControl()
        {
            if (_frameCount is null)
            {
                if (!_seekable)
                {
                    throw new InvalidOperationException("An APNG holds its frame count before the first frame; on a stream that cannot seek, set PngWriterOptions.FrameCount.");
                }

                _actlAt = _output.Position;
            }

            WriteAnimationChunk(_frameCount ?? 0);
        }

        private void WriteAnimationChunk(int frames)
        {
            Span<byte> actl = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(actl, (uint)frames);
            BinaryPrimitives.WriteUInt32BigEndian(actl[4..], (uint)Math.Max(0, _metadata.LoopCount));
            WriteChunk(_output, PngChunkType.Actl, actl);
        }

        /// <summary>Keeps the frame just written to compare the next one with: Save's own frame, or a copy when streaming.</summary>
        private void Keep(Frame<TPixel> frame)
        {
            if (_streaming)
            {
                _kept ??= PixelPlane.Allocate<TPixel>(_header.Width, _header.Height, _memory);
                for (int y = 0; y < _header.Height; y++)
                {
                    frame.Row(y).CopyTo(_kept.Row(y));
                }

                _previous ??= new Frame<TPixel>(_kept, new FrameMetadata());
                return;
            }

            _previous = frame;
        }
    }

    /// <summary>Writes the image Save was given after mapping it to a built palette, as Rgba8 that fits the palette exactly.</summary>
    private sealed class Quantized<TPixel>(Raster<Rgba8> quantized, RowEncoder<Rgba8> inner) : RowEncoder<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        public override void WriteRow(ReadOnlySpan<TPixel> row) => throw new NotSupportedException();

        public override IEnumerable<int> WriteWhole(Raster<TPixel> raster) => inner.WriteWhole(quantized);

        public override void Complete() => inner.Complete();

        protected override void Dispose(bool disposing)
        {
            inner.Dispose();
            quantized.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Chooses the color type, bit depth and palette (PNG design, "Choosing the color type").</summary>
    private static Plan PlanOutput<TPixel>(Raster<TPixel>? raster, PngWriterOptions options, bool animated)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelLayoutInfo layout = TPixel.Layout;
        bool wide = layout.NeedsWideSamples;
        PngColorType natural = layout.ChannelOrder switch
        {
            PixelChannelOrder.Gray => PngColorType.Gray,
            PixelChannelOrder.GrayAlpha => PngColorType.GrayAlpha,
            PixelChannelOrder.Rgb or PixelChannelOrder.Bgr => PngColorType.Rgb,
            _ => layout.HasAlpha ? PngColorType.Rgba : PngColorType.Rgb,
        };

        if (options.ColorType is PngColorType colorType)
        {
            int depth = options.BitDepth ?? (colorType == PngColorType.Palette ? 8 : wide ? 16 : 8);
            Validate(colorType, depth);
            if (colorType != PngColorType.Palette)
            {
                return new Plan(colorType, depth, []);
            }

            Scan scan = Scan.Of(raster!, animated);
            if (scan.Colors is null || scan.Colors.Count > 1 << depth)
            {
                throw new InvalidOperationException(
                    $"A palette PNG at bit depth {depth} holds at most {1 << depth} colors, but the image has {(scan.Colors is null ? "more than 256" : scan.Colors.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))}.");
            }

            return new Plan(PngColorType.Palette, depth, scan.PaletteOrdered(OriginalPalette(raster!), 1 << depth));
        }

        if (options.BitDepth is int bitDepth)
        {
            Validate(natural, bitDepth);
            return new Plan(natural, bitDepth, []);
        }

        if (!options.Reduce)
        {
            return new Plan(natural, wide ? 16 : 8, []);
        }

        return Scan.Of(raster!, animated).Smallest(OriginalPalette(raster!));
    }

    /// <summary>Returns whether a palette output needs a built palette: more colors (or 16-bit values) than the depth holds.</summary>
    private static bool NeedsQuantizing<TPixel>(Raster<TPixel> raster, PngWriterOptions options, bool animated)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int depth = options.BitDepth ?? 8;
        Validate(PngColorType.Palette, depth);
        Scan scan = Scan.Of(raster, animated);
        return scan.Colors is null || scan.Colors.Count > Math.Min(1 << depth, options.Palette.MaxColors);
    }

    /// <summary>
    /// Returns the frames mapped to one palette built from all of them (Wu's quantizer in four dimensions, so partial alpha stays),
    /// dithered as the options say. The result's colors all fit the palette, so it is then written exactly.
    /// </summary>
    private static Raster<Rgba8> Quantize<TPixel>(Raster<TPixel> raster, PngWriterOptions options, bool animated)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int frames = animated ? raster.FrameCount : 1;
        int width = raster.Width;
        int height = raster.Height;
        Quantization.PaletteOptions paletteOptions = options.Palette with { MaxColors = Math.Min(options.Palette.MaxColors, 1 << (options.BitDepth ?? 8)) };
        Rgba8[] buffer = new Rgba8[width];
        Quantization.BuiltPalette palette = Quantization.PaletteBuilder.Build(frames * height, r =>
        {
            PixelConvert.Convert<TPixel, Rgba8>(raster.Frames[r / height].Row(r % height), buffer);
            return buffer;
        }, paletteOptions, binaryAlpha: false);
        using Quantization.PaletteMapper mapper = new(palette.Colors, palette.TransparentIndex, withAlpha: true, 0);
        List<Frame<Rgba8>> result = [];
        byte[] indices = ArrayPool<byte>.Shared.Rent(width * height);
        try
        {
            for (int f = 0; f < frames; f++)
            {
                Frame<TPixel> source = raster.Frames[f];
                Quantization.FrameMapper.Map(width, height, (y, row) => PixelConvert.Convert<TPixel, Rgba8>(source.Row(y), row), palette, mapper, options.Palette.Dither, indices.AsSpan(0, width * height));
                PixelPlane<Rgba8> plane = PixelPlane.Allocate<Rgba8>(width, height, raster.Context.Memory);
                for (int y = 0; y < height; y++)
                {
                    Span<Rgba8> row = plane.Row(y);
                    for (int x = 0; x < width; x++)
                    {
                        row[x] = palette.Colors[indices[(y * width) + x]];
                    }
                }

                result.Add(new Frame<Rgba8>(plane, source.Metadata.Copy()));
            }
        }
        catch
        {
            foreach (Frame<Rgba8> frame in result)
            {
                frame.Plane.Dispose();
            }

            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(indices);
        }

        return new Raster<Rgba8>(result, raster.Metadata, raster.Context);
    }

    private static void WriteFrameControl(Stream output, uint sequence, Rect bounds, TimeSpan delay, bool over)
    {
        Span<byte> fctl = stackalloc byte[26];
        BinaryPrimitives.WriteUInt32BigEndian(fctl, sequence);
        BinaryPrimitives.WriteUInt32BigEndian(fctl[4..], (uint)bounds.Width);
        BinaryPrimitives.WriteUInt32BigEndian(fctl[8..], (uint)bounds.Height);
        BinaryPrimitives.WriteUInt32BigEndian(fctl[12..], (uint)bounds.X);
        BinaryPrimitives.WriteUInt32BigEndian(fctl[16..], (uint)bounds.Y);
        (ushort numerator, ushort denominator) = DelayFraction(delay);
        BinaryPrimitives.WriteUInt16BigEndian(fctl[20..], numerator);
        BinaryPrimitives.WriteUInt16BigEndian(fctl[22..], denominator);
        fctl[24] = 0;
        fctl[25] = over ? (byte)1 : (byte)0;
        WriteChunk(output, PngChunkType.Fctl, fctl);
    }

    /// <summary>
    /// Returns a delay as the simplest fraction of a second with both parts up to 65,535, from continued fractions: the first
    /// convergent within half a tick, so a delay read as 1/30 s is written as 1/30 again.
    /// </summary>
    internal static (ushort Numerator, ushort Denominator) DelayFraction(TimeSpan delay)
    {
        double seconds = Math.Max(0, delay.TotalSeconds);
        if (seconds == 0)
        {
            return (0, 100);
        }

        if (seconds >= ushort.MaxValue)
        {
            return (ushort.MaxValue, 1);
        }

        double tolerance = 0.5 / TimeSpan.TicksPerSecond;
        long h0 = 0, h1 = 1, k0 = 1, k1 = 0;
        double x = seconds;
        (ushort, ushort) best = ((ushort)Math.Round(seconds * 1000), 1000);
        for (int step = 0; step < 32; step++)
        {
            long a = (long)Math.Floor(x);
            long h = (a * h1) + h0;
            long k = (a * k1) + k0;
            if (h > ushort.MaxValue || k > ushort.MaxValue)
            {
                break;
            }

            best = ((ushort)h, (ushort)k);
            if (Math.Abs(((double)h / k) - seconds) <= tolerance || x - a < 1e-12)
            {
                break;
            }

            (h0, h1, k0, k1) = (h1, h, k1, k);
            x = 1 / (x - a);
        }

        return best;
    }

    /// <summary>Returns the bounding box of the pixels that differ between two frames, at least 1 × 1.</summary>
    private static Rect Changed<TPixel>(Frame<TPixel> previous, Frame<TPixel> frame)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = frame.Plane.Width;
        int left = width;
        int top = -1;
        int right = -1;
        int bottom = -1;
        for (int y = 0; y < frame.Plane.Height; y++)
        {
            ReadOnlySpan<byte> a = MemoryMarshal.AsBytes(previous.Row(y));
            ReadOnlySpan<byte> b = MemoryMarshal.AsBytes(frame.Row(y));
            int first = a.CommonPrefixLength(b);
            if (first == a.Length)
            {
                continue;
            }

            int size = Unsafe.SizeOf<TPixel>();
            int last = a.Length - 1;
            while (a[last] == b[last])
            {
                last--;
            }

            left = Math.Min(left, first / size);
            right = Math.Max(right, last / size);
            top = top < 0 ? y : top;
            bottom = y;
        }

        return right < 0 ? new Rect(0, 0, 1, 1) : new Rect(left, top, right - left + 1, bottom - top + 1);
    }

    /// <summary>
    /// Returns whether a frame is better drawn Over the one before, with unchanged pixels transparent: only when every changed
    /// pixel is opaque (Over cannot make a pixel more transparent) and at least a quarter of the rectangle is unchanged.
    /// </summary>
    private static bool PreferOver<TPixel>(Frame<TPixel> previous, Frame<TPixel> frame, Rect bounds)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Rgba16[] row = ArrayPool<Rgba16>.Shared.Rent(bounds.Width);
        try
        {
            long unchanged = 0;
            for (int y = bounds.Y; y < bounds.Y + bounds.Height; y++)
            {
                Span<TPixel> now = frame.Row(y).Slice(bounds.X, bounds.Width);
                Span<TPixel> before = previous.Row(y).Slice(bounds.X, bounds.Width);
                PixelConvert.Convert<TPixel, Rgba16>(now, row.AsSpan(0, bounds.Width));
                for (int x = 0; x < bounds.Width; x++)
                {
                    if (Same(now[x], before[x]))
                    {
                        unchanged++;
                    }
                    else if (row[x].A != ushort.MaxValue)
                    {
                        return false;
                    }
                }
            }

            return unchanged * 4 >= (long)bounds.Width * bounds.Height;
        }
        finally
        {
            ArrayPool<Rgba16>.Shared.Return(row);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Same<TPixel>(TPixel a, TPixel b)
        where TPixel : unmanaged
    {
        return MemoryMarshal.AsBytes(new ReadOnlySpan<TPixel>(in a)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<TPixel>(in b)));
    }

    /// <summary>Returns the palette the raster was read with, if it was a palette PNG.</summary>
    private static Rgba8[]? OriginalPalette(Raster raster)
    {
        return raster.Metadata.FormatDetails is PngDetails { ColorType: PngColorType.Palette } png && !png.Palette.IsEmpty ? png.Palette.ToArray() : null;
    }

    private static void Validate(PngColorType colorType, int bitDepth)
    {
        if (!PngHeader.IsValid(colorType, bitDepth))
        {
            throw new ArgumentException($"PNG does not allow bit depth {bitDepth} with color type {colorType}.", nameof(bitDepth));
        }
    }

    /// <summary>Filters and compresses the rows <paramref name="rows"/> gives (header.Height of them, header.Width wide) into <paramref name="idat"/>.</summary>
    private static void WriteImageData<TPixel>(RowGetter<TPixel> rows, IdatChunkStream idat, PngWriterOptions options, PngHeader header, Plan plan)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using ImageData<TPixel> data = new(idat, options, header, plan, ownsData: false);
        for (int y = 0; y < header.Height; y++)
        {
            data.Row(rows(y));
        }

        data.Finish();
    }

    /// <summary>One image's rows being filtered and compressed into IDAT or fdAT chunks, a row at a time.</summary>
    private sealed class ImageData<TPixel> : IDisposable
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly IdatChunkStream _idat;
        private readonly bool _ownsData;
        private readonly PngFilterMode _mode;
        private readonly int _rowBytes;
        private readonly int _stride;
        private readonly byte[] _filtered;
        private readonly RowPacker<TPixel> _packer;
        private readonly ZLibStream _zlib;
        private byte[] _current;
        private byte[] _previous;
        private bool _disposed;

        internal ImageData(IdatChunkStream idat, PngWriterOptions options, PngHeader header, Plan plan, bool ownsData)
        {
            _idat = idat;
            _ownsData = ownsData;
            bool lowDepthOrPalette = header.BitDepth < 8 || header.ColorType == PngColorType.Palette;
            _mode = options.Filter == PngFilterMode.Adaptive && lowDepthOrPalette ? PngFilterMode.None : options.Filter;
            ZLibCompressionStrategy strategy = options.CompressionStrategy switch
            {
                PngCompressionStrategy.Auto => ZLibCompressionStrategy.Default,
                PngCompressionStrategy.Filtered => ZLibCompressionStrategy.Filtered,
                PngCompressionStrategy.HuffmanOnly => ZLibCompressionStrategy.HuffmanOnly,
                PngCompressionStrategy.RunLengthEncoding => ZLibCompressionStrategy.RunLengthEncoding,
                _ => ZLibCompressionStrategy.Default,
            };

            _rowBytes = (int)header.RowBytes(header.Width);
            _stride = header.FilterStride;
            int candidates = _mode == PngFilterMode.Adaptive ? 5 : 1;
            _current = ArrayPool<byte>.Shared.Rent(_rowBytes);
            _previous = ArrayPool<byte>.Shared.Rent(_rowBytes);
            _filtered = ArrayPool<byte>.Shared.Rent(_rowBytes * candidates);
            _packer = new(header, plan);
            _previous.AsSpan(0, _rowBytes).Clear();
            _zlib = new(idat, new ZLibCompressionOptions { CompressionLevel = options.CompressionLevel, CompressionStrategy = strategy }, leaveOpen: true);
        }

        public void Row(ReadOnlySpan<TPixel> source)
        {
            int rowBytes = _rowBytes;
            Span<byte> row = _current.AsSpan(0, rowBytes);
            _packer.Pack(source, row);
            ReadOnlySpan<byte> above = _previous.AsSpan(0, rowBytes);
            byte filter;
            ReadOnlySpan<byte> chosen;
            if (_mode == PngFilterMode.Adaptive)
            {
                filter = 0;
                long best = long.MaxValue;
                for (byte f = 0; f < 5; f++)
                {
                    Span<byte> candidate = _filtered.AsSpan(f * rowBytes, rowBytes);
                    PngFilterEncoder.Filter(f, row, above, _stride, candidate);
                    long sum = PngFilterEncoder.SumOfAbsolutes(candidate, PixelKernels.HardwareBits);
                    if (sum < best)
                    {
                        best = sum;
                        filter = f;
                    }
                }

                chosen = _filtered.AsSpan(filter * rowBytes, rowBytes);
            }
            else
            {
                filter = (byte)(_mode - PngFilterMode.None);
                Span<byte> destination = _filtered.AsSpan(0, rowBytes);
                PngFilterEncoder.Filter(filter, row, above, _stride, destination);
                chosen = destination;
            }

            // Stream.WriteByte allocates a one-byte array on every call, so the filter byte goes through a span.
            Span<byte> filterByte = [filter];
            _zlib.Write(filterByte);
            _zlib.Write(chosen);
            (_current, _previous) = (_previous, _current);
        }

        /// <summary>Ends the compressed data and writes the last chunk.</summary>
        public void Finish()
        {
            _zlib.Dispose();
            _idat.Finish();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _zlib.Dispose();
            ArrayPool<byte>.Shared.Return(_current);
            ArrayPool<byte>.Shared.Return(_previous);
            ArrayPool<byte>.Shared.Return(_filtered);
            _packer.Dispose();
            if (_ownsData)
            {
                _idat.Dispose();
            }
        }
    }

    /// <summary>
    /// Writes cICP, then iCCP or sRGB, then gAMA, cHRM, mDCV and cLLI (section 5.6 orders them before PLTE), from the generic
    /// metadata, so a file converted from another format keeps its color tags; the original chunks fill in what it lacks.
    /// </summary>
    private static void WriteColorChunks(Stream output, RasterMetadata metadata, PngDetails? original, ReadOnlyMemory<byte> profile)
    {
        if (metadata.CodePoints is Colors.ColorCodePoints codes)
        {
            WriteChunk(output, PngChunkType.Cicp, [codes.Primaries, codes.TransferCharacteristics, codes.MatrixCoefficients, (byte)(codes.FullRange ? 1 : 0)]);
        }
        else if (original is not null && original.CodingIndependentCodePoints.Length == 4)
        {
            WriteChunk(output, PngChunkType.Cicp, original.CodingIndependentCodePoints.Span);
        }

        if (!profile.IsEmpty)
        {
            string name = original?.IccProfileName is { Length: > 0 } n && IsValidKeyword(n) ? n : "ICC profile";
            WriteChunk(output, PngChunkType.Iccp, [.. Encoding.Latin1.GetBytes(name), 0, 0, .. Compress(profile.Span)]);
        }
        else if (original?.SrgbRenderingIntent is byte intent)
        {
            WriteChunk(output, PngChunkType.Srgb, [intent]);
        }
        else if (metadata.ColorSpace == ColorSpaceHint.Srgb || !metadata.IccProfile.IsEmpty)
        {
            // The sRGB tag, or an sRGB profile left out by OmitSrgbProfile.
            WriteChunk(output, PngChunkType.Srgb, [0]);
        }

        uint? gamma = original?.FileGamma;
        if (gamma is null && metadata.ColorSpace == ColorSpaceHint.Linear)
        {
            gamma = 100_000;
        }
        else if (gamma is null && metadata.ColorSpace == ColorSpaceHint.Gamma && metadata.Gamma is double g and > 0)
        {
            gamma = (uint)Math.Round(100_000 / g);
        }

        if (gamma is uint value)
        {
            Span<byte> data = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(data, value);
            WriteChunk(output, PngChunkType.Gama, data);
        }

        if (original is not null && original.Chromaticities.Length == 32)
        {
            WriteChunk(output, PngChunkType.Chrm, original.Chromaticities.Span);
        }
        else if (metadata.Primaries is Colors.ColorPrimaries p && metadata.IccProfile.IsEmpty)
        {
            Span<byte> data = stackalloc byte[32];
            Colors.Chromaticity[] points = [p.White, p.Red, p.Green, p.Blue];
            for (int i = 0; i < 4; i++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(data[(i * 8)..], (uint)Math.Round(points[i].X * 100_000));
                BinaryPrimitives.WriteUInt32BigEndian(data[((i * 8) + 4)..], (uint)Math.Round(points[i].Y * 100_000));
            }

            WriteChunk(output, PngChunkType.Chrm, data);
        }

        if (metadata.Hdr is Colors.HdrMetadata hdr)
        {
            if (hdr.MasteringPrimaries is Colors.ColorPrimaries d && hdr.MasteringMaxLuminance is double max)
            {
                Span<byte> data = stackalloc byte[24];
                Colors.Chromaticity[] points = [d.Red, d.Green, d.Blue, d.White];
                for (int i = 0; i < 4; i++)
                {
                    BinaryPrimitives.WriteUInt16BigEndian(data[(i * 4)..], (ushort)Math.Clamp(Math.Round(points[i].X / 0.00002), 0, ushort.MaxValue));
                    BinaryPrimitives.WriteUInt16BigEndian(data[((i * 4) + 2)..], (ushort)Math.Clamp(Math.Round(points[i].Y / 0.00002), 0, ushort.MaxValue));
                }

                BinaryPrimitives.WriteUInt32BigEndian(data[16..], Luminance(max));
                BinaryPrimitives.WriteUInt32BigEndian(data[20..], Luminance(hdr.MasteringMinLuminance ?? 0));
                WriteChunk(output, 0x6D444356 /* mDCV */, data);
            }

            if (hdr.MaxContentLightLevel is double cll)
            {
                Span<byte> data = stackalloc byte[8];
                BinaryPrimitives.WriteUInt32BigEndian(data, Luminance(cll));
                BinaryPrimitives.WriteUInt32BigEndian(data[4..], Luminance(hdr.MaxFrameAverageLightLevel ?? 0));
                WriteChunk(output, 0x634C4C49 /* cLLI */, data);
            }
        }

        static uint Luminance(double nits) => (uint)Math.Clamp(Math.Round(nits * 10_000), 0, uint.MaxValue);
    }

    /// <summary>Writes pHYs, eXIf and the text chunks.</summary>
    private static void WriteMetadataChunks(Stream output, RasterMetadata metadata, PngDetails? original)
    {
        if (metadata.Resolution is Resolution resolution)
        {
            (double x, double y, byte unit) = resolution.Unit switch
            {
                ResolutionUnit.PerMeter => (resolution.X, resolution.Y, (byte)1),
                ResolutionUnit.PerCentimeter => (resolution.X * 100, resolution.Y * 100, (byte)1),
                ResolutionUnit.PerInch => (resolution.X / 0.0254, resolution.Y / 0.0254, (byte)1),
                _ => (resolution.X, resolution.Y, (byte)0),
            };
            if (x >= 1 && y >= 1 && x <= uint.MaxValue && y <= uint.MaxValue)
            {
                Span<byte> data = stackalloc byte[9];
                BinaryPrimitives.WriteUInt32BigEndian(data, (uint)Math.Round(x));
                BinaryPrimitives.WriteUInt32BigEndian(data[4..], (uint)Math.Round(y));
                data[8] = unit;
                WriteChunk(output, PngChunkType.Phys, data);
            }
        }

        if (metadata.Exif is not null)
        {
            WriteChunk(output, PngChunkType.Exif, metadata.Exif.ToBytes().Span);
        }

        // XMP as the XMP specification part 3 puts it in PNG: an uncompressed iTXt chunk with this keyword.
        ReadOnlySpan<byte> xmp = metadata.Xmp.Span;
        if (!xmp.IsEmpty)
        {
            WriteChunk(output, PngChunkType.Itxt, [.. XmpKeyword, 0, 0, 0, 0, 0, .. xmp]);
        }

        // IPTC as ImageMagick, ExifTool and others read it from PNG: a zTXt "Raw profile type iptc" chunk holding a Photoshop
        // resource block, since PNG has no IPTC chunk of its own.
        ReadOnlySpan<byte> iptc = metadata.IptcBytes.Span;
        if (!iptc.IsEmpty)
        {
            string text = RawProfiles.Encode("iptc", PhotoshopResources.Create(iptc));
            WriteChunk(output, PngChunkType.Ztxt, [.. Encoding.Latin1.GetBytes(RawProfiles.Prefix + "iptc"), 0, 0, .. Compress(Encoding.Latin1.GetBytes(text))]);
        }

        for (int i = 0; i < metadata.Text.Count; i++)
        {
            TextEntry entry = metadata.Text[i];
            if ((!xmp.IsEmpty && entry.Key == "XML:com.adobe.xmp") || (!iptc.IsEmpty && entry.Key == RawProfiles.Prefix + "iptc"))
            {
                continue;
            }

            if (!IsValidKeyword(entry.Key))
            {
                throw new ArgumentException(
                    $"The text key '{entry.Key}' is not a valid PNG keyword: it must be 1 to 79 Latin-1 printable characters with no leading, trailing or repeated spaces.");
            }

            PngTextSource? source = original is not null && i < original.TextSources.Count ? original.TextSources[i] : null;
            byte[] key = Encoding.Latin1.GetBytes(entry.Key);
            bool latin1 = entry.Language is null && IsLatin1(entry.Value);
            if (latin1 && source?.ChunkType == "zTXt")
            {
                WriteChunk(output, PngChunkType.Ztxt, [.. key, 0, 0, .. Compress(Encoding.Latin1.GetBytes(entry.Value))]);
            }
            else if (latin1 && source?.ChunkType != "iTXt")
            {
                WriteChunk(output, PngChunkType.Text, [.. key, 0, .. Encoding.Latin1.GetBytes(entry.Value)]);
            }
            else
            {
                bool compress = source?.Compressed == true;
                byte[] text = Encoding.UTF8.GetBytes(entry.Value);
                byte[] language = Encoding.ASCII.GetBytes(entry.Language ?? string.Empty);
                WriteChunk(output, PngChunkType.Itxt, [.. key, 0, compress ? (byte)1 : (byte)0, 0, .. language, 0, 0, .. compress ? Compress(text) : text]);
            }
        }
    }

    /// <summary>
    /// Writes kept chunks from one position group. Safe-to-copy chunks are always written; the others only when the color type,
    /// bit depth and palette are unchanged, as the specification's copy rules require (section 14.2).
    /// </summary>
    private static void WriteKeptChunks(Stream output, PngDetails? original, PngChunkPosition position, bool sameFormat, bool hdrWritten)
    {
        if (original is null)
        {
            return;
        }

        foreach (PngChunk chunk in original.OtherChunks)
        {
            if (chunk.Position != position || chunk.Type.Length != 4)
            {
                continue;
            }

            // mDCV and cLLI were written from the metadata.
            if (hdrWritten && (string.Equals(chunk.Type, "mDCV", StringComparison.OrdinalIgnoreCase) || string.Equals(chunk.Type, "cLLI", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            uint type = BinaryPrimitives.ReadUInt32BigEndian(Encoding.ASCII.GetBytes(chunk.Type));
            if (PngChunkType.IsValid(type) && !PngChunkType.IsCritical(type) && (sameFormat || PngChunkType.IsSafeToCopy(type)))
            {
                WriteChunk(output, type, chunk.Data.Span);
            }
        }
    }

    internal static void WriteChunk(Stream output, uint type, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)data.Length);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], type);
        output.Write(header);
        output.Write(data);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32.Update(Crc32.Update(0, header[4..]), data));
        output.Write(crc);
    }

    private static byte[] Compress(ReadOnlySpan<byte> data)
    {
        using MemoryStream compressed = new();
        using (ZLibStream zlib = new(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return compressed.ToArray();
    }

    /// <summary>Keywords are 1 to 79 Latin-1 printable characters, without leading, trailing or consecutive spaces (section 11.3.3.2).</summary>
    private static bool IsValidKeyword(string keyword)
    {
        if (keyword.Length is < 1 or > 79 || keyword[0] == ' ' || keyword[^1] == ' ' || keyword.Contains("  ", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (char c in keyword)
        {
            if (!((c >= 32 && c <= 126) || (c >= 161 && c <= 255)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLatin1(string text)
    {
        foreach (char c in text)
        {
            if (c is '\0' or > (char)255)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The output form: color type, bit depth, and the palette for palette images.</summary>
    private sealed record Plan(PngColorType ColorType, int BitDepth, Rgba8[] Palette);

    /// <summary>What a full scan of the image found, for <see cref="PngWriterOptions.Reduce"/> and palette output.</summary>
    private sealed class Scan
    {
        public bool Opaque { get; private set; } = true;

        public bool Gray { get; private set; } = true;

        /// <summary>Gets a value indicating whether every 16-bit value is an exact 8-bit value (v × 257).</summary>
        public bool EightBit { get; private set; } = true;

        /// <summary>Gets the distinct 8-bit colors, or <see langword="null"/> once there are more than 256 or values need 16 bits.</summary>
        public HashSet<Rgba8>? Colors { get; private set; } = [];

        /// <summary>Gets the lowest gray bit depth (1, 2, 4 or 8) that holds every gray value exactly.</summary>
        public int GrayDepth { get; private set; } = 1;

        /// <summary>Scans the first frame, or every frame of an animation, which shares one color type and palette.</summary>
        public static Scan Of<TPixel>(Raster<TPixel> raster, bool allFrames)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            Scan scan = new();
            Rgba16[] row = ArrayPool<Rgba16>.Shared.Rent(raster.Width);
            try
            {
                for (int f = 0; f < (allFrames ? raster.FrameCount : 1); f++)
                {
                    for (int y = 0; y < raster.Height; y++)
                    {
                        Span<Rgba16> pixels = row.AsSpan(0, raster.Width);
                        PixelConvert.Convert<TPixel, Rgba16>(raster.Frames[f].Row(y), pixels);
                        foreach (Rgba16 p in pixels)
                        {
                            scan.Add(p);
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<Rgba16>.Shared.Return(row);
            }

            return scan;
        }

        public Plan Smallest(Rgba8[]? original)
        {
            if (!EightBit)
            {
                PngColorType wide = (Gray, Opaque) switch
                {
                    (true, true) => PngColorType.Gray,
                    (true, false) => PngColorType.GrayAlpha,
                    (false, true) => PngColorType.Rgb,
                    _ => PngColorType.Rgba,
                };
                return new Plan(wide, 16, []);
            }

            int paletteDepth = Colors is null ? 16 : Colors.Count <= 2 ? 1 : Colors.Count <= 4 ? 2 : Colors.Count <= 16 ? 4 : 8;
            if (Gray && Opaque && GrayDepth <= paletteDepth)
            {
                return new Plan(PngColorType.Gray, GrayDepth, []);
            }

            if (Colors is not null)
            {
                Rgba8[] palette = PaletteOrdered(original, 256);
                int depth = Math.Max(paletteDepth, palette.Length <= 2 ? 1 : palette.Length <= 4 ? 2 : palette.Length <= 16 ? 4 : 8);
                return new Plan(PngColorType.Palette, depth, palette);
            }

            PngColorType colorType = (Gray, Opaque) switch
            {
                (true, true) => PngColorType.Gray,
                (true, false) => PngColorType.GrayAlpha,
                (false, true) => PngColorType.Rgb,
                _ => PngColorType.Rgba,
            };
            return new Plan(colorType, 8, []);
        }

        /// <summary>
        /// Returns the original palette when it holds every color, so a palette PNG keeps its indices; otherwise the colors with
        /// the translucent ones first, so tRNS can stop after them.
        /// </summary>
        public Rgba8[] PaletteOrdered(Rgba8[]? original, int maxEntries)
        {
            if (original is not null && original.Length <= maxEntries && Colors!.IsSubsetOf(original) && original.Distinct().Count() == original.Length)
            {
                return original;
            }

            return [.. Colors!.OrderBy(c => c.A == 255 ? 1 : 0).ThenBy(c => (c.R << 16) | (c.G << 8) | c.B).ThenBy(c => c.A)];
        }

        private void Add(Rgba16 p)
        {
            if (p.A != ushort.MaxValue)
            {
                Opaque = false;
            }

            if (p.R != p.G || p.G != p.B)
            {
                Gray = false;
            }

            if (EightBit && (p.R % 257 != 0 || p.G % 257 != 0 || p.B % 257 != 0 || p.A % 257 != 0))
            {
                EightBit = false;
                Colors = null;
            }

            if (!EightBit)
            {
                return;
            }

            byte r = (byte)(p.R / 257);
            if (Gray)
            {
                while (GrayDepth < 8 && r % (255 / ((1 << GrayDepth) - 1)) != 0)
                {
                    GrayDepth *= 2;
                }
            }

            if (Colors is not null && Colors.Add(new Rgba8(r, (byte)(p.G / 257), (byte)(p.B / 257), (byte)(p.A / 257))) && Colors.Count > 256)
            {
                Colors = null;
            }
        }
    }

    /// <summary>Converts one row of the raster into the bytes of one unfiltered scanline.</summary>
    private sealed class RowPacker<TPixel> : IDisposable
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly PngHeader _header;
        private readonly Dictionary<Rgba8, byte> _indices = [];
        private readonly byte[] _buffer;
        private readonly Gray16[]? _gray16;

        public RowPacker(PngHeader header, Plan plan)
        {
            _header = header;
            for (int i = 0; i < plan.Palette.Length; i++)
            {
                _indices[plan.Palette[i]] = (byte)i;
            }

            _buffer = ArrayPool<byte>.Shared.Rent(header.Width * 8);
            if (header.ColorType == PngColorType.GrayAlpha && header.BitDepth == 16)
            {
                _gray16 = ArrayPool<Gray16>.Shared.Rent(header.Width);
            }
        }

        public void Pack(ReadOnlySpan<TPixel> row, Span<byte> scanline)
        {
            int width = row.Length;
            switch (_header.ColorType, _header.BitDepth)
            {
                case (PngColorType.Gray, 16):
                    BinaryPrimitives.ReverseEndianness(Convert<Gray16, ushort>(row), MemoryMarshal.Cast<byte, ushort>(scanline));
                    break;
                case (PngColorType.Gray, 8):
                    Convert<Gray8, byte>(row).CopyTo(scanline);
                    break;
                case (PngColorType.Gray, int depth):
                    PackLow(Convert<Gray8, byte>(row), scanline, depth);
                    break;
                case (PngColorType.GrayAlpha, 8):
                    Convert<GrayAlpha8, byte>(row).CopyTo(scanline);
                    break;
                case (PngColorType.GrayAlpha, 16) when typeof(TPixel) == typeof(GrayAlpha16):
                    BinaryPrimitives.ReverseEndianness(MemoryMarshal.Cast<TPixel, ushort>(row), MemoryMarshal.Cast<byte, ushort>(scanline));
                    break;
                case (PngColorType.GrayAlpha, _):
                    Span<Gray16> gray = _gray16.AsSpan(0, width);
                    PixelConvert.Convert<TPixel, Gray16>(row, gray);
                    ReadOnlySpan<ushort> wide = Convert<Rgba16, ushort>(row);
                    for (int x = 0; x < width; x++)
                    {
                        BinaryPrimitives.WriteUInt16BigEndian(scanline[(x * 4)..], gray[x].L);
                        BinaryPrimitives.WriteUInt16BigEndian(scanline[((x * 4) + 2)..], wide[(x * 4) + 3]);
                    }

                    break;
                case (PngColorType.Rgb, 8):
                    Convert<Rgb8, byte>(row).CopyTo(scanline);
                    break;
                case (PngColorType.Rgb, 16) when typeof(TPixel) == typeof(Rgb16):
                    BinaryPrimitives.ReverseEndianness(MemoryMarshal.Cast<TPixel, ushort>(row), MemoryMarshal.Cast<byte, ushort>(scanline));
                    break;
                case (PngColorType.Rgb, _):
                    ReadOnlySpan<ushort> rgba = Convert<Rgba16, ushort>(row);
                    for (int x = 0; x < width; x++)
                    {
                        BinaryPrimitives.WriteUInt16BigEndian(scanline[(x * 6)..], rgba[x * 4]);
                        BinaryPrimitives.WriteUInt16BigEndian(scanline[((x * 6) + 2)..], rgba[(x * 4) + 1]);
                        BinaryPrimitives.WriteUInt16BigEndian(scanline[((x * 6) + 4)..], rgba[(x * 4) + 2]);
                    }

                    break;
                case (PngColorType.Rgba, 8):
                    Convert<Rgba8, byte>(row).CopyTo(scanline);
                    break;
                case (PngColorType.Rgba, _):
                    BinaryPrimitives.ReverseEndianness(Convert<Rgba16, ushort>(row), MemoryMarshal.Cast<byte, ushort>(scanline));
                    break;
                default:
                    ReadOnlySpan<Rgba8> colors = MemoryMarshal.Cast<byte, Rgba8>(Convert<Rgba8, byte>(row));
                    if (_header.BitDepth == 8)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            scanline[x] = _indices[colors[x]];
                        }
                    }
                    else
                    {
                        scanline.Clear();
                        int depth = _header.BitDepth;
                        for (int x = 0; x < width; x++)
                        {
                            int bit = x * depth;
                            scanline[bit >> 3] |= (byte)(_indices[colors[x]] << (8 - depth - (bit & 7)));
                        }
                    }

                    break;
            }
        }

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            if (_gray16 is not null)
            {
                ArrayPool<Gray16>.Shared.Return(_gray16);
            }
        }

        /// <summary>Packs 8-bit gray samples at 1, 2 or 4 bits, rounding each to the nearest value the depth holds.</summary>
        private static void PackLow(ReadOnlySpan<byte> samples, Span<byte> scanline, int depth)
        {
            int max = (1 << depth) - 1;
            scanline.Clear();
            for (int x = 0; x < samples.Length; x++)
            {
                int bit = x * depth;
                scanline[bit >> 3] |= (byte)((((samples[x] * max) + 127) / 255) << (8 - depth - (bit & 7)));
            }
        }

        /// <summary>Converts the row into the internal buffer as <typeparamref name="TTo"/> and returns it as <typeparamref name="TElement"/> values.</summary>
        private ReadOnlySpan<TElement> Convert<TTo, TElement>(ReadOnlySpan<TPixel> row)
            where TTo : unmanaged, IPixelType<TTo>
            where TElement : unmanaged
        {
            Span<TTo> converted = MemoryMarshal.Cast<byte, TTo>(_buffer.AsSpan())[..row.Length];
            PixelConvert.Convert(row, converted);
            return MemoryMarshal.Cast<TTo, TElement>(converted);
        }
    }

    /// <summary>Returns row <paramref name="y"/> of the image being written.</summary>
    private delegate ReadOnlySpan<TPixel> RowGetter<TPixel>(int y);

    /// <summary>
    /// Collects compressed bytes and writes them as IDAT chunks of at most <c>maxSize</c> bytes, or as fdAT chunks, each with the
    /// next sequence number in front.
    /// </summary>
    private sealed class IdatChunkStream(Stream output, int maxSize, uint type, Func<uint>? sequence) : Stream
    {
        private readonly byte[] _buffer = ArrayPool<byte>.Shared.Rent(maxSize);
        private int _count;
        private bool _returned;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (!buffer.IsEmpty)
            {
                int n = Math.Min(buffer.Length, maxSize - _count);
                buffer[..n].CopyTo(_buffer.AsSpan(_count));
                _count += n;
                buffer = buffer[n..];
                if (_count == maxSize)
                {
                    Emit();
                }
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Write(buffer.AsSpan(offset, count));
        }

        /// <summary>Writes the last, partly filled chunk.</summary>
        public void Finish()
        {
            if (_count > 0)
            {
                Emit();
            }
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (!_returned)
            {
                _returned = true;
                ArrayPool<byte>.Shared.Return(_buffer);
            }

            base.Dispose(disposing);
        }

        private void Emit()
        {
            if (sequence is null)
            {
                WriteChunk(output, type, _buffer.AsSpan(0, _count));
            }
            else
            {
                byte[] data = ArrayPool<byte>.Shared.Rent(_count + 4);
                BinaryPrimitives.WriteUInt32BigEndian(data, sequence());
                _buffer.AsSpan(0, _count).CopyTo(data.AsSpan(4));
                WriteChunk(output, type, data.AsSpan(0, _count + 4));
                ArrayPool<byte>.Shared.Return(data);
            }

            _count = 0;
        }
    }
}
