using Egami.Colors;
using Egami.Formats.Av1;
using Egami.Formats.Heif;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Avif;

/// <summary>
/// Writes AVIF (Phase 2.9.6): the image coded by the managed AV1 intra encoder (<see cref="Av1Encoder"/>), in a HEIF file laid
/// out as libavif lays it out: ftyp with the avif, mif1 and miaf brands (and MA1B or MA1A when the image fits them), one meta
/// box, then the coded data in one mdat. The color item carries ispe, pixi, av1C and an nclx colr; an alpha item, when the image
/// has transparency, is a monochrome AV1 image linked by an auxl reference. The encoder needs every pixel first, so rows are
/// gathered and encoded at <see cref="RowEncoder{TPixel}.Complete"/>.
/// </summary>
internal sealed class AvifWriter : IFormatWriter, IRowFormatWriter
{
    internal static readonly AvifWriter Instance = new();

    /// <summary>
    /// libavif's quality to quantizer table for libaom's tune IQ (codec_aom.c), indexed by quality 0 to 100: the quantizer, 0 to
    /// 63, that libaom maps to base_q_idx through <see cref="QuantizerToQIndex"/>.
    /// </summary>
    private static ReadOnlySpan<byte> QualityToQuantizer =>
    [
        63, 63, 63, 62, 62, 62, 61, 61, 60, 60, 59, 59, 58, 58, 57, 57, 56, 56, 55, 55, 54, 54, 53, 53, 52, 52, 51, 51, 50, 50,
        49, 49, 48, 48, 47, 46, 46, 45, 45, 44, 43, 43, 42, 42, 41, 40, 40, 39, 39, 38, 37, 37, 36, 36, 35, 34, 33, 33, 32, 31,
        30, 30, 29, 28, 27, 27, 26, 25, 24, 24, 23, 22, 21, 21, 20, 19, 18, 18, 17, 16, 15, 15, 14, 13, 12, 12, 11, 10, 9, 9,
        8, 7, 6, 6, 5, 4, 3, 3, 2, 1, 0,
    ];

