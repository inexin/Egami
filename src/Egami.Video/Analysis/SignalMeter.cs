using System.Numerics;
using System.Runtime.Intrinsics;

namespace Egami.Video.Analysis;

/// <summary>
/// Measures the signal, as ffmpeg's signalstats and colordetect do: the lowest, 10th percentile, mean, 90th percentile and highest
/// code values of Y, Cb and Cr; chroma saturation; the share of pixels outside broadcast range (16 to 235 for luma, 16 to 240 for
/// chroma, scaled to the bit depth); which range the samples really use; and whether an alpha plane is used and how. With
/// <see cref="SignalOptions.Blur"/> and <see cref="SignalOptions.Blockiness"/> it also scores sharpness and coding blocks, as
/// blurdetect and blockdetect do.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-signal"/>
/// </example>
public sealed class SignalMeter : IVideoDetector
{
    private readonly SignalOptions _options;
    private readonly List<SignalFrame> _series = [];
    private readonly EdgeMeasures _edges = new();
    private int[] _frameY = [];
    private int[] _frameCb = [];
    private int[] _frameCr = [];
    private int[] _frameSat = [];
    private long[] _totalY = [];
    private long[] _totalCb = [];
    private long[] _totalCr = [];
    private long[] _totalSat = [];
    private byte[] _chromaOut = [];
    private float[] _luma = [];
    private long _count;
    private int _depth;
    private long _lumaSamples;
    private long _outOfRange;
    private bool _anyChroma;
    private bool _alphaSeen;
    private bool _translucent;
    private bool _straight;
    private double _blurSum;
    private long _blurFrames;
    private double _blockSum;
    private long _blockFrames;
    private bool _complete;

    /// <summary>Creates a meter.</summary>
    /// <param name="options">What to measure, or <see langword="null"/> for <see cref="SignalOptions.Default"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-signal"/>
    /// </example>
    public SignalMeter(SignalOptions? options = null)
    {
        _options = options ?? SignalOptions.Default;
        _options.Validate();
    }

    /// <summary>Gets the options the meter runs with.</summary>
    public SignalOptions Options => _options;

    /// <summary>Gets how many frames the meter has taken.</summary>
    public long FrameCount => _count;

    /// <summary>Gets every frame's measures, when <see cref="SignalOptions.KeepSeries"/> is set; otherwise empty.</summary>
    public IReadOnlyList<SignalFrame> Series => _series;

    /// <summary>Returns the measures over every frame so far.</summary>
    /// <returns>The report.</returns>
    public SignalReport Report()
    {
        VideoLevelsLite levels = new(_depth);
        PlaneLevels y = Levels(_totalY), cb = Levels(_totalCb), cr = Levels(_totalCr), sat = Levels(_totalSat);
        SignalRange range = _count == 0 || (y.Min >= levels.Black && y.Max <= levels.White
            && (!_anyChroma || (cb.Min >= levels.ChromaLow && cb.Max <= levels.ChromaHigh && cr.Min >= levels.ChromaLow && cr.Max <= levels.ChromaHigh)))
            ? SignalRange.Limited
            : SignalRange.Full;
        AlphaUse alpha = !_alphaSeen ? AlphaUse.None : !_translucent ? AlphaUse.Opaque : _straight ? AlphaUse.Straight : AlphaUse.Premultiplied;
        return new SignalReport(
            _count,
            _depth,
            y,
            cb,
            cr,
            sat,
            _lumaSamples == 0 ? 0 : (double)_outOfRange / _lumaSamples,
            range,
            alpha,
            _blurFrames == 0 ? null : _blurSum / _blurFrames,
            _blockFrames == 0 ? null : _blockSum / _blockFrames);
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
        if (_depth != raster.BitDepth)
        {
            if (_count > 0)
            {
                throw new NotSupportedException("The bit depth changed partway; measure each part with its own meter.");
            }

            Allocate(raster.BitDepth);
        }

        switch (raster)
        {
            case YuvRaster<byte> bytes:
                Measure(bytes, frame);
                break;
            case YuvRaster<ushort> words:
                Measure(words, frame);
                break;
            default:
                throw new NotSupportedException($"Planes of {raster.GetType().Name} are not supported.");
        }

        _count++;
    }

