namespace Egami.Video.Codecs.Encoding;

/// <summary>What a frame is in its group, which sets its share of bits.</summary>
internal enum FrameType
{
    /// <summary>A keyframe.</summary>
    Key,

    /// <summary>An ordinary shown inter frame.</summary>
    Inter,

    /// <summary>A shown inter frame that becomes the golden reference, in a group without an alternate reference.</summary>
    Golden,

    /// <summary>A hidden alternate reference, built by the temporal filter from the group's last frame and its neighbours.</summary>
    AltRef,

    /// <summary>The shown frame at the alternate reference's place, which mostly copies it.</summary>
    Overlay,
}

/// <summary>A frame of a group as rate control plans it: its type, how hard it is to code, and its step against an inter frame's (0 for the type's usual).</summary>
internal readonly record struct GroupFrame(FrameType Type, long Complexity, double StepFactor = 0);

/// <summary>The settings rate control works from, taken from <see cref="VideoEncoderOptions"/> by the codec.</summary>
internal sealed record RateSettings
{
    public RateControlMode Mode { get; init; }

    /// <summary>The quantizer that <see cref="RateControlMode.Quality"/> and <see cref="RateControlMode.Quantizer"/> start from.</summary>
    public int BaseQuantizer { get; init; }

    public int MinQuantizer { get; init; }

    public int MaxQuantizer { get; init; }

    public long Bitrate { get; init; }

    public long MaxBitrate { get; init; }

    public long BufferSize { get; init; }

    public double FrameRate { get; init; }

    /// <summary>The luma samples per frame.</summary>
    public long Pixels { get; init; }

    /// <summary>The quantizer index's step sizes, lowest index first; their ratios are what the bit model scales by.</summary>
    public required short[] Steps { get; init; }

    /// <summary>Each frame type's step against an inter frame's in constant quality, or null for VP8's (0.7, 1, 0.8, 0.7, 1).</summary>
    public double[]? StepFactors { get; init; }

    /// <summary>For two passes, every frame's complexity from the first pass, in display order.</summary>
    public long[]? FirstPass { get; init; }

    /// <summary>
    /// Whether the frames past the current group are planned like the whole group, hidden frames included, rather than like its
    /// inter frames: right when hidden frames cost as much as the shown ones, as AV1's pyramid does.
    /// </summary>
    public bool PlanHiddenAhead { get; init; }

    /// <summary>
    /// Whether a probe of a keyframe alone (real time, where no second frame is in yet) also sets the inter frames' model, at the
    /// keyframe's: cautious, as an inter frame then costs what its complexity would as a keyframe, which keeps a constant bitrate
    /// buffer from running dry on the first inter frame.
    /// </summary>
    public bool InterFromKeyProbe { get; init; }
}

/// <summary>
/// Picks each frame's quantizer. Constant quality maps the quality to a base quantizer and gives keyframes, alternate
/// references and golden frames a finer step (libvpx's frame levels are the idea). Bitrate modes spread a budget over each
/// group by complexity and frame type, then pick the quantizer whose predicted size fits, from a model of bits against
/// complexity over step size that each frame type corrects from what its frames really cost. One pass budgets from the bits
/// spent so far; two passes from the first pass's complexities of the frames still to come; constant bitrate also keeps a
/// leaky-bucket decoder buffer from running dry. Doubles use only the basic operations and square roots, which IEEE 754 makes
/// exact, so every machine picks the same quantizers.
/// </summary>
internal sealed class RateController
{
    /// <summary>Each type's step size relative to an inter frame's in constant quality.</summary>
    private static readonly double[] StepFactors = [0.70, 1.0, 0.80, 0.70, 1.0];

    private readonly RateSettings _settings;
    private readonly double[] _correction = [2.0, 0.25, 0.4, 0.5, 0.25];
    private readonly int[] _corrected = new int[5];
    private readonly double _frameBits;
    private readonly List<GroupFrame> _plan = [];
    private double _step;
    private int _groupNext;
    private long _spent;
    private long _framesDone;
    private double _bufferLevel;
    private readonly double[] _suffix = [0];

    public RateController(RateSettings settings)
    {
        _settings = settings;
        _frameBits = settings.Bitrate / Math.Max(1e-9, settings.FrameRate);
        _bufferLevel = settings.BufferSize * 0.75;
        if (settings.FirstPass is { } pass)
        {
            _suffix = new double[pass.Length + 1];
            for (int i = pass.Length - 1; i >= 0; i--)
            {
                _suffix[i] = _suffix[i + 1] + Weight(pass[i]);
            }
        }
    }

    /// <summary>The quantizer the content probe codes at, mid-range.</summary>
    public const int ProbeQuantizer = 40;

    /// <summary>Gets whether a bitrate mode wants the first frames probed before it plans them.</summary>
    public bool WantsProbe => _settings.Mode is RateControlMode.Bitrate or RateControlMode.ConstantBitrate && _corrected[0] == 0;

