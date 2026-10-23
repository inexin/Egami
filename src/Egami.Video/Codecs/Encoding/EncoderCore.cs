using Egami.Pixels;

namespace Egami.Video.Codecs.Encoding;

/// <summary>How the core asks a codec to code one frame.</summary>
/// <param name="Type">The frame's place in its group.</param>
/// <param name="Quantizer">The codec's quantizer index.</param>
/// <param name="AltRefAhead">Whether the alternate reference is a frame still to be shown (its vectors point forward).</param>
/// <param name="RefreshGolden">Whether the frame becomes the golden reference.</param>
/// <param name="Index">
/// The frame's place in display order, counted from the first frame sent: a hidden frame's is the place it is shown at, and an
/// overlay's that of the hidden frame it shows.
/// </param>
internal readonly record struct FrameDecision(FrameType Type, int Quantizer, bool AltRefAhead, bool RefreshGolden, int Index = 0);

/// <summary>A frame a codec coded.</summary>
/// <param name="Data">The frame's bytes.</param>
/// <param name="Alpha">The alpha frame's bytes, for a shown frame of a stream with alpha.</param>
/// <param name="Quantizer">The quantizer the frame used.</param>
/// <param name="HiddenLength">
/// The bytes at the start of <paramref name="Data"/> that belong to hidden frames coded before this one (a VP9 superframe), which
/// rate control already counted.
/// </param>
/// <param name="Bits">
/// The frame's own bits when <paramref name="Data"/> does not hold them: a hidden frame a coder keeps for the next shown frame's
/// packet (AV1); null to count <paramref name="Data"/>.
/// </param>
internal readonly record struct CodedFrame(ReadOnlyMemory<byte> Data, ReadOnlyMemory<byte>? Alpha, int Quantizer, int HiddenLength = 0, long? Bits = null);

/// <summary>A codec's frame encoder under the core: VP8's wraps Egami's Vp8StreamEncoder.</summary>
internal interface IFrameCoder : IDisposable
{
    /// <summary>Gets the step size of each quantizer index, for the bit model.</summary>
    short[] Steps { get; }

    /// <summary>
    /// Gets whether the coder keeps a hidden frame and puts it in the next shown frame's packet (VP9 superframes), so a hidden
    /// frame makes no packet of its own.
    /// </summary>
    bool MergesHiddenFrames => false;

    /// <summary>Codes a frame; a shown frame of a stream with alpha also codes the picture's alpha plane.</summary>
    CodedFrame Code(YuvRaster<byte> picture, FrameDecision decision);

    /// <summary>
    /// Codes a frame from its picture in the stream's own format (another bit depth or chroma format), when the core keeps one
    /// (<see cref="CoreSettings.FullPictures"/>); <paramref name="picture"/> is the 8-bit 4:2:0 picture the core analyzes,
    /// which still carries the alpha plane. A hidden alternate reference gets its frame's full picture, unfiltered.
    /// </summary>
    CodedFrame Code(YuvRaster<byte> picture, YuvRaster? full, FrameDecision decision) => Code(picture, decision);

    /// <summary>
    /// Codes a keyframe and, when given, the frame after it at one quantizer on the side, without touching the stream, and
    /// returns their sizes in bits: rate control learns from them what this content costs before it plans the first frame.
    /// </summary>
    (long Key, long Next) Probe(YuvRaster<byte> key, YuvRaster<byte>? next, int quantizer);
}

/// <summary>The settings the core plans with, from <see cref="VideoEncoderOptions"/> and the codec's own options.</summary>
internal sealed record CoreSettings
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    public required Rational FrameRate { get; init; }

    public LatencyMode LatencyMode { get; init; }

    public required int KeyframeInterval { get; init; }

    public bool SceneCuts { get; init; } = true;

    public bool AltRef { get; init; }

    public int? GoldenInterval { get; init; }

    public int FilterFrames { get; init; } = 7;

    public int FilterStrength { get; init; } = 5;

    public int Parallelism { get; init; } = 1;

    /// <summary>How much better than intra the group's first frame must predict its last for an alternate reference (VP8: 4 times).</summary>
    public double AltRefReach { get; init; } = 4;

    /// <summary>The alternate reference's step against an inter frame's on still groups, and how much coarser on busy ones.</summary>
    public double AltRefStep { get; init; } = 0.85;

    public double AltRefStepRange { get; init; } = 0.10;

    /// <summary>The golden frame's step against an inter frame's on still groups, and how much coarser on busy ones.</summary>
    public double GoldenStep { get; init; } = 0.80;

    public double GoldenStepRange { get; init; } = 0.15;

    public required RateSettings Rate { get; init; }

    /// <summary>Whether each frame is also kept in the stream's own format (<see cref="EncoderPictures.ImportFull"/>) for the coder.</summary>
    public bool FullPictures { get; init; }

    /// <summary>
    /// Whether an overlay costs next to nothing because the coder shows the hidden frame again (AV1's show_existing_frame), so
    /// rate control plans no bits for it.
    /// </summary>
    public bool FreeOverlays { get; init; }

    /// <summary>
    /// Whether a group with an alternate reference also codes a hidden frame halfway to it, shown later as an overlay of its own
    /// (a two-level pyramid, AV1's ALTREF2). Needs a coder that keeps several hidden frames for one packet.
    /// </summary>
    public bool Pyramid { get; init; }
}