    /// <summary>Finishes the measures.</summary>
    /// <param name="endTime">When the video ends.</param>
    public void Complete(MediaTime endTime) => _complete = true;

    private static PlaneLevels Levels(int[] histogram, long count, double sum)
    {
        if (count == 0)
        {
            return default;
        }

        int min = -1, max = 0, low = -1, high = -1;
        long running = 0, lowAt = (long)Math.Ceiling(count * 0.1), highAt = (long)Math.Ceiling(count * 0.9);
        for (int v = 0; v < histogram.Length; v++)
        {
            int n = histogram[v];
            if (n == 0)
            {
                continue;
            }

            if (min < 0)
            {
                min = v;
            }

            max = v;
            running += n;
            if (low < 0 && running >= lowAt)
            {
                low = v;
            }

            if (high < 0 && running >= highAt)
            {
                high = v;
            }
        }

        return new PlaneLevels(min, low, sum / count, high, max);
    }

    private static PlaneLevels Levels(long[] histogram)
    {
        long count = 0;
        double sum = 0;
        for (int v = 0; v < histogram.Length; v++)
        {
            count += histogram[v];
            sum += (double)histogram[v] * v;
        }

        if (count == 0)
        {
            return default;
        }

        int min = -1, max = 0, low = -1, high = -1;
        long running = 0, lowAt = (long)Math.Ceiling(count * 0.1), highAt = (long)Math.Ceiling(count * 0.9);
        for (int v = 0; v < histogram.Length; v++)
        {
            long n = histogram[v];
            if (n == 0)
            {
                continue;
            }

            if (min < 0)
            {
                min = v;
            }

            max = v;
            running += n;
            if (low < 0 && running >= lowAt)
            {
                low = v;
            }

            if (high < 0 && running >= highAt)
            {
                high = v;
            }
        }

        return new PlaneLevels(min, low, sum / count, high, max);
    }

    private void Allocate(int depth)
    {
        _depth = depth;
        int size = 1 << depth;
        _frameY = new int[size];
        _frameCb = new int[size];
        _frameCr = new int[size];
        _frameSat = new int[size];
        _totalY = new long[size];
        _totalCb = new long[size];
        _totalCr = new long[size];
        _totalSat = new long[size];
    }

