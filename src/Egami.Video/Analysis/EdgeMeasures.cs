namespace Egami.Video.Analysis;

/// <summary>
/// Blur and blockiness from a luma picture scaled 0 to 1, for <see cref="SignalMeter"/>. Blur is the mean width of sharp edges:
/// edge pixels are found from the Sobel gradient with two thresholds, thinned to the steepest pixel across the edge, and measured
/// from where the ramp starts to where it ends. Blockiness compares the steps between columns and rows that fall on a block grid
/// with the steps between them, for every block size in the range, and keeps the strongest.
/// </summary>
internal sealed class EdgeMeasures
{
    private const int BlockSize = 32;
    private const int MaxWalk = 64;
    private float[] _magnitude = [];
    private double[] _columns = [];
    private double[] _rows = [];
    private long[] _blockWidths = [];
    private int[] _blockEdges = [];
    private int[] _order = [];

    public double? Blur(float[] luma, int width, int height, double high, double low, double blockShare)
    {
        if (width < 3 || height < 3)
        {
            return null;
        }

        if (_magnitude.Length != luma.Length)
        {
            _magnitude = new float[luma.Length];
        }

        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                (float gx, float gy) = Gradient(luma, width, x, y);
                _magnitude[(y * width) + x] = MathF.Sqrt((gx * gx) + (gy * gy)) / 4;
            }
        }

        int across = (width + BlockSize - 1) / BlockSize, down = (height + BlockSize - 1) / BlockSize;
        int blocks = across * down;
        if (_blockEdges.Length != blocks)
        {
            _blockEdges = new int[blocks];
            _blockWidths = new long[blocks];
            _order = new int[blocks];
        }
        else
        {
            Array.Clear(_blockEdges);
            Array.Clear(_blockWidths);
        }

        float strong = (float)high, weak = (float)low;
        for (int y = 2; y < height - 2; y++)
        {
            for (int x = 2; x < width - 2; x++)
            {
                int i = (y * width) + x;
                float m = _magnitude[i];
                if (m < weak || (m < strong && !StrongNeighbor(width, i, strong)))
                {
                    continue;
                }

                (float gx, float gy) = Gradient(luma, width, x, y);
                bool across0 = MathF.Abs(gx) >= MathF.Abs(gy);
                int step = across0 ? 1 : width;
                if (m < _magnitude[i - step] || m <= _magnitude[i + step])
                {
                    continue;
                }

                float sign = across0 ? MathF.Sign(gx) : MathF.Sign(gy);
                int before = Walk(luma, i, -step, sign, across0 ? x : y);
                int after = Walk(luma, i, step, sign, across0 ? width - 1 - x : height - 1 - y);
                int b = ((y / BlockSize) * across) + (x / BlockSize);
                _blockWidths[b] += before + after;
                _blockEdges[b]++;
            }
        }

        // The blocks with the most edges carry the measure, as blurdetect's block percentage.
        int used = 0;
        for (int b = 0; b < blocks; b++)
        {
            if (_blockEdges[b] > 0)
            {
                _order[used++] = b;
            }
        }

        if (used == 0)
        {
            return null;
        }

        Span<int> order = _order.AsSpan(0, used);
        int[] edges = _blockEdges;
        order.Sort((a, b) => edges[b].CompareTo(edges[a]));
        int take = Math.Max(1, (int)Math.Ceiling(used * blockShare));
        long widths = 0, count = 0;
        foreach (int b in order[..take])
        {
            widths += _blockWidths[b];
            count += _blockEdges[b];
        }

        return (double)widths / count;
    }

    public double Blockiness(float[] luma, int width, int height, int minPeriod, int maxPeriod)
    {
        if (_columns.Length != width)
        {
            _columns = new double[width];
        }
        else
        {
            Array.Clear(_columns);
        }

        if (_rows.Length != height)
        {
            _rows = new double[height];
        }
        else
        {
            Array.Clear(_rows);
        }

        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<float> row = luma.AsSpan(y * width, width);
            ReadOnlySpan<float> above = y > 0 ? luma.AsSpan((y - 1) * width, width) : default;
            double rowStep = 0;
            for (int x = 0; x < width; x++)
            {
                if (x > 0)
                {
                    _columns[x] += MathF.Abs(row[x] - row[x - 1]);
                }

                if (y > 0)
                {
                    rowStep += MathF.Abs(row[x] - above[x]);
                }
            }

            _rows[y] = rowStep;
        }

        double best = 1;
        for (int p = minPeriod; p <= maxPeriod; p++)
        {
            double score = (Ratio(_columns, p) + Ratio(_rows, p)) / 2;
            best = Math.Max(best, score);
        }

        return best;
    }

    private static double Ratio(double[] steps, int period)
    {
        double onGrid = 0, between = 0;
        int grid = 0, others = 0;
        for (int i = 1; i < steps.Length; i++)
        {
            if (i % period == 0)
            {
                onGrid += steps[i];
                grid++;
            }
            else
            {
                between += steps[i];
                others++;
            }
        }

        if (grid == 0 || others == 0)
        {
            return 1;
        }

        const double Floor = 1e-9;
        return ((onGrid / grid) + Floor) / ((between / others) + Floor);
    }

    private static (float X, float Y) Gradient(float[] p, int width, int x, int y)
    {
        int i = (y * width) + x;
        float tl = p[i - width - 1], t = p[i - width], tr = p[i - width + 1];
        float l = p[i - 1], r = p[i + 1];
        float bl = p[i + width - 1], b = p[i + width], br = p[i + width + 1];
        float gx = (tr + (2 * r) + br) - (tl + (2 * l) + bl);
        float gy = (bl + (2 * b) + br) - (tl + (2 * t) + tr);
        return (gx, gy);
    }

    private bool StrongNeighbor(int width, int i, float strong)
    {
        ReadOnlySpan<float> m = _magnitude;
        return m[i - width - 1] >= strong || m[i - width] >= strong || m[i - width + 1] >= strong
            || m[i - 1] >= strong || m[i + 1] >= strong
            || m[i + width - 1] >= strong || m[i + width] >= strong || m[i + width + 1] >= strong;
    }

    /// <summary>Counts the steps from <paramref name="start"/> along <paramref name="step"/> while the ramp keeps going the edge's way.</summary>
    private static int Walk(float[] p, int start, int step, float sign, int room)
    {
        int count = 0, i = start;
        int limit = Math.Min(room, MaxWalk);
        while (count < limit)
        {
            float change = step > 0 ? p[i + step] - p[i] : p[i] - p[i + step];
            if (change * sign <= 0)
            {
                break;
            }

            i += step;
            count++;
        }

        return count;
    }
}
