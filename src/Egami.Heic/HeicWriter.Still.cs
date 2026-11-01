using Egami.Colors;
using Egami.Formats;
using Egami.Formats.Heif;
using Egami.Metadata;
using Egami.Heic.Hevc;
using Egami.Pixels;

namespace Egami.Heic;

/// <content>A still image's encode: the planes converted once, the color image coded at a quality, alpha, gain map and thumbnail.</content>
internal sealed partial class HeicWriter
{
    /// <summary>
    /// What a still image's encodes share whatever their quality: the pixels as 16-bit RGBA, the depth, color and metadata, the
    /// color planes (converted once), and the alpha image, gain map and thumbnail (coded once).
    /// </summary>
    internal sealed class HeicStill : IDisposable
    {
        private readonly Raster _raster;
        private readonly RasterMetadata _metadata;
        private readonly HeicWriterOptions _options;
        private readonly EgamiContext _context;
        private readonly Raster<Rgba16> _rgba;
        private readonly bool _gray;
        private readonly int _bitDepth;
        private readonly int _primaries;
        private readonly int _transfer;
        private readonly int _matrix;
        private readonly HeifImageMetadata _extras;
        private readonly bool _hasAlpha;
        private readonly ChromaFormat _chroma;
        private Planes? _planes;
        private Coded? _alpha;
        private HeifGainMapImage? _gainMap;
        private HeifThumbnailImage? _thumbnail;
        private bool _extrasDone;

        public HeicStill(Raster raster, RasterMetadata metadata, HeicWriterOptions options, EgamiContext context)
        {
            (_raster, _metadata, _options, _context) = (raster, metadata, options, context);
            PixelLayoutInfo layout = raster.Layout;
            _gray = layout.ChannelOrder is PixelChannelOrder.Gray or PixelChannelOrder.GrayAlpha;
            bool deep = layout.NeedsWideSamples;
            Lossless = options.Lossless ?? false;
            _bitDepth = options.BitDepth ?? (!deep ? 8 : Lossless ? 12 : 10);
            _extras = HeifImageMetadata.From(metadata, options);
            (_primaries, _transfer) = metadata.CodePoints is ColorCodePoints points ? (points.Primaries, points.TransferCharacteristics)
                : !_extras.Icc.IsEmpty ? (2, 2) : (1, 13);

            // Lossless: 4:4:4 G, B, R with the identity matrix; else 4:2:0 unless asked (design questions 7 and 9).
            _chroma = Lossless ? (options.Chroma == ChromaFormat.Yuv400 ? ChromaFormat.Yuv400 : ChromaFormat.Yuv444)
                : options.Chroma is ChromaFormat chosen and (ChromaFormat.Yuv420 or ChromaFormat.Yuv422 or ChromaFormat.Yuv444 or ChromaFormat.Yuv400) ? chosen
                : ChromaFormat.Yuv420;
            bool hdr = _transfer is 16 or 18 || _primaries == 9;
            _matrix = Lossless && _chroma == ChromaFormat.Yuv444 ? 0 : _chroma == ChromaFormat.Yuv400 ? 2 : hdr ? 9 : 6;
            _rgba = raster.As<Rgba16>();
            _hasAlpha = layout.HasAlpha && !Opaque(_rgba);
        }

        /// <summary>Gets whether the image is coded lossless.</summary>
        internal bool Lossless { get; }

        public void Dispose() => _rgba.Dispose();

        /// <summary>Codes the image at <paramref name="quality"/> (or at <paramref name="qp"/>) and writes its file.</summary>
        internal void Write(int quality, Stream output, SaveReporter? reporter, int? qp = null)
        {
            CancellationToken cancel = reporter?.Cancellation ?? default;
            Planes planes = _planes ??= ColorPlanes();
            (int cellWidth, int cellHeight) = GridCells(_raster.Width, _raster.Height, _chroma, _options.GridTileSize);
            reporter?.Part(0, _hasAlpha ? 0.75 : 1);
            Coded color = Encode(planes, cellWidth, cellHeight, Settings(planes, Lossless, qp ?? QpFor(quality), _primaries, _transfer, _matrix), cancel);
            if (_hasAlpha && _alpha is null)
            {
                reporter?.Part(0.75, 1);
                Planes alpha = HeicWriter.Alpha(_rgba, _bitDepth);
                _alpha = Encode(alpha, cellWidth, cellHeight, Settings(alpha, _options.AlphaQuality == 100, QpFor(_options.AlphaQuality), 2, 2, 2), cancel);
            }

            if (!_extrasDone)
            {
                _extrasDone = true;
                if (!_options.StripMetadata && _metadata.GainMap is GainMap map)
                {
                    _gainMap = EncodeGainMap(map, _options.GainMapQuality ?? quality, cancel);
                }

                if (_options.ThumbnailSize is int box)
                {
                    _thumbnail = EncodeThumbnail(box, quality, cancel);
                }
            }

            List<string> brands = ["mif1", "heic", "miaf"];
            if (_gainMap is not null)
            {
                brands.Add("tmap");
            }

            byte[]? clap = color.Image.IsGrid || (color.Image.CellWidth == _raster.Width && color.Image.CellHeight == _raster.Height) ? null
                : HeifImageLayout.CleanAperture(color.Image.CellWidth, color.Image.CellHeight, _raster.Width, _raster.Height);
            int width = color.Image.IsGrid ? _raster.Width : color.Image.CellWidth, height = color.Image.IsGrid ? _raster.Height : color.Image.CellHeight;
            HeifImageLayout.WriteFile(output, Codec, "heic", brands, width, height, _chroma == ChromaFormat.Yuv400, true, color.Image, _alpha?.Image, _primaries,
                _transfer, _matrix, _extras, _gainMap, _thumbnail, clap);
        }