    private void Measure<T>(YuvRaster<T> raster, YuvVideoFrame frame)
        where T : unmanaged, IBinaryInteger<T>
    {
        VideoLevels levels = new(raster);
        Array.Clear(_frameY);
        Array.Clear(_frameCb);
        Array.Clear(_frameCr);
        Array.Clear(_frameSat);
        YuvPlane<T> lumaPlane = raster.Y;
        YuvPlane<T>? u = raster.U, v = raster.V;
        int width = lumaPlane.Width, height = lumaPlane.Height;
        int xShift = u is null ? 0 : width > u.Width ? 1 : 0;
        int yShift = u is null ? 0 : height > u.Height ? 1 : 0;
        int bb = levels.BroadcastBlack, bw = levels.BroadcastWhite, cl = levels.ChromaLow, ch = levels.ChromaHigh;
        long frameOut = 0;
        double sumY = 0, sumCb = 0, sumCr = 0, sumSat = 0;
        if (typeof(T) == typeof(byte))
        {
            (sumY, sumCb, sumCr, sumSat, frameOut) = Measure8((YuvPlane<byte>)(object)lumaPlane, (YuvPlane<byte>?)(object?)u,
                (YuvPlane<byte>?)(object?)v, levels, xShift, yShift);
        }
        else
        {
            if (u is not null && v is not null)
            {
                _anyChroma = true;
                if (_chromaOut.Length < u.Width)
                {
                    _chromaOut = new byte[u.Width];
                }

                for (int y = 0; y < u.Height; y++)
                {
                    ReadOnlySpan<T> ur = u.Row(y)[..u.Width], vr = v.Row(y)[..u.Width];
                    for (int x = 0; x < ur.Length; x++)
                    {
                        int cu = int.CreateTruncating(ur[x]), cv = int.CreateTruncating(vr[x]);
                        _frameCb[cu]++;
                        _frameCr[cv]++;
                        sumCb += cu;
                        sumCr += cv;
                        double du = cu - levels.Middle, dv = cv - levels.Middle;
                        double sat = Math.Sqrt((du * du) + (dv * dv));
                        sumSat += sat;
                        _frameSat[Math.Min((int)Math.Round(sat), _frameSat.Length - 1)]++;
                    }
                }
            }

            int chromaRow = -1;
            for (int y = 0; y < height; y++)
            {
                if (u is not null && v is not null && (y >> yShift) != chromaRow)
                {
                    chromaRow = y >> yShift;
                    ReadOnlySpan<T> ur = u.Row(chromaRow)[..u.Width], vr = v.Row(chromaRow)[..u.Width];
                    for (int x = 0; x < ur.Length; x++)
                    {
                        int cu = int.CreateTruncating(ur[x]), cv = int.CreateTruncating(vr[x]);
                        _chromaOut[x] = (byte)(cu < cl || cu > ch || cv < cl || cv > ch ? 1 : 0);
                    }
                }

                ReadOnlySpan<T> row = lumaPlane.Row(y)[..width];
                for (int x = 0; x < width; x++)
                {
                    int value = int.CreateTruncating(row[x]);
                    _frameY[value]++;
                    sumY += value;
                    if (value < bb || value > bw || (u is not null && _chromaOut[x >> xShift] != 0))
                    {
                        frameOut++;
                    }
                }
            }
        }

        if (raster.A is { } alpha)
        {
            MeasureAlpha(lumaPlane, alpha, levels);
        }

        long lumaCount = (long)width * height;
        long chromaCount = u is null ? 0 : (long)u.Width * u.Height;
        _lumaSamples += lumaCount;
        _outOfRange += frameOut;
        Add(_totalY, _frameY);
        Add(_totalCb, _frameCb);
        Add(_totalCr, _frameCr);
        Add(_totalSat, _frameSat);

        double? blur = null, blockiness = null;
        if (_options.Blur || _options.Blockiness)
        {
            LoadLuma(lumaPlane, levels);
            if (_options.Blur)
            {
                blur = _edges.Blur(_luma, width, height, _options.BlurHigh, _options.BlurLow, _options.BlurBlockShare);
                if (blur is { } b)
                {
                    _blurSum += b;
                    _blurFrames++;
                }
            }

            if (_options.Blockiness)
            {
                blockiness = _edges.Blockiness(_luma, width, height, _options.BlockPeriodMin, _options.BlockPeriodMax);
                _blockSum += blockiness.Value;
                _blockFrames++;
            }
        }

        if (_options.KeepSeries)
        {
            _series.Add(new SignalFrame(
                _count,
                frame.Timestamp,
                Levels(_frameY, lumaCount, sumY),
                Levels(_frameCb, chromaCount, sumCb),
                Levels(_frameCr, chromaCount, sumCr),
                Levels(_frameSat, chromaCount, sumSat),
                (double)frameOut / lumaCount,
                blur,
                blockiness));
        }
    }

    /// <summary>The saturation of each 8-bit Cb and Cr pair (index Cb x 256 + Cr), shared by every meter.</summary>
    private static readonly Lazy<double[]> Saturation8 = new(() =>
    {
        double[] table = new double[256 * 256];
        for (int cu = 0; cu < 256; cu++)
        {
            for (int cv = 0; cv < 256; cv++)
            {
                double du = cu - 128, dv = cv - 128;
                table[(cu << 8) | cv] = Math.Sqrt((du * du) + (dv * dv));
            }
        }

        return table;
    });

