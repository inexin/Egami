using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Video.Analysis;

/// <summary>
/// Finds repeated frames with ffmpeg mpdecimate's test, without dropping anything: a frame repeats the last kept frame when none
/// of its 8 by 8 luma blocks differs by more than <see cref="DuplicateOptions.High"/> and no more than
/// <see cref="DuplicateOptions.Fraction"/> of them differ by more than <see cref="DuplicateOptions.Low"/>. A block's difference is
/// the mean absolute difference of its samples as a share of the range, so the thresholds mean the same at every bit depth.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-video-detectors"/>
/// </example>
public sealed class DuplicateDetector : IVideoDetector
{
    private readonly DuplicateOptions _options;
    private readonly RangeBuilder _ranges = new(MediaTime.Zero);
    private ushort[] _kept = [];
    private byte[] _kept8 = [];
    private int _width;
    private int _height;
    private int _depth;
    private long _count;
    private long _duplicates;
    private long _run;
    private MediaTime _lastEnd;
    private bool _complete;

    /// <summary>Creates a detector.</summary>
    /// <param name="options">The thresholds, or <see langword="null"/> for <see cref="DuplicateOptions.Default"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-video-detectors"/>
    /// </example>
    public DuplicateDetector(DuplicateOptions? options = null)
    {
        _options = options ?? DuplicateOptions.Default;
        _options.Validate();
    }

    /// <summary>Gets the options the detector runs with.</summary>
    public DuplicateOptions Options => _options;

    /// <summary>Gets the runs of repeated frames found so far; each range's value is how many frames it holds.</summary>
    public IReadOnlyList<DetectedRange> Ranges => _ranges.Ranges;

    /// <summary>Gets how many frames the detector has taken.</summary>
    public long FrameCount => _count;

    /// <summary>Gets how many frames repeated the last kept frame.</summary>
    public long DuplicateCount => _duplicates;

    /// <summary>Gets whether the last frame repeated the last kept frame.</summary>
    public bool LastWasDuplicate { get; private set; }

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

        _count++;
        _lastEnd = frame.Duration is { } duration ? frame.Timestamp + duration : frame.Timestamp;
        YuvRaster raster = frame.Raster;
        bool fresh = raster.Width != _width || raster.Height != _height || raster.BitDepth != _depth;
        bool duplicate = !fresh && raster switch
        {
            YuvRaster<byte> bytes => Repeats(bytes.Y),
            YuvRaster<ushort> words => Repeats(words.Y),
            _ => throw new NotSupportedException($"Planes of {raster.GetType().Name} are not supported."),
        };
        LastWasDuplicate = duplicate;
        if (duplicate)
        {
            _duplicates++;
            _run++;
            _ranges.Extend(frame.Timestamp, _run, static (_, run) => run);
            return;
        }

