using Egami.Colors;
using Egami.Formats.Av1;
using Egami.Formats.Heif;
using Egami.Metadata;
using Egami.Pixels;

using FileMetadata = Egami.Formats.Heif.HeifImageMetadata;

namespace Egami.Formats.Avif;

/// <content>
/// A still image's encode, at a quality chosen once or searched for a target size (AVIF extras design, question 8), in one layer
/// or in the layers of a progressive image (question 7).
/// </content>
internal sealed partial class AvifWriter
{
    /// <summary>The quality below which avifenc --progressive writes no first layer, and the first layer's quality.</summary>
    private const int ProgressiveWorstQuality = 10, ProgressiveStartQuality = 2;

    /// <summary>
    /// The highest first-layer quality at which a two-layer image codes its second layer on its own rather than from the first
    /// (libavif 1.4.0's TWO_LAYER_ALL_INTRA_QUALITY_THRESHOLD, an idea only): a near-empty first layer predicts little.
    /// </summary>
    private const int AllIntraFirstLayerQuality = 10;

    /// <summary>Encodes an image and writes its file.</summary>
    internal static void WriteImage(Raster raster, RasterMetadata metadata, AvifWriterOptions options, EgamiContext context, Stream output)
    {
        if (options.BitDepth == 16)
        {
            WriteSampleTransform(raster, metadata, options, context, output);
            return;
        }

        using Still still = new(raster, metadata, options, context);
        if (options.TargetSize is int target && !still.Lossless)
        {
            WriteTargetSize(still, target, output, context);
            return;
        }

        still.Write(options.Quality, output, SaveReporter.Current);
    }

    /// <summary>
    /// Searches the quality, as avifenc --target-size does, for the largest file not above <paramref name="target"/> bytes: a
    /// binary search from 0 to 100 that keeps the best file found. When quality 0 is still larger it is written with a warning.
    /// </summary>
    private static void WriteTargetSize(Still still, int target, Stream output, EgamiContext context)
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

        // A quality step can move the size by more than 5 %: between the best quality and the lowest one found too large, search
        // base_q_idx itself (the encoder's own scale, 4 steps a quality or finer), keeping the chroma format of the best quality.
        if (best is not null && found < 100 && best.Length < target * 0.95 && !still.Layered)
        {
            // The quality found too large bounds the search when it codes the same chroma format; across the switch to 4:4:4 at
            // quality 90 the best quality's 4:2:0 may still fit at finer quantizers.
            int fine = failed <= 100 && still.ChromaFor(failed) == still.ChromaFor(found) ? QIndexFor(failed) + 1 : 0, coarse = QIndexFor(found) - 1;
            while (fine <= coarse)
            {
                cancel.ThrowIfCancellationRequested();
                int qindex = (fine + coarse) / 2;
                using MemoryStream file = new();
                still.Write(found, file, reporter, qindex);
                if (file.Length <= target)
                {
                    best = file.ToArray();
                    coarse = qindex - 1;
                }
                else
                {
                    fine = qindex + 1;
                }
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

            SaveLosses.Report(context, "Avif.TargetSizeExceeded", $"AVIF at quality 0 takes {smallest.Length} bytes, more than the target size of {target}; it was written at quality 0.");
            best = smallest;
        }

        reporter?.Part(0, 1);
        output.Write(best);
    }

    /// <summary>
    /// What a still image's encodes share whatever their quality: the pixels as 16-bit RGBA, the depth, color, metadata, the alpha
    /// image and gain map (coded once), and the color planes of each chroma format asked for.
    /// </summary>
    private sealed class Still : IDisposable
    {
        private readonly Raster _raster;
        private readonly RasterMetadata _metadata;
        private readonly AvifWriterOptions _options;
        private readonly EgamiContext _context;
        private readonly Raster<Rgba16> _rgba;
        private readonly bool _gray;
        private readonly int _bitDepth;
        private readonly int _primaries;
        private readonly int _transfer;
        private readonly int _matrix;
        private readonly FileMetadata _extras;
        private readonly bool _hasAlpha;
        private readonly Dictionary<ChromaFormat, (Av1EncoderInput Planes, Av1FilmGrain? Grain)> _planes = [];
        private readonly Dictionary<string, CodedImage> _alpha = [];
        private GainMapItem? _gainMap;
        private bool _gainMapDone;

