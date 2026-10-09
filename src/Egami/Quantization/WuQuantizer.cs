using System.Buffers;

using Egami.Pixels;

namespace Egami.Quantization;

/// <summary>
/// Xiaolin Wu's color quantizer ("Efficient Statistical Computations for Optimal Color Quantization", Graphics Gems II, 1991),
/// written from the paper. A histogram of reduced colors keeps cumulative moments (count, per-channel sums, sum of squares), so
/// the variance of any box of colors comes from 2^D lookups. The box with the largest variance is split, again and again, where
/// the split reduces variance most, and each box's mean becomes a palette color.
/// </summary>
/// <remarks>
/// Three dimensions (RGB, 5 bits each, 33 × 33 × 33 cells) for palettes whose transparency is on or off, where transparent
/// pixels are left out and get their own entry; four (RGBA, 4 bits each, 17^4 cells) when the palette keeps partial alpha.
/// </remarks>
internal sealed class WuQuantizer : IDisposable
{
    private readonly int _dimensions;
    private readonly int _side;
    private readonly int _shift;
    private readonly int _cells;
    private readonly int[] _strides;
    private readonly long[] _weight;
    private readonly long[] _sums;
    private readonly double[] _squares;
    private bool _cumulative;

    /// <summary>Creates an empty histogram.</summary>
    /// <param name="withAlpha">Whether alpha is a fourth dimension; otherwise pixels are added as RGB.</param>
    internal WuQuantizer(bool withAlpha)
    {
        _dimensions = withAlpha ? 4 : 3;
        _shift = withAlpha ? 4 : 3;
        _side = (256 >> _shift) + 1;
        _cells = 1;
        _strides = new int[_dimensions];
        for (int d = _dimensions - 1; d >= 0; d--)
        {
            _strides[d] = _cells;
            _cells *= _side;
        }

        _weight = ArrayPool<long>.Shared.Rent(_cells);
        _sums = ArrayPool<long>.Shared.Rent(_cells * _dimensions);
        _squares = ArrayPool<double>.Shared.Rent(_cells);
        Array.Clear(_weight, 0, _cells);
        Array.Clear(_sums, 0, _cells * _dimensions);
        Array.Clear(_squares, 0, _cells);
    }

    /// <summary>Adds pixels. In three dimensions, pixels with alpha below <paramref name="alphaThreshold"/> are skipped.</summary>
    internal void Add(ReadOnlySpan<Rgba8> pixels, byte alphaThreshold)
    {
        int shift = _shift;
        if (_dimensions == 3)
        {
            foreach (Rgba8 p in pixels)
            {
                if (p.A < alphaThreshold)
                {
                    continue;
                }

                int index = ((((p.R >> shift) + 1) * _side) + (p.G >> shift) + 1) * _side + (p.B >> shift) + 1;
                _weight[index]++;
                _sums[index * 3] += p.R;
                _sums[(index * 3) + 1] += p.G;
                _sums[(index * 3) + 2] += p.B;
                _squares[index] += (p.R * p.R) + (p.G * p.G) + (p.B * p.B);
            }

            return;
        }

        foreach (Rgba8 p in pixels)
        {
            int index = ((((((p.R >> shift) + 1) * _side) + (p.G >> shift) + 1) * _side) + (p.B >> shift) + 1) * _side + (p.A >> shift) + 1;
            _weight[index]++;
            _sums[index * 4] += p.R;
            _sums[(index * 4) + 1] += p.G;
            _sums[(index * 4) + 2] += p.B;
            _sums[(index * 4) + 3] += p.A;
            _squares[index] += (p.R * p.R) + (p.G * p.G) + (p.B * p.B) + (p.A * p.A);
        }
    }

    /// <summary>Adds the histogram of another quantizer of the same kind, as when bands of rows were counted in parallel.</summary>
    internal void Add(WuQuantizer other)
    {
        for (int i = 0; i < _cells; i++)
        {
            _weight[i] += other._weight[i];
            _squares[i] += other._squares[i];
        }

        for (int i = 0; i < _cells * _dimensions; i++)
        {
            _sums[i] += other._sums[i];
        }
    }