    /// <summary>
    /// The 8-bit pass: each chroma row is measured with the luma rows it covers. Saturation comes from a table, sums from the
    /// histograms, and a luma row whose chroma is all in range counts its out-of-range samples with vector compares; the
    /// histograms are counted four ways so neighboring samples do not wait on each other's counts.
    /// </summary>
    private (double SumY, double SumCb, double SumCr, double SumSat, long Out) Measure8(YuvPlane<byte> luma, YuvPlane<byte>? u, YuvPlane<byte>? v,
        VideoLevels levels, int xShift, int yShift)
    {
        int width = luma.Width, height = luma.Height;
        int bb = levels.BroadcastBlack, bw = levels.BroadcastWhite, cl = levels.ChromaLow, ch = levels.ChromaHigh;
        Span<int> counts = stackalloc int[4 * 256];
        counts.Clear();
        double sumSat = 0;
        long frameOut = 0;
        bool chroma = u is not null && v is not null;
        double[] saturation = chroma ? Saturation8.Value : [];
        if (chroma)
        {
            _anyChroma = true;
            if (_chromaOut.Length < u!.Width)
            {
                _chromaOut = new byte[u.Width];
            }
        }

        int chromaRow = -1;
        bool rowOut = false;
        for (int y = 0; y < height; y++)
        {
            if (chroma && (y >> yShift) != chromaRow)
            {
                chromaRow = y >> yShift;
                ReadOnlySpan<byte> ur = u!.Row(chromaRow)[..u.Width], vr = v!.Row(chromaRow)[..u.Width];
                rowOut = false;
                for (int x = 0; x < ur.Length; x++)
                {
                    int cu = ur[x], cv = vr[x];
                    _frameCb[cu]++;
                    _frameCr[cv]++;
                    double sat = saturation[(cu << 8) | cv];
                    sumSat += sat;
                    _frameSat[Math.Min((int)Math.Round(sat), _frameSat.Length - 1)]++;
                    bool outside = cu < cl || cu > ch || cv < cl || cv > ch;
                    _chromaOut[x] = (byte)(outside ? 1 : 0);
                    rowOut |= outside;
                }
            }

            ReadOnlySpan<byte> row = luma.Row(y)[..width];
            int i = 0;
            for (; i <= width - 4; i += 4)
            {
                counts[row[i]]++;
                counts[256 + row[i + 1]]++;
                counts[512 + row[i + 2]]++;
                counts[768 + row[i + 3]]++;
            }

            for (; i < width; i++)
            {
                counts[row[i]]++;
            }

            if (rowOut)
            {
                for (int x = 0; x < width; x++)
                {
                    int value = row[x];
                    if (value < bb || value > bw || _chromaOut[x >> xShift] != 0)
                    {
                        frameOut++;
                    }
                }
            }
            else
            {
                frameOut += CountOutside(row, bb, bw);
            }
        }

        double sumY = 0, sumCb = 0, sumCr = 0;
        for (int value = 0; value < 256; value++)
        {
            int count = counts[value] + counts[256 + value] + counts[512 + value] + counts[768 + value];
            _frameY[value] += count;
            sumY += (double)value * count;
            sumCb += (double)value * _frameCb[value];
            sumCr += (double)value * _frameCr[value];
        }

        return (sumY, sumCb, sumCr, sumSat, frameOut);
    }

    /// <summary>Counts the samples under <paramref name="low"/> or over <paramref name="high"/>.</summary>
    private static long CountOutside(ReadOnlySpan<byte> row, int low, int high)
    {
        long count = 0;
        int x = 0;
        if (Vector256.IsHardwareAccelerated && row.Length >= Vector256<byte>.Count)
        {
            Vector256<byte> lo = Vector256.Create((byte)low), hi = Vector256.Create((byte)high);
            for (; x <= row.Length - Vector256<byte>.Count; x += Vector256<byte>.Count)
            {
                Vector256<byte> values = Vector256.Create(row.Slice(x, Vector256<byte>.Count));
                Vector256<byte> outside = Vector256.LessThan(values, lo) | Vector256.GreaterThan(values, hi);
                count += BitOperations.PopCount(outside.ExtractMostSignificantBits());
            }
        }

        for (; x < row.Length; x++)
        {
            if (row[x] < low || row[x] > high)
            {
                count++;
            }
        }

        return count;
    }

    private static void Add(long[] total, int[] frame)
    {
        for (int i = 0; i < frame.Length; i++)
        {
            total[i] += frame[i];
        }
    }

