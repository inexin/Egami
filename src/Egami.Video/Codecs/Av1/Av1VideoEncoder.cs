using System.Diagnostics.CodeAnalysis;

using Egami.Colors;
using Egami.Formats.Av1;
using Egami.Pixels;
using Egami.Video.Codecs.Encoding;

namespace Egami.Video.Codecs.Av1;

/// <summary>
/// Encodes AV1 with Egami's <see cref="Av1StreamEncoder"/>, the encoder core planning every frame: keyframes, golden frames, a
/// hidden alternate reference at each group's end and a hidden frame halfway to it, each shown later with show_existing_frame,
/// and quantizers. Packets are temporal units of low-overhead OBUs without temporal delimiters, as MP4 and Matroska store them;
/// a keyframe's unit carries the sequence header and the static HDR metadata OBUs.
/// </summary>
internal sealed class Av1VideoEncoder : IVideoEncoder
{
    /// <summary>The quality when the options leave it open, as VP8's and VP9's.</summary>
    public const int DefaultQuality = 60;

    /// <summary>The effort when the options leave it open.</summary>
    public const int DefaultEffort = 4;

    private readonly VideoEncoderOptions _options;
    private readonly Av1EncoderOptions _av1;
    private readonly MediaContext _context;
    private readonly CoreSettings _settings;
    private readonly Dictionary<int, ReadOnlyMemory<byte>> _hdr10Plus = [];
    private EncoderPictures? _pictures;
    private Av1FrameCoder? _coder;
    private EncoderCore? _core;
    private int _sent;
    private bool _disposed;

    public Av1VideoEncoder(VideoEncoderOptions options, MediaContext context)
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
        Av1EncoderOptions av1 = options.Av1 ?? Av1EncoderOptions.Default;
        if (av1.MinQuantizer > av1.MaxQuantizer)
        {
            throw new ArgumentException($"The smallest quantizer {av1.MinQuantizer} is above the largest {av1.MaxQuantizer}.", nameof(options));
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
        _av1 = av1;
        _context = context;
        _settings = new CoreSettings
        {
            Width = options.Width,
            Height = options.Height,
            FrameRate = options.FrameRate,
            LatencyMode = options.LatencyMode,
            KeyframeInterval = keyInterval,
            SceneCuts = options.SceneCutKeyframes,
            AltRef = av1.AlternateReference && !realtime,
            Pyramid = av1.Pyramid,
            TemporalModel = av1.Pyramid && Environment.GetEnvironmentVariable("EGAMI_AV1X")?.Contains("notpl", StringComparison.Ordinal) != true,
            Lookahead = av1.Pyramid ? 40 : EncoderCore.Lookahead,
            ImportanceStep = X("is", 1.0),
            KeyImportanceStep = X("ks", 0.9),
            ImportanceStepMin = X("ismin", 0.25),
            ImportanceStepMax = X("ismax", 0.8),
            LeafStep = X("leaf", 1.0),
            FilterKeyframes = X("kf", 0) != 0,
            AdaptiveFilter = X("atf", 0) != 0,
            FilterReach = X("tfc", 3),
            FilterReferenceQuantizer = (int)X("tfq", 96),
            ReferencedLeaves = X("rl", 0) != 0,
            FreeOverlays = true,
            GoldenInterval = av1.GoldenInterval,
            FilterFrames = (int)X("tff", av1.TemporalFilterFrames),
            FilterStrength = (int)X("tfs", av1.TemporalFilterStrength),
            Parallelism = context.Egami.MaxParallelism,

            // AV1's hidden frames carry the group as VP9's do: every frame before them predicts from them, from both sides.
            AltRefReach = 2,
            AltRefStep = 0.35,
            AltRefStepRange = 0.15,
            GoldenStep = 0.6,
            GoldenStepRange = 0.2,
            Rate = new RateSettings
            {
                Mode = options.RateControl,
                BaseQuantizer = Math.Clamp(baseQuantizer, av1.MinQuantizer, av1.MaxQuantizer),
                MinQuantizer = av1.MinQuantizer,
                MaxQuantizer = av1.MaxQuantizer,
                Bitrate = bitrate,
                MaxBitrate = options.MaxBitrate is > 0 and var m ? Math.Max(m, bitrate) : bitrate * 2,
                BufferSize = options.BufferSize is > 0 and var buffer ? buffer : bitrate,
                FrameRate = fps,
                Pixels = (long)options.Width * options.Height,

                // AV1's 8-bit AC steps are VP9's.
                Steps = Vp9.Vp9Tables.AcQLookup8,
                StepFactors = [0.45, 1.0, 0.8, 0.7, 1.0],
                PlanHiddenAhead = true,
                InterFromKeyProbe = true,
                FirstPass = options.RateControl == RateControlMode.Bitrate && options.FirstPassStats is { } stats ? FirstPassComplexities(stats) : null,
            },
        };
        Track = MakeTrack(null);
    }

