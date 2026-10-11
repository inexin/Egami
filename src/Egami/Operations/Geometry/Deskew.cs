using System.Buffers;
using System.Numerics;

using Egami.Pixels;

namespace Egami.Operations.Geometry;

/// <summary>
/// Finds the tilt of text lines (or other strong horizontal structure) by projection profiles (Postl 1986): a gray copy about
/// 1,500 pixels on its long side is binarized with Otsu's threshold (Otsu 1979), and every candidate angle is scored by the
/// variance of the row sums of the ink projected along it, which is sharpest when the projection follows the lines.
/// </summary>
internal static class SkewDetector
{
    /// <summary>The long side of the working copy, in pixels.</summary>
    internal const int WorkingSize = 1500;

    /// <summary>The coarse search step and the fine one around the best coarse angle, in degrees.</summary>
    private const double Coarse = 0.5, Fine = 0.05;

    /// <summary>Returns the tilt in degrees clockwise, or 0 when no angle stands out from level.</summary>
    internal static double Detect<TPixel>(PixelPlane<TPixel> plane, double maxAngle, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        (byte[] gray, int width, int height) = Downscale(plane);
        try
        {
            int count = width * height;
            byte threshold = Otsu(gray.AsSpan(0, count));
            List<(float X, float Y)> ink = Ink(gray.AsSpan(0, count), width, height, threshold);
            if (ink.Count < 16 || ink.Count > count * 0.6)
            {
                return 0;
            }

            int steps = (int)Math.Round(maxAngle / Coarse);
            double best = 0, bestScore = double.MinValue, level = 0;
            List<double> scores = new(2 * steps + 1);
            for (int i = -steps; i <= steps; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double angle = i * Coarse;
                double score = Score(ink, angle, width, height);
                scores.Add(score);
                if (i == 0)
                {
                    level = score;
                }

                if (score > bestScore)
                {
                    (best, bestScore) = (angle, score);
                }
            }

            // Structure shows as one angle standing well above the rest; noise and pictures score about the same everywhere.
            scores.Sort();
            double median = scores[scores.Count / 2];
            if (!(bestScore > median * 1.25))
            {
                return 0;
            }

            double center = best;
            for (double angle = center - Coarse; angle <= center + Coarse + 1e-9; angle += Fine)
            {
                double score = Score(ink, angle, width, height);
                if (score > bestScore)
                {
                    (best, bestScore) = (angle, score);
                }
            }

            best = Math.Clamp(Math.Round(best / Fine) * Fine, -maxAngle, maxAngle);
            return bestScore > level * 1.005 ? best : 0;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(gray);
        }
    }

