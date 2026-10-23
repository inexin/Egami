using System.Diagnostics.CodeAnalysis;

using Egami.Colors;
using Egami.Formats.Webp;
using Egami.Pixels;
using Egami.Video.Codecs.Encoding;

namespace Egami.Video.Codecs.Vp8;

/// <summary>
/// Encodes VP8 through Egami's <see cref="Vp8StreamEncoder"/>, with the encoder core planning every frame: keyframes, golden
/// frames, hidden alternate references and quantizers. A stream with alpha codes the alpha plane as a second, grey VP8 stream
/// whose frames ride in each shown packet's addition 1, as WebM carries them.
/// </summary>
internal sealed class Vp8VideoEncoder : IVideoEncoder
{
    /// <summary>The quality when the options leave it open: the scale and default of Egami's AVIF writer (Design Q3).</summary>
    public const int DefaultQuality = 60;

    /// <summary>The effort when the options leave it open, 2.3.8's default.</summary>
    public const int DefaultEffort = 4;

    private readonly VideoEncoderOptions _options;
    private readonly Vp8EncoderOptions _vp8;
    private readonly MediaContext _context;
    private readonly CoreSettings _settings;
    private EncoderPictures? _pictures;
    private Vp8FrameCoder? _coder;
    private EncoderCore? _core;
    private bool _disposed;

