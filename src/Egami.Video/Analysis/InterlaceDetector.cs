using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Egami.Video.Analysis;

/// <summary>
/// Tells progressive, interlaced and telecined video apart, as ffmpeg's idet does. Each frame's fields are woven three ways (with
/// each other, and each with the other field of the frame before) and the combing of each weave compared: the weave of fields
/// closest in time combs least. Fields that repeat the frame before's are counted, and repeats in a five-frame pattern mark 3:2
/// telecine. Motion is needed to decide; frames without it count as undetermined.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-video-detectors"/>
/// </example>
public sealed class InterlaceDetector : IVideoDetector
{
    private readonly InterlaceOptions _options;
    private readonly long[] _repeatPhase = new long[5];
    private ushort[] _previous = [];
    private ushort[] _current = [];
    private int _width;
    private int _height;
    private long _count;
    private long _topFirst;
    private long _bottomFirst;
    private long _progressive;
    private long _undetermined;
    private long _repeatedTop;
    private long _repeatedBottom;
    private bool _complete;

    /// <summary>Creates a detector.</summary>
    /// <param name="options">The thresholds, or <see langword="null"/> for <see cref="InterlaceOptions.Default"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-video-detectors"/>
    /// </example>
    public InterlaceDetector(InterlaceOptions? options = null)
    {
        _options = options ?? InterlaceOptions.Default;
        _options.Validate();
    }

    /// <summary>Gets the options the detector runs with.</summary>
    public InterlaceOptions Options => _options;

    /// <summary>Gets how many frames the detector has taken.</summary>
    public long FrameCount => _count;

    /// <summary>Gets the field order the last frame shows on its own.</summary>
    public FieldOrder LastFieldOrder { get; private set; }

    /// <summary>Returns the counts so far and the verdict they give.</summary>
    /// <returns>The report.</returns>
    public InterlaceReport Report()
    {
        long repeats = _repeatedTop + _repeatedBottom;
        return new InterlaceReport(_topFirst, _bottomFirst, _progressive, _undetermined, _repeatedTop, _repeatedBottom, Math.Max(0, _count - 1 - repeats), Verdict());
    }

    /// <summary>Takes the next frame.</summary>
    /// <param name="frame">The frame, in presentation order.</param>
    /// <exception cref="InvalidOperationException"><see cref="Complete"/> was called.</exception>
    public void Add(YuvVideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_complete)
        {
            throw new InvalidOperationException("The detector is complete; create a new one for more frames.");
        }

        YuvRaster raster = frame.Raster;
        bool restart = raster.Width != _width || raster.Height != _height;
        _width = raster.Width;
        _height = raster.Height;
        if (_current.Length != _width * _height)
        {
            _current = new ushort[_width * _height];
            _previous = new ushort[_width * _height];
        }

        switch (raster)
        {
            case YuvRaster<byte> bytes:
                Load(bytes.Y, _current);
                break;
            case YuvRaster<ushort> words:
                Load(words.Y, _current);
                break;
            default:
                throw new NotSupportedException($"Planes of {raster.GetType().Name} are not supported.");
        }

        long index = _count++;
        if (index > 0 && !restart && _height >= 6)
        {
            Classify(index);
        }
        else
        {
            LastFieldOrder = FieldOrder.Undetermined;
        }

