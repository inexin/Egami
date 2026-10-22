using System.Numerics;

namespace Egami.Video.Audio.Codecs.Flac;

/// <summary>A planned subframe: how a channel's block is coded and how many bits that takes.</summary>
internal sealed class FlacSubframe
{
    public int[] Residual = [];
    public int[] Coefficients = new int[32];
    public byte[] Parameters = new byte[1 << 8];

    /// <summary>0 constant, 1 verbatim, 2 fixed, 3 LPC.</summary>
    public int Kind;
    public int Order;
    public int Precision;
    public int Shift;
    public int PartitionOrder;
    public bool Rice2;
    public int Wasted;
    public int Bits;
    public long Cost;

    public void CopyFrom(FlacSubframe other, int n)
    {
        if (Residual.Length < n)
        {
            Residual = new int[n];
        }

        other.Residual.AsSpan(0, n).CopyTo(Residual);
        other.Coefficients.CopyTo(Coefficients, 0);
        other.Parameters.CopyTo(Parameters, 0);
        Kind = other.Kind;
        Order = other.Order;
        Precision = other.Precision;
        Shift = other.Shift;
        PartitionOrder = other.PartitionOrder;
        Rice2 = other.Rice2;
        Wasted = other.Wasted;
        Bits = other.Bits;
        Cost = other.Cost;
    }
}

/// <summary>
/// Chooses and writes FLAC subframes: constant, verbatim, the best fixed predictor, and LPC predictors from windowed
/// autocorrelation and Levinson-Durbin over each apodization window, with the Rice partition order and parameters that cost
/// the fewest bits.
/// </summary>
internal sealed class FlacSubframeEncoder
{
    private readonly int _maxLpcOrder;
    private readonly int _maxPartitionOrder;
    private readonly double[][] _windows;
    private readonly FlacSubframe _trial = new();
    private int[] _shifted = [];
    private uint[] _zigzag = [];
    private int _windowSize;
    private readonly int _windowCount;

    public FlacSubframeEncoder(int maxLpcOrder, int maxPartitionOrder, FlacApodization apodization)
    {
        _maxLpcOrder = maxLpcOrder;
        _maxPartitionOrder = maxPartitionOrder;
        _windowCount = apodization.Count;
        _windows = new double[_windowCount][];
    }

