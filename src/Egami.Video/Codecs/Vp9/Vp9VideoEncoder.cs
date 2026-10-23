using System.Diagnostics.CodeAnalysis;

using Egami.Colors;
using Egami.Pixels;
using Egami.Video.Codecs.Encoding;

namespace Egami.Video.Codecs.Vp9;

/// <summary>
/// Encodes VP9 with <see cref="Vp9Encoder{T}"/>, the encoder core planning every frame: keyframes, golden frames, hidden
/// alternate references (sent in a superframe with the next shown frame) and quantizers. A stream with alpha codes the alpha
/// plane as a second VP9 stream whose frames ride in each shown packet's addition 1, as WebM carries them.
/// </summary>
internal sealed class Vp9VideoEncoder : IVideoEncoder
{
    /// <summary>The quality when the options leave it open, as VP8's.</summary>
    public const int DefaultQuality = 60;

    /// <summary>The effort when the options leave it open.</summary>
    public const int DefaultEffort = 4;

    private readonly VideoEncoderOptions _options;
    private readonly Vp9EncoderOptions _vp9;
    private readonly MediaContext _context;
    private readonly CoreSettings _settings;
    private EncoderPictures? _pictures;
    private Vp9FrameCoder? _coder;
    private EncoderCore? _core;
    private bool _disposed;