/// <summary>
/// The encoder core every codec shares: a lookahead that analyzes each frame as it arrives and places keyframes (scene cuts,
/// the interval, the caller's flag), groups of frames between golden frames with a hidden alternate reference at their end in
/// <see cref="LatencyMode.Quality"/>, rate control per frame, and packets out in decode order with presentation timestamps.
/// A hidden frame's packet carries the timestamp of the shown frame after it. Every decision depends only on the frames and
/// options, so the output is the same at any thread count (Design Q9).
/// </summary>
internal sealed class EncoderCore : IDisposable
{
    /// <summary>The frames <see cref="LatencyMode.Quality"/> looks ahead (Design Q4, libvpx's lag-in-frames).</summary>
    public const int Lookahead = 25;

    private readonly CoreSettings _settings;
    private readonly IFrameCoder _coder;
    private readonly EncoderPictures _pictures;
    private readonly FrameAnalyzer _analyzer;
    private readonly RateController _rate;
    private readonly List<Pending> _queue = [];
    private readonly Stack<byte[]> _halves = new();
    private readonly Queue<EncodedPacket> _ready = new();
    private readonly List<YuvRaster<byte>> _filterFrames = [];
    private readonly GroupFrame[] _group = new GroupFrame[80];
    private readonly Rational _timeBase;
    private readonly int _depth;
    private readonly bool _alpha;
    private int _sent;
    private int _lastKey = int.MinValue / 2;
    private double _previousRatio = 1;
    private int _groupStart = -1;
    private int _groupEnd = -1;
    private bool _refreshGoldenAtEnd;
    private int _altRef = -1;
    private bool _altRefCoded;
    private int _altRef2 = -1;
    private bool _altRef2Coded;
    private bool _flushing;
    private double _altRefFactor = 0.7;
    private double _goldenFactor = 0.8;

    public EncoderCore(CoreSettings settings, IFrameCoder coder, EncoderPictures pictures, bool alpha)
    {
        _settings = settings;
        _coder = coder;
        _pictures = pictures;
        _alpha = alpha;
        _analyzer = new FrameAnalyzer(settings.Width, settings.Height);
        _rate = new RateController(settings.Rate);
        _timeBase = settings.FrameRate.Reciprocal();
        _depth = settings.LatencyMode == LatencyMode.Quality ? Lookahead : 0;
    }

    /// <summary>Gets or sets where each coded frame's type, quantizer and size go, for tuning.</summary>
    internal static Action<string>? Trace { get; set; }

    /// <summary>Gets the rate controller, for tests of the buffer model.</summary>
    internal RateController Rate => _rate;

    /// <summary>Takes a frame into the lookahead and codes what the lookahead lets go.</summary>
    public void Send(VideoFrame frame, bool forceKeyframe)
    {
        if (_flushing)
        {
            throw new InvalidOperationException("The encoder was flushed; no more frames can be sent.");
        }

        YuvRaster<byte> picture = _pictures.Import(frame, _alpha);
        YuvRaster? full = _settings.FullPictures ? _pictures.ImportFull(frame) : null;
        FrameStats stats = _analyzer.Analyze(picture);
        byte[] half = _halves.TryPop(out byte[]? free) ? free : new byte[_analyzer.HalfLength];
        _analyzer.CopyHalf(half);
        int index = _sent++;
        double ratio = stats.InterRatio;
        bool cut = _settings.SceneCuts && index > 0 && ratio > 0.7 && ratio > (_previousRatio * 2) + 0.05;
        bool key = index == 0 || forceKeyframe || cut || index - _lastKey >= _settings.KeyframeInterval;
        if (key)
        {
            _lastKey = index;
        }

        _previousRatio = ratio;
        MediaTime timestamp = frame.Timestamp.Rescale(_timeBase);
        MediaTime duration = frame.Duration is { } d ? d.Rescale(_timeBase) : new MediaTime(1, _timeBase);
        _queue.Add(new Pending(picture, half, index, timestamp, duration, key, stats) { Full = full });
        while (_queue.Count > _depth)
        {
            CodeNext();
        }
    }