    /// <summary>
    /// Returns a gray copy reduced by a whole factor so its long side is at most <see cref="WorkingSize"/>, each pixel the mean
    /// luma of its block, with transparent pixels counting as white paper.
    /// </summary>
    private static (byte[] Gray, int Width, int Height) Downscale<TPixel>(PixelPlane<TPixel> plane)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int factor = Math.Max(1, (int)Math.Ceiling(Math.Max(plane.Width, plane.Height) / (double)WorkingSize));
        int width = (plane.Width + factor - 1) / factor, height = (plane.Height + factor - 1) / factor;
        byte[] gray = ArrayPool<byte>.Shared.Rent(width * height);
        int[] sums = ArrayPool<int>.Shared.Rent(width);
        int[] counts = ArrayPool<int>.Shared.Rent(width);
        Rgba8[] row = ArrayPool<Rgba8>.Shared.Rent(plane.Width);
        try
        {
            for (int gy = 0; gy < height; gy++)
            {
                Array.Clear(sums, 0, width);
                Array.Clear(counts, 0, width);
                for (int y = gy * factor; y < Math.Min(plane.Height, (gy + 1) * factor); y++)
                {
                    Span<Rgba8> pixels = row.AsSpan(0, plane.Width);
                    PixelConvert.Convert<TPixel, Rgba8>(plane.Row(y), pixels);
                    for (int x = 0; x < pixels.Length; x++)
                    {
                        Rgba8 p = pixels[x];
                        int luma = ((54 * p.R) + (183 * p.G) + (19 * p.B) + 128) >> 8;
                        sums[x / factor] += ((luma * p.A) + (255 * (255 - p.A)) + 127) / 255;
                        counts[x / factor]++;
                    }
                }

                for (int x = 0; x < width; x++)
                {
                    gray[(gy * width) + x] = (byte)((sums[x] + (counts[x] / 2)) / counts[x]);
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(sums);
            ArrayPool<int>.Shared.Return(counts);
            ArrayPool<Rgba8>.Shared.Return(row);
        }

        return (gray, width, height);
    }

    /// <summary>Otsu's threshold: the level that maximizes the variance between the two classes it splits the histogram into.</summary>
    internal static byte Otsu(ReadOnlySpan<byte> gray)
    {
        Span<long> histogram = stackalloc long[256];
        foreach (byte g in gray)
        {
            histogram[g]++;
        }

        double total = gray.Length, sumAll = 0;
        for (int i = 0; i < 256; i++)
        {
            sumAll += i * (double)histogram[i];
        }

        double below = 0, sumBelow = 0, bestVariance = -1;
        int best = 0;
        for (int t = 0; t < 256; t++)
        {
            below += histogram[t];
            sumBelow += t * (double)histogram[t];
            double above = total - below;
            if (below == 0 || above == 0)
            {
                continue;
            }

            double meanBelow = sumBelow / below, meanAbove = (sumAll - sumBelow) / above;
            double variance = below * above * (meanBelow - meanAbove) * (meanBelow - meanAbove);
            if (variance > bestVariance)
            {
                (bestVariance, best) = (variance, t);
            }
        }

        return (byte)best;
    }

    /// <summary>The positions of the ink: the pixels on the minority side of the threshold (dark text on light paper, or the reverse).</summary>
    private static List<(float X, float Y)> Ink(ReadOnlySpan<byte> gray, int width, int height, byte threshold)
    {
        int dark = 0;
        foreach (byte g in gray)
        {
            dark += g <= threshold ? 1 : 0;
        }

        bool inkIsDark = dark <= gray.Length / 2;
        List<(float X, float Y)> ink = [];
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = gray.Slice(y * width, width);
            for (int x = 0; x < width; x++)
            {
                if ((row[x] <= threshold) == inkIsDark)
                {
                    ink.Add((x - (width / 2f), y - (height / 2f)));
                }
            }
        }

        return ink;
    }

    /// <summary>
    /// The variance-like sharpness of the projection profile at <paramref name="degrees"/> clockwise: the sum of the squared row
    /// counts, which for a fixed amount of ink is largest when the ink piles up in few rows.
    /// </summary>
    private static double Score(List<(float X, float Y)> ink, double degrees, int width, int height)
    {
        double radians = degrees * Math.PI / 180;
        float sin = (float)Math.Sin(radians), cos = (float)Math.Cos(radians);
        int reach = (int)Math.Ceiling(Math.Sqrt(((double)width * width) + ((double)height * height)) / 2) + 2;
        int[] bins = ArrayPool<int>.Shared.Rent((2 * reach) + 1);
        try
        {
            Span<int> profile = bins.AsSpan(0, (2 * reach) + 1);
            profile.Clear();
            foreach ((float x, float y) in ink)
            {
                // The row a point falls in once the image is turned back by the angle.
                profile[reach + (int)MathF.Floor((y * cos) - (x * sin))]++;
            }

            double sum = 0;
            foreach (int count in profile)
            {
                sum += (double)count * count;
            }

            return sum;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(bins);
        }
    }
}

/// <summary>
/// Deskew: detects the tilt of the first frame and turns every frame back by it, keeping the page size (corners in the border
/// color) or growing the canvas as Rotate does.
/// </summary>
internal sealed class DeskewOperation(float maxAngle, Rgba8? background, bool keepSize, ResampleFilter filter) : IMeasuredStep
{
    public Size OutputSize(Size input) => throw new InvalidOperationException("Deskew is measured, so it is never sized up front.");

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        throw new InvalidOperationException("Deskew is measured, so it never runs itself.");
    }

    public IRasterOperation Measure<TPixel>(IReadOnlyList<PixelPlane<TPixel>> frames, EgamiContext context, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelPlane<TPixel> first = frames[0];
        double angle = SkewDetector.Detect(first, maxAngle, cancellationToken);
        if (angle == 0)
        {
            return KeepOperation.Instance;
        }

        Rgba8 fill = background ?? first.Row(0)[0].ToRgba8();
        if (!keepSize)
        {
            return new RotateOperation(-angle, filter, fill);
        }

        Size size = new(first.Width, first.Height);
        Homography turn = Homography.CreateRotation(-angle * Math.PI / 180, new Vector2(size.Width / 2f, size.Height / 2f));
        return new TransformOperation(_ => turn, new TransformOptions { Filter = filter, Background = fill, Viewport = new Rect(0, 0, size.Width, size.Height) });
    }
}
