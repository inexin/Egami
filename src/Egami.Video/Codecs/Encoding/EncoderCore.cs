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
/// <param name="Layer">The frame's layer in its group's pyramid: 0 for keyframes and the group's alternate reference, more for each level below.</param>
/// <param name="Referenced">Whether later frames predict from the frame; false for a pyramid's leaves.</param>
internal readonly record struct FrameDecision(FrameType Type, int Quantizer, bool AltRefAhead, bool RefreshGolden, int Index = 0, int Layer = 0, bool Referenced = true);

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
    /// Runs the coder's temporal model over frames ahead in coding order, each naming the earlier ones (by their place in the list)
    /// it will predict from, and keeps what it finds for when they are coded; returns each frame's importance (1 when nothing
    /// predicts from it, more the more later frames reuse it), or null when the coder has no model.
    /// </summary>
    double[]? Analyze(IReadOnlyList<AnalysisFrame> frames) => null;

    /// <summary>
    /// Codes a keyframe and, when given, the frame after it at one quantizer on the side, without touching the stream, and
    /// returns their sizes in bits: rate control learns from them what this content costs before it plans the first frame.
    /// </summary>
    (long Key, long Next) Probe(YuvRaster<byte> key, YuvRaster<byte>? next, int quantizer);
}

/// <summary>A frame for <see cref="IFrameCoder.Analyze"/>: its place in display order, its pictures, and the earlier frames of the list it predicts from.</summary>
internal readonly record struct AnalysisFrame(int Index, YuvRaster<byte> Picture, YuvRaster? Full, int[] References);

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
    /// Whether a group with an alternate reference is coded as a pyramid: after the alternate reference at its end, a hidden frame
    /// halfway to it, then halfway to that, down to pairs of frames, each shown later as an overlay of its own, and the frames
    /// between them (the leaves) predicted from both sides and from nothing later (libaom's and SVT-AV1's hierarchical groups are
    /// the idea). Needs a coder that keeps several hidden frames for one packet and shows them for free (AV1).
    /// </summary>
    public bool Pyramid { get; init; }

    /// <summary>The shown frames of a pyramid's group, its alternate reference included: 16 makes five layers.</summary>
    public int PyramidGroup { get; init; } = 16;

    /// <summary>Whether a pyramid's leaves are references for later frames too (they are not by default).</summary>
    public bool ReferencedLeaves { get; init; }

    /// <summary>A pyramid leaf's step against an inter frame's in constant quality.</summary>
    public double LeafStep { get; init; } = 1.0;

    /// <summary>
    /// Whether a pyramid's group and the one after it are run through the coder's temporal model (<see cref="IFrameCoder.Analyze"/>)
    /// before the group is coded, its alternate reference's and keyframe's steps then following their importance.
    /// </summary>
    public bool TemporalModel { get; init; }

    /// <summary>The frames <see cref="LatencyMode.Quality"/> looks ahead.</summary>
    public int Lookahead { get; init; } = EncoderCore.Lookahead;

    /// <summary>Whether keyframes are coded temporally filtered with the frames after them, as alternate references are.</summary>
    public bool FilterKeyframes { get; init; }

    /// <summary>Whether the temporal filter's strength follows each picture's noise and the frame's quantizer rather than <see cref="FilterStrength"/>.</summary>
    public bool AdaptiveFilter { get; init; }

    /// <summary>How many noise deviations apart two samples may be before the adaptive filter stops blending them, at the reference quantizer.</summary>
    public double FilterReach { get; init; } = 3;

    /// <summary>The quantizer at whose step the adaptive filter blends as <see cref="FilterReach"/> says; finer steps blend less, coarser more.</summary>
    public int FilterReferenceQuantizer { get; init; } = 96;

    /// <summary>The temporal model's alternate reference step against a leaf's at an importance of 1, before its square root divides it.</summary>
    public double ImportanceStep { get; init; } = 1.0;

    /// <summary>The temporal model's keyframe step against a leaf's at an importance of 1.</summary>
    public double KeyImportanceStep { get; init; } = 0.9;

    /// <summary>The finest step the temporal model gives an alternate reference or keyframe, against a leaf's.</summary>
    public double ImportanceStepMin { get; init; } = 0.25;

    /// <summary>The coarsest step the temporal model gives an alternate reference or keyframe, against a leaf's.</summary>
    public double ImportanceStepMax { get; init; } = 0.8;
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
    private readonly List<Step> _steps = [];
    private int _stepNext;
    private bool _flushing;
    private double _altRefFactor = 0.7;
    private double _keyFactor;
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
        _depth = settings.LatencyMode == LatencyMode.Quality ? settings.Lookahead : 0;
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

        FrameType type = frame.Key ? FrameType.Key
            : frame.Index == _groupEnd ? (_altRef == frame.Index ? FrameType.Overlay : _refreshGoldenAtEnd ? FrameType.Golden : FrameType.Inter)
            : FrameType.Inter;
        int layer = 0;
        bool referenced = true;
        if (_stepNext < _steps.Count)
        {
            // The pyramid's next frame in coding order: a hidden frame of the group, or the frame at the head of the queue.
            Step step = _steps[_stepNext++];
            if (step.Kind == StepKind.Hidden)
            {
                CodeAltRef(frame, step.Index, LayerFactor(step.Layer), step.Layer, step.From);
                return;
            }

            if (step.Index != frame.Index)
            {
                throw new InvalidOperationException($"The pyramid shows frame {step.Index} where frame {frame.Index} is next.");
            }

            (layer, referenced) = (step.Layer, step.Kind != StepKind.Leaf || _settings.ReferencedLeaves || frame.Key);
            type = step.Kind == StepKind.Shown ? FrameType.Overlay : type;
        }

        long complexity = type switch
        {
            FrameType.Key => frame.Stats.Intra,
            FrameType.Overlay => OverlayComplexity(frame.Stats),
            _ => frame.Stats.Inter,
        };
        int quantizer = _rate.Next(type, complexity, layer > 0 && type == FrameType.Inter ? LeafFactor : type == FrameType.Key ? _keyFactor : Factor(type));
        bool refreshGolden = frame.Index == _groupEnd && _refreshGoldenAtEnd;
        bool ahead = _altRef > frame.Index && _altRefCoded;
        FrameDecision decision = new(type, quantizer, ahead, refreshGolden, frame.Index, layer, referenced);
        CodedFrame coded;
        if (type == FrameType.Key && _settings.FilterKeyframes && frame.Full is null && _depth > 0)
        {
            // The keyframe every frame of its group predicts from, blended with the frames after it as an alternate reference is.
            _filterFrames.Clear();
            foreach (Pending p in _queue)
            {
                if (p.Index >= frame.Index && p.Index <= frame.Index + (_settings.FilterFrames / 2) && !(p.Key && p.Index > frame.Index))
                {
                    _filterFrames.Add(p.Picture);
                }
            }

            YuvRaster<byte> filtered = _pictures.Rent(false);
            Filter(0, quantizer, filtered);
            coded = _coder.Code(filtered, decision);
            _pictures.Return(filtered);
        }
        else
        {
            coded = frame.Full is null ? _coder.Code(frame.Picture, decision) : _coder.Code(frame.Picture, frame.Full, decision);
        }
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

        // A pyramid's group is its shown frames after the keyframe; other groups adapt to the motion.
        bool pyramid = _settings.Pyramid && _settings.AltRef && _depth > 0;
        int length = _settings.GoldenInterval ?? (pyramid ? _settings.PyramidGroup + (first.Key ? 1 : 0) : AdaptiveInterval());
        bool wantAltRef = _settings.AltRef && _depth > 0;
        (double motion, double ratio) = Motion();
        if (wantAltRef && !pyramid && _settings.GoldenInterval is null && motion > 0.5)
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

        if (wantAltRef && !pyramid)
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

        // A pyramid: the alternate reference, then hidden frames halving the distances down to pairs, in coding order.
        int firstShown = first.Key ? start + 1 : start;
        _steps.Clear();
        _stepNext = 0;
        if (altRef && _settings.Pyramid)
        {
            if (first.Key)
            {
                _steps.Add(new Step(start, StepKind.Leaf, 0, -1, -1));
            }

            _steps.Add(new Step(end, StepKind.Hidden, 0, firstShown - 1, -1));
            AddPyramid(_steps, firstShown - 1, end, 1);
            _altRefCoded = true;
        }

        _keyFactor = 0;
        if (_steps.Count > 0 && _settings.TemporalModel)
        {
            AnalyzeGroup(start, end, first.Key, nextKey);
        }

        // The group's frames in coding order, for its bit budget.
        int n = 0;
        if (_steps.Count > 0)
        {
            foreach (Step step in _steps)
            {
                FrameStats stats = Stats(step.Index);
                _group[n++] = step.Kind switch
                {
                    StepKind.Hidden => new GroupFrame(FrameType.AltRef, HiddenComplexity(step.From, step.Index), LayerFactor(step.Layer)),
                    StepKind.Shown => new GroupFrame(FrameType.Overlay, OverlayComplexity(stats)),
                    _ when step.Index == start && first.Key => new GroupFrame(FrameType.Key, stats.Intra, _keyFactor),
                    _ => new GroupFrame(FrameType.Inter, stats.Inter, LeafFactor),
                };
            }

            _rate.StartGroup(_group.AsSpan(0, n));
            return;
        }

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

            FrameType type = index == end ? (altRef ? FrameType.Overlay : _refreshGoldenAtEnd ? FrameType.Golden : FrameType.Inter) : FrameType.Inter;
            _group[n++] = new GroupFrame(type, type == FrameType.Overlay ? OverlayComplexity(stats) : stats.Inter, Factor(type));
        }

        _rate.StartGroup(_group.AsSpan(0, n));
    }

    /// <summary>Adds the group's hidden frames to its plan, in coding order.</summary>
    private void AddHidden(ref int n) => _group[n++] = new GroupFrame(FrameType.AltRef, AltRefComplexity(_altRef), _altRefFactor);

    /// <summary>
    /// Adds the pyramid's frames after <paramref name="from"/> (shown) up to <paramref name="to"/> (hidden, coded) in coding
    /// order: a hidden frame halfway, the frames up to it, and those after it; a lone frame between is a leaf.
    /// </summary>
    private static void AddPyramid(List<Step> steps, int from, int to, int layer)
    {
        if (to - from > 2)
        {
            int middle = (from + to) / 2;
            steps.Add(new Step(middle, StepKind.Hidden, layer, from, to));
            AddPyramid(steps, from, middle, layer + 1);
            AddPyramid(steps, middle, to, layer + 1);
            return;
        }

        if (to - from == 2)
        {
            steps.Add(new Step(from + 1, StepKind.Leaf, layer, from, to));
        }

        steps.Add(new Step(to, StepKind.Shown, layer, from, -1));
    }

    /// <summary>
    /// Runs the temporal model over the group from <paramref name="start"/> to <paramref name="end"/> and the group after it, in
    /// coding order, each frame predicting from the frames it sits between (SVT-AV1 looks one group ahead the same way); the
    /// alternate reference's step, and a keyframe's, then fall with the square root of their importance.
    /// </summary>
    private void AnalyzeGroup(int start, int end, bool key, int nextKey)
    {
        List<Step> order = [.. _steps];
        int last = _queue[^1].Index;
        int limit = nextKey >= 0 ? Math.Min(last, nextKey - 1) : last;
        int nextEnd = Math.Min(end + _settings.PyramidGroup, limit);
        if (nextEnd - end >= 4)
        {
            order.Add(new Step(nextEnd, StepKind.Hidden, 0, end, -1));
            AddPyramid(order, end, nextEnd, 1);
        }

        List<AnalysisFrame> frames = [];
        Dictionary<int, int> place = [];
        foreach (Step step in order)
        {
            if (step.Kind == StepKind.Shown || Find(step.Index) is not Pending p)
            {
                continue;
            }

            List<int> refs = [];
            foreach (int r in (ReadOnlySpan<int>)[step.From, step.To])
            {
                if (r >= 0 && r != step.Index && place.TryGetValue(r, out int at) && !refs.Contains(at))
                {
                    refs.Add(at);
                }
            }

            place[step.Index] = frames.Count;
            frames.Add(new AnalysisFrame(step.Index, p.Picture, p.Full, [.. refs]));
        }

        if (_coder.Analyze(frames) is not { } importance)
        {
            return;
        }

        if (place.TryGetValue(end, out int arf))
        {
            _altRefFactor = Math.Clamp(_settings.ImportanceStep / Math.Sqrt(importance[arf]), _settings.ImportanceStepMin, _settings.ImportanceStepMax);
        }

        if (key && place.TryGetValue(start, out int k))
        {
            _keyFactor = Math.Clamp(_settings.KeyImportanceStep / Math.Sqrt(importance[k]), _settings.ImportanceStepMin, _settings.ImportanceStepMax);
        }
    }

    private Pending? Find(int index)
    {
        foreach (Pending p in _queue)
        {
            if (p.Index == index)
            {
                return p;
            }
        }

        return null;
    }

    /// <summary>
    /// A pyramid layer's step against an inter frame's: the alternate reference's at layer 0, each layer below halving the
    /// distance to the leaves' (libaom's levels move halfway to the leaf quantizer in the same way).
    /// </summary>
    private double LayerFactor(int layer)
    {
        double factor = _altRefFactor;
        for (int i = 0; i < layer; i++)
        {
            factor = Math.Sqrt(factor * LeafFactor);
        }

        return factor;
    }

    /// <summary>A pyramid leaf's step against an inter frame's.</summary>
    private double LeafFactor => _settings.LeafStep;

    /// <summary>The statistics of the frame at a place in the lookahead, or the last one held.</summary>
    private FrameStats Stats(int index)
    {
        foreach (Pending p in _queue)
        {
            if (p.Index == index)
            {
                return p.Stats;
            }
        }

        return _queue[^1].Stats;
    }

    /// <summary>A guess at a pyramid's hidden frame's cost: what the frames from its anchor change, but no more than coding it alone.</summary>
    private long HiddenComplexity(int from, int target)
    {
        long sum = 0, intra = 0;
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
        int from = _groupStart;
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

    private void CodeAltRef(Pending next, int target, double factor, int layer = 0, int from = -1)
    {
        // A pyramid's hidden frames below its alternate reference are coded unfiltered, as libaom's are.
        int radius = layer == 0 ? _settings.FilterFrames / 2 : 0;
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

        long complexity = from >= 0 ? HiddenComplexity(from, target) : AltRefComplexity(target);
        int quantizer = _rate.Next(FrameType.AltRef, complexity, factor);
        YuvRaster<byte> filtered = _pictures.Rent(false);
        Filter(center, quantizer, filtered);
        FrameDecision decision = new(FrameType.AltRef, quantizer, AltRefAhead: true, RefreshGolden: false, target, layer);
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

    /// <summary>
    /// Filters <see cref="_filterFrames"/>[<paramref name="center"/>] into <paramref name="output"/>: at the settings' strength, or,
    /// adaptive, with a decay that follows the picture's noise and the frame's quantizer step, so clean pictures and fine
    /// quantizers are left nearly as they are and noisy ones at coarse quantizers are smoothed most (libaom's and SVT-AV1's
    /// noise-adaptive filters are the idea).
    /// </summary>
    private void Filter(int center, int quantizer, YuvRaster<byte> output)
    {
        if (!_settings.AdaptiveFilter || _filterFrames.Count == 1)
        {
            TemporalFilter.Apply(_filterFrames, center, _settings.FilterStrength, output, _settings.Parallelism);
            return;
        }

        double sigma = TemporalFilter.Noise(_filterFrames[center].Y);
        short[] steps = _settings.Rate.Steps;
        double q = Math.Clamp((double)steps[quantizer] / steps[Math.Clamp(_settings.FilterReferenceQuantizer, 0, steps.Length - 1)], 0.25, 2.5);
        double reach = _settings.FilterReach * sigma;
        int decay = (int)Math.Clamp(48 * reach * reach * q, 16, 256 << 6);
        int match = (int)Math.Clamp(Math.Round(1.5 * sigma), 2, 8);
        Trace?.Invoke($"filter noise {sigma:F2} q{quantizer} decay {decay} match {match}");
        TemporalFilter.Apply(_filterFrames, center, decay, match, output, _settings.Parallelism);
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

    private enum StepKind
    {
        /// <summary>A shown frame coded in display order: a keyframe or a leaf.</summary>
        Leaf,

        /// <summary>A hidden frame coded ahead of its place.</summary>
        Hidden,

        /// <summary>A hidden frame shown at its place.</summary>
        Shown,
    }

    /// <summary>
    /// One frame of a pyramid's coding order, its layer, and the frames it sits between: the shown frame before it that its part
    /// of the group starts from, and the hidden frame after it (−1 for none).
    /// </summary>
    private readonly record struct Step(int Index, StepKind Kind, int Layer, int From, int To);

    private sealed record Pending(YuvRaster<byte> Picture, byte[] Half, int Index, MediaTime Timestamp, MediaTime Duration, bool Key, FrameStats Stats)
    {
        /// <summary>Gets the frame in the stream's own format, when the core keeps it.</summary>
        public YuvRaster? Full { get; init; }
    }
}