    /// <summary>Returns up to <paramref name="colors"/> palette colors; fewer when the pixels have fewer distinct boxes.</summary>
    internal Rgba8[] Build(int colors)
    {
        if (!_cumulative)
        {
            Accumulate();
            _cumulative = true;
        }

        Box[] boxes = new Box[colors];
        double[] variance = new double[colors];
        boxes[0] = new Box(_dimensions);
        for (int d = 0; d < _dimensions; d++)
        {
            boxes[0].High[d] = _side - 1;
        }

        if (Weight(boxes[0]) == 0)
        {
            return [];
        }

        int count = colors;
        int next = 0;
        for (int i = 1; i < colors; i++)
        {
            if (Cut(boxes[next], out boxes[i]))
            {
                variance[next] = Cells(boxes[next]) > 1 ? Variance(boxes[next]) : 0;
                variance[i] = Cells(boxes[i]) > 1 ? Variance(boxes[i]) : 0;
            }
            else
            {
                variance[next] = 0;
                i--;
            }

            next = 0;
            double largest = variance[0];
            for (int k = 1; k <= i; k++)
            {
                if (variance[k] > largest)
                {
                    largest = variance[k];
                    next = k;
                }
            }

            if (largest <= 0)
            {
                count = i + 1;
                break;
            }
        }

        List<Rgba8> palette = new(count);
        Span<long> sums = stackalloc long[4];
        for (int i = 0; i < count; i++)
        {
            long weight = Weight(boxes[i]);
            if (weight == 0)
            {
                continue;
            }

            Sums(boxes[i], sums);
            palette.Add(new Rgba8(
                Mean(sums[0], weight),
                Mean(sums[1], weight),
                Mean(sums[2], weight),
                _dimensions == 4 ? Mean(sums[3], weight) : (byte)255));
        }

        return [.. palette];
    }

    public void Dispose()
    {
        ArrayPool<long>.Shared.Return(_weight);
        ArrayPool<long>.Shared.Return(_sums);
        ArrayPool<double>.Shared.Return(_squares);
    }

    private static byte Mean(long sum, long weight)
    {
        return (byte)Math.Clamp((sum + (weight / 2)) / weight, 0, 255);
    }

    /// <summary>Turns the histogram into cumulative moments: each cell holds the totals of every cell at or below it.</summary>
    private void Accumulate()
    {
        for (int d = 0; d < _dimensions; d++)
        {
            int stride = _strides[d];
            for (int i = 0; i < _cells; i++)
            {
                if ((i / stride) % _side == 0)
                {
                    continue;
                }

                _weight[i] += _weight[i - stride];
                _squares[i] += _squares[i - stride];
                for (int c = 0; c < _dimensions; c++)
                {
                    _sums[(i * _dimensions) + c] += _sums[((i - stride) * _dimensions) + c];
                }
            }
        }
    }

    /// <summary>Returns the cell index of one corner of a box (bit d of <paramref name="corner"/> picks the low side in dimension d) and its inclusion-exclusion sign.</summary>
    private int Corner(Box box, int corner, out int sign)
    {
        int index = 0;
        int lows = 0;
        for (int d = 0; d < _dimensions; d++)
        {
            bool low = (corner & (1 << d)) != 0;
            lows += low ? 1 : 0;
            index += (low ? box.Low[d] : box.High[d]) * _strides[d];
        }

        sign = (lows & 1) == 0 ? 1 : -1;
        return index;
    }

    private long Weight(Box box)
    {
        long total = 0;
        for (int corner = 0; corner < 1 << _dimensions; corner++)
        {
            int index = Corner(box, corner, out int sign);
            total += sign * _weight[index];
        }

        return total;
    }

    private void Sums(Box box, Span<long> sums)
    {
        sums.Clear();
        for (int corner = 0; corner < 1 << _dimensions; corner++)
        {
            int index = Corner(box, corner, out int sign);
            for (int c = 0; c < _dimensions; c++)
            {
                sums[c] += sign * _sums[(index * _dimensions) + c];
            }
        }
    }

    private double Variance(Box box)
    {
        double squares = 0;
        for (int corner = 0; corner < 1 << _dimensions; corner++)
        {
            int index = Corner(box, corner, out int sign);
            squares += sign * _squares[index];
        }

        Span<long> sums = stackalloc long[4];
        Sums(box, sums);
        double weight = Weight(box);
        double total = 0;
        for (int c = 0; c < _dimensions; c++)
        {
            total += (double)sums[c] * sums[c];
        }

        return squares - (total / weight);
    }