    /// <summary>Plans the subframe for a channel block of the given sample size; fills <paramref name="plan"/>.</summary>
    public void Plan(ReadOnlySpan<int> samples, int bits, FlacSubframe plan)
    {
        int n = samples.Length;
        EnsureCapacity(n);
        if (plan.Residual.Length < n)
        {
            plan.Residual = new int[n];
        }

        // Constant blocks, and wasted low bits shared by every sample.
        int or = 0;
        bool constant = true;
        for (int i = 0; i < n; i++)
        {
            or |= samples[i];
            constant &= samples[i] == samples[0];
        }

        if (constant)
        {
            plan.Kind = 0;
            plan.Bits = bits;
            plan.Wasted = 0;
            plan.Residual[0] = samples[0];
            plan.Cost = 8 + bits;
            return;
        }

        int wasted = Math.Min(BitOperations.TrailingZeroCount(or), bits - 1);
        int coded = bits - wasted;
        Span<int> s = _shifted.AsSpan(0, n);
        for (int i = 0; i < n; i++)
        {
            s[i] = samples[i] >> wasted;
        }

        long header = 8 + (wasted > 0 ? wasted : 0);

        // Verbatim is the fallback.
        plan.Kind = 1;
        plan.Bits = coded;
        plan.Wasted = wasted;
        plan.Cost = header + ((long)n * coded);
        s.CopyTo(plan.Residual);

        // Fixed: the order with the smallest residual magnitude, then its exact Rice cost.
        int bestOrder = BestFixedOrder(s, Math.Min(4, n - 1));
        if (FixedResidual(s, bestOrder, _trial.Residual.AsSpan(0, n)))
        {
            long cost = header + ((long)bestOrder * coded) + RiceCost(_trial.Residual.AsSpan(0, n), bestOrder, _trial);
            if (cost < plan.Cost)
            {
                _trial.Kind = 2;
                _trial.Order = bestOrder;
                _trial.Bits = coded;
                _trial.Wasted = wasted;
                _trial.Cost = cost;
                plan.CopyFrom(_trial, n);
            }
        }

        int maxOrder = Math.Min(_maxLpcOrder, n - 1);
        if (maxOrder < 1 || n < 16)
        {
            return;
        }

        int precision = QlpPrecision(n, coded);
        Span<double> autoc = stackalloc double[33];
        Span<double> lpc = stackalloc double[32 * 32];
        Span<double> errors = stackalloc double[32];
        Span<int> q = stackalloc int[32];
        for (int w = 0; w < _windowCount; w++)
        {
            Autocorrelation(s, _windows[w], autoc[..(maxOrder + 1)]);
            if (autoc[0] <= 0)
            {
                continue;
            }

            int orders = Levinson(autoc[..(maxOrder + 1)], lpc, errors);
            int order = BestLpcOrder(errors[..orders], n, precision);
            if (order == 0)
            {
                continue;
            }

            int shift = Quantize(lpc.Slice((order - 1) * 32, order), precision, q[..order]);
            if (shift < 0 || !LpcResidual(s, q[..order], shift, _trial.Residual.AsSpan(0, n)))
            {
                continue;
            }

            long cost = header + ((long)order * coded) + 4 + 5 + ((long)order * precision) + RiceCost(_trial.Residual.AsSpan(0, n), order, _trial);
            if (cost < plan.Cost)
            {
                _trial.Kind = 3;
                _trial.Order = order;
                _trial.Precision = precision;
                _trial.Shift = shift;
                q[..order].CopyTo(_trial.Coefficients);
                _trial.Bits = coded;
                _trial.Wasted = wasted;
                _trial.Cost = cost;
                plan.CopyFrom(_trial, n);
            }
        }
    }

    /// <summary>Writes a planned subframe.</summary>
    public static void Write(FlacBitWriter w, FlacSubframe plan, int n)
    {
        int type = plan.Kind switch
        {
            0 => 0,
            1 => 1,
            2 => 8 + plan.Order,
            _ => 31 + plan.Order,
        };
        w.Write(0, 1);
        w.Write((uint)type, 6);
        if (plan.Wasted > 0)
        {
            w.Write(1, 1);
            w.WriteUnary((uint)(plan.Wasted - 1));
        }
        else
        {
            w.Write(0, 1);
        }

        ReadOnlySpan<int> r = plan.Residual.AsSpan(0, n);
        switch (plan.Kind)
        {
            case 0:
                w.WriteSigned(r[0], plan.Bits);
                return;
            case 1:
                for (int i = 0; i < n; i++)
                {
                    w.WriteSigned(r[i], plan.Bits);
                }

                return;
        }

        for (int i = 0; i < plan.Order; i++)
        {
            w.WriteSigned(r[i], plan.Bits);
        }

        if (plan.Kind == 3)
        {
            w.Write((uint)(plan.Precision - 1), 4);
            w.WriteSigned(plan.Shift, 5);
            for (int i = 0; i < plan.Order; i++)
            {
                w.WriteSigned(plan.Coefficients[i], plan.Precision);
            }
        }

        w.Write(plan.Rice2 ? 1u : 0u, 2);
        w.Write((uint)plan.PartitionOrder, 4);
        int partitions = 1 << plan.PartitionOrder;
        int position = plan.Order;
        for (int p = 0; p < partitions; p++)
        {
            int count = (n >> plan.PartitionOrder) - (p == 0 ? plan.Order : 0);
            int k = plan.Parameters[p];
            w.Write((uint)k, plan.Rice2 ? 5 : 4);
            w.WriteRice(r.Slice(position, count), k);
            position += count;
        }
    }