    /// <summary>Codes every frame still held.</summary>
    public void Flush()
    {
        _flushing = true;
        while (_queue.Count > 0)
        {
            CodeNext();
        }
    }

    public bool TryReceive(out EncodedPacket? packet) => _ready.TryDequeue(out packet);

    private void CodeNext()
    {
        Pending frame = _queue[0];
        if (frame.Index == 0 && _rate.WantsProbe)
        {
            Pending? second = _queue.Count > 1 ? _queue[1] : null;
            (long key, long next) = _coder.Probe(frame.Picture, second?.Picture, RateController.ProbeQuantizer);
            _rate.Calibrate(frame.Stats.Intra, key, second?.Stats.Inter ?? 0, next);
        }

        if (frame.Index > _groupEnd || (frame.Key && frame.Index != _groupStart))
        {
            PlanGroup(frame);
        }

        // The alternate reference goes out before the first shown frame that predicts from it, after the group's keyframe, and
        // the pyramid's middle frame after it.
        if (_altRef >= 0 && !_altRefCoded && !frame.Key)
        {
            CodeAltRef(frame, _altRef, _altRefFactor);
            _altRefCoded = true;
            return;
        }

        if (_altRef2 >= 0 && !_altRef2Coded && !frame.Key)
        {
            CodeAltRef(frame, _altRef2, AltRef2Factor);
            _altRef2Coded = true;
            return;
        }

        FrameType type = frame.Key ? FrameType.Key
            : frame.Index == _altRef2 ? FrameType.Overlay
            : frame.Index == _groupEnd ? (_altRef == frame.Index ? FrameType.Overlay : _refreshGoldenAtEnd ? FrameType.Golden : FrameType.Inter)
            : FrameType.Inter;
        long complexity = type switch
        {
            FrameType.Key => frame.Stats.Intra,
            FrameType.Overlay => OverlayComplexity(frame.Stats),
            _ => frame.Stats.Inter,
        };
        int quantizer = _rate.Next(type, complexity, Factor(type));
        bool refreshGolden = frame.Index == _groupEnd && _refreshGoldenAtEnd;
        bool ahead = _altRef > frame.Index && _altRefCoded;
        FrameDecision decision = new(type, quantizer, ahead, refreshGolden, frame.Index);
        CodedFrame coded = frame.Full is null ? _coder.Code(frame.Picture, decision) : _coder.Code(frame.Picture, frame.Full, decision);
        _rate.Done(type, coded.Quantizer, coded.Bits ?? ((coded.Data.Length - coded.HiddenLength) * 8L), complexity);
        Trace?.Invoke($"{frame.Index} {type} q{coded.Quantizer} {coded.Data.Length - coded.HiddenLength}B c{complexity}");
        EncodedPacket packet = Packet(coded.Data, frame.Timestamp, frame.Key);
        packet.Duration = frame.Duration;
        if (coded.Alpha is { } alpha)
        {
            packet.Additions = [new PacketAddition(1, alpha)];
        }

        _ready.Enqueue(packet);
        _queue.RemoveAt(0);
        _pictures.Return(frame.Picture);
        if (frame.Full is not null)
        {
            _pictures.ReturnFull(frame.Full);
        }

        _halves.Push(frame.Half);
    }

