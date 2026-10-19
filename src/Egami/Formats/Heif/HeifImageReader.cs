using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Colors;
using Egami.Formats.Avif;
using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Formats.Heif;

/// <summary>
/// Reads a HEIF file's still image through its codec (2.9.4 design, question 7): the container gives the items, the codec their
/// planes, and this layer the pixels: grids assembled (their cells decoded in parallel when the codec allows), overlays composed,
/// converted to RGB as libavif converts them, the clean aperture cropped, and irot and imir left as the orientation. Rows are
/// converted as they are read; a Region decodes only the grid cells it touches. AVIF and HEIC each make one with their codec.
/// </summary>
/// <param name="format">Gets the registration the metadata and RasterInfo name.</param>
/// <param name="ids">The format's diagnostic ids.</param>
/// <param name="createCodec">Makes the codec for one read.</param>
internal sealed partial class HeifImageReader(Func<FormatInfo> format, HeifDiagnosticIds ids, Func<ReaderOptions, IHeifCodec> createCodec)
{
    /// <summary>Gets or sets what adds the format's own details to the metadata after decoding.</summary>
    internal Action<HeifImage, IHeifCodec, RasterMetadata>? Describe { get; init; }

    /// <summary>
    /// Gets or sets what converts planes (color, alpha, whether premultiplied, code points) to RGB; libavif's conversion
    /// (<see cref="HeifRgbConverter"/>) unless set.
    /// </summary>
    internal Func<HeifConversion, IHeifRgbConverter> Converter { get; init; } =
        c => new HeifRgbConverter(c.Color, c.Alpha, c.Premultiplied, c.CodePoints);

    /// <summary>
    /// Gets or sets whether an overlay's 16-bit fill is cut to the output depth by dropping low bits (heif-dec) rather than rounded
    /// (libavif).
    /// </summary>
    internal bool TruncateFill { get; init; }

    /// <summary>
    /// Gets or sets whether an image is coded in layers whose base layer alone loads, at its own size (AVIF's a1lx without lsel).
    /// </summary>
    internal Func<HeifImage, bool>? Layered { get; init; }

