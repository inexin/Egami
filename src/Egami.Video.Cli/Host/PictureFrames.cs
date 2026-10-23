using System.Globalization;

using Egami.Cli.Steps;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Video.Cli.Host;

/// <summary>
/// What the picture commands share: the frame shown at a time, a rate on the command line, egami's size syntax, and a small
/// summary of a frame (a grid of mean lumas and a color histogram) for choosing and comparing frames.
/// </summary>
internal static class PictureFrames
{
    /// <summary>The width of the luma grid a frame is summarized by.</summary>
    public const int GridWidth = 32;

    /// <summary>The height of the luma grid a frame is summarized by.</summary>
    public const int GridHeight = 18;

    /// <summary>The bins of the color histogram: 4 levels of red, green and blue.</summary>
    public const int HistogramBins = 64;

    /// <summary>A small margin for comparing timestamps given as seconds.</summary>
    private const double Epsilon = 1e-6;

    /// <summary>Parses a frame rate: 25, 29.97 or 30000/1001, above 0 and at most 1000.</summary>
    public static Rational ParseRate(string text, string option)
    {
        Rational rate;
        try
        {
            rate = text.Contains('/', StringComparison.Ordinal)
                ? Rational.Parse(text)
                : double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value)
                    ? new Rational((long)Math.Round(value * 1000), 1000)
                    : throw new FormatException();
        }
        catch (FormatException)
        {
            throw new UsageException($"{option} '{text}' is not a frame rate: give frames per second, such as 10, 29.97 or 30000/1001.");
        }

        double fps = rate.ToDouble();
        return fps > 0 && fps <= 1000 ? Reduce(rate) : throw new UsageException($"{option} must be above 0 and at most 1000 frames per second.");
    }

    /// <summary>Parses egami's size syntax (W, xH, WxH, P%, with !, ^, &gt; or &lt;).</summary>
    public static SizeSpec ParseSize(string text) => SizeSpec.Parse(text);

    /// <summary>Resizes a frame to a size in egami's size syntax, as <c>egami resize</c> does; returns the frame itself when the size does not change.</summary>
    public static Raster<Rgba8> Resize(Raster<Rgba8> image, SizeSpec? size)
    {
        if (size is null)
        {
            return image;
        }

        Size source = new(image.Width, image.Height);
        image.Apply(ops => BuiltinSteps.AddResize(ops, size, FitMode.Contain, Anchor.Center, ResampleFilter.Bicubic, false, default, source));
        return image;
    }

    /// <summary>The length of an input's video in seconds, or <see langword="null"/> when its headers do not say.</summary>
    public static double? Duration(FrameSource source)
    {
        MediaTime? duration = source.Info.Duration is { Ticks: > 0 } d ? d : source.Track.Duration is { Ticks: > 0 } t ? t : null;
        return duration?.Seconds;
    }

    /// <summary>
    /// Calls <paramref name="take"/> with the frame shown at each of <paramref name="times"/> (seconds, in increasing order): the
    /// last frame starting at or before the time, the first frame for a time before it and the last one for a time after the
    /// end. A seekable input seeks to each time, as <see cref="VideoReader.Seek(MediaTime)"/> does; standard input is decoded
    /// through. The frames belong to this method; <paramref name="take"/> must not keep them.
    /// </summary>
    public static void AtTimes(FrameSource source, IReadOnlyList<double> times, JobProgress progress, Action<int, YuvVideoFrame> take)
    {
        if (times.Count == 0)
        {
            return;
        }

        if (source.Reader.CanSeek)
        {
            YuvVideoFrame? last = null;
            try
            {
                for (int k = 0; k < times.Count; k++)
                {
                    source.Reader.Seek(MediaTime.FromSeconds(times[k]));
                    if (source.Reader.ReadFrame() is { } frame)
                    {
                        last?.Dispose();
                        last = frame;
                        progress.Frame(frame.Timestamp);
                    }

                    if (last is null)
                    {
                        return;
                    }

                    take(k, last);
                }
            }
            finally
            {
                last?.Dispose();
            }

            return;
        }

        YuvVideoFrame? held = null;
        int next = 0;
        try
        {
            foreach (YuvVideoFrame frame in source.Frames())
            {
                progress.Frame(frame.Timestamp);
                while (next < times.Count && frame.Timestamp.Seconds > times[next] + Epsilon)
                {
                    take(next++, held ?? frame);
                }

                held?.Dispose();
                held = frame;
                if (next == times.Count)
                {
                    break;
                }
            }

            while (held is not null && next < times.Count)
            {
                take(next++, held);
            }
        }
        finally
        {
            held?.Dispose();
        }
    }

    /// <summary>Summarizes a frame: its grid of mean lumas, its color histogram, and the mean and spread of its luma, from 0 to 1.</summary>
    public static FrameSummary Summarize(Raster<Rgba8> image)
    {
        int width = image.Width, height = image.Height;
        double[] sums = new double[GridWidth * GridHeight];
        int[] counts = new int[GridWidth * GridHeight];
        double[] histogram = new double[HistogramBins];

        // Every pixel counts in the grid; the histogram and the spread take a lattice of about 65,536 of them.
        int step = Math.Max(1, (int)Math.Sqrt((double)width * height / 65536));
        double lumaSum = 0, lumaSquares = 0;
        long sampled = 0;
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = image.Row(y);
            int gy = y * GridHeight / height;
            bool sampleRow = y % step == 0;
            for (int x = 0; x < width; x++)
            {
                Rgba8 p = row[x];
                int luma = ((p.R * 77) + (p.G * 150) + (p.B * 29)) >> 8;
                int cell = (gy * GridWidth) + (x * GridWidth / width);
                sums[cell] += luma;
                counts[cell]++;
                if (sampleRow && x % step == 0)
                {
                    histogram[((p.R >> 6) << 4) | ((p.G >> 6) << 2) | (p.B >> 6)]++;
                    lumaSum += luma;
                    lumaSquares += (double)luma * luma;
                    sampled++;
                }
            }
        }

        double[] grid = new double[sums.Length];
        for (int i = 0; i < grid.Length; i++)
        {
            grid[i] = counts[i] > 0 ? sums[i] / counts[i] / 255 : 0;
        }

        for (int i = 0; i < histogram.Length; i++)
        {
            histogram[i] /= Math.Max(1, sampled);
        }

        double mean = lumaSum / Math.Max(1, sampled);
        double spread = Math.Sqrt(Math.Max(0, (lumaSquares / Math.Max(1, sampled)) - (mean * mean)));
        return new FrameSummary(grid, histogram, mean / 255, spread / 255);
    }

    private static Rational Reduce(Rational rate)
    {
        long a = Math.Abs(rate.Numerator), b = rate.Denominator;
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a > 1 ? new Rational(rate.Numerator / a, rate.Denominator / a) : rate;
    }
}

/// <summary>A frame in brief.</summary>
/// <param name="Grid">The mean luma of each cell of a <see cref="PictureFrames.GridWidth"/> by <see cref="PictureFrames.GridHeight"/> grid, from 0 to 1.</param>
/// <param name="Histogram">The share of pixels in each of <see cref="PictureFrames.HistogramBins"/> color bins.</param>
/// <param name="Mean">The mean luma, from 0 to 1.</param>
/// <param name="Spread">The standard deviation of the luma, from 0 to 1.</param>
internal sealed record FrameSummary(double[] Grid, double[] Histogram, double Mean, double Spread);
