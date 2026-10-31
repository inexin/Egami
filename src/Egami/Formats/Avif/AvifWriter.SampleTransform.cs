using Egami.Colors;
using Egami.Formats.Av1;
using Egami.Formats.Heif;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Avif;

/// <content>
/// 16-bit AVIF through a sample transform (AVIF 1.2.0 section 4.2.3; AVIF extras design, question 4): two AV1 images and a sato
/// item that combines them, in an altr group with the first image as the fallback. Lossless images split each sample into its high
/// and low bytes (8+8); lossy ones code its 12 high bits, then the difference from that image's decode in an 8-bit residual that
/// overlaps it by 4 bits. The recipes follow the AVIF specification's Appendix A; libavif 1.4.2's were a cross-check only.
/// </content>
internal sealed partial class AvifWriter
{
    /// <summary>Writes a 16-bit image through a sample transform.</summary>
    private static void WriteSampleTransform(Raster raster, RasterMetadata metadata, AvifWriterOptions options, EgamiContext context, Stream output)
    {
        PixelLayoutInfo layout = raster.Layout;
        bool gray = layout.ChannelOrder is PixelChannelOrder.Gray or PixelChannelOrder.GrayAlpha;
        AvifDetails? details = metadata.FormatDetails as AvifDetails;
        bool lossless = options.Lossless ?? details?.Lossless ?? false;
        FileMetadata extras = FileMetadata.From(metadata, options);
        (int primaries, int transfer) = metadata.CodePoints is ColorCodePoints points ? (points.Primaries, points.TransferCharacteristics)
            : !extras.Icc.IsEmpty ? (2, 2) : (1, 13);
        int matrix = gray ? 2 : lossless ? 0 : 6;
        ChromaFormat chroma = gray ? ChromaFormat.Yuv400
            : lossless ? ChromaFormat.Yuv444
            : options.Chroma is ChromaFormat chosen and (ChromaFormat.Yuv420 or ChromaFormat.Yuv422 or ChromaFormat.Yuv444) ? chosen
            : options.Quality >= 90 ? ChromaFormat.Yuv444 : ChromaFormat.Yuv420;
        (int cellWidth, int cellHeight) = GridCells(raster.Width, raster.Height, chroma, options.GridTileSize);
        if (cellWidth < raster.Width || cellHeight < raster.Height)
        {
            throw new NotSupportedException("A 16-bit AVIF image larger than one AV1 frame (a grid) is not supported; write it at 12 bits (BitDepth = 12).");
        }

        if (!options.StripMetadata && metadata.GainMap is not null)
        {
            SaveLosses.Report(context, "Save.GainMapDropped", "AVIF at 16 bits is written without a gain map; the image was written as its base. Save at 12 bits to keep it.");
        }

        using Raster<Rgba16> rgba = raster.As<Rgba16>();
        Av1EncoderInput color = lossless || gray
            ? DirectPlanes(rgba, gray, 16, primaries, transfer, matrix)
            : ConvertedPlanes(raster, chroma, 16, primaries, transfer, matrix);
        bool hasAlpha = layout.HasAlpha && !Opaque(rgba);
        SaveReporter? reporter = SaveReporter.Current;
        int parallelism = context.MaxParallelism;
        Av1EncoderSettings Settings(bool exact, int quality) => new()
        {
            Lossless = exact,
            QIndex = QIndexFor(quality),
            Effort = options.Effort,
            Parallelism = parallelism,
            CancellationToken = reporter?.Cancellation ?? default,
        };

        reporter?.Part(0, hasAlpha ? 0.75 : 1);
        (Av1Encoder colorBase, Av1Encoder colorResidual) = Split(color, lossless, Settings(lossless, options.Quality));
        (Av1Encoder, Av1Encoder)? alpha = null;
        if (hasAlpha)
        {
            reporter?.Part(0.75, 1);
            bool exact = options.AlphaQuality == 100;
            alpha = Split(AlphaPlane(rgba, 16), lossless, Settings(exact, options.AlphaQuality));
        }

        // The expression in postfix notation, at 32-bit intermediates: base * 256 | residual, or base * 16 + residual - 128.
        byte[] sato = lossless
            ? AvifSampleTransform.Write(32, [(AvifSampleTransform.Constant, 256), (1, 0), (AvifSampleTransform.Product, 0), (2, 0), (AvifSampleTransform.Or, 0)])
            : AvifSampleTransform.Write(32, [(AvifSampleTransform.Constant, 16), (1, 0), (AvifSampleTransform.Product, 0), (2, 0), (AvifSampleTransform.Sum, 0),
                (AvifSampleTransform.Constant, 128), (AvifSampleTransform.Difference, 0)]);
        WriteSampleTransformFile(output, raster.Width, raster.Height, colorBase, colorResidual, alpha, sato, primaries, transfer, matrix, extras);
    }