    /// <summary>Gets the RasterInfo of a still image, <paramref name="size"/> replacing the output size when given.</summary>
    internal RasterInfo Info(HeifImage file, EgamiInput input, ReaderOptions options, (int Width, int Height)? size = null)
    {
        if (input.Reports(DiagnosticLevel.Warning))
        {
            // A probe has no raster to list warnings on, so skipped entries go to the handler directly.
            foreach (HeifProblem problem in file.Heif.Problems)
            {
                input.Diagnostics.Report(DiagnosticLevel.Warning, problem.Id, problem.Message, problem.Offset);
            }
        }

        (int width, int height) = size ?? OutputSize(file);
        input.Limits.CheckFrameSize(width, height);
        PixelLayoutInfo layout = options.Hdr is not null && (file.ToneMap is not null || file.AppleGainMap is not null) ? RgbaF16.Layout : NaturalLayout(file);
        return new RasterInfo
        {
            Format = format(),
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

    /// <summary>Reads the still image into its natural pixel type.</summary>
    internal Raster Read(EgamiInput input, HeifImage file, ReaderOptions options) => Natural(input, Prepare(input, file, options, null));

    /// <summary>Reads the still image into <typeparamref name="TPixel"/>.</summary>
    internal Raster<TPixel> Read<TPixel>(EgamiInput input, HeifImage file, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel> => Read<TPixel>(input, Prepare(input, file, options, null));

    /// <summary>Opens the still image as a row source, decoding only the grid cells <see cref="ReaderOptions.Region"/> needs.</summary>
    internal RowSource Open(EgamiInput input, HeifImage file, ReaderOptions options) => new Rows(input, Prepare(input, file, options, options.Region));

    /// <summary>Reads the still image's planes as they are coded.</summary>
    internal YuvRaster ReadYuv(EgamiInput input, HeifImage file, ReaderOptions options, string formatName)
    {
        if (file.Overlay is not null)
        {
            throw new NotSupportedException($"This {formatName} is an overlay of several images, which has no single set of YUV planes; load it with Raster.Load.");
        }

        IHeifCodec codec = createCodec(options);
        (HeifPlanes color, HeifPlanes? alpha) = Decode(file, codec, input, null, out ColorCodePoints points);
        RasterMetadata metadata = Metadata(file, codec, input, points);
        YuvColor yuvColor = PlaneColor(points, file.ChromaSiting, metadata);
        return color.BitDepth == 8
            ? Planes<byte>(color, alpha, yuvColor, metadata, input.Context)
            : Planes<ushort>(color, alpha, yuvColor, metadata, input.Context);
    }

    /// <summary>Probes the still image's planes: the info, the chroma format (none for an overlay), the depth and the encoding.</summary>
    internal (RasterInfo Info, ChromaFormat? Format, int BitDepth, YuvColor Color) ProbeYuv(HeifImage file)
    {
        RasterInfo info = new()
        {
            Format = format(),
            Width = file.Width,
            Height = file.Height,
            Layout = NaturalLayout(file),
            FrameCount = 1,
            ImageCount = 1,
            Orientation = file.Orientation,
        };
        return (info, file.Overlay is null ? file.Chroma : null, file.BitDepth, PlaneColor(file.CodePoints, file.ChromaSiting, null));
    }

    /// <summary>
    /// Reads the HDR rendition as Ultra HDR JPEGs are read: the SDR base <paramref name="read"/> gives, with its metadata, then
    /// its gain map applied for the display's headroom (2.9.2 design, question 12). Without a gain map the SDR base is returned.
    /// </summary>
    internal static Raster ReadHdr(HdrDecode hdr, Func<Raster> read)
    {
        Raster sdr = read();
        if (sdr.Metadata.GainMap is not GainMap map)
        {
            return sdr;
        }

        using (sdr)
        {
            Raster<RgbaF16> result = map.Apply(sdr, hdr.Headroom);
            RasterMetadata metadata = result.Metadata;
            metadata.FormatName = sdr.Metadata.FormatName;
            metadata.FormatDetails = sdr.Metadata.FormatDetails;
            metadata.Exif = sdr.Metadata.Exif;
            metadata.Xmp = sdr.Metadata.Xmp;
            metadata.WarningList.Prepend(sdr.Metadata.WarningList);
            return result;
        }
    }

    /// <summary>
    /// Gets the natural pixel layout (2.9.2 design, question 7): 8-bit types for 8-bit files and 16-bit ones for 10 and 12 bits;
    /// gray for monochrome; with alpha when there is an alpha item.
    /// </summary>
    internal static PixelLayoutInfo NaturalLayout(int bitDepth, bool monochrome, bool alpha)
    {
        return bitDepth <= 8
            ? (monochrome ? (alpha ? GrayAlpha8.Layout : Gray8.Layout) : alpha ? Rgba8.Layout : Rgb8.Layout)
            : (monochrome ? (alpha ? GrayAlpha16.Layout : Gray16.Layout) : alpha ? Rgba16.Layout : Rgb16.Layout);
    }

    /// <summary>Gets the natural layout of a file.</summary>
    internal static PixelLayoutInfo NaturalLayout(HeifImage file) => NaturalLayout(file.BitDepth, file.Monochrome, file.HasAlpha);

    /// <summary>
    /// Decodes the image and its alpha into planes; the alpha must match the image's size. With <paramref name="needed"/> (in image
    /// coordinates), only the grid cells it touches are decoded and the rest of the planes stay zero.
    /// </summary>
    internal static (HeifPlanes Color, HeifPlanes? Alpha) Decode(HeifImage file, IHeifCodec codec, EgamiInput input, Rect? needed) =>
        Decode(file, codec, input, needed, out _);

    /// <inheritdoc cref="Decode(HeifImage, IHeifCodec, EgamiInput, Rect?)"/>
    /// <param name="file">The image.</param>
    /// <param name="codec">The codec.</param>
    /// <param name="input">The input.</param>
    /// <param name="needed">The part of the image needed, or null for all of it.</param>
    /// <param name="points">The image's color: the nclx property's, else what the codec read from the color image's stream.</param>
    internal static (HeifPlanes Color, HeifPlanes? Alpha) Decode(HeifImage file, IHeifCodec codec, EgamiInput input, Rect? needed, out ColorCodePoints points)
    {
        input.CheckMemory(WorkingBytes(file));
        HeifPlanes color = file.Primary.Type == HeifImage.Grid
            ? Grid(file, codec, file.Cells, (file.GridSize.Rows, file.GridSize.Columns, file.Width, file.Height), input, needed, lumaOnly: false)
            : Cell(file, codec, file.Cells[0], input);
        if (color.BitDepth != file.BitDepth || color.Monochrome != file.Monochrome)
        {
            // The layout Load gives comes from the coding configuration, so the image must match it.
            throw input.Corrupt(-1, $"The image is {color.BitDepth}-bit{(color.Monochrome ? " monochrome" : "")}, but its configuration says {file.BitDepth}-bit{(file.Monochrome ? " monochrome" : "")}.");
        }

        points = file.Nclx is null ? codec.CodePoints : file.CodePoints;

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

    /// <summary>
    /// Decodes a grid's cells, those <paramref name="needed"/> touches (with a margin for chroma upsampling) or all, and tiles them.
    /// When the codec allows, the cells decode on the context's threads, each on one; the output is the same at any thread count.
    /// </summary>
    internal static HeifPlanes Grid(HeifImage file, IHeifCodec codec, IReadOnlyList<HeifItem> cells, (int Rows, int Columns, int Width, int Height) layout,
        EgamiInput input, Rect? needed, bool lumaOnly)
    {
        (int cellWidth, int cellHeight) = file.Extents(cells[0]);
        HeifPlanes?[] decoded = new HeifPlanes?[cells.Count];
        List<int> wanted = [];
        for (int t = 0; t < cells.Count; t++)
        {
            int x0 = cellWidth * (t % layout.Columns), y0 = cellHeight * (t / layout.Columns);
            bool touched = needed is not Rect r
                || (x0 < r.X + r.Width + 2 && x0 + cellWidth > r.X - 2 && y0 < r.Y + r.Height + 2 && y0 + cellHeight > r.Y - 2);
            if (touched || t == 0)
            {
                wanted.Add(t);
            }
        }

        void DecodeCell(int t, int parallelism)
        {
            HeifPlanes planes = codec.Decode(file, cells[t], input, parallelism);
            decoded[t] = lumaOnly ? planes.LumaOnly() : planes;
        }

        int threads = input.Context.MaxParallelism;
        if (codec.Concurrent && threads > 1 && wanted.Count > 1)
        {
            try
            {
                Parallel.For(0, wanted.Count, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = input.CancellationToken },
                    i => DecodeCell(wanted[i], 1));
            }
            catch (AggregateException e) when (e.InnerExceptions.Count > 0)
            {
                // A damaged cell throws as it would on one thread.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
            }
        }
        else
        {
            foreach (int t in wanted)
            {
                input.CancellationToken.ThrowIfCancellationRequested();
                DecodeCell(t, threads);
            }
        }

        return HeifPlanes.Grid(decoded, layout.Rows, layout.Columns, layout.Width, layout.Height, input);
    }

    /// <summary>
    /// Reads image item <paramref name="itemId"/>: a coded item (of <paramref name="codedType"/>) or a grid, with its own color and
    /// clean aperture, and no alpha; its own irot and imir are given as the orientation.
    /// </summary>
    internal Raster ReadItem(EgamiInput input, HeifImage file, uint itemId, ReaderOptions options, uint codedType, string codedName)
    {
        HeifFile heif = file.Heif;
        string name = format().Name;
        HeifItem item = heif.Item(itemId) ?? throw new ArgumentException($"The {name} file has no item {itemId}.", nameof(itemId));
        if (item.Type != codedType && item.Type != HeifImage.Grid)
        {
            throw new ArgumentException($"Item {itemId} is a \"{FourCC.ToString(item.Type)}\" item, not an image ({codedName} or grid).", nameof(itemId));
        }

        IHeifCodec codec = createCodec(options);
        HeifItem first = item;
        HeifPlanes planes;
        if (item.Type == HeifImage.Grid)
        {
            (List<HeifItem> cells, int rows, int columns, int width, int height) = file.ReadGrid(item);
            planes = Grid(file, codec, cells, (rows, columns, width, height), input, null, lumaOnly: false);
            first = cells[0];
        }
        else
        {
            planes = Cell(file, codec, item, input);
        }

        NclxColor? nclx = null;
        ReadOnlyMemory<byte> icc = default;
        (int X, int Y, int Width, int Height)? crop = null;
        foreach (HeifItem source in (HeifItem[])[item, first])
        {
            foreach ((HeifProperty property, _) in heif.PropertiesOf(source))
            {
                if (property.Type == AvifProperties.Colr)
                {
                    (NclxColor? n, ReadOnlyMemory<byte> profile) = AvifProperties.ReadColor(heif, property);
                    nclx ??= n;
                    icc = icc.IsEmpty ? profile : icc;
                }
                else if (property.Type == AvifProperties.Clap && source == item)
                {
                    crop = AvifProperties.ReadCleanAperture(heif, property).CropRectangle(planes.Width, planes.Height);
                }
            }
        }

        ColorCodePoints points = nclx is NclxColor c ? new ColorCodePoints((byte)c.Primaries, (byte)c.Transfer, (byte)c.Matrix, c.FullRange) : codec.CodePoints;
        IHeifRgbConverter converter = Converter(new HeifConversion(planes, null, false, points, false));
        (int x, int y, int w, int h) = crop ?? (0, 0, planes.Width, planes.Height);
        input.Limits.CheckFrameSize(w, h);
        PixelRows rows2 = new(converter.Channels, converter.OutputDepth, (row, column, samples) => converter.Row(y + row, x + column, samples.Length / converter.Channels, samples))
        {
            Write = (row, column, width, target) => converter.Write(y + row, x + column, width, target),
        };
        RasterMetadata metadata = new() { FormatName = name, IccProfile = icc, CodePoints = RasterCodePoints(points, icc) };
        if (!converter.Supported)
        {
            metadata.WarningList.Add(ids.MatrixApproximated, $"Matrix coefficients {points.MatrixCoefficients} are not converted with this range and chroma; the nearest matrix was used.");
        }

        ImageOrientation orientation = file.OrientationOf(item);
        if (orientation != ImageOrientation.Normal)
        {
            metadata.Exif = new ExifData { Orientation = orientation };
        }

        PixelLayoutInfo natural = NaturalLayout(planes.BitDepth, planes.Monochrome, false);
        return Natural(input, new Prepared(w, h, natural, rows2, metadata, 0));
    }

    /// <summary>Decodes one coded item.</summary>
    internal static HeifPlanes Cell(HeifImage file, IHeifCodec codec, HeifItem item, EgamiInput input) =>
        codec.Decode(file, item, input, input.Context.MaxParallelism);

    /// <summary>Reads a prepared image into its natural pixel type.</summary>
    internal static Raster Natural(EgamiInput input, Prepared image)
    {
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

    /// <summary>Reads a prepared image into <typeparamref name="TPixel"/>.</summary>
    internal static Raster<TPixel> Read<TPixel>(EgamiInput input, Prepared image)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Raster<TPixel> raster = input.CreateRaster<TPixel>(image.Width, image.Height);
        try
        {
            Fill(raster.Width, raster.Height, y => raster.Row(y), image.Rows, image.Natural, input);
            Adopt(raster.Metadata, image.Metadata);
            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    /// <summary>Gets the size Load gives: the clean aperture's, or the image's.</summary>
    internal static (int Width, int Height) OutputSize(HeifImage file) => file.Crop is (_, _, int w, int h) ? (w, h) : (file.Width, file.Height);

    /// <summary>Gets the bytes a decode holds besides the raster: the planes of the image and of its alpha, at two bytes a sample.</summary>
    internal static long WorkingBytes(HeifImage file)
    {
        long luma = (long)file.Width * file.Height;
        long chroma = file.Monochrome ? 0
            : 2L * ((file.Width + file.SubsamplingX) >> file.SubsamplingX) * ((file.Height + file.SubsamplingY) >> file.SubsamplingY);
        return 2 * (luma + chroma + (file.Alpha is null ? 0 : luma));
    }

    /// <summary>
    /// Decodes what the output needs and returns how to read its rows: the planes and their converter, or an overlay's composed
    /// canvas; with the metadata. <paramref name="region"/> (in output coordinates) limits the grid cells decoded.
    /// </summary>
    private Prepared Prepare(EgamiInput input, HeifImage file, ReaderOptions options, Rect? region)
    {
        (int width, int height) = OutputSize(file);
        input.Limits.CheckFrameSize(width, height);
        (int x, int y) = file.Crop is (int cx, int cy, _, _) ? (cx, cy) : (0, 0);
        bool layered = file.Overlay is null && Layered?.Invoke(file) == true;
        PixelLayoutInfo natural = NaturalLayout(file);
        IHeifCodec codec = createCodec(options);
        ColorCodePoints points = file.CodePoints;
        PixelRows rows;
        List<string> unsupported = [];
        if (file.Overlay is HeifOverlay overlay)
        {
            int[] canvas = Overlay(file, codec, overlay, input, unsupported, out int channels, out int depth);
            points = file.Nclx is null ? codec.CodePoints : file.CodePoints;
            rows = new PixelRows(channels, depth, (row, column, samples) =>
                canvas.AsSpan((((y + row) * overlay.Width) + x + column) * channels, samples.Length).CopyTo(samples));
        }
        else
        {
            Rect? needed = region is Rect r ? r with { X = r.X + x, Y = r.Y + y } : null;
            (HeifPlanes color, HeifPlanes? alpha) = Decode(file, codec, input, needed, out points);
            if (layered && (color.Width, color.Height) != (file.Width, file.Height))
            {
                // A spatially layered image's base layer is smaller than the image; it loads whole, at its own size.
                (x, y, width, height) = (0, 0, color.Width, color.Height);
            }

            if (x + width > color.Width || y + height > color.Height)
            {
                throw input.Corrupt(-1, $"The image is {color.Width} x {color.Height}, smaller than its {width} x {height} clean aperture at ({x}, {y}).");
            }

            IHeifRgbConverter converter = Converter(new HeifConversion(color, alpha, file.AlphaPremultiplied, points, false));
            if (!converter.Supported)
            {
                unsupported.Add("color");
            }

            rows = new PixelRows(converter.Channels, converter.OutputDepth, (row, column, samples) =>
                converter.Row(y + row, x + column, samples.Length / converter.Channels, samples))
            {
                Write = (row, column, width, target) => converter.Write(y + row, x + column, width, target),
            };
        }

        RasterMetadata metadata = Metadata(file, codec, input, points);
        metadata.CodePoints = RasterCodePoints(metadata.CodePoints, metadata.IccProfile);
        if (file.ToneMap is not null && options.ReadMetadata)
        {
            ReadGainMap(file, codec, input, metadata);
        }
        else if (file.AppleGainMap is not null && options.ReadMetadata)
        {
            ReadAppleGainMap(file, codec, input, metadata);
        }

        if (layered)
        {
            metadata.WarningList.Add(ids.BaseLayerOnly!, "The image is coded in layers whose later ones are inter frames, which Egami decodes from 2.9.5; its first (lowest quality) layer was loaded.");
        }

        if (unsupported.Count > 0)
        {
            metadata.WarningList.Add(ids.MatrixApproximated, $"Matrix coefficients {points.MatrixCoefficients} are not converted with this range and chroma; the nearest matrix was used.");
        }

        return new Prepared(width, height, natural, rows, metadata, WorkingBytes(file));
    }

    /// <summary>
    /// Reads the tmap item's ISO 21496-1 metadata and decodes its gain map image (converted with its own color, 8 bits a sample)
    /// into <see cref="RasterMetadata.GainMap"/>. A gain map that cannot be read never fails the load: it is dropped with a
    /// GainMapDropped warning.
    /// </summary>
    private void ReadGainMap(HeifImage file, IHeifCodec codec, EgamiInput input, RasterMetadata metadata)
    {
        string problem;
        try
        {
            byte[] data = file.Heif.ItemData(file.ToneMap!, input.Limits).ToArray();
            if (data.Length < 1 || data[0] != 0)
            {
                problem = data.Length < 1 ? "the tmap item is empty" : $"tmap version {data[0]} is unknown";
            }
            else if (Jpeg.JpegGainMap.ParseIso(data[1..], out string? why) is not GainMapInfo parsed)
            {
                problem = why ?? "its metadata is damaged";
            }
            else
            {
                // The tmap says which rendition the base is by its headrooms alone, as libavif reads it: an alternate headroom below
                // the base's makes the base the HDR one, and the map is applied toward SDR as the display's headroom drops.
                GainMapInfo info = parsed.HdrCapacityMax < parsed.HdrCapacityMin
                    ? parsed with { BaseRenditionIsHdr = true, HdrCapacityMin = parsed.HdrCapacityMax, HdrCapacityMax = parsed.HdrCapacityMin }
                    : parsed with { BaseRenditionIsHdr = false };
                (int rows, int columns, _, _) = file.GainMapLayout;
                HeifPlanes planes = file.GainMapCells.Count == 1 && rows * columns == 1
                    ? Cell(file, codec, file.GainMapCells[0], input)
                    : Grid(file, codec, file.GainMapCells, file.GainMapLayout, input, null, lumaOnly: false);
                ColorCodePoints points = GainMapColor(file) ?? codec.CodePoints;
                IHeifRgbConverter converter = Converter(new HeifConversion(planes, null, false, points, false));
                int channels = converter.Channels;
                byte[] pixels = Pixels8(converter, planes.Width, planes.Height);
                metadata.GainMap = new GainMap(info, planes.Width, planes.Height, channels, pixels);
                return;
            }
        }
        catch (EgamiException e)
        {
            problem = e.Message;
        }

        metadata.WarningList.Add(ids.GainMapDropped, $"The gain map could not be read ({problem}); the image loads as SDR only.");
    }

    /// <summary>
    /// Reads Apple's HDRGainMap auxiliary image into <see cref="RasterMetadata.GainMap"/>: its luma, 8 bits a sample, with the
    /// headroom from the map's XMP or the base image's maker note, as for Apple's JPEG files. A map that cannot be read is dropped
    /// with a GainMapDropped warning.
    /// </summary>
    private void ReadAppleGainMap(HeifImage file, IHeifCodec codec, EgamiInput input, RasterMetadata metadata)
    {
        string problem;
        try
        {
            XmpData? xmp = file.AppleGainMapXmp is HeifItem x ? XmpData.Parse(file.Heif.ItemData(x, input.Limits).Span, input.Limits) : null;
            if (Jpeg.JpegGainMap.Apple(xmp, metadata.Exif) is not GainMapInfo info)
            {
                problem = "it has no headroom in its XMP or the maker note";
            }
            else
            {
                (int rows, int columns, _, _) = file.GainMapLayout;
                HeifPlanes planes = (file.GainMapCells.Count == 1 && rows * columns == 1
                    ? Cell(file, codec, file.GainMapCells[0], input)
                    : Grid(file, codec, file.GainMapCells, file.GainMapLayout, input, null, lumaOnly: true)).LumaOnly();
                ColorCodePoints points = GainMapColor(file, file.AppleGainMap!) ?? codec.CodePoints;
                IHeifRgbConverter converter = Converter(new HeifConversion(planes, null, false, points, false));
                byte[] pixels = Pixels8(converter, planes.Width, planes.Height);
                (int width, int height) = (planes.Width, planes.Height);

                // The map may be stored turned differently from the image (iPhones store it landscape with its own irot): turn it as it
                // displays, then back as the image is stored, so it lies over the image's stored pixels.
                ImageOrientation mapOrientation = file.OrientationOf(file.AppleGainMap!);
                if (mapOrientation != file.Orientation)
                {
                    (pixels, width, height) = Turn(pixels, width, height, mapOrientation);
                    (pixels, width, height) = Turn(pixels, width, height, Inverse(file.Orientation));
                }

                metadata.GainMap = new GainMap(info, width, height, 1, pixels);
                return;
            }
        }
        catch (EgamiException e)
        {
            problem = e.Message;
        }

        metadata.WarningList.Add(ids.GainMapDropped, $"The gain map could not be read ({problem}); the image loads as SDR only.");
    }

    /// <summary>Turns one-sample-a-pixel rows as an EXIF orientation displays them (what AutoOrient does).</summary>
    private static (byte[] Pixels, int Width, int Height) Turn(byte[] pixels, int width, int height, ImageOrientation orientation)
    {
        if (orientation == ImageOrientation.Normal)
        {
            return (pixels, width, height);
        }

        bool swap = orientation is ImageOrientation.Transpose or ImageOrientation.Rotate90 or ImageOrientation.Transverse or ImageOrientation.Rotate270;
        (int w, int h) = swap ? (height, width) : (width, height);
        byte[] turned = new byte[pixels.Length];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                (int sx, int sy) = orientation switch
                {
                    ImageOrientation.FlipHorizontal => (width - 1 - x, y),
                    ImageOrientation.Rotate180 => (width - 1 - x, height - 1 - y),
                    ImageOrientation.FlipVertical => (x, height - 1 - y),
                    ImageOrientation.Transpose => (y, x),
                    ImageOrientation.Rotate90 => (y, height - 1 - x),
                    ImageOrientation.Transverse => (width - 1 - y, height - 1 - x),
                    _ => (width - 1 - y, x),
                };
                turned[(y * w) + x] = pixels[(sy * width) + sx];
            }
        }

        return (turned, w, h);
    }

    /// <summary>Gets the orientation that undoes another.</summary>
    private static ImageOrientation Inverse(ImageOrientation orientation) => orientation switch
    {
        ImageOrientation.Rotate90 => ImageOrientation.Rotate270,
        ImageOrientation.Rotate270 => ImageOrientation.Rotate90,
        _ => orientation,
    };

    /// <summary>Converts planes to 8-bit samples, as gain map pixels are kept.</summary>
    private static byte[] Pixels8(IHeifRgbConverter converter, int width, int height)
    {
        int channels = converter.Channels;
        byte[] pixels = new byte[width * height * channels];
        byte[] row = new byte[width * channels * 2];
        for (int y = 0; y < height; y++)
        {
            converter.Write(y, 0, width, row);
            Span<byte> target = pixels.AsSpan(y * width * channels, width * channels);
            if (converter.OutputDepth > 8)
            {
                ReadOnlySpan<ushort> words = MemoryMarshal.Cast<byte, ushort>(row);
                for (int i = 0; i < target.Length; i++)
                {
                    target[i] = (byte)(((words[i] * 255) + 32767) / 65535);
                }
            }
            else
            {
                row.AsSpan(0, target.Length).CopyTo(target);
            }
        }

        return pixels;
    }

    /// <summary>Gets the gain map image's nclx color, from its item or its first cell, or null to take its stream's.</summary>
    private static ColorCodePoints? GainMapColor(HeifImage file) => GainMapColor(file, file.Heif.Item(file.ToneMap!.ReferencesOf(HeifImage.Dimg)[1])!);

    /// <summary>Gets a gain map item's nclx color, from the item or its first cell, or null to take its stream's.</summary>
    private static ColorCodePoints? GainMapColor(HeifImage file, HeifItem map)
    {
        HeifFile heif = file.Heif;
        foreach (HeifItem item in (HeifItem[])[map, file.GainMapCells[0]])
        {
            foreach ((HeifProperty property, _) in heif.PropertiesOf(item))
            {
                if (property.Type == Avif.AvifProperties.Colr && Avif.AvifProperties.ReadColor(heif, property).Nclx is NclxColor n)
                {
                    return new ColorCodePoints((byte)n.Primaries, (byte)n.Transfer, (byte)n.Matrix, n.FullRange);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the code points of a raster's RGB pixels from the file's: matrix 0 and full range, unspecified primaries or transfer as
    /// BT.709's and sRGB's (as libavif takes them), and none when an ICC profile says what the pixels mean (2.9.2 design, question 8).
    /// </summary>
    internal static ColorCodePoints? RasterCodePoints(ColorCodePoints? points, ReadOnlyMemory<byte> icc) =>
        icc.IsEmpty && points is ColorCodePoints p
            ? p with
            {
                Primaries = p.Primaries == 2 ? (byte)1 : p.Primaries,
                TransferCharacteristics = p.TransferCharacteristics == 2 ? (byte)13 : p.TransferCharacteristics,
                MatrixCoefficients = 0,
                FullRange = true,
            }
            : null;

    /// <summary>Moves what <see cref="Metadata"/> gathered onto the raster's metadata.</summary>
    internal static void Adopt(RasterMetadata target, RasterMetadata source)
    {
        target.IccProfile = source.IccProfile;
        target.CodePoints = source.CodePoints;
        target.Hdr = source.Hdr;
        target.Exif = source.Exif;
        target.Xmp = source.Xmp;
        target.FormatDetails = source.FormatDetails;
        target.GainMap = source.GainMap;
        foreach (EgamiDiagnostic warning in source.Warnings)
        {
            target.WarningList.Add(warning);
        }
    }

    /// <summary>
    /// Composes an overlay (HEIF 6.6.2.3.3) in the natural type's samples: the canvas filled with its RGBA fill (scaled to the
    /// output depth), then each layer converted to RGB and drawn over it in order at its offset, opaque, clipped to the canvas.
    /// </summary>
    private int[] Overlay(HeifImage file, IHeifCodec codec, HeifOverlay overlay, EgamiInput input, List<string> unsupported, out int channels, out int depth)
    {
        bool mono = file.Monochrome, alpha = file.HasAlpha;
        int color = mono ? 1 : 3;
        channels = color + (alpha ? 1 : 0);
        depth = file.BitDepth > 8 ? 16 : 8;
        int max = (1 << depth) - 1;
        long size = (long)overlay.Width * overlay.Height * channels;
        input.CheckMemory((size * sizeof(int)) + WorkingBytes(file));
        int[] canvas = new int[size];
        Span<int> fill = stackalloc int[4];
        for (int c = 0; c < 4; c++)
        {
            fill[c] = TruncateFill ? overlay.Fill[c] >> (16 - depth) : (int)(((overlay.Fill[c] * (long)max) + 32767) / 65535);
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
            if (layer.BitDepth != file.BitDepth || layer.Monochrome != mono)
            {
                throw input.Corrupt(-1, "The overlay's layers differ in depth or in being monochrome.");
            }

            IHeifRgbConverter converter = Converter(new HeifConversion(layer, null, false, file.Nclx is null ? codec.CodePoints : file.CodePoints, true));
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

    /// <summary>Converts rows of samples into the rows <paramref name="target"/> gives, in bands on several threads for a large image.</summary>
    internal static void Fill<TPixel>(int width, int height, RowTarget<TPixel> target, PixelRows source, PixelLayoutInfo natural, EgamiInput input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        void Rows(int from, int to)
        {
            int[] samples = new int[width * source.Channels];
            byte[] packed = new byte[width * natural.BytesPerPixel];
            for (int row = from; row < to; row++)
            {
                input.CancellationToken.ThrowIfCancellationRequested();
                ReadRow(source, natural, row, 0, samples, packed, target(row));
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
    internal static YuvRaster<TSample> Planes<TSample>(HeifPlanes color, HeifPlanes? alpha, YuvColor yuvColor, RasterMetadata metadata, EgamiContext context)
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
    /// Gets the planes' encoding: the matrix and range of the color code points (2.9.2 design, question 8), with BT.601 for a matrix
    /// <see cref="YuvColor"/> has no member for, and the chroma siting.
    /// </summary>
    internal YuvColor PlaneColor(ColorCodePoints points, ChromaSiting siting, RasterMetadata? metadata)
    {
        if (YuvColor.TryFromH273(points.MatrixCoefficients, points.FullRange, siting, out YuvColor color))
        {
            return color;
        }

        metadata?.WarningList.Add(ids.MatrixUnknown, $"The planes' matrix coefficients code {points.MatrixCoefficients} has no YuvMatrix; the planes are marked BT.601.");
        return new YuvColor(YuvMatrix.Bt601, points.FullRange ? YuvRange.Full : YuvRange.Limited, siting);
    }

    /// <summary>Gathers the metadata: ICC, code points, HDR, Exif, XMP, the orientation, the details, and the container's warnings.</summary>
    private RasterMetadata Metadata(HeifImage file, IHeifCodec codec, EgamiInput input, ColorCodePoints points)
    {
        RasterMetadata metadata = new() { FormatName = format().Name };
        metadata.IccProfile = file.Icc;
        metadata.CodePoints = points;
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

            metadata.WarningList.Add(ids.ExifDamaged, "An Exif item's TIFF header offset points past its end; the item is skipped.");
        }

        if (file.XmpItems.Count > 0)
        {
            metadata.Xmp = file.Heif.ItemData(file.XmpItems[0], input.Limits).ToArray();
        }

        // irot and imir are the orientation that counts (2.9.2 design, question 9); AutoOrient reads it from EXIF.
        if (file.Orientation != ImageOrientation.Normal || metadata.Exif is not null)
        {
            metadata.Exif ??= new ExifData();
            metadata.Exif.Orientation = file.Orientation;
        }

        Describe?.Invoke(file, codec, metadata);
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
        Span<byte> target = direct ? MemoryMarshal.AsBytes(destination) : packed;
        if (rows.Write is RowWriter write)
        {
            write(row, column, destination.Length, target);
            if (!direct)
            {
                Convert(natural, packed, destination);
            }

            return;
        }

        rows.Read(row, column, samples);
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
    private sealed class Rows(EgamiInput input, Prepared image) : RowSource
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
            HeifImageReader.ReadRow(image.Rows, image.Natural, y, x, samples, packed, destination);
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
    internal sealed record PixelRows(int Channels, int Depth, RowReader Read)
    {
        /// <summary>Gets how to write part of a row straight into natural samples, when the source can.</summary>
        internal RowWriter? Write { get; init; }
    }

    /// <summary>What a read decodes: the output size, natural layout, rows, metadata and the bytes held.</summary>
    internal sealed record Prepared(int Width, int Height, PixelLayoutInfo Natural, PixelRows Rows, RasterMetadata Metadata, long WorkingBytes);

    /// <summary>Reads output row <paramref name="row"/> from column <paramref name="column"/> into <paramref name="samples"/> (as many pixels as fit).</summary>
    internal delegate void RowReader(int row, int column, Span<int> samples);

    /// <summary>Gets row <paramref name="y"/> of what Fill writes into.</summary>
    internal delegate Span<TPixel> RowTarget<TPixel>(int y)
        where TPixel : unmanaged, IPixelType<TPixel>;

    /// <summary>Writes <paramref name="width"/> pixels of output row <paramref name="row"/> from column <paramref name="column"/> into <paramref name="target"/> as natural samples.</summary>
    internal delegate void RowWriter(int row, int column, int width, Span<byte> target);
}

/// <summary>The diagnostic ids a format's HEIF image layer reports, written out so the catalog test finds them.</summary>
/// <param name="MatrixApproximated">The matrix is converted with the nearest one the reference decoder has.</param>
/// <param name="MatrixUnknown">The planes' matrix has no <see cref="YuvMatrix"/>.</param>
/// <param name="ExifDamaged">An Exif item's TIFF header offset points past its end.</param>
/// <param name="GainMapDropped">The gain map could not be read.</param>
/// <param name="SequenceFirstFrame">A sequence's frames are not all key frames, so only the first loaded.</param>
/// <param name="FramesDropped">A damaged frame ended a sequence's lenient load.</param>
/// <param name="InterFrames">Why a sequence with inter frames gives its first frame only, as the end of a sentence.</param>
/// <param name="BaseLayerOnly">Only a layered image's base layer was loaded, for formats that have layers.</param>
internal sealed record HeifDiagnosticIds(string MatrixApproximated, string MatrixUnknown, string ExifDamaged, string GainMapDropped,
    string SequenceFirstFrame, string FramesDropped, string InterFrames, string? BaseLayerOnly = null);
