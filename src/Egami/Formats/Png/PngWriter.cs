using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Png;

/// <summary>
/// Writes PNG, non-interlaced, from the W3C PNG Specification (Third Edition), and APNG for rasters with more than one frame. It
/// streams: one row at a time is filtered and compressed, and IDAT (or fdAT) chunks are written as compressed data fills them.
/// </summary>
internal sealed class PngWriter : IFormatWriter
{
    internal static readonly PngWriter Instance = new();

    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public void Write<TPixel>(Raster<TPixel> raster, Stream output, WriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentNullException.ThrowIfNull(output);
        PngWriterOptions png = options as PngWriterOptions ?? new PngWriterOptions { StripMetadata = options.StripMetadata };
        bool animated = raster.FrameCount > 1 && !png.FirstFrameOnly;
        if (png.ColorType == PngColorType.Palette && NeedsQuantizing(raster, png, animated))
        {
            using Raster<Rgba8> quantized = Quantize(raster, png, animated);
            Write(quantized, output, png);
            return;
        }

        Plan plan = PlanOutput(raster, png, animated);
        PngHeader header = new(raster.Width, raster.Height, plan.BitDepth, plan.ColorType, Interlaced: false);
        bool strip = png.StripMetadata;
        PngDetails? original = raster.Metadata.FormatDetails as PngDetails;
        bool sameFormat = original is not null && original.ColorType == plan.ColorType && original.BitDepth == plan.BitDepth
            && (plan.ColorType != PngColorType.Palette || original.Palette.Span.SequenceEqual(plan.Palette));

        output.Write(Signature);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)header.Width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], (uint)header.Height);
        ihdr[8] = (byte)header.BitDepth;
        ihdr[9] = (byte)header.ColorType;
        WriteChunk(output, PngChunkType.Ihdr, ihdr);

        if (!strip)
        {
            WriteColorChunks(output, raster.Metadata, original);
            WriteMetadataChunks(output, raster.Metadata, original);
            WriteKeptChunks(output, original, PngChunkPosition.BeforePalette, sameFormat);
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
            WriteKeptChunks(output, original, PngChunkPosition.BeforeImageData, sameFormat);
        }

        if (animated)
        {
            WriteAnimation(raster, output, png, header, plan);
        }
        else
        {
            using IdatChunkStream idat = new(output, png.MaxIdatSize, PngChunkType.Idat, null);
            WriteImageData(y => raster.Row(y), idat, png, header, plan);
        }

        if (!strip)
        {
            WriteKeptChunks(output, original, PngChunkPosition.AfterImageData, sameFormat);
        }

        WriteChunk(output, PngChunkType.Iend, []);
    }

    /// <summary>Chooses the color type, bit depth and palette (PNG design, "Choosing the color type").</summary>
    private static Plan PlanOutput<TPixel>(Raster<TPixel> raster, PngWriterOptions options, bool animated)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelLayoutInfo layout = TPixel.Layout;
        bool wide = layout.ChannelType != PixelChannelType.Unorm8;
        PngColorType natural = layout.ChannelOrder switch
        {
            PixelChannelOrder.Gray => PngColorType.Gray,
            PixelChannelOrder.GrayAlpha => PngColorType.GrayAlpha,
            PixelChannelOrder.Rgb or PixelChannelOrder.Bgr => PngColorType.Rgb,
            _ => PngColorType.Rgba,
        };

        if (options.ColorType is PngColorType colorType)
        {
            int depth = options.BitDepth ?? (colorType == PngColorType.Palette ? 8 : wide ? 16 : 8);
            Validate(colorType, depth);
            if (colorType != PngColorType.Palette)
            {
                return new Plan(colorType, depth, []);
            }

            Scan scan = Scan.Of(raster, animated);
            if (scan.Colors is null || scan.Colors.Count > 1 << depth)
            {
                throw new InvalidOperationException(
                    $"A palette PNG at bit depth {depth} holds at most {1 << depth} colors, but the image has {(scan.Colors is null ? "more than 256" : scan.Colors.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))}.");
            }

            return new Plan(PngColorType.Palette, depth, scan.PaletteOrdered(OriginalPalette(raster), 1 << depth));
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

        return Scan.Of(raster, animated).Smallest(OriginalPalette(raster));
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
        Quantization.BuiltPalette palette = Quantization.PaletteBuilder.Build(frames * height, r =>
        {
            Rgba8[] row = new Rgba8[width];
            PixelConvert.Convert<TPixel, Rgba8>(raster.Frames[r / height].Row(r % height), row);
            return row;
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

    /// <summary>
    /// Writes acTL and every frame. Frame 0 is the IDAT image (so the default image is always a frame). Each later frame stores
    /// the rectangle that differs from the frame before; it replaces those pixels (blend Source), or, when it has alpha, its
    /// changed pixels are all opaque and enough are unchanged, it is drawn Over with the unchanged ones made transparent, which
    /// compresses better. Either way the canvas then equals the frame, so no frame needs disposing.
    /// </summary>
    private static void WriteAnimation<TPixel>(Raster<TPixel> raster, Stream output, PngWriterOptions options, PngHeader header, Plan plan)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int frames = raster.FrameCount;
        Span<byte> actl = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(actl, (uint)frames);
        BinaryPrimitives.WriteUInt32BigEndian(actl[4..], (uint)Math.Max(0, raster.Metadata.LoopCount));
        WriteChunk(output, PngChunkType.Actl, actl);

        uint sequence = 0;
        bool hasAlpha = plan.ColorType is PngColorType.Rgba or PngColorType.GrayAlpha
            && TPixel.Layout.ChannelOrder is PixelChannelOrder.Rgba or PixelChannelOrder.Bgra or PixelChannelOrder.GrayAlpha;
        TPixel[] patch = ArrayPool<TPixel>.Shared.Rent(raster.Width);
        try
        {
            for (int i = 0; i < frames; i++)
            {
                Frame<TPixel> frame = raster.Frames[i];
                Rect bounds = i == 0 ? new Rect(0, 0, raster.Width, raster.Height) : Changed(raster.Frames[i - 1], frame);
                bool over = i > 0 && hasAlpha && PreferOver(raster.Frames[i - 1], frame, bounds);
                WriteFrameControl(output, sequence++, bounds, frame.Metadata.Delay, over);
                PngHeader frameHeader = header with { Width = bounds.Width, Height = bounds.Height };
                using IdatChunkStream data = i == 0
                    ? new(output, options.MaxIdatSize, PngChunkType.Idat, null)
                    : new(output, options.MaxIdatSize, PngChunkType.Fdat, () => sequence++);
                Frame<TPixel> previous = raster.Frames[Math.Max(0, i - 1)];
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
                }, data, options, frameHeader, plan);
            }
        }
        finally
        {
            ArrayPool<TPixel>.Shared.Return(patch);
        }
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
        bool lowDepthOrPalette = header.BitDepth < 8 || header.ColorType == PngColorType.Palette;
        PngFilterMode mode = options.Filter == PngFilterMode.Adaptive && lowDepthOrPalette ? PngFilterMode.None : options.Filter;
        ZLibCompressionStrategy strategy = options.CompressionStrategy switch
        {
            PngCompressionStrategy.Automatic => ZLibCompressionStrategy.Default,
            PngCompressionStrategy.Filtered => ZLibCompressionStrategy.Filtered,
            PngCompressionStrategy.HuffmanOnly => ZLibCompressionStrategy.HuffmanOnly,
            PngCompressionStrategy.RunLengthEncoding => ZLibCompressionStrategy.RunLengthEncoding,
            _ => ZLibCompressionStrategy.Default,
        };

        int rowBytes = (int)header.RowBytes(header.Width);
        int stride = header.FilterStride;
        int candidates = mode == PngFilterMode.Adaptive ? 5 : 1;
        byte[] current = ArrayPool<byte>.Shared.Rent(rowBytes);
        byte[] previous = ArrayPool<byte>.Shared.Rent(rowBytes);
        byte[] filtered = ArrayPool<byte>.Shared.Rent(rowBytes * candidates);
        RowPacker<TPixel> packer = new(header, plan);
        try
        {
            previous.AsSpan(0, rowBytes).Clear();
            Span<byte> filterByte = stackalloc byte[1];
            using (ZLibStream zlib = new(idat, new ZLibCompressionOptions { CompressionLevel = options.CompressionLevel, CompressionStrategy = strategy }, leaveOpen: true))
            {
                for (int y = 0; y < header.Height; y++)
                {
                    Span<byte> row = current.AsSpan(0, rowBytes);
                    packer.Pack(rows(y), row);
                    ReadOnlySpan<byte> above = previous.AsSpan(0, rowBytes);
                    byte filter;
                    ReadOnlySpan<byte> chosen;
                    if (mode == PngFilterMode.Adaptive)
                    {
                        filter = 0;
                        long best = long.MaxValue;
                        for (byte f = 0; f < 5; f++)
                        {
                            Span<byte> candidate = filtered.AsSpan(f * rowBytes, rowBytes);
                            PngFilterEncoder.Filter(f, row, above, stride, candidate);
                            long sum = PngFilterEncoder.SumOfAbsolutes(candidate, PixelKernels.HardwareBits);
                            if (sum < best)
                            {
                                best = sum;
                                filter = f;
                            }
                        }

                        chosen = filtered.AsSpan(filter * rowBytes, rowBytes);
                    }
                    else
                    {
                        filter = (byte)(mode - PngFilterMode.None);
                        Span<byte> destination = filtered.AsSpan(0, rowBytes);
                        PngFilterEncoder.Filter(filter, row, above, stride, destination);
                        chosen = destination;
                    }

                    // Stream.WriteByte allocates a one-byte array on every call, so the filter byte goes through a span.
                    filterByte[0] = filter;
                    zlib.Write(filterByte);
                    zlib.Write(chosen);
                    (current, previous) = (previous, current);
                }
            }

            idat.Finish();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(current);
            ArrayPool<byte>.Shared.Return(previous);
            ArrayPool<byte>.Shared.Return(filtered);
            packer.Dispose();
        }
    }

    /// <summary>Writes cICP, then iCCP or sRGB, then gAMA and cHRM (section 5.6 orders them before PLTE).</summary>
    private static void WriteColorChunks(Stream output, RasterMetadata metadata, PngDetails? original)
    {
        if (original is not null && original.CodingIndependentCodePoints.Length == 4)
        {
            WriteChunk(output, PngChunkType.Cicp, original.CodingIndependentCodePoints.Span);
        }

        if (!metadata.IccProfile.IsEmpty)
        {
            string name = original?.IccProfileName is { Length: > 0 } n && IsValidKeyword(n) ? n : "ICC profile";
            WriteChunk(output, PngChunkType.Iccp, [.. Encoding.Latin1.GetBytes(name), 0, 0, .. Compress(metadata.IccProfile.Span)]);
        }
        else if (original?.SrgbRenderingIntent is byte intent)
        {
            WriteChunk(output, PngChunkType.Srgb, [intent]);
        }
        else if (metadata.ColorSpace == ColorSpaceHint.Srgb)
        {
            WriteChunk(output, PngChunkType.Srgb, [0]);
        }

        uint? gamma = original?.Gamma;
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

        for (int i = 0; i < metadata.Text.Count; i++)
        {
            TextEntry entry = metadata.Text[i];
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
    private static void WriteKeptChunks(Stream output, PngDetails? original, PngChunkPosition position, bool sameFormat)
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
