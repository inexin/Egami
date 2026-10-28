using System.Diagnostics.CodeAnalysis;

using Egami.Pixels;

namespace Egami.Video.Codecs.Ffv1;

/// <summary>
/// The FFV1 codec adapter: V1's send and receive loop over <see cref="Ffv1Encoder"/>. The first frame sets the layout (planes keep
/// theirs; RGB rasters are coded as RGB), so <see cref="Track"/> describes the stream once a frame is sent; every later frame
/// must have the same layout.
/// </summary>
internal sealed class Ffv1VideoEncoder : IVideoEncoder
{
    private readonly VideoEncoderOptions _options;
    private readonly Ffv1EncoderOptions _ffv1;
    private readonly MediaContext _context;
    private readonly int _keyframeInterval;
    private readonly Queue<EncodedPacket> _ready = new();
    private Ffv1Encoder? _encoder;
    private Layout _layout;
    private byte[] _output = [];
    private long _frames;
    private bool _qualityReported;
    private bool _disposed;

    public Ffv1VideoEncoder(VideoEncoderOptions options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Width, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Height, 1, nameof(options));
        if (options.FrameRate.Numerator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.FrameRate, "The frame rate must be positive.");
        }

        _options = options;
        _ffv1 = options.Ffv1 ?? Ffv1EncoderOptions.Default;
        _context = context;
        if (_ffv1.Slices is { } slices)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(slices, 1, nameof(options));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(slices, 1024, nameof(options));
        }

        if (_ffv1.BitDepth is { } bits)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(bits, 8, nameof(options));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(bits, 16, nameof(options));
        }

        if (options.Effort is { } effort)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(effort, 0, nameof(options));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(effort, 10, nameof(options));
        }

        _keyframeInterval = options.KeyframeInterval ?? 1;
        ArgumentOutOfRangeException.ThrowIfLessThan(_keyframeInterval, 1, nameof(options));
        ChromaFormat format = options.ChromaFormat ?? ChromaFormat.Yuv420;
        Track = Describe(new Layout(format, 8, 8, false, false), null);
    }

    public VideoTrackInfo Track { get; private set; }

    public void Send(VideoFrame frame) => Send(frame, EncodeFrameOptions.Default);

    public void Send(VideoFrame frame, EncodeFrameOptions options)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frame.Width != _options.Width || frame.Height != _options.Height)
        {
            throw new ArgumentException($"The frame is {frame.Width}x{frame.Height}; the encoder makes {_options.Width}x{_options.Height}.", nameof(frame));
        }

        YuvRaster? converted = null;
        try
        {
            YuvRaster planes = frame.Planes ?? (converted = ToPlanes(frame.Pixels ?? throw new ArgumentException("The frame has neither planes nor pixels.", nameof(frame))));
            Layout layout = LayoutOf(planes);
            if (_encoder is null)
            {
                Start(layout);
            }
            else if (layout != _layout)
            {
                throw new ArgumentException($"The frame's layout ({Name(layout)}) differs from the stream's ({Name(_layout)}); FFV1 keeps one layout per stream.", nameof(frame));
            }

            bool keyframe = _frames % _keyframeInterval == 0 || options.ForceKeyframe;
            int length = _encoder!.Encode(planes, keyframe, _context.Egami.MaxParallelism, ref _output);
            MediaTime timestamp = frame.Timestamp.Rescale(Track.TimeBase);
            EncodedPacket packet = EncodedPacket.Rent(Track.Number, length, timestamp);
            _output.AsSpan(0, length).CopyTo(packet.GetWritableData().Span);
            packet.IsKeyframe = keyframe;
            packet.Duration = new MediaTime(1, Track.TimeBase);
            _ready.Enqueue(packet);
            _frames++;
        }
        finally
        {
            converted?.Dispose();
        }
    }

    public void Flush()
    {
    }

    public bool TryReceive([NotNullWhen(true)] out EncodedPacket? packet) => _ready.TryDequeue(out packet);

    public void Dispose()
    {
        _disposed = true;
        while (_ready.TryDequeue(out EncodedPacket? packet))
        {
            packet.Dispose();
        }
    }

    private static YuvRaster ToPlanes(Raster pixels)
    {
        // RGB is coded as RGB: G, B and R planes tagged Identity, which the reversible color transform takes losslessly.
        bool deep = pixels.Layout.BitsPerChannel > 8;
        var color = new YuvColor(YuvMatrix.Identity, YuvRange.Full, ChromaSiting.Center);
        return deep
            ? YuvRaster.FromRaster(pixels, ChromaFormat.Yuv444, 16, color, pixels.Layout.HasAlpha)
            : YuvRaster.FromRaster(pixels, ChromaFormat.Yuv444, color, pixels.Layout.HasAlpha);
    }

    private Layout LayoutOf(YuvRaster planes)
    {
        bool rgb = planes.Color.Matrix == YuvMatrix.Identity && planes.ChromaFormat == ChromaFormat.Yuv444;
        int bits = _ffv1.BitDepth ?? planes.BitDepth;
        if (bits > planes.BitDepth)
        {
            throw new ArgumentException($"Ffv1EncoderOptions.BitDepth is {bits}, more than the frame's {planes.BitDepth}-bit planes hold.");
        }

        return new Layout(planes.ChromaFormat, planes.BitDepth, bits, planes.HasAlpha, rgb);
    }

    private void Start(Layout layout)
    {
        Ffv1Coder coder = _ffv1.Coder;
        if (coder == Ffv1Coder.GolombRice && layout.Bits > 8)
        {
            throw new ArgumentException($"FFV1 Golomb-Rice coding takes 8 bits per sample at most; the frames have {layout.Bits}. Use the range coder.");
        }

        if (_options.Quality is not null && !_qualityReported)
        {
            _qualityReported = true;
            if (_context.Egami.Diagnostics is not null)
            {
                using DiagnosticReporter reporter = _context.Egami.StartDiagnostics();
                reporter.Report(DiagnosticLevel.Warning, "Ffv1.QualityIgnored", "FFV1 is lossless, so VideoEncoderOptions.Quality is ignored.");
            }
        }

        (int hs, int vs) = layout.Format switch
        {
            ChromaFormat.Yuv420 => (1, 1),
            ChromaFormat.Yuv422 => (1, 0),
            ChromaFormat.Yuv411 => (2, 0),
            ChromaFormat.Yuv410 => (2, 2),
            _ => (0, 0),
        };
        bool large = _ffv1.ContextModel is { } model ? model == Ffv1ContextModel.Large : (_options.Effort ?? 4) >= 7;
        int bits = layout.Rgb ? layout.Bits + 1 : layout.Bits;
        int slices = _ffv1.Slices ?? DefaultSlices(_options.Width, _options.Height);

        // Above CIF a slice may cover at most a quarter of the grid (RFC 9043 section 5).
        int least = (long)_options.Width * _options.Height > 352 * 288 ? 4 : 1;
        (int h, int v) = SliceGrid(Math.Max(slices, least), least, _options.Width, _options.Height, layout.Rgb ? 0 : hs, layout.Rgb ? 0 : vs);
        Ffv1Parameters p = Ffv1Parameters.ForEncoding(
            coder switch { Ffv1Coder.GolombRice => 0, Ffv1Coder.RangeDefaultTable => 1, _ => 2 },
            layout.Rgb ? 1 : 0,
            layout.Bits,
            layout.Rgb || layout.Format != ChromaFormat.Yuv400,
            hs,
            vs,
            layout.Alpha,
            h,
            v,
            [Ffv1QuantTables.Build(bits, large)],
            [null],
            _ffv1.SliceCrc,
            _keyframeInterval == 1);
        _encoder = new Ffv1Encoder(_options.Width, _options.Height, p, layout.Depth);
        _layout = layout;
        Track = Describe(layout, _encoder.ConfigurationRecord);
    }

    private VideoTrackInfo Describe(Layout layout, byte[]? record) => new()
    {
        Number = 1,
        Kind = TrackKind.Video,
        CodecId = CodecIds.Ffv1,
        CodecPrivate = record ?? [],
        TimeBase = _options.FrameRate.Reciprocal(),
        Width = _options.Width,
        Height = _options.Height,
        FrameRate = _options.FrameRate,
        BitDepth = layout.Bits,
        ChromaFormat = layout.Format,
    };

    /// <summary>The slice count picked from the frame size.</summary>
    internal static int DefaultSlices(int width, int height)
    {
        long pixels = (long)width * height;
        return pixels <= 352 * 288 ? 1 : pixels <= 1280 * 720 ? 4 : pixels <= 2560 * 1440 ? 16 : 24;
    }

    /// <summary>
    /// Splits a slice count into columns and rows, as square as it goes and wider than tall, each slice at least 16 samples a
    /// side. Every slice must start on a whole chroma sample, since RFC 9043 sizes a slice's chroma from its own width: a grid
    /// whose edges fall mid-sample would leave chroma columns or rows that no slice codes. When the count asked for has no such
    /// grid, the nearest count that has one is taken, never fewer than <paramref name="least"/> when one exists.
    /// </summary>
    internal static (int Columns, int Rows) SliceGrid(int slices, int least, int width, int height, int hs, int vs)
    {
        int most = Math.Max(1, Math.Min(1024, Math.Max(1, width / 16) * Math.Max(1, height / 16)));
        slices = Math.Clamp(slices, 1, most);
        for (int step = 0; step <= most; step++)
        {
            foreach (int count in (int[])[slices - step, slices + step])
            {
                if (count >= Math.Min(least, most) && count <= most && Grid(count) is { } grid)
                {
                    return grid;
                }
            }
        }

        return (1, 1);

        (int, int)? Grid(int count)
        {
            for (int rows = (int)Math.Sqrt(count); rows >= 1; rows--)
            {
                int columns = count / rows;
                if (columns * rows == count && width / columns >= 16 && height / rows >= 16 && Aligned(columns, width, hs) && Aligned(rows, height, vs))
                {
                    return (columns, rows);
                }
            }

            return count == 1 ? (1, 1) : null;
        }

        static bool Aligned(int parts, int size, int shift)
        {
            for (int i = 1; i < parts; i++)
            {
                if (((long)i * size / parts) % (1 << shift) != 0)
                {
                    return false;
                }
            }

            return true;
        }
    }

    private static string Name(Layout l) =>
        $"{(l.Rgb ? "RGB" : l.Format.ToString())} {l.Depth}-bit{(l.Alpha ? " with alpha" : "")}";

    /// <summary>A stream's layout: the planes' format and storage depth, the depth coded, alpha, and whether it is RGB.</summary>
    private readonly record struct Layout(ChromaFormat Format, int Depth, int Bits, bool Alpha, bool Rgb);
}
