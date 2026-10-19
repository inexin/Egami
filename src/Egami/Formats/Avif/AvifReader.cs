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
/// decoder their planes, and the HEIF image layer (<see cref="HeifImageReader"/>) the pixels: grids assembled, overlays composed,
/// converted to RGB as libavif converts them, the clean aperture cropped, and irot and imir left as the orientation (design
/// question 9). Rows are converted as they are read, after the in-loop filters; a Region decodes only the grid cells it touches
/// (design question 11). Image sequences are read here.
/// </summary>
internal sealed partial class AvifReader : IFormatReader, IYuvFormatReader, IRowSourceReader
{
    internal static readonly AvifReader Instance = new();

    private static readonly HeifDiagnosticIds Ids = new("Avif.MatrixApproximated", "Avif.MatrixUnknown", "Avif.ExifDamaged", "Avif.GainMapDropped", "Avif.BaseLayerOnly");

    /// <summary>The HEIF image layer with the AV1 codec, film grain as the options say, and AvifDetails.</summary>
    private static readonly HeifImageReader Still = new(() => AvifFormat.Info, Ids, Codec)
    {
        Describe = (file, codec, metadata) =>
        {
            AvifDetails details = AvifDetails.From((AvifFile)file);
            details.FilmGrain = ((AvifCodec)codec).FilmGrain;
            details.Lossless = ((AvifCodec)codec).Lossless;
            metadata.FormatDetails = details;
        },
        Layered = file => file.Heif.Property(file.Cells[0], AvifProperties.A1lx) is not null,
    };

    public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        HeifFile heif = HeifFile.Read(input);
        if (AvifSequence.Find(heif) is AvifSequence sequence)
        {
            return SequenceInfo(sequence, input, options);
        }

        AvifFile file = AvifFile.Open(heif);
        (int Width, int Height)? size = null;
        if (file.Overlay is null && file.Primary.Type != AvifFile.Grid && file.Heif.Property(file.Cells[0], AvifProperties.A1lx) is not null
            && Av1.Av1Decoder.FirstFrameSize(file.Heif.ItemData(file.Cells[0], input.Limits).ToArray(), input.FormatName) is (int w, int h)
            && (w, h) != (file.Width, file.Height))
        {
            // Load gives a spatially layered image's base layer, at its own size (design question 15).
            size = (w, h);
        }