    /// <summary>libaom's quantizer_to_qindex (av1_quantize.c): base_q_idx for each quantizer 0 to 63.</summary>
    private static ReadOnlySpan<byte> QuantizerToQIndex =>
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 48, 52, 56, 60, 64, 68, 72, 76, 80, 84, 88, 92, 96, 100, 104, 108, 112, 116,
        120, 124, 128, 132, 136, 140, 144, 148, 152, 156, 160, 164, 168, 172, 176, 180, 184, 188, 192, 196, 200, 204, 208, 212,
        216, 220, 224, 228, 232, 236, 240, 244, 249, 255,
    ];

    public void Write<TPixel>(Raster<TPixel> raster, Stream output, WriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Streaming.RowEncoding.Save(this, raster, output, options);
    }

    public RowEncoder<TPixel> Start<TPixel>(Stream output, EncodeStart<TPixel> start)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        AvifWriterOptions avif = start.Options as AvifWriterOptions ?? AvifWriterOptions.Default;
        start.Refuse("AVIF", "AV1 encoding", "Save from a whole raster.");
        SaveLosses.Report(start, "AVIF", SaveKeeps.Alpha | SaveKeeps.Wide);
        return new Encoder<TPixel>(output, start, avif);
    }

    /// <summary>Gets the base_q_idx libaom codes a quality with, through libavif's tune IQ mapping.</summary>
    internal static int QIndexFor(int quality) => QuantizerToQIndex[QualityToQuantizer[Math.Clamp(quality, 0, 100)]];

    /// <summary>
    /// Writes YUV planes as they are, with no color conversion or chroma resampling: their matrix, range and chroma siting go into
    /// the file, and alpha into an alpha item. Lossless keeps every sample in its YUV plane.
    /// </summary>
    internal static void WritePlanes<TSample>(YuvRaster<TSample> yuv, Stream output, AvifWriterOptions options, EgamiContext context)
        where TSample : unmanaged
    {
        int width = yuv.Width, height = yuv.Height;
        (int primaries, int transfer) = yuv.Metadata.CodePoints is ColorCodePoints points ? (points.Primaries, points.TransferCharacteristics) : (1, 13);
        int matrix = yuv.ChromaFormat == ChromaFormat.Yuv400 ? 2 : yuv.Color.ToH273();
        List<ushort[]> planes = [Samples(yuv.Y)];
        if (yuv.U is YuvPlane<TSample> u && yuv.V is YuvPlane<TSample> v)
        {
            planes.Add(Samples(u));
            planes.Add(Samples(v));
        }

        Av1EncoderInput color = new()
        {
            Width = width,
            Height = height,
            BitDepth = yuv.BitDepth,
            Chroma = yuv.ChromaFormat,
            Planes = [.. planes],
            ColorPrimaries = primaries,
            TransferCharacteristics = transfer,
            MatrixCoefficients = matrix,
            FullRange = yuv.Color.Range == YuvRange.Full,
            ChromaSamplePosition = yuv.Color.Siting switch { ChromaSiting.TopLeft => 2, ChromaSiting.Left => 1, _ => 0 },
        };
        bool lossless = options.Lossless ?? false;
        Av1Encoder colorStream = Av1Encoder.Encode(color, new Av1EncoderSettings
        {
            Lossless = lossless,
            QIndex = QIndexFor(options.Quality),
            Effort = options.Effort,
            Parallelism = context.MaxParallelism,
        });
        Av1Encoder? alphaStream = null;
        if (yuv.A is YuvPlane<TSample> a)
        {
            Av1EncoderInput alpha = new()
            {
                Width = width,
                Height = height,
                BitDepth = yuv.BitDepth,
                Chroma = ChromaFormat.Yuv400,
                Planes = [Samples(a)],
                FullRange = true,
            };
            alphaStream = Av1Encoder.Encode(alpha, new Av1EncoderSettings
            {
                Lossless = options.AlphaQuality == 100,
                QIndex = QIndexFor(options.AlphaQuality),
                Effort = options.Effort,
                Parallelism = context.MaxParallelism,
            });
        }

        WriteFile(output, width, height, colorStream, alphaStream, primaries, transfer, matrix);

        static ushort[] Samples(YuvPlane<TSample> plane)
        {
            ushort[] samples = new ushort[plane.Width * plane.Height];
            for (int y = 0; y < plane.Height; y++)
            {
                Span<TSample> row = plane.Row(y);
                Span<ushort> to = samples.AsSpan(y * plane.Width, plane.Width);
                if (typeof(TSample) == typeof(byte))
                {
                    Span<byte> bytes = System.Runtime.InteropServices.MemoryMarshal.Cast<TSample, byte>(row);
                    for (int x = 0; x < to.Length; x++)
                    {
                        to[x] = bytes[x];
                    }
                }
                else
                {
                    System.Runtime.InteropServices.MemoryMarshal.Cast<TSample, ushort>(row).CopyTo(to);
                }
            }

            return samples;
        }
    }

    /// <summary>Encodes an image and writes its file.</summary>
    internal static void WriteImage(Raster raster, RasterMetadata metadata, AvifWriterOptions options, EgamiContext context, Stream output)
    {
        PixelLayoutInfo layout = raster.Layout;
        bool gray = layout.ChannelOrder is PixelChannelOrder.Gray or PixelChannelOrder.GrayAlpha;
        bool deep = layout.NeedsWideSamples;
        AvifDetails? details = metadata.FormatDetails as AvifDetails;
        bool lossless = options.Lossless ?? details?.Lossless ?? false;
        int bitDepth = options.BitDepth ?? (!deep ? 8 : lossless ? (details is { BitDepth: > 8 } ? details.BitDepth : 12) : 10);
        ChromaFormat chroma = gray ? ChromaFormat.Yuv400
            : lossless ? ChromaFormat.Yuv444
            : options.Chroma is ChromaFormat chosen and not ChromaFormat.Yuv400 ? chosen
            : options.Quality >= 90 ? ChromaFormat.Yuv444 : ChromaFormat.Yuv420;
        (int primaries, int transfer) = metadata.CodePoints is ColorCodePoints points ? (points.Primaries, points.TransferCharacteristics) : (1, 13);
        int matrix = gray ? 2 : lossless ? 0 : 6;
        int parallelism = context.MaxParallelism;

        using Raster<Rgba16> rgba = raster.As<Rgba16>();
        Av1EncoderInput color = lossless || gray
            ? DirectPlanes(rgba, chroma == ChromaFormat.Yuv400, bitDepth, primaries, transfer, matrix)
            : chroma == ChromaFormat.Yuv420 && bitDepth == 8 && options.Effort >= 5 ? SharpPlanes(raster, primaries, transfer, matrix)
            : ConvertedPlanes(raster, chroma, bitDepth, primaries, transfer, matrix);
        Av1Encoder colorStream = Av1Encoder.Encode(color, new Av1EncoderSettings
        {
            Lossless = lossless,
            QIndex = QIndexFor(options.Quality),
            Effort = options.Effort,
            Parallelism = parallelism,
        });

        Av1Encoder? alphaStream = null;
        if (layout.HasAlpha && !Opaque(rgba))
        {
            Av1EncoderInput alpha = AlphaPlane(rgba, bitDepth);
            alphaStream = Av1Encoder.Encode(alpha, new Av1EncoderSettings
            {
                Lossless = options.AlphaQuality == 100,
                QIndex = QIndexFor(options.AlphaQuality),
                Effort = options.Effort,
                Parallelism = parallelism,
            });
        }

        WriteFile(output, raster.Width, raster.Height, colorStream, alphaStream, primaries, transfer, matrix);
    }

    /// <summary>
    /// Writes the HEIF file: ftyp, meta, mdat, with the color item first and the alpha item after it, each with its properties in
    /// libavif's order (ispe, pixi, av1C, then colr or auxC).
    /// </summary>
    internal static void WriteFile(Stream output, int width, int height, Av1Encoder color, Av1Encoder? alpha, int primaries, int transfer, int matrix)
    {
        Av1SequenceHeader sequence = color.Sequence;
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
        uint colorItem = heif.AddItem("av01", color.Stream, "Color");
        heif.PrimaryItemId = colorItem;
        int ispe = heif.AddProperty(HeifWriter.SpatialExtents(width, height));
        heif.Associate(colorItem, ispe);
        heif.AddProperty(colorItem, HeifWriter.PixelInformation(sequence.BitDepth, sequence.Monochrome ? 1 : 3));
        heif.AddProperty(colorItem, ConfigurationBox(color), essential: true);
        heif.AddProperty(colorItem, HeifWriter.ColorNclx(primaries, transfer, matrix, sequence.FullRange));
        if (alpha is not null)
        {
            uint alphaItem = heif.AddItem("av01", alpha.Stream, "Alpha", hidden: true);
            heif.Associate(alphaItem, ispe);
            heif.AddProperty(alphaItem, HeifWriter.PixelInformation(alpha.Sequence.BitDepth, 1));
            heif.AddProperty(alphaItem, ConfigurationBox(alpha), essential: true);
            heif.AddProperty(alphaItem, HeifWriter.AuxiliaryType(AvifProperties.AlphaUrn));
            heif.Reference("auxl", alphaItem, colorItem);
        }

        heif.Write(output);
    }

    /// <summary>The av1C property (AV1 Codec ISO Media File Format Binding, section 2.3): the configuration, then the sequence header OBU.</summary>
    internal static byte[] ConfigurationBox(Av1Encoder encoded)
    {
        Av1SequenceHeader s = encoded.Sequence;
        byte[] obu = encoded.SequenceHeaderObu;
        byte[] content = new byte[4 + obu.Length];
        content[0] = 0x81;
        content[1] = (byte)((s.Profile << 5) | s.OperatingPoints[0].Level);
        content[2] = (byte)((s.OperatingPoints[0].Tier << 7) | ((s.BitDepth > 8 ? 1 : 0) << 6) | ((s.BitDepth == 12 ? 1 : 0) << 5)
            | ((s.Monochrome ? 1 : 0) << 4) | (s.SubsamplingX << 3) | (s.SubsamplingY << 2) | s.ChromaSamplePosition);
        obu.CopyTo(content, 4);
        return HeifWriter.Box("av1C", content);
    }

    /// <summary>The samples as they are: G, B and R (or gray) at the bit depth, for lossless files and gray images.</summary>
    private static Av1EncoderInput DirectPlanes(Raster<Rgba16> rgba, bool gray, int bitDepth, int primaries, int transfer, int matrix)
    {
        int width = rgba.Width, height = rgba.Height;
        int planes = gray ? 1 : 3;
        ushort[][] data = [.. Enumerable.Range(0, planes).Select(_ => new ushort[width * height])];
        int max = (1 << bitDepth) - 1;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<Rgba16> row = rgba.Row(y);
            int at = y * width;
            for (int x = 0; x < width; x++)
            {
                Rgba16 p = row[x];
                if (gray)
                {
                    data[0][at + x] = Narrow(p.R, max);
                }
                else
                {
                    data[0][at + x] = Narrow(p.G, max);
                    data[1][at + x] = Narrow(p.B, max);
                    data[2][at + x] = Narrow(p.R, max);
                }
            }
        }

        return new Av1EncoderInput
        {
            Width = width,
            Height = height,
            BitDepth = bitDepth,
            Chroma = gray ? ChromaFormat.Yuv400 : ChromaFormat.Yuv444,
            Planes = data,
            ColorPrimaries = primaries,
            TransferCharacteristics = transfer,
            MatrixCoefficients = matrix,
            FullRange = true,
        };
    }

    /// <summary>The image converted to YUV with Egami's matrices (2.1.4), full range, chroma sited left of its luma pair.</summary>
    private static Av1EncoderInput ConvertedPlanes(Raster raster, ChromaFormat chroma, int bitDepth, int primaries, int transfer, int matrix)
    {
        YuvColor yuvColor = new(YuvMatrix.Bt601, YuvRange.Full, ChromaSiting.Left);
        int width = raster.Width, height = raster.Height;
        ushort[][] data;
        if (bitDepth == 8)
        {
            using YuvRaster<byte> yuv = YuvRaster.FromRaster(raster, chroma, yuvColor);
            data = [.. Planes(yuv).Select(p => Widen(p))];
        }
        else
        {
            using YuvRaster<ushort> yuv = YuvRaster.FromRaster(raster, chroma, bitDepth, yuvColor);
            data = [.. Planes(yuv).Select(p => Copy(p))];
        }

        return new Av1EncoderInput
        {
            Width = width,
            Height = height,
            BitDepth = bitDepth,
            Chroma = chroma,
            Planes = data,
            ColorPrimaries = primaries,
            TransferCharacteristics = transfer,
            MatrixCoefficients = matrix,
            FullRange = true,
        };

        static IEnumerable<YuvPlane<T>> Planes<T>(YuvRaster<T> yuv)
            where T : unmanaged
        {
            yield return yuv.Y;
            if (yuv.U is YuvPlane<T> u && yuv.V is YuvPlane<T> v)
            {
                yield return u;
                yield return v;
            }
        }

        static ushort[] Widen(YuvPlane<byte> plane)
        {
            ushort[] samples = new ushort[plane.Width * plane.Height];
            for (int y = 0; y < plane.Height; y++)
            {
                Span<byte> row = plane.Row(y);
                for (int x = 0; x < row.Length; x++)
                {
                    samples[(y * plane.Width) + x] = row[x];
                }
            }

            return samples;
        }

        static ushort[] Copy(YuvPlane<ushort> plane)
        {
            ushort[] samples = new ushort[plane.Width * plane.Height];
            for (int y = 0; y < plane.Height; y++)
            {
                plane.Row(y).CopyTo(samples.AsSpan(y * plane.Width));
            }

            return samples;
        }
    }

    /// <summary>
    /// The image converted to 8-bit 4:2:0 with sharp RGB to YUV (libwebp's sharpyuv, as libavif uses it from effort 5): luma and
    /// chroma refined together so the upsampled image comes back closest to the source, and sharp colored edges keep their color.
    /// </summary>
    private static Av1EncoderInput SharpPlanes(Raster raster, int primaries, int transfer, int matrix)
    {
        int width = raster.Width, height = raster.Height;
        using Raster<Bgra8> bgra = raster.As<Bgra8>();
        uint[] argb = new uint[width * height];
        for (int y = 0; y < height; y++)
        {
            System.Runtime.InteropServices.MemoryMarshal.Cast<Bgra8, uint>(bgra.Row(y)).CopyTo(argb.AsSpan(y * width, width));
        }

        Webp.Vp8Planes planes = new(width, height);
        YuvKernels8.FromRgbCoefficients k = YuvKernels8.FromRgbCoefficients.For(new YuvColor(YuvMatrix.Bt601, YuvRange.Full, ChromaSiting.Left));
        Webp.WebpSharpYuv.Convert(argb, width, height, planes, in k);
        int cw = (width + 1) >> 1, ch = (height + 1) >> 1;
        return new Av1EncoderInput
        {
            Width = width,
            Height = height,
            BitDepth = 8,
            Chroma = ChromaFormat.Yuv420,
            Planes = [Plane(planes.Y, planes.Stride, width, height), Plane(planes.U, planes.ChromaStride, cw, ch), Plane(planes.V, planes.ChromaStride, cw, ch)],
            ColorPrimaries = primaries,
            TransferCharacteristics = transfer,
            MatrixCoefficients = matrix,
            FullRange = true,
        };

        static ushort[] Plane(byte[] source, int stride, int w, int h)
        {
            ushort[] samples = new ushort[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    samples[(y * w) + x] = source[(y * stride) + x];
                }
            }

            return samples;
        }
    }

    /// <summary>The alpha channel as a monochrome image at the bit depth, full range.</summary>
    private static Av1EncoderInput AlphaPlane(Raster<Rgba16> rgba, int bitDepth)
    {
        int width = rgba.Width, height = rgba.Height;
        ushort[] alpha = new ushort[width * height];
        int max = (1 << bitDepth) - 1;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<Rgba16> row = rgba.Row(y);
            for (int x = 0; x < width; x++)
            {
                alpha[(y * width) + x] = Narrow(row[x].A, max);
            }
        }

        return new Av1EncoderInput { Width = width, Height = height, BitDepth = bitDepth, Chroma = ChromaFormat.Yuv400, Planes = [alpha], FullRange = true };
    }

    private static bool Opaque(Raster<Rgba16> rgba)
    {
        for (int y = 0; y < rgba.Height; y++)
        {
            foreach (Rgba16 p in rgba.Row(y))
            {
                if (p.A != ushort.MaxValue)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>A 16-bit sample at a lower bit depth, rounded, so a sample widened from that depth comes back exactly.</summary>
    private static ushort Narrow(ushort value, int max) => (ushort)(((value * max) + 32767) / 65535);

    /// <summary>Gathers rows and encodes them at the end.</summary>
    private sealed class Encoder<TPixel>(Stream output, EncodeStart<TPixel> start, AvifWriterOptions options) : RowEncoder<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private Raster<TPixel>? _whole;

        public override void WriteRow(ReadOnlySpan<TPixel> row) =>
            throw new NotSupportedException("AVIF needs every pixel before the first byte, so it cannot be streamed. Save from a whole raster.");

        public override IEnumerable<int> WriteWhole(Raster<TPixel> raster)
        {
            ArgumentNullException.ThrowIfNull(raster);
            _whole = raster;
            return [];
        }

        public override void Complete()
        {
            Raster<TPixel> raster = _whole ?? start.Whole ?? throw new InvalidOperationException("No raster was written.");
            WriteImage(raster, start.Metadata, options, start.Context, output);
        }
    }
}
