using Egami.Colors;
using Egami.Formats;
using Egami.Formats.Heif;
using Egami.Formats.Webp;
using Egami.Heic.Hevc;
using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Heic;

/// <summary>
/// Writes HEIC: the image coded by Egami.Heic's managed HEVC intra encoder (<see cref="HevcEncoder"/>), in the HEIF item layout
/// AVIF shares (<see cref="HeifImageLayout"/>): ftyp heic with the mif1, heic and miaf brands, one hvc1 item (or a grid of 512 x 512
/// hvc1 cells above 8,912,896 samples, as iPhones write), each with hvcC, ispe, pixi and an nclx colr; alpha as an auxiliary hvc1
/// item; a gain map as a tmap item; an optional thumbnail; Exif and XMP items. The encoder needs every pixel first, so the image is
/// coded at <see cref="RowEncoder{TPixel}.Complete"/>. A raster of several frames writes its first.
/// </summary>
internal sealed partial class HeicWriter : IFormatWriter, IRowFormatWriter
{
    internal static readonly HeicWriter Instance = new();

    /// <summary>The HEVC codec's part of the HEIF item layout: hvc1 items, and HEVC's alpha auxiliary type (HEIF Annex B).</summary>
    internal static readonly HeifImageCodec Codec = new("hvc1", "urn:mpeg:hevc:2015:auxid:1");

    /// <summary>The most luma samples one picture holds before the image becomes a grid: MaxLumaPs of HEVC level 5.1.</summary>
    internal const int MaxPictureSamples = 8912896;

    /// <summary>The longest side of a picture at level 5.1: sqrt(8 MaxLumaPs).</summary>
    internal const int MaxPictureSide = 8444;

    /// <summary>The size of the cells of a grid the image's size needs, as iPhones write them.</summary>
    internal const int GridCell = 512;

    /// <summary>Gets or sets the built-in perceptual tuning (design question 6): the adaptive quantizer and the chroma QP offset.</summary>
    internal static HeicTuning Tuning { get; set; } = new();

    /// <summary>The perceptual tuning's settings, which tests and the calibration may change.</summary>
    internal sealed record HeicTuning
    {
        internal bool AdaptiveQp { get; init; } = true;

        internal int ChromaQpOffset { get; init; }
    }

    public void Write<TPixel>(Raster<TPixel> raster, Stream output, WriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        RowEncoding.Save(this, raster, output, options);
    }