    public Vp8VideoEncoder(VideoEncoderOptions options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Width, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Width, 16383, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Height, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Height, 16383, nameof(options));
        if (options.FrameRate.Numerator <= 0 || options.FrameRate.Denominator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.FrameRate, "The frame rate must be positive.");
        }

        int quality = options.Quality ?? DefaultQuality;
        int effort = options.Effort ?? DefaultEffort;
        Check(quality, 0, 100, "Quality");
        Check(effort, 0, 10, "Effort");
        Vp8EncoderOptions vp8 = options.Vp8 ?? Vp8EncoderOptions.Default;
        if (vp8.MinQuantizer > vp8.MaxQuantizer)
        {
            throw new ArgumentException($"The smallest quantizer {vp8.MinQuantizer} is above the largest {vp8.MaxQuantizer}.", nameof(options));
        }

        int baseQuantizer = options.Quantizer is int q ? Check(q, 0, 127, "Quantizer") : QualityToQuantizer(quality);
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
        bool realtime = options.LatencyMode == LatencyMode.Realtime;
        int keyInterval = options.KeyframeInterval is int k ? Check(k, 1, int.MaxValue, "KeyframeInterval") : Math.Max(1, (int)Math.Ceiling(fps * 10));
        _options = options;
        _vp8 = vp8;
        _context = context;
        _settings = new CoreSettings
        {
            Width = options.Width,
            Height = options.Height,
            FrameRate = options.FrameRate,
            LatencyMode = options.LatencyMode,
            KeyframeInterval = keyInterval,
            SceneCuts = options.SceneCutKeyframes,
            AltRef = vp8.AlternateReference && !realtime,
            GoldenInterval = vp8.GoldenInterval,
            FilterFrames = vp8.TemporalFilterFrames,
            FilterStrength = vp8.TemporalFilterStrength,
            Parallelism = context.Egami.MaxParallelism,
            Rate = new RateSettings
            {
                Mode = options.RateControl,
                BaseQuantizer = Math.Clamp(baseQuantizer, vp8.MinQuantizer, vp8.MaxQuantizer),
                MinQuantizer = vp8.MinQuantizer,
                MaxQuantizer = vp8.MaxQuantizer,
                Bitrate = bitrate,
                MaxBitrate = options.MaxBitrate is > 0 and var m ? Math.Max(m, bitrate) : bitrate * 2,
                BufferSize = options.BufferSize is > 0 and var buffer ? buffer : bitrate,
                FrameRate = fps,
                Pixels = (long)options.Width * options.Height,
                Steps = AcSteps,
                FirstPass = options.RateControl == RateControlMode.Bitrate && options.FirstPassStats is { } stats ? FirstPassComplexities(stats) : null,
            },
        };
        Track = MakeTrack(vp8.Alpha ?? false, null);
    }

    public VideoTrackInfo Track { get; private set; }

    /// <summary>Gets the rate controller once the first frame is in, for tests.</summary>
    internal RateController? Rate => _core?.Rate;

    /// <summary>VP8's AC quantizer step for each index (RFC 6386 section 14.1), which the bit model scales by.</summary>
    internal static short[] AcSteps { get; } =
    [
        4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19,
        20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35,
        36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51,
        52, 53, 54, 55, 56, 57, 58, 60, 62, 64, 66, 68, 70, 72, 74, 76,
        78, 80, 82, 84, 86, 88, 90, 92, 94, 96, 98, 100, 102, 104, 106, 108,
        110, 112, 114, 116, 119, 122, 125, 128, 131, 134, 137, 140, 143, 146, 149, 152,
        155, 158, 161, 164, 167, 170, 173, 177, 181, 185, 189, 193, 197, 201, 205, 209,
        213, 217, 221, 225, 229, 234, 239, 245, 249, 254, 259, 264, 269, 274, 279, 284,
    ];

    /// <summary>
    /// The quantizer index for a quality of 0 to 100, as lossy WebP maps its quality: the quality's compression factor (two
    /// thirds of it up to 0.75, steeper above), its cube root, scaled to 127 down to 0. The cube root is Newton's, so every
    /// machine gets the same index.
    /// </summary>
    internal static int QualityToQuantizer(int quality)
    {
        double q = quality / 100.0;
        double linear = q < 0.75 ? q * (2.0 / 3.0) : (2.0 * q) - 1.0;
        double c = linear <= 0 ? 0 : linear;
        if (c > 0)
        {
            double x = c < 0.125 ? 0.5 : c;
            for (int i = 0; i < 40; i++)
            {
                x -= ((x * x * x) - c) / (3 * x * x);
            }

            c = x;
        }

        return Math.Clamp((int)(127 * (1 - c) + 0.5), 0, 127);
    }

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
            bool alpha = _vp8.Alpha ?? EncoderPictures.HasAlpha(frame);
            Track = MakeTrack(alpha, frame);
            _pictures = new EncoderPictures(_options.Width, _options.Height, EncoderPictures.Vp8Color, _context.Egami);
            bool realtime = _options.LatencyMode == LatencyMode.Realtime;
            int partitions = _vp8.Partitions ?? (realtime ? (_options.Height >= 1080 ? 4 : _options.Height >= 720 ? 2 : 1) : 1);
            _coder = new Vp8FrameCoder(_options.Width, _options.Height, _options.Effort ?? DefaultEffort, partitions, !(_vp8.ErrorResilient ?? realtime), alpha, _context.Egami);
            _core = new EncoderCore(_settings, _coder, _pictures, alpha);
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

    private VideoTrackInfo MakeTrack(bool alpha, VideoFrame? first) => new()
    {
        Number = 1,
        Kind = TrackKind.Video,
        CodecId = CodecIds.Vp8,
        TimeBase = _options.FrameRate.Reciprocal(),
        Width = _options.Width,
        Height = _options.Height,
        FrameRate = _options.FrameRate,
        BitDepth = 8,
        ChromaFormat = ChromaFormat.Yuv420,
        Color = first is null ? Vp8VideoDecoder.Vp8Color : ColorOf(first),
        HasAlpha = alpha,
    };

    /// <summary>
    /// The color a track's frames have: 4:2:0 8-bit planes are coded as they are, so their color (the frame's, else the planes')
    /// is the track's; everything else is converted to VP8's own.
    /// </summary>
    private static ColorCodePoints ColorOf(VideoFrame frame)
    {
        if (frame.Planes is not YuvRaster<byte> { ChromaFormat: ChromaFormat.Yuv420 } planes)
        {
            return Vp8VideoDecoder.Vp8Color;
        }

        if (frame.Color is { } color)
        {
            return color;
        }

        int matrix = planes.Color.Matrix switch
        {
            YuvMatrix.Bt709 => 1,
            YuvMatrix.Bt2020Ncl => 9,
            _ => 6,
        };
        return new ColorCodePoints(2, 2, (byte)matrix, planes.Color.Range == YuvRange.Full);
    }

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