        public Still(Raster raster, RasterMetadata metadata, AvifWriterOptions options, EgamiContext context)
        {
            (_raster, _metadata, _options, _context) = (raster, metadata, options, context);
            PixelLayoutInfo layout = raster.Layout;
            _gray = layout.ChannelOrder is PixelChannelOrder.Gray or PixelChannelOrder.GrayAlpha;
            bool deep = layout.NeedsWideSamples;
            AvifDetails? details = metadata.FormatDetails as AvifDetails;
            Lossless = options.Lossless ?? details?.Lossless ?? false;
            _bitDepth = options.BitDepth ?? (!deep ? 8 : Lossless ? (details is { BitDepth: > 8 and <= 12 } ? details.BitDepth : 12) : 10);
            _extras = FileMetadata.From(metadata, options);
            (_primaries, _transfer) = metadata.CodePoints is ColorCodePoints points ? (points.Primaries, points.TransferCharacteristics)
                : !_extras.Icc.IsEmpty ? (2, 2) : (1, 13);
            _matrix = _gray ? 2 : Lossless ? 0 : 6;
            _rgba = raster.As<Rgba16>();
            _hasAlpha = layout.HasAlpha && !Opaque(_rgba);
        }

        /// <summary>Gets whether the image is coded lossless.</summary>
        internal bool Lossless { get; }

        /// <summary>Gets whether the image is coded in layers, whose qualities the target size search keeps to the quality scale.</summary>
        internal bool Layered => _options.Layers is { Count: > 1 } || _options.Progressive;

        public void Dispose() => _rgba.Dispose();

        /// <summary>Gets the chroma format the image is coded in at a quality: gray 4:0:0, lossless 4:4:4, the options', else 4:4:4 from 90.</summary>
        internal ChromaFormat ChromaFor(int quality) => _gray ? ChromaFormat.Yuv400
            : Lossless ? ChromaFormat.Yuv444
            : _options.Chroma is ChromaFormat chosen and (ChromaFormat.Yuv420 or ChromaFormat.Yuv422 or ChromaFormat.Yuv444) ? chosen
            : quality >= 90 ? ChromaFormat.Yuv444 : ChromaFormat.Yuv420;

        /// <summary>Codes the image at <paramref name="quality"/> and writes its file.</summary>
        internal void Write(int quality, Stream output, SaveReporter? reporter, int? qindex = null)
        {
            AvifWriterOptions options = _options;
            ChromaFormat chroma = ChromaFor(quality);
            (Av1EncoderInput color, Av1FilmGrain? grain) = Planes(chroma);
            (int cellWidth, int cellHeight) = GridCells(_raster.Width, _raster.Height, chroma, options.GridTileSize);
            IReadOnlyList<AvifLayer>? layers = LayerPlan(quality);
            if (layers is not null && (cellWidth < _raster.Width || cellHeight < _raster.Height))
            {
                SaveLosses.Report(_context, "Avif.LayersDropped", "AVIF images larger than one AV1 frame are written as a grid, which Egami writes in one layer; the layers were left out.");
                layers = null;
            }

            // A save's progress counts superblock rows: the color planes take the first three quarters when an alpha plane follows.
            reporter?.Part(0, _hasAlpha ? 0.75 : 1);
            CodedImage colorStream = layers is null
                ? CodedImage.Encode(color, cellWidth, cellHeight, Settings(Lossless, qindex ?? QIndexFor(quality), reporter, grain))
                : EncodeLayers(color, layers, Lossless, reporter, grain);

            CodedImage? alphaStream = null;
            if (_hasAlpha)
            {
                reporter?.Part(0.75, 1);
                string key = layers is null ? "" : string.Join(",", layers.Select(l => l.Scale));
                if (!_alpha.TryGetValue(key, out alphaStream))
                {
                    Av1EncoderInput alpha = AlphaPlane(_rgba, _bitDepth);
                    bool exact = options.AlphaQuality == 100;
                    alphaStream = layers is null
                        ? CodedImage.Encode(alpha, cellWidth, cellHeight, Settings(exact, QIndexFor(options.AlphaQuality), reporter))
                        : EncodeLayers(alpha, [.. layers.Select(l => l with { Quality = options.AlphaQuality })], exact, reporter);
                    _alpha[key] = alphaStream;
                }
            }

            if (!_gainMapDone)
            {
                _gainMapDone = true;
                if (!options.StripMetadata && _metadata.GainMap is GainMap map)
                {
                    _gainMap = EncodeGainMap(map, options, _context.MaxParallelism, _primaries, options.GainMapQuality ?? quality);
                }
            }

            WriteFile(output, _raster.Width, _raster.Height, colorStream, alphaStream, _primaries, _transfer, _matrix, _extras, _gainMap);
        }