    private void MeasureAlpha<T>(YuvPlane<T> luma, YuvPlane<T> alpha, VideoLevels levels)
        where T : unmanaged, IBinaryInteger<T>
    {
        _alphaSeen = true;
        double range = levels.White - levels.Black;
        int height = Math.Min(luma.Height, alpha.Height), width = Math.Min(luma.Width, alpha.Width);
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<T> a = alpha.Row(y)[..width], l = luma.Row(y)[..width];
            for (int x = 0; x < width; x++)
            {
                int av = int.CreateTruncating(a[x]);
                if (av >= levels.Max)
                {
                    continue;
                }

                _translucent = true;

                // Premultiplied color never outshines its alpha; straight color does wherever a light pixel is see-through.
                double lv = (int.CreateTruncating(l[x]) - levels.Black) / range;
                if (lv > ((double)av / levels.Max) + (2.0 / 255))
                {
                    _straight = true;
                }
            }
        }
    }

    private void LoadLuma<T>(YuvPlane<T> plane, VideoLevels levels)
        where T : unmanaged, IBinaryInteger<T>
    {
        int width = plane.Width, height = plane.Height;
        if (_luma.Length != width * height)
        {
            _luma = new float[width * height];
        }

        float black = levels.Black, scale = 1f / (levels.White - levels.Black);
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<T> row = plane.Row(y)[..width];
            Span<float> to = _luma.AsSpan(y * width, width);
            for (int x = 0; x < width; x++)
            {
                to[x] = (int.CreateTruncating(row[x]) - black) * scale;
            }
        }
    }

    /// <summary>Range limits for a bit depth, for the report.</summary>
    private readonly struct VideoLevelsLite(int depth)
    {
        public int Black => depth == 0 ? 0 : 16 << (depth - 8);

        public int White => depth == 0 ? 0 : 235 << (depth - 8);

        public int ChromaLow => depth == 0 ? 0 : 16 << (depth - 8);

        public int ChromaHigh => depth == 0 ? 0 : 240 << (depth - 8);
    }
}

/// <summary>What <see cref="SignalMeter"/> measures besides levels. The blur and blockiness settings follow ffmpeg's blurdetect and blockdetect.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-signal"/>
/// </example>
public sealed record SignalOptions
{
    /// <summary>Gets the default options: levels, range and alpha only.</summary>
    public static SignalOptions Default { get; } = new();

    /// <summary>Gets whether every frame's measures are kept in <see cref="SignalMeter.Series"/>. Off by default to keep memory flat.</summary>
    public bool KeepSeries { get; init; }

    /// <summary>Gets whether sharpness is measured: the mean width of edges, in pixels. Off by default; it costs a pass over the picture.</summary>
    public bool Blur { get; init; }

    /// <summary>Gets the gradient, as a fraction of the range from black to white, that makes a pixel an edge. Default 30/255, as blurdetect's high.</summary>
    public double BlurHigh { get; init; } = 30.0 / 255;

    /// <summary>Gets the gradient a pixel next to an edge needs to join it. Default 15/255, as blurdetect's low.</summary>
    public double BlurLow { get; init; } = 15.0 / 255;

    /// <summary>Gets the share of 32 by 32 blocks, those with the most edges, that the blur is averaged over. Default 0.8, as blurdetect's 80 %.</summary>
    public double BlurBlockShare { get; init; } = 0.8;

    /// <summary>Gets whether coding blocks are measured. Off by default.</summary>
    public bool Blockiness { get; init; }

    /// <summary>Gets the smallest block size looked for. Default 3, as blockdetect's period_min.</summary>
    public int BlockPeriodMin { get; init; } = 3;

    /// <summary>Gets the largest block size looked for. Default 24, as blockdetect's period_max.</summary>
    public int BlockPeriodMax { get; init; } = 24;