/// <summary>Codes the core's decisions with <see cref="Vp8StreamEncoder"/>, and the alpha plane with a second one.</summary>
internal sealed class Vp8FrameCoder : IFrameCoder
{
    private readonly Vp8StreamEncoder _color;
    private readonly Vp8StreamEncoder? _alpha;
    private readonly YuvRaster<byte>? _alphaPicture;
    private readonly EgamiContext _context;
    private readonly int _effort;
    private readonly int _partitions;
    private readonly bool _refreshEntropy;

    public Vp8FrameCoder(int width, int height, int effort, int partitions, bool refreshEntropy, bool alpha, EgamiContext context)
    {
        _color = new Vp8StreamEncoder(width, height, context);
        _context = context;
        _effort = effort;
        _partitions = partitions;
        _refreshEntropy = refreshEntropy;
        if (alpha)
        {
            _alpha = new Vp8StreamEncoder(width, height, context);
            _alphaPicture = YuvRaster.Create(width, height, ChromaFormat.Yuv420, EncoderPictures.Vp8Color, false, context);
            EncoderPictures.Fill(_alphaPicture.U!, 128);
            EncoderPictures.Fill(_alphaPicture.V!, 128);
        }
    }

    public short[] Steps => Vp8VideoEncoder.AcSteps;

    public CodedFrame Code(YuvRaster<byte> picture, FrameDecision decision)
    {
        Vp8FramePlan plan = decision.Type switch
        {
            FrameType.Key => new Vp8FramePlan { KeyFrame = true },
            FrameType.AltRef => new Vp8FramePlan
            {
                Show = false,
                References = Vp8References.Last | Vp8References.Golden,
                RefreshLast = false,
                RefreshAlternate = true,
                AlternateSignBias = true,
            },
            _ => new Vp8FramePlan { RefreshGolden = decision.RefreshGolden, AlternateSignBias = decision.AltRefAhead },
        };
        plan = plan with { Quantizer = decision.Quantizer, Effort = _effort, Partitions = _partitions, RefreshEntropy = _refreshEntropy };
        Vp8EncodedFrame color = _color.Encode(picture, plan);
        ReadOnlyMemory<byte>? alpha = null;
        if (_alpha is not null && decision.Type != FrameType.AltRef)
        {
            // The alpha stream has no hidden frames: every shown frame predicts from the one before, and keyframes line up.
            EncoderPictures.CopyPlane(picture.A!, _alphaPicture!.Y);
            Vp8FramePlan alphaPlan = new()
            {
                KeyFrame = decision.Type == FrameType.Key,
                Quantizer = decision.Quantizer,
                Effort = _effort,
                References = Vp8References.Last,
                RefreshEntropy = _refreshEntropy,
            };
            alpha = _alpha.Encode(_alphaPicture, alphaPlan).Data;
        }

        return new CodedFrame(color.Data, alpha, color.Quantizer);
    }

    public (long Key, long Next) Probe(YuvRaster<byte> key, YuvRaster<byte>? next, int quantizer)
    {
        using Vp8StreamEncoder probe = new(_color.Width, _color.Height, _context);
        long keyBits = probe.Encode(key, new Vp8FramePlan { KeyFrame = true, Quantizer = quantizer, Effort = _effort }).Bits;
        long nextBits = next is null ? 0 : probe.Encode(next, new Vp8FramePlan { Quantizer = quantizer, Effort = _effort }).Bits;
        return (keyBits, nextBits);
    }

    public void Dispose()
    {
        _color.Dispose();
        _alpha?.Dispose();
        _alphaPicture?.Dispose();
    }
}
