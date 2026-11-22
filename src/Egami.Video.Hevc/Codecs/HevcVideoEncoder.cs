using System.Diagnostics.CodeAnalysis;

using Egami.Colors;
using Egami.Heic.Hevc;
using Egami.Pixels;
using Egami.Video.Codecs.Encoding;
using Egami.Video.Codecs.Hevc.Encoder;

namespace Egami.Video.Codecs;

/// <summary>
/// Encodes HEVC with the shared encoder core planning keyframes and quantizers and <see cref="HevcFrameCoder"/> coding them:
/// 4:2:0 progressive frames in the Main or Main 10 profile, the picture's CTB rows as wavefront substreams.
/// </summary>
internal sealed class HevcVideoEncoder : IVideoEncoder
{
    /// <summary>The quality when the options leave it open, on the H.264 encoder's scale (HEVC decision Q6).</summary>
    public const int DefaultQuality = 60;

    /// <summary>The effort when the options leave it open.</summary>
    public const int DefaultEffort = 4;

    /// <summary>The color of frames converted to the coded format, and of tracks before the first frame: BT.601 limited range.</summary>
    internal static readonly ColorCodePoints DefaultColor = new(2, 2, 6, false);

    private readonly VideoEncoderOptions _options;
    private readonly HevcEncoderOptions _hevc;
    private readonly MediaContext _context;
    private readonly CoreSettings _settings;
    private readonly int _effort;
    private readonly int _level;
    private readonly int _bFrames;
    private readonly bool _pyramid;
    private readonly int _references;
    private EncoderPictures? _pictures;
    private HevcFrameCoder? _coder;
    private EncoderCore? _core;
    private bool _disposed;

    public HevcVideoEncoder(VideoEncoderOptions options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Width, 2, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Height, 2, nameof(options));
        if ((options.Width & 1) != 0 || (options.Height & 1) != 0)
        {
            throw new ArgumentException($"HEVC 4:2:0 needs an even width and height; {options.Width}x{options.Height} is not.", nameof(options));
        }