    private static int QlpPrecision(int n, int bits)
    {
        int precision = n switch
        {
            <= 192 => 7,
            <= 384 => 8,
            <= 576 => 9,
            <= 1152 => 10,
            <= 2304 => 11,
            <= 4608 => 12,
            _ => 13,
        };

        // Keep coefficient times sample within 32 bits plus the order's growth for narrow samples, as decoders on 32 bits need.
        return bits <= 16 ? precision : Math.Min(15, precision + 1);
    }

    private static int BestFixedOrder(ReadOnlySpan<int> s, int maxOrder)
    {
        Span<long> sums = stackalloc long[5];
        sums.Clear();
        int n = s.Length;
        int start = Math.Min(4, n);
        for (int i = start; i < n; i++)
        {
            long e0 = s[i];
            long e1 = e0 - s[i - 1];
            long e2 = e1 - (s[i - 1] - (long)s[i - 2]);
            long e3 = e2 - (s[i - 1] - (2L * s[i - 2]) + s[i - 3]);
            long e4 = e3 - (s[i - 1] - (3L * s[i - 2]) + (3L * s[i - 3]) - s[i - 4]);
            sums[0] += Math.Abs(e0);
            sums[1] += Math.Abs(e1);
            sums[2] += Math.Abs(e2);
            sums[3] += Math.Abs(e3);
            sums[4] += Math.Abs(e4);
        }

        int best = 0;
        for (int o = 1; o <= maxOrder; o++)
        {
            if (sums[o] < sums[best])
            {
                best = o;
            }
        }

        return best;
    }

    private static bool FixedResidual(ReadOnlySpan<int> s, int order, Span<int> r)
    {
        int n = s.Length;
        s[..order].CopyTo(r);
        for (int i = order; i < n; i++)
        {
            long prediction = order switch
            {
                0 => 0,
                1 => s[i - 1],
                2 => (2L * s[i - 1]) - s[i - 2],
                3 => (3L * s[i - 1]) - (3L * s[i - 2]) + s[i - 3],
                _ => (4L * s[i - 1]) - (6L * s[i - 2]) + (4L * s[i - 3]) - s[i - 4],
            };
            long e = s[i] - prediction;
            if (e is > int.MaxValue or < int.MinValue + 1)
            {
                return false;
            }

            r[i] = (int)e;
        }

        return true;
    }

    private static bool LpcResidual(ReadOnlySpan<int> s, ReadOnlySpan<int> q, int shift, Span<int> r)
    {
        int n = s.Length;
        int order = q.Length;
        s[..order].CopyTo(r);
        for (int i = order; i < n; i++)
        {
            long sum = 0;
            for (int j = 0; j < order; j++)
            {
                sum += (long)q[j] * s[i - 1 - j];
            }

            long e = s[i] - (sum >> shift);
            if (e is > int.MaxValue or < int.MinValue + 1)
            {
                return false;
            }

            r[i] = (int)e;
        }

        return true;
    }

    private static void Autocorrelation(ReadOnlySpan<int> s, ReadOnlySpan<double> window, Span<double> autoc)
    {
        int n = s.Length;
        Span<double> x = n <= 8192 ? stackalloc double[n] : new double[n];
        for (int i = 0; i < n; i++)
        {
            x[i] = s[i] * window[i];
        }

        for (int lag = 0; lag < autoc.Length; lag++)
        {
            double sum = 0;
            for (int i = lag; i < n; i++)
            {
                sum += x[i] * x[i - lag];
            }

            autoc[lag] = sum;
        }
    }