    private long Cells(Box box)
    {
        long cells = 1;
        for (int d = 0; d < _dimensions; d++)
        {
            cells *= box.High[d] - box.Low[d];
        }

        return cells;
    }

    /// <summary>Splits <paramref name="box"/> where the variance falls most; it keeps the lower part and the upper goes to <paramref name="upper"/>.</summary>
    private bool Cut(Box box, out Box upper)
    {
        long wholeWeight = Weight(box);
        Span<long> whole = stackalloc long[4];
        Sums(box, whole);
        double best = -1;
        int bestDimension = -1;
        int bestCut = -1;
        Span<long> half = stackalloc long[4];
        Span<long> bottom = stackalloc long[4];
        Span<int> bases = stackalloc int[8];
        Span<int> signs = stackalloc int[8];
        for (int d = 0; d < _dimensions; d++)
        {
            // The lower part, cut at p in dimension d, is the box's corners over the other dimensions, each taken at p minus at
            // Low[d]: the Low[d] half ("bottom") is the same for every p, so only the 2^(D-1) cells at p are read per position.
            int corners = 0;
            for (int corner = 0; corner < 1 << _dimensions; corner++)
            {
                if ((corner & (1 << d)) != 0)
                {
                    continue;
                }

                int index = 0;
                int lows = 0;
                for (int e = 0; e < _dimensions; e++)
                {
                    if (e == d)
                    {
                        continue;
                    }

                    bool low = (corner & (1 << e)) != 0;
                    lows += low ? 1 : 0;
                    index += (low ? box.Low[e] : box.High[e]) * _strides[e];
                }

                bases[corners] = index;
                signs[corners++] = (lows & 1) == 0 ? 1 : -1;
            }

            int stride = _strides[d];
            long bottomWeight = Total(_weight, bases, signs, corners, box.Low[d] * stride);
            TotalSums(bases, signs, corners, box.Low[d] * stride, bottom);
            for (int position = box.Low[d] + 1; position < box.High[d]; position++)
            {
                long lowerWeight = Total(_weight, bases, signs, corners, position * stride) - bottomWeight;
                if (lowerWeight == 0 || lowerWeight == wholeWeight)
                {
                    continue;
                }

                TotalSums(bases, signs, corners, position * stride, half);
                double score = 0;
                for (int c = 0; c < _dimensions; c++)
                {
                    long lower = half[c] - bottom[c];
                    double other = whole[c] - lower;
                    score += ((double)lower * lower / lowerWeight) + (other * other / (wholeWeight - lowerWeight));
                }

                if (score > best)
                {
                    best = score;
                    bestDimension = d;
                    bestCut = position;
                }
            }
        }

        upper = box.Clone();
        if (bestDimension < 0)
        {
            return false;
        }

        box.High[bestDimension] = bestCut;
        upper.Low[bestDimension] = bestCut;
        return true;
    }

    private static long Total(long[] values, ReadOnlySpan<int> bases, ReadOnlySpan<int> signs, int corners, int offset)
    {
        long total = 0;
        for (int i = 0; i < corners; i++)
        {
            total += signs[i] * values[bases[i] + offset];
        }

        return total;
    }

    private void TotalSums(ReadOnlySpan<int> bases, ReadOnlySpan<int> signs, int corners, int offset, Span<long> sums)
    {
        sums.Clear();
        for (int i = 0; i < corners; i++)
        {
            int at = (bases[i] + offset) * _dimensions;
            for (int c = 0; c < _dimensions; c++)
            {
                sums[c] += signs[i] * _sums[at + c];
            }
        }
    }

    /// <summary>A box of histogram cells: from just above <see cref="Low"/> to <see cref="High"/>, in each dimension.</summary>
    private sealed class Box(int dimensions)
    {
        internal int[] Low { get; } = new int[dimensions];

        internal int[] High { get; } = new int[dimensions];

        internal Box Clone()
        {
            Box copy = new(Low.Length);
            Low.CopyTo(copy.Low, 0);
            High.CopyTo(copy.High, 0);
            return copy;
        }
    }
}
