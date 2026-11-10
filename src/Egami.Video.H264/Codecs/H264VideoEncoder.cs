using System.Diagnostics.CodeAnalysis;

using Egami.Colors;
using Egami.Pixels;
using Egami.Video.Codecs.Encoding;
using Egami.Video.Codecs.H264;

namespace Egami.Video.Codecs;

/// <summary>
/// Encodes H.264 with the shared encoder core planning keyframes and quantizers and <see cref="H264FrameCoder"/> coding them:
/// 8-bit 4:2:0 progressive frames in the Constrained Baseline, Main or High profile, with CABAC or CAVLC.
/// </summary>
internal sealed class H264VideoEncoder : IVideoEncoder
{
    /// <summary>The quality when the options leave it open, as Egami's other encoders' (50 is about x264's default CRF 23).</summary>
    public const int DefaultQuality = 60;

    /// <summary>The effort when the options leave it open.</summary>
    public const int DefaultEffort = 4;

    private readonly VideoEncoderOptions _options;
    private readonly H264EncoderOptions _h264;
    private readonly MediaContext _context;
    private readonly CoreSettings _settings;
    private readonly H264StreamConfig _config;
    private EncoderPictures? _pictures;
    private H264FrameCoder? _coder;
    private EncoderCore? _core;
    private bool _disposed;

    public H264VideoEncoder(VideoEncoderOptions options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Width, 2, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Height, 2, nameof(options));
        if ((options.Width & 1) != 0 || (options.Height & 1) != 0)
        {
            throw new ArgumentException($"H.264 4:2:0 needs an even width and height; {options.Width}x{options.Height} is not.", nameof(options));
        }