        /// <summary>
        /// Gets the layers to code at <paramref name="quality"/>: the options' (the last at that quality when it is searched for a
        /// target size), or avifenc --progressive's two, or null for one layer.
        /// </summary>
        private IReadOnlyList<AvifLayer>? LayerPlan(int quality)
        {
            if (_options.Layers is IReadOnlyList<AvifLayer> layers)
            {
                return layers.Count < 2 ? null
                    : _options.TargetSize is null || Lossless ? layers
                    : [.. layers.Take(layers.Count - 1), layers[^1] with { Quality = quality }];
            }

            return _options.Progressive && quality >= ProgressiveWorstQuality
                ? [new AvifLayer { Quality = ProgressiveStartQuality }, new AvifLayer { Quality = quality }]
                : null;
        }

        private Av1EncoderSettings Settings(bool lossless, int qindex, SaveReporter? reporter, Av1FilmGrain? grain = null) => new()
        {
            Lossless = lossless,
            FilmGrain = grain,
            QIndex = qindex,
            Effort = _options.Effort,
            Parallelism = _context.MaxParallelism,
            Report = reporter,
            CancellationToken = reporter?.Cancellation ?? default,
        };

        /// <summary>
        /// Gets the color planes of a chroma format, converted once, and the film grain their frames carry: with FilmGrainDenoise
        /// the planes denoised and their grain modeled (AVIF extras design, question 9), else a loaded raster's grain that Load
        /// left out, else none. Lossless images carry none.
        /// </summary>
        private (Av1EncoderInput Planes, Av1FilmGrain? Grain) Planes(ChromaFormat chroma)
        {
            if (!_planes.TryGetValue(chroma, out (Av1EncoderInput Planes, Av1FilmGrain? Grain) entry))
            {
                Av1EncoderInput planes = Lossless || _gray
                    ? DirectPlanes(_rgba, chroma == ChromaFormat.Yuv400, _bitDepth, _primaries, _transfer, _matrix)
                    : chroma == ChromaFormat.Yuv420 && _bitDepth == 8 && _options.Effort >= 5 ? SharpPlanes(_raster, _primaries, _transfer, _matrix)
                    : ConvertedPlanes(_raster, chroma, _bitDepth, _primaries, _transfer, _matrix);
                Av1FilmGrain? grain = null;
                if (!Lossless && _options.FilmGrainDenoise > 0 && Av1FilmGrainModel.Run(planes, _options.FilmGrainDenoise) is (Av1EncoderInput denoised, Av1FilmGrain modeled))
                {
                    (planes, grain) = (denoised, modeled);
                }
                else if (!Lossless && _options.FilmGrainDenoise == 0 && _metadata.FormatDetails is AvifDetails { FilmGrain: AvifFilmGrain kept, GrainApplied: false })
                {
                    grain = Grain(kept, planes);
                }

                entry = (planes, grain);
                _planes[chroma] = entry;
            }

            return entry;
        }