        (_previous, _current) = (_current, _previous);
    }

    /// <summary>Finishes the counts.</summary>
    /// <param name="endTime">When the video ends.</param>
    public void Complete(MediaTime endTime) => _complete = true;

    private static void Load<T>(YuvPlane<T> plane, ushort[] target)
        where T : unmanaged, IBinaryInteger<T>
    {
        int width = plane.Width;
        for (int y = 0; y < plane.Height; y++)
        {
            ReadOnlySpan<T> row = plane.Row(y)[..width];
            Span<ushort> to = target.AsSpan(y * width, width);
            if (typeof(T) == typeof(ushort))
            {
                MemoryMarshal.Cast<T, ushort>(row).CopyTo(to);
                continue;
            }

            int x = 0;
            if (typeof(T) == typeof(byte) && Vector256.IsHardwareAccelerated)
            {
                ReadOnlySpan<byte> bytes = MemoryMarshal.Cast<T, byte>(row);
                for (; x <= width - Vector256<byte>.Count; x += Vector256<byte>.Count)
                {
                    (Vector256<ushort> lo, Vector256<ushort> hi) = Vector256.Widen(Vector256.Create(bytes.Slice(x, Vector256<byte>.Count)));
                    lo.CopyTo(to.Slice(x, Vector256<ushort>.Count));
                    hi.CopyTo(to.Slice(x + Vector256<ushort>.Count, Vector256<ushort>.Count));
                }
            }

            for (; x < width; x++)
            {
                to[x] = ushort.CreateTruncating(row[x]);
            }
        }
    }

    private void Classify(long index)
    {
        // Weaves: this frame's own fields; this frame's top field with the frame before's bottom; the frame before's top with this
        // frame's bottom. For top field first video the fields run T0 B0 T1 B1, so B0 with T1 are neighbors in time and T0 with B1
        // are a frame and a half apart; bottom field first mirrors it; progressive frames have no combing of their own.
        double own = Comb(_current, _current);
        double top = Comb(_current, _previous);
        double bottom = Comb(_previous, _current);
        FieldOrder order;
        if (own * _options.ProgressiveRatio < Math.Min(top, bottom))
        {
            order = FieldOrder.Progressive;
            _progressive++;
        }
        else if (top * _options.InterlaceRatio < bottom)
        {
            order = FieldOrder.TopFieldFirst;
            _topFirst++;
        }
        else if (bottom * _options.InterlaceRatio < top)
        {
            order = FieldOrder.BottomFieldFirst;
            _bottomFirst++;
        }
        else
        {
            order = FieldOrder.Undetermined;
            _undetermined++;
        }

        LastFieldOrder = order;

        // A field that repeats the frame before's differs from it far less than the other field does.
        double topChange = FieldChange(0), bottomChange = FieldChange(1);
        if (topChange * _options.RepeatRatio < bottomChange)
        {
            _repeatedTop++;
            _repeatPhase[index % 5]++;
        }
        else if (bottomChange * _options.RepeatRatio < topChange)
        {
            _repeatedBottom++;
            _repeatPhase[index % 5]++;
        }
    }

    /// <summary>The combing of a weave: top field rows from <paramref name="topSource"/>, bottom field rows from <paramref name="bottomSource"/>.</summary>
    private double Comb(ushort[] topSource, ushort[] bottomSource)
    {
        int width = _width;
        long total = 0;
        for (int y = 1; y < _height - 1; y++)
        {
            ushort[] here = (y & 1) == 0 ? topSource : bottomSource;
            ushort[] other = (y & 1) == 0 ? bottomSource : topSource;
            ReadOnlySpan<ushort> above = other.AsSpan((y - 1) * width, width);
            ReadOnlySpan<ushort> row = here.AsSpan(y * width, width);
            ReadOnlySpan<ushort> below = other.AsSpan((y + 1) * width, width);
            total += CombRow(above, row, below);
        }

        return total;
    }

    /// <summary>Sums |2 row - above - below| over a row, sixteen samples at a time in 32-bit lanes.</summary>
    private static long CombRow(ReadOnlySpan<ushort> above, ReadOnlySpan<ushort> row, ReadOnlySpan<ushort> below)
    {
        long total = 0;
        int x = 0;
        if (Vector256.IsHardwareAccelerated && row.Length >= Vector256<ushort>.Count)
        {
            // A lane gains at most 2 x 131,070 per 16 samples, so rows up to 16,000 samples stay within 32 bits.
            Vector256<int> sum = Vector256<int>.Zero;
            for (; x <= row.Length - Vector256<ushort>.Count; x += Vector256<ushort>.Count)
            {
                (Vector256<uint> a0, Vector256<uint> a1) = Vector256.Widen(Vector256.Create(above.Slice(x, Vector256<ushort>.Count)));
                (Vector256<uint> r0, Vector256<uint> r1) = Vector256.Widen(Vector256.Create(row.Slice(x, Vector256<ushort>.Count)));
                (Vector256<uint> b0, Vector256<uint> b1) = Vector256.Widen(Vector256.Create(below.Slice(x, Vector256<ushort>.Count)));
                Vector256<int> d0 = (r0 + r0 - a0 - b0).AsInt32();
                Vector256<int> d1 = (r1 + r1 - a1 - b1).AsInt32();
                sum += Vector256.Abs(d0) + Vector256.Abs(d1);
            }

            total = Vector256.Sum(Vector256.WidenLower(sum) + Vector256.WidenUpper(sum));
        }

        for (; x < row.Length; x++)
        {
            int d = (2 * row[x]) - above[x] - below[x];
            total += d < 0 ? -d : d;
        }

        return total;
    }

    /// <summary>How much one field (0 top, 1 bottom) changed from the frame before.</summary>
    private double FieldChange(int parity)
    {
        long total = 0;
        for (int y = parity; y < _height; y += 2)
        {
            total += PlaneMath.AbsoluteDifference<ushort>(_current.AsSpan(y * _width, _width), _previous.AsSpan(y * _width, _width));
        }

        return total;
    }

    private InterlaceVerdict Verdict()
    {
        long frames = _count - 1;
        long repeats = _repeatedTop + _repeatedBottom;
        if (frames < 5)
        {
            return InterlaceVerdict.Unknown;
        }

        // 3:2 pulldown repeats a field in two of every five frames, always at the same two phases.
        if (repeats >= frames / 5)
        {
            long[] phases = (long[])_repeatPhase.Clone();
            Array.Sort(phases);
            if (phases[4] + phases[3] >= repeats * 0.8)
            {
                return InterlaceVerdict.Telecined;
            }
        }

        long decided = _topFirst + _bottomFirst + _progressive;
        if (decided == 0)
        {
            return InterlaceVerdict.Unknown;
        }

        if (_progressive >= decided * 0.8)
        {
            return InterlaceVerdict.Progressive;
        }

        if (_topFirst >= decided * 0.8)
        {
            return InterlaceVerdict.InterlacedTopFirst;
        }

        if (_bottomFirst >= decided * 0.8)
        {
            return InterlaceVerdict.InterlacedBottomFirst;
        }

        return InterlaceVerdict.Mixed;
    }
}

