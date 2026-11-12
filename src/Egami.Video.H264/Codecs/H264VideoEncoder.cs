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

        if (h264.Profile == H264Profile.ConstrainedBaseline && h264.BFrames > 0)
        {
            throw new ArgumentException("Constrained Baseline has no B-frames.", nameof(options));
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
        long maxBitrate = options.RateControl switch
        {
            RateControlMode.ConstantBitrate => bitrate,
            RateControlMode.Bitrate => options.MaxBitrate is > 0 and var peak ? Math.Max(peak, bitrate) : bitrate * 2,
            _ => 0,
        };
        long buffer = bitrate > 0 ? options.BufferSize is > 0 and var size ? size : bitrate : 0;
        H264Levels.Demand demand = new(fps, 1, maxBitrate, buffer, h264.Profile == H264Profile.High ? 1250 : 1000);
        int level = h264.Level ?? H264Levels.Choose(widthMbs, heightMbs, demand);
        if (H264Levels.Misfit(H264Levels.Of(level), widthMbs, heightMbs, demand) is { } why)
        {
            throw new ArgumentException($"The stream does not fit its level: {why}.", nameof(options));
        }

        // B-frames predict from a reference on each side, a pyramid's middle B-frame from a third; more references where the
        // level's buffer holds them, and fewer B-frames where it does not.
        bool Fits(int refs) => H264Levels.Misfit(H264Levels.Of(level), widthMbs, heightMbs, fps, refs) is null;
        int bFrames = h264.Profile == H264Profile.ConstrainedBaseline || options.LatencyMode != LatencyMode.Quality ? 0 : h264.BFrames ?? (effort >= 1 ? 3 : 0);
        bool pyramid = bFrames is 2 or 3 && effort >= 4 && Fits(3);
        if (bFrames > 0 && !Fits(2))
        {
            bFrames = 0;
        }

        int refFrames = Math.Max(RefFrames(effort), pyramid ? 3 : bFrames > 0 ? 2 : 1);
        while (refFrames > 1 && !Fits(refFrames))
        {
            refFrames--;
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
            ReorderFrames = bFrames,
            FrameRate = options.FrameRate,
            Color = H264Color,
            Slices = Math.Min(h264.Slices ?? AutoSlices(widthMbs, heightMbs), heightMbs),
            Parallelism = Math.Max(1, context.Egami.MaxParallelism),
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
            BFrames = bFrames,
            BPyramid = pyramid,
            MacroblockTree = TreeStrength,
            Rate = new RateSettings
            {
                Mode = options.RateControl,
                BaseQuantizer = Math.Clamp(qp, h264.MinQuantizer, h264.MaxQuantizer),
                MinQuantizer = h264.MinQuantizer,
                MaxQuantizer = h264.MaxQuantizer,
                Bitrate = bitrate,
                MaxBitrate = options.MaxBitrate is > 0 and var m ? Math.Max(m, bitrate) : bitrate * 2,
                BufferSize = buffer,
                FrameRate = fps,
                Pixels = (long)options.Width * options.Height,
                Steps = H264FrameCoder.QpSteps,
                PlanWholeGroupAhead = bFrames > 0,
                ProbeQuantizer = 26,
                InterFromKeyProbe = true,
                BufferPull = 8,
                BufferStepPull = 2,
                FirstPass = options.RateControl == RateControlMode.Bitrate && options.FirstPassStats is { } stats ? FirstPassComplexities(stats) : null,
            },
        };
        Track = MakeTrack(_config);
    }

    public VideoTrackInfo Track { get; private set; }

    /// <summary>Gets the frame coder once the first frame is in, for tests.</summary>
    internal H264FrameCoder? Coder => _coder;

    /// <summary>Gets the stream's settings, for tests.</summary>
    internal H264StreamConfig Config => _config;

    /// <summary>The color of frames converted to the coded format, and of tracks before the first frame: BT.601 limited range.</summary>
    internal static readonly ColorCodePoints H264Color = new(2, 2, 6, false);

    /// <summary>The slices a picture of a size is cut into when the options leave it open: one per 1800 macroblocks, up to eight.</summary>
    internal static int AutoSlices(int widthMbs, int heightMbs) => Math.Clamp(widthMbs * heightMbs / 1800, 1, 8);

    /// <summary>The reference frames P pictures search at an effort: one, two from effort 2, three from 4.</summary>
    internal static int RefFrames(int effort) => effort >= 4 ? 3 : effort >= 2 ? 2 : 1;

    /// <summary>
    /// The macroblock tree's strength (<see cref="CoreSettings.MacroblockTree"/>): a frame's step falls by 2^(-0.25) for each
    /// doubling of its content's worth through reuse, 1.5 QP.
    /// </summary>
    internal const double TreeStrength = 0.25;

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
            _coder = new H264FrameCoder(config, _options.Effort ?? DefaultEffort, _h264, frame.Hdr);
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
/// and the HDR SEI when the frames have HDR metadata), a bidirectional frame a B picture (a reference one in a pyramid's
/// middle), every other frame a reference P picture.
/// </summary>
internal sealed class H264FrameCoder : IFrameCoder
{
    /// <summary>The step of each QP, 0.625 · 2^(QP / 6) in sixteenths, for the core's bit model.</summary>
    internal static readonly short[] QpSteps = [.. Enumerable.Range(0, 52).Select(qp => (short)Math.Round(10 * Math.Pow(2, qp / 6.0)))];