        if (options.FrameRate.Numerator <= 0 || options.FrameRate.Denominator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.FrameRate, "The frame rate must be positive.");
        }

        int quality = options.Quality ?? DefaultQuality;
        int effort = options.Effort ?? DefaultEffort;
        Check(quality, 0, 100, "Quality");
        Check(effort, 0, 10, "Effort");
        HevcEncoderOptions hevc = options.Hevc ?? HevcEncoderOptions.Default;
        if (hevc.MinQuantizer > hevc.MaxQuantizer)
        {
            throw new ArgumentException($"The smallest QP {hevc.MinQuantizer} is above the largest {hevc.MaxQuantizer}.", nameof(options));
        }

        if (hevc.Profile is { } profile && !Enum.IsDefined(profile))
        {
            throw new ArgumentOutOfRangeException(nameof(options), profile, "Unknown HEVC profile.");
        }

        int qp = options.Quantizer is int q ? Check(q, 0, 51, "Quantizer") : QualityToQp(quality);
        long bitrate = 0;
        if (options.RateControl is RateControlMode.Bitrate or RateControlMode.ConstantBitrate)
        {
            bitrate = options.Bitrate is > 0 and var b ? b : throw new ArgumentException($"{options.RateControl} needs a positive Bitrate.", nameof(options));
        }
        else if (!Enum.IsDefined(options.RateControl))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.RateControl, "Unknown rate control mode.");
        }

        if (options.FirstPassStats is { } pass && (pass.Width != options.Width || pass.Height != options.Height))
        {
            throw new ArgumentException($"The first pass saw {pass.Width}x{pass.Height} frames; this encoder makes {options.Width}x{options.Height}.", nameof(options));
        }

        double fps = (double)options.FrameRate.Numerator / options.FrameRate.Denominator;
        int keyInterval = options.KeyframeInterval is int k ? Check(k, 1, int.MaxValue, "KeyframeInterval") : Math.Max(1, (int)Math.Ceiling(fps * 10));
        long maxBitrate = options.RateControl switch
        {
            RateControlMode.ConstantBitrate => bitrate,
            RateControlMode.Bitrate => options.MaxBitrate is > 0 and var peak ? Math.Max(peak, bitrate) : bitrate * 2,
            _ => 0,
        };
        long buffer = bitrate > 0 ? options.BufferSize is > 0 and var size ? size : bitrate : 0;
        HevcLevels.Demand demand = new(fps, maxBitrate, buffer);
        _level = hevc.Level ?? HevcLevels.Choose(options.Width, options.Height, demand);
        if (HevcLevels.Misfit(HevcLevels.Of(_level), options.Width, options.Height, demand) is { } why)
        {
            throw new ArgumentException($"The stream does not fit its level: {why}.", nameof(options));
        }

        // B-frames as the H.264 encoder plans them (HEVC encoder Build Plan, PR C): three from effort 1, in a pyramid whose
        // middle one is a reference from effort 3. The decoded picture buffer holds the P pictures' references, the P picture
        // after a run, the pyramid's middle B picture and the picture being decoded; fewer references where the level's buffer
        // is smaller.
        int bFrames = options.LatencyMode != LatencyMode.Quality ? 0 : hevc.BFrames ?? (effort >= 1 ? 3 : 0);
        bool pyramid = bFrames is 2 or 3 && effort >= 3;
        int dpb = HevcLevels.MaxDpbSize(HevcLevels.Of(_level), options.Width, options.Height);
        int references = HevcInterProfile.For(effort).References;
        int Buffer(int refs, bool middle) => refs + (bFrames > 0 ? 1 : 0) + (middle ? 1 : 0) + 1;
        while (references > 1 && Buffer(references, pyramid) > dpb)
        {
            references--;
        }

        pyramid &= Buffer(references, true) <= dpb;
        _options = options;
        _hevc = hevc;
        _context = context;
        _effort = effort;
        _bFrames = bFrames;
        _pyramid = pyramid;
        _references = references;
        _settings = new CoreSettings
        {
            Width = options.Width,
            Height = options.Height,
            FrameRate = options.FrameRate,
            LatencyMode = options.LatencyMode,
            KeyframeInterval = keyInterval,
            SceneCuts = options.SceneCutKeyframes,
            Parallelism = context.Egami.MaxParallelism,

            BFrames = bFrames,
            BPyramid = pyramid,

            // As the H.264 encoder: with a pyramid every run has its full count of B-frames; without one runs follow the content.
            AdaptiveBFrames = !pyramid,

            // A pyramid's middle B picture a coarser step than the default 1.15, as the H.264 encoder's (H.264 compression plan, PR D).
            BReferenceStep = 1.3,
            MacroblockTree = TreeStrength,
            Rate = new RateSettings
            {
                Mode = options.RateControl,
                BaseQuantizer = Math.Clamp(qp, hevc.MinQuantizer, hevc.MaxQuantizer),
                MinQuantizer = hevc.MinQuantizer,
                MaxQuantizer = hevc.MaxQuantizer,
                Bitrate = bitrate,
                MaxBitrate = options.MaxBitrate is > 0 and var m ? Math.Max(m, bitrate) : bitrate * 2,
                BufferSize = buffer,
                FrameRate = fps,
                Pixels = (long)options.Width * options.Height,
                Steps = HevcFrameCoder.QpSteps,
                PlanWholeGroupAhead = bFrames > 0,
                ProbeQuantizer = 26,
                InterFromKeyProbe = true,
                BufferPull = 8,
                BufferStepPull = 2,
                FirstPass = options.RateControl == RateControlMode.Bitrate && options.FirstPassStats is { } stats ? FirstPassComplexities(stats) : null,
            },
        };
        Track = MakeTrack(hevc.Profile ?? HevcProfile.Main, DefaultColor, null, null);
    }

    public VideoTrackInfo Track { get; private set; }

    /// <summary>Gets the frame coder once the first frame is in, for tests.</summary>
    internal HevcFrameCoder? Coder => _coder;

    /// <summary>
    /// The frame-level tree's strength (<see cref="CoreSettings.MacroblockTree"/>), the H.264 encoder's: a frame's step falls by
    /// 2^(-0.25) for each doubling of its content's worth through reuse.
    /// </summary>
    internal const double TreeStrength = 0.25;

    /// <summary>The QP for a quality of 0 to 100, as the H.264 encoder's: 51 at 0, 23 at 50, 0 at 100 or so.</summary>
    internal static int QualityToQp(int quality) => Math.Clamp((int)Math.Round(51 - (0.56 * quality)), 0, 51);

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

        if (frame.Planes is null && frame.Pixels is null)
        {
            throw new ArgumentException("The frame has neither planes nor pixels.", nameof(frame));
        }

        if (_core is null)
        {
            HevcProfile profile = _hevc.Profile ?? (frame.Planes is YuvRaster<ushort> ? HevcProfile.Main10 : HevcProfile.Main);
            int depth = profile == HevcProfile.Main10 ? 10 : 8;

            // 8-bit 4:2:0 planes are coded from the core's own copy (shifted up for Main 10); deeper planes and RGB frames coded
            // as Main 10 are kept at 10 bits as well.
            bool full = depth == 10 && frame.Planes is not YuvRaster<byte> { ChromaFormat: ChromaFormat.Yuv420 };
            ColorCodePoints color = ColorOf(frame, depth, full);
            HevcEncoderSettings settings = new()
            {
                Width = _options.Width,
                Height = _options.Height,
                ChromaFormat = 1,
                BitDepth = depth,
                Effort = _effort,
                ColourPrimaries = color.Primaries,
                TransferCharacteristics = color.TransferCharacteristics,
                MatrixCoefficients = color.MatrixCoefficients,
                FullRange = color.FullRange,
                AdaptiveQp = _hevc.AdaptiveQuantization,
                Parallelism = Math.Max(1, _context.Egami.MaxParallelism),
                LevelIdc = HevcLevels.Of(_level).Idc,
                Timing = Timing(_options.FrameRate),
            };
            _coder = new HevcFrameCoder(settings, _hevc.AnnexB, _bFrames, _pyramid, _references);
            Track = MakeTrack(profile, color, frame.Hdr, _coder);
            _pictures = new EncoderPictures(_options.Width, _options.Height, EncoderPictures.Vp8Color, _context.Egami)
            {
                FullFormat = (ChromaFormat.Yuv420, depth, YuvColorOf(color)),
            };
            _core = new EncoderCore(_settings with { FullPictures = full }, _coder, _pictures, alpha: false);
        }

        _core.Send(frame, options.ForceKeyframe);
    }

    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _core?.Flush();
    }

    public bool TryReceive([NotNullWhen(true)] out EncodedPacket? packet)
    {
        packet = null;
        return _core is not null && _core.TryReceive(out packet) && packet is not null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _core?.Dispose();
        _coder?.Dispose();
        _pictures?.Dispose();
    }

    /// <summary>The VUI's timing: a tick of one frame, when the frame rate's terms fit its 32-bit fields.</summary>
    internal static (uint, uint)? Timing(Rational frameRate) =>
        frameRate.Numerator <= uint.MaxValue && frameRate.Denominator <= uint.MaxValue ? ((uint)frameRate.Denominator, (uint)frameRate.Numerator) : null;

    private VideoTrackInfo MakeTrack(HevcProfile profile, ColorCodePoints color, HdrMetadata? hdr, HevcFrameCoder? coder) => new()
    {
        Number = 1,
        Kind = TrackKind.Video,
        CodecId = CodecIds.Hevc,
        CodecPrivate = _hevc.AnnexB || coder is null ? ReadOnlyMemory<byte>.Empty : coder.ConfigurationRecord(),
        TimeBase = _options.FrameRate.Reciprocal(),
        Width = _options.Width,
        Height = _options.Height,
        FrameRate = _options.FrameRate,
        BitDepth = profile == HevcProfile.Main10 ? 10 : 8,
        ChromaFormat = ChromaFormat.Yuv420,
        Color = color,
        Hdr = hdr,
    };

    /// <summary>
    /// The color a track's frames have: planes coded as they are keep theirs (the frame's code points, else the planes'
    /// matrix and range); frames converted to the coded format are BT.601 limited range, unless they say otherwise.
    /// </summary>
    private static ColorCodePoints ColorOf(VideoFrame frame, int depth, bool full)
    {
        bool asTheyAre = frame.Planes is YuvRaster<byte> { ChromaFormat: ChromaFormat.Yuv420 }
            || (full && frame.Planes is YuvRaster<ushort> { ChromaFormat: ChromaFormat.Yuv420 } words && words.BitDepth == depth);
        if (frame.Color is { } color && (asTheyAre || full))
        {
            return color;
        }

        if (!asTheyAre)
        {
            return DefaultColor;
        }

        YuvRaster planes = frame.Planes!;
        int matrix = planes.Color.Matrix switch
        {
            YuvMatrix.Bt709 => 1,
            YuvMatrix.Bt2020Ncl => 9,
            _ => 6,
        };
        return new ColorCodePoints(2, 2, (byte)matrix, planes.Color.Range == YuvRange.Full);
    }

    private static YuvColor YuvColorOf(ColorCodePoints color) => new(
        color.MatrixCoefficients switch { 1 => YuvMatrix.Bt709, 9 or 10 => YuvMatrix.Bt2020Ncl, _ => YuvMatrix.Bt601 },
        color.FullRange ? YuvRange.Full : YuvRange.Limited,
        ChromaSiting.Center);

    private static long[] FirstPassComplexities(FirstPassStats stats)
    {
        long[] result = new long[stats.FrameCount];
        ReadOnlySpan<FrameStats> frames = stats.Frames;
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = frames[i].Inter;
        }

        return result;
    }

    private static int Check(int value, int min, int max, string name, VideoEncoderOptions? options = null) =>
        value < min || value > max ? throw new ArgumentOutOfRangeException(nameof(options), value, $"{name} must be {min} to {max}.") : value;
}
