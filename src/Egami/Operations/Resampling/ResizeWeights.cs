using System.Buffers;

namespace Egami.Operations.Resampling;

/// <summary>The filter kernels, from their textbook definitions. Each takes a distance in source pixels at scale 1.</summary>
internal static class ResampleKernels
{
    /// <summary>Returns how far from its center the kernel is non-zero, in source pixels at scale 1.</summary>
    internal static double Support(ResampleFilter filter)
    {
        return filter switch
        {
            ResampleFilter.Nearest => 0,
            ResampleFilter.Box => 0.5,
            ResampleFilter.Bilinear or ResampleFilter.Hermite => 1,
            ResampleFilter.Bicubic or ResampleFilter.Mitchell or ResampleFilter.Lanczos2 or ResampleFilter.Gaussian or ResampleFilter.Spline => 2,
            ResampleFilter.Lanczos3 => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, null),
        };
    }

    /// <summary>Evaluates the kernel at distance <paramref name="x"/>.</summary>
    internal static double Evaluate(ResampleFilter filter, double x)
    {
        if (filter == ResampleFilter.Box)
        {
            // Half-open, (−0.5, 0.5], so a source pixel exactly on the boundary between two outputs counts once, for the one on
            // its left, as ImageMagick and other libraries do.
            return x > -0.5 && x <= 0.5 ? 1 : 0;
        }

        x = Math.Abs(x);
        switch (filter)
        {
            case ResampleFilter.Bilinear:
                return x < 1 ? 1 - x : 0;
            case ResampleFilter.Bicubic:
                return Cubic(x, b: 0, c: 0.5);
            case ResampleFilter.Mitchell:
                return Cubic(x, b: 1 / 3.0, c: 1 / 3.0);
            case ResampleFilter.Lanczos3:
                return x < 3 ? Sinc(x) * Sinc(x / 3) : 0;
            case ResampleFilter.Lanczos2:
                return x < 2 ? Sinc(x) * Sinc(x / 2) : 0;
            case ResampleFilter.Hermite:
                return Cubic(x, b: 0, c: 0);
            case ResampleFilter.Gaussian:
                // σ = ½: exp(−x² / 2σ²). Weights are normalized, so the Gaussian's own scale factor is left out, as ImageMagick does.
                return x < 2 ? Math.Exp(-2 * x * x) : 0;
            case ResampleFilter.Spline:
                return Cubic(x, b: 1, c: 0);
            default:
                return x < 0.5 ? 1 : 0;
        }
    }

    /// <summary>The Mitchell–Netravali family of cubics; B = 0, C = 0.5 is Keys with a = −0.5 (Catmull-Rom).</summary>
    private static double Cubic(double x, double b, double c)
    {
        if (x < 1)
        {
            return (((12 - (9 * b) - (6 * c)) * x * x * x) + ((-18 + (12 * b) + (6 * c)) * x * x) + (6 - (2 * b))) / 6;
        }

        if (x < 2)
        {
            return (((-b - (6 * c)) * x * x * x) + (((6 * b) + (30 * c)) * x * x) + (((-12 * b) - (48 * c)) * x) + ((8 * b) + (24 * c))) / 6;
        }

        return 0;
    }

    private static double Sinc(double x)
    {
        if (x == 0)
        {
            return 1;
        }

        double px = Math.PI * x;
        return Math.Sin(px) / px;
    }
}

/// <summary>
/// The weights of one resize axis: for each output position, the first source index and a run of weights that sum to 1.
/// One table serves every frame and every band.
/// </summary>
internal sealed class ResizeWeights : IDisposable
{
    private float[]? _pairs;
    private int[]? _pairCount;
    private short[]? _fixed;
    private int[]? _groupCount;

    private ResizeWeights(int size, int[] first, int[] count, float[] weights, int stride)
    {
        Size = size;
        First = first;
        Count = count;
        Weights = weights;
        Stride = stride;
    }

    /// <summary>Gets the number of output positions. The pooled arrays may be longer.</summary>
    internal int Size { get; }

    /// <summary>Gets the first source index for each output position. Never decreases.</summary>
    internal int[] First { get; }