    private void PlanGroup(Pending first)
    {
        int start = first.Index;
        int last = _queue[^1].Index;
        int nextKey = -1;
        for (int i = 1; i < _queue.Count; i++)
        {
            if (_queue[i].Key)
            {
                nextKey = _queue[i].Index;
                break;
            }
        }

        int length = _settings.GoldenInterval ?? AdaptiveInterval();
        bool wantAltRef = _settings.AltRef && _depth > 0;
        (double motion, double ratio) = Motion();
        if (wantAltRef && _settings.GoldenInterval is null && motion > 0.5)
        {
            // An alternate reference only pays when the frames before it can reach it with short vectors: about 24 pixels.
            int reach = (int)(24 / motion);
            if (reach < 6)
            {
                wantAltRef = false;
            }
            else
            {
                length = Math.Min(length, reach);
            }
        }

        if (wantAltRef)
        {
            // Nor when the group's first frame predicts its last badly (zooms, fades, deformation): the alternate reference
            // stands in for frames the first can no longer reach. Noise does not count against it, as the filter removes it.
            int reach = Math.Min(Math.Min(_queue.Count, length), _depth - (_settings.FilterFrames / 2) - 1) - 1;
            while (reach >= 5 && (reach >= _queue.Count || !Reachable(_queue[0], _queue[reach])))
            {
                reach -= Math.Max(1, reach / 4);
            }

            if (reach < 5)
            {
                wantAltRef = false;
            }
            else
            {
                length = reach + 1;
            }
        }

        // Still, well-predicted groups give their reference frames finer steps (libvpx's boost is the idea).
        double boost = Math.Clamp(ratio, 0, 0.4) / 0.4;
        _altRefFactor = _settings.AltRefStep + (_settings.AltRefStepRange * boost);
        _goldenFactor = _settings.GoldenStep + (_settings.GoldenStepRange * boost);
        if (_depth > 0)
        {
            // The group's end and the frames the filter takes after it must be in the lookahead.
            length = Math.Min(length, _depth - (_settings.FilterFrames / 2) - 1);
        }

        int end = start + Math.Max(1, length) - 1;
        bool beforeKey = false;
        if (nextKey >= 0 && end >= nextKey - 1)
        {
            end = nextKey - 1;
            beforeKey = true;
        }

        if (_depth > 0 && end >= last)
        {
            end = last;
            beforeKey |= _flushing;
        }

        _groupStart = start;
        _groupEnd = end;
        _refreshGoldenAtEnd = !beforeKey && end > start;
        bool altRef = wantAltRef && end - start + 1 >= 4;
        _altRef = altRef ? end : -1;
        _altRefCoded = false;

        // The pyramid's middle frame halves the distance the frames before the alternate reference predict across.
        int firstShown = first.Key ? start + 1 : start;
        _altRef2 = altRef && _settings.Pyramid && end - firstShown >= 6 ? (firstShown + end) / 2 : -1;
        _altRef2Coded = false;

        // The group's frames in coding order, for its bit budget.
        int n = 0;
        int count = Math.Min(end - start + 1, _group.Length - 1);
        for (int i = 0; i < count; i++)
        {
            int index = start + i;
            FrameStats stats = i < _queue.Count ? _queue[i].Stats : _queue[^1].Stats;
            if (i == 0 && first.Key)
            {
                _group[n++] = new GroupFrame(FrameType.Key, stats.Intra);
                if (altRef)
                {
                    AddHidden(ref n);
                }

                continue;
            }

            if (i == 0 && altRef)
            {
                AddHidden(ref n);
            }

            FrameType type = index == end ? (altRef ? FrameType.Overlay : _refreshGoldenAtEnd ? FrameType.Golden : FrameType.Inter)
                : index == _altRef2 ? FrameType.Overlay : FrameType.Inter;
            _group[n++] = new GroupFrame(type, type == FrameType.Overlay ? OverlayComplexity(stats) : stats.Inter, Factor(type));
        }

        _rate.StartGroup(_group.AsSpan(0, n));
    }

    /// <summary>Adds the group's hidden frames to its plan, in coding order.</summary>
    private void AddHidden(ref int n)
    {
        _group[n++] = new GroupFrame(FrameType.AltRef, AltRefComplexity(_altRef), _altRefFactor);
        if (_altRef2 >= 0)
        {
            _group[n++] = new GroupFrame(FrameType.AltRef, AltRefComplexity(_altRef2), AltRef2Factor);
        }
    }

    /// <summary>The pyramid's middle frame's step: between the alternate reference's and an inter frame's.</summary>
    private double AltRef2Factor => Math.Sqrt(_altRefFactor);

    /// <summary>An overlay's complexity: a quarter of its frame's, or none when the coder shows the hidden frame again.</summary>
    private long OverlayComplexity(FrameStats stats) => _settings.FreeOverlays ? 0 : stats.Inter / 4;

    /// <summary>Whether <paramref name="to"/>'s coarse picture predicts from <paramref name="from"/>'s with at most a quarter of its intra error.</summary>
    private bool Reachable(Pending from, Pending to)
    {
        (long inter, long intra) = _analyzer.Distance(from.Half, to.Half);
        return inter * _settings.AltRefReach <= intra;
    }

    /// <summary>A frame type's step against an inter frame's in this group, or 0 for rate control's own.</summary>
    private double Factor(FrameType type) => type switch
    {
        FrameType.AltRef => _altRefFactor,
        FrameType.Golden => _goldenFactor,
        _ => 0,
    };