        private Planes ColorPlanes()
        {
            if (Lossless)
            {
                return Direct(_rgba, _chroma == ChromaFormat.Yuv400, _bitDepth);
            }

            YuvMatrix matrix = _matrix == 9 ? YuvMatrix.Bt2020Ncl : YuvMatrix.Bt601;
            return _chroma == ChromaFormat.Yuv420 && _bitDepth == 8 && _options.Effort >= 5 && !_gray ? Sharp(_raster, matrix)
                : Converted(_raster, _chroma, _bitDepth, matrix);
        }

        private HevcEncoderSettings Settings(Planes planes, bool lossless, int qp, int primaries, int transfer, int matrix) => new()
        {
            Width = planes.Width,
            Height = planes.Height,
            ChromaFormat = planes.Chroma switch { ChromaFormat.Yuv400 => 0, ChromaFormat.Yuv422 => 2, ChromaFormat.Yuv444 => 3, _ => 1 },
            BitDepth = planes.BitDepth,
            Qp = qp,
            Effort = _options.Effort,
            Lossless = lossless,
            ColourPrimaries = primaries,
            TransferCharacteristics = transfer,
            MatrixCoefficients = matrix,
            FullRange = true,
            AdaptiveQp = !lossless && Tuning.AdaptiveQp,
            ChromaQpOffset = Tuning.ChromaQpOffset,
            Parallelism = Math.Max(1, _context.MaxParallelism),
        };

        /// <summary>A gain map: one channel as a 4:0:0 image, three as 4:4:4 G, B, R with the identity matrix, as the AVIF writer codes it.</summary>
        private HeifGainMapImage EncodeGainMap(GainMap map, int quality, CancellationToken cancel)
        {
            int n = map.Width * map.Height;
            byte[] pixels = map.Pixels;
            int channels = map.Channels;
            ushort[][] samples = channels == 1 ? [new ushort[n]] : [new ushort[n], new ushort[n], new ushort[n]];
            for (int i = 0; i < n; i++)
            {
                if (channels == 1)
                {
                    samples[0][i] = pixels[i];
                }
                else
                {
                    (samples[0][i], samples[1][i], samples[2][i]) = (pixels[(i * channels) + 1], pixels[(i * channels) + 2], pixels[i * channels]);
                }
            }

            Planes planes = new(samples, map.Width, map.Height, channels == 1 ? ChromaFormat.Yuv400 : ChromaFormat.Yuv444, 8);
            bool lossless = _options.GainMapQuality is int chosen ? chosen == 100 : _options.Lossless == true;
            Coded coded = Encode(planes, map.Width, map.Height, Settings(planes, lossless, QpFor(quality), 2, 2, channels == 1 ? 2 : 0), cancel);
            return new HeifGainMapImage(coded.Image.Cells[0], coded.Image.CellWidth, coded.Image.CellHeight, channels == 1, map.Info, _primaries,
                map.Info.BaseRenditionIsHdr ? 13 : 16);
        }