        /// <summary>
        /// Codes the layers of a progressive image as libavif lays them out: one AV1 stream with a spatial layer per layer, the
        /// first a key frame and each later one shown in turn, predicted from the one before when it is the same size (and the first
        /// of two is not near empty), else coded on its own; the item's data is the frames in order, which a1lx indexes.
        /// </summary>
        private CodedImage EncodeLayers(Av1EncoderInput input, IReadOnlyList<AvifLayer> layers, bool lossless, SaveReporter? reporter, Av1FilmGrain? grain = null)
        {
            using Av1StreamEncoder encoder = new(new Av1SequenceOptions
            {
                Width = input.Width,
                Height = input.Height,
                BitDepth = input.BitDepth,
                Chroma = input.Chroma,
                CodePoints = new ColorCodePoints((byte)input.ColorPrimaries, (byte)input.TransferCharacteristics, (byte)input.MatrixCoefficients, input.FullRange),
                ChromaSiting = input.ChromaSamplePosition switch { 1 => ChromaSiting.Left, 2 => ChromaSiting.TopLeft, _ => null },
                SpatialLayers = layers.Count,
                FilmGrain = grain is not null,
            }, _context);
            bool allIntra = layers.Count == 2 && layers[0].Quality <= AllIntraFirstLayerQuality;
            List<byte> data = [];
            int[] sizes = new int[layers.Count];
            (int Width, int Height) previous = default;
            for (int k = 0; k < layers.Count; k++)
            {
                reporter?.Cancellation.ThrowIfCancellationRequested();
                int width = Math.Max(1, (int)Math.Round(input.Width * layers[k].Scale)), height = Math.Max(1, (int)Math.Round(input.Height * layers[k].Scale));
                Av1EncoderInput layer = (width, height) == (input.Width, input.Height) ? input : Downscale(input, width, height);
                bool last = k == layers.Count - 1;
                Av1FrameKind kind = k == 0 ? Av1FrameKind.Key : allIntra || (width, height) != previous ? Av1FrameKind.IntraOnly : Av1FrameKind.Inter;
                Av1EncodedFrame frame = encoder.EncodePlanes(layer, new Av1FramePlan
                {
                    Kind = kind,
                    QIndex = lossless && last ? 0 : QIndexFor(layers[k].Quality),
                    Effort = _options.Effort,
                    ReferenceSlots = k == 0 ? Av1FramePlan.Default.ReferenceSlots : [.. Enumerable.Repeat(k - 1, 7)],
                    References = Av1References.Last,
                    RefreshSlots = 1 << k,
                    Referenced = !last,
                    Tune = Av1Tune.Image,
                    // A lower layer is a preview: finer quantizers in flat areas (Variance Boost) cost it more than they show.
                    DeltaQuantizers = last,
                    OrderHint = 0,
                    SpatialId = k,
                    FilmGrain = grain,
                });
                data.AddRange(frame.Data.Span);
                sizes[k] = frame.Data.Length;
                previous = (width, height);
            }

            byte[] stream = [.. data];
            return new CodedImage([Av1Encoder.FromStream(stream, encoder.Sequence, encoder.Reconstruction!)], 1, 1, input.Width, input.Height) { LayerSizes = sizes };
        }
    }

    /// <summary>
    /// Gets the AV1 film grain parameters of a loaded raster's grain, for planes of their chroma format: a chroma table the planes'
    /// format cannot carry (4:0:0) is left out, and AV1's rules on 4:2:0 chroma without luma points are kept.
    /// </summary>
    internal static Av1FilmGrain Grain(AvifFilmGrain g, Av1EncoderInput planes)
    {
        bool mono = planes.Chroma == ChromaFormat.Yuv400;
        bool fromLuma = !mono && g.ChromaScalingFromLuma;
        bool chroma = !mono && !fromLuma && !(planes.Chroma == ChromaFormat.Yuv420 && g.ScalingPointsY.Count == 0);
        (int, int)[] cb = chroma ? [.. g.ScalingPointsCb] : [], cr = chroma ? [.. g.ScalingPointsCr] : [];
        int lumaCount = 2 * g.ArCoeffLag * (g.ArCoeffLag + 1), chromaCount = lumaCount + (g.ScalingPointsY.Count > 0 ? 1 : 0);
        int[] Coefficients(IReadOnlyList<int> values, int n) => [.. Enumerable.Range(0, n).Select(i => (i < values.Count ? values[i] : 0) + 128)];
        return new Av1FilmGrain
        {
            ApplyGrain = true,
            UpdateGrain = true,
            GrainSeed = g.RandomSeed,
            PointsY = [.. g.ScalingPointsY],
            ChromaScalingFromLuma = fromLuma,
            PointsCb = cb,
            PointsCr = cr,
            GrainScalingMinus8 = g.ScalingShift - 8,
            ArCoeffLag = g.ArCoeffLag,
            ArCoeffsYPlus128 = g.ScalingPointsY.Count > 0 ? Coefficients(g.ArCoeffsY, lumaCount) : [],
            ArCoeffsCbPlus128 = fromLuma || cb.Length > 0 ? Coefficients(g.ArCoeffsCb, chromaCount) : [],
            ArCoeffsCrPlus128 = fromLuma || cr.Length > 0 ? Coefficients(g.ArCoeffsCr, chromaCount) : [],
            ArCoeffShiftMinus6 = g.ArCoeffShift - 6,
            GrainScaleShift = g.GrainScaleShift,
            CbMult = g.Cb.Mult,
            CbLumaMult = g.Cb.LumaMult,
            CbOffset = g.Cb.Offset,
            CrMult = g.Cr.Mult,
            CrLumaMult = g.Cr.LumaMult,
            CrOffset = g.Cr.Offset,
            OverlapFlag = g.Overlap,
            ClipToRestrictedRange = g.ClipToRestrictedRange,
        };
    }

