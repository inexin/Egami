using Egami.Barcodes.Reading;

namespace Egami.Barcodes.Tests;

/// <summary>
/// The line binarizations, sixteen samples at a time, against the plain loops they replaced: the runs and offsets must be the
/// same floats, so that the linear readers see the same lines as before.
/// </summary>
public class LineRunsTests
{
    public static TheoryData<int> Lengths => [0, 1, 2, 3, 15, 16, 17, 31, 33, 100, 255, 299, 640, 1931];

    [Theory]
    [MemberData(nameof(Lengths))]
    public void RunsMatchThePlainLoops(int length)
    {
        var random = new Random(length + 7);
        List<float> runs = [], offsets = [], expectedRuns = [], expectedOffsets = [];
        byte[] work = [];
        int[] prefixes = [];
        for (int trial = 0; trial < 40; trial++)
        {
            byte[] row = Row(random, length, trial % 5);
            Binarizer.RowRuns(row, runs, offsets, ref work);
            PlainRowRuns(row, expectedRuns, expectedOffsets);
            Assert.Equal(expectedRuns, runs);
            Assert.Equal(expectedOffsets, offsets);

            Binarizer.AdaptiveRowRuns(row, runs, offsets, ref work, ref prefixes);
            PlainAdaptiveRowRuns(row, expectedRuns, expectedOffsets);
            Assert.Equal(expectedRuns, runs);
            Assert.Equal(expectedOffsets, offsets);
        }
    }

    /// <summary>Bars with blur and noise, a gradient under bars, pure noise, a flat row or a row of two levels.</summary>
    private static byte[] Row(Random random, int length, int kind)
    {
        byte[] row = new byte[length];
        int level = 200, left = 0;
        for (int i = 0; i < length; i++)
        {
            if (left-- <= 0)
            {
                level = level > 128 ? random.Next(0, 90) : random.Next(160, 256);
                left = random.Next(1, 9);
            }

            row[i] = kind switch
            {
                0 => (byte)Math.Clamp(level + random.Next(-30, 31), 0, 255),
                1 => (byte)Math.Clamp((level / 2) + (i * 120 / Math.Max(length, 1)) + random.Next(-8, 9), 0, 255),
                2 => (byte)random.Next(256),
                3 => 117,
                _ => (byte)(level > 128 ? 255 : 0),
            };
        }

        return row;
    }

    private static void PlainRowRuns(ReadOnlySpan<byte> row, List<float> runs, List<float> offsets)
    {
        runs.Clear();
        offsets.Clear();
        int n = row.Length;
        Span<int> histogram = stackalloc int[32];
        foreach (byte p in row)
        {
            histogram[p >> 3]++;
        }

        float threshold = Valley(histogram) << 3;
        float[] sharp = new float[n];
        for (int i = 0; i < n; i++)
        {
            int left = row[Math.Max(i - 1, 0)], right = row[Math.Min(i + 1, n - 1)];
            sharp[i] = ((4 * row[i]) - left - right) / 2f;
        }

        Cut(sharp, threshold, runs, offsets);
    }

    private static void PlainAdaptiveRowRuns(ReadOnlySpan<byte> row, List<float> runs, List<float> offsets)
    {
        runs.Clear();
        offsets.Clear();
        int n = row.Length, radius = Math.Max(7, n / 16);
        int[] prefix = new int[n + 1];
        for (int i = 0; i < n; i++)
        {
            prefix[i + 1] = prefix[i] + row[i];
        }

        float[] level = new float[n];
        int min = 255, max = 0;
        for (int i = 0; i < n; i++)
        {
            int a = Math.Max(0, i - radius), b = Math.Min(n, i + radius + 1);
            level[i] = row[i] - ((prefix[b] - prefix[a]) / (float)(b - a));
            min = Math.Min(min, row[i]);
            max = Math.Max(max, row[i]);
        }

        if (max - min < 24)
        {
            runs.Add(n);
            offsets.Add(0);
            offsets.Add(n);
            return;
        }

        Cut(level, 0, runs, offsets);
    }

    private static void Cut(float[] values, float threshold, List<float> runs, List<float> offsets)
    {
        int n = values.Length;
        bool dark = false;
        float start = 0;
        for (int i = 0; i < n; i++)
        {
            bool d = values[i] < threshold;
            if (d == dark)
            {
                continue;
            }

            float a = i > 0 ? values[i - 1] : values[i], b = values[i];
            float edge = a == b ? i : i - 0.5f + Math.Clamp((threshold - a) / (b - a), 0, 1);
            runs.Add(Math.Max(edge - start, 0));
            offsets.Add(start);
            start = edge;
            dark = d;
        }

        runs.Add(n - start);
        offsets.Add(start);
        offsets.Add(n);
    }

    private static int Valley(ReadOnlySpan<int> histogram)
    {
        int first = 0, max = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            if (histogram[i] > max)
            {
                (first, max) = (i, histogram[i]);
            }
        }

        int second = 0, best = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            int distance = i - first, score = histogram[i] * distance * distance;
            if (score > best)
            {
                (second, best) = (i, score);
            }
        }

        if (first > second)
        {
            (first, second) = (second, first);
        }

        if (second - first <= 1)
        {
            return 16;
        }

        int valley = second - 1, bestValley = -1;
        for (int i = second - 1; i > first; i--)
        {
            int fromFirst = i - first;
            int score = fromFirst * fromFirst * (second - i) * (max - histogram[i]);
            if (score > bestValley)
            {
                (valley, bestValley) = (i, score);
            }
        }

        return valley;
    }
}
