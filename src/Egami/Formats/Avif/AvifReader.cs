using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Colors;
using Egami.Formats.Heif;
using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Formats.Avif;

/// <summary>
/// Reads AVIF (AV1 Image File Format 1.1 over HEIF), written from the specifications. The container gives the items, the AV1
/// decoder their planes, and the HEIF image layer the pixels: grids assembled, overlays composed, converted to RGB as libavif
/// converts them, the clean aperture cropped, and irot and imir left as the orientation (design question 9). Rows are converted
/// as they are read, after the in-loop filters; a Region decodes only the grid cells it touches (design question 11).
/// </summary>
internal sealed class AvifReader : IFormatReader, IYuvFormatReader, IRowSourceReader
{
    internal static readonly AvifReader Instance = new();

    public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        AvifFile file = AvifFile.Read(input);
        if (input.Reports(DiagnosticLevel.Warning))
        {
            // A probe has no raster to list warnings on, so skipped entries go to the handler directly.
            foreach (HeifProblem problem in file.Heif.Problems)
            {
                input.Diagnostics.Report(DiagnosticLevel.Warning, problem.Id, problem.Message, problem.Offset);
            }
        }

        (int width, int height) = OutputSize(file);
        input.Limits.CheckFrameSize(width, height);
        PixelLayoutInfo layout = NaturalLayout(file);
        return new RasterInfo
        {
            Format = AvifFormat.Info,
            Width = width,
            Height = height,
            Layout = layout,
            FrameCount = 1,
            ImageCount = 1,
            HasMetadata = !file.Icc.IsEmpty || file.ExifItems.Count > 0 || file.XmpItems.Count > 0 || file.ContentLightLevel is not null
                || file.MasteringDisplay is not null,
            EstimatedBytes = (long)width * height * layout.BytesPerPixel,
            Orientation = file.Orientation,
            Access = RasterAccess.Streamed,
            WorkingBytes = WorkingBytes(file),
        };
    }

    public Raster Read(EgamiInput input, ReaderOptions options)
    {
        Prepared image = Prepare(input, AvifFile.Read(input), options, null);
        return image.Natural switch
        {
            _ when image.Natural == Gray8.Layout => Read<Gray8>(input, image),
            _ when image.Natural == GrayAlpha8.Layout => Read<GrayAlpha8>(input, image),
            _ when image.Natural == Rgb8.Layout => Read<Rgb8>(input, image),
            _ when image.Natural == Rgba8.Layout => Read<Rgba8>(input, image),
            _ when image.Natural == Gray16.Layout => Read<Gray16>(input, image),
            _ when image.Natural == GrayAlpha16.Layout => Read<GrayAlpha16>(input, image),
            _ when image.Natural == Rgb16.Layout => Read<Rgb16>(input, image),
            _ => (Raster)Read<Rgba16>(input, image),
        };
    }

    public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return Read<TPixel>(input, Prepare(input, AvifFile.Read(input), options, null));
    }

    public RowSource Open(EgamiInput input, ReaderOptions options)
    {
        try
        {
            return new AvifRows(input, Prepare(input, AvifFile.Read(input), options, options.Region));
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    public YuvRaster ReadYuv(EgamiInput input, ReaderOptions options)
    {
        AvifFile file = AvifFile.Read(input);
        if (file.Overlay is not null)
        {
            throw new NotSupportedException("This AVIF is an overlay of several images, which has no single set of YUV planes; load it with Raster.Load.");
        }

        AvifCodec codec = new(options is not AvifReaderOptions { FilmGrain: false });
        (HeifPlanes color, HeifPlanes? alpha) = Decode(file, codec, input, null);
        RasterMetadata metadata = Metadata(file, codec, input);
        YuvColor yuvColor = PlaneColor(file, metadata);
        return color.BitDepth == 8
            ? Planes<byte>(color, alpha, yuvColor, metadata, input.Context)
            : Planes<ushort>(color, alpha, yuvColor, metadata, input.Context);
    }

    public (RasterInfo Info, ChromaFormat? Format, int BitDepth, YuvColor Color) ProbeYuv(EgamiInput input, ReaderOptions options)
    {
        AvifFile file = AvifFile.Read(input);
        RasterInfo info = new()
        {
            Format = AvifFormat.Info,
            Width = file.Width,
            Height = file.Height,
            Layout = NaturalLayout(file),
            FrameCount = 1,
            ImageCount = 1,
            Orientation = file.Orientation,
        };
        return (info, file.Overlay is null ? file.Config.Chroma : null, file.Config.BitDepth, PlaneColor(file, null));
    }

    /// <summary>
    /// Gets the natural pixel layout (design question 7): 8-bit types for 8-bit files and 16-bit ones for 10 and 12 bits; gray for
    /// monochrome; with alpha when there is an alpha item.
    /// </summary>
    internal static PixelLayoutInfo NaturalLayout(int bitDepth, bool monochrome, bool alpha)
    {
        return bitDepth <= 8
            ? (monochrome ? (alpha ? GrayAlpha8.Layout : Gray8.Layout) : alpha ? Rgba8.Layout : Rgb8.Layout)
            : (monochrome ? (alpha ? GrayAlpha16.Layout : Gray16.Layout) : alpha ? Rgba16.Layout : Rgb16.Layout);
    }

    /// <summary>
    /// Decodes the image and its alpha into planes; the alpha must match the image's size. With <paramref name="needed"/> (in image
    /// coordinates), only the grid cells it touches are decoded and the rest of the planes stay zero.
    /// </summary>
    internal static (HeifPlanes Color, HeifPlanes? Alpha) Decode(AvifFile file, AvifCodec codec, EgamiInput input, Rect? needed)
    {
        input.CheckMemory(WorkingBytes(file));
        HeifPlanes color = file.Primary.Type == AvifFile.Grid
            ? Grid(file, codec, file.Cells, (file.GridSize.Rows, file.GridSize.Columns, file.Width, file.Height), input, needed, lumaOnly: false)
            : Cell(file, codec, file.Cells[0], input);
        HeifPlanes? alpha = null;
        if (file.Alpha is not null)
        {
            (int rows, int columns, _, _) = file.AlphaLayout;
            alpha = file.AlphaCells.Count == 1 && rows * columns == 1
                ? Cell(file, codec, file.AlphaCells[0], input).LumaOnly()
                : Grid(file, codec, file.AlphaCells, file.AlphaLayout, input, needed, lumaOnly: true);
            if (alpha.Width != color.Width || alpha.Height != color.Height)
            {
                throw input.Corrupt(-1, $"The alpha image is {alpha.Width} x {alpha.Height}, but the color image is {color.Width} x {color.Height}.");
            }
        }

        return (color, alpha);
    }

    /// <summary>Decodes a grid's cells, those <paramref name="needed"/> touches (with a margin for chroma upsampling) or all, and tiles them.</summary>
    private static HeifPlanes Grid(AvifFile file, AvifCodec codec, IReadOnlyList<HeifItem> cells, (int Rows, int Columns, int Width, int Height) layout,
        EgamiInput input, Rect? needed, bool lumaOnly)
    {
        (int cellWidth, int cellHeight) = file.Extents(cells[0]);
        HeifPlanes?[] decoded = new HeifPlanes?[cells.Count];
        for (int t = 0; t < cells.Count; t++)
        {
            int x0 = cellWidth * (t % layout.Columns), y0 = cellHeight * (t / layout.Columns);
            bool touched = needed is not Rect r
                || (x0 < r.X + r.Width + 2 && x0 + cellWidth > r.X - 2 && y0 < r.Y + r.Height + 2 && y0 + cellHeight > r.Y - 2);
            if (touched || t == 0)
            {
                HeifPlanes planes = Cell(file, codec, cells[t], input);
                decoded[t] = lumaOnly ? planes.LumaOnly() : planes;
            }
        }

        return HeifPlanes.Grid(decoded, layout.Rows, layout.Columns, layout.Width, layout.Height, input);
    }

    /// <summary>Decodes one av01 item.</summary>
    private static HeifPlanes Cell(AvifFile file, AvifCodec codec, HeifItem item, EgamiInput input) =>
        codec.Decode(file.Heif.ItemData(item, input.Limits).ToArray(), input);

    /// <summary>Gets the natural layout of a file.</summary>
    private static PixelLayoutInfo NaturalLayout(AvifFile file) => NaturalLayout(file.Config.BitDepth, file.Config.Monochrome, file.HasAlpha);

    /// <summary>Gets the size Load gives: the clean aperture's, or the image's.</summary>
    private static (int Width, int Height) OutputSize(AvifFile file) => file.Crop is (_, _, int w, int h) ? (w, h) : (file.Width, file.Height);

    /// <summary>Gets the bytes a decode holds besides the raster: the planes of the image and of its alpha, at two bytes a sample.</summary>
    private static long WorkingBytes(AvifFile file)
    {
        long luma = (long)file.Width * file.Height;
        long chroma = file.Config.Monochrome ? 0
            : 2L * ((file.Width + file.Config.SubsamplingX) >> file.Config.SubsamplingX) * ((file.Height + file.Config.SubsamplingY) >> file.Config.SubsamplingY);
        return 2 * (luma + chroma + (file.Alpha is null ? 0 : luma));
    }

    /// <summary>
    /// Decodes what the output needs and returns how to read its rows: the planes and their converter, or an overlay's composed
    /// canvas; with the metadata. <paramref name="region"/> (in output coordinates) limits the grid cells decoded.
    /// </summary>
    private static Prepared Prepare(EgamiInput input, AvifFile file, ReaderOptions options, Rect? region)
    {
        (int width, int height) = OutputSize(file);
        input.Limits.CheckFrameSize(width, height);
        (int x, int y) = file.Crop is (int cx, int cy, _, _) ? (cx, cy) : (0, 0);
        PixelLayoutInfo natural = NaturalLayout(file);
        AvifCodec codec = new(options is not AvifReaderOptions { FilmGrain: false });
        PixelRows rows;
        List<string> unsupported = [];
        if (file.Overlay is HeifOverlay overlay)
        {
            int[] canvas = Overlay(file, codec, overlay, input, unsupported, out int channels, out int depth);
            rows = new PixelRows(channels, depth, (row, column, samples) =>
                canvas.AsSpan((((y + row) * overlay.Width) + x + column) * channels, samples.Length).CopyTo(samples));
        }
        else
        {
            Rect? needed = region is Rect r ? r with { X = r.X + x, Y = r.Y + y } : null;
            (HeifPlanes color, HeifPlanes? alpha) = Decode(file, codec, input, needed);
            if (x + width > color.Width || y + height > color.Height)
            {
                throw input.Corrupt(-1, $"The image is {color.Width} x {color.Height}, smaller than its {width} x {height} clean aperture at ({x}, {y}).");
            }

            HeifRgbConverter converter = new(color, alpha, file.AlphaPremultiplied, file.CodePoints);
            if (!converter.Supported)
            {
                unsupported.Add("color");
            }

            rows = new PixelRows(converter.Channels, converter.OutputDepth, (row, column, samples) =>
                converter.Row(y + row, x + column, samples.Length / converter.Channels, samples));
        }

        RasterMetadata metadata = Metadata(file, codec, input);
        if (unsupported.Count > 0)
        {
            metadata.WarningList.Add("Avif.MatrixApproximated", $"libavif does not convert matrix coefficients {file.CodePoints.MatrixCoefficients} with this range and chroma; the nearest matrix it has was used.");
        }

        return new Prepared(width, height, natural, rows, metadata, WorkingBytes(file));
    }

    private static Raster<TPixel> Read<TPixel>(EgamiInput input, Prepared image)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Raster<TPixel> raster = input.CreateRaster<TPixel>(image.Width, image.Height);
        try
        {
            Fill(raster, image.Rows, image.Natural, input);
            Adopt(raster.Metadata, image.Metadata);
            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    /// <summary>Moves what <see cref="Metadata"/> gathered onto the raster's metadata.</summary>
    private static void Adopt(RasterMetadata target, RasterMetadata source)
    {
        target.IccProfile = source.IccProfile;
        target.CodePoints = source.CodePoints;
        target.Hdr = source.Hdr;
        target.Exif = source.Exif;
        target.Xmp = source.Xmp;
        target.FormatDetails = source.FormatDetails;
        foreach (EgamiDiagnostic warning in source.Warnings)
        {
            target.WarningList.Add(warning);
        }
    }

    /// <summary>
    /// Composes an overlay (HEIF 6.6.2.3.3) in the natural type's samples: the canvas filled with its RGBA fill (scaled to the
    /// output depth), then each layer converted to RGB and drawn over it in order at its offset, opaque, clipped to the canvas.
    /// </summary>
    private static int[] Overlay(AvifFile file, AvifCodec codec, HeifOverlay overlay, EgamiInput input, List<string> unsupported, out int channels, out int depth)
    {
        bool mono = file.Config.Monochrome, alpha = file.HasAlpha;
        int color = mono ? 1 : 3;
        channels = color + (alpha ? 1 : 0);
        depth = file.Config.BitDepth > 8 ? 16 : 8;
        int max = (1 << depth) - 1;
        long size = (long)overlay.Width * overlay.Height * channels;
        input.CheckMemory((size * sizeof(int)) + WorkingBytes(file));
        int[] canvas = new int[size];
        Span<int> fill = stackalloc int[4];
        for (int c = 0; c < 4; c++)
        {
            fill[c] = (int)(((overlay.Fill[c] * (long)max) + 32767) / 65535);
        }

        for (long at = 0; at < size; at += channels)
        {
            for (int c = 0; c < color; c++)
            {
                canvas[at + c] = fill[c];
            }

            if (alpha)
            {
                canvas[at + color] = fill[3];
            }
        }

        for (int i = 0; i < file.Cells.Count; i++)
        {
            HeifPlanes layer = Cell(file, codec, file.Cells[i], input);
            if (layer.BitDepth != file.Config.BitDepth || layer.Monochrome != mono)
            {
                throw input.Corrupt(-1, "The overlay's layers differ in depth or in being monochrome.");
            }

            HeifRgbConverter converter = new(layer, null, false, file.CodePoints);
            if (!converter.Supported)
            {
                unsupported.Add("layer");
            }

            (int ox, int oy) = overlay.Offsets[i];
            int x0 = Math.Max(0, ox), x1 = (int)Math.Min(overlay.Width, (long)ox + layer.Width);
            int y0 = Math.Max(0, oy), y1 = (int)Math.Min(overlay.Height, (long)oy + layer.Height);
            if (x0 >= x1 || y0 >= y1)
            {
                continue;
            }

            int[] samples = new int[(x1 - x0) * color];
            for (int y = y0; y < y1; y++)
            {
                converter.Row(y - oy, x0 - ox, x1 - x0, samples);
                for (int x = x0, s = 0; x < x1; x++, s += color)
                {
                    long at = (((long)y * overlay.Width) + x) * channels;
                    samples.AsSpan(s, color).CopyTo(canvas.AsSpan((int)at, color));
                    if (alpha)
                    {
                        canvas[at + color] = max;
                    }
                }
            }
        }

        return canvas;
    }

    /// <summary>Converts rows of samples into the raster, in bands on several threads for a large image.</summary>
    private static void Fill<TPixel>(Raster<TPixel> raster, PixelRows source, PixelLayoutInfo natural, EgamiInput input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = raster.Width, height = raster.Height;
        void Rows(int from, int to)
        {
            int[] samples = new int[width * source.Channels];
            byte[] packed = new byte[width * natural.BytesPerPixel];
            for (int row = from; row < to; row++)
            {
                input.CancellationToken.ThrowIfCancellationRequested();
                ReadRow(source, natural, row, 0, samples, packed, raster.Row(row));
            }
        }

        int parts = (long)width * height < 1 << 16 ? 1 : Math.Min(input.Context.MaxParallelism, Math.Max(1, height / 16));
        if (parts <= 1)
        {
            Rows(0, height);
            return;
        }

        Parallel.For(0, parts, new ParallelOptions { MaxDegreeOfParallelism = parts, CancellationToken = input.CancellationToken },
            i => Rows(height * i / parts, height * (i + 1) / parts));
    }

    /// <summary>Converts a row of a natural type to <typeparamref name="TPixel"/>.</summary>
    private static void Convert<TPixel>(PixelLayoutInfo natural, ReadOnlySpan<byte> source, Span<TPixel> target)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (natural == Gray8.Layout)
        {
            PixelConvert.Convert(MemoryMarshal.Cast<byte, Gray8>(source), target);
        }
        else if (natural == GrayAlpha8.Layout)
        {
            PixelConvert.Convert(MemoryMarshal.Cast<byte, GrayAlpha8>(source), target);
        }
        else if (natural == Rgb8.Layout)
        {
            PixelConvert.Convert(MemoryMarshal.Cast<byte, Rgb8>(source), target);
        }
        else if (natural == Rgba8.Layout)
        {
            PixelConvert.Convert(MemoryMarshal.Cast<byte, Rgba8>(source), target);
        }
        else if (natural == Gray16.Layout)
        {
            PixelConvert.Convert(MemoryMarshal.Cast<byte, Gray16>(source), target);
        }
        else if (natural == GrayAlpha16.Layout)
        {
            PixelConvert.Convert(MemoryMarshal.Cast<byte, GrayAlpha16>(source), target);
        }
        else if (natural == Rgb16.Layout)
        {
            PixelConvert.Convert(MemoryMarshal.Cast<byte, Rgb16>(source), target);
        }
        else
        {
            PixelConvert.Convert(MemoryMarshal.Cast<byte, Rgba16>(source), target);
        }
    }

    /// <summary>Copies the planes into a <see cref="YuvRaster{TSample}"/>, the alpha item's luma as its alpha plane.</summary>
    private static YuvRaster<TSample> Planes<TSample>(HeifPlanes color, HeifPlanes? alpha, YuvColor yuvColor, RasterMetadata metadata, EgamiContext context)
        where TSample : unmanaged
    {
        YuvRaster<TSample> planes = YuvRaster<TSample>.Allocate(color.Width, color.Height, color.Chroma, color.BitDepth, yuvColor, alpha is not null,
            metadata, context, ClearMode.None);
        try
        {
            Copy(color.Samples[0], color.Strides[0], color.Width, color.Height, planes.Y, color.BitDepth, color.BitDepth);
            if (!color.Monochrome)
            {
                Copy(color.Samples[1], color.Strides[1], color.ChromaWidth, color.ChromaHeight, planes.U!, color.BitDepth, color.BitDepth);
                Copy(color.Samples[2], color.Strides[2], color.ChromaWidth, color.ChromaHeight, planes.V!, color.BitDepth, color.BitDepth);
            }

            if (alpha is not null)
            {
                // The alpha plane holds samples at the color's depth; an alpha image of another depth is rescaled.
                Copy(alpha.Samples[0], alpha.Strides[0], alpha.Width, alpha.Height, planes.A!, alpha.BitDepth, color.BitDepth);
            }

            return planes;
        }
        catch
        {
            planes.Dispose();
            throw;
        }
    }

    private static void Copy<TSample>(ushort[] source, int stride, int width, int height, YuvPlane<TSample> target, int fromDepth, int toDepth)
        where TSample : unmanaged
    {
        int max = (1 << toDepth) - 1, fromMax = (1 << fromDepth) - 1;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<ushort> row = source.AsSpan(y * stride, width);
            if (typeof(TSample) == typeof(byte))
            {
                Span<byte> to = MemoryMarshal.Cast<TSample, byte>(target.Row(y));
                for (int x = 0; x < width; x++)
                {
                    to[x] = (byte)(fromDepth == toDepth ? row[x] : ((row[x] * max) + (fromMax / 2)) / fromMax);
                }
            }
            else
            {
                Span<ushort> to = MemoryMarshal.Cast<TSample, ushort>(target.Row(y));
                for (int x = 0; x < width; x++)
                {
                    to[x] = (ushort)(fromDepth == toDepth ? row[x] : ((row[x] * max) + (fromMax / 2)) / fromMax);
                }
            }
        }
    }

    /// <summary>
    /// Gets the planes' encoding: the matrix and range of the color code points (design question 8), with BT.601 for a matrix
    /// <see cref="YuvColor"/> has no member for, and the AV1 chroma sample position.
    /// </summary>
    private static YuvColor PlaneColor(AvifFile file, RasterMetadata? metadata)
    {
        ColorCodePoints points = file.CodePoints;
        ChromaSiting siting = file.Config.Chroma != ChromaFormat.Yuv420 ? ChromaSiting.Center
            : file.Sequence.ChromaSamplePosition == 2 ? ChromaSiting.TopLeft : ChromaSiting.Left;
        if (YuvColor.TryFromH273(points.MatrixCoefficients, points.FullRange, siting, out YuvColor color))
        {
            return color;
        }

        metadata?.WarningList.Add("Avif.MatrixUnknown", $"The planes' matrix coefficients code {points.MatrixCoefficients} has no YuvMatrix; the planes are marked BT.601.");
        return new YuvColor(YuvMatrix.Bt601, points.FullRange ? YuvRange.Full : YuvRange.Limited, siting);
    }

    /// <summary>Gathers the metadata: ICC, code points, HDR, Exif, XMP, the orientation, the details, and the container's warnings.</summary>
    private static RasterMetadata Metadata(AvifFile file, AvifCodec codec, EgamiInput input)
    {
        RasterMetadata metadata = new() { FormatName = AvifFormat.Info.Name };
        metadata.IccProfile = file.Icc;
        metadata.CodePoints = file.CodePoints;
        HdrMetadata? hdr = null;
        if (file.MasteringDisplay is MasteringDisplay m)
        {
            // mdcv lists the primaries green, blue, red, in units of 0.00002, and the luminances in units of 0.0001 cd/m².
            ColorPrimaries display = new(Xy(m.PrimariesXY[4], m.PrimariesXY[5]), Xy(m.PrimariesXY[0], m.PrimariesXY[1]),
                Xy(m.PrimariesXY[2], m.PrimariesXY[3]), Xy(m.WhiteX, m.WhiteY));
            hdr = new HdrMetadata
            {
                MasteringPrimaries = display.IsValid ? display : null,
                MasteringMaxLuminance = m.MaxLuminance / 10_000.0,
                MasteringMinLuminance = m.MinLuminance / 10_000.0,
            };
        }

        if (file.ContentLightLevel is (int maxCll, int maxFall))
        {
            hdr = (hdr ?? new HdrMetadata()) with { MaxContentLightLevel = maxCll, MaxFrameAverageLightLevel = maxFall };
        }

        metadata.Hdr = hdr;
        foreach (HeifItem item in file.ExifItems)
        {
            ReadOnlySpan<byte> data = file.Heif.ItemData(item, input.Limits).Span;
            // An Exif item starts with the offset of the TIFF header after its own 4 bytes.
            if (data.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(data) <= (uint)(data.Length - 4))
            {
                int offset = 4 + (int)BinaryPrimitives.ReadUInt32BigEndian(data);
                metadata.Exif = Jpeg.JpegSegmentReader.ParseExif(data[offset..], input.Limits.MaxMetadataBytes, metadata.WarningList);
                break;
            }

            metadata.WarningList.Add("Avif.ExifDamaged", "An Exif item's TIFF header offset points past its end; the item is skipped.");
        }

        if (file.XmpItems.Count > 0)
        {
            metadata.Xmp = file.Heif.ItemData(file.XmpItems[0], input.Limits).ToArray();
        }

        // irot and imir are the orientation that counts (design question 9); AutoOrient reads it from EXIF.
        if (file.Orientation != ImageOrientation.Normal || metadata.Exif is not null)
        {
            metadata.Exif ??= new ExifData();
            metadata.Exif.Orientation = file.Orientation;
        }

        AvifDetails details = AvifDetails.From(file);
        details.FilmGrain = codec.FilmGrain;
        metadata.FormatDetails = details;
        foreach (HeifProblem problem in file.Heif.Problems)
        {
            metadata.WarningList.Add(problem.Id, problem.Message, problem.Offset);
        }

        return metadata;

        static Chromaticity Xy(ushort x, ushort y) => new(x * 0.00002, y * 0.00002);
    }

    /// <summary>Reads part of an output row into <paramref name="destination"/> through the natural type.</summary>
    private static void ReadRow<TPixel>(PixelRows rows, PixelLayoutInfo natural, int row, int column, Span<int> samples, Span<byte> packed, Span<TPixel> destination)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        bool direct = TPixel.Layout == natural;
        rows.Read(row, column, samples);
        Span<byte> target = direct ? MemoryMarshal.AsBytes(destination) : packed;
        if (rows.Depth > 8)
        {
            Span<ushort> words = MemoryMarshal.Cast<byte, ushort>(target);
            for (int i = 0; i < samples.Length; i++)
            {
                words[i] = (ushort)samples[i];
            }
        }
        else
        {
            for (int i = 0; i < samples.Length; i++)
            {
                target[i] = (byte)samples[i];
            }
        }

        if (!direct)
        {
            Convert(natural, packed, destination);
        }
    }

    /// <summary>The streaming source: the image decoded (only the grid cells a Region needs), rows converted as they are read.</summary>
    private sealed class AvifRows(EgamiInput input, Prepared image) : RowSource
    {
        private bool _started;
        private bool _disposed;

        public override int StoredWidth => image.Width;

        public override int StoredHeight => image.Height;

        public override int NativeScale => 1;

        public override int MaxNativeScale => 1;

        public override PixelLayoutInfo Layout => image.Natural;

        public override RasterMetadata Metadata => image.Metadata;

        public override int? FrameCount => 1;

        public override RasterAccess Access => RasterAccess.Streamed;

        public override long WorkingBytes => image.WorkingBytes;

        public override TResult WithNaturalType<TResult>(INaturalTypeVisitor<TResult> visitor) => image.Natural switch
        {
            _ when image.Natural == Gray8.Layout => visitor.Visit<Gray8>(),
            _ when image.Natural == GrayAlpha8.Layout => visitor.Visit<GrayAlpha8>(),
            _ when image.Natural == Rgb8.Layout => visitor.Visit<Rgb8>(),
            _ when image.Natural == Rgba8.Layout => visitor.Visit<Rgba8>(),
            _ when image.Natural == Gray16.Layout => visitor.Visit<Gray16>(),
            _ when image.Natural == GrayAlpha16.Layout => visitor.Visit<GrayAlpha16>(),
            _ when image.Natural == Rgb16.Layout => visitor.Visit<Rgb16>(),
            _ => visitor.Visit<Rgba16>(),
        };

        public override FrameMetadata? NextFrame()
        {
            if (_started)
            {
                return null;
            }

            _started = true;
            return new FrameMetadata();
        }

        public override void ReadRow<TPixel>(int y, int x, Span<TPixel> destination)
        {
            input.CancellationToken.ThrowIfCancellationRequested();
            int[] samples = new int[destination.Length * image.Rows.Channels];
            byte[] packed = new byte[destination.Length * image.Natural.BytesPerPixel];
            AvifReader.ReadRow(image.Rows, image.Natural, y, x, samples, packed, destination);
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                input.Dispose();
            }
        }
    }

    /// <summary>Where output rows come from: samples a pixel has, their depth (8 or 16), and how to read part of a row.</summary>
    private sealed record PixelRows(int Channels, int Depth, RowReader Read);

    /// <summary>What a read decodes: the output size, natural layout, rows, metadata and the bytes held.</summary>
    private sealed record Prepared(int Width, int Height, PixelLayoutInfo Natural, PixelRows Rows, RasterMetadata Metadata, long WorkingBytes);

    /// <summary>Reads output row <paramref name="row"/> from column <paramref name="column"/> into <paramref name="samples"/> (as many pixels as fit).</summary>
    private delegate void RowReader(int row, int column, Span<int> samples);
}