    /// <summary>The average vector length in pixels per frame, and how much of each frame the one before does not explain, over the next 16 frames.</summary>
    private (double Motion, double Ratio) Motion()
    {
        long motion = 0, intra = 0, inter = 0;
        int count = 0;
        for (int i = 1; i < Math.Min(_queue.Count, 17); i++)
        {
            motion += _queue[i].Stats.Motion;
            intra += _queue[i].Stats.Intra - _queue[i].Stats.Detail;
            inter += _queue[i].Stats.Inter - _queue[i].Stats.Detail;
            count++;
        }

        return count == 0 ? (0, 0) : (motion / 16.0 / count, intra == 0 ? 0 : (double)inter / intra);
    }

    /// <summary>8 to 16 frames by how well each frame predicts from the one before over the lookahead: still pictures get longer groups.</summary>
    private int AdaptiveInterval()
    {
        if (_queue.Count < 2)
        {
            return 16;
        }

        long intra = 0, inter = 0;
        for (int i = 1; i < Math.Min(_queue.Count, 17); i++)
        {
            intra += _queue[i].Stats.Intra - _queue[i].Stats.Detail;
            inter += _queue[i].Stats.Inter - _queue[i].Stats.Detail;
        }

        double ratio = intra == 0 ? 0 : (double)inter / intra;
        return ratio < 0.15 ? 16 : ratio < 0.35 ? 12 : 8;
    }

    /// <summary>A guess at a hidden frame's cost: what the frames up to it change, but no more than coding it alone.</summary>
    private long AltRefComplexity(int target)
    {
        long sum = 0, intra = 0;
        int from = target == _altRef2 ? _groupStart : _altRef2 >= 0 ? _altRef2 : _groupStart;
        foreach (Pending p in _queue)
        {
            if (p.Index > from && p.Index <= target)
            {
                sum += p.Stats.Inter;
            }

            if (p.Index == target)
            {
                intra = p.Stats.Intra;
            }
        }

        return Math.Max(1, Math.Min(intra, sum));
    }

    private void CodeAltRef(Pending next, int target, double factor)
    {
        int radius = _settings.FilterFrames / 2;
        _filterFrames.Clear();
        int center = 0;
        YuvRaster? full = null;
        foreach (Pending p in _queue)
        {
            if (p.Index == target)
            {
                full = p.Full;
            }

            if (p.Index >= target - radius && p.Index <= target + radius && !(p.Key && p.Index > target))
            {
                if (p.Index == target)
                {
                    center = _filterFrames.Count;
                }

                _filterFrames.Add(p.Picture);
            }
        }

        YuvRaster<byte> filtered = _pictures.Rent(false);
        TemporalFilter.Apply(_filterFrames, center, _settings.FilterStrength, filtered, _settings.Parallelism);
        long complexity = AltRefComplexity(target);
        int quantizer = _rate.Next(FrameType.AltRef, complexity, factor);
        FrameDecision decision = new(FrameType.AltRef, quantizer, AltRefAhead: true, RefreshGolden: false, target);
        CodedFrame coded = full is null ? _coder.Code(filtered, decision) : _coder.Code(filtered, full, decision);
        long bits = coded.Bits ?? (coded.Data.Length * 8L);
        _rate.Done(FrameType.AltRef, coded.Quantizer, bits, complexity);
        Trace?.Invoke($"{target} AltRef q{coded.Quantizer} {bits / 8}B c{complexity}");
        _pictures.Return(filtered);
        if (!_coder.MergesHiddenFrames)
        {
            _ready.Enqueue(Packet(coded.Data, next.Timestamp, key: false));
        }
    }

    private static EncodedPacket Packet(ReadOnlyMemory<byte> data, MediaTime timestamp, bool key)
    {
        EncodedPacket packet = EncodedPacket.Rent(1, data.Length, timestamp);
        data.Span.CopyTo(packet.GetWritableData().Span);
        packet.IsKeyframe = key;
        return packet;
    }

    public void Dispose()
    {
        foreach (Pending p in _queue)
        {
            p.Picture.Dispose();
            p.Full?.Dispose();
        }

        _queue.Clear();
        while (_ready.TryDequeue(out EncodedPacket? packet))
        {
            packet.Dispose();
        }
    }

    private sealed record Pending(YuvRaster<byte> Picture, byte[] Half, int Index, MediaTime Timestamp, MediaTime Duration, bool Key, FrameStats Stats)
    {
        /// <summary>Gets the frame in the stream's own format, when the core keeps it.</summary>
        public YuvRaster? Full { get; init; }
    }
}