    /// <summary>Levinson-Durbin: the predictor of every order up to autoc.Length - 1, row (order - 1), and each order's error.</summary>
    private static int Levinson(ReadOnlySpan<double> autoc, Span<double> lpc, Span<double> errors)
    {
        int max = autoc.Length - 1;
        Span<double> a = stackalloc double[33];
        Span<double> previous = stackalloc double[33];
        a.Clear();
        double error = autoc[0];
        for (int m = 1; m <= max; m++)
        {
            double acc = autoc[m];
            for (int j = 1; j < m; j++)
            {
                acc -= a[j] * autoc[m - j];
            }

            double k = acc / error;
            a.CopyTo(previous);
            a[m] = k;
            for (int j = 1; j < m; j++)
            {
                a[j] = previous[j] - (k * previous[m - j]);
            }

            error *= 1 - (k * k);
            if (error <= 0 || !double.IsFinite(error))
            {
                return m - 1;
            }

            errors[m - 1] = error;
            for (int j = 0; j < m; j++)
            {
                lpc[((m - 1) * 32) + j] = a[j + 1];
            }
        }

        return max;
    }

    /// <summary>Picks the order with the fewest estimated bits: residual bits from each order's error, plus its coefficients.</summary>
    private static int BestLpcOrder(ReadOnlySpan<double> errors, int n, int precision)
    {
        int best = 0;
        double bestBits = double.MaxValue;
        for (int o = 1; o <= errors.Length; o++)
        {
            double perSample = Math.Max(0, 0.5 * Math.Log2(Math.Max(errors[o - 1], 1e-30) / n));
            double bits = (perSample * (n - o)) + (o * precision);
            if (bits < bestBits)
            {
                bestBits = bits;
                best = o;
            }
        }

        return best;
    }

    /// <summary>Quantizes coefficients to <paramref name="precision"/> signed bits with error feedback; returns the shift, or -1.</summary>
    private static int Quantize(ReadOnlySpan<double> lpc, int precision, Span<int> q)
    {
        double cmax = 0;
        foreach (double c in lpc)
        {
            cmax = Math.Max(cmax, Math.Abs(c));
        }

        if (cmax <= 0)
        {
            return -1;
        }

        int log2 = Math.ILogB(cmax) + 1;
        int shift = Math.Min(precision - 1 - log2, 15);
        if (shift < 0)
        {
            return -1;
        }

        int max = (1 << (precision - 1)) - 1;
        int min = -(1 << (precision - 1));
        double error = 0;
        for (int i = 0; i < lpc.Length; i++)
        {
            error += lpc[i] * (1 << shift);
            int v = (int)Math.Clamp(Math.Round(error), min, max);
            q[i] = v;
            error -= v;
        }

        return shift;
    }

    /// <summary>The exact bits of the residual after the warm-up with the best partition order and Rice parameters; fills the plan's parameters.</summary>
    private long RiceCost(ReadOnlySpan<int> residual, int order, FlacSubframe plan)
    {
        int n = residual.Length;
        int maxOrder = Math.Min(_maxPartitionOrder, BitOperations.TrailingZeroCount(n));
        while (maxOrder > 0 && (n >> maxOrder) <= order)
        {
            maxOrder--;
        }

        Span<uint> u = _zigzag.AsSpan(0, n);
        for (int i = order; i < n; i++)
        {
            int r = residual[i];
            u[i] = (uint)((r << 1) ^ (r >> 31));
        }

        long best = long.MaxValue;
        Span<byte> bestParameters = stackalloc byte[256];
        Span<byte> parameters = stackalloc byte[256];
        bool bestRice2 = false;
        int bestOrder = 0;
        for (int po = maxOrder; po >= 0; po--)
        {
            int partitions = 1 << po;
            long bits = 0;
            bool rice2 = false;
            for (int p = 0; p < partitions; p++)
            {
                int from = p == 0 ? order : p * (n >> po);
                int to = (p + 1) * (n >> po);
                ReadOnlySpan<uint> part = u[from..to];
                (int k, long cost) = BestParameter(part);
                rice2 |= k > 14;
                parameters[p] = (byte)k;
                bits += cost;
            }

            bits += 2 + 4 + ((long)partitions * (rice2 ? 5 : 4));
            if (bits < best)
            {
                best = bits;
                bestOrder = po;
                bestRice2 = rice2;
                parameters[..partitions].CopyTo(bestParameters);
            }
        }

        plan.PartitionOrder = bestOrder;
        plan.Rice2 = bestRice2;
        bestParameters[..(1 << bestOrder)].CopyTo(plan.Parameters);
        return best;
    }

