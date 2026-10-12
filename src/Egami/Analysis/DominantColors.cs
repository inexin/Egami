using System.Buffers;

using Egami.Colors;
using Egami.Pixels;
using Egami.Quantization;

namespace Egami.Analysis;

/// <summary>A color and the share of an image's pixels it stands for, from <see cref="Raster.DominantColors"/>.</summary>
/// <param name="Color">The color.</param>
/// <param name="Weight">The share of the counted pixels nearest this color, from 0 to 1.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Palettes.cs" region="dominant"/>
/// </example>
public readonly record struct WeightedColor(Rgba8 Color, double Weight);

/// <summary>Options for <see cref="Raster.DominantColors"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Palettes.cs" region="dominant"/>
/// </example>
public sealed record DominantColorOptions
{
    private readonly int _maxSamples = 65536;
    private readonly int _iterations = 16;
    private readonly float _merge = 0.04f;

    /// <summary>Gets the default options.</summary>
    public static DominantColorOptions Default { get; } = new();

    /// <summary>
    /// Gets the most pixels clustered, 256 to 16,777,216: larger images are averaged down in square blocks first. Defaults to 65,536.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is out of range.</exception>
    public int MaxSamples
    {
        get => _maxSamples;
        init => _maxSamples = value is >= 256 and <= 1 << 24 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The sample count must be from 256 to 16,777,216.");
    }

    /// <summary>Gets the alpha below which a pixel is left out, as background. Defaults to 128.</summary>
    public byte AlphaThreshold { get; init; } = 128;

    /// <summary>Gets the most k-means passes, 0 to 100; clustering stops sooner once nothing moves. Defaults to 16.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is out of range.</exception>
    public int Iterations
    {
        get => _iterations;
        init => _iterations = value is >= 0 and <= 100 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The passes must be from 0 to 100.");
    }

    /// <summary>
    /// Gets how close two clusters must be in Oklab to count as one color, 0 to 1. Defaults to 0.04, about the smallest difference
    /// people name as a different color.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is out of range.</exception>
    public float MergeDistance
    {
        get => _merge;
        init => _merge = value is >= 0 and <= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The distance must be from 0 to 1.");
    }
}

/// <summary>
/// Dominant colors: the image averaged down in blocks to at most MaxSamples samples (transparent pixels left out), Wu's
/// palette of twice the colors asked for as seeds, Lloyd's k-means in Oklab weighted by how many pixels each sample covers,
/// the closest clusters merged down to the count asked for (and any closer than MergeDistance), and k-means once more. Every
/// counted pixel belongs to a returned color, so the weights sum to 1. No random seeds, so the result is the same every run.
/// </summary>
internal static class DominantColorFinder
{
    internal static List<WeightedColor> Find(Raster image, int count, DominantColorOptions options)
    {
        (List<Rgba8> colors, List<double> weights) = Sample(image, options);
        if (colors.Count == 0)
        {
            return [];
        }

        Rgba8[] seeds;
        using (WuQuantizer wu = new(false))
        {
            wu.Add(colors.ToArray(), 0);
            seeds = wu.Build(Math.Min(256, 2 * count));
        }

        double[][] points = [.. colors.Select(Lab)];
        double[] w = [.. weights];

        // Cluster from twice the colors, merge the closest pairs down to the count asked for (and any closer than
        // MergeDistance), then let k-means settle the merged centers.
        List<(double[] Center, double Mass)> clusters = Cluster(points, w, [.. seeds.Select(Lab)], options.Iterations);
        double limit = (double)options.MergeDistance * options.MergeDistance;
        Merge(clusters, count, limit);
        clusters = Cluster(points, w, [.. clusters.Select(c => c.Center)], options.Iterations);
        Merge(clusters, count, limit);

        double total = w.Sum();
        return [.. clusters
            .OrderByDescending(c => c.Mass)
            .ThenBy(c => c.Center[0])
            .Select(c => new WeightedColor(new OklabColor((float)c.Center[0], (float)c.Center[1], (float)c.Center[2]).ToRgba8(), c.Mass / total))];
    }