    /// <summary>
    /// Sets the bit model from a probe at <see cref="ProbeQuantizer"/>: a keyframe's bits and, when there was a second frame,
    /// an inter frame's. Golden frames, alternate references and overlays start from the inter frame's model.
    /// </summary>
    public void Calibrate(long keyComplexity, long keyBits, long interComplexity, long interBits)
    {
        double step = _settings.Steps[ProbeQuantizer];
        _correction[(int)FrameType.Key] = Math.Clamp(keyBits / (Effective(keyComplexity) / Power(step)), 0.01, 1e6);
        if (interBits > 0)
        {
            double inter = Math.Clamp(interBits / (Effective(interComplexity) / Power(step)), 0.01, 1e6);
            _correction[(int)FrameType.Inter] = inter;
            _correction[(int)FrameType.Golden] = inter * 1.6;
            _correction[(int)FrameType.AltRef] = inter * 2;
            _correction[(int)FrameType.Overlay] = inter;
        }
        else if (_settings.InterFromKeyProbe)
        {
            double key = _correction[(int)FrameType.Key];
            _correction[(int)FrameType.Inter] = key;
            _correction[(int)FrameType.Golden] = key;
            _correction[(int)FrameType.AltRef] = key;
            _correction[(int)FrameType.Overlay] = key;
        }
    }

    /// <summary>Gets the decoder buffer's fill in bits for <see cref="RateControlMode.ConstantBitrate"/>, which never drops below zero.</summary>
    public double BufferLevel => _bufferLevel;

    /// <summary>Gets the bits per second rate control aims at, 0 for the quality modes.</summary>
    public long Bitrate => _settings.Bitrate;

    /// <summary>Plans the bits of a group: its frames in coding order, hidden ones included.</summary>
    public void StartGroup(ReadOnlySpan<GroupFrame> frames)
    {
        _plan.Clear();
        _groupNext = 0;
        if (_settings.Mode is RateControlMode.Quality or RateControlMode.Quantizer)
        {
            return;
        }

        foreach (GroupFrame frame in frames)
        {
            _plan.Add(frame);
        }
    }

    /// <summary>Returns the quantizer for the group's next frame.</summary>
    /// <param name="type">The frame's type.</param>
    /// <param name="complexity">How hard it is to code.</param>
    /// <param name="stepFactor">Its step against an inter frame's in constant quality, or 0 for the type's usual.</param>
    public int Next(FrameType type, long complexity, double stepFactor = 0)
    {
        RateSettings s = _settings;
        if (s.Mode == RateControlMode.Quantizer)
        {
            return Clamp(s.BaseQuantizer);
        }

        if (s.Mode == RateControlMode.Quality)
        {
            return Clamp(QuantizerForStep(s.Steps[s.BaseQuantizer] * (stepFactor > 0 ? stepFactor : TypeFactor(type))));
        }

        // The inter step at which the frames of the next two seconds fill their budget, by the model of bits as complexity over
        // step: the group's frames still to come at their types' steps, and after them frames like the group's inter frames.
        // A keyframe's bits are so spread over two seconds rather than over its group.
        double need = 0, interNeed = 0;
        int shown = 0, inter = 0;
        for (int i = _groupNext; i < _plan.Count; i++)
        {
            GroupFrame f = _plan[i];
            double n = _correction[(int)f.Type] * Effective(f.Complexity) / Power(Factor(f.Type, f.StepFactor));
            need += n;
            if (f.Type != FrameType.AltRef)
            {
                shown++;
            }

            if (f.Type is FrameType.Inter or FrameType.Golden)
            {
                interNeed += n;
                inter++;
            }
        }

        if (_groupNext >= _plan.Count)
        {
            need += _correction[(int)type] * Effective(complexity) / Power(Factor(type, stepFactor));
            shown++;
        }

        int horizon = Math.Max(shown, (int)(2 * s.FrameRate));
        double perInter = inter > 0 ? interNeed / inter : _correction[(int)FrameType.Inter] * Effective(complexity);
        if (s.PlanHiddenAhead && GroupNeedPerShown() is > 0 and var perShown)
        {
            perInter = perShown;
        }

        need += (horizon - shown) * perInter;
        double budget = Budget(horizon);
        double ratio = need / Math.Max(_frameBits * 0.05 * horizon, budget);

        // need / step^1.5 = budget, so step = ratio^(2/3): the cube of the square root's cube root, by Newton for repeatability.
        double interStep = CubeRoot(ratio * ratio);
        if (_step > 0)
        {
            // Steps move gradually, so a frame never re-codes much of its reference at a very different quality; coarser
            // steps may come faster than finer ones, so an overshoot is caught quickly.
            interStep = Math.Clamp(interStep, _step * 0.88, _step * 1.6);
        }

        _step = interStep;
        _groupNext++;
        int quantizer = Clamp(QuantizerForStep(interStep * Factor(type, stepFactor)));
        if (s.Mode == RateControlMode.ConstantBitrate)
        {
            // Never plan a frame the buffer cannot take, with a margin for the model's error.
            double room = _bufferLevel + _frameBits - (s.BufferSize * 0.15);
            double model = _correction[(int)type] * Effective(complexity);
            while (quantizer < s.MaxQuantizer && model * 2 / Power(s.Steps[quantizer]) > room)
            {
                quantizer++;
            }
        }

        return quantizer;
    }