    public Vp9VideoEncoder(VideoEncoderOptions options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Width, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Width, 65536, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Height, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Height, 65536, nameof(options));
        if (options.FrameRate.Numerator <= 0 || options.FrameRate.Denominator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.FrameRate, "The frame rate must be positive.");
        }

        int quality = options.Quality ?? DefaultQuality;
        int effort = options.Effort ?? DefaultEffort;
        Check(quality, 0, 100, "Quality");
        Check(effort, 0, 10, "Effort");
        Vp9EncoderOptions vp9 = options.Vp9 ?? Vp9EncoderOptions.Default;
        if (vp9.MinQuantizer > vp9.MaxQuantizer)
        {
            throw new ArgumentException($"The smallest quantizer {vp9.MinQuantizer} is above the largest {vp9.MaxQuantizer}.", nameof(options));
        }

        int baseQuantizer = options.Quantizer is int q ? Check(q, 0, 255, "Quantizer") : QualityToQuantizer(quality);
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
        _vp9 = vp9;
        _context = context;
        _settings = new CoreSettings
        {
            Width = options.Width,
            Height = options.Height,
            FrameRate = options.FrameRate,
            LatencyMode = options.LatencyMode,
            KeyframeInterval = keyInterval,
            SceneCuts = options.SceneCutKeyframes,
            AltRef = vp9.AlternateReference && !realtime,
            GoldenInterval = vp9.GoldenInterval,
            FilterFrames = vp9.TemporalFilterFrames,
            FilterStrength = vp9.TemporalFilterStrength,
            Parallelism = context.Egami.MaxParallelism,

            // VP9's hidden frames carry the group (compound prediction reads them from both sides): they are tried where the
            // group's first frame predicts its last only twice as well as intra, and coded with much finer steps than VP8's.
            AltRefReach = 2,
            AltRefStep = 0.35,
            AltRefStepRange = 0.15,
            GoldenStep = 0.6,
            GoldenStepRange = 0.2,
            Rate = new RateSettings
            {
                Mode = options.RateControl,
                BaseQuantizer = Math.Clamp(baseQuantizer, vp9.MinQuantizer, vp9.MaxQuantizer),
                MinQuantizer = vp9.MinQuantizer,
                MaxQuantizer = vp9.MaxQuantizer,
                Bitrate = bitrate,
                MaxBitrate = options.MaxBitrate is > 0 and var m ? Math.Max(m, bitrate) : bitrate * 2,
                BufferSize = options.BufferSize is > 0 and var buffer ? buffer : bitrate,
                FrameRate = fps,
                Pixels = (long)options.Width * options.Height,
                Steps = Vp9Tables.AcQLookup8,
                StepFactors = [0.45, 1.0, 0.8, 0.7, 1.4],
                FirstPass = options.RateControl == RateControlMode.Bitrate && options.FirstPassStats is { } stats ? FirstPassComplexities(stats) : null,
            },
        };
        Track = MakeTrack(vp9.Alpha ?? false, null);
    }

    public VideoTrackInfo Track { get; private set; }

    /// <summary>Gets the rate controller once the first frame is in, for tests.</summary>
    internal RateController? Rate => _core?.Rate;

    /// <summary>
    /// The quantizer index for a quality of 0 to 100: VP8's mapping (lossy WebP's) on VP9's twice finer index scale, so equal
    /// qualities look alike in both.
    /// </summary>
    internal static int QualityToQuantizer(int quality) => Math.Clamp(2 * Vp8.Vp8VideoEncoder.QualityToQuantizer(quality), 0, 255);

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
            bool alpha = _vp9.Alpha ?? EncoderPictures.HasAlpha(frame);
            (int depth, ChromaFormat chroma) = FormatOf(frame);
            Track = MakeTrack(alpha, frame, depth, chroma);
            ColorCodePoints color = Track.Color ?? Vp9Color;
            _pictures = new EncoderPictures(_options.Width, _options.Height, EncoderPictures.Vp8Color, _context.Egami)
            {
                FullFormat = (chroma, depth, YuvColorOf(color)),
            };
            bool realtime = _options.LatencyMode == LatencyMode.Realtime;
            int tileLog2 = _vp9.TileColumns is int t ? System.Numerics.BitOperations.Log2((uint)t) : 2;
            _coder = new Vp9FrameCoder(_options.Width, _options.Height, depth, chroma, ColorSpaceOf(color), color.FullRange ? 1 : 0,
                _options.Effort ?? DefaultEffort, tileLog2, _vp9.AdaptiveQuantization, _vp9.ErrorResilient ?? realtime, alpha, _context.Egami);
            bool full = depth != 8 || chroma != ChromaFormat.Yuv420;
            _core = new EncoderCore(_settings with { FullPictures = full }, _coder, _pictures, alpha);
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

    /// <summary>VP9's default color, BT.601 limited range, as VP8's.</summary>
    internal static readonly ColorCodePoints Vp9Color = new(2, 2, 6, false);

    /// <summary>The VP9 color_space of a matrix: BT.601 1, BT.709 2, SMPTE 170 3, SMPTE 240 4, BT.2020 5, RGB 7.</summary>
    internal static int ColorSpaceOf(ColorCodePoints color) => color.MatrixCoefficients switch
    {
        0 => 7,
        1 => 2,
        5 or 6 => 1,
        7 => 4,
        9 or 10 => 5,
        _ => 1,
    };

    private VideoTrackInfo MakeTrack(bool alpha, VideoFrame? first, int depth = 8, ChromaFormat chroma = ChromaFormat.Yuv420) => new()
    {
        Number = 1,
        Kind = TrackKind.Video,
        CodecId = CodecIds.Vp9,
        TimeBase = _options.FrameRate.Reciprocal(),
        Width = _options.Width,
        Height = _options.Height,
        FrameRate = _options.FrameRate,
        BitDepth = depth,
        ChromaFormat = chroma,
        Color = first is null ? Vp9Color : ColorOf(first, depth, chroma),
        Hdr = first?.Hdr,
        HasAlpha = alpha,
    };

    /// <summary>
    /// The bit depth and chroma format to code: the options' depth, else the planes' (16-bit planes of 9 or 10 bits code 10,
    /// deeper ones 12); the planes' chroma format (4:4:0 and gray code 4:2:0), else the options' for RGB frames, else 4:2:0.
    /// </summary>
    private (int Depth, ChromaFormat Chroma) FormatOf(VideoFrame frame)
    {
        int depth = _vp9.BitDepth ?? (frame.Planes is YuvRaster<ushort> words ? (words.BitDepth <= 10 ? 10 : 12) : 8);
        ChromaFormat chroma = frame.Planes?.ChromaFormat ?? _options.ChromaFormat ?? ChromaFormat.Yuv420;
        if (chroma is not (ChromaFormat.Yuv420 or ChromaFormat.Yuv422 or ChromaFormat.Yuv444))
        {
            chroma = ChromaFormat.Yuv420;
        }

        return (depth, chroma);
    }

    private static YuvColor YuvColorOf(ColorCodePoints color) => new(
        color.MatrixCoefficients switch { 1 => YuvMatrix.Bt709, 9 or 10 => YuvMatrix.Bt2020Ncl, _ => YuvMatrix.Bt601 },
        color.FullRange ? YuvRange.Full : YuvRange.Limited,
        ChromaSiting.Center);

    /// <summary>
    /// The color a track's frames have: planes coded as they are keep theirs (the frame's code points, else the planes'
    /// matrix and range); frames converted to the coded format are BT.601 limited range, unless they say otherwise.
    /// </summary>
    private static ColorCodePoints ColorOf(VideoFrame frame, int depth, ChromaFormat chroma)
    {
        if (frame.Color is { } color)
        {
            return color;
        }

        if (frame.Planes is not { } planes || planes.ChromaFormat != chroma || (depth == 8) != (planes is YuvRaster<byte>))
        {
            return Vp9Color;
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
/// Codes the core's decisions with <see cref="Vp9Encoder{T}"/>: slot 0 holds LAST, 1 GOLDEN and 2 ALTREF. A hidden alternate
/// reference waits for the next shown frame and goes out with it as one superframe. Streams of another bit depth or chroma
/// format than 8-bit 4:2:0 code the core's full pictures (an alternate reference then is its frame unfiltered). The alpha plane,
/// when there is one, is a second 8-bit 4:2:0 stream of shown frames only, keyframes lined up with the picture's.
/// </summary>
internal sealed class Vp9FrameCoder : IFrameCoder
{
    private readonly Vp9Encoder<byte>? _color8;
    private readonly Vp9Encoder<ushort>? _color16;
    private readonly Vp9Encoder<byte>? _alpha;
    private readonly EgamiContext _context;
    private readonly int _effort;
    private readonly int _tileLog2;
    private readonly bool _aq;
    private readonly bool _errorResilient;
    private readonly int _colorSpace;
    private readonly int _colorRange;
    private readonly int _width;
    private readonly int _height;
    private byte[]? _hidden;

    public Vp9FrameCoder(int width, int height, int bitDepth, ChromaFormat chroma, int colorSpace, int colorRange, int effort, int tileLog2, bool aq,
        bool errorResilient, bool alpha, EgamiContext context)
    {
        _context = context;
        _effort = effort;
        _tileLog2 = tileLog2;
        _aq = aq;
        _errorResilient = errorResilient;
        _colorSpace = colorSpace;
        _colorRange = colorRange;
        _width = width;
        _height = height;
        int subX = chroma == ChromaFormat.Yuv444 ? 0 : 1, subY = chroma == ChromaFormat.Yuv420 ? 1 : 0;
        int parallelism = Math.Max(1, context.MaxParallelism);
        if (bitDepth == 8)
        {
            _color8 = new Vp9Encoder<byte>(width, height, 8, subX, subY, colorSpace, colorRange, context.Memory) { MaxParallelism = parallelism };
        }
        else
        {
            _color16 = new Vp9Encoder<ushort>(width, height, bitDepth, subX, subY, colorSpace, colorRange, context.Memory) { MaxParallelism = parallelism };
        }

        if (alpha)
        {
            _alpha = new Vp9Encoder<byte>(width, height, 8, 1, 1, 1, 0, context.Memory) { MaxParallelism = parallelism };
        }
    }

    public short[] Steps => Vp9Tables.AcQLookup8;

    public bool MergesHiddenFrames => true;

    public CodedFrame Code(YuvRaster<byte> picture, FrameDecision decision) => Code(picture, null, decision);

    public CodedFrame Code(YuvRaster<byte> picture, YuvRaster? full, FrameDecision decision)
    {
        Vp9FramePlan plan = PlanOf(decision);
        ReadOnlyMemory<byte> data;
        if (_color8 is not null)
        {
            YuvRaster<byte> source = full as YuvRaster<byte> ?? picture;
            Load(_color8, source.Y, source.U, source.V);
            data = _color8.Encode(plan);
        }
        else
        {
            YuvRaster<ushort> source = (YuvRaster<ushort>)full!;
            Load(_color16!, source.Y, source.U, source.V);

            // The core's quantizers are on the 8-bit scale; the index means the same step at any depth.
            data = _color16!.Encode(plan);
        }

        if (decision.Type == FrameType.AltRef)
        {
            _hidden = data.ToArray();
            return new CodedFrame(_hidden, null, decision.Quantizer);
        }

        ReadOnlyMemory<byte>? alpha = null;
        if (_alpha is not null)
        {
            Load(_alpha, picture.A!, null, null);
            Vp9FramePlan alphaPlan = new()
            {
                Key = decision.Type == FrameType.Key,
                Quantizer = decision.Quantizer,
                Effort = _effort,
                RefreshFlags = 1,
                TileColumnsLog2 = _tileLog2,
                ErrorResilient = _errorResilient,
                RefreshContext = !_errorResilient,
            };
            alpha = _alpha.Encode(alphaPlan).ToArray();
        }

        if (_hidden is { } hidden)
        {
            _hidden = null;
            byte[] merged = Vp9Superframe.Merge([hidden, data]);
            return new CodedFrame(merged, alpha, decision.Quantizer, hidden.Length);
        }

        return new CodedFrame(data, alpha, decision.Quantizer);
    }

    private Vp9FramePlan PlanOf(FrameDecision decision) => decision.Type switch
    {
        FrameType.Key => Common(new Vp9FramePlan { Key = true }, decision),
        FrameType.AltRef => Common(new Vp9FramePlan { Show = false, RefreshFlags = 4, AltRefAhead = true, FrameContext = 1 }, decision),
        FrameType.Overlay => Common(new Vp9FramePlan { RefreshFlags = 1 | (decision.RefreshGolden ? 2 : 0), Overlay = true }, decision),
        _ => Common(new Vp9FramePlan { RefreshFlags = 1 | (decision.RefreshGolden ? 2 : 0), AltRefAhead = decision.AltRefAhead }, decision),
    };

    private Vp9FramePlan Common(Vp9FramePlan plan, FrameDecision decision) => plan with
    {
        Quantizer = decision.Quantizer,
        Effort = _effort,
        TileColumnsLog2 = _tileLog2,
        AqMode = _aq ? 1 : 0,
        ErrorResilient = _errorResilient,
        RefreshContext = !_errorResilient,
    };

    /// <summary>Copies a picture's planes into an encoder; a missing chroma plane is mid-grey.</summary>
    private static void Load<T>(Vp9Encoder<T> encoder, YuvPlane<T> y, YuvPlane<T>? u, YuvPlane<T>? v)
        where T : unmanaged, System.Numerics.IBinaryInteger<T>
    {
        for (int row = 0; row < encoder.Height; row++)
        {
            encoder.SetSourceRow(0, row, y.Row(row));
        }

        int chromaHeight = (encoder.Height + encoder.SubsamplingY) >> encoder.SubsamplingY;
        int chromaWidth = (encoder.Width + encoder.SubsamplingX) >> encoder.SubsamplingX;
        if (u is null)
        {
            T[] grey = new T[chromaWidth];
            Array.Fill(grey, T.CreateTruncating(1 << (encoder.BitDepth - 1)));
            for (int row = 0; row < chromaHeight; row++)
            {
                encoder.SetSourceRow(1, row, grey);
                encoder.SetSourceRow(2, row, grey);
            }
        }
        else
        {
            for (int row = 0; row < chromaHeight; row++)
            {
                encoder.SetSourceRow(1, row, u.Row(row));
                encoder.SetSourceRow(2, row, v!.Row(row));
            }
        }

        encoder.FinishSource();
    }

    public (long Key, long Next) Probe(YuvRaster<byte> key, YuvRaster<byte>? next, int quantizer)
    {
        // The probe codes the core's 8-bit 4:2:0 pictures: what rate control learns is how the content's bits fall with the step.
        using Vp9Encoder<byte> probe = new(_width, _height, 8, 1, 1, _colorSpace, _colorRange, _context.Memory)
        {
            MaxParallelism = Math.Max(1, _context.MaxParallelism),
        };
        Load(probe, key.Y, key.U!, key.V!);
        long keyBits = probe.Encode(new Vp9FramePlan { Key = true, Quantizer = quantizer, Effort = _effort, TileColumnsLog2 = _tileLog2 }).Length * 8L;
        long nextBits = 0;
        if (next is not null)
        {
            Load(probe, next.Y, next.U!, next.V!);
            nextBits = probe.Encode(new Vp9FramePlan { Quantizer = quantizer, Effort = _effort, TileColumnsLog2 = _tileLog2 }).Length * 8L;
        }

        return (keyBits, nextBits);
    }

    public void Dispose()
    {
        _color8?.Dispose();
        _color16?.Dispose();
        _alpha?.Dispose();
    }
}