    /// <summary>Gets the number of taps for each output position.</summary>
    internal int[] Count { get; }

    /// <summary>Gets the weights, <see cref="Stride"/> per output position, zero past each position's count.</summary>
    internal float[] Weights { get; }

    /// <summary>Gets the most taps any output position has.</summary>
    internal int Stride { get; }

    /// <summary>Gets the tap pairs per output position in <see cref="Pairs"/>.</summary>
    internal int PairStride => (Stride + 1) / 2;

    /// <summary>
    /// Gets the weights laid out for <see cref="ResizeKernels.Horizontal"/>: per output position, <see cref="PairStride"/> groups of
    /// eight floats, the even tap's weight four times and then the odd tap's, zero past the last tap.
    /// </summary>
    internal float[] Pairs => _pairs ?? BuildPairs();

    /// <summary>Gets the number of tap pairs for each output position.</summary>
    internal int[] PairCount
    {
        get
        {
            if (_pairCount is null)
            {
                BuildPairs();
            }

            return _pairCount!;
        }
    }

    /// <summary>Gets the groups of four taps per output position in <see cref="Fixed"/>.</summary>
    internal int GroupStride => (Stride + 3) / 4;

    /// <summary>Gets the fixed-point weights laid out for <see cref="ResizeKernels.HorizontalFixed8"/>.</summary>
    internal short[] Fixed
    {
        get
        {
            if (_fixed is null)
            {
                BuildFixed();
            }

            return _fixed!;
        }
    }

    /// <summary>Gets the number of four-tap groups for each output position.</summary>
    internal int[] GroupCount
    {
        get
        {
            if (_groupCount is null)
            {
                BuildFixed();
            }

            return _groupCount!;
        }
    }

    /// <summary>Gets the weights of output position <paramref name="i"/>.</summary>
    internal ReadOnlySpan<float> Row(int i)
    {
        return Weights.AsSpan(i * Stride, Count[i]);
    }

    /// <summary>Returns the pooled arrays.</summary>
    public void Dispose()
    {
        ArrayPool<int>.Shared.Return(First);
        ArrayPool<int>.Shared.Return(Count);
        ArrayPool<float>.Shared.Return(Weights);
        if (_pairs is not null)
        {
            ArrayPool<float>.Shared.Return(_pairs);
            ArrayPool<int>.Shared.Return(_pairCount!);
            _pairs = null;
            _pairCount = null;
        }

        if (_fixed is not null)
        {
            ArrayPool<short>.Shared.Return(_fixed);
            ArrayPool<int>.Shared.Return(_groupCount!);
            _fixed = null;
            _groupCount = null;
        }
    }

    /// <summary>
    /// Rounds the weights to 1.14 fixed point so each output position sums to exactly <see cref="ResizeKernels.FixedOne"/> (the
    /// rounding error goes to the largest weight), laid out in interleaved tap pairs for <see cref="ResizeKernels.HorizontalFixed8"/>.
    /// </summary>
    private void BuildFixed()
    {
        int groupStride = GroupStride;
        short[] table = ArrayPool<short>.Shared.Rent(Size * groupStride * 16);
        int[] groupCount = ArrayPool<int>.Shared.Rent(Size);
        table.AsSpan(0, Size * groupStride * 16).Clear();
        int[] rounded = new int[Stride];
        for (int i = 0; i < Size; i++)
        {
            ReadOnlySpan<float> row = Row(i);
            int total = 0;
            int largest = 0;
            for (int k = 0; k < row.Length; k++)
            {
                rounded[k] = (int)MathF.Round(row[k] * ResizeKernels.FixedOne);
                total += rounded[k];
                largest = Math.Abs(row[k]) > Math.Abs(row[largest]) ? k : largest;
            }

            rounded[largest] += ResizeKernels.FixedOne - total;
            Span<short> target = table.AsSpan(i * groupStride * 16, groupStride * 16);
            for (int k = 0; k < row.Length; k++)
            {
                // Tap k goes to group k / 4: taps 0 and 1 of a group alternate in its first eight shorts, taps 2 and 3 in its last.
                int at = ((k / 4) * 16) + ((k % 4) / 2 * 8) + (k % 2);
                for (int r = 0; r < 8; r += 2)
                {
                    target[at + r] = checked((short)rounded[k]);
                }
            }

            groupCount[i] = (row.Length + 3) / 4;
        }

        _groupCount = groupCount;
        _fixed = table;
    }