    /// <summary>Weighted Lloyd's k-means from <paramref name="centers"/>; returns the clusters that kept any weight.</summary>
    private static List<(double[] Center, double Mass)> Cluster(double[][] points, double[] weights, List<double[]> centers, int iterations)
    {
        double[] mass = new double[centers.Count];
        int[] nearest = new int[points.Length];
        for (int pass = 0; pass <= iterations; pass++)
        {
            bool changed = pass == 0;
            for (int i = 0; i < points.Length; i++)
            {
                int best = Nearest(centers, points[i]);
                changed |= best != nearest[i];
                nearest[i] = best;
            }

            double[][] sums = [.. centers.Select(_ => new double[3])];
            Array.Clear(mass);
            for (int i = 0; i < points.Length; i++)
            {
                double[] s = sums[nearest[i]];
                s[0] += points[i][0] * weights[i];
                s[1] += points[i][1] * weights[i];
                s[2] += points[i][2] * weights[i];
                mass[nearest[i]] += weights[i];
            }

            for (int k = 0; k < centers.Count; k++)
            {
                if (mass[k] > 0)
                {
                    centers[k] = [sums[k][0] / mass[k], sums[k][1] / mass[k], sums[k][2] / mass[k]];
                }
            }

            if (!changed)
            {
                break;
            }
        }

        return [.. centers.Zip(mass).Where(c => c.Second > 0)];
    }

    /// <summary>Merges the closest pair, by weighted mean, while there are more than <paramref name="count"/> clusters or the pair is closer than √<paramref name="limit"/>.</summary>
    private static void Merge(List<(double[] Center, double Mass)> clusters, int count, double limit)
    {
        while (clusters.Count > 1)
        {
            (int a, int b, double d) = (-1, -1, double.MaxValue);
            for (int i = 0; i < clusters.Count; i++)
            {
                for (int j = i + 1; j < clusters.Count; j++)
                {
                    double distance = Distance2(clusters[i].Center, clusters[j].Center);
                    if (distance < d)
                    {
                        (a, b, d) = (i, j, distance);
                    }
                }
            }

            if (clusters.Count <= count && d >= limit)
            {
                break;
            }

            (double[] ca, double ma) = clusters[a];
            (double[] cb, double mb) = clusters[b];
            double m = ma + mb;
            clusters[a] = ([((ca[0] * ma) + (cb[0] * mb)) / m, ((ca[1] * ma) + (cb[1] * mb)) / m, ((ca[2] * ma) + (cb[2] * mb)) / m], m);
            clusters.RemoveAt(b);
        }
    }

    /// <summary>Averages the first frame in square blocks so at most MaxSamples remain; each sample weighs the pixels it counted.</summary>
    private static (List<Rgba8> Colors, List<double> Weights) Sample(Raster image, DominantColorOptions options)
    {
        int width = image.Width, height = image.Height;
        int block = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)width * height / options.MaxSamples)));
        int columns = (width + block - 1) / block;
        double[] sums = new double[columns * 4];
        List<Rgba8> colors = [];
        List<double> weights = [];
        Rgba8[] row = ArrayPool<Rgba8>.Shared.Rent(width);
        try
        {
            for (int y0 = 0; y0 < height; y0 += block)
            {
                Array.Clear(sums);
                for (int y = y0; y < Math.Min(height, y0 + block); y++)
                {
                    image.ReadFrameRow(0, y, 0, row.AsSpan(0, width));
                    for (int x = 0; x < width; x++)
                    {
                        Rgba8 p = row[x];
                        if (p.A < options.AlphaThreshold)
                        {
                            continue;
                        }

                        int at = (x / block) * 4;
                        sums[at] += p.R;
                        sums[at + 1] += p.G;
                        sums[at + 2] += p.B;
                        sums[at + 3]++;
                    }
                }

                for (int c = 0; c < columns; c++)
                {
                    double n = sums[(c * 4) + 3];
                    if (n > 0)
                    {
                        colors.Add(new Rgba8((byte)Math.Round(sums[c * 4] / n), (byte)Math.Round(sums[(c * 4) + 1] / n), (byte)Math.Round(sums[(c * 4) + 2] / n), 255));
                        weights.Add(n);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<Rgba8>.Shared.Return(row);
        }

        return (colors, weights);
    }

    private static double[] Lab(Rgba8 color)
    {
        OklabColor lab = OklabColor.From(color);
        return [lab.L, lab.A, lab.B];
    }

    private static int Nearest(List<double[]> centers, double[] point)
    {
        int best = 0;
        double bestDistance = double.MaxValue;
        for (int k = 0; k < centers.Count; k++)
        {
            double d = Distance2(centers[k], point);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = k;
            }
        }

        return best;
    }

    private static double Distance2(double[] a, double[] b)
    {
        double dl = a[0] - b[0], da = a[1] - b[1], db = a[2] - b[2];
        return (dl * dl) + (da * da) + (db * db);
    }
}