        return Still.Info(file, input, options, size);
    }

    public Raster Read(EgamiInput input, ReaderOptions options)
    {
        if (options.Hdr is HdrDecode hdr)
        {
            // As Ultra HDR JPEGs: the SDR base, then its gain map applied for the display's headroom (design question 12).
            return HeifImageReader.ReadHdr(hdr, () => Read(input, options with { Hdr = null, ReadMetadata = true }));
        }

        HeifFile heif = HeifFile.Read(input);
        if (AvifSequence.Find(heif) is AvifSequence sequence)
        {
            return ReadSequence(input, sequence, options);
        }

        return Still.Read(input, AvifFile.Open(heif), options);
    }

    public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (options.Hdr is not null)
        {
            Raster hdr = Read(input, options);
            if (hdr is Raster<TPixel> typed)
            {
                return typed;
            }

            using (hdr)
            {
                return hdr.As<TPixel>();
            }
        }

        HeifFile heif = HeifFile.Read(input);
        return AvifSequence.Find(heif) is AvifSequence sequence
            ? ReadSequence<TPixel>(input, sequence, options)
            : Still.Read<TPixel>(input, AvifFile.Open(heif), options);
    }

    public RowSource Open(EgamiInput input, ReaderOptions options)
    {
        try
        {
            HeifFile heif = HeifFile.Read(input);
            if (AvifSequence.Find(heif) is AvifSequence sequence)
            {
                // A sequence is read whole, its frames served from the raster.
                return BufferedRowSource.From(ReadSequence(input, sequence, options));
            }

            return Still.Open(input, AvifFile.Open(heif), options);
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    public YuvRaster ReadYuv(EgamiInput input, ReaderOptions options)
    {
        HeifFile heif = HeifFile.Read(input);
        return AvifSequence.Find(heif) is AvifSequence sequence
            ? SequenceYuv(sequence, input, options)
            : Still.ReadYuv(input, AvifFile.Open(heif), options, "AVIF");
    }

    public (RasterInfo Info, ChromaFormat? Format, int BitDepth, YuvColor Color) ProbeYuv(EgamiInput input, ReaderOptions options)
    {
        HeifFile heif = HeifFile.Read(input);
        if (AvifSequence.Find(heif) is AvifSequence sequence)
        {
            RasterInfo first = SequenceInfo(sequence, input, options);
            return (first, sequence.Config.Chroma, sequence.Config.BitDepth, PlaneColor(sequence.CodePoints, sequence.Config, sequence.Sequence, null));
        }

        return Still.ProbeYuv(AvifFile.Open(heif));
    }

    /// <summary>Gets the natural pixel layout (design question 7).</summary>
    internal static PixelLayoutInfo NaturalLayout(int bitDepth, bool monochrome, bool alpha) => HeifImageReader.NaturalLayout(bitDepth, monochrome, alpha);

    /// <summary>Makes the AV1 codec for one read: film grain applied unless the options turn it off (design question 6).</summary>
    private static AvifCodec Codec(ReaderOptions options) => new(options is not AvifReaderOptions { FilmGrain: false });

    /// <summary>
    /// Reads image item <paramref name="itemId"/> for <see cref="AvifImage.ReadItem(Stream, uint, ReaderOptions?, EgamiContext?)"/>:
    /// an av01 item or a grid, with its own color and clean aperture, and no alpha.
    /// </summary>
    internal static Raster ReadItem(EgamiInput input, ReaderOptions options, uint itemId)
    {
        AvifFile file = AvifFile.Read(input);
        HeifFile heif = file.Heif;
        HeifItem item = heif.Item(itemId) ?? throw new ArgumentException($"The AVIF file has no item {itemId}.", nameof(itemId));
        if (item.Type != AvifFile.Av01 && item.Type != AvifFile.Grid)
        {
            throw new ArgumentException($"Item {itemId} is a \"{FourCC.ToString(item.Type)}\" item, not an image (av01 or grid).", nameof(itemId));
        }

        AvifCodec codec = Codec(options);
        HeifItem first = item;
        HeifPlanes planes;
        if (item.Type == AvifFile.Grid)
        {
            (List<HeifItem> cells, int rows, int columns, int width, int height) = file.ReadGrid(item);
            planes = HeifImageReader.Grid(file, codec, cells, (rows, columns, width, height), input, null, lumaOnly: false);
            first = cells[0];
        }
        else
        {
            planes = HeifImageReader.Cell(file, codec, item, input);
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
        HeifRgbConverter converter = new(planes, null, false, points);
        (int x, int y, int w, int h) = crop ?? (0, 0, planes.Width, planes.Height);
        input.Limits.CheckFrameSize(w, h);
        HeifImageReader.PixelRows rows2 = new(converter.Channels, converter.OutputDepth, (row, column, samples) => converter.Row(y + row, x + column, samples.Length / converter.Channels, samples))
        {
            Write = (row, column, width, target) => converter.Write(y + row, x + column, width, target),
        };
        RasterMetadata metadata = new() { FormatName = AvifFormat.Info.Name, IccProfile = icc, CodePoints = HeifImageReader.RasterCodePoints(points, icc) };
        if (!converter.Supported)
        {
            metadata.WarningList.Add("Avif.MatrixApproximated", $"libavif does not convert matrix coefficients {points.MatrixCoefficients} with this range and chroma; the nearest matrix it has was used.");
        }

        PixelLayoutInfo natural = NaturalLayout(planes.BitDepth, planes.Monochrome, false);
        return HeifImageReader.Natural(input, new HeifImageReader.Prepared(w, h, natural, rows2, metadata, 0));
    }

    /// <summary>Gets an AV1 sequence's planes encoding (design question 8), with the AV1 chroma sample position.</summary>
    private static YuvColor PlaneColor(ColorCodePoints points, Av1Config config, Av1.Av1SequenceHeader sequence, RasterMetadata? metadata) =>
        Still.PlaneColor(points, AvifFile.Siting(config, sequence), metadata);
}