        /// <summary>A thumbnail the size of <paramref name="box"/> at most on its longer side, 4:2:0 at the image's quality.</summary>
        private HeifThumbnailImage EncodeThumbnail(int box, int quality, CancellationToken cancel)
        {
            double scale = Math.Min(1, box / (double)Math.Max(_raster.Width, _raster.Height));
            int width = Math.Max(1, (int)Math.Round(_raster.Width * scale)), height = Math.Max(1, (int)Math.Round(_raster.Height * scale));
            int depth = Math.Min(_bitDepth, 10);
            YuvMatrix yuvMatrix = _matrix == 9 ? YuvMatrix.Bt2020Ncl : YuvMatrix.Bt601;
            Planes planes;
            if ((width, height) == (_raster.Width, _raster.Height))
            {
                planes = Converted(_raster, ChromaFormat.Yuv420, depth, yuvMatrix);
            }
            else
            {
                using Raster small = _raster.Derive(ops => ops.Resize(width, height), cancel);
                planes = Converted(small, ChromaFormat.Yuv420, depth, yuvMatrix);
            }

            int matrix = _matrix is 0 or 2 ? 6 : _matrix;
            Coded coded = Encode(planes, width, height, Settings(planes, false, QpFor(quality), _primaries, _transfer, matrix), cancel);
            byte[]? clap = coded.Image.CellWidth == width && coded.Image.CellHeight == height ? null
                : HeifImageLayout.CleanAperture(coded.Image.CellWidth, coded.Image.CellHeight, width, height);
            return new HeifThumbnailImage(coded.Image.Cells[0], coded.Image.CellWidth, coded.Image.CellHeight, depth, false, clap);
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
    }

    /// <summary>A coded image and the pictures it was coded from.</summary>
    internal sealed record Coded(HeifCodedImage Image, HevcEncodedPicture[] Pictures);

    /// <summary>
    /// Codes planes as one picture (padded to even sides when chroma is subsampled; the file's clap crops the padding), or as a grid
    /// of cells of the size given, each picture on its own and the cells in parallel.
    /// </summary>
    internal static Coded Encode(Planes planes, int cellWidth, int cellHeight, HevcEncoderSettings settings, CancellationToken cancel)
    {
        int sx = planes.Chroma is ChromaFormat.Yuv420 or ChromaFormat.Yuv422 ? 2 : 1, sy = planes.Chroma == ChromaFormat.Yuv420 ? 2 : 1;
        if (cellWidth >= planes.Width && cellHeight >= planes.Height)
        {
            int width = (planes.Width + sx - 1) / sx * sx, height = (planes.Height + sy - 1) / sy * sy;
            Planes padded = Cell(planes, 0, 0, width, height);
            HevcEncodedPicture picture = HevcEncoder.Encode(settings with { Width = width, Height = height }, padded.Samples, padded.Strides, cancel);
            return new Coded(new HeifCodedImage([Item(picture)], 1, 1, width, height, planes.BitDepth), [picture]);
        }

        int columns = (planes.Width + cellWidth - 1) / cellWidth, rows = (planes.Height + cellHeight - 1) / cellHeight;
        HevcEncodedPicture[] pictures = new HevcEncodedPicture[columns * rows];
        HevcEncoderSettings cellSettings = settings with { Width = cellWidth, Height = cellHeight, Parallelism = 1 };
        ParallelOptions parallel = new() { MaxDegreeOfParallelism = Math.Max(1, settings.Parallelism), CancellationToken = cancel };
        Parallel.For(0, pictures.Length, parallel, i =>
        {
            Planes cell = Cell(planes, (i % columns) * cellWidth, (i / columns) * cellHeight, cellWidth, cellHeight);
            pictures[i] = HevcEncoder.Encode(cellSettings, cell.Samples, cell.Strides, cancel);
        });

        return new Coded(new HeifCodedImage([.. pictures.Select(Item)], columns, rows, cellWidth, cellHeight, planes.BitDepth), pictures);
    }

    private static HeifCodedItem Item(HevcEncodedPicture picture) => new(picture.ItemData(), HeifWriter.Box("hvcC", picture.ConfigurationRecord()));

    /// <summary>A part of planes, past their right and bottom edges padded by repeating the last column and row.</summary>
    internal static Planes Cell(Planes planes, int x0, int y0, int width, int height)
    {
        int ssx = planes.Chroma is ChromaFormat.Yuv420 or ChromaFormat.Yuv422 ? 1 : 0, ssy = planes.Chroma == ChromaFormat.Yuv420 ? 1 : 0;
        ushort[][] samples = new ushort[planes.Samples.Length][];
        for (int c = 0; c < samples.Length; c++)
        {
            int sx = c > 0 ? ssx : 0, sy = c > 0 ? ssy : 0;
            int fullW = (planes.Width + sx) >> sx, fullH = (planes.Height + sy) >> sy;
            int w = (width + sx) >> sx, h = (height + sy) >> sy, cx = x0 >> sx, cy = y0 >> sy;
            ushort[] source = planes.Samples[c], cell = new ushort[w * h];
            for (int y = 0; y < h; y++)
            {
                int row = Math.Min(cy + y, fullH - 1) * fullW;
                for (int x = 0; x < w; x++)
                {
                    cell[(y * w) + x] = source[row + Math.Min(cx + x, fullW - 1)];
                }
            }

            samples[c] = cell;
        }

        return planes with { Samples = samples, Width = width, Height = height };
    }
}
