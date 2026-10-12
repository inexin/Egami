using System.Buffers;

using Egami.Operations;

namespace Egami.Analysis;

/// <summary>
/// Computes <see cref="RasterStatistics"/> in two passes over straight rows in double precision: sums, extremes and a 16-bit histogram per
/// channel, then the central moments about the exact mean. Bands run in parallel and their sums are added in band order, so the
/// result does not depend on the thread count.
/// </summary>
internal static class StatisticsMeter
{
    /// <summary>Red, green, blue, alpha and luma.</summary>
    private const int Channels = 5;

    private const int Levels = 65536;

    internal static RasterStatistics Measure(Raster raster, int frame, CancellationToken cancellationToken)
    {
        int width = raster.Width, height = raster.Height;
        long rowBytes = (long)width * 16;
        int bandRows = RowRunner.BandRows(rowBytes);
        int bands = (height + bandRows - 1) / bandRows;
        double[][] firstPass = new double[bands][];
        long[] histogram = new long[Channels * Levels];
        object gate = new();
        int parallelism = raster.Context.MaxParallelism;

        // Pass 1: Σx, min and max per channel, and how often each 16-bit level occurs.
        RowRunner.Run(height, rowBytes, parallelism, (start, end) =>
        {
            double[] sums = new double[Channels * 3];
            for (int c = 0; c < Channels; c++)
            {
                sums[(c * 3) + 1] = double.MaxValue;
                sums[(c * 3) + 2] = double.MinValue;
            }

            int[] counts = ArrayPool<int>.Shared.Rent(Channels * Levels);
            RgbaD[] buffer = ArrayPool<RgbaD>.Shared.Rent(width);
            using RowReader reader = new(raster, frame, width);
            try
            {
                counts.AsSpan(0, Channels * Levels).Clear();
                Span<double> values = stackalloc double[Channels];
                Span<double> rowSums = stackalloc double[Channels];
                for (int y = start; y < end; y++)
                {
                    Span<RgbaD> row = buffer.AsSpan(0, width);
                    reader.Read(y, 0, row);
                    rowSums.Clear();
                    foreach (RgbaD p in row)
                    {
                        Values(p, values);
                        for (int c = 0; c < Channels; c++)
                        {
                            double v = values[c];
                            rowSums[c] += v;
                            sums[(c * 3) + 1] = Math.Min(sums[(c * 3) + 1], v);
                            sums[(c * 3) + 2] = Math.Max(sums[(c * 3) + 2], v);
                            counts[(c * Levels) + Level(v)]++;
                        }
                    }

                    for (int c = 0; c < Channels; c++)
                    {
                        sums[c * 3] += rowSums[c];
                    }
                }

                lock (gate)
                {
                    for (int i = 0; i < Channels * Levels; i++)
                    {
                        histogram[i] += counts[i];
                    }
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(counts);
                ArrayPool<RgbaD>.Shared.Return(buffer);
            }

            firstPass[start / bandRows] = sums;
        }, cancellationToken);

        long n = (long)width * height;
        double[] mean = new double[Channels];
        double[] min = new double[Channels];
        double[] max = new double[Channels];
        Array.Fill(min, double.MaxValue);
        Array.Fill(max, double.MinValue);
        foreach (double[] band in firstPass)
        {
            for (int c = 0; c < Channels; c++)
            {
                mean[c] += band[c * 3];
                min[c] = Math.Min(min[c], band[(c * 3) + 1]);
                max[c] = Math.Max(max[c], band[(c * 3) + 2]);
            }
        }

        for (int c = 0; c < Channels; c++)
        {
            mean[c] /= n;
        }

        // Pass 2: Σ(x − mean)², ³ and ⁴ per channel.
        double[][] secondPass = new double[bands][];
        RowRunner.Run(height, rowBytes, parallelism, (start, end) =>
        {
            double[] moments = new double[Channels * 3];
            RgbaD[] buffer = ArrayPool<RgbaD>.Shared.Rent(width);
            using RowReader reader = new(raster, frame, width);
            try
            {
                Span<double> values = stackalloc double[Channels];
                Span<double> rowMoments = stackalloc double[Channels * 3];
                for (int y = start; y < end; y++)
                {
                    Span<RgbaD> row = buffer.AsSpan(0, width);
                    reader.Read(y, 0, row);
                    rowMoments.Clear();
                    foreach (RgbaD p in row)
                    {
                        Values(p, values);
                        for (int c = 0; c < Channels; c++)
                        {
                            double d = values[c] - mean[c];
                            double d2 = d * d;
                            rowMoments[c * 3] += d2;
                            rowMoments[(c * 3) + 1] += d2 * d;
                            rowMoments[(c * 3) + 2] += d2 * d2;
                        }
                    }

                    for (int i = 0; i < Channels * 3; i++)
                    {
                        moments[i] += rowMoments[i];
                    }
                }
            }
            finally
            {
                ArrayPool<RgbaD>.Shared.Return(buffer);
            }

            secondPass[start / bandRows] = moments;
        }, cancellationToken);

        double[] central = new double[Channels * 3];
        foreach (double[] band in secondPass)
        {
            for (int i = 0; i < Channels * 3; i++)
            {
                central[i] += band[i];
            }
        }

        ChannelStatistics Channel(int c)
        {
            // A constant channel has no spread, whatever rounding the mean picked up.
            double variance = n > 1 && min[c] != max[c] ? central[c * 3] / (n - 1) : 0;
            double sd = Math.Sqrt(variance);
            double m3 = central[(c * 3) + 1] / n;
            double m4 = central[(c * 3) + 2] / n;
            return new ChannelStatistics
            {
                Min = min[c],
                Max = max[c],
                Mean = mean[c],
                StdDev = sd,
                Skewness = variance > 0 ? m3 / (variance * sd) : 0,
                Kurtosis = variance > 0 ? (m4 / (variance * variance)) - 3 : 0,
                Entropy = Entropy(histogram.AsSpan(c * Levels, Levels), n),
            };
        }

        return new RasterStatistics(Channel(0), Channel(1), Channel(2), Channel(3), Channel(4), n);
    }

    /// <summary>Returns −Σ p·log p over the levels present, divided by log of how many are present (0 for one level).</summary>
    private static double Entropy(ReadOnlySpan<long> counts, long total)
    {
        int present = 0;
        double sum = 0;
        foreach (long count in counts)
        {
            if (count > 0)
            {
                present++;
                double p = (double)count / total;
                sum -= p * Math.Log(p);
            }
        }

        return present > 1 ? sum / Math.Log(present) : 0;
    }

    /// <summary>Returns a value's 16-bit level, rounded to nearest and clamped.</summary>
    internal static int Level(double v)
    {
        return (int)Math.Clamp(Math.Round(v * 65535), 0, 65535);
    }

    private static void Values(RgbaD p, Span<double> values)
    {
        values[0] = p.R;
        values[1] = p.G;
        values[2] = p.B;
        values[3] = p.A;
        values[4] = (0.2126 * p.R) + (0.7152 * p.G) + (0.0722 * p.B);
    }
}