        _ranges.Close(frame.Timestamp);
        _run = 0;
        _width = raster.Width;
        _height = raster.Height;
        _depth = raster.BitDepth;
        switch (raster)
        {
            case YuvRaster<byte> bytes:
                if (_kept8.Length != _width * _height)
                {
                    _kept8 = new byte[_width * _height];
                }

                for (int y = 0; y < _height; y++)
                {
                    bytes.Y.Row(y)[.._width].CopyTo(_kept8.AsSpan(y * _width, _width));
                }

                break;
            case YuvRaster<ushort> words:
                if (_kept.Length != _width * _height)
                {
                    _kept = new ushort[_width * _height];
                }

                Keep(words.Y);
                break;
        }
    }

    /// <summary>Ends a run that lasts to the end of the video.</summary>
    /// <param name="endTime">When the video ends.</param>
    public void Complete(MediaTime endTime)
    {
        if (_complete)
        {
            return;
        }

        _complete = true;
        _ranges.Close(endTime > _lastEnd ? endTime : _lastEnd);
    }

    private void Keep<T>(YuvPlane<T> plane)
        where T : unmanaged, IBinaryInteger<T>
    {
        for (int y = 0; y < _height; y++)
        {
            ReadOnlySpan<T> row = plane.Row(y)[.._width];
            Span<ushort> to = _kept.AsSpan(y * _width, _width);
            for (int x = 0; x < _width; x++)
            {
                to[x] = ushort.CreateTruncating(row[x]);
            }
        }
    }

    private bool Repeats<T>(YuvPlane<T> plane)
        where T : unmanaged, IBinaryInteger<T>
    {
        if (typeof(T) == typeof(byte))
        {
            return Repeats8((YuvPlane<byte>)(object)plane);
        }

        int shift = _depth - 8;
        long high = BlockSum(_options.High) << shift, low = BlockSum(_options.Low) << shift;
        int across = _width / 8, down = _height / 8;
        long blocks = (long)across * down;
        if (blocks == 0)
        {
            return false;
        }

        long allowed = (long)Math.Floor(_options.Fraction * blocks);
        long overLow = 0;
        for (int by = 0; by < down; by++)
        {
            for (int bx = 0; bx < across; bx++)
            {
                long sum = 0;
                for (int y = by * 8; y < (by * 8) + 8; y++)
                {
                    ReadOnlySpan<T> row = plane.Row(y).Slice(bx * 8, 8);
                    ReadOnlySpan<ushort> kept = _kept.AsSpan((y * _width) + (bx * 8), 8);
                    for (int x = 0; x < 8; x++)
                    {
                        int d = int.CreateTruncating(row[x]) - kept[x];
                        sum += d < 0 ? -d : d;
                    }
                }

                if (sum > high)
                {
                    return false;
                }

                if (sum > low && ++overLow > allowed)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// The 8-bit test, a band of 8 rows at a time: vpsadbw sums each block row of 8 samples in one lane, so four blocks side by
    /// side take one instruction per row.
    /// </summary>
    private bool Repeats8(YuvPlane<byte> plane)
    {
        int across = _width / 8, down = _height / 8;
        long blocks = (long)across * down;
        if (blocks == 0)
        {
            return false;
        }

        long high = BlockSum(_options.High), low = BlockSum(_options.Low);
        long allowed = (long)Math.Floor(_options.Fraction * blocks);
        long overLow = 0;
        Span<ulong> sums = stackalloc ulong[4];
        for (int by = 0; by < down; by++)
        {
            int bx = 0;
            if (Avx2.IsSupported)
            {
                for (; bx <= across - 4; bx += 4)
                {
                    Vector256<ulong> sum = Vector256<ulong>.Zero;
                    for (int y = by * 8; y < (by * 8) + 8; y++)
                    {
                        Vector256<byte> a = Vector256.Create(plane.Row(y).Slice(bx * 8, 32));
                        Vector256<byte> b = Vector256.Create(_kept8.AsSpan((y * _width) + (bx * 8), 32));
                        sum += Avx2.SumAbsoluteDifferences(a, b).AsUInt64();
                    }

                    sum.CopyTo(sums);
                    for (int k = 0; k < 4; k++)
                    {
                        if (!Allowed((long)sums[k], high, low, allowed, ref overLow))
                        {
                            return false;
                        }
                    }
                }
            }

            for (; bx < across; bx++)
            {
                long sum = 0;
                for (int y = by * 8; y < (by * 8) + 8; y++)
                {
                    ReadOnlySpan<byte> row = plane.Row(y).Slice(bx * 8, 8);
                    ReadOnlySpan<byte> kept = _kept8.AsSpan((y * _width) + (bx * 8), 8);
                    for (int x = 0; x < 8; x++)
                    {
                        int d = row[x] - kept[x];
                        sum += d < 0 ? -d : d;
                    }
                }

                if (!Allowed(sum, high, low, allowed, ref overLow))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>A share of the range as the sum of 64 absolute differences on the 8-bit scale, the unit mpdecimate's test counts in.</summary>
    private static long BlockSum(double share) => (long)Math.Round(share * 64 * 255);

    private static bool Allowed(long sum, long high, long low, long allowed, ref long overLow) =>
        sum <= high && (sum <= low || ++overLow <= allowed);
}

/// <summary>What <see cref="DuplicateDetector"/> counts as a repeat. The defaults are ffmpeg mpdecimate's.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-video-detectors"/>
/// </example>
public sealed record DuplicateOptions
{
    /// <summary>Gets the default options.</summary>
    public static DuplicateOptions Default { get; } = new();

    /// <summary>
    /// Gets the block difference, from 0 to 1, that any one block may not pass: the mean absolute difference of its 64 samples as a
    /// share of the range. Default 12/255, as mpdecimate's hi of 768 (64 times 12 on the 8-bit scale).
    /// </summary>
    public double High { get; init; } = 12.0 / 255;

    /// <summary>
    /// Gets the block difference, from 0 to <see cref="High"/>, that only <see cref="Fraction"/> of blocks may pass. Default 5/255, as
    /// mpdecimate's lo of 320 (64 times 5).
    /// </summary>
    public double Low { get; init; } = 5.0 / 255;

    /// <summary>Gets the share of blocks, from 0 to 1, that may pass <see cref="Low"/>. Default 0.33, as mpdecimate's frac.</summary>
    public double Fraction { get; init; } = 0.33;

    internal void Validate()
    {
        if (High is < 0 or > 1 || double.IsNaN(High))
        {
            throw new ArgumentOutOfRangeException(nameof(High), High, "The high threshold must be from 0 to 1.");
        }

        if (Low < 0 || Low > High || double.IsNaN(Low))
        {
            throw new ArgumentOutOfRangeException(nameof(Low), Low, "The low threshold must be from 0 to the high threshold.");
        }

        if (Fraction is < 0 or > 1 || double.IsNaN(Fraction))
        {
            throw new ArgumentOutOfRangeException(nameof(Fraction), Fraction, "The fraction must be from 0 to 1.");
        }
    }
}