    /// <summary>What the whole group's frames other than a keyframe need, hidden ones included, per shown frame; 0 when none is shown.</summary>
    private double GroupNeedPerShown()
    {
        double need = 0;
        int shown = 0;
        foreach (GroupFrame f in _plan)
        {
            if (f.Type == FrameType.Key)
            {
                continue;
            }

            need += _correction[(int)f.Type] * Effective(f.Complexity) / Power(Factor(f.Type, f.StepFactor));
            if (f.Type != FrameType.AltRef)
            {
                shown++;
            }
        }

        return shown == 0 ? 0 : need / shown;
    }

    /// <summary>The bits the next <paramref name="horizon"/> shown frames may spend.</summary>
    private double Budget(int horizon)
    {
        RateSettings s = _settings;
        double budget;
        if (s.Mode == RateControlMode.ConstantBitrate)
        {
            // Steer the buffer back towards three quarters full.
            budget = (horizon * _frameBits) + (_bufferLevel - (s.BufferSize * 0.75));
        }
        else if (s.FirstPass is { } pass && _framesDone < pass.Length)
        {
            // Two passes: the horizon's share of what is left, by the first pass's complexities of the frames still to come.
            int start = (int)_framesDone, end = Math.Min(pass.Length, start + horizon);
            double share = _suffix[start] - _suffix[end];
            double left = (_frameBits * pass.Length) - _spent;
            budget = left * share / Math.Max(1e-9, _suffix[start]);
            budget += (horizon - (end - start)) * _frameBits;
        }
        else
        {
            // One pass: the nominal bits, plus or minus what was saved or overspent so far.
            budget = (horizon * _frameBits) + ((_frameBits * _framesDone) - _spent);
        }

        return Math.Min(Math.Max(budget, horizon * _frameBits * 0.2), horizon * (s.MaxBitrate / s.FrameRate));
    }

    /// <summary>
    /// A step to the power 1.5: bits fall faster than the step grows, as detail below the step stops being coded at all.
    /// </summary>
    private static double Power(double step) => step * Math.Sqrt(step);

    private static double CubeRoot(double value)
    {
        if (value <= 0)
        {
            return 0;
        }

        double x = value > 1 ? value / 3 : 1;
        for (int i = 0; i < 200; i++)
        {
            double next = x - (((x * x * x) - value) / (3 * x * x));
            if (next == x)
            {
                break;
            }

            x = next;
        }

        return x;
    }

    private double Factor(FrameType type, double stepFactor) => stepFactor > 0 ? stepFactor : TypeFactor(type);

    private double TypeFactor(FrameType type) => (_settings.StepFactors ?? StepFactors)[(int)type];

    /// <summary>A complexity with a floor: even a frame equal to the one before costs some bits.</summary>
    private double Effective(long complexity) => Math.Max(1, complexity) + (_settings.Pixels / 16.0);


    /// <summary>Learns from a coded frame: corrects its type's model and spends its bits.</summary>
    public void Done(FrameType type, int quantizer, long bits, long complexity)
    {
        RateSettings s = _settings;
        _spent += bits;
        if (type != FrameType.AltRef)
        {
            _framesDone++;
            _bufferLevel = Math.Min(s.BufferSize, _bufferLevel + _frameBits);
        }

        _bufferLevel -= bits;
        if (_bufferLevel < 0)
        {
            _bufferLevel = 0;
        }

        double predicted = Effective(complexity) / Power(s.Steps[quantizer]);
        double ratio = Math.Clamp(bits / predicted, 0.01, 1e6);
        int t = (int)type;
        if (_corrected[t] == 0)
        {
            _correction[t] = ratio;
        }
        else
        {
            // Quick to learn early on, then steadier; overshoots count more, for the buffer's sake.
            double rate = _corrected[t] < 4 ? 0.5 : ratio > _correction[t] ? 0.45 : 0.35;
            _correction[t] += (ratio - _correction[t]) * rate;
        }

        _corrected[t]++;
    }

    private int Clamp(int q) => Math.Clamp(q, _settings.MinQuantizer, _settings.MaxQuantizer);

    private int QuantizerForStep(double step)
    {
        short[] steps = _settings.Steps;
        for (int q = 0; q < steps.Length; q++)
        {
            if (steps[q] >= step)
            {
                return q == 0 || steps[q] - step <= step - steps[q - 1] ? q : q - 1;
            }
        }

        return steps.Length - 1;
    }

    /// <summary>A first-pass complexity's weight: its 3/4 power, as bits grow slower than complexity at equal quality.</summary>
    private static double Weight(long complexity)
    {
        double c = Math.Max(1, complexity);
        double root = Math.Sqrt(c);
        return root * Math.Sqrt(root);
    }
}