        if (options.FrameRate.Numerator <= 0 || options.FrameRate.Denominator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.FrameRate, "The frame rate must be positive.");
        }

        int quality = options.Quality ?? DefaultQuality;
        int effort = options.Effort ?? DefaultEffort;
        Check(quality, 0, 100, "Quality");
        Check(effort, 0, 10, "Effort");
        H264EncoderOptions h264 = options.H264 ?? H264EncoderOptions.Default;
        if (h264.MinQuantizer > h264.MaxQuantizer)
        {
            throw new ArgumentException($"The smallest QP {h264.MinQuantizer} is above the largest {h264.MaxQuantizer}.", nameof(options));
        }

        if (h264.Profile == H264Profile.ConstrainedBaseline && h264.Entropy == H264EntropyCoder.Cabac)
        {
            throw new ArgumentException("Constrained Baseline codes with CAVLC only.", nameof(options));
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
        int widthMbs = (options.Width + 15) >> 4, heightMbs = (options.Height + 15) >> 4;
        const int refFrames = 1;
        int level = h264.Level ?? H264Levels.Choose(widthMbs, heightMbs, fps, refFrames);
        if (H264Levels.Misfit(H264Levels.Of(level), widthMbs, heightMbs, fps, refFrames) is { } why)
        {
            throw new ArgumentException($"The stream does not fit its level: {why}.", nameof(options));
        }

        _options = options;
        _h264 = h264;
        _context = context;
        bool cabac = h264.Profile != H264Profile.ConstrainedBaseline && h264.Entropy != H264EntropyCoder.Cavlc;
        _config = new H264StreamConfig
        {
            Width = options.Width,
            Height = options.Height,
            ProfileIdc = h264.Profile switch { H264Profile.ConstrainedBaseline => 66, H264Profile.Main => 77, _ => 100 },
            LevelIdc = level,
            Cabac = cabac,
            Transform8x8 = h264.Profile == H264Profile.High,
            MaxRefFrames = refFrames,
            FrameRate = options.FrameRate,
            Color = H264Color,
        };
        _settings = new CoreSettings
        {
            Width = options.Width,
            Height = options.Height,
            FrameRate = options.FrameRate,
            LatencyMode = options.LatencyMode,
            KeyframeInterval = keyInterval,
            SceneCuts = options.SceneCutKeyframes,
            Parallelism = context.Egami.MaxParallelism,
            Rate = new RateSettings
            {
                Mode = options.RateControl,
                BaseQuantizer = Math.Clamp(qp, h264.MinQuantizer, h264.MaxQuantizer),
                MinQuantizer = h264.MinQuantizer,
                MaxQuantizer = h264.MaxQuantizer,
                Bitrate = bitrate,
                MaxBitrate = options.MaxBitrate is > 0 and var m ? Math.Max(m, bitrate) : bitrate * 2,
                BufferSize = options.BufferSize is > 0 and var buffer ? buffer : bitrate,
                FrameRate = fps,
                Pixels = (long)options.Width * options.Height,
                Steps = H264FrameCoder.QpSteps,
                FirstPass = options.RateControl == RateControlMode.Bitrate && options.FirstPassStats is { } stats ? FirstPassComplexities(stats) : null,
            },
        };
        Track = MakeTrack(_config);
    }

    public VideoTrackInfo Track { get; private set; }

    /// <summary>Gets the frame coder once the first frame is in, for tests.</summary>
    internal H264FrameCoder? Coder => _coder;

    /// <summary>The color of frames converted to the coded format, and of tracks before the first frame: BT.601 limited range.</summary>
    internal static readonly ColorCodePoints H264Color = new(2, 2, 6, false);

    /// <summary>The QP for a quality of 0 to 100: 51 at 0, 23 (x264's default CRF) at 50, 0 at 100 or so.</summary>
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
            ColorCodePoints color = ColorOf(frame);
            H264StreamConfig config = _config with { Color = color };
            _coder = new H264FrameCoder(config, _options.Effort ?? DefaultEffort, _h264.AnnexB, frame.Hdr);
            Track = MakeTrack(config, frame.Hdr);
            _pictures = new EncoderPictures(_options.Width, _options.Height, EncoderPictures.Vp8Color, _context.Egami);
            _core = new EncoderCore(_settings, _coder, _pictures, alpha: false);
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

    private VideoTrackInfo MakeTrack(H264StreamConfig config, HdrMetadata? hdr = null) => new()
    {
        Number = 1,
        Kind = TrackKind.Video,
        CodecId = CodecIds.Avc,
        CodecPrivate = _h264.AnnexB ? ReadOnlyMemory<byte>.Empty : H264StreamWriter.AvcC(config, H264StreamWriter.Sps(config), H264StreamWriter.Pps(config)),
        TimeBase = _options.FrameRate.Reciprocal(),
        Width = _options.Width,
        Height = _options.Height,
        FrameRate = _options.FrameRate,
        BitDepth = 8,
        ChromaFormat = ChromaFormat.Yuv420,
        Color = config.Color,
        Hdr = hdr,
    };

    /// <summary>
    /// The color a track's frames have: 4:2:0 8-bit planes are coded as they are, so their color (the frame's, else the planes')
    /// is the track's; everything else is converted to BT.601 limited range.
    /// </summary>
    private static ColorCodePoints ColorOf(VideoFrame frame)
    {
        if (frame.Planes is not YuvRaster<byte> { ChromaFormat: ChromaFormat.Yuv420 } planes)
        {
            return H264Color;
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

/// <summary>
/// Codes the core's decisions as H.264 access units: a keyframe is an IDR picture (with the parameter sets in Annex B streams,
/// and the HDR SEI when the frames have HDR metadata), every other frame a reference I picture.
/// </summary>
internal sealed class H264FrameCoder : IFrameCoder
{
    /// <summary>The step of each QP, 0.625 · 2^(QP / 6) in sixteenths, for the core's bit model.</summary>
    internal static readonly short[] QpSteps = [.. Enumerable.Range(0, 52).Select(qp => (short)Math.Round(10 * Math.Pow(2, qp / 6.0)))];

    private readonly H264StreamConfig _config;
    private readonly int _effort;
    private readonly bool _annexB;
    private readonly H264PictureEncoder _encoder;
    private readonly byte[] _sps;
    private readonly byte[] _pps;
    private readonly byte[]? _sei;
    private int _frameNum;
    private int _idrPicId = -1;
    private int _idrIndex;

    public H264FrameCoder(H264StreamConfig config, int effort, bool annexB, HdrMetadata? hdr)
    {
        _config = config;
        _effort = effort;
        _annexB = annexB;
        _encoder = new H264PictureEncoder(config, effort);
        _sps = H264StreamWriter.Sps(config);
        _pps = H264StreamWriter.Pps(config);
        _sei = H264StreamWriter.HdrSei(hdr);
    }

    public short[] Steps => QpSteps;

    /// <summary>Gets the reconstruction of the last frame coded, for tests.</summary>
    internal H264Picture Recon => _encoder.Recon;

    /// <summary>Gets or sets what runs after each frame is coded, with its reconstruction, for tests.</summary>
    internal Action<H264Picture>? Coded { get; set; }

    public CodedFrame Code(YuvRaster<byte> picture, FrameDecision decision)
    {
        bool idr = decision.Type == FrameType.Key;
        if (idr)
        {
            _idrPicId = (_idrPicId + 1) & 0xFFFF;
            _frameNum = 0;
            _idrIndex = decision.Index;
        }

        int qp = Math.Clamp(decision.Quantizer, 0, 51);
        _encoder.Load(picture);
        byte[] slice = _encoder.EncodeIntra(idr, _idrPicId, _frameNum, 2 * (decision.Index - _idrIndex), qp);
        if (decision.Referenced)
        {
            _frameNum = (_frameNum + 1) & ((1 << H264StreamWriter.FrameNumBits) - 1);
        }

        List<byte[]> nals = [];
        if (idr && _annexB)
        {
            nals.Add(_sps);
            nals.Add(_pps);
        }

        if (idr && _sei is not null)
        {
            nals.Add(_sei);
        }

        nals.Add(slice);
        Coded?.Invoke(_encoder.Recon);
        return new CodedFrame(H264StreamWriter.AccessUnit(nals, _annexB), null, qp);
    }

    public (long Key, long Next) Probe(YuvRaster<byte> key, YuvRaster<byte>? next, int quantizer)
    {
        H264PictureEncoder probe = new(_config, _effort);
        int qp = Math.Clamp(quantizer, 0, 51);
        probe.Load(key);
        long keyBits = probe.EncodeIntra(true, 0, 0, 0, qp).Length * 8L;
        long nextBits = 0;
        if (next is not null)
        {
            probe.Load(next);
            nextBits = probe.EncodeIntra(false, 0, 1, 2, qp).Length * 8L;
        }

        return (keyBits, nextBits);
    }

    public void Dispose()
    {
    }
}
