using Egami.Pixels;

namespace Egami.Video.Analysis;

/// <summary>A stretch of time a detector found: black, frozen, duplicated or silent.</summary>
/// <param name="Start">When it starts: the timestamp of its first frame.</param>
/// <param name="End">When it ends: the timestamp of the first frame after it, or the end of the video.</param>
/// <param name="Value">
/// The detector's measure for the range: the lowest black ratio for <see cref="BlackDetector"/>, the largest difference for
/// <see cref="FreezeDetector"/>, the number of repeated frames for <see cref="DuplicateDetector"/>.
/// </param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-video-detectors"/>
/// </example>
public sealed record DetectedRange(MediaTime Start, MediaTime End, double Value)
{
    /// <summary>Gets the range's length.</summary>
    public MediaTime Duration => End - Start;
}

/// <summary>One frame's value in a detector's series, kept when the detector's options ask for it.</summary>
/// <param name="Frame">The frame, counted from 0 among the frames the detector saw.</param>
/// <param name="Time">The frame's timestamp.</param>
/// <param name="Value">The detector's measure for the frame.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-video-detectors"/>
/// </example>
public readonly record struct FrameValue(long Frame, MediaTime Time, double Value);

/// <summary>Collects ranges of frames that pass a test, keeping those that last long enough.</summary>
internal sealed class RangeBuilder(MediaTime minDuration)
{
    private readonly List<DetectedRange> _ranges = [];
    private readonly double _min = minDuration.Seconds;
    private MediaTime _start;
    private double _value;

    public IReadOnlyList<DetectedRange> Ranges => _ranges;

    public bool Open { get; private set; }

    public MediaTime Start => _start;

    /// <summary>Starts a range at <paramref name="time"/>, or widens the open one's value with <paramref name="combine"/>.</summary>
    public void Extend(MediaTime time, double value, Func<double, double, double> combine)
    {
        if (!Open)
        {
            Open = true;
            _start = time;
            _value = value;
        }
        else
        {
            _value = combine(_value, value);
        }
    }

    /// <summary>Sets the open range's value.</summary>
    public void SetValue(double value) => _value = value;

    /// <summary>Ends the open range at <paramref name="end"/>, keeping it when it is long enough.</summary>
    public void Close(MediaTime end)
    {
        if (!Open)
        {
            return;
        }

        Open = false;
        if (end.Seconds - _start.Seconds >= _min - 1e-9)
        {
            _ranges.Add(new DetectedRange(_start, end, _value));
        }
    }
}

/// <summary>The code values that mark black, white and the chroma middle for a stream's bit depth and range.</summary>
internal readonly struct VideoLevels
{
    public VideoLevels(YuvRaster raster)
    {
        BitDepth = raster.BitDepth;
        Limited = raster.Color.Range == YuvRange.Limited;
        Max = (1 << BitDepth) - 1;
        int shift = BitDepth - 8;
        Black = Limited ? 16 << shift : 0;
        White = Limited ? 235 << shift : Max;
        ChromaLow = 16 << shift;
        ChromaHigh = 240 << shift;
        Middle = 1 << (BitDepth - 1);
        Scale8 = 1.0 / (1 << shift);
    }

    public int BitDepth { get; }

    public bool Limited { get; }

    public int Max { get; }

    /// <summary>The luma code of black: 16 scaled to the bit depth for limited range, else 0.</summary>
    public int Black { get; }

    /// <summary>The luma code of white: 235 scaled to the bit depth for limited range, else the largest code.</summary>
    public int White { get; }

    /// <summary>The broadcast-range floor for chroma, 16 scaled to the bit depth.</summary>
    public int ChromaLow { get; }

    /// <summary>The broadcast-range ceiling for chroma, 240 scaled to the bit depth.</summary>
    public int ChromaHigh { get; }

    /// <summary>The chroma code of no color.</summary>
    public int Middle { get; }

    /// <summary>The factor that brings a code value to the 8-bit scale.</summary>
    public double Scale8 { get; }

    /// <summary>The broadcast-range luma floor, 16 scaled to the bit depth, whatever the stream's range.</summary>
    public int BroadcastBlack => 16 << (BitDepth - 8);

    /// <summary>The broadcast-range luma ceiling, 235 scaled to the bit depth.</summary>
    public int BroadcastWhite => 235 << (BitDepth - 8);
}