    /// <summary>Scales planes down to a layer's size, each output sample the mean of the input samples its area covers.</summary>
    internal static Av1EncoderInput Downscale(Av1EncoderInput input, int width, int height)
    {
        int ssx = input.Chroma is ChromaFormat.Yuv420 or ChromaFormat.Yuv422 ? 1 : 0, ssy = input.Chroma == ChromaFormat.Yuv420 ? 1 : 0;
        ushort[][] planes = new ushort[input.Planes.Length][];
        for (int p = 0; p < planes.Length; p++)
        {
            int sx = p > 0 ? ssx : 0, sy = p > 0 ? ssy : 0;
            int fw = (input.Width + sx) >> sx, fh = (input.Height + sy) >> sy, tw = (width + sx) >> sx, th = (height + sy) >> sy;
            ushort[] from = input.Planes[p], to = new ushort[tw * th];
            for (int y = 0; y < th; y++)
            {
                int y0 = (int)((long)y * fh / th), y1 = Math.Max(y0 + 1, (int)((long)(y + 1) * fh / th));
                for (int x = 0; x < tw; x++)
                {
                    int x0 = (int)((long)x * fw / tw), x1 = Math.Max(x0 + 1, (int)((long)(x + 1) * fw / tw));
                    long sum = 0;
                    for (int yy = y0; yy < y1; yy++)
                    {
                        for (int xx = x0; xx < x1; xx++)
                        {
                            sum += from[(yy * fw) + xx];
                        }
                    }

                    int count = (y1 - y0) * (x1 - x0);
                    to[(y * tw) + x] = (ushort)((sum + (count / 2)) / count);
                }
            }

            planes[p] = to;
        }

        return new Av1EncoderInput
        {
            Width = width,
            Height = height,
            BitDepth = input.BitDepth,
            Chroma = input.Chroma,
            Planes = planes,
            ColorPrimaries = input.ColorPrimaries,
            TransferCharacteristics = input.TransferCharacteristics,
            MatrixCoefficients = input.MatrixCoefficients,
            FullRange = input.FullRange,
            ChromaSamplePosition = input.ChromaSamplePosition,
        };
    }

    /// <summary>The a1lx property of a layered item: the sizes of every layer but the last, 16 or 32 bits each.</summary>
    internal static byte[] LayerIndex(IReadOnlyList<int> sizes)
    {
        bool large = sizes.Take(sizes.Count - 1).Any(s => s > ushort.MaxValue);
        List<byte> content = [(byte)(large ? 1 : 0)];
        for (int i = 0; i < 3; i++)
        {
            int size = i < sizes.Count - 1 ? sizes[i] : 0;
            content.AddRange(large ? HeifWriter.U32((uint)size) : HeifWriter.U16(size));
        }

        return HeifWriter.Box("a1lx", [.. content]);
    }
}