    internal void Validate()
    {
        if (BlurHigh is <= 0 or > 1 || double.IsNaN(BlurHigh))
        {
            throw new ArgumentOutOfRangeException(nameof(BlurHigh), BlurHigh, "The high edge threshold must be above 0 and at most 1.");
        }

        if (BlurLow < 0 || BlurLow > BlurHigh || double.IsNaN(BlurLow))
        {
            throw new ArgumentOutOfRangeException(nameof(BlurLow), BlurLow, "The low edge threshold must be from 0 to the high threshold.");
        }

        if (BlurBlockShare is <= 0 or > 1 || double.IsNaN(BlurBlockShare))
        {
            throw new ArgumentOutOfRangeException(nameof(BlurBlockShare), BlurBlockShare, "The block share must be above 0 and at most 1.");
        }

        if (BlockPeriodMin < 2 || BlockPeriodMax < BlockPeriodMin)
        {
            throw new ArgumentOutOfRangeException(nameof(BlockPeriodMin), BlockPeriodMin, "The block sizes must be 2 or more, smallest first.");
        }
    }
}

/// <summary>What <see cref="SignalMeter"/> measured over every frame.</summary>
/// <param name="Frames">How many frames were measured.</param>
/// <param name="BitDepth">The samples' bit depth; code values below are on its scale.</param>
/// <param name="Y">Luma levels.</param>
/// <param name="Cb">Blue-difference chroma levels.</param>
/// <param name="Cr">Red-difference chroma levels.</param>
/// <param name="Saturation">Chroma saturation: the distance of (Cb, Cr) from the no-color middle, in code values.</param>
/// <param name="OutOfRange">The share of pixels, from 0 to 1, whose luma or chroma is outside broadcast range.</param>
/// <param name="Range">Which range the samples really use.</param>
/// <param name="Alpha">Whether and how the alpha plane is used.</param>
/// <param name="Blur">The mean edge width in pixels, with <see cref="SignalOptions.Blur"/>; higher is blurrier.</param>
/// <param name="Blockiness">How much stronger steps on a block grid are than steps between it, with <see cref="SignalOptions.Blockiness"/>; 1 is none.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-signal"/>
/// </example>
public sealed record SignalReport(long Frames, int BitDepth, PlaneLevels Y, PlaneLevels Cb, PlaneLevels Cr, PlaneLevels Saturation, double OutOfRange, SignalRange Range, AlphaUse Alpha, double? Blur, double? Blockiness);

/// <summary>One frame's measures from <see cref="SignalMeter"/>.</summary>
/// <param name="Frame">The frame, counted from 0.</param>
/// <param name="Time">The frame's timestamp.</param>
/// <param name="Y">Luma levels.</param>
/// <param name="Cb">Blue-difference chroma levels.</param>
/// <param name="Cr">Red-difference chroma levels.</param>
/// <param name="Saturation">Chroma saturation levels.</param>
/// <param name="OutOfRange">The share of pixels outside broadcast range.</param>
/// <param name="Blur">The mean edge width, when measured and the frame has edges.</param>
/// <param name="Blockiness">The blockiness, when measured.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-signal"/>
/// </example>
public sealed record SignalFrame(long Frame, MediaTime Time, PlaneLevels Y, PlaneLevels Cb, PlaneLevels Cr, PlaneLevels Saturation, double OutOfRange, double? Blur, double? Blockiness);

/// <summary>The spread of one plane's code values.</summary>
/// <param name="Min">The lowest value.</param>
/// <param name="Low">The 10th percentile.</param>
/// <param name="Mean">The mean.</param>
/// <param name="High">The 90th percentile.</param>
/// <param name="Max">The highest value.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-signal"/>
/// </example>
public readonly record struct PlaneLevels(double Min, double Low, double Mean, double High, double Max);

/// <summary>Which range a video's samples really use.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-signal"/>
/// </example>
public enum SignalRange
{
    /// <summary>Every sample is within the limited (video) range, so the video may be limited range whatever it is labeled.</summary>
    Limited,

    /// <summary>Some samples are outside the limited range: the video uses the full range (or is out of spec).</summary>
    Full,
}

/// <summary>How a video's alpha plane is used.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-signal"/>
/// </example>
public enum AlphaUse
{
    /// <summary>The frames have no alpha plane.</summary>
    None,

    /// <summary>The alpha plane is opaque everywhere, so it could be dropped.</summary>
    Opaque,

    /// <summary>Some see-through pixels are brighter than their alpha allows premultiplied color to be: straight alpha.</summary>
    Straight,

    /// <summary>No see-through pixel is brighter than its alpha: likely premultiplied.</summary>
    Premultiplied,
}