    public RowEncoder<TPixel> Start<TPixel>(Stream output, EncodeStart<TPixel> start)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        HeicWriterOptions heic = start.Options as HeicWriterOptions ?? HeicWriterOptions.Default;
        start.Refuse("HEIC", "HEVC encoding", "Save from a whole raster.");
        SaveLosses.Report(start, "HEIC", SaveKeeps.Alpha | SaveKeeps.Wide | SaveKeeps.Exif | SaveKeeps.Xmp | SaveKeeps.Icc | SaveKeeps.GainMap);
        return new Encoder<TPixel>(output, start, heic);
    }

    /// <summary>
    /// Gets the size of a grid's cells: <paramref name="chosen"/>, or for an image above one level 5.1 picture 512 x 512; the whole
    /// image when it fits one picture. Cell sides are even when chroma is subsampled.
    /// </summary>
    internal static (int Width, int Height) GridCells(int width, int height, ChromaFormat chroma, Size? chosen)
    {
        int alignX = chroma is ChromaFormat.Yuv420 or ChromaFormat.Yuv422 ? 2 : 1, alignY = chroma == ChromaFormat.Yuv420 ? 2 : 1;
        if (chosen is Size size)
        {
            return size.Width >= width && size.Height >= height ? (width, height) : (Align(size.Width, alignX), Align(size.Height, alignY));
        }

        if ((long)width * height <= MaxPictureSamples && width <= MaxPictureSide && height <= MaxPictureSide)
        {
            return (width, height);
        }

        return (GridCell, GridCell);

        static int Align(int value, int to) => (value + to - 1) / to * to;
    }

    /// <summary>
    /// Gets the slice QP a quality codes with: a line fitted to the sizes heif-enc -e x265 -p preset=medium gives (libheif's quality
    /// scale) on 28 photos (Kodak's 24 and four others), so a quality gives about the file size heif-enc gives at it. The geometric
    /// means of the sizes match within a fifth of a QP from quality 10 to 90.
    /// </summary>
    internal static int QpFor(int quality) => Math.Clamp((int)Math.Round(46.9 - (0.495 * Math.Clamp(quality, 0, 100))), 0, 51);

    /// <summary>Encodes an image and writes its file.</summary>
    internal static void WriteImage(Raster raster, RasterMetadata metadata, HeicWriterOptions options, EgamiContext context, Stream output)
    {
        using HeicStill still = new(raster, metadata, options, context);
        if (options.TargetSize is int target && !still.Lossless)
        {
            WriteTargetSize(still, target, output, context);
            return;
        }

        still.Write(options.Quality, output, SaveReporter.Current);
    }

    /// <summary>
    /// Searches the quality for the largest file not above <paramref name="target"/> bytes: a binary search from 0 to 100 that keeps
    /// the best file found, then the QP between the best quality and the lowest one found too large. When quality 0 is still larger
    /// it is written with a warning.
    /// </summary>
    private static void WriteTargetSize(HeicStill still, int target, Stream output, EgamiContext context)
    {
        SaveReporter? reporter = SaveReporter.Current;
        CancellationToken cancel = reporter?.Cancellation ?? default;
        byte[]? best = null, smallest = null;
        int low = 0, high = 100, tries = 0, found = -1, failed = 101;
        while (low <= high)
        {
            cancel.ThrowIfCancellationRequested();
            int quality = (low + high + 1) / 2;
            reporter?.Part(Math.Min(tries, 6) / 7.0, Math.Min(tries + 1, 7) / 7.0);
            tries++;
            using MemoryStream file = new();
            still.Write(quality, file, reporter);
            if (file.Length <= target)
            {
                best = file.ToArray();
                found = quality;
                low = quality + 1;
            }
            else
            {
                if (quality == 0)
                {
                    smallest = file.ToArray();
                }

                failed = Math.Min(failed, quality);
                high = quality - 1;
            }
        }

        // Between the best quality's QP and the failed one's, the QP itself: one step changes the size by about 12 %.
        if (best is not null && found < 100 && best.Length < target * 0.95)
        {
            int fine = failed <= 100 ? QpFor(failed) + 1 : 0, coarse = QpFor(found) - 1;
            for (int qp = coarse; qp >= fine; qp--)
            {
                cancel.ThrowIfCancellationRequested();
                using MemoryStream file = new();
                still.Write(found, file, reporter, qp);
                if (file.Length > target)
                {
                    break;
                }

                best = file.ToArray();
            }
        }

        if (best is null)
        {
            if (smallest is null)
            {
                using MemoryStream file = new();
                still.Write(0, file, null);
                smallest = file.ToArray();
            }

            SaveLosses.Report(context, "Heic.TargetSizeExceeded", $"HEIC at quality 0 takes {smallest.Length} bytes, more than the target size of {target}; it was written at quality 0.");
            best = smallest;
        }

        reporter?.Part(0, 1);
        output.Write(best);
    }

    /// <summary>Gathers rows and encodes them at the end.</summary>
    private sealed class Encoder<TPixel>(Stream output, EncodeStart<TPixel> start, HeicWriterOptions options) : RowEncoder<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private Raster<TPixel>? _whole;

        public override void WriteRow(ReadOnlySpan<TPixel> row) =>
            throw new NotSupportedException("HEIC needs every pixel before the first byte, so it cannot be streamed. Save from a whole raster.");

        public override IEnumerable<int> WriteWhole(Raster<TPixel> raster)
        {
            ArgumentNullException.ThrowIfNull(raster);
            _whole = raster;
            return [];
        }

        public override void Complete()
        {
            Raster<TPixel> raster = _whole ?? start.Whole ?? throw new InvalidOperationException("No raster was written.");
            if (raster.FrameCount > 1)
            {
                // HEIC sequences are not written (design question 15): the first frame stands for the image.
                using Raster<TPixel> first = raster.CopyFrame(0);
                WriteImage(first, start.Metadata, options, start.Context, output);
                return;
            }

            WriteImage(raster, start.Metadata, options, start.Context, output);
        }
    }

    /// <summary>The image as planes the encoder takes, in rows of the plane's width, and their size.</summary>
    internal sealed record Planes(ushort[][] Samples, int Width, int Height, ChromaFormat Chroma, int BitDepth)
    {
        internal int[] Strides => [.. Samples.Select((_, c) => c == 0 ? Width : ChromaWidth)];

        private int ChromaWidth => Chroma is ChromaFormat.Yuv420 or ChromaFormat.Yuv422 ? (Width + 1) / 2 : Width;
    }

    /// <summary>The image converted to YUV with Egami's matrices (2.1.4), full range, chroma sited left of its luma pair.</summary>
    internal static Planes Converted(Raster raster, ChromaFormat chroma, int bitDepth, YuvMatrix matrix)
    {
        YuvColor color = new(matrix, YuvRange.Full, ChromaSiting.Left);
        List<ushort[]> samples = [];
        if (bitDepth == 8)
        {
            using YuvRaster<byte> yuv = YuvRaster.FromRaster(raster, chroma, color);
            samples.Add(Widen(yuv.Y));
            if (yuv.U is YuvPlane<byte> u && yuv.V is YuvPlane<byte> v)
            {
                samples.Add(Widen(u));
                samples.Add(Widen(v));
            }
        }
        else
        {
            using YuvRaster<ushort> yuv = YuvRaster.FromRaster(raster, chroma, bitDepth, color);
            samples.Add(Copy(yuv.Y));
            if (yuv.U is YuvPlane<ushort> u && yuv.V is YuvPlane<ushort> v)
            {
                samples.Add(Copy(u));
                samples.Add(Copy(v));
            }
        }

        return new Planes([.. samples], raster.Width, raster.Height, chroma, bitDepth);

        static ushort[] Widen(YuvPlane<byte> plane)
        {
            ushort[] values = new ushort[plane.Width * plane.Height];
            for (int y = 0; y < plane.Height; y++)
            {
                Span<byte> row = plane.Row(y);
                for (int x = 0; x < row.Length; x++)
                {
                    values[(y * plane.Width) + x] = row[x];
                }
            }

            return values;
        }

        static ushort[] Copy(YuvPlane<ushort> plane)
        {
            ushort[] values = new ushort[plane.Width * plane.Height];
            for (int y = 0; y < plane.Height; y++)
            {
                plane.Row(y).CopyTo(values.AsSpan(y * plane.Width));
            }

            return values;
        }
    }

    /// <summary>
    /// The image converted to 8-bit 4:2:0 with sharp RGB to YUV (libwebp's sharpyuv, as the AVIF writer uses it from effort 5):
    /// luma and chroma refined together so the upsampled image comes back closest to the source.
    /// </summary>
    internal static Planes Sharp(Raster raster, YuvMatrix matrix)
    {
        int width = raster.Width, height = raster.Height;
        using Raster<Bgra8> bgra = raster.As<Bgra8>();
        uint[] argb = new uint[width * height];
        for (int y = 0; y < height; y++)
        {
            System.Runtime.InteropServices.MemoryMarshal.Cast<Bgra8, uint>(bgra.Row(y)).CopyTo(argb.AsSpan(y * width, width));
        }

        Vp8Planes planes = new(width, height);
        YuvKernels8.FromRgbCoefficients k = YuvKernels8.FromRgbCoefficients.For(new YuvColor(matrix, YuvRange.Full, ChromaSiting.Left));
        WebpSharpYuv.Convert(argb, width, height, planes, in k);
        int cw = (width + 1) >> 1, ch = (height + 1) >> 1;
        return new Planes([Plane(planes.Y, planes.Stride, width, height), Plane(planes.U, planes.ChromaStride, cw, ch), Plane(planes.V, planes.ChromaStride, cw, ch)],
            width, height, ChromaFormat.Yuv420, 8);

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

    /// <summary>The samples as they are: G, B and R (or gray) at the bit depth, for lossless files.</summary>
    internal static Planes Direct(Raster<Rgba16> rgba, bool gray, int bitDepth)
    {
        int width = rgba.Width, height = rgba.Height;
        ushort[][] data = [.. Enumerable.Range(0, gray ? 1 : 3).Select(_ => new ushort[width * height])];
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
                    (data[0][at + x], data[1][at + x], data[2][at + x]) = (Narrow(p.G, max), Narrow(p.B, max), Narrow(p.R, max));
                }
            }
        }

        return new Planes(data, width, height, gray ? ChromaFormat.Yuv400 : ChromaFormat.Yuv444, bitDepth);
    }

    /// <summary>
    /// The alpha channel as a 4:2:0 image at the bit depth with flat chroma, which every HEVC decoder reads (design question 10).
    /// </summary>
    internal static Planes Alpha(Raster<Rgba16> rgba, int bitDepth)
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

        int cw = (width + 1) / 2, ch = (height + 1) / 2;
        ushort[] flat = new ushort[cw * ch];
        Array.Fill(flat, (ushort)(1 << (bitDepth - 1)));
        return new Planes([alpha, flat, flat], width, height, ChromaFormat.Yuv420, bitDepth);
    }

    /// <summary>A 16-bit sample at a lower bit depth, rounded, so a sample widened from that depth comes back exactly.</summary>
    internal static ushort Narrow(ushort value, int max) => (ushort)(((value * max) + 32767) / 65535);
}