    /// <summary>The Rice parameter with the fewest bits for a partition, trying those next to log2 of the mean, and its cost.</summary>
    private static (int K, long Cost) BestParameter(ReadOnlySpan<uint> u)
    {
        if (u.IsEmpty)
        {
            return (0, 0);
        }

        long total = 0;
        foreach (uint v in u)
        {
            total += v;
        }

        long mean = total / u.Length;
        int guess = mean <= 0 ? 0 : Math.Min(30, 63 - BitOperations.LeadingZeroCount((ulong)mean));
        int bestK = 0;
        long bestCost = long.MaxValue;
        for (int k = Math.Max(0, guess - 1); k <= Math.Min(30, guess + 1); k++)
        {
            long cost = (long)u.Length * (k + 1);
            if (k == 0)
            {
                cost += total;
            }
            else
            {
                foreach (uint v in u)
                {
                    cost += v >> k;
                }
            }

            if (cost < bestCost)
            {
                bestCost = cost;
                bestK = k;
            }
        }

        return (bestK, bestCost);
    }

    private void EnsureCapacity(int n)
    {
        if (_shifted.Length < n)
        {
            _shifted = new int[n];
            _zigzag = new uint[n];
            _trial.Residual = new int[n];
        }

        if (_windowSize != n && _maxLpcOrder > 0)
        {
            _windowSize = n;
            for (int w = 0; w < _windowCount; w++)
            {
                _windows[w] = FlacApodization.Window(w, n);
            }
        }

    }
}

/// <summary>
/// The apodization windows LPC analysis tries: tukey(0.5), and for subdivide_tukey(n) also, for each m from 2 to n, the m
/// partial windows (a tukey(0.5) over each m-th of the block) and the m punchout windows (the block with each m-th cut out).
/// </summary>
internal sealed class FlacApodization(int subdivisions)
{
    public int Count { get; } = 1 + Enumerable.Range(2, Math.Max(0, subdivisions - 1)).Sum(m => 2 * m);

    public static double[] Window(int index, int n)
    {
        double[] w = new double[n];
        if (index == 0)
        {
            Tukey(w, 0, n, 0.5);
            return w;
        }

        int i = index - 1;
        for (int m = 2; ; m++)
        {
            if (i < 2 * m)
            {
                int part = i % m;
                int start = part * n / m;
                int end = (part + 1) * n / m;
                if (i < m)
                {
                    Tukey(w, start, end, 0.5);
                }
                else
                {
                    // Punchout: the whole block with this part tapered out.
                    Tukey(w, 0, n, 0.5 / m);
                    double[] cut = new double[n];
                    Tukey(cut, start, end, 0.5);
                    for (int k = 0; k < n; k++)
                    {
                        w[k] *= 1 - cut[k];
                    }
                }

                return w;
            }

            i -= 2 * m;
        }
    }

    /// <summary>A Tukey window over [start, end): flat in the middle, cosine tapers covering <paramref name="p"/> of it.</summary>
    private static void Tukey(Span<double> w, int start, int end, double p)
    {
        int length = end - start;
        int taper = (int)(p / 2 * length);
        for (int k = 0; k < length; k++)
        {
            double v = 1;
            if (taper > 0 && k < taper)
            {
                v = 0.5 - (0.5 * Math.Cos(Math.PI * k / taper));
            }
            else if (taper > 0 && k >= length - taper)
            {
                v = 0.5 - (0.5 * Math.Cos(Math.PI * (length - 1 - k) / taper));
            }

            w[start + k] = v;
        }
    }
}