    private float[] BuildPairs()
    {
        int pairStride = PairStride;
        float[] pairs = ArrayPool<float>.Shared.Rent(Size * pairStride * 8);
        int[] pairCount = ArrayPool<int>.Shared.Rent(Size);
        pairs.AsSpan(0, Size * pairStride * 8).Clear();
        for (int i = 0; i < Size; i++)
        {
            ReadOnlySpan<float> row = Row(i);
            Span<float> target = pairs.AsSpan(i * pairStride * 8, pairStride * 8);
            for (int k = 0; k < row.Length; k++)
            {
                target.Slice(((k / 2) * 8) + ((k % 2) * 4), 4).Fill(row[k]);
            }

            pairCount[i] = (row.Length + 1) / 2;
        }

        _pairCount = pairCount;
        _pairs = pairs;
        return pairs;
    }

    /// <summary>
    /// Builds the weights for resizing the source span starting at <paramref name="offset"/> and <paramref name="extent"/> pixels long
    /// (both may be fractional) to <paramref name="size"/> output pixels. Taps outside <paramref name="low"/>..<paramref name="high"/>
    /// are dropped and the rest renormalized, as ImageMagick does, so edge pixels are not weighted twice.
    /// </summary>
    internal static ResizeWeights Build(ResampleFilter filter, int size, double offset, double extent, int low, int high)
    {
        double scale = size / extent;
        int[] first = ArrayPool<int>.Shared.Rent(size);
        int[] count = ArrayPool<int>.Shared.Rent(size);
        if (filter == ResampleFilter.Nearest)
        {
            for (int i = 0; i < size; i++)
            {
                first[i] = Math.Clamp((int)Math.Floor(offset + ((i + 0.5) / scale)), low, high);
                count[i] = 1;
            }

            float[] ones = ArrayPool<float>.Shared.Rent(size);
            ones.AsSpan().Fill(1f);
            return new ResizeWeights(size, first, count, ones, 1);
        }

        // Shrinking stretches the kernel so every source pixel contributes.
        double stretch = Math.Max(1.0, 1.0 / scale);
        double support = ResampleKernels.Support(filter) * stretch;
        int stride = Math.Min((int)Math.Ceiling(support * 2) + 1, high - low + 1);
        float[] weights = ArrayPool<float>.Shared.Rent(size * stride);
        Span<double> buffer = stride <= 256 ? stackalloc double[stride] : new double[stride];
        for (int i = 0; i < size; i++)
        {
            double center = offset + ((i + 0.5) / scale) - 0.5;
            int left = (int)Math.Ceiling(center - support);
            int right = (int)Math.Floor(center + support);
            int start = Math.Clamp(left, low, high);
            int end = Math.Clamp(right, low, high);
            int n = end - start + 1;
            buffer.Clear();
            double sum = 0;
            for (int j = start; j <= end; j++)
            {
                double w = ResampleKernels.Evaluate(filter, (j - center) / stretch);
                buffer[j - start] = w;
                sum += w;
            }

            if (sum == 0)
            {
                // Only reachable for Box exactly between pixels at extreme ratios: take the nearest pixel.
                buffer[Math.Clamp((int)Math.Round(center), start, end) - start] = 1;
                sum = 1;
            }

            Span<float> row = weights.AsSpan(i * stride, n);
            float total = 0;
            for (int k = 0; k < n; k++)
            {
                row[k] = (float)(buffer[k] / sum);
                total += row[k];
            }

            // Push the float rounding error onto the largest weight so the row sums to 1 in float.
            int largest = 0;
            for (int k = 1; k < n; k++)
            {
                if (row[k] > row[largest])
                {
                    largest = k;
                }
            }

            row[largest] += 1f - total;
            first[i] = start;
            count[i] = n;
        }

        return new ResizeWeights(size, first, count, weights, stride);
    }
}