/// <summary>How sure <see cref="InterlaceDetector"/> must be before it decides. The defaults are ffmpeg idet's.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-video-detectors"/>
/// </example>
public sealed record InterlaceOptions
{
    /// <summary>Gets the default options.</summary>
    public static InterlaceOptions Default { get; } = new();

    /// <summary>Gets how many times less one field order must comb than the other to decide it. Default 1.04, as idet's interlace threshold.</summary>
    public double InterlaceRatio { get; init; } = 1.04;

    /// <summary>Gets how many times less a frame's own fields must comb than either cross weave to call it progressive. Default 1.5, as idet's.</summary>
    public double ProgressiveRatio { get; init; } = 1.5;

    /// <summary>Gets how many times less a field must change than the other to count as repeated. Default 3, as idet's repeat threshold.</summary>
    public double RepeatRatio { get; init; } = 3;

    internal void Validate()
    {
        Check(InterlaceRatio, nameof(InterlaceRatio));
        Check(ProgressiveRatio, nameof(ProgressiveRatio));
        Check(RepeatRatio, nameof(RepeatRatio));
    }

    private static void Check(double value, string name)
    {
        if (value < 1 || double.IsNaN(value))
        {
            throw new ArgumentOutOfRangeException(name, value, "The ratio must be 1 or more.");
        }
    }
}

/// <summary>What <see cref="InterlaceDetector"/> counted.</summary>
/// <param name="TopFieldFirst">Frames that looked interlaced, top field first.</param>
/// <param name="BottomFieldFirst">Frames that looked interlaced, bottom field first.</param>
/// <param name="Progressive">Frames that looked progressive.</param>
/// <param name="Undetermined">Frames that could not be told (usually still pictures).</param>
/// <param name="RepeatedTop">Frames whose top field repeated the frame before's.</param>
/// <param name="RepeatedBottom">Frames whose bottom field repeated the frame before's.</param>
/// <param name="NotRepeated">Frames with no repeated field.</param>
/// <param name="Verdict">What the counts say about the whole video.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-video-detectors"/>
/// </example>
public sealed record InterlaceReport(long TopFieldFirst, long BottomFieldFirst, long Progressive, long Undetermined, long RepeatedTop, long RepeatedBottom, long NotRepeated, InterlaceVerdict Verdict);

/// <summary>One frame's field order, as <see cref="InterlaceDetector"/> judged it.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-video-detectors"/>
/// </example>
public enum FieldOrder
{
    /// <summary>Not enough motion to tell, or the first frame.</summary>
    Undetermined,

    /// <summary>The frame's two fields are from one moment.</summary>
    Progressive,

    /// <summary>Interlaced, top field first.</summary>
    TopFieldFirst,

    /// <summary>Interlaced, bottom field first.</summary>
    BottomFieldFirst,
}

/// <summary>What a video is, by <see cref="InterlaceDetector"/>'s counts.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-video-detectors"/>
/// </example>
public enum InterlaceVerdict
{
    /// <summary>Too few frames, or no frame could be decided.</summary>
    Unknown,

    /// <summary>At least 80 % of the decided frames are progressive.</summary>
    Progressive,

    /// <summary>At least 80 % of the decided frames are interlaced top field first.</summary>
    InterlacedTopFirst,

    /// <summary>At least 80 % of the decided frames are interlaced bottom field first.</summary>
    InterlacedBottomFirst,

    /// <summary>Fields repeat in a five-frame pattern: film put through 3:2 pulldown, which inverse telecine can undo.</summary>
    Telecined,

    /// <summary>The decided frames disagree: edited from mixed sources, or partly interlaced.</summary>
    Mixed,
}