    private readonly H264StreamConfig _config;
    private readonly int _effort;
    private readonly bool _annexB;
    private readonly H264EncoderOptions _options;
    private readonly H264PictureEncoder _encoder;
    private readonly byte[] _sps;
    private readonly byte[] _pps;
    private readonly byte[]? _sei;
    private int _frameNum;
    private int _idrPicId = -1;
    private int _idrIndex;

    public H264FrameCoder(H264StreamConfig config, int effort, H264EncoderOptions options, HdrMetadata? hdr)
    {
        _config = config;
        _effort = effort;
        _annexB = options.AnnexB;
        _options = options;
        _encoder = NewEncoder();
        _sps = H264StreamWriter.Sps(config);
        _pps = H264StreamWriter.Pps(config);
        _sei = H264StreamWriter.HdrSei(hdr);
    }

    public short[] Steps => QpSteps;

    private H264PictureEncoder NewEncoder() => new(_config, _effort)
    {
        AdaptiveQuantization = _options.AdaptiveQuantization,
        MinQp = _options.MinQuantizer,
        MaxQp = _options.MaxQuantizer,
    };

    /// <summary>Gets the reconstruction of the last frame coded, for tests.</summary>
    internal H264Picture Recon => _encoder.Recon;

    /// <summary>Gets or sets what runs after each frame is coded, with its reconstruction, for tests.</summary>
    internal Action<H264Picture, int>? Coded { get; set; }

    /// <summary>Gets or sets the direct prediction B pictures are forced to, temporal or spatial, for tests.</summary>
    internal bool? TemporalDirect
    {
        get => _encoder.TemporalDirect;
        set => _encoder.TemporalDirect = value;
    }

    /// <summary>Gets or sets the motion search shortcuts in use, none by default, for tests and measurements.</summary>
    internal H264SearchShortcuts SearchShortcuts
    {
        get => _encoder.SearchShortcuts;
        set => _encoder.SearchShortcuts = value;
    }

    /// <summary>Gets how many B pictures were coded, for tests.</summary>
    internal int BPictures { get; private set; }

    /// <summary>Gets how many B pictures predicted direct blocks temporally, for tests.</summary>
    internal int TemporalPictures { get; private set; }

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
        int poc = 2 * (decision.Index - _idrIndex);
        _encoder.Load(picture);
        byte[][] slices = idr || _encoder.References == 0
            ? _encoder.EncodeIntra(idr, _idrPicId, _frameNum, poc, qp, decision.Referenced)
            : decision.Type == FrameType.Bidirectional
            ? EncodeB(poc, qp, decision.Referenced)
            : _encoder.EncodePredicted(_frameNum, poc, qp, _config.MaxRefFrames, decision.Referenced);
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

        nals.AddRange(slices);
        Coded?.Invoke(_encoder.Recon, decision.Index);
        return new CodedFrame(H264StreamWriter.AccessUnit(nals, _annexB), null, qp);
    }

    private byte[][] EncodeB(int poc, int qp, bool referenced)
    {
        byte[][] slices = _encoder.EncodeBidirectional(_frameNum, poc, qp, referenced);
        BPictures++;
        TemporalPictures += _encoder.LastTemporal ? 1 : 0;
        return slices;
    }

    public (long Key, long Next) Probe(YuvRaster<byte> key, YuvRaster<byte>? next, int quantizer)
    {
        H264PictureEncoder probe = NewEncoder();
        int qp = Math.Clamp(quantizer, 0, 51);
        probe.Load(key);
        long keyBits = probe.EncodeIntra(true, 0, 0, 0, qp).Sum(n => (long)n.Length) * 8;
        long nextBits = 0;
        if (next is not null)
        {
            probe.Load(next);
            nextBits = probe.EncodePredicted(1, 2, qp, 1).Sum(n => (long)n.Length) * 8;
        }

        return (keyBits, nextBits);
    }

    public void Dispose()
    {
    }
}