    public VideoTrackInfo Track { get; private set; }

    private static double X(string name, double fallback)
    {
        foreach (string part in (Environment.GetEnvironmentVariable("EGAMI_AV1X") ?? "").Split(','))
        {
            string[] kv = part.Split('=');
            if (kv.Length == 2 && kv[0] == name)
            {
                return double.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return fallback;
    }

    /// <summary>Gets the rate controller once the first frame is in, for tests.</summary>
    internal RateController? Rate => _core?.Rate;

    /// <summary>
    /// The base_q_idx for a quality of 0 to 100: VP9's, whose 8-bit AC steps AV1 shares, so equal qualities look alike in VP8,
    /// VP9 and AV1.
    /// </summary>
    internal static int QualityToQuantizer(int quality) => Vp9.Vp9VideoEncoder.QualityToQuantizer(quality);

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
            (int depth, ChromaFormat chroma) = FormatOf(frame);
            ColorCodePoints color = ColorOf(frame, depth, chroma);
            ChromaSiting? siting = chroma == ChromaFormat.Yuv420 && frame.Planes is { } planes && SameFormat(planes, depth, chroma) ? planes.Color.Siting : null;
            _pictures = new EncoderPictures(_options.Width, _options.Height, EncoderPictures.Vp8Color, _context.Egami)
            {
                FullFormat = (chroma, depth, YuvColorOf(color)),
            };
            bool realtime = _options.LatencyMode == LatencyMode.Realtime;
            double fps = (double)_options.FrameRate.Numerator / _options.FrameRate.Denominator;
            Av1SequenceOptions sequence = new()
            {
                Width = _options.Width,
                Height = _options.Height,
                BitDepth = depth,
                Chroma = chroma,
                CodePoints = color,
                ChromaSiting = siting is ChromaSiting.Left or ChromaSiting.TopLeft ? siting : null,
                Level = Level(_options.Width, _options.Height, fps),
            };
            Av1FramePlan plan = new()
            {
                Effort = _options.Effort ?? DefaultEffort,
                ErrorResilient = _av1.ErrorResilient ?? realtime,
                DeltaQuantizers = _av1.AdaptiveQuantization,
                ScreenContent = _av1.ScreenContent,
            };
            _coder = new Av1FrameCoder(sequence, plan, HdrObus(frame.Hdr), _hdr10Plus, _context.Egami);
            Track = MakeTrack(frame, depth, chroma, color, siting) with { CodecPrivate = Av1Config.FromObus(_coder.SequenceHeader.Span) ?? [] };
            bool full = depth != 8 || chroma != ChromaFormat.Yuv420;
            _core = new EncoderCore(_settings with { FullPictures = full }, _coder, _pictures, alpha: false);
        }

        if (frame.Hdr10Plus is { Raw.IsEmpty: false } dynamic)
        {
            _hdr10Plus[_sent] = dynamic.Raw;
        }

        _sent++;
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

    /// <summary>
    /// The smallest level (seq_level_idx) whose picture size, dimensions and display rate (AV1 Annex A.3) hold the stream, or 31
    /// (no limits) beyond level 6.3.
    /// </summary>
    internal static int Level(int width, int height, double fps)
    {
        ReadOnlySpan<(int Index, long PicSize, int MaxWidth, int MaxHeight, long DisplayRate)> levels =
        [
            (0, 147456, 2048, 1152, 4423680), (1, 278784, 2816, 1584, 8363520), (4, 665856, 4352, 2448, 19975680),
            (5, 1065024, 5504, 3096, 31950720), (8, 2359296, 6144, 3456, 70778880), (9, 2359296, 6144, 3456, 141557760),
            (12, 8912896, 8192, 4352, 267386880), (13, 8912896, 8192, 4352, 534773760), (14, 8912896, 8192, 4352, 1069547520),
            (16, 35651584, 16384, 8704, 1069547520), (17, 35651584, 16384, 8704, 2139095040), (18, 35651584, 16384, 8704, 4278190080),
        ];
        long size = (long)width * height;
        double rate = size * fps;
        foreach ((int index, long picSize, int maxWidth, int maxHeight, long displayRate) in levels)
        {
            if (size <= picSize && width <= maxWidth && height <= maxHeight && rate <= displayRate)
            {
                return index;
            }
        }

        return 31;
    }

    /// <summary>
    /// The metadata OBUs of static HDR metadata (AV1 section 5.8.3 and 5.8.4): content light levels and the mastering display,
    /// each with its size field, for every keyframe's temporal unit.
    /// </summary>
    internal static byte[] HdrObus(HdrMetadata? hdr)
    {
        if (hdr is null)
        {
            return [];
        }

        List<byte> obus = [];
        if (hdr.MaxContentLightLevel is not null || hdr.MaxFrameAverageLightLevel is not null)
        {
            byte[] body = new byte[4];
            Write16(body, 0, (int)Math.Clamp(Math.Round(hdr.MaxContentLightLevel ?? 0), 0, 65535));
            Write16(body, 2, (int)Math.Clamp(Math.Round(hdr.MaxFrameAverageLightLevel ?? 0), 0, 65535));
            MetadataObu(obus, 1, body);
        }

        if (hdr.MasteringPrimaries is { } p)
        {
            byte[] body = new byte[24];
            Chromaticity[] points = [p.Red, p.Green, p.Blue, p.White];
            for (int i = 0; i < 4; i++)
            {
                Write16(body, 4 * i, (int)Math.Clamp(Math.Round(points[i].X * 65536), 0, 65535));
                Write16(body, (4 * i) + 2, (int)Math.Clamp(Math.Round(points[i].Y * 65536), 0, 65535));
            }

            Write32(body, 16, (uint)Math.Clamp(Math.Round((hdr.MasteringMaxLuminance ?? 0) * 256), 0, uint.MaxValue));
            Write32(body, 20, (uint)Math.Clamp(Math.Round((hdr.MasteringMinLuminance ?? 0) * 16384), 0, uint.MaxValue));
            MetadataObu(obus, 2, body);
        }

        return [.. obus];

        static void Write16(byte[] b, int at, int v)
        {
            b[at] = (byte)(v >> 8);
            b[at + 1] = (byte)v;
        }

        static void Write32(byte[] b, int at, uint v)
        {
            Write16(b, at, (int)(v >> 16));
            Write16(b, at + 2, (int)(v & 0xFFFF));
        }
    }

    /// <summary>Appends a metadata OBU of a type and a byte-aligned body, with its size field and trailing bits.</summary>
    internal static void MetadataObu(List<byte> to, int type, ReadOnlySpan<byte> body)
    {
        Span<byte> leb = stackalloc byte[10];
        int typeLength = Av1Obus.WriteLeb128(leb, (ulong)type);
        int payload = typeLength + body.Length + 1;
        to.Add((byte)((Av1Obus.Metadata << 3) | 2));
        int sizeLength = Av1Obus.WriteLeb128(leb[typeLength..], (ulong)payload);
        to.AddRange(leb.Slice(typeLength, sizeLength));
        to.AddRange(leb[..typeLength]);
        to.AddRange(body);
        to.Add(0x80);
    }

    /// <summary>VP9's default color for AV1 too: BT.601 limited range, primaries and transfer unspecified.</summary>
    internal static readonly ColorCodePoints DefaultColor = Vp9.Vp9VideoEncoder.Vp9Color;

    private VideoTrackInfo MakeTrack(VideoFrame? first, int depth = 8, ChromaFormat chroma = ChromaFormat.Yuv420, ColorCodePoints? color = null,
        ChromaSiting? siting = null) => new()
        {
            Number = 1,
            Kind = TrackKind.Video,
            CodecId = CodecIds.Av1,
            TimeBase = _options.FrameRate.Reciprocal(),
            Width = _options.Width,
            Height = _options.Height,
            FrameRate = _options.FrameRate,
            BitDepth = depth,
            ChromaFormat = chroma,
            ChromaSiting = siting,
            Color = color ?? DefaultColor,
            Hdr = first?.Hdr,
        };

    /// <summary>
    /// The bit depth and chroma format to code: the options' depth, else the planes' (16-bit planes of 9 or 10 bits code 10,
    /// deeper ones 12); the planes' chroma format (4:4:0 and gray code 4:2:0), else the options' for RGB frames, else 4:2:0.
    /// </summary>
    private (int Depth, ChromaFormat Chroma) FormatOf(VideoFrame frame)
    {
        int depth = _av1.BitDepth ?? (frame.Planes is YuvRaster<ushort> words ? (words.BitDepth <= 10 ? 10 : 12) : 8);
        ChromaFormat chroma = frame.Planes?.ChromaFormat ?? _options.ChromaFormat ?? ChromaFormat.Yuv420;
        if (chroma is not (ChromaFormat.Yuv420 or ChromaFormat.Yuv422 or ChromaFormat.Yuv444))
        {
            chroma = ChromaFormat.Yuv420;
        }

        return (depth, chroma);
    }

    private static bool SameFormat(YuvRaster planes, int depth, ChromaFormat chroma) =>
        planes.ChromaFormat == chroma && (depth == 8) == (planes is YuvRaster<byte>) && (depth == 8 || planes.BitDepth == depth);

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

        if (frame.Planes is not { } planes || !SameFormat(planes, depth, chroma))
        {
            return DefaultColor;
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