    /// <summary>
    /// Splits 16-bit planes into the base image and the residual image and codes both: the high and low bytes, each 8 bits (lossless,
    /// both coded with <paramref name="settings"/>); or the 12 high bits, coded with <paramref name="settings"/>, and an 8-bit
    /// residual, the original less the base's decode times 16, plus 128, coded the same way.
    /// </summary>
    private static (Av1Encoder Base, Av1Encoder Residual) Split(Av1EncoderInput input, bool lossless, Av1EncoderSettings settings)
    {
        ushort[][] high = new ushort[input.Planes.Length][], low = new ushort[input.Planes.Length][];
        for (int p = 0; p < input.Planes.Length; p++)
        {
            ushort[] source = input.Planes[p];
            high[p] = new ushort[source.Length];
            low[p] = new ushort[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                high[p][i] = (ushort)(lossless ? source[i] >> 8 : source[i] >> 4);
                low[p][i] = (ushort)(source[i] & 0xFF);
            }
        }

        Av1Encoder baseImage = Av1Encoder.Encode(With(input, lossless ? 8 : 12, high), settings);
        if (!lossless)
        {
            // The residual makes up what the base's coding lost: original - decoded * 16 + 128, clamped to 8 bits.
            Av1Frame decoded = baseImage.Reconstruction;
            ushort[][] planes = decoded.WidePlanes();
            for (int p = 0; p < input.Planes.Length; p++)
            {
                int width = p == 0 ? input.Width : PlaneWidth(input, p), height = p == 0 ? input.Height : PlaneHeight(input, p);
                ushort[] source = input.Planes[p];
                int stride = decoded.Strides[p];
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        low[p][(y * width) + x] = (ushort)Math.Clamp(source[(y * width) + x] - (planes[p][(y * stride) + x] * 16) + 128, 0, 255);
                    }
                }
            }
        }

        return (baseImage, Av1Encoder.Encode(With(input, 8, low), settings));

        static int PlaneWidth(Av1EncoderInput input, int plane) =>
            plane == 0 || input.Chroma is ChromaFormat.Yuv444 or ChromaFormat.Yuv400 ? input.Width : (input.Width + 1) >> 1;

        static int PlaneHeight(Av1EncoderInput input, int plane) =>
            plane == 0 || input.Chroma != ChromaFormat.Yuv420 ? input.Height : (input.Height + 1) >> 1;
    }

    private static Av1EncoderInput With(Av1EncoderInput input, int bitDepth, ushort[][] planes) => new()
    {
        Width = input.Width,
        Height = input.Height,
        BitDepth = bitDepth,
        Chroma = input.Chroma,
        Planes = planes,
        ColorPrimaries = input.ColorPrimaries,
        TransferCharacteristics = input.TransferCharacteristics,
        MatrixCoefficients = input.MatrixCoefficients,
        FullRange = input.FullRange,
        ChromaSamplePosition = input.ChromaSamplePosition,
    };

    /// <summary>
    /// Writes a sample transform's file as libavif lays it out: the base image as the primary item (its alpha after it), the hidden
    /// residual image (and its alpha), and the sato item over both, preferred to the base in an altr group; metadata describes the base.
    /// </summary>
    private static void WriteSampleTransformFile(Stream output, int width, int height, Av1Encoder colorBase, Av1Encoder colorResidual,
        (Av1Encoder Base, Av1Encoder Residual)? alpha, byte[] sato, int primaries, int transfer, int matrix, FileMetadata metadata)
    {
        Av1SequenceHeader sequence = colorBase.Sequence;
        List<string> brands = ["avif", "mif1", "miaf"];
        if (sequence.BitDepth <= 10 && sequence.Chroma == ChromaFormat.Yuv420)
        {
            brands.Add("MA1B");
        }
        else if (sequence.BitDepth <= 10 && sequence.Chroma == ChromaFormat.Yuv444)
        {
            brands.Add("MA1A");
        }

        HeifWriter heif = new("avif", [.. brands]);
        int ispe = heif.AddProperty(HeifWriter.SpatialExtents(width, height));
        int channels = sequence.Monochrome ? 1 : 3;
        byte[] nclx = HeifWriter.ColorNclx(primaries, transfer, matrix, sequence.FullRange);
        byte[] auxC = HeifWriter.AuxiliaryType(AvifProperties.AlphaUrn);
        CodedImage One(Av1Encoder stream) => new([stream], 1, 1, width, height);

        uint baseItem = AddImage(heif, One(colorBase), width, height, "Color", hidden: false, ispe, channels, nclx);
        heif.PrimaryItemId = baseItem;
        if (!metadata.Icc.IsEmpty)
        {
            heif.AddProperty(baseItem, HeifWriter.ColorIcc(metadata.Icc.Span));
        }

        heif.AddProperty(baseItem, nclx);
        AddHdrProperties(heif, baseItem, metadata.Hdr);
        AddOrientation(heif, baseItem, metadata.Orientation);
        if (alpha is (Av1Encoder alphaBase, _))
        {
            uint item = AddImage(heif, One(alphaBase), width, height, "Alpha", hidden: true, ispe, 1, auxC);
            heif.AddProperty(item, auxC);
            heif.Reference("auxl", item, baseItem);
            AddOrientation(heif, item, metadata.Orientation);
        }

        uint residualItem = AddImage(heif, One(colorResidual), width, height, "Color", hidden: true, ispe, channels, nclx);
        heif.AddProperty(residualItem, nclx);
        AddOrientation(heif, residualItem, metadata.Orientation);
        if (alpha is (_, Av1Encoder alphaResidual))
        {
            uint item = AddImage(heif, One(alphaResidual), width, height, "Alpha", hidden: true, ispe, 1, auxC);
            heif.AddProperty(item, auxC);
            heif.Reference("auxl", item, residualItem);
            AddOrientation(heif, item, metadata.Orientation);
        }

        uint satoItem = heif.AddItem("sato", sato, "SampleTransform");
        heif.Associate(satoItem, ispe);
        heif.AddProperty(satoItem, HeifWriter.PixelInformation(16, channels));
        heif.AddProperty(satoItem, nclx);
        AddOrientation(heif, satoItem, metadata.Orientation);
        heif.Reference("dimg", satoItem, baseItem, residualItem);
        heif.Group("altr", satoItem, baseItem);

        if (!metadata.Exif.IsEmpty)
        {
            uint exifItem = heif.AddItem("Exif", (byte[])[0, 0, 0, 0, .. metadata.Exif.Span], "Exif");
            heif.Reference("cdsc", exifItem, baseItem);
        }

        if (!metadata.Xmp.IsEmpty)
        {
            uint xmpItem = heif.AddItem("mime", metadata.Xmp, "XMP", contentType: "application/rdf+xml");
            heif.Reference("cdsc", xmpItem, baseItem);
        }

        heif.Write(output);
    }
}
